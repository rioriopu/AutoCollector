// SmoothNav（SmoothNav.Core/CurveBuilder.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Numerics;

namespace SmoothNav.Core;

public enum CornerStyle { Circle, Continuous }
// 並び全体が落ちたときの分け方：Both＝通さない公式の区間で先に分け、そう分けたときは半分に分ける方も作って良い方（既定）。
public enum SplitOrder { Both, BlockedFirst, HalvesFirst }
public sealed record CurveSettings(float GroundMinimumRadius = 2, float FlightMinimumRadius = 10,
    float GroundPreferredRadius = 6, float FlightPreferredRadius = 24, float Spacing = .5f,
    float MaximumSlopeDegrees = 45, float TakeoffDegrees = 30, float LandingDegrees = 30,
    float WallMargin = .75f, float MaximumLengthRatio = 1.05f, int MaximumQueries = 20000,
    int MaximumPoints = 12000, int RadiusIterations = 8, CornerStyle CornerStyle = CornerStyle.Circle,
    bool Shortcuts = true, bool MoveAwayFromWalls = true, float JumpToleranceDegrees = 1, float ImprovementDegrees = 5,
    float WallSmoothingMeters = 2, float WallRampMeters = 6, bool SwingCorners = true, float SwingRadiusTarget = 3.9f,
    float SwingMaxOffset = 1.5f, float SwingChainLeg = 7.8f, SplitOrder SplitOrder = SplitOrder.Both);
public sealed record RouteDecision(int OriginalStart, int OriginalEnd, int UsedStart, int UsedEnd, bool Changed, string Reason);
public sealed record RouteCheck(int Segment, string Backend, ProbeResult Result, bool Changed);
public sealed record CurveResult(Vector3[] OfficialPath, Vector3[] ProposedPath, Vector3[] UsedPath,
    RouteDecision[] Decisions, RouteCheck[] Checks, double AddedSeconds, int Queries);

