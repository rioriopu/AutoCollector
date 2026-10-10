// SmoothNav（SmoothNav.Core/WindowMetrics.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

public sealed record MeasurementSettings(float Spacing = .25f, float GroundWindow = 2, float FlightWindow = 8,
    float SharpDegrees = 30, float GroundMinimumRadius = 2, float FlightMinimumRadius = 10,
    float SlopeHorizontalWindow = 4, float SteepDegrees = 60, float HeightReversal = .5f);
public sealed record WindowShape(double Length, int SharpTurns, double SharpTurnsPer100m, double MaxWindowDegrees,
    double? MinimumRadius, double BelowRadiusLength, double CurvatureJumpMax, double CurvatureJumpP95,
    double SteepLength, int HeightReversals, double HeightReversalsPer100m, double HeightVariation, double? HeightVariationRatio);

// 元の折れ線を弧長で評価する。補間点を足しても窓の位置と接線の測り方は変わらない。
public sealed class ArcPath
{
    public Vector3[] Points { get; }
    public double[] Distances { get; }
    public double Length => Distances.Length == 0 ? 0 : Distances[^1];
    public ArcPath(IReadOnlyList<Vector3> points)
    {
        if (points.Any(p => !Metrics.Finite(p))) throw new ArgumentException("有限でない座標です");
        Points = points.ToArray(); Distances = new double[points.Count];
        for (var i = 1; i < points.Count; i++) Distances[i] = Distances[i - 1] + Vector3.Distance(points[i - 1], points[i]);
    }
    public Vector3 At(double distance)
    {
        if (Points.Length == 0) return default;
        if (distance <= 0) return Points[0];
        if (distance >= Length) return Points[^1];
        var i = Array.BinarySearch(Distances, distance);
        if (i >= 0) return Points[i];
        i = ~i;
        return Vector3.Lerp(Points[i - 1], Points[i], (float)((distance - Distances[i - 1]) / (Distances[i] - Distances[i - 1])));
    }
    public Vector3[] Sample(float spacing)
    {
        if (!float.IsFinite(spacing) || spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing));
        if (Points.Length == 0) return [];
        var count = checked((int)Math.Ceiling(Length / spacing));
        var result = new Vector3[count + 1];
        for (var i = 0; i < count; i++) result[i] = At(i * (double)spacing);
        result[^1] = Points[^1];
        return result;
    }
}

