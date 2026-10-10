// FATE の戦いと開始の判断（本物の TargetLock・AttackWatch・StartAttempts・FlightJoin）を試す。ゲームの操作はしない。
// 実行：python -X utf8 tools/regression/fate_combat.py
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Automation;
using AutoCollector.Game;

static class Program
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }

    // 敵の写し。既定は「この FATE の敵（狙ってよい）・味方でない」。
    static MobView Mob(ulong id, float distance, bool mayAttack = true, bool ally = false, bool forlorn = false, uint nameId = 0)
        => new(id, nameId == 0 ? (uint)id : nameId, mayAttack, ally, distance, forlorn);

    public static void Main()
    {
        TargetLockTests();
        AttackWatchTests();
        StartTests();
        FlightTests();
        Console.WriteLine($"回帰試験（FATE の戦いと開始）{count} 件 合格");
    }

    static void TargetLockTests()
    {
        // 狙ってよい敵の規則（利用者 2026-10-10 夕：FATE の中では紫の印＝その FATE の敵だけ）。
        Check(!TargetLock.MayAttack(5, 0, true), "FATE の中では、絡んできたフィールドのモンスター（FateId 0）も狙わない");
        Check(!TargetLock.MayAttack(5, 0, false), "FATE の中では、フィールドのモンスターを狙わない");
        Check(TargetLock.MayAttack(5, 5, false), "FATE の中では、その FATE の敵を狙う");
        Check(!TargetLock.MayAttack(5, 6, true), "隣の FATE の敵は狙わない");
        Check(TargetLock.MayAttack(0, 0, true) && !TargetLock.MayAttack(0, 0, false), "FATE の外では、絡んできた敵だけに反撃する");

        var a = Mob(1, 30); var b = Mob(2, 10);
        var d = TargetLock.Decide(0, 0, 0, [a, b]);
        Check(d is { Action: LockAction.Switch, TargetId: 2 }, "狙いが無ければ一番近い敵を選ぶ");

        d = TargetLock.Decide(2, 2, 2, [a, b]);
        Check(d is { Action: LockAction.Keep, TargetId: 2 }, "固定した相手が生きていれば、そのまま");

        // 10-10 12:40 の記録：RSR の Auto がハードターゲットを別の敵へ移していた。
        d = TargetLock.Decide(2, 2, 1, [a, b]);
        Check(d is { Action: LockAction.Restore, TargetId: 2 }, "他から替えられたら固定した相手へ戻す（倒れるまで変えない）");

        var near = Mob(3, 2);
        d = TargetLock.Decide(2, 2, 2, [a, b, near]);
        Check(d is { Action: LockAction.Keep, TargetId: 2 }, "もっと近い敵が現れても、固定した相手が倒れるまで替えない");

        d = TargetLock.Decide(2, 2, 2, [a, near]);
        Check(d is { Action: LockAction.Switch, TargetId: 3 }, "固定した相手が倒れたら（一覧に居ない）一番近い敵へ");
        Check(d.Reason.Contains("倒れた"), "倒れたので次へ、を理由に残す");

        d = TargetLock.Decide(2, 2, 2, [a, Mob(2, 10, nameId: 999)]);
        Check(d.Action == LockAction.Switch, "同じ枠に別の敵が入ったら（名前の番号が違う）固定した相手は倒れたとみなし、固定し直す");

        var forlorn = Mob(9, 50, forlorn: true);
        d = TargetLock.Decide(2, 2, 2, [a, b, forlorn]);
        Check(d is { Action: LockAction.Switch, TargetId: 9 }, "フォーローンが出たら固定を替える（利用者の要件 docs/27）");
        d = TargetLock.Decide(9, 9, 9, [a, b, forlorn, Mob(8, 5, forlorn: true)]);
        Check(d is { Action: LockAction.Keep, TargetId: 9 }, "フォーローンを固定している間は、別のフォーローンにも替えない");

        var ally = Mob(5, 3, ally: true);
        d = TargetLock.Decide(5, 5, 5, [ally, a]);
        Check(d is { Action: LockAction.Switch, TargetId: 1 }, "同じ陣営（味方の NPC）を固定していて本物の敵がいれば替える");
        d = TargetLock.Decide(5, 5, 5, [ally]);
        Check(d is { Action: LockAction.Keep, TargetId: 5 }, "敵を見分けられないとき（味方しか居ない）は今までどおり狙い続ける");

        var field = Mob(6, 1, mayAttack: false);
        d = TargetLock.Decide(0, 0, 0, [field, a]);
        Check(d is { Action: LockAction.Switch, TargetId: 1 }, "フィールドのモンスターは、一番近くても選ばない");

        d = TargetLock.Decide(0, 0, 1, [a, b]);
        Check(d is { Action: LockAction.Switch, TargetId: 2 }, "他から置かれたいまのターゲットは引き継がず、一番近い敵を選ぶ");

        var stray = Mob(7, 4, mayAttack: false);
        d = TargetLock.Decide(0, 0, 7, [stray]);
        Check(d is { Action: LockAction.Clear }, "狙ってはいけない敵が置かれていて他に居なければ外す（Henched が殴らないように）");
        d = TargetLock.Decide(0, 0, 7, [stray, a]);
        Check(d is { Action: LockAction.Switch, TargetId: 1 }, "狙ってはいけない敵が置かれていても、狙う敵が居ればそちらを固定する");
        d = TargetLock.Decide(2, 2, 7, [stray, a, b]);
        Check(d is { Action: LockAction.Restore, TargetId: 2 }, "固定している間にフィールドのモンスターへ替えられたら戻す");

        d = TargetLock.Decide(2, 2, 2, [a, Mob(2, 10, mayAttack: false)]);
        Check(d is { Action: LockAction.Switch, TargetId: 1 }, "固定した相手が狙ってよい敵でなくなったら次へ");

        d = TargetLock.Decide(0, 0, 0, []);
        Check(d.Action == LockAction.None, "誰も居なければ何もしない");
    }

    static void AttackWatchTests()
    {
        var t0 = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var watch = new AttackWatch();
        Check(!watch.Update(1, false, t0), "狙った直後は迂回しない");
        Check(!watch.Update(1, false, t0.AddSeconds(3.9)), "4 秒までは迂回しない");
        Check(watch.Update(1, false, t0.AddSeconds(4)) && watch.Detouring, "4 秒攻撃できなければ迂回する（利用者の指定）");
        Check(!watch.Update(1, true, t0.AddSeconds(5)) && !watch.Detouring, "届いたら迂回をやめる");
        Check(!watch.Update(1, false, t0.AddSeconds(6)) && !watch.Update(1, false, t0.AddSeconds(8.9)), "届いた後は、届いた時から 4 秒数え直す");
        Check(watch.Update(1, false, t0.AddSeconds(9)), "届かないまま 4 秒でまた迂回");
        Check(!watch.Update(2, false, t0.AddSeconds(11)), "狙う相手が替わったら数え直す");

        watch.Update(2, false, t0.AddSeconds(20));
        watch.NextGoal(3); watch.NextGoal(3);
        Check(watch.GoalIndex == 2, "行き先を順に替える");
        watch.NextGoal(3);
        Check(watch.GoalIndex == 0, "使い切ったら最初へ戻る（諦めない）");

        var player = new Vector3(0, 10, -20);
        var target = new Vector3(0, 12, 0);
        var goals = DetourGoals.Around(player, target, 3);
        Check(goals.Count == 9 && goals[0] == target, "最初の行き先は敵の位置、ほかに周り 8 方向");
        Check(goals.Skip(1).All(g => MathF.Abs(Vector2.Distance(new(g.X, g.Z), new(target.X, target.Z)) - 3) < .001f && g.Y == target.Y),
            "周りの点は敵から水平に同じ距離・敵と同じ高さ");
        Check(Vector3.Distance(goals[1], player) == goals.Skip(1).Min(g => Vector3.Distance(g, player)), "周りの点は自分に近い側から並べる");
        Check(Vector3.Distance(goals[8], player) == goals.Skip(1).Max(g => Vector3.Distance(g, player)), "最後は敵の向こう側");
    }

    static void StartTests()
    {
        // 利用者の例：A・B・C がいるとき、A に話しかけて 3 秒変化なし → B に話しかけて 3 秒以内に開始 → C には話しかけない。
        var t0 = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var attempts = new StartAttempts();
        List<StartCandidate> npcs = [new(10, 12, true), new(11, 4, false), new(12, 8, false)];
        var (step, id) = attempts.Choose(npcs, TimeSpan.Zero);
        Check(step == StartStep.Talk && id == 10, "ゲームが指定した開始役（A）を最初に試す（近い NPC より先）");

        npcs[0] = new(10, 30, true); npcs[2] = new(12, 1, false);
        Check(attempts.Choose(npcs, TimeSpan.FromSeconds(1)) == (StartStep.Talk, 10UL), "向かっている間は相手を替えない");

        attempts.MarkTalked();
        attempts.DialogEnded(t0);
        Check(attempts.Waiting(t0.AddSeconds(2.9)) && !attempts.Expired(t0.AddSeconds(2.9)), "話し終えて 3 秒までは待つ");
        Check(attempts.Expired(t0.AddSeconds(3)), "3 秒たっても始まらなければ見切る");
        attempts.Next();

        npcs[2] = new(12, 8, false);
        (step, id) = attempts.Choose(npcs, TimeSpan.FromSeconds(5));
        Check(step == StartStep.Talk && id == 11, "次は残りのうち近い NPC（B）");
        attempts.MarkTalked();
        attempts.DialogEnded(t0.AddSeconds(6));
        // ここで FATE が始まる。呼び出し側は以後 Choose を呼ばない。
        Check(attempts.Waiting(t0.AddSeconds(7)) && attempts.TriedCount == 1 && attempts.Current == 11, "B で始まれば C には話しかけない");

        var hidden = new StartAttempts();
        List<StartCandidate> others = [new(21, 5, false), new(22, 9, false)];
        Check(hidden.Choose(others, TimeSpan.FromSeconds(2)).Step == StartStep.Wait, "開始役がまだ見えないうちは少し待つ");
        Check(hidden.Choose(others, TimeSpan.FromSeconds(6)) == (StartStep.Talk, 21UL), "待っても見えなければ近い NPC から試す");
        hidden.MarkTalked(); hidden.DialogEnded(t0); hidden.Next();
        Check(hidden.Choose(others, TimeSpan.FromSeconds(9)) == (StartStep.Talk, 22UL), "次の NPC へ");
        hidden.MarkTalked(); hidden.DialogEnded(t0); hidden.Next();
        Check(hidden.Choose(others, TimeSpan.FromSeconds(12)).Step == StartStep.None, "全員に話しかけたら、それ以上は話しかけない");

        var gone = new StartAttempts();
        gone.Choose([new(31, 5, true), new(32, 6, false)], TimeSpan.Zero);
        Check(gone.Choose([new(32, 6, false)], TimeSpan.FromSeconds(1)) == (StartStep.Talk, 32UL), "向かっていた NPC が見えなくなったら次へ");

        var silent = new StartAttempts();
        silent.Choose([new(41, 3, false)], TimeSpan.FromSeconds(10));
        silent.MarkTalked();
        silent.DialogEnded(t0); // 窓が出なかったときは、話しかけた時刻を渡す（FateStarter.EndDialog）
        Check(silent.Expired(t0.AddSeconds(3)), "窓が出なければ、話しかけてから 3 秒で次へ");
        var notTalked = new StartAttempts();
        notTalked.Choose([new(51, 3, false)], TimeSpan.FromSeconds(10));
        notTalked.DialogEnded(t0);
        Check(!notTalked.Waiting(t0) && !notTalked.Expired(t0.AddSeconds(10)), "話しかけていなければ待ちも見切りもしない");

        Check(StartPrompt.MayAccept("F.A.T.E.「危ない野良仕事」に参加しますか？", "危ない野良仕事"), "FATE の開始の確認なら押す");
        Check(StartPrompt.MayAccept("「危ない 野良仕事」を手伝いますか？", "危ない野良仕事"), "FATE の名前が入っていれば押す（空白は無視）");
        Check(!StartPrompt.MayAccept("リムサ・ロミンサへ移動しますか？", "危ない野良仕事"), "運び屋の移動の確認は押さない");
        Check(!StartPrompt.MayAccept("アイテムを購入する", "危ない野良仕事"), "店の選択肢は選ばない");
        Check(!StartPrompt.MayAccept("", "危ない野良仕事") && !StartPrompt.MayAccept(null, ""), "文が無ければ押さない");
    }

    static void FlightTests()
    {
        Vector3 origin = new(0, 45, 0), mid = new(50, 60, 50), entry = new(100, 60, 100), d1 = new(110, 40, 110), landing = new(120, 20, 120);
        var joined = FlightJoin.Join([origin, mid, entry], [entry, d1, landing]);
        Check(joined.SequenceEqual([origin, mid, entry, d1, landing]), "外周上空への経路と降下の経路を 1 本につなぐ（外周の点は重ねない）");
        Check(FlightJoin.Join([origin, entry], null).SequenceEqual([origin, entry]), "降下の経路が無ければつながない");
        var near = entry + new Vector3(1, 0, 0);
        Check(FlightJoin.Join([origin, entry], [near, landing]).Count == 4, "降下の先頭が外周の点と違えば残す（確かめた線を勝手に省かない）");

        Check(!FlightJoin.PassedEntry(new(95, 60, 95), entry, landing), "外周の手前ではまだ越えていない");
        Check(FlightJoin.PassedEntry(new(104, 58, 106), entry, landing), "外周の内側（着地点に近い側）へ入ったら越えた");

        var plan = new List<Vector3> { new(0, 5, 0), new(10, 20, 10), new(30, 25, 30) };
        Check(FlightJoin.FromHere(plan, new(0, 3, 0), 7).SequenceEqual(plan.Skip(1)), "離陸の合図の点がすぐそばなら飛ばして 2 点目へ（真上に上がりきらない）");
        Check(FlightJoin.FromHere(plan, new(30, 3, 0), 7).SequenceEqual(plan), "離陸の合図の点から離れていれば飛ばさない");
        Check(FlightJoin.FromHere([new(0, 5, 0)], new(0, 3, 0), 7).Count == 1, "点が 1 つなら飛ばさない");
    }
}
