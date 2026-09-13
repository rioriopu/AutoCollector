using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using ECommons.Throttlers;

namespace AutoCollector.Automation;

public enum CraftRunStep
{
    Idle,

    /// <summary>Artisan へ作らせるよう頼んだ直後。動き出すのを待っている。</summary>
    Starting,

    /// <summary>作っている。終わるのを待っている。</summary>
    Crafting,

    Done,
    Error,
}

/// <summary>
/// 計画にそって収集品を作らせる。
///
/// 作るのは Artisan に任せる。こちらは「何を何回作らせるか」を決めて、
/// 終わったことを所持数で確かめる役に徹する。
///
/// <code>
/// Artisan.CraftItem(レシピ, 回数)   作らせる
/// Artisan.IsBusy()                  終わったか
/// </code>
///
/// **回数であって個数ではない。**
/// 黒麦粉は 1 回で 3 個できるため、162 個ほしいなら 54 回。
///
/// **中間素材から先に作る。**
/// 黒麦粉が無いまま収集品を作らせても、素材が足りず止まるだけ。
///
/// 終わりの判定は所持数で行う。Artisan が動いていないことだけでは、
/// 始まる前なのか終わった後なのか区別できない。
/// </summary>
public sealed class CraftRunner(
    AnomalyLog anomalyLog,
    CurrencyService currency,
    ArtisanIpc artisan)
{
    /// <summary>1 手順にかけてよい時間。長い製作でも足りる幅を取る。</summary>
    private static readonly TimeSpan StepLimit = TimeSpan.FromMinutes(30);

    /// <summary>頼んでから動き出すまでに待つ時間。</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(20);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CurrencyService currency = currency;
    private readonly ArtisanIpc artisan = artisan;

    private readonly List<CraftStep> steps = [];
    private readonly List<string> trace = [];

    private int index;
    private DateTime deadlineUtc = DateTime.MinValue;

    public CraftRunStep Step { get; private set; } = CraftRunStep.Idle;

    public string StatusDetail { get; private set; } = string.Empty;

    public string LastFailure { get; private set; } = string.Empty;

    public bool IsRunning => this.Step is CraftRunStep.Starting or CraftRunStep.Crafting;

    /// <summary>どこまで進んだかの記録。画面にそのまま出す。</summary>
    public IReadOnlyList<string> Trace => this.trace;

    /// <summary>いまの手順。</summary>
    public CraftStep? Current => this.index >= 0 && this.index < this.steps.Count ? this.steps[this.index] : null;

    /// <summary>全体の手順数。</summary>
    public int StepCount => this.steps.Count;

    /// <summary>いま何手順目か。</summary>
    public int StepIndex => this.index;

    /// <summary>
    /// 計画から手順を組み立てる。
    ///
    /// 中間素材が足りなければ、それを先に作る手順を前に置く。
    /// </summary>
    public static List<CraftStep> BuildSteps(CraftPlan plan)
    {
        var list = new List<CraftStep>();

        foreach (var material in plan.Materials)
        {
            if (!material.IsIntermediate || material.Shortfall <= 0 || material.RecipeId == 0)
            {
                continue;
            }

            var perCraft = Math.Max(1, material.AmountResult);
            var crafts = (material.Shortfall + perCraft - 1) / perCraft;

            list.Add(new CraftStep(
                material.RecipeId,
                crafts,
                material.Name,
                material.ItemId,
                material.Held + (crafts * perCraft)));
        }

        if (plan.Crafts > 0)
        {
            list.Add(new CraftStep(
                plan.Target.RecipeId,
                plan.Crafts,
                plan.Target.Name,
                plan.Target.ItemId,
                plan.Crafts));
        }

        return list;
    }

    /// <summary>作り始める。</summary>
    public bool Start(CraftPlan plan, out string reason)
    {
        this.LastFailure = string.Empty;
        this.trace.Clear();

        if (this.IsRunning)
        {
            reason = this.LastFailure = "すでに動いています";
            return false;
        }

        if (!this.artisan.IsLoaded)
        {
            reason = this.LastFailure = "Artisan が導入されていません";
            return false;
        }

        var built = BuildSteps(plan);

        if (built.Count == 0)
        {
            reason = this.LastFailure = "作るものがありません";
            return false;
        }

        // 素材が足りないまま始めても止まるだけ。先に知らせる。
        var missing = plan.Materials
            .Where(x => x.Shortfall > 0 && !x.IsIntermediate)
            .Select(x => $"{x.Name} があと {x.Shortfall}")
            .ToList();

        if (missing.Count > 0)
        {
            reason = this.LastFailure = $"素材が足りません（{string.Join(" / ", missing)}）";
            return false;
        }

        this.steps.Clear();
        this.steps.AddRange(built);
        this.index = 0;

        this.Note($"{built.Count} 手順: {string.Join(" → ", built.Select(x => $"{x.Name}×{x.Crafts}回"))}");

        this.BeginCurrent();
        reason = string.Empty;
        return true;
    }

    /// <summary>止める。Artisan 側は自分では止めない。</summary>
    public void Stop(string reason)
    {
        if (!this.IsRunning)
        {
            return;
        }

        this.Note($"止めます: {reason}");
        this.Step = CraftRunStep.Done;
        this.StatusDetail = reason;
    }

    /// <summary>Framework.Update から毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        if (!this.IsRunning)
        {
            return;
        }

        var step = this.Current;

        if (step is null)
        {
            this.Finish(CraftRunStep.Done, "すべて作り終えました");
            return;
        }

        var have = this.currency.TryGetCount(step.ResultItemId, out var count, includeEquipped: true, includeArmory: true)
            ? count
            : 0;

        // 目標に届いたら次へ。Artisan の状態ではなく所持数で見る。
        if (have >= step.ExpectedCount)
        {
            this.Note($"{step.Name} ができました（{have} 個）");
            this.index++;

            if (this.index >= this.steps.Count)
            {
                this.Finish(CraftRunStep.Done, "すべて作り終えました");
                return;
            }

            this.BeginCurrent();
            return;
        }

        this.StatusDetail = $"{step.Name} を作っています（{have} / {step.ExpectedCount}）";

        var busyKnown = this.artisan.TryIsBusy(out var busy);

        if (this.Step == CraftRunStep.Starting)
        {
            if (busyKnown && busy)
            {
                this.Step = CraftRunStep.Crafting;
                this.deadlineUtc = DateTime.UtcNow.Add(StepLimit);
                this.Note($"{step.Name} の製作が始まりました");
                return;
            }

            if (DateTime.UtcNow > this.deadlineUtc)
            {
                this.Finish(CraftRunStep.Error, $"{step.Name} の製作が始まりませんでした");
            }

            return;
        }

        // 動きが止まったのに届いていない。素材切れか、作れない状態。
        if (busyKnown && !busy)
        {
            if (!EzThrottler.Throttle("AutoCollector.CraftSettle", 3000))
            {
                return;
            }

            this.Finish(
                CraftRunStep.Error,
                $"{step.Name} が {have} 個で止まりました（目標 {step.ExpectedCount}）。素材が足りない可能性があります");
            return;
        }

        if (DateTime.UtcNow > this.deadlineUtc)
        {
            this.Finish(CraftRunStep.Error, $"{step.Name} の製作が終わりませんでした");
        }
    }

    /// <summary>いまの手順を Artisan へ頼む。</summary>
    private void BeginCurrent()
    {
        var step = this.Current;

        if (step is null)
        {
            this.Finish(CraftRunStep.Done, "すべて作り終えました");
            return;
        }

        // すでに足りているなら頼まない。
        var have = this.currency.TryGetCount(step.ResultItemId, out var count, includeEquipped: true, includeArmory: true)
            ? count
            : 0;

        if (have >= step.ExpectedCount)
        {
            this.Note($"{step.Name} は足りています（{have} 個）。飛ばします");
            this.index++;
            this.BeginCurrent();
            return;
        }

        if (step.RecipeId > ushort.MaxValue)
        {
            this.Finish(CraftRunStep.Error, $"{step.Name} のレシピ番号が大きすぎます");
            return;
        }

        this.Note($"{step.Name} を {step.Crafts} 回作らせます（レシピ {step.RecipeId}）");

        if (!this.artisan.TryCraftItem((ushort)step.RecipeId, step.Crafts))
        {
            this.Finish(CraftRunStep.Error, $"{step.Name} の製作を頼めませんでした");
            return;
        }

        this.Step = CraftRunStep.Starting;
        this.StatusDetail = $"{step.Name} の製作を始めています";
        this.deadlineUtc = DateTime.UtcNow.Add(StartLimit);
    }

    private void Note(string text)
    {
        this.trace.Add($"{DateTime.Now:HH:mm:ss}  {text}");

        if (this.trace.Count > 40)
        {
            this.trace.RemoveAt(0);
        }
    }

    private void Finish(CraftRunStep step, string detail)
    {
        this.Note($"終了: {detail}");
        this.Step = step;
        this.StatusDetail = detail;

        if (step == CraftRunStep.Error)
        {
            this.LastFailure = detail;
        }

        this.anomalyLog.Info("Craft", $"製作を終えます: {detail}");
    }
}
