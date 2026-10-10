// NavigationService（本物）と SmoothNav の写し（本物）を、偽物の vnavmesh・ゲームの上で試す。ゲームの操作はしない。
// 実行：python -X utf8 tools/regression/navigation_smooth.py
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;
using ECommons.GameHelpers;
using SmoothNav.Core;

static class Program
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }

    static readonly Vector3 Start = Vector3.Zero, Goal = new(20, 0, 20);
    static readonly List<Vector3> Official = [Start, new(20, 0, 0), Goal];
    static readonly Vector3[] Smooth = [Start, new(14, 0, 0), new(18, 0, 2), new(20, 0, 6), Goal];
    static CurveResult Changed() => new([.. Official], Smooth, Smooth, [new(0, 2, 0, 4, true, "試験の加工")], [], .001, 10);

    static (NavigationService Nav, VnavmeshIpc Vnav, SmoothMoveService Smooth, AnomalyLog Log) Make(Func<CancellationToken, CurveResult>? refine = null)
    {
        Player.Available = true; Player.Position = Start;
        var vnav = new VnavmeshIpc(); var log = new AnomalyLog();
        var smooth = new SmoothMoveService { Refiner = new FakeRefiner(refine ?? (_ => Changed())) };

        // BMR は未導入あつかい。移動権の受け渡しは、この試験の見るところではない。
        return (new NavigationService(log, vnav, new BossModIpc(), smooth), vnav, smooth, log);
    }
    static void TickUntil(NavigationService nav, Func<bool> done, string why)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) { nav.Tick(Goal, 3); Thread.Sleep(2); }
        Check(done(), why);
    }

    public static void Main()
    {
        {
            var (nav, vnav, smooth, log) = Make();
            Check(nav.BeginMove(Goal, 3, false, out _) && vnav.MoveCloseToCalls == 0 && vnav.LastSearch != null, "曲線が有効なら vnavmesh 任せにせず自分で経路を探す");
            Check(Enumerable.Range(0, 5).All(_ => nav.Tick(Goal, 3) == MoveStatus.Moving) && vnav.MoveAlongCalls.Count == 0,
                "探索の間は何回見張っても移動中（届かない・止まったと数えない）");
            vnav.LastSearch!.SetResult([.. Official]);
            TickUntil(nav, () => vnav.MoveAlongCalls.Count == 1, "整った経路を辿らせる");
            var path = vnav.MoveAlongCalls[0];
            Check(path[0] == Smooth[1] && MathF.Abs(Vector3.Distance(path[^1], Goal) - 3) < .01f, "整えた経路を range の手前で切って渡す（先頭の出発点は除く）");
            Check(log.Lines.Any(l => l.Contains("曲線に整えた経路")), "どう整えたかを記録に残す");
            vnav.Running = false; Player.Position = Goal + new Vector3(0, 0, -2.9f);
            var status = MoveStatus.Moving;
            for (var i = 0; i < 3; i++) status = nav.Tick(Goal, 3);
            Check(status == MoveStatus.Arrived, "辿り終えたら今までどおり到着を判定する");
        }
        {
            var (nav, vnav, _, log) = Make();
            vnav.AcceptMoveCloseTo = false;
            nav.BeginMove(Goal, 3, true, out _);
            vnav.LastSearch!.SetResult([]);
            Check(nav.Tick(Goal, 3) == MoveStatus.Moving && vnav.MoveCloseToCalls == 1 && vnav.MoveAlongCalls.Count == 0, "経路が空なら vnavmesh 任せへ戻す");
            Check(nav.Tick(Goal, 3) == MoveStatus.Moving && vnav.MoveCloseToCalls == 2, "断られたら受け取られるまで頼み直す");
            vnav.AcceptMoveCloseTo = true;
            nav.Tick(Goal, 3);
            Check(vnav.MoveCloseToCalls == 3 && vnav.LastFly, "受け取られたら頼み直しをやめる（飛ぶかは最初の頼みのまま）");
            nav.Tick(Goal, 3);
            Check(vnav.MoveCloseToCalls == 3, "受け取られた後は頼み直さない");
            Check(log.Lines.Any(l => l.Contains("vnavmesh 任せへ戻す")), "戻したことを記録に残す");
        }
        {
            var (nav, vnav, smooth, _) = Make();
            smooth.Enabled = false;
            Check(nav.BeginMove(Goal, 3, false, out _) && vnav.MoveCloseToCalls == 1 && vnav.LastSearch == null, "設定で切ってあれば今までどおり vnavmesh 任せ");
            var (nav2, vnav2, _, _) = Make();
            vnav2.PathfindWorks = false;
            Check(nav2.BeginMove(Goal, 3, false, out _) && vnav2.MoveCloseToCalls == 1, "探索を頼めなければ vnavmesh 任せ");
        }
        {
            var (nav, vnav, _, _) = Make(cancel => { cancel.WaitHandle.WaitOne(5000); return Changed(); });
            nav.BeginMove(Goal, 3, false, out _);
            var token = vnav.LastToken;
            nav.Stop();
            Check(token.IsCancellationRequested && vnav.StopCalls == 1, "止めたら自分で頼んだ探索を取り消す");
            vnav.LastSearch!.SetResult([.. Official]);
            for (var i = 0; i < 20; i++) { nav.Tick(Goal, 3); Thread.Sleep(2); }
            Check(vnav.MoveAlongCalls.Count == 0, "止めた後に経路を辿らせない");
        }
        {
            using var gate = new ManualResetEventSlim();
            var (nav, vnav, smooth, _) = Make(cancel => { WaitHandle.WaitAny([gate.WaitHandle, cancel.WaitHandle], 5000); return Changed(); });
            nav.BeginMove(Goal, 0, false, out _);
            vnav.LastSearch!.SetResult([.. Official]);
            Check(Enumerable.Range(0, 5).All(_ => nav.Tick(Goal, 0) == MoveStatus.Moving) && vnav.MoveAlongCalls.Count == 0,
                "整える間（予算の中）は何回見張っても移動中");
            smooth.Clock = 1; nav.Tick(Goal, 0);
            Check(vnav.MoveAlongCalls.Count == 1 && vnav.MoveAlongCalls[0].SequenceEqual(Official.Skip(1)), "整える処理が間に合わなければ公式の経路で先に動かす");
            gate.Set();
            vnav.Running = false; Player.Position = new(20, 0, 8);
            for (var i = 0; i < 40; i++) { smooth.Clock = 1.1 + i * .01; nav.Tick(Goal, 0); Thread.Sleep(2); }
            Check(vnav.MoveAlongCalls.Count == 1, "辿り終えた後は途中からの乗り換えを足さない");
        }
        {
            using var gate = new ManualResetEventSlim();
            var (nav, vnav, smooth, _) = Make(cancel => { WaitHandle.WaitAny([gate.WaitHandle, cancel.WaitHandle], 5000); return Changed(); });
            nav.BeginMove(Goal, 0, false, out _);
            vnav.LastSearch!.SetResult([.. Official]);
            nav.Tick(Goal, 0); smooth.Clock = 1; nav.Tick(Goal, 0);
            gate.Set(); Player.Position = new(20, 0, 8);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (vnav.MoveAlongCalls.Count < 2 && DateTime.UtcNow < until) { smooth.Clock += .01; nav.Tick(Goal, 0); Thread.Sleep(2); }
            Check(vnav.MoveAlongCalls.Count == 2 && vnav.MoveAlongCalls[1].SequenceEqual([Goal]), "辿っている間に整ったら途中から乗り換える");
        }
        {
            var (nav, vnav, _, _) = Make();
            nav.BeginMove(Goal, 3, false, out _);
            vnav.LastSearch!.SetResult([.. Official]);
            TickUntil(nav, () => vnav.MoveAlongCalls.Count == 1, "1 回目の経路");
            var firstToken = vnav.LastToken;
            Check(nav.Reissue(new Vector3(25, 0, 25), 3, out _) && vnav.StopCalls == 0 && vnav.Running, "頼み直しは今の経路を止めない（整った経路で差し替える）");
            Check(vnav.LastToken != firstToken && nav.Tick(new Vector3(25, 0, 25), 3) == MoveStatus.Moving, "頼み直しの探索の間も移動中");
        }
        {
            // 経路の終点で止まったのに目的地まで遠い：呼び出し側は引き直すまで毎フレーム見張る。記録は 1 回だけ（10-10 に 45 件並んだ）。
            var (nav, vnav, smooth, log) = Make();
            smooth.Enabled = false;
            nav.BeginMove(Goal, 3, false, out _);
            vnav.Running = false; Player.Position = Start;
            var statuses = Enumerable.Range(0, 20).Select(_ => nav.Tick(Goal, 3)).ToList();
            Check(statuses[^1] == MoveStatus.ShortOfTarget, "経路の終点で止まって遠ければ届かないと返す");
            Check(log.Lines.Count(l => l.Contains("ヤルム残っています")) == 1, "届かないの記録は 1 回の移動につき 1 回だけ");
            nav.BeginMove(Goal, 3, false, out _);
            vnav.Running = false;
            for (var i = 0; i < 5; i++) nav.Tick(Goal, 3);
            Check(log.Lines.Count(l => l.Contains("ヤルム残っています")) == 2, "頼み直したら、また 1 回だけ記録する");
        }
        Console.WriteLine($"回帰試験（移動・曲線）{count} 件 合格");
    }
}

