using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>作れる収集品 1 件。</summary>
/// <param name="CraftType">ジョブ。CraftType の行番号（0 木工 〜 7 調理）。</param>
/// <param name="ClassJobLevel">必要なレベル。Lv 帯で絞るのに使う。</param>
/// <param name="NotebookOrder">製作手帳での並び順。小さいほど先。</param>
public sealed record CraftableCollectable(
    uint ItemId,
    string Name,
    uint RecipeId,
    uint CraftType,
    string JobName,
    int ClassJobLevel,
    long NotebookOrder,
    uint CurrencyItemId,
    ushort HighReward,
    int AmountResult);

/// <summary>選べるジョブ 1 件。</summary>
public sealed record CraftJob(uint CraftType, string Name);

/// <summary>作るのに要る素材 1 件。</summary>
/// <param name="PerCraft">1 回あたりの数。</param>
/// <param name="Needed">全部で要る数。</param>
/// <param name="Held">いま鞄にある数。</param>
/// <param name="Shortfall">足りない数。これをリテイナーから引き出す。</param>
/// <param name="NewSlots">引き出したときに新たに要る枠。</param>
/// <param name="IsIntermediate">自分で作れる素材か。無ければ先に作る必要がある。</param>
/// <param name="SubMaterials">
/// 作れる素材の、さらにその素材。
/// リテイナーに完成品が無い場合、こちらを取り出して自分で作ることになる。
/// </param>
/// <param name="RecipeId">作れる素材の場合のレシピ。作れないなら 0。</param>
/// <param name="AmountResult">そのレシピが 1 回で作る数。</param>
/// <param name="IsCrystal">
/// クリスタルか。**鞄ではなく専用の入れ物に入るため、枠を使わない。**
/// 足りなければ引き出す点はほかの素材と同じ。
/// </param>
public sealed record PlanMaterial(
    uint ItemId,
    string Name,
    int PerCraft,
    int Needed,
    int Held,
    int Shortfall,
    int NewSlots,
    bool IsIntermediate,
    uint RecipeId,
    int AmountResult,
    IReadOnlyList<PlanMaterial> SubMaterials,
    bool IsCrystal = false)
{
    /// <summary>
    /// いま鞄にあるものだけで用意できるか。
    ///
    /// **中間素材は「足りない」で終わらせない。**
    /// 黒麦粉が 133 足りなくても、その素材の黒麦が鞄にあるなら自分で作れる。
    /// 製作の手順は中間素材から先に作るようにできているので、そのまま進めてよい。
    ///
    /// 2026-09-14 の実測。黒麦 270 個（黒麦粉 135 個ぶん）を取り出し終えているのに、
    /// 黒麦粉が足りないという理由だけで製作に入らず止まっていた。
    /// </summary>
    public bool CanCoverFromBag()
    {
        if (this.Shortfall <= 0)
        {
            return true;
        }

        // 自分で作れない、または素材を辿れていないなら、足りないまま。
        if (!this.IsIntermediate || this.RecipeId == 0 || this.SubMaterials.Count == 0)
        {
            return false;
        }

        return this.SubMaterials.All(x => x.Shortfall <= 0);
    }
}

/// <summary>作る計画。</summary>
/// <param name="TargetHeld">
/// 計画を立てた時点で、作る物をすでに何個持っているか。
/// 終わりの判定は所持数で行うため、これを足さないと最初から達成済みに見えたり、
/// いつまでも届かなかったりする。
/// </param>
public sealed record CraftPlan(
    CraftableCollectable Target,
    int Crafts,
    int FreeSlots,
    int KeepFree,
    IReadOnlyList<PlanMaterial> Materials,
    IReadOnlyList<string> Notes,
    int TargetHeld = 0);

