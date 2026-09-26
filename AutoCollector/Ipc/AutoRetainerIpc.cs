using AutoCollector.Diagnostics;
using ECommons.Throttlers;

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
            // **理由を添え、間引く。**
            //
            // これは毎フレーム呼ばれる。間引かずに書いていたため、
            // 取得できない状況では記録が同じ 1 行で埋まり、
            // ほかの警告が押し流されていた。
            //
            // しかも「取得できない」としか出ないので、IPC が未登録なのか、
            // 相手が例外を投げたのかが分からなかった。
            if (EzThrottler.Throttle("AutoCollector.ArBusyWarn", 10000))
            {
                var detail = string.IsNullOrEmpty(this.LastError) ? string.Empty : $"（{this.LastError}）";
                this.AnomalyLog.Warn(
                    "Ipc",
                    $"[AutoRetainer] 状態を取得できないため、処理中とみなして待機します{detail}");
            }

            return true;
        }

        return busy;
    }

    /// <summary>マルチモードが有効か。すぐに処理が始まりうるかの判断材料。</summary>
    public bool TryGetMultiModeStatus(out bool enabled)
        => this.TryInvoke("PluginState.GetMultiModeStatus", () => this.Func<bool>("AutoRetainer.PluginState.GetMultiModeStatus").InvokeFunc(), out enabled);

    /// <summary>ベンチャーを回収できるかの判定結果。</summary>
    public enum VentureState
    {
        /// <summary>まだ判断できない。時間を置いてもう一度聞く。</summary>
        Unknown,

        /// <summary>回収できるベンチャーがある。</summary>
        Collectable,

        /// <summary>回収できるベンチャーは無い。</summary>
        None,
    }

    /// <summary>
    /// このキャラクターに、いま回収できるベンチャーを持つリテイナーが居るか。
    ///
    /// <b>残り秒数を聞く方（GetClosestRetainerVentureSecondsRemaining）は使わない。</b>
    /// GbrVentureRelay で実機確認した結果、こちらを選ぶ理由が3つある。
    /// <list type="bullet">
    /// <item>戻り値が素の bool。<c>long?</c> のような Nullable を跨がないので、
    ///       値の受け渡しで失敗する余地が無い。秒数を聞く方は実機で
    ///       「取得に失敗しました」になった</item>
    /// <item>キャラクターの判定を AutoRetainer 側が行う。こちらが ContentId を
    ///       読める状態かに左右されない（エリア移動中は 0 になる）</item>
    /// <item>「回収できる」の線引きが AutoRetainer 本体と完全に同じになる
    ///       （内部は UnsyncCompensation を使った判定。既定 -5 秒）。
    ///       自分で「残り0秒」と線を引くと本体とずれる</item>
    /// </list>
    /// </summary>
    public bool TryAnyRetainersAvailable(out bool available)
        => this.TryInvoke(
            "PluginState.AreAnyRetainersAvailableForCurrentChara",
            () => this.Func<bool>("AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara").InvokeFunc(),
            out available);

    /// <summary>
    /// 回収できるベンチャーがあるか。
    ///
    /// <b>「無い」と「まだ分からない」を区別して返す。</b>
    /// 一緒にすると、読めなかった一瞬のせいで
    /// 実際は回収できるのに「ベンチャーがありません」と誤判定する
    /// （GbrVentureRelay の知見 2-3）。
    /// </summary>
    public VentureState CheckCollectableVenture()
    {
        if (!this.IsLoaded)
        {
            return VentureState.Unknown;
        }

        if (!this.TryAnyRetainersAvailable(out var available))
        {
            return VentureState.Unknown;
        }

        return available ? VentureState.Collectable : VentureState.None;
    }

    /// <summary>
    /// AutoRetainer の処理を中断させる。
    /// こちらから呼び鈴を閉じる前に呼び、AutoRetainer が動いたままにならないようにする。
    /// </summary>
    public bool TryAbort()
        => this.TryAction(
            "PluginState.AbortAllTasks",
            () => this.Func<object>("AutoRetainer.PluginState.AbortAllTasks").InvokeAction());

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
