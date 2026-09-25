using System.Collections.Generic;
using System.Numerics;
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

    /// <summary>組み上げた経路点をそのまま辿らせる。</summary>
    public bool TryMoveAlong(List<Vector3> waypoints, bool fly)
        => this.TryAction(
            "Path.MoveTo",
            () => this.Func<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo").InvokeAction(waypoints, fly));

    /// <summary>移動を停止する。</summary>
    public bool TryStop()
        => this.TryAction("Path.Stop", () => this.Func<object>("vnavmesh.Path.Stop").InvokeAction());
}
