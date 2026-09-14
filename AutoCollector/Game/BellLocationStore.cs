using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;

namespace AutoCollector.Game;

/// <summary>1 エリアぶんの呼び鈴の場所。</summary>
public sealed class BellLocation
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    /// <summary>見かけた名前。画面に出すためだけに持つ。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>いつ見たか。</summary>
    public DateTime SeenAt { get; set; }

    public Vector3 Position => new(this.X, this.Y, this.Z);
}

/// <summary>保存用。</summary>
public sealed class BellLocationFile
{
    /// <summary>エリア番号 → 呼び鈴の場所。</summary>
    public Dictionary<uint, BellLocation> Bells { get; set; } = [];
}

/// <summary>
/// 呼び鈴の場所をエリアごとに覚える。
///
/// **遠い呼び鈴は <c>Svc.Objects</c> に載らない。**
/// 載らなければ場所が分からず、向かうこともできない。
/// 一度でも見かけた場所を覚えておけば、離れていてもそこへ歩ける。
/// 近づけば読み込まれるので、あとは普段どおり話しかけられる。
///
/// **街だけでなく、宿屋や地下工房も同じ仕組みで覚える。**
/// エリアを判別して対象を絞ったりはしない。呼び鈴が無いエリアでは
/// 探しても見つからないだけで、害が無いため。
///
/// キャラクターでは分けない。呼び鈴はワールドの設置物で、
/// 誰から見ても同じ場所にある。
///
/// **当てが外れたら忘れる。**
/// 覚えた場所まで歩いて呼び鈴が無ければ、その記録は捨てる。
/// 動かせる呼び鈴（ハウジングの調度品）を覚えてしまっても、
/// 1 度無駄に歩くだけで済み、間違ったまま居座らない。
/// </summary>
public sealed class BellLocationStore(AnomalyLog anomalyLog)
{
    private const string FileName = "bell-locations.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly AnomalyLog anomalyLog = anomalyLog;

    private Dictionary<uint, BellLocation>? bells;
    private bool dirty;
    private DateTime nextSaveUtc = DateTime.MinValue;

    /// <summary>覚えているエリアの数。</summary>
    public int Count => this.Load().Count;

    private static string ResolvePath()
        => Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, FileName);

    private Dictionary<uint, BellLocation> Load()
    {
        if (this.bells is not null)
        {
            return this.bells;
        }

        this.bells = [];

        try
        {
            var path = ResolvePath();

            if (!File.Exists(path))
            {
                return this.bells;
            }

            var file = JsonSerializer.Deserialize<BellLocationFile>(File.ReadAllText(path));

            if (file is null)
            {
                return this.bells;
            }

            this.bells = file.Bells;
            this.anomalyLog.Info("Bell", $"呼び鈴の場所を読み込みました（{this.bells.Count} エリア）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Bell", $"呼び鈴の場所を読めませんでした: {ex.Message}");
            this.bells = [];
        }

        return this.bells;
    }

    /// <summary>
    /// この場所を覚える。
    ///
    /// 同じエリアで何度も見かけるので、**動いていなければ書き換えない。**
    /// 書き換えるたびに保存が走ると、何もしていないのにファイルを触り続ける。
    /// </summary>
    public void Remember(uint territoryId, Vector3 position, string name)
    {
        if (territoryId == 0)
        {
            return;
        }

        var store = this.Load();

        if (store.TryGetValue(territoryId, out var existing) &&
            Vector3.Distance(existing.Position, position) < 1f)
        {
            return;
        }

        store[territoryId] = new BellLocation
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            Name = name,
            SeenAt = DateTime.Now,
        };

        this.dirty = true;

        this.anomalyLog.Info(
            "Bell",
            $"呼び鈴の場所を覚えました（エリア {territoryId} / {position.X:F1}, {position.Y:F1}, {position.Z:F1}）");
    }

    /// <summary>このエリアで覚えている場所。無ければ null。</summary>
    public Vector3? Get(uint territoryId)
        => this.Load().TryGetValue(territoryId, out var bell) ? bell.Position : null;

    /// <summary>
    /// 覚えていた場所を忘れる。
    ///
    /// そこまで歩いて呼び鈴が無かったときに通す。
    /// 当てにならない記録を残すと、毎回そこへ歩いて無駄になる。
    /// </summary>
    public void Forget(uint territoryId)
    {
        if (!this.Load().Remove(territoryId))
        {
            return;
        }

        this.dirty = true;
        this.anomalyLog.Info("Bell", $"呼び鈴の場所を忘れました（エリア {territoryId}）。そこにありませんでした");
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

            var file = new BellLocationFile { Bells = this.Load() };

            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Bell", $"呼び鈴の場所を保存できませんでした: {ex.Message}");
        }
    }

    /// <summary>終了時に書き残しを出す。</summary>
    public void Dispose()
    {
        this.nextSaveUtc = DateTime.MinValue;
        this.SaveIfDirty();
    }
}
