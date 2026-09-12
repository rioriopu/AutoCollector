using System;
using System.Collections.Generic;
using AutoCollector.Diagnostics;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoCollector.Automation;

/// <summary>納品できる品 1 件。画面の一覧に並んでいる 1 行に対応する。</summary>
public sealed record CollectableOffer(int RowIndex, uint ItemId, string ItemName);

/// <summary>
/// 収集品納品画面（CollectablesShop）の読み取りと納品。
///
/// この画面には型付きの構造体定義が無いため、配置はすべて実測で確かめた。
/// 実測の記録は docs/10_収集品納品仕様.md にある。
///
/// 確定している配置:
///   AtkValues[20]            一覧の行数
///   AtkValues[33 + i*11]     行番号
///   AtkValues[34 + i*11]     ItemId + CollectableOffset
///
/// 納品:
///   Callback.Fire(addon, true, 12, 行番号)
///   確認ダイアログは出ない。1 回の発火で 1 個だけ渡される。
/// </summary>
public sealed unsafe class CollectablesShopService(AnomalyLog anomalyLog)
{
    public const string AddonName = "CollectablesShop";

    /// <summary>納品の発火コマンド。実測で確定した値。</summary>
    private const int DeliverCommand = 12;

    /// <summary>一覧の先頭が入っている位置。</summary>
    private const uint FirstEntry = 33;

    /// <summary>1 行あたりの AtkValue の数。</summary>
    private const uint EntryStride = 11;

    /// <summary>一覧の行数が入っている位置。</summary>
    private const uint EntryCountIndex = 20;

    /// <summary>
    /// 収集品の ItemId に足されているオフセット。
    /// 画面では 544232 のように入っており、実体は 44232。
    /// </summary>
    private const uint CollectableOffset = 500000;

    private readonly AnomalyLog anomalyLog = anomalyLog;

    public bool IsOpen()
    {
        try
        {
            return GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) &&
                   GenericHelpers.IsAddonReady(addon);
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetAddon(out AtkUnitBase* addon)
    {
        addon = null;

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var found) ||
                !GenericHelpers.IsAddonReady(found))
            {
                return false;
            }

            addon = found;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 画面に並んでいる「納品できる品」を読み出す。
    ///
    /// 申告された行数と実際に読めた件数が食い違う場合は失敗にする。
    /// 欠けたまま返すと、行番号を取り違えて別の品を納品することになる。
    /// </summary>
    public bool TryReadOffers(
        AtkUnitBase* addon,
        out IReadOnlyList<CollectableOffer> offers,
        out string failureReason)
    {
        offers = [];
        failureReason = string.Empty;

        try
        {
            var values = addon->AtkValues;
            var total = addon->AtkValuesCount;

            if (values is null || total <= EntryCountIndex)
            {
                failureReason = "画面の値を読み取れませんでした";
                return false;
            }

            var declared = ReadUInt(values[EntryCountIndex]);
            if (declared == 0)
            {
                failureReason = "納品できる品がありません";
                return false;
            }

            var sheet = Svc.Data.GetExcelSheet<Item>();
            var list = new List<CollectableOffer>((int)declared);

            for (var i = 0u; i < declared; i++)
            {
                var indexAt = FirstEntry + (i * EntryStride);
                var itemAt = indexAt + 1;

                if (itemAt >= total)
                {
                    failureReason = $"行 {i} が画面の範囲を超えています（値の総数 {total}）";
                    return false;
                }

                var rowIndex = ReadUInt(values[indexAt]);
                var rawItemId = ReadUInt(values[itemAt]);

                if (rawItemId == 0)
                {
                    // 空の行がありうる。行番号は詰めない。
                    continue;
                }

                if (rawItemId < CollectableOffset)
                {
                    failureReason = $"行 {i} のアイテムが収集品の形をしていません（値 {rawItemId}）";
                    return false;
                }

                // 行番号は画面の値をそのまま使う。こちらで数え直さない。
                // 空行があるため、並び順と行番号は一致しない。
                if (rowIndex != i)
                {
                    failureReason = $"行 {i} の行番号が {rowIndex} になっています。配置がずれている可能性があります";
                    return false;
                }

                var itemId = rawItemId - CollectableOffset;
                var name = sheet?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"ItemId {itemId}";
                list.Add(new CollectableOffer((int)rowIndex, itemId, name));
            }

            if (list.Count == 0)
            {
                failureReason = "納品できる品を 1 件も読み取れませんでした";
                return false;
            }

            offers = list;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"読み取りに失敗しました: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 1 個だけ納品する。
    ///
    /// 撃つ前に、渡された行番号が画面の内容と一致していることをもう一度確かめる。
    /// 行番号を取り違えると、意図しない品を渡してしまい取り返しがつかない。
    /// </summary>
    public bool TryDeliverOne(CollectableOffer offer, out string failureReason)
    {
        failureReason = string.Empty;

        if (!Svc.Framework.IsInFrameworkUpdateThread)
        {
            failureReason = "Framework スレッド以外から実行されました";
            return false;
        }

        if (!this.TryGetAddon(out var addon))
        {
            failureReason = "納品画面が開いていません";
            return false;
        }

        if (!this.TryReadOffers(addon, out var offers, out var readFailure))
        {
            failureReason = readFailure;
            return false;
        }

        CollectableOffer? current = null;
        foreach (var candidate in offers)
        {
            if (candidate.RowIndex == offer.RowIndex)
            {
                current = candidate;
                break;
            }
        }

        if (current is null)
        {
            failureReason = $"行 {offer.RowIndex} が画面にありません";
            return false;
        }

        if (current.ItemId != offer.ItemId)
        {
            failureReason =
                $"行 {offer.RowIndex} の中身が変わっています（期待 {offer.ItemName} / 画面 {current.ItemName}）。納品しません";
            return false;
        }

        this.anomalyLog.Info("Collect", $"納品します: {current.ItemName}（行 {current.RowIndex}）");

        Callback.Fire(addon, true, DeliverCommand, current.RowIndex);
        return true;
    }

    private static uint ReadUInt(AtkValue value) => value.Type switch
    {
        AtkValueType.UInt => value.UInt,
        AtkValueType.Int => value.Int < 0 ? 0u : (uint)value.Int,
        _ => 0u,
    };
}
