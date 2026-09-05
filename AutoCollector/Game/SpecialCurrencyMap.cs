using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace AutoCollector.Game;

/// <summary>special_currency_map.json の 1 件。</summary>
public sealed class SpecialCurrencyBucket
{
    public int Index { get; set; }

    public uint ItemId { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>ファイル全体。</summary>
public sealed class SpecialCurrencyMapFile
{
    [JsonProperty("verifiedGameVersion")]
    public string? VerifiedGameVersion { get; set; }

    [JsonProperty("buckets")]
    public List<SpecialCurrencyBucket> Buckets { get; set; } = [];
}

/// <summary>
/// SpecialShop の CostType==3 に出てくる特殊通貨インデックスを ItemId へ解決する。
///
/// この対応表はゲームのシート内に存在しないため、外部データとして持つしかない。
/// トームストーンが Tomestones シートで解決できるのとは対照的に、
/// スクリップにはシート上の参照先が無い。
///
/// ただしこの表に頼るのは「候補を一覧に出すため」だけである。
/// 実際に交換するときは、InclusionShop の画面が本物のコスト通貨 ItemId を持っているので、
/// そちらと照合してから実行する。表がずれていても誤った通貨で交換することはない。
/// </summary>
public sealed class SpecialCurrencyMap
{
    private readonly AnomalyLog anomalyLog;
    private readonly Dictionary<int, uint> indexToItemId = [];

    public SpecialCurrencyMap(AnomalyLog anomalyLog)
    {
        this.anomalyLog = anomalyLog;
        this.Reload();
    }

    public string? VerifiedGameVersion { get; private set; }

    public IReadOnlyDictionary<int, uint> Entries => this.indexToItemId;

    public void Reload()
    {
        this.indexToItemId.Clear();

        var file = DataFileLoader.Load<SpecialCurrencyMapFile>("special_currency_map.json", this.anomalyLog);
        this.VerifiedGameVersion = file.VerifiedGameVersion;

        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        foreach (var bucket in file.Buckets)
        {
            if (bucket.Index <= 0 || bucket.ItemId == 0)
            {
                continue;
            }

            // 実在しない ItemId を載せたまま使うと、存在しない通貨を監視することになる。
            var row = itemSheet?.GetRowOrDefault(bucket.ItemId);
            if (row is null || string.IsNullOrEmpty(row.Value.Name.ExtractText()))
            {
                this.anomalyLog.Warn(
                    "Currency",
                    $"special_currency_map.json の index {bucket.Index} が指す ItemId {bucket.ItemId} が見つかりません。この項目は無視します");
                continue;
            }

            this.indexToItemId[bucket.Index] = bucket.ItemId;
        }
    }

    public bool TryResolve(uint index, out uint itemId)
        => this.indexToItemId.TryGetValue((int)index, out itemId);

    /// <summary>この表に載っている通貨の一覧。UI で監視対象として選ばせるのに使う。</summary>
    public IReadOnlyList<(uint ItemId, string Name)> ListCurrencies()
    {
        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        return this.indexToItemId.Values
            .Distinct()
            .Select(id => (id, itemSheet?.GetRowOrDefault(id)?.Name.ExtractText() ?? $"<{id}>"))
            .OrderBy(x => x.Item2, StringComparer.Ordinal)
            .ToList();
    }
}
