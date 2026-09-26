using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AutoCollector.Diagnostics;

namespace AutoCollector.Ipc;

/// <summary>
/// vnavmesh との連携。
///
/// IPC 名は vnavmesh/IPCProvider.cs で "vnavmesh." + name として登録されているものだけを使う。
/// バージョンを返す IPC は存在しないため、可用性は登録の有無で判断する。
/// </summary>
public sealed class VnavmeshIpc(AnomalyLog anomalyLog) : IpcGateBase("vnavmesh", anomalyLog)
{
    /// <summary>ナビメッシュが利用可能か。エリア読み込み直後は false の期間がある。</summary>
    public bool TryIsReady(out bool ready)
        => this.TryInvoke("Nav.IsReady", () => this.Func<bool>("vnavmesh.Nav.IsReady").InvokeFunc(), out ready);

    /// <summary>経路探索が進行中か。</summary>
    public bool TryNavPathfindInProgress(out bool inProgress)
        => this.TryInvoke("Nav.PathfindInProgress", () => this.Func<bool>("vnavmesh.Nav.PathfindInProgress").InvokeFunc(), out inProgress);

    /// <summary>SimpleMove 側の経路探索が進行中か。</summary>
    public bool TrySimpleMovePathfindInProgress(out bool inProgress)
        => this.TryInvoke("SimpleMove.PathfindInProgress", () => this.Func<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc(), out inProgress);

    /// <summary>移動中か。実体は「経路点が残っているか」。</summary>
    public bool TryPathIsRunning(out bool running)
        => this.TryInvoke("Path.IsRunning", () => this.Func<bool>("vnavmesh.Path.IsRunning").InvokeFunc(), out running);

    /// <summary>
    /// 目的地の近くまで移動する。
    ///
    /// 戻り値の true は「経路探索を積んだ」という意味でしかない。移動の成否ではない。
    /// false は「別の経路探索が進行中」を意味する。
    /// 経路探索に失敗した場合は例外が握り潰されて経路が空のままになるため、
    /// 「移動失敗」と「移動完了」は IPC の状態だけでは区別できない。
    /// 呼び出し側で距離とタイムアウトを必ず確認すること。
    /// </summary>
    public bool TryMoveCloseTo(Vector3 destination, bool fly, float range, out bool accepted)
        => this.TryInvoke(
            "SimpleMove.PathfindAndMoveCloseTo",
            () => this.Func<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo").InvokeFunc(destination, fly, range),
            out accepted);

    /// <summary>
    /// 指定座標に最も近いナビメッシュ上の点を返す。
    ///
    /// PointOnFloor は「真下の床」を探すため、建物の中にいる NPC に対して
    /// 別の階層や屋外の地面を拾うことがある。
    /// こちらはメッシュ上の最近傍を返すので、屋内でもその場の床に乗る。
    /// </summary>
    public bool TryNearestPoint(Vector3 position, float halfExtentXZ, float halfExtentY, out Vector3? nearest)
        => this.TryInvoke(
            "Query.Mesh.NearestPoint",
            () => this.Func<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint").InvokeFunc(position, halfExtentXZ, halfExtentY),
            out nearest);

    /// <summary>
    /// 指定座標に最も近い、<b>歩いて辿り着ける</b>ナビメッシュ上の点を返す。
    ///
    /// <see cref="TryNearestPoint"/> は allowUnreachable=true 相当で、
    /// メッシュに載ってさえいれば湖の向こうの小島のような
    /// 「そこまで行けない点」も返す（NavmeshQuery.cs の
    /// FindNearestPointOnMesh は既定が allowUnreachable=true）。
    /// 降りる場所を決めるときは、辿り着けることまで確かめたいのでこちらを使う。
    /// </summary>
    public bool TryNearestPointReachable(Vector3 position, float halfExtentXZ, float halfExtentY, out Vector3? nearest)
        => this.TryInvoke(
            "Query.Mesh.NearestPointReachable",
            () => this.Func<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable").InvokeFunc(position, halfExtentXZ, halfExtentY),
            out nearest);

