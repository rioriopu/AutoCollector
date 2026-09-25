using System;
using System.Numerics;
using AutoCollector.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoCollector.Automation;

/// <summary>
/// 移動のためにマウントへ乗り、飛べるところでは飛び上がる。
///
/// <b>遠くへ歩かせない。</b>
/// FATE は同じマップの中でも数百メートル離れていることがある。
/// 徒歩で向かうと、着く前に FATE が終わってしまう。
///
/// <b>離陸はしない。乗せるだけ。</b>
/// 飛び上がるのは vnavmesh が自分で行う。FollowPath は
/// 「次の経路点が自分より高い」「騎乗中」「まだ飛んでいない」の
/// 3 つが揃うとジャンプを連打する（FollowPath.cs:144）。
///
/// こちらから撃つと二重になり、しかも離陸を待つあいだ移動を止めるため、
/// vnavmesh が経路を進められず地上すれすれを走ることになる。
/// 実測でそうなった（2026-09-25 中央ラノシア）。
///
/// <code>
/// 目的地まで 20m 超 ＋ 乗っていない → マウントルーレット
/// 乗ったら                          → あとは vnavmesh に任せる
/// 着いたら                          → 降りる（降下は数秒かかる）
/// </code>
///
/// <b>マウントは「ルーレット」で呼ぶ。</b>
/// GeneralAction 9（マウントルーレット）を使う。特定のマウントを指定すると、
/// 利用者が持っていない可能性があるうえ、飛べないマウントを選ぶこともある。
/// ルーレットなら利用者が設定したものが出る。
///
/// <b>飛べるかどうかは 2 つの意味がある。</b>
/// PlayerState.CanFly は「このエリアで風脈を解放しているか」。
/// 解放していないエリアでは飛び上がれないので、そこは走る。
/// </summary>
public sealed unsafe class MountService(AnomalyLog anomalyLog, FateTrace trace)
{
    /// <summary>マウントルーレット。利用者が設定したマウントが出る。</summary>
    private const uint MountRouletteAction = 9;

    /// <summary>ジャンプ。マウント中にこれを撃つと飛び上がる。</summary>
    private const uint JumpAction = 2;

    /// <summary>これより近ければ乗らない。乗り降りのほうが時間を食う。</summary>
    private const float MinDistanceToMount = 20f;

    /// <summary>同じ操作を送り続けないための間隔。</summary>
    private const int ActionThrottleMs = 1000;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly FateTrace trace = trace;

    /// <summary>降りると決めたか。着くまで離陸させないための旗。</summary>
    private bool dismounting;

