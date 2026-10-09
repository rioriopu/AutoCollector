using System;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

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
/// <b>ここは vnavmesh の代わりではない。公式に足りない分だけを足す層。</b>
///
/// 経路探索・経路追従・障害物回避・飛行経路の生成は、すべて公式 vnavmesh が持つ。
/// こちらで作り直さない。公式の実装のほうが遥かに作り込まれているうえ、
/// 二重に持つと更新に追従できなくなる。
///
/// 一方で、vnavmesh は「言われた点へ経路を引いて走る」ところまでが仕事で、
/// 自動化の側が困るところは面倒を見てくれない。その差分がここの役目になる。
///
/// <list type="number">
/// <item>
/// <b>目的地のサニタイズ。</b>
/// NPC やモブの座標はメッシュに乗っていないことがある。
/// vnavmesh の <c>NearestPoint</c> は到達できない孤島（柵の内側、
/// 別の階層の棚）も平気で返すため、そこへ向かわせると永久に着かない。
/// <c>NearestPointReachable</c> は自分がいる連結成分からだけ選ぶので、
/// こちらを使う。どちらを使うかは公式が呼び出し側へ委ねている。
/// </item>
/// <item>
/// <b>到着の自前判定。</b>
/// vnavmesh は経路探索に失敗しても例外を握り潰してログを出すだけで、
/// 経路が空のまま終わる。つまり「移動失敗」と「移動完了」が
/// IPC の状態としては同じになる。距離で見るしかない。
/// </item>
/// <item>
/// <b>詰まり検知と脱出。</b>
/// vnavmesh 側の自動再試行は、こちらが何をしたいかを知らない。
/// 進めていないことを自分で測り、跳ねて外して引き直す。
/// </item>
/// <item>
/// <b>引き直しの間隔制御。</b>
/// vnavmesh は毎フレーム <c>MoveTo</c> を投げても受け取ってしまい、
/// 探索を積んでは捨てるを繰り返して暴れる。最短間隔をこちらで持つ。
/// </item>
/// <item>
/// <b>追従の許容値（Tolerance）の持ち主を 1 つにする。</b>
/// この設定は vnavmesh 全体で 1 つしかない。他のプラグインが
/// 変えていることがあるので、移動を始めるたびに入れ直す。
/// </item>
/// <item>
/// <b>外部プラグインとの移動権の受け渡し。</b>
/// BMR の AI も同じキャラクターを動かそうとする。取り合うと
/// 前にも後ろにも進まない。こちらが動かしている間だけ
/// 協調的に止めてもらい、<b>終わったら必ず戻す</b>。
/// </item>
/// </list>
///
/// また vnavmesh 側の設定でスタック時に自動再試行が働くため、
/// 走行フラグは false と true を往復する。1 回 false を見ただけで完了と判定してはいけない。
/// </summary>
public sealed class NavigationService(AnomalyLog anomalyLog, VnavmeshIpc vnavmesh, BossModIpc bossMod)
{
    /// <summary>
    /// 到着したと認める前に、条件を満たし続ける必要のある判定回数。
    /// vnavmesh は再試行のたびに走行フラグを false と true で往復させるため、
    /// 1 回 false を見ただけで完了と判定してはいけない。
    /// 呼び出し間隔が 100 ミリ秒なので、3 回で約 0.3 秒の安定を要求することになる。
    /// </summary>
    private const int RequiredStableFrames = 3;

    /// <summary>
    /// 経路を引き直す最短間隔。
    ///
    /// <b>毎フレーム投げてはいけない。</b>
    /// vnavmesh の MoveTo は探索を積むだけで、積んだ時点では何も起きない
    /// （AsyncMoveRequest.cs: _pendingTask を置き、Update が IsCompleted を
    /// 見てから _follow.Move を呼ぶ）。毎フレーム投げると、終わる前に
    /// 次の探索で上書きされ続け、いつまでも走り出さない。
    /// </summary>
    private static readonly TimeSpan ReissueInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 移動の層の詰まり判定。この時間の間に <see cref="StallProgressMeters"/>
    /// も進めていなければ、引っかかっているものとして手当てする。
    ///
    /// 走っていれば毎秒 6 ヤルム前後は進むので、1 ヤルムはかなり低い線引き。
    /// 普通に走れているときに踏むことはない。
    /// </summary>
    private static readonly TimeSpan StallWindow = TimeSpan.FromMilliseconds(1000);

