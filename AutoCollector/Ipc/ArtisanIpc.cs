using AutoCollector.Diagnostics;

namespace AutoCollector.Ipc;

/// <summary>
/// Artisan との協調。
///
/// 製作の最中は SafetyGuard が弾くので飛び出さないが、製作と製作の合間は
/// どの ConditionFlag も落ちるため、そこで交換に入ると Artisan が次の製作を
/// 始めようとして操作を取り合うことになる。そのため交換の間は止めておく。
///
/// 止めるには Artisan.SetStopRequest を使う。これは Artisan が
/// 「外部プラグインからの停止要求」として用意している経路で、
/// Artisan 自身もコンテンツ突入時に同じものを使っている。
///
/// 内部では停止時に動作中のモード（耐久モード / 製作リスト）を記録し、
/// 解除時にそのモードだけを戻す。何も動いていなければ何も起きない。
///
/// Artisan.SetListPause は使わない。実装が
///   if (IsListPaused()) CraftingListFunctions.Paused = s;
/// となっており、すでに一時停止しているときしか効かないため、
/// 外から止める用途には使えない。
/// </summary>
public sealed class ArtisanIpc(AnomalyLog anomalyLog) : IpcGateBase("Artisan", anomalyLog)
{
    /// <summary>自分が停止を要求したか。他が止めたものを勝手に再開しないために持つ。</summary>
    public bool StoppedByUs { get; private set; }

    /// <summary>耐久モード（同じ物を作り続けるモード）が動いているか。</summary>
    public bool TryGetEnduranceStatus(out bool running)
        => this.TryInvoke("GetEnduranceStatus", () => this.Func<bool>("Artisan.GetEnduranceStatus").InvokeFunc(), out running);

    /// <summary>製作リストを実行中か。</summary>
    public bool TryIsListRunning(out bool running)
        => this.TryInvoke("IsListRunning", () => this.Func<bool>("Artisan.IsListRunning").InvokeFunc(), out running);

    /// <summary>停止要求が立っているか。自分以外が立てている場合もある。</summary>
    public bool TryGetStopRequest(out bool stopped)
        => this.TryInvoke("GetStopRequest", () => this.Func<bool>("Artisan.GetStopRequest").InvokeFunc(), out stopped);

    /// <summary>
    /// 自動製作が動いているか。
    ///
    /// 取得できない場合は false を返す。
    /// これは「自動交換を始めてよいか」の判断に使うため、
    /// 分からないときは動いていないものとして扱い、交換を始めない側へ倒れる。
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

    /// <summary>
    /// 何か処理中か。取得できない場合は「処理中」とみなす。
    ///
    /// こちらは「交換に入ってよいか」の判断に使う。
    /// 暇だと誤判定して割り込む方が危険なため、安全側に倒す。
    /// </summary>
    public bool IsBusyFailClosed()
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        if (!this.TryInvoke("IsBusy", () => this.Func<bool>("Artisan.IsBusy").InvokeFunc(), out bool busy))
        {
            this.AnomalyLog.Warn("Ipc", "[Artisan] 状態を取得できないため、処理中とみなして待機します");
            return true;
        }

        return busy;
    }

    /// <summary>
    /// 停止を要求する。
    ///
    /// Artisan 側は動作中のモードを記録したうえで、耐久モードなら停止、
    /// 製作リストなら一時停止し、製作画面から抜ける。
    /// </summary>
    public bool Stop()
    {
        if (!this.IsLoaded)
        {
            return true;
        }

        if (!this.TryAction("SetStopRequest", () => this.Func<bool, object>("Artisan.SetStopRequest").InvokeAction(true)))
        {
            return false;
        }

        this.StoppedByUs = true;
        return true;
    }

    /// <summary>
    /// 停止要求を解除する。自分が立てた場合だけ解除する。
    /// 他プラグインやユーザーが止めたものを勝手に再開しない。
    /// </summary>
    public void Release()
    {
        if (!this.IsLoaded || !this.StoppedByUs)
        {
            return;
        }

        if (this.TryAction("SetStopRequest", () => this.Func<bool, object>("Artisan.SetStopRequest").InvokeAction(false)))
        {
            this.StoppedByUs = false;
            this.AnomalyLog.Info("Ipc", "[Artisan] 停止要求を解除しました");
        }
    }
}
