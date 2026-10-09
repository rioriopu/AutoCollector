using System;
using System.Collections.Generic;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;

namespace AutoCollector.Automation;

/// <summary>
/// vnavmesh が返した経路の、曲がり角だけを弧で丸める。
///
/// <b>経路探索はしない。</b>公式 vnavmesh が返した折れ線を受け取り、
/// 角を置き換えて返すだけ。どこを通るかを決めるのは公式の仕事で、
/// ここがやるのは「同じ道を、人がやるように曲がる」ことだけ。
///
/// 【なぜ必要か】
///
/// vnavmesh の経路は、ナビメッシュのポリゴンの境目を結んだ折れ線。
/// 追従する FollowPath は「次の点へ向かって直進する」だけなので、
/// 折れ線の角がそのまま動きの角になる。角で一度ほぼ止まり、
/// 向きを変えてまた走り出すため、見るからに機械的になる。
///
/// 公式はここを滑らかにしない。つまり公式に足りない差分であり、
/// こちらで補ってよい領域にあたる。
///
/// 【手順】
///
/// <list type="number">
/// <item>間引く — 直線の途中に並ぶ点を落とす（Ramer–Douglas–Peucker）。
///       これをやらないと、角でない場所まで丸めて経路全体がうねる</item>
/// <item>丸める — 各頂点を二次ベジェの弧に置き換える</item>
/// <item>検査する — <b>角を丸めるとは内側へ切り込むこと</b>。切り込んだ先が
///       壁の中かもしれない。メッシュに乗っていない点を含む弧は、
///       まるごと捨てて元の角に戻す。ここを省くと壁へ突っ込む</item>
/// </list>
/// </summary>
public sealed class PathSmoother(AnomalyLog anomalyLog, VnavmeshIpc vnavmesh)
{
    /// <summary>
    /// これ以上まっすぐなら丸めない（度）。180 が直進。
    ///
    /// ほぼ直進の点まで弧にすると、点が増えるだけで見た目は変わらない。
    /// </summary>
    private const float StraightEnoughDegrees = 160f;

    /// <summary>この半径を下回るなら、丸めても見えないので角のまま使う。</summary>
    private const float MinRadiusMeters = 0.6f;

    /// <summary>
    /// 弧をいくつの点で表すか。
    ///
    /// 増やすと滑らかになるが、点が増えて追従が重くなる。
    /// 3 点（入口・出口を含めて 5 点）で、走っている速さなら十分滑らかに見える。
    /// </summary>
    private const int ArcPoints = 3;

    /// <summary>
    /// 弧の半径を、隣の区間の長さの何割まで許すか。
    ///
    /// 半分にすると隣り合う弧が接してしまい、区間によっては重なる。
    /// 少し内側に取る。
    /// </summary>
    private const float SegmentShare = 0.45f;

    /// <summary>近すぎる点は落とす。詰まりすぎると追従が震える。</summary>
    private const float MinPointGapMeters = 0.1f;

    /// <summary>
    /// 丸める角の上限。
    ///
    /// 遠くの角は、着く前に経路を引き直すことがほとんどで、
    /// いま丸めても無駄になる。手前から数えて、この数だけにする。
    /// </summary>
    private const int MaxRoundedCorners = 24;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly VnavmeshIpc vnavmesh = vnavmesh;

