using System;
using System.Collections.Generic;
using System.Numerics;
using AutoCollector.Diagnostics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoCollector.Game;

/// <summary>
/// 狙ってよい敵を決める。
///
/// <b>目指す挙動：自分からは FATE の敵にしか仕掛けない。ただし攻撃されたら反撃する。</b>
///
/// これを BossMod Reborn（BMR）の設定だけでは表現できない。
/// BMR の <c>AIHintsBuilder</c> は「敵視リストに載っている敵」を
/// FateId に関係なく優先度 0 にし（AIHintsBuilder.cs:148）、
/// <c>AutoTarget</c> がそれを候補に入れる（AutoTarget.cs:224）。
/// <c>Everything=Disabled</c> は「新しく引っ張らない」だけで、
/// 一度絡まれた FATE 外の敵は候補に残る。
///
/// <b>そこで、狙う相手はこちらで決める。</b>
/// ハードターゲットを自分で置き、BMR には「いま狙っている相手と戦う」ことだけを
/// させる。NorthHornAutoFates が同じやり方をしている
/// （AutomationController.cs:2048-2067 の AcquireFateTarget）。
///
/// <b>「攻撃されたか」はゲームの敵視リストで見る。</b>
/// BMR も同じものを読んでいる（WorldStateGameSync.cs:258-265 →
/// <c>UIState.Instance()->Hater</c>）。これは画面の敵リストに出るものと同じで、
/// 「敵のターゲットが自分か」を見るより確実。敵は範囲攻撃の詠唱中に
/// ターゲットを別の人へ移すことがあり、その瞬間だけ「狙われていない」に見える。
/// </summary>
public sealed unsafe class FateTargetService(AnomalyLog anomalyLog)
{
    /// <summary>
    /// FATE の円からこれだけ外に出た敵も、その FATE の敵として扱う。
    ///
    /// 敵は円の縁を越えて動く。厳密に円内だけにすると、
    /// 追いかけている最中に対象から外れる。
    /// </summary>
    private const float FateMobSlackMeters = 8f;

    /// <summary>
    /// 一度「自分に敵視がある」と見た敵を、何秒のあいだ覚えておくか。
    ///
    /// <b>その場の値だけで判断しない。</b>
    /// 敵視リストは戦闘が切れると消える。消えた瞬間に「もう狙われていない」と
    /// 判断して攻撃をやめると、まだ生きている敵を残して離れることになる。
    /// </summary>
    private static readonly TimeSpan AggroMemory = TimeSpan.FromSeconds(8);

    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>自分に敵視を持っていた敵と、最後に見た時刻。</summary>
    private readonly Dictionary<ulong, DateTime> aggroSeen = [];

    /// <summary>いま自分が置いたターゲット。外すときに、他人が変えたものを消さないため。</summary>
    private ulong ownedTarget;

    /// <summary>直前に読んだ敵視リスト。毎フレーム作り直さないために持つ。</summary>
    private readonly HashSet<uint> enmity = [];

    private DateTime enmityReadUtc = DateTime.MinValue;

