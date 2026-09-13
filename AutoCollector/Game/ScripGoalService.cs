using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;

namespace AutoCollector.Game;

/// <summary>欲しいアイテム 1 件ぶんの逆算。</summary>
/// <param name="Want">最終的に持っていたい数。0 は上限なし。</param>
/// <param name="Held">いま持っている数。</param>
/// <param name="Remaining">あと何個交換すればよいか。</param>
/// <param name="Cost">1 個あたりのスクリップ。</param>
/// <param name="Subtotal">残りぶんに要るスクリップ。</param>
public sealed record GoalItem(
    uint RewardItemId,
    string Name,
    int Want,
    int Held,
    int Remaining,
    uint Cost,
    long Subtotal,
    bool Unlimited);

/// <summary>
/// 欲しいアイテムから逆算した、いま何が足りないか。
/// </summary>
/// <param name="RequiredScrips">残りを全部交換するのに要るスクリップ。</param>
/// <param name="HeldScrips">いま持っているスクリップ。</param>
/// <param name="MissingScrips">あと稼ぐ必要があるスクリップ。</param>
/// <param name="CollectablesNeeded">そのために作る収集品の個数。</param>
/// <param name="Achieved">欲しいアイテムが全部そろっているか。</param>
public sealed record ScripGoal(
    uint CurrencyItemId,
    string CurrencyName,
    IReadOnlyList<GoalItem> Items,
    long RequiredScrips,
    int HeldScrips,
    long MissingScrips,
    CraftableCollectable? Collectable,
    int CollectablesNeeded,
    bool Achieved,
    IReadOnlyList<string> Notes);

/// <summary>
/// 「欲しいアイテム」から「作る収集品の個数」までを逆算する。
///
/// 順番はこうなる。
///
/// <code>
/// 欲しいアイテムと個数
///   → 残りぶんに要るスクリップ（個数 × 交換費用）
///     → 手持ちを引いて、あと稼ぐスクリップ
///       → 作る収集品の個数（稼ぐスクリップ ÷ 1 個あたりの納品報酬）
/// </code>
///
/// **橙貨と紫貨で難しさが違う。**
///
/// 橙貨はジョブごとに収集品が 1 件しかないため、ジョブを決めれば作る物が決まる。
/// 紫貨はレベル帯ごとに複数あり、しかも生むスクリップの量が違う。
/// そのため紫貨は、ジョブに加えてどの収集品を作るかまで決めないと個数が出ない。
///
/// **報酬は最高品質（HighReward）で見積もる。**
/// Artisan は最高品質で納品する前提。届かなかったぶんは次の周で埋まる。
/// 見積もりが外れても、実際のスクリップを見て毎回やり直すため止まらない。
/// </summary>
public sealed class ScripGoalService(
    AnomalyLog anomalyLog,
    CurrencyService currency,
    InclusionShopCatalog catalog,
    CraftPlanService craftPlans,
    CurrencyCatalog currencyCatalog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CurrencyService currency = currency;
    private readonly InclusionShopCatalog catalog = catalog;
    private readonly CraftPlanService craftPlans = craftPlans;
    private readonly CurrencyCatalog currencyCatalog = currencyCatalog;

    /// <summary>
    /// 交換費用の控え。
    ///
    /// 費用を引くには系統と種別を総なめする必要がある。毎フレーム引くと重い。
    /// 交換所の値段は遊んでいるあいだ変わらないので、1 度引けば使い回せる。
    /// </summary>
    private readonly Dictionary<(uint Reward, uint Currency), uint> costCache = [];

    /// <summary>このプリセットの目標を逆算する。通貨を解決できなければ null。</summary>
    public ScripGoal? Build(ExchangePreset preset)
    {
        if (!this.currencyCatalog.TryResolve(preset, out var currencyItemId))
        {
            return null;
        }

        var notes = new List<string>();
        var items = new List<GoalItem>();
        var required = 0L;

        foreach (var entry in preset.Rewards)
        {
            var name = ItemName(entry.RewardItemId);
            var held = this.currency.TryGetCount(entry.RewardItemId, out var owned, includeEquipped: true, includeArmory: true)
                ? owned
                : 0;

            var cost = this.FindCost(entry.RewardItemId, currencyItemId);

            // 上限なしは「いくつ欲しいか」が決まらない。目標にはできない。
            if (entry.OwnedLimit <= 0)
            {
                items.Add(new GoalItem(entry.RewardItemId, name, 0, held, 0, cost, 0, true));
                notes.Add($"{name} は所持の上限が未設定のため、目標の計算に入れていません");
                continue;
            }

            var remaining = Math.Max(0, entry.OwnedLimit - held);
            var subtotal = (long)remaining * cost;

            required += subtotal;
            items.Add(new GoalItem(entry.RewardItemId, name, entry.OwnedLimit, held, remaining, cost, subtotal, false));

            if (cost == 0)
            {
                notes.Add($"{name} の交換費用が分かりません。交換候補の一覧を作り直してください");
            }
        }

        var heldScrips = this.currency.TryGetCount(currencyItemId, out var scrips) ? scrips : 0;
        var missing = Math.Max(0, required - heldScrips);

        var collectable = preset.CraftCollectableItemId == 0
            ? null
            : this.craftPlans.ListCraftable(currencyItemId)
                .FirstOrDefault(x => x.ItemId == preset.CraftCollectableItemId);

        var needed = 0;

        if (collectable is not null && collectable.HighReward > 0 && missing > 0)
        {
            // 端数は切り上げる。足りないまま止まるより、1 個多く作るほうがよい。
            needed = (int)((missing + collectable.HighReward - 1) / collectable.HighReward);
        }

        // 目標が 1 件も立っていないなら達成扱いにしない。何もせず終わったように見える。
        var hasGoal = items.Any(x => !x.Unlimited);
        var achieved = hasGoal && items.Where(x => !x.Unlimited).All(x => x.Remaining == 0);

        if (!hasGoal && preset.Rewards.Count > 0)
        {
            notes.Add("所持の上限を入れると、その数に届くまで自動で作って納品します");
        }

        return new ScripGoal(
            currencyItemId,
            this.currencyCatalog.NameOf(currencyItemId),
            items,
            required,
            heldScrips,
            missing,
            collectable,
            needed,
            achieved,
            notes);
    }

    /// <summary>
    /// 交換費用を引く。
    ///
    /// 交換画面の一覧（InclusionShop）から引く。
    /// 交換候補の索引（ExchangeResolver）は作るのに時間がかかるため、
    /// 費用を知りたいだけのここでは使わない。
    /// </summary>
    private uint FindCost(uint rewardItemId, uint currencyItemId)
    {
        var key = (rewardItemId, currencyItemId);

        if (this.costCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            foreach (var category in this.catalog.ListCategories(currencyItemId))
            {
                foreach (var series in category.Series)
                {
                    foreach (var offer in this.catalog.ListOffers(series.SpecialShopId, currencyItemId))
                    {
                        if (offer.RewardItemId == rewardItemId)
                        {
                            this.costCache[key] = offer.CurrencyCost;
                            return offer.CurrencyCost;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Goal", $"ItemId {rewardItemId} の交換費用を引けませんでした: {ex.Message}");
        }

        return 0;
    }

    private static string ItemName(uint itemId)
        => ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            ?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"ItemId {itemId}";
}
