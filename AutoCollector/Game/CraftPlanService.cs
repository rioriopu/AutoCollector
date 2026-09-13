using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>作れる収集品 1 件。</summary>
public sealed record CraftableCollectable(
    uint ItemId,
    string Name,
    uint RecipeId,
    string JobName,
    uint CurrencyItemId,
    ushort HighReward);

/// <summary>作るのに要る素材 1 件。</summary>
/// <param name="PerCraft">1 回あたりの数。</param>
/// <param name="Needed">全部で要る数。</param>
/// <param name="Held">いま鞄にある数。</param>
/// <param name="Shortfall">足りない数。これをリテイナーから引き出す。</param>
/// <param name="NewSlots">引き出したときに新たに要る枠。</param>
/// <param name="IsIntermediate">自分で作れる素材か。無ければ先に作る必要がある。</param>
public sealed record PlanMaterial(
    uint ItemId,
    string Name,
    int PerCraft,
    int Needed,
    int Held,
    int Shortfall,
    int NewSlots,
    bool IsIntermediate);

/// <summary>作る計画。</summary>
public sealed record CraftPlan(
    CraftableCollectable Target,
    int Crafts,
    int FreeSlots,
    int KeepFree,
    IReadOnlyList<PlanMaterial> Materials,
    IReadOnlyList<string> Notes);

