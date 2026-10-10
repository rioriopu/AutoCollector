using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoCollector.Automation;

/// <summary>
/// FATE へ飛んで入るとき、外周上空への経路と降下の経路を 1 本につなぐ（ゲームに触らない）。
///
/// <b>利用者の要件（2026-10-10）：地上の曲線の動きを空中にも。</b>
/// 以前の進入は「真上へ離陸 → 止まって外周上空への経路を求める → 外周上空で止まって降下の経路を求める」で、
/// 段の境目ごとに止まって向きを変えていた（2026-10-10 12:39〜12:55 の記録：離陸後に 0.1〜0.55 秒、外周で 0.1〜0.18 秒止まる）。
/// 経路を 1 本につなげば、外周の角も曲線に整える対象になり、止まらずに降下へ移れる。
/// </summary>
public static class FlightJoin
{
    /// <summary>
    /// 外周上空への経路と、外周上空から着地点への経路をつなぐ。
    /// 降下の経路の先頭は外周上空の点そのもの（探索の出発点）なので、外周への経路の末尾と近ければ重ねない。
    /// </summary>
    /// <param name="toEntry">出発点から外周上空への経路（出発点つき）。</param>
    /// <param name="descent">外周上空から着地点への経路。null ならつながない。</param>
    public static List<Vector3> Join(IReadOnlyList<Vector3> toEntry, IReadOnlyList<Vector3>? descent)
    {
        var joined = new List<Vector3>(toEntry.Count + (descent?.Count ?? 0));
        joined.AddRange(toEntry);

        if (descent is null)
        {
            return joined;
        }

        for (var i = 0; i < descent.Count; i++)
        {
            if (i == 0 && joined.Count > 0 && Vector3.Distance(joined[^1], descent[0]) < 0.5f)
            {
                continue;
            }

            joined.Add(descent[i]);
        }

        return joined;
    }

    /// <summary>
    /// つないだ経路で、外周を越えて降下へ入ったか。
    ///
    /// <b>外周点の近くを通るとは限らない。</b>角を曲線に整えると、外周点の内側を回って降下へ入る。
    /// 「着地点までの水平距離が、外周点から着地点までより短くなった」を越えた印にする。
    /// </summary>
    public static bool PassedEntry(Vector3 player, Vector3 entry, Vector3 landing, float margin = 1f)
        => Flat(player, landing) <= Flat(entry, landing) - margin;

    /// <summary>
    /// 離陸した所から辿らせる経路にする。
    ///
    /// 経路は離陸の合図の点（離陸した所の真上 5m）から求めてある。その点がもうすぐそばなら飛ばして、
    /// 真上へ上がりきってから向きを変える動きをなくす（2 点目へそのまま斜めに上がる）。
    /// </summary>
    /// <param name="plan">出発点つきの経路。</param>
    /// <param name="player">いまの位置。</param>
    /// <param name="skipWithin">先頭をこの距離の内なら飛ばす。</param>
    public static List<Vector3> FromHere(IReadOnlyList<Vector3> plan, Vector3 player, float skipWithin)
    {
        var start = plan.Count > 1 && Vector3.Distance(plan[0], player) <= skipWithin ? 1 : 0;
        var result = new List<Vector3>(plan.Count - start);
        for (var i = start; i < plan.Count; i++)
        {
            result.Add(plan[i]);
        }

        return result;
    }

    private static float Flat(Vector3 a, Vector3 b)
        => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
}
