using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Earning;
using ECommons.Configuration;

namespace AutoCollector.Automation;

/// <summary>
/// プリセットのチェックを、動作が終わったら自動で外す。
///
/// <b>なぜ要るか。</b>
/// チェックが入ったままだと、止まっているあいだも勝手に動き出す。
/// 目標つきの周回（<see cref="GoalRunner"/>）は、チェックの入ったプリセットのうち
/// 目標に届いていないものを数秒ごとに探して自分で始める。
/// 自動交換の見張り（<see cref="MonitorService"/>）も、周回が動くたびに
/// 閾値へ届いたプリセットで交換へ向かう。
/// 利用者からは「チェックが入っていて暴走する」に見えていた（2026-09-28 の報告）。
///
/// <b>動作中は外さない。</b>
/// 次のどれか 1 つでも当てはまれば動作中とみなす。
///   ・目標つきの周回・交換・取り出し・製作・納品のどれかが動いている
///   ・交換の結果がまだ確かめられていない
///   ・稼ぎ手（AutoDuty・Artisan・FATE 周回）のどれかが稼いでいる
///   ・交換のために稼ぎ手を止めていて、まだ戻し終えていない
///
/// <b>終わったと判断するのは、止まった状態が続いたとき。</b>
/// 交換から周回へ戻るときなど、動いているものが一瞬すべて止まって見える隙間がある。
/// 外部プラグインは、再開を頼んでから「動いている」と答えるまでに遅れがある。
/// その隙間で外すと周回の途中でチェックが消えるため、
/// 止まった状態が <see cref="IdleConfirm"/> 続いたことを見てから外す。
/// これは次の処理へ進むための待ち時間ではなく、「終わった」と判断するための条件。
///
/// 外すのは、その動作のあいだにチェックが入っていたプリセットだけ。
/// 止まっているあいだに入れたチェックは、次に動かすまで残す。
/// </summary>
public sealed class PresetAutoRelease(
    AnomalyLog anomalyLog,
    GoalRunner goal,
    ExchangeExecutor executor,
    RetainerRestockRunner restock,
    CraftRunner craft,
    CollectableCycleRunner cycle,
    CollectableDeliveryRunner delivery,
    EarnerRegistry earners)
{
    /// <summary>
    /// 止まった状態がこれだけ続いたら、動作が終わったとみなす。
    ///
    /// 周回の稼ぎ手が再開してから「動いている」と答えるまでの遅れ
    /// （<c>CombatEarner.RunningGrace</c> と同じ考え方）より長く取る。
    /// </summary>
    private static readonly TimeSpan IdleConfirm = TimeSpan.FromSeconds(10);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly GoalRunner goal = goal;
    private readonly ExchangeExecutor executor = executor;
    private readonly RetainerRestockRunner restock = restock;
    private readonly CraftRunner craft = craft;
    private readonly CollectableCycleRunner cycle = cycle;
    private readonly CollectableDeliveryRunner delivery = delivery;
    private readonly EarnerRegistry earners = earners;

    /// <summary>いまの動作のあいだにチェックが入っていたプリセット。</summary>
    private readonly HashSet<Guid> enabledDuringRun = [];

    /// <summary>動作を見ている最中か。</summary>
    private bool inRun;

    /// <summary>止まった状態がいつから続いているか。動いていれば MinValue。</summary>
    private DateTime idleSinceUtc = DateTime.MinValue;

    /// <summary>
    /// 読み込んだときに、すべてのチェックを外す。
    ///
    /// プラグインの更新・入れ直し・ゲームの再起動のどれでも、ここを通る。
    /// 前回のチェックを持ち越すと、読み込んだ直後に勝手に動き出す。
    /// 読み込んだ時点で動いている処理は無いので、動作中を気にする必要はない。
    /// </summary>
    public static int ReleaseAllOnLoad(AnomalyLog log)
    {
        var released = Plugin.C.Presets.Where(x => x.Enabled).ToList();

        if (released.Count == 0)
        {
            return 0;
        }

        foreach (var preset in released)
        {
            preset.Enabled = false;
        }

        EzConfig.Save();

        log.Info(
            "Preset",
            $"読み込んだので、プリセットのチェックを外しました（{released.Count} 件: " +
            $"{string.Join("、", released.Select(x => x.Name))}）");

        return released.Count;
    }

    /// <summary>毎回の処理の最後に呼ぶ。</summary>
    public void Tick()
    {
        var busy = this.IsBusy(out var why);

        if (busy)
        {
            this.idleSinceUtc = DateTime.MinValue;

            if (!this.inRun)
            {
                this.inRun = true;
                this.enabledDuringRun.Clear();
                this.anomalyLog.Info("Preset", $"動作を見張ります（{why}）。終わったらチェックを外します");
            }

            // 動作の途中で入れたチェックも、この動作のぶんとして外す。
            foreach (var preset in Plugin.C.Presets)
            {
                if (preset.Enabled)
                {
                    this.enabledDuringRun.Add(preset.Id);
                }
            }

            return;
        }

        if (!this.inRun)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (this.idleSinceUtc == DateTime.MinValue)
        {
            this.idleSinceUtc = now;
            return;
        }

        if (now - this.idleSinceUtc < IdleConfirm)
        {
            return;
        }

        this.inRun = false;
        this.idleSinceUtc = DateTime.MinValue;
        this.ReleaseAfterRun();
    }

    private void ReleaseAfterRun()
    {
        var released = Plugin.C.Presets
            .Where(x => x.Enabled && this.enabledDuringRun.Contains(x.Id))
            .ToList();

        this.enabledDuringRun.Clear();

        if (released.Count == 0)
        {
            this.anomalyLog.Info("Preset", "動作が終わりました。外すチェックはありません");
            return;
        }

        foreach (var preset in released)
        {
            preset.Enabled = false;
        }

        EzConfig.Save();

        this.anomalyLog.Info(
            "Preset",
            $"動作が終わったので、プリセットのチェックを外しました（{released.Count} 件: " +
            $"{string.Join("、", released.Select(x => x.Name))}）");
    }

    /// <summary>何か 1 つでも動いているか。動いているものの名前を why に返す。</summary>
    private bool IsBusy(out string why)
    {
        if (this.goal.IsRunning)
        {
            why = "目標つきの周回";
            return true;
        }

        if (this.executor.IsBusy)
        {
            why = "交換";
            return true;
        }

        // 結果を確かめていない交換があるあいだは、終わったと言えない。
        if (this.executor.InFlight is not null)
        {
            why = "交換の結果の確認";
            return true;
        }

        if (this.restock.IsRunning || this.craft.IsRunning || this.cycle.IsRunning || this.delivery.IsRunning)
        {
            why = "取り出し・製作・納品";
            return true;
        }

        if (this.earners.HoldsAnySuspended)
        {
            why = "交換のために止めた周回の復帰待ち";
            return true;
        }

        if (this.earners.IsAnyRunning(out var running))
        {
            why = running;
            return true;
        }

        why = string.Empty;
        return false;
    }
}
