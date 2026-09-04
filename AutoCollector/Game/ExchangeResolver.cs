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
public sealed class ExchangeResolver(AnomalyLog anomalyLog, TomestoneService tomestoneService, NpcLocationService npcLocationService)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly TomestoneService tomestoneService = tomestoneService;
    private readonly NpcLocationService npcLocationService = npcLocationService;

    /// <summary>構築中の一時データ: ShopId → そのショップ内の該当エントリ。</summary>
    private readonly Dictionary<uint, List<ShopEntryRecord>> shopEntries = [];

    /// <summary>構築中の一時データ: ShopId → その ShopId を持つ NPC の集合。</summary>
    private readonly Dictionary<uint, List<NpcHandlerRecord>> shopToNpcs = [];

    private List<ExchangeDefinition> results = [];
    private IReadOnlyList<ExchangeDefinition>? liveResults;
    private uint targetCurrencyItemId;
    private uint enpcCursor;

    public ResolverBuildStage Stage { get; private set; } = ResolverBuildStage.NotStarted;

    public float BuildProgress { get; private set; }

    public uint TargetCurrencyItemId => this.targetCurrencyItemId;

    public IReadOnlyList<ExchangeDefinition> Results => this.results;

    /// <summary>指定通貨で購入できる交換定義の索引構築を開始する。</summary>
    public void BeginBuild(uint currencyItemId)
    {
        this.shopEntries.Clear();
        this.shopToNpcs.Clear();
        this.results = [];
        this.liveResults = null;
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

                // 報酬やコストが複数あるエントリは、1 通貨 1 アイテムのモデルで表せない。
                // 定義としては残すが、交換の実行は事前条件で拒否する。
                var costEntries = 0;
                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.CurrencyCost != 0 && cost.ItemCost.RowId != 0)
                    {
                        costEntries++;
                    }
                }

                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.CurrencyCost == 0)
                    {
                        continue;
                    }

                    if (!this.TryResolveCostCurrency(cost.CostType, cost.ItemCost.RowId, out var costItemId))
                    {
                        continue;
                    }

                    if (costItemId != this.targetCurrencyItemId)
                    {
                        continue;
                    }

                    if (!this.shopEntries.TryGetValue(shop.RowId, out var list))
                    {
                        this.shopEntries[shop.RowId] = list = [];
                    }

                    list.Add(new ShopEntryRecord(
                        entryIndex,
                        rewardItemId,
                        rewardCount == 0 ? 1u : rewardCount,
                        rewardHq,
                        cost.CurrencyCost,
                        cost.CostType,
                        rewardEntries == 1,
                        costEntries == 1));
                }
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
                // Phase 4 で special_currency_map.json を使って解決する。MVP では扱わない。
                return false;

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
            this.Stage = ResolverBuildStage.ResolvingLocations;
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
        // より単純な経路（Direct < PreHandler < TopicSelect < CustomTalk）を優先して残す。
        var existing = list.FindIndex(x => x.NpcId == npcId);
        if (existing >= 0)
        {
            if (path < list[existing].Path)
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
                        NpcDataId = npc.NpcId,
                        NpcName = NpcLocationService.GetName(npc.NpcId),
                        TerritoryId = hasLocation ? location.TerritoryId : 0,
                        NpcPosition = hasLocation ? location.Position : default,
                        Path = npc.Path,
                        MenuHint = npc.MenuHint,
                    });
                }
            }
        }

        this.results = definitions;
        this.liveResults = null;
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
    public IReadOnlyList<ExchangeDefinition> LiveResults
        => this.liveResults ??= [.. this.results.Where(x => x.HasLocation)];

    /// <summary>報酬アイテムごとにまとめた候補一覧を返す。UI 表示用。</summary>
    public List<ExchangeCandidateGroup> GroupByReward(bool onlyWithLocation)
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
    /// 通貨と報酬アイテムから 1 件へ絞る。
    /// preferredNpcDataId が指定されていればそれを最優先する。
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

        if (preferredNpcDataId != 0)
        {
            var preferred = candidates.FirstOrDefault(x => x.NpcDataId == preferredNpcDataId);
            if (preferred is not null)
            {
                return preferred;
            }
        }

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
            .OrderByDescending(x => reachable.Contains(x.TerritoryId))
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
        bool SingleCost);

    private sealed record NpcHandlerRecord(uint NpcId, HandlerPath Path, string? MenuHint);
}
