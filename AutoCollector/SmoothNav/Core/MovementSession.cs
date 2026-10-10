// SmoothNav（SmoothNav.Core/MovementSession.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Numerics;

namespace SmoothNav.Core;

public interface IOfficialNavigation
{
    bool IsReady { get; }
    bool IsRunning { get; }
    bool IsPathfinding { get; }
    float BuildProgress { get; }
    IReadOnlyList<Vector3> Waypoints { get; }
    Task<List<Vector3>> Pathfind(Vector3 from, Vector3 to, bool fly);
    void Move(List<Vector3> points, bool fly);
    void Stop();
}

public sealed record PlayerState(Vector3 Position, uint Territory, bool Available, bool Flying, bool Mounted, bool ManualInput);
public enum MoveState { Idle, WaitingForMesh, Searching, Following, Finished, Refining }
public sealed record SessionReport(string Id, string Reason, bool Arrived, double Seconds,
    ShapeMetrics? OfficialShape, TravelMetrics Travel, int Requests, double PathfindSeconds, double AddedSeconds,
    double? PathEndDistance, double? DestinationHorizontalDistance, double? DestinationHeightDistance, bool? Landed);

// 1件の探索だけを保持し、結果を使わなくなっても完了までは次を積まない。
// refiner があれば、公式の経路を裏のスレッドで整え、RefineBudgetSeconds 以内に終われば整えた経路で動く。
// 終わらない・失敗した・変わらなかったときは公式の経路で動く（公式より悪くしない）。
public sealed class MovementSession(IOfficialNavigation nav, Action<object> record, IPathRefiner? refiner = null)
{
    public MoveState State { get; private set; }
    public string Detail { get; private set; } = "待機中";
    public SessionReport? LastReport { get; private set; }
    public bool Active => State is MoveState.WaitingForMesh or MoveState.Searching or MoveState.Refining or MoveState.Following;
    // 整える部品。待機中だけ差し替えられる（移動中の要求には効かない）。
    public IPathRefiner? Refiner { get => refiner; set { if (!Active) refiner = value; } }
    public string Mode => refiner == null ? "official" : "smooth";
    public bool FollowingRefinedPath => State == MoveState.Following && smoothed;
    public bool FellBack => fellBack;
    public bool HasPending => pending != null;
    public IReadOnlyList<Vector3> OfficialPath => official;
    public string Id { get; private set; } = "";
    private readonly List<PositionSample> samples = [];
    private List<Vector3> official = [];
    private List<Vector3> issued = [];
    private Task<List<Vector3>>? pending;
    private Vector3 destination, requestedFrom;
    private uint territory;
    private bool fly;
    private bool inferredReplan;
    private double started, searchStarted, lastSample, pathfindSeconds, addedSeconds;
    private double? firstSearchStarted;
    private double lastBuildAdvance;
    private float lastBuildProgress;
    private int requests, retries;
    private ShapeMetrics? shape;
    private Task<CurveResult>? refineTask;
    private CancellationTokenSource? refineCancel;
    private double refineStarted, progressTime, endTime;
    private Vector3 progressAnchor, endAnchor;
    private bool smoothed, fellBack, lateRefine;
    private CurveResult? lateResult;
    // 整える処理を待つ上限。超えたら公式の経路で動き出す（素早さ：動き出しを遅らせすぎない）。
    public double RefineBudgetSeconds { get; set; } = .25;
    // 整えた経路で StuckSeconds の間に StuckDistance も進まなければ、公式の経路の残りへ切り替える（止めずに立て直す）。
    public double StuckSeconds { get; set; } = 3;
    public float StuckDistance { get; set; } = .5f;
    public double SearchTimeoutSeconds { get; set; } = 25;
    public double BuildStallTimeoutSeconds { get; set; } = 60;
    public double MoveTimeoutSeconds { get; set; } = 600;
    // 整える処理が RefineBudgetSeconds に終わらなければ公式の経路で動き出し、この時間（整え始めから）までは裏で続ける。
    // できたら、整えた経路が今の位置の近くを通る所で途中から乗り換える（動き出しを遅らせずに、残りの角を曲線にする）。0 なら取り消す。
    public double LateRefineSeconds { get; set; } = 3;
    public float SpliceTolerance { get; set; } = .5f;
    // 終点の手前（水平 EndGuardDistance 以内）で EndGuardSeconds の間 0.05m も動かなければ、到着として止める。
    // 公式の追従は、終点が壁の中だと壁に向かって走り続ける（ゲームで確認 10-09 G1）。
    public bool EndGuard { get; set; } = true;
    public float EndGuardDistance { get; set; } = 1;
    public double EndGuardSeconds { get; set; } = .6;

