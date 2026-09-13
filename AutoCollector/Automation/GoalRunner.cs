using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;

namespace AutoCollector.Automation;

public enum GoalStep
{
    Idle,

    /// <summary>素材をリテイナーから取り出している。</summary>
    Restocking,

    /// <summary>収集品を作っている。</summary>
    Crafting,

    /// <summary>納品と交換を回している。</summary>
    Cycling,

    /// <summary>交換だけを行っている。</summary>
    Exchanging,

    Done,
    Error,
}

/// <summary>
/// 欲しいアイテムが揃うまで、素材取り出し・製作・納品・交換を通しで回す。
///
/// <code>
/// ① 足りない素材をリテイナーから取り出す
/// ② 鞄の空き枠まで収集品を作る
/// ③ 納品する
/// ④ スクリップが上限に達したら納品をやめる
/// ⑤ スクリップを使って欲しいアイテムと交換する
///    → まだ足りなければ ① へ戻る
/// </code>
///
/// ③④⑤ は <see cref="CollectableCycleRunner"/> がすでに持っている。
/// ここはその外側で「まだ足りないか」を見て、足りなければ ①② を挟む役に徹する。
///
/// **毎回やり直す。**
/// 見積もりは最高品質の納品を前提にしているため、外れることがある。
/// 1 つ動作を終えるたびに目標を計算し直し、次に何をするかを決め直す。
/// 途中で手動でアイテムを使っても、次の周でつじつまが合う。
///
/// **進まなくなったら必ず止まる。**
/// 目標が減らず、スクリップも収集品も鞄の空きも動かない周が 2 回続いたら打ち切る。
/// 理由が分からなくても止まるようにしておく。
///
/// **呼び鈴が要るのは素材を取り出すときだけ。**
/// 納品窓口のそばには呼び鈴があるため、一度納品まで進めば以後は自力で回る。
/// 素材が鞄にあるうちは呼び鈴が無くても進む。
/// </summary>
public sealed class GoalRunner(
    AnomalyLog anomalyLog,
    ScripGoalService goals,
    CraftPlanService craftPlans,
    RetainerRestockRunner restock,
    CraftRunner craft,
    CollectableCycleRunner cycle,
    MonitorService monitor,
    ExchangeExecutor executor,
    CurrencyService currency,
    CollectableRewardService rewards)
{
    /// <summary>暴走への歯止め。これを超えたら理由に関わらず打ち切る。</summary>
    private const int MaxRounds = 300;

    /// <summary>何も動かない周がこれだけ続いたら止める。</summary>
    private const int MaxIdleRounds = 2;

    /// <summary>依頼した処理が動き出すのを待つ時間。交換は索引づくりを待つことがある。</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(90);

    /// <summary>止まっているあいだ、始める相手を探す間隔。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly ScripGoalService goals = goals;
    private readonly CraftPlanService craftPlans = craftPlans;
    private readonly RetainerRestockRunner restock = restock;
    private readonly CraftRunner craft = craft;
    private readonly CollectableCycleRunner cycle = cycle;
    private readonly MonitorService monitor = monitor;
    private readonly ExchangeExecutor executor = executor;
    private readonly CurrencyService currency = currency;
    private readonly CollectableRewardService rewards = rewards;

    private readonly List<string> trace = [];

    private Guid presetId;
    private bool observedBusy;
    private DateTime startDeadlineUtc = DateTime.MinValue;
    private (long Remaining, int Scrips, int Collectables, uint FreeSlots) snapshot;
    private int idleRounds;
    private DateTime nextScanUtc = DateTime.MinValue;

    /// <summary>直前に取りに行った不足の内訳。同じ不足で 2 度行かないために覚える。</summary>
    private string lastRestockSignature = string.Empty;

    /// <summary>
    /// 目標に届かずに終わったプリセットと、その理由。
    ///
    /// 覚えておかないと、終わった 2 秒後に同じ条件でまた走り出す。
    /// プリセットを一度無効にすると忘れる。やり直したいときはそれで入り直せる。
    /// </summary>
    private readonly Dictionary<Guid, string> blocked = [];

    public GoalStep Step { get; private set; } = GoalStep.Idle;

    public string StatusDetail { get; private set; } = string.Empty;

    public string LastFailure { get; private set; } = string.Empty;

    public int Rounds { get; private set; }

    public bool IsRunning => this.Step
        is GoalStep.Restocking or GoalStep.Crafting or GoalStep.Cycling or GoalStep.Exchanging;

    /// <summary>いま回しているプリセット。走っていなければ null。</summary>
    public ExchangePreset? Preset => this.IsRunning
        ? Plugin.C.Presets.FirstOrDefault(x => x.Id == this.presetId)
        : null;

    /// <summary>どこまで進んだかの記録。画面にそのまま出す。</summary>
    public IReadOnlyList<string> Trace => this.trace;

    /// <summary>
    /// 走らせる相手を探して、必要なら始める。Framework.Update から毎フレーム呼ぶ。
    /// </summary>
    public void Tick()
    {
        if (this.IsRunning)
        {
            this.Drive();
            return;
        }

        this.TryStartSomething();
    }

    /// <summary>止める。外部プラグインの抑制も必ず解く。</summary>
    public void Stop(string reason)
    {
        if (!this.IsRunning)
        {
            return;
        }

        this.restock.Stop(reason);
        this.craft.Stop(reason);
        this.cycle.Stop(reason);

        this.Finish(GoalStep.Done, reason);
    }

    /// <summary>
    /// 目標つきのプリセットを 1 件選んで始める。
    ///
    /// **上から順に 1 つずつ片づける。**
    /// 橙貨と紫貨の目標を同時に走らせると、移動と製作がぶつかって進まなくなる。
    /// </summary>
    private void TryStartSomething()
    {
        // ほかの処理が動いているあいだは手を出さない。
        if (this.executor.IsBusy || this.restock.IsRunning || this.craft.IsRunning || this.cycle.IsRunning)
        {
            return;
        }

        // 目標の計算は所持数をひととおり数える。毎フレーム行う必要はない。
        if (DateTime.UtcNow < this.nextScanUtc)
        {
            return;
        }

        this.nextScanUtc = DateTime.UtcNow.Add(ScanInterval);

        foreach (var preset in Plugin.C.Presets)
        {
            // 無効にしたら、前回止まった理由は忘れる。入れ直せばやり直せる。
            if (!preset.Enabled)
            {
                this.blocked.Remove(preset.Id);
                continue;
            }

            if (!preset.CraftToEarn || this.blocked.ContainsKey(preset.Id))
            {
                continue;
            }

            var goal = this.goals.Build(preset);

            if (goal is null || goal.Achieved)
            {
                continue;
            }

            this.Begin(preset);
            return;
        }
    }

    /// <summary>目標に届かずに止まった理由。届いている・走っている場合は空。</summary>
    public string BlockedReason(Guid presetId)
        => this.blocked.TryGetValue(presetId, out var reason) ? reason : string.Empty;

    /// <summary>止まった理由を忘れて、もう一度走らせられるようにする。</summary>
    public void ClearBlock(Guid presetId) => this.blocked.Remove(presetId);

    private void Begin(ExchangePreset preset)
    {
        this.presetId = preset.Id;
        this.trace.Clear();
        this.LastFailure = string.Empty;
        this.Rounds = 0;
        this.idleRounds = 0;
        this.snapshot = this.TakeSnapshot(preset);

        // 走っているあいだは、この 1 件だけを相手にする。
        // 自動発火は止める。納品と交換の順番はこちらが決める。
        this.monitor.PreferredPresetId = preset.Id;
        this.monitor.SuppressAutoStart = true;

        this.Note($"「{preset.Name}」の目標に向けて回し始めます");
        this.anomalyLog.Info("Goal", $"{preset.Name}: 目標つきの周回を始めます");

        if (!this.BeginNextAction(out var reason))
        {
            this.Finish(GoalStep.Done, reason);
        }
    }

    /// <summary>始めた処理の終わりを待ち、終わったら次を決める。</summary>
    private void Drive()
    {
        var preset = this.Preset;

        if (preset is null || !preset.Enabled)
        {
            this.Stop("プリセットが無効になりました");
            return;
        }

        if (this.IsSubRunnerBusy(out var busyDetail))
        {
            this.observedBusy = true;
            this.StatusDetail = busyDetail;
            return;
        }

        // まだ動き出していない。動き出すまで待つ。
        if (!this.observedBusy)
        {
            if (DateTime.UtcNow > this.startDeadlineUtc)
            {
                this.Finish(GoalStep.Error, $"{Describe(this.Step)}が始まりませんでした");
            }

            return;
        }

        this.Rounds++;

        if (this.Rounds >= MaxRounds)
        {
            this.Finish(GoalStep.Done, $"上限の {MaxRounds} 回に達しました");
            return;
        }

        // 1 つ終わるたびに、実際に何か動いたかを見る。
        if (this.MadeProgress(preset, out var progressDetail))
        {
            this.idleRounds = 0;
            this.snapshot = this.TakeSnapshot(preset);
        }
        else
        {
            this.idleRounds++;

            if (this.idleRounds >= MaxIdleRounds)
            {
                this.Finish(GoalStep.Done, $"続けても何も動きませんでした（{progressDetail}）");
                return;
            }
        }

        if (!this.BeginNextAction(out var reason))
        {
            this.Finish(GoalStep.Done, reason);
        }
    }

    /// <summary>
    /// 次にやることを決めて始める。始めるものが無ければ false。
    ///
    /// 上から順に見る。順番には意味がある。
    ///
    /// | 順 | 条件 | やること |
    /// |---|---|---|
    /// | 1 | 目標に届いた | 終わる |
    /// | 2 | スクリップがもう足りている | 交換して終わらせる |
    /// | 3 | 納品できる収集品を持っている | 納品と交換を回す |
    /// | 4 | 素材が足りない | リテイナーから取り出す |
    /// | 5 | 素材がある | 作る |
    ///
    /// **2 を 3 より先に置く。**
    /// もう足りているのに納品を続けると、要らない収集品を作って納品し続ける。
    ///
    /// **3 を 5 より先に置く。**
    /// 作った物を鞄に溜めたまま作り続けると、枠が尽きて作れなくなる。
    /// </summary>
    private bool BeginNextAction(out string reason)
    {
        reason = string.Empty;

        var preset = this.Preset ?? Plugin.C.Presets.FirstOrDefault(x => x.Id == this.presetId);

        if (preset is null)
        {
            reason = "プリセットが見つかりません";
            return false;
        }

        var goal = this.goals.Build(preset);

        if (goal is null)
        {
            reason = "通貨を解決できません";
            return false;
        }

        // 1. 届いた。始めるものは無い。呼び出し側がここで終わらせる。
        if (goal.Achieved)
        {
            reason = "目標に届きました";
            return false;
        }

        // 2. スクリップはもう足りている。交換して終わらせる。
        //
        // 監視の閾値は問わない。上限まで貯めてから交換する設定にしていると、
        // 目標ぶんが貯まっても閾値に届かず、いつまでも交換されない。
        if (goal.MissingScrips <= 0)
        {
            if (this.monitor.RequestManualRun(preset, out var exchangeReason))
            {
                this.BeginWaiting(GoalStep.Exchanging, $"交換へ向かいます（残り {TotalRemaining(goal)} 個）");
                return true;
            }

            reason = $"交換を始められません（{exchangeReason}）";
            return false;
        }

        // 3. 納品できる収集品を持っている。納品して貯める。
        if (this.HasDeliverable(goal.CurrencyItemId))
        {
            if (this.cycle.Start(out var cycleReason))
            {
                this.BeginWaiting(
                    GoalStep.Cycling,
                    $"納品へ向かいます（あと {goal.MissingScrips:N0} {goal.CurrencyName}）");
                return true;
            }

            this.Note($"納品へ進めませんでした: {cycleReason}");
        }

        // 4 と 5. 作る。
        return this.BeginCraft(preset, goal, out reason);
    }

    /// <summary>足りないぶんを作る。素材が足りなければ先に取り出す。</summary>
    private bool BeginCraft(ExchangePreset preset, ScripGoal goal, out string reason)
    {
        reason = string.Empty;

        if (preset.CraftCollectableItemId == 0)
        {
            reason = "作る収集品が選ばれていません。プリセットで製作するジョブと収集品を選んでください";
            return false;
        }

        if (goal.Collectable is null)
        {
            reason = "選ばれている収集品が、この通貨を生みません。選び直してください";
            return false;
        }

        if (goal.CollectablesNeeded <= 0)
        {
            reason = "作る個数を計算できません";
            return false;
        }

        var plan = this.craftPlans.BuildPlan(
            preset.CraftCollectableItemId,
            Math.Max(0, preset.CraftKeepFreeSlots),
            goal.CollectablesNeeded);

        if (plan is null)
        {
            reason = "製作の計画を作れません";
            return false;
        }

        if (plan.Crafts == 0)
        {
            reason = plan.Notes.Count > 0
                ? string.Join(" / ", plan.Notes)
                : "鞄に空きが無いため作れません";
            return false;
        }

        var shortfalls = plan.Materials.Where(x => x.Shortfall > 0).ToList();

        // 同じ不足で 2 度取りに行かない。
        //
        // リテイナーに無いものは、何度行っても無い。
        // 覚えておかないと「取りに行く → 取れない → 取りに行く」を繰り返し、
        // 作れるぶんまで作らずに終わってしまう。
        var signature = string.Join(",", shortfalls.Select(x => $"{x.ItemId}:{x.Shortfall}"));

        if (shortfalls.Count > 0 && signature != this.lastRestockSignature)
        {
            var bell = this.restock.DescribeBell();

            if (!bell.StartsWith("呼び鈴が見つかりました", StringComparison.Ordinal))
            {
                this.Note($"素材が {shortfalls.Count} 種類足りませんが、{bell}");
                return this.BeginCraftWithinMaterials(preset, goal, out reason);
            }

            this.lastRestockSignature = signature;

            var requests = shortfalls
                .Select(x => new RestockRequest
                {
                    ItemId = x.ItemId,
                    Name = x.Name,
                    Remaining = x.Shortfall,

                    // 作れる素材が手に入らなければ、その素材を取りに行く。
                    Fallback = x.SubMaterials
                        .Where(sub => sub.Shortfall > 0)
                        .Select(sub => new RestockRequest
                        {
                            ItemId = sub.ItemId,
                            Name = sub.Name,
                            Remaining = sub.Shortfall,
                        })
                        .ToList(),
                })
                .ToList();

            if (!this.restock.Start(requests, out var restockReason))
            {
                reason = $"素材を取り出せません（{restockReason}）";
                return false;
            }

            this.BeginWaiting(
                GoalStep.Restocking,
                $"素材を取り出します（{shortfalls.Count} 種類）");
            return true;
        }

        // 取りに行っても足りなかった。作れるぶんだけ作る。
        if (shortfalls.Count > 0)
        {
            return this.BeginCraftWithinMaterials(preset, goal, out reason);
        }

        return this.StartCraft(plan, goal, out reason);
    }

    /// <summary>
    /// いま鞄にある素材だけで作れるぶんを作る。
    ///
    /// 取り出しても足りなかったときに通る。**途中まででも成果が残る。**
    /// 何もせず止めると、集めた素材がそのまま鞄を塞ぐだけになる。
    /// </summary>
    private bool BeginCraftWithinMaterials(ExchangePreset preset, ScripGoal goal, out string reason)
    {
        var plan = this.craftPlans.BuildPlan(
            preset.CraftCollectableItemId,
            Math.Max(0, preset.CraftKeepFreeSlots),
            goal.CollectablesNeeded,
            requireMaterials: true);

        if (plan is null || plan.Crafts == 0)
        {
            reason = plan is not null && plan.Notes.Count > 0
                ? string.Join(" / ", plan.Notes)
                : "素材が足りないため作れません";

            return false;
        }

        this.Note($"素材が足りるぶんまで減らします（{goal.CollectablesNeeded} 個 → {plan.Crafts} 個）");
        return this.StartCraft(plan, goal, out reason);
    }

    private bool StartCraft(CraftPlan plan, ScripGoal goal, out string reason)
    {
        reason = string.Empty;

        if (!this.craft.Start(plan, out var craftReason))
        {
            reason = $"製作を始められません（{craftReason}）";
            return false;
        }

        this.BeginWaiting(
            GoalStep.Crafting,
            $"{plan.Target.Name} を {plan.Crafts} 個作ります（目標まであと {goal.CollectablesNeeded} 個）");
        return true;
    }

    /// <summary>依頼を出した直後の状態にする。動き出すのを待つ。</summary>
    private void BeginWaiting(GoalStep step, string detail)
    {
        this.Step = step;
        this.StatusDetail = detail;
        this.observedBusy = false;
        this.startDeadlineUtc = DateTime.UtcNow.Add(StartLimit);

        this.Note(detail);
    }

    private bool IsSubRunnerBusy(out string detail)
    {
        if (this.restock.IsRunning)
        {
            detail = $"素材を取り出しています: {this.restock.StatusDetail}";
            return true;
        }

        if (this.craft.IsRunning)
        {
            detail = $"作っています: {this.craft.StatusDetail}";
            return true;
        }

        if (this.cycle.IsRunning)
        {
            detail = $"納品と交換を回しています: {this.cycle.StatusDetail}";
            return true;
        }

        if (this.executor.IsBusy)
        {
            detail = this.executor.StatusDetail;
            return true;
        }

        detail = string.Empty;
        return false;
    }

    /// <summary>このスクリップを生む収集品を持っているか。</summary>
    private bool HasDeliverable(uint currencyItemId)
    {
        foreach (var (itemId, _, held) in CollectablesShopReader.ListHeldCollectables())
        {
            if (held <= 0)
            {
                continue;
            }

            // 何のスクリップになるか分からないものは、持っている扱いにしない。
            // 分からないまま納品へ行っても、目標のスクリップは増えない。
            if (this.rewards.TryResolve(itemId, out var reward) && reward.CurrencyItemId == currencyItemId)
            {
                return true;
            }
        }

        return false;
    }

    private (long Remaining, int Scrips, int Collectables, uint FreeSlots) TakeSnapshot(ExchangePreset preset)
    {
        var goal = this.goals.Build(preset);
        var collectables = CollectablesShopReader.ListHeldCollectables().Sum(x => x.Count);
        var freeSlots = this.currency.TryGetEmptyBagSlots(out var slots) ? slots : 0;

        return (
            goal is null ? 0 : TotalRemaining(goal),
            goal?.HeldScrips ?? 0,
            collectables,
            freeSlots);
    }

    /// <summary>
    /// 一周して何か動いたか。
    ///
    /// 目標の残り・スクリップ・収集品・鞄の空き のどれかが動いていれば進んだと見る。
    /// 鞄の空きを入れているのは、素材を取り出しただけの周を「進んだ」と数えるため。
    /// </summary>
    private bool MadeProgress(ExchangePreset preset, out string detail)
    {
        var now = this.TakeSnapshot(preset);
        var before = this.snapshot;

        detail =
            $"残り {before.Remaining} → {now.Remaining} / " +
            $"スクリップ {before.Scrips} → {now.Scrips} / " +
            $"収集品 {before.Collectables} → {now.Collectables} / " +
            $"空き枠 {before.FreeSlots} → {now.FreeSlots}";

        return now != before;
    }

    private static long TotalRemaining(ScripGoal goal)
        => goal.Items.Where(x => !x.Unlimited).Sum(x => (long)x.Remaining);

    private static string Describe(GoalStep step) => step switch
    {
        GoalStep.Restocking => "素材の取り出し",
        GoalStep.Crafting => "製作",
        GoalStep.Cycling => "納品",
        GoalStep.Exchanging => "交換",
        _ => "処理",
    };

    private void Note(string text)
    {
        this.trace.Add($"{DateTime.Now:HH:mm:ss}  {text}");

        if (this.trace.Count > 60)
        {
            this.trace.RemoveAt(0);
        }
    }

    private void Finish(GoalStep step, string detail)
    {
        // 握ったままにしない。ここを通さないと、以後どのプリセットも自動発火しなくなる。
        this.monitor.PreferredPresetId = Guid.Empty;
        this.monitor.SuppressAutoStart = false;

        // 届かずに終わったなら、同じ条件ですぐ走り出さないよう覚えておく。
        if (this.presetId != Guid.Empty)
        {
            var preset = Plugin.C.Presets.FirstOrDefault(x => x.Id == this.presetId);
            var goal = preset is null ? null : this.goals.Build(preset);

            if (goal is null || !goal.Achieved)
            {
                this.blocked[this.presetId] = detail;
            }
        }

        this.lastRestockSignature = string.Empty;

        this.Note($"終了: {detail}");
        this.Step = step;
        this.StatusDetail = detail;

        if (step == GoalStep.Error)
        {
            this.LastFailure = detail;
        }

        this.anomalyLog.Info("Goal", $"目標つきの周回を終えます: {detail}");
    }
}
