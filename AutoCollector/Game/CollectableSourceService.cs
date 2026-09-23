using System;
using System.Collections.Generic;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>通貨を生む収集品の出どころ。</summary>
public enum CollectableSource
{
    /// <summary>分からない。</summary>
    None,

    /// <summary>製作する収集品（レシピがある）。</summary>
    Craft,

    /// <summary>採集する収集品（採集地がある）。</summary>
    Gather,
}

/// <summary>
/// この通貨は「作って」稼ぐものか、「採って」稼ぐものか。
///
/// **名前で判断しない。**「ギャザラースクリップ」という文字列を探しにいくと、
/// 言語を変えた利用者の画面で振り分けが崩れる。
///
/// 収集品そのものの**出どころ**で決める。
///
/// <code>
/// 納品すると通貨を生む収集品   CollectableRewardService（出どころを問わない表）
///   ├ レシピがある            → 製作
///   └ 採集地がある            → 採集
/// </code>
///
/// これはゲームデータで決まるので、新しいスクリップが来ても追従する。
/// </summary>
public sealed class CollectableSourceService(AnomalyLog anomalyLog, CollectableRewardService rewards)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CollectableRewardService rewards = rewards;

    /// <summary>通貨ごとの、製作の収集品と採集の収集品の数。</summary>
    private Dictionary<uint, (int Craft, int Gather)>? counts;

    /// <summary>この通貨は製作で稼ぐものか。</summary>
    public bool IsCraft(uint currencyItemId) => this.Classify(currencyItemId) == CollectableSource.Craft;

    /// <summary>この通貨は採集で稼ぐものか。</summary>
    public bool IsGather(uint currencyItemId) => this.Classify(currencyItemId) == CollectableSource.Gather;

    /// <summary>
    /// この通貨の出どころ。
    ///
    /// **両方から出る通貨は想定していない。**
    /// 実データにそれがあった場合は数の多いほうを採り、記録へ残す。
    /// 黙って片方に寄せると、なぜその側に並んだのかが追えない。
    /// </summary>
    public CollectableSource Classify(uint currencyItemId)
    {
        if (currencyItemId == 0)
        {
            return CollectableSource.None;
        }

        if (!this.Build().TryGetValue(currencyItemId, out var pair))
        {
            return CollectableSource.None;
        }

        if (pair.Craft > 0 && pair.Gather > 0)
        {
            this.anomalyLog.Warn(
                "Collectables",
                $"ItemId {currencyItemId} は製作 {pair.Craft} 件・採集 {pair.Gather} 件の収集品から出ます。多いほうで振り分けます");
        }

        if (pair.Craft == 0 && pair.Gather == 0)
        {
            return CollectableSource.None;
        }

        return pair.Craft >= pair.Gather ? CollectableSource.Craft : CollectableSource.Gather;
    }

    /// <summary>数え直す。索引を作り直したときに呼ぶ。</summary>
    public void Invalidate() => this.counts = null;

    private Dictionary<uint, (int Craft, int Gather)> Build()
    {
        if (this.counts is not null)
        {
            return this.counts;
        }

        var result = new Dictionary<uint, (int Craft, int Gather)>();

        try
        {
            var all = this.rewards.ListAll();

            // **索引がまだ空なら、控えない。**
            // 読み込みの途中で 0 件を控えると、以後ずっと「どの通貨も出どころ不明」になる。
            if (all.Count == 0)
            {
                return result;
            }

            var recipes = Svc.Data.GetExcelSheet<Recipe>();
            var gatherings = Svc.Data.GetExcelSheet<GatheringItem>();

            if (recipes is null || gatherings is null)
            {
                this.anomalyLog.Warn("Collectables", "シートを読めないため、収集品の出どころを数えられません");
                return result;
            }

            var recipeResults = new HashSet<uint>();
            foreach (var recipe in recipes)
            {
                var itemId = recipe.ItemResult.RowId;
                if (itemId != 0)
                {
                    recipeResults.Add(itemId);
                }
            }

            var gatheredItems = new HashSet<uint>();
            foreach (var gathering in gatherings)
            {
                var itemId = gathering.Item.RowId;
                if (itemId != 0)
                {
                    gatheredItems.Add(itemId);
                }
            }

            foreach (var (itemId, reward) in all)
            {
                var currency = reward.CurrencyItemId;
                if (currency == 0)
                {
                    continue;
                }

                result.TryGetValue(currency, out var pair);

                // レシピを先に見る。両方に載っている品は実データに無い想定。
                if (recipeResults.Contains(itemId))
                {
                    pair.Craft++;
                }
                else if (gatheredItems.Contains(itemId))
                {
                    pair.Gather++;
                }

                result[currency] = pair;
            }

            this.anomalyLog.Info(
                "Collectables",
                $"収集品の出どころを数えました（通貨 {result.Count} 種 / 収集品 {all.Count} 件）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Collectables", $"収集品の出どころを数えられませんでした: {ex.Message}");
            return result;
        }

        return this.counts = result;
    }
}