    /// <summary>
    /// 指定座標の真下にある床の座標を返す。
    ///
    /// 配置ファイル由来の NPC 座標はナビメッシュ上に乗っていないことがあり、
    /// そのまま目的地にすると経路探索が失敗しやすい。多層構造の都市で特に問題になる。
    /// </summary>
    public bool TryPointOnFloor(Vector3 position, out Vector3? onFloor)
        => this.TryInvoke(
            "Query.Mesh.PointOnFloor",
            () => this.Func<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor").InvokeFunc(position, false, 5f),
            out onFloor);

    /// <summary>
    /// いま組まれている経路点の数。0 なら経路を持っていない。
    ///
    /// 「まだ動いているつもりなのに進まない」を見分けるのに使う。
    /// </summary>
    public bool TryNumWaypoints(out int count)
        => this.TryInvoke("Path.NumWaypoints", () => this.Func<int>("vnavmesh.Path.NumWaypoints").InvokeFunc(), out count);

    /// <summary>
    /// 指定の球を避けて経路を引き、その経路点を返す。
    ///
    /// 障害物に張り付いて進めなくなったとき、そこを避けて引き直すのに使う。
    /// 飛行時は voxel の空間（PathfindVolume）で探すため、
    /// 地形の内側を通る経路にはならない。
    /// </summary>
    /// <remarks>
    /// <b>戻り値は Task。</b>vnavmesh 側は QueryPathBasic をそのまま返しており
    /// （IPCProvider.cs:23）、その型は <c>Task&lt;List&lt;Vector3&gt;&gt;</c>。
    /// List として受け取ろうとすると、Task を JSON 化しようとして
    /// 「Self referencing loop detected」で失敗する（2026-09-25 実測）。
    /// </remarks>
    public bool TryPathfindAvoid(
        Vector3 from,
        Vector3 to,
        bool fly,
        Vector3 avoidCenter,
        float avoidRadius,
        out Task<List<Vector3>>? waypoints)
        => this.TryInvoke(
            "Nav.PathfindAvoid",
            () => this.Func<Vector3, Vector3, bool, Vector3, float, Task<List<Vector3>>>("vnavmesh.Nav.PathfindAvoid")
                      .InvokeFunc(from, to, fly, avoidCenter, avoidRadius),
            out waypoints);

    /// <summary>
    /// 経路だけを求める。<b>移動は始まらない。</b>
    ///
    /// <c>SimpleMove.PathfindAndMoveCloseTo</c> との違いはここが肝心。
    /// あちらは vnavmesh 内部の <c>AsyncMoveRequest</c> に積むので、
    /// 探索が終わった瞬間に <b>vnavmesh 自身が勝手に移動を始める</b>
    /// （AsyncMoveRequest.cs:44-60 の Update が IsCompleted を見て
    /// _follow.Move を呼ぶ）。こちらは求めるだけなので、
    /// 中身を検査してから <see cref="TryMoveAlong"/> で渡せる。
    ///
    /// 飛行進入では「引けた経路が本当に斜めに降りているか」を
    /// 見てから走らせたいので、この形でなければならない。
    /// </summary>
    /// <remarks>
    /// <b>戻り値は Task。</b>List として受け取ると、Task を JSON 化しようとして
    /// 「Self referencing loop detected」で失敗する（PathfindAvoid で実測）。
    /// </remarks>
    public bool TryPathfind(Vector3 from, Vector3 to, bool fly, out Task<List<Vector3>>? waypoints)
        => this.TryInvoke(
            "Nav.Pathfind",
            () => this.Func<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind")
                      .InvokeFunc(from, to, fly),
            out waypoints);

    /// <summary>
    /// 経路だけを求める。あとから取り消せる。
    ///
    /// <b>段階を切り替えるときは、前の探索を取り消す。</b>
    /// 取り消さないと、遠い目的地の探索が十数秒後に終わり、
    /// そのときにはもう別の段階へ移っているのに結果が返ってくる。
    /// 世代番号で捨てることもできるが、探索自体は走り続けて重い。
    /// </summary>
    public bool TryPathfindCancelable(
        Vector3 from,
        Vector3 to,
        bool fly,
        CancellationToken cancel,
        out Task<List<Vector3>>? waypoints)
        => this.TryInvoke(
            "Nav.PathfindCancelable",
            () => this.Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable")
                      .InvokeFunc(from, to, fly, cancel),
            out waypoints);