    public bool Begin(PlayerState player, Vector3 target, bool flying, double now)
    {
        Drain();
        if (Active || pending != null) { Detail = "前の要求が終わるまで新しい要求を受け付けません"; return false; }
        if (!player.Available || !Metrics.Finite(target)) { Detail = "現在位置または目的地を確認できません"; return false; }
        if (nav.IsRunning || nav.IsPathfinding) { Detail = "別の移動・探索が動いているため開始しません"; return false; }
        Id = Guid.NewGuid().ToString("N");
        destination = target; fly = flying; territory = player.Territory;
        started = now; lastSample = double.NegativeInfinity; pathfindSeconds = 0; addedSeconds = 0;
        firstSearchStarted = null; lastBuildAdvance = now; lastBuildProgress = nav.BuildProgress;
        requests = 0; retries = 0; official = []; issued = []; shape = null; samples.Clear();
        inferredReplan = false; smoothed = false; fellBack = false; lateRefine = false; lateResult = null;
        State = MoveState.WaitingForMesh; Detail = "地図の準備を待っています";
        record(new { type = "要求", id = Id, utc = DateTimeOffset.UtcNow, territory, from = player.Position, to = target, fly, mode = Mode });
        Sample(player, now, true);
        return true;
    }
    public void Tick(PlayerState player, double now)
    {
        if (!Active) { Drain(); return; }
        Sample(player, now, false);
        if (!player.Available || player.Territory != territory) { Finish("エリア変更・ログアウト", false, player, now, true); return; }
        if (player.ManualInput) { Finish("移動入力による手動停止", false, player, now, true, true); return; }
        if ((State == MoveState.Following && now - started >= MoveTimeoutSeconds) ||
            (State != MoveState.Following && firstSearchStarted.HasValue && now - firstSearchStarted.Value >= SearchTimeoutSeconds))
        { Finish("時間上限による終了", false, player, now, true); return; }

        if (State == MoveState.WaitingForMesh)
        {
            if (nav.IsRunning || nav.IsPathfinding) { Finish("別の移動・探索が始まりました", false, player, now, false); return; }
            if (!nav.IsReady)
            {
                var progress = nav.BuildProgress;
                if (float.IsFinite(progress) && progress > lastBuildProgress)
                { lastBuildAdvance = now; lastBuildProgress = progress; }
                if (now - lastBuildAdvance >= BuildStallTimeoutSeconds)
                { Finish("地図の準備進捗が停止したため上限で終了", false, player, now, false); return; }
                Detail = $"地図の準備待ち（進捗 {progress:F1}）"; return;
            }
            StartSearch(player, now);
            return;
        }
        if (State == MoveState.Searching)
        {
            if (pending is not { IsCompleted: true }) return;
            var task = pending; pending = null;
            pathfindSeconds += now - searchStarted;
            var error = task.Exception?.GetBaseException();
            if (task.IsCanceled || error != null || task.Result.Count == 0)
            {
                var why = task.IsCanceled ? "公式の探索が取り消された" : error?.GetType().Name ?? "公式の経路が空";
                record(new { type = "探索失敗", id = Id, reason = why, attempt = requests });
                if (retries++ < 2 && now - firstSearchStarted!.Value < SearchTimeoutSeconds) { State = MoveState.WaitingForMesh; Detail = "始点を上へずらして再試行"; return; }
                Finish(why, false, player, now, false); return;
            }
            if (nav.IsRunning) { Finish("探索中に別の移動が始まりました", false, player, now, false); return; }
            // マウント直後の位置ずれも含め、古い始点の経路は使わない。
            var foot = requestedFrom - Vector3.UnitY * (fly ? 0.2f * (retries + 1) : 0.2f * retries);
            if (Vector3.Distance(player.Position, foot) > 0.5f)
            {
                record(new { type = "結果破棄", id = Id, reason = "探索中に始点が移動", from = foot, current = player.Position });
                State = MoveState.WaitingForMesh; return;
            }
            official = [.. task.Result];
            shape = Metrics.Shape(official);
            shape = shape with { LengthRatio = shape.Length > 0 ? 1 : null };
            if (refiner != null && official.Count > 1)
            {
                // 整える処理は裏のスレッドで。窓口の照会（点の照会・ゲームの当たり判定の待ち行列）は窓口の側で扱う。
                refineCancel = new(); var cancel = refineCancel.Token; var path = official.ToArray(); var flying = fly; var worker = refiner;
                refineTask = Task.Run(() => worker.Refine(path, flying, cancel));
                refineStarted = now; State = MoveState.Refining; Detail = "経路を整えています";
                return;
            }
            Issue(official, null, "公式経路を採用（先頭の出発点を除去・末尾に0.01m以内の所有目印）", player, now);
            return;
        }
        if (State == MoveState.Refining)
        {
            if (nav.IsRunning || nav.IsPathfinding) { Finish("整えている間に別の移動・探索が始まりました", false, player, now, false); return; }
            if (refineTask is { IsCompleted: true } done)
            {
                refineTask = null;
                addedSeconds += now - refineStarted;
                var result = done.Status == TaskStatus.RanToCompletion ? done.Result : null;
                if (result != null && Usable(result)) { Issue(result.UsedPath, result, "整えた経路を採用", player, now, true); return; }
                var why = result == null ? $"整える処理が失敗したため公式経路（{done.Exception?.GetBaseException().GetType().Name ?? "取り消し"}）" : "整えても変わらないため公式経路";
                Issue(official, result, why, player, now);
                return;
            }
            if (now - refineStarted >= RefineBudgetSeconds)
            {
                addedSeconds += now - refineStarted;
                if (LateRefineSeconds > RefineBudgetSeconds)
                {
                    // 取り消さずに裏で続け、公式の経路で動き出す。できたら Following で途中から乗り換える。
                    lateRefine = true;
                    Issue(official, null, "整える処理が時間内に終わらないため公式経路で動き出す（できたら途中から乗り換える）", player, now);
                    return;
                }
                refineCancel?.Cancel(); ObserveLate(refineTask); refineTask = null;
                Issue(official, null, "整える処理が時間内に終わらないため公式経路", player, now);
                return;
            }
            return;
        }
        if (State == MoveState.Following)
        {
            if (lateRefine) LateRefine(player, now);
            if (!nav.IsRunning)
            {
                if (nav.IsPathfinding) { Detail = "公式の探し直し待ち（推定）"; return; }
                var arrived = Vector3.Distance(player.Position, destination) <= 1;
                // 整えた経路の追従が経路の終わりの手前で止まったら、公式の経路の残りで動き続ける（公式より悪くしない）。
                if (!arrived && smoothed && !fellBack && Vector3.Distance(player.Position, official[^1]) > 1) { FallBack(player, now, "整えた経路の追従が終わりの手前で止まったため公式の経路へ戻した"); return; }
                Finish(arrived ? "到着" : "経路の終了（目的地に未到達）", arrived, player, now, false);
                return;
            }
            // 終点の手前で進まない（壁に向かって走り続ける）なら、到着として止める。
            if (EndGuard)
            {
                var flat = Vector2.Distance(new(player.Position.X, player.Position.Z), new(destination.X, destination.Z));
                if (flat > EndGuardDistance || MathF.Abs(player.Position.Y - destination.Y) > 2 || Vector3.Distance(player.Position, endAnchor) > .05f)
                { endAnchor = player.Position; endTime = now; }
                else if (now - endTime >= EndGuardSeconds) { Finish("終点の手前で進まないため到着として停止", true, player, now, true, true); return; }
            }
            if (smoothed && !fellBack && OwnsCurrentPath())
            {
                if (Vector3.Distance(player.Position, progressAnchor) >= StuckDistance) { progressAnchor = player.Position; progressTime = now; }
                else if (now - progressTime >= StuckSeconds) { FallBack(player, now, "整えた経路で進まないため公式の経路へ戻した"); return; }
            }
            if (!OwnsCurrentPath())
            {
                if (!SameDestination()) { Finish("経路が外部で変更されたため管理を解除", false, player, now, false); return; }
                if (!inferredReplan) record(new { type = "経路差し替え", id = Id, reason = "公式の探し直し（推定・同じ目的地の外部投入とは識別不能）" });
                inferredReplan = true; Detail = "公式の探し直し（推定）。明示停止だけを適用します";
            }
            // 公式の追従が終わるまで早期到着で切らない。基準の終点を変えない。
        }
    }
    // 整えた経路として使えるか（端点が公式と同じ・どこかを変えた・有限）。RoutePlanner も使う。
    internal static bool Usable(CurveResult result) =>
        result.UsedPath.Length > 1 && result.Decisions.Any(d => d.Changed) && result.UsedPath.All(Metrics.Finite)
        && result.OfficialPath.Length > 1 && result.UsedPath[0] == result.OfficialPath[0] && result.UsedPath[^1] == result.OfficialPath[^1];
    // 先頭の出発点を除き、末尾に所有の目印を足して公式の追従へ渡す。
    private void Issue(IReadOnlyList<Vector3> path, CurveResult? refined, string reason, PlayerState player, double now, bool refinedPath = false)
    {
        var timer = Stopwatch.StartNew();
        smoothed = refinedPath;
        // 非空の1点経路は保持する。始点と目的地が同じ場合の空化を避ける。
        issued = path.Count > 1 ? path.Skip(1).ToList() : [.. path];
        issued.Add(PathMarker.For(path[^1], Id));
        // 他の経路が無いことを確認済み。公式の設定には触れない。
        nav.Move([.. issued], fly);
        addedSeconds += timer.Elapsed.TotalSeconds;
        State = MoveState.Following; Detail = smoothed ? "整えた経路を移動しています" : "公式の経路を移動しています";
        progressAnchor = player.Position; progressTime = now; endAnchor = player.Position; endTime = now;
        // 記録は別の処理で後から文字にする。後で一覧を差し替えても記録が変わらないよう、写しを渡す。
        record(new { type = "経路", id = Id, officialPath = official.ToArray(), smoothPath = refined?.UsedPath.ToArray(), usedPath = issued.ToArray(),
            reason, shape, pathfindSeconds, addedSeconds, smoothed, refineSeconds = refined?.AddedSeconds,
            changedIntervals = refined?.Decisions.Count(d => d.Changed), queries = refined?.Queries,
            decisions = refined?.Decisions.Select(d => new { d.OriginalStart, d.OriginalEnd, d.Changed, d.Reason }).ToArray() });
    }
    // 遅れて終わった整える処理を受け取り、今の位置の近くを整えた経路が通るようになったら、そこから先を整えた経路へ乗り換える。
    private void LateRefine(PlayerState player, double now)
    {
        if (refineTask != null)
        {
            if (refineTask.IsCompleted)
            {
                var done = refineTask; refineTask = null;
                var result = done.Status == TaskStatus.RanToCompletion ? done.Result : null;
                if (result != null && Usable(result)) lateResult = result;
                else { lateRefine = false; record(new { type = "遅れた整え", id = Id, outcome = result == null ? "失敗" : "変わらない", seconds = now - refineStarted }); return; }
            }
            else if (now - refineStarted >= LateRefineSeconds)
            {
                refineCancel?.Cancel(); ObserveLate(refineTask); refineTask = null; lateRefine = false;
                record(new { type = "遅れた整え", id = Id, outcome = "上限で取り消し", seconds = now - refineStarted });
                return;
            }
        }
        if (lateResult == null || smoothed || fellBack || !OwnsCurrentPath()) return;
        var index = SpliceIndex(official, lateResult.UsedPath, player.Position, fly, SpliceTolerance);
        if (index < 0) return;
        var rest = new List<Vector3> { player.Position };
        rest.AddRange(lateResult.UsedPath.Skip(index));
        var refined = lateResult; lateResult = null; lateRefine = false;
        Issue(rest, refined, $"整えた経路へ途中から乗り換え（整え始めから{now - refineStarted:F2}秒）", player, now, true);
    }
    // 公式の経路を進んでいる位置 p から、整えた経路へ乗り換える点の番号（その点から先を渡す）。乗り換えられなければ -1。
    // 公式の経路の上で進んだ長さの前後（6m か 15% の大きい方）にある整えた経路の線のうち、p に一番近い線を選び、
    // tolerance 以内ならその線の終わりの点（0.3m より近ければ次の点）。地上は水平の距離で測る（公式の追従は地上で高さを見ない）。
    public static int SpliceIndex(IReadOnlyList<Vector3> official, IReadOnlyList<Vector3> refined, Vector3 p, bool fly, float tolerance = .5f)
    {
        if (official.Count < 2 || refined.Count < 2) return -1;
        Vector3 Flat(Vector3 v) => fly ? v : v with { Y = 0 };
        var progress = 0f;
        {
            float best = float.MaxValue, walked = 0;
            for (var i = 0; i + 1 < official.Count; i++)
            {
                var a = Flat(official[i]); var ab = Flat(official[i + 1]) - a; var length = ab.Length();
                var t = length < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(Flat(p) - a, ab) / (length * length), 0, 1);
                var d = Vector3.Distance(Flat(p), a + ab * t);
                if (d < best) { best = d; progress = walked + length * t; }
                walked += length;
            }
        }
        var window = MathF.Max(6, progress * .15f);
        float bestDistance = float.MaxValue, cumulative = 0; var bestIndex = -1;
        for (var i = 0; i + 1 < refined.Count; i++)
        {
            var a = Flat(refined[i]); var ab = Flat(refined[i + 1]) - a; var length = ab.Length();
            if (cumulative + length >= progress - window && cumulative <= progress + window)
            {
                var t = length < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(Flat(p) - a, ab) / (length * length), 0, 1);
                var d = Vector3.Distance(Flat(p), a + ab * t);
                if (d < bestDistance) { bestDistance = d; bestIndex = i + 1; }
            }
            cumulative += length;
        }
        if (bestIndex < 0 || bestDistance > tolerance) return -1;
        if (bestIndex + 1 < refined.Count && Vector3.Distance(Flat(refined[bestIndex]), Flat(p)) < .3f) bestIndex++;
        return bestIndex;
    }
    // 公式の経路で、今の位置に最も近い区間の終わりから先を渡す。1回だけ。
    private void FallBack(PlayerState player, double now, string reason)
    {
        fellBack = true;
        var best = 0; var bestDistance = float.MaxValue;
        for (var i = 0; i + 1 < official.Count; i++)
        {
            var d = FollowerSimulation.DistanceToSegment(player.Position, official[i], official[i + 1]);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        var rest = new List<Vector3> { player.Position };
        rest.AddRange(official.Skip(best + 1));
        record(new { type = "立て直し", id = Id, reason, from = player.Position, officialIndex = best + 1 });
        Issue(rest, null, reason, player, now);
    }
    private static void ObserveLate(Task? task) => task?.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private void StartSearch(PlayerState player, double now)
    {
        requestedFrom = player.Position + Vector3.UnitY * (fly ? 0.2f * (retries + 1) : 0.2f * retries);
        searchStarted = now; requests++;
        firstSearchStarted ??= now;
        pending = nav.Pathfind(requestedFrom, destination, fly);
        State = MoveState.Searching; Detail = $"公式の経路を探しています（{requests}回目）";
        record(new { type = "探索", id = Id, from = requestedFrom, to = destination, fly, attempt = requests, seconds = now - started });
    }
    public void Stop(PlayerState player, double now, string reason = "利用者の停止", bool userRequested = true)
    {
        if (Active) Finish(reason, false, player, now, true, userRequested);
    }
    private void Finish(string reason, bool arrived, PlayerState player, double now, bool stopOwned, bool explicitStop = false)
    {
        if (stopOwned && State == MoveState.Following && (OwnsCurrentPath() || (explicitStop && SameDestination()))) nav.Stop();
        if (refineTask != null) { refineCancel?.Cancel(); ObserveLate(refineTask); refineTask = null; }
        // Cancelは呼ばず、未完了Taskは保持して後で結果と例外を読み捨てる。
        Sample(player, now, true);
        State = MoveState.Finished; Detail = reason;
        LastReport = new(Id, reason, arrived, now - started, shape,
            TravelMeasurement.Calculate(samples, arrived ? now - started : null), requests, pathfindSeconds, addedSeconds,
            player.Available && player.Territory == territory && official.Count > 0 ? Vector3.Distance(player.Position, official[^1]) : null,
            player.Available && player.Territory == territory ? Vector2.Distance(new(player.Position.X, player.Position.Z), new(destination.X, destination.Z)) : null,
            player.Available && player.Territory == territory ? Math.Abs(player.Position.Y - destination.Y) : null,
            player.Available && player.Territory == territory ? !player.Flying : null);
        record(new { type = "結果", id = Id, report = LastReport });
    }
    public bool OwnsCurrentPath()
    {
        if (issued.Count == 0 || !nav.IsRunning) return false;
        return IsSuffix(issued, nav.Waypoints);
    }
    private bool SameDestination()
    {
        var current = nav.Waypoints;
        return official.Count > 0 && current.Count > 0 && Vector3.Distance(current[^1], official[^1]) <= .01f;
    }
    public static bool IsSuffix(IReadOnlyList<Vector3> mine, IReadOnlyList<Vector3> current)
    {
        if (current.Count == 0 || current.Count > mine.Count) return false;
        for (var i = 0; i < current.Count; i++) if (current[i] != mine[mine.Count - current.Count + i]) return false;
        return true;
    }
    private void Sample(PlayerState player, double now, bool force)
    {
        if (!player.Available || player.Territory != territory || now <= lastSample || (!force && now - lastSample < 0.2 - 1e-6)) return;
        var sample = new PositionSample(now - started, player.Position, player.Flying, player.Mounted);
        samples.Add(sample); lastSample = now;
        record(new { type = "位置", id = Id, sample });
    }
    private void Drain()
    {
        if (!Active && pending is { IsCompleted: true })
        {
            _ = pending.Exception;
            record(new { type = "結果破棄", id = Id, reason = "終了した要求の遅い結果" });
            pending = null;
        }
    }
    public void ObservePendingOnDispose()
    {
        if (refineTask != null) { refineCancel?.Cancel(); ObserveLate(refineTask); refineTask = null; }
        if (pending != null) _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
