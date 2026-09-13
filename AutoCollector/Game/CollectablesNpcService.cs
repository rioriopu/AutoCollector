using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>収集品の納品窓口 1 件。</summary>
/// <param name="ShopName">会話メニューの手がかり。シート上の名前をそのまま使う。</param>
/// <param name="DisplayName">一覧に出す名前。何を扱う窓口かが分かるようにする。</param>
/// <param name="IsGeneralDelivery">一般の収集品納品か。false は道具強化などの専用窓口。</param>
public sealed record CollectablesNpc(
    uint NpcDataId,
    string NpcName,
    uint TerritoryId,
    Vector3 Position,
    bool HasLocation,
    string ShopName,
    string DisplayName,
    bool IsGeneralDelivery);

/// <summary>
/// 収集品の納品窓口（収集品納品窓口）をゲームデータから探す。
///
/// この窓口は ENpcData に CollectablesShop のハンドラを直接持っていない。
/// 実データを読むと、9 体すべてが同じ CustomTalk を 1 つだけ持ち、
/// その CustomTalk の SpecialLinks が CollectablesShop の行を指している。
///
/// <code>
/// ENpc 1003632（リムサ・ロミンサ：下甲板層）
///   └ CustomTalk 721585
///       └ SpecialLinks 3866626  ＝ CollectablesShop「収集品納品」
/// </code>
///
/// そのため <c>ENpcBase → CustomTalk → SpecialLinks</c> と辿って判定する。
/// CustomTalk の行番号も NPC の ID も埋め込まない。
///
/// 走査は要求されたときに 1 度だけ行う。有効化の直後に重い処理を増やさないため、
/// 起動時には何もしない。
/// </summary>
public sealed class CollectablesNpcService(AnomalyLog anomalyLog, NpcLocationService npcLocationService)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly NpcLocationService npcLocationService = npcLocationService;

    /// <summary>一般の収集品納品を扱う CollectablesShop。シート上の名前は「収集品納品」。</summary>
    private const uint GeneralDeliveryShopId = 3866626;

    /// <summary>
    /// 一覧に出さない窓口。
    ///
    /// 3866628「最終改良用部材の交換」（蒼天街・豪腕くん）は、
    /// 座標はデータにあるが実際には NPC がいないため、行っても交換できない。
    /// </summary>
    private static readonly HashSet<uint> ExcludedShops = [3866628];

    /// <summary>
    /// 一覧に出す名前。
    ///
    /// シート上の名前（「改良用部材の交換：クラフター」など）では何の窓口か分からない。
    /// 道具の名前で示したほうが選びやすい。CollectablesShop の行番号で対応づける。
    /// </summary>
    private static readonly Dictionary<uint, string> DisplayNames = new()
    {
        [GeneralDeliveryShopId] = "収集品納品",
        [3866625] = "スカイスチールツール",
        [3866630] = "リスプレンデントツール",
        [3866631] = "モーエンツール",
    };

    private List<CollectablesNpc>? cache;

    /// <summary>走査済みか。</summary>
    public bool IsBuilt => this.cache is not null;

    /// <summary>納品窓口の一覧。初回の呼び出しで走査する。</summary>
    public IReadOnlyList<CollectablesNpc> List()
    {
        if (this.cache is not null)
        {
            return this.cache;
        }

        var found = new List<CollectablesNpc>();

        try
        {
            var bases = Svc.Data.GetExcelSheet<ENpcBase>();
            var residents = Svc.Data.GetExcelSheet<ENpcResident>();
            var talks = Svc.Data.GetExcelSheet<CustomTalk>();

            if (bases is null || talks is null)
            {
                this.anomalyLog.Error("Collectables", "シートを読めないため納品窓口を探せません");
                return this.cache = found;
            }

            foreach (var npc in bases)
            {
                if (!LeadsToCollectablesShop(npc, talks, out var shopHandlerId))
                {
                    continue;
                }

                // 話しかけると会話メニューが出ることがある。
                // そのときどれを選ぶかの手がかりとして、シート上の名前を持たせる。
                // ハンドラの値がそのまま CollectablesShop の行番号になっている。
                var shopName = Svc.Data.GetExcelSheet<CollectablesShop>()
                    ?.GetRowOrDefault(shopHandlerId)?.Name.ExtractText() ?? string.Empty;

                var name = residents?.GetRowOrDefault(npc.RowId)?.Singular.ExtractText() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"ENpc {npc.RowId}";
                }

                // 実際に行っても NPC がいない窓口は一覧から外す。
                if (ExcludedShops.Contains(shopHandlerId))
                {
                    continue;
                }

                var hasLocation = this.npcLocationService.TryGet(npc.RowId, out var location);
                var general = shopHandlerId == GeneralDeliveryShopId;

                found.Add(new CollectablesNpc(
                    npc.RowId,
                    name,
                    hasLocation ? location.TerritoryId : 0,
                    hasLocation ? location.Position : default,
                    hasLocation,
                    shopName,
                    DisplayNames.TryGetValue(shopHandlerId, out var label) ? label : name,
                    general));
            }

            this.anomalyLog.Info(
                "Collectables",
                $"納品窓口を {found.Count} 件見つけました（場所が分かるもの {found.Count(x => x.HasLocation)} 件）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Collectables", $"納品窓口を探せませんでした: {ex.Message}");
        }

        return this.cache = found;
    }

    /// <summary>
    /// この NPC が納品窓口かを判定する。
    ///
    /// ENpcData のハンドラが CollectablesShop なら直接。
    /// CustomTalk なら SpecialLinks の先を見る。収集品納品窓口はこちらの経路。
    /// </summary>
    private static bool LeadsToCollectablesShop(ENpcBase npc, Lumina.Excel.ExcelSheet<CustomTalk> talks, out uint shopHandlerId)
    {
        shopHandlerId = 0;

        foreach (var handler in npc.ENpcData)
        {
            var id = handler.RowId;
            if (id == 0)
            {
                continue;
            }

            if (EventHandlerType.Is(id, EventHandlerType.CollectablesShop))
            {
                shopHandlerId = id;
                return true;
            }

            if (!EventHandlerType.Is(id, EventHandlerType.CustomTalk))
            {
                continue;
            }

            if (!talks.TryGetRow(id, out var talk))
            {
                continue;
            }

            if (EventHandlerType.Is(talk.SpecialLinks.RowId, EventHandlerType.CollectablesShop))
            {
                shopHandlerId = talk.SpecialLinks.RowId;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 行き先を 1 つ選ぶ。
    ///
    /// いまいるエリアにあればそれを使う。無ければ、アクセス済みエーテライトのある
    /// エリアを優先する。飛べない場所を選ぶと移動そのものが成立しない。
    /// </summary>
    public CollectablesNpc? ChooseDestination(uint preferredNpcDataId)
    {
        var all = this.List().Where(x => x.HasLocation).ToList();
        if (all.Count == 0)
        {
            return null;
        }

        // 指定が無ければ一般の納品窓口から選ぶ。専用窓口を勝手に選ぶと目的が変わってしまう。
        var candidates = all.Where(x => x.IsGeneralDelivery).ToList();
        if (candidates.Count == 0)
        {
            candidates = all;
        }

        if (preferredNpcDataId != 0)
        {
            // 指定されたものは専用窓口でもそのまま使う。選んだのは本人なので尊重する。
            var preferred = all.FirstOrDefault(x => x.NpcDataId == preferredNpcDataId);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        var here = Svc.ClientState.TerritoryType;
        var sameArea = candidates.FirstOrDefault(x => x.TerritoryId == here);
        if (sameArea is not null)
        {
            return sameArea;
        }

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
            this.anomalyLog.Warn("Collectables", $"エーテライトの一覧を読めませんでした: {ex.Message}");
        }

        return candidates.FirstOrDefault(x => reachable.Contains(x.TerritoryId)) ?? candidates[0];
    }
}
