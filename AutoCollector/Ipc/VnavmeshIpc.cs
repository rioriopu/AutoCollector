using System.Numerics;
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

    /// <summary>移動を停止する。</summary>
    public bool TryStop()
        => this.TryAction("Path.Stop", () => this.Func<object>("vnavmesh.Path.Stop").InvokeAction());
}