public static class CurveBuilder
{
    // cancel：組み込み先が時間切れで打ち切るための合図。打ち切った後の照会は Unknown になり、残りの区間は公式のまま返す。
    // 打ち切らなければ結果は決定的（同じ入力から同じ出力）。
    public static CurveResult Build(IReadOnlyList<Vector3> official, bool fly, IReadOnlyList<ICollisionProbe> probes,
        CurveSettings? settings = null, IGroundSurface? ground = null, CancellationToken cancel = default)
    {
        var c = settings ?? new(); var timer = Stopwatch.StartNew();
        if (c.Spacing <= 0 || !float.IsFinite(c.Spacing) || c.MaximumQueries <= 0 || c.MaximumPoints < 2 || c.MaximumLengthRatio < 1)
            throw new ArgumentOutOfRangeException(nameof(settings));
        var original = official.ToArray(); var used = new List<Vector3>(); var proposed = new List<Vector3>();
        var decisions = new List<RouteDecision>(); var queries = 0;
        Vector3[]? rootProposal = null;
        // blockedFirst：並び全体が落ちたとき、通さない公式の区間で分けるのを半分に分けるより先にする。splitUsed：そう分けたか。
        bool blockedFirst = c.SplitOrder != SplitOrder.HalvesFirst, splitUsed = false;
        var cache = new Dictionary<(int, Vector3, Vector3), ProbeResult>();
        var all = probes.ToList();
        if (!fly && ground != null && !all.Contains(ground)) all.Add(ground);
        ProbeResult Check(int backend, Vector3 a, Vector3 b)
        {
            var key = (backend, a, b);
            if (cache.TryGetValue(key, out var known)) return known;
            ProbeResult result;
            if (cancel.IsCancellationRequested) return new(Verdict.Unknown, "打ち切り");
            if (queries >= c.MaximumQueries) result = new(Verdict.Unknown, "照会回数の上限");
            else
            {
                queries++;
                try { result = all[backend].Segment(a, b, fly); }
                catch (Exception ex) { result = new(Verdict.Unknown, "照会例外: " + ex.GetType().Name); }
            }
            cache[key] = result; return result;
        }
        // まとめて頼める窓口へ、まだ答えを知らない線を先に頼む。答えは覚えに入り、後の Check はそれを使う（結果は 1 本ずつ頼んだときと同じ）。
        // ゲームの当たり判定は画面のスレッドの待ち行列を通るので、1 本ずつ頼むと線の数だけフレームを待つ。
        void Prefetch(IEnumerable<(Vector3 A, Vector3 B)> segments)
        {
            if (cancel.IsCancellationRequested) return;
            for (var k = 0; k < all.Count; k++)
            {
                if (all[k] is not IBatchCollisionProbe batch) continue;
                var wanted = segments.Where(s => !cache.ContainsKey((k, s.A, s.B))).Distinct().Take(Math.Max(0, c.MaximumQueries - queries)).ToList();
                if (wanted.Count == 0) continue;
                ProbeResult[] answers;
                try { answers = batch.Segments(wanted, fly); }
                catch (Exception ex) { answers = [.. wanted.Select(_ => new ProbeResult(Verdict.Unknown, "照会例外: " + ex.GetType().Name))]; }
                queries += wanted.Count;
                for (var i = 0; i < wanted.Count; i++)
                    cache[(k, wanted[i].A, wanted[i].B)] = i < answers.Length ? answers[i] : new(Verdict.Unknown, "照会の答えが足りない");
            }
        }
        static IEnumerable<(Vector3, Vector3)> Lines(IReadOnlyList<Vector3> p) => Enumerable.Range(1, Math.Max(0, p.Count - 1)).Select(i => (p[i - 1], p[i]));
        bool Clear(Vector3 a, Vector3 b) => all.Count > 0 && Enumerable.Range(0, all.Count).All(k => Check(k, a, b).Verdict == Verdict.Clear);
        // 公式の区間の上に乗っている線は、公式の追従が通る線そのもの。公式 vnavmesh が通れるとした線なので、窓口が通さなくても
        // （飛行用 voxel の床付近の過大判定・点の照会の誤差など）そのまま使う。新しく作った線（曲線・近道・高さ・壁から離す）は窓口で確かめる。
        bool OfficialLine(Vector3 a, Vector3 b) => OnOfficialLine(original, a, b, fly);
        bool Pass(Vector3 a, Vector3 b) => OfficialLine(a, b) || Clear(a, b);
        bool Valid(IReadOnlyList<Vector3> p)
        {
            if (p.Count == 0 || p.Count > c.MaximumPoints || p.Any(v => !Metrics.Finite(v))) return false;
            Prefetch(Lines(p).Where(line => !OfficialLine(line.Item1, line.Item2)));
            for (var i = 1; i < p.Count; i++) if (!Pass(p[i - 1], p[i])) return false;
            return true;
        }
        List<Vector3>? Project(List<Vector3> p, bool keepEnds = true)
        {
            if (fly || ground == null) return p;
            var projected = new List<Vector3>();
            for (var i = 0; i < p.Count; i++)
            {
                if (keepEnds && (i == 0 || i == p.Count - 1)) { projected.Add(p[i]); continue; }
                var point = ground.Project(p[i]); if (!point.HasValue) return null;
                projected.Add(point.Value);
            }
            return projected;
        }
        // 地上の大回りの曲線：q の角 i〜j（同じ向き・合計 total 度）を1つの曲がり角とみなし、外へ膨らむ1本の曲線に置き換える。
        // 入る辺・出る辺を外側へ d ずらした線に接する半径 R の円弧と、その前後のなだらかなS字（半径6m以上）で元の辺へつなぐ。
        // 公式の経路は削った面の縁に沿って曲がるので、縁に接したまま半径を大きくすると内側（壁）へ食い込む。
        // 外へ膨らめば大きい半径で回れる（人が建物の角を大回りで曲がる形）。半径は大きい順・膨らみは小さい順に試し、最初に通る形を返す。
        static Vector2 Flat(Vector3 a) => new(a.X, a.Z);
        static float Cross2(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
        static float TurnDegrees(IReadOnlyList<Vector3> q, int k)
        {
            var a = Flat(q[k]) - Flat(q[k - 1]); var b = Flat(q[k + 1]) - Flat(q[k]);
            if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f) return 0;
            return MathF.Atan2(Cross2(a, b), Vector2.Dot(a, b)) * 180 / MathF.PI;
        }
        List<Vector3>? SwingCurve(IReadOnlyList<Vector3> q, int i, int j, float total, Vector3 previous)
        {
            var inLeg = Flat(q[i]) - Flat(q[i - 1]); var outLeg = Flat(q[j + 1]) - Flat(q[j]);
            var inLength = inLeg.Length(); var outLength = outLeg.Length();
            if (inLength < .01f || outLength < .01f) return null;
            var u = inLeg / inLength; var v = outLeg / outLength;
            var side = MathF.Sign(total);
            var inside1 = side * new Vector2(-u.Y, u.X); var inside2 = side * new Vector2(-v.Y, v.X);
            var theta = MathF.Abs(total) * MathF.PI / 180;
            var den = Cross2(u, v);
            if (MathF.Abs(den) < 1e-4f) return null;
            // S は入る辺の上、T は出る辺の上。前後の角の分として、辺の半分は残す（端の辺なら全部使える）。
            var inRoom = i - 1 == 0 ? inLength : inLength * .5f; var outRoom = j + 1 == q.Count - 1 ? outLength : outLength * .5f;
            foreach (var radius in new[] { c.GroundPreferredRadius, 4.5f, c.SwingRadiusTarget }.Where(r => r >= c.SwingRadiusTarget).Distinct())
                for (var offset = 0f; offset <= c.SwingMaxOffset + 1e-4f; offset += .5f)
                {
                    var a1 = Flat(q[i]) - inside1 * offset; var a2 = Flat(q[j]) - inside2 * offset;
                    var corner = a1 + u * (Cross2(a2 - a1, v) / den);
                    var trim = radius * MathF.Tan(theta / 2);
                    var entry = corner - u * trim; var exit = corner + v * trim;
                    var ramp = offset > 0 ? MathF.Max(2, MathF.Sqrt(29.6f * offset)) : 0;
                    var start = entry - u * ramp + inside1 * offset; var end = exit + v * ramp + inside2 * offset;
                    var back = Vector2.Dot(Flat(q[i]) - start, u); var ahead = Vector2.Dot(end - Flat(q[j]), v);
                    if (back < 0 || back > inRoom || ahead < 0 || ahead > outRoom) continue;
                    var flat = new List<Vector2>();
                    void Ramp(Vector2 from, Vector2 direction, Vector2 lateral, bool outward)
                    {
                        var n = Math.Max(1, (int)MathF.Ceiling(ramp / c.Spacing));
                        for (var k = flat.Count == 0 ? 0 : 1; k <= n; k++)
                        {
                            var tau = k / (float)n; var shift = offset * (1 - MathF.Cos(MathF.PI * tau)) / 2;
                            flat.Add(from + direction * (ramp * tau) + lateral * (outward ? shift : -shift));
                        }
                    }
                    if (ramp > 0) Ramp(start, u, -inside1, true); else flat.Add(entry);
                    var center = entry + inside1 * radius; var arm = entry - center;
                    var steps = Math.Max(2, (int)MathF.Ceiling(radius * theta / c.Spacing));
                    for (var k = 1; k <= steps; k++)
                    {
                        var phi = side * theta * k / steps;
                        flat.Add(center + new Vector2(arm.X * MathF.Cos(phi) - arm.Y * MathF.Sin(phi), arm.X * MathF.Sin(phi) + arm.Y * MathF.Cos(phi)));
                    }
                    flat[^1] = exit;
                    if (ramp > 0) Ramp(exit, v, -inside2, false);
                    // 高さは S と T の面の高さの間を弧長でつなぎ、面へ載せる（地上の追従は高さを無視するので記録のため）。
                    var yStart = float.Lerp(q[i - 1].Y, q[i].Y, Math.Clamp(1 - back / inLength, 0, 1));
                    var yEnd = float.Lerp(q[j].Y, q[j + 1].Y, Math.Clamp(ahead / outLength, 0, 1));
                    var lengths = new float[flat.Count];
                    for (var k = 1; k < flat.Count; k++) lengths[k] = lengths[k - 1] + Vector2.Distance(flat[k - 1], flat[k]);
                    var raw = flat.Select((point, k) => new Vector3(point.X, float.Lerp(yStart, yEnd, lengths[^1] > 0 ? lengths[k] / lengths[^1] : 0), point.Y)).ToList();
                    var candidate = Project(raw, false);
                    if (candidate == null || MinimumRadius(candidate, true) < c.GroundMinimumRadius * .95f || !Valid(candidate)) continue;
                    if (!Pass(previous, candidate[0]) || !Pass(candidate[^1], q[j + 1])) continue;
                    return candidate;
                }
            return null;
        }
        // 同じ向きに短い辺で続く角を先に大回りの曲線へ置き換える。置き換えた点の添字を返す（後の角の円弧の段では触らない）。
        (List<Vector3> Path, HashSet<int> CurvePoints) Swing(List<Vector3> p)
        {
            var curvePoints = new HashSet<int>();
            if (fly || !c.SwingCorners || ground == null || p.Count < 4) return (p, curvePoints);
            var output = new List<Vector3> { p[0] };
            var index = 1;
            while (index + 1 < p.Count)
            {
                var first = TurnDegrees(p, index);
                var last = index; var total = first;
                while (MathF.Abs(first) >= 3 && last + 2 < p.Count && MathF.Sign(TurnDegrees(p, last + 1)) == MathF.Sign(first)
                    && MathF.Abs(TurnDegrees(p, last + 1)) >= 3 && Vector2.Distance(Flat(p[last]), Flat(p[last + 1])) < c.SwingChainLeg)
                { last++; total += TurnDegrees(p, last); }
                var curve = last > index && MathF.Abs(total) is >= 30 and < 170 ? SwingCurve(p, index, last, total, output[^1]) : null;
                if (curve == null) { output.Add(p[index]); index++; continue; }
                foreach (var point in curve) { curvePoints.Add(output.Count); output.Add(point); }
                index = last + 1;
            }
            output.Add(p[^1]);
            return (output, curvePoints);
        }
        // 壁から離す前の形（近道・大回り・円弧・高さ）と、離した後の形を返す。離せない・離さない設定なら同じ形を2つ返す。
        (List<Vector3> Plain, List<Vector3> Walls) Refine(List<Vector3> source)
        {
            var p = source;
            if (c.Shortcuts)
            {
                var shortcut = new List<Vector3> { p[0] };
                for (var i = 0; i + 1 < p.Count;)
                {
                    var end = i + 1;
                    var from = i;
                    Prefetch(Enumerable.Range(i + 2, Math.Max(0, p.Count - i - 2)).Select(j => (p[from], p[j])));
                    for (var j = p.Count - 1; j > i + 1; j--) if (Clear(p[i], p[j])) { end = j; break; }
                    shortcut.Add(p[end]); i = end;
                }
                p = shortcut;
            }
            var (swung, curvePoints) = Swing(p);
            p = swung;
            var rounded = new List<Vector3> { p[0] };
            for (var i = 1; i + 1 < p.Count; i++)
            {
                if (curvePoints.Contains(i)) { rounded.Add(p[i]); continue; }
                var incoming = p[i] - p[i - 1]; var outgoing = p[i + 1] - p[i];
                if (!fly) { incoming.Y = 0; outgoing.Y = 0; }
                var before = incoming.Length(); var after = outgoing.Length();
                if (before < .001 || after < .001) { rounded.Add(p[i]); continue; }
                var u = incoming / before; var v = outgoing / after;
                var angle = MathF.Acos(Math.Clamp(Vector3.Dot(u, v), -1, 1));
                if (angle < .01 || angle > MathF.PI - .01) { rounded.Add(p[i]); continue; }
                var minimum = fly ? c.FlightMinimumRadius : c.GroundMinimumRadius;
                var preferred = fly ? c.FlightPreferredRadius : c.GroundPreferredRadius;
                var tangent = MathF.Tan(angle / 2);
                var maximum = Math.Min(preferred, Math.Min(before, after) * .49f / tangent);
                List<Vector3>? Candidate(float radius)
                {
                    var trim = radius * tangent;
                    var entry = p[i] - u * trim; var exit = p[i] + v * trim;
                    if (!fly)
                    {
                        entry.Y = float.Lerp(p[i].Y, p[i - 1].Y, trim / before);
                        exit.Y = float.Lerp(p[i].Y, p[i + 1].Y, trim / after);
                    }
                    var raw = c.CornerStyle == CornerStyle.Continuous
                        ? ContinuousCorner(entry, exit, u, v, trim, c.Spacing)
                        : CircularCorner(entry, exit, u, v, radius, angle, c.Spacing);
                    var candidate = Project(raw, false);
                    if (candidate == null || MinimumRadius(candidate, !fly) < minimum * .95f) return null;
                    Prefetch(Lines(candidate).Append((rounded[^1], candidate[0])).Append((candidate[^1], p[i + 1])).Where(line => !OfficialLine(line.Item1, line.Item2)));
                    if (!Valid(candidate)) return null;
                    if (!Pass(rounded[^1], candidate[0]) || !Pass(candidate[^1], p[i + 1])) return null;
                    return candidate;
                }
                List<Vector3>? best = null; var bestRadius = 0f;
                if (maximum >= minimum)
                {
                    best = Candidate(maximum); bestRadius = maximum;
                    if (best == null)
                    {
                        var lo = minimum; var hi = maximum;
                        best = Candidate(lo); bestRadius = lo;
                        if (best != null) for (var iteration = 0; iteration < c.RadiusIterations; iteration++)
                        {
                            var mid = (lo + hi) / 2; var trial = Candidate(mid);
                            if (trial == null) hi = mid; else { lo = mid; best = trial; bestRadius = mid; }
                        }
                    }
                }
                // 地上で、普通の円弧が急な曲がりと数えない半径に届かない単独の角は、外へ膨らむ大回りを試す。
                if (!fly && c.SwingCorners && ground != null && (best == null || bestRadius < c.SwingRadiusTarget) && angle >= MathF.PI / 6
                    && SwingCurve(p, i, i, TurnDegrees(p, i), rounded[^1]) is { } swing) best = swing;
                if (best == null) rounded.Add(p[i]); else rounded.AddRange(best);
            }
            if (p.Count > 1) rounded.Add(p[^1]);
            p = rounded;
            if (fly && p.Count > 2)
            {
                // 水平弧長に沿った単調な高さ候補。障害物があれば元の高さを保持する。
                var h = new double[p.Count]; for (var i = 1; i < p.Count; i++) h[i] = h[i - 1] + WindowMeasurement.HorizontalDistance(p[i - 1], p[i]);
                if (h[^1] > .01)
                {
                    var height = p.Select((point, i) => point with { Y = float.Lerp(p[0].Y, p[^1].Y, (float)(h[i] / h[^1])) }).ToList();
                    var slope = Math.Atan2(Math.Abs(p[^1].Y - p[0].Y), h[^1]) * 180 / Math.PI;
                    if (slope <= Math.Min(c.MaximumSlopeDegrees, Math.Min(c.TakeoffDegrees, c.LandingDegrees)) && Valid(height)) p = height;
                }
            }
            var plain = p;
            if (!fly && ground != null && c.MoveAwayFromWalls)
            {
                // 区間ごとに細かく分けて面へ載せる。面へ載せられない点を含む区間（公式が届かない目的地へ足した直線など）は
                // 分けずに元のまま残し、両端も動かさない（元の区間と同じ線なので、窓口の答えも元と同じ）。
                var spacing = Math.Max(c.Spacing, .5f);
                var pieces = Enumerable.Range(1, p.Count - 1).Sum(i => Math.Max(1, (int)Math.Ceiling(Vector3.Distance(p[i - 1], p[i]) / spacing)));
                var onSurface = new List<Vector3> { p[0] }; var anchoredList = new List<bool> { true };
                for (var i = 1; i < p.Count; i++)
                {
                    var n = pieces + 1 > c.MaximumPoints ? 1 : Math.Max(1, (int)Math.Ceiling(Vector3.Distance(p[i - 1], p[i]) / spacing));
                    var inner = new List<Vector3>();
                    for (var j = 1; j < n; j++)
                    {
                        if (ground.Project(Vector3.Lerp(p[i - 1], p[i], j / (float)n)) is not { } on) { inner = null; break; }
                        inner.Add(on);
                    }
                    var end = i == p.Count - 1 ? p[i] : ground.Project(p[i]);
                    if (inner == null || end == null) { anchoredList[^1] = true; onSurface.Add(p[i]); anchoredList.Add(true); continue; }
                    onSurface.AddRange(inner); anchoredList.AddRange(inner.Select(_ => false));
                    onSurface.Add(end.Value); anchoredList.Add(i == p.Count - 1);
                }
                var anchored = anchoredList.ToArray();
                if (Valid(onSurface))
                {
                    // 壁から離す量を点ごとに決め、前後 WallSmoothingMeters の範囲でならしてから、両端を WallRampMeters のS字で絞る。
                    // 点ごとにずらすと、ずらし始めに小さな折れができて向きが急に変わる（H1）。絞ってからならすと、端の隣で急に折れる。
                    var step = Math.Max(c.Spacing, .5f);
                    var desired = new Vector3[onSurface.Count];
                    for (var i = 1; i + 1 < onSurface.Count; i++)
                    {
                        if (anchored[i]) continue;
                        var point = ground.Point(onSurface[i]);
                        if (point.WallDistance is not { } wall || wall >= c.WallMargin || point.AwayFromWall is not { } away) continue;
                        var flat = away with { Y = 0 };
                        if (flat.LengthSquared() < .01f) continue;
                        desired[i] = Vector3.Normalize(flat) * Math.Min(.5f, c.WallMargin - wall);
                    }
                    var half = Math.Max(1, (int)MathF.Round(c.WallSmoothingMeters / step));
                    var moved = onSurface.ToList();
                    for (var i = 1; i + 1 < moved.Count; i++)
                    {
                        Vector3 sum = default; float weights = 0;
                        for (var k = -half; k <= half; k++)
                        {
                            var j = i + k;
                            if (j <= 0 || j >= desired.Length - 1) continue;
                            var weight = half + 1 - Math.Abs(k); sum += desired[j] * weight; weights += weight;
                        }
                        var ramp = Math.Clamp(Math.Min(Vector3.Distance(onSurface[i], onSurface[0]), Vector3.Distance(onSurface[i], onSurface[^1])) / c.WallRampMeters, 0, 1);
                        var offset = weights > 0 ? sum / weights * (ramp * ramp * (3 - 2 * ramp)) : default;
                        if (anchored[i] || offset.LengthSquared() < 1e-6f) continue;
                        var target = ground.Project(onSurface[i] + offset);
                        if (target.HasValue && Clear(moved[i - 1], target.Value)) moved[i] = target.Value;
                    }
                    if (Valid(moved)) p = moved;
                }
            }
            return (plain, p);
        }
        if (original.Length > 0) { used.Add(original[0]); proposed.Add(original[0]); }
        // 元へ戻す境界も含める。近道単体では消えていた急角が、次の元区間との間に現れることがある。
        // 前後の点は、窓（地上 2m・飛行 8m）の 2 倍の長さに届くまで含める。1 点だけだと、短い区間の先の角の向きの変化を測れない。
        var contextLength = 2 * (fly ? new MeasurementSettings().FlightWindow : new MeasurementSettings().GroundWindow);
        Vector3[] Before()
        {
            var points = new List<Vector3>(); var length = 0f;
            for (var k = used.Count - 2; k >= 0 && length < contextLength; k--) { length += Vector3.Distance(used[k], used[k + 1]); points.Add(used[k]); }
            points.Reverse(); return [.. points];
        }
        Vector3[] After(int end)
        {
            var points = new List<Vector3>(); var length = 0f;
            for (var k = end + 1; k < original.Length && length < contextLength; k++) { length += Vector3.Distance(original[k - 1], original[k]); points.Add(original[k]); }
            return [.. points];
        }
        string? JudgeConnection(List<Vector3> source, List<Vector3> candidate, int end) => Judge(source, candidate, Before(), After(end), fly, ground, c);
        // 打ち切った後の区間は、窓口へ頼まずに公式のまま残す。
        void Keep(int start, int end, string reason)
        {
            var kept = original[start..(end + 1)];
            decisions.Add(new(start, end, used.Count - 1, used.Count - 1 + kept.Length - 1, false, reason));
            proposed.AddRange(kept.Skip(1)); used.AddRange(kept.Skip(1));
        }
        string? Reject(List<Vector3> source, List<Vector3> candidate, int end)
        {
            if (new ArcPath(candidate).Length > new ArcPath(source).Length * c.MaximumLengthRatio + 1e-4) return "長さの比が上限を超えたため公式へ差し戻し";
            if (fly && !candidate.SequenceEqual(source) && !SlopesWithinLimits(candidate,c)) return "上昇・降下角度の設定を超えたため公式へ差し戻し";
            if (JudgeConnection(source, candidate, end) is { } judged) return judged;
            if (FollowerSimulation.Profiles(fly).Any(profile => FollowerSimulation.Run(candidate, fly, profile, false) is { Completed: false } or { ReverseEvents: > 0 } or { MissedPoints: > 0 }))
                return "追従の逆戻り・取りこぼし・上限により公式へ差し戻し";
            return null;
        }
        // 並び全体が落ちたら、まず窓口が通さない公式の区間で分ける（以前の分け方。その区間は公式のまま残し、両端の角は丸めない）。
        // 全体で試す方が、落ちた区間の両側の角まで丸められるが、全体が落ちたときに半分に分けると、以前は丸められた角が分け目に来ることがある。
        // 分けられる区間が無ければ false（呼び出し側が半分に分ける）。
        bool SplitAtBlocked(int start, int end)
        {
            if (end - start < 2) return false;
            Prefetch(Enumerable.Range(start, end - start).Select(i => (original[i], original[i + 1])));
            var blocked = Enumerable.Range(start, end - start).Where(i => !Clear(original[i], original[i + 1])).ToList();
            if (blocked.Count == 0) return false;
            splitUsed = true;
            var runStart = start;
            foreach (var i in blocked)
            {
                if (i > runStart) Process(runStart, i);
                var reasons = Enumerable.Range(0, all.Count).Select(k => all[k].Name + ":" + Check(k, original[i], original[i + 1]).Verdict + " " + Check(k, original[i], original[i + 1]).Reason);
                Keep(i, i + 1, "公式区間を保持：" + string.Join("／", reasons));
                runStart = i + 1;
            }
            if (runStart < end) Process(runStart, end);
            return true;
        }
        void Process(int start, int end)
        {
            if (cancel.IsCancellationRequested) { Keep(start, end, "打ち切りのため公式区間を保持"); return; }
            var source = original[start..(end + 1)].ToList(); var reason = "検査済みの近道・曲線・高さ・壁余裕";
            List<Vector3> candidate; List<Vector3>? plain = null;
            try
            {
                (plain, candidate) = Refine(source);
                // 壁から離す段は、離す前の形と比べて単独で確かめる。こぶ・跳び・小半径を増やす、または壁沿いが減らないなら離す前の形を使う。
                // 並び全体で比べると、角を丸めて良くなった分が、離して作ったこぶを打ち消してしまう。
                if (!candidate.SequenceEqual(plain) && Judge(plain, candidate, Before(), After(end), fly, ground, c) is { } wallWhy)
                { candidate = plain; reason = "検査済みの近道・曲線・高さ（壁から離す形は差し戻し：" + wallWhy + "）"; }
            }
            catch (Exception ex) { candidate = source; reason = "加工例外で公式区間を保持: " + ex.GetType().Name; }
            if (start == 0 && end == original.Length - 1) rootProposal = candidate.ToArray();
            if (candidate[0] != source[0] || candidate[^1] != source[^1] || !Valid(candidate))
            {
                if (blockedFirst && queries < c.MaximumQueries && SplitAtBlocked(start, end)) return;
                if (end - start > 1 && queries < c.MaximumQueries)
                {
                    var middle = (start + end) / 2; Process(start, middle); Process(middle, end); return;
                }
                var reasons = Enumerable.Range(0, all.Count).Select(k => all[k].Name + ":" + Check(k, source[0], source[^1]).Verdict + " " + Check(k, source[0], source[^1]).Reason);
                candidate = source; reason = "公式区間を保持：" + string.Join("／", reasons);
            }
            else if (Reject(source, candidate, end) is { } rejected)
            {
                // 壁から離した形が全体の条件で落ちたら、離す前の形でもう一度確かめる。
                if (plain != null && !plain.SequenceEqual(source) && !plain.SequenceEqual(candidate) && plain[0] == source[0] && plain[^1] == source[^1]
                    && Valid(plain) && Reject(source, plain, end) == null)
                { candidate = plain; reason = "検査済みの近道・曲線・高さ（壁から離す形は差し戻し：" + rejected + "）"; }
                else if (blockedFirst && queries < c.MaximumQueries && SplitAtBlocked(start, end)) return;
                // 衝突以外の理由で落ちた並びは、内側に角が残るように半分に分けて作り直す（追補-5）。分けた境目の角は丸めない。
                // どの物差しも良くならない並びは、分けても良くならないので分けない。
                else if (end - start >= 3 && queries < c.MaximumQueries && !rejected.StartsWith("物差しが良くならない"))
                {
                    var middle = (start + end) / 2; Process(start, middle); Process(middle, end); return;
                }
                else { candidate = source; reason = rejected; }
            }
            var changed = !candidate.SequenceEqual(source);
            if (!changed && reason.StartsWith("検査済み")) reason = "検査済み・形を変えられない区間（半径・空間の制約）";
            decisions.Add(new(start, end, used.Count - 1, used.Count - 1 + candidate.Count - 1, changed, reason));
            proposed.AddRange(candidate.Skip(1)); used.AddRange(candidate.Skip(1));
        }
        // 公式の区間は公式と同じ線なので、窓口が通さない区間があっても並びを分けない（その両側の角も丸める）。
        // 以前は落ちた区間を公式のまま残し、両端の角を丸めなかった（飛行の角の 8 割がこれで曲線にならなかった）。
        // 全体で試して落ちたら、まず通さない公式の区間で分ける（以前の分け方より悪くしない）。そう分けたときだけ、半分に分けるのを先にして
        // もう一度作り、1回目より悪くならずどれかが良くなるなら2回目を使う（全体で試す良さを、分けた所でも生かすため）。
        CurveResult Collect()
        {
            var checks = new List<RouteCheck>();
            Prefetch(Lines(used));
            foreach (var decision in decisions) for (var i = decision.UsedStart; i < decision.UsedEnd; i++)
                for (var k = 0; k < all.Count; k++) checks.Add(new(i, all[k].Name, Check(k, used[i], used[i + 1]), decision.Changed));
            return new(original, rootProposal ?? proposed.ToArray(), used.ToArray(), decisions.ToArray(), checks.ToArray(), timer.Elapsed.TotalSeconds, queries);
        }
        if (original.Length > 1) Process(0, original.Length - 1);
        var first = Collect();
        if (c.SplitOrder != SplitOrder.Both || !splitUsed || cancel.IsCancellationRequested) return first;
        used.Clear(); proposed.Clear(); decisions.Clear(); rootProposal = null; blockedFirst = false;
        used.Add(original[0]); proposed.Add(original[0]);
        Process(0, original.Length - 1);
        var second = Collect();
        var better = !cancel.IsCancellationRequested && !second.UsedPath.SequenceEqual(first.UsedPath)
            && Judge(first.UsedPath, second.UsedPath, [], [], fly, ground, c) == null;
        return better ? second with { AddedSeconds = timer.Elapsed.TotalSeconds, Queries = queries }
            : first with { AddedSeconds = timer.Elapsed.TotalSeconds, Queries = queries };
    }
    // 前後の公式の点を含めた窓で、加工の前後を比べる。悪くする変更と、どの物差しも良くならない変更は採らない。
    // 跳び（M4）は、希望半径の円弧に入るときの跳び（測る間隔÷希望半径）までは加工が作ってよい。それより増やす変更は戻す。
    // 良くなったと数えるもの：急な曲がりの数・最大の窓角度（M2）、半径の下限未満（M3）、急傾斜（M5）、壁沿い（M7・地上）、跳びの最大（M4）、長さ（M6）。
    public static string? Judge(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> candidate, IReadOnlyList<Vector3> prefix,
        IReadOnlyList<Vector3> suffix, bool fly, IGroundSurface? ground = null, CurveSettings? settings = null)
    {
        var c = settings ?? new();
        if (candidate.SequenceEqual(source)) return null;
        Vector3[] Around(IReadOnlyList<Vector3> p) => [.. prefix, .. p, .. suffix];
        var before = WindowMeasurement.Measure(Around(source), fly); var after = WindowMeasurement.Measure(Around(candidate), fly);
        if (after.BelowRadiusLength > before.BelowRadiusLength + .25 || after.SteepLength > before.SteepLength + .25)
            return "接続部を含む小半径・急傾斜長が増すため公式へ差し戻し";
        if (after.SharpTurns > before.SharpTurns) return "急な曲がりが増えるため公式へ差し戻し";
        // 円弧は c.Spacing ごとの点で作るので、測る刻みより粗い場合はその刻みで跳びが出る。
        var gentle = Math.Max(new MeasurementSettings().Spacing, c.Spacing) / (fly ? c.FlightPreferredRadius : c.GroundPreferredRadius) * 180 / Math.PI;
        var jumpBefore = WindowMeasurement.JumpMaxOverPhases(Around(source), fly); var jumpAfter = WindowMeasurement.JumpMaxOverPhases(Around(candidate), fly);
        if (jumpAfter > Math.Max(jumpBefore, gentle) + c.JumpToleranceDegrees) return "曲率の跳びの最大が増えるため公式へ差し戻し";
        double wallBefore = 0, wallAfter = 0;
        if (!fly && ground != null)
        {
            wallBefore = GroundMeasurement.Measure(ground, Around(source)).NearWallLength;
            wallAfter = GroundMeasurement.Measure(ground, Around(candidate)).NearWallLength;
            if (wallAfter > wallBefore + .25) return "壁沿いが増えるため公式へ差し戻し";
        }
        var improved = after.SharpTurns < before.SharpTurns || after.MaxWindowDegrees < before.MaxWindowDegrees - c.ImprovementDegrees
            || after.BelowRadiusLength < before.BelowRadiusLength - .25 || after.SteepLength < before.SteepLength - .25
            || wallAfter < wallBefore - .25 || jumpAfter < jumpBefore - c.JumpToleranceDegrees
            || after.Length < before.Length - .25;
        return improved ? null : "物差しが良くならないため公式を保持";
    }
    // 線 a→b が公式の経路のどれか 1 本の区間の上にあるか（両端がその区間から 0.01m 以内）。
    // 地上は水平で比べる（公式の追従は地上で高さを見ない）。飛行は立体で比べる（高さを変えた線は新しい線）。
    public static bool OnOfficialLine(IReadOnlyList<Vector3> official, Vector3 a, Vector3 b, bool fly)
    {
        Vector3 Flat(Vector3 v) => fly ? v : v with { Y = 0 };
        for (var k = 0; k + 1 < official.Count; k++)
            if (FollowerSimulation.DistanceToSegment(Flat(a), Flat(official[k]), Flat(official[k + 1])) <= .01f
                && FollowerSimulation.DistanceToSegment(Flat(b), Flat(official[k]), Flat(official[k + 1])) <= .01f) return true;
        return false;
    }
    public static List<Vector3> CircularCorner(Vector3 entry, Vector3 exit, Vector3 u, Vector3 v, float radius, float angle, float spacing)
    {
        var inward = Vector3.Normalize(v - u * Vector3.Dot(u, v)); var center = entry + inward * radius;
        var axis = Vector3.Normalize(Vector3.Cross(u, v)); var radial = entry - center;
        var count = Math.Max(2, (int)Math.Ceiling(radius * angle / spacing));
        var points = new List<Vector3>();
        for (var i = 0; i <= count; i++)
        {
            var a = angle * i / count;
            points.Add(center + radial * MathF.Cos(a) + Vector3.Cross(axis, radial) * MathF.Sin(a));
        }
        points[0] = entry; points[^1] = exit; return points;
    }
    public static List<Vector3> ContinuousCorner(Vector3 entry, Vector3 exit, Vector3 u, Vector3 v, float trim, float spacing)
    {
        // 5次Bezierの端の二階微分を0にして、直線との曲率の跳びを抑える。
        var arm = trim * .3f;
        Vector3[] control = [entry, entry + u * arm, entry + u * (2 * arm), exit - v * (2 * arm), exit - v * arm, exit];
        var count = Math.Max(4, (int)Math.Ceiling(2 * trim / spacing)); var points = new List<Vector3>();
        for (var i = 0; i <= count; i++)
        {
            var q = control.ToArray(); var t = i / (float)count;
            for (var depth = 5; depth > 0; depth--) for (var j = 0; j < depth; j++) q[j] = Vector3.Lerp(q[j], q[j + 1], t);
            points.Add(q[0]);
        }
        points[0] = entry; points[^1] = exit; return points;
    }
    public static float MinimumRadius(IReadOnlyList<Vector3> p, bool horizontal)
    {
        var radius = float.PositiveInfinity;
        for (var i = 1; i + 1 < p.Count; i++)
        {
            var a = p[i] - p[i - 1]; var b = p[i + 1] - p[i];
            if (horizontal) { a.Y = b.Y = 0; }
            var area = Vector3.Cross(a, b).Length();
            if (area > 1e-8) radius = Math.Min(radius, a.Length() * b.Length() * (a + b).Length() / (2 * area));
        }
        return radius;
    }
    public static bool SlopesWithinLimits(IReadOnlyList<Vector3> path,CurveSettings c)
    {
        for(var i=1;i<path.Count;i++)
        {
            var d=path[i]-path[i-1];if(d.LengthSquared()<1e-10)continue;
            var limit=c.MaximumSlopeDegrees;
            if(i==1 && d.Y>0)limit=Math.Min(limit,c.TakeoffDegrees);
            if(i==path.Count-1 && d.Y<0)limit=Math.Min(limit,c.LandingDegrees);
            if(Math.Atan2(Math.Abs(d.Y),WindowMeasurement.HorizontalDistance(path[i-1],path[i]))*180/Math.PI>limit+1e-4)return false;
        }
        return true;
    }
    private static List<Vector3> Densify(IReadOnlyList<Vector3> p, float spacing, int maximum)
    {
        var result = new List<Vector3> { p[0] };
        for (var i = 1; i < p.Count; i++)
        {
            var n = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(p[i - 1], p[i]) / spacing));
            if (result.Count + n > maximum) return p.ToList();
            for (var j = 1; j <= n; j++) result.Add(Vector3.Lerp(p[i - 1], p[i], j / (float)n));
        }
        return result;
    }
}
