using System;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>
/// 通貨・アイテムの所持数と、閾値到達判定。
///
/// InventoryManager の各メソッドはシグネチャ走査で解決されるため、パッチ直後に
/// FFXIVClientStructs が未追従だと例外になる。すべての呼び出しを捕捉し、
/// 読み取れないときは「交換を開始しない」側へ倒す（fail-closed）。
/// </summary>
public sealed class CurrencyService(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>
    /// 所持数を取得する。HQ / NQ は必ず合算する（GetInventoryItemCount の isHq 既定は false のため 2 回呼ぶ）。
    /// 通貨は装備・アーマリーに入らないため既定では数えない。
    /// </summary>
    public bool TryGetCount(uint itemId, out int count, bool includeEquipped = false, bool includeArmory = false)
    {
        count = 0;
        if (itemId == 0)
        {
            return false;
        }

        try
        {
            unsafe
            {
                var manager = InventoryManager.Instance();
                if (manager is null)
                {
                    return false;
                }

                var nq = manager->GetInventoryItemCount(itemId, false, includeEquipped, includeArmory);
                var hq = manager->GetInventoryItemCount(itemId, true, includeEquipped, includeArmory);
                count = nq + hq;
                return true;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Currency", $"ItemId {itemId} の所持数取得に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>読み取れなかった場合は 0 を返す。表示専用。判定には TryGetCount を使うこと。</summary>
    public int GetCountOrZero(uint itemId) => this.TryGetCount(itemId, out var count) ? count : 0;

    /// <summary>Item.StackSize を所持上限として使う。行が引けなければ null。</summary>
    public uint? GetStackCap(uint itemId)
    {
        var row = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        return row?.StackSize;
    }

    /// <summary>所持枠の空き数。</summary>
    /// <summary>
    /// 実効の所持上限。
    ///
    /// クライアントが上限を管理している通貨はそちらを優先し、無ければ Item.StackSize を使う。
    /// 表示と発火判定は必ずこの 1 本を通す。食い違うと
    /// 「画面では条件を満たしているのに交換されない」という最も説明しにくい壊れ方になる。
    /// </summary>
    public unsafe uint? GetEffectiveCap(uint itemId)
    {
        try
        {
            var manager = CurrencyManager.Instance();
            if (manager is not null && manager->IsItemLimited(itemId))
            {
                var max = manager->GetItemMaxCount(itemId);
                if (max > 0)
                {
                    return max;
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Currency", $"ItemId {itemId} の上限を取得できませんでした: {ex.Message}");
        }

        return this.GetStackCap(itemId);
    }

    public bool TryGetEmptyBagSlots(out uint slots)
    {
        slots = 0;
        try
        {
            unsafe
            {
                var manager = InventoryManager.Instance();
                if (manager is null)
                {
                    return false;
                }

                slots = manager->GetEmptySlotsInBag();
                return true;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Currency", $"所持枠の空き取得に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 閾値に到達しているか判定する。
    /// 所持数または上限が取得できない場合は false を返す（fail-closed）。
    /// </summary>
    public bool IsThresholdReached(ThresholdSetting setting, uint currencyItemId, out int current, out int cap)
    {
        current = 0;
        cap = 0;

        if (!this.TryGetCount(currencyItemId, out current))
        {
            return false;
        }

        var stackCap = this.GetEffectiveCap(currencyItemId);
        if (stackCap is null or 0)
        {
            // 上限が分からないと Percentage / BeforeCap は判定できない。
            if (setting.Mode != ThresholdMode.Fixed)
            {
                this.anomalyLog.Warn("Currency", $"ItemId {currencyItemId} の所持上限が取得できないため、{setting.Mode} での判定を行えません");
                return false;
            }
        }
        else
        {
            cap = (int)Math.Min(stackCap.Value, int.MaxValue);
        }

        return setting.Mode switch
        {
            ThresholdMode.Fixed => current >= setting.Value,
            ThresholdMode.Percentage => cap > 0 && current >= (long)cap * Math.Clamp(setting.Value, 0, 100) / 100,
            ThresholdMode.BeforeCap => cap > 0 && current >= cap - Math.Max(0, setting.Value),
            _ => false,
        };
    }

    /// <summary>UI 表示用に、閾値が実際に何個で発火するかを返す。判定不能なら null。</summary>
    public int? CalculateTriggerAmount(ThresholdSetting setting, uint currencyItemId)
    {
        var stackCap = this.GetEffectiveCap(currencyItemId);
        var cap = stackCap is null or 0 ? 0 : (int)Math.Min(stackCap.Value, int.MaxValue);

        return setting.Mode switch
        {
            ThresholdMode.Fixed => setting.Value,
            ThresholdMode.Percentage => cap > 0 ? (int)((long)cap * Math.Clamp(setting.Value, 0, 100) / 100) : null,
            ThresholdMode.BeforeCap => cap > 0 ? cap - Math.Max(0, setting.Value) : null,
            _ => null,
        };
    }
}
