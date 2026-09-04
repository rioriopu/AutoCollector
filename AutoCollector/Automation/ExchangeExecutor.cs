using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using ECommons;
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
    ExchangeResolver resolver,
    NavigationService navigation,
    InteractionService interaction,
    MenuService menu,
    AddonOwnershipTracker ownership,
    AetheryteService aetheryte,
    LifestreamIpc lifestream)
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
    private readonly NavigationService navigation = navigation;
    private readonly InteractionService interaction = interaction;
    private readonly MenuService menu = menu;
    private readonly AddonOwnershipTracker ownership = ownership;
    private readonly AetheryteService aetheryte = aetheryte;
    private readonly LifestreamIpc lifestream = lifestream;

    private int teleportAttempts;
    private bool aethernetTried;
    private Vector3 hopStartPosition;
    private Vector3 navigationDestination;

    /// <summary>移動から始める場合の対象。null なら手動でショップを開いた状態からの実行。</summary>
    private ExchangeDefinition? travelTarget;
    private DateTime stepDeadlineUtc;

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
        this.aethernetTried = false;
        this.ownership.Clear();
        this.ownership.IsClaiming = true;

        if (needsTeleport)
        {
            this.teleportAttempts = 0;
            this.Step = ExchangeStep.Teleport;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(90);
            this.StatusDetail = $"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} へテレポートしています";
            return true;
        }

        if (!this.BeginTravelToNpc(definition, out var navFailure))
        {
            this.Fail(ExchangeFailure.NavigationFailed, navFailure);
            reason = this.StatusDetail;
            return false;
        }

        return true;
    }

    /// <summary>Framework.Update から毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        switch (this.Step)
        {
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

        if (this.lifestream.TryIsBusy(out var busy) && busy)
        {
            return;
        }

        // 送りすぎないよう間隔を空ける。回数にも上限を設ける。
        if (!EzThrottler.Throttle("AutoCollector.Teleport", 3000))
        {
            return;
        }

        if (this.teleportAttempts >= 3)
        {
            this.Fail(ExchangeFailure.TeleportFailed, "テレポートを開始できませんでした");
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
                this.Fail(ExchangeFailure.NavigationFailed, "移動が進まなくなりました");
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

        // 会話メニューが出たらそちらを処理する。
        if (this.menu.IsMenuOpen())
        {
            this.Step = ExchangeStep.SelectMenu;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(45);
            this.StatusDetail = "会話メニューを処理しています";
            return;
        }

        if (this.interaction.TryFindNpc(target.NpcDataId, out var npc) && npc is not null)
        {
            this.interaction.StepInteract(npc);
        }
        else if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.Fail(ExchangeFailure.NpcNotFound, $"{target.NpcName} が見つかりません");
            return;
        }

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
            this.travelTarget = null;
            this.ownership.Clear();
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
