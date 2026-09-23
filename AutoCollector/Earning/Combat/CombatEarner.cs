using System;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;

namespace AutoCollector.Earning.Combat;

/// <summary>
/// 中断する前の状態。戻すときにこれを見る。
/// </summary>
/// <param name="WasRunning">中断する前、実際に周回していたか。**false なら復帰させない。**</param>
/// <param name="ResumeTerritoryId">再開させるエリア。</param>
/// <param name="WasLooping">周回設定が入っていたか。</param>
public sealed record CombatInterrupt(bool WasRunning, uint ResumeTerritoryId, bool WasLooping);

/// <summary>
/// 戦闘で稼ぐ（AutoDuty の周回に相乗りする）。
///
/// **AutoDuty を触るのはここだけ。**
/// 交換の側は「中断して」「戻して」としか言わない。
/// どのエリアで再開するか、止めてよいか、止めたのは自分か——は全部ここが持つ。
///
/// 以前は交換の実行側が AutoDuty を直接触っており、
/// 止めるとき立てた旗を戻す側が全部下ろせていなかった（F-60）。
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

    /// <summary>Duty 中に見たエリア。再開先の第一候補。</summary>
    private uint observedDutyTerritoryId;

    /// <summary>待っているあいだに動いているのを見たか。止めたあとは証拠が残らないため控える。</summary>
    private bool sawRunning;

    /// <summary>停止要求を何回送ったか。0 のあいだは安全条件を確認し直してよい。</summary>
    private int stopAttempts;

    public string Id => "Combat";

    public string DisplayName => "AutoDuty";

    public bool IsAvailable => this.autoDuty.IsLoaded;

    /// <summary>
    /// 中断する前の状態。中断していなければ null。
    ///
    /// **後始末が消してしまうため、失敗の経路では退避して戻している。**
    /// 交換に失敗しても、止めた周回は元に戻さなければならない。
    /// </summary>
    public CombatInterrupt? Interrupt { get; set; }

    /// <summary>停止要求をもう送ったか。まだなら安全条件を確認し直してよい。</summary>
    public bool HasAttemptedStop => this.stopAttempts > 0;

    /// <summary>
    /// いま周回しているか。
    ///
    /// **状態を取得できない場合は「動いていない」として扱う。**
    /// 判断がつかないまま自動交換を始める方が危ない。
    ///
    /// 直前まで動いていた場合は猶予のあいだ動作中として扱う（<see cref="RunningGrace"/>）。
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

    /// <summary>猶予で動作中と見なしているだけか。画面の表記を分けるために使う。</summary>
    public bool IsRunningOnGrace
        => !(this.autoDuty.IsLoaded && this.autoDuty.TryIsStopped(out var stopped) && !stopped)
           && DateTime.UtcNow - this.lastRunningUtc < RunningGrace;

    /// <summary>稼働中の表記。文言は従来のまま。</summary>
    public string DescribeRunning()
        => this.IsRunningOnGrace ? "AutoDuty（直前まで動作）" : "AutoDuty";

    /// <summary>
    /// 戦闘に「自分で稼ぐ」設定は無い。
    ///
    /// 周回そのものは通貨を生むが、それは利用者が別に回しているもので、
    /// プリセットが自分で起こすものではない。
    /// 交換の歯止めを緩める根拠にはしない。
    /// </summary>
    public bool SuppliesCurrencyFor(ExchangePreset preset) => false;

    /// <summary>直前に記録した再開先。UI から手動で再開するときにも使う。</summary>
    public uint ResumeTerritoryId => this.Interrupt?.ResumeTerritoryId ?? this.observedDutyTerritoryId;

    /// <summary>
    /// 待っているあいだの見張り。**毎フレーム呼ぶ。**
    ///
    /// 交換を始めるのは AutoDuty が停止したあとなので、
    /// その時点では「動いていた」「どのエリアにいた」の証拠が残らない。
    /// 動いているうちに控えておく。
    /// </summary>
    public void Observe()
    {
        if (Player.IsInDuty)
        {
            var current = Svc.ClientState.TerritoryType;
            if (current != 0 && current != this.observedDutyTerritoryId)
            {
                this.observedDutyTerritoryId = current;
            }
        }

        if (this.autoDuty.IsLoaded && this.autoDuty.TryIsStopped(out var stopped) && !stopped)
        {
            this.sawRunning = true;
        }
    }

    /// <summary>見張りの記憶と停止要求の回数を捨てる。交換の区切りで呼ぶ。</summary>
    public void ResetObservation()
    {
        this.sawRunning = false;
        this.stopAttempts = 0;
    }

    /// <summary>中断の記憶を捨てる。</summary>
    public void ForgetInterrupt() => this.Interrupt = null;

    /// <summary>
    /// 中断する前の状態を控える。
    ///
    /// **再開先は「経路があるところ」を選ぶ。現在地に落とさない。**
    ///
    /// 自分で見た Duty のエリアを優先するが、それは待機中に
    /// コンテンツの中にいたときしか記録されない。
    /// 交換はたいてい GC 納品のあと、街から始まるため空のままになる。
    ///
    /// そこで現在地へ落ちていた。街には AutoDuty の経路が無いので、
    /// 「ソリューション・ナイン に経路が無いため再開できません」と出て
    /// 毎回失敗していた（周回の維持が別途拾うので実害は無かったが、
    /// 記録にエラーが残り、原因を探す手間になる）。
    ///
    /// 周回していたエリアは <c>AutoDutyKeeper</c> が覚えている。そちらを次に見る。
    /// </summary>
    public void MarkInterrupting()
    {
        if (this.Interrupt is not null)
        {
            return;
        }

        // 待機中に動いているのを見ていたなら、いま停止していても再開の対象にする。
        var wasRunning = this.autoDuty.IsRunningFailClosed() || this.sawRunning;
        this.autoDuty.TryIsLooping(out var looping);

        uint resumeTerritory = 0;

        foreach (var candidate in new[]
                 {
                     this.observedDutyTerritoryId,
                     Plugin.C.LastDutyTerritoryId,
                     Svc.ClientState.TerritoryType,
                 })
        {
            if (candidate == 0)
            {
                continue;
            }

            if (this.autoDuty.TryContentHasPath(candidate, out var usable) && usable)
            {
                resumeTerritory = candidate;
                break;
            }
        }

        // どれも使えないなら、覚えている値をそのまま持っておく。
        // 再開の段で理由を出す。
        if (resumeTerritory == 0)
        {
            resumeTerritory = this.observedDutyTerritoryId != 0
                ? this.observedDutyTerritoryId
                : Plugin.C.LastDutyTerritoryId;
        }

        this.Interrupt = new CombatInterrupt(wasRunning, resumeTerritory, looping);

        if (wasRunning)
        {
            var hasPathForResume = this.autoDuty.TryContentHasPath(resumeTerritory, out var canResume) && canResume;
            this.anomalyLog.Info(
                "AutoDuty",
                hasPathForResume
                    ? $"再開先として {NpcLocationService.GetTerritoryName(resumeTerritory)} を記録しました"
                    : $"再開先の候補 {NpcLocationService.GetTerritoryName(resumeTerritory)} に AutoDuty の経路がありません。交換後の再開はできない見込みです");

            this.anomalyLog.Info("AutoDuty", "AutoDuty を停止します");
        }
    }

    /// <summary>
    /// コンテンツの最中かどうかの判断は <c>SafetyGuard</c> が持っており、交換の側が見ている。
    /// ここで二重に判断しない。
    /// </summary>
    public bool IsAtSafeBreak(out string reason)
    {
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 中断を 1 フレーム進める。
    ///
    /// **窓は短い。最初の 1 回は間を置かずに送る。**
    /// ここで 1 秒待つと、その間に AutoDuty が次のコンテンツへ入ってしまい、
    /// 交換の機会を逃して次の周回まで持ち越しになる。
    ///
    /// 打ち切りは呼ぶ側が決める。ここでは時間切れを判断しない。
    /// </summary>
    public EarnerStepResult TickSuspend()
    {
        if (!this.autoDuty.IsLoaded)
        {
            this.Interrupt = null;
            return EarnerStepResult.Untouched("AutoDuty は導入されていません");
        }

        this.MarkInterrupting();

        if (this.Interrupt is not { WasRunning: true })
        {
            return EarnerStepResult.Untouched("AutoDuty は動いていません");
        }

        if (this.autoDuty.TryIsStopped(out var stopped) && stopped)
        {
            return EarnerStepResult.Handled("AutoDuty を止めました");
        }

        if (this.stopAttempts > 0 && !EzThrottler.Throttle("AutoCollector.StopAutoDuty", 1000))
        {
            return EarnerStepResult.InProgress("AutoDuty が止まるのを待っています");
        }

        this.stopAttempts++;
        this.autoDuty.TryStop();
        return EarnerStepResult.InProgress("AutoDuty を止めています");
    }

    /// <summary>
    /// 復帰を 1 フレーム進める。
    ///
    /// **呼ぶ前に、こちらが立てた外部の抑制を解いておくこと。**
    /// AutoDuty はループ間処理で AutoRetainer を呼ぶため、
    /// 抑制したまま再開するとリテイナー処理が動かないまま次の周回に入る。
    /// 抑制は稼ぎ手の持ち物ではないので、解除は交換の側が行う。
    /// </summary>
    /// <summary>
    /// 復帰させる必要がもう無いかを、手を出さずに見る。
    ///
    /// **副作用を持たせない。**
    /// 呼ぶ側は、この答えを見てから抑制の解除などの後始末に入る。
    /// 手を出す判断（<see cref="TickResume"/>）と分けておかないと、
    /// 「もう戻っている相手に対して抑制を解く」順序が組み立てられない。
    /// </summary>
    public EarnerStepResult PeekResume()
    {
        if (this.Interrupt is not { WasRunning: true } || !this.autoDuty.IsLoaded)
        {
            return EarnerStepResult.Untouched();
        }

        if (this.autoDuty.TryIsNavigating(out var navigating) && navigating)
        {
            return EarnerStepResult.Handled("AutoDuty は自分で動き出しています");
        }

        if (this.autoDuty.TryIsLooping(out var looping) && looping)
        {
            return EarnerStepResult.Handled("AutoDuty は周回に戻っています");
        }

        return EarnerStepResult.InProgress();
    }

    public EarnerStepResult TickResume()
    {
        var peek = this.PeekResume();
        if (peek.Progress == EarnerProgress.Done)
        {
            return peek;
        }

        var context = this.Interrupt!;

        if (!EzThrottler.Throttle("AutoCollector.ResumeAutoDuty", 2000))
        {
            return EarnerStepResult.InProgress("AutoDuty の再開を待っています");
        }

        if (!this.autoDuty.TryContentHasPath(context.ResumeTerritoryId, out var hasPath) || !hasPath)
        {
            // 周回の維持がこのあと拾うので、ここで止まっても周回は続く。
            // 止まったと誤解させないよう、警告に留める。
            this.anomalyLog.Warn(
                "AutoDuty",
                $"{NpcLocationService.GetTerritoryName(context.ResumeTerritoryId)} に AutoDuty の経路が無いため、ここからは再開できません。" +
                "周回の維持が引き継ぎます（引き継がれない場合は状況タブの「AutoDuty を再開」から）");

            return EarnerStepResult.Failed("再開先に経路がありません");
        }

        this.anomalyLog.Info("AutoDuty", "AutoDuty を再開します（周回カウンタは 0 から数え直しになります）");
        this.autoDuty.TryRun(context.ResumeTerritoryId);
        return EarnerStepResult.InProgress("AutoDuty へ再開を依頼しました");
    }

    /// <summary>
    /// 周回の切れ目まで待つ。割り込んでよければ true。
    ///
    /// AutoDuty は最終周のあとにループ間処理（納品・修理・リテイナー）を行う。
    /// 「停止した」は「ループ間処理も終了処理も全部終わった」を意味する。
    /// </summary>
    public bool WaitForCycleEnd(out string detail)
    {
        detail = string.Empty;

        if (!this.autoDuty.IsLoaded)
        {
            return true;
        }

        if (!this.autoDuty.TryIsStopped(out var stopped))
        {
            // 状態が読めないうちは割り込まない。
            detail = "AutoDuty の状態を取得できません";
            return false;
        }

        return stopped;
    }

    /// <summary>手動で再開する。状況タブのボタンから呼ぶ。</summary>
    public bool TryResumeManually(out string reason)
    {
        var territory = this.ResumeTerritoryId;

        if (territory == 0)
        {
            reason = "再開先のエリアが分かりません";
            return false;
        }

        if (!this.autoDuty.IsLoaded)
        {
            reason = "AutoDuty が導入されていません";
            return false;
        }

        if (!this.autoDuty.TryContentHasPath(territory, out var hasPath) || !hasPath)
        {
            reason = $"{NpcLocationService.GetTerritoryName(territory)} に AutoDuty の経路がありません";
            return false;
        }

        if (!this.autoDuty.TryRun(territory))
        {
            reason = "AutoDuty へ再開を依頼できませんでした";
            return false;
        }

        this.anomalyLog.Info("AutoDuty", $"{NpcLocationService.GetTerritoryName(territory)} で AutoDuty を再開しました");
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 交換に失敗したときの復帰。
    ///
    /// **自分が止めた AutoDuty は、交換に失敗しても元に戻す。**
    /// 止めっぱなしにすると周回が止まったまま棒立ちになる。
    /// </summary>
    public void ResumeAfterFailure()
    {
        try
        {
            if (Plugin.C.ResumeAutoDutyOnFailure &&
                this.Interrupt is { WasRunning: true } context &&
                this.autoDuty.IsLoaded &&
                this.autoDuty.TryContentHasPath(context.ResumeTerritoryId, out var hasPath) && hasPath)
            {
                this.anomalyLog.Info("AutoDuty", "交換に失敗しましたが、停止前に動いていた AutoDuty を再開します");
                this.autoDuty.TryRun(context.ResumeTerritoryId);
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Cleanup", $"AutoDuty を再開できませんでした: {ex.Message}");
        }
    }

    /// <summary>記録に出す状態。原因を追うときの手がかり。</summary>
    public string DescribeState()
    {
        if (!this.autoDuty.IsLoaded)
        {
            return string.Empty;
        }

        var stopped = this.autoDuty.TryIsStopped(out var s) ? s.ToString() : "?";
        var looping = this.autoDuty.TryIsLooping(out var l) ? l.ToString() : "?";
        var navigating = this.autoDuty.TryIsNavigating(out var n) ? n.ToString() : "?";
        return $"AD(停止={stopped} 周回={looping} 移動={navigating})";
    }

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
