using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using ECommons;
using ECommons.Automation;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoCollector.Automation;

public enum ExchangeStep
{
    Idle,

    /// <summary>次の Framework.Update で事前条件を評価し、通れば発火する。</summary>
    Armed,

    /// <summary>発火済み。結果を待っている。</summary>
    WaitOutcome,

    /// <summary>確認ダイアログを処理している。</summary>
    ConfirmDialog,

    /// <summary>数量ダイアログが出てしまったので閉じている。</summary>
    CancelDialog,

    Done,
    Error,
}

public enum ExchangeFailure
{
    None,
    NotOnFrameworkThread,
    PreviousExchangeUnresolved,
    Aborted,
    ShopNotOpen,
    BlockingAddonPresent,
    MultiCostOrMultiRewardEntry,
    ResolverNotReady,
    ShopMismatch,
    ExchangeItemNotFound,
    ExchangeAmbiguous,
    CostMismatch,
    EntryCountMismatch,
    IndexOutOfRange,
    IndexDuplicated,
    CurrencyMismatch,
    InsufficientCurrency,
    NoBagSpace,
    RewardCountUnreadable,
    ExchangeNotApplied,
    ExchangeUnexpectedDelta,
    ExchangeUnresolved,
    ConfirmDialogTimeout,
    ConfirmDialogUnexpected,
    ConfirmDialogNotConfirmable,
}

/// <summary>
/// 撃った 1 回分の記録。撃つ直前に立て、結果が確定しても自動ではクリアしない。
/// 設定に永続化するため、プラグインのリロードやクラッシュを跨いでも残る。
/// </summary>
public sealed class PurchaseAttempt
{
    public uint ShopId { get; set; }

    public uint RewardItemId { get; set; }

    public string RewardName { get; set; } = string.Empty;

    public uint CurrencyItemId { get; set; }

    public int CallbackIndex { get; set; }

    public uint CurrencyCost { get; set; }

    public uint RewardQuantity { get; set; }

    public int RewardBefore { get; set; }

    public int CurrencyBefore { get; set; }

    public DateTime FiredAtUtc { get; set; }

    /// <summary>結果が確定したか。false のまま残っているものは「結果未確認」。</summary>
    public bool Resolved { get; set; }

    public string Outcome { get; set; } = string.Empty;
}