sealed class FakeRefiner(Func<CancellationToken, CurveResult> body) : IPathRefiner
{
    public CurveResult Refine(IReadOnlyList<Vector3> official, bool fly, CancellationToken cancel) => body(cancel);
}

namespace AutoCollector.Automation
{
    // 詰まったときに跳ぶ処理（rio-pc 側で足した分）。試験では跳べないことにする。
    public static class MountService
    {
        public static bool TryJumpOnGround() => false;
    }

    // 本物は Dalamud の毎フレームの更新と当たり判定を使うので、試験では時計と整える部品だけを持つ偽物にする。
    public sealed class SmoothMoveService
    {
        public bool Enabled = true;
        public double Clock;
        public IPathRefiner? Refiner;
        public double Now => this.Clock;
        public RoutePlanner CreatePlanner() => new(this.Refiner);
        public static string Describe(RoutePlan plan) =>
            (plan.Fallback ? "vnavmesh 任せへ戻す" : plan.Splice ? "途中から曲線へ乗り換え" : plan.Smoothed ? "曲線に整えた経路" : "公式の経路") + "：" + plan.Reason;
    }
}

namespace AutoCollector.Diagnostics
{
    public sealed class AnomalyLog
    {
        public readonly List<string> Lines = [];
        public void Info(string category, string message) => this.Lines.Add(message);
        public void Warn(string category, string message) => this.Lines.Add(message);
    }
}