    /// <summary>
    /// 組み上げた経路点をそのまま辿らせる。
    ///
    /// <b>ここに渡した経路は、そのとおりに辿る。</b>
    /// 障害物を避ける計算はしない。だから渡す前に検査する。
    ///
    /// fly は vnavmesh 側で反転されて <c>IgnoreDeltaY</c> になる
    /// （IPCProvider.cs:41）。<c>fly=true</c> で Y 差を見るようになり、
    /// 「次の点が自分より高い」ときにジャンプで離陸する。
    /// <c>fly=false</c> では Y を 0 にして測るため、高さの差が消える。
    /// </summary>
    public bool TryMoveAlong(List<Vector3> waypoints, bool fly)
        => this.TryAction(
            "Path.MoveTo",
            () => this.Func<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo").InvokeAction(waypoints, fly));

    /// <summary>いま辿っている経路点の一覧。残りの形を見るのに使う。</summary>
    public bool TryListWaypoints(out List<Vector3>? waypoints)
        => this.TryInvoke(
            "Path.ListWaypoints",
            () => this.Func<List<Vector3>>("vnavmesh.Path.ListWaypoints").InvokeFunc(),
            out waypoints);

    /// <summary>
    /// 指定の座標がメッシュに乗っているか。
    ///
    /// 着地点の候補を検査するのに使う。allowUnreachable=false にすると
    /// 「乗っているが辿り着けない」点を弾ける。
    /// </summary>
    public bool TryIsPointOnMesh(Vector3 position, float halfExtentY, bool allowUnreachable, out bool onMesh)
        => this.TryInvoke(
            "Query.Mesh.IsPointOnMesh",
            () => this.Func<Vector3, float, bool, bool>("vnavmesh.Query.Mesh.IsPointOnMesh")
                      .InvokeFunc(position, halfExtentY, allowUnreachable),
            out onMesh);

    /// <summary>
    /// 真下の床を、探索の幅を指定して求める。
    ///
    /// <see cref="TryPointOnFloor"/> は幅 5m の決め打ちで聞く。
    /// 着地点を探すときは広く見たいことがあるので、こちらを使う。
    /// </summary>
    public bool TryPointOnFloorWide(Vector3 position, bool allowUnlandable, float halfExtentXZ, out Vector3? onFloor)
        => this.TryInvoke(
            "Query.Mesh.PointOnFloor",
            () => this.Func<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor")
                      .InvokeFunc(position, allowUnlandable, halfExtentXZ),
            out onFloor);

    /// <summary>
    /// ナビメッシュを読み込み直す（キャッシュから）。
    ///
    /// 経路探索も全部取り消される（Nav.PathfindCancelAll と同じ実体）。
    /// 詰まって抜け出せないときの最後の手段に使う。
    /// </summary>
    public bool TryReloadNavmesh(out bool accepted)
        => this.TryInvoke("Nav.Reload", () => this.Func<bool>("vnavmesh.Nav.Reload").InvokeFunc(), out accepted);

    /// <summary>
    /// ナビメッシュを作り直す（キャッシュを使わない）。
    ///
    /// <b>重い。</b>マップによっては数十秒かかる。
    /// キャッシュが壊れている疑いがあるときだけ使う。
    /// </summary>
    public bool TryRebuildNavmesh(out bool accepted)
        => this.TryInvoke("Nav.Rebuild", () => this.Func<bool>("vnavmesh.Nav.Rebuild").InvokeFunc(), out accepted);

    /// <summary>
    /// 経路からどれだけ外れてよいかを決める。
    ///
    /// vnavmesh の既定は 0.25。大きいほど経路を端折って進むため、
    /// 狭い通路では壁に寄って引っかかる。小さくすると経路をなぞる。
    /// </summary>
    public bool TrySetPathTolerance(float meters)
        => this.TryAction("Path.SetTolerance", () => this.Func<float, object>("vnavmesh.Path.SetTolerance").InvokeAction(meters));

    /// <summary>いまの経路追従の許容値。</summary>
    public bool TryGetPathTolerance(out float meters)
        => this.TryInvoke("Path.GetTolerance", () => this.Func<float>("vnavmesh.Path.GetTolerance").InvokeFunc(), out meters);

    /// <summary>移動を停止する。</summary>
    public bool TryStop()
        => this.TryAction("Path.Stop", () => this.Func<object>("vnavmesh.Path.Stop").InvokeAction());
}
