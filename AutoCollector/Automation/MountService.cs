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
/// <code>
/// 目的地まで 10m 超 ＋ 乗っていない → マウントルーレット
/// マウント中 ＋ このエリアで飛べる  → ジャンプして飛び上がる
/// </code>
///
/// <b>マウントは「ルーレット」で呼ぶ。</b>
/// GeneralAction 9（マウントルーレット）を使う。特定のマウントを指定すると、
/// 利用者が持っていない可能性があるうえ、飛べないマウントを選ぶこともある。
/// ルーレットなら利用者が設定したものが出る。
///
/// <b>飛べるかどうかは 2 つの意味がある。</b>
/// PlayerState.CanFly は「このエリアで風脈を解放しているか」。
/// 解放していないエリアでジャンプしても飛び上がらないので、そこは走る。
/// </summary>
public sealed unsafe class MountService(AnomalyLog anomalyLog)
{
    /// <summary>マウントルーレット。利用者が設定したマウントが出る。</summary>
    private const uint MountRouletteAction = 9;

    /// <summary>ジャンプ。マウント中にこれを撃つと飛び上がる。</summary>
    private const uint JumpAction = 2;

    /// <summary>これより近ければ乗らない。乗り降りのほうが時間を食う。</summary>
    private const float MinDistanceToMount = 20f;

    /// <summary>同じ操作を送り続けないための間隔。</summary>
    private const int ActionThrottleMs = 1000;

    /// <summary>
    /// 飛び上がったあと、上昇に使う時間。
    ///
    /// <b>ジャンプ 1 回では地面すれすれにしかならない。</b>
    /// そのまま経路を追わせると、起伏に触れて飛行が解除される。
    /// 実測（中央ラノシア）でそうなった。
    ///
    /// 飛行中は前進キーで斜め上に上がるのではなく、
    /// vnavmesh が引いた 3D 経路に沿って進む。出発時に十分な高さが
    /// 無いと、その経路自体が地面を擦る高さになる。
    /// </summary>
    private static readonly TimeSpan ClimbDuration = TimeSpan.FromSeconds(2.5);

    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>飛び上がった時刻。上昇の猶予を計るのに使う。</summary>
    private DateTime tookOffUtc = DateTime.MinValue;

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

        var distance = Vector3.Distance(Player.Position, destination);

        // 近ければ歩く。乗り降りのほうが時間を食う。
        if (distance < MinDistanceToMount)
        {
            return false;
        }

        if (!IsMounted)
        {
            this.tookOffUtc = DateTime.MinValue;
            return this.TryMount();
        }

        // 乗っている。飛べないエリアなら、このまま走って向かう。
        if (!CanFlyHere)
        {
            return false;
        }

        if (!IsFlying)
        {
            // まだ飛んでいない。飛び上がる。
            //
            // 2 回目以降もここへ来る。経路の途中で地面に触れて
            // 飛行が解けたときに、もう一度飛び上がらせるため。
            return this.TryTakeOff();
        }

        // **飛んだ直後は少しだけ待つ。**
        //
        // ジャンプの動作が終わる前に経路を引くと、まだ地上判定のまま
        // 経路が作られて、地面に沿った高さになってしまう。
        if (DateTime.UtcNow - this.tookOffUtc < ClimbDuration)
        {
            return true;
        }

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
    public static Vector3 LiftForFlight(Vector3 destination, float lift = FlightLift)
        => IsFlying ? destination with { Y = destination.Y + lift } : destination;

    /// <summary>
    /// 飛行時に目的地を持ち上げる高さ。
    ///
    /// 低すぎると起伏に触れ、高すぎると目的地の真上で長く降りることになる。
    /// </summary>
    public const float FlightLift = 30f;

    /// <summary>マウントから降りる。降りるまで true（まだ途中）を返す。</summary>
    public bool TickDismount()
    {
        if (!IsMounted)
        {
            return false;
        }

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
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Mount", $"降りられませんでした: {ex.Message}");
        }

        return true;
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

    private bool TryTakeOff()
    {
        if (!EzThrottler.Throttle("AutoCollector.TakeOff", ActionThrottleMs))
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

            am->UseAction(ActionType.GeneralAction, JumpAction);
            this.tookOffUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Mount", $"飛び上がれませんでした: {ex.Message}");
            return false;
        }
    }
}
