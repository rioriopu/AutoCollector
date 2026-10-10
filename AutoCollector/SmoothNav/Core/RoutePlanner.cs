// SmoothNav（SmoothNav.Core/RoutePlanner.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

// 渡す経路。Waypoints は先頭の出発点を除いた点の並び（公式の追従 Path.MoveTo へそのまま渡せる）。空なら動かさなくてよい（既に range の内側）。
// Fallback：公式の経路が得られない（空・例外・時間切れ）。呼び出し側は今までの頼み方（vnavmesh 任せ）に戻す。
// Smoothed：整えた経路。Splice：公式の経路で動き出した後の、途中からの乗り換え。
public sealed record RoutePlan(IReadOnlyList<Vector3> Waypoints, bool Smoothed, bool Splice, bool Fallback, string Reason, CurveResult? Refined, double Seconds);

// 自分で移動を見張る組み込み先（到着・止まった・やり直しを自分で判定するプラグイン）向けの、経路だけを整える部品。
// 公式に頼んだ経路（Begin に渡す探索）を、予算内に整えば整えた経路で、間に合わなければ公式の経路を先に渡し、
// 整え終わったら今の位置の近くから乗り換える経路を渡す。整えられない・変わらないときは公式の経路（公式より悪くしない）。
// range>0 なら、公式の追従の DestinationTolerance と同じく、終点から range 以内に入る所で経路を切る（FollowPath.cs:73）。
// Begin・Tick・Cancel は同じスレッド（画面のスレッド）から呼ぶ。整える処理は裏のスレッドで動く。
public sealed class RoutePlanner(IPathRefiner? refiner)
{
    public double RefineBudgetSeconds { get; set; } = .25;
    public double LateRefineSeconds { get; set; } = 3;
    // 遅れて整った経路へ乗り換える機会を待つ上限（整え始めから）。公式の経路の角の中にいる間は乗り換えないので、その分を待つ。
    public double SpliceWaitSeconds { get; set; } = 15;
    public double SearchTimeoutSeconds { get; set; } = 25;
    public float SpliceTolerance { get; set; } = .5f;
    public IPathRefiner? Refiner { get => refiner; set => refiner = value; }
    // 最初の経路を渡すまでの間（探索中・整える予算の中）。呼び出し側はこの間を「移動中」として扱う。
    public bool Pending => active && !issued;
    // 遅れて整う経路を待っている間も含めて、まだ経路を渡すことがあるか。
    public bool Active => active;

    private Task<List<Vector3>>? search;
    private Task<CurveResult>? refineTask;
    private CancellationTokenSource? cancel;
    private List<Vector3> official = [];
    private CurveResult? late;
    private float range;
    private bool fly, active, issued;
    private double started, refineStarted;