public static class WindowMeasurement
{
    public static WindowShape Measure(IReadOnlyList<Vector3> path, bool fly, MeasurementSettings? settings = null)
    {
        var c = settings ?? new();
        if (c.Spacing <= 0 || c.GroundWindow <= 0 || c.FlightWindow <= 0 || c.SlopeHorizontalWindow <= 0 || c.HeightReversal <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings));
        var arc = new ArcPath(path); var p = arc.Sample(c.Spacing);
        var w = fly ? c.FlightWindow : c.GroundWindow;
        var minimum = fly ? c.FlightMinimumRadius : c.GroundMinimumRadius;
        double max = 0, below = 0, steep = 0, height = 0;
        double? radius = null;
        var turns = 0; var inTurn = false;
        var jumps = new List<double>(); double? previous = null;
        (double X, double Z) HorizontalAt(double distance)
        {
            if (distance <= 0) return (path[0].X, path[0].Z);
            if (distance >= arc.Length) return (path[^1].X, path[^1].Z);
            var index = Array.BinarySearch(arc.Distances, distance);
            if (index >= 0) return (path[index].X, path[index].Z);
            index = ~index;
            var t = (distance - arc.Distances[index - 1]) / (arc.Distances[index] - arc.Distances[index - 1]);
            // 同じ水平座標の垂直区間に、float補間の丸めによる偽の方向を作らない。
            return (path[index - 1].X + ((double)path[index].X - path[index - 1].X) * t,
                path[index - 1].Z + ((double)path[index].Z - path[index - 1].Z) * t);
        }
        (double X, double Z) Tangent(double d)
        {
            var a = HorizontalAt(Math.Max(0, d - c.Spacing / 2));
            var b = HorizontalAt(Math.Min(arc.Length, d + c.Spacing / 2));
            return (b.X - a.X, b.Z - a.Z);
        }
        var horizontal = new double[p.Length];
        for (var i = 1; i < p.Length; i++) horizontal[i] = horizontal[i - 1] + HorizontalDistance(p[i - 1], p[i]);
        double HeightAt(double h, bool last)
        {
            if (horizontal[^1] <= 1e-9) return last ? p[^1].Y : p[0].Y;
            if (h <= 0) return p[0].Y;
            if (h >= horizontal[^1]) return p[^1].Y;
            var j = Array.BinarySearch(horizontal, h);
            if (j >= 0)
            {
                if (last) while (j + 1 < p.Length && horizontal[j + 1] == h) j++;
                else while (j > 0 && horizontal[j - 1] == h) j--;
                return p[j].Y;
            }
            j = ~j;
            return p[j - 1].Y + (p[j].Y - p[j - 1].Y) * (h - horizontal[j - 1]) / (horizontal[j] - horizontal[j - 1]);
        }
        for (var i = 0; i + 1 < p.Length; i++)
        {
            var s = i * (double)c.Spacing; var ds = Math.Min(c.Spacing, arc.Length - s);
            var a = Tangent(Math.Max(0, s - w / 2)); var b = Tangent(Math.Min(arc.Length, s + w / 2));
            double angle = 0;
            if (Math.Sqrt(a.X * a.X + a.Z * a.Z) > 1e-6 && Math.Sqrt(b.X * b.X + b.Z * b.Z) > 1e-6)
                angle = Math.Atan2(a.X * b.Z - a.Z * b.X, a.X * b.X + a.Z * b.Z) * 180 / Math.PI;
            var magnitude = Math.Abs(angle);
            var sharp = magnitude >= c.SharpDegrees - 1e-5;
            if (sharp && !inTurn) turns++;
            inTurn = sharp; max = Math.Max(max, magnitude);
            if (magnitude > 1e-6)
            {
                var r = w / (magnitude * Math.PI / 180);
                radius = radius.HasValue ? Math.Min(radius.Value, r) : r;
                if (r < minimum) below += ds;
            }
            if (previous.HasValue) jumps.Add(Math.Abs(angle - previous.Value));
            previous = angle;
            var lo = Math.Max(0, horizontal[i] - c.SlopeHorizontalWindow / 2);
            var hi = Math.Min(horizontal[^1], horizontal[i] + c.SlopeHorizontalWindow / 2);
            var slope = Math.Atan2(Math.Abs(HeightAt(hi, true) - HeightAt(lo, false)), hi - lo) * 180 / Math.PI;
            if (HorizontalDistance(p[i], p[i + 1]) < 1e-6f && Math.Abs(p[i + 1].Y - p[i].Y) > 1e-6f) slope = 90;
            if (slope >= c.SteepDegrees - 1e-5) steep += ds;
            height += Math.Abs(p[i + 1].Y - p[i].Y);
        }
        // ヒステリシスで0.5m未満の揺れを反転に数えない。
        var reversals = 0; var direction = 0;
        double extreme = p.Length > 0 ? p[0].Y : 0;
        foreach (var point in p)
        {
            var change = point.Y - extreme;
            if (direction == 0 && Math.Abs(change) >= c.HeightReversal) { direction = Math.Sign(change); extreme = point.Y; }
            else if (direction != 0)
            {
                if (change * direction > 0) extreme = point.Y;
                else if (-change * direction >= c.HeightReversal) { reversals++; direction = -direction; extreme = point.Y; }
            }
        }
        var net = p.Length > 0 ? Math.Abs(p[^1].Y - p[0].Y) : 0;
        return new(arc.Length, turns, arc.Length > 0 ? turns * 100 / arc.Length : 0, max, radius, below,
            jumps.DefaultIfEmpty().Max(), Metrics.Percentile(jumps, .95) ?? 0, steep, reversals,
            arc.Length > 0 ? reversals * 100 / arc.Length : 0, height, net > 1e-5 ? height / net : null);
    }
    public static float HorizontalDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    // 尖った角の跳び（M4）は、角が標本の刻みのどこに来るかで角度の半分から全部まで変わる。
    // 加工の前後を比べるときは、刻みの始まりを phases 通りずらした最大で比べる（32通りで90度の角の差は1度未満）。
    public static double JumpMaxOverPhases(IReadOnlyList<Vector3> path, bool fly, int phases = 32, MeasurementSettings? settings = null)
    {
        var c = settings ?? new();
        var arc = new ArcPath(path);
        if (arc.Length <= 0 || phases < 1) return 0;
        var w = fly ? c.FlightWindow : c.GroundWindow;
        (double X, double Z) Flat(double d)
        {
            var q = Math.Clamp(d, 0, arc.Length);
            var i = Array.BinarySearch(arc.Distances, q);
            if (i >= 0) return (path[i].X, path[i].Z);
            i = ~i;
            var t = (q - arc.Distances[i - 1]) / (arc.Distances[i] - arc.Distances[i - 1]);
            return (path[i - 1].X + ((double)path[i].X - path[i - 1].X) * t, path[i - 1].Z + ((double)path[i].Z - path[i - 1].Z) * t);
        }
        (double X, double Z) Heading(double d)
        {
            var from = Flat(d - c.Spacing / 2); var to = Flat(d + c.Spacing / 2);
            return (to.X - from.X, to.Z - from.Z);
        }
        double best = 0;
        for (var k = 0; k < phases; k++)
        {
            double? previous = null;
            for (var s = k * (double)c.Spacing / phases; s < arc.Length; s += c.Spacing)
            {
                var a = Heading(Math.Max(0, s - w / 2)); var b = Heading(Math.Min(arc.Length, s + w / 2));
                double angle = 0;
                if (Math.Sqrt(a.X * a.X + a.Z * a.Z) > 1e-6 && Math.Sqrt(b.X * b.X + b.Z * b.Z) > 1e-6)
                    angle = Math.Atan2(a.X * b.Z - a.Z * b.X, a.X * b.X + a.Z * b.Z) * 180 / Math.PI;
                if (previous.HasValue) best = Math.Max(best, Math.Abs(angle - previous.Value));
                previous = angle;
            }
        }
        return best;
    }
}