    /// <summary>詰まっていないと認める、<see cref="StallWindow"/> の間の移動量。</summary>
    private const float StallProgressMeters = 1.0f;

    /// <summary>
    /// 跳んだあと、引き直すまでに置く間。
    ///
    /// 着地した直後はまだ姿勢が戻っていない。その場で引き直すと
    /// 跳ぶ前と同じ場所から同じ経路を引くことになる。
    /// </summary>
    private static readonly TimeSpan JumpSettleDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 作業の層の詰まり判定。これを過ぎたら呼び出し側へ返して、
    /// 別の手（迂回・力づくの脱出・その目的地を諦める）を打たせる。
    ///
    /// <b>移動の層より十分長く取る。</b>
    /// 跳ねて外す手当てが効くかどうかを見てから返したい。
    /// </summary>
    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(10);

    /// <summary><see cref="StuckWindow"/> の間に、これだけ進めば詰まっていない。</summary>
    private const float StuckProgressMeters = 1.0f;

    /// <summary>1 回の移動で、跳ねて外すのを試す上限。</summary>
    private const int MaxEscapes = 2;

    /// <summary>
    /// 追従の許容値の既定。vnavmesh 自身の既定と同じ値。
    ///
    /// 大きいほど経路を端折って曲がり角を内側に切る。
    /// 狭い通路ではそれが壁への接触になるため、呼び出し側が
    /// <see cref="SetTolerance"/> で詰めることがある。
    /// </summary>
    public const float DefaultTolerance = 0.25f;

    /// <summary>目的地をメッシュへ乗せるときに広げていく探索範囲（水平, 垂直）。</summary>
    private static readonly (float Flat, float Height)[] SanitiseSteps =
    [
        (3f, 5f),
        (6f, 10f),
        (12f, 20f),
    ];

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly VnavmeshIpc vnavmesh = vnavmesh;
    private readonly BossModIpc bossMod = bossMod;

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

    /// <summary>
    /// 実際に vnavmesh へ渡した目的地。
    ///
    /// 呼び出し側が渡してきた生の座標とは違うことがある
    /// （メッシュへ乗せ直しているため）。引き直すときはこちらを使う。
    /// </summary>
    private Vector3 issuedDestination;

    private int stableFrames;
    private int idleShortFrames;
    private uint startTerritory;

    /// <summary>移動の層の見張り。</summary>
    private Vector3 stallPosition;
    private DateTime stallSinceUtc;

    /// <summary>作業の層の見張り。</summary>
    private Vector3 lastPosition;
    private DateTime lastMovementUtc;

    private DateTime lastIssueUtc;
    private int escapes;

    /// <summary>跳んだ時刻。<c>default</c> なら跳んでいない。</summary>
    private DateTime escapeJumpedUtc;

    /// <summary>いま入れてある追従の許容値。移動を始めるたびに入れ直す。</summary>
    private float desiredTolerance = DefaultTolerance;

    /// <summary>BMR の AI の移動を、こちらが止めているか。</summary>
    private bool pausedExternalMovement;

    public bool IsAvailable => this.vnavmesh.IsLoaded;

    /// <summary>
    /// 移動権の受け渡し先。
    ///
    /// <b>プリセットの移動は、プリセットの名前を知っている側にしか止められない。</b>
    /// BMR で実際にキャラクターを動かすのは、有効なプリセットの中の
    /// NormalMovement モジュール。それを黙らせるには
    /// 一時方針をそのプリセットへ入れる必要があり、名前を持っているのは
    /// <see cref="FateRunner"/>。こちらは「いつ動かし始めて、いつ終わったか」
    /// しか知らない。そこで、知っている側に差し込んでもらう。
    ///
    /// true で呼ばれたら黙る、false で呼ばれたら戻す。
    /// 差し込まれていなければ何もしない（交換や呼び鈴の移動では
    /// プリセットを入れていないため、止めるものが無い）。
    /// </summary>
    public Action<bool>? ExternalMovementGate { get; set; }

    /// <summary>
    /// 追従の許容値を決める。
    ///
    /// <b>この設定は vnavmesh 全体で 1 つしかない。</b>
    /// 直に <c>SetTolerance</c> を呼ぶと、こちらが移動を始め直したときに
    /// 既定へ戻してしまい、詰めたつもりが効かない。持ち主をここに 1 つへまとめる。
    /// </summary>
    public void SetTolerance(float meters)
    {
        this.desiredTolerance = meters;
        this.vnavmesh.TrySetPathTolerance(meters);
    }

