using System;
using System.Collections.Generic;
using AutoCollector.Diagnostics;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoCollector.Automation;

/// <summary>
/// 納品できる品 1 件。画面の一覧に並んでいる 1 行に対応する。
///
/// <para>
/// Verified は「発火に渡す番号の意味が実測で裏づけられている範囲か」を表す。
/// 画面の値には、番号だけがあって品目が空の位置が混ざる。そこを境に
/// 「並びの位置」と「書かれている番号」がずれ、どちらを渡すべきかが決まらなくなる。
/// 実測できているのはずれる前の行だけなので、ずれた先は発火させない。
/// </para>
/// </summary>
public sealed record CollectableOffer(int RowIndex, uint ItemId, string ItemName, bool Verified);

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
            var seen = new HashSet<uint>();

            // 位置と書かれている番号が一致しているあいだだけ、発火してよい範囲とする。
            // 一度ずれたら、そこから先はどちらを渡すべきか実測で裏づけられていない。
            var stillAligned = true;

            // 並び順と行番号が一致するとは限らない。
            // 実測したデータには、行番号だけがあって品目が空の位置があり、
            // そのあとの位置に同じ行番号と品目が入っていた。
            // そのため位置から行番号を決めつけず、画面に書かれた行番号をそのまま使う。
            //
            // 走査する範囲は申告件数より広く取る。空の位置があるぶん後ろへずれるため。
            var maxSlots = declared * 2;

            for (var i = 0u; i < maxSlots; i++)
            {
                var indexAt = FirstEntry + (i * EntryStride);
                var itemAt = indexAt + 1;

                if (itemAt >= total)
                {
                    break;
                }

                var rowIndex = ReadUInt(values[indexAt]);
                var rawItemId = ReadUInt(values[itemAt]);

                if (rawItemId == 0)
                {
                    // 品目が無い位置。行番号だけが入っていることがある。
                    continue;
                }

                if (rawItemId < CollectableOffset)
                {
                    // 収集品の形をしていない。配置がずれた可能性があるので、
                    // ここまでの読み取りごと捨てる。撃つ前に止める方を選ぶ。
                    failureReason = $"位置 {i} のアイテムが収集品の形をしていません（値 {rawItemId}）";
                    return false;
                }

                if (!seen.Add(rowIndex))
                {
                    failureReason = $"行番号 {rowIndex} が複数の位置にあります。配置がずれている可能性があります";
                    return false;
                }

                if (rowIndex != i)
                {
                    stillAligned = false;
                }

                var itemId = rawItemId - CollectableOffset;
                var name = sheet?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"ItemId {itemId}";
                list.Add(new CollectableOffer((int)rowIndex, itemId, name, stillAligned));

                if (list.Count == declared)
                {
                    break;
                }
            }

            if (list.Count == 0)
            {
                failureReason = "納品できる品を 1 件も読み取れませんでした";
                return false;
            }

            // 申告された件数と読めた件数が違う場合は撃たない。
            // 欠けたまま進むと、目的の品が一覧に無いのに別の行を掴むことになる。
            if (list.Count != declared)
            {
                failureReason = $"一覧の件数が合いません（申告 {declared} / 読めた {list.Count}）";
                return false;
            }

            // 番号は 0 から連番で並ぶはず。
            // 走査が実体の末尾を越えて無関係な値を拾った場合、ここで崩れる。
            // 画面の別の場所には 4150289 のような大きな値も入っており、
            // それらを収集品として読んでしまう余地があるため、形で弾く。
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].RowIndex != i)
                {
                    failureReason =
                        $"行番号が連番になっていません（{i} 番目の行番号が {list[i].RowIndex}）。配置がずれている可能性があります";
                    return false;
                }
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

        if (!current.Verified)
        {
            failureReason =
                $"{current.ItemName} は、渡す番号の意味が実測で裏づけられていない範囲にあります。" +
                "別の品を納品してしまう恐れがあるため実行しません";
            return false;
        }

        this.anomalyLog.Info("Collect", $"納品します: {current.ItemName}（行 {current.RowIndex}）");

        // 実測は Fire(12, 0u)。第 2 引数は UInt だった。
        // int のまま渡すと AtkValueType.Int になり、実測と型が食い違う。
        Callback.Fire(addon, true, DeliverCommand, (uint)current.RowIndex);
        return true;
    }

    private static uint ReadUInt(AtkValue value) => value.Type switch
    {
        AtkValueType.UInt => value.UInt,
        AtkValueType.Int => value.Int < 0 ? 0u : (uint)value.Int,
        _ => 0u,
    };
}
