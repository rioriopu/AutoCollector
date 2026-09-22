using System;
using System.Linq;
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
    ExchangeExecutor executor,
    AutoDutySetup setup,
    MonitorService monitor)
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

    /// <summary>
    /// 交換がまだ渡せていないときに、周回の再開を待つ上限。
    ///
    /// 交換先を解決できない設定では永久に渡せない。
    /// 待ち続けると周回まで止まるので、諦めて回し直す。
    /// </summary>
    private static readonly TimeSpan PendingExchangeHold = TimeSpan.FromMinutes(3);

    /// <summary>交換を待ち始めた時刻。</summary>
    private DateTime pendingSinceUtc = DateTime.MinValue;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly AutoDutyIpc autoDuty = autoDuty;
    private readonly AutoRetainerIpc autoRetainer = autoRetainer;
    private readonly ExchangeExecutor executor = executor;

    /// <summary>
    /// AutoDuty 側の設定の点検。版が古くて読み書きできないかを見るために持つ。
    ///
    /// **Plugin.P 経由で取りに行かない。** 静的な入り口から掴むと、
    /// 誰が誰に依存しているのかが呼び出し側から読めなくなる。
    /// 稼ぎ方をモジュールへ分けるとき、この依存がそのまま引き継がれる。
    /// </summary>
    private readonly AutoDutySetup setup = setup;

    /// <summary>
    /// 閾値の監視。まだ渡せていない交換があるかを見るために持つ。
    ///
    /// 同じ理由で、こちらも引数で受け取る。
    /// </summary>
    private readonly MonitorService monitor = monitor;

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

    /// <summary>
    /// 周回の維持を止めているか。
    ///
    /// 利用者が「止める」を押したら、押し返さない。
    /// これを見ずに再開させていたため、止めたつもりでも AutoDuty が回り続け、
    /// AutoDuty 側を無効にするまで止まらなかった。
    /// </summary>
    public bool Suspended { get; private set; }

    /// <summary>維持をやり直す。UI から呼ぶ。</summary>
    public void Resume()
    {
        this.GaveUp = false;
        this.Suspended = false;
        this.countermands = 0;
        this.Status = string.Empty;
    }

    /// <summary>
    /// 周回の維持を止める。停止操作から必ず通す。
    ///
    /// **いま走っている周回は別に止める必要がある。**
    /// ここで止まるのは「終わったあとに再開させる」ほうだけ。
    /// AutoDuty には渡した周回数ぶんを自走する力があるため、
    /// 片方だけだと止めたつもりで回り続ける。
    /// </summary>
    public void Suspend(string reason)
    {
        if (this.Suspended)
        {
            return;
        }

        this.Suspended = true;
        this.Status = $"周回の維持を止めています（{reason}）";
        this.anomalyLog.Info("AutoDuty", $"周回の維持を止めます: {reason}");
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

        // **止めているあいだも観測は続ける。**
        //
        // ここで戻ると AutoDuty の状態を見ないまま値が古いまま残る。
        // 有効に戻した 1 回目の Tick が「前に見た停止の続き」として扱われ、
        // その場でコンテンツへ突入してしまう。
        //
        // 見るのは続け、再開させるかどうかだけを止める。
        var holdReason = string.Empty;

        if (!Plugin.C.KeepAutoDutyLooping)
        {
            holdReason = "設定で周回の維持を切っています";
        }
        else if (this.GaveUp)
        {
            holdReason = "押し返されたため維持をやめています";
        }
        else if (this.Suspended)
        {
            holdReason = "止めています";
        }
        else if (this.setup is { NeedsAutoDutyUpdate: true })
        {
            // 古い版では設定を確かめられない。任せると交換に入れないまま回り続ける。
            holdReason = "AutoDuty を更新してください。古い版では周回を任せられません";
        }
        else if (!Plugin.C.Presets.Any(x => x.Enabled))
        {
            // **交換するものが 1 件も無いなら、周回を維持する理由が無い。**
            //
            // ここはプリセットを一切見ていなかった。そのため全部のチェックを外しても、
            // プリセットを消しても、周回だけが回り続けた。
            // 利用者から見ると「オートコレクターを無効にしても止まらない」。
            holdReason = "有効なプリセットが無いため、周回は維持しません";
        }

        if (holdReason.Length > 0)
        {
            this.Status = holdReason;

            // 見た目の状態だけ更新して、再開の判断には進まない。
            // 覚えている途中経過は捨てる。止めているあいだの変化を
            // 「続き」として扱わないため。
            this.sawRunning = false;
            this.stoppedSinceUtc = DateTime.MinValue;
            this.sawLoopingClearedBeforeStop = false;
            this.pendingSinceUtc = DateTime.MinValue;
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

        if (Plugin.C.DetailedLogActive)
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

        // **まだ渡せていない交換があるなら待つ。**
        //
        // ここは「交換が走っているか」しか見ていなかった。
        // 閾値に達していても、索引ができていない・移動できないなどで
        // 渡せずにいるあいだは走っていないため、そのまま周回を再開させていた。
        // 結果、交換されないままコンテンツへ戻り続けた。
        //
        // **ただし永久には待たない。**
        // 交換先をどうしても解決できない設定だと、待ち続けると周回まで止まる。
        // 交換できないことより、周回が止まることのほうが損が大きい。
        // **AD が止まっているなら、待つと詰む。**
        //
        // 交換は周回への相乗りとして動く設計で、AD が動いていないと始まらない
        // （RequireExternalAutomationRunning）。
        //
        // その状態で「交換がまだ済んでいない」を理由に周回を止めると、
        // 交換は AD が動き出すのを待ち、AD は交換が済むのを待つ。
        // 互いに待ち合って、どちらも永久に動かない。
        //
        // 実際、2 周目の軍票交換と納品が終わったところで止まる形で再現した。
        // 待つ意味があるのは「AD がまだ後片づけをしている」あいだだけで、
        // 完全に止まったあとは、再開させることが交換への近道になる。
        if (this.monitor is { HasPendingExchange: true, Snapshot.AutomationRunning: true })
        {
            if (this.pendingSinceUtc == DateTime.MinValue)
            {
                this.pendingSinceUtc = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - this.pendingSinceUtc <= PendingExchangeHold)
            {
                reason = "閾値に達したプリセットの交換がまだ済んでいません";
                return false;
            }

            this.anomalyLog.Warn(
                "AutoDuty",
                $"交換が {PendingExchangeHold.TotalMinutes:F0} 分待っても始まらないため、周回を再開します");
        }

        this.pendingSinceUtc = DateTime.MinValue;

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
