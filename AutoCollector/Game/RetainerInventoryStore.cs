using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;

namespace AutoCollector.Game;

/// <summary>1 人のリテイナーの持ち物。</summary>
public sealed class RetainerContents
{
    public string Name { get; set; } = string.Empty;

    /// <summary>いつ見たか。古ければ当てにしない。</summary>
    public DateTime SeenAt { get; set; }

    /// <summary>ItemId → 個数。</summary>
    public Dictionary<uint, int> Items { get; set; } = [];
}

/// <summary>保存用。</summary>
public sealed class RetainerInventoryFile
{
    public string Character { get; set; } = string.Empty;

    public Dictionary<string, RetainerContents> Retainers { get; set; } = [];
}

/// <summary>
/// リテイナーが何を持っているかを覚える。
///
/// 覚えていないと、1 種類の素材を取り出すたびに全員を開いて回ることになる。
/// 実測では黒麦 1 種類のために 4 人を開閉して 14 秒かかった。
///
/// **Allagan Tools には依存しない。**
/// リテイナーを開いた時点で持ち物は <c>RetainerPage1..7</c> から直接読める。
/// 開いたついでに全部控えておけば、次からは当たりのリテイナーへ直接行ける。
///
/// 自分で開いたときだけでなく、人が手で開いたときも控える。
/// 普段の出し入れで勝手に埋まっていく。
///
/// 中身は変わるので、いつ見たかを一緒に持つ。
/// 古い記録しかない場合は、その旨を画面に出して当てにしないようにする。
/// </summary>
public sealed class RetainerInventoryStore(AnomalyLog anomalyLog)
{
    private const string FileName = "retainer-inventory.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>これより古い記録は「当てにできない」として扱う。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);

    private readonly AnomalyLog anomalyLog = anomalyLog;

    private Dictionary<string, RetainerContents>? retainers;
    private string character = string.Empty;
    private bool dirty;
    private DateTime nextSaveUtc = DateTime.MinValue;

    /// <summary>覚えているリテイナーの数。</summary>
    public int Count => this.Load().Count;

    /// <summary>覚えている中で、いちばん古い記録の時刻。</summary>
    public DateTime? OldestSeenAt
    {
        get
        {
            var all = this.Load();
            return all.Count == 0 ? null : all.Values.Min(x => x.SeenAt);
        }
    }

    /// <summary>覚えているリテイナーの名前。</summary>
    public IReadOnlyList<string> Names => [.. this.Load().Keys];

    private static string ResolvePath()
        => Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, FileName);

    private static string CurrentCharacter()
    {
        try
        {
            return ECommons.GameHelpers.Player.NameWithWorld;
        }
        catch
        {
            return string.Empty;
        }
    }

    private Dictionary<string, RetainerContents> Load()
    {
        var character = CurrentCharacter();

        // キャラクターが変わったら別物。読み直す。
        if (this.retainers is not null && this.character == character)
        {
            return this.retainers;
        }

        this.character = character;
        this.retainers = [];

        try
        {
            var path = ResolvePath();

            if (!File.Exists(path))
            {
                return this.retainers;
            }

            var file = JsonSerializer.Deserialize<RetainerInventoryFile>(File.ReadAllText(path));

            if (file is null || (!string.IsNullOrEmpty(character) && file.Character != character))
            {
                return this.retainers;
            }

            this.retainers = file.Retainers;
            this.anomalyLog.Info("Retainer", $"リテイナーの持ち物を読み込みました（{this.retainers.Count} 人）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Retainer", $"リテイナーの持ち物を読めませんでした: {ex.Message}");
            this.retainers = [];
        }

        return this.retainers;
    }

    /// <summary>1 人分の持ち物を覚える。</summary>
    public void Remember(string retainerName, IReadOnlyDictionary<uint, int> items)
    {
        if (string.IsNullOrEmpty(retainerName))
        {
            return;
        }

        var store = this.Load();

        store[retainerName] = new RetainerContents
        {
            Name = retainerName,
            SeenAt = DateTime.Now,
            Items = items.ToDictionary(x => x.Key, x => x.Value),
        };

        this.dirty = true;
    }

    /// <summary>この品を持っているリテイナー。多い順。</summary>
    public IReadOnlyList<(string Name, int Quantity, DateTime SeenAt)> WhoHas(uint itemId)
    {
        var list = new List<(string, int, DateTime)>();

        foreach (var (name, contents) in this.Load())
        {
            if (contents.Items.TryGetValue(itemId, out var quantity) && quantity > 0)
            {
                list.Add((name, quantity, contents.SeenAt));
            }
        }

        return list.OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>この品をリテイナー全体で何個持っているか。</summary>
    public int TotalHeld(uint itemId) => this.WhoHas(itemId).Sum(x => x.Quantity);

    /// <summary>
    /// 覚えている中身が当てにできるか。
    ///
    /// 1 人も覚えていない、または記録が古い場合は false。
    /// このときは総当たりするしかない。
    /// </summary>
    public bool IsUsable(out string reason)
    {
        var all = this.Load();

        if (all.Count == 0)
        {
            reason = "リテイナーの持ち物をまだ覚えていません";
            return false;
        }

        var oldest = all.Values.Min(x => x.SeenAt);

        if (DateTime.Now - oldest > StaleAfter)
        {
            reason = $"リテイナーの持ち物の記録が古いです（{oldest:MM/dd HH:mm}）";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>覚えたものがあれば保存する。書き込みは間隔を空ける。</summary>
    public void SaveIfDirty()
    {
        var now = DateTime.UtcNow;

        if (!this.dirty || now < this.nextSaveUtc)
        {
            return;
        }

        this.dirty = false;
        this.nextSaveUtc = now.AddSeconds(5);

        try
        {
            var path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var file = new RetainerInventoryFile
            {
                Character = this.character,
                Retainers = this.Load(),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Retainer", $"リテイナーの持ち物を保存できませんでした: {ex.Message}");
        }
    }

    /// <summary>覚えたものを消す。中身が大きく変わったときのやり直し用。</summary>
    public void Clear()
    {
        this.retainers = [];
        this.dirty = true;
        this.nextSaveUtc = DateTime.MinValue;
        this.SaveIfDirty();
    }

    /// <summary>終了時に書き残しを出す。</summary>
    public void Dispose()
    {
        this.nextSaveUtc = DateTime.MinValue;
        this.SaveIfDirty();
    }
}
