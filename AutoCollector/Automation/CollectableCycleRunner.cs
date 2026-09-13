using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;

namespace AutoCollector.Automation;

public enum CycleStep
{
    Idle,

    /// <summary>納品窓口へ向かい、納品している。</summary>
    Delivering,

    /// <summary>交換所へ向かい、交換している。</summary>
    Exchanging,

    Done,
    Error,
}

/// <summary>
/// 納品と交換を交互に回す。
///
/// <code>
/// 収集品を納品 → スクリップが上限に近づいて停止
///   → スクリップを使って交換 → 枠が空く
///     → まだ収集品があれば納品へ戻る
///       → 収集品が尽きる、または進まなくなったら終了
/// </code>
///
/// **止まらなくなる組み合わせがある。**
///
/// 交換の設定が橙貨だけの場合を考える。紫貨を生む収集品を持っていると、
/// 紫貨が上限に達していても納品しようとし、上限なので納品できず、
/// 交換に回っても紫貨は減らず、収集品は鞄に残ったまま。
/// 「収集品がある → 納品しよう → 上限だ → 交換しよう → 紫貨は対象外 →
/// 収集品がある → …」を延々と繰り返す。
///
/// これを二段で防ぐ。
///
/// 1 段目は、納品する意味のある収集品だけを数えること。
///   生むスクリップに余裕がある、またはそのスクリップを減らす設定がある、
///   のどちらかを満たすものだけを対象にする。
///   これにより、上の例の紫貨の収集品は最初から数に入らない。
///   止まる理由も具体的に出せる。
///
/// 2 段目は、一周して何も減っていなければ止めること。
///   1 段目の予測が外れることはある。たとえば紫貨を減らす設定があっても、
///   その交換対象をすでに全部持っていれば実際には減らない。
///   理由が分からなくても必ず止まるようにしておく。
/// </summary>
public sealed class CollectableCycleRunner(
    AnomalyLog anomalyLog,
    ExchangeExecutor executor,
    MonitorService monitor,
    CollectablesNpcService collectablesNpc,
    CollectableRewardService rewards,
    CurrencyService currency,
    SpecialCurrencyMap specialCurrencyMap)
{
    /// <summary>暴走への歯止め。この回数を超えたら理由に関わらず打ち切る。</summary>
    private const int MaxCycles = 50;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly ExchangeExecutor executor = executor;
    private readonly MonitorService monitor = monitor;
    private readonly CollectablesNpcService collectablesNpc = collectablesNpc;
    private readonly CollectableRewardService rewards = rewards;
    private readonly CurrencyService currency = currency;
    private readonly SpecialCurrencyMap specialCurrencyMap = specialCurrencyMap;

    /// <summary>一周の前に控えた状態。何も進まなかったことの判定に使う。</summary>
    private (int Collectables, long Scrips) snapshotBeforeCycle;

    /// <summary>
    /// 依頼した処理が実際に動き出したのを見たか。
    ///
    /// 依頼を受け付けても、その場では動き出さないことがある。
    /// 交換は候補の索引ができるまで待つためで、依頼した直後はまだ止まっている。
    /// 動き出す前に「終わった」と数えると、1 周もせずに止まってしまう。
    /// </summary>
    private bool observedBusy;

    /// <summary>動き出すのを待つ期限。ここを過ぎても動かなければ諦める。</summary>
    private DateTime startDeadlineUtc = DateTime.MinValue;

    public CycleStep Step { get; private set; } = CycleStep.Idle;

    public string StatusDetail { get; private set; } = string.Empty;

    public int Cycles { get; private set; }

    public int Deliveries { get; private set; }

    public int Exchanges { get; private set; }

    public bool IsRunning => this.Step is CycleStep.Delivering or CycleStep.Exchanging;

    /// <summary>回し始める。</summary>
    public bool Start(out string reason)
    {
        if (this.IsRunning)
        {
            reason = "すでに動いています";
            return false;
        }

        if (this.executor.IsBusy)
        {
            reason = "ほかの処理が動いています";
            return false;
        }

        this.Cycles = 0;
        this.Deliveries = 0;
        this.Exchanges = 0;
        this.snapshotBeforeCycle = this.TakeSnapshot();

        this.anomalyLog.Info("Cycle", "納品と交換の繰り返しを始めます");

        return this.BeginNextAction(out reason);
    }

    /// <summary>止める。走っていなければ何もしない。</summary>
    public void Stop(string reason)
    {
        if (!this.IsRunning)
        {
            return;
        }

        this.anomalyLog.Info("Cycle", $"繰り返しを止めます: {reason}");
        this.Step = CycleStep.Done;
        this.StatusDetail = reason;
    }

    /// <summary>Framework.Update から毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        if (!this.IsRunning)
        {
            return;
        }

        // 交換や納品の実行中は待つ。終わってから次を決める。
        if (this.executor.IsBusy)
        {
            this.observedBusy = true;
            this.StatusDetail = this.executor.StatusDetail;
            return;
        }

        // まだ動き出していない。動き出すまで待つ。
        if (!this.observedBusy)
        {
            if (DateTime.UtcNow > this.startDeadlineUtc)
            {
                this.Finish("依頼した処理が始まりませんでした");
            }

            return;
        }

        if (this.Step == CycleStep.Delivering)
        {
            this.Deliveries++;
        }
        else if (this.Step == CycleStep.Exchanging)
        {
            this.Exchanges++;
            this.Cycles++;

            // 交換まで終えて一周。ここで進んだかどうかを見る。
            if (!this.MadeProgress(out var progressDetail))
            {
                this.Finish($"一周しても何も減りませんでした（{progressDetail}）");
                return;
            }

            this.snapshotBeforeCycle = this.TakeSnapshot();

            if (this.Cycles >= MaxCycles)
            {
                this.Finish($"上限の {MaxCycles} 周に達しました");
                return;
            }
        }

        if (!this.BeginNextAction(out var reason))
        {
            this.Finish(reason);
        }
    }

    /// <summary>次にやることを決めて始める。始めるものが無ければ false。</summary>
    private bool BeginNextAction(out string reason)
    {
        reason = string.Empty;

        // 納品する意味のある収集品があるなら納品する。
        var deliverable = this.CountDeliverable(out var blockedDetail);

        if (deliverable > 0)
        {
            var npc = this.collectablesNpc.ChooseDestination(Plugin.C.PreferredCollectablesNpcDataId);

            if (npc is null)
            {
                reason = "納品窓口が見つかりません";
                return false;
            }

            if (!this.executor.RequestDeliveryTrip(npc, out var deliveryFailure))
            {
                reason = deliveryFailure;
                return false;
            }

            this.BeginWaiting(CycleStep.Delivering, $"納品へ向かいます（対象 {deliverable} 種）");
            return true;
        }

        // 納品できないなら、スクリップを使って枠を空ける。
        if (this.monitor.RequestManualRun(out var exchangeReason))
        {
            this.BeginWaiting(CycleStep.Exchanging, "交換へ向かいます");
            return true;
        }

        reason = string.IsNullOrEmpty(blockedDetail)
            ? $"納品も交換もできません（{exchangeReason}）"
            : blockedDetail;

        return false;
    }

    /// <summary>依頼を出した直後の状態にする。動き出すのを待つ。</summary>
    private void BeginWaiting(CycleStep step, string detail)
    {
        this.Step = step;
        this.StatusDetail = detail;
        this.observedBusy = false;

        // 交換は候補の索引づくりを待つことがある。そのぶんの余裕を見る。
        this.startDeadlineUtc = DateTime.UtcNow.AddSeconds(90);
    }

    /// <summary>
    /// 納品する意味のある収集品の種類数。
    ///
    /// 生むスクリップに余裕がある、またはそのスクリップを減らす設定がある、
    /// のどちらかを満たすものだけを数える。
    /// </summary>
    private int CountDeliverable(out string blockedDetail)
    {
        blockedDetail = string.Empty;

        var blocked = new List<string>();
        var count = 0;

        foreach (var (itemId, name, held) in Diagnostics.CollectablesShopReader.ListHeldCollectables())
        {
            if (held <= 0)
            {
                continue;
            }

            if (!this.rewards.TryResolve(itemId, out var reward))
            {
                // 何のスクリップになるか分からないものは、止める理由にしない。
                // 納品してみれば分かる。
                count++;
                continue;
            }

            if (this.HasRoom(reward))
            {
                count++;
                continue;
            }

            // 上限に近い。そのスクリップを減らす設定があるなら、交換で空く見込みがある。
            if (this.HasPresetConsuming(reward.CurrencyItemId))
            {
                count++;
                continue;
            }

            blocked.Add($"{name}（{ItemName(reward.CurrencyItemId)} が上限）");
        }

        if (count == 0 && blocked.Count > 0)
        {
            blockedDetail =
                $"{string.Join(" / ", blocked.Take(3))} を納品できません。" +
                "そのスクリップを減らす交換設定がないため、納品しても上限で止まります";
        }

        return count;
    }

    /// <summary>このスクリップに、あと 1 回納品するだけの余裕があるか。</summary>
    private bool HasRoom(CollectableReward reward)
    {
        if (!this.currency.TryGetCount(reward.CurrencyItemId, out var have))
        {
            return true;
        }

        var cap = this.currency.GetEffectiveCap(reward.CurrencyItemId);

        if (cap is null or 0)
        {
            return true;
        }

        // 一番多くもらえる場合で見る。少なく見積もると上限を超えてしまう。
        return have + reward.HighReward <= cap;
    }

    /// <summary>このスクリップを減らす、有効なプリセットがあるか。</summary>
    private bool HasPresetConsuming(uint currencyItemId)
    {
        foreach (var preset in Plugin.C.Presets)
        {
            if (!preset.Enabled || preset.Rewards.Count == 0)
            {
                continue;
            }

            if (preset.CurrencyItemId == currencyItemId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>いまの収集品とスクリップの量。</summary>
    private (int Collectables, long Scrips) TakeSnapshot()
    {
        var collectables = Diagnostics.CollectablesShopReader.ListHeldCollectables().Sum(x => x.Count);

        long scrips = 0;
        foreach (var (itemId, _) in this.specialCurrencyMap.ListCurrencies())
        {
            if (this.currency.TryGetCount(itemId, out var have))
            {
                scrips += have;
            }
        }

        return (collectables, scrips);
    }

    /// <summary>一周して何かが減ったか。減っていなければ止める。</summary>
    private bool MadeProgress(out string detail)
    {
        var now = this.TakeSnapshot();
        var before = this.snapshotBeforeCycle;

        var collectablesUsed = before.Collectables - now.Collectables;
        var scripsUsed = before.Scrips - now.Scrips;

        detail = $"収集品 {before.Collectables} → {now.Collectables} / スクリップ {before.Scrips} → {now.Scrips}";

        // どちらかが減っていれば進んでいる。
        // スクリップは納品で増えるため、増えていることも進んだ証拠になる。
        return collectablesUsed > 0 || scripsUsed != 0;
    }

    private static string ItemName(uint itemId)
        => ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
               ?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"<{itemId}>";

    private void Finish(string reason)
    {
        this.anomalyLog.Info(
            "Cycle",
            $"繰り返しを終えます（{this.Cycles} 周 / 納品 {this.Deliveries} 回 / 交換 {this.Exchanges} 回）: {reason}");

        this.Step = CycleStep.Done;
        this.StatusDetail = reason;
    }
}
