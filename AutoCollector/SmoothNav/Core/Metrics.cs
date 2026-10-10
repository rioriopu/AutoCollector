// SmoothNav（SmoothNav.Core/Metrics.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

public sealed record ShapeMetrics(int Points, double Length, int SharpTurns, double SharpTurnsPer100m,
    double MaxTurnDegrees, double SteepLength, double HeightVariation, double? HeightVariationRatio,
    double? LengthRatio);

public static class Metrics
{
    public static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    public static ShapeMetrics Shape(IReadOnlyList<Vector3> path, double? officialLength = null)
    {
        if (path.Any(p => !Finite(p))) throw new ArgumentException("有限でない座標です", nameof(path));
        double length = 0, steep = 0, height = 0, maxAngle = 0;
        var turns = 0;
        Vector3? previous = null;
        for (var i = 1; i < path.Count; i++)
        {
            var delta = path[i] - path[i - 1];
            var distance = delta.Length();
            if (distance <= 1e-5f) continue;
            length += distance;
            height += Math.Abs(delta.Y);
            if (Math.Atan2(Math.Abs(delta.Y), Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z)) >= Math.PI / 3 - 1e-6)
                steep += distance;
            var direction = delta / distance;
            if (previous is { } before)
            {
                var angle = Math.Acos(Math.Clamp(Vector3.Dot(before, direction), -1, 1)) * 180 / Math.PI;
                maxAngle = Math.Max(maxAngle, angle);
                if (angle >= 30 - 1e-5) turns++;
            }
            previous = direction;
        }
        var netHeight = path.Count > 1 ? Math.Abs(path[^1].Y - path[0].Y) : 0;
        return new(path.Count, length, turns, length > 0 ? turns * 100 / length : 0,
            maxAngle, steep, height, netHeight > 1e-5 ? height / netHeight : null,
            officialLength > 1e-5 ? length / officialLength : null);
    }

    // 分位点は nearest-rank。空集合は未測定として返す。
    public static double? Percentile(IEnumerable<double> values, double probability)
    {
        if (probability is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(probability));
        var a = values.Order().ToArray();
        return a.Length == 0 ? null : a[Math.Clamp((int)Math.Ceiling(a.Length * probability) - 1, 0, a.Length - 1)];
    }
}

public sealed record PositionSample(double Seconds, Vector3 Position, bool Flying, bool Mounted);
public sealed record TravelMetrics(double? StartDelaySeconds, double? ArrivalSeconds, int Stops,
    double StoppedSeconds, double MovingSeconds, double? MovingSpeed, double? GroundSpeed, double? FlightSpeed,
    int SampleGaps, ShapeMetrics ActualShape, WindowShape? ActualWindow = null);

public static class TravelMeasurement
{
    // 0.2秒の標本間で0.05m以下を静止とする。0.5秒以上の標本欠落は別計上し、静止と推測しない。
    public static TravelMetrics Calculate(IReadOnlyList<PositionSample> samples, double? arrivalSeconds = null)
    {
        double? start = null;
        double stopDuration = 0, stopped = 0, movingTime = 0, movingLength = 0;
        double groundTime = 0, groundLength = 0, flightTime = 0, flightLength = 0;
        var stops = 0;
        var gaps = 0;
        void EndStop() { if (stopDuration >= 0.5 - 1e-6) { stops++; stopped += stopDuration; } stopDuration = 0; }
        for (var i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1]; var b = samples[i];
            var dt = b.Seconds - a.Seconds;
            if (dt <= 0) throw new ArgumentException("標本の時刻は増加する必要があります");
            if (dt > 0.5 + 1e-6) { gaps++; EndStop(); continue; }
            var distance = Vector3.Distance(a.Position, b.Position);
            if (distance <= 0.05f)
            {
                if (start.HasValue) stopDuration += dt;
                continue;
            }
            start ??= b.Seconds;
            EndStop();
            movingTime += dt; movingLength += distance;
            if (a.Mounted != b.Mounted || a.Flying != b.Flying) continue;
            if (a.Flying) { flightTime += dt; flightLength += distance; }
            else { groundTime += dt; groundLength += distance; }
        }
        EndStop();
        return new(start, arrivalSeconds, stops, stopped, movingTime, movingTime > 0 ? movingLength / movingTime : null,
            groundTime > 0 ? groundLength / groundTime : null, flightTime > 0 ? flightLength / flightTime : null,
            gaps, Metrics.Shape(samples.Select(s => s.Position).ToArray()),
            WindowMeasurement.Measure(samples.Select(s => s.Position).ToArray(), samples.Any(s => s.Flying)));
    }
}
