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
/// RowIndex は画面に書かれている行番号で、品目だけを数えた通し番号である。
/// 見出しを含む「並びの位置」とは一致しない。発火に渡すのはこちら。
/// 位置とずれる行（ItemId 35665 / 行 7 / 位置 8）で実測し、
/// Fire(12, 7u) が記録されたことで確定した。
/// </para>
/// </summary>
public sealed record CollectableOffer(int RowIndex, uint ItemId, string ItemName);

/// <summary>納品ボタンの状態。押せない場合に、なぜ押せないのかを言葉で持つ。</summary>
/// <param name="Ready">いま押せるか。</param>
/// <param name="Detail">読み取った状態。押せないときの記録に使う。</param>
public sealed record TradeButtonState(bool Ready, string Detail);

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
///   Callback.Fire(addon, true, 12, 行番号)   選ぶ
///   Callback.Fire(addon, true, 15, 0u)       納品する
///   1 回の発火で 1 個だけ渡される。
/// </summary>
public sealed unsafe class CollectablesShopService(AnomalyLog anomalyLog)
{
    public const string AddonName = "CollectablesShop";

    /// <summary>
    /// 一覧から品目を選ぶコマンド。実測で確定した値。
    ///
    /// これは選択であって納品ではない。撃つと画面がその品目の表示に切り替わり、
    /// 納品ボタン（node 51）が現れる。
    /// </summary>
    private const int SelectCommand = 12;

    /// <summary>
    /// 納品するコマンド。実測で確定した値。
    ///
    /// 納品ボタン（node 51）を押したときにゲームへ渡っていたのがこれである。
    /// 2026-09-13 の記録では、成功した納品のすべてが
    /// <c>Fire(12, 行番号)</c> → <c>Fire(15, 0u)</c> の 2 手だった。
    /// 行番号 0 / 1 / 7 のいずれでも第 2 引数は 0 で変わらない。
    ///
    /// ボタンの押下を模す方法（ECommons の <c>ClickAddonButton</c>）は、
    /// ノードのイベント列の先頭をそのまま使うため、ボタンが隠れている間や
    /// イベント列の並びが変わった場合に何が起きるかを説明できない。
    /// ゲームが実際に受け取っていた値が分かっている以上、そちらを直接渡す。
    /// </summary>
    private const int DeliverCommand = 15;

    /// <summary>納品ボタンのノード。ECommons の AddonMaster.CollectablesShop と同じ。</summary>
    private const uint TradeButtonNodeId = 51;

    /// <summary>納品できる品の一覧（左のツリー）のノード。</summary>
    private const uint OfferListNodeId = 28;

    /// <summary>一覧の先頭が入っている位置。</summary>
    private const uint FirstEntry = 33;

    /// <summary>1 行あたりの AtkValue の数。</summary>
    private const uint EntryStride = 11;

    /// <summary>
    /// 画面の一覧の行数が入っている位置。
    ///
    /// ここに入っているのは品目の件数ではなく、見出しを含めた表示行の数である。
    /// 実測では品目 28 件・見出し 5 件で 33 だった。品目の件数と比べてはいけない。
    /// 走査の上限としてだけ使う。
    /// </summary>
    private const uint DisplayRowCountIndex = 20;

    /// <summary>空の位置がこの回数続いたら、一覧の終わりとみなす。</summary>
    private const int EmptyRunToStop = 3;

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

            if (values is null || total <= DisplayRowCountIndex)
            {
                failureReason = "画面の値を読み取れませんでした";
                return false;
            }

            var displayRows = ReadUInt(values[DisplayRowCountIndex]);
            if (displayRows == 0)
            {
                failureReason = "納品できる品がありません";
                return false;
            }

            var sheet = Svc.Data.GetExcelSheet<Item>();
            var list = new List<CollectableOffer>((int)displayRows);
            var seen = new HashSet<uint>();
            var emptyRun = 0;


            // 並び順と行番号が一致するとは限らない。
            // 実測したデータには、行番号だけがあって品目が空の位置があり、
            // そのあとの位置に同じ行番号と品目が入っていた。
            // そのため位置から行番号を決めつけず、画面に書かれた行番号をそのまま使う。
            //
            // 見出しのぶん位置が後ろへずれるので、表示行数より広く走査する。
            var maxSlots = displayRows * 2;

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
                    // 品目が無い位置。見出しの行で、次の品目の番号だけが入っている。
                    // 一覧の終わりにも空の位置が続くため、続いたら打ち切る。
                    emptyRun++;

                    if (list.Count > 0 && emptyRun >= EmptyRunToStop)
                    {
                        break;
                    }

