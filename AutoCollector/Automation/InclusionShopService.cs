using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoCollector.Automation;

/// <summary>InclusionShop の画面から読み取った 1 エントリ。</summary>
public sealed record InclusionShopEntry(
    int Slot,
    uint ItemId,
    string ItemName,
    uint CostItemId,
    byte CostType,
    uint CostAmount,
    uint Index,
    bool CanSelectAmount);

/// <summary>ドロップダウンの選択状態。</summary>
public sealed record InclusionShopSelection(
    uint InclusionShopId,
    byte CategoryCount,
    byte SelectedCategoryIndex,
    byte VisibleSubCategoryCount,
    byte SelectedSubCategoryTab,
    ushort SelectedCategoryRowId,
    ushort SelectedSeriesId);

/// <summary>
/// スクリップ交換などで使われる InclusionShop 画面の読み取りと操作。
///
/// ShopExchangeCurrency とは別のアドオンで、次の点が違う。
///
/// - 系統（カテゴリ）と種別（サブカテゴリ）の 2 段のドロップダウンで絞ってから交換する
/// - AtkValue の配置が違う（エントリ間隔 18）
/// - 交換のコマンドが違う
///
/// AtkValue の位置は FFXIVClientStructs の AddonInclusionShop に型付き定義があるため、
/// ShopExchangeCurrency のように外部 JSON で持つ必要はない。
/// </summary>
public sealed unsafe class InclusionShopService(AnomalyLog anomalyLog, SpecialCurrencyMap specialCurrencyMap)
{
    public const string AddonName = "InclusionShop";

    /// <summary>AddonInclusionShop.InclusionShopAtkValues の定義に対応する位置。</summary>
    private const int PinnedCurrencyCount = 297;
    private const int ItemCount = 298;
    private const int ItemsBase = 299;
    private const int ItemStride = 18;
    private const int OffsetItemId = 1;
    private const int OffsetAmountOwned = 4;
    private const int OffsetGiveItemId = 6;
    private const int OffsetGiveAmount = 12;
    private const int OffsetFlags = 16;
    private const int OffsetIndex = 17;

    /// <summary>Flags の bit1 が立っていると個数を選べる。</summary>
    private const uint FlagCanSelectAmount = 0b10;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly SpecialCurrencyMap specialCurrencyMap = specialCurrencyMap;

