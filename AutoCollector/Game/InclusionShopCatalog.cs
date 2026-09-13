using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>交換画面の「種別」1 件。ゲーム内のタブに相当する。</summary>
public sealed record InclusionSeries(uint SpecialShopId, string Name);

/// <summary>交換画面の「系統」1 件。ゲーム内のプルダウンに相当する。</summary>
public sealed record InclusionCategory(string Name, IReadOnlyList<InclusionSeries> Series);

/// <summary>種別の中に並ぶ品 1 件。</summary>
public sealed record InclusionOffer(
    uint RewardItemId,
    string RewardName,
    uint RewardQuantity,
    uint CurrencyItemId,
    uint CurrencyCost);

/// <summary>
/// アイテム交換画面の中身を、ゲーム内と同じ形で引く。
///
/// 画面は「系統」を選び、その中の「種別」を選んでから品が並ぶ。
/// 設定画面でも同じ形にしないと、数百件が五十音順に並ぶだけになって探せない。
///
/// <code>
/// InclusionShop
///   └ Category（系統）      InclusionShopCategory.Name
///       └ Series（種別）    InclusionShopSeries → SpecialShop.Name
///           └ 品            SpecialShop.Item[]
/// </code>
///
/// 系統と種別の一覧だけを先に作り、**品は種別を開いたときに読む。**
/// 全部を先に読むと数千件になり、開いた瞬間に固まる。
///
/// 系統は都市によって中身が違う（新しい種別は新しい都市にしかない）。
/// 名前でまとめて全都市の和を取る。どの窓口へ行くかは
/// <see cref="ExchangeResolver"/> が品から決めるため、ここでは気にしない。
/// </summary>
public sealed class InclusionShopCatalog(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    private List<InclusionCategory>? categories;

    /// <summary>種別ごとの品。開いたものだけを覚える。</summary>
    private readonly Dictionary<uint, List<InclusionOffer>> offerCache = [];

    /// <summary>系統と種別の一覧。初回の呼び出しで作る。</summary>
    public IReadOnlyList<InclusionCategory> ListCategories()
    {
        if (this.categories is not null)
        {
            return this.categories;
        }

        var result = new List<InclusionCategory>();

        try
        {
            var shops = Svc.Data.GetExcelSheet<InclusionShop>();
            var categorySheet = Svc.Data.GetExcelSheet<InclusionShopCategory>();
            var seriesSheet = Svc.Data.GetSubrowExcelSheet<InclusionShopSeries>();
            var specialShops = Svc.Data.GetExcelSheet<SpecialShop>();

            if (shops is null || categorySheet is null || seriesSheet is null || specialShops is null)
            {
                this.anomalyLog.Error("Inclusion", "シートを読めないため交換の一覧を作れません");
                return this.categories = result;
            }

            // 系統名 → 種別（SpecialShop の重複は取り除く）
            var merged = new Dictionary<string, List<InclusionSeries>>();
            var order = new List<string>();

            foreach (var shop in shops)
            {
                foreach (var categoryRef in shop.Category)
                {
                    if (categoryRef.RowId == 0 || !categorySheet.TryGetRow(categoryRef.RowId, out var category))
                    {
                        continue;
                    }

                    var categoryName = category.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(categoryName))
                    {
                        continue;
                    }

                    var seriesId = category.InclusionShopSeries.RowId;
                    if (seriesId == 0 || !seriesSheet.TryGetSubrowCount(seriesId, out var count))
                    {
                        continue;
                    }

                    if (!merged.TryGetValue(categoryName, out var list))
                    {
                        merged[categoryName] = list = [];
                        order.Add(categoryName);
                    }

                    for (ushort sub = 0; sub < count; sub++)
                    {
                        if (!seriesSheet.TryGetSubrow(seriesId, sub, out var row))
                        {
                            continue;
                        }

                        var specialShopId = row.SpecialShop.RowId;
                        if (specialShopId == 0 || list.Any(x => x.SpecialShopId == specialShopId))
                        {
                            continue;
                        }

                        var name = specialShops.GetRowOrDefault(specialShopId)?.Name.ExtractText() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        list.Add(new InclusionSeries(specialShopId, name));
                    }
                }
            }

            foreach (var name in order)
            {
                var series = merged[name];
                if (series.Count > 0)
                {
                    result.Add(new InclusionCategory(name, series));
                }
            }

            this.anomalyLog.Info(
                "Inclusion",
                $"交換の一覧を作りました（系統 {result.Count} 件 / 種別 {result.Sum(x => x.Series.Count)} 件）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Inclusion", $"交換の一覧を作れませんでした: {ex.Message}");
        }

        return this.categories = result;
    }

    /// <summary>
    /// 1 つの種別に並ぶ品を読む。
    ///
    /// 開いたときに初めて読む。一度読んだものは覚えておく。
    /// currencyItemId を指定すると、その通貨で買えるものだけに絞る。
    /// </summary>
    public IReadOnlyList<InclusionOffer> ListOffers(uint specialShopId, uint currencyItemId)
    {
        if (!this.offerCache.TryGetValue(specialShopId, out var all))
        {
            all = this.ReadOffers(specialShopId);
            this.offerCache[specialShopId] = all;
        }

        if (currencyItemId == 0)
        {
            return all;
        }

        return all.Where(x => x.CurrencyItemId == currencyItemId).ToList();
    }

    private List<InclusionOffer> ReadOffers(uint specialShopId)
    {
        var offers = new List<InclusionOffer>();

        try
        {
            var specialShops = Svc.Data.GetExcelSheet<SpecialShop>();
            var items = Svc.Data.GetExcelSheet<Item>();

            if (specialShops is null || !specialShops.TryGetRow(specialShopId, out var shop))
            {
                return offers;
            }

            foreach (var entry in shop.Item)
            {
                // 報酬。既定値のプロパティに触ると ExcelPage が null で落ちるため、
                // 必ずループで取り出す。
                uint rewardItemId = 0;
                uint rewardQuantity = 0;
                var rewardCount = 0;

                foreach (var receive in entry.ReceiveItems)
                {
                    if (receive.Item.RowId == 0)
                    {
                        continue;
                    }

                    rewardCount++;
                    if (rewardItemId != 0)
                    {
                        continue;
                    }

                    rewardItemId = receive.Item.RowId;
                    rewardQuantity = receive.ReceiveCount;
                }

                // 報酬が複数あるものは 1 通貨 1 アイテムの形で表せない。出さない。
                if (rewardItemId == 0 || rewardCount != 1)
                {
                    continue;
                }

                uint costItemId = 0;
                uint costAmount = 0;
                var costCount = 0;

                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.ItemCost.RowId == 0)
                    {
                        continue;
                    }

                    costCount++;
                    if (costItemId != 0)
                    {
                        continue;
                    }

                    costItemId = cost.ItemCost.RowId;
                    costAmount = cost.CurrencyCost;
                }

                // コストが複数あるものは「通貨が減った AND アイテムが増えた」で検証しきれない。
                if (costItemId == 0 || costCount != 1)
                {
                    continue;
                }

                var name = items?.GetRowOrDefault(rewardItemId)?.Name.ExtractText() ?? $"<{rewardItemId}>";

                offers.Add(new InclusionOffer(rewardItemId, name, rewardQuantity, costItemId, costAmount));
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Inclusion", $"品を読めませんでした（SpecialShop {specialShopId}）: {ex.Message}");
        }

        return offers;
    }

    /// <summary>
    /// 名前で探す。系統も種別も横断する。
    ///
    /// 「この辺りにあった」で探せない場合の逃げ道。
    /// すべての種別を読むことになるため、入力されたときだけ呼ぶこと。
    /// </summary>
    public IReadOnlyList<(InclusionCategory Category, InclusionSeries Series, InclusionOffer Offer)> Search(
        string keyword,
        uint currencyItemId,
        int limit = 60)
    {
        var hits = new List<(InclusionCategory, InclusionSeries, InclusionOffer)>();

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return hits;
        }

        foreach (var category in this.ListCategories())
        {
            foreach (var series in category.Series)
            {
                foreach (var offer in this.ListOffers(series.SpecialShopId, currencyItemId))
                {
                    if (offer.RewardName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add((category, series, offer));

                        if (hits.Count >= limit)
                        {
                            return hits;
                        }
                    }
                }
            }
        }

        return hits;
    }
}
