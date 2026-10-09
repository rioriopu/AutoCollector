using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Automation;

/// <summary>ゲームが指定した開始 NPC への接近と、一度だけの対話。</summary>
public sealed unsafe class FateStarter : IDisposable
{
    private readonly InteractionService interaction;
    private readonly AddonOwnershipTracker ownership;
    private readonly AnomalyLog log;
    private readonly HashSet<ulong> talked = [];
    private (uint Territory, ushort Id, int Start) encounter;
    private DateTime dialogStarted;
    private DateTime nextDialogAction;
    private DateTime approachStarted;
    private DateTime nextMove;
    private bool moving;
    private uint dialogNpc;

    /// <summary>この会話で窓が一度でも出たか。猶予の長さを変えるのに使う。</summary>
    private bool sawDialog;

    /// <summary>選択肢を選んだか。同じ選択肢を押し続けない。</summary>
    private bool pickedMenu;

    /// <summary>いま向かっている立ち位置。</summary>
    private Vector3 standSpot;

    /// <summary>立ち位置が決まっているか。着いても用が足せなければ倒して、次を探す。</summary>
    private bool hasStandSpot;

    /// <summary>立ち位置をどこまで試したか。<see cref="NavigationService.TryPlanApproachSpot"/> が進める。</summary>
    private int standCursor;

    private DateTime started;

    /// <summary>立ち位置に着いたと認める距離。立ち位置そのものが目的地なので短くてよい。</summary>
    private const float StandArrivalRange = 1.5f;

    /// <summary>
    /// 立ち位置として認める、相手までの距離。
    ///
    /// <see cref="InteractionService.IsWithinInteractRange"/> は 5.5 ヤルムで見る。
    /// ぎりぎりを狙うと、着いた時点で判定から外れることがあるので余裕を取る。
    /// </summary>
    private const float StandMaxRange = 4.8f;

    /// <summary>開始 NPC へ寄るのに与える上限。</summary>
    private static readonly TimeSpan ApproachPatience = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 距離に入ってから、声をかけられるまでに与える上限。
    ///
    /// 戦闘が解けるのを待つぶん、少し長めに取る。
    /// </summary>
    private static readonly TimeSpan InteractPatience = TimeSpan.FromSeconds(25);

    /// <summary>距離に入って、声をかけ始めた時刻。</summary>
    private DateTime interactStarted;

    /// <summary>直前に記録した「声をかけられない理由」。同じ理由を毎フレーム書かないために持つ。</summary>
    private string lastBlocker = string.Empty;

    public FateStarter(AnomalyLog log)
    {
        this.log = log;
        this.interaction = new(log);
        this.ownership = new(log);
    }

    public string Detail { get; private set; } = string.Empty;
    public Vector3? Destination { get; private set; }
    public bool TimedOut => this.started != default && DateTime.UtcNow - this.started > TimeSpan.FromSeconds(60);

