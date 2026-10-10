using System;
using System.Numerics;
using System.Threading;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using SmoothNav.Core;

namespace AutoCollector.Automation;

public enum MoveStatus
{
    /// <summary>移動中。</summary>
    Moving,

    /// <summary>到着した。</summary>
    Arrived,

    /// <summary>同じ場所から動いていない。</summary>
    Stuck,

    /// <summary>
    /// vnavmesh は走り終わったが、目的地まで届いていない。
    ///
    /// NPC がカウンターの内側に立っている場合など、目的地そのものが
    /// ナビメッシュの外にあると、近づけるところまで行って終わる。
    /// この状態から放置しても二度と動かない。
    /// ただし話しかけられる距離には入っていることが多いため、失敗ではない。
    /// </summary>
    ShortOfTarget,

    /// <summary>失敗した。エリアが変わった、vnavmesh が使えない等。</summary>
    Failed,
}

/// <summary>
/// vnavmesh を使ったエリア内の移動。
///
/// vnavmesh は経路探索に失敗しても例外を握り潰してログを出すだけで、
/// 経路が空のまま終わる。つまり「移動失敗」と「移動完了」が IPC の状態としては同じになる。
/// そのため距離とタイムアウトの確認が必須になる。
///
/// また vnavmesh 側の設定でスタック時に自動再試行が働くため、
/// 走行フラグは false と true を往復する。1 回 false を見ただけで完了と判定してはいけない。
///
/// <b>曲線にする移動（<see cref="SmoothMoveService"/>）。</b>
/// 有効なら vnavmesh 任せ（SimpleMove）で頼まず、自分で経路を探して角を曲線に整え、その経路を辿らせる。
/// 整えられない・間に合わないときは公式の経路を辿らせ、経路が無い・頼めないときは今までどおり vnavmesh 任せで頼む。
/// 到着・止まった・届かないの見張りは今までと同じ。
/// </summary>
public sealed class NavigationService(AnomalyLog anomalyLog, VnavmeshIpc vnavmesh, SmoothMoveService? smooth = null)
{
    /// <summary>
    /// 到着したと認める前に、条件を満たし続ける必要のある判定回数。
    /// vnavmesh は再試行のたびに走行フラグを false と true で往復させるため、
    /// 1 回 false を見ただけで完了と判定してはいけない。
    /// 呼び出し間隔が 100 ミリ秒なので、3 回で約 0.3 秒の安定を要求することになる。
    /// </summary>
    private const int RequiredStableFrames = 3;

    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(15);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly VnavmeshIpc vnavmesh = vnavmesh;
    private readonly SmoothMoveService? smooth = smooth;

    /// <summary>曲線に整える部品の入れ物。FATE の接近（<see cref="FateApproach"/>）も同じものを使う。</summary>
    internal SmoothMoveService? Smooth => this.smooth;

    /// <summary>曲線に整えた経路の頼み先（移動の部品ごとに 1 つ）。</summary>
    private RoutePlanner? planner;

    /// <summary>自分で頼んだ経路探索の取り消し。目的地を変えた・止めたときに取り消す（遠い探索は十数秒走り続けるため）。</summary>
    private CancellationTokenSource? searchCancel;

    /// <summary>整えた経路が使えず vnavmesh 任せで頼み直したが、別の探索中で断られ、受け取られるのを待っているか。</summary>
    private bool fallbackPending;
    private DateTime fallbackSinceUtc;

    /// <summary>
    /// 直前の <see cref="BeginMove(Vector3, float, bool, out string)"/> が
    /// 「別の経路探索の最中」で断られたか。
    ///
    /// <b>これは失敗ではない。</b>少し待てば受け取れる。
    /// 呼び出し側は、これが true のときに「辿り着けない」と数えてはいけない。
    /// </summary>
    public bool Busy { get; private set; }

    private bool moveIssued;

    /// <summary>直前の移動を飛行で頼んだか。引き直すときに同じ条件を使う。</summary>
    private bool issuedWithFly;
    private int stableFrames;
    private int idleShortFrames;
    private uint startTerritory;
    private Vector3 lastPosition;
    private DateTime lastMovementUtc;
    private bool retriedAfterStuck;

    public bool IsAvailable => this.vnavmesh.IsLoaded;

