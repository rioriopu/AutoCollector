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

    /// <summary>
    /// エーテライト網で別のエリアへ移る。
    ///
    /// ウルダハ：ザル回廊のようにエーテライトが無いエリアは、
    /// 同じ網の親エーテライトへ飛んでから、網で移動する必要がある。
    /// </summary>
    AethernetTransfer,

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

    /// <summary>アイテム交換画面で、系統と種別を選んでいる。</summary>
    SelectInclusionCategory,

    /// <summary>アイテム交換画面の確認ダイアログに答えている。</summary>
    InclusionConfirm,

    /// <summary>収集品を納品している。交換ではなく納品のための移動だった場合に入る。</summary>
    DeliverCollectables,

    /// <summary>納品画面を閉じている。閉じたことを確認してから次へ進む。</summary>
    CloseDeliveryWindow,

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

    /// <summary>
    /// 交換の途中でショップが閉じた。
    ///
    /// 話しかけられる距離から外れると、ゲームがウィンドウを閉じる。
    /// 品の問題ではないので、次の機会に試し直してよい。
    /// </summary>
    ShopClosedUnexpectedly,
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
    ExternalPluginError,
    InclusionShopUnsupported,
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

    /// <summary>この 1 回で何個交換しようとしたか。まとめ買いのときに 2 以上になる。</summary>
    public int Amount { get; set; } = 1;

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
/// <summary>1 回の移動で交換する品の 1 件分。</summary>
public sealed class ExchangeTarget
{
    public required ExchangeDefinition Definition { get; init; }

    /// <summary>上限を設けないか。true なら回数では止めない。</summary>
    public bool Unlimited { get; init; }

    /// <summary>
    /// 残りの**個数**。<see cref="Unlimited"/> が true のときは見ない。
    ///
    /// **回数ではない。** 利用者が入れる「一括交換する個数」がそのまま入る。
    /// 1 回の交換で 2 個以上もらえる品があるため、
    /// 撃つ回数へ直すときは 1 回あたりの個数で割ること。
    /// 混ぜていたため、1 回で 3 個もらえる品で 3 倍の数を交換していた。
    /// </summary>
    public int Remaining { get; set; }

    /// <summary>
    /// 所持数の上限。ここまで持つように交換する。0 なら上限なし。
    /// 足りないぶんだけ交換し、すでに達していれば飛ばす。
    /// </summary>
    public int OwnedLimit { get; init; }

    /// <summary>この品を交換した回数。</summary>
    public int Completed { get; set; }
}

public sealed class ExchangeSession
{
    /// <summary>
    /// 暴走への歯止め。この回数を超えたら理由に関わらず打ち切る。
    ///
    /// **1 回の移動で 200 回は買いすぎ。**
    /// 交換は取り返しがつかない。条件の判定をすり抜けたとき、
    /// 利用者が気づいて止めるまでに何個買われるかがこの数で決まる。
    ///
    /// 実際に上限を見ていない不具合があり、通貨が尽きるまで買い続けた。
    /// そのとき止めたのは利用者の手で、この歯止めではなかった。
    ///
    /// 1 回の移動で 30 回も交換すれば普通の用は足りる。
    /// 足りなければ次の周回でまた出かける。
    /// </summary>
    public const int HardLimit = 30;

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

    /// <summary>
    /// この移動で交換する品の並び。上から順に進める。
    ///
    /// 空なら従来どおり 1 品だけを扱う（手動実行など）。
    /// </summary>
    public List<ExchangeTarget> Targets { get; init; } = [];

    /// <summary>いま扱っている品の位置。</summary>
    public int TargetIndex { get; set; }

