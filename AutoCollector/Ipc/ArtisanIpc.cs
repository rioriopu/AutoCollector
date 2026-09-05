using AutoCollector.Diagnostics;

namespace AutoCollector.Ipc;

/// <summary>
/// Artisan との連携。状態を読むだけで、こちらから操作はしない。
/// </summary>
public sealed class ArtisanIpc(AnomalyLog anomalyLog) : IpcGateBase("Artisan", anomalyLog)
{
    /// <summary>耐久モード（同じ物を作り続けるモード）が動いているか。</summary>
    public bool TryGetEnduranceStatus(out bool running)
        => this.TryInvoke("GetEnduranceStatus", () => this.Func<bool>("Artisan.GetEnduranceStatus").InvokeFunc(), out running);

    /// <summary>製作リストを実行中か。</summary>
    public bool TryIsListRunning(out bool running)
        => this.TryInvoke("IsListRunning", () => this.Func<bool>("Artisan.IsListRunning").InvokeFunc(), out running);

    /// <summary>
    /// 何らかの製作を実行中か。
    ///
    /// 取得できない場合は false を返す。
    /// ここでの false は「自動交換を始めない」側に倒れるため、これで安全側になる。
    /// </summary>
    public bool IsRunning()
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        if (this.TryGetEnduranceStatus(out var endurance) && endurance)
        {
            return true;
        }

        return this.TryIsListRunning(out var list) && list;
    }
}