    /// <summary>
    /// 経路の角を丸める。
    ///
    /// <b>丸められなければ null を返す。</b>呼び出し側は、そのときは
    /// 元の経路（公式に任せたまま）で進めること。失敗を失敗として扱わない。
    /// </summary>
    /// <param name="path">vnavmesh が返した経路。</param>
    /// <param name="epsilon">間引きの許容（ヤルム）。</param>
    /// <param name="maxRadius">弧の最大半径（ヤルム）。</param>
    /// <param name="validateOnMesh">
    /// 作った点がナビメッシュに乗っているかを確かめるか。
    ///
    /// <b>地上の経路では必ず true にする。</b>
    /// 空中の経路では false にするしかない。vnavmesh が公開している
    /// 問い合わせは <c>Query.Mesh.*</c> だけで、<b>空中が空いているかを
    /// 聞く口が無い</b>（実ソースで確認: vnavmesh/IPCProvider.cs:35-39）。
    /// 乗っていないのが当たり前の空中の点を地上の判定にかけると、
    /// すべて外れて 1 つも丸まらない。
    /// </param>
    public List<Vector3>? Smooth(List<Vector3>? path, float epsilon, float maxRadius, bool validateOnMesh)
    {
        if (path is null || path.Count < 3)
        {
            // 角が無い。丸めるものが無い。
            return null;
        }

        var simplified = Simplify(path, MathF.Max(0.05f, epsilon));

        if (simplified.Count < 3)
        {
            return null;
        }

        var result = new List<Vector3>(simplified.Count * (ArcPoints + 2)) { simplified[0] };
        var rounded = 0;
        var rejected = 0;

        for (var i = 1; i < simplified.Count - 1; i++)
        {
            var a = simplified[i - 1];
            var b = simplified[i];
            var c = simplified[i + 1];

            if (rounded >= MaxRoundedCorners || !TryBuildArc(a, b, c, maxRadius, out var arc))
            {
                result.Add(b);
                continue;
            }

            // **検査してから採る。** 切り込んだ先が壁の中かもしれない。
            if (validateOnMesh && !this.IsArcOnMesh(arc))
            {
                rejected++;
                result.Add(b);
                continue;
            }

            rounded++;
            result.AddRange(arc);
        }

        result.Add(simplified[^1]);

        if (rounded == 0)
        {
            // 1 つも丸められなかった。元のままでよい。
            return null;
        }

        Deduplicate(result);

        // **丸めたときは必ず記録する。**
        //
        // 以前は弾かれた角があるときしか書いていなかった。そのため
        // 「効いているのに見た目が変わらない」のか「そもそも効いていない」のかを
        // 区別できなかった（2026-10-09 実機で、どちらか分からず切り分けに困った）。
        this.anomalyLog.Info(
            "Navigation",
            $"経路の角を {rounded} 箇所丸めました" +
            (rejected > 0 ? $"（{rejected} 箇所はメッシュから外れるため角のまま）" : string.Empty) +
            $" {simplified.Count} 点 → {result.Count} 点" +
            (validateOnMesh ? string.Empty : "・空中のため検査なし"));

        return result;
    }

    // ---- 部品 ----

    /// <summary>
    /// 1 つの角から弧を作る。
    ///
    /// 入口と出口は、角から隣の点へ向かって半径ぶん戻った位置に置く。
    /// その 2 点と角を制御点にした二次ベジェが弧になる。
    /// </summary>
    private static bool TryBuildArc(Vector3 a, Vector3 b, Vector3 c, float maxRadius, out List<Vector3> arc)
    {
        arc = [];

        var toA = Flat(a - b);
        var toC = Flat(c - b);

        var lenA = toA.Length();
        var lenC = toC.Length();

        if (lenA < 0.01f || lenC < 0.01f)
        {
            return false;
        }

        // 曲がりの角さ。180 度が直進。
        var cosTurn = Math.Clamp(Vector2.Dot(toA / lenA, toC / lenC), -1f, 1f);
        var turnDegrees = MathF.Acos(cosTurn) * 180f / MathF.PI;

        if (turnDegrees >= StraightEnoughDegrees)
        {
            return false;
        }

        // 隣り合う弧が重ならないよう、短いほうの区間に合わせて抑える。
        var radius = MathF.Min(maxRadius, MathF.Min(lenA, lenC) * SegmentShare);

        if (radius < MinRadiusMeters)
        {
            return false;
        }

        // 入口と出口は、もとの区間の上に置く。区間上の点なので高さもそのまま乗る。
        var entry = Lerp(b, a, radius / lenA);
        var exit = Lerp(b, c, radius / lenC);

        arc.Add(entry);

        for (var k = 1; k <= ArcPoints; k++)
        {
            var t = k / (float)(ArcPoints + 1);

            // **水平だけをベジェで曲げ、高さは直線で結ぶ。**
            // 高さまで曲げると、階段や坂で浮いたり沈んだりする。
            var xz = Bezier(
                new Vector2(entry.X, entry.Z),
                new Vector2(b.X, b.Z),
                new Vector2(exit.X, exit.Z),
                t);

            arc.Add(new Vector3(xz.X, entry.Y + ((exit.Y - entry.Y) * t), xz.Y));
        }

        arc.Add(exit);
        return true;
    }