    /// <summary>
    /// 目的地をナビメッシュ上の床へスナップする。
    /// 配置ファイル由来の座標はメッシュに乗っていないことがあり、経路探索が失敗しやすい。
    /// </summary>
    public bool TrySnapToFloor(Vector3 position, out Vector3 snapped)
    {
        snapped = position;

        // まずメッシュ上の最近傍を探す。建物の中でもその場の床に乗る。
        // 探索範囲を狭くしているのは、遠くの別の場所を拾わないため。
        if (this.vnavmesh.TryNearestPoint(position, 2f, 2f, out var nearest) &&
            nearest is not null &&
            Vector3.Distance(nearest.Value, position) <= 3f)
        {
            snapped = nearest.Value;
            return true;
        }

        // 見つからなければ真下の床を探す。ただし別の階層を拾いやすいので許容幅を狭くする。
        if (!this.vnavmesh.TryPointOnFloor(position, out var result) || result is null)
        {
            return false;
        }

        if (Vector3.Distance(result.Value, position) > 3f)
        {
            return false;
        }

        snapped = result.Value;
        return true;
    }

    /// <summary>
    /// 目的地を変えて移動をやり直す。
    /// 目的地だけ書き換えて移動を出し直さないと、経路と到着判定がずれる。
    ///
    /// **先に止めてはいけない。**
    /// vnavmesh の MoveTo は経路探索を積むだけで、今の経路は消さない。
    /// 差し替わるのは探索が終わってからで、それまでは元の経路を歩き続ける。
    /// （AsyncMoveRequest.cs: MoveTo は _pendingTask を積むだけ、
    ///   Update が IsCompleted を見て初めて _follow.Move を呼ぶ）
    ///
    /// ここで止めると、探索が終わるまで棒立ちになる。
    /// 街中では数秒かかるため、目に見えて固まる。止めなければ
    /// 元の目的地へ歩きながら、探索が終わった時点で新しい経路へ移る。
    /// </summary>
    public bool Reissue(Vector3 destination, float range, out string failureReason)
    {
        var wasMoving = this.moveIssued;

        if (this.BeginMove(destination, range, out failureReason))
        {
            return true;
        }

        // 引き直しに失敗しても、元の経路はまだ生きている。
        // BeginMove は入口で moveIssued を倒すので、そのままだと
        // 移動していないことになり、Tick が失敗を返して交換ごと中止になる。
        // 歩いている事実は変わらないため、元の状態へ戻す。
        this.moveIssued = wasMoving;
        return false;
    }

    /// <summary>移動を開始する。1 回だけ発行し、以降は状態を監視するだけにする。</summary>
    public bool BeginMove(Vector3 destination, float range, out string failureReason)
        => this.BeginMove(destination, range, false, out failureReason);

    /// <summary>
    /// 移動を開始する。飛ぶかどうかを指定できる。
    ///
    /// <b>飛ぶのは呼び出し側が決める。</b>
    /// 交換や呼び鈴のように街なかの短い移動では飛ばない。
    /// FATE のように数百メートル離れた目的地へ向かうときだけ飛ぶ。
    ///
    /// fly を true にしても、乗っていなければ vnavmesh は走って向かう。
    /// マウントに乗せるのは <see cref="MountService"/> の役目で、
    /// ここは「飛べる状態なら飛ぶ経路を引く」ことだけを頼む。
    /// </summary>
    public bool BeginMove(Vector3 destination, float range, bool fly, out string failureReason)
    {
        this.moveIssued = false;
        this.stableFrames = 0;
        this.idleShortFrames = 0;
        this.retriedAfterStuck = false;
        this.startTerritory = Svc.ClientState.TerritoryType;
        this.lastPosition = Player.Available ? Player.Position : default;
        this.lastMovementUtc = DateTime.UtcNow;

        // **入口で Busy を倒す。**
        //
        // 倒していなかったため、一度 Busy になったあとで vnavmesh が
        // 落ちた・未導入になったときに、古い Busy を引き継いでいた。
        // 呼び出し側は Busy を「失敗ではない、待とう」と読むので、
        // 本当の失敗を待ち続けることになる。しかも待っている間は
        // 移動の時間制限の判定にも届かない（FateRunner の TickMoving）。
        this.Busy = false;

        if (!this.vnavmesh.IsLoaded)
        {
            failureReason = "vnavmesh が導入されていないため移動できません";
            return false;
        }

        if (!this.vnavmesh.TryIsReady(out var ready))
        {
            failureReason = "vnavmesh の状態を取得できません";
            return false;
        }

        if (!ready)
        {
            // **これも「まだ受け取れない」。**
            //
            // エリアを移った直後はメッシュの読み込みが終わっていない。
            // 待てば使えるようになるので、呼び出し側が「辿り着けない」と
            // 数えてはいけない。数えていたため、テレポ直後に 2 回失敗しただけで
            // その FATE を候補から外していた。
            this.Busy = true;
            failureReason = "このエリアのナビメッシュがまだ利用できません";
            return false;
        }

        // 曲線にする移動：自分で経路を探し、整えてから辿らせる（受け取りは Tick）。
        // 探索を頼めないときは、今までどおり vnavmesh 任せで頼む。
        //
        // **今の経路は止めない。** 整った経路を辿らせた時点で差し替わる（Reissue の約束と同じ）。
        this.CancelPlanning();
        if (this.smooth is { Enabled: true } && Player.Available)
        {
            this.searchCancel = new CancellationTokenSource();
            if (this.vnavmesh.TryPathfindCancelable(Player.Position, destination, fly, this.searchCancel.Token, out var search) &&
                search is not null)
            {
                this.planner ??= this.smooth.CreatePlanner();
                this.planner.Begin(search, fly, range, this.smooth.Now);
                this.moveIssued = true;
                this.issuedWithFly = fly;
                failureReason = string.Empty;
                return true;
            }

            this.CancelPlanning();
        }

        if (!this.vnavmesh.TryMoveCloseTo(destination, fly, range, out var accepted))
        {
            failureReason = "vnavmesh へ移動を依頼できませんでした";
            return false;
        }

        if (!accepted)
        {
            // **これは失敗ではなく「まだ受け取れない」。**
            //
            // vnavmesh の経路探索は非同期で、前の探索が終わる前に頼むと断られる。
            // 少し待てば受け取れるので、呼び出し側が「辿り着けない」と
            // 数えてはいけない。数えていたため、FATE へ向かい始めた直後に
            // 2 回断られただけで、その FATE を候補から外していた
            // （2026-09-25 実測。5 つの FATE を 0.3 秒で全部外していた）。
            this.Busy = true;
            failureReason = "vnavmesh が別の経路探索を実行中です";
            return false;
        }

        this.Busy = false;

        this.moveIssued = true;
        this.issuedWithFly = fly;
        failureReason = string.Empty;
        return true;
    }

