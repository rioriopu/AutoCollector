using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using ECommons;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoCollector.Automation;

public enum ExchangeStep
{
    Idle,

    /// <summary>安全に始められる状態になるのを待っている。</summary>
    WaitingSafeWindow,

    /// <summary>外部プラグインの新規開始を抑制している。</summary>
    SuppressExternal,

    /// <summary>AutoDuty を停止している。</summary>
    StopAutoDuty,

    /// <summary>目的のエリアへテレポートしている。</summary>
    Teleport,

    /// <summary>エーテライト網で都市内を短縮移動している。</summary>
    AethernetHop,

    /// <summary>NPC のいる場所へ移動している。</summary>
    Navigate,

    /// <summary>NPC に話しかけている。</summary>
    Interact,

    /// <summary>会話メニューを通過している。</summary>
    SelectMenu,

    /// <summary>次の Framework.Update で事前条件を評価し、通れば発火する。</summary>
    Armed,

    /// <summary>発火済み。結果を待っている。</summary>
    WaitOutcome,

    /// <summary>確認ダイアログを処理している。</summary>
    ConfirmDialog,

    /// <summary>数量ダイアログが出てしまったので閉じている。</summary>
    CancelDialog,

    /// <summary>AutoDuty を再開している。</summary>
    ResumeAutoDuty,

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
    NavigationUnavailable,
    NavigationFailed,
    NpcNotFound,
    WrongTerritory,
    InteractFailed,
    MenuResolutionFailed,
    MenuAmbiguous,
    NotSafeToStart,
    TeleportUnavailable,
    TeleportFailed,
    AetheryteNotAttuned,
    AutoDutyStopFailed,
    AutoDutyResumeFailed,
    AutoRetainerIpcBroken,
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
/// 1 回の来店でどこまで交換するかの設定。
///
/// 一度ショップを開いたら、条件を満たす限り続けて交換する。
/// 交換のたびに移動し直すのは無駄が大きい。
/// </summary>
public sealed class ExchangeSession
{
    /// <summary>暴走への歯止め。この回数を超えたら理由に関わらず打ち切る。</summary>
    public const int HardLimit = 200;

    public required ExchangeMode Mode { get; init; }

    /// <summary>UntilCurrencyReserve のときに残す通貨量。</summary>
    public int CurrencyReserve { get; init; }

    /// <summary>FixedQuantity のときの残り回数。</summary>
    public int RemainingCount { get; set; }

    /// <summary>UntilTargetQuantity のときの目標所持数。</summary>
    public int TargetQuantity { get; init; }

    /// <summary>これまでに交換した回数。</summary>
    public int Completed { get; set; }