/// <summary>
/// 「どの収集品を何個作るか」と「そのために何の素材が何個いるか」を求める。
///
/// 順番はこうなる。
///
/// <code>
/// 欲しいスクリップ（橙 or 紫）
///   → そのスクリップを生む収集品（レシピがあるものだけ）
///     → 作る個数（鞄の空き枠から決まる）
///       → 素材と個数（レシピ × 個数）
/// </code>
///
/// 個数が決まらないと素材の数も決まらない。順番を飛ばせない。
///
/// **収集品は重ならない（StackSize = 1）。** 1 個作ると 1 枠使う。
/// そのため空き枠から作れる個数が一意に決まる。
///
/// クリスタルは鞄ではなく専用の入れ物に入る。枠の計算にも引き出しにも含めない。
/// </summary>
public sealed class CraftPlanService(
    AnomalyLog anomalyLog,
    CollectableRewardService rewards,
    CurrencyService currency)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CollectableRewardService rewards = rewards;
    private readonly CurrencyService currency = currency;

    private List<CraftableCollectable>? craftables;
    private uint crystalCategoryRowId;

    /// <summary>このスクリップを生む、作れる収集品の一覧。</summary>
    public IReadOnlyList<CraftableCollectable> ListCraftable(uint currencyItemId)
        => this.Build().Where(x => x.CurrencyItemId == currencyItemId).ToList();

    /// <summary>作れる収集品の総数。</summary>
    public int KnownCount => this.Build().Count;

    private List<CraftableCollectable> Build()
    {
        if (this.craftables is not null)
        {
            return this.craftables;
        }

        var result = new List<CraftableCollectable>();

        try
        {
            var items = Svc.Data.GetExcelSheet<Item>();
            var recipes = Svc.Data.GetExcelSheet<Recipe>();
            var craftTypes = Svc.Data.GetExcelSheet<CraftType>();
            var categories = Svc.Data.GetExcelSheet<ItemUICategory>();

            if (items is null || recipes is null || craftTypes is null)
            {
                this.anomalyLog.Error("Craft", "シートを読めないため、作れる収集品を求められません");
                return this.craftables = result;
            }

            // クリスタルの分類。名前で引く。番号を埋め込まない。
            if (categories is not null)
            {
                foreach (var category in categories)
                {
                    if (category.Name.ExtractText() == "クリスタル")
                    {
                        this.crystalCategoryRowId = category.RowId;
                        break;
                    }
                }
            }

            // 収集品 → レシピ。同じ品に複数のレシピがあることは想定しない（先に見つけたものを使う）。
            var seen = new HashSet<uint>();

            foreach (var recipe in recipes)
            {
                var resultItemId = recipe.ItemResult.RowId;

                if (resultItemId == 0 || !seen.Add(resultItemId))
                {
                    continue;
                }

                if (!this.rewards.TryResolve(resultItemId, out var reward))
                {
                    continue;
                }

                var name = items.GetRowOrDefault(resultItemId)?.Name.ExtractText() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var job = craftTypes.GetRowOrDefault(recipe.CraftType.RowId)?.Name.ExtractText() ?? string.Empty;

                result.Add(new CraftableCollectable(
                    resultItemId,
                    name,
                    recipe.RowId,
                    job,
                    reward.CurrencyItemId,
                    reward.HighReward));
            }

            this.anomalyLog.Info("Craft", $"作れる収集品を求めました（{result.Count} 件）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Craft", $"作れる収集品を求められませんでした: {ex.Message}");
        }

        return this.craftables = result.OrderBy(x => x.JobName, StringComparer.Ordinal)
            .ThenByDescending(x => x.HighReward)
            .ToList();
    }

    /// <summary>
    /// 何個作るか、そのために何の素材が何個いるかを求める。
    ///
    /// 素材も枠を使うため、作る個数と素材の枠は互いに影響する。
    /// 多い方から順に試して、収まる個数を採る。
    /// </summary>
    public CraftPlan? BuildPlan(uint collectableItemId, int keepFreeSlots)
    {
        var target = this.Build().FirstOrDefault(x => x.ItemId == collectableItemId);

        if (target is null)
        {
            return null;
        }

        var notes = new List<string>();

        if (!this.currency.TryGetEmptyBagSlots(out var freeSlotsRaw))
        {
            notes.Add("鞄の空き枠を読み取れませんでした");
            return new CraftPlan(target, 0, 0, keepFreeSlots, [], notes);
        }

        var freeSlots = (int)freeSlotsRaw;
        var usable = Math.Max(0, freeSlots - Math.Max(0, keepFreeSlots));

        if (usable == 0)
        {
            notes.Add($"空き枠が {freeSlots} で、残す設定が {keepFreeSlots} のため作れません");
            return new CraftPlan(target, 0, freeSlots, keepFreeSlots, [], notes);
        }

        // 多い方から試して、素材の枠まで含めて収まる個数を採る。
        for (var crafts = usable; crafts >= 1; crafts--)
        {
            var materials = this.BuildMaterials(target, crafts, out var materialSlots);

            if (materials is null)
            {
                notes.Add("レシピを読み取れませんでした");
                return new CraftPlan(target, 0, freeSlots, keepFreeSlots, [], notes);
            }

            // 収集品は 1 個 1 枠。素材の枠と合わせて収まるか。
            if (crafts + materialSlots <= usable)
            {
                if (materials.Any(x => x.IsIntermediate && x.Shortfall > 0))
                {
                    notes.Add("自分で作る素材が足りません。先にそちらを作る必要があります");
                }

                if (materials.Any(x => x.Shortfall > 0))
                {
                    notes.Add("足りない素材はリテイナーから引き出します");
                }

                return new CraftPlan(target, crafts, freeSlots, keepFreeSlots, materials, notes);
            }
        }

        notes.Add("素材の置き場も要るため、1 個も作れません");
        return new CraftPlan(target, 0, freeSlots, keepFreeSlots, [], notes);
    }

    /// <summary>この個数を作るのに要る素材と、新たに要る枠数。</summary>
    private List<PlanMaterial>? BuildMaterials(CraftableCollectable target, int crafts, out int totalNewSlots)
    {
        totalNewSlots = 0;

        var recipes = Svc.Data.GetExcelSheet<Recipe>();
        var items = Svc.Data.GetExcelSheet<Item>();

        if (recipes is null || items is null || !recipes.TryGetRow(target.RecipeId, out var recipe))
        {
            return null;
        }

        var list = new List<PlanMaterial>();

        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            var ingredient = recipe.Ingredient[i];
            var perCraft = (int)recipe.AmountIngredient[i];

            if (ingredient.RowId == 0 || perCraft == 0)
            {
                continue;
            }

            if (!items.TryGetRow(ingredient.RowId, out var item))
            {
                continue;
            }

            // クリスタルは鞄ではなく専用の入れ物に入る。枠にも引き出しにも数えない。
            if (this.crystalCategoryRowId != 0 && item.ItemUICategory.RowId == this.crystalCategoryRowId)
            {
                continue;
            }

            var needed = perCraft * crafts;
            var held = this.currency.TryGetCount(ingredient.RowId, out var have) ? have : 0;
            var shortfall = Math.Max(0, needed - held);

            var stack = Math.Max(1, (int)item.StackSize);

            // いま持っているぶんで埋まっている枠と、引き出したあとの枠の差。
            var slotsNow = (held + stack - 1) / stack;
            var slotsAfter = (held + shortfall + stack - 1) / stack;
            var newSlots = Math.Max(0, slotsAfter - slotsNow);

            totalNewSlots += newSlots;

            var isIntermediate = recipes.Any(x => x.ItemResult.RowId == ingredient.RowId);

            list.Add(new PlanMaterial(
                ingredient.RowId,
                item.Name.ExtractText(),
                perCraft,
                needed,
                held,
                shortfall,
                newSlots,
                isIntermediate));
        }

        return list;
    }
}
