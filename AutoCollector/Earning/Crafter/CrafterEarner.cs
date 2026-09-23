using System;
using AutoCollector.Ipc;

namespace AutoCollector.Earning.Crafter;

/// <summary>
/// クラフターで稼ぐ（収集品を作って納品し、スクリップを得る）。
///
/// **Artisan を直接触るのはここだけにしていく。**
/// いまは稼働判定と緊急停止だけをここへ寄せてある。
/// 稼ぎの手順（製作・納品・取り出し）は <c>GoalRunner</c> に残っており、段 6 で移す。
/// </summary>
public sealed class CrafterEarner(ArtisanIpc artisan) : IEarner
{
    private readonly ArtisanIpc artisan = artisan;

    public string Id => "Crafter";

    public string DisplayName => "Artisan";

    public bool IsAvailable => this.artisan.IsLoaded;

    /// <summary>
    /// いま製作しているか。
    ///
    /// **判定式は <c>ExternalAutomationGate</c> から 1 文字も変えずに移した。**
    /// 取得できない場合は false（＝動いていない）。
    /// </summary>
    public bool IsRunning => this.artisan.IsRunning();

    public string DescribeRunning() => "Artisan";

    /// <summary>
    /// 製作で稼ぐかどうかはプリセットごとの設定で決まる。
    ///
    /// **ここでは判断できない。**
    /// 段 5 で、プリセットの設定を見て答えられるようにする。
    /// それまでは共通側（<c>GoalRunner</c> / UI）が <c>preset.CraftToEarn</c> を直接見ている。
    /// </summary>
    public bool SuppliesCurrency => false;

    public void MarkInterrupting()
    {
    }

    public bool IsAtSafeBreak(out string reason)
    {
        // 製作の途中で止めると、進行中の 1 個を落とす。
        if (this.artisan.IsLoaded && this.artisan.IsBusyFailClosed())
        {
            reason = "製作中です";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public EarnerStepResult TickSuspend()
        => throw new NotSupportedException(
            "中断は ExchangeExecutor に残っている。段 4 でここへ移す");

    public EarnerStepResult TickResume()
        => throw new NotSupportedException(
            "復帰は ExchangeExecutor に残っている。段 4 でここへ移す");

    /// <summary>
    /// 停止を要求する。**自分が立てた旗だけを、あとで解除する。**
    /// </summary>
    public void StopForEmergency() => this.artisan.Stop();

    /// <summary>
    /// 停止要求を解除する。
    ///
    /// <c>ArtisanIpc.Release</c> が「自分が止めた場合だけ」を見ている。
    /// 利用者が自分で止めたものは動かさない。
    /// </summary>
    public void ResumeAfterStop() => this.artisan.Release();
}
