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
/// <param name="LowCollectability">納品を受け付ける最低の収集価値。これに届かないと渡せない。</param>
/// <param name="MidCollectability">中の段に上がる収集価値。</param>
/// <param name="HighCollectability">高の段に上がる収集価値。</param>
public sealed record CollectableReward(
    uint CurrencyItemId,
    ushort LowReward,
    ushort MidReward,
    ushort HighReward,
    ushort LowCollectability,
    ushort MidCollectability,
    ushort HighCollectability)
{
    /// <summary>
    /// この収集価値で納品できるか。
    ///
    /// 下限が読めなかった場合は止めない。判断材料が無いだけで、
    /// 納品してみれば分かる。読めない値を根拠に品を捨てる方が害が大きい。
    /// </summary>
    public bool Accepts(int collectability)
        => this.LowCollectability == 0 || collectability >= this.LowCollectability;
}

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
/// 納品を受け付ける収集価値の下限は <c>CollectablesShopRefine</c> にある。
/// これに届かない品は、窓口まで行っても渡せない。
///
/// 実測（2026-09-13）:
/// 収集用のタコス・カルネ・アサーダ → Currency 6（クラフタースクリップ:橙貨）
/// 低 120 / 中 134 / 高 144。実際の納品で 144 を確認している。
///
/// 実データ（2026-09-20、シートを直接読んで確認）:
/// 収集用のタコス・カルネ・アサーダ の収集価値は 低 660 / 中 900 / 高 1140。
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

    /// <summary>
    /// 解決できた収集品をすべて返す。
    ///
    /// **出どころは問わない。**このシートは納品を受ける側の表なので、
    /// 製作の収集品も採集の収集品も同じように入っている。
    /// どちらの稼ぎ方かを知りたい側が、レシピや採集地の有無で振り分ける。
    /// </summary>
    public IReadOnlyDictionary<uint, CollectableReward> ListAll() => this.Build();

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
            var refines = Svc.Data.GetExcelSheet<CollectablesShopRefine>();
            var items = Svc.Data.GetExcelSheet<Item>();

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

                    // 収集品だけを採る。
                    //
                    // このシートには旧方式で納品できた普通の品も載っている。
                    // 実測では 木工 Lv50〜60 で 42 件が引けたが、
                    // 収集品は 6 件だけで、残りはバーチロングボウのような普通の装備だった。
                    // 製作手帳の収集品欄と件数が合わなくなる。
                    if (items is not null && items.TryGetRow(itemId, out var item) && !item.IsCollectable)
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

                    // 納品を受け付ける収集価値の段。
                    //
                    // 0 行目が実在して全 0 のため、行番号 0 は「未設定」として扱う。
                    // 引けなかった場合も 0 のままにして、下限での判断を行わない。
                    ushort lowCollectability = 0;
                    ushort midCollectability = 0;
                    ushort highCollectability = 0;

                    var refineRowId = row.CollectablesShopRefine.RowId;
                    if (refineRowId != 0 && refines is not null && refines.TryGetRow(refineRowId, out var refine))
                    {
                        lowCollectability = refine.LowCollectability;
                        midCollectability = refine.MidCollectability;
                        highCollectability = refine.HighCollectability;
                    }

                    result[itemId] = new CollectableReward(
                        currencyItemId,
                        scrip.LowReward,
                        scrip.MidReward,
                        scrip.HighReward,
                        lowCollectability,
                        midCollectability,
                        highCollectability);
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