/// <summary>製作の 1 手順。</summary>
/// <param name="RecipeId">作らせるレシピ。</param>
/// <param name="Crafts">何回作らせるか。個数ではない。</param>
/// <param name="ResultItemId">できあがる品。所持数で終わりを確かめる。</param>
/// <param name="ExpectedCount">終わったときに持っているはずの数。すでに持っているぶんを含む。</param>
public sealed record CraftStep(uint RecipeId, int Crafts, string Name, uint ResultItemId, int ExpectedCount);

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

    /// <summary>
    /// 製作手帳の「RECIPE LEVEL」と同じ区切り。
    ///
    /// **50-60 は Lv60 を含む。** 10 で割った刻みではない。
    /// 実際の手帳（木工 50-60）には Lv50・52・54・56・58・60 の 6 件が並ぶ。
    ///
    /// 製作計画タブとプリセットタブの両方が使う。区切りを 2 か所に持つと、
    /// 片方だけ直したときに同じ収集品が別の帯に出る。
    /// </summary>
    public static readonly (int Min, int Max)[] LevelBands =
    [
        (50, 60),
        (61, 70),
        (71, 80),
        (81, 90),
        (91, 100),
    ];

    private List<CraftableCollectable>? craftables;
    private List<CraftJob>? jobs;
    private uint crystalCategoryRowId;

    /// <summary>
    /// できあがる品 → レシピ の索引。
    ///
    /// **これが無いと表を毎回すべて走る。**
    /// 素材 1 件ごとに Recipe シート（数万行）を端から探していた。
    /// BuildPlan は個数を 1 つずつ下げながら BuildMaterials を呼ぶため、
    /// 空き枠 95・素材 6 種なら 1 回の計算で 570 回の全件走査になる。
    /// しかもフレームワークスレッドで動く。ここは必ず索引で引く。
    /// </summary>
    private Dictionary<uint, uint>? recipeByResult;

    /// <summary>
    /// このスクリップを生む、作れる収集品の一覧。
    ///
    /// 並びは製作手帳の収集品欄と同じにする。
    /// 名前順やもらえる量の順では、手帳と見比べたときに探せない。
    /// </summary>
    public IReadOnlyList<CraftableCollectable> ListCraftable(uint currencyItemId)
        => this.Build()
            .Where(x => x.CurrencyItemId == currencyItemId)
            .OrderBy(x => x.NotebookOrder)
            .ToList();

    /// <summary>
    /// この通貨を生む収集品が 1 つでもあるか。
    ///
    /// **一覧を作らない。**画面は毎フレーム描かれるので、
    /// そのたびに絞り込んで並べ替えた一覧を作ると、描画だけで重くなる。
    /// 有無だけを聞かれているなら、有無だけ答える。
    /// </summary>
    public bool HasCraftableFor(uint currencyItemId)
        => currencyItemId != 0 && this.Build().Any(x => x.CurrencyItemId == currencyItemId);

    /// <summary>クラフターのジョブ一覧。</summary>
    public IReadOnlyList<CraftJob> ListJobs()
    {
        if (this.jobs is not null)
        {
            return this.jobs;
        }

        var result = new List<CraftJob>();

        try
        {
            var craftTypes = Svc.Data.GetExcelSheet<CraftType>();

            if (craftTypes is not null)
            {
                foreach (var craftType in craftTypes)
                {
                    var name = craftType.Name.ExtractText();

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.Add(new CraftJob(craftType.RowId, name));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Craft", $"ジョブの一覧を作れませんでした: {ex.Message}");
        }

        return this.jobs = result;
    }

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
            var levelTable = Svc.Data.GetExcelSheet<RecipeLevelTable>();

            // 製作手帳の並び。行の番号と、その行の中の位置で決まる。
            var notebookOrder = BuildNotebookOrder();

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
                var level = levelTable?.GetRowOrDefault(recipe.RecipeLevelTable.RowId)?.ClassJobLevel ?? 0;

                result.Add(new CraftableCollectable(
                    resultItemId,
                    name,
                    recipe.RowId,
                    recipe.CraftType.RowId,
                    job,
                    level,
                    notebookOrder.TryGetValue(recipe.RowId, out var order) ? order : long.MaxValue,
                    reward.CurrencyItemId,
                    reward.HighReward,
                    Math.Max(1, (int)recipe.AmountResult)));
            }

            this.anomalyLog.Info("Craft", $"作れる収集品を求めました（{result.Count} 件）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Craft", $"作れる収集品を求められませんでした: {ex.Message}");
        }

        return this.craftables = result.OrderBy(x => x.NotebookOrder).ToList();
    }

    /// <summary>
    /// 作れる素材を、その素材まで辿る。
    ///
    /// 1 段だけ辿る。何段も辿ると話が大きくなりすぎるうえ、
    /// 実際に必要になるのはたいてい 1 段。
    ///
    /// 実測: 黒麦粉は 1 回で 3 個できて、黒麦を 6 個使う。
    /// 162 個要るなら 54 回で、黒麦が 324 個いる。
    /// </summary>
    private List<PlanMaterial> BuildSubMaterials(uint intermediateItemId, int shortfall)
    {
        var list = new List<PlanMaterial>();

        try
        {
            var recipes = Svc.Data.GetExcelSheet<Recipe>();
            var items = Svc.Data.GetExcelSheet<Item>();

            if (recipes is null || items is null)
            {
                return list;
            }

            if (!this.TryFindRecipeByResult(intermediateItemId, out var recipeRowId)
                || !recipes.TryGetRow(recipeRowId, out var recipe))
            {
                return list;
            }

            var perCraftResult = Math.Max(1, (int)recipe.AmountResult);
            var crafts = (shortfall + perCraftResult - 1) / perCraftResult;

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

                // クリスタルは鞄を使わないが、足りなければ引き出す。
                var isCrystal = this.crystalCategoryRowId != 0
                    && item.ItemUICategory.RowId == this.crystalCategoryRowId;

                var needed = perCraft * crafts;
                var held = this.HeldOf(ingredient.RowId, isCrystal);

                list.Add(new PlanMaterial(
                    ingredient.RowId,
                    item.Name.ExtractText(),
                    perCraft,
                    needed,
                    held,
                    Math.Max(0, needed - held),
                    0,
                    false,
                    0,
                    1,
                    [],
                    isCrystal));
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Craft", $"素材の素材を辿れませんでした: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// レシピ → 製作手帳での並び順。
    ///
    /// 手帳は行ごとにレシピが並んでいる。行の番号と、その行の中の位置で順が決まる。
    /// 名前順やレベル順ではなく、この順で出さないと手帳と見比べられない。
    /// </summary>
    private static Dictionary<uint, long> BuildNotebookOrder()
    {
        var map = new Dictionary<uint, long>();

        try
        {
            var notebook = Svc.Data.GetExcelSheet<RecipeNotebookList>();

            if (notebook is null)
            {
                return map;
            }

            foreach (var row in notebook)
            {
                var index = 0;

                foreach (var recipe in row.Recipe)
                {
                    if (recipe.RowId != 0)
                    {
                        // 行の中は 1000 件も無いので、この掛け方で順が崩れない。
                        map.TryAdd(recipe.RowId, ((long)row.RowId * 1000) + index);
                    }

                    index++;
                }
            }
        }
        catch
        {
            // 引けなければ並べ替えないだけ。動作には影響しない。
        }

        return map;
    }

    /// <summary>
    /// 何個作るか、そのために何の素材が何個いるかを求める。
    ///
    /// 素材も枠を使うため、作る個数と素材の枠は互いに影響する。
    /// 多い方から順に試して、収まる個数を採る。
    /// </summary>
    /// <param name="maxCrafts">
    /// 作る個数の上限。0 なら空き枠いっぱいまで。
    ///
    /// 目標から逆算して回すときに使う。あと 3 個ぶんのスクリップしか要らないのに
    /// 空き枠いっぱいの 40 個を作っても、素材と時間を捨てるだけになる。
    /// </param>
    /// <param name="requireMaterials">
    /// いま鞄にある素材だけで作れる個数に抑える。
    ///
    /// リテイナーから取り出しても足りなかったときに使う。
    /// 足りないまま Artisan へ頼んでも、途中で止まるだけで何も進まない。
    /// </param>
    public CraftPlan? BuildPlan(
        uint collectableItemId,
        int keepFreeSlots,
        int maxCrafts = 0,
        bool requireMaterials = false)
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

        // 要るぶんより多く作らない。
        var ceiling = maxCrafts > 0 ? Math.Min(usable, maxCrafts) : usable;

        if (ceiling == 0)
        {
            notes.Add("これ以上は作る必要がありません");
            return new CraftPlan(target, 0, freeSlots, keepFreeSlots, [], notes);
        }

        // 多い方から試して、素材の枠まで含めて収まる個数を採る。
        for (var crafts = ceiling; crafts >= 1; crafts--)
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
                // 手持ちの素材だけで作れる個数まで落とす指定なら、
                // 用意できないものが 1 つでもあるあいだは個数を減らし続ける。
                //
                // **中間素材は、その素材が鞄にあるなら用意できる扱いにする。**
                // ここで一緒に数えていたため、黒麦を取り出し終えていても
                // 黒麦粉が足りないという理由だけで 0 個まで落ちていた。
                if (requireMaterials && materials.Any(x => !x.CanCoverFromBag()))
                {
                    continue;
                }

                if (materials.Any(x => x.IsIntermediate && x.Shortfall > 0))
                {
                    notes.Add("自分で作る素材が足りません。先にそちらを作る必要があります");
                }

                if (materials.Any(x => x.Shortfall > 0))
                {
                    notes.Add("足りない素材はリテイナーから引き出します");
                }

                // 作る物をすでに持っているぶん。収集品は GetInventoryItemCount では
                // 数えられないため、鞄の枠を直接見る。
                var held = this.currency.TryGetBagCount(target.ItemId, out var heldCount) ? heldCount : 0;

                return new CraftPlan(target, crafts, freeSlots, keepFreeSlots, materials, notes, held);
            }
        }

        notes.Add(requireMaterials
            ? "いま持っている素材では 1 個も作れません"
            : "素材の置き場も要るため、1 個も作れません");

        return new CraftPlan(target, 0, freeSlots, keepFreeSlots, [], notes);
    }

    /// <summary>
    /// できあがる品からレシピを引く。索引が無ければ 1 度だけ作る。
    ///
    /// 同じ品を作るレシピが複数ある場合は、先に見つけたものを使う。
    /// 元の実装（FirstOrDefault）と同じ選び方になるよう、行番号の小さい方を残す。
    /// </summary>
    private bool TryFindRecipeByResult(uint resultItemId, out uint recipeRowId)
    {
        recipeRowId = 0;

        if (resultItemId == 0)
        {
            return false;
        }

        if (this.recipeByResult is null)
        {
            var map = new Dictionary<uint, uint>();
            var sheet = Svc.Data.GetExcelSheet<Recipe>();

            if (sheet is null)
            {
                return false;
            }

            foreach (var row in sheet)
            {
                if (row.ItemResult.RowId != 0)
                {
                    map.TryAdd(row.ItemResult.RowId, row.RowId);
                }
            }

            this.recipeByResult = map;
            this.anomalyLog.Info("Craft", $"できあがる品からレシピを引く索引を作りました（{map.Count} 件）");
        }

        return this.recipeByResult.TryGetValue(resultItemId, out recipeRowId) && recipeRowId != 0;
    }

    /// <summary>
    /// 所持数。クリスタルは入れ物が違うので、そちらを見る。
    ///
    /// 鞄を見る数え方ではクリスタルは 0 になる。
    /// 0 と読むと、持っているのに「足りない」と判断して引き出しに行くことになる。
    /// </summary>
    private int HeldOf(uint itemId, bool isCrystal)
    {
        var normal = this.currency.TryGetCount(itemId, out var have) ? have : 0;

        if (!isCrystal)
        {
            return normal;
        }

        // **どちらか一方だけを信じない。**
        // 専用の入れ物が読めないことがあり、0 と読むと、9999 個持っている物まで
        // 「足りない」と判断してリテイナーへ取りに行くことになる。
        var crystals = this.currency.TryGetCrystalCount(itemId, out var stock) ? stock : 0;

        return Math.Max(normal, crystals);
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

            // クリスタルは鞄ではなく専用の入れ物に入る。
            // **枠は使わないが、足りなければ引き出す。** 数に入れないと製作が止まる。
            var isCrystal = this.crystalCategoryRowId != 0
                && item.ItemUICategory.RowId == this.crystalCategoryRowId;

            var needed = perCraft * crafts;
            var held = this.HeldOf(ingredient.RowId, isCrystal);
            var shortfall = Math.Max(0, needed - held);

            var newSlots = 0;

            if (!isCrystal)
            {
                var stack = Math.Max(1, (int)item.StackSize);

                // いま持っているぶんで埋まっている枠と、引き出したあとの枠の差。
                var slotsNow = (held + stack - 1) / stack;
                var slotsAfter = (held + shortfall + stack - 1) / stack;
                newSlots = Math.Max(0, slotsAfter - slotsNow);
            }

            totalNewSlots += newSlots;

            var isIntermediate = false;
            var subAmountResult = 1;
            uint subRecipeRowId = 0;

            if (this.TryFindRecipeByResult(ingredient.RowId, out var foundRowId)
                && recipes.TryGetRow(foundRowId, out var subRecipe))
            {
                isIntermediate = true;
                subRecipeRowId = foundRowId;
                subAmountResult = Math.Max(1, (int)subRecipe.AmountResult);
            }

            // 作れる素材が足りないなら、その素材を作るのに要るものまで辿る。
            // リテイナーに完成品が無いとき、これが無いと手が止まる。
            var subMaterials = isIntermediate && shortfall > 0
                ? this.BuildSubMaterials(ingredient.RowId, shortfall)
                : [];

            list.Add(new PlanMaterial(
                ingredient.RowId,
                item.Name.ExtractText(),
                perCraft,
                needed,
                held,
                shortfall,
                newSlots,
                isIntermediate,
                isIntermediate ? subRecipeRowId : 0,
                subAmountResult,
                subMaterials,
                isCrystal));
        }

        return list;
    }
}
