using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace AutoCollector.Automation;

/// <summary>FATE へ飛んで入るときの段階。</summary>
public enum ApproachPhase
{
    /// <summary>何もしていない。</summary>
    Idle,

    /// <summary>着地点と外周点を決めている。</summary>
    PlanEntry,

    /// <summary>マウントに乗っている。</summary>
    Mounting,

    /// <summary>離陸している。飛行が確認できるまで待つ。</summary>
    TakingOff,

    /// <summary>外周の上空へ向かっている。</summary>
    FlyToEntry,

    /// <summary>外周の上空から、中心の地上へ斜めに降りている。</summary>
    Descending,

    /// <summary>地表に着いたかを確かめている。</summary>
    GroundConfirm,

    /// <summary>マウントから降りている。</summary>
    Dismounting,

    /// <summary>地上に立った。戦闘へ渡してよい。</summary>
    ReadyForCombat,

    /// <summary>進入できなかった。理由は <see cref="FateApproach.FailureReason"/>。</summary>
    Failed,
}

/// <summary>
/// FATE へ「外周の上空から中心の地上へ斜めに降りる」進入を受け持つ。
///
/// <b>なぜ別の入れ物に切り出すのか。</b>
/// これまでは FateRunner の TickMoving が、乗る・飛ぶ・近づく・降りるを
/// 1 つの関数で見ていた。そのため
/// <list type="bullet">
/// <item>経路は持ち上げた点へ頼み、到着は地上の点で測る（点が 2 つある）</item>
/// <item>「まだ離陸していない」と「着地して飛行が解けた」を区別できない</item>
/// <item>目的地が動く敵なので、毎フレーム変わる</item>
/// </list>
/// という噛み合わせが残った。段階を分け、段階ごとに目的地を固定し、
/// 段階が変わったときだけ経路を引き直すことで、これを断つ。
///
/// <b>経路は自分で求めてから渡す。</b>
/// <c>SimpleMove.PathfindAndMoveCloseTo</c> は探索が終わった瞬間に
/// vnavmesh 自身が移動を始める（AsyncMoveRequest.cs:44-60）。
/// それでは「引けた経路が本当に斜めに降りているか」を見る隙が無い。
/// <c>Nav.PathfindCancelable</c> で求め、検査してから
/// <c>Path.MoveTo</c> へ渡す。
///
/// <b>飛んでいる間は BMR に任せない。</b>
/// BMR の NormalMovement は飛行中に何もしない（NormalMovement.cs:125-128）。
/// 地上に立ってから渡す。
/// </summary>
public sealed class FateApproach(
    AnomalyLog anomalyLog,
    FateTrace trace,
    VnavmeshIpc vnavmesh,
    MountService mount)
{
    /// <summary>外周点を円のどれだけ外に置くか。</summary>
    private const float EntryMarginMeters = 10f;

    /// <summary>降下の角度（度）。大きいほど急に降りる。</summary>
    private const float DescentAngleDegrees = 20f;

    /// <summary>外周点を、その場の床からどれだけ高く置くか。</summary>
    private const float EntryClearanceMeters = 8f;

    /// <summary>外周点に着いたと認める水平距離。</summary>
    private const float EntryReachedFlatMeters = 5f;

    /// <summary>外周点に着いたと認める高さの差。</summary>
    private const float EntryReachedHeightMeters = 8f;

    /// <summary>着地点の真上と認める水平距離。</summary>
    private const float LandingFlatMeters = 2f;

    /// <summary>着地点の上と認める高さの差。これ以下なら降車してよい。</summary>
    private const float LandingHeightMeters = 3f;

    /// <summary>
    /// 接地を認める水平距離。
    ///
    /// <b>円の中であることでは足りない。</b>円の半径は数十メートルあるので、
    /// 「円の中で降りた」を接地と認めると、着地点から遠く離れた場所で
    /// 戦闘を始めてしまう。着地点の近くであることを要求する。
    /// </summary>
    private const float GroundConfirmFlatMeters = 10f;

    /// <summary>
    /// 着地点より下にいてよい深さ。
    ///
    /// <b>高さの差は絶対値で見ない。</b>
    /// 以前は「自分 - 着地点 &lt;= 8」で判定していたため、
    /// 着地点よりずっと下の別の階層にいても真になった。
    /// 下にいるのは階層が違う証拠なので、狭く取る。
    /// </summary>
    private const float LandingBelowMeters = 2f;

    /// <summary>離陸を待つ上限。過ぎたら飛べないものとして扱う。</summary>
    private static readonly TimeSpan TakeoffTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 離陸したと認めるまでに、飛行を確認し続ける回数。
    ///
    /// 乗った直後の 1 フレームだけ見て判断しない。
    /// </summary>
    private const int FlyingStableFrames = 3;

    /// <summary>乗るのを待つ上限。</summary>
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(10);

    /// <summary>1 つの段階に与える上限。経路探索の時間も含む。</summary>
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 進入全体に与える上限。
    ///
    /// <b>段階ごとの上限だけでは足りない。</b>
    /// 向きを変えてやり直すたびに時間が積み上がり、
    /// 段階 90 秒 × やり直し 5 回で 20 分を超えうる。
    /// FATE は 15 分ほどで終わるので、それでは間に合わない。
    /// 諦めて地上の経路で向かうほうがましなので、ここで切る。
    /// </summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(3);

    /// <summary>地表に着いたかを確かめるのに与える上限。</summary>
    private static readonly TimeSpan GroundConfirmTimeout = TimeSpan.FromSeconds(20);

    /// <summary>降車を待つ上限。</summary>
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(15);

    /// <summary>進入の向きを変えて試す上限。</summary>
    private const int MaxReplans = 4;

    /// <summary>進入の向きを変えるときに回す角度（度）。</summary>
    private static readonly float[] ReplanAngles = [0f, 40f, -40f, 90f, -90f];

    /// <summary>降下を分割するときの刻み。</summary>
    private static readonly float[] DescentFractions = [0.25f, 0.5f, 0.75f, 1.0f];

    /// <summary>
    /// 最後に残してよい垂直落下の高さ。
    ///
    /// 地表すぐ上の短い落下は避けられない。問題なのは
    /// 上空から長く落ちることで、その間は何もできない。
    /// </summary>
    private const float MaxVerticalTailMeters = 12f;

    /// <summary>「もう水平には着いている」と見る距離。垂直落下の判定に使う。</summary>
    private const float MaxVerticalTailFlatMeters = 3f;

    /// <summary>段に分けた降下で、その段に着いたと認める距離。</summary>
    private const float StageReachedMeters = 4f;

    /// <summary>
    /// 「飛べた記録」より上を、どれだけ目指してよいか。
    ///
    /// 記録は下限の証拠でしかないので、そのまま上限にすると
    /// 低く見積もりすぎる。余裕を足して試し、本当に頭打ちなら
    /// 段階の上限で拾う。
    /// </summary>
    private const float CeilingHeadroomMeters = 25f;

    /// <summary>地表に着いたと認めるまでに、安定を確認し続ける回数。</summary>
    private const int GroundStableFrames = 5;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly FateTrace trace = trace;
    private readonly VnavmeshIpc vnavmesh = vnavmesh;
    private readonly MountService mount = mount;

    /// <summary>いまの段階。</summary>
    public ApproachPhase Phase { get; private set; } = ApproachPhase.Idle;

    /// <summary>失敗した理由。<see cref="ApproachPhase.Failed"/> のときだけ意味を持つ。</summary>
    public string FailureReason { get; private set; } = string.Empty;

    /// <summary>画面に出す一言。</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>この進入がどの FATE のどの湧きに対するものか。</summary>
    private (uint Territory, ushort Id, int Start) spawnKey;

    /// <summary>
    /// 要求の世代。
    ///
    /// <b>古い探索の結果を捨てるために使う。</b>
    /// 経路探索は数秒から十数秒かかる。終わったときには段階が
    /// 変わっていることがあり、その結果で動き出すと筋が通らない。
    /// </summary>
    private int generation;

    private Vector3 centre;
    private float radius;

    /// <summary>固定した着地点。敵が動いても変えない。</summary>
    private Vector3 landing;

    /// <summary>外周の上空点。</summary>
    private Vector3 entry;

    private DateTime phaseSinceUtc;
    private DateTime startedUtc;
    private int replans;
    private int flyingFrames;
    private int groundFrames;
    private Vector3 groundPosition;

    /// <summary>走らせている経路探索。</summary>
    private Task<List<Vector3>>? pending;
    private CancellationTokenSource? pendingCancel;
    private int pendingGeneration;

    /// <summary>この段階で経路を渡したか。段階ごとに 1 度だけ渡す。</summary>
    private bool pathIssued;

    /// <summary>
    /// 段に分けた降下の中継点。
    ///
    /// 空なら一本の経路で降りる。一本が垂直落下になると分かったときだけ、
    /// ここへ中継点を入れて段ごとに探索する。
    /// </summary>
    private List<Vector3> descentStages = [];

    /// <summary>いま何段目を目指しているか。</summary>
    private int descentStage;

    /// <summary>いま辿らせている経路の終点。監視に使う。</summary>
    /// <summary>
    /// いま辿らせている経路の終点。
    ///
    /// <b>記録のためだけに持つのではない。</b>
    /// 監視している点と食い違っていないかを毎フレーム見る。
    /// 食い違っていたら引き直す。以前の実装は、経路を持ち上げた点へ頼み、
    /// 到着を地上の点で測っていた。その食い違いをここで拾えるようにする。
    /// </summary>
    private Vector3 issuedDestination;

    /// <summary>進入中か。</summary>
    public bool Active => this.Phase is not (ApproachPhase.Idle or ApproachPhase.ReadyForCombat or ApproachPhase.Failed);

    /// <summary>
    /// 進入を始める。
    ///
    /// <b>飛べないなら始めない。</b>false を返したら、
    /// 呼び出し側は従来の地上の移動で向かう。
    /// </summary>
    public bool Begin(FateInfo fate)
    {
        if (!Player.Available)
        {
            return false;
        }

        this.generation++;
        this.CancelPending();

        this.spawnKey = (Svc.ClientState.TerritoryType, fate.Id, fate.StartTimeEpoch);
        this.centre = fate.Position;
        this.radius = MathF.Max(fate.Radius, 20f);
        this.landing = default;
        this.entry = default;
        this.replans = 0;
        this.flyingFrames = 0;
        this.groundFrames = 0;
        this.pathIssued = false;
        this.descentStages = [];
        this.descentStage = 0;
        this.FailureReason = string.Empty;
        this.startedUtc = DateTime.UtcNow;

        this.SetPhase(ApproachPhase.PlanEntry, $"{fate.Name} への進入を組み立てています");
        return true;
    }

    /// <summary>
    /// 進入をやめる。
    ///
    /// <b>世代を進めてから取り消す。</b>
    /// これで、走っている探索が後から終わっても結果が使われない。
    /// </summary>
    public void Cancel(string why)
    {
        if (this.Phase == ApproachPhase.Idle)
        {
            return;
        }

        this.generation++;
        this.CancelPending();
        this.vnavmesh.TryStop();
        this.pathIssued = false;
        this.descentStages = [];
        this.descentStage = 0;
        this.Phase = ApproachPhase.Idle;
        this.Detail = string.Empty;

        // **vnavmesh 側に遅れて入る経路がないかを見る。**
        //
        // Path.Stop は積んである経路点を捨てるだけで、走っている
        // 経路探索は止めない。さらに vnavmesh の RetryOnStuck が
        // 有効だと、Path.MoveTo で始めた経路でも詰まったときに
        // vnavmesh 自身が SimpleMove として引き直す
        // （FollowPath.cs:121-127 → AsyncMoveRequest.cs:24-30）。
        // その結果は AsyncMoveRequest.Update が勝手に積むため、
        // こちらの世代番号では捨てられない。
        //
        // ここでは知らせるだけにする。止める手立ては
        // 呼び出し側（FateRunner の止めたあとの見張り）が持っている。
        if (this.vnavmesh.TrySimpleMovePathfindInProgress(out var pending) && pending)
        {
            this.trace.Trouble(
                "遅れて入る経路がある",
                "vnavmesh が経路探索中です。止めたあとに動き出す可能性があります");
        }

        this.trace.State("進入をやめた", why);
    }

    /// <summary>
    /// 1 フレーム進める。
    ///
    /// <b>この段階の間、呼び出し側は vnavmesh に触らない。</b>
    /// 二重に経路を積むと、積んでは捨てるを繰り返して暴れる。
    /// </summary>
    /// <param name="fate">狙っている FATE のいまの姿。</param>
    public void Tick(FateInfo fate)
    {
        if (!this.Active)
        {
            return;
        }

        if (!Player.Available)
        {
            return;
        }

        // 別のエリアへ移った、別の湧きになった。この進入はもう意味が無い。
        var here = Svc.ClientState.TerritoryType;
        if (here != this.spawnKey.Territory ||
            fate.Id != this.spawnKey.Id ||
            fate.StartTimeEpoch != this.spawnKey.Start)
        {
            this.Fail("狙っていた FATE が入れ替わりました");
            return;
        }

        // **進入そのものに与える上限。**
        //
        // 段階ごとの上限だけでは、やり直すたびに時間が積み上がる。
        // 段階 90 秒 × やり直し 5 回で 20 分を超えうる。
        // FATE は 15 分ほどで終わるので、それでは間に合わない。
        if (DateTime.UtcNow - this.startedUtc > TotalBudget)
        {
            this.Fail($"進入に {TotalBudget.TotalMinutes:F0} 分かかっても着けませんでした");
            return;
        }

        // 段階に与えた時間を使い切った。
        if (DateTime.UtcNow - this.phaseSinceUtc > this.TimeoutFor(this.Phase))
        {
            this.OnPhaseTimeout();
            return;
        }

        switch (this.Phase)
        {
            case ApproachPhase.PlanEntry:
                this.TickPlanEntry(fate);
                return;

            case ApproachPhase.Mounting:
                this.TickMounting();
                return;

            case ApproachPhase.TakingOff:
                this.TickTakingOff();
                return;

            case ApproachPhase.FlyToEntry:
                this.TickFlyToEntry();
                return;

            case ApproachPhase.Descending:
                this.TickDescending();
                return;

            case ApproachPhase.GroundConfirm:
                this.TickGroundConfirm();
                return;

            case ApproachPhase.Dismounting:
                this.TickDismounting();
                return;
        }
    }

    // ---- 各段階 ----

    /// <summary>
    /// 着地点と外周点を決める。
    ///
    /// <b>敵の位置を着地点にしない。</b>
    /// 敵は動くので、移動を頼んだときと監視するときで点が変わる。
    /// 中心付近の床を 1 度だけ求めて固定する。
    /// </summary>
    private void TickPlanEntry(FateInfo fate)
    {
        if (!this.vnavmesh.TryIsReady(out var ready) || !ready)
        {
            this.Detail = "このエリアのナビメッシュを待っています";
            return;
        }

        if (this.landing == default)
        {
            if (this.FindLanding(fate) is not { } spot)
            {
                this.Fail("降りられる場所が見つかりません");
                return;
            }

            this.landing = spot;
        }

        if (this.PlanEntryPoint() is not { } entryPoint)
        {
            this.Fail("外周から進入できる空間が見つかりません");
            return;
        }

        this.entry = entryPoint;

        this.trace.Decision(
            "進入を決めた",
            $"{fate.Name} 半径{this.radius:F0}m " +
            $"着地({this.landing.X:F0},{this.landing.Y:F0},{this.landing.Z:F0}) " +
            $"外周上空({this.entry.X:F0},{this.entry.Y:F0},{this.entry.Z:F0}) " +
            $"やり直し{this.replans}回目");

        // もう飛んでいるなら、乗る・離陸は済んでいる。
        if (MountService.IsFlying)
        {
            this.SetPhase(ApproachPhase.FlyToEntry, $"{fate.Name} の外周上空へ向かっています");
            return;
        }

        this.SetPhase(ApproachPhase.Mounting, $"{fate.Name} へ飛ぶ準備をしています");
    }

    /// <summary>マウントに乗る。</summary>
    private void TickMounting()
    {
        if (MountService.IsFlying)
        {
            this.SetPhase(ApproachPhase.FlyToEntry, "外周上空へ向かっています");
            return;
        }

        if (!MountService.CanFlyHere)
        {
            this.Fail($"このエリアでは飛べません（{MountService.DescribeFlightStatus()}）");
            return;
        }

        // **降りる途中の記録を消しておく。**
        // 残っていると TickPrepare が「降りています」で返り続け、二度と乗らない。
        this.mount.ClearDismounting();

        if (MountService.IsMounted)
        {
            this.SetPhase(ApproachPhase.TakingOff, "離陸しています");
            return;
        }

        // 距離で乗るかどうかを決めさせない。進入では必ず乗る。
        if (this.mount.TickPrepareAlways())
        {
            this.Detail = "マウントに乗っています";
            return;
        }

        if (!MountService.IsMounted)
        {
            this.Fail("マウントに乗れません");
        }
    }

    /// <summary>
    /// 離陸する。
    ///
    /// <b>「まだ飛んでいない」を理由に止めない。</b>
    /// 乗った直後は当然まだ飛んでいない。そこで経路を止めては引き直すと、
    /// ジャンプを撃つ隙が無くなって永久に離陸しない。
    ///
    /// 離陸そのものは vnavmesh がやる。fly=true の経路で
    /// 「次の点が自分より高い」「騎乗中」「まだ飛んでいない」が揃うと
    /// ジャンプを連打する（FollowPath.cs:142-154）。
    /// こちらは少し上の点への経路を渡して、その条件を作るだけ。
    /// </summary>
    private void TickTakingOff()
    {
        if (!MountService.IsMounted)
        {
            // 降りてしまった。乗り直す。
            this.vnavmesh.TryStop();
            this.pathIssued = false;
            this.SetPhase(ApproachPhase.Mounting, "乗り直しています");
            return;
        }

        if (MountService.IsFlying)
        {
            this.flyingFrames++;

            // **何度か続けて確認してから進む。**
            // 降下の途中でも InFlight は落ちるので、1 回では信じない。
            if (this.flyingFrames >= FlyingStableFrames)
            {
                this.trace.Decision("離陸した", $"高さ {Player.Position.Y:F0}");
                this.vnavmesh.TryStop();
                this.pathIssued = false;
                this.SetPhase(ApproachPhase.FlyToEntry, "外周上空へ向かっています");
            }

            return;
        }

        this.flyingFrames = 0;

        // 少し上へ向かう経路を 1 度だけ渡す。これが離陸の合図になる。
        if (!this.pathIssued)
        {
            var up = Player.Position with { Y = Player.Position.Y + 5f };

            // メッシュに聞かずに渡してよい唯一の場所。
            // 真上へ 5m は、飛べるエリアなら必ず空いている。
            // 検査すべきなのは「移動の経路」で、これは離陸の合図。
            if (this.vnavmesh.TryMoveAlong([up], fly: true))
            {
                this.pathIssued = true;
                this.issuedDestination = up;
                this.trace.State("離陸させる", $"真上 {up.Y:F0} への経路でジャンプを促します");
            }
        }

        this.Detail = "離陸しています";
    }

    /// <summary>外周の上空へ向かう。</summary>
    private void TickFlyToEntry()
    {
        if (!this.EnsureStillFlying())
        {
            return;
        }

        var flat = Flat(Player.Position, this.entry);
        var height = MathF.Abs(Player.Position.Y - this.entry.Y);

        // **水平だけで着いたことにしない。** 高さも揃ってから降下に移る。
        if (flat <= EntryReachedFlatMeters && height <= EntryReachedHeightMeters)
        {
            this.trace.Decision(
                "外周上空に着いた",
                $"水平{flat:F1}m 高さ差{height:F1}m → 中心の地上へ降ります");

            this.vnavmesh.TryStop();
            this.pathIssued = false;

            // 降り方は、これから引く経路を見て決める。前の記録は捨てる。
            this.descentStages = [];
            this.descentStage = 0;

            this.SetPhase(ApproachPhase.Descending, "中心の地上へ降りています");
            return;
        }

        this.RunPath(this.entry, fly: true, describe: "外周上空へ");
        this.Detail = $"外周上空へ向かっています（残り {flat:F0}m）";
    }

    /// <summary>
    /// 外周の上空から、中心の地上へ斜めに降りる。
    ///
    /// <b>ここで経路を引き直すのが肝心。</b>
    /// 以前は、経路は持ち上げた点へ頼んだまま、到着判定だけ
    /// 地上の点で測っていた。経路が変わらないので、斜めに降りる
    /// 保証はどこにも無かった。
    /// </summary>
    private void TickDescending()
    {
        if (!this.EnsureStillFlying())
        {
            return;
        }

        var flat = Flat(Player.Position, this.landing);
        var above = Player.Position.Y - this.landing.Y;

        // 着地点の真上まで来て、十分下りた。
        // **上下を分けて見る。** 下にいるのは別の階層にいる証拠。
        if (flat <= LandingFlatMeters && above <= LandingHeightMeters && above >= -LandingBelowMeters)
        {
            this.trace.Decision("着地点の上に来た", $"水平{flat:F1}m 高さ{above:F1}m");
            this.vnavmesh.TryStop();
            this.pathIssued = false;
            this.groundFrames = 0;
            this.groundPosition = Player.Position;
            this.SetPhase(ApproachPhase.GroundConfirm, "地表に降りています");
            return;
        }

        // **段に分けて降りる場合は、その段の終点を目指す。**
        //
        // 一本の経路が垂直落下になると分かったときだけ、ここに入る。
        var destination = this.descentStages.Count > 0 && this.descentStage < this.descentStages.Count
            ? this.descentStages[this.descentStage]
            : this.landing;

        this.RunPath(destination, fly: true, describe: "中心の地上へ");

        // 段の終点に着いたら、次の段へ。
        if (this.descentStages.Count > 0 &&
            this.descentStage < this.descentStages.Count &&
            this.pathIssued &&
            Flat(Player.Position, destination) <= StageReachedMeters &&
            MathF.Abs(Player.Position.Y - destination.Y) <= StageReachedMeters)
        {
            this.descentStage++;
            this.vnavmesh.TryStop();
            this.pathIssued = false;

            this.trace.State(
                "降下の段を進めた",
                $"{this.descentStage}/{this.descentStages.Count} 段目へ");
        }

        this.Detail = this.descentStages.Count > 0
            ? $"中心の地上へ降りています（{this.descentStage + 1}/{this.descentStages.Count} 段・残り 水平{flat:F0}m 高さ{above:F0}m）"
            : $"中心の地上へ降りています（残り 水平{flat:F0}m 高さ{above:F0}m）";
    }

    /// <summary>
    /// 地表に着いたかを確かめる。
    ///
    /// <b>InFlight が false になっただけで着地と認めない。</b>
    /// 降下の最中も InFlight は false になる。位置が落ち着くことと、
    /// 着地点との高さの差を合わせて見る。
    /// </summary>
    private void TickGroundConfirm()
    {
        var here = Player.Position;
        var above = here.Y - this.landing.Y;

        // まだ飛んでいる。降下を促す。
        if (MountService.IsFlying)
        {
            // 経路は渡さない。ゲーム側の降下に任せる。
            // ここで経路を積むと、降下の動きが操作として読まれて捨てられる。
            this.mount.RequestDescent();
            this.Detail = "降下しています";
            this.groundFrames = 0;
            return;
        }

        // 位置が落ち着いているか。
        if (Vector3.DistanceSquared(here, this.groundPosition) > 0.25f)
        {
            this.groundPosition = here;
            this.groundFrames = 0;
            this.Detail = "降下しています";
            return;
        }

        // 着地点との高さが噛み合っているか。
        //
        // **上下を分けて見る。** 以前は絶対値で ±5m としていたため、
        // 着地点より 4.9m 下（1 階層下の床、崖の途中の棚）でも
        // 接地確定になっていた。下にいるのは階層が違う証拠なので狭く取る。
        if (above > LandingHeightMeters || above < -LandingBelowMeters)
        {
            this.Detail = above < 0
                ? $"着地点より {-above:F1}m 下にいます（別の階層かもしれません）"
                : $"着地点より {above:F1}m 上にいます";

            return;
        }

        // **水平にも離れていないか。**
        //
        // EnsureStillFlying から来た場合、円の中でさえあれば
        // ここへ入れてしまう。円の半径は数十メートルあるので、
        // 着地点から遠く離れた場所で接地を認めることになる。
        var flat = Flat(here, this.landing);

        if (flat > GroundConfirmFlatMeters)
        {
            this.Detail = $"着地点から水平に {flat:F1}m 離れています";
            return;
        }

        this.groundFrames++;

        if (this.groundFrames < GroundStableFrames)
        {
            return;
        }

        this.trace.Decision("地表に着いた", $"高さ差 {above:F1}m 水平 {Flat(here, this.landing):F1}m");
        this.SetPhase(ApproachPhase.Dismounting, "マウントから降りています");
    }

    /// <summary>降車する。</summary>
    private void TickDismounting()
    {
        if (!MountService.IsMounted)
        {
            this.trace.Decision("降りた", "地上に立ちました。戦闘へ渡します");
            this.SetPhase(ApproachPhase.ReadyForCombat, "戦闘の準備ができました");
            return;
        }

        this.mount.TickDismount();
        this.Detail = "マウントから降りています";
    }

    // ---- 部品 ----

    /// <summary>
    /// 飛んでいるかを確かめる。落ちていれば作り直す。
    ///
    /// <b>離陸前と接地後を区別する。</b>
    /// この段階へ来ているなら、一度は飛べたことが確認済み。
    /// ここで飛んでいないのは「落ちた」を意味する。
    /// </summary>
    private bool EnsureStillFlying()
    {
        if (MountService.IsFlying)
        {
            return true;
        }

        // 地面に触れて飛行が解けた。もう着地点の近くなら、それでよい。
        //
        // **「円の中」では広すぎる。** 円の半径は数十メートルあるので、
        // 着地点から遠く離れた場所で接地の確認へ進んでしまう。
        // 接地の確認と同じ狭さを要求する。
        if (Flat(Player.Position, this.landing) <= GroundConfirmFlatMeters &&
            Player.Position.Y - this.landing.Y <= LandingHeightMeters &&
            Player.Position.Y - this.landing.Y >= -LandingBelowMeters)
        {
            this.trace.State("降りてしまった", "着地点の近くなので、そのまま地表の確認へ移ります");
            this.vnavmesh.TryStop();
            this.pathIssued = false;
            this.groundFrames = 0;
            this.groundPosition = Player.Position;
            this.SetPhase(ApproachPhase.GroundConfirm, "地表に降りています");
            return false;
        }

        // 遠いところで落ちた。乗り直して飛び直す。
        this.trace.Trouble("飛行が解けた", "乗り直して飛び直します");
        this.vnavmesh.TryStop();
        this.pathIssued = false;
        this.flyingFrames = 0;
        this.SetPhase(ApproachPhase.Mounting, "乗り直しています");
        return false;
    }

    /// <summary>
    /// 経路を求めて、検査してから辿らせる。
    ///
    /// <b>求めるのと辿らせるのを分ける。</b>
    /// SimpleMove に頼むと、探索が終わった瞬間に vnavmesh が
    /// 勝手に動き出すので、検査する隙が無い。
    /// </summary>
    private void RunPath(Vector3 destination, bool fly, string describe)
    {
        // 経路を渡してある。まだ辿っているなら見守る。
        if (this.pathIssued)
        {
            // **読めなかったときは「使い切った」と読まない。**
            //
            // TryNumWaypoints は IPC が失敗しても false を返す。
            // out の既定値 0 と合わせて「経路が無い」と同じ扱いになるため、
            // vnavmesh 側が答えられない状態だと毎フレーム
            // 新しい経路探索を投げ続けることになる。
            if (!this.vnavmesh.TryNumWaypoints(out var left))
            {
                this.Detail = $"{describe}の経路の状態を読めません";
                return;
            }

            if (left > 0)
            {
                // **頼んだ先と見ている先が食い違っていないか。**
                //
                // 段階が進んで目的地が変わったのに、前の段階の経路を
                // まだ辿っていることがありうる。そのまま見守ると、
                // 別の場所へ向かいながら「着くのを待っている」ことになる。
                if (Vector3.DistanceSquared(this.issuedDestination, destination) > 1f)
                {
                    this.trace.Trouble(
                        "経路の終点が変わった",
                        $"{describe} 頼んだ先({this.issuedDestination.X:F0},{this.issuedDestination.Y:F0},{this.issuedDestination.Z:F0}) " +
                        $"→ いま見ている先({destination.X:F0},{destination.Y:F0},{destination.Z:F0}) 引き直します");

                    this.vnavmesh.TryStop();
                    this.pathIssued = false;
                }
                else
                {
                    return;
                }
            }
            else
            {
                // 経路を使い切ったが、まだ着いていない。引き直す。
                this.pathIssued = false;
            }
        }

        // 探索の結果を待っている。
        if (this.pending is { } task)
        {
            if (!task.IsCompleted)
            {
                this.Detail = $"{describe}の経路を探しています";
                return;
            }

            var mine = this.pendingGeneration == this.generation;
            this.pending = null;
            this.CancelPending();

            if (!mine)
            {
                // 世代が違う。段階が変わったあとの結果なので捨てる。
                this.trace.State("古い経路を捨てた", $"世代 {this.pendingGeneration} ≠ {this.generation}");
                return;
            }

            if (task.IsFaulted || task.IsCanceled)
            {
                this.OnPathRejected(describe, "経路探索に失敗しました");
                return;
            }

            var waypoints = task.Result;

            if (!this.Validate(waypoints, destination, out var why))
            {
                this.OnPathRejected(describe, why);
                return;
            }

            // **降下の経路は、形も見る。**
            //
            // 段に分けていない一本の経路が垂直落下になるなら、
            // まず段に分けて引き直す。それでも駄目なら向きを変える。
            if (this.Phase == ApproachPhase.Descending &&
                this.descentStages.Count == 0 &&
                !this.IsDescentAcceptable(waypoints, this.landing, out var shape))
            {
                this.trace.Trouble("垂直落下になる経路", $"{shape} 段に分けて引き直します");

                this.descentStages = this.BuildDescentStages(Player.Position, this.landing);
                this.descentStage = 0;
                this.pathIssued = false;
                this.vnavmesh.TryStop();
                return;
            }

            if (!this.vnavmesh.TryMoveAlong(waypoints, fly))
            {
                this.OnPathRejected(describe, "経路を渡せませんでした");
                return;
            }

            this.pathIssued = true;
            this.issuedDestination = destination;

            this.trace.Decision(
                $"{describe}の経路を渡した",
                $"{waypoints.Count}点 終点({destination.X:F0},{destination.Y:F0},{destination.Z:F0}) fly={fly}");

            return;
        }

        // 探索を始める。
        this.pendingCancel = new CancellationTokenSource();
        this.pendingGeneration = this.generation;

        if (!this.vnavmesh.TryPathfindCancelable(
                Player.Position,
                destination,
                fly,
                this.pendingCancel.Token,
                out var started) ||
            started is null)
        {
            this.CancelPending();
            this.Detail = $"{describe}の経路を頼めませんでした";
            return;
        }

        this.pending = started;
        this.Detail = $"{describe}の経路を探しています";
    }

    /// <summary>
    /// 経路が使えるかを確かめる。
    ///
    /// <b>末尾が目的地であることは、証明にならない。</b>
    /// vnavmesh の PathfindVolume は、探した結果の後ろに
    /// 指定した目的地をそのまま足す（NavmeshQuery.cs:226-229）。
    /// つまり探索が失敗していても末尾は目的地になる。
    /// 点の数と、途中の形を見る。
    /// </summary>
    private bool Validate(List<Vector3>? waypoints, Vector3 destination, out string why)
    {
        if (waypoints is null || waypoints.Count == 0)
        {
            why = "経路が空でした";
            return false;
        }

        foreach (var point in waypoints)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
            {
                why = "経路に読めない座標が入っていました";
                return false;
            }
        }

        // **1 点だけの経路は、目的地を足しただけのもの。**
        // 探索が何も見つけられなかったときにこうなる。
        // ただし目的地がすぐ近くなら、1 点でも正しい。
        if (waypoints.Count == 1 && Vector3.Distance(Player.Position, destination) > 15f)
        {
            why = "経路が目的地 1 点だけでした（探索できていません）";
            return false;
        }

        var last = waypoints[^1];

        // **この判定は飛行経路ではほぼ通る。**
        // PathfindVolume は結果の末尾に目的地をそのまま足すため
        // （NavmeshQuery.cs:226-229）、末尾は必ず目的地になる。
        // それでも地上経路では意味があるので残す。
        if (Vector3.Distance(last, destination) > 5f)
        {
            why = $"経路の終点が目的地から {Vector3.Distance(last, destination):F0}m 離れています";
            return false;
        }

        why = string.Empty;
        return true;
    }

    /// <summary>
    /// 降下の経路が、本当に降りているかを確かめる。
    ///
    /// <b>ここが C の肝心なところ。</b>
    /// vnavmesh は「指定の角度で降りる」API ではない。空間が開けていれば
    /// 斜めの経路になりうるが、「中央上空まで水平に飛んでから垂直に落ちる」
    /// 形の経路を返すこともある。それを検査せずに走らせると、
    /// 以前と同じ垂直落下になる。
    ///
    /// 末尾が目的地であることは証明にならない（PathfindVolume が
    /// そのまま足すため）。<b>途中の形を見る。</b>
    /// </summary>
    /// <returns>斜めに降りていれば true。</returns>
    private bool IsDescentAcceptable(List<Vector3> waypoints, Vector3 landingPoint, out string why)
    {
        var from = Player.Position;
        var totalDrop = from.Y - landingPoint.Y;
        var totalFlat = Flat(from, landingPoint);

        // ほとんど降りない、あるいはほとんど動かないなら、形を問う意味が無い。
        if (totalDrop <= LandingHeightMeters || totalFlat <= LandingFlatMeters)
        {
            why = string.Empty;
            return true;
        }

        // **最後の垂直落下がどれだけ残るかを見る。**
        //
        // 経路の各点で「まだ水平にどれだけ残っているか」と
        // 「まだ高さがどれだけ残っているか」を測り、
        // 水平が尽きた時点で高さが大きく残っていれば、それは垂直落下。
        var worstFlatAtDrop = 0f;
        var verticalTail = 0f;

        foreach (var point in waypoints)
        {
            var flatLeft = Flat(point, landingPoint);
            var dropLeft = point.Y - landingPoint.Y;

            // 水平にはもう着いているのに、高さが残っている点。
            if (flatLeft <= MaxVerticalTailFlatMeters && dropLeft > verticalTail)
            {
                verticalTail = dropLeft;
                worstFlatAtDrop = flatLeft;
            }
        }

        if (verticalTail > MaxVerticalTailMeters)
        {
            why = $"中央上空から {verticalTail:F0}m の垂直落下になります" +
                  $"（水平の残り {worstFlatAtDrop:F1}m）";

            return false;
        }

        why = string.Empty;
        return true;
    }

    /// <summary>
    /// 斜めに降りる中継点を作る。
    ///
    /// <b>補間した点へ直接向かわせてはいけない。</b>
    /// その点が地形の内側かどうかを誰も見ていない。
    /// ここで作るのは「探索を頼む区間の切れ目」で、
    /// 区間ごとに vnavmesh へ聞き、返った経路を検査して連結する。
    /// </summary>
    private List<Vector3> BuildDescentStages(Vector3 from, Vector3 to)
    {
        var stages = new List<Vector3>(DescentFractions.Length);

        foreach (var t in DescentFractions)
        {
            stages.Add(new Vector3(
                from.X + ((to.X - from.X) * t),
                from.Y + ((to.Y - from.Y) * t),
                from.Z + ((to.Z - from.Z) * t)));
        }

        return stages;
    }

    /// <summary>経路が使えなかった。進入の向きを変えて試す。</summary>
    private void OnPathRejected(string describe, string why)
    {
        this.trace.Trouble($"{describe}の経路を使えない", why);

        this.replans++;

        if (this.replans > MaxReplans)
        {
            this.Fail($"{describe}の経路が引けません（{why}）");
            return;
        }

        // 向きを変えて外周点を取り直す。
        this.generation++;
        this.vnavmesh.TryStop();
        this.pathIssued = false;
        this.SetPhase(ApproachPhase.PlanEntry, "別の向きから進入し直します");
    }

    /// <summary>
    /// 降りられる場所を、中心の近くで探す。
    ///
    /// <b>1 度だけ求めて固定する。</b>敵の位置は使わない。
    /// </summary>
    private Vector3? FindLanding(FateInfo fate)
    {
        // まず中心の真下の床。
        if (this.vnavmesh.TryPointOnFloorWide(this.centre, false, 5f, out var floor) &&
            floor is { } onFloor &&
            Flat(onFloor, this.centre) <= this.radius)
        {
            return onFloor;
        }

        // 中心が湖や崖の上だった。中心の周りのリングから探す。
        for (var ring = 1; ring <= 4; ring++)
        {
            var distance = this.radius * ring / 5f;

            for (var step = 0; step < 8; step++)
            {
                var angle = step * MathF.PI / 4f;
                var probe = this.centre with
                {
                    X = this.centre.X + (MathF.Cos(angle) * distance),
                    Z = this.centre.Z + (MathF.Sin(angle) * distance),
                };

                if (this.vnavmesh.TryNearestPointReachable(probe, 10f, 30f, out var near) &&
                    near is { } spot &&
                    Flat(spot, this.centre) <= this.radius)
                {
                    this.trace.State(
                        "着地点を中心からずらした",
                        $"{fate.Name} 中心から {Flat(spot, this.centre):F0}m");

                    return spot;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 外周の上空点を決める。
    ///
    /// いまいる側から入る。入れなければ向きを回して試す。
    /// </summary>
    private Vector3? PlanEntryPoint()
    {
        var here = Player.Position;

        // 中心から自分へ向かう水平の向き。
        var away = new Vector2(here.X - this.centre.X, here.Z - this.centre.Z);

        // 中心とほぼ同じ場所にいる。向きが決まらないので適当な向きから。
        var baseDirection = away.LengthSquared() > 1f
            ? Vector2.Normalize(away)
            : new Vector2(1f, 0f);

        // やり直した回数に応じて向きを回す。
        var turns = Math.Min(this.replans, ReplanAngles.Length - 1);

        for (var i = turns; i < ReplanAngles.Length; i++)
        {
            var radians = ReplanAngles[i] * MathF.PI / 180f;
            var cos = MathF.Cos(radians);
            var sin = MathF.Sin(radians);

            var direction = new Vector2(
                (baseDirection.X * cos) - (baseDirection.Y * sin),
                (baseDirection.X * sin) + (baseDirection.Y * cos));

            var distance = this.radius + EntryMarginMeters;

            var spot = new Vector3(
                this.centre.X + (direction.X * distance),
                0f,
                this.centre.Z + (direction.Y * distance));

            if (this.HeightForEntry(spot) is not { } y)
            {
                continue;
            }

            return spot with { Y = y };
        }

        return null;
    }

    /// <summary>
    /// 外周点の高さを決める。
    ///
    /// その場の床より少し上、かつ着地点まで指定の角度で降りられる高さ。
    /// 飛べる高さを超えないように抑える。
    /// </summary>
    private float? HeightForEntry(Vector3 spot)
    {
        // その場の床。無ければ、その向きは使えない。
        float floorY;

        if (this.vnavmesh.TryPointOnFloorWide(spot with { Y = this.centre.Y + 50f }, true, 10f, out var floor) &&
            floor is { } found)
        {
            floorY = found.Y;
        }
        else if (this.vnavmesh.TryNearestPoint(spot, 20f, 100f, out var near) && near is { } onMesh)
        {
            floorY = onMesh.Y;
        }
        else
        {
            return null;
        }

        // 着地点まで、指定の角度で降りるのに要る高さ。
        var horizontal = Flat(spot, this.landing);
        var slope = MathF.Tan(DescentAngleDegrees * MathF.PI / 180f);

        var y = MathF.Max(floorY + EntryClearanceMeters, this.landing.Y + (slope * horizontal));

        // **飛べた高さは「上限」ではない。**
        //
        // 覚えているのは「そこまでは行けた」という下限の証拠でしかなく、
        // 本当の上限はもっと高い可能性が高い。初めて来たマップや、
        // 低く飛んだだけのマップでは、記録がそのまま上限になってしまう。
        // それを上限として扱うと、必要な高さに外周点を置けず、
        // 指定の角度で降りられない。
        //
        // そこで、記録より上を目指すことは禁じない。<b>余裕を足して許す。</b>
        // 本当に頭打ちなら、その段階の上限（TakingOff / FlyToEntry）が
        // 有限時間で拾い、向きを変えるか地上の経路へ落ちる。
        // 「上限を読めていないなら保証しない」ので、抑え込みもしない。
        if (MountService.KnownCeiling is { } known)
        {
            var allowed = known + CeilingHeadroomMeters;

            if (y > allowed)
            {
                // ここまで抑えても着地点より低くなるなら、この向きは使えない。
                if (allowed <= this.landing.Y + EntryClearanceMeters)
                {
                    this.trace.State(
                        "この向きは使えない",
                        $"必要な高さ {y:F0} に対し、飛べた記録は {known:F0}（+{CeilingHeadroomMeters:F0} 余裕）");

                    return null;
                }

                this.trace.State(
                    "外周点の高さを抑えた",
                    $"{y:F0} → {allowed:F0}" +
                    $"（このマップで飛べた記録 {known:F0} に余裕 {CeilingHeadroomMeters:F0} を足した値。" +
                    "これは上限の保証ではない）");

                y = allowed;
            }
        }

        return y;
    }

    /// <summary>段階ごとの上限。</summary>
    private TimeSpan TimeoutFor(ApproachPhase phase) => phase switch
    {
        ApproachPhase.Mounting => MountTimeout,
        ApproachPhase.TakingOff => TakeoffTimeout,
        ApproachPhase.GroundConfirm => GroundConfirmTimeout,
        ApproachPhase.Dismounting => DismountTimeout,
        _ => PhaseTimeout,
    };

    /// <summary>段階に与えた時間を使い切ったときの始末。</summary>
    private void OnPhaseTimeout()
    {
        switch (this.Phase)
        {
            case ApproachPhase.TakingOff:
                // **飛べないことを、飛べたことにしない。**
                this.Fail($"離陸できませんでした（{MountService.DescribeFlightStatus()}）");
                return;

            case ApproachPhase.Mounting:
                this.Fail("マウントに乗れませんでした");
                return;

            case ApproachPhase.GroundConfirm:
                this.Fail("地表に降りられたことを確認できませんでした");
                return;

            case ApproachPhase.Dismounting:
                this.Fail("マウントから降りられませんでした");
                return;

            default:
                this.replans++;

                if (this.replans > MaxReplans)
                {
                    this.Fail($"{this.Phase} が終わりませんでした");
                    return;
                }

                this.trace.Trouble(
                    "進入が進まない",
                    $"段階={this.Phase} 向きを変えて {this.replans} 回目をやり直します");

                this.generation++;
                this.CancelPending();
                this.vnavmesh.TryStop();
                this.pathIssued = false;
                this.SetPhase(ApproachPhase.PlanEntry, "別の向きから進入し直します");
                return;
        }
    }

    private void SetPhase(ApproachPhase phase, string detail)
    {
        if (this.Phase != phase)
        {
            this.trace.State(
                "進入の段階",
                $"{this.Phase} → {phase}（開始から {(DateTime.UtcNow - this.startedUtc).TotalSeconds:F0}秒）");
        }

        this.Phase = phase;
        this.Detail = detail;
        this.phaseSinceUtc = DateTime.UtcNow;
    }

    private void Fail(string why)
    {
        this.generation++;
        this.CancelPending();
        this.vnavmesh.TryStop();
        this.pathIssued = false;
        this.FailureReason = why;
        this.Phase = ApproachPhase.Failed;
        this.Detail = why;
        this.anomalyLog.Warn("Fate", $"FATE へ飛んで入れませんでした: {why}");
    }

    private void CancelPending()
    {
        var cts = this.pendingCancel;
        this.pendingCancel = null;
        this.pending = null;

        if (cts is null)
        {
            return;
        }

        // **取り消しに失敗しても必ず捨てる。**
        // Cancel と Dispose を同じ try に入れていたため、
        // Cancel が投げると Dispose を飛ばして漏らしていた。
        try
        {
            cts.Cancel();
        }
        catch
        {
            // 取り消せなくても、世代番号で結果を捨てる。
        }

        try
        {
            cts.Dispose();
        }
        catch
        {
            // 捨てられなくても動きは変えない。
        }
    }

    /// <summary>水平距離。飛行中は真上に大きな差があるので、高さを混ぜない。</summary>
    private static float Flat(Vector3 a, Vector3 b)
        => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
}
