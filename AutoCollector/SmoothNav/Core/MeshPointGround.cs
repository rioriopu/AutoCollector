// SmoothNav（SmoothNav.Core/MeshPointGround.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

// ゲームの中で組み込み先のプラグインが呼べる公式の点の照会（IPC の vnavmesh.Query.Mesh.NearestPoint と同じ）。
// 公式の照会は地図を読むだけで、経路探索の作業領域を使わない。
public interface IMeshPointQuery
{
    Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY);
}

// HorizontalTolerance：面の上と見なす水平の誤差。StepHeight：0.25m 進む間に許す高さの変化（階段・坂）。
// WallProbeRadii：壁（面の縁）を探す距離。点の照会には壁までの距離が無いので、周りに点を打って縁を探す。
public sealed record MeshPointSettings(float HorizontalTolerance = .05f, float HeightTolerance = 1, float StepHeight = 1,
    float SampleSpacing = .25f, float SnapRadius = .5f, int WallDirections = 12, float WallStep = .25f, float WallProbeRadius = .75f);

// 点の照会だけで作った歩ける面の窓口。区間を SampleSpacing ごとにたどり、各点で前の点の高さから続く面を探す。
// 面が途切れる・段差が大きい・終点が別の高さの面なら Blocked。地上の追従は高さを無視するので、直線の高さは面と比べない。
public sealed class MeshPointGround(IMeshPointQuery query, MeshPointSettings? settings = null) : IGroundSurface, IGroundDrape
{
    private readonly MeshPointSettings c = settings ?? new();
    private readonly Dictionary<(Vector3, Vector3), ProbeResult> segments = new();
    private readonly Dictionary<Vector3, SurfacePoint> points = new();
    public string Name => "地上の歩ける面（点の照会）";

    private Vector3? Near(Vector3 p, float halfXZ, float halfY)
    {
        var q = query.NearestPoint(p, halfXZ, halfY);
        return q is { } v && Metrics.Finite(v) ? v : null;
    }
    // 始点・単独の点：点の高さから HeightTolerance 以内の面の上か
    private Vector3? Anchor(Vector3 p) =>
        Near(p, c.HorizontalTolerance, c.HeightTolerance) is { } v && WindowMeasurement.HorizontalDistance(v, p) <= c.HorizontalTolerance
            && Math.Abs(v.Y - p.Y) <= c.HeightTolerance ? v : null;
    // たどる点：前の点の高さから StepHeight 以内で続く面の上か
    private Vector3? Follow(Vector3 p, float previousY) =>
        Near(p with { Y = previousY }, c.HorizontalTolerance, c.StepHeight) is { } v && WindowMeasurement.HorizontalDistance(v, p) <= c.HorizontalTolerance
            && Math.Abs(v.Y - previousY) <= c.StepHeight ? v : null;

    public ProbeResult Segment(Vector3 from, Vector3 to, bool fly)
    {
        if (fly) return new(Verdict.Unknown, "地上専用の検査");
        if (segments.TryGetValue((from, to), out var known)) return known;
        var result = Walk(from, to);
        segments[(from, to)] = result;
        return result;
    }
    private ProbeResult Walk(Vector3 from, Vector3 to)
    {
        if (!Metrics.Finite(from) || !Metrics.Finite(to)) return new(Verdict.Unknown, "座標が有限でない");
        try
        {
            if (Anchor(from) is not { } start) return new(Verdict.Blocked, "始点が歩ける面の外");
            var count = Math.Max(1, (int)Math.Ceiling(WindowMeasurement.HorizontalDistance(from, to) / c.SampleSpacing));
            var y = start.Y;
            for (var i = 1; i <= count; i++)
            {
                var p = Vector3.Lerp(from, to, i / (float)count);
                if (Follow(p, y) is not { } on) return new(Verdict.Blocked, "面が途切れるか段差が大きい", Vector3.Distance(from, p), p);
                y = on.Y;
            }
            return Math.Abs(y - to.Y) <= c.HeightTolerance ? new(Verdict.Clear) : new(Verdict.Blocked, "終点が別の高さの面");
        }
        catch (Exception ex) { return new(Verdict.Unknown, "点の照会の例外: " + ex.GetType().Name); }
    }
    public Vector3? Project(Vector3 point)
    {
        if (!Metrics.Finite(point)) return null;
        try
        {
            if (Anchor(point) is { } on) return on;
            return Near(point, c.SnapRadius, c.HeightTolerance) is { } v && WindowMeasurement.HorizontalDistance(v, point) <= c.SnapRadius
                && Math.Abs(v.Y - point.Y) <= c.HeightTolerance ? v : null;
        }
        catch (Exception) { return null; }
    }
    public SurfacePoint Point(Vector3 point)
    {
        if (!Metrics.Finite(point)) return new(new(Verdict.Unknown, "座標が有限でない"), null);
        if (points.TryGetValue(point, out var known)) return known;
        SurfacePoint result;
        try
        {
            if (Anchor(point) is not { } on) result = new(new(Verdict.Blocked, "歩ける面の外または高さ許容外"), Project(point));
            else { var (distance, away) = Wall(on); result = new(new(Verdict.Clear), on, distance, away); }
        }
        catch (Exception ex) { result = new(new(Verdict.Unknown, "点の照会の例外: " + ex.GetType().Name), null); }
        points[point] = result;
        return result;
    }
    // 周りの WallDirections 方向へ WallStep ごとに点を打ち、最初に面の外になった距離を縁までの距離と見なす（刻みの半分を引く）。
    // 離れる向きは、面の外になった方向の和の逆。WallProbeRadius まで面が続けば、その距離を返す。
    private (float Distance, Vector3 Away) Wall(Vector3 on)
    {
        // 外周が全部面の上なら、縁は探す範囲の外と見なす（開けた所の照会を WallDirections 回で済ませる）。
        // 公式の面は身体の半径の分だけ縮めてあるので、面の穴は直径 1m 以上あり、30 度ごとの外周の点のどれかに掛かる。
        var open = true;
        for (var k = 0; k < c.WallDirections && open; k++)
        {
            var angle = 2 * MathF.PI * k / c.WallDirections;
            if (Follow(on + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * c.WallProbeRadius, on.Y) == null) open = false;
        }
        if (open) return (c.WallProbeRadius, default);
        for (var r = c.WallStep; r <= c.WallProbeRadius + 1e-4f; r += c.WallStep)
        {
            Vector3 outside = default; var found = false;
            for (var k = 0; k < c.WallDirections; k++)
            {
                var angle = 2 * MathF.PI * k / c.WallDirections;
                var direction = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
                if (Follow(on + direction * r, on.Y) == null) { outside += direction; found = true; }
            }
            if (found)
                return (r - c.WallStep / 2, outside.LengthSquared() > 1e-6f ? -Vector3.Normalize(outside) : default);
        }
        return (c.WallProbeRadius, default);
    }
    // 物差し用：弧長の標本点を、前の点の高さから続く面の高さへ載せる。続かない点はそのまま返す。
    public Vector3[] Drape(IReadOnlyList<Vector3> path, float spacing)
    {
        var samples = new ArcPath(path).Sample(spacing);
        if (samples.Length == 0) return samples;
        float? y = Anchor(samples[0])?.Y;
        for (var i = 0; i < samples.Length; i++)
        {
            var on = y.HasValue ? Follow(samples[i], y.Value) : Anchor(samples[i]);
            if (on is { } v) { samples[i] = samples[i] with { Y = v.Y }; y = v.Y; }
        }
        return samples;
    }
}