namespace AutoCollector.Ipc
{
    public sealed class VnavmeshIpc
    {
        public bool IsLoaded = true, Ready = true, Running, NavPathfinding, SimplePathfinding, AcceptMoveCloseTo = true, PathfindWorks = true, LastFly;
        public int MoveCloseToCalls, StopCalls;
        public readonly List<List<Vector3>> MoveAlongCalls = [];
        public TaskCompletionSource<List<Vector3>>? LastSearch;
        public CancellationToken LastToken;
        public bool TryIsReady(out bool ready) { ready = this.Ready; return true; }
        public bool TryMoveCloseTo(Vector3 destination, bool fly, float range, out bool accepted)
        {
            this.MoveCloseToCalls++; this.LastFly = fly; accepted = this.AcceptMoveCloseTo;
            if (accepted) this.Running = true;
            return true;
        }
        public bool TryPathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken cancel, out Task<List<Vector3>>? waypoints)
        {
            if (!this.PathfindWorks) { waypoints = null; return false; }
            this.LastSearch = new(TaskCreationOptions.RunContinuationsAsynchronously); this.LastToken = cancel;
            waypoints = this.LastSearch.Task; return true;
        }
        public bool TryMoveAlong(List<Vector3> waypoints, bool fly) { this.MoveAlongCalls.Add(waypoints); this.Running = true; return true; }
        public bool TryPathIsRunning(out bool running) { running = this.Running; return true; }
        public bool TryNavPathfindInProgress(out bool inProgress) { inProgress = this.NavPathfinding; return true; }
        public bool TrySimpleMovePathfindInProgress(out bool inProgress) { inProgress = this.SimplePathfinding; return true; }
        public bool TryStop() { this.StopCalls++; this.Running = false; return true; }
        public bool TryNearestPoint(Vector3 position, float halfExtentXZ, float halfExtentY, out Vector3? nearest) { nearest = null; return false; }
        public bool TryPointOnFloor(Vector3 position, out Vector3? onFloor) { onFloor = null; return false; }

        // 目的地をメッシュへ乗せ直す／追従の許容値／立ち位置を探す（rio-pc 側で足した分）。
        // 既定では「乗せられない」を返し、今までの試験の筋道を変えない。
        public bool TryNearestPointReachable(Vector3 position, float halfExtentXZ, float halfExtentY, out Vector3? nearest) { nearest = null; return false; }
        public bool TrySetPathTolerance(float meters) => true;
        public bool TryIsPointOnMesh(Vector3 position, float halfExtentY, bool allowUnreachable, out bool onMesh) { onMesh = false; return true; }
    }

    // BMR との移動権の受け渡し（rio-pc 側で足した分）。試験では呼ばれた回数だけ覚える。
    public sealed class BossModIpc
    {
        public bool IsLoaded;
        public readonly List<bool> PauseCalls = [];
        public bool TryPauseMovement(bool pause) { this.PauseCalls.Add(pause); return true; }
    }
}

namespace ECommons.DalamudServices
{
    public static class Svc
    {
        public static readonly FakeClientState ClientState = new();

        // 詰まりの手当てで「自分の都合で止まっている状態か」を見る（rio-pc 側で足した分）。
        public static readonly FakeCondition Condition = new();
    }

    public sealed class FakeClientState { public uint TerritoryType = 1; }

    public sealed class FakeCondition
    {
        private readonly System.Collections.Generic.HashSet<Dalamud.Game.ClientState.Conditions.ConditionFlag> set = [];
        public bool this[Dalamud.Game.ClientState.Conditions.ConditionFlag flag]
        {
            get => this.set.Contains(flag);
            set { if (value) this.set.Add(flag); else this.set.Remove(flag); }
        }
    }
}

namespace Dalamud.Game.ClientState.Conditions
{
    // 本物の列挙のうち、移動の判定で使うものだけ。
    public enum ConditionFlag
    {
        Casting, BetweenAreas, BetweenAreas51, WatchingCutscene, WatchingCutscene78,
        OccupiedInCutSceneEvent, Mounting, Mounting71, Jumping, Jumping61, Unconscious,
    }
}

namespace ECommons.GameHelpers
{
    public static class Player { public static bool Available = true; public static Vector3 Position; }
}