    public bool Tick(FateInfo fate, NavigationService navigation)
    {
        var key = (Svc.ClientState.TerritoryType, fate.Id, fate.StartTimeEpoch);
        if (this.encounter.Territory != key.TerritoryType || this.encounter.Id != key.Id ||
            (this.encounter.Start > 0 && key.StartTimeEpoch > 0 && this.encounter.Start != key.StartTimeEpoch))
        {
            this.CancelMovement(navigation);
            this.Reset();
        }
        this.encounter = key;

        if (this.started == default) this.started = DateTime.UtcNow;
        if (this.TimedOut) return false;

        if (fate.MotivationNpcId is 0 or 0xE0000000 ||
            (fate.State != FateState.Preparing && fate.Progress > 0)) return false;

        // MotivationNpc is an EntityId; the actor may be a friendly BattleNpc.
        var npc = Svc.Objects.FirstOrDefault(x => x.EntityId == fate.MotivationNpcId && x.IsTargetable);
        if (npc is null || this.talked.Contains(npc.GameObjectId)) return false;
        if (Vector3.Distance(npc.Position, fate.Position) > Math.Max(fate.Radius, 20) + 20) return false;

        if (!InteractionService.IsWithinInteractRange(npc))
        {
            if (this.approachStarted == default) this.approachStarted = DateTime.UtcNow;
            if (DateTime.UtcNow - this.approachStarted > ApproachPatience)
            {
                this.talked.Add(npc.GameObjectId);
                this.CancelMovement(navigation);
                this.log.Warn("Fate", $"開始 NPC {npc.Name} に近づけませんでした。この出現では再試行しません");
                return false;
            }

            this.Detail = $"開始 NPC {npc.Name} へ向かっています";

            // **NPC の座標そのものを目的地にしない。**
            //
            // イエロージャケットのように柵やカウンターの内側に立つ NPC は
            // ナビメッシュに乗らない。その座標へ頼むと、vnavmesh は
            // 近づけるところまで行って終わり、対話できる距離に入らない。
            // 以前はそれを検知できず、同じ場所へ 2 秒ごとに頼み直して
            // 棒立ちのまま時間切れになっていた（2026-10-09 実機）。
            //
            // 相手の周りの「立てる場所」を、手前の側から順に当たっていく。
            if (this.moving)
            {
                var status = navigation.Tick(this.standSpot, StandArrivalRange);

                if (status == MoveStatus.Moving)
                {
                    return true;
                }

                // 経路が終わった。ここで用が足せるかは、次のフレームの
                // IsWithinInteractRange が決める。足せなければ次の立ち位置へ。
                navigation.Stop();
                this.moving = false;
                this.hasStandSpot = false;
                this.nextMove = DateTime.UtcNow.AddMilliseconds(300);

                this.log.Info(
                    "Fate",
                    $"{npc.Name} への立ち位置に着きました（{status}）。" +
                    $"残り {Vector3.Distance(Player.Position, npc.Position):F1} ヤルム");

                return true;
            }

            if (DateTime.UtcNow < this.nextMove)
            {
                return true;
            }

            this.nextMove = DateTime.UtcNow.AddMilliseconds(500);

            // 立ち位置がまだ無ければ探す。見つからなければ、もう寄りようがない。
            if (!this.hasStandSpot)
            {
                if (!navigation.TryPlanApproachSpot(
                        npc.Position, StandMaxRange, ref this.standCursor, out var spot))
                {
                    this.talked.Add(npc.GameObjectId);
                    this.CancelMovement(navigation);
                    this.log.Warn(
                        "Fate",
                        $"開始 NPC {npc.Name} に話しかけられる立ち位置が見つかりませんでした。" +
                        "この出現では再試行しません");

                    return false;
                }

                this.standSpot = spot;
                this.hasStandSpot = true;
            }

            // 断られたら（経路探索が混んでいる等）、立ち位置は変えずに次のフレームで出し直す。
            this.moving = navigation.BeginMove(this.standSpot, StandArrivalRange, false, out _);

            if (this.moving)
            {
                this.Destination = this.standSpot;
            }

            return true;
        }

        this.CancelMovement(navigation);
        this.approachStarted = default;

        // 既に開いていた選択肢や会話は、この操作の所有物ではない。
        foreach (var name in new[] { "Talk", "SelectYesno", "SelectString", "SelectIconString" })
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && GenericHelpers.IsAddonReady(addon))
                return false;

        // **声をかける側にも時間を切る。**
        //
        // StepInteract は「硬直中」「戦闘中」「まだ狙えていない」で
        // false を返すだけなので、理由が解消しないかぎり永久に回る。
        // ここには上限が無く、東ラノシアの開始 NPC で止まったままになっていた
        // （2026-10-09 実機。FATE の前に雑魚へ絡まれていると戦闘が解けない）。
        if (this.interactStarted == default) this.interactStarted = DateTime.UtcNow;

