using System;
using System.IO;
using System.Reflection;
using AutoCollector.Diagnostics;
using ECommons.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCollector.Automation;

/// <summary>
/// ShopExchangeCurrency アドオンの AtkValues 配置。
///
/// この配置はゲーム／Dalamud のアップデートでずれる。実際に ECommons でも
/// 3.2.0.4 の 84 系から 3.2.1.17 の 86 系へ +2 ずれた履歴がある。
/// コードに埋め込まず、設定ディレクトリの JSON から読む形にして、
/// ずれたときにユーザーが数値を直すだけで復旧できるようにする。
/// </summary>
public sealed class ShopAddonLayout
{
    /// <summary>エントリ数が入っている AtkValue の位置。</summary>
    public int NumEntries { get; set; } = 4;

    /// <summary>現在の所持通貨量が入っている位置。</summary>
    public int CurrencyAmount { get; set; } = 86;

    /// <summary>通貨アイコン ID が入っている位置。</summary>
    public int CurrencyIcon { get; set; } = 87;

    /// <summary>各エントリのコストが並ぶ先頭位置。</summary>
    public int EntryCost { get; set; } = 456;

    /// <summary>各エントリの ItemId が並ぶ先頭位置。</summary>
    public int EntryItemId { get; set; } = 1066;

    /// <summary>各エントリの「交換に渡す index」が並ぶ先頭位置。</summary>
    public int EntryIndex { get; set; } = 1310;

    /// <summary>並列配列の要素間隔。</summary>
    public int EntryStride { get; set; } = 1;

    /// <summary>
    /// 読み取るエントリ数の上限。異常値で暴走しないための歯止め。
    ///
    /// <b>配列の間隔より十分小さくする。</b>
    /// 3 本の並列配列は間隔をあけて並んでおり、既定値では
    /// ItemId(1066) と Index(1310) の間隔が 244 しかない。
    /// ここを 240 にしていたため、余裕が 4 しか無かった。
    /// パッチで間隔が少しでも縮むと既定値そのものが不正になり、
    /// 読み込みに失敗したときのフォールバック（＝同じ既定値）も
    /// 不正なままになる。
    ///
    /// 交換所に 200 件も並ぶことは無いので、余裕を取って下げる。
    /// </summary>
    public int MaxEntries { get; set; } = 200;

    /// <summary>3 本の並列配列のうち最も手前の位置。エントリ読み取りの基点にする。</summary>
    [JsonIgnore]
    public int EntryBase => Math.Min(this.EntryCost, Math.Min(this.EntryItemId, this.EntryIndex));

    [JsonIgnore]
    public int CostRelative => this.EntryCost - this.EntryBase;

    [JsonIgnore]
    public int ItemIdRelative => this.EntryItemId - this.EntryBase;

    [JsonIgnore]
    public int IndexRelative => this.EntryIndex - this.EntryBase;