    /// <summary>いま扱っている品。並びが空なら null。</summary>
    public ExchangeTarget? Current
        => this.TargetIndex >= 0 && this.TargetIndex < this.Targets.Count ? this.Targets[this.TargetIndex] : null;
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
    AutoRetainerIpc autoRetainer,
    ArtisanIpc artisan,
    InclusionShopService inclusionShop,
    CollectablesShopService collectablesShop,
    CollectableDeliveryRunner collectableDelivery)
{
    /// <summary>交換コマンド。0 が購入であることの根拠は実測のみ。他の用途に流用しない。</summary>
    private const int ExchangeCommand = 0;

    /// <summary>
    /// アイテム交換画面（InclusionShop）の購入コマンド。
    /// ECommons の AddonMaster.InclusionShop が Fire(14, index, amount) を使っている。
    /// </summary>
    private const int InclusionExchangeCommand = 14;

    /// <summary>
    /// 1 回の発火で交換する上限。
    ///
    /// 実測で Fire(14, 0u, 2u) が通り +2 になることを確認している。
    /// 上限がどこまでかは確かめていないため、控えめに置く。
    /// 1 個ずつだと 1 回あたり 1.5 秒かかり、10 個で 15 秒になる。
    /// </summary>
    private const int MaxBatchAmount = 20;

    /// <summary>個数。MVP は 1 固定。2 以上は未実測のため設定に露出させない。</summary>
    private const int ExchangeQuantity = 1;

    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 本文を照合できないまま確認ダイアログを押してよい、発火からの猶予。
    ///
    /// 実測では発火から確認まで約 1 秒（docs/03 の F-10）。
    /// 短く取るほど、無関係なダイアログを押す余地が減る。
    /// </summary>
    private static readonly TimeSpan OwnedDialogWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 本文にこれが出ていたら、交換の確認ではないと判断して押さない。
    ///
    /// 本文で「合っている」ことを確かめられない経路の最後の砦。
    /// 交換の確認には出ない語だけを並べる。取り返しのつかない操作を優先して挙げる。
    /// </summary>
    private static readonly string[] DangerousDialogWords =
    [
        "捨て", "破棄", "削除", "分解", "精製", "売却", "ログアウト", "タイトル", "トレード",
    ];

    /// <summary>
    /// 交換に付随して出ることが分かっている確認の言い回し。
    ///
    /// 本文に通貨名もコストも出ないため、値段での照合では拾えない。
    /// ここに並べるのは**実際に見た文面だけ**。想像で足さない。
    /// </summary>
    private static readonly string[] KnownExchangeConfirmations =
    [
        // 装備できない品を交換するとき（利用者の実測・2026-09-19）
        // 「クラスやレベル、装備状態が合わないためこのアイテムを装備することが出来ません。交換しますか？」
        "装備することが出来ません",
    ];

    /// <summary>
    /// 外部プラグインの手が空くのを待つ上限。
    ///
    /// 待つこと自体は正しいので、全体の制限時間からは外してある。
    /// ただし**抑制を握ったまま無期限には待たない。**
    /// AutoRetainer の IPC が読めなくなると fail-closed で「処理中」を返し続け、
    /// 誰も抑制を解かないまま AutoRetainer と Artisan が止まったままになる。
    ///
    /// リテイナーの処理は数分かかることがある。それより長く、かつ有限にする。
    /// 超えたら失敗として終わらせる。失敗の経路は必ず抑制を解く。
    /// </summary>
    private static readonly TimeSpan SuppressWaitLimit = TimeSpan.FromMinutes(5);

    /// <summary>外部プラグインの手が空くのを待ち始めた区間の期限。</summary>
    private DateTime suppressWaitDeadlineUtc = DateTime.MinValue;

    /// <summary>
    /// このセッションを丸ごと打ち切る理由が出たか。
    ///
    /// 「この品はもう買わない」と「この移動はもう終わり」は別物。
    /// 混ぜると、高い品で通貨を使い切ったときに、まだ買える安い品まで見送る。
    /// </summary>
    private bool sessionExhausted;
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 連続して交換するときに、次を撃つまで待つ時間。
    ///
    /// サーバーからの反映は通貨と報酬で届く順序が前後する。
    /// 間を置かずに次を撃つと、前回の反映が終わる前に次の検証が始まり、
    /// 成功しているのに「想定外の変化」と誤判定する。
    /// </summary>
    private static readonly TimeSpan SettleBetweenExchanges = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// 会話メニューの選択肢が落ち着いたとみなすまでの時間。
    /// これを過ぎても一致しなければ、待っても一致しない。
    /// </summary>
    private static readonly TimeSpan MenuSettleWindow = TimeSpan.FromMilliseconds(2000);

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
    private readonly ArtisanIpc artisan = artisan;
    private readonly InclusionShopService inclusionShop = inclusionShop;
    private readonly CollectablesShopService collectablesShop = collectablesShop;
    private readonly CollectableDeliveryRunner collectableDelivery = collectableDelivery;

    /// <summary>
    /// 目的エリアへ直接飛べない場合の経路。null なら直接飛べる。
    /// テレポートの到着判定を、最終目的地ではなく玄関口で行うために持つ。
    /// </summary>
    private AethernetRoute? aethernetRoute;

    /// <summary>エーテライト網での転送を送った回数。</summary>
    private int aethernetTransferAttempts;

    /// <summary>
    /// この 1 回ぶん全体の締切。段階ごとの締切とは別に持つ。
    /// 安全な状態を待つ段階では見ない。
    /// </summary>
    private DateTime tripDeadlineUtc = DateTime.MinValue;

    /// <summary>
    /// 話しかけと会話メニューを往復した回数。
    /// 会話メニューを閉じられると話しかけへ戻るため、放っておくと止まらない。
    /// </summary>
    private int menuBounces;

    /// <summary>会話メニューで選択肢を選んだ回数。多段のメニューがあるため 1 回とは限らない。</summary>
    private int menuSelections;

    /// <summary>直前に選んだ選択肢の文字列。同じものを選び続けていないかを見る。</summary>
    private string lastMenuSelection = string.Empty;

    /// <summary>同じ選択肢を続けて選んだ回数。</summary>
    private int sameMenuSelections;

    /// <summary>直前に見た選択肢の並び。変化が無いことの判定に使う。</summary>
    private string lastMenuSignature = string.Empty;

    /// <summary>その並びを最初に見た時刻。</summary>
    private DateTime menuSignatureSinceUtc = DateTime.MinValue;

    /// <summary>
    /// ゲームに交換を拒まれた品。
    ///
    /// 習得済みの秘伝書のように、撃っても何も起きない品がある。
    /// 毎回試すと時間を無駄にするうえ、そのたびに 15 秒待つことになる。
    /// プラグインを読み込み直すまで覚えておく。
    /// </summary>
    private readonly HashSet<uint> rejectedRewards = [];

    /// <summary>納品を開始済みか。開始と終了の区別に使う。</summary>
    private bool deliveryStarted;

    /// <summary>納品を始められなかった理由。空なら問題なく走った。</summary>
    private string deliveryFailure = string.Empty;

    /// <summary>納品の結果。閉じる段階を挟むため、表示用に持ち越す。</summary>
    private string deliverySummary = string.Empty;

    /// <summary>納品画面を閉じようとした回数。どの手で閉じたかを記録するために数える。</summary>
    private int closeAttempts;

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

    /// <summary>待機中の状態を次に記録する時刻。</summary>
    private DateTime nextContextLogUtc = DateTime.MinValue;

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

    private ExchangeStep step = ExchangeStep.Idle;

    /// <summary>
    /// いまの手順。
    ///
    /// 代入箇所が多く、どこで何に移ったかを追うのが難しい。
    /// 遷移のたびに詳細ログへ残し、そのときの外部プラグインの状態も一緒に記録する。
    /// </summary>
    /// <summary>ゲームに拒まれた品か。監視側が並びを作るときに飛ばす。</summary>
    public bool IsRejected(uint rewardItemId) => this.rejectedRewards.Contains(rewardItemId);

    /// <summary>拒まれた品の数。UI に出す。</summary>
    public int RejectedCount => this.rejectedRewards.Count;

    /// <summary>拒まれた品の記録を消す。設定を変えたあとにやり直せるようにする。</summary>
    public void ClearRejected() => this.rejectedRewards.Clear();

    /// <summary>いま動いているか。UI でボタンを塞ぐために使う。</summary>
    public bool IsBusy => this.Step is not (ExchangeStep.Idle or ExchangeStep.Done or ExchangeStep.Error);

    public ExchangeStep Step
    {
        get => this.step;

        private set
        {
            if (this.step == value)
            {
                return;
            }

            var previous = this.step;
            this.step = value;

            if (Plugin.C.DetailedLogActive)
            {
                this.anomalyLog.Trace("Step", $"{previous} → {value} / {this.DescribeContext()}");
            }
        }
    }

    /// <summary>
    /// いまの外部状態をひとまとめにする。遷移ログに添えて、あとから原因を追えるようにする。
    /// 例外は握り潰す。ログのために本体を止めない。
    /// </summary>
    private string DescribeContext()
    {
        try
        {
            var parts = new List<string>();

            if (this.autoDuty.IsLoaded)
            {
                var stopped = this.autoDuty.TryIsStopped(out var s) ? s.ToString() : "?";
                var looping = this.autoDuty.TryIsLooping(out var l) ? l.ToString() : "?";
                var navigating = this.autoDuty.TryIsNavigating(out var n) ? n.ToString() : "?";
                parts.Add($"AD(停止={stopped} 周回={looping} 移動={navigating})");
            }

            if (this.autoRetainer.IsLoaded)
            {
                var suppressed = this.autoRetainer.TryGetSuppressed(out var sup) ? sup.ToString() : "?";
                parts.Add($"AR(処理中={this.autoRetainer.IsBusyFailClosed()} 抑制={suppressed} 本体抑制={this.autoRetainer.SuppressedByUs})");
            }

            if (this.artisan.IsLoaded)
            {
                parts.Add($"Artisan(処理中={this.artisan.IsBusyFailClosed()} 本体停止={this.artisan.StoppedByUs})");
            }

            parts.Add($"エリア={Svc.ClientState.TerritoryType}");
            parts.Add($"Duty={Player.IsInDuty}");
            parts.Add(SafetyGuard.IsSafeToStart(out var reason) ? "安全=OK" : $"安全=NG({reason})");

            return string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return $"状態の取得に失敗: {ex.Message}";
        }
    }

    public ExchangeFailure Failure { get; private set; }

    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>結果が未確定の発火。null でない間は新しい交換を受け付けない。</summary>
    public PurchaseAttempt? InFlight => Plugin.C.InFlight;

    /// <summary>いま進行中のセッションで交換した回数。</summary>
    public int SessionCompleted => this.session?.Completed ?? 0;

    /// <summary>いま進行中のプリセット。監視からの実行でなければ空。</summary>
    public Guid ActivePresetId => this.session?.PresetId ?? Guid.Empty;

    /// <summary>直前に完了した交換の回数。session は片付けられるため、終わったあとはこちらを見る。</summary>
    public int LastSessionCompleted { get; private set; }

    /// <summary>直前の交換が終わった時刻。</summary>
    public DateTime LastFinishedAt { get; private set; }

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

    /// <summary>
    /// 直前に記録した再開先。UI から手動で再開するときに使う。
    /// </summary>
    public uint LastResumeTerritoryId => this.returnContext?.AutoDutyTerritoryId ?? this.observedDutyTerritoryId;

    /// <summary>手動で AutoDuty を再開する。</summary>
    public bool TryResumeAutoDutyManually(out string reason)
    {
        // 手で起こしたなら、止めたときの旗も全部下ろす。
        // 維持だけ戻すと、周回は回るのに交換が「停止中です」で弾かれ続ける。
        Plugin.P.ResumeAfterStop();

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
        this.artisan.Release();
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
        this.artisan.Release();

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
            this.CloseOwned("ShopExchangeItemDialog", useCloseFirst: false);

            // 4. ショップ本体
            this.CloseOwned("ShopExchangeCurrency", useCloseFirst: true);
        this.CloseOwned("InclusionShop", useCloseFirst: true);
        this.CloseOwned("CollectablesShop", useCloseFirst: true);

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
        this.artisan.Release();
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
    /// <summary>
    /// 品と品のあいだで、答え終えていない確認を片づける。
    ///
    /// 次の品を撃つ前に閉じておかないと、発火直前の事前条件（P-5）が
    /// 阻害アドオンとして弾き、以後の品が 1 つも試されなくなる。
    ///
    /// 自分が開かせたものだけを閉じる。
    /// ほかのプラグインが出したものに手を出すと、相手の処理を壊す。
    /// </summary>
    private void CloseLeftoverDialogs()
    {
        this.CloseOwned("SelectYesno", useCloseFirst: false);
        this.CloseOwned("ShopExchangeCurrencyDialog", useCloseFirst: false);
        this.CloseOwned("ShopExchangeItemDialog", useCloseFirst: false);
    }

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

    /// <summary>
    /// 収集品の納品窓口へ向かい、着いたらまとめて納品する。
    ///
    /// 移動の仕組みは交換と同じものを使う。安全な状態になるまで待ち、
    /// 外部プラグインを抑制し、AutoDuty を止めてから動き出す点も同じ。
    /// </summary>
    public bool RequestDeliveryTrip(CollectablesNpc npc, out string reason)
    {
        var definition = ExchangeDefinition.ForCollectableDelivery(npc);

        // 納品は交換ではないため、この値は使われない。Armed まで進まないため。
        // 終わりは納品側が「納品できる品が無くなったか、スクリップが全部上限か」で決める。
        var session = new ExchangeSession { Mode = ExchangeMode.MaxExchange };

        return this.RequestWithTravel(definition, session, out reason);
    }

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

        var needsTeleport = Svc.ClientState.TerritoryType != definition.TerritoryId;
        AethernetRoute? plannedRoute = null;

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

            // **行けるかどうかは、判断できるときにだけ判断する。**
            //
            // アクセス済みエーテライトの一覧は、コンテンツの中では空になる。
            // 交換は周回の途中で予約されるため、この判定はまさにその最中に走る。
            // 空を「アクセスしていない」と読んで、行ける街へ行けないと言っていた。
            //
            // ここで判断しなくても、実際にテレポートする段（TickTeleport）で
            // もう一度引き直す。そのときは街にいるので一覧が読める。
            //
            // エーテライトが無いエリアがある（例: ウルダハ：ザル回廊）。
            // その場合は同じ網の親エーテライトへ飛び、そこから網で移動する。
            if (this.aetheryte.IsListReady() &&
                !this.aetheryte.TryFindTarget(definition.TerritoryId, out _))
            {
                if (!this.aetheryte.TryFindAethernetRoute(definition.TerritoryId, out plannedRoute) || plannedRoute is null)
                {
                    this.pendingRequest = null;
                    this.Fail(
                        ExchangeFailure.AetheryteNotAttuned,
                        $"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} へ行けません。" +
                        "エーテライトが無く、エーテライト網の玄関口にもアクセスしていません");
                    reason = this.StatusDetail;
                    return false;
                }

                this.anomalyLog.Info(
                    "Travel",
                    $"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} へは " +
                    $"{plannedRoute.Hub.Name} からエーテライト網で向かいます");
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

        // 前の移動の打ち切り理由を持ち越さない。
        // 残っていると、次の移動で交換リストの 2 件目以降が 1 度も試されない。
        this.sessionExhausted = false;
        this.aethernetTried = false;
        this.reapproachAttempts = 0;
        this.destinationUpdates = 0;
        this.nextArmedAllowedUtc = DateTime.MinValue;
        this.stopAttempts = 0;
        this.sawAutoDutyRunning = false;
        this.aethernetRoute = plannedRoute;
        this.deliveryStarted = false;
        this.aethernetTransferAttempts = 0;
        this.menuBounces = 0;
        this.menuSelections = 0;
        this.sameMenuSelections = 0;
        this.lastMenuSelection = string.Empty;
        this.lastMenuSignature = string.Empty;
        this.menuSignatureSinceUtc = DateTime.MinValue;

        // 納品は品数ぶん繰り返すため長くかかる。移動と会話を含めても
        // 15 分あれば足りる。これを超えるのは何かが噛み合っていないとき。
        this.tripDeadlineUtc = DateTime.UtcNow.AddMinutes(15);
        this.deliveryFailure = string.Empty;
        this.deliverySummary = string.Empty;
        this.closeAttempts = 0;
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
        // 手順が進まないまま止まっている場合、遷移ログだけでは何も残らない。
        // 待っている間の外部状態を定期的に残して、あとから追えるようにする。
        if (Plugin.C.DetailedLogActive &&
            this.Step is not (ExchangeStep.Idle or ExchangeStep.Done) &&
            DateTime.UtcNow >= this.nextContextLogUtc)
        {
            this.nextContextLogUtc = DateTime.UtcNow.AddSeconds(5);
            this.anomalyLog.Trace("State", $"{this.Step} 継続中: {this.StatusDetail} / {this.DescribeContext()}");
        }

        // 全体の制限時間。
        //
        // 各段階はそれぞれ締切を持つが、段階を移るたびに引き直される。
        // そのため 2 つの段階を往復し続けると永久に終わらない。
        // 実際、話しかける → 会話メニュー → 閉じる → 話しかける、の往復で
        // 操作不能になった。段階ごとの締切とは別に、全体の上限が要る。
        //
        // 安全な状態を待っている間は数えない。コンテンツが終わるのを待つのは正しい動作で、
        // 時間切れで打ち切っても意味がない。
        //
        // **外部プラグインの処理待ちも同じ。**
        // SuppressExternal は AutoRetainer や Artisan が手を離すのを待つ段で、
        // 「時間で打ち切らない」ことを意図して作ってある（TickSuppressExternal）。
        // それを全体の制限時間が横から殺していた。
        //
        // 安全待ちに時間を使ったあとここへ入ると、待つのが正しい状態のまま
        // 「制限時間を超えたため中止しました（SuppressExternal で停止）」になり、
        // しかも失敗として数えられてプリセットが自動で無効化されていた。
        if (this.Step is not (ExchangeStep.Idle or ExchangeStep.Done or ExchangeStep.Error
                or ExchangeStep.WaitingSafeWindow or ExchangeStep.SuppressExternal) &&
            this.tripDeadlineUtc != DateTime.MinValue &&
            DateTime.UtcNow > this.tripDeadlineUtc)
        {
            this.anomalyLog.Error("Executor", $"制限時間を超えました（{this.Step} で停止）");
            this.navigation.Stop();
            this.Fail(ExchangeFailure.Aborted, $"制限時間を超えたため中止しました（{this.Step} で停止）");
            return;
        }

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

            case ExchangeStep.DeliverCollectables:
                this.TickDeliverCollectables();
                return;

            case ExchangeStep.CloseDeliveryWindow:
                this.TickCloseDeliveryWindow();
                return;

            case ExchangeStep.ResumeAutoDuty:
                this.TickResumeAutoDuty();
                break;

            case ExchangeStep.Teleport:
                this.TickTeleport();
                break;

            case ExchangeStep.AethernetHop:
                this.TickAethernetHop();
                break;

            case ExchangeStep.AethernetTransfer:
                this.TickAethernetTransfer();
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

            case ExchangeStep.SelectInclusionCategory:
                this.TickSelectInclusionCategory();
                break;

            case ExchangeStep.InclusionConfirm:
                this.TickInclusionConfirm();
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
        this.suppressWaitDeadlineUtc = DateTime.UtcNow.Add(SuppressWaitLimit);
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
        if (!this.autoDuty.IsLoaded)
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
        // **抑制を握ったまま無期限に待たない。**
        //
        // この段は「時間で打ち切らない」ことを意図して作ってあり、全体の制限時間からも外した。
        // だが外部プラグインの状態は fail-closed で読む（読めないときは「処理中」とみなす）。
        // 相手の IPC が壊れると永久に「処理中」が返り、抑制を立てたまま誰も解かなくなる。
        //
        // 待つのは正しいが、有限にする。終わらせる経路は必ず抑制を解く。
        if (this.suppressWaitDeadlineUtc != DateTime.MinValue &&
            DateTime.UtcNow > this.suppressWaitDeadlineUtc)
        {
            this.Fail(
                ExchangeFailure.Aborted,
                $"外部プラグインの処理が {SuppressWaitLimit.TotalMinutes:F0} 分待っても終わりませんでした（{this.StatusDetail}）");
            return;
        }

        // 待っている間に次のコンテンツへ入ってしまうことがある。
        // Duty 中に AutoDuty を止めるのは最も避けたい事故なので、必ず戻る。
        if (this.ReturnToWaitIfUnsafe())
        {
            return;
        }

        // Artisan を先に止める。
        // 製作の最中は SafetyGuard が弾くのでここへは来ないが、製作の合間は素通りする。
        // その状態で移動を始めると、Artisan が次の製作を始めようとして操作を取り合う。
        if (!this.StopArtisanForExchange())
        {
            return;
        }

        if (!this.autoRetainer.IsLoaded || !Plugin.C.SuppressAutoRetainer)
        {
            this.Step = ExchangeStep.StopAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(10);
            return;
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

    /// <summary>
    /// 交換の間だけ Artisan を止める。進んでよければ true を返す。
    ///
    /// Artisan は停止時に動作中のモード（耐久モード / 製作リスト）を自分で記録し、
    /// 解除時にそのモードだけを戻す。何も動いていなければ何も起きない。
    /// </summary>
    private bool StopArtisanForExchange()
    {
        if (!this.artisan.IsLoaded || !Plugin.C.StopArtisan)
        {
            return true;
        }

        if (!this.artisan.StoppedByUs)
        {
            // **動いていないなら止める必要が無い。**
            //
            // 製作していない人の交換まで、Artisan の都合で止めていた。
            // 止めるのは操作を取り合わないためなので、相手が何もしていないなら用が無い。
            if (!this.artisan.IsRunning())
            {
                return true;
            }

            if (!this.artisan.Stop())
            {
                // **止められなかったことを、交換の失敗にしない。**
                //
                // ここで Fail していたため、2 回続くとプリセットが自動で無効化された。
                // 実際の報告では、製作と無関係な周回の交換がこれで丸ごと止まっていた。
                //
                // 相手が手を離すのを待つ。待ちには上限があり
                // （SuppressWaitLimit）、超えれば中止として終わる。
                // 中止は設定の誤りではないので、プリセットは無効化されない。
                this.StatusDetail = "Artisan へ停止を依頼できません。手が空くのを待っています";

                if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(60))
                {
                    this.lastWaitLogUtc = DateTime.UtcNow;
                    var detail = string.IsNullOrEmpty(this.artisan.LastError)
                        ? string.Empty
                        : $"（{this.artisan.LastError}）";

                    this.anomalyLog.Warn(
                        "Suppress",
                        $"Artisan へ停止を依頼できませんでした{detail}。手が空くのを待っています");
                }

                return false;
            }

            this.anomalyLog.Info("Suppress", "交換の間、Artisan の製作を止めました");

            // 止めた直後は製作画面から抜ける処理が残っている。次の呼び出しで確認する。
            return false;
        }

        // 製作画面から抜け終わるまで待つ。抜ける前に移動すると操作が噛み合わない。
        if (this.artisan.IsBusyFailClosed())
        {
            this.StatusDetail = "Artisan の製作が止まるのを待っています";

            if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(60))
            {
                this.lastWaitLogUtc = DateTime.UtcNow;
                this.anomalyLog.Info("Wait", "Artisan の製作が止まるのを待っています");
            }

            return false;
        }

        return true;
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

            // **再開先は「経路があるところ」を選ぶ。現在地に落とさない。**
            //
            // 自分で見た Duty のエリアを優先するが、それは待機中に
            // コンテンツの中にいたときしか記録されない。
            // 交換はたいてい GC 納品のあと、街から始まるため空のままになる。
            //
            // そこで現在地へ落ちていた。街には AutoDuty の経路が無いので、
            // 「ソリューション・ナイン に経路が無いため再開できません」と出て
            // 毎回失敗していた（周回の維持が別途拾うので実害は無かったが、
            // 記録にエラーが残り、原因を探す手間になる）。
            //
            // 周回していたエリアは AutoDutyKeeper が覚えている。そちらを次に見る。
            uint resumeTerritory = 0;

            foreach (var candidate in new[]
                     {
                         this.observedDutyTerritoryId,
                         Plugin.C.LastDutyTerritoryId,
                         Svc.ClientState.TerritoryType,
                     })
            {
                if (candidate == 0)
                {
                    continue;
                }

                if (this.autoDuty.TryContentHasPath(candidate, out var usable) && usable)
                {
                    resumeTerritory = candidate;
                    break;
                }
            }

            // どれも使えないなら、覚えている値をそのまま持っておく。
            // 再開の段で理由を出す。
            if (resumeTerritory == 0)
            {
                resumeTerritory = this.observedDutyTerritoryId != 0
                    ? this.observedDutyTerritoryId
                    : Plugin.C.LastDutyTerritoryId;
            }

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
    /// <summary>
    /// 収集品を納品する。
    ///
    /// 実際の納品は <see cref="CollectableDeliveryRunner"/> が行う。
    /// ここは、窓口に着いて画面が開いたあとの引き渡しと、終わったかの見張りだけを持つ。
    ///
    /// 納品は品数ぶん繰り返すため時間がかかる。締切は移動より長く取ってある。
    /// </summary>
    private void TickDeliverCollectables()
    {
        // 走っている間は見張るだけ。
        if (this.collectableDelivery.IsRunning)
        {
            this.StatusDetail = this.collectableDelivery.StatusDetail;

            if (DateTime.UtcNow > this.stepDeadlineUtc)
            {
                this.anomalyLog.Error("Collectables", "納品が終わりませんでした");
                this.Failure = ExchangeFailure.Aborted;
                this.Step = ExchangeStep.ResumeAutoDuty;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            }

            return;
        }

        // まだ始めていなければ始める。
        if (!this.deliveryStarted)
        {
            if (!this.collectablesShop.IsOpen())
            {
                if (DateTime.UtcNow > this.stepDeadlineUtc)
                {
                    this.Fail(ExchangeFailure.ShopNotOpen, "納品画面が開きませんでした");
                }

                return;
            }

            if (!this.collectableDelivery.Start(out var reason))
            {
                // 納品できる品が無い場合もここへ来る。異常ではないので止めずに終える。
                this.anomalyLog.Info("Collectables", $"納品しませんでした: {reason}");
                this.deliveryFailure = reason;
                this.deliveryStarted = true;
                return;
            }

            this.deliveryStarted = true;
            this.anomalyLog.Info("Collectables", "納品を始めます");
            return;
        }

        // 走り終わった。結果は納品側が記録している。
        var summary = string.IsNullOrEmpty(this.deliveryFailure)
            ? $"納品を終えました（{this.collectableDelivery.Delivered} 個）"
            : this.deliveryFailure;

        this.anomalyLog.Info("Collectables", summary);
        this.deliverySummary = summary;
        this.closeAttempts = 0;
        this.Step = ExchangeStep.CloseDeliveryWindow;
        this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(10);
        this.StatusDetail = summary;
    }

    /// <summary>
    /// 納品画面を閉じる。
    ///
    /// 閉じ方の実測データが無いため、確実な手を 1 つ選べない。
    /// そこで順に試し、**実際に閉じたことを確認してから**次へ進む。
    /// どの手が効いたかを記録に残すので、分かった時点で 1 つに絞れる。
    /// </summary>
    private void TickCloseDeliveryWindow()
    {
        if (!this.collectablesShop.IsOpen())
        {
            if (this.closeAttempts > 0)
            {
                this.anomalyLog.Info("Cleanup", $"納品画面を閉じました（{this.closeAttempts} 手目）");
            }

            this.Step = ExchangeStep.ResumeAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            return;
        }

        // 自分が開いたものでなければ触らない。
        if (!this.ownership.TryGetOwned("CollectablesShop", out var addon))
        {
            this.anomalyLog.Info("Cleanup", "納品画面は自分が開いたものではないため閉じません");
            this.Step = ExchangeStep.ResumeAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            return;
        }

        // 押した結果が反映されるまで間を置く。連打しても閉じない。
        if (!EzThrottler.Throttle("AutoCollector.CloseDelivery", 800))
        {
            return;
        }

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.anomalyLog.Error("Cleanup", "納品画面を閉じられませんでした。手動で閉じてください");
            this.Step = ExchangeStep.ResumeAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            return;
        }

        this.closeAttempts++;

        try
        {
            switch (this.closeAttempts)
            {
                case 1:
                    addon->Close(true);
                    break;

                default:
                    Callback.Fire(addon, true, -1);
                    break;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Cleanup", $"納品画面を閉じる操作に失敗しました: {ex.Message}");
        }
    }

    private void TickResumeAutoDuty()
    {
        var context = this.returnContext;

        if (context is null || !context.WasAutoDutyRunning || !this.autoDuty.IsLoaded)
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
        this.CloseOwned("InclusionShop", useCloseFirst: true);

        // AutoDuty を動かす前に抑制を解く。
        // AutoDuty はループ間処理で AutoRetainer を呼ぶため、抑制したまま再開すると
        // リテイナー処理が動かないまま次の周回に入る。
        this.autoRetainer.Release();
        this.artisan.Release();

        if (!this.autoDuty.TryContentHasPath(context.AutoDutyTerritoryId, out var hasPath) || !hasPath)
        {
            // 周回の維持がこのあと拾うので、ここで止まっても周回は続く。
            // 止まったと誤解させないよう、警告に留める。
            this.anomalyLog.Warn(
                "AutoDuty",
                $"{NpcLocationService.GetTerritoryName(context.AutoDutyTerritoryId)} に AutoDuty の経路が無いため、ここからは再開できません。" +
                "周回の維持が引き継ぎます（引き継がれない場合は状況タブの「AutoDuty を再開」から）");
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
        this.CloseOwned("InclusionShop", useCloseFirst: true);

        this.autoRetainer.Release();
        this.artisan.Release();
        this.returnContext = null;

        // session を片付ける前に控える。片付けたあとは交換回数を出す手段が無くなる。
        this.LastSessionCompleted = this.session?.Completed ?? 0;
        this.LastFinishedAt = DateTime.Now;
        this.session = null;
        this.travelTarget = null;
        this.ownership.Clear();
        this.Step = ExchangeStep.Done;

        if (this.Failure == ExchangeFailure.None)
        {
            // **1 回も交換していないなら「完了しました」で潰さない。**
            //
            // 撃てずに帰ってきた場合、その理由が StatusDetail に入っている。
            // それを上書きすると、往復しているのに画面は成功に見える。
            var didNothing = this.LastSessionCompleted == 0 &&
                             string.IsNullOrEmpty(this.deliverySummary) &&
                             !string.IsNullOrEmpty(this.StatusDetail);

            if (didNothing)
            {
                this.StatusDetail = $"交換せずに戻りました: {this.StatusDetail}";
            }
            else
            {
                // 納品だった場合は、何個納品したかを残す。「完了しました」だけでは分からない。
                this.StatusDetail = string.IsNullOrEmpty(this.deliverySummary)
                    ? "完了しました"
                    : this.deliverySummary;
            }
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
    /// <summary>
    /// エーテライト網で別のエリアへ移る。
    ///
    /// 目的エリアに着いたかどうかだけを見る。
    /// 送った直後はまだ玄関口にいるため、エリアが変わるまで待つ。
    /// </summary>
    private void TickAethernetTransfer()
    {
        var target = this.travelTarget;
        var route = this.aethernetRoute;

        if (target is null || route is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        // 着いた。ここから先は通常の移動と同じ。
        if (Svc.ClientState.TerritoryType == target.TerritoryId)
        {
            if (!GenericHelpers.IsScreenReady() || !Player.Available || !Player.Interactable)
            {
                return;
            }

            this.anomalyLog.Info("Travel", $"エーテライト網で {target.NpcName} のいるエリアへ移りました");

            if (!this.BeginTravelToNpc(target, out var navFailure))
            {
                this.Fail(ExchangeFailure.NavigationFailed, navFailure);
            }

            return;
        }

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.Fail(
                ExchangeFailure.TeleportFailed,
                $"エーテライト網で「{route.ShardName}」へ移動できませんでした");
            return;
        }

        // 送れる状態になるまで待つ。
        if (!Player.Available || Player.IsCasting || GenericHelpers.IsOccupied() || !GenericHelpers.IsScreenReady() ||
            Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        if (this.lifestream.TryIsBusy(out var busy) && busy)
        {
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.AethernetTransfer", 3000))
        {
            return;
        }

        if (this.aethernetTransferAttempts >= 3)
        {
            this.Fail(
                ExchangeFailure.TeleportFailed,
                $"エーテライト網で「{route.ShardName}」へ移動できませんでした（3 回試行）");
            return;
        }

        this.aethernetTransferAttempts++;

        if (!this.lifestream.TryAethernetTeleportById(route.ShardAetheryteRowId, out var accepted) || !accepted)
        {
            this.anomalyLog.Warn(
                "Travel",
                $"エーテライト網への転送を受け付けてもらえませんでした（{this.aethernetTransferAttempts} 回目）");
        }
    }

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
        //
        // 直接飛べないエリアでは、いったん玄関口のエリアへ着く。
        // そこからエーテライト網で目的エリアへ移るため、到着の基準が変わる。
        var arrivalTerritory = this.aethernetRoute?.Hub.TerritoryId ?? target.TerritoryId;

        if (Svc.ClientState.TerritoryType == arrivalTerritory)
        {
            if (!GenericHelpers.IsScreenReady() || !Player.Available || !Player.Interactable)
            {
                return;
            }

            // 玄関口に着いただけなら、まだ目的エリアではない。網で移る。
            if (this.aethernetRoute is { } route && Svc.ClientState.TerritoryType != target.TerritoryId)
            {
                this.Step = ExchangeStep.AethernetTransfer;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(60);
                this.StatusDetail = $"エーテライト網で「{route.ShardName}」へ移動しています";
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

        // 直接飛べないエリアでは玄関口へ飛ぶ。目的エリアへは網で移る。
        var destination = this.aethernetRoute?.Hub;

        if (destination is null && !this.aetheryte.TryFindTarget(target.TerritoryId, out destination))
        {
            // **ここで網の経路を引き直す。**
            //
            // 予約した時点ではコンテンツの中にいて、アクセス済みエーテライトの
            // 一覧が空だった可能性がある。そのときは判断を先送りしてある。
            // いまは街にいるので読める。
            if (this.aetheryte.TryFindAethernetRoute(target.TerritoryId, out var lateRoute) && lateRoute is not null)
            {
                this.aethernetRoute = lateRoute;
                destination = lateRoute.Hub;

                this.anomalyLog.Info(
                    "Travel",
                    $"{NpcLocationService.GetTerritoryName(target.TerritoryId)} へは " +
                    $"{lateRoute.Hub.Name} からエーテライト網で向かいます");
            }
        }

        if (destination is null)
        {
            this.Fail(
                ExchangeFailure.AetheryteNotAttuned,
                $"{NpcLocationService.GetTerritoryName(target.TerritoryId)} のエーテライトにアクセスしていません");
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
            // **届く距離まで来たら、経路が終わるのを待たずに話しかける。**
            //
            // vnavmesh は NPC の足元まで行こうとする。カウンターやテーブルの向こうに
            // 立っている NPC だと、そこへは行けないので近くを走り続ける。
            //
            // 到着の判定は「vnavmesh が走り終わったか」を条件にしていたため、
            // 走り続けているあいだは Arrived も ShortOfTarget も返らない。
            // 実際、ジルコン（ソリューション・ナイン）で、話しかけられる距離まで
            // 近づいているのに、あいだのテーブルへ向かって走り続けた。
            //
            // 話しかけられるかどうかは距離だけで決まる。
            // 届いているなら、経路の都合は関係ない。
            if (InteractionService.IsWithinInteractRange(liveNpc))
            {
                this.navigation.Stop();
                this.Step = ExchangeStep.Interact;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
                this.StatusDetail = $"{target.NpcName} に話しかけています";
                return;
            }

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

            case MoveStatus.ShortOfTarget:
                // 届いていなくても、話しかけられる距離なら用は足りる。
                // 足りなければ Interact 側が近づき直しを試み、それでも駄目なら失敗する。
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

        // 納品のための移動なら、収集品の画面が開いた時点で納品へ渡す。
        // 交換用の判定より先に見る。納品画面はショップ扱いではないため、
        // 後ろに置くと会話の処理へ落ちてしまう。
        if (target.IsCollectableDelivery)
        {
            if (this.collectablesShop.IsOpen())
            {
                this.Step = ExchangeStep.DeliverCollectables;
                this.stepDeadlineUtc = DateTime.UtcNow.AddMinutes(10);
                this.StatusDetail = "収集品を納品しています";
                return;
            }
        }
        else if (target.UsesInclusionShop)
        {
            if (this.inclusionShop.IsOpen())
            {
                this.Step = ExchangeStep.SelectInclusionCategory;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
                this.StatusDetail = "交換の種類を選んでいます";
                return;
            }
        }
        else if (this.shopService.IsShopOpen())
        {
            // ショップが開いたら事前条件の評価へ進む。
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

        // 納品のための移動なら、交換の発火段階（Armed）へ入れてはいけない。
        // 納品の定義は交換に関わる値をすべて空にしてあるため、
        // そこへ入ると中身の無い交換を撃とうとする。
        if (target.IsCollectableDelivery)
        {
            if (this.collectablesShop.IsOpen())
            {
                this.Step = ExchangeStep.DeliverCollectables;
                this.stepDeadlineUtc = DateTime.UtcNow.AddMinutes(10);
                this.StatusDetail = "収集品を納品しています";
                return;
            }
        }
        else if (this.shopService.IsShopOpen())
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
            //
            // ただし無制限に戻ってはいけない。人が手で閉じた場合や、
            // 選ぶべき選択肢が無い場合、話しかけ直しても同じ画面に戻るだけで
            // 永久に往復する。締切も毎回引き直されるため自力では抜けられない。
            // 人がキャンセルを押した場合もここへ来る。
            // 押し返すように話しかけ直すと、操作を奪い合って抜けられなくなる。
            // 1 度だけやり直し、それでも駄目なら諦める。
            this.menuBounces++;

            if (this.menuBounces > 1)
            {
                this.Fail(
                    ExchangeFailure.MenuResolutionFailed,
                    $"会話を抜けられませんでした（{this.menuBounces} 回やり直し）。" +
                    $"選択肢: {string.Join(" / ", this.menu.ListEntries())}");
                return;
            }

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
                // 選んでも画面が進まないことがある。
                //
                // 道具強化の窓口のように、選択肢の先がさらに会話メニューだったり、
                // 条件を満たしていなくて元の一覧へ戻される場合、
                // 同じ選択肢を延々と選び直すことになる。メニューは開いたままなので
                // 「閉じたらやり直す」側の上限には掛からない。
                //
                // 実測では 2 秒おきに同じ選択肢を撃ち続けていた。
                if (hint == this.lastMenuSelection)
                {
                    this.sameMenuSelections++;
                }
                else
                {
                    this.lastMenuSelection = hint;
                    this.sameMenuSelections = 1;
                }

                this.menuSelections++;

                if (this.sameMenuSelections > 3 || this.menuSelections > 8)
                {
                    this.Fail(
                        ExchangeFailure.MenuResolutionFailed,
                        $"「{hint}」を選んでも先へ進みませんでした" +
                        $"（同じ選択 {this.sameMenuSelections} 回 / 合計 {this.menuSelections} 回）。" +
                        $"選択肢: {string.Join(" / ", this.menu.ListEntries())}");
                    return;
                }

                this.StatusDetail = $"「{hint}」を選びました（{this.menuSelections} 回目）";
                return;
            }

            if (failure == MenuSelectFailure.Ambiguous)
            {
                ambiguous = true;
            }
        }

        // 一致しないまま待ち続けない。
        //
        // メニューが切り替わる途中なら選択肢は変わる。変わらないなら、
        // 待っても一致しない。実測では、選択肢に候補が 1 つも無い状態で
        // 30 秒待つ間に人がキャンセルを押し、そのたびに話しかけ直して
        // 抜けられなくなっていた。
        //
        // 選択肢が変わらないまま一定時間が過ぎたら、その場で失敗させる。
        var signature = string.Join("", this.menu.ListEntries());

        if (signature != this.lastMenuSignature)
        {
            this.lastMenuSignature = signature;
            this.menuSignatureSinceUtc = DateTime.UtcNow;
            return;
        }

        var settled = DateTime.UtcNow - this.menuSignatureSinceUtc > MenuSettleWindow;

        if (!settled && DateTime.UtcNow <= this.stepDeadlineUtc)
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
        // 納品の定義がここへ来ることは無いはずだが、来たら撃たずに止める。
        // 交換に関わる値をすべて空にしてあるため、進むと中身の無い交換を撃つことになる。
        if (this.travelTarget?.IsCollectableDelivery == true ||
            this.pendingRequest?.IsCollectableDelivery == true)
        {
            this.anomalyLog.Error("Exchange", "納品の行き先で交換を撃とうとしました。中止します");
            this.pendingRequest = null;
            this.Fail(ExchangeFailure.Aborted, "納品の行き先では交換できません");
            return;
        }

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

        // アイテム交換画面は読み取りも発火も別経路になる。
        if (definition.UsesInclusionShop)
        {
            this.FireInclusionExchange(definition);
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
        // **枠が足りないことは失敗にしない。**
        //
        // 鞄が埋まっているのは設定の誤りではない。硬い失敗にすると
        // 連続失敗に数えられ、プリセットが自動で無効になっていた。
        // 枠の判定は共通の歯止め（ExchangeLimits）が行い、
        // 撃たずに次の品へ進む穏当な終わり方にする。
        //
        // ここでは読めるかどうかだけを見る。
        var keepFree = this.KeepFreeSlots();
        if (!this.currencyService.TryGetEmptyBagSlots(out var freeSlots))
        {
            this.Fail(ExchangeFailure.NoBagSpace, "所持枠の空きを取得できませんでした");
            return;
        }

        // P-17: 報酬は装備・アーマリーも数える。装備品はアーマリーへ入るため。
        if (!this.currencyService.TryGetCount(definition.RewardItemId, out var rewardBefore, includeEquipped: true, includeArmory: true))
        {
            this.Fail(ExchangeFailure.RewardCountUnreadable, "報酬アイテムの所持数を取得できませんでした");
            return;
        }

        var rewardName = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(definition.RewardItemId)?.Name.ExtractText() ?? string.Empty;

        // P-18: 利用者が決めた歯止め。**撃つ前に必ず見る。**
        //
        // ここに関門が無かった。アイテム交換画面の経路にはあるのに、
        // こちらの経路には無く、所持の上限 10 を指定しても
        // 通貨が尽きるまで買い続けた。取り返しがつかない不具合だった。
        //
        // 判断は ExchangeLimits に集めてある。窓口ごとに書かない。
        // 書き分けていたから片方だけ育ち、差が開いた。
        //
        // この窓口は 1 回の発火で 1 回ぶんしか撃てないため maxBatch は 1。
        var allowance = this.EvaluateAllowance(
            definition, rewardBefore, currencyBefore, (int)freeSlots, keepFree, maxBatch: 1);

        if (!allowance.Allowed)
        {
            this.anomalyLog.Info("Exchange", $"{rewardName} は交換しません: {allowance.Reason}");

            this.sessionExhausted = allowance.EndsSession;

            if (this.TryAdvanceToNextTarget(allowance.Reason))
            {
                return;
            }

            this.Step = ExchangeStep.ResumeAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            this.StatusDetail = allowance.Reason;
            return;
        }

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

    /// <summary>
    /// アイテム交換画面で 1 回交換する。
    ///
    /// 事前条件の考え方は ShopExchangeCurrency と同じ。
    /// 画面から読んだ内容とゲームデータが完全に一致したときだけ撃つ。
    /// 一致しないものは、直せる見込みがなくても撃たずに止める。
    /// </summary>
    private unsafe void FireInclusionExchange(ExchangeDefinition definition)
    {
        foreach (var name in BlockingAddons)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var blocking) && GenericHelpers.IsAddonReady(blocking))
            {
                this.Fail(ExchangeFailure.BlockingAddonPresent, $"{name} が開いています。閉じてから実行してください");
                return;
            }
        }

        if (!this.inclusionShop.TryGetAddon(out var addon))
        {
            this.Fail(ExchangeFailure.ShopNotOpen, "アイテム交換画面が開いていません");
            return;
        }

        if (!this.inclusionShop.TryReadEntries(addon, out var entries, out var currencyOnScreen, out var readFailure))
        {
            this.Fail(ExchangeFailure.ShopNotOpen, readFailure);
            return;
        }

        // index の重複は配置ずれの証拠。撃たない。
        if (entries.Select(x => x.Index).Distinct().Count() != entries.Count)
        {
            this.Fail(ExchangeFailure.IndexDuplicated, "画面から読んだ index に重複があります。配置がずれている可能性があります");
            return;
        }

        var match = this.inclusionShop.Match(definition, entries);
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

        if (!this.currencyService.TryGetCount(definition.CurrencyItemId, out var currencyBefore))
        {
            this.Fail(ExchangeFailure.CurrencyMismatch, "所持通貨を取得できませんでした");
            return;
        }

        // 画面の所持数とインベントリが一致すること。通貨違いに対する唯一の実効的な検証。
        if (currencyBefore != (int)currencyOnScreen)
        {
            this.Fail(
                ExchangeFailure.CurrencyMismatch,
                $"画面の通貨 {currencyOnScreen} とインベントリの {currencyBefore} が一致しません。別通貨の画面の可能性があります");
            return;
        }

        if (currencyBefore < definition.CurrencyCost)
        {
            this.Fail(ExchangeFailure.InsufficientCurrency, $"通貨が足りません（所持 {currencyBefore} / 必要 {definition.CurrencyCost}）");
            return;
        }

        // **枠が足りないことは失敗にしない。**
        //
        // 鞄が埋まっているのは設定の誤りではない。硬い失敗にすると
        // 連続失敗に数えられ、プリセットが自動で無効になっていた。
        // 枠の判定は共通の歯止め（ExchangeLimits）が行い、
        // 撃たずに次の品へ進む穏当な終わり方にする。
        //
        // ここでは読めるかどうかだけを見る。
        var keepFree = this.KeepFreeSlots();
        if (!this.currencyService.TryGetEmptyBagSlots(out var freeSlots))
        {
            this.Fail(ExchangeFailure.NoBagSpace, "所持枠の空きを取得できませんでした");
            return;
        }

        if (!this.currencyService.TryGetCount(definition.RewardItemId, out var rewardBefore, includeEquipped: true, includeArmory: true))
        {
            this.Fail(ExchangeFailure.RewardCountUnreadable, "報酬アイテムの所持数を取得できませんでした");
            return;
        }

        var rewardName = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(definition.RewardItemId)?.Name.ExtractText() ?? string.Empty;

        var amount = this.DecideBatchAmount(
            definition, entry, currencyBefore, rewardBefore, (int)freeSlots, keepFree,
            out var batchBlock, out var batchEndsSession);

        // 利用者が決めた歯止めに触れた。この品は撃たずに次へ。
        if (amount <= 0)
        {
            this.anomalyLog.Info("Exchange", $"{rewardName} は交換しません: {batchBlock}（所持 {rewardBefore}）");

            this.sessionExhausted = batchEndsSession;

            if (this.TryAdvanceToNextTarget(batchBlock))
            {
                return;
            }

            this.Step = ExchangeStep.ResumeAutoDuty;
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            this.StatusDetail = batchBlock;
            return;
        }

        // ここから先は不可逆。記録を先に立ててから撃つ。
        //
        // まとめ買いでは、費やす通貨も受け取る数も個数ぶん増える。
        // 検証はこの値と突き合わせるので、掛けた後の値を記録する。
        Plugin.C.InFlight = new PurchaseAttempt
        {
            ShopId = definition.ShopId,
            RewardItemId = definition.RewardItemId,
            RewardName = rewardName,
            CurrencyItemId = definition.CurrencyItemId,
            CallbackIndex = callbackIndex,
            CurrencyCost = definition.CurrencyCost * (uint)amount,
            RewardQuantity = definition.RewardQuantity * (uint)amount,
            RewardBefore = rewardBefore,
            CurrencyBefore = currencyBefore,
            Amount = amount,
            FiredAtUtc = DateTime.UtcNow,
        };
        EzConfig.Save();

        this.anomalyLog.Info(
            "Exchange",
            $"交換を実行します: {rewardName} × {definition.RewardQuantity * (uint)amount}" +
            $"（{amount} 回ぶん / コスト {definition.CurrencyCost * (uint)amount} / index {callbackIndex} / アイテム交換画面）");

        // 実測は Fire(14, 0u, 1u) と Fire(14, 0u, 2u)。
        // コマンドは Int、index と数量は UInt だった。
        // int のまま渡すと AtkValueType.Int になり、実測と型が食い違う。
        Callback.Fire(addon, true, InclusionExchangeCommand, (uint)callbackIndex, (uint)amount);

        // 実測では、撃った直後に確認ダイアログが出る。
        // これに答えないと交換は成立しない。
        this.Step = ExchangeStep.InclusionConfirm;
        this.dialogDeadlineUtc = DateTime.UtcNow.Add(DialogTimeout);
        this.outcomeDeadlineUtc = DateTime.UtcNow.Add(OutcomeTimeout);
        this.StatusDetail = "確認ダイアログに答えています";
    }

    /// <summary>
    /// アイテム交換画面で、目的の系統と種別を選ぶ。
    ///
    /// この画面は 2 段の絞り込みを通さないと目的の品が一覧に出てこない。
    /// 選んだあとは中身が入れ替わるので、実際に切り替わったことを確認してから次へ進む。
    /// </summary>
    private void TickSelectInclusionCategory()
    {
        var target = this.travelTarget;
        if (target is null || target.Inclusion is not { } path)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        if (!this.inclusionShop.IsOpen())
        {
            if (DateTime.UtcNow > this.stepDeadlineUtc)
            {
                this.Fail(ExchangeFailure.ShopNotOpen, "アイテム交換画面が開いていません");
            }

            return;
        }

        if (!this.inclusionShop.TryGetSelection(out var selection) || selection is null)
        {
            if (DateTime.UtcNow > this.stepDeadlineUtc)
            {
                this.Fail(ExchangeFailure.ShopNotOpen, "アイテム交換画面の状態を読み取れませんでした");
            }

            return;
        }

        // 系統がまだ目的のものでなければ切り替える。
        if (selection.SelectedCategoryRowId != path.CategoryId)
        {
            if (!EzThrottler.Throttle("AutoCollector.InclusionCategory", 500))
            {
                return;
            }

            if (!this.inclusionShop.TrySelectCategory(path.CategoryId, out var categoryFailure))
            {
                if (DateTime.UtcNow > this.stepDeadlineUtc)
                {
                    this.Fail(ExchangeFailure.ShopMismatch, categoryFailure);
                }

                return;
            }

            this.StatusDetail = $"「{path.CategoryName}」を選んでいます";
            return;
        }

        // 系統は合っている。目的の品が一覧に出ているかを確かめる。
        if (!this.inclusionShop.TryGetAddon(out var addon))
        {
            return;
        }

        if (this.inclusionShop.TryReadEntries(addon, out var entries, out _, out _))
        {
            foreach (var entry in entries)
            {
                if (entry.ItemId == target.RewardItemId)
                {
                    this.pendingRequest = target;
                    this.Step = ExchangeStep.Armed;
                    this.StatusDetail = "交換の直前確認をしています";
                    return;
                }
            }
        }

        // 出ていなければ種別を切り替えて探す。
        if (!EzThrottler.Throttle("AutoCollector.InclusionSubCategory", 700))
        {
            return;
        }

        if (!this.inclusionShop.TrySelectSubCategory(selection.SelectedSeriesId, target.ShopId, out var subFailure))
        {
            if (DateTime.UtcNow > this.stepDeadlineUtc)
            {
                this.Fail(ExchangeFailure.ShopMismatch, subFailure);
            }

            return;
        }

        if (DateTime.UtcNow > this.stepDeadlineUtc)
        {
            this.Fail(
                ExchangeFailure.ExchangeItemNotFound,
                "アイテム交換画面に目的の品が見つかりませんでした。種別を手動で選んでからお試しください");
        }
    }

    /// <summary>
    /// アイテム交換画面の確認ダイアログに答える。
    ///
    /// 実測では、交換を撃った直後に ShopExchangeItemDialog が出る。
    /// さらに品によっては SelectYesno も続く。
    ///
    /// ここは撃ったあとなので、答えないと結果が確定しない。
    /// ただしこちらから内容を照合する手立てが無いため、
    /// 「自分が撃った直後に出たものだけ」を対象にする。
    /// </summary>
    private void TickInclusionConfirm()
    {
        var attempt = this.InFlight;
        if (attempt is null)
        {
            this.Step = ExchangeStep.Idle;
            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeItemDialog", out var dialog) &&
            GenericHelpers.IsAddonReady(dialog))
        {
            if (!EzThrottler.Throttle("AutoCollector.InclusionConfirm", 400))
            {
                return;
            }

            this.anomalyLog.Info("Exchange", "交換の確認ダイアログに答えます");

            try
            {
                new AddonMaster.ShopExchangeItemDialog((nint)dialog).Exchange();
            }
            catch (Exception ex)
            {
                this.FailUnresolved(ExchangeFailure.ConfirmDialogNotConfirmable, $"確認ダイアログを押せませんでした: {ex.Message}");
            }

            return;
        }

        // 品によっては、さらに確認が続く。
        if (this.TryFindConfirmDialog(attempt, out _, out _))
        {
            this.Step = ExchangeStep.ConfirmDialog;
            this.dialogDeadlineUtc = DateTime.UtcNow.Add(DialogTimeout);
            this.StatusDetail = "確認ダイアログを処理しています";
            return;
        }

        // ダイアログが消えたら結果の確認へ進む。
        this.Step = ExchangeStep.WaitOutcome;
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

        // アイテム交換画面の確認ダイアログが遅れて出ることがある。
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeItemDialog", out var itemDialog) &&
            GenericHelpers.IsAddonReady(itemDialog))
        {
            this.Step = ExchangeStep.InclusionConfirm;
            this.dialogDeadlineUtc = DateTime.UtcNow.Add(DialogTimeout);
            this.StatusDetail = "確認ダイアログに答えています";
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

        // **ウィンドウが閉じたら、待ち続けても結果は出ない。**
        //
        // 交換の途中で話しかけられる距離から外れると、ゲームがショップを閉じる。
        // 実測（2026-09-17 22:49）では、撃った 33 ミリ秒後に
        // ShopExchangeCurrency が閉じ、続いて確認ダイアログも消えていた。
        // AutoRetainer がベンチャーの回収で呼び鈴へ歩き出した場面。
        //
        // **これは「ゲームが購入を拒んだ」のとは別。**
        // 品自体に問題は無いので、以後飛ばす対象にしてはいけない。
        // 15 秒待ってから「所持数が動いていません」と誤診断し、
        // その品を恒久的に除外していた。
        // どちらの窓口で撃ったかは attempt に無いので、両方が閉じていることを見る。
        if (!this.shopService.IsShopOpen() && !this.inclusionShop.IsOpen())
        {
            this.anomalyLog.Warn(
                "Exchange",
                $"{attempt.RewardName} の交換中にショップが閉じました。" +
                "話しかけられる距離から外れた可能性があります（ほかのプラグインの移動など）");

            attempt.Resolved = true;
            attempt.Outcome = "交換の途中でショップが閉じました";
            Plugin.C.InFlight = null;
            EzConfig.Save();

            // 残っている確認を片づけてから終わる。
            this.CloseLeftoverDialogs();

            this.Fail(
                ExchangeFailure.ShopClosedUnexpectedly,
                "交換の途中でショップが閉じました。話しかけられる距離から外れた可能性があります");
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
                // 通貨も品も、まったく動いていない。
                //
                // ゲーム側が購入を拒んだということ。実測では、習得済みの秘伝書で
                // Fire(14, 0u, 1u) と確認ダイアログまで通るのに増減が無かった。
                //
                // 片方だけ動いている場合と違い、ここは「交換されていない」と言い切れる。
                // 未確定として残すと、以後すべての交換が受け付けられなくなる。
                // 1 品の都合で全体を止めるのは割に合わない。
                this.anomalyLog.Warn(
                    "Exchange",
                    $"{attempt.RewardName} は交換できませんでした。所持数が動いていません。この品は以後飛ばします");

                attempt.Resolved = true;
                attempt.Outcome = "交換できませんでした（所持数が動いていません）";
                Plugin.C.InFlight = null;
                EzConfig.Save();

                // 同じ品を何度も試さない。プラグインを読み込み直すまで覚えておく。
                this.rejectedRewards.Add(attempt.RewardItemId);

                this.Failure = ExchangeFailure.None;
                this.StatusDetail = $"{attempt.RewardName} は交換できませんでした";

                // **開いたままの確認を片づけてから次へ進む。**
                //
                // ここへ来る理由の 1 つが「確認ダイアログを押せなかった」。
                // 押せないまま残っているものを置いて次の品を撃つと、
                // 発火直前の P-5 が阻害アドオンとして弾き、リストの残りが
                // 1 品も試されないままプリセット全体が失敗になる。
                //
                // 実際、装備品 9 件のうち 1 件目でこうなり、
                // 残り 8 件は一度も試されずに終わっていた。
                this.CloseLeftoverDialogs();

                if (this.TryAdvanceToNextTarget("交換できませんでした"))
                {
                    return;
                }

                this.Step = ExchangeStep.ResumeAutoDuty;
                this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
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
                // **1 回の発火で何回ぶん撃ったかを見る。**
                //
                // 1 しか引いていなかった。まとめ買いが効く窓口では
                // 「交換する回数 5」を指定しても 5 → 4 → 3 … と撃ち続け、
                // 合計 15 回買うことになる。個数と回数を取り違えないという
                // 方針は、カウンタを減らす側にも要る。
                var fired = Math.Max(1, attempt.Amount);

                current.Completed += fired;
                current.RemainingCount = Math.Max(0, current.RemainingCount - fired);

                if (current.Current is { } finishedTarget)
                {
                    // **回数と個数を混ぜない。**
                    // fired は撃った回数。Remaining は利用者が入れた個数。
                    //
                    // attempt.RewardQuantity は「この発火で受け取る合計個数」で、
                    // まとめ買いのときは既に回数を掛けた値が入っている
                    // （FireInclusionExchange で definition.RewardQuantity * amount）。
                    // ここで回数を掛け直すと二重になる。
                    var gained = Math.Max(1, (int)attempt.RewardQuantity);

                    finishedTarget.Completed += fired;
                    if (!finishedTarget.Unlimited && finishedTarget.Remaining > 0)
                    {
                        finishedTarget.Remaining = Math.Max(0, finishedTarget.Remaining - gained);
                    }
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

            // この品は終わり。交換リストに次があれば、同じ窓口で続ける。
            if (this.TryAdvanceToNextTarget(stopReason))
            {
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
    /// <summary>
    /// 1 回の発火で何個交換するかを決める。
    ///
    /// 1 個ずつだと 1 回あたり 1.5 秒かかる。10 個で 15 秒になり、
    /// 交換リストを並べるほど待ち時間が積み上がる。
    ///
    /// **足りない側に合わせる。** 通貨・所持枠・残りの必要数のうち一番小さい数にする。
    /// 画面が数量を選べない品は 1 個のまま。
    /// </summary>
    /// <summary>
    /// 1 回の発火で何回交換するかを決める。
    ///
    /// **判断は <see cref="ExchangeLimits"/> に集めてある。ここでは持たない。**
    /// 以前はこの経路にだけ歯止めがあり、トームストーンの窓口には無かった。
    /// 同じ概念の実装が 2 本あると、片方だけ育って差が開く。
    ///
    /// ここが決めるのは「この窓口で 1 回にまとめて撃てるか」だけ。
    /// </summary>
    private int DecideBatchAmount(
        ExchangeDefinition definition,
        InclusionShopEntry entry,
        int currencyBefore,
        int rewardBefore,
        int freeSlots,
        int keepFree,
        out string blockReason,
        out bool endsSession)
    {
        // 数量を選べない品はまとめ買いできない。
        var maxBatch = entry.CanSelectAmount && definition.CurrencyCost > 0 ? MaxBatchAmount : 1;

        var allowance = this.EvaluateAllowance(
            definition, rewardBefore, currencyBefore, freeSlots, keepFree, maxBatch);

        blockReason = allowance.Reason;
        endsSession = allowance.EndsSession;
        return allowance.Trades;
    }

    /// <summary>
    /// 交換リストの次の品へ進む。
    ///
    /// **いま行っている窓口で扱えるものだけを続ける。**
    /// 別の窓口の品まで追いかけると、移動を繰り返して手に負えなくなる。
    /// 扱えなかったものは次回の判定で拾う。
    ///
    /// 交換画面は開いたままなので、品を探し直すところから再開する。
    /// </summary>
    private bool TryAdvanceToNextTarget(string previousStopReason)
    {
        var current = this.session;

        if (current is null || current.Targets.Count == 0)
        {
            return false;
        }

        // **セッション全体の打ち切りなら、次の品へ進まない。**
        //
        // 止まった理由を見ずに進んでいたため、「残す通貨量に達しました」で
        // 終わった直後に次の品を撃ち直していた。
        // 予備として残すはずの通貨を、品の数だけ削っていくことになる。
        //
        // **文字列で判断しない。**
        // 「通貨」を含むかどうかで見ていたが、「通貨が足りません」は
        // いま扱っている品の値段に対する判断で、セッション全体の話ではない。
        // 高い品で使い切ると、安い品がまだ買えるのに全部見送っていた。
        if (this.sessionExhausted)
        {
            this.anomalyLog.Info("Exchange", $"次の品へは進みません（{previousStopReason}）");
            return false;
        }

        var currentNpc = this.travelTarget?.NpcDataId ?? 0;

        while (current.TargetIndex + 1 < current.Targets.Count)
        {
            current.TargetIndex++;

            var next = current.Current;
            if (next is null)
            {
                break;
            }

            if (this.rejectedRewards.Contains(next.Definition.RewardItemId))
            {
                continue;
            }

            if (currentNpc != 0 && next.Definition.NpcDataId != currentNpc)
            {
                this.anomalyLog.Info(
                    "Exchange",
                    $"{next.Definition.RewardItemId} は別の窓口のため、この移動では交換しません");
                continue;
            }

            // 撃てない品は飛ばす。**判断は共通のものを使う。**
            // ここに条件を書き写すと、また片方だけ育って差が開く。
            var owned = this.currencyService.TryGetCount(
                next.Definition.RewardItemId, out var held,
                includeEquipped: true, includeArmory: true) ? held : 0;

            var currency = this.currencyService.TryGetCount(
                next.Definition.CurrencyItemId, out var have) ? have : 0;

            var bag = this.currencyService.TryGetEmptyBagSlots(out var slots) ? (int)slots : int.MaxValue / 2;

            // この時点で Current は next を指している。BuildLimits がそれを見る。
            var check = this.EvaluateAllowance(
                next.Definition, owned, currency, bag, this.KeepFreeSlots(), maxBatch: 1);

            if (!check.Allowed)
            {
                this.anomalyLog.Info(
                    "Exchange",
                    $"{next.Definition.RewardItemId} は飛ばします: {check.Reason}");
                continue;
            }

            this.anomalyLog.Info("Exchange", $"次の品へ進みます（前の品: {previousStopReason}）");

            this.travelTarget = next.Definition;
            this.nextArmedAllowedUtc = DateTime.UtcNow.Add(SettleBetweenExchanges);
            this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(30);

            if (next.Definition.UsesInclusionShop)
            {
                // 種別が違うかもしれない。探し直すところから。
                this.pendingRequest = null;
                this.Step = ExchangeStep.SelectInclusionCategory;
                this.StatusDetail = "次の品の種別を選んでいます";
            }
            else
            {
                this.pendingRequest = next.Definition;
                this.Step = ExchangeStep.Armed;
                this.StatusDetail = "次の品の交換を確認しています";
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// いまのセッションから、利用者が決めた歯止めを取り出す。
    ///
    /// 交換リストを使う場合は品ごとの設定、使わない場合（手動実行など）は
    /// セッション全体の設定を見る。どちらも同じ形にして
    /// <see cref="ExchangeLimits"/> へ渡す。
    /// </summary>
    private ExchangeLimitSet BuildLimits()
    {
        var current = this.session;

        if (current is null)
        {
            // セッションが無いなら「1 回だけ」。
            //
            // **回数の欄で表す。** 個数の欄に 1 を入れると、
            // 1 回で 2 個以上もらえる品が「あと 1 個なので撃てない」と判断され、
            // 手動の「交換する」が永久に無反応になる。
            return ExchangeLimitSet.SingleTrade();
        }

        var target = current.Current;

        return ExchangeLimitSet.ForRun(
            targetUnlimited: target?.Unlimited,
            targetRemainingItems: target?.Remaining ?? 0,
            targetOwnedLimit: target?.OwnedLimit ?? 0,
            mode: current.Mode,
            currencyReserve: current.CurrencyReserve,
            remainingTrades: current.RemainingCount,
            targetQuantity: current.TargetQuantity,

            // 製作で稼ぐプリセットは「素材が尽きるまで」が正規の遊び方。
            allowOpenEnded: Plugin.C.Presets
                .FirstOrDefault(x => x.Id == current.PresetId)?.CraftToEarn ?? false);
    }

    /// <summary>
    /// いま何回まで交換してよいか。**窓口の種類に依らず、ここを通す。**
    /// </summary>
    private ExchangeAllowance EvaluateAllowance(
        ExchangeDefinition definition, int owned, int currency, int freeSlots, int keepFree, int maxBatch)
        => ExchangeLimits.Evaluate(
            perTrade: (int)definition.RewardQuantity,
            currencyCost: (int)definition.CurrencyCost,
            owned: owned,
            currency: currency,
            freeSlots: freeSlots,
            keepFree: keepFree,
            limits: this.BuildLimits(),
            maxBatch: maxBatch);

    /// <summary>
    /// 同じショップでもう一度交換してよいか。
    ///
    /// **判断は ExchangeLimits に集めてある。ここでは持たない。**
    ///
    /// 以前はここに条件を並べており、交換リストを足したときに
    /// 既存の判断の**手前**へ分岐を差し込んで early return したため、
    /// プリセットの「どこまで交換するか」が二度と評価されなくなった
    /// （2026-09-13 のコミット 8af4f8f）。
    /// 「所持の上限を超えて買い続ける」はそこから来ている。
    ///
    /// 答えを 1 つ返す関数にしておけば、呼ぶ側に分岐を足しても判断はすり抜けない。
    /// </summary>
    private bool ShouldContinueSession(int currencyAfter, int rewardAfter, out string stopReason)
    {
        stopReason = string.Empty;
        this.sessionExhausted = false;

        var current = this.session;
        var definition = this.travelTarget;

        if (current is null || definition is null)
        {
            stopReason = "セッションが設定されていません";
            this.sessionExhausted = true;
            return false;
        }

        if (current.Completed >= ExchangeSession.HardLimit)
        {
            stopReason = $"上限の {ExchangeSession.HardLimit} 回に達しました";
            this.sessionExhausted = true;
            return false;
        }

        if (this.aborted)
        {
            stopReason = "停止が要求されました";
            this.sessionExhausted = true;
            return false;
        }

        var keepFree = this.KeepFreeSlots();
        if (!this.currencyService.TryGetEmptyBagSlots(out var freeSlots))
        {
            stopReason = "所持枠の空きを取得できませんでした";
            this.sessionExhausted = true;
            return false;
        }

        // 撃つ前とまったく同じ判断を、交換後の数で行う。
        // 次に撃てるのは 1 回ぶんなので maxBatch は 1。
        var allowance = this.EvaluateAllowance(
            definition, rewardAfter, currencyAfter, (int)freeSlots, keepFree, maxBatch: 1);

        if (allowance.Allowed)
        {
            return true;
        }

        stopReason = allowance.Reason;
        this.sessionExhausted = allowance.EndsSession;
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

        // 開いている確認ダイアログを、いったん全部拾う。
        // 本文が合わないものも捨てずに持っておく。合わなかったという事実を記録に残す。
        var open = new List<(nint Address, string Body)>();

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

            try
            {
                var master = new AddonMaster.SelectYesno((nint)addon);
                if (master.Addon->PromptText is null)
                {
                    continue;
                }

                open.Add(((nint)addon, master.Text ?? string.Empty));
            }
            catch
            {
                continue;
            }
        }

        if (open.Count == 0)
        {
            return false;
        }

        // --- 1 段目: 本文が合うもの。いちばん確かな採り方 ---
        foreach (var (address, body) in open)
        {
            if (!body.Contains(currencyName, StringComparison.Ordinal) ||
                !body.Contains(cost, StringComparison.Ordinal))
            {
                continue;
            }

            // 追加の裏付け: 報酬名がアドオン内のどこかに出ているか
            if (!string.IsNullOrEmpty(attempt.RewardName))
            {
                var texts = CollectTexts((AtkUnitBase*)address);
                var rewardShown = texts.Any(t => t.Contains(attempt.RewardName, StringComparison.Ordinal));
                if (!rewardShown)
                {
                    this.anomalyLog.Warn(
                        "Exchange",
                        $"確認ダイアログに報酬名「{attempt.RewardName}」が見つかりませんでした。表示されていたテキスト: {string.Join(" / ", texts.Where(x => !string.IsNullOrWhiteSpace(x)))}");
                }
            }

            found = (AtkUnitBase*)address;
            text = body;
            return true;
        }

        // --- 1.5 段目: 交換に付随する確認として分かっているもの ---
        //
        // **装備できない品を交換するときは、別の確認が出る。**
        // 実測（利用者の報告・2026-09-19）:
        //   「クラスやレベル、装備状態が合わないためこのアイテムを装備することが
        //     出来ません。交換しますか？」
        //
        // 本文に通貨名もコストも出ないため 1 段目では拾えない。
        // ナイトで周回しながら弓術士用の装備を交換する、といった場面で必ず出る。
        // **交換できない品という意味ではない。** 受け取った装備はアーマリーへ入る。
        //
        // **文面が合うだけでは押さない。**
        // 撃った時刻より後に、自分が開かせたものであることも要る。
        // 文面だけを条件にすると、たまたま同じ語を含む別のダイアログを押しうる。
        var firedAtKnown = attempt.FiredAtUtc;
        var freshWindow = firedAtKnown != default &&
                          DateTime.UtcNow - firedAtKnown <= OwnedDialogWindow;

        if (freshWindow &&
            this.ownership.TryGetOwnedSince("SelectYesno", firedAtKnown, out var knownDialog))
        {
            var knownBody = open.FirstOrDefault(x => x.Address == (nint)knownDialog).Body ?? string.Empty;

            if (KnownExchangeConfirmations.Any(w => knownBody.Contains(w, StringComparison.Ordinal)))
            {
                this.anomalyLog.Info(
                    "Exchange",
                    $"交換に付随する確認に答えます: 「{knownBody}」");

                found = knownDialog;
                text = knownBody;
                return true;
            }
        }

        // --- 2 段目: 自分が開かせたものとして採る ---
        //
        // **本文は品によって変わる。**
        // 実測できていたのは消耗品の「アラガントームストーン:数理×20と交換します。」1 例だけ。
        // 装備品は確認がもう 1 枚増えることが別途分かっていた（docs/05）。
        // 本文照合だけを入口にしていたため、装備品では一度も押せず、
        // 15 秒待って「所持数が動いていません」と誤診断していた。
        //
        // **推測では採らない。「撃った直後に自分が開かせたもの」だけを採る。**
        //
        // 所有権の記録だけでは足りない。自分の操作は移動や会話を含めて何分も続き、
        // その間に利用者が出した確認ウィンドウまで「自分のもの」になる。
        // 本文を見ない経路なので、そのまま押すと
        // 「アイテムを捨てますか」「ログアウトしますか」に Yes を押しうる。
        //
        // 撃った時刻より**あとに**開いたものに限り、かつ撃ってすぐの間だけを見る。
        var firedAt = attempt.FiredAtUtc;
        var withinWindow = firedAt != default && DateTime.UtcNow - firedAt <= OwnedDialogWindow;

        if (withinWindow &&
            this.ownership.TryGetOwnedSince("SelectYesno", firedAt, out var ownedDialog))
        {
            var body = open.FirstOrDefault(x => x.Address == (nint)ownedDialog).Body ?? string.Empty;

            // **押してはいけない本文は拒む。**
            // 本文で「合っている」ことを確かめられない経路なので、
            // せめて「明らかに違う」ものは弾く。取り返しのつかない操作を防ぐ。
            var dangerous = DangerousDialogWords.FirstOrDefault(
                w => body.Contains(w, StringComparison.Ordinal));

            if (dangerous is not null)
            {
                this.anomalyLog.Error(
                    "Exchange",
                    $"確認ダイアログに「{dangerous}」が含まれるため押しません。交換のものではない可能性があります。本文: 「{body}」");
                return false;
            }

            this.anomalyLog.Warn(
                "Exchange",
                $"確認ダイアログの本文が想定と違いますが、撃った直後に自分が開かせたものとして扱います。" +
                $"本文: 「{body}」 / 探していた語: 「{currencyName}」「{cost}」 / " +
                $"表示されていたテキスト: {string.Join(" / ", CollectTexts(ownedDialog).Where(x => !string.IsNullOrWhiteSpace(x)))}");

            found = ownedDialog;
            text = body;
            return true;
        }

        // 押さずに見送ったことを残す。黙って見送ると、次の報告でも原因が分からない。
        if (EzThrottler.Throttle("AutoCollector.ConfirmUnmatched", 5000))
        {
            this.anomalyLog.Warn(
                "Exchange",
                $"確認ダイアログを {open.Count} 枚見つけましたが、どれも自分のものと判断できませんでした。" +
                $"探していた語: 「{currencyName}」「{cost}」 / 本文: {string.Join(" / ", open.Select(x => $"「{x.Body}」"))}");
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
    /// <summary>
    /// 交換で埋めずに残しておく所持枠。
    ///
    /// **設定タブの項目は廃止した。** プリセットの「残す空き枠」だけで決める。
    /// 2 つあると、片方を直しても交換の数が変わらず、理由が読めなくなる。
    ///
    /// ただし 0 にはしない。**鞄を空き 0 まで埋めてはいけない。**
    /// AutoRetainer は所持枠が空いていないキャラクタを処理対象から外し、
    /// その判断を設定へ保存する（既定の下限は 2 枠）。
    /// 埋め切ると、手で戻すまでリテイナーが回らなくなる。
    /// 次の周の製作も「空き枠が 0」で始められなくなる。
    /// </summary>
    private int KeepFreeSlots()
    {
        // **製作用の枠を、交換の関門に使わない。**
        //
        // プリセットの「残す空き枠」は、次に作る収集品を入れる余地のための値。
        // 橙のプリセットでは 100 のように大きく取る。
        // それをそのまま交換の関門にしていたため、鞄の空きが 100 以下だと
        // 交換所へ着くたびに必ず落ちていた。2 周でプリセットが無効になる形で
        // 表に出た（いまは失敗に数えないが、交換できないことは変わらない）。
        //
        // 交換で受け取るのは数個。残す枠も数枠でよい。
        // 製作の側は BuildPlan が自分で「残す空き枠」を見る。役割が違う。
        const int floor = 2;

        return floor;
    }

    private void Fail(ExchangeFailure failure, string detail)
    {
        this.Step = ExchangeStep.Error;
        this.Failure = failure;
        this.StatusDetail = detail;
        this.anomalyLog.Warn("Exchange", $"{failure}: {detail}");

        // **開いた窓を必ず閉じてから離れる。**
        //
        // 失敗のときだけ Cleanup を通していなかった。成功なら閉じるのに、
        // 失敗すると交換画面が開いたまま残る。画面が開いている間は
        // OccupiedInEvent が立ちっぱなしになり、安全判定が
        // 「他の操作中です」を返し続けて、以後どの動作も始められなくなる。
        //
        // 2026-09-14 実測: 通貨不足で終わったあと、3 分放置しても復帰しなかった。
        //
        // Cleanup は returnContext を消すので、AutoDuty の再開に使うぶんは取っておく。
        var context = this.returnContext;

        this.Cleanup();

        this.returnContext = context;
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
        this.artisan.Release();
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
