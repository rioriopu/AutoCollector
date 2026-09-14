using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;

namespace AutoCollector.Game;

/// <summary>欲しいアイテム 1 件ぶんの逆算。</summary>
/// <param name="Want">最終的に持っていたい数。0 は上限なし。</param>
/// <param name="Held">いま持っている数。</param>
/// <param name="Remaining">あと何個交換すればよいか。</param>
/// <param name="Cost">1 回の交換あたりのスクリップ。**1 個あたりではない。**</param>
/// <param name="PerTrade">1 回の交換でもらえる個数。たいてい 1 だが、まとめて渡す品もある。</param>
/// <param name="Trades">残りをそろえるのに要る交換回数。</param>
/// <param name="Subtotal">残りぶんに要るスクリップ。</param>
public sealed record GoalItem(
    uint RewardItemId,
    string Name,
    int Want,
    int Held,
    int Remaining,
    uint Cost,
    uint PerTrade,
    int Trades,
    long Subtotal,
    bool Unlimited);

/// <summary>
/// 欲しいアイテムから逆算した、いま何が足りないか。
/// </summary>
/// <param name="RequiredScrips">残りを全部交換するのに要るスクリップ。</param>
/// <param name="HeldScrips">いま持っているスクリップ。</param>
/// <param name="MissingScrips">あと稼ぐ必要があるスクリップ。</param>
/// <param name="CollectablesNeeded">そのために作る収集品の個数。上限なしなら 0（空き枠いっぱい）。</param>
/// <param name="Achieved">欲しいアイテムが全部そろっているか。</param>
/// <param name="Endless">
/// 所持の上限がどれにも入っていない。**終わりを決めずに回す。**
///
/// 素材が尽きるまで作って納品し、交換し続ける遊び方。
/// 目標が無いので「届いた」は永遠に来ない。素材が尽きたときに止まる。
/// </param>
/// <param name="CheapestCost">まだ買う必要がある品のうち、いちばん安い費用。0 なら買えるものが無い。</param>
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
    bool Endless,
    uint CheapestCost,
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
    private readonly Dictionary<(uint Reward, uint Currency), (uint Cost, uint PerTrade)> costCache = [];

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

            var (cost, perTrade) = this.FindOffer(entry.RewardItemId, currencyItemId);

            // 上限なしは「いくつ欲しいか」が決まらない。終わりを決めずに回す。
            if (entry.OwnedLimit <= 0)
            {
                items.Add(new GoalItem(entry.RewardItemId, name, 0, held, 0, cost, perTrade, 0, 0, true));
                continue;
            }

            var remaining = Math.Max(0, entry.OwnedLimit - held);

            // **交換 1 回で 2 個以上もらえる品がある。**
            // 個数ぶん交換すると、要るスクリップも作る収集品もその倍数だけ多くなる。
            // 要るのは回数であって個数ではない。
            var trades = (remaining + (int)perTrade - 1) / (int)perTrade;
            var subtotal = (long)trades * cost;

            required += subtotal;
            items.Add(new GoalItem(
                entry.RewardItemId, name, entry.OwnedLimit, held, remaining, cost, perTrade, trades, subtotal, false));
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
            // 上限に桁の大きな数を入れられるため、int に収まるところで頭打ちにする。
            // あふれて負になると「作る個数を計算できません」という的外れな理由で止まる。
            var raw = (missing + collectable.HighReward - 1) / collectable.HighReward;
            needed = (int)Math.Min(int.MaxValue, raw);
        }

        // **上限なしの品が 1 件でもあれば、終わりは無い。**
        //
        // 上限ありと混ぜたとき、上限ありのぶんだけで「届いた」と数えていた。
        // 上限なしの品は 1 個も交換されないまま「目標に届いています」と出て、
        // 二度と走らなくなっていた。
        var hasUnlimited = items.Any(x => x.Unlimited);
        var hasGoal = items.Any(x => !x.Unlimited);
        var endless = hasUnlimited;
        var achieved = hasGoal && !hasUnlimited && items.Where(x => !x.Unlimited).All(x => x.Remaining == 0);

        // まだ買う必要がある品のうち、いちばん安い費用。
        // これだけ持っていれば 1 個は交換できる、という判断に使う。
        var buyable = items
            .Where(x => x.Cost > 0 && (x.Unlimited || x.Remaining > 0))
            .Select(x => x.Cost)
            .ToList();

        var cheapest = buyable.Count > 0 ? buyable.Min() : 0u;

        if (endless)
        {
            notes.Add(hasGoal
                ? "上限なしの品があるため、終わりを決めずに回します（素材が尽きるまで）"
                : "所持の上限が 0 のため、終わりを決めずに回します（素材が尽きるまで）");
        }

        // 費用が引けないと、要るスクリップが 0 になって「もう足りている」に化ける。
        // 黙って進めず、はっきり出す。
        foreach (var item in items.Where(x => x.Cost == 0))
        {
            notes.Add($"{item.Name} の交換費用を読み取れません。この品は計算に入っていません");
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
            endless,
            cheapest,
            notes);
    }

    /// <summary>
    /// 交換費用を引く。
    ///
    /// 交換画面の一覧（InclusionShop）から引く。
    /// 交換候補の索引（ExchangeResolver）は作るのに時間がかかるため、
    /// 費用を知りたいだけのここでは使わない。
    /// </summary>
    private (uint Cost, uint PerTrade) FindOffer(uint rewardItemId, uint currencyItemId)
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
                            var found = (offer.CurrencyCost, Math.Max(1u, offer.RewardQuantity));
                            this.costCache[key] = found;
                            return found;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Goal", $"ItemId {rewardItemId} の交換費用を引けませんでした: {ex.Message}");
        }

        // 引けなかったことは控えない。
        // 交換画面の一覧はあとから作られることがあり、控えると 0 のまま固定されてしまう。
        return (0, 1);
    }

    private static string ItemName(uint itemId)
        => ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            ?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"ItemId {itemId}";
}
