using System;
using System.Collections.Generic;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>収集品を納品したときに得られるもの。</summary>
/// <param name="CurrencyItemId">得られるスクリップの ItemId。</param>
/// <param name="LowReward">低い収集価値のときの量。</param>
/// <param name="MidReward">中くらいのときの量。</param>
/// <param name="HighReward">高いときの量。</param>
public sealed record CollectableReward(uint CurrencyItemId, ushort LowReward, ushort MidReward, ushort HighReward);

/// <summary>
/// 収集品を納品すると何のスクリップがいくつ得られるかを、納品する前に求める。
///
/// これが分からないと、納品しても意味のない収集品を見分けられない。
/// 生むスクリップが上限に達していて、そのスクリップを減らす設定も無い場合、
/// その収集品は「持っているが納品できない」状態になる。
/// 納品しようとしては止まる、を繰り返すことになる。
///
/// <code>
/// Item（収集品）
///   └ CollectablesShopItem              品ごとの行
///       └ CollectablesShopRewardScrip   Currency と 低/中/高 の量
/// </code>
///
/// Currency は特殊通貨の番号で、ItemId ではない。
/// <see cref="SpecialCurrencyMap"/> で ItemId へ直す。
///
/// 実測（2026-09-13）:
/// 収集用のタコス・カルネ・アサーダ → Currency 6（クラフタースクリップ:橙貨）
/// 低 120 / 中 134 / 高 144。実際の納品で 144 を確認している。
/// </summary>
public sealed class CollectableRewardService(AnomalyLog anomalyLog, SpecialCurrencyMap specialCurrencyMap)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly SpecialCurrencyMap specialCurrencyMap = specialCurrencyMap;

    private Dictionary<uint, CollectableReward>? index;

    /// <summary>この収集品が生むスクリップ。分からなければ false。</summary>
    public bool TryResolve(uint collectableItemId, out CollectableReward reward)
        => this.Build().TryGetValue(collectableItemId, out reward!);

    /// <summary>解決できた収集品の数。</summary>
    public int KnownCount => this.Build().Count;

    private Dictionary<uint, CollectableReward> Build()
    {
        if (this.index is not null)
        {
            return this.index;
        }

        var result = new Dictionary<uint, CollectableReward>();

        try
        {
            var shopItems = Svc.Data.GetSubrowExcelSheet<CollectablesShopItem>();
            var scrips = Svc.Data.GetExcelSheet<CollectablesShopRewardScrip>();

            if (shopItems is null || scrips is null)
            {
                this.anomalyLog.Error("Collectables", "シートを読めないため、収集品の報酬を求められません");
                return this.index = result;
            }

            foreach (var group in shopItems)
            {
                foreach (var row in group)
                {
                    var itemId = row.Item.RowId;
                    if (itemId == 0 || result.ContainsKey(itemId))
                    {
                        continue;
                    }

                    var scripRowId = row.CollectablesShopRewardScrip.RowId;
                    if (scripRowId == 0 || !scrips.TryGetRow(scripRowId, out var scrip))
                    {
                        continue;
                    }

                    // Currency は特殊通貨の番号。ItemId ではない。
                    if (!this.specialCurrencyMap.TryResolve(scrip.Currency, out var currencyItemId))
                    {
                        continue;
                    }

                    result[itemId] = new CollectableReward(
                        currencyItemId,
                        scrip.LowReward,
                        scrip.MidReward,
                        scrip.HighReward);
                }
            }

            this.anomalyLog.Info("Collectables", $"収集品の報酬を求めました（{result.Count} 種）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Collectables", $"収集品の報酬を求められませんでした: {ex.Message}");
        }

        return this.index = result;
    }
}