    public bool IsOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) && GenericHelpers.IsAddonReady(addon);

    public bool TryGetAddon(out AtkUnitBase* addon)
    {
        addon = null;
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var candidate) || !GenericHelpers.IsAddonReady(candidate))
        {
            return false;
        }

        addon = candidate;
        return true;
    }

    /// <summary>いまの選択状態を読む。ドロップダウンを合わせるために使う。</summary>
    public bool TryGetSelection(out InclusionShopSelection? selection)
    {
        selection = null;

        try
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.InclusionShop);
            if (agent is null || !agent->IsAgentActive())
            {
                return false;
            }

            var typed = (AgentInclusionShop*)agent;
            var dataPtr = typed->Data;
            if (dataPtr is null)
            {
                return false;
            }

            ref var data = ref *dataPtr;
            if (!data.IsShopReady)
            {
                return false;
            }

            ushort categoryRowId = 0;
            ushort seriesId = 0;

            if (data.SelectedCategoryIndex < data.CategoryCount)
            {
                var mapped = data.CategoryIndexMap[data.SelectedCategoryIndex];
                if (mapped < 30)
                {
                    ref var category = ref data.Categories[mapped];
                    categoryRowId = category.InclusionShopRowId;
                    seriesId = category.InclusionShopSeriesId;
                }
            }

            selection = new InclusionShopSelection(
                data.InclusionShopId,
                data.CategoryCount,
                data.SelectedCategoryIndex,
                data.VisibleSubCategoryCount,
                data.SelectedSubCategoryTab,
                categoryRowId,
                seriesId);

            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("InclusionShop", $"選択状態を読めませんでした: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 系統（カテゴリ）を、InclusionShopCategory の行 ID で選ぶ。
    /// 画面の並び順ではなく ID で選ぶため、並びが変わっても影響を受けない。
    /// </summary>
    public bool TrySelectCategory(uint categoryRowId, out string failureReason)
    {
        failureReason = string.Empty;

        try
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.InclusionShop);
            if (agent is null || !agent->IsAgentActive())
            {
                failureReason = "交換画面が開いていません";
                return false;
            }

            var typed = (AgentInclusionShop*)agent;
            var dataPtr = typed->Data;
            if (dataPtr is null || !dataPtr->IsShopReady)
            {
                failureReason = "交換画面の準備ができていません";
                return false;
            }

            ref var data = ref *dataPtr;

            for (byte i = 0; i < data.CategoryCount && i < 30; i++)
            {
                var mapped = data.CategoryIndexMap[i];
                if (mapped >= 30)
                {
                    continue;
                }

                if (data.Categories[mapped].InclusionShopRowId != categoryRowId)
                {
                    continue;
                }

                if (data.SelectedCategoryIndex == i)
                {
                    return true;
                }

                typed->SelectCategory(i);
                return true;
            }

            failureReason = $"系統 {categoryRowId} が画面に見つかりません";
            return false;
        }
        catch (Exception ex)
        {
            failureReason = $"系統を選べませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 種別（サブカテゴリ）を SpecialShop の行 ID で選ぶ。
    ///
    /// サブカテゴリと SpecialShop の対応はシート（InclusionShopSeries）側にあるため、
    /// 目的の SpecialShop がシリーズ内の何番目かを求めてから選ぶ。
    /// 先頭には「選択してください」の項目が入るため、その分をずらす。
    /// </summary>
    public bool TrySelectSubCategory(uint seriesId, uint specialShopId, out string failureReason)
    {
        failureReason = string.Empty;

        var seriesSheet = Svc.Data.GetSubrowExcelSheet<InclusionShopSeries>();
        if (seriesSheet is null || !seriesSheet.TryGetSubrowCount(seriesId, out var seriesCount))
        {
            failureReason = $"シリーズ {seriesId} を読めませんでした";
            return false;
        }

        var seriesIndex = -1;
        for (ushort i = 0; i < seriesCount; i++)
        {
            var row = seriesSheet.GetSubrowOrDefault(seriesId, i);
            if (row is not null && row.Value.SpecialShop.RowId == specialShopId)
            {
                seriesIndex = i;
                break;
            }
        }

        if (seriesIndex < 0)
        {
            failureReason = $"シリーズ {seriesId} に Shop {specialShopId} が含まれていません";
            return false;
        }

        try
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.InclusionShop);
            if (agent is null || !agent->IsAgentActive())
            {
                failureReason = "交換画面が開いていません";
                return false;
            }

            var dataPtr = ((AgentInclusionShop*)agent)->Data;
            if (dataPtr is null || !dataPtr->IsShopReady)
            {
                failureReason = "交換画面の準備ができていません";
                return false;
            }

            ref var data = ref *dataPtr;

            // 先頭の「選択してください」を含むため、タブ番号は 1 つずれる。
            var tab = (byte)(seriesIndex + 1);
            if (tab >= data.VisibleSubCategoryCount)
            {
                failureReason = $"種別 {tab} は選べません（表示されているのは {data.VisibleSubCategoryCount} 件）";
                return false;
            }

            if (data.SelectedSubCategoryTab == tab)
            {
                return true;
            }

            if (!data.SelectSubCategory(tab))
            {
                failureReason = $"種別 {tab} を選べませんでした";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"種別を選べませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// いま表示されているエントリを読む。
    /// 種別を選んでいないと 0 件になる。
    /// </summary>
    public bool TryReadEntries(AtkUnitBase* addon, out IReadOnlyList<InclusionShopEntry> entries, out uint currencyOnScreen, out string failureReason)
    {
        entries = [];
        currencyOnScreen = 0;

        if (addon is null)
        {
            failureReason = "交換画面が開いていません";
            return false;
        }

        try
        {
            var countProbe = AtkValueIntReader.Probe(addon, ItemCount);
            if (!countProbe.Usable)
            {
                failureReason = $"エントリ数を読めません（型 {countProbe.TypeName}）";
                return false;
            }

            var currencyProbe = AtkValueIntReader.Probe(addon, PinnedCurrencyCount);
            currencyOnScreen = currencyProbe.Usable ? currencyProbe.Value : 0;

            var itemSheet = Svc.Data.GetExcelSheet<Item>();
            var result = new List<InclusionShopEntry>();
            var count = (int)Math.Min(countProbe.Value, 60u);

            for (var i = 0; i < count; i++)
            {
                var baseIndex = ItemsBase + (i * ItemStride);

                var itemProbe = AtkValueIntReader.Probe(addon, baseIndex + OffsetItemId);
                if (!itemProbe.Usable || itemProbe.Value == 0)
                {
                    continue;
                }

                var costItemProbe = AtkValueIntReader.Probe(addon, baseIndex + OffsetGiveItemId);
                var costAmountProbe = AtkValueIntReader.Probe(addon, baseIndex + OffsetGiveAmount);
                var indexProbe = AtkValueIntReader.Probe(addon, baseIndex + OffsetIndex);
                var flagsProbe = AtkValueIntReader.Probe(addon, baseIndex + OffsetFlags);

                if (!costItemProbe.Usable || !costAmountProbe.Usable || !indexProbe.Usable)
                {
                    continue;
                }

                var name = itemSheet?.GetRowOrDefault(itemProbe.Value)?.Name.ExtractText() ?? $"<{itemProbe.Value}>";

                result.Add(new InclusionShopEntry(
                    i,
                    itemProbe.Value,
                    name,
                    costItemProbe.Value,
                    0,
                    costAmountProbe.Value,
                    indexProbe.Value,
                    flagsProbe.Usable && (flagsProbe.Value & FlagCanSelectAmount) != 0));
            }

            entries = result;
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"交換画面を読めませんでした: {ex.Message}";
            this.anomalyLog.Error("InclusionShop", failureReason);
            return false;
        }
    }

    /// <summary>
    /// 定義と画面のエントリを照合する。
    ///
    /// 画面が持つコスト側の値は CostType 依存で、スクリップの場合は
    /// 特殊通貨のバケットインデックスであって ItemId ではない。
    /// そのため定義側の通貨 ItemId を同じ土俵に載せてから比べる。
    /// </summary>
    public ShopMatchResultForInclusion Match(ExchangeDefinition definition, IReadOnlyList<InclusionShopEntry> entries)
    {
        InclusionShopEntry? found = null;
        var duplicates = 0;

        foreach (var entry in entries)
        {
            if (entry.ItemId != definition.RewardItemId)
            {
                continue;
            }

            if (found is null)
            {
                found = entry;
            }
            else
            {
                duplicates++;
            }
        }

        if (found is null)
        {
            return new ShopMatchResultForInclusion(
                ShopMatchKind.ItemNotFound,
                null,
                $"ItemId {definition.RewardItemId} が画面に見つかりません（読めたのは {entries.Count} 件）");
        }

        if (duplicates > 0)
        {
            return new ShopMatchResultForInclusion(
                ShopMatchKind.Ambiguous,
                null,
                $"ItemId {definition.RewardItemId} が {duplicates + 1} 件あり、どれを選ぶべきか決められません");
        }

        if (found.CostAmount != definition.CurrencyCost)
        {
            return new ShopMatchResultForInclusion(
                ShopMatchKind.CostMismatch,
                found,
                $"コストが一致しません。ゲームデータ {definition.CurrencyCost} に対して画面は {found.CostAmount} です");
        }

        // 画面のコスト値を ItemId へ解決して、監視している通貨と同じか確かめる。
        if (!this.TryResolveScreenCurrency(found.CostItemId, out var screenCurrencyItemId))
        {
            return new ShopMatchResultForInclusion(
                ShopMatchKind.CostMismatch,
                found,
                $"画面のコスト通貨（値 {found.CostItemId}）を解決できませんでした");
        }

        if (screenCurrencyItemId != definition.CurrencyItemId)
        {
            return new ShopMatchResultForInclusion(
                ShopMatchKind.CostMismatch,
                found,
                $"通貨が一致しません。想定 {definition.CurrencyItemId} に対して画面は {screenCurrencyItemId} です");
        }

        return new ShopMatchResultForInclusion(ShopMatchKind.Matched, found, $"一致（index {found.Index}）");
    }

    /// <summary>
    /// 画面のコスト値を ItemId へ解決する。
    /// 8 以上ならそのまま ItemId、それ未満なら特殊通貨のインデックスとして扱う。
    /// </summary>
    private bool TryResolveScreenCurrency(uint value, out uint itemId)
    {
        if (value >= 8)
        {
            itemId = value;
            return true;
        }

        if (this.specialCurrencyMap.TryResolve(value, out itemId))
        {
            return true;
        }

        // 表に無ければクライアントへ直接聞く。
        try
        {
            var manager = CurrencyManager.Instance();
            if (manager is not null)
            {
                var resolved = manager->GetItemIdBySpecialId((byte)value);
                if (resolved != 0)
                {
                    itemId = resolved;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("InclusionShop", $"特殊通貨 {value} を解決できませんでした: {ex.Message}");
        }

        itemId = 0;
        return false;
    }
}

public sealed record ShopMatchResultForInclusion(ShopMatchKind Kind, InclusionShopEntry? Entry, string Detail);