        var blocker = InteractionService.DescribeInteractBlocker(npc);

        if (DateTime.UtcNow - this.interactStarted > InteractPatience)
        {
            this.talked.Add(npc.GameObjectId);
            this.log.Warn(
                "Fate",
                $"開始 NPC {npc.Name} に声をかけられませんでした（{(blocker.Length > 0 ? blocker : "理由不明")}）。" +
                "この出現では再試行しません");

            this.interactStarted = default;
            return false;
        }

        // 理由が変わったときだけ記録する。毎フレーム書くとログが埋まる。
        if (blocker != this.lastBlocker)
        {
            this.lastBlocker = blocker;

            if (blocker.Length > 0)
            {
                this.log.Info("Fate", $"{npc.Name} にまだ声をかけられません: {blocker}");
            }
        }

        this.ownership.IsClaiming = true;
        var now = DateTime.UtcNow;
        if (this.interaction.StepInteract(npc))
        {
            // ターゲットを合わせた段階では記録しない。実際の Interact 送信後だけ。
            this.talked.Add(npc.GameObjectId);
            this.dialogStarted = now;
            this.dialogNpc = npc.EntityId;
            this.interactStarted = default;
            this.log.Info("Fate", $"開始 NPC に声をかけました: {npc.Name} ({npc.EntityId:X})");
        }
        else this.ownership.IsClaiming = false;

        this.Detail = blocker.Length > 0
            ? $"開始 NPC {npc.Name} に声をかけています（{blocker}）"
            : $"開始 NPC {npc.Name} に声をかけています";