    /// <summary>このエリアで飛べるか（風脈を解放しているか）。</summary>
    public static bool CanFlyHere
    {
        get
        {
            try
            {
                var ps = PlayerState.Instance();
                return ps is not null && ps->CanFly;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// いまマウントに乗っているか。
    ///
    /// 二人乗りの同乗（RidingPillion）は見ない。他人のマウントに
    /// 乗せてもらっている状態で、こちらから操作できないため。
    /// </summary>
    public static bool IsMounted => Svc.Condition[ConditionFlag.Mounted];

    /// <summary>いま飛んでいるか。</summary>
    public static bool IsFlying => Svc.Condition[ConditionFlag.InFlight];

    /// <summary>
    /// 移動の準備を 1 フレーム進める。
    ///
    /// 戻り値が true なら「まだ準備中」。呼び出し側は移動を始めずに待つ。
    /// false なら準備が済んでいる（乗る必要が無い場合を含む）ので進んでよい。
    /// </summary>
    /// <param name="destination">目的地。距離で乗るかどうかを決める。</param>
    public bool TickPrepare(Vector3 destination)
    {
        if (!Player.Available || Svc.Condition[ConditionFlag.Unconscious])
        {
            return false;
        }

        // 戦闘中は乗れない。歩いて向かうしかない。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            return false;
        }

        // 詠唱中・動作中に重ねて送っても弾かれる。落ち着くまで待つ。
        if (Player.IsCasting || Player.IsAnimationLocked)
        {
            return true;
        }

        // 乗る・降りるの最中。
        if (Svc.Condition[ConditionFlag.Mounting] || Svc.Condition[ConditionFlag.Mounting71])
        {
            return true;
        }

        // **降りると決めたあとは、着くまで何もしない。**
        //
        // 空中で降りる操作をすると降下が始まり、その最中は InFlight が
        // false になる。ここで「飛んでいない」と見て飛び上がらせると、
        // 降りかけては上がるのを繰り返す。
        //
        // **距離の判定より先に置く。** 着いた場所で降りている最中は
        // 距離が近いので、あとに置くと素通りしてしまう。
        if (this.dismounting)
        {
            this.trace.State("マウント", $"降りています（{FateTrace.Describe()}）");
            return true;
        }

        // 水平の距離で測る。飛んでいる最中は真上に数十メートルの差があり、
        // そのまま測ると近くても「遠い」と出る。
        var distance = Vector2.Distance(
            new Vector2(Player.Position.X, Player.Position.Z),
            new Vector2(destination.X, destination.Z));

        // 近ければ歩く。乗り降りのほうが時間を食う。
        if (distance < MinDistanceToMount)
        {
            return false;
        }

        if (!IsMounted)
        {
            return this.TryMount();
        }

        // **離陸は vnavmesh に任せる。**
        //
        // FollowPath は「次の経路点が自分より高い」かつ「騎乗中」かつ
        // 「まだ飛んでいない」なら、自分でジャンプを連打して飛び上がる
        // （vnavmesh/Movement/FollowPath.cs:144 ExecuteJump）。
        //
        // こちらから撃つと二重になり、しかも離陸を待つあいだ移動を
        // 止めるため、vnavmesh が経路を進められない。結果として
        // 地上すれすれを走ることになった（2026-09-25 実測）。
        //
        // こちらの役目は「乗せること」と「目的地を上に置くこと」だけ。
        return false;
    }

    /// <summary>
    /// 飛行で向かうときの目的地。
    ///
    /// <b>そのままの座標を渡すと地面を擦る。</b>
    /// vnavmesh は 3D の経路を引くが、出発点と目的地がどちらも地表だと
    /// 経路全体が地表付近になる。起伏に触れれば飛行が解ける。
    /// 実測（中央ラノシア）でそうなった。
    ///
    /// 目的地を真上へ持ち上げると、経路が一度上がってから降りる形になり、
    /// 途中の起伏を越えられる。着いてから下は自分で降りる。
    /// </summary>
    /// <param name="destination">本来の目的地。</param>
    /// <param name="lift">持ち上げる高さ。</param>
    /// <remarks>
    /// <b>飛んでいるかは見ない。</b>まだ地上にいても、これから飛ぶなら
    /// 持ち上げた座標を渡す。vnavmesh はその高さの差を見て離陸するため、
    /// 「飛んでから持ち上げる」のでは離陸してくれない。
    /// </remarks>
    public static Vector3 LiftForFlight(Vector3 destination, float lift = FlightLift)
        => destination with { Y = destination.Y + lift };

    /// <summary>
    /// 飛行時に目的地を持ち上げる高さ。
    ///
    /// 低すぎると起伏に触れ、高すぎると目的地の真上で長く降りることになる。
    /// </summary>
    public const float FlightLift = 30f;

    /// <summary>
    /// マウントから降りる。降りるまで true（まだ途中）を返す。
    ///
    /// <b>空中では 1 回では降りない。</b>
    /// 上空で降りる操作をすると、その場から降下が始まるだけで、
    /// 地面に着くまで数秒かかる。その間もずっと「騎乗中」のままなので、
    /// 呼び出し側は true が返るあいだ待ち続けること。
    ///
    /// <b>降下中は飛行の判定が落ちる。</b>
    /// 降下に入ると InFlight が false になるが、まだ騎乗中で空にいる。
    /// ここを「飛んでいないから飛び上がろう」と扱うと、降りかけては
    /// 上がるのを繰り返す。実際にそうなった（2026-09-25 中央ラノシア）。
    /// そのため降りると決めた時点で旗を立て、着くまで離陸を止める。
    /// </summary>
    public bool TickDismount()
    {
        if (!IsMounted)
        {
            this.dismounting = false;
            return false;
        }

        // 降りると決めた。着くまで離陸させない。
        this.dismounting = true;

        if (Player.IsCasting || Player.IsAnimationLocked)
        {
            return true;
        }

        if (!EzThrottler.Throttle("AutoCollector.Dismount", ActionThrottleMs))
        {
            return true;
        }

        try
        {
            ActionManager.Instance()->UseAction(ActionType.Mount, 0);
            this.trace.Decision("降りる", IsFlying ? "空中なので降下してから降りる" : "地上で降りる");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Mount", $"降りられませんでした: {ex.Message}");
        }

        return true;
    }

    /// <summary>
    /// 降りる途中かどうかを忘れる。
    ///
    /// 目的地が変わって、また飛んで向かうときに呼ぶ。
    /// これを呼ばないと二度と離陸しない。
    ///
    /// <b>降下が続いていれば止める。</b>
    /// ゲーム側の降下は、こちらが操作をやめても続く。そしてその動きが
    /// プレイヤーの操作として vnavmesh に読まれ、次に積んだ経路を
    /// その場で捨てさせる。空中でジャンプを撃つと降下が終わる。
    /// </summary>
    public void ClearDismounting()
    {
        this.dismounting = false;
        this.CancelDescent();
    }

    /// <summary>
    /// 進行中の降下を止める。
    ///
    /// 空中でジャンプを撃つと、ゲーム自身の降下が終わる。
    /// これをやらないと降下が続き、vnavmesh がそれを操作と見て
    /// 経路を捨て続ける。AutoFATEGrind の Landing.cs にある実測。
    /// </summary>
    public void CancelDescent()
    {
        if (!IsFlying)
        {
            return;
        }

        try
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, JumpAction);
            this.trace.Decision("降下を止める", "経路を捨てさせないため");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Mount", $"降下を止められませんでした: {ex.Message}");
        }
    }

    private bool TryMount()
    {
        if (!EzThrottler.Throttle("AutoCollector.Mount", ActionThrottleMs))
        {
            return true;
        }

        try
        {
            var am = ActionManager.Instance();
            if (am is null)
            {
                return false;
            }

            // 使えないなら乗ること自体を諦める。陸路で向かう。
            // 例: 騎乗が解放されていないエリア、コンテンツの中。
            if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteAction) != 0)
            {
                return false;
            }

            am->UseAction(ActionType.GeneralAction, MountRouletteAction);
            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Mount", $"マウントを呼べませんでした: {ex.Message}");
            return false;
        }
    }


}
