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

    /// <summary>Talk を進めたか。自分が触ったものだけ閉じるために持つ。</summary>
    private bool advancedTalk;
    private DateTime started;

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
            if (DateTime.UtcNow - this.approachStarted > TimeSpan.FromSeconds(30))
            {
                this.talked.Add(npc.GameObjectId);
                this.CancelMovement(navigation);
                this.log.Warn("Fate", $"開始 NPC {npc.Name} に近づけませんでした。この出現では再試行しません");
                return false;
            }
            this.Detail = $"開始 NPC {npc.Name} へ向かっています";
            if (this.moving && navigation.Tick(npc.Position, 3f) == MoveStatus.Moving)
                return true;
            this.moving = false;
            if (DateTime.UtcNow >= this.nextMove)
            {
                this.nextMove = DateTime.UtcNow.AddSeconds(2);
                this.moving |= navigation.BeginMove(npc.Position, 3f, false, out _);
                if (this.moving) this.Destination = npc.Position;
            }
            return true;
        }

        this.CancelMovement(navigation);
        this.approachStarted = default;
        // 既に開いていた選択肢や会話は、この操作の所有物ではない。
        foreach (var name in new[] { "Talk", "SelectYesno", "SelectString", "SelectIconString" })
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && GenericHelpers.IsAddonReady(addon))
                return false;

        this.ownership.IsClaiming = true;
        var now = DateTime.UtcNow;
        if (this.interaction.StepInteract(npc))
        {
            // ターゲットを合わせた段階では記録しない。実際の Interact 送信後だけ。
            this.talked.Add(npc.GameObjectId);
            this.dialogStarted = now;
            this.dialogNpc = npc.EntityId;
            this.log.Info("Fate", $"開始 NPC に声をかけました: {npc.Name} ({npc.EntityId:X})");
        }
        else this.ownership.IsClaiming = false;
        this.Detail = $"開始 NPC {npc.Name} に声をかけています";
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
            this.advancedTalk = true;
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

        // Talk は所有権が付かないので、自分が進めたかどうかで判断する。
        // 開いたままにすると、次の FATE へ向かうあいだ操作が塞がれる。
        if (this.advancedTalk &&
            ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) &&
            ECommons.GenericHelpers.IsAddonReady(talk))
        {
            talk->Close(true);
        }

        this.dialogStarted = default;
        this.dialogNpc = 0;
        this.sawDialog = false;
        this.pickedMenu = false;
        this.advancedTalk = false;
        this.ownership.Clear();
    }

    public void CancelMovement(NavigationService navigation)
    {
        if (this.moving) navigation.Stop();
        this.moving = false;
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
    }

    public void Dispose() => this.ownership.Dispose();
}