    /// <summary>敵視リストを読み直す間隔。</summary>
    private static readonly TimeSpan EnmityRefresh = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 敵視リストを読み直す。
    ///
    /// <c>UIState.Instance()->Hater</c> は画面の敵リストの中身。
    /// BMR もここから <c>AggroPlayer</c> を作っている
    /// （WorldStateGameSync.cs:258-265）。
    /// </summary>
    private void RefreshEnmity()
    {
        var now = DateTime.UtcNow;

        if (now - this.enmityReadUtc < EnmityRefresh)
        {
            return;
        }

        this.enmityReadUtc = now;
        this.enmity.Clear();

        try
        {
            var ui = UIState.Instance();
            if (ui is null)
            {
                return;
            }

            ref var hater = ref ui->Hater;
            var count = hater.HaterCount;

            // 配列は 32 件で固定。読み過ぎないように押さえる。
            if (count > 32)
            {
                count = 32;
            }

            for (var i = 0; i < count; i++)
            {
                var id = hater.Haters[i].EntityId;
                if (id != 0)
                {
                    this.enmity.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Target", $"敵視リストを読めませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// この敵は自分に敵視を持っているか（＝攻撃されたか）。
    ///
    /// 一度でも持っていたら、しばらく覚えておく。
    /// </summary>
    public bool IsAggroOnMe(IGameObject obj)
    {
        this.RefreshEnmity();

        var now = DateTime.UtcNow;
        var id = obj.GameObjectId;

        // いま敵視リストに載っている。
        if (this.enmity.Contains((uint)obj.EntityId))
        {
            this.aggroSeen[id] = now;
            return true;
        }

        // 載っていないが、少し前に載っていた。
        if (this.aggroSeen.TryGetValue(id, out var seen))
        {
            if (now - seen <= AggroMemory)
            {
                return true;
            }

            this.aggroSeen.Remove(id);
        }

        return false;
    }

    /// <summary>
    /// いま参加している FATE の番号。参加していなければ 0。
    ///
    /// <b>レベルシンクの有無で 0 に化けさせない。</b>
    /// RSR はシンクのレベル上限を条件に入れているため、上限を超えると
    /// 0 を返す（DataCenter.cs:751）。ここでは番号だけを読む。
    /// </summary>
    public static ushort CurrentFateId()
    {
        try
        {
            var fm = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager.Instance();
            if (fm is null || fm->CurrentFate is null)
            {
                return 0;
            }

            return fm->CurrentFate->FateId;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>この敵が属している FATE の番号。FATE の敵でなければ 0。</summary>
    private static ushort FateIdOf(IGameObject obj)
    {
        try
        {
            return ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->FateId;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 狙ってよい敵か。
    ///
    /// <b>「FATE の敵」か「自分に敵視を持っている敵」のどちらか。</b>
    /// これが要望そのもの。前者が「自分から仕掛ける相手」、
    /// 後者が「反撃する相手」。
    /// </summary>
    /// <param name="obj">相手。</param>
    /// <param name="fateId">いま参加している FATE。0 なら FATE の敵は対象外。</param>
    /// <param name="fateCentre">FATE の中心。円で絞るのに使う。</param>
    /// <param name="fateRadius">FATE の半径。</param>
    public bool MayAttack(IGameObject obj, ushort fateId, Vector3 fateCentre, float fateRadius)
    {
        if (!IsLiveEnemy(obj))
        {
            return false;
        }

        return this.IsFateMob(obj, fateId, fateCentre, fateRadius) || this.IsAggroOnMe(obj);
    }

    /// <summary>
    /// この FATE の敵か。
    ///
    /// <b>FateId と円の両方で見る。</b>
    /// FateId だけだと、隣の FATE の敵を拾う（番号が違えば弾けるが、
    /// 読めないこともある）。円だけだと、たまたま通りかかった
    /// 野良を拾う。両方を要求すれば取り違えない。
    /// </summary>
    public bool IsFateMob(IGameObject obj, ushort fateId, Vector3 fateCentre, float fateRadius)
    {
        if (fateId == 0)
        {
            return false;
        }

        // **番号が読めたなら、それを信じる。**
        // 自分の FATE と違えば、隣の FATE の敵。BMR 側でも
        // 優先度 -2（無敵扱い）になるので、攻撃しても意味がない。
        var mobFate = FateIdOf(obj);

        if (mobFate != 0)
        {
            return mobFate == fateId;
        }

        // 番号が読めなかった。円の中にいるかで判断する。
        var flat = Vector2.Distance(
            new Vector2(obj.Position.X, obj.Position.Z),
            new Vector2(fateCentre.X, fateCentre.Z));

        return flat <= MathF.Max(fateRadius, 20f) + FateMobSlackMeters;
    }

    /// <summary>
    /// 生きていて、狙える敵か。
    ///
    /// <c>Combatant</c> が「敵・衛兵」（GameObject.cs:386-387）。
    /// ペット・チョコボ・プレイヤーは別の種別なので、これで弾ける。
    /// <c>BNpcPart</c>（弱点部位）は狙う相手ではないので入れない。
    ///
    /// <b>敵意も見る。</b>種別が Combatant でも、街の衛兵のように
    /// 敵ではないものが居る。
    /// </summary>
    private static bool IsLiveEnemy(IGameObject obj)
        => obj is IBattleNpc
        {
            BattleNpcKind: BattleNpcSubKind.Combatant,
            IsTargetable: true,
            IsDead: false,
        } npc
        && npc.CurrentHp > 0
        && npc.StatusFlags.HasFlag(StatusFlags.Hostile);

    /// <summary>
    /// 狙う相手を選んで、ハードターゲットに置く。
    ///
    /// <b>いまの相手が有効なら、変えない。</b>
    /// 毎フレーム選び直すと、撃破する前に別の敵へ移ってしまう。
    ///
    /// <b>選ぶ順は「大きい敵 → 近い敵」。</b>
    /// 最大 HP が大きいものはボスや強敵で、倒さないと達成度が伸びない。
    /// 同じなら近いほうから。NorthHornAutoFates も同じ順で選んでいる。
    /// </summary>
    /// <returns>置いた相手。居なければ null。</returns>
    public IGameObject? AcquireTarget(ushort fateId, Vector3 fateCentre, float fateRadius)
    {
        if (!Player.Available)
        {
            return null;
        }

        // いま狙っている相手がまだ有効なら、そのまま。
        if (Svc.Targets.Target is { } current &&
            this.MayAttack(current, fateId, fateCentre, fateRadius))
        {
            this.ownedTarget = current.GameObjectId;
            return current;
        }

        var here = Player.Position;

        IGameObject? best = null;
        var bestHp = 0u;
        var bestDistance = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (!this.MayAttack(obj, fateId, fateCentre, fateRadius))
            {
                continue;
            }

            var hp = obj is IBattleNpc npc ? npc.MaxHp : 0u;
            var distance = Vector3.Distance(here, obj.Position);

            if (best is null || hp > bestHp || (hp == bestHp && distance < bestDistance))
            {
                best = obj;
                bestHp = hp;
                bestDistance = distance;
            }
        }

        if (best is null)
        {
            return null;
        }

        Svc.Targets.Target = best;
        this.ownedTarget = best.GameObjectId;

        return best;
    }

    /// <summary>
    /// ターゲットを外す。
    ///
    /// <b>これを呼ばないと戦闘から抜けられない。</b>
    /// ターゲットが残っているとオートアタックが続き、戦闘中は
    /// マウントに乗れないので、飛んで脱出できない。
    ///
    /// BMR 側にターゲットを外す手立ては無い。<c>Plugin.SetTarget</c> は
    /// null を渡されると何もせずに戻る（Plugin.cs:432-437）。
    /// つまり自分で外すしかない。
    ///
    /// <b>他人が置いたターゲットは外さない。</b>
    /// 自分が置いたものだけを消す。利用者が手で選んだ相手を
    /// 勝手に外すと、操作を奪うことになる。
    /// </summary>
    public void ReleaseTarget()
    {
        try
        {
            if (this.ownedTarget != 0 &&
                Svc.Targets.Target is { } current &&
                current.GameObjectId == this.ownedTarget)
            {
                Svc.Targets.Target = null;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Target", $"ターゲットを外せませんでした: {ex.Message}");
        }

        this.ownedTarget = 0;
    }

    /// <summary>
    /// オートアタックが走っていれば止める。
    ///
    /// <b>ターゲットを外すだけでは足りないことがある。</b>
    /// ゲームは「武器を抜いていて対象がいる」あいだ殴り続ける。
    /// 対象を外せば止まるが、外れる前に次の対象を拾われることがある。
    ///
    /// 止め方は、ゲームと同じトグル（GeneralAction 1）を送ること。
    /// RSR の AutoAttackUpdater が同じ手を使っている
    /// （AutoAttackUpdater.cs:74-82）。
    ///
    /// <b>トグルなので連打してはいけない。</b>
    /// 入れたり切ったりを往復する。RSR は 500 ミリ秒の間隔を空けている。
    /// </summary>
    public void StopAutoAttack()
    {
        try
        {
            var ui = UIState.Instance();
            if (ui is null)
            {
                return;
            }

            if (!ui->WeaponState.AutoAttackState.IsAutoAttacking)
            {
                return;
            }

            if (!ECommons.Throttlers.EzThrottler.Throttle("AutoCollector.StopAutoAttack", AutoAttackToggleMs))
            {
                return;
            }

            var am = ActionManager.Instance();
            if (am is null)
            {
                return;
            }

            am->UseAction(ActionType.GeneralAction, AutoAttackToggleAction);
            this.anomalyLog.Info("Target", "オートアタックを止めました");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Target", $"オートアタックを止められませんでした: {ex.Message}");
        }
    }

    /// <summary>オートアタックの切り替え。ゲームの汎用アクション 1 番。</summary>
    private const uint AutoAttackToggleAction = 1;

    /// <summary>
    /// オートアタックを切り替える間隔。
    ///
    /// トグルなので、短くすると入れたり切ったりを往復する。
    /// RSR も 500 ミリ秒を空けている（AutoAttackUpdater.cs:50-51）。
    /// </summary>
    private const int AutoAttackToggleMs = 500;

    /// <summary>覚えていることを忘れる。周回を始め直すときに呼ぶ。</summary>
    public void Reset()
    {
        this.aggroSeen.Clear();
        this.enmity.Clear();
        this.enmityReadUtc = DateTime.MinValue;
        this.ownedTarget = 0;
    }

    /// <summary>
    /// いま自分に敵視を持っている敵の数。
    ///
    /// 「まだ絡まれているか」を見て、脱出してよいかを判断するのに使う。
    /// </summary>
    public int CountAggroOnMe()
    {
        this.RefreshEnmity();
        return this.enmity.Count;
    }
}