                    continue;
                }

                emptyRun = 0;

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

                var itemId = rawItemId - CollectableOffset;
                var name = sheet?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"ItemId {itemId}";
                list.Add(new CollectableOffer((int)rowIndex, itemId, name));

                if (list.Count >= displayRows)
                {
                    // 表示行数を超えることはない。超えたら読み違えている。
                    failureReason = $"品目の件数が表示行数 {displayRows} を超えました。配置がずれている可能性があります";
                    return false;
                }
            }

            if (list.Count == 0)
            {
                failureReason = "納品できる品を 1 件も読み取れませんでした";
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
    /// 納品ボタンの状態を読む。
    ///
    /// 品目を選ぶ前は隠れており、選ぶと現れる。
    /// これが押せることをもって「選択が効いた」と判断していた。
    ///
    /// **読めなかったことを黙って「押せない」に丸めない。**
    /// 以前はここが例外を握りつぶしており、ボタンを掴めなくなっても
    /// 「まだ押せる状態ではない」としか見えず、原因を追えなかった。
    ///
    /// ノードは <see cref="AtkUnitBase.GetComponentByNodeId"/> で取る。
    /// <c>GetComponentButtonById</c> は別のシグネチャで解決される関数で、
    /// そちらだけが引けなくなる可能性があるため、診断と同じ経路に揃える。
    /// </summary>
    public TradeButtonState ReadTradeButton()
    {
        try
        {
            if (!this.TryGetAddon(out var addon))
            {
                return new TradeButtonState(false, "納品画面を掴めません");
            }

            var component = addon->GetComponentByNodeId(TradeButtonNodeId);
            if (component is null)
            {
                return new TradeButtonState(false, $"納品ボタン（node {TradeButtonNodeId}）がありません");
            }

            var type = component->GetComponentType();
            if (type != ComponentType.Button)
            {
                return new TradeButtonState(false, $"node {TradeButtonNodeId} がボタンではありません（{type}）");
            }

            var owner = component->OwnerNode;
            if (owner is null)
            {
                return new TradeButtonState(false, "納品ボタンのノードを取れません");
            }

            var visible = owner->AtkResNode.IsVisible();
            var enabled = ((AtkComponentButton*)component)->IsEnabled;

            return new TradeButtonState(visible && enabled, $"見える={visible} 押せる={enabled}");
        }
        catch (Exception ex)
        {
            return new TradeButtonState(false, $"納品ボタンを読めませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 選んだ品が、いま画面で選ばれている品と一致しているか。
    ///
    /// 納品を撃つ前の最後の関門。ここが一致していなければ、撃つと別の品を渡す。
    /// 一覧（node 28）の選択位置から行を引き、そこに入っている行番号と照らす。
    ///
    /// 見出し行は納品の対象ではないので、選択位置が見出しなら不一致として扱う。
    /// </summary>
    public bool TryConfirmSelection(CollectableOffer offer, out string detail)
    {
        detail = string.Empty;

        try
        {
            if (!this.TryGetAddon(out var addon))
            {
                detail = "納品画面を掴めません";
                return false;
            }

            var component = addon->GetComponentByNodeId(OfferListNodeId);
            if (component is null || component->GetComponentType() != ComponentType.TreeList)
            {
                detail = $"一覧（node {OfferListNodeId}）を取れません";
                return false;
            }

            var tree = (AtkComponentTreeList*)component;
            var selected = tree->SelectedItemIndex;

            if (selected < 0 || selected >= tree->Items.LongCount)
            {
                detail = $"一覧で何も選ばれていません（選択位置 {selected} / 行数 {tree->Items.LongCount}）";
                return false;
            }

            var item = tree->Items[selected].Value;
            if (item is null)
            {
                detail = $"選択位置 {selected} の行を取れません";
                return false;
            }

            if ((item->Type & (TreeListItemType.Group | TreeListItemType.SectionHeader)) != 0)
            {
                detail = $"選択位置 {selected} は見出しです（{item->Type}）";
                return false;
            }

            // 行番号は UIntValues[1] に入っている。AtkValues[33 + i*11] と同じ値で、
            // 発火に渡すのもこれである（docs/10 の 3 回目の実測で確定）。
            if (item->UIntValues.LongCount < 2)
            {
                detail = $"選択位置 {selected} に行番号が入っていません";
                return false;
            }

            var rowIndex = (int)item->UIntValues[1];
            if (rowIndex != offer.RowIndex)
            {
                detail = $"選ばれているのは行 {rowIndex} で、狙いの行 {offer.RowIndex}（{offer.ItemName}）ではありません";
                return false;
            }

            detail = $"選択位置 {selected} = 行 {rowIndex}（{offer.ItemName}）";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"選択を確かめられませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 納品を撃つ。選んだ品が画面で選ばれていることを確かめてから呼ぶこと。
    ///
    /// 渡すのは実測で確定した <c>Fire(15, 0u)</c>。
    /// これは納品ボタンを押したときにゲームが受け取っていた値そのものである。
    /// </summary>
    public bool TryDeliver(out string failureReason)
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

        try
        {
            // 実測は Fire(15, 0u)。第 2 引数は UInt で、行番号に関わらず 0 だった。
            Callback.Fire(addon, true, DeliverCommand, 0u);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"納品を撃てませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 一覧から品目を選ぶ。納品はしない。
    ///
    /// 撃つ前に、渡された行番号が画面の内容と一致していることをもう一度確かめる。
    /// 行番号を取り違えると、そのあとの納品で意図しない品を渡してしまう。
    /// </summary>
    public bool TrySelect(CollectableOffer offer, out string failureReason)
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

        this.anomalyLog.Info("Collect", $"{current.ItemName} を選びます（行 {current.RowIndex}）");

        // 実測は Fire(12, 0u)。第 2 引数は UInt だった。
        // int のまま渡すと AtkValueType.Int になり、実測と型が食い違う。
        Callback.Fire(addon, true, SelectCommand, (uint)current.RowIndex);
        return true;
    }

    private static uint ReadUInt(AtkValue value) => value.Type switch
    {
        AtkValueType.UInt => value.UInt,
        AtkValueType.Int => value.Int < 0 ? 0u : (uint)value.Int,
        _ => 0u,
    };
}
