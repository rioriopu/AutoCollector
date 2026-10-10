using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Automation;

/// <summary>
/// NPC に話しかけて始まる FATE で、NPC へ近づいて話しかける。
///
/// <b>ゲームが指定した開始役から試し、3 秒で始まらなければ周りの別の NPC へ（利用者 2026-10-10）。</b>
/// 誰に話しかけるかは <see cref="StartAttempts"/> が決める。指定外の NPC の「はい／いいえ」と選択肢は、
/// FATE を始める文のときだけ押す（<see cref="StartPrompt"/>）。
/// </summary>
public sealed unsafe class FateStarter : IDisposable
{
    private readonly InteractionService interaction;
    private readonly AddonOwnershipTracker ownership;
    private readonly AnomalyLog log;
    private readonly StartAttempts attempts = new();
    private (uint Territory, ushort Id, int Start) encounter;

    /// <summary>この湧きの開始を試し始めた時刻。開始役が見えるのを待つのに使う（<see cref="started"/> は NPC ごとに数え直す）。</summary>
    private DateTime encounterStarted;

    /// <summary>いま話している相手がゲームの指定した開始役か。指定外なら確認の窓をむやみに押さない。</summary>
    private bool dialogDesignated;

    /// <summary>話しかけている FATE の名前。指定外の NPC の確認の窓を押してよいかの判断に使う。</summary>
    private string fateName = string.Empty;

    /// <summary>いま向かっている・話しかけた NPC の名前（記録用）。</summary>
    private string currentNpcName = string.Empty;
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

        var now = DateTime.UtcNow;
        if (this.started == default) this.started = now;
        if (this.encounterStarted == default) this.encounterStarted = now;
        if (this.TimedOut) return false;

        // **NPC に話しかけて始まる FATE だけ。** 準備中（Preparing）で、開始役の番号がある。
        // AutoFATEGrind の AwaitsNpcStart と同じ見分け方（FateScanner.cs:16-17）。始まったら何もしない。
        if (fate.State != FateState.Preparing || fate.MotivationNpcId is 0 or 0xE0000000) return false;
        this.fateName = fate.Name;

        // 話し終えて、始まるのを待っている（3 秒）。
        if (this.attempts.Waiting(now))
        {
            this.Detail = $"{this.currentNpcName} に話しかけました。{fate.Name} が始まるのを待っています";
            return true;
        }

        // 3 秒たっても始まらない。別の NPC へ。
        if (this.attempts.Expired(now))
        {
            this.log.Info(
                "Fate",
                $"{this.currentNpcName} に話しかけて {StartAttempts.StartWait.TotalSeconds:F0} 秒たっても {fate.Name} が始まらないため、別の NPC に話しかけます");
            this.attempts.Next();
            this.CancelMovement(navigation);
            this.approachStarted = default;

            // 進みが無い時間の上限（TimedOut）は、次の NPC を試し始めた所から数え直す。
            this.started = now;
        }

        // **話しかける候補。** ゲームが指定した開始役（BattleNpc のこともある）と、FATE の周りの EventNpc。
        // 範囲は今までの開始役の判定と同じ（円の半径か 20m の大きいほう＋20m）。
        var reach = Math.Max(fate.Radius, 20) + 20;
        var objects = new Dictionary<ulong, IGameObject>();
        var candidates = new List<StartCandidate>();
        foreach (var x in Svc.Objects)
        {
            if (!x.IsTargetable)
            {
                continue;
            }

            var designated = x.EntityId == fate.MotivationNpcId;
            if (!designated && x.ObjectKind != ObjectKind.EventNpc)
            {
                continue;
            }

            if (Vector3.Distance(x.Position, fate.Position) > reach)
            {
                continue;
            }

            objects[x.GameObjectId] = x;
            candidates.Add(new StartCandidate(x.GameObjectId, Vector3.Distance(Player.Position, x.Position), designated));
        }

        var (step, id) = this.attempts.Choose(candidates, now - this.encounterStarted);
        if (step == StartStep.Wait)
        {
            this.Detail = $"{fate.Name} の開始役が現れるのを待っています";
            return true;
        }

        if (step == StartStep.None)
        {
            // 全員に話しかけた。ここで止めず、周回へ戻す（始まらなければ TimedOut で一時的に見送る）。
            return false;
        }

