using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace AutoCollector.Automation;

/// <summary>FATE 周回のいまの段階。</summary>
public enum FateStep
{
    /// <summary>止まっている。</summary>
    Idle,

    /// <summary>周回するマップにいない。テレポートする。</summary>
    Traveling,

    /// <summary>マップにいるが、狙える FATE が無い。待つ。</summary>
    Waiting,

    /// <summary>狙う FATE を決めた。そこへ移動している。</summary>
    MovingToFate,

    /// <summary>FATE の中で戦っている。</summary>
    Fighting,

    /// <summary>達成度 100% を見た。次の FATE へ向かい始めている。</summary>
    Leaving,

    /// <summary>
    /// FATE に着いた。マウントから降りている。
    ///
    /// <b>この間は vnavmesh に一切触らない。</b>
    /// 空中からの降下はゲーム側が行うもので、その動きが
    /// プレイヤーの操作として vnavmesh に読まれる。経路を積んでも
    /// その場で捨てられ、積んでは捨てるを繰り返して暴れる。
    /// </summary>
    Landing,

    /// <summary>戦闘不能。設定に従って待つか戻る。</summary>
    Dead,

    /// <summary>止めた。</summary>
    Done,

    /// <summary>続けられない状態になった。</summary>
    Error,
}

/// <summary>
/// FATE を自動で回す。
///
/// <b>稼ぎ方の 1 つとして動く。</b>
/// 交換で中断され、交換が済んだら元のエリアへ戻って再開する。
/// その受け渡しは <c>Earning.Fate.FateEarner</c> が IEarner として受け持ち、
/// ここは周回そのものだけを行う。
///
/// <b>戦闘は BossMod Reborn に任せる。</b>
/// 自前の戦闘 AI は持たない。プリセットを有効にして、
/// 離脱するときに解除するだけ。
///
/// <b>達成度 100% を見たら即座に離れる。</b>
/// FATE が消えるのを待つと、その場に立ち尽くす時間が生まれる。
/// 次の FATE は達成度が閾値を超えた時点で決めておき、
/// 100% を見た瞬間に動き出せるようにする。
/// </summary>
public sealed class FateRunner(
    AnomalyLog anomalyLog,
    FateScanner scanner,
    NavigationService navigation,
    BossModIpc bossMod,
    BuddyService buddy,
    MountService mount,
    FateTrace trace,
    LifestreamIpc lifestream,
    AetheryteService aetherytes,
    VnavmeshIpc vnavmesh)
{
    /// <summary>FATE の円へ入ったとみなす距離の余裕。</summary>
    private const float FateArrivalSlack = 5f;

    /// <summary>移動が進まないまま経過したら、その FATE を諦める。</summary>
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 飛ぶために、乗れるのを待つ上限。
    ///
    /// これを過ぎても乗れないなら、地上の経路で向かう。
    /// 乗騎が解放されていない、直前の戦闘で行動が塞がれているなど、
    /// 待っても乗れない事情がありうる。待ち続けて一歩も進まないより、
    /// 走ってでも着いたほうがよい。
    /// </summary>
    private static readonly TimeSpan MountWaitBeforeGroundPath = TimeSpan.FromSeconds(5);

    /// <summary>同じ FATE で詰まってよい回数。超えたら二度と狙わない。</summary>
    private const int MaxStuckPerFate = 2;

    /// <summary>
    /// 敵がこれより遠ければ歩いて近づく。
    ///
    /// BMR は見えている敵と戦うだけで、遠くの敵を探しには行かない。
    /// 遠距離職でも届く範囲より少し内側にしてある。
    /// </summary>
    private const float MobReachMeters = 15f;

    /// <summary>戦闘中にプリセットが有効なままかを確かめる間隔。</summary>
    private static readonly TimeSpan PresetCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 追いかけている敵がこれだけ動いたら、経路を引き直す。
    ///
    /// 小さくすると敵に吸い付くが、引き直しが増えて足が止まる。
    /// 敵が多少動いても、近づいていれば BMR が拾うので、粗くてよい。
    /// </summary>
    private const float ApproachRepathMeters = 5f;

    /// <summary>近づく経路を引き直す間隔。経路探索は重いので続けて投げない。</summary>
    private static readonly TimeSpan ApproachRepathInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 進んでいるかを見る間隔。
    ///
    /// <b>短くしすぎない。</b>遠い FATE への飛行経路は、探すのに時間がかかる。
    /// 1000m 先だと十数秒かかることがあり、その間は当然 1m も進まない。
    /// 3 秒で見ていたため、経路が出来る前に「詰まった」と判断して
    /// 引き直しを繰り返し、いつまでも出発できなかった（2026-09-25 実測）。
    ///
    /// 逆に長すぎると、壁に当たったまま何十秒も押し続けることになる。
    /// 出発前は経路の本数で見送るようにしたので、ここは
    /// 「飛んでいるのに進んでいない」を拾える長さでよい。
    /// </summary>
    private static readonly TimeSpan BlockCheckInterval = TimeSpan.FromSeconds(6);

    /// <summary>この間隔でこれだけ進んでいなければ、詰まっているとみなす。</summary>
    private const float BlockProgressMeters = 5f;

    /// <summary>詰まったときに避ける球の半径。迂回するたびに広げる。</summary>
    private const float DetourRadiusMeters = 15f;

    /// <summary>
    /// 狭い通路で経路をなぞらせるときの許容値。
    ///
    /// vnavmesh の既定は 0.25。これだけ経路から外れてよいため、
    /// 曲がり角を端折って壁に寄る。詰まったときはここまで詰める。
    /// </summary>
    private const float TightPathTolerance = 0.05f;

    /// <summary>vnavmesh の既定の許容値。詰めたあとはここへ戻す。</summary>
    private const float DefaultPathTolerance = 0.25f;

    /// <summary>降りられる場所を探すとき、中心から何周ぶん見るか。</summary>
    private const int LandableSearchRings = 4;

    /// <summary>辿り着けなかった FATE を見送る長さ。過ぎたらもう一度試す。</summary>
    private static readonly TimeSpan BlacklistDuration = TimeSpan.FromMinutes(5);

    /// <summary>1 つの移動で迂回を試す上限。これを超えたら力づくで離れる。</summary>
    private const int MaxDetours = 2;

    /// <summary>詰まりから抜け出そうとする上限。これを超えたら諦める。</summary>
    private const int MaxEscapeAttempts = 4;

    /// <summary>抜け出す動きに与える時間。この間は測り直さない。</summary>
    private static readonly TimeSpan EscapeSettleTime = TimeSpan.FromSeconds(4);

    /// <summary>手動の脱出を「もう一度押した」とみなす間隔。</summary>
    private static readonly TimeSpan EscapeNowRetryWindow = TimeSpan.FromSeconds(30);

    /// <summary>見張りが「動いた」と認める距離。</summary>
    private const float WatchdogProgressMeters = 3f;

    /// <summary>
    /// 動くはずの段階で、これだけ動かなければ詰まりとみなす。
    ///
    /// 経路探索や着地には時間がかかるので、短くしすぎない。
    /// </summary>
    private static readonly TimeSpan WatchdogPatience = TimeSpan.FromSeconds(25);

    /// <summary>
    /// 敵が 1 匹も見えないとき、中心からこれ以上離れていたら寄る。
    ///
    /// 円の端では敵が湧いても見えないことがある。
    /// </summary>
    private const float CentreSeekMeters = 20f;

    /// <summary>
    /// 飛んできたとき、中心からこれだけの範囲に入ってから降りる。
    ///
    /// <b>端で降りると高低差で詰む。</b>
    /// 円に入った時点で降りていたため、低い場所に降りて高所の敵へ
    /// 近づけなくなった（2026-09-25 実測）。
    /// 中心付近なら、たいてい敵の居る高さに降りられる。
    /// </summary>
    private const float LandNearCentreMeters = 15f;

    /// <summary>テレポートが終わるのを待つ上限。</summary>
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 降りるのを待つ上限。空からの降下は数秒かかる。
    ///
    /// 長く取りすぎない。降りられない場所（水面の上など）に来てしまったときは、
    /// 待っても降りられない。早めに諦めて別の FATE へ移るほうがよい。
    /// </summary>
    private static readonly TimeSpan LandingTimeout = TimeSpan.FromSeconds(12);

    /// <summary>納品 FATE の報酬が着地するまでの猶予。1 分 + 余裕。</summary>
    private static readonly TimeSpan CollectRewardWindow = TimeSpan.FromSeconds(90);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly FateScanner scanner = scanner;
    private readonly NavigationService navigation = navigation;
    private readonly BossModIpc bossMod = bossMod;
    private readonly BuddyService buddy = buddy;
    private readonly MountService mount = mount;
    private readonly FateTrace trace = trace;
    private readonly LifestreamIpc lifestream = lifestream;
    private readonly AetheryteService aetherytes = aetherytes;
    private readonly VnavmeshIpc vnavmesh = vnavmesh;

    /// <summary>
    /// FATE ごとに 1 度だけ求めた、降りられる座標。
    ///
    /// <b>エリアも一緒に覚える。</b>FATE の番号はマップをまたぐと使い回されるため、
    /// 番号だけで覚えると、テレポした先で別マップの座標を使ってしまう。
    /// </summary>
    private (uint TerritoryId, ushort Id, Vector3 Point)? landable;

    /// <summary>降りられずに移っている先。着くまで降下を試さない。</summary>
    private Vector3? landingRefuge;

    /// <summary>進み具合を最後に見た時刻と、そのときの位置。</summary>
    private DateTime blockCheckedUtc = DateTime.MinValue;
    private Vector3 blockCheckPosition;

    /// <summary>この移動で迂回を試した回数。</summary>
    private int blockDetours;

    /// <summary>この移動で、詰まりから抜け出そうとした回数。</summary>
    private int escapeAttempts;

    /// <summary>この移動で、経路追従の許容値を詰めたか。戻すときに使う。</summary>
    private bool tightenedPath;

    /// <summary>手動の脱出を最後に押した時刻と場所。二度目は帰還に切り替える。</summary>
    private DateTime lastEscapeNowUtc = DateTime.MinValue;
    private Vector3 lastEscapeNowFrom;

    /// <summary>動けていない状態がいつから続いているか。段階をまたいで見張る。</summary>
    private DateTime watchdogSince = DateTime.MinValue;
    private Vector3 watchdogPosition;

    /// <summary>頼んである迂回の経路探索。出来るまで待つ。</summary>
    private System.Threading.Tasks.Task<System.Collections.Generic.List<Vector3>>? detourTask;

    /// <summary>このセッションで詰まった FATE。もう狙わない。</summary>
    private readonly HashSet<ushort> blacklist = [];

    /// <summary>見送りを解く時刻。入り組んだ地形でも、置いてから再挑戦する。</summary>
    private readonly Dictionary<ushort, DateTime> blacklistUntil = [];

    /// <summary>FATE ごとの詰まり回数。</summary>
    private readonly Dictionary<ushort, int> stuckCounts = [];

    /// <summary>いま狙っている FATE。</summary>
    private FateInfo? target;

    /// <summary>次に狙う FATE。達成度が閾値を超えた時点で決めておく。</summary>
    private FateInfo? prefetched;

    /// <summary>報酬待ちの納品 FATE。着地するまでマップを離れない。</summary>
    private (ushort Id, int Start, DateTime DeadlineUtc)? pendingReward;

    /// <summary>
    /// もう離脱を済ませた FATE。
    ///
    /// <b>離れたことを覚えていないと、同じ FATE で無限に離脱し続ける。</b>
    /// FateManager.CurrentFate は、達成度 100% になっても円の中に立っている限り
    /// その FATE を指したままになる。覚えていないと毎フレーム
    /// 「100% の FATE に参加している」と読んで LeaveFate を呼び、
    /// 次の FATE への経路を引いた直後に引き直す、を延々と繰り返す。
    /// その場から一歩も動けない（2026-09-25 実測・18 ミリ秒ごとに往復していた）。
    /// </summary>
    private ushort? leftFateId;

    private DateTime moveStartedUtc = DateTime.MinValue;
    private DateTime teleportStartedUtc = DateTime.MinValue;

    /// <summary>この行き先へテレポを撃ったか。毎フレーム撃ち直さないための旗。</summary>
    private bool teleportIssued;
    private DateTime waitingSinceUtc = DateTime.MinValue;
    private DateTime deadSinceUtc = DateTime.MinValue;

    /// <summary>降り始めた時刻。降りられないまま続くのを打ち切るのに使う。</summary>
    private DateTime landingSinceUtc = DateTime.MinValue;
    private uint travelTargetTerritory;
    private int zoneIndex;
    /// <summary>いまの目的地へ経路を引いたか。乗ってから引くので旗で覚える。</summary>
    private bool moveIssued;

    /// <summary>経路を引いたとき飛んでいたか。地面に触れて解けたのを見分ける。</summary>
    private bool flyingWhenIssued;

    /// <summary>敵へ近づいている最中か。戦闘に入ったら下ろす。</summary>
    private bool approaching;

    /// <summary>いま経路を引いている先。敵が動いても、ここから離れるまでは引き直さない。</summary>
    private Vector3 approachTarget;

    /// <summary>近づく経路を最後に引いた時刻。続けて引き直さないための間隔に使う。</summary>
    private DateTime approachIssuedUtc = DateTime.MinValue;

    /// <summary>近づく経路を引いた回数。増え続けるなら引き直しすぎている。</summary>
    private int approachIssues;

    /// <summary>vnavmesh で歩かせるためにプリセットの移動を止めているか。止めたぶんは必ず戻す。</summary>
    private bool movementParked;

    /// <summary>プリセットが有効かを最後に確かめた時刻。</summary>
    private DateTime presetCheckedUtc = DateTime.MinValue;

    /// <summary>交換から戻ったときに向かう座標。設定で座標まで戻す場合だけ入る。</summary>
    private Vector3? resumePosition;

    private bool presetApplied;
    private string appliedPresetName = string.Empty;

    /// <summary>いまの段階。</summary>
    public FateStep Step { get; private set; } = FateStep.Idle;

    /// <summary>画面に出す短い説明。</summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>完了した FATE の数。</summary>
    public int Completed { get; private set; }

    /// <summary>動いているか。</summary>
    public bool IsRunning => this.Step is not (FateStep.Idle or FateStep.Done or FateStep.Error);

    /// <summary>止めた理由。</summary>
    public string? StoppedReason { get; private set; }

    private static Config C => Plugin.C;

    /// <summary>周回を始める。始められない場合は理由を返す。</summary>
    public bool Start(out string reason)
    {
        this.trace.Decision("開始を押した", $"段階={this.Step}");

        if (this.IsRunning)
        {
            reason = "すでに動いています";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        var cfg = C;

        if (cfg.FateZones.Count == 0)
        {
            reason = "周回するマップが選ばれていません";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        if (!this.bossMod.IsLoaded)
        {
            reason = "BossMod Reborn が導入されていません";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        if (!this.navigation.IsAvailable)
        {
            reason = "vnavmesh が導入されていません";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        // 利用者がプリセット名を指定していなければ、こちらで用意する。
        // どれを選べばよいか分からないのが普通なので、名前を知らなくても動くようにする。
        if (string.IsNullOrWhiteSpace(cfg.FateCombatPreset)
            && !FateCombatPreset.Ensure(this.bossMod, this.anomalyLog))
        {
            reason = $"BossMod Reborn にプリセット「{FateCombatPreset.Name}」を用意できませんでした";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        this.blacklist.Clear();
        this.blacklistUntil.Clear();
        this.stuckCounts.Clear();
        this.target = null;
        this.prefetched = null;
        this.pendingReward = null;
        this.leftFateId = null;
        this.landable = null;
        this.landingRefuge = null;
        this.travelTargetTerritory = 0;
        this.teleportIssued = false;
        this.Completed = 0;
        this.StoppedReason = null;
        this.presetApplied = false;
        this.appliedPresetName = string.Empty;
        this.buddy.Reset();

        // いまいるマップが選択に含まれていれば、そこから始める。
        var here = Svc.ClientState.TerritoryType;
        var index = cfg.FateZones.IndexOf(here);
        this.zoneIndex = index >= 0 ? index : 0;

        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
        this.anomalyLog.Info("Fate", $"FATE 周回を開始しました（マップ {cfg.FateZones.Count} 件）");
        this.trace.Decision("開始した", $"マップ {cfg.FateZones.Count} 件 いまのエリア={Svc.ClientState.TerritoryType}");

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 段階に関係なく、動けなくなっていないかを見張る。
    ///
    /// <b>「動くつもりなのに動いていない」を捕まえる。</b>
    /// 待っているだけの段階（FATE が湧くのを待つ、報酬を待つ）は
    /// 動かなくて当たり前なので見ない。
    ///
    /// 動くはずの段階で座標が変わらない状態が続いたら、
    /// 地形に挟まっているとみなして脱出へ進む。
    /// </summary>
    /// <returns>脱出の処理をしたなら true。その場合この Tick は先へ進めない。</returns>
    private bool TickStuckWatchdog()
    {
        // 動くはずの段階かどうか。
        var shouldMove = this.Step is FateStep.MovingToFate
                                   or FateStep.Landing
                                   or FateStep.Traveling
                      || (this.Step == FateStep.Fighting && this.approaching);

        if (!shouldMove || !Player.Available)
        {
            this.watchdogSince = DateTime.MinValue;
            return false;
        }

        // 報酬待ちは足を止めてよい。
        if (this.pendingReward is not null)
        {
            this.watchdogSince = DateTime.MinValue;
            return false;
        }

        var now = DateTime.UtcNow;
        var here = Player.Position;

        if (this.watchdogSince == DateTime.MinValue ||
            Vector3.Distance(here, this.watchdogPosition) > WatchdogProgressMeters)
        {
            this.watchdogSince = now;
            this.watchdogPosition = here;
            return false;
        }

        if (now - this.watchdogSince < WatchdogPatience)
        {
            return false;
        }

        // 動くはずなのに、ずっと同じ場所にいる。
        this.trace.Trouble(
            "動けていない",
            $"段階={this.Step} {WatchdogPatience.TotalSeconds:F0}秒間 " +
            $"({here.X:F0},{here.Y:F0},{here.Z:F0}) から動いていません");

        this.watchdogSince = now;
        this.watchdogPosition = here;

        // 狙っている FATE があればそれを、無ければ番号なしで脱出へ。
        this.EscapeStuckSpot(this.target, now, here);
        return true;
    }

    /// <summary>
    /// 詰めていた経路追従の許容値を、vnavmesh の既定へ戻す。
    ///
    /// <b>詰めたままにしない。</b>この設定は vnavmesh 全体のもので、
    /// 他のプラグインの移動にも効いてしまう。
    /// </summary>
    private void RestorePathTolerance()
    {
        if (!this.tightenedPath)
        {
            return;
        }

        this.tightenedPath = false;
        this.vnavmesh.TrySetPathTolerance(DefaultPathTolerance);
    }

    /// <summary>
    /// いまの場所から脱出する。画面のボタンから呼ぶ。
    ///
    /// <b>周回していなくても使える。</b>
    /// 止めた状態で入り組んだ場所に取り残されることがあるため、
    /// 手元にも逃げ道を置いておく。
    /// </summary>
    public void EscapeNow()
    {
        this.navigation.Stop();
        this.vnavmesh.TryStop();
        this.moveIssued = false;

        if (!Player.Available)
        {
            return;
        }

        var here = Player.Position;

        // 立てる場所を探す。まず真下、次に周り。
        Vector3? found = null;

        if (this.vnavmesh.TryIsReady(out var ready) && ready)
        {
            if (this.vnavmesh.TryPointOnFloor(here, out var floor) && floor is not null)
            {
                found = floor;
            }
            else if (this.vnavmesh.TryNearestPointReachable(here, 60f, 200f, out var near) && near is not null)
            {
                found = near;
            }
            else if (this.vnavmesh.TryNearestPoint(here, 150f, 300f, out var any) && any is not null)
            {
                found = any;
            }
        }

        // **2 回目は帰還する。**
        //
        // 1 回押して動かなかったのなら、動かして出せる場所ではない。
        // 同じことを繰り返しても結果は同じなので、座標ごと外へ出す。
        var repeated = DateTime.UtcNow - this.lastEscapeNowUtc < EscapeNowRetryWindow
                    && Vector3.Distance(here, this.lastEscapeNowFrom) < 5f;

        this.lastEscapeNowUtc = DateTime.UtcNow;
        this.lastEscapeNowFrom = here;

        if (found is { } spot && !repeated)
        {
            this.anomalyLog.Info(
                "Fate",
                $"({here.X:F0},{here.Y:F0},{here.Z:F0}) から " +
                $"({spot.X:F0},{spot.Y:F0},{spot.Z:F0}) へ脱出します。" +
                "動かなければ、もう一度押すと帰還します");

            this.vnavmesh.TryMoveAlong([spot], true);
            return;
        }

        // 動かして出せない。帰還で外へ出す。
        this.anomalyLog.Warn(
            "Fate",
            repeated
                ? "動かして出られないため、帰還して脱出します"
                : "立てる場所が見つからないため、帰還して脱出します");

        ReturnHome();
    }

    /// <summary>周回を止める。戦闘とプリセットを必ず元に戻す。</summary>
    public void Stop(string reason)
    {
        // **解除は状態を見ずに先に行う。**
        //
        // 止まっている状態でも、プリセットを有効にしたまま何らかの理由で
        // 段階だけが進んでいることがありうる。そのまま戻ると BMR が
        // 動き続けてしまい、利用者からは「止めたのに戦い続ける」に見える。
        // ReleaseCombat は適用していなければ即座に戻るので、余分な害は無い。
        this.ReleaseCombat();

        // **移動は段階を見ずに必ず止める。**
        //
        // 詰まりからの脱出は vnavmesh へ直接 Path.MoveTo を送っている。
        // これは段階（Step）と関係なく走り続けるため、
        // Idle や Done で先に return していると止まらない。
        // 利用者から見ると「止めたのに勝手に飛び続ける」になる
        // （2026-09-25 実測）。
        this.navigation.Stop();
        this.vnavmesh.TryStop();

        // 頼んである経路探索の結果も捨てる。
        // 残しておくと、止めたあとに出来上がって積まれてしまう。
        this.detourTask = null;
        this.landingRefuge = null;
        this.escapeAttempts = 0;
        this.blockDetours = 0;
        this.moveIssued = false;
        this.watchdogSince = DateTime.MinValue;
        this.RestorePathTolerance();

        if (this.Step is FateStep.Idle or FateStep.Done)
        {
            return;
        }

        // 降下の途中で止められることがある。そのままだと降下が続き、
        // 次に誰かが経路を積んだときに捨てさせてしまう。
        this.mount.ClearDismounting();

        this.StoppedReason = reason;
        this.target = null;
        this.prefetched = null;
        this.SetStep(FateStep.Done, reason);
        this.anomalyLog.Info("Fate", $"FATE 周回を止めました: {reason}（完了 {this.Completed} 件）");
    }

    // ---- 交換のための中断と復帰（FateEarner から呼ばれる） ----

    /// <summary>
    /// この環境で周回を使えるか。
    ///
    /// 周回には BossMod Reborn と vnavmesh の両方が要る。
    /// 片方でも欠けていれば、稼ぎ方の一覧にも出さない。
    /// </summary>
    public bool IsUsable => this.bossMod.IsLoaded && this.navigation.IsAvailable;

    /// <summary>いま FATE の中で戦っているか。中断してよい切れ目かの判断に使う。</summary>
    /// <remarks>
    /// 着地の途中も含める。空中で中断されると、降りきらないまま
    /// 放り出されて空に取り残される。
    /// </remarks>
    public bool IsInFate => this.Step is FateStep.Fighting or FateStep.Leaving or FateStep.Landing;

    /// <summary>
    /// 納品 FATE の報酬を待っているか。
    ///
    /// 待っている間にエリアを離れると報酬が消える。
    /// 交換のための中断もこの間は見送る。
    /// </summary>
    public bool HasPendingReward => this.pendingReward is not null;

    /// <summary>
    /// 交換のために一時的に止める。
    ///
    /// <see cref="Stop"/> と違い、<see cref="StoppedReason"/> を
    /// 「利用者が止めた」とは扱わない。戻すのは FateEarner の役目。
    /// </summary>
    public void Suspend(string reason)
    {
        if (!this.IsRunning)
        {
            return;
        }

        this.Stop(reason);
    }

    /// <summary>
    /// 中断していた周回を戻す。
    ///
    /// エリアが違っていれば、そのエリアへ向かうところから始める。
    /// 座標まで戻すかどうかは設定で決まる（既定は戻さない）。
    /// 着いてしまえば、いちばん近い FATE から回り直すので支障はない。
    /// </summary>
    public bool Resume(uint territoryId, Vector3 position, out string reason)
    {
        if (this.IsRunning)
        {
            reason = string.Empty;
            return true;
        }

        if (!this.Start(out reason))
        {
            return false;
        }

        // 戻る先が選択に含まれていれば、そこから再開する。
        var index = Plugin.C.FateZones.IndexOf(territoryId);
        if (index >= 0)
        {
            this.zoneIndex = index;
        }

        // 座標まで戻すかは設定次第。エリアへは必ず戻るので、
        // 入れていなくても、いちばん近い FATE から回り直せる。
        this.resumePosition = null;

        if (Plugin.C.FateReturnToExactSpot
            && position != Vector3.Zero
            && Svc.ClientState.TerritoryType == territoryId
            && this.navigation.BeginMove(position, 10f, out _))
        {
            this.resumePosition = position;
        }

        return true;
    }

    /// <summary>毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        if (!this.IsRunning)
        {
            return;
        }

        try
        {
            this.TickCore();
        }
        catch (Exception ex)
        {
            // 1 回の例外で周回ごと落とさない。記録して次のフレームで続ける。
            this.anomalyLog.Warn("Fate", $"処理中に例外が出ました: {ex.Message}");
        }
    }

    private void TickCore()
    {
        var cfg = C;

        // 報酬待ちの状態はどの段階でも更新する。マップを離れてよいかの判断に使う。
        this.RefreshPendingReward();

        // 飛んでいるあいだ、届いた高さを覚える。
        // そのマップで飛べる高さの上限を知る手段が他に無い。
        MountService.ObserveCeiling();

        // 見送りの期限が切れた FATE を戻す。
        this.ExpireBlacklist();

        // 1. 自動操作が成立しない状況では何もしない。
        //    戦闘・詠唱・動作中は FATE 周回では正常なので弾かれない。
        if (!SafetyGuard.IsSafeToRunFate(out var blockReason, out _))
        {
            this.StatusDetail = blockReason;
            return;
        }

        // 2. 戦闘不能。
        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            this.TickDead(cfg);
            return;
        }

        // **どの段階でも、動けなくなっていないかを見張る。**
        //
        // 詰まりの検出は FATE へ向かっている最中にしか置いていなかった。
        // そのため着地の途中や、敵へ近づいている最中に地形へ挟まると、
        // 誰も気づかないまま止まり続けていた。
        // 段階に関係なく見張り、抜け出せなければ帰還まで持っていく。
        if (this.TickStuckWatchdog())
        {
            return;
        }

        if (this.Step == FateStep.Dead)
        {
            // 生き返った。
            this.deadSinceUtc = DateTime.MinValue;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
        }

        // 3. バディの面倒を見る。戦闘外でだけ動く。
        if (cfg.FateBuddyEnabled && this.Step is FateStep.Waiting or FateStep.MovingToFate)
        {
            if (this.buddy.Tick(cfg.FateBuddyMinSecondsRemaining, cfg.FateGysahlMinCount))
            {
                this.StatusDetail = this.buddy.StatusDetail;
                return;
            }
        }

        // 4. 周回するマップにいるか。
        //
        // **「一覧に載っているマップにいるか」だけで判断しない。**
        //
        // 一覧のマップはどれも載っているので、FATE が尽きて次のマップへ
        // 移ろうと決めた直後も「いま載っているマップにいる」が成立する。
        // そのため移動の段階が次のフレームで取り消され、
        // テレポが一度も実行されなかった（2026-09-25 実測。
        // Traveling に入って 18 ミリ秒で Waiting に戻っていた）。
        //
        // 行き先を決めているあいだは、そちらへ着くまで移動を続ける。
        var inZone = cfg.FateZones.Contains(Svc.ClientState.TerritoryType);
        var heading = this.travelTargetTerritory != 0
                   && this.travelTargetTerritory != Svc.ClientState.TerritoryType;

        if (!inZone || heading)
        {
            this.TickTraveling(cfg);
            return;
        }

        // 行き先に着いた。移動状態は畳む。
        if (this.Step == FateStep.Traveling)
        {
            this.teleportStartedUtc = DateTime.MinValue;
            this.travelTargetTerritory = 0;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
        }

        // 交換から戻ったときの座標へ寄る。
        //
        // **FATE があればそちらを優先する。**
        // 戻る途中で FATE が湧いていたら、わざわざ元の場所まで行く意味がない。
        // ここで足を止めるのは、狙える FATE が無いあいだだけ。
        if (this.resumePosition is { } spot)
        {
            if (this.PickNext(cfg) is not null || !Player.Available)
            {
                this.resumePosition = null;
            }
            else if (Vector3.Distance(Player.Position, spot) < 15f)
            {
                this.resumePosition = null;
                this.navigation.Stop();
            }
            else
            {
                this.StatusDetail = "交換前にいた場所へ戻っています";
                var status = this.navigation.Tick(spot, 10f);
                if (status is MoveStatus.Arrived or MoveStatus.ShortOfTarget or MoveStatus.Stuck or MoveStatus.Failed)
                {
                    this.resumePosition = null;
                    this.navigation.Stop();
                }
                else if (status == MoveStatus.Moving)
                {
                    return;
                }
            }
        }

        // **降りている最中はここで完結させる。**
        // この下には vnavmesh を触る処理がある。降下中に触ると、
        // ゲームの降下がプレイヤーの操作として読まれ、経路が捨てられる。
        if (this.Step == FateStep.Landing)
        {
            this.TickLanding(cfg);
            return;
        }

        // 5. いま参加している FATE があるか。
        //
        // **もう離れた FATE は見ない。**
        // 円の中に立っている限り CurrentFate はその FATE を指したままなので、
        // これが無いと 100% の FATE を毎フレーム拾い直し、
        // 離脱と経路引きを往復して一歩も動けなくなる。
        var current = this.scanner.GetCurrent();

        if (this.leftFateId is { } left)
        {
            if (current is not null && current.Id == left)
            {
                // まだその FATE の円の中にいる。見なかったことにする。
                current = null;
            }
            else
            {
                // 円から出た。もう拾われないので、印は要らない。
                // ここで消さないと、同じ番号の FATE が後から湧いたときに
                // 二度と入れなくなる。
                this.leftFateId = null;
            }
        }

        if (current is not null && current.State == FateState.Running)
        {
            this.TickInFate(cfg, current);
            return;
        }

        // 参加していないのに Fighting のままなら、FATE が終わったか離れた。
        if (this.Step is FateStep.Fighting or FateStep.Leaving)
        {
            this.FinishCurrentFate();
        }

        // 6. 狙う FATE を決めて向かう。
        this.TickSeek(cfg);
    }

    // ---- 各段階 ----

    private void TickDead(Config cfg)
    {
        if (this.Step != FateStep.Dead)
        {
            this.ReleaseCombat();
            this.navigation.Stop();
            this.deadSinceUtc = DateTime.UtcNow;
            this.SetStep(FateStep.Dead, "戦闘不能です");
            this.anomalyLog.Info("Fate", "戦闘不能になりました");
        }

        var action = cfg.FateDeathAction;
        if (action == FateDeathAction.Auto)
        {
            action = Svc.Party.Length == 0 ? FateDeathAction.Return : FateDeathAction.Wait;
        }

        if (action == FateDeathAction.Wait)
        {
            var waited = DateTime.UtcNow - this.deadSinceUtc;
            var remaining = cfg.FateRaiseWaitSeconds - (int)waited.TotalSeconds;
            if (remaining > 0)
            {
                this.StatusDetail = $"レイズを待っています（残り {remaining} 秒）";
                return;
            }

            this.anomalyLog.Info("Fate", $"{cfg.FateRaiseWaitSeconds} 秒待ちましたがレイズされませんでした。街へ戻ります");
        }

        this.StatusDetail = "街へ戻っています";
        ReturnHome();
    }

    private void TickTraveling(Config cfg)
    {
        var destination = cfg.FateZones[Math.Clamp(this.zoneIndex, 0, cfg.FateZones.Count - 1)];

        // 納品 FATE の報酬を待っている間はマップを離れない。離れると報酬が消える。
        if (this.pendingReward is not null)
        {
            this.StatusDetail = "納品 FATE の報酬を待っています";
            return;
        }

        // 行き先が決まっていなければ、ここで決める。
        // （周回外のマップに居るときは、この経路で戻ってくる）
        if (this.Step != FateStep.Traveling || this.travelTargetTerritory != destination)
        {
            this.travelTargetTerritory = destination;
            this.teleportStartedUtc = DateTime.UtcNow;
            this.teleportIssued = false;
            this.SetStep(FateStep.Traveling, $"{NpcLocationService.GetTerritoryName(destination)} へ移動しています");
        }

        // **テレポは 1 度だけ撃つ。**
        // 毎フレーム撃つと、詠唱が始まるたびに撃ち直して一生飛べない。
        if (!this.teleportIssued)
        {
            this.teleportIssued = true;

            if (!this.TryTeleportTo(destination))
            {
                this.anomalyLog.Warn("Fate", $"{NpcLocationService.GetTerritoryName(destination)} へテレポートできませんでした");
                this.AdvanceZone(cfg);
                return;
            }

            this.trace.Decision("テレポを撃った", NpcLocationService.GetTerritoryName(destination));
            return;
        }

        this.StatusDetail = $"{NpcLocationService.GetTerritoryName(destination)} へ移動しています";

        if (DateTime.UtcNow - this.teleportStartedUtc > TeleportTimeout)
        {
            this.anomalyLog.Warn("Fate", $"{NpcLocationService.GetTerritoryName(destination)} へのテレポートが終わりませんでした");
            this.teleportStartedUtc = DateTime.MinValue;
            this.AdvanceZone(cfg);
        }
    }

    private void TickSeek(Config cfg)
    {
        // 移動中なら続ける。
        if (this.Step == FateStep.MovingToFate && this.target is not null)
        {
            this.TickMoving(cfg);
            return;
        }

        var next = this.prefetched ?? this.PickNext(cfg);
        this.prefetched = null;

        if (next is null)
        {
            this.TickWaiting(cfg);
            return;
        }

        this.BeginMoveTo(next);
    }

    private void TickWaiting(Config cfg)
    {
        if (this.Step != FateStep.Waiting)
        {
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
        }

        // 報酬待ちの間はマップを離れない。
        if (this.pendingReward is not null)
        {
            this.StatusDetail = "納品 FATE の報酬を待っています";
            return;
        }

        if (!cfg.FateSwapZoneWhenEmpty || cfg.FateZones.Count <= 1)
        {
            this.StatusDetail = "FATE が湧くのを待っています";
            return;
        }

        var waited = (int)(DateTime.UtcNow - this.waitingSinceUtc).TotalSeconds;
        var remaining = cfg.FateZoneSwapWaitSeconds - waited;

        if (remaining > 0)
        {
            this.StatusDetail = $"FATE が湧くのを待っています（{remaining} 秒後に次のマップへ）";
            return;
        }

        this.AdvanceZone(cfg);
    }

    /// <summary>
    /// FATE へ向かい始める。
    ///
    /// <b>移動そのものは次のフレーム以降に始める。</b>
    /// 先にマウントへ乗る必要があり、乗るには数フレームかかる。
    /// ここで経路を引いてしまうと、乗る前に走り出してしまう。
    /// </summary>
    private void BeginMoveTo(FateInfo fate)
    {
        this.target = fate;
        this.moveStartedUtc = DateTime.UtcNow;
        this.moveIssued = false;

        // 詰まり判定をやり直す。前の移動の記録を引き継がない。
        this.blockCheckedUtc = DateTime.MinValue;
        this.blockDetours = 0;
        this.detourTask = null;
        this.escapeAttempts = 0;
        this.watchdogSince = DateTime.MinValue;
        this.RestorePathTolerance();

        // **離れた FATE の印は、ここで消さない。**
        //
        // 消していたため、次の FATE へ向かうと決めた瞬間に
        // さっき終えた FATE がまた「参加中」として拾われ、
        // 100% なので離脱、また次を決める、を繰り返していた。
        //
        // 利用者からは、終わった FATE の場所へわざわざ飛んで戻り、
        // 不自然に動いてから次へ向かうように見える（2026-09-25 実測）。
        //
        // 印は、その FATE の円から出たときに消す（TickCore を参照）。

        // 別の FATE へ向かうので、降りる途中だった記録は捨てる。
        // 残すと二度と飛ばなくなる。
        this.mount.ClearDismounting();

        this.trace.Decision(
            "次の FATE へ",
            $"{fate.Name} 進捗{fate.Progress}% {FateTrace.DescribeDistance(fate.Position)}");
        this.SetStep(FateStep.MovingToFate, $"{fate.Name} へ向かっています");
    }

    private void TickMoving(Config cfg)
    {
        var fate = this.target!;

        // 狙っていた FATE が消えた・終わった。
        var live = this.scanner.GetById(fate.Id);
        if (live is null || live.State != FateState.Running || live.Progress >= 100)
        {
            this.navigation.Stop();
            this.target = null;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        var range = Math.Max(1f, live.Radius - FateArrivalSlack);

        // **着いていたら、乗る判断より先に降りる。**
        //
        // 先に乗る判断をすると、着いた直後に降りて、また乗って、を繰り返す。
        //
        // 高さは見ない。飛んでいる最中は真上に数十メートルの差があり、
        // そのまま測ると「まだ遠い」と判断してしまう。
        // 飛んでいるあいだは中心付近まで、歩きなら円に入れば着いたとみなす。
        var arriveWithin = this.flyingWhenIssued ? LandNearCentreMeters : range + FateArrivalSlack;
        var arrived = Player.Available
                   && Vector2.Distance(
                          new Vector2(Player.Position.X, Player.Position.Z),
                          new Vector2(live.Position.X, live.Position.Z)) <= arriveWithin;

        if (!arrived)
        {
            // 先にマウントへ乗る。遠い FATE へ歩いて向かうと、着く前に終わる。
            // 飛べるエリアなら飛び上がるところまで面倒を見る。
            if (this.mount.TickPrepare(live.Position))
            {
                this.StatusDetail = $"{live.Name} へ向かう準備をしています";
                return;
            }

            // **乗る前に地上の経路を引かない。**
            //
            // 乗る動作には 1 秒ほどかかる。その間に経路を引くと地上の経路になり、
            // 乗り終わっても地面を走り続けることになる。
            // まだ乗っておらず、これから乗って飛べるのなら、経路は引かずに待つ。
            //
            // GatherBuddyReborn も既定ではこうしている
            // （AutoGather.Movement.cs の Navigate: 乗ると決めたら
            // MoveWhileMounting が無ければ StopNavigation して抜ける）。
            //
            // ただし待ち続けない。乗れない事情（乗騎が解放されていない、
            // 直前の戦闘で行動が塞がれている等）があるなら、地上で向かう。
            if (!this.moveIssued
                && !MountService.IsMounted
                && MountService.CanFlyHere
                && DateTime.UtcNow - this.moveStartedUtc < MountWaitBeforeGroundPath)
            {
                this.StatusDetail = $"{live.Name} へ飛ぶ準備をしています";
                this.trace.State("経路を待つ", "乗ってから飛ぶ経路を引く");
                return;
            }
        }

        // **飛行が解けたら経路を引き直す。**
        //
        // 地面に触れると飛行が解ける。そのまま走って向かうと遅いので、
        // 乗り直し・飛び直しのあとに経路も引き直す。
        if (this.moveIssued && this.flyingWhenIssued && !MountService.IsFlying)
        {
            this.navigation.Stop();
            this.moveIssued = false;
        }

        // **乗れたら経路を引き直す。**
        //
        // 乗るには 1 秒ほどかかる。その間に地上の経路を引いてしまうと、
        // 乗り終わってもそのまま地面を走り続ける。引き直す口がここに
        // 無かったため、一度地上で引かれたら二度と飛ばなかった
        // （2026-09-25 実測。48.615 に 騎乗=False で地上の経路を引き、
        // 49.232 に乗れたが、最後まで地上のままだった）。
        if (this.moveIssued && !this.flyingWhenIssued && MountService.IsMounted && MountService.CanFlyHere)
        {
            this.trace.Decision("経路を引き直す", "乗れたので飛ぶ経路にする");
            this.navigation.Stop();
            this.moveIssued = false;
        }

        // 乗ったので経路を引く。飛べる状態なら飛ぶ経路になる。
        if (!this.moveIssued)
        {
            // **まだ飛んでいなくても、飛ぶつもりなら fly: true で引く。**
            //
            // vnavmesh は「次の経路点が自分より高い」「騎乗中」
            // 「まだ飛んでいない」「ignoreDeltaY でない」の 4 つが揃ったとき
            // 自分でジャンプを連打して離陸する
            // （FollowPath.cs:144）。ignoreDeltaY は fly の反転なので、
            // 飛んでから fly: true にしたのでは離陸してくれない。
            //
            // 乗っていて、そのエリアで飛べるなら、飛ぶつもりで引く。
            var flying = MountService.IsMounted && MountService.CanFlyHere;

            // **敵が見えているなら、敵を目指す。**
            //
            // FATE の中心は、ただの円の中心であって敵が居る場所とは限らない。
            // 中心の上空まで飛んでから真下へ降りると、降りるあいだ
            // 空に浮いたままになり、不自然に見える。
            //
            // 敵が見えていれば、そこは必ず立てる場所で、しかも降りた先が
            // そのまま戦う場所になる。FATE の敵は FateId で見分けられる。
            var spotted = this.scanner.FindNearestMob(live.Id, Player.Available ? Player.Position : live.Position);
            var aimedAtMob = spotted is not null;

            var ground = spotted is { } mob
                ? mob.Position
                : this.ResolveLandablePoint(live);

            // 目的地を持ち上げる。これが離陸の条件（次の点が自分より高い）
            // を満たすことにもなり、経路が地面を擦るのも防ぐ。
            //
            // 飛べる高さの上限は LiftForFlight が抑える。
            var destination = flying
                ? MountService.LiftForFlight(ground)
                : ground;

            // **経路は最後まで辿らせる。**
            //
            // ここで渡す range は、vnavmesh では DestinationTolerance になる
            // （AsyncMoveRequest.MoveTo → FollowPath）。これが 0 より大きいと、
            // 目的地からその距離まで近づいた時点で<b>残りの経路点を全部捨てる</b>
            // （FollowPath.cs:73）。
            //
            // 以前は飛行時に 15m を渡していた。そのため入り組んだ地形では、
            // せっかく引けた「通路を抜ける経路」の最後の部分が捨てられ、
            // 手前で止まって空に浮いたままになっていた。
            //
            // 狭い通路でも、経路を最後まで辿れば抜けられる。
            // 着いたかどうかはこちらで測っているので、ここでは切り上げない。
            var moveRange = flying ? 0f : range;

            if (!this.navigation.BeginMove(destination, moveRange, flying, out var failure))
            {
                // **経路探索の最中なら、ただ待つ。**
                // これは失敗ではないので、辿り着けなかったと数えない。
                // 数えていたため、向かい始めた直後に 2 回断られただけで
                // その FATE を候補から外していた（2026-09-25 実測）。
                if (this.navigation.Busy)
                {
                    this.StatusDetail = $"{live.Name} へ向かう経路を待っています";
                    return;
                }

                this.anomalyLog.Warn("Fate", $"{live.Name} へ移動できませんでした: {failure}");
                this.MarkStuck(live.Id);
                this.target = null;
                return;
            }

            // 何を頼んだかを残す。飛ばないときに、条件のどれが
            // 欠けているのかをここから辿れる。
            this.trace.Decision(
                "経路を引いた",
                $"{live.Name} fly={flying} " +
                $"騎乗={MountService.IsMounted} 飛行={MountService.IsFlying} " +
                $"{MountService.DescribeFlightStatus()} " +
                $"目的地の高さ={destination.Y:F0}（本来{live.Position.Y:F0}） " +
                $"自分の高さ={(Player.Available ? Player.Position.Y : 0):F0} " +
                $"飛べる高さ={(MountService.KnownCeiling is { } c ? $"{c:F0}" : "未確認")} " +
                $"狙い={(aimedAtMob ? "敵" : "中心")}");

            this.moveIssued = true;
            this.flyingWhenIssued = flying;
            this.moveStartedUtc = DateTime.UtcNow;
        }

        // **敵の近くまで来たら降りる。**
        //
        // 敵が見えていれば、そこを目安にする。降りた先がそのまま戦う場所になり、
        // 降りてから走る距離も無くなる。
        //
        // 見えていなければ中心を目安にする。円に入った端で降りると、
        // そこが低い場所だと高所の敵へ近づけなくなるため
        // （2026-09-25 実測。高低差のあるマップで棒立ちになった）。
        if (this.flyingWhenIssued && Player.Available)
        {
            var landAt = this.scanner.FindNearestMob(live.Id, Player.Position) is { } near
                ? near.Position
                : live.Position;

            var flat = Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(landAt.X, landAt.Z));

            if (flat <= LandNearCentreMeters)
            {
                this.navigation.Stop();
                this.EnterFate(cfg, live);
                return;
            }
        }

        var status = this.navigation.Tick(
            this.flyingWhenIssued ? MountService.LiftForFlight(live.Position) : live.Position,
            this.flyingWhenIssued ? MountService.FlightLift : range);

        switch (status)
        {
            case MoveStatus.Arrived:
            case MoveStatus.ShortOfTarget:
                // 近くまで来た。圏内に入っていれば戦い始める。
                this.EnterFate(cfg, live);
                return;

            case MoveStatus.Stuck:
            case MoveStatus.Failed:
                this.anomalyLog.Warn("Fate", $"{live.Name} へ辿り着けませんでした（{status}）");
                this.navigation.Stop();
                this.MarkStuck(live.Id);
                this.target = null;
                return;

            default:
                if (DateTime.UtcNow - this.moveStartedUtc > MoveTimeout)
                {
                    this.anomalyLog.Warn("Fate", $"{live.Name} への移動に時間がかかりすぎました");
                    this.navigation.Stop();
                    this.MarkStuck(live.Id);
                    this.target = null;
                    return;
                }

                // 進まなくなっていないかを見る。壁に押し付けられている場合がある。
                this.CheckBlocked(
                    live,
                    this.flyingWhenIssued ? MountService.LiftForFlight(live.Position) : live.Position);

                this.StatusDetail = $"{live.Name} へ向かっています（{live.Progress}%）";
                this.trace.State(
                    "移動中",
                    $"{live.Name} {live.Progress}% {FateTrace.DescribeDistance(live.Position)} " +
                    $"経路={(this.flyingWhenIssued ? "飛行" : "地上")}");
                return;
        }
    }

    /// <summary>
    /// 進まなくなっていないかを見て、詰まっていれば避けて引き直す。
    ///
    /// <b>経路そのものは地形を貫いていない。</b>
    /// vnavmesh は飛行時に voxel の空間（PathfindVolume）で探すため、
    /// 岩や建物の内側を通る経路は引かない。
    ///
    /// それでも壁に張り付くのは、目的地を 30m 持ち上げているせい。
    /// 持ち上げた先が崖や岩の内側に入ると、そこへ行こうとして
    /// 手前の面に押し付けられる。実際、目的地の高さ 27（本来 -3）で
    /// 1121m まで詰めたあと 1156m まで押し戻されていた（2026-09-25）。
    ///
    /// 進んでいないと分かったら、いまの場所を避ける球として指定し、
    /// 迂回する経路を引き直す。
    /// </summary>
    private void CheckBlocked(FateInfo fate, Vector3 destination)
    {
        if (!Player.Available)
        {
            return;
        }

        // 頼んでおいた迂回の経路が出来ていたら、それを積む。
        if (this.detourTask is { IsCompleted: true } done)
        {
            this.detourTask = null;

            if (done.IsCompletedSuccessfully && done.Result is { Count: > 0 } points)
            {
                this.vnavmesh.TryMoveAlong(points, true);
                this.trace.Decision("迂回の経路を積んだ", $"経路点 {points.Count} 個");

                // 積み直したので、進み具合の基準も取り直す。
                this.blockCheckedUtc = DateTime.UtcNow;
                this.blockCheckPosition = Player.Position;
                return;
            }

            this.trace.Trouble("迂回の経路が引けなかった", $"{fate.Name} へ普通に引き直します");
            this.navigation.Stop();
            this.moveIssued = false;
            return;
        }

        // 頼んだ経路を待っている間は、二重に頼まない。
        if (this.detourTask is not null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var here = Player.Position;

        // **「経路を探している」を理由に見送らない。**
        //
        // vnavmesh は詰まると自分で経路を引き直す
        // （AsyncMoveRequest の OnStuck → MoveTo。設定 RetryOnStuck が既定で有効）。
        // そのため壁に当たっている間こそ「探している」が立ち続ける。
        // これで見送っていたら、詰まりを一度も検出できなかった
        // （2026-09-25 実測。探している 2305 回に対し、検出は 1 回だけ）。
        //
        // 経路がまだ 1 本も無いあいだ（出発前）だけ待つ。
        // 経路を持っているなら、進んでいるかどうかで判断してよい。
        if (this.vnavmesh.TryNumWaypoints(out var waypoints) && waypoints == 0)
        {
            this.blockCheckedUtc = now;
            this.blockCheckPosition = here;
            this.trace.State("経路を待っている", $"{fate.Name} まで {FateTrace.DescribeDistance(destination)}");
            return;
        }

        // 初めて見るときは基準を置くだけ。
        if (this.blockCheckedUtc == DateTime.MinValue)
        {
            this.blockCheckedUtc = now;
            this.blockCheckPosition = here;
            return;
        }

        if (now - this.blockCheckedUtc < BlockCheckInterval)
        {
            return;
        }

        var advanced = Vector3.Distance(here, this.blockCheckPosition);
        this.blockCheckedUtc = now;
        this.blockCheckPosition = here;

        // 進んでいるなら何もしない。
        if (advanced >= BlockProgressMeters)
        {
            this.blockDetours = 0;
            return;
        }

        // 迂回で駄目なら、経路を捨ててその場から力づくで離れる。
        if (this.blockDetours >= MaxDetours)
        {
            this.EscapeStuckSpot(fate, now, here);
            return;
        }

        this.blockDetours++;

        // **まず経路をなぞらせ、引き直させる。**
        //
        // vnavmesh は経路から Tolerance（既定 0.25）まで外れてよい作りで、
        // 曲がり角を端折って進む。狭い通路ではそれが壁への接触になる。
        // 引っかかったら、まず許容値を詰めて経路をなぞらせる。
        // 通路そのものは通れるので、なぞれば抜けられることが多い。
        //
        // あわせて経路を捨てる。次のフレームで、いまの場所から引き直される。
        // 壁に寄った状態から引き直すと、別の抜け道が見つかることが多い。
        // ICE も詰まったときは「経路を止めて引き直させる」だけで抜けている
        // （逆コンパイルして確認。CheckIfIsStuck の RetargetIfStuck）。
        if (this.blockDetours == 1)
        {
            if (!this.tightenedPath)
            {
                this.tightenedPath = true;
                this.vnavmesh.TrySetPathTolerance(TightPathTolerance);
            }

            this.trace.Decision(
                "経路をなぞらせて引き直す",
                $"許容値 {TightPathTolerance:0.00}／{advanced:F1}m しか進めず");

            this.navigation.Stop();
            this.moveIssued = false;

            // 跳ねてみる。段差に引っかかっているだけなら、これで外れる。
            TryJump();

            this.blockCheckedUtc = now;
            this.blockCheckPosition = here;
            return;
        }

        // いる場所を中心に、その周りを避けて引き直す。
        // 半径は詰まりを抜け出せる程度に取る。大きすぎると経路が見つからない。
        var radius = DetourRadiusMeters * this.blockDetours;

        this.trace.Decision(
            "遮蔽物を避けて引き直す",
            $"{fate.Name} {BlockCheckInterval.TotalSeconds:F0}秒で {advanced:F1}m しか進めず " +
            $"（{this.blockDetours} 回目・半径 {radius:F0}m）");

        // **避ける球は、進もうとしている先に置く。**
        //
        // いる場所を中心にすると、そこから出る経路まで避けてしまい、
        // 「どこへも行けない」経路しか引けないことがある。
        // ぶつかっているのは進行方向の先なので、そちらに置く。
        var forward = Vector3.Normalize(new Vector3(
            destination.X - here.X,
            0f,
            destination.Z - here.Z));

        var avoidAt = float.IsNaN(forward.X)
            ? here
            : here + (forward * radius);

        // **経路探索は非同期。**その場では結果が出ない。
        // 頼んでおいて、出来たら積む。出来るまでは今の経路のまま進む。
        if (this.vnavmesh.TryPathfindAvoid(here, destination, true, avoidAt, radius, out var task) &&
            task is not null)
        {
            this.detourTask = task;
            this.trace.Decision("迂回の経路を頼んだ", $"半径 {radius:F0}m");
            return;
        }

        // 頼めなかった。いったん止めて普通に引き直させる。
        this.trace.Trouble("迂回の経路が頼めない", $"{fate.Name} へ普通に引き直します");
        this.navigation.Stop();
        this.moveIssued = false;
    }

    /// <summary>
    /// 詰まった場所から力づくで離れる。
    ///
    /// <b>経路探索に頼らない。</b>
    /// 地形の内側や狭い隙間に入り込むと、そこを起点にした経路は
    /// どう引いても出口が無く、迂回もメッシュの引き直しも効かない。
    ///
    /// そこへ連れてきたのはこちらなので、こちらで出す。
    /// vnavmesh の Path.MoveTo は経路探索を通さず、渡した点へそのまま向かう。
    /// これで真上や斜め上へ動かし、詰まりから抜け出す。
    ///
    /// GatherBuddyReborn も同じ考え方で、詰まったときは
    /// ナビメッシュを介さず直接movementを奪って 25m 先へ動かしている
    /// （AutoGather/Helpers/AdvancedUnstuck.cs の Start）。
    /// </summary>
    private void EscapeStuckSpot(FateInfo? fate, DateTime now, Vector3 here)
    {
        this.escapeAttempts++;

        // **最後は帰還する。**
        //
        // 動かして抜けられないなら、座標ごと外へ出すしかない。
        // 帰還（ExecuteCommand 200/8）は経路も地形も高度も関係なく、
        // どこに居ても必ずホームポイントへ運んでくれる。
        // 入り組んだ地形に入り込んでしまったときの、確実な逃げ道。
        //
        // 手で助けてもらうことはしない。連れてきたのはこちらなので、
        // こちらで出す。
        if (this.escapeAttempts > MaxEscapeAttempts)
        {
            this.trace.Trouble(
                "動いて抜け出せない",
                $"{MaxEscapeAttempts} 回試しても動けないため、帰還して脱出します");

            this.navigation.Stop();
            this.vnavmesh.TryStop();
            this.moveIssued = false;
            if (fate is not null)
            {
                this.MarkStuck(fate.Id);
            }

            this.target = null;

            // 帰還は詠唱がある。終わるまで周回を進めない。
            this.SetStep(FateStep.Traveling, "詰まったため帰還しています");
            this.travelTargetTerritory = 0;
            this.teleportIssued = true;
            this.teleportStartedUtc = now;
            this.escapeAttempts = 0;

            ReturnHome();
            this.anomalyLog.Warn(
                "Fate",
                "地形から抜け出せなかったため帰還しました。周回は続けます");
            return;
        }

        // 積んである経路を捨てる。残っていると、また同じ壁へ押し付けられる。
        this.navigation.Stop();
        this.moveIssued = false;

        // **逃げ先はメッシュに聞く。**
        //
        // やみくもに動かすと、また壁の中へ突っ込むことがある。
        // ナビメッシュに載っている点は、そこに立てることが保証されている。
        // まず真下の床、次に周りの立てる場所を探し、見つかればそこへ。
        //
        // 上は当てにしない。飛行には高度の上限があり、そこに張り付いていると
        // 上へ向かわせても 1m も上がらない（2026-09-25 実測。Y=58 で頭打ち。
        // ゲームが「高度上限付近です」と出していた）。
        Vector3? found = null;

        if (this.vnavmesh.TryIsReady(out var ready) && ready)
        {
            // 真下の床。入り組んだ地形でも、下は空いていることが多い。
            if (this.vnavmesh.TryPointOnFloor(here, out var floor) && floor is not null)
            {
                found = floor;
            }
            else
            {
                // 探す範囲を、試行のたびに広げる。
                var reach = 30f * this.escapeAttempts;

                if (this.vnavmesh.TryNearestPointReachable(here, reach, 200f, out var near) && near is not null)
                {
                    found = near;
                }
                else if (this.vnavmesh.TryNearestPoint(here, reach * 2f, 200f, out var any) && any is not null)
                {
                    found = any;
                }
            }
        }

        // メッシュから答えが出なければ、下と横へ振る。
        // 向きは黄金角でずらし、毎回ちがう方向へ出る。
        var angle = this.escapeAttempts * 2.39996f;
        var spread = 15f * this.escapeAttempts;

        var away = found ?? new Vector3(
            here.X + (MathF.Cos(angle) * spread),
            here.Y - (15f * this.escapeAttempts),
            here.Z + (MathF.Sin(angle) * spread));

        this.trace.Trouble(
            "詰まった場所から離れる",
            $"{fate?.Name ?? "移動"} ({here.X:F0},{here.Y:F0},{here.Z:F0}) → " +
            $"({away.X:F0},{away.Y:F0},{away.Z:F0}) {this.escapeAttempts} 回目 " +
            $"{(found is null ? "（当て推量）" : "（メッシュ上の点）")}");

        // 経路探索を通さずに、その点へ直接向かわせる。
        this.vnavmesh.TryMoveAlong([away], true);

        // 動く時間を与えてから測り直す。
        this.blockCheckedUtc = now + EscapeSettleTime;
        this.blockCheckPosition = here;
        this.blockDetours = 0;
        this.detourTask = null;
    }

    /// <summary>
    /// FATE に着いた。降りる段階へ移る。
    ///
    /// <b>ここで経路を止め、以後 vnavmesh に触らない。</b>
    /// 降下はゲーム側の動きで、それがプレイヤーの操作として
    /// vnavmesh に読まれる。触り続けると経路を積んでは捨てるを
    /// 繰り返し、着地したあとも暴れて降りられない。
    /// </summary>
    private void EnterFate(Config cfg, FateInfo fate)
    {
        this.navigation.Stop();
        this.moveIssued = false;
        this.target = fate;

        // 乗っていなければ降りる必要がない。そのまま戦う。
        if (!MountService.IsMounted)
        {
            this.ApplyCombat(cfg);
            this.SetStep(FateStep.Fighting, $"{fate.Name} と戦っています");
            return;
        }

        this.landingSinceUtc = DateTime.UtcNow;
        this.landingRefuge = null;
        this.SetStep(FateStep.Landing, $"{fate.Name} に着きました（降りています）");
    }

    /// <summary>
    /// 降りきるのを待つ。
    ///
    /// <b>vnavmesh を呼ばない。</b>降下中に経路を積むと、ゲームの降下が
    /// プレイヤーの操作として読まれ、積んだ経路がその場で捨てられる。
    /// これを繰り返すのが「暴れる」正体だった（2026-09-25 実測）。
    /// </summary>
    private void TickLanding(Config cfg)
    {
        if (this.target is not { } fate)
        {
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        // 降りられた。戦いに入る。
        if (!MountService.IsMounted)
        {
            this.mount.ClearDismounting();
            this.ApplyCombat(cfg);
            this.SetStep(FateStep.Fighting, $"{fate.Name} と戦っています");
            return;
        }

        // 降りられないまま時間が過ぎた。
        if (DateTime.UtcNow - this.landingSinceUtc > LandingTimeout)
        {
            this.trace.Trouble("着地できない", $"{LandingTimeout.TotalSeconds:F0}秒たっても降りられませんでした");
            this.mount.ClearDismounting();

            // **その場に浮いたまま放り出さない。**
            //
            // 水面の上など、降りられない場所で諦めると、空中に止まったまま
            // 次の判断へ進むことになる。そこから次の FATE を目指しても、
            // 出発点が宙に浮いたままなので経路が引けないことがある。
            //
            // 立てる場所を聞いて、そこまで飛んでから降りる。
            // 立てる場所を探す。空にいるので、まず真下の床を見る。
            // 見つからなければ、周りを広く探す。
            Vector3? refuge = null;

            if (Player.Available && this.vnavmesh.TryIsReady(out var meshReady) && meshReady)
            {
                if (this.vnavmesh.TryPointOnFloor(Player.Position, out var below) && below is not null)
                {
                    refuge = below;
                }
                else if (this.vnavmesh.TryNearestPointReachable(Player.Position, 50f, 200f, out var near) && near is not null)
                {
                    refuge = near;
                }
                else if (this.vnavmesh.TryNearestPoint(Player.Position, 100f, 200f, out var any) && any is not null)
                {
                    refuge = any;
                }
            }

            if (refuge is { } spot)
            {
                this.trace.Decision(
                    "降りられる所まで移る",
                    $"({Player.Position.X:F0},{Player.Position.Y:F0},{Player.Position.Z:F0}) → " +
                    $"({spot.X:F0},{spot.Y:F0},{spot.Z:F0})");

                // **経路探索を通さずに向かう。**
                //
                // 降りられない場所は、たいてい経路も引けない場所でもある。
                // BeginMove で頼むと経路が見つからず、その場に浮いたままになる。
                // Path.MoveTo は渡した点へそのまま向かうので、ここを抜けられる。
                //
                // 途中で引っかからないよう、いったん上へ出てから向かう。
                var overhead = Player.Position with { Y = Player.Position.Y + 20f };
                this.vnavmesh.TryMoveAlong([overhead, MountService.LiftForFlight(spot), spot], true);

                this.landingSinceUtc = DateTime.UtcNow;
                this.landingRefuge = spot;
                return;
            }

            // 立てる場所すら分からない。力づくで上へ出てから考え直す。
            if (Player.Available)
            {
                var up = Player.Position with { Y = Player.Position.Y + 30f };
                this.trace.Trouble("降りられる所が無い", $"いったん上へ出ます ({up.Y:F0})");
                this.vnavmesh.TryMoveAlong([up], true);
            }

            this.MarkStuck(fate.Id);
            this.target = null;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        // 立てる場所へ移っている最中は、着くまで降りようとしない。
        // 降下と移動が競合して、また同じ所で止まる。
        if (this.landingRefuge is { } moving)
        {
            var flat = Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(moving.X, moving.Z));

            if (flat > 5f)
            {
                this.trace.State("移動中（着地先）", $"あと {flat:F0}m");
                return;
            }

            this.navigation.Stop();
            this.landingRefuge = null;
        }

        this.mount.TickDismount();
        this.trace.State("着地", $"{fate.Name} {FateTrace.DescribeDistance(fate.Position)}");
    }

    private void TickInFate(Config cfg, FateInfo current)
    {
        // 達成度 100%。ここが最優先。待たずに離れる。
        if (current.Progress >= 100)
        {
            this.LeaveFate(cfg, current);
            return;
        }

        // **乗ったままでは戦えない。**
        //
        // FATE の円へ入った時点でここへ来るので、移動の段階を通らずに
        // 戦闘へ入ることがある。降りる処理は移動の側に置いてあったため、
        // 乗ったまま戦おうとして何もできなかった（2026-09-25 実測）。
        //
        // 降りるまでは戦闘を始めない。Landing はこの下にある
        // 「経路を触らない」段階へ移す。
        if (MountService.IsMounted)
        {
            this.target = current;

            // **飛んでいる最中は、中心の近くまで進んでから降りる。**
            //
            // 円に入った端で降りると、そこが低い場所だと高所の敵へ
            // 近づけない。移動の段階に戻して、中心まで飛ばせる。
            if (MountService.IsFlying && Player.Available)
            {
                var flat = Vector2.Distance(
                    new Vector2(Player.Position.X, Player.Position.Z),
                    new Vector2(current.Position.X, current.Position.Z));

                if (flat > LandNearCentreMeters)
                {
                    if (this.Step != FateStep.MovingToFate)
                    {
                        this.SetStep(FateStep.MovingToFate, $"{current.Name} の中心へ向かっています");
                    }

                    this.TickMoving(cfg);
                    return;
                }
            }

            // **敵が見えているなら、その高さまで降りる。**
            //
            // 中心に降りても、敵が崖の上にいれば近づけない。
            // 飛んでいるうちに敵の真上まで行けば、その高さに降りられる。
            if (MountService.IsFlying
                && this.scanner.FindNearestMob(current.Id, Player.Position) is { } mob
                && mob.Distance > MobReachMeters)
            {
                // 持ち上げは LiftForFlight に通す。自分で足すと、
                // 飛べる高さの上限を超えた点を目指してしまう。
                var above = MountService.LiftForFlight(mob.Position);

                // 引いた経路は積み直さない。積むたびに探索が走り、
                // そのあいだ進まない。
                if (this.Step != FateStep.MovingToFate)
                {
                    this.SetStep(FateStep.MovingToFate, $"{current.Name} の敵の上へ向かっています");

                    if (this.navigation.BeginMove(above, MobReachMeters, true, out _))
                    {
                        this.trace.Decision("敵の上へ飛ぶ", $"最寄りの敵まで {mob.Distance:F0}m");
                        this.moveIssued = true;
                        this.flyingWhenIssued = true;
                    }
                }
                else
                {
                    this.navigation.Tick(above, MobReachMeters);
                }

                return;
            }

            if (this.Step != FateStep.Landing)
            {
                this.landingSinceUtc = DateTime.UtcNow;
                this.landingRefuge = null;
                this.navigation.Stop();
                this.SetStep(FateStep.Landing, $"{current.Name} に入りました（降りています）");
            }

            return;
        }

        if (this.Step != FateStep.Fighting)
        {
            this.ApplyCombat(cfg);
            this.target = current;
            this.SetStep(FateStep.Fighting, $"{current.Name} と戦っています");
        }

        this.EnsurePresetActive(cfg);

        // 達成度が閾値を超えたら、次に向かう FATE を先に決めておく。
        // 100% を見てから探し始めると、その間その場に立ち尽くすことになる。
        if (this.prefetched is null && current.Progress >= cfg.FatePrefetchPct)
        {
            this.prefetched = this.PickNext(cfg, exclude: current.Id);
        }

        // **敵が遠ければ歩いて近づく。**
        //
        // BMR は見えている敵と戦うだけで、遠くの敵を探しには行かない。
        // 円の端に降りると、敵が遠くて棒立ちになる（2026-09-25 実測）。
        this.TickApproachMob(current);

        this.StatusDetail = $"{current.Name}（{current.Progress}%）";
        this.trace.State(
            "戦闘中",
            $"{current.Name} {current.Progress}% " +
            $"シンク={(this.scanner.IsPlayerSyncedToFate() ? "済" : "未")} " +
            $"プリセット={(this.bossMod.TryGetActivePreset(out var nowActive) ? nowActive ?? "なし" : "読めず")}");
    }

    /// <summary>
    /// 敵が遠ければ近づく。
    ///
    /// <b>近ければ何もしない。</b>BMR が戦っている最中に経路を積むと、
    /// 回避の動きと取り合いになる。
    /// </summary>
    private void TickApproachMob(FateInfo fate)
    {
        if (!Player.Available)
        {
            return;
        }

        // 戦闘に入っていれば BMR に任せる。近づく必要はない。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            if (this.approaching)
            {
                this.StopApproach();
                this.trace.Decision("近づくのをやめた", "戦闘に入った");
            }

            return;
        }

        // **納品 FATE の納品中は、こちらから動かさない。**
        //
        // BMR の FateUtils は、集めた品が 10 個たまると納品 NPC を狙い、
        // そこへ向けて移動を強制する（Hints.ForcedMovement）。
        // こちらが同時に経路を積むと、2 つの力が引っ張り合って
        // NPC の前でガクガク動き、何度も話しかけることになる
        // （2026-09-26 実測。「彼らの想いで」でアンロスト・セントリーGX に
        // 繰り返し話しかけていた）。
        //
        // 納品は BMR のほうが段取りを知っている。任せる。
        if (fate.IsCollect && this.scanner.HasHandInItems(fate.Id))
        {
            if (this.approaching)
            {
                this.StopApproach();
                this.trace.Decision("近づくのをやめた", "納品は BossMod Reborn に任せる");
            }

            return;
        }

        var nearest = this.scanner.FindNearestMob(fate.Id, Player.Position);

        if (nearest is not { } mob)
        {
            // 敵が 1 匹も見えない。FATE の中心へ寄って湧くのを待つ。
            var toCentre = Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(fate.Position.X, fate.Position.Z));

            if (toCentre > CentreSeekMeters)
            {
                this.BeginApproach(fate.Position, "敵が見えないので中心へ寄る");
            }

            return;
        }

        // 十分近い。BMR が拾うはずなので任せる。
        if (mob.Distance <= MobReachMeters)
        {
            if (this.approaching)
            {
                this.StopApproach();
            }

            return;
        }

        this.BeginApproach(mob.Position, $"最寄りの敵まで {mob.Distance:F0}m");
    }

    /// <summary>近づく移動を始める。すでに向かっていれば何もしない。</summary>
    private void BeginApproach(Vector3 destination, string why)
    {
        if (this.approaching)
        {
            // **追いかける先は、動いた分だけ引き直す。**
            //
            // 敵は動く。毎フレーム新しい座標を渡すと、そのたびに
            // 「目的地が変わった」と見えて経路を引き直すことになる。
            // 経路を引き直すたびに足が止まるので、進んでは止まりを
            // 繰り返してガクガク動く（2026-09-25 実測。170 ミリ秒ごとに
            // 引き直していて、その間ほとんど進んでいなかった）。
            //
            // 少し動いたくらいでは引き直さない。最後に狙った場所から
            // 大きく離れたときだけ引き直す。
            var moved = Vector3.Distance(destination, this.approachTarget);

            // **間隔は、動いているかに関わらず空ける。**
            //
            // 以前は「動いているなら引き直さない」としていた。
            // ところが引っかかって動けていないときこそ Tick が Moving を返さず、
            // 毎フレーム引き直すことになっていた。
            // 引き直すたびに足が止まるので、いつまでも動き出せない
            // （2026-09-26 実測。160 ミリ秒ごとに「経路 1 回目」を繰り返し、
            // その間 1m しか進んでいなかった）。
            //
            // 進んでいないなら、なおさら間を置く。
            if (DateTime.UtcNow - this.approachIssuedUtc < ApproachRepathInterval)
            {
                this.navigation.Tick(this.approachTarget, MobReachMeters);
                return;
            }

            // 間隔が空いていても、狙う先がほとんど動いていないなら引き直さない。
            // 同じ場所へ何度も引き直しても結果は変わらない。
            if (moved <= ApproachRepathMeters &&
                this.navigation.Tick(this.approachTarget, MobReachMeters) is MoveStatus.Moving)
            {
                return;
            }

            this.approaching = false;
        }

        if (!this.navigation.BeginMove(destination, MobReachMeters, false, out var failure))
        {
            // 経路探索の最中なら、ただ待つ。失敗ではない。
            if (this.navigation.Busy)
            {
                return;
            }

            // 歩かせられないので、止めていたプリセットの移動を戻す。
            this.ResumePresetMovement();
            this.trace.Trouble("近づけない", failure);

            // **歩いて行けないなら飛ぶ。**
            //
            // 敵が崖の上にいると、地上の経路では辿り着けない。
            // 乗り直して飛べば、高さを越えられる。
            if (!MountService.IsMounted && MountService.CanFlyHere)
            {
                this.trace.Decision("飛んで近づく", "歩いて行ける経路が無い");
                this.mount.ClearDismounting();
            }

            return;
        }

        // 歩いている間はプリセットの移動を止める。止めないと vnavmesh と取り合う。
        this.ParkPresetMovement();
        this.approaching = true;
        this.approachTarget = destination;
        this.approachIssuedUtc = DateTime.UtcNow;

        // 引き直した回数を出す。ガクガクするときはここが増え続ける。
        this.approachIssues++;
        this.trace.Decision("敵へ近づく", $"{why}（経路 {this.approachIssues} 回目）");
    }

    /// <summary>
    /// その FATE で実際に降りられる座標を求める。
    ///
    /// <b>FATE の中心は、立てる場所とは限らない。</b>
    /// 石緑湖の「石緑湖の主「オアンネス」」のように、中心が湖の上にある FATE がある。
    /// 中心をそのまま目指すと水面の上まで飛んで、降りようとしても降りられず、
    /// 空中で止まったままになる（2026-09-25 実測。高さ 17 で 13 秒動けなかった）。
    ///
    /// vnavmesh に「その座標に最も近いナビメッシュ上の点」を聞く。
    /// ナビメッシュに載っている点は、定義上そこに立てる。
    /// GatherBuddyReborn も採集地点をこの方法で補正している
    /// （AutoGather.cs の NearestPoint(pos, 10, 10000)）。
    ///
    /// 1 つの FATE につき 1 度だけ聞いて覚える。毎フレーム聞くと重い。
    /// </summary>
    /// <summary>
    /// ある地点のまわりから、降りられる場所を探す。
    ///
    /// <b>1 点だけ聞かない。</b>
    /// 中心が湖や崖の上だと、そこを起点に聞いても答えが出ない。
    /// 中心から外へ向かって渦を描くように候補を並べ、近い順に試す。
    /// 近いものから返すので、なるべく中心の近くに降りられる。
    ///
    /// ICE（Cosmic Exploration の自動化）も同じ形で、
    /// 格子状に並べた点を近い順に試している
    /// （逆コンパイルして確認。FishingUtil.TryFindStand）。
    /// </summary>
    private Vector3? FindLandableAround(Vector3 centre, float radius)
    {
        // 刻み幅。細かすぎると問い合わせが増えるだけなので、半径から決める。
        var step = MathF.Max(radius / 4f, 5f);

        for (var ring = 0; ring <= LandableSearchRings; ring++)
        {
            for (var dx = -ring; dx <= ring; dx++)
            {
                for (var dz = -ring; dz <= ring; dz++)
                {
                    // その輪の縁だけを見る。内側はもう見ている。
                    if (ring != 0 && Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                    {
                        continue;
                    }

                    var probe = new Vector3(
                        centre.X + (dx * step),
                        centre.Y,
                        centre.Z + (dz * step));

                    // 辿り着ける点を優先する。ただのメッシュ上の最近傍だと、
                    // 湖の向こうの小島のように「そこには行けない」点が返りうる。
                    if (this.vnavmesh.TryNearestPointReachable(probe, step * 2f, 100f, out var reachable) &&
                        reachable is { } ok)
                    {
                        return ok;
                    }
                }
            }
        }

        // 辿り着ける点が 1 つも無い。せめてメッシュ上の点を返す。
        return this.vnavmesh.TryNearestPoint(centre, radius, 100f, out var any) ? any : null;
    }

    private Vector3 ResolveLandablePoint(FateInfo fate)
    {
        var here = Svc.ClientState.TerritoryType;

        if (this.landable is { } cached && cached.Id == fate.Id && cached.TerritoryId == here)
        {
            return cached.Point;
        }

        // **メッシュが出来ていないうちは聞かない。**
        //
        // エリアを移った直後は読み込みが終わっておらず、vnavmesh 側は
        // Query が null のまま何を聞いても null を返す
        // （IPCProvider.cs の navmeshManager.Query?.～）。
        // 誤った座標が返るわけではないが、補正できないまま
        // 「中心でよい」と覚えてしまうと、読み込みが終わっても直らない。
        // 覚えずに、次のフレームで聞き直す。
        if (!this.vnavmesh.TryIsReady(out var ready) || !ready)
        {
            this.trace.State("メッシュ待ち", $"{fate.Name} の降りられる場所を、まだ求められません");
            return fate.Position;
        }

        // **中心の 1 点だけを聞かない。**
        //
        // 中心が湖や崖の上だと、そこを起点に聞いても答えが出ないか、
        // 出ても遠くの行けない場所になる。
        // 中心から渦を描くように候補を並べ、近い順に聞いていく。
        //
        // ICE（Cosmic Exploration の自動化）も同じやり方をしている。
        // 中心の周りを格子状に並べ、辿り着ける点が見つかるまで試す
        // （逆コンパイルして確認。FishingUtil.TryFindStand）。
        var resolved = this.FindLandableAround(fate.Position, Math.Max(20f, fate.Radius));

        if (resolved is not { } found)
        {
            // 聞けなかった。中心のまま向かう。行けないと決まったわけではない。
            // ここも覚えない。次に聞けば答えが返るかもしれない。
            this.trace.Trouble("降りられる場所が分からない", $"{fate.Name} は中心をそのまま目指します");
            return fate.Position;
        }

        var moved = Vector3.Distance(found, fate.Position);

        // 大きく動いたときだけ残す。数メートルの補正はふつうのこと。
        if (moved > 3f)
        {
            this.trace.Decision(
                "降りられる場所へ補正",
                $"{fate.Name} 中心({fate.Position.X:F0},{fate.Position.Y:F0},{fate.Position.Z:F0}) → " +
                $"({found.X:F0},{found.Y:F0},{found.Z:F0}) {moved:F0}m ずらした");
        }

        this.landable = (here, fate.Id, found);
        return found;
    }

    /// <summary>近づく移動をやめ、プリセットの移動を戻す。</summary>
    private void StopApproach()
    {
        this.approaching = false;
        this.approachIssues = 0;
        this.navigation.Stop();
        this.ResumePresetMovement();
    }

    private void LeaveFate(Config cfg, FateInfo finished)
    {
        // 戦闘 AI を先に解除する。これを呼ばないと敵を追い続けて離れられない。
        this.ReleaseCombat();
        this.navigation.Stop();

        this.Completed++;

        // 納品 FATE は 100% の時点ではまだ報酬が入っていない。
        // 1 分後に FATE が消えるときに入る。それまでマップを離れない。
        if (finished.IsCollect)
        {
            this.pendingReward = (finished.Id, finished.StartTimeEpoch, DateTime.UtcNow + CollectRewardWindow);
            this.anomalyLog.Info("Fate", $"{finished.Name} が 100% になりました。報酬が入るまでこのマップに留まります");
        }
        else
        {
            this.anomalyLog.Info("Fate", $"{finished.Name} が 100% になりました（完了 {this.Completed} 件）");
        }

        // この FATE はもう離れた。円の中に立っていても、二度と拾わない。
        this.leftFateId = finished.Id;

        this.target = null;
        this.SetStep(FateStep.Leaving, "次の FATE へ向かっています");

        // 先に決めてあった FATE があれば、そのまま動き出す。
        var next = this.prefetched ?? this.PickNext(cfg, exclude: finished.Id);
        this.prefetched = null;

        if (next is not null)
        {
            this.BeginMoveTo(next);
            return;
        }

        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
    }

    private void FinishCurrentFate()
    {
        this.ReleaseCombat();
        this.target = null;
        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
    }

    // ---- 部品 ----

    private FateInfo? PickNext(Config cfg, ushort? exclude = null)
    {
        if (!Player.Available)
        {
            return null;
        }

        var skip = this.blacklist;
        if (exclude is { } id)
        {
            skip = [.. this.blacklist, id];
        }

        return this.scanner.PickNext(
            Player.Position,
            cfg.FateMinTimeRemainingSec,
            cfg.FateMaxProgressPct,
            cfg.FateLevelFilterEnabled,
            Player.Level,
            cfg.FateMaxLevelBelow,
            cfg.FateMaxLevelAbove,
            skipCollect: !cfg.FateCollectEnabled,
            skip,
            FateScanner.DefaultSortOrder);
    }

    /// <summary>見送りの期限が切れた FATE を、また狙えるようにする。</summary>
    private void ExpireBlacklist()
    {
        if (this.blacklistUntil.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        List<ushort>? done = null;

        foreach (var (id, until) in this.blacklistUntil)
        {
            if (now >= until)
            {
                (done ??= []).Add(id);
            }
        }

        if (done is null)
        {
            return;
        }

        foreach (var id in done)
        {
            this.blacklistUntil.Remove(id);
            this.blacklist.Remove(id);
            this.stuckCounts.Remove(id);
            this.anomalyLog.Info("Fate", $"FATE {id} の見送りを解きました。もう一度狙います");
        }
    }

    private void MarkStuck(ushort fateId)
    {
        // **諦めるなら、いま走っている経路も止める。**
        //
        // 止めないと vnavmesh は前の目的地へ向かって飛び続ける。
        // こちらは「探している」つもりでも体は元の FATE へ進み、
        // 次の FATE を決めた瞬間に向きが変わるため、
        // 空中でくるりと反転したように見える
        // （2026-09-25 実測。諦めてから 6 秒間に 111m 進んでいた）。
        this.navigation.Stop();
        this.moveIssued = false;

        this.stuckCounts.TryGetValue(fateId, out var count);
        count++;
        this.stuckCounts[fateId] = count;

        if (count >= MaxStuckPerFate)
        {
            // **見送るのは一時的にする。**
            //
            // 入り組んだ地形の FATE でも、通路さえ辿れば行ける。
            // 辿り着けなかったのは、こちらの経路の追い方が悪かっただけの
            // ことが多い。周回のあいだずっと除け続けると、
            // そのマップで稼げる FATE が減っていく。
            //
            // 少し置いてから、もう一度試す。
            this.blacklist.Add(fateId);
            this.blacklistUntil[fateId] = DateTime.UtcNow + BlacklistDuration;

            this.anomalyLog.Warn(
                "Fate",
                $"FATE {fateId} は {count} 回続けて辿り着けなかったため、" +
                $"{BlacklistDuration.TotalMinutes:F0} 分ほど見送ります");
        }
    }

    private void AdvanceZone(Config cfg)
    {
        if (cfg.FateZones.Count <= 1)
        {
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        // **いまいるマップは飛ばす。**
        // 一覧の並び順によっては、次の番号が「いまいるマップ」になる。
        // そこへテレポしても何も変わらず、FATE が無いまま待ち続ける。
        var here = Svc.ClientState.TerritoryType;
        uint next = 0;

        for (var i = 0; i < cfg.FateZones.Count; i++)
        {
            this.zoneIndex = (this.zoneIndex + 1) % cfg.FateZones.Count;

            if (cfg.FateZones[this.zoneIndex] != here)
            {
                next = cfg.FateZones[this.zoneIndex];
                break;
            }
        }

        if (next == 0)
        {
            // 一覧がいまいるマップだけだった。移りようがない。
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        // マップを移るので、そのマップ固有の記録は捨てる。
        this.blacklist.Clear();
        this.blacklistUntil.Clear();
        this.stuckCounts.Clear();
        this.target = null;
        this.prefetched = null;

        // **行き先を控える。**
        // これが入っていると、いま一覧のマップにいても移動を続ける。
        // TickTraveling は「段階が Traveling で、控えた行き先と一致する」
        // ときだけテレポを撃つので、ここでは段階も一緒に立てる。
        this.travelTargetTerritory = next;
        this.teleportStartedUtc = DateTime.UtcNow;
        this.teleportIssued = false;
        this.SetStep(FateStep.Traveling, $"{NpcLocationService.GetTerritoryName(next)} へ移動しています");
        this.anomalyLog.Info("Fate", $"次のマップ {NpcLocationService.GetTerritoryName(next)} へ移ります");
    }

    private bool TryTeleportTo(uint territoryId)
    {
        if (!this.aetherytes.TryFindTarget(territoryId, out var teleport) || teleport is null)
        {
            return false;
        }

        return this.lifestream.TryTeleport(teleport.AetheryteId, teleport.SubIndex, out var accepted) && accepted;
    }

    /// <summary>納品 FATE の報酬が着地したかを見る。</summary>
    private void RefreshPendingReward()
    {
        if (this.pendingReward is not { } pending)
        {
            return;
        }

        var live = this.scanner.GetById(pending.Id);

        // FATE が消えた = 報酬が入った。
        if (live is null || live.StartTimeEpoch != pending.Start)
        {
            this.anomalyLog.Info("Fate", "納品 FATE の報酬が入りました");
            this.pendingReward = null;
            return;
        }

        // マップを離れてしまった。報酬は失われる。
        if (!C.FateZones.Contains(Svc.ClientState.TerritoryType) && this.Step == FateStep.Traveling)
        {
            this.anomalyLog.Warn("Fate", "報酬が入る前にマップを離れました。この納品 FATE の報酬は失われます");
            this.pendingReward = null;
            return;
        }

        if (DateTime.UtcNow > pending.DeadlineUtc)
        {
            this.anomalyLog.Warn("Fate", "納品 FATE の報酬を待ちましたが、確認できませんでした");
            this.pendingReward = null;
        }
    }

    // BMR のモジュール名。RotationModuleRegistry は「名前空間 + 型名」で引く
    // （BossMod.SourceGen の RuntimeFullName）。
    private const string ModuleFateUtils = "BossMod.Autorotation.MiscAI.FateUtils";
    private const string ModuleAutoTarget = "BossMod.Autorotation.MiscAI.AutoTarget";
    private const string ModuleNormalMovement = "BossMod.Autorotation.MiscAI.NormalMovement";
    private const string TrackDestination = "Destination";

    // トラック名と選択肢名は enum の名前そのもの
    // （RotationModule.Define が expectedIndex.ToString() を InternalName にする）。
    private const string TrackHandin = "Handin";
    private const string TrackCollect = "Collect";
    private const string TrackSync = "Sync";
    private const string TrackChocobo = "Chocobo";
    private const string TrackFate = "FATE";
    private const string OptionEnabled = "Enabled";
    private const string OptionDisabled = "Disabled";
    private const string OptionNone = "None";

    /// <summary>レベルシンクを入れる。FateSync.Enable の名前。</summary>
    private const string OptionSyncEnable = "Enable";

    /// <summary>
    /// 戦闘プリセットを有効にする。
    ///
    /// 設定が空なら、こちらで用意したプリセット（<see cref="FateCombatPreset"/>）を使う。
    /// 利用者が名前を知らなくても、FATE の敵だけを狙い、
    /// 絡まれたら反撃する設定で動き出せる。
    ///
    /// <b>BMR の AI（/bmrai）は入れない。プリセットだけで戦わせる。</b>
    ///
    /// プリセットの中で、狙う（AutoTarget）・撃つ（ジョブのモジュール）・
    /// 寄る（ジョブのモジュールが間合いを GoalZones に出し、NormalMovement が動く）・
    /// シンクする（FateUtils）がすべて揃う。AutoFATEGrind も AI を使わずこの形で動く。
    ///
    /// AI を入れると、この仕組みが 3 か所で壊れる（BMR 7.5.6.19 で確認）。
    /// <list type="number">
    /// <item><c>/bmrai on</c> は最初に SwitchToIdle を通り、有効なプリセットを null にする
    /// （AI/AIManager.cs SwitchToIdle）。直前に入れたプリセットが消える。</item>
    /// <item>AI は毎回「ターゲットがあれば AI 用プリセット、無ければ null」で有効プリセットを
    /// 上書きする（AI/AIBehaviour.cs）。AI 用プリセットは利用者の BMR 設定
    /// （AIAutorotPresetName）で、既定は空。空だと技を撃つモジュールも
    /// シンクする FateUtils も動かない。</item>
    /// <item>AI が動いている間、NormalMovement は何もしない
    /// （Autorotation/MiscAI/NormalMovement.cs 冒頭）。</item>
    /// </list>
    /// 「プリセットだけでは敵を追わない」と見えたのは、当時レベルシンクが入っていなかったため。
    /// シンクしていないと BMR は FATE の敵を攻撃対象から外す
    /// （Framework/Utils.cs IsPlayerSyncedToFate・BossModule/AIHintsBuilder.cs FillEnemies）。
    /// </summary>
    private void ApplyCombat(Config cfg)
    {
        if (this.presetApplied)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(cfg.FateCombatPreset)
            ? FateCombatPreset.Name
            : cfg.FateCombatPreset;

        // **プリセットを入れる前に AI を切る。**
        //
        // 以前の版が入れた AI や、利用者が自分で入れていた AI が残っていると、
        // 上の 3 つの理由でプリセットが働かない。/bmrai off もプリセットを null にするので、
        // 必ず SetActive より先に送る。
        this.bossMod.TrySetAiEnabled(false);

        if (!this.bossMod.TrySetActivePreset(name, out var accepted) || !accepted)
        {
            this.anomalyLog.Warn("Fate", $"BossMod Reborn のプリセット「{name}」を有効にできませんでした");
            return;
        }

        this.presetApplied = true;
        this.appliedPresetName = name;
        this.presetCheckedUtc = DateTime.UtcNow;
        this.ApplyFateStrategies(cfg, name);

        // 本当に有効になったかを確かめる。SetActive が true を返しても、
        // 別の機能があとから解除していることがある。
        this.bossMod.TryGetActivePreset(out var active);
        this.trace.Decision(
            "戦闘の用意ができた",
            $"要求={name} 実際={active ?? "なし"}");
    }

    /// <summary>
    /// 戦っている間、プリセットが有効なままかを見張る。外れていたら入れ直す。
    ///
    /// <b>1 度入れたきりにしない。</b>
    /// 利用者や別のプラグインが BMR の AI を入れる・プリセットを切り替えると、
    /// こちらのプリセットは黙って外れ、技も移動もシンクも止まる。
    /// AutoFATEGrind も戦闘中は毎回プリセットを確かめ直している。
    /// </summary>
    private void EnsurePresetActive(Config cfg)
    {
        if (DateTime.UtcNow - this.presetCheckedUtc < PresetCheckInterval)
        {
            return;
        }

        this.presetCheckedUtc = DateTime.UtcNow;

        // 最初に入れられなかった（BMR の起動待ちなど）なら、ここで入れ直す。
        if (!this.presetApplied)
        {
            this.ApplyCombat(cfg);
            return;
        }

        if (!this.bossMod.TryGetActivePreset(out var active)
            || string.Equals(active, this.appliedPresetName, StringComparison.Ordinal))
        {
            return;
        }

        this.trace.Trouble("プリセットが外れていた", $"期待={this.appliedPresetName} 実際={active ?? "なし"}。入れ直します");

        // 外した犯人が AI なら、切らないと次の瞬間にまた外される。
        this.bossMod.TrySetAiEnabled(false);
        this.bossMod.TrySetActivePreset(this.appliedPresetName, out _);
    }

    /// <summary>
    /// vnavmesh で歩かせる間、プリセットの移動（NormalMovement）を止める。
    ///
    /// <b>両方が同時に動かすと取り合いになる。</b>
    /// プリセットは戦闘を続けたまま、移動だけを vnavmesh に明け渡す。
    /// AutoFATEGrind の ParkBossModMovement と同じ作法。
    /// </summary>
    private void ParkPresetMovement()
    {
        if (!this.presetApplied || this.movementParked)
        {
            return;
        }

        if (this.bossMod.TryAddTransientStrategy(this.appliedPresetName, ModuleNormalMovement, TrackDestination, OptionNone, out var ok) && ok)
        {
            this.movementParked = true;
        }
    }

    /// <summary>止めていたプリセットの移動を戻す。</summary>
    private void ResumePresetMovement()
    {
        if (!this.movementParked)
        {
            return;
        }

        this.movementParked = false;

        if (!this.presetApplied)
        {
            return;
        }

        if (!this.bossMod.TryClearTransientStrategy(this.appliedPresetName, ModuleNormalMovement, TrackDestination, out var ok) || !ok)
        {
            this.anomalyLog.Warn("Fate", "BossMod Reborn の移動を戻せませんでした（NormalMovement.Destination）");
        }
    }

    /// <summary>
    /// FATE 向けの一時方針を立てる。
    ///
    /// <b>納品は BMR の FATE helper に任せる。</b>
    /// 自前で NPC へ歩いて話しかける処理は書かない。
    /// BMR 側は 10 個溜まった時点で自動的に納品へ向かい、
    /// 向かう間は新しい敵に絡まず、着いたら話しかける。
    /// 戻ってきたら通常の戦闘に戻る。要望の挙動をそのまま満たす。
    ///
    /// 立てた方針はプリセット本体を書き換えない。
    /// 解除は ReleaseCombat でまとめて行う。
    /// </summary>
    private void ApplyFateStrategies(Config cfg, string preset)
    {
        // 納品 FATE のアイテムを 10 個溜めたら自動で納品しに行く。
        TrySet(ModuleFateUtils, TrackHandin, cfg.FateCollectEnabled ? OptionEnabled : OptionDisabled);

        // 地面に落ちているアイテムは拾わない。
        //
        // この選択肢は「拾うかどうか」ではなく
        // 「戦闘の代わりに拾いに行くか」を決めるもの。
        // 討伐で進めたいので必ず Disabled にする。
        TrySet(ModuleFateUtils, TrackCollect, OptionDisabled);

        // **レベルシンクを入れさせる。**
        //
        // ゲームが自動でかけるものだと思っていたが、実際は
        // 「LEVEL SYNC」を押す必要がある。押さないと FATE に
        // 参加した扱いにならず、敵も狙わない（2026-09-25 実測）。
        //
        // BMR 側は IsSyncedToFate を見て、入っていなければ
        // FateManager.LevelSync() を 0.5 秒ごとに呼ぶ。
        TrySet(ModuleFateUtils, TrackSync, OptionSyncEnable);

        // バディの面倒は BuddyService が見る。二重に動かさない。
        // BMR 側は在庫があるかしか見ず、切れたときに買いに行かない。
        TrySet(ModuleFateUtils, TrackChocobo, OptionDisabled);

        // FATE 内の敵を優先して狙う。
        TrySet(ModuleAutoTarget, TrackFate, OptionEnabled);

        void TrySet(string module, string track, string value)
        {
            if (this.bossMod.TryAddTransientStrategy(preset, module, track, value, out var ok) && ok)
            {
                return;
            }

            // 失敗しても周回は続ける。BMR の版によって
            // トラック名が変わっている可能性があるため、記録だけ残す。
            this.anomalyLog.Warn("Fate", $"BossMod Reborn の方針を設定できませんでした: {module}.{track} = {value}");
        }
    }

    /// <summary>
    /// 戦闘プリセットを解除する。
    ///
    /// <b>離脱のたびに必ず呼ぶ。</b>解除しないと BossMod が敵を追い続け、
    /// その場から離れられない。
    /// </summary>
    private void ReleaseCombat()
    {
        if (!this.presetApplied)
        {
            return;
        }

        this.bossMod.TryClearActivePreset(out _);

        // 一時方針はまとめて外す。移動を止めていた分（NormalMovement）もここで戻る。
        if (!string.IsNullOrEmpty(this.appliedPresetName))
        {
            this.bossMod.TryClearTransientPresetStrategies(this.appliedPresetName, out _);
        }

        this.presetApplied = false;
        this.appliedPresetName = string.Empty;
        this.movementParked = false;
        this.approaching = false;
    }

    /// <summary>
    /// 跳ぶ。段差や small な引っかかりは、これだけで外れる。
    ///
    /// 飛んでいる最中は跳べないので何もしない。
    /// ICE も詰まったときの手当てとして同じことをしている
    /// （逆コンパイルして確認。CheckIfIsStuck の JumpIfStuck）。
    /// </summary>
    private static unsafe void TryJump()
    {
        try
        {
            if (MountService.IsFlying || Svc.Condition[ConditionFlag.Jumping] || Svc.Condition[ConditionFlag.Jumping61])
            {
                return;
            }

            var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            if (am is null)
            {
                return;
            }

            // GeneralAction 2 が「ジャンプ」。
            if (am->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2) == 0)
            {
                am->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2);
            }
        }
        catch
        {
            // 跳べなくても進行は止めない。
        }
    }

    /// <summary>ホームポイントへ戻る（戦闘不能からの復帰）。</summary>
    private static unsafe void ReturnHome()
    {
        try
        {
            // ExecuteCommand 200 / param 8 が「帰還」。
            FFXIVClientStructs.FFXIV.Client.Game.GameMain.ExecuteCommand(200, 8, 0, 0, 0);
        }
        catch
        {
            // 失敗しても次のフレームで呼び直される。
        }
    }

    private void SetStep(FateStep step, string detail)
    {
        if (this.Step != step)
        {
            this.trace.Decision($"{this.Step} → {step}", detail);
            this.Step = step;
        }

        this.StatusDetail = detail;
    }
}
