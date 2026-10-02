using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>索引構築の進行段階。</summary>
public enum ResolverBuildStage
{
    NotStarted,
    ScanningShops,
    ScanningNpcs,
    ResolvingLocations,
    Completed,
    Failed,
}

/// <summary>
/// ゲームデータから交換定義を解決する。
///
/// ENpcBase は約 6 万行 × 32 要素あるため、一括で走査するとフレーム落ちする。
/// Framework.Update から TickBuild を呼び、1 フレームあたりの処理行数を制限する。
/// </summary>
public sealed class ExchangeResolver(
    AnomalyLog anomalyLog,
    TomestoneService tomestoneService,
    NpcLocationService npcLocationService,
    SpecialCurrencyMap specialCurrencyMap,
    NpcShopLinkMap npcShopLinks,
    SharedFateRankService sharedFateRanks,
    FateShopRankMap fateShopRanks)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly TomestoneService tomestoneService = tomestoneService;
    private readonly NpcLocationService npcLocationService = npcLocationService;
    private readonly SpecialCurrencyMap specialCurrencyMap = specialCurrencyMap;

    /// <summary>シートから辿れない交換所を NPC に結びつける表。</summary>
    private readonly NpcShopLinkMap npcShopLinks = npcShopLinks;

    /// <summary>F.A.T.E達成度。都市の交易商が使えるかの判定に要る。</summary>
    private readonly SharedFateRankService sharedFateRanks = sharedFateRanks;

    /// <summary>品ごとの「何ランクで解放されるか」。</summary>
    private readonly FateShopRankMap fateShopRanks = fateShopRanks;

    /// <summary>構築中の一時データ: ShopId → そのショップ内の該当エントリ。</summary>
    private readonly Dictionary<uint, List<ShopEntryRecord>> shopEntries = [];

    /// <summary>構築中の一時データ: ShopId → その ShopId を持つ NPC の集合。</summary>
    private readonly Dictionary<uint, List<NpcHandlerRecord>> shopToNpcs = [];

    /// <summary>ShopId → SpecialShop.Name。会話メニューの選択肢と照合するために保持する。</summary>
    private readonly Dictionary<uint, string> shopNames = [];

    /// <summary>
    /// (ShopId, NpcId) → InclusionShop の経路。
    ///
    /// 同じ SpecialShop が複数の InclusionShop・複数のカテゴリにぶら下がるため、
    /// ShopId だけをキーにすると後から見つかった NPC のカテゴリで上書きされ、
    /// 別の NPC に対して誤ったカテゴリを使うことになる。
    /// </summary>
    private readonly Dictionary<(uint ShopId, uint NpcId), InclusionPath> inclusionPaths = [];

    private List<ExchangeDefinition> results = [];
    private IReadOnlyList<ExchangeDefinition>? liveResults;

    /// <summary>GroupByReward の結果。UI から毎フレーム呼ばれるため、作り直さない。</summary>
    private List<ExchangeCandidateGroup>? groupedAll;
    private List<ExchangeCandidateGroup>? groupedWithLocation;

    /// <summary>通貨ごとの構築済み結果。監視で通貨を切り替えるたびに作り直さないために持つ。</summary>
    private readonly Dictionary<uint, List<ExchangeDefinition>> cacheByCurrency = [];
    private uint targetCurrencyItemId;
    private uint enpcCursor;

    public ResolverBuildStage Stage { get; private set; } = ResolverBuildStage.NotStarted;

    public float BuildProgress { get; private set; }

    public uint TargetCurrencyItemId => this.targetCurrencyItemId;

    public IReadOnlyList<ExchangeDefinition> Results => this.results;

    /// <summary>ShopId → そのショップが持つ品目の数。撃つ前の index の上限に使う。</summary>
    private readonly Dictionary<uint, int> shopItemCounts = [];

    /// <summary>
    /// そのショップが持つ品目の数を返す。
    ///
    /// **画面に出ている件数を index の上限に使ってはいけない。**
    /// 交換画面は区分（武具 / 防具 / アクセサリ / その他）ごとにしか品を出さないが、
    /// 発火に渡す index は**ショップ全体での通し番号**で、画面の件数とは無関係である。
    ///
    /// 実データ（2026-09-23）: ジルコンの Shop 1770911 は全 37 件。
    /// アクセサリへ切り替えると画面は 12 件になるが、その 12 件の index は 25〜36。
    /// 画面の件数を上限にすると、12 件すべてが範囲外として弾かれる。
    ///
    /// 同じ画面を動かしている ICE の実働テーブルでも、防具タブは 20 件しか出ないのに
    /// index は 14〜42 を使っている（fork-ICE/ICE/Utilities/Shop_Cosmocredits.cs）。
    ///
    /// 数はシートから実行時に引く。件数をコードへ埋め込まない。
    /// 索引の構築状態に依存しないよう、ここで直接シートを読んで覚える。
    /// </summary>
    public bool TryGetShopItemCount(uint shopId, out int count)
    {
        count = 0;

        if (shopId == 0)
        {
            return false;
        }

        if (this.shopItemCounts.TryGetValue(shopId, out count))
        {
            return true;
        }

        try
        {
            var shops = Svc.Data.GetExcelSheet<SpecialShop>();

            if (shops is null || !shops.TryGetRow(shopId, out var shop))
            {
                return false;
            }

            var found = 0;

            foreach (var entry in shop.Item)
            {
                foreach (var receive in entry.ReceiveItems)
                {
                    if (receive.Item.RowId != 0)
                    {
                        found++;
                        break;
                    }
                }
            }

            if (found == 0)
            {
                return false;
            }

            this.shopItemCounts[shopId] = found;
            count = found;
            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Resolver", $"Shop {shopId} の品目数を読めませんでした: {ex.Message}");
            return false;
        }
    }

    /// <summary>指定通貨で購入できる交換定義の索引構築を開始する。</summary>
    public void InvalidateCurrencies()
    {
        this.cacheByCurrency.Clear();
        this.BeginBuild(this.targetCurrencyItemId, true);
    }

    public void BeginBuild(uint currencyItemId) => this.BeginBuild(currencyItemId, false);

    /// <summary>
    /// 索引を構築する。すでに同じ通貨で構築済みならキャッシュを使う。
    /// forceRebuild を指定すると作り直す。
    /// </summary>
    public void BeginBuild(uint currencyItemId, bool forceRebuild)
    {
        if (!forceRebuild && currencyItemId != 0 && this.cacheByCurrency.TryGetValue(currencyItemId, out var cached))
        {
            if (this.targetCurrencyItemId == currencyItemId && this.Stage == ResolverBuildStage.Completed)
            {
                // すでにこの通貨で構築済み。作り直すとキャッシュが無駄になる。
                return;
            }

            this.targetCurrencyItemId = currencyItemId;
            this.results = cached;
            this.liveResults = null;
            this.groupedAll = null;
            this.groupedWithLocation = null;
            this.BuildProgress = 1f;
            this.Stage = ResolverBuildStage.Completed;
            return;
        }

        // **同じ通貨で作っている最中なら、何もしない。**
        //
        // これが無かったため、キャッシュに無い通貨で毎フレーム呼ぶと
        // 毎フレームここへ落ちて、作りかけを捨てて最初からやり直していた。
        // 進むのは次のフレームの TickBuild で 1 段だけなので、
        // <b>索引が永久に完成しない</b>。画面は毎フレーム呼ぶので、
        // プリセットタブを開いているあいだずっとこの状態になる。
        //
        // 上の早期 return は Stage == Completed のときしか効かず、
        // 構築中（ScanningShops / ScanningNpcs）は素通りしていた。
        if (!forceRebuild
            && currencyItemId != 0
            && this.targetCurrencyItemId == currencyItemId
            && this.Stage is ResolverBuildStage.ScanningShops
                          or ResolverBuildStage.ScanningNpcs
                          or ResolverBuildStage.ResolvingLocations)
        {
            return;
        }

        this.shopEntries.Clear();
        this.shopToNpcs.Clear();
        this.shopNames.Clear();
        this.inclusionPaths.Clear();
        this.results = [];
        this.liveResults = null;
        this.groupedAll = null;
        this.groupedWithLocation = null;
        this.targetCurrencyItemId = currencyItemId;
        this.enpcCursor = 0;
        this.BuildProgress = 0f;
        this.Stage = currencyItemId == 0 ? ResolverBuildStage.Failed : ResolverBuildStage.ScanningShops;
    }

    /// <summary>1 フレーム分の処理を進める。true を返したら完了（成功・失敗どちらも）。</summary>
    public bool TickBuild(int npcRowBudget = 4000)
    {
        try
        {
            switch (this.Stage)
            {
                case ResolverBuildStage.ScanningShops:
                    this.ScanShops();
                    return false;

                case ResolverBuildStage.ScanningNpcs:
                    this.ScanNpcs(npcRowBudget);
                    return false;

                case ResolverBuildStage.ResolvingLocations:
                    this.ResolveLocations();
                    return true;

                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Resolver", $"交換定義の索引構築に失敗しました: {ex.Message}");
            this.Stage = ResolverBuildStage.Failed;
            return true;
        }
    }

    /// <summary>
    /// SpecialShop 全体を走査し、対象通貨をコストとするエントリを集める。
    /// 1489 行程度なので 1 フレームで処理して問題ない。
    /// </summary>
    private void ScanShops()
    {
        var shops = Svc.Data.GetExcelSheet<SpecialShop>();
        if (shops is null)
        {
            this.Stage = ResolverBuildStage.Failed;
            return;
        }

        foreach (var shop in shops)
        {
            var entryIndex = -1;
            foreach (var entry in shop.Item)
            {
                entryIndex++;

                // 報酬が入っていない行はパディング。
                // 注意: FirstOrDefault で取得した既定値のプロパティに触ると
                // ExcelPage が null のため NullReferenceException になる。必ずループで取り出す。
                uint rewardItemId = 0;
                uint rewardCount = 0;
                var rewardHq = false;
                var rewardEntries = 0;
                foreach (var receive in entry.ReceiveItems)
                {
                    if (receive.Item.RowId == 0)
                    {
                        continue;
                    }

                    rewardEntries++;
                    if (rewardItemId != 0)
                    {
                        continue;
                    }

                    rewardItemId = receive.Item.RowId;
                    rewardCount = receive.ReceiveCount;
                    rewardHq = receive.ReceiveHq;
                }

                if (rewardItemId == 0)
                {
                    continue;
                }

                // この品がショップ画面のどの区分に出るか。
                //
                // **交換画面は区分ごとに 1 つずつしか品を出さない。**
                // 同じショップでも、防具の区分を開いているあいだはアクセサリの品が見えない。
                // 区分を合わせないと、正しいショップを選んでも目的の品に届かない。
                //
                // 複数入っている行が実データにあるため（武器ショップの先頭など）、
                // 最初の非 0 を採る。
                uint itemCategory = 0;
                foreach (var category in entry.Category)
                {
                    if (category.RowId != 0)
                    {
                        itemCategory = category.RowId;
                        break;
                    }
                }

                // **コストは 1 種類とは限らない。**
                //
                // 武器の交換は「詩片 + 強化素材」のように 2 つ払う。
                // 対象の通貨ぶんだけを見て残りを捨てると、
                // 素材を持っていないのに交換所まで行って空振りする。
                //
                // ここで払うものを全部拾っておき、実行前の確認と、
                // 交換後の「払ったぶんが減ったか」の検証に使う。
                // 対象の通貨ぶんと、それ以外とに仕分ける。
                //
                // **1 エントリにつき記録は 1 件にする。**
                // コスト行ごとに記録を作ると、同じ通貨が 2 行に分かれている
                // エントリで同じ index の記録が 2 件でき、重複として弾かれる。
                var currencyCost = 0u;
                byte currencyCostType = 0;
                var hasCurrency = false;
                var extras = new List<ExchangeCost>();
                var unresolved = false;

                foreach (var cost in entry.ItemCosts)
                {
                    // 使っていないコスト枠。以前からここで数えていない。
                    if (cost.CurrencyCost == 0 || cost.ItemCost.RowId == 0)
                    {
                        continue;
                    }

                    // 実 ItemId へ解決できないものも「払うものがある」事実は残す。
                    // 何を払うか確定できない以上、そのエントリは実行させない。
                    if (!this.TryResolveCostCurrency(cost.CostType, cost.ItemCost.RowId, out var costItemId))
                    {
                        unresolved = true;
                        continue;
                    }

                    if (costItemId == this.targetCurrencyItemId)
                    {
                        // 同じ通貨が複数行に分かれていることがある。足し合わせる。
                        currencyCost += cost.CurrencyCost;
                        currencyCostType = cost.CostType;
                        hasCurrency = true;
                        continue;
                    }

                    extras.Add(new ExchangeCost(costItemId, cost.CurrencyCost));
                }

                // この通貨では買えないエントリ。
                if (!hasCurrency)
                {
                    continue;
                }

                if (!this.shopEntries.TryGetValue(shop.RowId, out var list))
                {
                    this.shopEntries[shop.RowId] = list = [];
                    this.shopNames[shop.RowId] = shop.Name.ExtractText();
                }

                list.Add(new ShopEntryRecord(
                    entryIndex,
                    rewardItemId,
                    rewardCount == 0 ? 1u : rewardCount,
                    rewardHq,
                    currencyCost,
                    currencyCostType,
                    rewardEntries == 1,
                    extras.Count == 0 && !unresolved,
                    itemCategory,
                    extras,
                    unresolved));
            }
        }

        this.BuildProgress = 0.1f;
        this.Stage = this.shopEntries.Count == 0 ? ResolverBuildStage.ResolvingLocations : ResolverBuildStage.ScanningNpcs;
    }

    /// <summary>
    /// コストの表現を実 ItemId へ解決する。
    /// 解決できない表現（特殊通貨バケット等）は false を返し、呼び出し側でスキップさせる。
    /// </summary>
    private bool TryResolveCostCurrency(byte costType, uint costRowId, out uint itemId)
    {
        itemId = 0;
        switch (costType)
        {
            case SpecialShopCostType.DirectItem:
            case SpecialShopCostType.DirectItemAlt:
                // 実 ItemId。8 未満は特殊表現の名残なので採用しない。
                if (costRowId < 8)
                {
                    return false;
                }

                itemId = costRowId;
                return true;

            case SpecialShopCostType.TomestoneSlot:
                return this.tomestoneService.TryResolveItemId(costRowId, out itemId);

            case SpecialShopCostType.SpecialCurrencyBucket:
                // シート内に対応表が無いため、外部データで解決する。
                // ここでの解決は候補を一覧に出すためのもので、
                // 実際の交換時は画面が持つ本物の通貨 ItemId と照合してから実行する。
                return this.specialCurrencyMap.TryResolve(costRowId, out itemId);

            default:
                return false;
        }
    }

    /// <summary>
    /// ENpcBase を走査し、対象ショップを持つ NPC を集める。
    /// ENpcData は 32 要素すべてを見る。0 が入っていても break してはならない
    /// （先頭が 0 で、その後ろにショップ ID を持つ NPC が実在する）。
    /// </summary>
    private void ScanNpcs(int rowBudget)
    {
        var npcs = Svc.Data.GetExcelSheet<ENpcBase>();
        if (npcs is null)
        {
            this.Stage = ResolverBuildStage.Failed;
            return;
        }

        var total = (uint)npcs.Count;
        var processed = 0;

        while (this.enpcCursor < total && processed < rowBudget)
        {
            processed++;
            var cursor = this.enpcCursor++;

            // ENpcBase の RowId は 1000000 から始まり連番ではない。
            // 位置でのアクセスには GetRowAt を使う。TryGetRow(0..Count) では 1 件も取れない。
            ENpcBase npc;
            try
            {
                npc = npcs.GetRowAt((int)cursor);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            foreach (var handlerRef in npc.ENpcData)
            {
                var handler = handlerRef.RowId;
                if (handler == 0)
                {
                    continue;
                }

                this.InspectHandler(npc.RowId, handler, HandlerPath.Direct, null, 0);
            }
        }

        this.BuildProgress = 0.1f + (0.8f * this.enpcCursor / Math.Max(1u, total));

        if (this.enpcCursor >= total)
        {
            // **シートから辿れないショップを、ここで結びつける。**
            //
            // 一部のショップは ENpcData からどう辿っても届かない。
            // 例：広域交易商 ベリルは CustomTalk を 1 つ持つだけで、
            // その CustomTalk の中身が空（実測 ver 2026.09.15）。
            //
            // 走査を終えたこの時点なら、対象のショップは shopEntries に
            // 載っているので、あとは NPC を教えるだけで済む。
            this.ApplyManualNpcLinks();

            this.Stage = ResolverBuildStage.ResolvingLocations;
        }
    }

    /// <summary>
    /// 外部データで補った「この NPC はこのショップを開く」を当てはめる。
    ///
    /// シートに情報が無いものだけを対象にするので、
    /// すでに NPC が分かっているショップは触らない。
    /// </summary>
    private void ApplyManualNpcLinks()
    {
        var applied = 0;

        foreach (var shop in this.shopEntries.Keys)
        {
            foreach (var npcId in this.npcShopLinks.NpcsFor(shop))
            {
                // 走査で見つかっていれば、そちらを優先する。
                // 手で書いた表より、ゲームのデータのほうが確かなので。
                if (this.shopToNpcs.TryGetValue(shop, out var existing) && existing.Count > 0)
                {
                    continue;
                }

                this.RecordNpc(shop, npcId, HandlerPath.Direct, null);
                applied++;
            }
        }

        if (applied > 0)
        {
            this.anomalyLog.Info(
                "Exchange",
                $"シートから辿れない交換所 {applied} 件に、外部データの NPC を当てはめました");
        }
    }

    /// <summary>
    /// 1 つのハンドラ ID を検査し、対象ショップに到達できるなら記録する。
    /// depth は間接参照の深さ。循環参照で無限再帰しないよう上限を設ける。
    /// </summary>
    private void InspectHandler(uint npcId, uint handler, HandlerPath path, string? menuHint, int depth)
    {
        if (depth > 3)
        {
            return;
        }

        var type = EventHandlerType.Of(handler);

        switch (type)
        {
            case EventHandlerType.SpecialShop:
                if (this.shopEntries.ContainsKey(handler))
                {
                    this.RecordNpc(handler, npcId, path, menuHint);
                }

                return;

            case EventHandlerType.PreHandler:
            {
                var pre = Svc.Data.GetExcelSheet<PreHandler>()?.GetRowOrDefault(handler);
                if (pre is null)
                {
                    return;
                }

                var target = pre.Value.Target.RowId;
                if (target != 0)
                {
                    this.InspectHandler(npcId, target, HandlerPath.PreHandler, menuHint, depth + 1);
                }

                return;
            }

            case EventHandlerType.TopicSelect:
            {
                var topic = Svc.Data.GetExcelSheet<TopicSelect>()?.GetRowOrDefault(handler);
                if (topic is null)
                {
                    return;
                }

                var hint = topic.Value.Name.ExtractText();
                foreach (var shopRef in topic.Value.Shop)
                {
                    if (shopRef.RowId == 0)
                    {
                        continue;
                    }

                    this.InspectHandler(npcId, shopRef.RowId, HandlerPath.TopicSelect, string.IsNullOrEmpty(hint) ? menuHint : hint, depth + 1);
                }

                return;
            }

            case EventHandlerType.InclusionShop:
            {
                // スクリップ交換はこの経路にしか無い。
                // NPC → PreHandler → InclusionShop → Category → Series → SpecialShop と辿る。
                var inclusion = Svc.Data.GetExcelSheet<InclusionShop>()?.GetRowOrDefault(handler);
                if (inclusion is null)
                {
                    return;
                }

                var shopName = inclusion.Value.ShopName.ExtractText();
                var categorySheet = Svc.Data.GetExcelSheet<InclusionShopCategory>();
                var seriesSheet = Svc.Data.GetSubrowExcelSheet<InclusionShopSeries>();
                if (categorySheet is null || seriesSheet is null)
                {
                    return;
                }

                foreach (var categoryRef in inclusion.Value.Category)
                {
                    if (categoryRef.RowId == 0)
                    {
                        continue;
                    }

                    var category = categorySheet.GetRowOrDefault(categoryRef.RowId);
                    if (category is null)
                    {
                        continue;
                    }

                    var categoryName = category.Value.Name.ExtractText();
                    var seriesId = category.Value.InclusionShopSeries.RowId;
                    if (seriesId == 0 || !seriesSheet.TryGetSubrowCount(seriesId, out var seriesCount))
                    {
                        continue;
                    }

                    for (ushort i = 0; i < seriesCount; i++)
                    {
                        var entry = seriesSheet.GetSubrowOrDefault(seriesId, i);
                        if (entry is null)
                        {
                            continue;
                        }

                        var specialShopId = entry.Value.SpecialShop.RowId;
                        if (specialShopId == 0 || !this.shopEntries.ContainsKey(specialShopId))
                        {
                            continue;
                        }

                        // どのカテゴリの中にあるかを、後で画面を操作するときのために覚えておく。
                        // NPC ごとにカテゴリが違うため、NPC も含めたキーで持つ。
                        this.inclusionPaths[(specialShopId, npcId)] = new InclusionPath(
                            handler,
                            string.IsNullOrEmpty(shopName) ? menuHint : shopName,
                            categoryRef.RowId,
                            categoryName);

                        this.RecordNpc(specialShopId, npcId, HandlerPath.InclusionShop, categoryName);
                    }
                }

                return;
            }

            case EventHandlerType.CustomTalk:
            {
                // CustomTalk は構造が一定でないため best-effort で辿る。
                var talk = Svc.Data.GetExcelSheet<CustomTalk>()?.GetRowOrDefault(handler);
                if (talk is null)
                {
                    return;
                }

                var hint = talk.Value.MainOption.ExtractText();
                var nextHint = string.IsNullOrEmpty(hint) ? menuHint : hint;

                foreach (var script in talk.Value.Script)
                {
                    var arg = script.ScriptArg;
                    if (EventHandlerType.Is(arg, EventHandlerType.SpecialShop))
                    {
                        this.InspectHandler(npcId, arg, HandlerPath.CustomTalk, nextHint, depth + 1);
                    }
                }

                var specialLinks = talk.Value.SpecialLinks.RowId;
                if (specialLinks != 0)
                {
                    var nest = Svc.Data.GetSubrowExcelSheet<CustomTalkNestHandlers>();
                    if (nest is not null && nest.TryGetSubrowCount(specialLinks, out var count))
                    {
                        for (ushort i = 0; i < count; i++)
                        {
                            var sub = nest.GetSubrowOrDefault(specialLinks, i);
                            if (sub is null)
                            {
                                continue;
                            }

                            var nested = sub.Value.NestHandler.RowId;
                            if (nested != 0)
                            {
                                this.InspectHandler(npcId, nested, HandlerPath.CustomTalk, nextHint, depth + 1);
                            }
                        }
                    }
                }

                return;
            }

            default:
                return;
        }
    }

    private void RecordNpc(uint shopId, uint npcId, HandlerPath path, string? menuHint)
    {
        if (!this.shopToNpcs.TryGetValue(shopId, out var list))
        {
            this.shopToNpcs[shopId] = list = [];
        }

        // 同一 NPC が複数経路で同じショップに到達することがある。
        // より単純な経路を優先して残すが、InclusionShop 経由は画面の操作方法が違うため
        // 「単純さ」で捨ててはいけない。到達手段として別物なので、こちらを優先する。
        var existing = list.FindIndex(x => x.NpcId == npcId);
        if (existing >= 0)
        {
            var current = list[existing];

            var replace = path == HandlerPath.InclusionShop
                ? current.Path != HandlerPath.InclusionShop
                : current.Path != HandlerPath.InclusionShop && path < current.Path;

            if (replace)
            {
                list[existing] = new NpcHandlerRecord(npcId, path, menuHint);
            }

            return;
        }

        list.Add(new NpcHandlerRecord(npcId, path, menuHint));
    }

    /// <summary>収集したショップ・NPC に座標を付けて最終的な交換定義を組み立てる。</summary>
    private void ResolveLocations()
    {
        var definitions = new List<ExchangeDefinition>();

        foreach (var (shopId, entries) in this.shopEntries)
        {
            this.shopToNpcs.TryGetValue(shopId, out var npcs);

            foreach (var entry in entries)
            {
                if (npcs is null || npcs.Count == 0)
                {
                    // NPC が特定できないエントリも、存在自体は UI に出す（原因調査のため）。
                    definitions.Add(new ExchangeDefinition
                    {
                        ShopId = shopId,
                        SheetEntryIndex = entry.EntryIndex,
                        CurrencyItemId = this.targetCurrencyItemId,
                        CurrencyCost = entry.CurrencyCost,
                        RewardItemId = entry.RewardItemId,
                        RewardQuantity = entry.RewardQuantity,
                        RewardHq = entry.RewardHq,
                        CostType = entry.CostType,
                        SingleReward = entry.SingleReward,
                        SingleCost = entry.SingleCost,
                        ExtraCosts = entry.ExtraCosts,
                        HasUnresolvedCost = entry.HasUnresolvedCost,
                        ItemCategory = entry.ItemCategory,
                        ShopName = this.shopNames.GetValueOrDefault(shopId, string.Empty),
                        RequiredRank = this.fateShopRanks.RequiredStage(shopId, entry.RewardItemId),
                    });
                    continue;
                }

                foreach (var npc in npcs)
                {
                    var hasLocation = this.npcLocationService.TryGet(npc.NpcId, out var location);

                    definitions.Add(new ExchangeDefinition
                    {
                        ShopId = shopId,
                        SheetEntryIndex = entry.EntryIndex,
                        CurrencyItemId = this.targetCurrencyItemId,
                        CurrencyCost = entry.CurrencyCost,
                        RewardItemId = entry.RewardItemId,
                        RewardQuantity = entry.RewardQuantity,
                        RewardHq = entry.RewardHq,
                        CostType = entry.CostType,
                        SingleReward = entry.SingleReward,
                        SingleCost = entry.SingleCost,
                        ExtraCosts = entry.ExtraCosts,
                        HasUnresolvedCost = entry.HasUnresolvedCost,
                        ItemCategory = entry.ItemCategory,
                        ShopName = this.shopNames.GetValueOrDefault(shopId, string.Empty),
                        Inclusion = this.inclusionPaths.GetValueOrDefault((shopId, npc.NpcId)),
                        NpcDataId = npc.NpcId,
                        NpcName = NpcLocationService.GetName(npc.NpcId),
                        TerritoryId = hasLocation ? location.TerritoryId : 0,
                        NpcPosition = hasLocation ? location.Position : default,
                        Path = npc.Path,
                        MenuHint = npc.MenuHint,
                        RequiredRank = this.fateShopRanks.RequiredStage(shopId, entry.RewardItemId),
                    });
                }
            }
        }

        this.results = definitions;
        this.liveResults = null;
        this.groupedAll = null;
        this.groupedWithLocation = null;
        this.cacheByCurrency[this.targetCurrencyItemId] = definitions;
        this.BuildProgress = 1f;
        this.Stage = ResolverBuildStage.Completed;

        var withLocation = definitions.Count(x => x.HasLocation);
        this.anomalyLog.Info(
            "Resolver",
            $"通貨 {this.targetCurrencyItemId}: 定義 {definitions.Count} 件（座標解決済み {withLocation} 件 / ショップ {this.shopEntries.Count} 件）を構築しました");
    }

    /// <summary>
    /// NPC から到達できる定義だけを返す。
    ///
    /// Results には過去パッチの到達不能ショップの定義も含まれている（原因調査のために残している）。
    /// 値段の比較や交換の実行には、必ずこちらを使うこと。Results を直接使うと、
    /// 同じアイテムを別の値段で持つ死んだショップの定義を掴む。
    /// </summary>
    /// <summary>その通貨の索引が構築済みか。</summary>
    public bool IsBuiltFor(uint currencyItemId) => this.cacheByCurrency.ContainsKey(currencyItemId);

    public IReadOnlyList<ExchangeDefinition> LiveResults
        => this.liveResults ??= [.. this.results.Where(x => x.HasLocation)];

    /// <summary>報酬アイテムごとにまとめた候補一覧を返す。UI 表示用。</summary>
    /// <summary>
    /// 報酬アイテムごとにまとめた候補一覧を返す。
    ///
    /// UI から毎フレーム呼ばれる。定義が数千件になることがあり、
    /// 呼ばれるたびに集計し直すと描画だけでフレーム時間を使い切る。
    /// 索引を作り直したときにだけ計算する。
    /// </summary>
    public List<ExchangeCandidateGroup> GroupByReward(bool onlyWithLocation)
    {
        if (onlyWithLocation)
        {
            return this.groupedWithLocation ??= this.BuildGroups(true);
        }

        return this.groupedAll ??= this.BuildGroups(false);
    }

    private List<ExchangeCandidateGroup> BuildGroups(bool onlyWithLocation)
    {
        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        return this.results
            .Where(x => !onlyWithLocation || x.HasLocation)
            .GroupBy(x => x.RewardItemId)
            .Select(g => new ExchangeCandidateGroup(
                g.Key,
                itemSheet?.GetRowOrDefault(g.Key)?.Name.ExtractText() ?? $"<{g.Key}>",
                g.OrderBy(x => x.Path).ThenBy(x => x.CurrencyCost).ToList()))
            .OrderBy(x => x.RewardName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// いま実際に使える交換所だけに絞る。
    ///
    /// <b>バイカラージェムの交換所は、F.A.T.E達成度で使える相手が変わる。</b>
    ///
    ///   ・都市の交易商（6 人）…その拡張の 6 マップすべてがランク最大のときだけ使える
    ///   ・各マップの交易商（18 人）…いつでも使えるが、そのマップの品しか扱わない
    ///
    /// 条件を満たしていないのに都市へ行くと、着いても交換できずに終わる。
    /// 逆にすべて最大になったら、都市で全部買えるのでマップの交易商へ行く理由はない。
    /// そこで次のように絞る。
    ///
    ///   ランク未達成 → 都市の交易商を候補から外す
    ///   ランク達成済 → 都市の交易商があるなら、そちらだけを残す
    ///
    /// <b>絞った結果が空になるなら、絞らない。</b>
    /// その品を都市でしか扱っていない場合、外すと交換先が無くなる。
    /// 行っても買えない可能性は残るが、候補を消して「交換できません」と
    /// 言うよりは、行って確かめられるほうがよい。
    ///
    /// F.A.T.E達成度と無関係な通貨では、都市の印が付いた NPC が
    /// そもそも居ないので、この処理は何もしない。
    /// </summary>
    private List<ExchangeDefinition> FilterByUnlock(List<ExchangeDefinition> candidates)
    {
        var city = candidates.Where(x => this.npcShopLinks.IsCityNpc(x.NpcDataId)).ToList();
        if (city.Count == 0)
        {
            // 都市の交易商が候補に居ない。絞る対象が無い。
            return candidates;
        }

        // 解放されているかは拡張ごとに違う。
        //
        // **都市が立つ場所では判定できない。**
        // クリスタリウム(819) や ラザハン(963) は、F.A.T.E達成度の
        // マップ 18 件に含まれていない。そこを渡しても組が見つからない。
        // 判定は必ず「その拡張のマップ」で行う必要がある。
        //
        // 候補にマップの交易商が居ればその territory を使う。
        // 居ない場合（その品を都市でしか扱っていない場合）は、
        // その都市が扱うショップと同じ拡張のマップを表から探す。
        var unlocked = candidates
            .Where(x => !this.npcShopLinks.IsCityNpc(x.NpcDataId))
            .Any(x => this.sharedFateRanks.IsCityShopUnlocked(x.TerritoryId));

        if (!unlocked)
        {
            // 候補にマップの交易商が居ない。都市の相棒となるマップを表から引く。
            foreach (var definition in city)
            {
                var zone = this.npcShopLinks.SampleZoneForCityNpc(definition.NpcDataId);
                if (zone != 0 && this.sharedFateRanks.IsCityShopUnlocked(zone))
                {
                    unlocked = true;
                    break;
                }
            }
        }

        if (unlocked)
        {
            // 全マップ最大。都市でまとめて買えるので、都市だけを残す。
            return city;
        }

        // まだ達成していない。都市へ行っても交換できないので外す。
        var others = candidates.Where(x => !this.npcShopLinks.IsCityNpc(x.NpcDataId)).ToList();
        return others.Count > 0 ? others : candidates;
    }

    /// <summary>
    /// 通貨と報酬アイテムから 1 件へ絞る。
    /// preferredNpcDataId が指定されていればそれを最優先する。
    ///
    /// <b>指定が無いときの選び方（アイテムから交換先を自動で決める）。</b>
    /// バイカラージェムのように、同じ品を 24 人の交易商が別々のエリアで扱う通貨がある。
    /// このとき「行ける場所ならどこでもよい」と選ぶと、目の前に交易商が居るのに
    /// 別大陸へ飛ばすことがある。F.A.T.E 周回中は特に困る。
    /// そこで次の順に見る。
    ///
    ///   1. いま居るエリアの NPC（移動もテレポも要らないので、これが最善）
    ///   2. アクセス済みエーテライトのあるエリア（テレポで行ける）
    ///   3. 経路の単純さ（画面の操作数が少ないほうが失敗しにくい）
    ///   4. 必要な通貨の少なさ
    ///
    /// 1 を足したのがこの版の変更点。2 以降は元のまま。
    /// </summary>
    public ExchangeDefinition? Resolve(uint currencyItemId, uint rewardItemId, uint preferredNpcDataId)
    {
        if (currencyItemId != this.targetCurrencyItemId || this.Stage != ResolverBuildStage.Completed)
        {
            return null;
        }

        var candidates = this.results.Where(x => x.RewardItemId == rewardItemId && x.HasLocation).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        candidates = this.FilterByUnlock(candidates);
        if (candidates.Count == 0)
        {
            return null;
        }

        if (preferredNpcDataId != 0)
        {
            var preferred = candidates.FirstOrDefault(x => x.NpcDataId == preferredNpcDataId);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        // いま居るエリア。取れなければ 0 で、その場合この条件は効かない。
        var currentTerritory = Svc.ClientState.TerritoryType;

        // アクセス済みエーテライトのあるエリアを優先し、次に経路の単純さで選ぶ。
        var reachable = new HashSet<uint>();
        try
        {
            foreach (var entry in Svc.AetheryteList)
            {
                reachable.Add(entry.TerritoryId);
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Resolver", $"エーテライト一覧を取得できませんでした: {ex.Message}");
        }

        return candidates
            .OrderByDescending(x => currentTerritory != 0 && x.TerritoryId == currentTerritory)
            .ThenByDescending(x => reachable.Contains(x.TerritoryId))
            .ThenBy(x => x.Path)
            .ThenBy(x => x.CurrencyCost)
            .First();
    }

    private sealed record ShopEntryRecord(
        int EntryIndex,
        uint RewardItemId,
        uint RewardQuantity,
        bool RewardHq,
        uint CurrencyCost,
        byte CostType,
        bool SingleReward,
        bool SingleCost,
        uint ItemCategory,
        IReadOnlyList<ExchangeCost> ExtraCosts,
        bool HasUnresolvedCost);

    private sealed record NpcHandlerRecord(uint NpcId, HandlerPath Path, string? MenuHint);
}
