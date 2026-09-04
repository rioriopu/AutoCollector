using AutoCollector.Diagnostics;

namespace AutoCollector.Ipc;

/// <summary>
/// Lifestream との連携。テレポートに使う。
///
/// 注意すべき性質:
/// IPC 経由の Teleport は Lifestream の内部で待機なしの経路を通るため、
/// TaskManager に何も積まれない。したがって呼び出したあと IsBusy() は true にならない。
/// 詠唱の完了もエリア遷移も、こちらで待つ必要がある。
/// </summary>
public sealed class LifestreamIpc(AnomalyLog anomalyLog) : IpcGateBase("Lifestream", anomalyLog)
{
    /// <summary>
    /// 指定のエーテライトへテレポートする。
    ///
    /// aetheryteId と subIndex は Svc.AetheryteList のエントリから取る。
    /// false が返るのは、未アクセスか、テレポートできない状態（操作不能・戦闘中など）のとき。
    /// </summary>
    public bool TryTeleport(uint aetheryteId, byte subIndex, out bool accepted)
        => this.TryInvoke(
            "Teleport",
            () => this.Func<uint, byte, bool>("Lifestream.Teleport").InvokeFunc(aetheryteId, subIndex),
            out accepted);

    /// <summary>
    /// エーテライト網を使って都市内を短縮移動する。
    ///
    /// 引数は Aetheryte シートの行 ID。
    /// エーテライトまたは転送先の範囲内にいないと失敗する。
    /// こちらは Teleport と違い Lifestream 内部のタスクに積まれるため、IsBusy() が true になる。
    /// </summary>
    public bool TryAethernetTeleportById(uint aetheryteRowId, out bool accepted)
        => this.TryInvoke(
            "AethernetTeleportById",
            () => this.Func<uint, bool>("Lifestream.AethernetTeleportById").InvokeFunc(aetheryteRowId),
            out accepted);

    /// <summary>Lifestream が何か処理中か。移動系の競合を避けるために見る。</summary>
    public bool TryIsBusy(out bool busy)
        => this.TryInvoke("IsBusy", () => this.Func<bool>("Lifestream.IsBusy").InvokeFunc(), out busy);

    /// <summary>Lifestream の処理を中断する。</summary>
    public bool TryAbort()
        => this.TryAction("Abort", () => this.Func<object>("Lifestream.Abort").InvokeAction());
}
