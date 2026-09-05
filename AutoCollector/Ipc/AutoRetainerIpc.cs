using AutoCollector.Diagnostics;

namespace AutoCollector.Ipc;

/// <summary>
/// AutoRetainer との協調。
///
/// 本プラグインは AutoRetainer に何かを依頼しない。軍票交換も自前で行う。
/// ここで行うのは競合の回避だけで、そのために抑制フラグを立てる。
///
/// SetSuppressed(true) は AutoRetainer の「新規開始」だけを止める。
/// 実行中のリテイナー処理は中断されない。
/// 具体的には MultiMode / スケジューラ / MiniTA / リテイナーセンス が止まる。
/// </summary>
public sealed class AutoRetainerIpc(AnomalyLog anomalyLog) : IpcGateBase("AutoRetainer", anomalyLog)
{
    /// <summary>自分が抑制を立てたかどうか。他人が立てた抑制を勝手に解除しないために持つ。</summary>
    public bool SuppressedByUs { get; private set; }

    /// <summary>
    /// AutoRetainer が処理中か。取得できない場合は「処理中」とみなす。
    ///
    /// 例外を握り潰して false を返すと「AutoRetainer は暇」と誤判定して割り込むことになる。
    /// そのため取得失敗は必ず安全側に倒す。
    /// 未導入の場合だけは false（処理中ではない）とする。
    /// </summary>
    public bool IsBusyFailClosed()
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        if (!this.TryInvoke("PluginState.IsBusy", () => this.Func<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc(), out bool busy))
        {
            this.AnomalyLog.Warn("Ipc", "[AutoRetainer] 状態を取得できないため、処理中とみなして待機します");
            return true;
        }

        return busy;
    }

    /// <summary>マルチモードが有効か。すぐに処理が始まりうるかの判断材料。</summary>
    public bool TryGetMultiModeStatus(out bool enabled)
        => this.TryInvoke("PluginState.GetMultiModeStatus", () => this.Func<bool>("AutoRetainer.PluginState.GetMultiModeStatus").InvokeFunc(), out enabled);

    /// <summary>
    /// もっとも早く完了するベンチャーまでの残り秒数。
    /// 取得できない場合は false。負の値は「すでに完了している」を意味しうる。
    /// </summary>
    public bool TryGetClosestVentureSeconds(ulong contentId, out long seconds)
    {
        seconds = 0;
        if (!this.TryInvoke(
                "PluginState.GetClosestRetainerVentureSecondsRemaining",
                () => this.Func<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining").InvokeFunc(contentId),
                out long? value))
        {
            return false;
        }

        if (value is null)
        {
            return false;
        }

        seconds = value.Value;
        return true;
    }

    public bool TryGetSuppressed(out bool suppressed)
        => this.TryInvoke("GetSuppressed", () => this.Func<bool>("AutoRetainer.GetSuppressed").InvokeFunc(), out suppressed);

    /// <summary>抑制を立てる。成功したら自分が立てたことを記録する。</summary>
    public bool Suppress()
    {
        if (!this.IsLoaded)
        {
            return true;
        }

        if (!this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true)))
        {
            return false;
        }

        this.SuppressedByUs = true;
        return true;
    }

    /// <summary>
    /// 抑制を解除する。自分が立てた場合だけ解除する。
    /// 他プラグインやユーザーが立てた抑制を勝手に外さない。
    /// </summary>
    public void Release()
    {
        if (!this.IsLoaded || !this.SuppressedByUs)
        {
            return;
        }

        if (this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false)))
        {
            this.SuppressedByUs = false;
            this.AnomalyLog.Info("Ipc", "[AutoRetainer] 抑制を解除しました");
        }
    }
}
