using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace AutoCollector.Game;

/// <summary>special_currency_map.json の 1 件。実行時に解決できないときの控えとして使う。</summary>
public sealed class SpecialCurrencyBucket
{
    public int Index { get; set; }

    public uint ItemId { get; set; }

    public string Name { get; set; } = string.Empty;
}

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
/// この対応表はゲームの Excel シートには存在しないが、
/// **クライアントは実行時に持っている**。
///
///   CurrencyManager.SpecialItemBucket : StdMap&lt;uint ItemId, SpecialCurrencyItem&gt;
///   CurrencyManager.GetItemIdBySpecialId(byte specialId) → ItemId
///
/// したがって一次情報源はクライアントとし、外部 JSON は
/// 「クライアントから引けなかったときの控え」と「差分検出用のスナップショット」に留める。
///
/// 画面（AgentInclusionShop）が持つ値も CostType 依存で、
/// CostType==3 のときはバケットインデックスであって ItemId ではない点に注意。
/// つまり画面から本物の通貨 ItemId を直接読むことはできない。
/// </summary>
public sealed class SpecialCurrencyMap
{
    private readonly AnomalyLog anomalyLog;
    private readonly Dictionary<int, uint> runtimeMap = [];
    private readonly Dictionary<int, uint> fallbackMap = [];

    public SpecialCurrencyMap(AnomalyLog anomalyLog)
    {
        this.anomalyLog = anomalyLog;
        this.LoadFallback();
    }

    /// <summary>クライアントから対応表を取得できたか。</summary>
    public bool ResolvedFromClient { get; private set; }

    public string? VerifiedGameVersion { get; private set; }

    /// <summary>いま有効な対応表。クライアント由来を優先する。</summary>
    public IReadOnlyDictionary<int, uint> Entries => this.runtimeMap.Count > 0 ? this.runtimeMap : this.fallbackMap;

    /// <summary>
    /// クライアントから対応表を作り直す。ログイン後に呼ぶ。
    /// 取得できなければ控えの JSON を使い続ける。
    /// </summary>
    public unsafe void RefreshFromClient()
    {
        this.runtimeMap.Clear();
        this.ResolvedFromClient = false;

        try
        {
            var manager = CurrencyManager.Instance();
            if (manager is null)
            {
                return;
            }

            var itemSheet = Svc.Data.GetExcelSheet<Item>();

            // SpecialItemBucket は ItemId をキーに SpecialId を持つ。
            // インデクサ this[key] は未登録キーでノードを挿入してしまうため、必ず列挙で読む。
            foreach (var pair in manager->SpecialItemBucket)
            {
                var itemId = pair.Item1;
                var specialId = pair.Item2.SpecialId;

                // 0 は特殊通貨バケットではない側の値。
                if (specialId == 0 || itemId == 0)
                {
                    continue;
                }

                var row = itemSheet?.GetRowOrDefault(itemId);
                if (row is null || string.IsNullOrEmpty(row.Value.Name.ExtractText()))
                {
                    continue;
                }

                if (this.runtimeMap.TryGetValue(specialId, out var existing) && existing != itemId)
                {
                    this.anomalyLog.Warn(
                        "Currency",
                        $"特殊通貨インデックス {specialId} に複数のアイテム（{existing} / {itemId}）が対応しています。先に見つかった方を使います");
                    continue;
                }

                this.runtimeMap[specialId] = itemId;
            }

            this.ResolvedFromClient = this.runtimeMap.Count > 0;

            if (this.ResolvedFromClient)
            {
                this.CompareWithFallback();
            }
        }
        catch (Exception ex)
        {
            this.runtimeMap.Clear();
            this.anomalyLog.Warn("Currency", $"クライアントから特殊通貨の対応表を取得できませんでした: {ex.Message}");
        }
    }

    /// <summary>控えの JSON と食い違っていたら知らせる。JSON 側を自動で書き換えることはしない。</summary>
    private void CompareWithFallback()
    {
        foreach (var (index, itemId) in this.runtimeMap)
        {
            if (!this.fallbackMap.TryGetValue(index, out var stored))
            {
                continue;
            }

            if (stored != itemId)
            {
                this.anomalyLog.Warn(
                    "Currency",
                    $"special_currency_map.json の index {index} が実際と違います（記録 {stored} / 実際 {itemId}）。実際の値を使います");
            }
        }
    }

    private void LoadFallback()
    {
        this.fallbackMap.Clear();

        var file = DataFileLoader.Load<SpecialCurrencyMapFile>("special_currency_map.json", this.anomalyLog);
        this.VerifiedGameVersion = file.VerifiedGameVersion;

        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        foreach (var bucket in file.Buckets)
        {
            if (bucket.Index <= 0 || bucket.ItemId == 0)
            {
                continue;
            }

            var row = itemSheet?.GetRowOrDefault(bucket.ItemId);
            if (row is null || string.IsNullOrEmpty(row.Value.Name.ExtractText()))
            {
                this.anomalyLog.Warn(
                    "Currency",
                    $"special_currency_map.json の index {bucket.Index} が指す ItemId {bucket.ItemId} が見つかりません。この項目は無視します");
                continue;
            }

            this.fallbackMap[bucket.Index] = bucket.ItemId;
        }
    }

    public bool TryResolve(uint index, out uint itemId)
        => this.Entries.TryGetValue((int)index, out itemId);

    /// <summary>この表に載っている通貨の一覧。UI で監視対象として選ばせるのに使う。</summary>
    public IReadOnlyList<(uint ItemId, string Name)> ListCurrencies()
    {
        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        return this.Entries.Values
            .Distinct()
            .Select(id => (id, itemSheet?.GetRowOrDefault(id)?.Name.ExtractText() ?? $"<{id}>"))
            .OrderBy(x => x.Item2, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 通貨の所持上限。クライアントが持っている値を優先し、無ければ Item.StackSize を使う。
    /// スクリップのように上限が別管理の通貨があるため。
    /// </summary>
    public unsafe uint? TryGetMaxCount(uint itemId)
    {
        try
        {
            var manager = CurrencyManager.Instance();
            if (manager is not null && manager->IsItemLimited(itemId))
            {
                var max = manager->GetItemMaxCount(itemId);
                if (max > 0)
                {
                    return max;
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Currency", $"ItemId {itemId} の上限を取得できませんでした: {ex.Message}");
        }

        return null;
    }
}
