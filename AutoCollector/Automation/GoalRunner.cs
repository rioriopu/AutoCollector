using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;

namespace AutoCollector.Automation;

/// <summary>目標に届かずに終わった記録。</summary>
/// <param name="Reason">画面に出す理由。</param>
/// <param name="RetryAtUtc">
/// この時刻を過ぎたら自動でもう一度試す。null なら自動では試さない。
/// </param>
public sealed record GoalBlock(string Reason, DateTime? RetryAtUtc);

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
    /// <summary>
    /// 暴走への歯止め。これを超えたら理由に関わらず打ち切る。
    ///
    /// **止める役はこれではない。**
    /// 普段は「何も動かない周が 2 回続いたら止める」が効く。
    /// こちらは、その判定をすり抜けて回り続ける状態（進んでいるように見えるのに
    /// 実は同じところを行き来している、など）への最後の受け皿。
    ///
    /// 300 にしていたため、素材が潤沢な上限なしの周回が
    /// 途中で打ち切られていた。素材が尽きるまで回すのが本来の動き。
    /// 歯止めとしての意味を保ちつつ、実用で当たらない数にする。
    /// </summary>
    private const int MaxRounds = 5000;

    /// <summary>何も動かない周がこれだけ続いたら止める。</summary>
    private const int MaxIdleRounds = 2;

    /// <summary>依頼した処理が動き出すのを待つ時間。交換は索引づくりを待つことがある。</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(90);

    /// <summary>止まっているあいだ、始める相手を探す間隔。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 呼び鈴が無くて止まったあと、もう一度試すまでの時間。
    ///
    /// 呼び鈴の前まで歩けば解消する。歩く時間は取りつつ、
    /// 2 秒ごとに試して記録を埋め尽くさない程度に空ける。
    /// </summary>
    private static readonly TimeSpan BellRetryInterval = TimeSpan.FromSeconds(30);

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

    /// <summary>枯渇をチャットへ出したか。1 回の実行で 1 度だけにする。</summary>
    private bool announcedDepletion;

    /// <summary>
    /// 目標に届かずに終わったプリセットと、その理由。
    ///
    /// 覚えておかないと、終わった 2 秒後に同じ条件でまた走り出す。
    /// プリセットを一度無効にすると忘れる。やり直したいときはそれで入り直せる。
    ///
    /// **理由には 2 種類ある。**
    ///
    /// | 種類 | 例 | 扱い |
    /// |---|---|---|
    /// | 変わらないもの | 素材が尽きた・利用者が止めた・打ち切り | 自動では試さない |
    /// | いずれ解消するもの | 呼び鈴が近くにない | 時間を空けて自動で試す |
    ///
    /// 全部を「変わらないもの」として扱っていたため、呼び鈴の前まで歩いても
    /// 二度と動き出さなかった。
    /// </summary>
    private readonly Dictionary<Guid, GoalBlock> blocked = [];

    /// <summary>
    /// 次に終わるとき、この時間だけ空けてから自動でもう一度試す。
    /// null なら自動では試さない。<see cref="BeginNextAction"/> の頭で毎回消す。
    /// </summary>
    private TimeSpan? retryAfterOnFinish;

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

    /// <summary>
    /// 止める。外部プラグインの抑制も必ず解く。
    ///
    /// **移動と交換も止める。**
    /// 束ねている側だけ止めても、いま走っている移動は ExchangeExecutor が握っている。
    /// CollectableCycleRunner.Stop は自分の状態を変えるだけで、移動は止めない。
    /// ここで Abort を通さないと、「止める」を押しても歩き続ける。
    /// </summary>
    public void Stop(string reason)
    {
        if (!this.IsRunning)
        {
            // 走っていなくても、止まった記録だけは残っていることがある。
            // 旗が残っていたら必ず解く。
            this.ReleaseMonitor();
            return;
        }

        this.restock.Stop(reason);
        this.craft.Stop(reason);
        this.cycle.Stop(reason);

        if (this.executor.IsBusy)
        {
            this.executor.Abort(reason);
        }

        this.Finish(GoalStep.Done, reason);
    }

    /// <summary>監視に預けた旗を解く。握ったままにすると、以後どのプリセットも動かない。</summary>
    private void ReleaseMonitor()
    {
        this.monitor.PreferredPresetId = Guid.Empty;
        this.monitor.SuppressAutoStart = false;
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

        // 製作中は始めない。移動やリテイナーへの操作が弾かれる。
        if (IsCraftingNow())
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

            // 欲しいアイテムが選ばれていなければ、目標が立たない。
            // 走らせると、何も買えない交換へ 2 回行って打ち切られるだけになる。
            if (!preset.CraftToEarn || preset.Rewards.Count == 0)
            {
                continue;
            }

            if (this.blocked.TryGetValue(preset.Id, out var block))
            {
                // いずれ解消する理由なら、時間を空けてもう一度試す。
                if (block.RetryAtUtc is null || DateTime.UtcNow < block.RetryAtUtc)
                {
                    continue;
                }

                this.blocked.Remove(preset.Id);
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
        => this.blocked.TryGetValue(presetId, out var block) ? block.Reason : string.Empty;

    /// <summary>止まったが、いずれ自動でもう一度試す場合、あと何秒か。試さないなら null。</summary>
    public int? BlockedRetryInSeconds(Guid presetId)
    {
        if (!this.blocked.TryGetValue(presetId, out var block) || block.RetryAtUtc is null)
        {
            return null;
        }

        return Math.Max(0, (int)(block.RetryAtUtc.Value - DateTime.UtcNow).TotalSeconds);
    }

    /// <summary>止まっているプリセットがあるか。状況タブの見出しで使う。</summary>
    public bool HasBlocked => this.blocked.Count > 0;

    /// <summary>止まった理由を忘れて、もう一度走らせられるようにする。</summary>
    public void ClearBlock(Guid presetId) => this.blocked.Remove(presetId);

    private void Begin(ExchangePreset preset)
    {
        this.presetId = preset.Id;
        this.trace.Clear();
        this.LastFailure = string.Empty;
        this.Rounds = 0;
        this.idleRounds = 0;
        this.announcedDepletion = false;
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

        // **製作中は次を始めない。**
        // 手が動いているあいだに移動やリテイナーへの操作を撃つと、ゲーム側で弾かれる。
        // 数え終わり（所持数が目標に届いた時点）と、製作画面が閉じる時点はずれる。
        if (IsCraftingNow())
        {
            this.StatusDetail = "製作が終わるのを待っています";
            return;
        }

        // まだ動き出していない。動き出すまで待つ。
        if (!this.observedBusy)
        {
            if (DateTime.UtcNow > this.startDeadlineUtc)
            {
                // 監視の判断を添える。これが無いと「始まりませんでした」だけが残り、
                // 交換するものが無かったのか、索引が間に合わなかったのか区別できない。
                var detail = this.Step == GoalStep.Exchanging && !string.IsNullOrEmpty(this.monitor.LastDecision)
                    ? $"{Describe(this.Step)}が始まりませんでした（{this.monitor.LastDecision}）"
                    : $"{Describe(this.Step)}が始まりませんでした";

                this.Finish(GoalStep.Error, detail);
            }

            return;
        }

        // 取り出しの結果を見る。
        //
        // **1 個でも取れたなら、同じ不足でもう一度行く価値がある。**
        // 覚えたままにしていたため、1 周目で取り出して使い切ったあと、
        // 2 周目は同じ不足に見えて取りに行かず、リテイナーに素材が山ほどあるのに
        // 「いま持っている素材では 1 個も作れません」で止まっていた。
        if (this.Step == GoalStep.Restocking && this.restock.Withdrawn > 0)
        {
            this.lastRestockSignature = string.Empty;
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

        // 自動でやり直すかどうかは、この 1 回の判断ごとに決め直す。
        this.retryAfterOnFinish = null;

        var preset = this.Preset ?? Plugin.C.Presets.FirstOrDefault(x => x.Id == this.presetId);

        if (preset is null)
        {
            reason = "プリセットが見つかりません";
            return false;
        }

        if (preset.Rewards.Count == 0)
        {
            reason = "欲しいアイテムが選ばれていません";
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

        // 2. 目標ぶんのスクリップが貯まった。交換して終わらせる。
        //
        // **上限なしのときはここを通らない。** 目標が無いので「貯まった」が常に真になり、
        // 作りもせず交換へ行き来するだけで終わってしまう。
        //
        // 監視の閾値は問わない。上限まで貯めてから交換する設定にしていると、
        // 目標ぶんが貯まっても閾値に届かず、いつまでも交換されない。
        if (!goal.Endless && goal.MissingScrips <= 0)
        {
            if (this.monitor.RequestManualRun(preset, out var exchangeReason))
            {
                // 監視の判断も残す。始まらなかったときに理由をたどれるようにするため。
                this.BeginWaiting(
                    GoalStep.Exchanging,
                    $"交換へ向かいます（残り {TotalRemaining(goal)} 個）… {exchangeReason}");
                return true;
            }

            reason = $"交換を始められません（{exchangeReason}）";
            return false;
        }

        // 3. 納品できる収集品を持っている。納品して貯める。
        // 上限に達したときの交換も CollectableCycleRunner が面倒をみる。
        if (this.HasDeliverable(goal.CurrencyItemId))
        {
            if (this.cycle.Start(out var cycleReason))
            {
                this.BeginWaiting(
                    GoalStep.Cycling,
                    goal.Endless
                        ? "納品へ向かいます"
                        : $"納品へ向かいます（あと {goal.MissingScrips:N0} {goal.CurrencyName}）");
                return true;
            }

            this.Note($"納品へ進めませんでした: {cycleReason}");
        }

        // 4 と 5. 作る。
        if (this.BeginCraft(preset, goal, out var craftReason))
        {
            return true;
        }

        // 作れない。持っているスクリップで買えるなら、使い切ってから終わる。
        //
        // ここが無いと、素材が尽きた時点で貯めたスクリップが宙に浮く。
        // 「作れるぶんだけ作って進める」のだから、貯めたぶんも使い切る。
        //
        // **やり直す見込みがあるうちは使わない。**
        // 呼び鈴へ行けないだけの一時的な理由で止まるとき、その場で交換所へ出発すると
        // 「取りに行けないのに、なぜ交換所へ行ったのか」が分からなくなる。
        // 近寄れば作れるのだから、貯めたぶんは残しておく。
        if (this.retryAfterOnFinish is null && goal.CheapestCost > 0 && goal.HeldScrips >= goal.CheapestCost)
        {
            this.Note($"これ以上作れません: {craftReason}");

            if (this.monitor.RequestManualRun(preset, out var lastReason))
            {
                this.BeginWaiting(GoalStep.Exchanging, "貯めたぶんを交換して終わります");
                return true;
            }

            reason = $"{craftReason} / 交換も始められません（{lastReason}）";
            return false;
        }

        reason = craftReason;
        return false;
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

        // 上限なしのときは個数を決めない。鞄の空き枠いっぱいまで作る。
        if (!goal.Endless && goal.CollectablesNeeded <= 0)
        {
            reason = "作る個数を計算できません";
            return false;
        }

        var plan = this.craftPlans.BuildPlan(
            preset.CraftCollectableItemId,
            Math.Max(0, preset.CraftKeepFreeSlots),
            goal.Endless ? 0 : goal.CollectablesNeeded);

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

        // **足りないことと、作れないことは別物。**
        //
        // 中間素材は、その素材が鞄にあるなら自分で作れる。製作の手順は
        // 中間素材から先に作るようにできているので、そのまま進めてよい。
        //
        // ここを分けていなかったため、黒麦 270 個（黒麦粉 135 個ぶん）を
        // 取り出し終えていても、黒麦粉が足りないという理由だけで止まっていた。
        var blocking = plan.Materials.Where(x => !x.CanCoverFromBag()).ToList();

        // 同じ不足で 2 度取りに行かない。
        //
        // リテイナーに無いものは、何度行っても無い。
        // 覚えておかないと「取りに行く → 取れない → 取りに行く」を繰り返し、
        // 作れるぶんまで作らずに終わってしまう。
        var signature = string.Join(",", shortfalls.Select(x => $"{x.ItemId}:{x.Shortfall}"));

        if (blocking.Count > 0 && signature != this.lastRestockSignature)
        {
            // 文言ではなく真偽で判定する。画面の文言を直しても動作が変わらないように。
            if (!this.restock.IsBellReachable())
            {
                var bell = this.restock.DescribeBell();
                this.Note($"素材が {blocking.Count} 種類足りませんが、{bell}");

                // 呼び鈴はいずれ近くに来る。歩いて行けば解消するので、自動でやり直す。
                this.retryAfterOnFinish = BellRetryInterval;

                // **取りに行けなかったことを理由に残す。**
                // ここを落とすと、止まった理由が「いま持っている素材では 1 個も作れません」
                // だけになり、素材を買い足しに行くことになる。本当は呼び鈴の前に立てば動く。
                return this.BeginCraftWithinMaterials(
                    preset,
                    goal,
                    $"{bell}。呼び鈴の近くへ移動してから、もう一度試してください",
                    out reason);
            }

            this.lastRestockSignature = signature;

            var requests = shortfalls
                .Select(x => new RestockRequest
                {
                    ItemId = x.ItemId,
                    Name = x.Name,
                    Remaining = x.Shortfall,

                    // クリスタルは個数指定が出ない。すべて受け取る。
                    RetrieveAll = x.IsCrystal,

                    // 作れる素材が手に入らなければ、その素材を取りに行く。
                    Fallback = x.SubMaterials
                        .Where(sub => sub.Shortfall > 0)
                        .Select(sub => new RestockRequest
                        {
                            ItemId = sub.ItemId,
                            Name = sub.Name,
                            Remaining = sub.Shortfall,
                            RetrieveAll = sub.IsCrystal,
                        })
                        .ToList(),
                })
                .ToList();

            // **行く前に、あるかどうかを見立てる。**
            //
            // 呼び鈴まで歩いて全員を開いて、何も無くて帰ってくるのは時間の無駄。
            // 覚えている持ち物で「どこにも無い」と言い切れるなら、行かずに止める。
            //
            // 覚えていない相手が 1 人でもいれば行って確かめる。
            // 知らないことを「無い」と決めつけない。
            var stock = this.restock.JudgeStock(requests, out var stockDetail);

            if (stock == RetainerStock.Depleted)
            {
                // 取れるぶんがあるなら、まずそれを作ってから枯渇と判断する。
                if (this.BeginCraftWithinMaterials(preset, goal, string.Empty, out reason))
                {
                    return true;
                }

                var depleted = string.IsNullOrEmpty(stockDetail)
                    ? "素材が尽きました"
                    : $"素材が尽きました（{stockDetail}）";

                this.Note($"リテイナーにも残っていません: {stockDetail}");
                this.AnnounceDepleted(preset, stockDetail);

                reason = depleted;
                return false;
            }

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

        // 取りに行っても用意できないものが残った。作れるぶんだけ作る。
        //
        // 中間素材は数えない。素材が鞄にあれば、先に作ってから進む。
        if (blocking.Count > 0)
        {
            var shortage = DescribeShortfalls(blocking);

            if (this.BeginCraftWithinMaterials(
                    preset,
                    goal,
                    $"リテイナーから取り出しても足りませんでした（{shortage}）",
                    out reason))
            {
                return true;
            }

            // 取りに行っても足りず、手持ちでも 1 個も作れない。そこで打ち止め。
            this.AnnounceDepleted(preset, shortage);
            return false;
        }

        // 自分で作る素材があるなら、そう分かるように残す。
        // 「足りないのに進んだ」ように見えるのを防ぐ。
        var makeFirst = plan.Materials.Where(x => x.Shortfall > 0 && x.CanCoverFromBag()).ToList();

        if (makeFirst.Count > 0)
        {
            this.Note($"先に作る素材: {string.Join(" / ", makeFirst.Select(x => $"{x.Name}×{x.Shortfall}"))}");
        }

        return this.StartCraft(plan, goal, out reason);
    }

    /// <summary>
    /// いま製作の最中か。
    ///
    /// 製作画面が開いているあいだは移動もリテイナーへの操作も弾かれる。
    /// <c>CraftRunner</c> は所持数が目標に届いた時点で終わりと見なすため、
    /// 画面が閉じるより先に次へ進もうとすることがある。
    /// </summary>
    private static bool IsCraftingNow()
        => Svc.Condition[ConditionFlag.Crafting]
        || Svc.Condition[ConditionFlag.PreparingToCraft]
        || Svc.Condition[ConditionFlag.ExecutingCraftingAction];

    /// <summary>
    /// 素材が尽きたことを、自分だけに見えるチャットへ出す。
    ///
    /// 画面を見ていないと止まったことに気づけない。
    /// 放置して戻ってきたときに、何が起きたかがチャット欄に残っているようにする。
    ///
    /// **自分だけに見える。** ほかの人へは流れない。
    /// </summary>
    private void AnnounceDepleted(ExchangePreset preset, string detail)
    {
        // 1 回の実行で 1 度だけ。
        // 止まるまでに何度かこの判断を通ることがあり、そのたびに出すと連呼になる。
        if (this.announcedDepletion)
        {
            return;
        }

        this.announcedDepletion = true;

        try
        {
            var body = string.IsNullOrEmpty(detail)
                ? $"[Auto Collector] アイテムが枯渇した為、「{preset.Name}」を停止します"
                : $"[Auto Collector] アイテムが枯渇した為、「{preset.Name}」を停止します（{detail}）";

            Svc.Chat.Print(body);
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Goal", $"チャットへ出せませんでした: {ex.Message}");
        }
    }

    /// <summary>足りない素材を、名前と個数で並べる。上位 3 件まで。</summary>
    private static string DescribeShortfalls(IReadOnlyList<PlanMaterial> shortfalls)
    {
        var top = shortfalls
            .OrderByDescending(x => x.Shortfall)
            .Take(3)
            .Select(x => $"{x.Name} があと {x.Shortfall}");

        var text = string.Join(" / ", top);

        return shortfalls.Count > 3 ? $"{text} ほか {shortfalls.Count - 3} 種類" : text;
    }

    /// <summary>
    /// いま鞄にある素材だけで作れるぶんを作る。
    ///
    /// 取り出しても足りなかったときに通る。**途中まででも成果が残る。**
    /// 何もせず止めると、集めた素材がそのまま鞄を塞ぐだけになる。
    /// </summary>
    /// <param name="cause">
    /// なぜ手持ちだけで作ることになったか。
    /// 0 個で失敗したときの理由に必ず混ぜる。**素材不足と呼び鈴不在は別の原因。**
    /// </param>
    private bool BeginCraftWithinMaterials(ExchangePreset preset, ScripGoal goal, string cause, out string reason)
    {
        var plan = this.craftPlans.BuildPlan(
            preset.CraftCollectableItemId,
            Math.Max(0, preset.CraftKeepFreeSlots),
            goal.Endless ? 0 : goal.CollectablesNeeded,
            requireMaterials: true);

        if (plan is null || plan.Crafts == 0)
        {
            var detail = plan is not null && plan.Notes.Count > 0
                ? string.Join(" / ", plan.Notes)
                : "素材が足りないため作れません";

            // 本当の原因を先に書く。あとに書くと読み飛ばされる。
            reason = string.IsNullOrEmpty(cause) ? detail : $"{cause}（{detail}）";
            return false;
        }

        this.Note(goal.Endless
            ? $"素材が足りるぶんまで減らします（{plan.Crafts} 個）"
            : $"素材が足りるぶんまで減らします（{goal.CollectablesNeeded} 個 → {plan.Crafts} 個）");
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

        // 頼めたことと、動き出したことは別。
        // Artisan への依頼がその場で失敗すると、Start は true を返したのに
        // 走っていない状態になる。そのまま待つと 90 秒後に「始まりませんでした」とだけ出て、
        // 本当の理由が消える。
        if (!this.craft.IsRunning)
        {
            reason = string.IsNullOrEmpty(this.craft.LastFailure)
                ? "製作が始まりませんでした"
                : this.craft.LastFailure;

            return false;
        }

        this.BeginWaiting(
            GoalStep.Crafting,
            goal.Endless
                ? $"{plan.Target.Name} を {plan.Crafts} 個作ります"
                : $"{plan.Target.Name} を {plan.Crafts} 個作ります（目標まであと {goal.CollectablesNeeded} 個）");
        return true;
    }

    /// <summary>依頼を出した直後の状態にする。動き出すのを待つ。</summary>
    private void BeginWaiting(GoalStep step, string detail)
    {
        // 何かを始められた。あとで終わるときに、やり直しの予約を持ち越さない。
        this.retryAfterOnFinish = null;

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
        this.ReleaseMonitor();

        // 届かずに終わったなら、同じ条件ですぐ走り出さないよう覚えておく。
        if (this.presetId != Guid.Empty)
        {
            var preset = Plugin.C.Presets.FirstOrDefault(x => x.Id == this.presetId);
            var goal = preset is null ? null : this.goals.Build(preset);

            if (goal is null || !goal.Achieved)
            {
                var retryAt = this.retryAfterOnFinish is null
                    ? (DateTime?)null
                    : DateTime.UtcNow.Add(this.retryAfterOnFinish.Value);

                this.blocked[this.presetId] = new GoalBlock(detail, retryAt);
            }
        }

        this.retryAfterOnFinish = null;
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