    /// <summary>設定として成立しているか検査する。</summary>
    public bool Validate(out string reason)
    {
        if (this.NumEntries < 0 || this.CurrencyAmount < 0 || this.CurrencyIcon < 0 ||
            this.EntryCost < 0 || this.EntryItemId < 0 || this.EntryIndex < 0)
        {
            reason = "負のオフセットが含まれています";
            return false;
        }

        if (this.EntryStride < 1)
        {
            reason = $"EntryStride が {this.EntryStride} です。1 以上である必要があります";
            return false;
        }

        if (this.MaxEntries is < 1 or > 4096)
        {
            reason = $"MaxEntries が {this.MaxEntries} です。1〜4096 の範囲である必要があります";
            return false;
        }

        // 3 本の並列配列は互いに間隔をあけて並んでいる。
        // 読み取り件数がその間隔を超えると、ある配列の読み取りが隣の配列に食い込む。
        // 既定値では ItemId(1066) と Index(1310) の間隔が 244 しかない。
        var bases = new[] { this.EntryCost, this.EntryItemId, this.EntryIndex };
        Array.Sort(bases);
        var minGap = Math.Min(bases[1] - bases[0], bases[2] - bases[1]);
        if ((long)this.EntryStride * this.MaxEntries > minGap)
        {
            reason = $"MaxEntries {this.MaxEntries} × EntryStride {this.EntryStride} が配列間隔 {minGap} を超えています。読み取りが隣の配列に食い込みます";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// 補助データ JSON の読み込み。
/// アセンブリに埋め込んだ既定値を初回起動時に設定ディレクトリへ展開し、以後はそちらを読む。
/// 出力へのコピー挙動に依存しないようにするための構成。
/// </summary>
public static class DataFileLoader
{
    private const string ResourcePrefix = "AutoCollector.Data.";

    public static string GetDataDirectory()
    {
        var dir = Path.Combine(EzConfig.GetPluginConfigDirectory(), "Data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 設定ディレクトリの JSON を読む。無ければ埋め込みの既定値を書き出してから読む。
    /// 壊れていた場合は既定値を返し、ファイルは上書きしない（ユーザーの編集内容を消さないため）。
    /// </summary>
    public static T Load<T>(string fileName, AnomalyLog anomalyLog)
        where T : new()
    {
        var path = Path.Combine(GetDataDirectory(), fileName);

        try
        {
            if (!File.Exists(path))
            {
                var defaults = ReadEmbedded(fileName);
                if (defaults is not null)
                {
                    File.WriteAllText(path, defaults);
                }
            }

            if (!File.Exists(path))
            {
                anomalyLog.Warn("Data", $"{fileName} を展開できませんでした。既定値を使用します");
                return new T();
            }

            var text = File.ReadAllText(path);
            var parsed = JsonConvert.DeserializeObject<T>(text);
            if (parsed is null)
            {
                anomalyLog.Warn("Data", $"{fileName} の内容が空でした。既定値を使用します");
                return new T();
            }

            return parsed;
        }
        catch (Exception ex)
        {
            anomalyLog.Error("Data", $"{fileName} の読み込みに失敗しました。既定値を使用します: {ex.Message}");
            return new T();
        }
    }

    /// <summary>ShopExchangeCurrency の配置を読む。JSON はアドオン名をキーに持つ。</summary>
    public static ShopAddonLayout LoadShopLayout(AnomalyLog anomalyLog)
    {
        var path = Path.Combine(GetDataDirectory(), "atkvalue_layout.json");

        try
        {
            if (!File.Exists(path))
            {
                var defaults = ReadEmbedded("atkvalue_layout.json");
                if (defaults is not null)
                {
                    File.WriteAllText(path, defaults);
                }
            }

            if (File.Exists(path))
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var node = root["ShopExchangeCurrency"];
                if (node is not null)
                {
                    var layout = node.ToObject<ShopAddonLayout>();
                    if (layout is null)
                    {
                        anomalyLog.Error("Data", "atkvalue_layout.json に ShopExchangeCurrency の設定を読み取れませんでした。既定値を使用します");
                    }
                    else if (layout.Validate(out var reason))
                    {
                        return layout;
                    }
                    else
                    {
                        anomalyLog.Error("Data", $"atkvalue_layout.json の内容が不正です（{reason}）。既定値を使用します");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            anomalyLog.Error("Data", $"atkvalue_layout.json の読み込みに失敗しました。既定値を使用します: {ex.Message}");
        }

        // **フォールバック先も確かめる。**
        //
        // 以前は既定値をそのまま返していた。パッチで配列の間隔が縮むと
        // 既定値自体が Validate を通らなくなるが、そのときも同じ既定値へ
        // 落ちるため、不正な設定のまま動き続けることになる。
        //
        // 通らないなら、読み取り件数を間隔に収まるところまで下げる。
        // 件数が減るだけで、読み取りが隣の配列へ食い込むよりはるかに安全。
        var fallback = new ShopAddonLayout();

        if (fallback.Validate(out var fallbackReason))
        {
            return fallback;
        }

        anomalyLog.Error(
            "Data",
            $"既定の読み取り位置も不正です（{fallbackReason}）。読み取り件数を下げて続けます");

        var bases = new[] { fallback.EntryCost, fallback.EntryItemId, fallback.EntryIndex };
        Array.Sort(bases);
        var minGap = Math.Min(bases[1] - bases[0], bases[2] - bases[1]);

        fallback.MaxEntries = Math.Max(1, (minGap / Math.Max(1, fallback.EntryStride)) - 1);

        anomalyLog.Error("Data", $"読み取り件数を {fallback.MaxEntries} 件に下げました");
        return fallback;
    }

    private static string? ReadEmbedded(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + fileName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