    /// <summary>
    /// 目的地をナビメッシュ上の床へスナップする。
    /// 配置ファイル由来の座標はメッシュに乗っていないことがあり、経路探索が失敗しやすい。
    /// </summary>
    public bool TrySnapToFloor(Vector3 position, out Vector3 snapped)
    {
        snapped = position;

        // まずメッシュ上の最近傍を探す。建物の中でもその場の床に乗る。
        //
        // **NearestPoint ではなく NearestPointReachable を使う。**
        // NearestPoint は距離だけで選ぶので、柵の内側や別の階層の棚のように
        // 到達できない点を返す。そこへ向かわせると、経路が引けないまま
        // 延々と引き直すことになる。Reachable は自分がいる連結成分からだけ選ぶ。
        if (this.vnavmesh.TryNearestPointReachable(position, 2f, 2f, out var nearest) &&
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
        // **引き直しには最短間隔を置く。**
        //
        // 呼び出し側は動く相手（モブ、納品 NPC）を追うために毎フレーム
        // ここへ来る。そのたびに探索を積むと、終わる前に次で上書きされ続け、
        // いつまでも走り出さない。間隔の内なら「いま引き直す必要はない」として
        // 成功を返す。元の経路はまだ生きているので、歩き続ける。
        if (this.moveIssued && DateTime.UtcNow - this.lastIssueUtc < ReissueInterval)
        {
            failureReason = string.Empty;
            return true;
        }

        var wasMoving = this.moveIssued;

        if (this.BeginMove(destination, range, this.issuedWithFly, out failureReason))
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
        this.escapes = 0;
        this.escapeJumpedUtc = default;
        this.startTerritory = Svc.ClientState.TerritoryType;
        this.lastPosition = Player.Available ? Player.Position : default;
        this.lastMovementUtc = DateTime.UtcNow;
        this.stallPosition = this.lastPosition;
        this.stallSinceUtc = this.lastMovementUtc;

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

        // **追従の許容値を毎回入れ直す。**
        //
        // この設定は vnavmesh 全体で 1 つしかなく、他のプラグインが
        // 変えていることがある。移動のたびに、こちらが望む値へ揃える。
        this.vnavmesh.TrySetPathTolerance(this.desiredTolerance);

        // 目的地をメッシュへ乗せ直す。飛ぶときは空中の点を狙っているので触らない。
        var target = fly ? destination : this.Sanitise(destination);

        if (!this.vnavmesh.TryMoveCloseTo(target, fly, range, out var accepted))
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
        this.issuedDestination = target;
        this.lastIssueUtc = DateTime.UtcNow;

        // ここから先はこちらが動かす。BMR には手を引いてもらう。
        this.TakeOverExternalMovement();

        failureReason = string.Empty;
        return true;
    }

    /// <summary>毎フレーム呼ぶ。到着・スタック・失敗を判定する。</summary>
    public MoveStatus Tick(Vector3 destination, float range)
    {
        if (!this.moveIssued)
        {
            return this.Finish(MoveStatus.Failed);
        }

        if (!Player.Available)
        {
            return MoveStatus.Moving;
        }

        // エリアが変わったら経路は破棄されている。続行してはいけない。
        if (Svc.ClientState.TerritoryType != this.startTerritory)
        {
            this.anomalyLog.Warn("Navigation", "移動中にエリアが変わりました");
            return this.Finish(MoveStatus.Failed);
        }

        var position = Player.Position;
        var now = DateTime.UtcNow;

        // 走行状態を 3 つとも見る。1 つでも進行中なら移動継続とみなす。
        if (!this.vnavmesh.TryPathIsRunning(out var running) ||
            !this.vnavmesh.TryNavPathfindInProgress(out var navPathfinding) ||
            !this.vnavmesh.TrySimpleMovePathfindInProgress(out var simplePathfinding))
        {
            return this.Finish(MoveStatus.Failed);
        }

        var idle = !running && !navPathfinding && !simplePathfinding;
        var distance = Vector3.Distance(position, destination);

        if (idle && distance <= range + 2f)
        {
            this.stableFrames++;
            if (this.stableFrames >= RequiredStableFrames)
            {
                // **ここでは移動権を返さない。**
                //
                // 着いたことを返しても、呼び出し側が動かすのを
                // やめたとは限らない（動く相手を追っている最中は、
                // 着いた・引き直すを行き来する）。そのたびに BMR へ
                // 返して奪い直すと、相手の移動が途切れ途切れになる。
                // 返すのは Stop() を受けたとき。呼び出し側は到着時に必ず呼ぶ。
                return MoveStatus.Arrived;
            }

            return MoveStatus.Moving;
        }

        this.stableFrames = 0;

        // vnavmesh が走り終わったのに届いていない。
        //
        // この状態から放置しても二度と動かない。以前はスタック判定が
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

                // 到着と同じ扱い。返すのは Stop() を受けたとき。
                return MoveStatus.ShortOfTarget;
            }

            return MoveStatus.Moving;
        }

