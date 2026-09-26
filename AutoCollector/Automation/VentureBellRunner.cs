using System;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Automation;

/// <summary>ベンチャー回収の呼び鈴での段階。</summary>
public enum VentureBellStep
{
    /// <summary>何もしていない。</summary>
    Idle,

    /// <summary>呼び鈴を探している。</summary>
    Searching,

    /// <summary>呼び鈴へ歩いている。</summary>
    Walking,

    /// <summary>呼び鈴に話しかけている。</summary>
    Interacting,

    /// <summary>AutoRetainer が回収し終わるのを待っている。</summary>
    Collecting,

    /// <summary>画面を閉じている。</summary>
    Closing,

    /// <summary>終わった。</summary>
    Done,

    /// <summary>できなかった。</summary>
    Failed,
}

/// <summary>
/// 呼び鈴まで行って開き、AutoRetainer にベンチャーを回収させ、閉じる。
///
/// <b>回収そのものは書かない。</b>
/// 呼び鈴を開けば AutoRetainer が自分で回収する。こちらの役目は
/// 「近寄る」「開く」「終わるのを待つ」「閉じる」だけ。
///
/// <b>呼び鈴が見えないのは「無い」ことではない。</b>
/// 読み込まれていない呼び鈴は <c>Svc.Objects</c> に載らない。
/// 街に着いた直後は目の前のものも見えないため、猶予を持って探す
/// （GbrVentureRelay の知見 2-2。F-2 で実際に踏んだ）。
///
/// <b>フラグ 1 つで開閉を判断しない。</b>
/// <c>ConditionFlag.OccupiedSummoningBell</c> はリテイナー一覧を開いている
/// あいだに落ちることがある。画面の表示も合わせて見る
/// （知見 2-5。MogColle で実際にそうなった）。
/// </summary>
public sealed unsafe class VentureBellRunner(
    AnomalyLog anomalyLog,
    FateTrace trace,
    NavigationService navigation,
    AutoRetainerIpc retainer)
{
    /// <summary>呼び鈴を探す猶予。街に着いた直後は一覧が埋まっていない。</summary>
    private static readonly TimeSpan SearchGrace = TimeSpan.FromSeconds(45);

    /// <summary>呼び鈴まで歩く上限。</summary>
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(90);

    /// <summary>話しかけられるようになるのを待つ上限。</summary>
    private static readonly TimeSpan InteractTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 「回収できるはずなのに AutoRetainer が動き出さない」ときに待つ上限。
    ///
    /// 回収するものが無い場合はこれを待たずに閉じる（直接聞けるため）。
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    /// <summary>回収が終わるのを待つ上限。</summary>
    private static readonly TimeSpan CollectTimeout = TimeSpan.FromMinutes(4);

    /// <summary>話しかけられる距離。</summary>
    private const float InteractRange = 4.5f;

    /// <summary>
    /// 画面を閉じる操作を送る間隔。
    ///
    /// 呼び鈴の画面は何枚か重なっていて、1 枚ずつ閉じる。
    /// 間隔が長いと、そのぶん開いたまま残る。
    /// ゲームが次の画面を出す時間だけあればよいので、短くてよい。
    /// </summary>
    private const int ActionThrottleMs = 150;

    /// <summary>
    /// 呼び鈴の画面。
    ///
    /// フラグだけでは足りないので、これらのどれかが出ているかも見る。
    /// </summary>
    private static readonly string[] BellAddons =
    [
        "RetainerList",
        "SelectString",
        "SelectYesno",
        "RetainerTaskAsk",
        "RetainerTaskResult",
    ];

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly FateTrace trace = trace;
    private readonly NavigationService navigation = navigation;
    private readonly AutoRetainerIpc retainer = retainer;

    /// <summary>いまの段階。</summary>
    public VentureBellStep Step { get; private set; } = VentureBellStep.Idle;

    /// <summary>画面に出す一言。</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>できなかった理由。</summary>
    public string FailureReason { get; private set; } = string.Empty;

    private DateTime stepSinceUtc = DateTime.MinValue;
    private DateTime openedUtc = DateTime.MinValue;
    private bool moveIssued;

    /// <summary>
    /// AutoRetainer が一度でも動き出したか。
    ///
    /// <b>動き出したあとは、開始の猶予を待たない。</b>
    /// 猶予は「まだ動き出していないだけかもしれない」ための保険なので、
    /// 動き終わったあとまで待つと、画面を開いたまま無駄に居座る。
    /// </summary>
    private bool retainerStarted;

    /// <summary>覚えている呼び鈴の場所（エリアごと）。</summary>
    private readonly System.Collections.Generic.Dictionary<uint, Vector3> remembered = [];

    /// <summary>始める。</summary>
    public void Begin()
    {
        this.Step = VentureBellStep.Searching;
        this.Detail = "呼び鈴を探しています";
        this.FailureReason = string.Empty;
        this.stepSinceUtc = DateTime.UtcNow;
        this.openedUtc = DateTime.MinValue;
        this.retainerStarted = false;
        this.moveIssued = false;

        // **AutoRetainer の抑制を外す。**
        // 周回中は交換との競合を避けるために抑制していることがある。
        // 抑制したままでは AutoRetainer が回収を始めない。
        this.retainer.Release();
    }

    /// <summary>やめる。呼び鈴の画面が開いていれば閉じる。</summary>
    public void Cancel()
    {
        if (this.Step is VentureBellStep.Idle or VentureBellStep.Done or VentureBellStep.Failed)
        {
            this.Step = VentureBellStep.Idle;
            return;
        }

        this.navigation.Stop();
        this.moveIssued = false;

        // 開いている途中で止めたなら、AutoRetainer を止めてから閉じる。
        // 止めずに閉じると、相手が動いたまま画面だけ消える。
        if (IsBellOpen())
        {
            this.retainer.TryAbort();
            this.SendCancel();
        }

        this.Step = VentureBellStep.Idle;
        this.Detail = string.Empty;
    }

    /// <summary>終わったか。</summary>
    /// <param name="ok">回収できたら true。</param>
    /// <param name="why">できなかった理由。</param>
    public bool IsFinished(out bool ok, out string why)
    {
        ok = this.Step == VentureBellStep.Done;
        why = this.FailureReason;

        return this.Step is VentureBellStep.Done or VentureBellStep.Failed;
    }

    /// <summary>1 フレーム進める。</summary>
    public void Tick()
    {
        switch (this.Step)
        {
            case VentureBellStep.Searching:
                this.TickSearching();
                return;

            case VentureBellStep.Walking:
                this.TickWalking();
                return;

            case VentureBellStep.Interacting:
                this.TickInteracting();
                return;

            case VentureBellStep.Collecting:
                this.TickCollecting();
                return;

            case VentureBellStep.Closing:
                this.TickClosing();
                return;
        }
    }

    /// <summary>呼び鈴を探す。</summary>
    private void TickSearching()
    {
        if (!Player.Available)
        {
            return;
        }

        var here = Svc.ClientState.TerritoryType;

        // 見えた。場所を覚えて、そこへ向かう。
        if (RetainerRestockRunner.ScanForBell() is { } bell)
        {
            this.remembered[here] = bell.Position;
            this.BeginWalk(bell.Position);
            return;
        }

        // 見えないが、前に来たときの場所を覚えている。そこへ歩けば読み込まれる。
        if (this.remembered.TryGetValue(here, out var known))
        {
            this.trace.State("呼び鈴を覚えている", "見えないので、覚えている場所へ向かいます");
            this.BeginWalk(known);
            return;
        }

        // **見えないことを「無い」と決めつけない。**
        // 街に着いた直後は一覧が埋まっていない。
        if (DateTime.UtcNow - this.stepSinceUtc <= SearchGrace)
        {
            this.Detail = "呼び鈴を探しています";
            return;
        }

        this.Fail($"{SearchGrace.TotalSeconds:F0} 秒探しても呼び鈴が見つかりませんでした");
    }

    /// <summary>呼び鈴へ歩く。</summary>
    private void BeginWalk(Vector3 destination)
    {
        this.Step = VentureBellStep.Walking;
        this.Detail = "呼び鈴へ向かっています";
        this.stepSinceUtc = DateTime.UtcNow;
        this.moveIssued = false;
        this.walkTarget = destination;
    }

    private Vector3 walkTarget;

    private void TickWalking()
    {
        if (!Player.Available)
        {
            return;
        }

        var distance = Vector3.Distance(Player.Position, this.walkTarget);

        // 着いた。
        if (distance <= InteractRange)
        {
            this.navigation.Stop();
            this.moveIssued = false;
            this.Step = VentureBellStep.Interacting;
            this.Detail = "呼び鈴に話しかけています";
            this.stepSinceUtc = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - this.stepSinceUtc > WalkTimeout)
        {
            this.navigation.Stop();
            this.Fail($"呼び鈴まで {WalkTimeout.TotalSeconds:F0} 秒で行けませんでした（残り {distance:F0}m）");
            return;
        }

        if (!this.moveIssued)
        {
            // 街の中なので飛ばない。乗り降りのほうが時間を食う。
            if (!this.navigation.BeginMove(this.walkTarget, 3f, out var failure))
            {
                if (this.navigation.Busy)
                {
                    this.Detail = "呼び鈴への経路を待っています";
                    return;
                }

                this.Fail($"呼び鈴へ向かえません（{failure}）");
                return;
            }

            this.moveIssued = true;
        }

        var status = this.navigation.Tick(this.walkTarget, 3f);

        if (status is MoveStatus.Stuck or MoveStatus.Failed)
        {
            this.navigation.Stop();
            this.Fail($"呼び鈴へ辿り着けませんでした（{status}）");
            return;
        }

        this.Detail = $"呼び鈴へ向かっています（残り {distance:F0}m）";
    }

    /// <summary>呼び鈴に話しかける。</summary>
    private void TickInteracting()
    {
        // 開いた。
        if (IsBellOpen())
        {
            this.openedUtc = DateTime.UtcNow;
            this.Step = VentureBellStep.Collecting;
            this.Detail = "ベンチャーを回収しています";
            this.stepSinceUtc = DateTime.UtcNow;
            this.trace.Decision("呼び鈴を開いた", "AutoRetainer の回収を待ちます");
            return;
        }

        if (DateTime.UtcNow - this.stepSinceUtc > InteractTimeout)
        {
            this.Fail($"呼び鈴に {InteractTimeout.TotalSeconds:F0} 秒で話しかけられませんでした");
            return;
        }

        if (!Player.Available || Player.IsCasting || Player.IsAnimationLocked)
        {
            return;
        }

        if (RetainerRestockRunner.ScanForBell() is not { } bell)
        {
            // 近くまで来たのに見えない。探し直す。
            this.Step = VentureBellStep.Searching;
            this.stepSinceUtc = DateTime.UtcNow;
            return;
        }

        var distance = Vector3.Distance(Player.Position, bell.Position);

        if (distance > InteractRange)
        {
            // 離れてしまった。近寄り直す。
            this.BeginWalk(bell.Position);
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.VentureBell", 1000))
        {
            return;
        }

        try
        {
            Svc.Targets.Target = bell;
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
                ->InteractWithObject(
                    (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bell.Address, false);
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Venture", $"呼び鈴に話しかけられませんでした: {ex.Message}");
        }
    }

    /// <summary>AutoRetainer が回収し終わるのを待つ。</summary>
    private void TickCollecting()
    {
        // 画面が閉じられた。こちらが閉じたのでなければ、終わったとみなす。
        if (!IsBellOpen())
        {
            this.trace.State("呼び鈴が閉じた", "回収が終わったとみなします");
            this.Step = VentureBellStep.Done;
            this.Detail = "回収が終わりました";
            return;
        }

        if (DateTime.UtcNow - this.stepSinceUtc > CollectTimeout)
        {
            this.anomalyLog.Warn(
                "Venture",
                $"回収が {CollectTimeout.TotalMinutes:F0} 分で終わりませんでした。画面を閉じます");

            this.BeginClosing();
            return;
        }

        var busy = this.retainer.IsBusyFailClosed();
        var state = this.retainer.CheckCollectableVenture();

        // まだ回収できるものが残っている。閉じずに待つ。
        //
        // **「分からない」で閉じない。**
        // 読めなかった一瞬のせいで回収を途中で打ち切ってしまう。
        if (state == AutoRetainerIpc.VentureState.Collectable || busy)
        {
            // **動き出したことを覚える。**
            // 一度でも動いたなら、止まった時点で終わったと判断してよい。
            // 開いた時刻からの猶予を待ち続ける必要が無くなる。
            this.retainerStarted = true;

            this.Detail = busy
                ? "ベンチャーを回収しています"
                : "まだ回収できるベンチャーがあります";

            return;
        }

        if (state == AutoRetainerIpc.VentureState.Unknown)
        {
            this.Detail = "回収が終わったか確認しています";
            return;
        }

        // 回収するものが無くなった。
        //
        // **動き出したあとなら、すぐ閉じる。**
        //
        // 以前は「開いてから 20 秒」を無条件で待っていた。
        // 猶予は「AutoRetainer がまだ動き出していないだけかもしれない」
        // ための保険なのに、動き終わったあとにも効いていたため、
        // 回収が済んでいるのに画面を開いたまま残りの秒数を待っていた
        // （2026-09-26 の報告「閉じるのが遅い」）。
        //
        // 一度でも動いたなら、止まった＝終わったと判断してよい。
        if (!this.retainerStarted && DateTime.UtcNow - this.openedUtc <= StartupGrace)
        {
            this.Detail = "AutoRetainer の開始を待っています";
            return;
        }

        this.trace.Decision(
            "回収が終わった",
            this.retainerStarted
                ? "回収できるベンチャーが無くなりました。画面を閉じます"
                : $"{StartupGrace.TotalSeconds:F0} 秒待っても AutoRetainer が動き出しませんでした");

        this.BeginClosing();
    }

    private void BeginClosing()
    {
        this.Step = VentureBellStep.Closing;
        this.Detail = "呼び鈴の画面を閉じています";
        this.stepSinceUtc = DateTime.UtcNow;

        // **閉じる前に AutoRetainer を止める。**
        // 止めずに閉じると、相手が動いたまま画面だけ消える。
        this.retainer.TryAbort();

        // **その場で 1 枚目を閉じる。**
        // 次のフレームを待つと、間引きの分だけ開いたまま残る。
        EzThrottler.Reset("AutoCollector.VentureBellClose");
        this.SendCancel();
    }

    /// <summary>画面を閉じる。</summary>
    private void TickClosing()
    {
        if (!IsBellOpen())
        {
            this.Step = VentureBellStep.Done;
            this.Detail = "回収が終わりました";
            return;
        }

        if (DateTime.UtcNow - this.stepSinceUtc > InteractTimeout)
        {
            // 閉じられないが、回収そのものは済んでいる可能性がある。
            // 周回へ戻れないほうが困るので、成功として扱い記録に残す。
            this.anomalyLog.Warn("Venture", "呼び鈴の画面を閉じられませんでした");
            this.Step = VentureBellStep.Done;
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.VentureBellClose", ActionThrottleMs))
        {
            return;
        }

        this.SendCancel();
    }

    /// <summary>キャンセル（Esc 相当）を送る。</summary>
    private void SendCancel()
    {
        try
        {
            // **手前に出ている画面から 1 枚ずつ閉じる。**
            // 一度に全部閉じようとすると、ゲーム側が次の画面を出す前に
            // 送ることになって空振りする。次のフレームで残りを閉じる。
            foreach (var name in BellAddons)
            {
                if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var unit) ||
                    !GenericHelpers.IsAddonReady(unit))
                {
                    continue;
                }

                unit->Close(true);
                return;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Venture", $"呼び鈴の画面を閉じられませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 呼び鈴の画面が開いているか。
    ///
    /// <b>フラグ 1 つで決めない。</b>
    /// <c>OccupiedSummoningBell</c> はリテイナー一覧を開いているあいだに
    /// 落ちることがある。これだけ見ていると「閉じられた」と誤判定し、
    /// 画面が開いたままなのに完了扱いになる。
    /// </summary>
    private static bool IsBellOpen()
    {
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
        {
            return true;
        }

        foreach (var name in BellAddons)
        {
            try
            {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var unit) &&
                    GenericHelpers.IsAddonReady(unit))
                {
                    return true;
                }
            }
            catch
            {
                // 読めないものは無いものとして扱う。
            }
        }

        return false;
    }

    private void Fail(string why)
    {
        this.navigation.Stop();
        this.moveIssued = false;
        this.FailureReason = why;
        this.Step = VentureBellStep.Failed;
        this.Detail = why;
        this.anomalyLog.Warn("Venture", why);
    }
}
