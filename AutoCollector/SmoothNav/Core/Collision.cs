// SmoothNav（SmoothNav.Core/Collision.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

public enum Verdict { Clear, Blocked, Unknown }
public sealed record ProbeResult(Verdict Verdict, string Reason = "", float? Distance = null,
    Vector3? Point = null, Vector3? Normal = null, ulong? Material = null);
public interface ICollisionProbe
{
    string Name { get; }
    ProbeResult Segment(Vector3 from, Vector3 to, bool fly);
}
// まとめて頼める窓口（画面のスレッドの待ち行列を通す窓口など）。1 本ずつ待つと照会の数だけフレームを待つので、
// 曲線の部品は、続けて確かめる線をまとめて先に頼む。答えは 1 本ずつ頼んだときと同じでなければならない。
public interface IBatchCollisionProbe : ICollisionProbe
{
    ProbeResult[] Segments(IReadOnlyList<(Vector3 From, Vector3 To)> segments, bool fly);
}
public sealed record SegmentCheck(int Index, float Length, ProbeResult Result);
public sealed record CollisionMetrics(string Backend, int BlockedSegments, double BlockedSegmentLength,
    int UnknownSegments, SegmentCheck[] Checks);

public static class CollisionMeasurement
{
    public static CollisionMetrics Measure(ICollisionProbe probe, IReadOnlyList<Vector3> path, bool fly)
    {
        var checks = Enumerable.Range(1, Math.Max(0, path.Count - 1))
            .Select(i => new SegmentCheck(i - 1, Vector3.Distance(path[i - 1], path[i]), probe.Segment(path[i - 1], path[i], fly))).ToArray();
        return new(probe.Name, checks.Count(c => c.Result.Verdict == Verdict.Blocked),
            checks.Where(c => c.Result.Verdict == Verdict.Blocked).Sum(c => (double)c.Length),
            checks.Count(c => c.Result.Verdict == Verdict.Unknown), checks);
    }

    public static IEnumerable<Vector3> BodyOffsets(Vector3 direction, float radius = 0.5f)
    {
        yield return Vector3.UnitY;
        if (radius == 0) yield break;
        var axis = Math.Abs(direction.Y) > 0.95 ? Vector3.UnitX : Vector3.UnitY;
        var side = Vector3.Normalize(Vector3.Cross(direction, axis)) * radius;
        var up = Vector3.Normalize(Vector3.Cross(side, direction)) * radius;
        yield return Vector3.UnitY + side; yield return Vector3.UnitY - side;
        yield return Vector3.UnitY + up; yield return Vector3.UnitY - up;
    }
}

public static class MaterialRules
{
    public static bool FlyThrough(ulong material) => new ulong[] { 0x100000, 0x1000000, 0x800000, 0xB400 }
        .Any(mask => (material & mask) == mask);
}

public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, ulong Material);

// 地形の三角形だけを二分木に保持する。探索用の地図は作らない。
public sealed class TriangleProbe : ICollisionProbe
{
    private sealed record Node(Vector3 Min, Vector3 Max, int Start, int Count, Node? Left, Node? Right);
    private readonly Triangle[] triangles;
    private readonly Node? root;
    public string Name => "地形の三角形（中心＋周囲4本）";
    public int TriangleCount => triangles.Length;
    public TriangleProbe(IEnumerable<Triangle> source)
    {
        triangles = source.ToArray();
        if (triangles.Length > 0) root = Build(0, triangles.Length);
    }
    private Node Build(int start, int count)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (var i = start; i < start + count; i++)
        {
            var t = triangles[i];
            min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
            max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
        }
        if (count <= 12) return new(min, max, start, count, null, null);
        var size = max - min;
        var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        Array.Sort(triangles, start, count, Comparer<Triangle>.Create((a, b) =>
            ((a.A + a.B + a.C)[axis]).CompareTo((b.A + b.B + b.C)[axis])));
        var half = count / 2;
        return new(min, max, start, count, Build(start, half), Build(start + half, count - half));
    }
    public ProbeResult Segment(Vector3 from, Vector3 to, bool fly)
    {
        if (!Metrics.Finite(from) || !Metrics.Finite(to)) return new(Verdict.Unknown, "座標が有限でない");
        if (root == null) return new(Verdict.Unknown, "地形の三角形が無い（配置物は別判定）");
        var d = to - from;
        if (d.LengthSquared() < 1e-10) return new(Verdict.Clear);
        ProbeResult? best = null;
        foreach (var offset in CollisionMeasurement.BodyOffsets(Vector3.Normalize(d)))
        {
            var hit = Ray(from + offset, to + offset, fly);
            if (hit.Verdict == Verdict.Blocked && (best == null || hit.Distance < best.Distance)) best = hit;
        }
        return best ?? new(Verdict.Clear);
    }
    public ProbeResult Ray(Vector3 from, Vector3 to, bool fly, bool wallsOnly = false)
    {
        if (root == null) return new(Verdict.Unknown, "地形の三角形が無い");
        float best = float.PositiveInfinity;
        Triangle? chosen = null;
        var delta = to - from;
        void Visit(Node node)
        {
            if (!BoxHit(from, delta, node.Min, node.Max, Math.Min(best, 1))) return;
            if (node.Left != null) { Visit(node.Left); Visit(node.Right!); return; }
            for (var i = node.Start; i < node.Start + node.Count; i++)
            {
                var t = triangles[i];
                if (fly && MaterialRules.FlyThrough(t.Material)) continue;
                var normal = Vector3.Cross(t.B - t.A, t.C - t.A);
                if (wallsOnly && Math.Abs(Vector3.Normalize(normal).Y) >= 0.5f) continue;
                var time = Intersection(from, delta, t);
                if (time.HasValue && time.Value < best) { best = time.Value; chosen = t; }
            }
        }
        Visit(root);
        if (chosen is not { } hit) return new(Verdict.Clear);
        var n = Vector3.Normalize(Vector3.Cross(hit.B - hit.A, hit.C - hit.A));
        if (Vector3.Dot(n, delta) > 0) n = -n;
        return new(Verdict.Blocked, "地形の面と交差", delta.Length() * best, from + delta * best, n, hit.Material);
    }
    public static float? Intersection(Vector3 origin, Vector3 delta, Triangle triangle)
    {
        var edge1 = triangle.B - triangle.A; var edge2 = triangle.C - triangle.A;
        var p = Vector3.Cross(delta, edge2); var det = Vector3.Dot(edge1, p);
        if (Math.Abs(det) < 1e-7f) return null;
        var relative = origin - triangle.A;
        var u = Vector3.Dot(relative, p) / det;
        var q = Vector3.Cross(relative, edge1);
        var v = Vector3.Dot(delta, q) / det;
        var t = Vector3.Dot(edge2, q) / det;
        return u >= -1e-6 && v >= -1e-6 && u + v <= 1 + 1e-6 && t >= 0 && t <= 1 ? t : null;
    }
    private static bool BoxHit(Vector3 origin, Vector3 delta, Vector3 min, Vector3 max, float end)
    {
        float start = 0;
        for (var axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(delta[axis]) < 1e-10)
            {
                if (origin[axis] < min[axis] - 1e-5 || origin[axis] > max[axis] + 1e-5) return false;
                continue;
            }
            var a = (min[axis] - origin[axis]) / delta[axis]; var b = (max[axis] - origin[axis]) / delta[axis];
            start = Math.Max(start, Math.Min(a, b)); end = Math.Min(end, Math.Max(a, b));
            if (start > end + 1e-6) return false;
        }
        return true;
    }
}
