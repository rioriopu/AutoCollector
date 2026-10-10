// SmoothNav（SmoothNav.Core/FrameworkQueue.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace SmoothNav.Core;

// ゲームの当たり判定のように画面のスレッドでしか呼べない照会を、裏のスレッド（整える処理）から頼んで待つ。
// 画面のスレッドは毎フレーム Pump で、予算の時間だけ処理する。待ちきれない・取り消されたら呼び出し側は Unknown として扱う。
public sealed class FrameworkQueue
{
    private sealed class Work(Func<object?> body)
    {
        public readonly Func<object?> Body = body;
        public readonly ManualResetEventSlim Done = new();
        public object? Result;
        public Exception? Error;
        public volatile bool Abandoned;
    }
    private readonly ConcurrentQueue<Work> queue = new();
    public int Pending => queue.Count;

    // 裏のスレッドから呼ぶ。wait の間に処理されなければ false（頼んだ仕事は処理されずに捨てられる）。
    public bool TryRun<T>(Func<T> body, TimeSpan wait, CancellationToken cancel, out T? result)
    {
        result = default;
        if (cancel.IsCancellationRequested) return false;
        var work = new Work(() => body());
        queue.Enqueue(work);
        try
        {
            if (!work.Done.Wait(wait, cancel)) { work.Abandoned = true; return false; }
        }
        catch (OperationCanceledException) { work.Abandoned = true; return false; }
        if (work.Error != null) throw new InvalidOperationException("画面のスレッドでの照会の例外", work.Error);
        result = (T?)work.Result;
        return true;
    }
    // 裏のスレッドから呼ぶ。全部を先に積んでから待つので、予算に収まる分は同じフレームで処理される。
    // done[i]：wait の間に処理されたか。errors[i]：画面のスレッドでの例外。処理されなかった仕事は捨てられる。
    public void TryRunAll<T>(IReadOnlyList<Func<T>> bodies, TimeSpan wait, CancellationToken cancel, T?[] results, bool[] done, Exception?[] errors)
    {
        if (cancel.IsCancellationRequested) return;
        var works = bodies.Select(body => new Work(() => body())).ToArray();
        foreach (var work in works) queue.Enqueue(work);
        var until = Stopwatch.StartNew();
        for (var i = 0; i < works.Length; i++)
        {
            var left = wait - until.Elapsed;
            try
            {
                if (left <= TimeSpan.Zero || !works[i].Done.Wait(left, cancel)) { Abandon(works, i); return; }
            }
            catch (OperationCanceledException) { Abandon(works, i); return; }
            done[i] = true; errors[i] = works[i].Error;
            if (works[i].Error == null) results[i] = (T?)works[i].Result;
        }
    }
    private static void Abandon(Work[] works, int from)
    {
        for (var k = from; k < works.Length; k++) works[k].Abandoned = true;
    }
    // 画面のスレッドから毎フレーム呼ぶ。budget を超えたら次のフレームへ回す。処理した数を返す。
    public int Pump(TimeSpan budget)
    {
        var timer = Stopwatch.StartNew(); var count = 0;
        while (timer.Elapsed < budget && queue.TryDequeue(out var work))
        {
            if (work.Abandoned) { work.Done.Set(); continue; }
            try { work.Result = work.Body(); }
            catch (Exception ex) { work.Error = ex; }
            work.Done.Set(); count++;
        }
        return count;
    }
    // 終了時：残りを処理せずに捨て、待っている側を起こす（待っている側は Unknown になる）。
    public void Abandon()
    {
        while (queue.TryDequeue(out var work)) { work.Abandoned = true; work.Done.Set(); }
    }
}

// 画面のスレッドでしか呼べない窓口を、待ち行列を通して裏のスレッドから呼ぶ。
public sealed class QueuedProbe(ICollisionProbe inner, FrameworkQueue queue, TimeSpan wait, CancellationToken cancel) : IBatchCollisionProbe
{
    public string Name => inner.Name;
    // まとめて積んで待つ。1 本ずつ頼んだときと同じ答え（時間内に終わらない・例外・打ち切りは Unknown）。
    public ProbeResult[] Segments(IReadOnlyList<(Vector3 From, Vector3 To)> segments, bool fly)
    {
        var results = new ProbeResult?[segments.Count]; var done = new bool[segments.Count]; var errors = new Exception?[segments.Count];
        var bodies = segments.Select(s => (Func<ProbeResult>)(() => inner.Segment(s.From, s.To, fly))).ToArray();
        queue.TryRunAll(bodies, wait, cancel, results, done, errors);
        return Enumerable.Range(0, segments.Count).Select(i =>
            errors[i] is { } error ? new(Verdict.Unknown, "照会の例外: " + error.GetType().Name)
            : done[i] && results[i] != null ? results[i]!
            : new ProbeResult(Verdict.Unknown, cancel.IsCancellationRequested ? "打ち切り" : "画面のスレッドの照会が時間内に終わらない")).ToArray();
    }
    public ProbeResult Segment(Vector3 from, Vector3 to, bool fly)
    {
        try
        {
            return queue.TryRun(() => inner.Segment(from, to, fly), wait, cancel, out var result) && result != null
                ? result : new(Verdict.Unknown, cancel.IsCancellationRequested ? "打ち切り" : "画面のスレッドの照会が時間内に終わらない");
        }
        catch (Exception ex) { return new(Verdict.Unknown, "照会の例外: " + (ex.InnerException ?? ex).GetType().Name); }
    }
}
