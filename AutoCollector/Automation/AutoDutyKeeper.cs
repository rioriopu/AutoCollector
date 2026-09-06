using System;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using ECommons;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace AutoCollector.Automation;

/// <summary>
/// AutoDuty の周回を維持する。
///
/// 1 周ごとに交換する構成では AutoDuty の周回数を 1 にする。
/// 交換が起きた場合は交換後の再開処理が AutoDuty を動かし直すが、
/// 閾値に達していない周回では誰も再開させないため、1 周で止まってしまう。
///
/// そこで、AutoDuty が周回を終えて停止したら、ここで再開させる。
/// 使うのは交換後の再開と同じ Run(エリア, 0) で、周回数の設定は書き換えない。
/// </summary>
public sealed class AutoDutyKeeper(
    AnomalyLog anomalyLog,
    AutoDutyIpc autoDuty,
    AutoRetainerIpc autoRetainer,
    ExchangeExecutor executor)
{
    /// <summary>
    /// 再開したのに、コンテンツへ入らないまますぐ止まった回数の上限。
    ///
    /// ユーザーが手動で AutoDuty を止めた場合、こちらが再開させると
    /// 止めたいのに止まらない状態になる。何度も押し返されたら諦める。
    /// </summary>
    private const int CountermandLimit = 2;

    /// <summary>再開したあと、これだけの間に止まったら「押し返された」とみなす。</summary>
    private static readonly TimeSpan CountermandWindow = TimeSpan.FromSeconds(20);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly AutoDutyIpc autoDuty = autoDuty;
    private readonly AutoRetainerIpc autoRetainer = autoRetainer;
    private readonly ExchangeExecutor executor = executor;

    private bool sawRunning;
    private bool enteredDutySinceRestart = true;
    private DateTime stoppedSinceUtc = DateTime.MinValue;
    private DateTime lastRestartUtc = DateTime.MinValue;
    private int countermands;

    /// <summary>
    /// 停止する前に「周回=false かつ 停止=false」を観測したか。
    ///
    /// これが周回完了と手動停止を分ける決め手になる。
    ///
    /// 周回完了では LoopsCompleteActions が
    ///   States &= ~PluginState.Looping;   ← 即座
    ///   TaskManager.Enqueue(... Stage = Stage.Stopped);  ← キューの最後
    /// の順で処理するため、Looping が落ちてから停止するまでに必ず間があく
    /// （ループ間処理の実行時間そのもの。実測で 19〜25 秒）。
    ///
    /// 手動停止は Stage = Stage.Stopped が直接 StopAndResetALL を呼び、
    /// States = PluginState.None と Stage の変更が同一フレームで起きる。
    /// つまりこの中間状態は存在しない。
    /// </summary>
    private bool sawLoopingClearedBeforeStop;

    /// <summary>直前に観測した状態。変化したときだけログに残す。</summary>
    private bool? lastStopped;
    private bool? lastLooping;

    /// <summary>この起動中に再開させた回数。</summary>
    public int RestartCount { get; private set; }

    /// <summary>押し返されたと判断して、維持をやめた状態か。</summary>
    public bool GaveUp { get; private set; }

    /// <summary>いま何をしているか。UI に出す。</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>維持をやり直す。UI から呼ぶ。</summary>
    public void Resume()
    {
        this.GaveUp = false;
        this.countermands = 0;
        this.Status = string.Empty;
    }

    public void Tick()
    {
        if (!this.autoDuty.IsLoaded)
        {
            return;
        }

        // 周回中のエリアを覚えておく。再開時に AutoDuty へ渡す必要がある。
        // 停止したあとでは現在地が街になっているため、そこでは取れない。
        this.RememberDutyTerritory();

        if (!Plugin.C.KeepAutoDutyLooping || this.GaveUp)
        {
            return;
        }

        if (!this.autoDuty.TryIsStopped(out var stopped))
        {
            return;
        }

        this.autoDuty.TryIsLooping(out var looping);
        this.TraceStateChange(stopped, looping);

        if (!stopped)
        {
            this.sawRunning = true;
            this.stoppedSinceUtc = DateTime.MinValue;

            if (looping)
            {
                // 周回中。ここから Looping が落ちるのを待つ。
                this.sawLoopingClearedBeforeStop = false;
            }
            else
            {
                // 停止していないのに周回でもない。
                // これは LoopsCompleteActions が Looping を落としたあと、
                // ループ間処理が終わって停止するまでの間にしか現れない。
                this.sawLoopingClearedBeforeStop = true;
            }

            if (Player.IsInDuty)
            {
                // コンテンツまで進めた。押し返されたわけではない。
                this.enteredDutySinceRestart = true;
                this.countermands = 0;
            }

            return;
        }

        // 一度も動いているのを見ていないなら、こちらから始めない。
        // 起動直後にユーザーの意図と無関係な周回を始めてしまう。
        if (!this.sawRunning)
        {
            return;
        }

        if (this.stoppedSinceUtc == DateTime.MinValue)
        {
            this.stoppedSinceUtc = DateTime.UtcNow;

            if (!this.LooksLikeLoopCompletion(out var manualReason))
            {
                // 手動で止められた。再開させない。
                // 次にユーザーが AutoDuty を動かしたら、そこからまた維持を始める。
                this.sawRunning = false;
                this.Status = $"手動停止と判断しました（{manualReason}）";
                this.anomalyLog.Info("AutoDuty", $"AutoDuty の停止を検知しましたが、再開させません（{manualReason}）");
                return;
            }

            this.anomalyLog.Trace("AutoDuty", "AutoDuty の停止を検知しました（周回完了と判断）");

            // 再開させた直後にコンテンツへ入らないまま止まったなら、
            // ユーザーが手で止めた可能性が高い。
            if (!this.enteredDutySinceRestart &&
                DateTime.UtcNow - this.lastRestartUtc < CountermandWindow)
            {
                this.countermands++;

                if (this.countermands >= CountermandLimit)
                {
                    this.GaveUp = true;
                    this.Status = "手動で止められたと判断し、周回の維持をやめました";
                    this.anomalyLog.Warn(
                        "AutoDuty",
                        "再開させても続けて停止したため、周回の維持をやめました。" +
                        "再開させたい場合は状況タブから再開してください");
                    Svc.Chat.Print("[Auto Collector] AutoDuty の周回維持をやめました（手動停止と判断）");
                    return;
                }
            }
        }

        if (!this.CanRestart(out var reason))
        {
            this.Status = reason;
            return;
        }

        this.Restart();
    }

    /// <summary>
    /// 観測した停止が「周回を終えた結果」かどうかを判定する。
    ///
    /// AutoDuty の 2 つの停止経路には、外から見て確実に区別できる差がある。
    ///
    /// 周回完了（LoopsCompleteActions）:
    ///   States &= ~PluginState.Looping;                    ← 即座
    ///   TaskManager.Enqueue(... Stage = Stage.Stopped);    ← ループ間処理の後
    ///   さらに、ここへ来るのは必ずコンテンツを出たあと。
    ///
    /// 手動停止（/ad stop・停止ボタン）:
    ///   Stage = Stage.Stopped → StopAndResetALL() → States = PluginState.None
    ///   Looping と Stage が同一フレームで変わるため、中間状態が存在しない。
    ///   コンテンツ中でも止められる。
    /// </summary>
    private bool LooksLikeLoopCompletion(out string manualReason)
    {
        // コンテンツ中の停止は手動と断定してよい。
        // 周回完了で停止するのはコンテンツを出たあとだからである。
        //
        // ただし AutoExitDuty が無効だと、最終周は CheckFinishing の else 側で
        // コンテンツ内のまま停止する。その構成ではこの判定を使わない。
        if (Player.IsInDuty && this.autoDuty.GetConfigBool("AutoExitDuty", true))
        {
            manualReason = "コンテンツ中に停止しました";
            return false;
        }

        // ループ間処理の期間（周回=false かつ 停止=false）を観測していないなら、
        // Looping と停止が同時に変わったということ。手動停止の形である。
        if (!this.sawLoopingClearedBeforeStop)
        {
            manualReason = "周回の終了処理を経ずに停止しました";
            return false;
        }

        manualReason = string.Empty;
        return true;
    }

    /// <summary>状態が変わったときだけ記録する。停止の前後を後から追えるようにする。</summary>
    private void TraceStateChange(bool stopped, bool looping)
    {
        if (this.lastStopped == stopped && this.lastLooping == looping)
        {
            return;
        }

        this.lastStopped = stopped;
        this.lastLooping = looping;

        if (Plugin.C.DetailedLogEnabled)
        {
            this.anomalyLog.Trace(
                "AutoDuty",
                $"状態が変わりました: 停止={stopped} 周回={looping} Duty={Player.IsInDuty} " +
                $"ループ間処理を観測={this.sawLoopingClearedBeforeStop}");
        }
    }

    /// <summary>周回中のエリアを記録する。</summary>
    private void RememberDutyTerritory()
    {
        if (!Player.IsInDuty)
        {
            return;
        }

        var territory = Svc.ClientState.TerritoryType;
        if (territory == 0 || territory == Plugin.C.LastDutyTerritoryId)
        {
            return;
        }

        // AutoDuty が経路を持っているエリアだけを覚える。
        // 経路が無いエリアを渡しても再開できない。
        if (!this.autoDuty.TryContentHasPath(territory, out var hasPath) || !hasPath)
        {
            return;
        }

        Plugin.C.LastDutyTerritoryId = territory;
        EzConfig.Save();

        this.anomalyLog.Trace("AutoDuty", $"周回中のエリアとして {NpcLocationService.GetTerritoryName(territory)} を記録しました");
    }

    private bool CanRestart(out string reason)
    {
        // 交換の最中は触らない。交換後の再開はそちらが行う。
        if (this.executor.Step is not (ExchangeStep.Idle or ExchangeStep.Done))
        {
            reason = "交換の処理中です";
            return false;
        }

        // 結果が未確認の交換が残っている場合は何も動かさない。
        if (this.executor.InFlight is not null)
        {
            reason = "前回の交換の結果が未確認です";
            return false;
        }

        var territory = Plugin.C.LastDutyTerritoryId;
        if (territory == 0)
        {
            reason = "周回していたエリアが分かりません";
            return false;
        }

        if (Player.IsInDuty)
        {
            reason = "コンテンツ中です";
            return false;
        }

        if (!GenericHelpers.IsScreenReady() || !Player.Available || !Player.Interactable)
        {
            reason = "画面の読み込み中です";
            return false;
        }

        // 停止直後は AutoDuty の終了処理が残っていることがある。少し置く。
        var settle = TimeSpan.FromSeconds(Math.Max(0, Plugin.C.AutoDutyRestartDelaySeconds));
        if (DateTime.UtcNow - this.stoppedSinceUtc < settle)
        {
            reason = "AutoDuty の停止後の処理を待っています";
            return false;
        }

        // AutoRetainer が動いているなら終わるまで待つ。
        // 途中で AutoDuty を動かすとリテイナー処理と取り合いになる。
        if (this.autoRetainer.IsLoaded && this.autoRetainer.IsBusyFailClosed())
        {
            reason = "AutoRetainer の処理が終わるのを待っています";
            return false;
        }

        if (!this.autoDuty.TryContentHasPath(territory, out var hasPath) || !hasPath)
        {
            reason = $"{NpcLocationService.GetTerritoryName(territory)} に AutoDuty の経路がありません";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void Restart()
    {
        var territory = Plugin.C.LastDutyTerritoryId;

        // loops には 0 を渡す。0 以外だと AutoDuty の周回数設定が恒久的に書き換わる。
        if (!this.autoDuty.TryRun(territory))
        {
            this.Status = "AutoDuty へ再開を依頼できませんでした";
            this.anomalyLog.Warn("AutoDuty", this.Status);
            return;
        }

        this.RestartCount++;
        this.lastRestartUtc = DateTime.UtcNow;
        this.stoppedSinceUtc = DateTime.MinValue;
        this.enteredDutySinceRestart = false;
        this.Status = $"周回を再開させました（{this.RestartCount} 回目）";

        this.anomalyLog.Info(
            "AutoDuty",
            $"周回が終わって停止したため、{NpcLocationService.GetTerritoryName(territory)} で再開させました（{this.RestartCount} 回目）");
    }
}