    public void Begin(Task<List<Vector3>> officialSearch, bool flying, float destinationRange, double now)
    {
        Cancel();
        search = officialSearch; fly = flying; range = destinationRange;
        started = now; active = true; issued = false; late = null; official = [];
    }
    // 毎回（画面のスレッドで）呼ぶ。渡す経路があれば返す。position は今の足元。
    public RoutePlan? Tick(Vector3 position, double now)
    {
        if (!active) return null;
        if (search != null)
        {
            if (!search.IsCompleted)
            {
                if (now - started < SearchTimeoutSeconds) return null;
                Observe(search); search = null;
                return Fail("公式の探索が時間内に終わらない", now);
            }
            var done = search; search = null;
            if (done.Status != TaskStatus.RanToCompletion || done.Result.Count == 0)
                return Fail(done.IsFaulted ? "公式の探索の例外: " + done.Exception!.GetBaseException().GetType().Name
                    : done.IsCanceled ? "公式の探索が取り消された" : "公式の経路が空", now);
            official = done.Result;
            if (refiner == null || official.Count < 2) return Final(official, null, false, false, "公式経路を採用", now);
            cancel = new(); var token = cancel.Token; var path = official.ToArray(); var flying = fly; var worker = refiner;
            refineTask = Task.Run(() => worker.Refine(path, flying, token));
            refineStarted = now;
            return null;
        }
        if (refineTask != null)
        {
            if (refineTask.IsCompleted)
            {
                var finished = refineTask; refineTask = null;
                var result = finished.Status == TaskStatus.RanToCompletion ? finished.Result : null;
                var usable = result != null && MovementSession.Usable(result);
                if (!issued)
                    return usable ? Final(result!.UsedPath, result, true, false, "整えた経路を採用", now)
                        : Final(official, result, false, false, result == null ? "整える処理が失敗したため公式経路" : "整えても変わらないため公式経路", now);
                if (!usable) { active = false; return null; }
                late = result;
            }
            else if (!issued && now - refineStarted >= RefineBudgetSeconds)
            {
                if (LateRefineSeconds <= RefineBudgetSeconds)
                {
                    cancel?.Cancel(); Observe(refineTask); refineTask = null;
                    return Final(official, null, false, false, "整える処理が時間内に終わらないため公式経路", now);
                }
                // 取り消さずに裏で続け、公式の経路を先に渡す。整ったら途中から乗り換える経路を渡す。
                issued = true;
                return Plan(official, null, false, false, "整える処理が時間内に終わらないため公式経路で動き出す（できたら途中から乗り換える）", now);
            }
            else if (issued && now - refineStarted >= LateRefineSeconds)
            {
                cancel?.Cancel(); Observe(refineTask); refineTask = null; active = false;
                return null;
            }
            if (refineTask != null) return null;
        }
        if (late != null)
        {
            if (now - refineStarted >= SpliceWaitSeconds) { late = null; active = false; return null; }
            var index = MovementSession.SpliceIndex(official, late.UsedPath, position, fly, SpliceTolerance);
            if (index < 0) return null;
            var rest = new List<Vector3> { position };
            rest.AddRange(late.UsedPath.Skip(index));
            var refined = late; late = null;
            return Final(rest, refined, true, true, $"整えた経路へ途中から乗り換え（整え始めから{now - refineStarted:F2}秒）", now);
        }
        return null;
    }
    // 自分の移動をやめた・目的地を変えたときに呼ぶ。整える処理を取り消し、以後は何も渡さない。
    public void Cancel()
    {
        if (refineTask != null) { cancel?.Cancel(); Observe(refineTask); refineTask = null; }
        if (search != null) { Observe(search); search = null; }
        late = null; active = false;
    }
    // 経路を、終点（最後の点）から range 以内に入る所で切る（公式の追従の DestinationTolerance と同じ見方＝立体の距離）。
    // 出発点が既に range の内側なら、出発点だけを返す。
    public static List<Vector3> Trim(IReadOnlyList<Vector3> path, float range)
    {
        if (path.Count == 0 || range <= 0) return [.. path];
        var center = path[^1];
        if (Vector3.Distance(path[0], center) <= range) return [path[0]];
        var result = new List<Vector3> { path[0] };
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1]; var b = path[i];
            if (Vector3.Distance(b, center) > range) { result.Add(b); continue; }
            // a は外、b は内：|a + t(b - a) - center| = range となる t（0..1）で切る。
            var d = b - a; var f = a - center;
            var qa = Vector3.Dot(d, d); var qb = 2 * Vector3.Dot(f, d); var qc = Vector3.Dot(f, f) - range * range;
            var disc = MathF.Max(0, qb * qb - 4 * qa * qc);
            var t = qa < 1e-9f ? 1 : Math.Clamp((-qb - MathF.Sqrt(disc)) / (2 * qa), 0, 1);
            result.Add(a + d * t);
            return result;
        }
        return result;
    }
    private RoutePlan Final(IReadOnlyList<Vector3> path, CurveResult? refined, bool smoothed, bool splice, string reason, double now)
    {
        active = false;
        return Plan(path, refined, smoothed, splice, reason, now);
    }
    private RoutePlan Plan(IReadOnlyList<Vector3> path, CurveResult? refined, bool smoothed, bool splice, string reason, double now)
    {
        var trimmed = Trim(path, range);
        return new(trimmed.Skip(1).ToList(), smoothed, splice, false, reason, refined, now - started);
    }
    private RoutePlan Fail(string reason, double now)
    {
        active = false;
        return new([], false, false, true, reason, null, now - started);
    }
    private static void Observe(Task task) => task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