    /// <summary>毎フレーム呼ぶ。到着・スタック・失敗を判定する。</summary>
    public MoveStatus Tick(Vector3 destination, float range)
    {
        if (!this.moveIssued)
        {
            return MoveStatus.Failed;
        }

        if (!Player.Available)
        {
            return MoveStatus.Moving;
        }

        // エリアが変わったら経路は破棄されている。続行してはいけない。
        if (Svc.ClientState.TerritoryType != this.startTerritory)
        {
            this.anomalyLog.Warn("Navigation", "移動中にエリアが変わりました");
            return MoveStatus.Failed;
        }

        var position = Player.Position;

        // 曲線にする移動：整った経路を受け取って辿らせる。最初の経路を渡すまで（探索・整える間）は移動中。
        if ((this.planner is { Active: true } || this.fallbackPending) &&
            this.TickPlanner(position, destination, range) is { } planning)
        {
            return planning;
        }

        // 走行状態を 3 つとも見る。1 つでも進行中なら移動継続とみなす。
        if (!this.vnavmesh.TryPathIsRunning(out var running) ||
            !this.vnavmesh.TryNavPathfindInProgress(out var navPathfinding) ||
            !this.vnavmesh.TrySimpleMovePathfindInProgress(out var simplePathfinding))
        {
            return MoveStatus.Failed;
        }

        var idle = !running && !navPathfinding && !simplePathfinding;
        var distance = Vector3.Distance(position, destination);

        if (idle && distance <= range + 2f)
        {
            this.stableFrames++;
            if (this.stableFrames >= RequiredStableFrames)
            {
                return MoveStatus.Arrived;
            }

            return MoveStatus.Moving;
        }

        this.stableFrames = 0;

        // vnavmesh が走り終わったのに届いていない。
        //
        // この状態から放置しても二度と動かない。以前はスタック判定（15 秒）が
        // 拾うまで棒立ちになっていた。走り終わったことはその場で分かるので待つ必要がない。
        //
        // 目的地に届かない理由の多くは、NPC がカウンターの内側など
        // ナビメッシュの外に立っていること。近づけるところまでは行けているので、
        // 話しかけられるかどうかは呼び出し側に判断させる。
        if (idle)
        {
            this.idleShortFrames++;
            if (this.idleShortFrames >= RequiredStableFrames)
            {
                this.anomalyLog.Info(
                    "Navigation",
                    $"経路の終点に着きましたが目的地まで {distance:F1} ヤルム残っています");
                return MoveStatus.ShortOfTarget;
            }

            return MoveStatus.Moving;
        }

        this.idleShortFrames = 0;

        // スタック判定。座標がほとんど動いていない状態が続いたら 1 度だけ再発行する。
        if (Vector3.DistanceSquared(position, this.lastPosition) > 0.01f)
        {
            this.lastPosition = position;
            this.lastMovementUtc = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - this.lastMovementUtc > StuckWindow)
        {
            if (this.retriedAfterStuck)
            {
                return MoveStatus.Stuck;
            }

            this.anomalyLog.Warn("Navigation", "移動が止まったため、経路を引き直します");
            this.retriedAfterStuck = true;
            this.lastMovementUtc = DateTime.UtcNow;

            // 引き直しは今までどおり vnavmesh 任せ（曲線の経路で止まったなら、公式の経路で動かす）。
            this.CancelPlanning();
            this.vnavmesh.TryStop();

            // **元の移動のしかたを引き継ぐ。**
            // ここで fly=false に固定していたため、飛んで向かっていたのに
            // 引き直した途端、地上の経路になっていた。
            if (!this.vnavmesh.TryMoveCloseTo(destination, this.issuedWithFly, range, out var accepted) || !accepted)
            {
                return MoveStatus.Stuck;
            }
        }

        return MoveStatus.Moving;
    }