    /// <summary>
    /// 弧の点がすべてナビメッシュに乗っているか。
    ///
    /// <b>読めなかったときは「乗っていない」に倒す。</b>
    /// <see cref="VnavmeshIpc.TryIsPointOnMesh"/> は IPC が失敗しても false を返す。
    /// 答えが得られないことを「乗っている」と読むと、確かめずに壁へ向かうことになる。
    /// </summary>
    private bool IsArcOnMesh(List<Vector3> arc)
    {
        foreach (var point in arc)
        {
            // allowUnreachable=false で、乗ってはいるが辿り着けない点も弾く。
            if (!this.vnavmesh.TryIsPointOnMesh(point, 2f, false, out var onMesh) || !onMesh)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 直線の途中に並ぶ点を落とす（Ramer–Douglas–Peucker）。
    ///
    /// <b>水平だけで測る。</b>高さを混ぜると、坂の途中の点が
    /// 「折れている」と判定されて残り続ける。
    ///
    /// 再帰ではなく自前の積みで書く。経路が長いときに深くなりすぎないようにするため。
    /// </summary>
    private static List<Vector3> Simplify(List<Vector3> path, float epsilon)
    {
        var keep = new bool[path.Count];
        keep[0] = true;
        keep[^1] = true;

        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, path.Count - 1));

        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();

            if (last <= first + 1)
            {
                continue;
            }

            var worst = 0f;
            var worstIndex = -1;

            for (var i = first + 1; i < last; i++)
            {
                var d = FlatDistanceToSegment(path[i], path[first], path[last]);

                if (d > worst)
                {
                    worst = d;
                    worstIndex = i;
                }
            }

            if (worstIndex < 0 || worst <= epsilon)
            {
                continue;
            }

            keep[worstIndex] = true;
            stack.Push((first, worstIndex));
            stack.Push((worstIndex, last));
        }

        var result = new List<Vector3>(path.Count);

        for (var i = 0; i < path.Count; i++)
        {
            if (keep[i])
            {
                result.Add(path[i]);
            }
        }

        return result;
    }

    /// <summary>近すぎる点を落とす。先頭と末尾は必ず残す。</summary>
    private static void Deduplicate(List<Vector3> points)
    {
        for (var i = points.Count - 2; i >= 1; i--)
        {
            if (Vector3.Distance(points[i], points[i + 1]) < MinPointGapMeters)
            {
                points.RemoveAt(i);
            }
        }
    }

    /// <summary>点から線分までの水平距離。</summary>
    private static float FlatDistanceToSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        var p = new Vector2(point.X, point.Z);
        var a = new Vector2(from.X, from.Z);
        var b = new Vector2(to.X, to.Z);

        var ab = b - a;
        var lengthSquared = ab.LengthSquared();

        if (lengthSquared < 1e-6f)
        {
            return Vector2.Distance(p, a);
        }

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(p, a + (ab * t));
    }

    private static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        var u = 1f - t;
        return (u * u * p0) + (2f * u * t * p1) + (t * t * p2);
    }

    /// <summary><paramref name="from"/> から <paramref name="to"/> へ割合ぶん進んだ点。</summary>
    private static Vector3 Lerp(Vector3 from, Vector3 to, float t)
        => from + ((to - from) * Math.Clamp(t, 0f, 1f));

    /// <summary>高さを落とした向き。曲がりの角さは水平で測る。</summary>
    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);
}
