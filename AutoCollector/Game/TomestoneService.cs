using System.Collections.Generic;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>
/// トームストーンのスロット（Tomestones シートの行番号）と、現在そこへ割り当てられている
/// アイテムの対応を解決する。
///
/// Tomestones シートは「スロット定義」であり、行番号が固定で中身がパッチごとに入れ替わる。
/// たとえば行 2 は現時点で数理(48) だが、次の大型パッチでは別のトームストーンになる。
/// そのため ItemId をハードコードせず、常に行番号から引く。
///
/// TomestonesItem を順番に並べて 1..N を振る「序数方式」は使ってはならない。
/// 行が増減した瞬間に全スロットがずれ、無警告で別の通貨を監視することになる。
/// </summary>
public sealed class TomestoneService(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>Tomestones の行番号から、現在割り当てられているトームストーンの ItemId を引く。</summary>
    public bool TryResolveItemId(uint tomestonesRowId, out uint itemId)
    {
        itemId = 0;
        var sheet = Svc.Data.GetExcelSheet<TomestonesItem>();
        if (sheet is null)
        {
            return false;
        }

        foreach (var row in sheet)
        {
            if (row.Tomestones.RowId != tomestonesRowId)
            {
                continue;
            }

            var candidate = row.Item.RowId;
            if (candidate == 0)
            {
                continue;
            }

            itemId = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Tomestones.WeeklyLimit（シート上の週上限）。0 なら週上限なし。</summary>
    public uint GetWeeklyLimitFromSheet(uint tomestonesRowId)
    {
        var row = Svc.Data.GetExcelSheet<Tomestones>()?.GetRowOrDefault(tomestonesRowId);
        return row?.WeeklyLimit ?? 0;
    }

    /// <summary>実行時の週上限。週制限つきトームストーン全体に対する 1 つの値。</summary>
    public int GetWeeklyLimitRuntime()
    {
        try
        {
            return InventoryManager.GetLimitedTomestoneWeeklyLimit();
        }
        catch (System.Exception ex)
        {
            this.anomalyLog.Warn("Tomestone", $"週上限の取得に失敗しました: {ex.Message}");
            return 0;
        }
    }

    /// <summary>今週すでに取得した週制限つきトームストーンの量。</summary>
    public unsafe int GetWeeklyAcquired()
    {
        try
        {
            var manager = InventoryManager.Instance();
            return manager is null ? 0 : manager->GetWeeklyAcquiredTomestoneCount();
        }
        catch (System.Exception ex)
        {
            this.anomalyLog.Warn("Tomestone", $"週間取得量の取得に失敗しました: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// シート値と実行時値が矛盾していないか検査する。
    /// 矛盾していたらパッチ追従が済んでいない可能性が高いので、週上限に依存する機能を止める。
    /// </summary>
    public bool ValidateWeeklyConsistency(uint tomestonesRowId, out string reason)
    {
        var sheetLimit = this.GetWeeklyLimitFromSheet(tomestonesRowId);
        var runtimeLimit = this.GetWeeklyLimitRuntime();
        var acquired = this.GetWeeklyAcquired();

        if (acquired < 0)
        {
            reason = $"週間取得量が負の値です ({acquired})";
            return false;
        }

        if (runtimeLimit < 0)
        {
            reason = $"実行時の週上限が負の値です ({runtimeLimit})";
            return false;
        }

        if (runtimeLimit > 0 && acquired > runtimeLimit)
        {
            reason = $"週間取得量 {acquired} が週上限 {runtimeLimit} を超えています";
            return false;
        }

        // シート上で週制限ありなのに実行時が 0、またはその逆は追従漏れの可能性がある。
        if (sheetLimit > 0 && runtimeLimit == 0)
        {
            reason = $"シートは週上限 {sheetLimit} を示していますが、実行時の週上限が 0 です";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// スロット → ItemId のスナップショット。前回起動時と比較して、
    /// パッチによるトームストーンの入れ替わりを検知するために使う。
    /// </summary>
    public Dictionary<uint, uint> SnapshotSlotToItemId()
    {
        var result = new Dictionary<uint, uint>();
        var sheet = Svc.Data.GetExcelSheet<Tomestones>();
        if (sheet is null)
        {
            return result;
        }

        foreach (var slot in sheet)
        {
            if (this.TryResolveItemId(slot.RowId, out var itemId))
            {
                result[slot.RowId] = itemId;
            }
        }

        return result;
    }

    /// <summary>UI 表示用に、解決できたスロットを一覧化する。</summary>
    public List<TomestoneSlotInfo> ListSlots()
    {
        var result = new List<TomestoneSlotInfo>();
        var sheet = Svc.Data.GetExcelSheet<Tomestones>();
        var itemSheet = Svc.Data.GetExcelSheet<Item>();
        if (sheet is null || itemSheet is null)
        {
            return result;
        }

        foreach (var slot in sheet)
        {
            if (!this.TryResolveItemId(slot.RowId, out var itemId))
            {
                continue;
            }

            var item = itemSheet.GetRowOrDefault(itemId);
            result.Add(new TomestoneSlotInfo(
                slot.RowId,
                itemId,
                item?.Name.ExtractText() ?? string.Empty,
                item?.StackSize ?? 0,
                slot.WeeklyLimit));
        }

        return result;
    }
}

public sealed record TomestoneSlotInfo(
    uint TomestonesRowId,
    uint ItemId,
    string Name,
    uint StackCap,
    uint SheetWeeklyLimit);