        return true;
    }

    public bool TickDialog(FateScanner scanner)
    {
        if (this.dialogStarted == default) return false;
        if (!Player.Available || !GenericHelpers.IsScreenReady() ||
            Svc.ClientState.TerritoryType != this.encounter.Territory ||
            DateTime.UtcNow - this.dialogStarted > TimeSpan.FromSeconds(20))
        {
            this.EndDialog();
            return false;
        }

        var fate = scanner.GetById(this.encounter.Id);
        if (fate is null || !fate.IsActive || fate.Progress > 0 ||
            Svc.Condition[ConditionFlag.InCombat] || scanner.FindNearestMob(fate.Id, Player.Position) is not null)
        {
            this.EndDialog();
            return false;
        }

        if (DateTime.UtcNow < this.nextDialogAction) return true;
        this.nextDialogAction = DateTime.UtcNow.AddMilliseconds(400);

        // **Talk は所有権で見分けられない。**
        //
        // AddonLifecycle の PostSetup は、その名前のアドオンが
        // 「組み立てられた」ときだけ発火する。Talk は一度作られたあと
        // 使い回されるため、2 回目以降の会話では発火しない。
        // そのため所有権が付かず、進めることも閉じることもできないまま
        // 窓が開いた状態で止まっていた（2026-10-08 実機）。
        //
        // ここへ来るのは、自分が Interact を撃った直後の 20 秒以内だけ。
        // その窓で開いている Talk は自分のものとみなしてよい。
        // 「はい／いいえ」と選択肢は作り直されるので、所有権で見分けられる。
        // 誤って押すと取り返しがつかないのはそちらなので、判定は残す。
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) &&
            ECommons.GenericHelpers.IsAddonReady(talk))
        {
            this.sawDialog = true;
            return this.interaction.TryAdvanceTalk();
        }

        if (this.ownership.TryGetOwnedSince("SelectYesno", this.dialogStarted, out var yesno))
        {
            this.sawDialog = true;
            Callback.Fire(yesno, true, 0);
            return true;
        }

        // **選択肢も進める。**
        //
        // 開始役が「手伝う／やめておく」のような選択肢を出す FATE がある。
        // Talk と SelectYesno しか見ていなかったため、ここで止まり、
        // 猶予が切れると窓を閉じて二度と話しかけなかった。
        //
        // 先頭を選ぶ。開始役の選択肢は「始める」が先頭に来る。
        // 何を選んだかは記録に残すので、違っていれば追える。
        foreach (var name in new[] { "SelectString", "SelectIconString" })
        {
            if (!this.ownership.TryGetOwnedSince(name, this.dialogStarted, out var menu))
            {
                continue;
            }

            this.sawDialog = true;

            if (this.pickedMenu)
            {
                return true;
            }

            this.pickedMenu = true;
            this.log.Info("Fate", $"開始役の選択肢（{name}）の先頭を選びます");
            Callback.Fire(menu, true, 0);
            return true;
        }

        // **窓が出るまでの猶予。**
        //
        // 以前は 3 秒で諦めていた。話しかけてから窓が開くまでは
        // 通信の往復ぶんかかるうえ、混んでいるときはさらに延びる。
        // 短すぎると、出かかった窓を閉じて「止まったまま」に見える。
        //
        // 一度でも窓が出ていれば、次のページを待つ猶予として使う。
        var grace = this.sawDialog ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(8);
        if (DateTime.UtcNow - this.dialogStarted < grace) return true;

        if (!this.sawDialog)
        {
            this.log.Warn("Fate", "開始役に声をかけましたが、会話の窓が出ませんでした");
        }

        this.EndDialog();
        return false;
    }

    private void EndDialog()
    {
        foreach (var name in new[] { "SelectYesno", "SelectString", "SelectIconString" })
            if (this.ownership.TryGetOwnedSince(name, this.dialogStarted, out var addon)) addon->Close(true);

        // **自分が会話を始めたなら、開いている Talk は閉じる。**
        //
        // 以前は「自分が送ったか」で判断していた。撃った直後に
        // プラグインが入れ替わると、窓が開く前に消えることになる。
        // 新しい側は会話を始めた覚えがないので送らず、誰も閉じない。
        // 会話中は移動もキーコマンドも受け付けないので、
        // 操作不能のまま取り残される（2026-10-08 実機）。
        //
        // 撃った時点から閉じる対象にする。
        if (this.dialogStarted != default &&
            ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) &&
            ECommons.GenericHelpers.IsAddonReady(talk))
        {
            talk->Close(true);
        }

        this.dialogStarted = default;
        this.dialogNpc = 0;
        this.sawDialog = false;
        this.pickedMenu = false;
        this.ownership.Clear();
    }

    public void CancelMovement(NavigationService navigation)
    {
        // **moving を見てから止めない。**
        //
        // 経路が終わった時点で moving は倒れる。そこで条件を付けていたため、
        // 止める側が一度も呼ばれず、BMR から借りた移動権が返らないことがあった。
        // Stop() は二重に呼んでも害が無い。
        navigation.Stop();

        this.moving = false;
        this.hasStandSpot = false;
        this.Destination = null;
    }

    public void Reset()
    {
        this.EndDialog();
        this.talked.Clear();
        this.encounter = default;
        this.approachStarted = default;
        this.nextMove = default;
        this.started = default;
        this.hasStandSpot = false;
        this.standCursor = 0;
        this.standSpot = default;
        this.interactStarted = default;
        this.lastBlocker = string.Empty;
    }

    /// <summary>
    /// 開いたままの会話を閉じる。
    ///
    /// <b>取り残されたときの逃げ道。</b>
    /// 会話中は移動もキーコマンドも受け付けないため、
    /// 誰も送らない窓が残ると操作不能になる。
    /// </summary>
    /// <returns>閉じたら true。</returns>
    public bool CloseStuckDialog()
    {
        var closed = false;

        foreach (var name in new[] { "Talk", "SelectYesno", "SelectString", "SelectIconString" })
        {
            if (ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) &&
                ECommons.GenericHelpers.IsAddonReady(addon))
            {
                addon->Close(true);
                closed = true;
            }
        }

        this.Reset();
        return closed;
    }

    public void Dispose() => this.ownership.Dispose();
}
