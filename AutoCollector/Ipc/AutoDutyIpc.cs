using AutoCollector.Diagnostics;

namespace AutoCollector.Ipc;

/// <summary>
/// AutoDuty との連携。
///
/// 公開されている IPC は状態の取得と Start / Stop / Run だけで、
/// 「いま Duty の途中か」を直接返すものは無い。
/// そのため Duty 中かどうかは ConditionFlag と組み合わせて判断する。
/// </summary>
public sealed class AutoDutyIpc(AnomalyLog anomalyLog) : IpcGateBase("AutoDuty", anomalyLog)
{
    /// <summary>完全に停止しているか。取得できない場合は false（停止と断定しない）。</summary>
    public bool TryIsStopped(out bool stopped)
        => this.TryInvoke("IsStopped", () => this.Func<bool>("AutoDuty.IsStopped").InvokeFunc(), out stopped);

    public bool TryIsNavigating(out bool navigating)
        => this.TryInvoke("IsNavigating", () => this.Func<bool>("AutoDuty.IsNavigating").InvokeFunc(), out navigating);

    public bool TryIsLooping(out bool looping)
        => this.TryInvoke("IsLooping", () => this.Func<bool>("AutoDuty.IsLooping").InvokeFunc(), out looping);

    public bool TryContentHasPath(uint territoryType, out bool hasPath)
        => this.TryInvoke("ContentHasPath", () => this.Func<uint, bool>("AutoDuty.ContentHasPath").InvokeFunc(territoryType), out hasPath);

    /// <summary>
    /// 動作中か。取得できない場合は「動作中」とみなす。
    /// 停止していないものを停止済みと誤認して割り込む方が危険なため。
    /// </summary>
    public bool IsRunningFailClosed()
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        if (!this.TryIsStopped(out var stopped))
        {
            this.AnomalyLog.Warn("Ipc", "[AutoDuty] 状態を取得できないため、動作中とみなします");
            return true;
        }

        return !stopped;
    }

    /// <summary>
    /// 停止する。
    ///
    /// AutoDuty 側では Stage の設定が同期的に全体停止処理を走らせるため、
    /// 必ず Framework スレッドから呼ぶこと。
    /// </summary>
    public bool TryStop()
        => this.TryAction("Stop", () => this.Func<object>("AutoDuty.Stop").InvokeAction());

    /// <summary>
    /// 周回を再開する。
    ///
    /// loops には必ず 0 を渡す。0 以外を渡すと AutoDuty 側の設定 LoopTimes が恒久的に書き換わり、
    /// ユーザーの設定を壊す。
    /// </summary>
    public bool TryRun(uint territoryType)
        => this.TryAction("Run", () => this.Func<uint, int, bool, object>("AutoDuty.Run").InvokeAction(territoryType, 0, false));
}