        this.idleShortFrames = 0;

        // ---- 移動の層：跳ねて外し、引き直す ----
        this.TickStallEscape(position, now, running, navPathfinding || simplePathfinding, range);

        // ---- 作業の層：ここまで来たら呼び出し側へ返す ----
        //
        // **移動の層より長く待つ。** 跳ねて外す手当てが効いたかどうかを
        // 見てから返したい。返したあとは、呼び出し側が迂回なり
        // 力づくの脱出なり、別の手を打つ。
        if (Vector3.DistanceSquared(position, this.lastPosition) > StuckProgressMeters * StuckProgressMeters)
        {
            this.lastPosition = position;
            this.lastMovementUtc = now;
        }
        else if (now - this.lastMovementUtc > StuckWindow)
        {
            this.anomalyLog.Warn(
                "Navigation",
                $"{StuckWindow.TotalSeconds:F0} 秒間 {StuckProgressMeters:F0} ヤルムも進めていません");

            return this.Finish(MoveStatus.Stuck);
        }

        return MoveStatus.Moving;
    }

    /// <summary>移動を止める。</summary>
    public void Stop()
    {
        // **移動権の返却は、自分が発行したかどうかに関わらず行う。**
        //
        // 以前は moveIssued を見て早々に返していた。移動を頼んだあとに
        // 経路が失敗して moveIssued が倒れると、BMR を止めたまま
        // 誰も戻さないことになり、BMR が二度と動かなかった。
        this.ReleaseExternalMovement();

        if (!this.moveIssued)
        {
            return;
        }

        this.vnavmesh.TryStop();
        this.moveIssued = false;
        this.escapeJumpedUtc = default;
    }

    /// <summary>
    /// 外部プラグインから奪った移動権を返す。
    ///
    /// <b>緊急停止やプラグインの終了時にも必ず通す。</b>
    /// 戻し忘れると、こちらを止めたあとも BMR が動けないままになる。
    /// </summary>
    public void ReleaseExternalMovement()
    {
        if (!this.pausedExternalMovement)
        {
            return;
        }

        this.pausedExternalMovement = false;

        // 外せなくても立て直さない。立てたままにするより、先へ進む。
        //
        // **ここでは記録しない。** 失敗の記録は IPC の層が持っており、
        // そちらは同じ内容を間引いてくれる。ここでも書くと、
        // 移動のたびに同じ警告が並んで他が読めなくなる
        // （2026-10-09 実測。FATE 周回中、警告がログを埋めていた）。
        this.bossMod.TryPauseMovement(false);

        // **こちらは必ず通す。** 一時方針を入れたまま戻さないと、
        // 戦闘へ渡したあとも BMR が動かず、敵に近づかなくなる。
        try
        {
            this.ExternalMovementGate?.Invoke(false);
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Navigation", $"BMR の移動を戻せませんでした: {ex.Message}");
        }
    }

    // ---- 部品 ----

    /// <summary>
    /// 目的地をメッシュへ乗せ直す。
    ///
    /// <b>乗せられなくても失敗にしない。</b>
    /// 乗せられるならそのほうが良いが、乗せられないからといって
    /// 向かわないのは行き過ぎ。公式の <c>MoveCloseTo</c> は
    /// 自身でも近傍を探すため、生の座標で頼んでも着くことがある。
    /// ここは「分かる範囲で良くする」だけに留める。
    /// </summary>
    private Vector3 Sanitise(Vector3 destination)
    {
        // まず真下の床。段差の上に立つ NPC はこれで乗る。
        var seed = destination;

        if (this.vnavmesh.TryPointOnFloor(destination, out var floor) &&
            floor is { } onFloor &&
            Vector3.Distance(onFloor, destination) <= 5f)
        {
            seed = onFloor;
        }

        // **到達できる点を選ぶ。**
        // NearestPoint では柵の内側や別の階層の棚を返しうる。
        // そこへ向かわせると経路が引けず、延々と引き直すことになる。
        for (var i = 0; i < SanitiseSteps.Length; i++)
        {
            var (flat, height) = SanitiseSteps[i];

            if (!this.vnavmesh.TryNearestPointReachable(seed, flat, height, out var near) ||
                near is not { } reachable)
            {
                continue;
            }

            if (i > 0)
            {
                this.anomalyLog.Info(
                    "Navigation",
                    $"目的地をメッシュへ乗せるのに範囲を {flat:F0} ヤルムまで広げました" +
                    $"（{Vector3.Distance(reachable, destination):F1} ヤルムずれます）");
            }

            return reachable;
        }

        // 乗せられなかった。生の座標のまま公式へ渡す。
        this.anomalyLog.Info(
            "Navigation",
            "目的地をナビメッシュへ乗せられませんでした。そのまま vnavmesh へ渡します");

        return seed;
    }

    /// <summary>
    /// 移動の層の詰まり手当て。
    ///
    /// <b>公式の自動再試行とは役目が違う。</b>
    /// vnavmesh 側の RetryOnStuck は「経路から外れたら引き直す」もので、
    /// 段差や柵に正面から押し付けられている状態は抜けられない。
    /// 跳ねれば外れることが多いので、まずそれを試す。
    ///
    /// ICE も詰まったときは「跳ぶ」と「経路を止めて引き直させる」の
    /// 2 つだけで抜けている（逆コンパイルして確認。CheckIfIsStuck）。
    /// </summary>
    private void TickStallEscape(Vector3 position, DateTime now, bool running, bool pathfinding, float range)
    {
        // 跳んだあと。着地して落ち着いてから引き直す。
        if (this.escapeJumpedUtc != default)
        {
            if (Svc.Condition[ConditionFlag.Jumping] ||
                Svc.Condition[ConditionFlag.Jumping61] ||
                now - this.escapeJumpedUtc < JumpSettleDelay)
            {
                return;
            }

            this.escapeJumpedUtc = default;

            // **ここでは先に止める。**
            //
            // 目的地を変える引き直し（Reissue）では止めてはいけないが、
            // 詰まりの手当ては逆。いま積まれている経路は、まさに
            // 引っかかっている壁へ向かっている。残したまま引き直すと、
            // 新しい経路が出来るまでの間、同じ壁を押し続ける。
            this.vnavmesh.TryStop();
            this.ReissueAfterEscape(range, "跳ねて外したので");
            this.ResetProgress(position, now);
            return;
        }

        // 経路を探している間は進まないのが当たり前。詰まりと数えない。
        if (pathfinding || !running)
        {
            this.ResetProgress(position, now);
            return;
        }

        // 自分の都合で止まっている間も数えない。
        if (IsOccupied())
        {
            this.ResetProgress(position, now);
            return;
        }

        if (Vector3.DistanceSquared(position, this.stallPosition) > StallProgressMeters * StallProgressMeters)
        {
            this.stallPosition = position;
            this.stallSinceUtc = now;
            return;
        }

        if (now - this.stallSinceUtc <= StallWindow)
        {
            return;
        }

        // 進めていない。手当てする。
        this.stallSinceUtc = now;

        // 跳ねて外す。乗っているとき・飛んでいるときは跳ばない
        // （乗車中は降車が弾かれ、空中では降下が終わってしまう）。
        if (this.escapes < MaxEscapes && MountService.TryJumpOnGround())
        {
            this.escapes++;
            this.escapeJumpedUtc = now;

            this.anomalyLog.Info(
                "Navigation",
                $"{StallWindow.TotalSeconds:F1} 秒で {StallProgressMeters:F0} ヤルムも進めないため跳ねます" +
                $"（{this.escapes}/{MaxEscapes} 回目）");

            return;
        }

        // 跳べない、あるいは跳んでも駄目だった。引き直すだけにする。
        // ここで抜けられなければ、作業の層が Stuck を返して
        // 呼び出し側の別の手（迂回・力づくの脱出）へ渡る。
        this.vnavmesh.TryStop();
        this.ReissueAfterEscape(range, "進めないので");
    }

    /// <summary>
    /// 詰まりの手当てとして、同じ目的地へ引き直す。
    ///
    /// 目的地は <see cref="issuedDestination"/> を使う。呼び出し側が渡す
    /// 生の座標ではなく、メッシュへ乗せ直した点のほうが経路が引ける。
    /// </summary>
    private void ReissueAfterEscape(float range, string why)
    {
        if (DateTime.UtcNow - this.lastIssueUtc < ReissueInterval)
        {
            return;
        }

        if (this.vnavmesh.TryMoveCloseTo(this.issuedDestination, this.issuedWithFly, range, out var accepted) &&
            accepted)
        {
            this.lastIssueUtc = DateTime.UtcNow;
            this.anomalyLog.Info("Navigation", $"{why}経路を引き直しました");
            return;
        }

        // 断られた。次のフレームでまた試す。経路は止めてあるので
        // その間は止まったままになるが、作業の層が有限時間で拾う。
        this.anomalyLog.Info("Navigation", $"{why}経路を引き直そうとしましたが、まだ受け取れません");
    }

    /// <summary>進んでいないことの数え直し。両方の層をまとめて揃える。</summary>
    private void ResetProgress(Vector3 position, DateTime now)
    {
        this.stallPosition = position;
        this.stallSinceUtc = now;
        this.lastPosition = position;
        this.lastMovementUtc = now;
    }

    /// <summary>
    /// こちらが動かしている間、BMR の AI には手を引いてもらう。
    ///
    /// <b>強制停止ではない。</b>BMR の移動だけを止める協調的な依頼で、
    /// 戦闘はそのまま続く。止めなければ、同じキャラクターを
    /// 2 つが別の向きへ動かそうとして、前にも後ろにも進まない
    /// （とくに騎乗中は飛び上がれなくなる）。
    /// </summary>
    private void TakeOverExternalMovement()
    {
        if (this.pausedExternalMovement || !this.bossMod.IsLoaded)
        {
            return;
        }

        // 先に立てる。IPC が片方だけ通ったときに、戻す側が走らなくなるのを防ぐ。
        this.pausedExternalMovement = true;

        // ① AI の移動。
        //
        // こちらは AI を使わない方針（FateRunner が /bmrai off を送る）なので
        // 普段は何も起きない。ただし利用者が自分で入れていることがあり、
        // そのときは AI が同じキャラクターを別の向きへ動かそうとする。
        this.bossMod.TryPauseMovement(true);

        // ② プリセットの移動（NormalMovement）。
        //
        // AI を使わなくても、有効なプリセットがこれを持っていれば動く。
        // 止められるのはプリセットの名前を知っている側だけ。
        this.ExternalMovementGate?.Invoke(true);
    }

    /// <summary>
    /// もう動かせない。移動権を返してから結果を返す。
    ///
    /// <b>行き止まりのときだけ通す。</b>
    /// 到着（Arrived / ShortOfTarget）では返さない。呼び出し側が
    /// 動かすのをやめたとは限らず、返して奪い直すと相手の移動が途切れる。
    /// </summary>
    private MoveStatus Finish(MoveStatus status)
    {
        this.ReleaseExternalMovement();
        return status;
    }

    /// <summary>
    /// 自分の都合で止まっている状態か。
    ///
    /// 詠唱・エリア移動・ムービーの間は進まないのが当たり前で、
    /// これを詰まりと数えると、そのたびに無駄に跳ねて引き直すことになる。
    /// </summary>
    private static bool IsOccupied()
    {
        var c = Svc.Condition;

        return c[ConditionFlag.Casting] ||
               c[ConditionFlag.BetweenAreas] ||
               c[ConditionFlag.BetweenAreas51] ||
               c[ConditionFlag.WatchingCutscene] ||
               c[ConditionFlag.WatchingCutscene78] ||
               c[ConditionFlag.OccupiedInCutSceneEvent] ||
               c[ConditionFlag.Mounting] ||
               c[ConditionFlag.Mounting71] ||
               c[ConditionFlag.Jumping] ||
               c[ConditionFlag.Jumping61] ||
               c[ConditionFlag.Unconscious];
    }
}
