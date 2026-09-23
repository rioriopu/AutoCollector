using System;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;

namespace AutoCollector.Earning.Combat;

/// <summary>
/// 戦闘で稼ぐ（AutoDuty の周回に相乗りする）。
///
/// **AutoDuty を直接触るのはここだけにしていく。**
/// いまは稼働判定と緊急停止だけをここへ寄せてある。
/// 中断と復帰は <c>ExchangeExecutor</c> に残っており、段 4 で移す。
/// </summary>
public sealed class CombatEarner(AutoDutyIpc autoDuty, AnomalyLog anomalyLog) : IEarner
{
    /// <summary>
    /// AutoDuty が止まってからも「動作中の扱い」を続ける時間。
    ///
    /// 交換は AutoDuty が全周回を終えて停止したあとに行う。
    /// 停止した瞬間に動作中でなくなると、その交換をここで弾いてしまい、
    /// AutoDuty も止まったままになる。周回の切れ目を跨げる長さにしてある。
    /// </summary>
    private static readonly TimeSpan RunningGrace = TimeSpan.FromSeconds(120);

    private readonly AutoDutyIpc autoDuty = autoDuty;
    private readonly AnomalyLog anomalyLog = anomalyLog;

    private DateTime lastRunningUtc = DateTime.MinValue;

    public string Id => "Combat";

    public string DisplayName => "AutoDuty";

    public bool IsAvailable => this.autoDuty.IsLoaded;

    /// <summary>
    /// いま周回しているか。
    ///
    /// **判定式は <c>ExternalAutomationGate</c> から 1 文字も変えずに移した。**
    /// 状態を取得できない場合は「動いていない」として扱う。
    /// 判断がつかないまま自動交換を始める方が危ない。
    ///
    /// 直前まで動いていた場合は猶予のあいだ動作中として扱う。
    /// 理由は <see cref="RunningGrace"/> に書いてある。
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (this.autoDuty.IsLoaded && this.autoDuty.TryIsStopped(out var stopped) && !stopped)
            {
                this.lastRunningUtc = DateTime.UtcNow;
                return true;
            }

            return DateTime.UtcNow - this.lastRunningUtc < RunningGrace;
        }
    }

    /// <summary>
    /// 猶予で動作中と見なしているだけか。画面の表記を分けるために使う。
    /// </summary>
    public bool IsRunningOnGrace
        => !(this.autoDuty.IsLoaded && this.autoDuty.TryIsStopped(out var stopped) && !stopped)
           && DateTime.UtcNow - this.lastRunningUtc < RunningGrace;

    /// <summary>
    /// 稼働中の表記。文言は <c>ExternalAutomationGate</c> のものをそのまま使う。
    /// </summary>
    public string DescribeRunning()
        => this.IsRunningOnGrace ? "AutoDuty（直前まで動作）" : "AutoDuty";

    /// <summary>
    /// 戦闘は周回そのものが通貨を生む。導入されていれば自力で増やせる。
    /// </summary>
    public bool SuppliesCurrency => this.autoDuty.IsLoaded;

    public void MarkInterrupting()
    {
        // 段 4 で、止める前の状態（周回していたエリア・ループ設定）をここで控える。
    }

    public bool IsAtSafeBreak(out string reason)
    {
        reason = string.Empty;
        return true;
    }

    public EarnerStepResult TickSuspend()
        => throw new NotSupportedException(
            "中断は ExchangeExecutor.TickStopAutoDuty に残っている。段 4 でここへ移す");

    public EarnerStepResult TickResume()
        => throw new NotSupportedException(
            "復帰は ExchangeExecutor.TickResumeAutoDuty に残っている。段 4 でここへ移す");

    /// <summary>
    /// 走っている周回を止める。
    ///
    /// 維持を止めるだけでは足りない。AutoDuty には渡した周回数ぶんを
    /// 自走する力があるため、止めたつもりで回り続ける。
    ///
    /// **利用者が明示的に止めたときだけ通す。**協調的な抑制で済む相手ではない。
    /// </summary>
    public void StopForEmergency()
    {
        if (!this.autoDuty.IsLoaded || !this.autoDuty.IsRunningFailClosed())
        {
            return;
        }

        this.autoDuty.TryStop();
        this.anomalyLog.Info("AutoDuty", "走っていた周回も止めました");
    }

    /// <summary>
    /// 緊急停止で止めたぶんを戻す。
    ///
    /// AutoDuty は「止めたら止めたまま」にする。
    /// 利用者が明示的に止めたものを、こちらの都合で動かし直さない。
    /// 周回の維持（<c>AutoDutyKeeper</c>）の抑制解除は呼ぶ側が行う。
    /// </summary>
    public void ResumeAfterStop()
    {
    }
}
