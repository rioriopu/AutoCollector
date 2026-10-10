using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>
/// 狙っている敵に「攻撃が届くか」。
///
/// <b>距離を自分で決め打ちしない。</b>
/// ジョブの「敵を狙う GCD（魔法・ウェポンスキル）」をゲームデータから選び、
/// 届くか（射程・視線）はゲームの <c>ActionManager.GetActionInRangeOrLoS</c> に聞く。
/// AutoMobFarm の Data\AttackReach.cs（AutoJobQuest から写したもの）と同じ作り。
/// <list type="bullet">
/// <item>近接の役割（ClassJob.Role 1＝タンク・2＝近接）：いちばん低いレベルで覚える技＝近接の射程。
///       近接職は遠くから撃てる技を持つことがあるので、それでは測らない（利用者 2026-10-10「近接職は近接攻撃が 4 秒間行えない時」）。</item>
/// <item>遠隔の役割：覚えている技のうち、いちばん遠くまで届くもの（ゲームの GetActionRange で比べる）。</item>
/// </list>
/// 戻り値の番号（LogMessage で確認済み）：0＝使える、562＝見えない、565＝向きが違う（向けば使える＝届くに入れる）、566＝射程外。
///
/// <b>ゲームが届くと言っても、RSR の視線の判定で遮られていれば撃たない。</b>
/// RSR は自分の足もと+2m から相手の足もと+2m へ地形のレイを飛ばし、遮られた敵を攻撃の候補から外す
/// （RSR の ObjectHelper.CanSeeFrom）。黒衣森・北ザナラーンの木や岩の陰で 45 秒棒立ちした実例がある
/// （メモリ reference_rsr_targeting）。同じレイを飛ばして確かめる。
/// </summary>
public sealed unsafe class AttackReach(AnomalyLog anomalyLog)
{
    private readonly record struct Candidate(uint ActionId, byte Level);

    private static readonly uint[] ReachableCodes = [0, 565];

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly Dictionary<uint, List<Candidate>> candidateCache = [];
    private readonly Dictionary<(uint Job, int Level), uint> pickCache = [];

    /// <summary>近接の役割か（タンクと近接）。読めなければ近接扱い（寄るほうが棒立ちより害が小さい）。</summary>
    public static bool IsMeleeRole(uint classJob)
        => !Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(classJob, out var row) || row.Role is 1 or 2;

    /// <summary>
    /// いまの自分から、その敵へ攻撃が届くか。
    /// </summary>
    /// <param name="target">狙っている敵。</param>
    /// <param name="why">届かない理由（記録用）。届くときは空。</param>
    /// <returns>届けば true。ゲームに聞けないときは null（呼び出し側が距離で判断する）。</returns>
    public bool? CanAttack(IGameObject target, out string why)
    {
        why = string.Empty;

        if (!Player.Available || Player.Object is not { } me)
        {
            return null;
        }

        var action = this.PickAction(me.ClassJob.RowId, me.Level);
        if (action == 0)
        {
            return null;
        }

        uint code;
        try
        {
            code = ActionManager.GetActionInRangeOrLoS(
                action,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)me.Address,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address);
        }
        catch (Exception ex)
        {
            if (EzThrottler.Throttle("AutoCollector.AttackReach.Error", 10000))
            {
                this.anomalyLog.Warn("Fate", $"攻撃が届くかをゲームに聞けませんでした（{ex.Message}）。距離で判断します");
            }

            return null;
        }

        if (!ReachableCodes.Contains(code))
        {
            why = code switch
            {
                562 => "見えない（視線が通らない）",
                566 => "射程の外",
                _ => $"使えない（{code}）",
            };
            return false;
        }

        if (SightBlocked(me.Position, target.Position) is { } hit)
        {
            why = $"岩などに遮られて RSR が撃たない（({hit.X:F0},{hit.Y:F0},{hit.Z:F0}) で当たる）";
            return false;
        }

        return true;
    }

    /// <summary>
    /// RSR の視線の判定（ObjectHelper.CanSeeFrom）と同じレイ。遮られていれば当たった点、通れば null。
    /// ClientStructs の静的な RaycastMaterialFilter は RSR と同じ絞り・層を使う（AutoMobFarm の GameWorld.RsrSightBlocked と同じ）。
    /// </summary>
    public static Vector3? SightBlocked(Vector3 from, Vector3 to)
    {
        var eye = from + new Vector3(0, 2f, 0);
        var offset = to + new Vector3(0, 2f, 0) - eye;
        var length = offset.Length();
        if (length < 0.01f)
        {
            return null;
        }

        return BGCollisionModule.RaycastMaterialFilter(eye, offset / length, out var hit, length) ? hit.Point : null;
    }

    private uint PickAction(uint classJob, int level)
    {
        if (classJob == 0 || level <= 0)
        {
            return 0;
        }

        if (this.pickCache.TryGetValue((classJob, level), out var cached))
        {
            return cached;
        }

        uint pick;
        try
        {
            var learned = this.Candidates(classJob).Where(c => c.Level <= Math.Max(level, 1)).ToList();
            if (learned.Count == 0)
            {
                pick = 0;
            }
            else if (IsMeleeRole(classJob))
            {
                pick = learned.OrderBy(c => c.Level).ThenBy(c => c.ActionId).First().ActionId;
            }
            else
            {
                pick = learned
                    .Select(c => (c.ActionId, c.Level, Range: ActionManager.GetActionRange(c.ActionId)))
                    .OrderByDescending(x => x.Range).ThenBy(x => x.Level).ThenBy(x => x.ActionId)
                    .First().ActionId;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Fate", $"攻撃の届く範囲を測る技を選べませんでした（{ex.Message}）。距離で判断します");
            pick = 0;
        }

        this.pickCache[(classJob, level)] = pick;
        return pick;
    }

    private List<Candidate> Candidates(uint classJob)
    {
        if (this.candidateCache.TryGetValue(classJob, out var cached))
        {
            return cached;
        }

        var jobs = new HashSet<uint> { classJob };
        if (Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(classJob, out var cj) && cj.ClassJobParent.RowId != 0)
        {
            jobs.Add(cj.ClassJobParent.RowId);
        }

        // ActionCategory 2＝魔法、3＝ウェポンスキル（AutoJobQuest でゲームデータの名前を確認済み）。
        var list = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .Where(a => a.IsPlayerAction && !a.IsPvP && a.CanTargetHostile && a.Range != 0
                        && a.ActionCategory.RowId is 2 or 3 && jobs.Contains(a.ClassJob.RowId))
            .Select(a => new Candidate(a.RowId, a.ClassJobLevel))
            .OrderBy(c => c.Level).ThenBy(c => c.ActionId)
            .ToList();

        this.candidateCache[classJob] = list;
        return list;
    }
}