    /// <summary>移動を止める。自分が開始していない場合は何もしない。</summary>
    public void Stop()
    {
        if (!this.moveIssued)
        {
            return;
        }

        this.CancelPlanning();
        this.vnavmesh.TryStop();
        this.moveIssued = false;
    }

    /// <summary>
    /// 整った経路を受け取って辿らせる。探索・整える間と、vnavmesh 任せの頼み直しを待つ間は <see cref="MoveStatus.Moving"/>。
    /// 経路を渡し終えたら null（以後は今までの見張り）。
    /// </summary>
    private MoveStatus? TickPlanner(Vector3 position, Vector3 destination, float range)
    {
        if (this.fallbackPending)
        {
            // vnavmesh が別の探索中で断った。受け取られるまで頼み直す。止まった判定と同じ時間で諦める。
            if (this.vnavmesh.TryMoveCloseTo(destination, this.issuedWithFly, range, out var accepted) && accepted)
            {
                this.fallbackPending = false;
                this.lastMovementUtc = DateTime.UtcNow;
                return MoveStatus.Moving;
            }

            return DateTime.UtcNow - this.fallbackSinceUtc > StuckWindow ? MoveStatus.Stuck : MoveStatus.Moving;
        }

        if (this.planner!.Tick(position, this.smooth!.Now) is { } plan)
        {
            this.Apply(plan, destination, range);
            if (this.fallbackPending)
            {
                // 断られた。頼み直しは次の見張りから。
                return MoveStatus.Moving;
            }
        }

        if (this.planner is { Pending: true })
        {
            // 探索・整える間は vnavmesh が走っていない。止まった・届かないと数えない。
            this.lastMovementUtc = DateTime.UtcNow;
            this.lastPosition = position;
            return MoveStatus.Moving;
        }

        return null;
    }

    /// <summary>受け取った経路を辿らせる。経路が無いときは今までどおり vnavmesh 任せで頼む。</summary>
    private void Apply(RoutePlan plan, Vector3 destination, float range)
    {
        this.anomalyLog.Info("Navigation", "移動の経路：" + SmoothMoveService.Describe(plan));
        if (!plan.Fallback)
        {
            if (plan.Waypoints.Count == 0)
            {
                // 既に目的地から range の内側。動かなくてよい（到着は今までの見張りが判定する）。
                return;
            }

            // 途中からの乗り換えは、まだ経路を辿っている間だけ（辿り終えた後に足すと、着いた所から動き直す）。
            if (plan.Splice && (!this.vnavmesh.TryPathIsRunning(out var running) || !running))
            {
                return;
            }

            if (this.vnavmesh.TryMoveAlong([.. plan.Waypoints], this.issuedWithFly))
            {
                this.lastMovementUtc = DateTime.UtcNow;
                return;
            }
        }

        if (!this.vnavmesh.TryMoveCloseTo(destination, this.issuedWithFly, range, out var accepted) || !accepted)
        {
            this.fallbackPending = true;
            this.fallbackSinceUtc = DateTime.UtcNow;
        }
    }

    /// <summary>自分で頼んだ探索と整える処理を取り消す。</summary>
    private void CancelPlanning()
    {
        this.planner?.Cancel();
        this.fallbackPending = false;
        if (this.searchCancel is not null)
        {
            // 取り消しの合図は破棄しない（vnavmesh の探索が後から合図を読むことがある）。
            this.searchCancel.Cancel();
            this.searchCancel = null;
        }
    }
}
