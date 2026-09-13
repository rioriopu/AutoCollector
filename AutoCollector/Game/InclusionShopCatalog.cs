using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>交換画面の「種別」1 件。ゲーム内のタブに相当する。</summary>
/// <param name="Name">シート上の名前。例: 紫貨の取引：Lv58～向け【ILv130】</param>
/// <param name="DisplayName">通貨名を落とした表示用。例: Lv58～向け【ILv130】</param>
/// <param name="Currencies">この種別で使う通貨。絞り込みに使う。</param>
public sealed record InclusionSeries(
    uint SpecialShopId,
    string Name,
    string DisplayName,
    IReadOnlyCollection<uint> Currencies);

/// <summary>交換画面の「系統」1 件。ゲーム内のプルダウンに相当する。</summary>
/// <param name="Name">シート上の名前。例: クラフタースクリップの取引：装備品</param>
/// <param name="DisplayName">通貨名を落とした表示用。例: 装備品</param>
public sealed record InclusionCategory(string Name, string DisplayName, IReadOnlyList<InclusionSeries> Series);

/// <summary>種別の中に並ぶ品 1 件。</summary>
/// <param name="ClassJobCategory">画面の並べ替えに使う。職ごとにまとまる。</param>
/// <param name="EquipSlotCategory">画面の並べ替えに使う。職の中で部位順になる。</param>
public sealed record InclusionOffer(
    uint RewardItemId,
    string RewardName,
    uint RewardQuantity,
    uint CurrencyItemId,
    uint CurrencyCost,
    uint ClassJobCategory,
    uint EquipSlotCategory);

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
public sealed class InclusionShopCatalog(
    AnomalyLog anomalyLog,
    TomestoneService tomestoneService,
    SpecialCurrencyMap specialCurrencyMap)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly TomestoneService tomestoneService = tomestoneService;
    private readonly SpecialCurrencyMap specialCurrencyMap = specialCurrencyMap;

    private List<InclusionCategory>? categories;

    /// <summary>
    /// 一覧を作ったとき、特殊通貨の対応表がクライアント由来だったか。
    ///
    /// スクリップのコストは特殊通貨の番号で入っており、実 ItemId への変換が要る。
    /// その対応表は起動直後には同梱の控えしか無く、あとからクライアント由来に入れ替わる。
    /// 控えで作った一覧をそのまま使い続けると、実際と食い違う可能性がある。
    /// </summary>
    private bool builtFromClientCurrencies;

    /// <summary>種別ごとの品。開いたものだけを覚える。</summary>
    private readonly Dictionary<uint, List<InclusionOffer>> offerCache = [];

    /// <summary>系統と種別の一覧。初回の呼び出しで作る。</summary>
    public IReadOnlyList<InclusionCategory> ListCategories()
    {
        // 対応表がクライアント由来へ入れ替わっていたら作り直す。
        if (this.categories is not null &&
            (this.builtFromClientCurrencies || !this.specialCurrencyMap.ResolvedFromClient))
        {
            return this.categories;
        }

        if (this.categories is not null)
        {
            this.anomalyLog.Info("Inclusion", "特殊通貨の対応表が確定したため、交換の一覧を作り直します");
            this.offerCache.Clear();
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

                        // どの通貨で買えるかを控える。プリセットで選んだ通貨に
                        // 関係のない系統や種別を出さないために要る。
                        // ここで読んだ品はそのまま覚えておくので、開いたときは読み直さない。
                        var offers = this.ReadOffers(specialShopId);
                        this.offerCache[specialShopId] = offers;

                        var currencies = new HashSet<uint>();
                        foreach (var offer in offers)
                        {
                            currencies.Add(offer.CurrencyItemId);
                        }

                        if (currencies.Count == 0)
                        {
                            continue;
                        }

                        list.Add(new InclusionSeries(specialShopId, name, Shorten(name), currencies));
                    }
                }
            }

            foreach (var name in order)
            {
                var series = merged[name];
                if (series.Count > 0)
                {
                    result.Add(new InclusionCategory(name, Shorten(name), series));
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

        // 何も作れなかった場合は覚えない。次の呼び出しでやり直す。
        if (result.Count == 0)
        {
            return result;
        }

        this.builtFromClientCurrencies = this.specialCurrencyMap.ResolvedFromClient;
        return this.categories = result;
    }

    /// <summary>
    /// 指定した通貨で買えるものだけに絞った一覧。
    ///
    /// クラフタースクリップを選んでいるのにギャザラーの系統が並ぶと選び違える。
    /// 紫貨を選んでいるなら、橙貨の種別も出さない。
    /// </summary>
    public IReadOnlyList<InclusionCategory> ListCategories(uint currencyItemId)
    {
        var all = this.ListCategories();

        if (currencyItemId == 0)
        {
            return all;
        }

        var result = new List<InclusionCategory>();

        foreach (var category in all)
        {
            var series = category.Series.Where(x => x.Currencies.Contains(currencyItemId)).ToList();

            if (series.Count > 0)
            {
                result.Add(category with { Series = series });
            }
        }

        return result;
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

    /// <summary>
    /// コストの表現を実 ItemId へ解決する。
    /// <see cref="ExchangeResolver"/> と同じ判断でなければ、
    /// 一覧と実際の交換で食い違いが出る。
    /// </summary>
    private bool TryResolveCostCurrency(byte costType, uint costRowId, out uint itemId)
    {
        itemId = 0;

        switch (costType)
        {
            case SpecialShopCostType.DirectItem:
            case SpecialShopCostType.DirectItemAlt:
                // 8 未満は特殊表現の名残なので採用しない。
                if (costRowId < 8)
                {
                    return false;
                }

                itemId = costRowId;
                return true;

            case SpecialShopCostType.TomestoneSlot:
                return this.tomestoneService.TryResolveItemId(costRowId, out itemId);

            case SpecialShopCostType.SpecialCurrencyBucket:
                return this.specialCurrencyMap.TryResolve(costRowId, out itemId);

            default:
                return false;
        }
    }

    /// <summary>
    /// 表示用に通貨名を落とす。
    ///
    /// 「クラフタースクリップの取引：装備品」→「装備品」
    /// 「紫貨の取引：Lv58～向け【ILv130】」→「Lv58～向け【ILv130】」
    ///
    /// どの通貨かはプリセット側で選んでいるため、繰り返す意味がない。
    /// 長い接頭辞が付いたままだと、肝心の部分が読み取りにくい。
    /// </summary>
    private static string Shorten(string name)
    {
        var separator = name.LastIndexOf('：');
        return separator >= 0 && separator + 1 < name.Length ? name[(separator + 1)..] : name;
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

                // コストは ItemCost.RowId をそのまま ItemId として読んではいけない。
                // スクリップは特殊通貨の番号が入っており、そのまま読むと
                // 全く別のアイテム（ウィンドシャード等）になる。
                // CostType を見て解決する。ExchangeResolver と同じ判断。
                uint costItemId = 0;
                uint costAmount = 0;
                var costCount = 0;

                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.CurrencyCost == 0 || cost.ItemCost.RowId == 0)
                    {
                        continue;
                    }

                    costCount++;
                    if (costItemId != 0)
                    {
                        continue;
                    }

                    if (!this.TryResolveCostCurrency(cost.CostType, cost.ItemCost.RowId, out var resolved))
                    {
                        continue;
                    }

                    costItemId = resolved;
                    costAmount = cost.CurrencyCost;
                }

                // コストが複数あるものは「通貨が減った AND アイテムが増えた」で検証しきれない。
                if (costItemId == 0 || costCount != 1)
                {
                    continue;
                }

                var row = items?.GetRowOrDefault(rewardItemId);
                var name = row?.Name.ExtractText() ?? $"<{rewardItemId}>";

                offers.Add(new InclusionOffer(
                    rewardItemId,
                    name,
                    rewardQuantity,
                    costItemId,
                    costAmount,
                    row?.ClassJobCategory.RowId ?? 0,
                    row?.EquipSlotCategory.RowId ?? 0));
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Inclusion", $"品を読めませんでした（SpecialShop {specialShopId}）: {ex.Message}");
        }

        // シートの並びは画面の並びと違う。
        //
        // 実測（【ILv55】職人向け装備・48 件）で確かめた。
        //   シート: 頭(8 職分) → 胴(8 職分) → 脚(8 職分) …  部位ごと
        //   画面  : 木工[道具・頭・胴・手・脚・足] → 鍛冶[…] …  職ごと
        //
        // 職（ClassJobCategory）→ 部位（EquipSlotCategory）の安定ソートで
        // 48 件すべてが観測と一致した。
        //
        // 秘伝書のように職も部位も持たないものは値が揃うため、
        // 安定ソートによりシートの並びがそのまま残る。こちらも観測と合う。
        return offers
            .OrderBy(x => x.ClassJobCategory)
            .ThenBy(x => x.EquipSlotCategory)
            .ToList();
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