    /// <summary>このセッションを開始したプリセット。監視からの実行時のみ設定される。</summary>
    public Guid PresetId { get; init; }
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
    ExchangeResolver resolver,
    NavigationService navigation,
    InteractionService interaction,
    MenuService menu,
    AddonOwnershipTracker ownership,
    AetheryteService aetheryte,
    LifestreamIpc lifestream,
    AutoDutyIpc autoDuty,
    AutoRetainerIpc autoRetainer)
{
    /// <summary>交換コマンド。0 が購入であることの根拠は実測のみ。他の用途に流用しない。</summary>
    private const int ExchangeCommand = 0;

    /// <summary>個数。MVP は 1 固定。2 以上は未実測のため設定に露出させない。</summary>
    private const int ExchangeQuantity = 1;

    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 連続して交換するときに、次を撃つまで待つ時間。
    ///
    /// サーバーからの反映は通貨と報酬で届く順序が前後する。
    /// 間を置かずに次を撃つと、前回の反映が終わる前に次の検証が始まり、
    /// 成功しているのに「想定外の変化」と誤判定する。
    /// </summary>
    private static readonly TimeSpan SettleBetweenExchanges = TimeSpan.FromMilliseconds(1200);

    /// <summary>発火前にこれらが開いていたら撃たない。</summary>
    private static readonly string[] BlockingAddons =
    [
        "ShopExchangeCurrencyDialog", "SelectYesno", "SelectString", "SelectIconString", "Talk", "_TextInput",
    ];

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly ShopService shopService = shopService;
    private readonly CurrencyService currencyService = currencyService;
    private readonly ExchangeResolver resolver = resolver;
    private readonly NavigationService navigation = navigation;
    private readonly InteractionService interaction = interaction;
    private readonly MenuService menu = menu;
    private readonly AddonOwnershipTracker ownership = ownership;
    private readonly AetheryteService aetheryte = aetheryte;
    private readonly LifestreamIpc lifestream = lifestream;
    private readonly AutoDutyIpc autoDuty = autoDuty;
    private readonly AutoRetainerIpc autoRetainer = autoRetainer;

    private int teleportAttempts;
    private bool aethernetTried;
    private Vector3 hopStartPosition;
    private Vector3 navigationDestination;
    private int reapproachAttempts;
    private int destinationUpdates;
    private ReturnContext? returnContext;
    private ExchangeSession? session;
    private DateTime lastWaitLogUtc;

    /// <summary>待機中に観測した Duty のエリア。AutoDuty を再開するときに渡す。</summary>
    private uint observedDutyTerritoryId;
    private int stopAttempts;

    /// <summary>
    /// 待っている間に AutoDuty が動いているのを観測したか。
    ///
    /// サイクルの終わりを待つ方式では、交換を始める時点で AutoDuty は既に停止している。
    /// 「停止しているから元々動いていなかった」と誤認すると交換後に再開しなくなるため、
    /// 待機中に見た状態を根拠にする。
    /// </summary>
    private bool sawAutoDutyRunning;

    /// <summary>移動から始める場合の対象。null なら手動でショップを開いた状態からの実行。</summary>
    private ExchangeDefinition? travelTarget;
    private DateTime stepDeadlineUtc;

    private ExchangeDefinition? pendingRequest;
    private DateTime outcomeDeadlineUtc;
    private DateTime nextArmedAllowedUtc;
    private DateTime dialogDeadlineUtc;
    private bool aborted;

    public ExchangeStep Step { get; private set; } = ExchangeStep.Idle;

    public ExchangeFailure Failure { get; private set; }

    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>結果が未確定の発火。null でない間は新しい交換を受け付けない。</summary>
    public PurchaseAttempt? InFlight => Plugin.C.InFlight;

    /// <summary>いま進行中のセッションで交換した回数。</summary>
    public int SessionCompleted => this.session?.Completed ?? 0;

    /// <summary>いま進行中のプリセット。監視からの実行でなければ空。</summary>
    public Guid ActivePresetId => this.session?.PresetId ?? Guid.Empty;

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

    private static ulong TryGetContentId()
    {
        try
        {
            return Svc.PlayerState.ContentId;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 直前に記録した再開先。UI から手動で再開するときに使う。
    /// </summary>
    public uint LastResumeTerritoryId => this.returnContext?.AutoDutyTerritoryId ?? this.observedDutyTerritoryId;

    /// <summary>手動で AutoDuty を再開する。</summary>
    public bool TryResumeAutoDutyManually(out string reason)
    {
        var territory = this.LastResumeTerritoryId;

        if (territory == 0)
        {
            reason = "再開先のエリアが分かりません";
            return false;
        }

        if (!this.autoDuty.IsLoaded)
        {
            reason = "AutoDuty が導入されていません";
            return false;
        }

        if (!this.autoDuty.TryContentHasPath(territory, out var hasPath) || !hasPath)
        {
            reason = $"{NpcLocationService.GetTerritoryName(territory)} に AutoDuty の経路がありません";
            return false;
        }

        if (!this.autoDuty.TryRun(territory))
        {
            reason = "AutoDuty へ再開を依頼できませんでした";
            return false;
        }

        this.anomalyLog.Info("AutoDuty", $"{NpcLocationService.GetTerritoryName(territory)} で AutoDuty を再開しました");
        reason = string.Empty;
        return true;
    }

    /// <summary>エラー状態を解除して、また実行できるようにする。</summary>
    public void ResetAfterError()
    {
        if (this.Step != ExchangeStep.Error)
        {
            return;
        }

        this.Step = ExchangeStep.Idle;
        this.Failure = ExchangeFailure.None;
        this.StatusDetail = string.Empty;
        this.aborted = false;

        this.autoRetainer.Release();
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

        // ここへ来る経路によっては抑制が残っている可能性がある。
        // 抑制したまま放置すると AutoRetainer が動かなくなるため、必ず解く。
        this.autoRetainer.Release();

        if (this.Step == ExchangeStep.Error)
        {
            this.Step = ExchangeStep.Idle;
            this.Failure = ExchangeFailure.None;
            this.StatusDetail = string.Empty;
        }
    }

    /// <summary>
    /// 緊急停止。発火経路を封鎖してから後始末する。
    /// inFlight はクリアしない（結果が未確認のまま残す）。
    /// </summary>
    public void Abort(string reason)
    {
        // 何よりも先に封鎖する
        this.aborted = true;
        this.pendingRequest = null;
        this.travelTarget = null;
        this.session = null;

        this.Cleanup();

        if (this.Step is not (ExchangeStep.Idle or ExchangeStep.Done))
        {
            this.Fail(ExchangeFailure.Aborted, $"停止しました: {reason}");
        }
    }

    /// <summary>
    /// 自分が握った制御だけを解放する。
    ///
    /// 各手順はべき等で、失敗しても次の手順を止めない。
    /// 内側（ダイアログ）から外側（ショップ）の順に閉じる。
    /// 逆順にするとダイアログだけが孤立して残る。
    ///
    /// 自分が開いたウィンドウでなければ触らない。
    /// 他プラグインやユーザーが手動で開いたものを閉じると、そちらの操作を壊す。
    /// </summary>
    public void Cleanup()
    {
        // 1. 自分が開始した移動だけを止める
        try
        {
            this.navigation.Stop();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Cleanup", $"移動を停止できませんでした: {ex.Message}");
        }

        // 1-2. Lifestream が自分の依頼で動いている場合に備えて中断を送る
        try
        {
            if (this.lifestream.TryIsBusy(out var lifestreamBusy) && lifestreamBusy)
            {
                this.lifestream.TryAbort();
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Cleanup", $"Lifestream を中断できませんでした: {ex.Message}");
        }

        // 2. エリアが変わっているならウィンドウは既に破棄されている。触ってはいけない。
        var territoryChanged = this.travelTarget is not null && Svc.ClientState.TerritoryType != this.travelTarget.TerritoryId;

        if (!territoryChanged)
        {
            // 3. 内側のダイアログ。確認ダイアログは絶対に Yes を押さない。
            this.CloseOwned("SelectYesno", useCloseFirst: false);
            this.CloseOwned("ShopExchangeCurrencyDialog", useCloseFirst: false);

            // 4. ショップ本体
            this.CloseOwned("ShopExchangeCurrency", useCloseFirst: true);

            // 5. 会話メニューの残骸
            this.CloseOwned("SelectString", useCloseFirst: false);
            this.CloseOwned("SelectIconString", useCloseFirst: false);
        }
        else
        {
            this.anomalyLog.Info("Cleanup", "エリアが変わっているため、ウィンドウの操作は行いません");
        }

        // 6. ターゲット解除
        try
        {
            Svc.Targets.Target = null;
        }
        catch
        {
            // 解除できなくても続行する
        }

        // 7. 外部抑制の解除。自分が立てた場合だけ外す。
        try
        {
            this.autoRetainer.Release();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Cleanup", $"AutoRetainer の抑制を解除できませんでした: {ex.Message}");
        }

        this.returnContext = null;
        this.ownership.Clear();
    }

    /// <summary>
    /// 自分が開いたウィンドウなら閉じる。そうでなければ何もしない。
    ///
    /// ショップ本体は Close(true) を先に試す。
    /// Callback.Fire(addon, true, -1) は購入コマンドと同じ経路を通るため、
    /// 値の取り違えが購入として解釈される余地がある。誤爆しにくい方を先にする。
    /// </summary>
    private void CloseOwned(string addonName, bool useCloseFirst)
    {
        if (!this.ownership.TryGetOwned(addonName, out var addon))
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var foreign) && GenericHelpers.IsAddonReady(foreign))
            {
                this.anomalyLog.Info("Cleanup", $"{addonName} は自分が開いたものではないため閉じません");
            }

            return;
        }

        try
        {
            if (useCloseFirst)
            {
                addon->Close(true);
            }
            else
            {
                Callback.Fire(addon, true, -1);
            }

            this.anomalyLog.Info("Cleanup", $"{addonName} を閉じました");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Cleanup", $"{addonName} を閉じられませんでした: {ex.Message}");
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

    /// <summary>
    /// NPC のところまで移動してから交換する。
    /// テレポートは行わないため、同じエリアにいる必要がある。
    /// </summary>
    public bool RequestWithTravel(ExchangeDefinition definition, out string reason)
        => this.RequestWithTravel(definition, new ExchangeSession { Mode = ExchangeMode.FixedQuantity, RemainingCount = 1 }, out reason);

    /// <summary>交換の回数や終了条件を指定して実行する。</summary>
    public bool RequestWithTravel(ExchangeDefinition definition, ExchangeSession session, out string reason)
    {
        if (!this.Request(definition, out reason))
        {
            return false;
        }

        if (!definition.HasLocation)
        {
            this.pendingRequest = null;
            this.Fail(ExchangeFailure.NpcNotFound, "この交換先は NPC の座標が解決できていません");
            reason = this.StatusDetail;
            return false;
        }

        // InclusionShop（スクリップ交換など）は画面の操作方法が違い、まだ実行に対応していない。
        // 移動して話しかけたところで交換できないため、始める前に断る。
        if (definition.UsesInclusionShop)
        {
            this.pendingRequest = null;
            this.Fail(
                ExchangeFailure.ShopMismatch,
                "この交換所（アイテム交換画面）はまだ自動実行に対応していません。手動で交換してください");
            reason = this.StatusDetail;
            return false;
        }

        var needsTeleport = Svc.ClientState.TerritoryType != definition.TerritoryId;

        if (needsTeleport)
        {
            if (!this.lifestream.IsLoaded)
            {
                this.pendingRequest = null;
                this.Fail(
                    ExchangeFailure.TeleportUnavailable,
                    $"交換先は {NpcLocationService.GetTerritoryName(definition.TerritoryId)} です。Lifestream が導入されていないため移動できません");
                reason = this.StatusDetail;
                return false;
            }

            if (!this.aetheryte.TryFindTarget(definition.TerritoryId, out _))
            {
                this.pendingRequest = null;
                this.Fail(
                    ExchangeFailure.AetheryteNotAttuned,
                    $"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} のエーテライトにアクセスしていないため、テレポートできません");
                reason = this.StatusDetail;
                return false;
            }
        }

        if (!this.navigation.IsAvailable)
        {
            this.pendingRequest = null;
            this.Fail(ExchangeFailure.NavigationUnavailable, "vnavmesh が導入されていないため移動できません");
            reason = this.StatusDetail;
            return false;
        }

        // 移動から始めるので、Armed ではなく Navigate から入る。
        this.pendingRequest = null;
        this.travelTarget = definition;
        this.session = session;
        this.aethernetTried = false;
        this.reapproachAttempts = 0;
        this.destinationUpdates = 0;
        this.nextArmedAllowedUtc = DateTime.MinValue;
        this.stopAttempts = 0;
        this.sawAutoDutyRunning = false;
        this.ownership.Clear();
        this.ownership.IsClaiming = true;

        // 移動を始める前に、安全な状態になるまで待ち、外部プラグインを抑制し、AutoDuty を止める。
        // Duty の途中で抜けさせないため、ここで待つことが最優先になる。
        this.Step = ExchangeStep.WaitingSafeWindow;
        this.StatusDetail = "安全に開始できる状態を待っています";
        this.lastWaitLogUtc = DateTime.MinValue;
        return true;
    }

    /// <summary>Framework.Update から毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        switch (this.Step)
        {
            case ExchangeStep.WaitingSafeWindow:
                this.TickWaitingSafeWindow();
                break;

            case ExchangeStep.SuppressExternal:
                this.TickSuppressExternal();
                break;

            case ExchangeStep.StopAutoDuty:
                this.TickStopAutoDuty();
                break;

            case ExchangeStep.ResumeAutoDuty:
                this.TickResumeAutoDuty();
                break;

            case ExchangeStep.Teleport:
                this.TickTeleport();
                break;

            case ExchangeStep.AethernetHop:
                this.TickAethernetHop();
                break;

            case ExchangeStep.Navigate:
                this.TickNavigate();
                break;

            case ExchangeStep.Interact:
                this.TickInteract();
                break;

            case ExchangeStep.SelectMenu:
                this.TickSelectMenu();
                break;

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
    /// 安全に開始できる状態を待つ。
    ///
    /// ここには時間制限を設けない。コンテンツ中や戦闘中に時間切れで打ち切っても意味がなく、
    /// 待つこと自体が正しい振る舞いだからである。
    /// 何を待っているかは定期的にログへ残す。
    /// </summary>
    private void TickWaitingSafeWindow()
    {
        if (this.travelTarget is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        // Duty 中に待たされている間に、そのエリアを覚えておく。
        // ここで記録しておかないと、あとで AutoDuty を再開するときに渡すエリアが分からない。
        if (Player.IsInDuty)
        {
            var current = Svc.ClientState.TerritoryType;
            if (current != 0 && current != this.observedDutyTerritoryId)
            {
                this.observedDutyTerritoryId = current;
            }

        }

        // AutoDuty が動いているうちに記録しておく。
        // 交換を始めるのは停止したあとなので、その時点では動いていた証拠が残らない。
        if (this.autoDuty.IsLoaded && this.autoDuty.TryIsStopped(out var adStopped) && !adStopped)
        {
            this.sawAutoDutyRunning = true;
        }

        if (!SafetyGuard.IsSafeToStart(out var reason))
        {
            this.StatusDetail = $"待機中: {reason}";

            if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(60))
            {
                this.lastWaitLogUtc = DateTime.UtcNow;
                this.anomalyLog.Info("Wait", $"開始を待っています: {reason}");
            }

            return;
        }

        // AutoRetainer を抑制する前に、AutoDuty のループ間処理を終わらせる。
        //
        // AutoDuty はダンジョンから戻った直後に、リテイナー・GC 納品・修理などを
        // 自分のタスク列へ積む。リテイナー処理は AutoRetainer 本体に任せる形なので、
        // 先に抑制をかけると「ベルにはアクセスするがアイテムを回収しない」状態になる。
        //
        // ループ間処理を積んでいる間、AutoDuty は移動状態ではない。
        // 処理を終えて次のコンテンツへ向かい始めると移動状態になるため、
        // それを割り込んでよい合図として使う。
        if (!this.WaitForAutoDutyCycleEnd())
        {
            return;
        }

        this.Step = ExchangeStep.SuppressExternal;
        this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(60);
        this.StatusDetail = "外部プラグインの状態を確認しています";
    }

    /// <summary>
    /// AutoDuty が 1 サイクル（設定した周回数）を終えて停止するまで待つ。
    /// 待つ必要がなければ true を返す。
    ///
    /// 周回の途中に割り込む方法は無い。
    /// ループ間処理は AutoDuty の TaskManager に積まれた予約で、
    /// 一時停止しても、こちらが交換のためにエリアを移動した時点で
    /// AutoDuty の TerritoryChanged が TaskManager.Abort() を呼び、予約ごと消える。
    ///
    /// 逆に Stage.Stopped は AutoDuty が完全に静止したことを保証する状態で、
    /// TerritoryChanged も冒頭で抜けるため、こちらが何をしても反応しない。
    /// さらに、最終周のあとに実行される LoopsCompleteActions の最後で
    /// Stage.Stopped になるので、「停止した」は「ループ間処理も終了処理も全部終わった」を意味する。
    /// </summary>
    private bool WaitForAutoDutyCycleEnd()
    {
        if (!Plugin.C.WaitForAutoDutyCycleEnd || !this.autoDuty.IsLoaded)
        {
            return true;
        }

        if (!this.autoDuty.TryIsStopped(out var stopped))
        {
            // 状態が読めないうちは割り込まない。
            this.StatusDetail = "AutoDuty の状態を取得できません";
            return false;
        }

        if (stopped)
        {
            return true;
        }

        this.StatusDetail = "AutoDuty の周回が終わるのを待っています";

        if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(300))
        {
            this.lastWaitLogUtc = DateTime.UtcNow;
            this.anomalyLog.Info(
                "AutoDuty",
                "AutoDuty の周回が終わるのを待っています（周回の途中では割り込めません）");
        }

        return false;
    }

    /// <summary>
    /// AutoRetainer と競合しないようにする。
    ///
    /// 抑制を立ててからもう一度 IsBusy を確認する二段構えにする。
    /// 「暇である」ことを確認した直後に処理が始まる隙間を塞ぐため。
    /// </summary>
    private void TickSuppressExternal()
    {
        // 待っている間に次のコンテンツへ入ってしまうことがある。
        // Duty 中に AutoDuty を止めるのは最も避けたい事故なので、必ず戻る。
        if (this.ReturnToWaitIfUnsafe())
        {
            return;
        }

        if (!this.autoRetainer.IsLoaded || !Plugin.C.SuppressAutoRetainer)
        {
            this.Step = ExchangeStep.StopAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(10);
            return;
        }

        // まもなくベンチャーが完了する場合は、先にそちらを処理させる。
        // AutoRetainer は一定条件で自動的に始まるため、その直前に抑制をかけると
        // リテイナー処理を横取りする形になり、周回の流れを壊す。
        if (Plugin.C.YieldToUpcomingRetainerVenture && !this.autoRetainer.SuppressedByUs)
        {
            var contentId = TryGetContentId();
            if (contentId != 0 &&
                this.autoRetainer.TryGetClosestVentureSeconds(contentId, out var remaining) &&
                remaining >= 0 &&
                remaining <= Plugin.C.RetainerVentureYieldSeconds)
            {
                this.StatusDetail = $"リテイナーのベンチャー完了が近いため待機しています（残り {remaining} 秒）";

                if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(60))
                {
                    this.lastWaitLogUtc = DateTime.UtcNow;
                    this.anomalyLog.Info("Wait", $"ベンチャー完了が近いため交換を後回しにします（残り {remaining} 秒）");
                }

                return;
            }
        }

        // 1 段目: 処理中なら待つ。時間で打ち切らない。
        if (this.autoRetainer.IsBusyFailClosed())
        {
            this.StatusDetail = "AutoRetainer の処理が終わるのを待っています";

            if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(60))
            {
                this.lastWaitLogUtc = DateTime.UtcNow;
                this.anomalyLog.Info("Wait", "AutoRetainer の処理が終わるのを待っています");
            }

            return;
        }

        // 2 段目: 先に抑制を立ててから、もう一度確認する。
        if (!this.autoRetainer.SuppressedByUs)
        {
            if (!this.autoRetainer.Suppress())
            {
                this.Fail(ExchangeFailure.AutoRetainerIpcBroken, "AutoRetainer の抑制を設定できませんでした");
                return;
            }

            this.anomalyLog.Info("Suppress", "AutoRetainer の新規処理を抑制しました（実行中の処理は中断していません）");
            return;
        }

        if (this.autoRetainer.IsBusyFailClosed())
        {
            // 抑制を立てる直前に始まっていた場合。終わるまで待つ。
            this.StatusDetail = "AutoRetainer の処理が終わるのを待っています";
            return;
        }

        this.Step = ExchangeStep.StopAutoDuty;
        this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
        this.StatusDetail = "AutoDuty の状態を確認しています";

        this.TickStopAutoDuty();
    }

    /// <summary>AutoDuty を止める。止める前の状態を記録して、後で戻せるようにする。</summary>
    private void TickStopAutoDuty()
    {
        var target = this.travelTarget;
        if (target is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        // まだ止めていないなら、安全条件を確認し直す。
        // 止めたあとは途中で戻ると中途半端になるため確認しない。
        if (this.stopAttempts == 0 && this.ReturnToWaitIfUnsafe())
        {
            return;
        }

        if (!this.autoDuty.IsLoaded)
        {
            this.returnContext = null;
            this.BeginTravel(target);
            return;
        }

        // 初回だけ、停止前の状態を記録する。
        if (this.returnContext is null)
        {
            // 待機中に動いているのを見ていたなら、いま停止していても再開の対象にする。
            var wasRunning = this.autoDuty.IsRunningFailClosed() || this.sawAutoDutyRunning;
            this.autoDuty.TryIsLooping(out var looping);

            // 観測できた Duty のエリアを優先する。無ければ現在地を使う。
            var resumeTerritory = this.observedDutyTerritoryId != 0
                ? this.observedDutyTerritoryId
                : Svc.ClientState.TerritoryType;

            this.returnContext = new ReturnContext
            {
                WasAutoDutyRunning = wasRunning,
                AutoDutyTerritoryId = resumeTerritory,
                WasLooping = looping,
            };

            if (wasRunning)
            {
                var hasPathForResume = this.autoDuty.TryContentHasPath(resumeTerritory, out var canResume) && canResume;
                this.anomalyLog.Info(
                    "AutoDuty",
                    hasPathForResume
                        ? $"再開先として {NpcLocationService.GetTerritoryName(resumeTerritory)} を記録しました"
                        : $"再開先の候補 {NpcLocationService.GetTerritoryName(resumeTerritory)} に AutoDuty の経路がありません。交換後の再開はできない見込みです");
            }

            if (!wasRunning)
            {
                this.BeginTravel(target);
                return;
            }

            this.anomalyLog.Info("AutoDuty", "AutoDuty を停止します");
        }

        if (this.autoDuty.TryIsStopped(out var stopped) && stopped)
        {
            this.BeginTravel(target);
            return;
        }

        // 窓は短い。最初の 1 回は間を置かずに送る。
        // ここで 1 秒待つと、その間に AutoDuty が次のコンテンツへ入ってしまい、
        // 交換の機会を逃して次の周回まで持ち越しになる。
        if (this.stopAttempts > 0 && !EzThrottler.Throttle("AutoCollector.StopAutoDuty", 1000))
        {
            return;
        }

        this.stopAttempts++;
        this.autoDuty.TryStop();

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.Fail(ExchangeFailure.AutoDutyStopFailed, "AutoDuty を停止できませんでした");
        }
    }

    /// <summary>
    /// 安全に進められない状態になっていたら、安全待機へ戻す。
    /// 戻したときは true を返す。
    /// </summary>
    private bool ReturnToWaitIfUnsafe()
    {
        if (SafetyGuard.IsSafeToStart(out var reason))
        {
            return false;
        }

        this.Step = ExchangeStep.WaitingSafeWindow;
        this.StatusDetail = $"待機中: {reason}";
        this.lastWaitLogUtc = DateTime.MinValue;

        if (Player.IsInDuty)
        {
            this.anomalyLog.Info("Wait", "コンテンツに入ったため、交換を次の切れ目まで見送ります");
        }

        return true;
    }

    /// <summary>移動を開始する。テレポートが必要かどうかはここで判断する。</summary>
    private void BeginTravel(ExchangeDefinition definition)
    {
        if (Svc.ClientState.TerritoryType != definition.TerritoryId)
        {
            this.teleportAttempts = 0;
            this.Step = ExchangeStep.Teleport;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(90);
            this.StatusDetail = $"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} へテレポートしています";
            return;
        }

        if (!this.BeginTravelToNpc(definition, out var navFailure))
        {
            this.Fail(ExchangeFailure.NavigationFailed, navFailure);
        }
    }

    /// <summary>
    /// AutoDuty を再開する。
    ///
    /// 停止前に動いていた場合だけ再開する。ユーザー自身が止めていたものを勝手に開始しない。
    /// 周回カウンタは AutoDuty 側から復元する手段がないため 0 から数え直しになる。
    /// </summary>
    private void TickResumeAutoDuty()
    {
        var context = this.returnContext;

        if (context is null || !context.WasAutoDutyRunning || !Plugin.C.ResumeAutoDuty || !this.autoDuty.IsLoaded)
        {
            this.FinishAfterExchange();
            return;
        }

        if (this.autoDuty.TryIsNavigating(out var navigating) && navigating)
        {
            this.FinishAfterExchange();
            return;
        }

        if (this.autoDuty.TryIsLooping(out var looping) && looping)
        {
            this.FinishAfterExchange();
            return;
        }

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            // 再開できなくても交換自体は成功している。自動で繰り返さず、ユーザーに知らせて終える。
            this.anomalyLog.Error("AutoDuty", "AutoDuty を再開できませんでした。手動で再開してください");
            this.Failure = ExchangeFailure.AutoDutyResumeFailed;
            this.FinishAfterExchange();
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.ResumeAutoDuty", 2000))
        {
            return;
        }

        // 抑制を解く前に、自分が開いたショップを閉じる。
        // 開いたまま解除すると、AutoRetainer が動き出したときに
        // こちらのウィンドウが残っていて操作が噛み合わなくなる。
        this.CloseOwned("ShopExchangeCurrency", useCloseFirst: true);

        // AutoDuty を動かす前に抑制を解く。
        // AutoDuty はループ間処理で AutoRetainer を呼ぶため、抑制したまま再開すると
        // リテイナー処理が動かないまま次の周回に入る。
        this.autoRetainer.Release();

        if (!this.autoDuty.TryContentHasPath(context.AutoDutyTerritoryId, out var hasPath) || !hasPath)
        {
            this.anomalyLog.Error(
                "AutoDuty",
                $"{NpcLocationService.GetTerritoryName(context.AutoDutyTerritoryId)} に AutoDuty の経路が無いため再開できません。" +
                "手動で再開してください（状況タブの「AutoDuty を再開」からも実行できます）");
            this.Failure = ExchangeFailure.AutoDutyResumeFailed;
            this.FinishAfterExchange();
            return;
        }

        this.anomalyLog.Info("AutoDuty", "AutoDuty を再開します（周回カウンタは 0 から数え直しになります）");
        this.autoDuty.TryRun(context.AutoDutyTerritoryId);
    }

    /// <summary>交換後の後始末。開いたショップを閉じ、抑制を解除して終了する。</summary>
    private void FinishAfterExchange()
    {
        // 自分が開いたショップは自分で閉じる。開けっ放しにすると
        // 次の交換の事前条件（ブロックするアドオンが無いこと）にも引っかかる。
        this.CloseOwned("ShopExchangeCurrency", useCloseFirst: true);

        this.autoRetainer.Release();
        this.returnContext = null;
        this.session = null;
        this.travelTarget = null;
        this.ownership.Clear();
        this.Step = ExchangeStep.Done;

        if (this.Failure == ExchangeFailure.None)
        {
            this.StatusDetail = "完了しました";
        }
    }

    /// <summary>
    /// 移動の手前で、エーテライト網で短縮できるならそちらを先に使う。
    /// 都市は広く、多層構造の場所では徒歩だと経路探索が詰まりやすい。
    /// </summary>
    private bool BeginTravelToNpc(ExchangeDefinition definition, out string failureReason)
    {
        failureReason = string.Empty;

        if (!this.aethernetTried &&
            this.lifestream.IsLoaded &&
            this.aetheryte.TryFindAethernetShortcut(definition.NpcPosition, 40f, out var shardId, out var shardName, out var gain))
        {
            this.aethernetTried = true;
            this.hopStartPosition = Player.Available ? Player.Position : default;

            if (this.lifestream.TryAethernetTeleportById(shardId, out var accepted) && accepted)
            {
                this.anomalyLog.Info("Travel", $"「{shardName}」へ転送します（約 {gain:F0} 短縮）");
                this.Step = ExchangeStep.AethernetHop;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(60);
                this.StatusDetail = $"「{shardName}」へ転送しています";
                return true;
            }

            // 受け付けられなかったら徒歩に切り替える。失敗にはしない。
            this.anomalyLog.Info("Travel", $"「{shardName}」への転送は使えなかったため、徒歩で向かいます");
        }

        return this.BeginNavigation(definition, out failureReason);
    }

    /// <summary>移動を開始して状態を Navigate にする。</summary>
    private bool BeginNavigation(ExchangeDefinition definition, out string failureReason)
    {
        // 配置ファイル由来の座標はナビメッシュに乗っていないことがある。
        // 床にスナップできるならそちらを目的地にする。
        var destination = definition.NpcPosition;
        if (this.navigation.TrySnapToFloor(destination, out var snapped))
        {
            destination = snapped;
        }

        if (!this.navigation.BeginMove(destination, Plugin.C.NpcApproachRange, out failureReason))
        {
            return false;
        }

        this.navigationDestination = destination;
        this.Step = ExchangeStep.Navigate;
        this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(180);
        this.StatusDetail = $"{definition.NpcName} のところへ移動しています";
        return true;
    }

    /// <summary>エーテライト網の転送完了を待つ。</summary>
    private void TickAethernetHop()
    {
        var target = this.travelTarget;
        if (target is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        var busyKnown = this.lifestream.TryIsBusy(out var busy);
        var moved = Player.Available && Vector3.DistanceSquared(Player.Position, this.hopStartPosition) > 100f;

        // Lifestream の処理が終わり、実際に移動していれば完了とみなす。
        if (busyKnown && !busy && moved && GenericHelpers.IsScreenReady() && Player.Interactable)
        {
            if (!this.BeginNavigation(target, out var navFailure))
            {
                this.Fail(ExchangeFailure.NavigationFailed, navFailure);
            }

            return;
        }

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            // 転送が完了しなくても徒歩で向かえばよい。失敗にはしない。
            this.anomalyLog.Warn("Travel", "エーテライト網の転送が完了しなかったため、徒歩で向かいます");
            if (!this.BeginNavigation(target, out var navFailure))
            {
                this.Fail(ExchangeFailure.NavigationFailed, navFailure);
            }
        }
    }

    /// <summary>
    /// テレポート。
    ///
    /// Lifestream の IPC 版 Teleport は待機なしの経路を通るため、
    /// 呼んだあと IsBusy() は true にならない。エリア遷移はこちらで待つ。
    /// </summary>
    private void TickTeleport()
    {
        var target = this.travelTarget;
        if (target is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        // 到着したかを見る。読み込み中の状態では判定しない。
        if (Svc.ClientState.TerritoryType == target.TerritoryId)
        {
            if (!GenericHelpers.IsScreenReady() || !Player.Available || !Player.Interactable)
            {
                return;
            }

            if (!this.BeginTravelToNpc(target, out var navFailure))
            {
                this.Fail(ExchangeFailure.NavigationFailed, navFailure);
            }

            return;
        }

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.Fail(ExchangeFailure.TeleportFailed, "テレポートがタイムアウトしました");
            return;
        }

        // 詠唱中・操作不能のときは送らない
        if (!Player.Available || Player.IsCasting || GenericHelpers.IsOccupied() || !GenericHelpers.IsScreenReady())
        {
            return;
        }

        // 戦闘中・騎乗動作中・移動中はテレポートが通らない。
        // ここで弾いておかないと、受け付けてもらえないまま試行回数だけを消費する。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            this.StatusDetail = "戦闘が終わるのを待っています";
            return;
        }

        if (Svc.Condition[ConditionFlag.Mounting] ||
            Svc.Condition[ConditionFlag.Mounting71] ||
            Svc.Condition[ConditionFlag.BetweenAreas] ||
            Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        // まだ足が止まっていないなら止める。移動中は詠唱が中断される。
        if (Player.Object is { } player && player.IsCasting)
        {
            return;
        }

        if (this.lifestream.TryIsBusy(out var busy) && busy)
        {
            return;
        }

        // 送りすぎないよう間隔を空ける。回数にも上限を設ける。
        if (!EzThrottler.Throttle("AutoCollector.Teleport", 3000))
        {
            return;
        }

        if (this.teleportAttempts >= 5)
        {
            this.Fail(
                ExchangeFailure.TeleportFailed,
                "テレポートを開始できませんでした（Lifestream が依頼を受け付けませんでした）");
            return;
        }

        if (!this.aetheryte.TryFindTarget(target.TerritoryId, out var destination) || destination is null)
        {
            this.Fail(ExchangeFailure.AetheryteNotAttuned, "テレポート先のエーテライトが見つかりません");
            return;
        }

        this.teleportAttempts++;

        if (!this.lifestream.TryTeleport(destination.AetheryteId, destination.SubIndex, out var accepted))
        {
            this.Fail(ExchangeFailure.TeleportFailed, "Lifestream へテレポートを依頼できませんでした");
            return;
        }

        if (!accepted)
        {
            this.anomalyLog.Warn("Teleport", $"{destination.Name} へのテレポートを受け付けてもらえませんでした（{this.teleportAttempts} 回目）");
            return;
        }

        this.StatusDetail = $"{destination.Name} へテレポートしています";
    }

    private void TickNavigate()
    {
        var target = this.travelTarget;
        if (target is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        // ショップがもう開いているなら移動は不要。
        if (this.shopService.IsShopOpen())
        {
            this.navigation.Stop();
            this.pendingRequest = target;
            this.Step = ExchangeStep.Armed;
            return;
        }

        // NPC が視界に入ったら、配置ファイル由来の座標ではなく実際の位置を目的地にする。
        // 配置ファイルの座標は実機とずれることがあり、そのままでは
        // 到着したつもりでも「話しかけられない距離」になる。
        //
        // ただし目的地だけ書き換えても、vnavmesh は元の座標へ向かい続ける。
        // 大きくずれている場合は移動そのものを出し直す必要がある。
        if (this.interaction.TryFindNpc(target.NpcDataId, out var liveNpc) && liveNpc is not null)
        {
            var live = liveNpc.Position;

            if (Vector3.Distance(live, this.navigationDestination) > 3f &&
                this.destinationUpdates < 3 &&
                EzThrottler.Throttle("AutoCollector.Reissue", 4000))
            {
                this.destinationUpdates++;
                this.navigationDestination = live;

                if (this.navigation.Reissue(live, Plugin.C.NpcApproachRange, out var reissueFailure))
                {
                    this.anomalyLog.Info("Navigation", $"{target.NpcName} の実際の位置へ経路を引き直しました");
                }
                else
                {
                    this.anomalyLog.Warn("Navigation", $"経路を引き直せませんでした: {reissueFailure}");
                }

                return;
            }

            // ずれが小さければ到着判定にだけ反映する。
            if (Vector3.Distance(live, this.navigationDestination) <= 3f)
            {
                this.navigationDestination = live;
            }
        }

        var status = this.navigation.Tick(this.navigationDestination, Plugin.C.NpcApproachRange);

        switch (status)
        {
            case MoveStatus.Arrived:
                this.navigation.Stop();
                this.Step = ExchangeStep.Interact;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
                this.StatusDetail = $"{target.NpcName} に話しかけています";
                return;

            case MoveStatus.Stuck:
                this.navigation.Stop();
                this.Fail(
                    ExchangeFailure.NavigationFailed,
                    $"{target.NpcName} へ移動できません。建物の中など、経路がつながらない場所にいる可能性があります");
                return;

            case MoveStatus.Failed:
                this.navigation.Stop();
                this.Fail(ExchangeFailure.NavigationFailed, "移動に失敗しました");
                return;
        }

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.navigation.Stop();
            this.Fail(ExchangeFailure.NavigationFailed, "移動がタイムアウトしました");
        }
    }

    private void TickInteract()
    {
        var target = this.travelTarget;
        if (target is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        // ショップが開いたら事前条件の評価へ進む。
        if (this.shopService.IsShopOpen())
        {
            this.pendingRequest = target;
            this.Step = ExchangeStep.Armed;
            return;
        }

        // 会話ウィンドウが出ていたら進める。
        // これを進めないと、話しかけてもショップまで辿り着かない。
        if (this.interaction.TryAdvanceTalk())
        {
            this.StatusDetail = "会話を進めています";
            return;
        }

        // 会話メニューが出たらそちらを処理する。
        if (this.menu.IsMenuOpen())
        {
            this.Step = ExchangeStep.SelectMenu;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(45);
            this.StatusDetail = "会話メニューを処理しています";
            return;
        }

        if (!this.interaction.TryFindNpc(target.NpcDataId, out var npc) || npc is null)
        {
            if (DateTime.UtcNow > this.stepDeadlineUtc)
            {
                this.Fail(ExchangeFailure.NpcNotFound, $"{target.NpcName} が見つかりません");
            }

            return;
        }

        // 遠すぎると、話しかけても「話しかけられない距離です」と出るだけで進まない。
        // その場合は諦めずに、NPC の実際の位置へ近づき直す。
        if (!InteractionService.IsWithinInteractRange(npc))
        {
            if (this.reapproachAttempts >= 3)
            {
                this.Fail(ExchangeFailure.InteractFailed, $"{target.NpcName} に近づけませんでした");
                return;
            }

            if (!EzThrottler.Throttle("AutoCollector.Reapproach", 3000))
            {
                return;
            }

            this.reapproachAttempts++;
            this.anomalyLog.Info("Interact", $"{target.NpcName} から離れているため、近づき直します（{this.reapproachAttempts} 回目）");

            this.navigationDestination = npc.Position;
            if (this.navigation.BeginMove(npc.Position, 2.5f, out var moveFailure))
            {
                this.Step = ExchangeStep.Navigate;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(60);
                this.StatusDetail = $"{target.NpcName} へ近づき直しています";
            }
            else
            {
                this.anomalyLog.Warn("Interact", $"近づき直せませんでした: {moveFailure}");
            }

            return;
        }

        this.interaction.StepInteract(npc);

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.Fail(ExchangeFailure.InteractFailed, "話しかけてもショップが開きませんでした");
        }
    }

    private void TickSelectMenu()
    {
        var target = this.travelTarget;
        if (target is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        if (this.shopService.IsShopOpen())
        {
            this.pendingRequest = target;
            this.Step = ExchangeStep.Armed;
            return;
        }

        // 選択後に会話が挟まることがある。
        if (this.interaction.TryAdvanceTalk())
        {
            this.StatusDetail = "会話を進めています";
            return;
        }

        if (!this.menu.IsMenuOpen())
        {
            // 閉じただけかもしれないので、対話からやり直す。
            this.Step = ExchangeStep.Interact;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        // ヒントの候補を順に試す。
        //
        // NPC が複数のショップを持つ場合、話しかけると SpecialShop.Name が選択肢として並ぶ。
        // TopicSelect を経由する場合は、先に話題を選んでからショップ名の一覧になることもある。
        // メニューは開いている限り毎フレームここへ来るため、候補を順に試すだけで多段のメニューも通過できる。
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.ShopName))
        {
            candidates.Add(target.ShopName);
        }

        if (!string.IsNullOrWhiteSpace(target.MenuHint))
        {
            candidates.Add(target.MenuHint);
        }

        if (candidates.Count == 0)
        {
            var entries = this.menu.ListEntries();
            this.Fail(
                ExchangeFailure.MenuResolutionFailed,
                $"会話メニューのどれを選ぶべきか分かりません。選択肢: {string.Join(" / ", entries)}");
            return;
        }

        var ambiguous = false;

        foreach (var hint in candidates)
        {
            if (this.menu.TrySelectByText(hint, out var failure))
            {
                this.StatusDetail = $"「{hint}」を選びました";
                return;
            }

            if (failure == MenuSelectFailure.Ambiguous)
            {
                ambiguous = true;
            }
        }

        if (DateTime.UtcNow <= this.stepDeadlineUtc)
        {
            // まだ猶予がある。メニューが切り替わる途中の可能性もあるので待つ。
            return;
        }

        var shown = this.menu.ListEntries();

        if (ambiguous)
        {
            this.Fail(
                ExchangeFailure.MenuAmbiguous,
                $"選択肢を 1 つに絞れませんでした。候補: {string.Join(" / ", candidates)} / 選択肢: {string.Join(" / ", shown)}");
            return;
        }

        this.Fail(
            ExchangeFailure.MenuResolutionFailed,
            $"一致する選択肢がありません。候補: {string.Join(" / ", candidates)} / 選択肢: {string.Join(" / ", shown)}");
    }

    /// <summary>
    /// 事前条件をすべて評価し、通ったらその場で発火する。
    /// この関数の中でフレームを跨がない。1 回で決着させ、失敗したら Error に落とす。
    /// </summary>
    private void TickArmed()
    {
        // 前回の交換の反映が終わるまで待つ。
        // ここで待たないと、前回分がまだ届いていない所持数で検証してしまう。
        if (DateTime.UtcNow < this.nextArmedAllowedUtc)
        {
            return;
        }

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
        var identification = this.shopService.IdentifyShop(entries, this.resolver.LiveResults, definition.ShopId);
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
        // 単に 1 枠空いていればよいのではない。
        // AutoRetainer は所持枠が空いていないキャラクタを処理対象から外して保存するため、
        // 交換で枠を使い切ると、あとでリテイナーが回らなくなる。
        var keepFree = Math.Max(0, Plugin.C.KeepFreeInventorySlots);
        if (!this.currencyService.TryGetEmptyBagSlots(out var freeSlots) || freeSlots <= keepFree)
        {
            this.Fail(
                ExchangeFailure.NoBagSpace,
                $"所持枠の空きが {freeSlots} です。{keepFree} 枠を残す設定のため交換しません");
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
            // 何も動いていないのか、片方だけ動いたのかで意味が違う。
            var currencyKnown = this.currencyService.TryGetCount(attempt.CurrencyItemId, out var currencyNow);
            var rewardKnown = this.currencyService.TryGetCount(attempt.RewardItemId, out var rewardNow, includeEquipped: true, includeArmory: true);

            if (currencyKnown && rewardKnown &&
                currencyNow == attempt.CurrencyBefore && rewardNow == attempt.RewardBefore)
            {
                this.FailUnresolved(
                    ExchangeFailure.ExchangeNotApplied,
                    "交換が行われた形跡がありません。所持数が変化していません");
                return;
            }

            if (currencyKnown && rewardKnown)
            {
                this.FailUnresolved(
                    ExchangeFailure.ExchangeUnexpectedDelta,
                    $"想定外の変化です。通貨 {attempt.CurrencyBefore} → {currencyNow} / {attempt.RewardName} {attempt.RewardBefore} → {rewardNow}");
                return;
            }

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

            this.Failure = ExchangeFailure.None;
            this.StatusDetail = attempt.Outcome;

            if (this.session is { } current)
            {
                current.Completed++;
                if (current.RemainingCount > 0)
                {
                    current.RemainingCount--;
                }
            }

            // まだ交換を続ける条件を満たしているなら、同じショップでもう一度行う。
            // ここへ戻れるのは結果が確定した後だけで、未確定のまま再送することはない。
            if (this.ShouldContinueSession(currencyAfter, rewardAfter, out var stopReason))
            {
                this.pendingRequest = this.travelTarget;
                this.Step = ExchangeStep.Armed;
                this.nextArmedAllowedUtc = DateTime.UtcNow.Add(SettleBetweenExchanges);
                this.StatusDetail = $"{this.session?.Completed ?? 0} 回交換しました。続けます";
                return true;
            }

            this.anomalyLog.Info("Exchange", $"交換を終了します（{this.session?.Completed ?? 0} 回）: {stopReason}");

            // 止めていたものを元に戻す。
            this.Step = ExchangeStep.ResumeAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            return true;
        }

        // どちらもまったく動いていない場合は、まだ反映されていないだけの可能性がある。
        // タイムアウトまで待ち、それでも動かなければ ExchangeNotApplied で止める。
        if (rewardAfter == attempt.RewardBefore && currencyAfter == attempt.CurrencyBefore)
        {
            return false;
        }

        // 片方だけ動いている状態は、サーバーからの反映が片方だけ先に届いただけのことがある。
        // ここで即座にエラーにすると、正常な交換を失敗と誤判定する。
        // タイムアウトまでは待ち、それでも揃わなければ想定外として扱う。
        return false;
    }

    /// <summary>
    /// もう一度交換すべきかを判断する。
    ///
    /// 判断材料が足りない場合は「続けない」に倒す。
    /// 買いすぎは取り返しがつかないため、迷ったら止める。
    /// </summary>
    private bool ShouldContinueSession(int currencyAfter, int rewardAfter, out string stopReason)
    {
        stopReason = string.Empty;

        var current = this.session;
        var definition = this.travelTarget;

        if (current is null || definition is null)
        {
            stopReason = "セッションが設定されていません";
            return false;
        }

        if (current.Completed >= ExchangeSession.HardLimit)
        {
            stopReason = $"上限の {ExchangeSession.HardLimit} 回に達しました";
            return false;
        }

        if (this.aborted)
        {
            stopReason = "停止が要求されました";
            return false;
        }

        // 次の 1 回分の通貨が無ければ終わり。
        if (currencyAfter < definition.CurrencyCost)
        {
            stopReason = "通貨が足りません";
            return false;
        }

        // 所持枠が無ければ終わり。残す枠の設定も守る。
        var keepFree = Math.Max(0, Plugin.C.KeepFreeInventorySlots);
        if (!this.currencyService.TryGetEmptyBagSlots(out var freeSlots) || freeSlots <= keepFree)
        {
            stopReason = $"所持枠の空きが {freeSlots} になりました（{keepFree} 枠を残す設定）";
            return false;
        }

        switch (current.Mode)
        {
            case ExchangeMode.FixedQuantity:
                if (current.RemainingCount <= 0)
                {
                    stopReason = "指定回数を交換しました";
                    return false;
                }

                return true;

            case ExchangeMode.UntilCurrencyReserve:
                if (currencyAfter - definition.CurrencyCost < current.CurrencyReserve)
                {
                    stopReason = $"残す通貨量 {current.CurrencyReserve} に達しました";
                    return false;
                }

                return true;

            case ExchangeMode.UntilTargetQuantity:
                if (rewardAfter >= current.TargetQuantity)
                {
                    stopReason = $"目標の {current.TargetQuantity} 個に達しました";
                    return false;
                }

                return true;

            case ExchangeMode.MaxExchange:
                return true;

            default:
                stopReason = "交換モードが不明です";
                return false;
        }
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

    /// <summary>
    /// 撃つ前の失敗。inFlight は立っていないので普通に Error へ落とす。
    ///
    /// 失敗しても握った制御は必ず手放す。
    /// ここで解放しないと、AutoRetainer を抑制したまま止まり続けることになる。
    /// </summary>
    private void Fail(ExchangeFailure failure, string detail)
    {
        this.Step = ExchangeStep.Error;
        this.Failure = failure;
        this.StatusDetail = detail;
        this.anomalyLog.Warn("Exchange", $"{failure}: {detail}");

        this.ReleaseHeldControl();
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

        this.ReleaseHeldControl();
    }

    /// <summary>
    /// 握った制御を手放す。移動を止め、外部プラグインの抑制を解く。
    /// 失敗しても必ず通す必要があるため、個々の失敗で止めない。
    /// </summary>
    private void ReleaseHeldControl()
    {
        // 自分が止めた AutoDuty は、交換に失敗しても元に戻す。
        // 止めっぱなしにすると周回が止まったまま棒立ちになる。
        try
        {
            if (Plugin.C.ResumeAutoDutyOnFailure &&
                this.returnContext is { WasAutoDutyRunning: true } context &&
                Plugin.C.ResumeAutoDuty &&
                this.autoDuty.IsLoaded &&
                this.autoDuty.TryContentHasPath(context.AutoDutyTerritoryId, out var hasPath) && hasPath)
            {
                this.anomalyLog.Info("AutoDuty", "交換に失敗しましたが、停止前に動いていた AutoDuty を再開します");
                this.autoDuty.TryRun(context.AutoDutyTerritoryId);
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Cleanup", $"AutoDuty を再開できませんでした: {ex.Message}");
        }

        try
        {
            this.navigation.Stop();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Cleanup", $"移動を停止できませんでした: {ex.Message}");
        }

        try
        {
            this.autoRetainer.Release();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Cleanup", $"AutoRetainer の抑制を解除できませんでした: {ex.Message}");
        }

        this.session = null;
        this.travelTarget = null;
        this.returnContext = null;
    }
}
