// SmoothNav（SmoothNav.Core/GroundSurface.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

public sealed record SurfacePoint(ProbeResult Check, Vector3? Position, float? WallDistance = null, Vector3? AwayFromWall = null);
public interface IGroundSurface : ICollisionProbe
{
    SurfacePoint Point(Vector3 point);
    Vector3? Project(Vector3 point);
}
// 地上の追従は高さを無視するので、角と角を結んだ直線の高さは地面と合わない。物差しの前に標本点を面の高さへ載せる。
public interface IGroundDrape
{
    Vector3[] Drape(IReadOnlyList<Vector3> path, float spacing);
}
public sealed record WallMetrics(double NearWallLength, double? Median, double? Minimum, int OutsidePoints, int UnknownPoints);
public static class GroundMeasurement
{
    public static WallMetrics Measure(IGroundSurface surface, IReadOnlyList<Vector3> path, float spacing = .25f, float margin = .5f)
    {
        var arc = new ArcPath(path);
        var points = surface is IGroundDrape drape ? drape.Drape(path, spacing) : arc.Sample(spacing);
        var walls = new List<double>(); double near = 0; var outside = 0; var unknown = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var p = surface.Point(points[i]);
            if (p.Check.Verdict == Verdict.Blocked) outside++;
            if (p.Check.Verdict == Verdict.Unknown) unknown++;
            if (p.WallDistance is { } distance)
            {
                walls.Add(distance);
                if (distance < margin && i + 1 < points.Length) near += Math.Min(spacing, arc.Length - i * (double)spacing);
            }
        }
        return new(near, Metrics.Percentile(walls, .5), walls.Count > 0 ? walls.Min() : null, outside, unknown);
    }
}