/// <summary>
/// 交換の実行。
///
/// 設計原則: 通貨の消費は取り消せない。判断材料が 1 つでも欠けたら撃たない。
/// 撃った後は、成功が確認できなくても二度と撃たない。
///
/// 読み取り → 全検証 → 発火を Framework.Update 内の 1 つの同期ブロックで完結させる。
/// ゲームロジックは単一スレッドなので、この間はタブ切替もサーバ応答も割り込めない。
/// これが index の陳腐化に対する唯一の完全な防御になる。
/// </summary>
public sealed unsafe class ExchangeExecutor(
    AnomalyLog anomalyLog,
    ShopService shopService,
    CurrencyService currencyService,
    ExchangeResolver resolver)
{
    /// <summary>交換コマンド。0 が購入であることの根拠は実測のみ。他の用途に流用しない。</summary>
    private const int ExchangeCommand = 0;

    /// <summary>個数。MVP は 1 固定。2 以上は未実測のため設定に露出させない。</summary>
    private const int ExchangeQuantity = 1;

    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    /// <summary>発火前にこれらが開いていたら撃たない。</summary>
    private static readonly string[] BlockingAddons =
    [
        "ShopExchangeCurrencyDialog", "SelectYesno", "SelectString", "SelectIconString", "Talk", "_TextInput",
    ];

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly ShopService shopService = shopService;
    private readonly CurrencyService currencyService = currencyService;
    private readonly ExchangeResolver resolver = resolver;

    private ExchangeDefinition? pendingRequest;
    private DateTime outcomeDeadlineUtc;
    private DateTime dialogDeadlineUtc;
    private bool aborted;

    public ExchangeStep Step { get; private set; } = ExchangeStep.Idle;

    public ExchangeFailure Failure { get; private set; }

    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>結果が未確定の発火。null でない間は新しい交換を受け付けない。</summary>
    public PurchaseAttempt? InFlight => Plugin.C.InFlight;

    public bool CanRequest => this.pendingRequest is null && this.InFlight is null && !this.aborted;

    /// <summary>UI から交換を予約する。ここでは撃たない。</summary>
    public bool Request(ExchangeDefinition definition, out string reason)
    {
        if (this.InFlight is not null)
        {
            reason = "前回の交換の結果が未確認です。診断タブで確認してからクリアしてください";
            return false;
        }

        if (this.pendingRequest is not null)
        {
            reason = "すでに交換を予約しています";
            return false;
        }

        if (this.aborted)
        {
            reason = "停止中です";
            return false;
        }

        this.pendingRequest = definition;
        this.Step = ExchangeStep.Armed;
        this.Failure = ExchangeFailure.None;
        this.StatusDetail = "事前条件を確認しています";
        reason = string.Empty;
        return true;
    }

    /// <summary>結果未確定の記録を、ユーザーの確認を経てクリアする。</summary>
    public void ClearInFlight()
    {
        if (Plugin.C.InFlight is null)
        {
            return;
        }

        this.anomalyLog.Warn("Exchange", "結果未確認の交換記録をユーザー操作でクリアしました");
        Plugin.C.InFlight = null;
        EzConfig.Save();

        if (this.Step == ExchangeStep.Error)
        {
            this.Step = ExchangeStep.Idle;
            this.Failure = ExchangeFailure.None;
            this.StatusDetail = string.Empty;
        }
    }

    /// <summary>緊急停止。発火経路を封鎖する。inFlight はクリアしない。</summary>
    public void Abort(string reason)
    {
        this.aborted = true;
        this.pendingRequest = null;

        if (this.Step is not (ExchangeStep.Idle or ExchangeStep.Done))
        {
            this.Fail(ExchangeFailure.Aborted, $"停止しました: {reason}");
        }
    }

    public void ClearAbort()
    {
        this.aborted = false;
        if (this.Step == ExchangeStep.Error && this.Failure == ExchangeFailure.Aborted)
        {
            this.Step = ExchangeStep.Idle;
            this.Failure = ExchangeFailure.None;
        }
    }

    /// <summary>Framework.Update から毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        switch (this.Step)
        {
            case ExchangeStep.Armed:
                this.TickArmed();
                break;

            case ExchangeStep.WaitOutcome:
                this.TickWaitOutcome();
                break;

            case ExchangeStep.ConfirmDialog:
                this.TickConfirmDialog();
                break;

            case ExchangeStep.CancelDialog:
                this.TickCancelDialog();
                break;
        }
    }

    /// <summary>
    /// 事前条件をすべて評価し、通ったらその場で発火する。
    /// この関数の中でフレームを跨がない。1 回で決着させ、失敗したら Error に落とす。
    /// </summary>
    private void TickArmed()
    {
        var definition = this.pendingRequest;
        this.pendingRequest = null;

        if (definition is null)
        {
            this.Fail(ExchangeFailure.None, "交換対象が指定されていません");
            return;
        }

        // P-1
        if (!Svc.Framework.IsInFrameworkUpdateThread)
        {
            this.Fail(ExchangeFailure.NotOnFrameworkThread, "Framework スレッド以外から実行されました");
            return;
        }

        // P-2 / P-3
        if (this.InFlight is not null)
        {
            this.Fail(ExchangeFailure.PreviousExchangeUnresolved, "前回の交換の結果が未確認です");
            return;
        }

        if (this.aborted)
        {
            this.Fail(ExchangeFailure.Aborted, "停止中です");
            return;
        }

        // P-6
        if (!definition.SingleReward || !definition.SingleCost)
        {
            this.Fail(ExchangeFailure.MultiCostOrMultiRewardEntry, "報酬またはコストが複数あるエントリです。交換結果を検証できないため実行しません");
            return;
        }

        // P-7
        if (this.resolver.Stage != ResolverBuildStage.Completed || this.resolver.TargetCurrencyItemId != definition.CurrencyItemId)
        {
            this.Fail(ExchangeFailure.ResolverNotReady, "交換候補の索引が、対象通貨に対して用意できていません");
            return;
        }

        // P-4: アドオンは 1 回だけ取得し、以降このポインタだけを使う
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrency", out var addon) || !GenericHelpers.IsAddonReady(addon))
        {
            this.Fail(ExchangeFailure.ShopNotOpen, "交換ショップが開いていません");
            return;
        }

        // P-5
        foreach (var name in BlockingAddons)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var blocking) && GenericHelpers.IsAddonReady(blocking))
            {
                this.Fail(ExchangeFailure.BlockingAddonPresent, $"{name} が開いています。閉じてから実行してください");
                return;
            }
        }

        if (!this.shopService.TryReadEntriesFrom(addon, out var entries, out var header, out var readFailure))
        {
            this.Fail(ExchangeFailure.ShopNotOpen, readFailure);
            return;
        }

        // P-10: 欠けでも過剰でも撃たない
        if (entries.Count != (int)header.DeclaredEntryCount || header.UnreadableEntries != 0)
        {
            this.Fail(
                ExchangeFailure.EntryCountMismatch,
                $"読み取り件数が合いません（申告 {header.DeclaredEntryCount} / 読めた {entries.Count} / 読めなかった {header.UnreadableEntries}）");
            return;
        }

        // P-12: index の重複は配置ずれの証拠
        if (entries.Select(x => x.Index).Distinct().Count() != entries.Count)
        {
            this.Fail(ExchangeFailure.IndexDuplicated, "画面から読んだ index に重複があります。配置がずれている可能性があります");
            return;
        }

        // P-8
        var identification = this.shopService.IdentifyShop(entries, this.resolver.LiveResults);
        if (!identification.IsConfident || identification.ShopId != definition.ShopId)
        {
            this.Fail(ExchangeFailure.ShopMismatch, identification.Detail);
            return;
        }

        // P-9
        var match = this.shopService.Match(definition, identification, entries);
        if (match.Kind != ShopMatchKind.Matched || match.Entry is null)
        {
            var failure = match.Kind switch
            {
                ShopMatchKind.ItemNotFound => ExchangeFailure.ExchangeItemNotFound,
                ShopMatchKind.Ambiguous => ExchangeFailure.ExchangeAmbiguous,
                ShopMatchKind.CostMismatch => ExchangeFailure.CostMismatch,
                _ => ExchangeFailure.ShopMismatch,
            };
            this.Fail(failure, match.Detail);
            return;
        }

        var entry = match.Entry;

        // P-11 / P-13
        if (entry.Index >= header.DeclaredEntryCount)
        {
            this.Fail(ExchangeFailure.IndexOutOfRange, $"index {entry.Index} がエントリ数 {header.DeclaredEntryCount} の範囲外です");
            return;
        }

        int callbackIndex;
        try
        {
            callbackIndex = checked((int)entry.Index);
        }
        catch (OverflowException)
        {
            this.Fail(ExchangeFailure.IndexOutOfRange, $"index {entry.Index} が int に収まりません");
            return;
        }

        // P-14: 画面の通貨とインベントリの通貨が一致すること。通貨違いに対する唯一の実効的な検証。
        if (!this.currencyService.TryGetCount(definition.CurrencyItemId, out var currencyBefore))
        {
            this.Fail(ExchangeFailure.CurrencyMismatch, "所持通貨を取得できませんでした");
            return;
        }

        if (currencyBefore != (int)header.CurrencyAmount)
        {
            this.Fail(
                ExchangeFailure.CurrencyMismatch,
                $"画面の通貨 {header.CurrencyAmount} とインベントリの {currencyBefore} が一致しません。別通貨のショップの可能性があります");
            return;
        }

        // P-15
        if (currencyBefore < definition.CurrencyCost)
        {
            this.Fail(ExchangeFailure.InsufficientCurrency, $"通貨が足りません（所持 {currencyBefore} / 必要 {definition.CurrencyCost}）");
            return;
        }

        // P-16
        if (!this.currencyService.TryGetEmptyBagSlots(out var freeSlots) || freeSlots < 1)
        {
            this.Fail(ExchangeFailure.NoBagSpace, "所持枠に空きがありません");
            return;
        }

        // P-17: 報酬は装備・アーマリーも数える。装備品はアーマリーへ入るため。
        if (!this.currencyService.TryGetCount(definition.RewardItemId, out var rewardBefore, includeEquipped: true, includeArmory: true))
        {
            this.Fail(ExchangeFailure.RewardCountUnreadable, "報酬アイテムの所持数を取得できませんでした");
            return;
        }

        var rewardName = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(definition.RewardItemId)?.Name.ExtractText() ?? string.Empty;

        // ここから先は不可逆。記録を先に立ててから撃つ。順序を逆にしない。
        Plugin.C.InFlight = new PurchaseAttempt
        {
            ShopId = definition.ShopId,
            RewardItemId = definition.RewardItemId,
            RewardName = rewardName,
            CurrencyItemId = definition.CurrencyItemId,
            CallbackIndex = callbackIndex,
            CurrencyCost = definition.CurrencyCost,
            RewardQuantity = definition.RewardQuantity,
            RewardBefore = rewardBefore,
            CurrencyBefore = currencyBefore,
            FiredAtUtc = DateTime.UtcNow,
        };
        EzConfig.Save();

        this.anomalyLog.Info(
            "Exchange",
            $"交換を実行します: {rewardName} × {definition.RewardQuantity}（コスト {definition.CurrencyCost} / index {callbackIndex} / Shop {definition.ShopId}）");

        // 実測どおり 4 値・すべて Int・updateState=true で撃つ。
        // index は必ず int にすること。uint のまま渡すと UInt 型になり実測と食い違う。
        Callback.Fire(addon, true, ExchangeCommand, callbackIndex, ExchangeQuantity, Callback.ZeroAtkValue);

        this.Step = ExchangeStep.WaitOutcome;
        this.outcomeDeadlineUtc = DateTime.UtcNow.Add(OutcomeTimeout);
        this.StatusDetail = "交換結果を待っています";
    }

    /// <summary>発火後の待機。判定は次フレーム以降に行う。</summary>
    private void TickWaitOutcome()
    {
        var attempt = this.InFlight;
        if (attempt is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        // 数量ダイアログが出たら閉じて失敗にする。MVP は数量指定に非対応。
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrencyDialog", out var dialog) && GenericHelpers.IsAddonReady(dialog))
        {
            this.Step = ExchangeStep.CancelDialog;
            this.dialogDeadlineUtc = DateTime.UtcNow.Add(DialogTimeout);
            this.StatusDetail = "数量ダイアログを閉じています";
            return;
        }

        // 確認ダイアログは実測で必ず出る。出たら本文を照合してから押す。
        if (this.TryFindConfirmDialog(attempt, out _, out _))
        {
            this.Step = ExchangeStep.ConfirmDialog;
            this.dialogDeadlineUtc = DateTime.UtcNow.Add(DialogTimeout);
            this.StatusDetail = "確認ダイアログを処理しています";
            return;
        }

        if (this.EvaluateOutcome(attempt))
        {
            return;
        }

        if (DateTime.UtcNow > this.outcomeDeadlineUtc)
        {
            this.FailUnresolved(ExchangeFailure.ExchangeUnresolved, "交換結果を確認できませんでした。実際に交換されたかどうかは不明です");
        }
    }

    /// <summary>
    /// 所持数から成否を判定する。
    /// 権威データは InventoryManager 一本にする。画面上の通貨量は更新タイミングを断定できない。
    /// </summary>
    private bool EvaluateOutcome(PurchaseAttempt attempt)
    {
        if (!this.currencyService.TryGetCount(attempt.RewardItemId, out var rewardAfter, includeEquipped: true, includeArmory: true) ||
            !this.currencyService.TryGetCount(attempt.CurrencyItemId, out var currencyAfter))
        {
            return false;
        }

        // 等値ではなく方向と下限で見る。周回中に通貨が増えるなどで等値判定は偽陰性になる。
        var rewardOk = rewardAfter >= attempt.RewardBefore + (int)attempt.RewardQuantity;
        var currencyOk = currencyAfter <= attempt.CurrencyBefore - (int)attempt.CurrencyCost;

        if (rewardOk && currencyOk)
        {
            attempt.Resolved = true;
            attempt.Outcome = $"成功: 通貨 {attempt.CurrencyBefore} → {currencyAfter} / {attempt.RewardName} {attempt.RewardBefore} → {rewardAfter}";
            this.anomalyLog.Info("Exchange", attempt.Outcome);

            Plugin.C.InFlight = null;
            EzConfig.Save();

            this.Step = ExchangeStep.Done;
            this.Failure = ExchangeFailure.None;
            this.StatusDetail = attempt.Outcome;
            return true;
        }

        // どちらもまったく動いていない場合は、まだ反映されていないだけの可能性がある。
        // タイムアウトまで待ち、それでも動かなければ ExchangeNotApplied で止める。
        if (rewardAfter == attempt.RewardBefore && currencyAfter == attempt.CurrencyBefore)
        {
            return false;
        }

        // 片方だけ動いた、量が合わないなど。想定外なので即座に止める。
        if (rewardOk != currencyOk)
        {
            this.FailUnresolved(
                ExchangeFailure.ExchangeUnexpectedDelta,
                $"想定外の変化です。通貨 {attempt.CurrencyBefore} → {currencyAfter} / {attempt.RewardName} {attempt.RewardBefore} → {rewardAfter}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 確認ダイアログを探す。
    ///
    /// TryGetAddonMaster はインデックス 1 しか見ないため、走査して内容を照合する。
    ///
    /// 実測した本文は「以下のアイテムを / アラガントームストーン:数理×20と交換します。」で、
    /// 報酬アイテム名は本文に含まれず、アイコン横の別ノードに表示される。
    /// そのため本文に対しては「通貨名」と「コスト」の一致を必須条件とし、
    /// 報酬名はアドオン内の全テキストから探して、見つかれば追加の裏付けとして使う。
    /// </summary>
    private bool TryFindConfirmDialog(PurchaseAttempt attempt, out AtkUnitBase* found, out string text)
    {
        found = null;
        text = string.Empty;

        var currencyName = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(attempt.CurrencyItemId)?.Name.ExtractText() ?? string.Empty;
        if (string.IsNullOrEmpty(currencyName))
        {
            // 通貨名が引けないと本文を照合できない。押さない側に倒す。
            return false;
        }

        var cost = attempt.CurrencyCost.ToString();

        for (var i = 1; i < 16; i++)
        {
            // GetAddonByName は AtkUnitBasePtr を返す。Address から生ポインタを取る。
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
            if (addon is null)
            {
                break;
            }

            if (!GenericHelpers.IsAddonReady(addon))
            {
                continue;
            }

            string body;
            try
            {
                var master = new AddonMaster.SelectYesno((nint)addon);
                if (master.Addon->PromptText is null)
                {
                    continue;
                }

                body = master.Text ?? string.Empty;
            }
            catch
            {
                continue;
            }

            // 必須条件: 本文に通貨名とコストの両方が含まれること
            if (!body.Contains(currencyName, StringComparison.Ordinal) || !body.Contains(cost, StringComparison.Ordinal))
            {
                continue;
            }

            // 追加の裏付け: 報酬名がアドオン内のどこかに出ているか
            if (!string.IsNullOrEmpty(attempt.RewardName))
            {
                var texts = CollectTexts(addon);
                var rewardShown = texts.Any(t => t.Contains(attempt.RewardName, StringComparison.Ordinal));
                if (!rewardShown)
                {
                    this.anomalyLog.Warn(
                        "Exchange",
                        $"確認ダイアログに報酬名「{attempt.RewardName}」が見つかりませんでした。表示されていたテキスト: {string.Join(" / ", texts.Where(x => !string.IsNullOrWhiteSpace(x)))}");
                }
            }

            found = addon;
            text = body;
            return true;
        }

        return false;
    }

    /// <summary>
    /// アドオンが表示している文字列をすべて集める。
    /// 報酬アイテム名がどのノードにあるかを決め打ちしないため、
    /// AtkValue の文字列とテキストノードの両方を走査する。
    /// </summary>
    private static List<string> CollectTexts(AtkUnitBase* addon)
    {
        var result = new List<string>();

        try
        {
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                var value = addon->AtkValues[i];
                if (value.Type is not (AtkValueType.String or AtkValueType.String8 or AtkValueType.WideString or AtkValueType.ManagedString))
                {
                    continue;
                }

                if (value.String.Value is null)
                {
                    continue;
                }

                result.Add(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
            }
        }
        catch
        {
            // 読めない値は無視する。ここは裏付け用であり、失敗しても判定は続行する。
        }

        try
        {
            CollectNodeTexts(addon->UldManager.NodeList, addon->UldManager.NodeListCount, result, 0);
        }
        catch
        {
            // 同上
        }

        return result;
    }

    private static void CollectNodeTexts(AtkResNode** nodes, int count, List<string> result, int depth)
    {
        if (nodes is null || depth > 4)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            if (node is null)
            {
                continue;
            }

            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                result.Add(GenericHelpers.ReadSeString(&textNode->NodeText).TextValue);
                continue;
            }

            // コンポーネントノードの中にも文字列がある
            if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null)
                {
                    CollectNodeTexts(component->UldManager.NodeList, component->UldManager.NodeListCount, result, depth + 1);
                }
            }
        }
    }

    private void TickConfirmDialog()
    {
        var attempt = this.InFlight;
        if (attempt is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        if (!this.TryFindConfirmDialog(attempt, out var addon, out var text))
        {
            // 押した結果として消えた可能性がある。結果判定へ戻る。
            this.Step = ExchangeStep.WaitOutcome;
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.ConfirmYes", 500))
        {
            return;
        }

        this.anomalyLog.Info("Exchange", $"確認ダイアログ: {text}");

        var master = new AddonMaster.SelectYesno((nint)addon)
        {
            // 既定の false だと、ゲームが押させないと決めた Yes ボタンの
            // NodeFlags を書き換えて強制的に押す。必ず true にする。
            RespectDisabledButtons = true,
        };

        // ClickButtonIfEnabled は button の null チェックをしないため、自分で確認する。
        if (master.Addon->YesButton is null ||
            !master.Addon->YesButton->IsEnabled ||
            !master.Addon->YesButton->AtkResNode->IsVisible())
        {
            this.FailUnresolved(ExchangeFailure.ConfirmDialogNotConfirmable, $"確認ダイアログの Yes を押せる状態ではありません: {text}");
            return;
        }

        master.Yes();
        this.Step = ExchangeStep.WaitOutcome;
        this.StatusDetail = "確認しました。結果を待っています";

        if (DateTime.UtcNow > this.dialogDeadlineUtc)
        {
            this.FailUnresolved(ExchangeFailure.ConfirmDialogTimeout, "確認ダイアログの処理がタイムアウトしました");
        }
    }

    private void TickCancelDialog()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrencyDialog", out var dialog) || !GenericHelpers.IsAddonReady(dialog))
        {
            this.FailUnresolved(ExchangeFailure.ConfirmDialogUnexpected, "数量ダイアログが開いたため中止しました。MVP は数量指定に対応していません");
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.CancelDialog", 1000))
        {
            return;
        }

        // Exchange ボタン（id 17）には絶対に触れない。Cancel（id 18）だけを押す。
        try
        {
            new AddonMaster.ShopExchangeCurrencyDialog((nint)dialog).Cancel();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Exchange", $"数量ダイアログを閉じられませんでした: {ex.Message}");
        }

        if (DateTime.UtcNow > this.dialogDeadlineUtc)
        {
            this.FailUnresolved(ExchangeFailure.ConfirmDialogUnexpected, "数量ダイアログを閉じられませんでした");
        }
    }

    /// <summary>撃つ前の失敗。inFlight は立っていないので普通に Error へ落とす。</summary>
    private void Fail(ExchangeFailure failure, string detail)
    {
        this.Step = ExchangeStep.Error;
        this.Failure = failure;
        this.StatusDetail = detail;
        this.anomalyLog.Warn("Exchange", $"{failure}: {detail}");
    }

    /// <summary>
    /// 撃った後の失敗。inFlight を未解決のまま残す。
    /// 自動で再送しない。ユーザーがゲーム内で確認してからクリアする。
    /// </summary>
    private void FailUnresolved(ExchangeFailure failure, string detail)
    {
        this.Step = ExchangeStep.Error;
        this.Failure = failure;
        this.StatusDetail = detail;
        this.anomalyLog.Error("Exchange", $"{failure}: {detail}");

        if (Plugin.C.InFlight is { } attempt)
        {
            attempt.Outcome = $"{failure}: {detail}";
            EzConfig.Save();
        }
    }
}