        var npc = objects[id];
        var designatedNpc = npc.EntityId == fate.MotivationNpcId;
        var role = designatedNpc ? "開始 NPC" : "近くの NPC";
        this.currentNpcName = npc.Name.TextValue;

        if (!InteractionService.IsWithinInteractRange(npc))
        {
            if (this.approachStarted == default) this.approachStarted = now;
            if (now - this.approachStarted > TimeSpan.FromSeconds(30))
            {
                // 近づけない。この NPC はあきらめて次へ（FATE そのものはあきらめない）。
                this.attempts.Next();
                this.CancelMovement(navigation);
                this.approachStarted = default;
                this.log.Warn("Fate", $"{role} {npc.Name} に近づけませんでした。別の NPC を試します");
                return true;
            }
            this.Detail = $"{role} {npc.Name} へ向かっています";
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

        // 話しかけた後に窓が閉じたが、まだ 3 秒の待ちに入っていない（窓が出ないまま終わった等）。話しかけ直さない。
        if (this.attempts.Talked)
        {
            this.attempts.DialogEnded(now);
            return true;
        }

        this.ownership.IsClaiming = true;
        if (this.interaction.StepInteract(npc))
        {
            // ターゲットを合わせた段階では記録しない。実際の Interact 送信後だけ。
            this.attempts.MarkTalked();
            this.dialogStarted = now;
            this.dialogNpc = npc.EntityId;
            this.dialogDesignated = designatedNpc;
            this.log.Info("Fate", $"{role} に声をかけました: {npc.Name} ({npc.EntityId:X}・{this.attempts.TriedCount + 1} 人目)");
        }
        else this.ownership.IsClaiming = false;
        this.Detail = $"{role} {npc.Name} に声をかけています";
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

            // **指定外の NPC では、FATE を始める確認のときだけ「はい」。** それ以外は閉じる（運び屋の移動などを押さない）。
            if (!this.dialogDesignated)
            {
                var text = new AddonMaster.SelectYesno((nint)yesno).Text;
                if (!StartPrompt.MayAccept(text, this.fateName))
                {
                    this.log.Info("Fate", $"近くの NPC の確認（{text}）は FATE の開始ではないため閉じます");
                    yesno->Close(true);
                    return true;
                }
            }

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

            // **指定外の NPC では、FATE を始める選択肢だけを選ぶ。** 無ければ閉じる（店などを開かない）。
            if (!this.dialogDesignated)
            {
                var entries = name == "SelectString"
                    ? new AddonMaster.SelectString((nint)menu).Entries.Select(x => x.Text ?? string.Empty).ToArray()
                    : new AddonMaster.SelectIconString((nint)menu).Entries.Select(x => x.Text ?? string.Empty).ToArray();
                var index = Array.FindIndex(entries, x => StartPrompt.MayAccept(x, this.fateName));
                if (index < 0)
                {
                    this.log.Info("Fate", $"近くの NPC の選択肢（{string.Join("／", entries)}）に FATE の開始が無いため閉じます");
                    menu->Close(true);
                    return true;
                }

                this.log.Info("Fate", $"近くの NPC の選択肢（{name}）から「{entries[index]}」を選びます");
                Callback.Fire(menu, true, index);
                return true;
            }

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
        //
        // 指定外の NPC は、窓が出なければ 3 秒で次へ（話しかけても何も起きない NPC がいる。利用者の「3 秒」）。
        var grace = this.sawDialog || !this.dialogDesignated ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(8);
        if (DateTime.UtcNow - this.dialogStarted < grace) return true;

        if (!this.sawDialog)
        {
            this.log.Warn("Fate", $"{(this.dialogDesignated ? "開始役" : "近くの NPC")}に声をかけましたが、会話の窓が出ませんでした");
        }

        this.EndDialog();
        return false;
    }

    private void EndDialog()
    {
        // **ここから 3 秒、FATE が始まるのを待つ**（StartAttempts）。
        // 窓が出なかったときは、話しかけた時点から数える（何も起きない NPC の前で 3 秒余計に待たない）。
        if (this.dialogStarted != default)
        {
            this.attempts.DialogEnded(this.sawDialog ? DateTime.UtcNow : this.dialogStarted);
        }

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
        this.attempts.Reset();
        this.encounter = default;
        this.encounterStarted = default;
        this.approachStarted = default;
        this.nextMove = default;
        this.started = default;
        this.dialogDesignated = false;
        this.currentNpcName = string.Empty;
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
