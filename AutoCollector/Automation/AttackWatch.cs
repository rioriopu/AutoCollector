using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoCollector.Automation;

/// <summary>
/// 狙った敵へ攻撃を始められない時間を数え、迂回へ移るかを決める（ゲームに触らない）。
///
/// <b>利用者の要件（2026-10-10）：</b>狙った敵との間に岩などがあって進めず、
/// 4 秒以内に攻撃を始められない（近接職は近接攻撃ができない）ときは、
/// 狙っている敵を目標にメッシュで経路を引き、迂回して近づく。
///
/// 「攻撃を始められるか」は <see cref="Game.AttackReach"/> が決める（射程・視線・RSR の視線）。
/// ここは時間を数えるだけ。届いた瞬間に数え直す。狙う相手が替わっても数え直す。
/// </summary>
public sealed class AttackWatch
{
    /// <summary>攻撃を始められないまま、これだけ経ったら迂回する（利用者の指定）。</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(4);

    private ulong target;
    private DateTime blockedSinceUtc;

    /// <summary>迂回している最中か。届くまで続ける。</summary>
    public bool Detouring { get; private set; }

    /// <summary>いま試している迂回の行き先の番号（<see cref="DetourGoals.Around"/> の並び）。</summary>
    public int GoalIndex { get; private set; }

    /// <summary>
    /// 毎フレーム呼ぶ。
    /// </summary>
    /// <param name="targetId">狙っている敵。</param>
    /// <param name="canAttack">いま攻撃が届くか。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>迂回すべきなら true。</returns>
    public bool Update(ulong targetId, bool canAttack, DateTime now)
    {
        if (targetId != this.target)
        {
            this.target = targetId;
            this.blockedSinceUtc = now;
            this.Detouring = false;
            this.GoalIndex = 0;
        }

        if (canAttack)
        {
            this.blockedSinceUtc = now;
            this.Detouring = false;
            this.GoalIndex = 0;
            return false;
        }

        if (!this.Detouring && now - this.blockedSinceUtc >= Patience)
        {
            this.Detouring = true;
            this.GoalIndex = 0;
        }

        return this.Detouring;
    }

    /// <summary>攻撃を始められないまま経った時間。</summary>
    public TimeSpan BlockedFor(DateTime now) => this.target == 0 ? TimeSpan.Zero : now - this.blockedSinceUtc;

    /// <summary>
    /// いまの行き先では届かなかった。次の行き先へ。
    ///
    /// <b>使い切っても諦めない。</b>最初の行き先（敵の位置）へ戻ってやり直す。
    /// 敵も自分も動くので、さっき駄目だった行き先が次は通ることがある（失敗は止めずに立て直す）。
    /// </summary>
    public void NextGoal(int count) => this.GoalIndex = count <= 0 ? 0 : (this.GoalIndex + 1) % count;

    /// <summary>忘れる。狙いを外したとき・戦闘を抜けたときに呼ぶ。</summary>
    public void Reset()
    {
        this.target = 0;
        this.Detouring = false;
        this.GoalIndex = 0;
    }
}

/// <summary>迂回の行き先の候補を作る（ゲームに触らない）。</summary>
public static class DetourGoals
{
    /// <summary>
    /// 0 番は敵の位置そのもの。あとは敵の周り 8 方向の点で、自分に近い側から順に並べる。
    ///
    /// <b>敵の位置を先に試す。</b>メッシュの経路は岩を避けて引かれるので、たいていはこれで回り込める。
    /// 敵がメッシュの外（岩の上など）に立っていて経路が手前で切れるときに、周りの点を順に試す。
    /// 点はメッシュに載っているとは限らない。呼び出し側がメッシュの上の行ける点へ寄せてから使う。
    /// </summary>
    /// <param name="player">自分の位置。</param>
    /// <param name="target">敵の位置。</param>
    /// <param name="ring">周りの点を置く、敵からの水平距離。</param>
    public static List<Vector3> Around(Vector3 player, Vector3 target, float ring)
    {
        var goals = new List<Vector3>(9) { target };
        var facing = MathF.Atan2(player.Z - target.Z, player.X - target.X);

        if (float.IsNaN(facing))
        {
            facing = 0;
        }

        foreach (var step in new[] { 0, 1, -1, 2, -2, 3, -3, 4 })
        {
            var angle = facing + (step * MathF.PI / 4f);
            goals.Add(new Vector3(target.X + (ring * MathF.Cos(angle)), target.Y, target.Z + (ring * MathF.Sin(angle))));
        }

        return goals;
    }
}
