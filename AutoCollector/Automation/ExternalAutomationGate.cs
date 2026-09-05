using System;
using System.Collections.Generic;
using AutoCollector.Ipc;

namespace AutoCollector.Automation;

/// <summary>
/// 外部の自動化プラグインが動いているかを判定する。
///
/// 自動交換はそれらの周回に相乗りする形で動く前提にしている。
/// プリセットを有効にしただけで動き出すと、手動で遊んでいる最中に
/// 勝手にテレポートして交換を始めてしまう。
/// </summary>
public sealed class ExternalAutomationGate(AutoDutyIpc autoDuty, ArtisanIpc artisan)
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
    private readonly ArtisanIpc artisan = artisan;

    private DateTime lastAutoDutyRunningUtc = DateTime.MinValue;

    /// <summary>いま動いている自動化プラグインの名前。1 つも無ければ空。</summary>
    public IReadOnlyList<string> GetRunning()
    {
        var running = new List<string>();

        // 状態を取得できない場合は「動いていない」として扱う。
        // 判断がつかないまま自動交換を始める方が危ない。
        if (this.autoDuty.IsLoaded && this.autoDuty.TryIsStopped(out var stopped) && !stopped)
        {
            this.lastAutoDutyRunningUtc = DateTime.UtcNow;
            running.Add("AutoDuty");
        }
        else if (DateTime.UtcNow - this.lastAutoDutyRunningUtc < RunningGrace)
        {
            running.Add("AutoDuty（直前まで動作）");
        }

        if (this.artisan.IsRunning())
        {
            running.Add("Artisan");
        }

        return running;
    }

    /// <summary>1 つでも動いていれば true。動いているものの名前を detail に返す。</summary>
    public bool IsAnyRunning(out string detail)
    {
        var running = this.GetRunning();
        detail = running.Count == 0 ? string.Empty : string.Join(" / ", running);
        return running.Count > 0;
    }
}
