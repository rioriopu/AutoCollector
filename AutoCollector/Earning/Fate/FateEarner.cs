using System;
using System.Numerics;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;

namespace AutoCollector.Earning.Fate;

/// <summary>
/// 交換で中断する前に控えておく、周回の状態。
/// </summary>
/// <param name="WasRunning">中断する前に周回していたか。</param>
/// <param name="TerritoryId">戻るエリア。</param>
/// <param name="Position">離れたときの座標。エリアの中で戻る位置の目安にする。</param>
public sealed record FateInterrupt(bool WasRunning, uint TerritoryId, Vector3 Position);

/// <summary>
/// FATE を回して通貨を稼ぐ。
///
/// <b>稼ぐのはバイカラージェムなどの FATE 通貨。</b>
/// どの通貨が対象かは <see cref="FateTokenService"/> がシートから引く。
/// トームストーンは FATE でも増えるが、それは <c>CombatEarner</c> の担当。
/// ここは「FATE でしか増えない通貨」を受け持つ。
///
/// <b>周回そのものは <see cref="FateRunner"/> が行う。</b>
/// ここは IEarner の口として、中断と復帰だけを受け持つ。
///
/// <code>
/// FATE を回す → 通貨が閾値へ → 中断（いたエリアを控える）
///   → 交換所へ移動して交換 → 控えたエリアへ戻って再開
/// </code>
///
/// <b>自分が止めたときだけ戻す。</b>
/// 利用者が自分で止めていた周回を、交換のあとに勝手に動かさない。
/// この判断は <see cref="EarnerStepResult.Handled"/> と
/// <see cref="EarnerStepResult.Untouched"/> の違いで登録簿へ伝える。
/// </summary>
public sealed class FateEarner(
    FateRunner runner,
    FateTokenService tokens,
    AnomalyLog anomalyLog) : IEarner
{
    /// <summary>周回を戻すまでに置く間。エリア移動の直後は落ち着くのを待つ。</summary>
    private const int ResumeThrottleMs = 2000;

    private readonly FateRunner runner = runner;
    private readonly FateTokenService tokens = tokens;
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>中断する前に控えた状態。復帰に使う。</summary>
    public FateInterrupt? Interrupt { get; private set; }

    public string Id => "Fate";

    public string DisplayName => "FATE 周回";

    public string KindName => "FATE";

    /// <summary>
    /// 使えるか。
    ///
    /// 周回には BossMod Reborn と vnavmesh が要る。
    /// どちらかが無ければ、この稼ぎ方は選べない。
    /// </summary>
    public bool IsAvailable => this.runner.IsUsable;

    public bool IsRunning => this.runner.IsRunning;

    public string DescribeRunning()
        => this.runner.IsRunning ? $"FATE 周回（{this.runner.StatusDetail}）" : "FATE 周回";

    /// <summary>
    /// このプリセットで、自力で通貨を増やせるか。
    ///
    /// <b>周回を動かしているときだけ true。</b>
    /// 止まっている周回は通貨を生まない。止まったまま「増える見込み」と扱うと、
    /// 交換の歯止めが「そのうち貯まる」と判断して終わらない交換を許してしまう。
    /// </summary>
    public bool SuppliesCurrencyFor(ExchangePreset preset)
    {
        if (!this.runner.IsRunning)
        {
            return false;
        }

        return this.CanEarn(preset.CurrencyItemId);
    }

    /// <summary>
    /// この通貨は FATE で増えるか。
    ///
    /// <b>ゲームデータで決まる。</b>FateTokenType シートに載っている通貨だけが対象。
    /// アイテム ID は埋め込まない（docs/16 の 2-2）。
    /// </summary>
    public bool CanEarn(uint currencyItemId) => this.tokens.IsFateCurrency(currencyItemId);

    /// <summary>
    /// これから中断する、という合図。戻る先をここで控える。
    ///
    /// <b>控えるのは 1 回だけ。</b>중断の途中で何度も呼ばれるが、
    /// 2 回目以降に上書きすると、交換所へ移動したあとの座標を
    /// 「戻る先」として覚えてしまう。
    /// </summary>
    public void MarkInterrupting()
    {
        if (this.Interrupt is not null)
        {
            return;
        }

        var wasRunning = this.runner.IsRunning;
        var territory = Svc.ClientState.TerritoryType;
        var position = Player.Available ? Player.Position : Vector3.Zero;

        this.Interrupt = new FateInterrupt(wasRunning, territory, position);

        if (wasRunning)
        {
            this.anomalyLog.Info(
                "Fate",
                $"交換のため FATE 周回を中断します。戻る先として {NpcLocationService.GetTerritoryName(territory)} を控えました");
        }
    }

    /// <summary>
    /// いま中断してよい切れ目か。
    ///
    /// <b>FATE の最中では止めない。</b>
    /// 達成度が進んだ FATE を抜けると、それまでの貢献が無駄になる。
    /// 納品 FATE なら、報酬が入る前に離れると報酬そのものを失う。
    /// </summary>
    public bool IsAtSafeBreak(out string reason)
    {
        if (!this.runner.IsRunning)
        {
            reason = string.Empty;
            return true;
        }

        if (this.runner.IsInFate)
        {
            reason = "FATE の最中です。終わるまで待ちます";
            return false;
        }

        if (this.runner.HasPendingReward)
        {
            reason = "納品 FATE の報酬を待っています";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public EarnerStepResult TickSuspend()
    {
        if (!this.runner.IsUsable)
        {
            this.Interrupt = null;
            return EarnerStepResult.Untouched("FATE 周回は使えません");
        }

        this.MarkInterrupting();

        if (this.Interrupt is not { WasRunning: true })
        {
            return EarnerStepResult.Untouched("FATE 周回は動いていません");
        }

        if (this.runner.IsRunning)
        {
            // 止めるのは 1 回で済む。周回はプラグインの中にあるので、
            // 外部プラグインのように「送ったが届かない」ことがない。
            this.runner.Suspend("交換のため中断");
            return EarnerStepResult.InProgress("FATE 周回を止めています");
        }

        this.anomalyLog.Info("Fate", "FATE 周回を止めました");
        return EarnerStepResult.Handled("FATE 周回を止めました");
    }

    public EarnerStepResult TickResume()
    {
        if (this.Interrupt is not { WasRunning: true } context)
        {
            this.Interrupt = null;
            return EarnerStepResult.Untouched("戻す周回がありません");
        }

        if (this.runner.IsRunning)
        {
            this.Interrupt = null;
            return EarnerStepResult.Handled("FATE 周回は動いています");
        }

        if (!EzThrottler.Throttle("AutoCollector.ResumeFate", ResumeThrottleMs))
        {
            return EarnerStepResult.InProgress("FATE 周回の再開を待っています");
        }

        this.anomalyLog.Info(
            "Fate",
            $"FATE 周回を再開します（{NpcLocationService.GetTerritoryName(context.TerritoryId)} へ戻ります）");

        if (!this.runner.Resume(context.TerritoryId, context.Position, out var reason))
        {
            this.Interrupt = null;
            return EarnerStepResult.Failed(reason);
        }

        this.Interrupt = null;
        return EarnerStepResult.Handled("FATE 周回を再開しました");
    }

    /// <summary>覚えている中断の記録を捨てる。</summary>
    public void ForgetInterrupt() => this.Interrupt = null;

    public void StopForEmergency()
    {
        if (!this.runner.IsRunning)
        {
            return;
        }

        this.runner.Stop("緊急停止");
        this.anomalyLog.Info("Fate", "走っていた FATE 周回も止めました");
    }

    public void ResumeAfterStop()
    {
        // 緊急停止からは自動で戻さない。
        //
        // 利用者が明示的に止めたときに通る道なので、
        // ここで勝手に動かすと「止めたのに止まらない」になる。
        // BossMod のプリセットは Stop の中で必ず解除してある。
        this.Interrupt = null;
    }
}
