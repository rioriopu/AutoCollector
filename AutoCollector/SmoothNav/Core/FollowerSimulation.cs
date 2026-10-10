// SmoothNav（SmoothNav.Core/FollowerSimulation.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

public sealed record FollowerSettings(float Speed = 6, int FramesPerSecond = 60, float Tolerance = .25f,
    float CameraDegreesPerSecond = 360, int MaximumFrames = 240000, bool InitiallyFlying = true, bool Mounted = true);
public sealed record FollowerResult(bool Completed, string Reason, int ReverseEvents, int MissedPoints,
    double Seconds, double MaxCameraDegreesPerSecond, int TakeoffRequests, PositionSample[] Trajectory);

// 公開された追従規則から作った一定速度の幾何模擬。物理・衝突応答・離陸の所要時間は含まない。
public static class FollowerSimulation
{
    public static FollowerResult Run(IReadOnlyList<Vector3> path, bool fly, FollowerSettings? settings = null, bool trajectory = true)
    {
        var c = settings ?? new();
        if (c.Speed <= 0 || !float.IsFinite(c.Speed) || c.FramesPerSecond <= 0 || c.Tolerance < 0 || c.MaximumFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings));
        if (path.Any(p => !Metrics.Finite(p))) throw new ArgumentException("有限でない座標です");
        if (path.Count == 0) return new(true, "空の公式経路", 0, 0, 0, 0, 0, []);
        var current = path[0]; var previous = current;
        var next = path.Count > 1 ? 1 : 0; var misses = new HashSet<int>();
        var reverse = 0; var reversing = false; var takeoff = 0; var flying = c.InitiallyFlying;
        double? camera = null; double maxCamera = 0;
        var samples = new List<PositionSample>();
        Vector3 Flat(Vector3 v) => fly ? v : new(v.X, 0, v.Z);
        for (var frame = 0; frame < c.MaximumFrames; frame++)
        {
            if (trajectory && (frame == 0 || frame % Math.Max(1, c.FramesPerSecond / 5) == 0)) samples.Add(new(frame / (double)c.FramesPerSecond, current, fly, c.Mounted));
            while (next < path.Count && DistanceToSegment(Flat(path[next]), Flat(previous), Flat(current)) <= c.Tolerance) next++;
            if (next == path.Count)
            {
                if (trajectory) samples.Add(new(frame / (double)c.FramesPerSecond, current, flying && fly, c.Mounted));
                return new(true, "追従終了", reverse, misses.Count, frame / (double)c.FramesPerSecond, maxCamera, takeoff, samples.ToArray());
            }
            if (fly && !flying && path[next].Y > current.Y)
            {
                if (!c.Mounted) return new(false, "未騎乗で上昇できない", reverse, misses.Count, frame / (double)c.FramesPerSecond, maxCamera, takeoff, samples.ToArray());
                takeoff++; flying = true;
            }
            var reference = Flat(path[next] - path[Math.Max(0, next - 1)]);
            if (reference.LengthSquared() > 1e-10 && Vector3.Dot(Flat(current - path[next]), reference) > 1e-5 &&
                DistanceToSegment(Flat(path[next]), Flat(previous), Flat(current)) > c.Tolerance) misses.Add(next);
            var direction = Flat(path[next] - current);
            if (direction.LengthSquared() < 1e-10) { next++; continue; }
            direction = Vector3.Normalize(direction);
            var backwards = reference.LengthSquared() > 1e-10 && Vector3.Dot(direction, reference) <= 0;
            if (backwards && !reversing) reverse++;
            reversing = backwards;
            var yaw = Math.Atan2(direction.Z, direction.X) * 180 / Math.PI;
            camera ??= yaw;
            var difference = Math.IEEERemainder(yaw - camera.Value, 360);
            var turn = Math.Clamp(difference, -c.CameraDegreesPerSecond / c.FramesPerSecond, c.CameraDegreesPerSecond / c.FramesPerSecond);
            maxCamera = Math.Max(maxCamera, Math.Abs(turn) * c.FramesPerSecond); camera += turn;
            previous = current;
            current += direction * (c.Speed / c.FramesPerSecond);
            if (!fly) current.Y = path[next].Y;
        }
        return new(false, "模擬のフレーム上限", reverse, misses.Count, c.MaximumFrames / (double)c.FramesPerSecond, maxCamera, takeoff, samples.ToArray());
    }
    public static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var d = b - a; var norm = d.LengthSquared();
        var t = norm > 1e-10 ? Math.Clamp(Vector3.Dot(point - a, d) / norm, 0, 1) : 0;
        return Vector3.Distance(point, a + d * t);
    }
    public static IEnumerable<FollowerSettings> Profiles(bool fly)
    {
        foreach (var fps in new[] { 30, 60 })
            foreach (var speed in fly ? new[] { 20f } : new[] { 6f, 9f }) yield return new(speed, fps);
    }
}
