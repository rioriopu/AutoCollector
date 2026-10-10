using System.Collections.Generic;
using System.Linq;

namespace AutoCollector.Game;

/// <summary>
/// 狙いを決めるときに見る、1 体ぶんの値。ゲームから読んだ値の写しで、ゲームには触らない。
/// </summary>
/// <param name="Id">GameObjectId。</param>
/// <param name="NameId">名前の番号（BNpcName）。ObjectTable の枠が別の敵に使い回されたのを見分ける。</param>
/// <param name="MayAttack">狙ってよい敵か（この FATE の敵か、自分に敵視を持っている敵）。</param>
/// <param name="Ally">自分と同じ陣営か（FATE で一緒に戦う NPC）。</param>
/// <param name="AggroNow">いま敵視リストに載っているか（＝いま自分を攻撃している）。</param>
/// <param name="MaxHp">最大 HP。大きい敵（ボス・強敵）を先に選ぶのに使う。</param>
/// <param name="Distance">自分からの距離。</param>
/// <param name="Forlorn">この FATE のフォーローン（ボーナスの敵）か。</param>
public readonly record struct MobView(
    ulong Id,
    uint NameId,
    bool MayAttack,
    bool Ally,
    bool AggroNow,
    uint MaxHp,
    float Distance,
    bool Forlorn);

/// <summary>狙いをどうするか。</summary>
public enum LockAction
{
    /// <summary>狙う相手が居ない。何もしない。</summary>
    None,

    /// <summary>固定している相手をそのまま狙う。</summary>
    Keep,

    /// <summary>固定している相手が他から替えられていた。戻す。</summary>
    Restore,

    /// <summary>新しい相手を固定する。</summary>
    Switch,

    /// <summary>狙ってはいけない敵が他から置かれていて、ほかに狙う相手も居ない。外す。</summary>
    Clear,
}

/// <summary>判断の結果。</summary>
/// <param name="Action">どうするか。</param>
/// <param name="TargetId">狙う相手（Keep・Restore・Switch のとき）。</param>
/// <param name="Reason">記録に残す理由。</param>
public readonly record struct LockDecision(LockAction Action, ulong TargetId, string Reason);

/// <summary>
/// 狙いを倒れるまで変えないための判断。
///
/// <b>利用者の要件（2026-10-10）：1 回狙ったら、その敵が倒れるまで狙いを変えない。</b>
/// 他の戦闘の仕組み（RSR・BMR）と取り合いになり、狙いがコロコロ変わりやすい所なので、
/// 「いまのハードターゲット」ではなく「自分が固定した相手」を正とする。
/// 他から替えられていたら戻す（戦闘の資料 8 章の取り合い・ClearForeignTarget と同じ考え方）。
///
/// <b>固定を外すのは次のときだけ。</b>
/// <list type="bullet">
/// <item>倒れた・消えた・狙えなくなった（一覧に居ない）</item>
/// <item>狙ってよい敵でなくなった（FATE の外の敵で、敵視が切れた）</item>
/// <item>フォーローンが出た（利用者の要件 docs/27 §4-3。フォーローンを固定している間は替えない）</item>
/// <item>同じ陣営（味方の NPC）を狙っていて、ほかに本物の敵が居る（陣営の読み違いを直す）</item>
/// </list>
///
/// <b>次の相手は「味方でない → いま攻撃してきている → 大きい → 近い」の順で選ぶ。</b>
/// 攻撃してきた敵を先に片付ける（反撃の方針・資料 7 章）。そのあとは今までどおり大きい敵、近い敵。
///
/// ゲームに触らないので、ゲーム無しで試せる（tools/regression/fate_combat.py）。
/// </summary>
public static class TargetLock
{
    /// <param name="locked">固定している相手。無ければ 0。</param>
    /// <param name="lockedNameId">固定したときの名前の番号。</param>
    /// <param name="current">いまのハードターゲット。無ければ 0。</param>
    /// <param name="mobs">見えている、生きていて狙える敵の一覧（狙ってよいかは <see cref="MobView.MayAttack"/>）。</param>
    public static LockDecision Decide(ulong locked, uint lockedNameId, ulong current, IReadOnlyList<MobView> mobs)
    {
        MobView? held = null;
        if (locked != 0)
        {
            foreach (var mob in mobs)
            {
                if (mob.Id == locked && mob.NameId == lockedNameId)
                {
                    held = mob;
                    break;
                }
            }
        }

        // **フォーローンは何より先。** 固定している相手がフォーローンなら替えない。
        if (held is not { Forlorn: true })
        {
            var forlorn = mobs.Where(x => x.Forlorn && x.MayAttack).OrderBy(x => x.Distance).Cast<MobView?>().FirstOrDefault();
            if (forlorn is { } bonus)
            {
                return new(LockAction.Switch, bonus.Id, "フォーローンを狙う");
            }
        }

        var hasEnemy = mobs.Any(x => x.MayAttack && !x.Ally);

        if (held is { MayAttack: true } keep)
        {
            if (keep.Ally && hasEnemy && Best(mobs) is { } enemy)
            {
                return new(LockAction.Switch, enemy.Id, "同じ陣営を狙っていたので敵へ替える");
            }

            return current == keep.Id
                ? new(LockAction.Keep, keep.Id, "倒れるまで同じ相手")
                : new(LockAction.Restore, keep.Id, "他から狙いを替えられたので戻す");
        }

        // ここから先は固定が無い（倒れた・消えた・狙ってよい敵でなくなった）。

        // **いまのハードターゲットが狙ってよい敵なら、それを固定する。** 利用者が手で選んだ相手など。
        var now = mobs.Where(x => x.Id == current).Cast<MobView?>().FirstOrDefault();
        if (now is { MayAttack: true } chosen && (!chosen.Ally || !hasEnemy))
        {
            return new(LockAction.Switch, chosen.Id, "いまのターゲットを固定する");
        }

        if (Best(mobs) is { } best)
        {
            return new(LockAction.Switch, best.Id, held is null && locked != 0 ? "前の相手が倒れたので次へ" : "狙う相手を選んだ");
        }

        // 狙う相手が居ない。狙ってはいけない敵が置かれていれば外す（RSR の Henched はハードターゲットを殴るため）。
        return now is { MayAttack: false }
            ? new(LockAction.Clear, 0, "狙ってはいけない敵が置かれていたので外す")
            : new(LockAction.None, 0, "狙う相手が居ない");
    }

    /// <summary>狙ってよい敵の中から、次に固定する相手を選ぶ。居なければ null。</summary>
    public static MobView? Best(IReadOnlyList<MobView> mobs)
        => mobs.Where(x => x.MayAttack)
            .OrderBy(x => x.Ally)
            .ThenByDescending(x => x.AggroNow)
            .ThenByDescending(x => x.MaxHp)
            .ThenBy(x => x.Distance)
            .Cast<MobView?>()
            .FirstOrDefault();
}
