using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace AutoCollector.Automation;

/// <summary>
/// 交換を開始してよい状態かの判定。
///
/// ここで見るのは「いま自動操作を始めてよいか」であって、
/// ショップ画面の操作可否ではない。IsOccupied はアドオンの有無を一切見ないため、
/// 「ショップが開いていて操作してよいか」の判定には使えない。
///
/// 不変条件: Duty 中は絶対に交換を始めない。AutoDuty を止めるのは Duty の外に出てから。
/// </summary>
public static class SafetyGuard
{
    /// <summary>交換を開始してよいか。理由つきで返す。</summary>
    public static bool IsSafeToStart(out string reason)
    {
        if (!Player.Available)
        {
            reason = "プレイヤーが利用できません";
            return false;
        }

        if (!Player.Interactable)
        {
            reason = "操作できない状態です";
            return false;
        }

        if (!GenericHelpers.IsScreenReady())
        {
            reason = "画面の読み込み中です";
            return false;
        }

        // Duty 中は最優先で除外する。ここで待つことが「AutoDuty を途中で止めない」の実装になる。
        if (Player.IsInDuty ||
            Svc.Condition[ConditionFlag.BoundByDuty] ||
            Svc.Condition[ConditionFlag.BoundByDuty56] ||
            Svc.Condition[ConditionFlag.BoundByDuty95])
        {
            reason = "コンテンツに参加中です。終わるまで待機します";
            return false;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            reason = "戦闘中です";
            return false;
        }

        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            reason = "戦闘不能です";
            return false;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "エリア移動中です";
            return false;
        }

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.WatchingCutscene78] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            reason = "カットシーン中です";
            return false;
        }

        if (Svc.Condition[ConditionFlag.Casting])
        {
            reason = "詠唱中です";
            return false;
        }

        if (Svc.Condition[ConditionFlag.TradeOpen])
        {
            reason = "トレード中です";
            return false;
        }

        if (Player.IsAnimationLocked)
        {
            reason = "動作中です";
            return false;
        }

        // IsOccupied は InCombat を含まないため、上で個別に見ている。
        if (GenericHelpers.IsOccupied())
        {
            reason = "他の操作中です";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// 交換の前に何が動いていたかの記録。交換後に元へ戻すために使う。
/// ユーザー自身が止めていたものを勝手に開始しないよう、開始前の状態だけを根拠にする。
/// </summary>
public sealed class ReturnContext
{
    /// <summary>交換を始める前に AutoDuty が動いていたか。</summary>
    public bool WasAutoDutyRunning { get; init; }

    /// <summary>
    /// 再開時に AutoDuty へ渡すエリア。
    ///
    /// AutoDuty を止めるのは必ず Duty の外なので、停止した瞬間の現在地は街になる。
    /// AutoDuty.Run はコンテンツのエリアを要求するため、街を渡しても再開できない。
    /// そこで、交換を待っている間に観測した「Duty 中のエリア」を使う。
    /// </summary>
    public uint AutoDutyTerritoryId { get; init; }

    /// <summary>AutoDuty が周回中だったか。</summary>
    public bool WasLooping { get; init; }

    /// <summary>
    /// Stop ではなく一時停止で止めたか。
    ///
    /// 一時停止で止めた場合は Run による再開ではなく、一時停止の解除で戻す。
    /// 周回カウンタもループ間処理の予約もそのまま残るため、こちらの方が副作用が少ない。
    /// </summary>
    public bool PausedAutoDuty { get; set; }
}
