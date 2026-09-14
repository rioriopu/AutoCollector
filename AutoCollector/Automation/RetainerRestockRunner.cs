using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoCollector.Automation;

public enum RestockStep
{
    Idle,

    /// <summary>呼び鈴まで歩いている。</summary>
    MoveToBell,

    /// <summary>呼び鈴に話しかけている。</summary>
    InteractBell,

    /// <summary>リテイナーの一覧が開くのを待っている。</summary>
    WaitRetainerList,

    /// <summary>リテイナーを選んでいる。</summary>
    SelectRetainer,

    /// <summary>「アイテムの受け渡し」を選んでいる。</summary>
    SelectEntrust,

    /// <summary>持ち物を調べて取り出している。</summary>
    Withdraw,

    /// <summary>品を右クリックしたメニューが出るのを待っている。</summary>
    OpenContextMenu,

    /// <summary>数量を入れている。</summary>
    InputQuantity,

    /// <summary>取り出しが鞄へ反映されるのを待っている。</summary>
    WaitWithdraw,

    /// <summary>リテイナーを閉じている。</summary>
    CloseRetainer,

    /// <summary>一覧を閉じている。</summary>
    CloseList,

    Done,
    Error,
}

/// <summary>取り出したい品 1 件。</summary>
public sealed class RestockRequest
{
    public required uint ItemId { get; init; }

    public required string Name { get; init; }

    /// <summary>まだ取り出す必要がある数。</summary>
    public int Remaining { get; set; }

    /// <summary>
    /// これが手に入らなかったときに代わりに取り出すもの。
    ///
    /// 黒麦粉のように自分で作る素材は、リテイナーに完成品が無いことがある。
    /// その場合は素材（黒麦）を取り出して自分で作ることになる。
    /// </summary>
    public IReadOnlyList<RestockRequest> Fallback { get; init; } = [];

    /// <summary>
    /// 右クリックのメニューで「リテイナーから受け取る」を選ぶ品か。
    ///
    /// **クリスタルのメニューには「個数指定」が出ない。**
    /// 選ぼうとして見つからず、取り出せずに次の相手へ進んでいた。
    ///
    /// ただし **「受け取る」を選んでも「いくつ受け取りますか？」は出る**（実測）。
    /// どちらを選んでも数値入力の段は必ず通す。
    /// </summary>
    public bool RetrieveAll { get; init; }
}

/// <summary>
/// リテイナーから素材を取り出す。
///
/// Artisan の「Restock Inventory From Retainers」と同じことを行う。
/// あちらは画面のボタンからしか呼べず、IPC も自動実行の設定も無いため、
/// 同じ手順を自分で踏む。
///
/// <code>
/// AutoRetainer を抑制
///   → 呼び鈴まで歩く → 話しかける
///     → リテイナーを選ぶ（持っていると分かっている人から）
///       → アイテムの受け渡し
///         → 目的の品を右クリック → 個数を指定して取る
///           → 閉じる → 次のリテイナーへ
///             → 一覧を閉じる → 抑制を解除
/// </code>
///
/// **Allagan Tools は使わない。**
/// リテイナーを開けば持ち物は <c>RetainerPage1..7</c> と <c>RetainerCrystals</c> から
/// 直接読める。開いたついでに控えておけば、次からは持っている人へ直接行ける。
/// 依存するプラグインを増やさずに済む。
///
/// **クリスタルは扱いが違う。**
/// 入れ物が別で、鞄の枠を使わない。右クリックのメニューにも「個数指定」が出ないため
/// 「リテイナーから受け取る」を選ぶ（<see cref="RestockRequest.RetrieveAll"/>）。
/// それでも個数は聞かれるので、数値入力の段は必ず通す。
/// </summary>
public sealed unsafe class RetainerRestockRunner(
    AnomalyLog anomalyLog,
    CurrencyService currency,
    MenuService menu,
    AutoRetainerIpc autoRetainer,
    RetainerInventoryStore inventoryStore,
    NavigationService navigation)
{
    /// <summary>
    /// 呼び鈴を探す範囲。
    ///
    /// **見えていれば拾う。足りないぶんは歩く。**
    /// 以前は 10m 以内しか見ておらず、しかも歩かずにその場から話しかけていた。
    /// 10m は話しかけられる距離より遠いため、実機では
    /// 「距離が離れています」が出続けるだけで進まなかった（2026-09-14 実測）。
    /// </summary>
    private const float BellSearchRange = 30f;

    /// <summary>この距離まで近づいてから話しかける。</summary>
    private const float BellInteractRange = 3.5f;

    /// <summary>
    /// リテイナーの持ち物が入る入れ物。
    ///
    /// **クリスタルは別の入れ物に入る。**
    /// 7 ページだけを見ていたため、クリスタルは持っていても見つけられず、
    /// 引き出しの対象にもできなかった。
    /// </summary>
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
        InventoryType.RetainerCrystals,
    ];

    /// <summary>この時間で終わらなければ諦める。取り残しても、握ったままにしない。</summary>
    private static readonly TimeSpan OverallLimit = TimeSpan.FromMinutes(5);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CurrencyService currency = currency;
    private readonly MenuService menu = menu;
    private readonly AutoRetainerIpc autoRetainer = autoRetainer;
    private readonly RetainerInventoryStore inventoryStore = inventoryStore;
    private readonly NavigationService navigation = navigation;

    private readonly List<RestockRequest> requests = [];

    /// <summary>まだ見ていないリテイナーの名前。上から順に開く。</summary>
    private readonly List<string> pendingRetainers = [];

    private string currentRetainer = string.Empty;
    private DateTime deadlineUtc = DateTime.MinValue;
    private DateTime stepDeadlineUtc = DateTime.MinValue;

    /// <summary>いま取り出そうとしている品。反映を確かめるまで覚えておく。</summary>
    private RestockRequest? activeRequest;

    /// <summary>取り出す前の鞄の所持数。増えた数で取り出せた数を数える。</summary>
    private int bagBefore;

    /// <summary>
    /// このリテイナーで取り出せなかった品。
    /// 同じ品を探し続けて進まなくなるのを防ぐ。リテイナーを移るたびに空にする。
    /// </summary>
    private readonly HashSet<uint> skippedHere = [];

    /// <summary>代わりの素材へ切り替えたか。1 段だけにして、際限なく辿らないようにする。</summary>
    private bool expandedFallback;

    /// <summary>呼び鈴へ向かう経路を頼んだか。頼み直しを防ぐ。</summary>
    private bool bellMoveIssued;

    /// <summary>持っていないと分かっていて開かなかった人数。取り逃したときの手掛かりになる。</summary>
    private int skippedKnownEmpty;

    /// <summary>クリスタルの入れ物の読み取り状況を書き残したか。1 回の実行で 1 度だけ。</summary>
    private bool notedCrystalContainer;

    /// <summary>メニューを開く操作を撃ったか。同じ操作を撃ち続けないための印。</summary>
    private bool contextMenuRequested;

    // いま開こうとしている枠。開く段と選ぶ段に分かれたので、間で持ち越す。
    private InventoryType pendingInventory;
    private int pendingSlot;
    private int pendingTake;
    private int pendingAvailable;
    private bool pendingRetrieveAll;

    public RestockStep Step { get; private set; } = RestockStep.Idle;

    public string StatusDetail { get; private set; } = string.Empty;

    public int Withdrawn { get; private set; }

    /// <summary>始められなかった理由。押しても動かないときに画面へ出す。</summary>
    public string LastFailure { get; private set; } = string.Empty;

    /// <summary>
    /// どこまで進んだかの記録。
    ///
    /// 詳細ログは既定で無効なうえ、押しても何も起きないときは
    /// そもそも何が起きたのかが分からない。画面にそのまま出す。
    /// </summary>
    public IReadOnlyList<string> Trace => this.trace;

    private readonly List<string> trace = [];

    /// <summary>記録に 1 行足す。古いものから捨てる。</summary>
    private void Note(string text)
    {
        this.trace.Add($"{DateTime.Now:HH:mm:ss.fff}  {text}");

        if (this.trace.Count > 40)
        {
            this.trace.RemoveAt(0);
        }
    }

    public bool IsRunning => this.Step is not (RestockStep.Idle or RestockStep.Done or RestockStep.Error);

    /// <summary>いま取り出そうとしているもの。表示用。</summary>
    public IReadOnlyList<RestockRequest> Requests => this.requests;

    /// <summary>取り出しを始める。</summary>
    public bool Start(IReadOnlyList<RestockRequest> wanted, out string reason)
    {
        this.LastFailure = string.Empty;
        this.trace.Clear();
        this.Note($"開始を要求されました（{wanted.Count} 種）");

        if (this.IsRunning)
        {
            reason = this.LastFailure = "すでに動いています";
            this.Note(reason);
            return false;
        }

        var targets = wanted.Where(x => x.ItemId != 0 && x.Remaining > 0).ToList();

        if (targets.Count == 0)
        {
            reason = this.LastFailure = "取り出すものがありません";
            this.Note(reason);
            return false;
        }

        if (!Player.Available)
        {
            reason = this.LastFailure = "プレイヤーの状態を読み取れません";
            this.Note(reason);
            return false;
        }

        if (FindBell() is null)
        {
            reason = this.LastFailure = $"近くに呼び鈴がありません（{DescribeNearby()}）";
            this.Note(reason);
            return false;
        }

        this.requests.Clear();
        this.requests.AddRange(targets);

        this.pendingRetainers.Clear();
        this.currentRetainer = string.Empty;
        this.Withdrawn = 0;
        this.activeRequest = null;
        this.expandedFallback = false;
        this.bellMoveIssued = false;
        this.skippedKnownEmpty = 0;
        this.notedCrystalContainer = false;
        this.contextMenuRequested = false;
        this.skippedHere.Clear();

        this.deadlineUtc = DateTime.UtcNow.Add(OverallLimit);

        // AutoRetainer が同じ呼び鈴を使おうとすると操作を取り合う。先に抑制する。
        this.autoRetainer.Suppress();

        this.anomalyLog.Info("Restock", $"リテイナーから取り出します（{targets.Count} 種）");
        this.Note($"AutoRetainer を抑制しました。{string.Join(" / ", targets.Select(x => $"{x.Name}×{x.Remaining}"))}");

        this.Move(RestockStep.MoveToBell, "呼び鈴へ向かっています", 60);
        reason = string.Empty;
        return true;
    }

    /// <summary>止める。抑制は必ず解除する。</summary>
    public void Stop(string reason)
    {
        if (!this.IsRunning)
        {
            return;
        }

        this.anomalyLog.Info("Restock", $"取り出しを止めます: {reason}");
        this.Finish(RestockStep.Done, reason);
    }

    /// <summary>Framework.Update から毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        if (!this.IsRunning)
        {
            return;
        }

        if (DateTime.UtcNow > this.deadlineUtc)
        {
            this.Finish(RestockStep.Error, "制限時間を超えました");
            return;
        }

        try
        {
            switch (this.Step)
            {
                case RestockStep.MoveToBell:
                    this.TickMoveToBell();
                    break;

                case RestockStep.InteractBell:
                    this.TickInteractBell();
                    break;

                case RestockStep.WaitRetainerList:
                    this.TickWaitRetainerList();
                    break;

                case RestockStep.SelectRetainer:
                    this.TickSelectRetainer();
                    break;

                case RestockStep.SelectEntrust:
                    this.TickSelectEntrust();
                    break;

                case RestockStep.Withdraw:
                    this.TickWithdraw();
                    break;

                case RestockStep.OpenContextMenu:
                    this.TickOpenContextMenu();
                    break;

                case RestockStep.InputQuantity:
                    this.TickInputQuantity();
                    break;

                case RestockStep.WaitWithdraw:
                    this.TickWaitWithdraw();
                    break;

                case RestockStep.CloseRetainer:
                    this.TickCloseRetainer();
                    break;

                case RestockStep.CloseList:
                    this.TickCloseList();
                    break;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Restock", $"取り出しで例外が出ました: {ex.Message}");
            this.Finish(RestockStep.Error, ex.Message);
        }
    }

    private void TickInteractBell()
    {
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
        {
            this.Move(RestockStep.WaitRetainerList, "リテイナーの一覧を待っています", 30);
            return;
        }

        if (this.Expired())
        {
            this.Finish(RestockStep.Error, "呼び鈴に話しかけられませんでした");
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.Bell", 2000))
        {
            return;
        }

        var bell = FindBell();

        if (bell is null)
        {
            this.Finish(RestockStep.Error, "近くに呼び鈴がありません");
            return;
        }

        // 離れたまま撃ち続けない。ゲームに「距離が離れています」を出させるだけで進まない。
        var distance = Vector3.Distance(bell.Position, Player.Position);

        if (distance > BellInteractRange)
        {
            this.Note($"呼び鈴から {distance:F1} 離れています。近寄り直します");
            this.Move(RestockStep.MoveToBell, "呼び鈴へ向かっています", 60);
            return;
        }

        this.Note($"呼び鈴に話しかけます（{bell.Name}・距離 {distance:F1}）");
        Svc.Targets.Target = bell;
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)bell.Address, false);
    }

    /// <summary>
    /// 呼び鈴まで歩く。
    ///
    /// **見えている ≠ 話しかけられる。**
    /// 以前は探す範囲を 10m に取り、そこから動かずに話しかけていた。
    /// 実際に話しかけられるのはもっと近い距離のため、
    /// 「距離が離れています」が出続けるだけで永久に進まなかった。
    /// </summary>
    /// <summary>
    /// いまの所持数。
    ///
    /// **クリスタルは鞄に入らない。** 専用の入れ物に入るため、
    /// 鞄を見る数え方では取り出しても増えたことにならず、
    /// 「取り出せなかった」と判断して次の相手へ進んでしまう。
    ///
    /// どちらの入れ物にあるかは品目で決まる。多いほうを採れば取り違えない。
    /// </summary>
    private int HeldOf(uint itemId)
    {
        var bag = this.currency.TryGetCount(itemId, out var have) ? have : 0;
        var crystals = this.currency.TryGetCrystalCount(itemId, out var stock) ? stock : 0;

        return Math.Max(bag, crystals);
    }

    /// <summary>自分が始めた移動だけを止める。頼み直せるよう印も消す。</summary>
    private void StopMoving()
    {
        if (!this.bellMoveIssued)
        {
            return;
        }

        this.bellMoveIssued = false;

        try
        {
            this.navigation.Stop();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Restock", $"移動を止められませんでした: {ex.Message}");
        }
    }

    private void TickMoveToBell()
    {
        var bell = FindBell();

        if (bell is null)
        {
            this.StopMoving();
            this.Finish(RestockStep.Error, $"近くに呼び鈴がありません（{DescribeNearby()}）");
            return;
        }

        var distance = Vector3.Distance(bell.Position, Player.Position);

        // もう届く。歩かない。
        if (distance <= BellInteractRange)
        {
            this.StopMoving();
            this.Move(RestockStep.InteractBell, "呼び鈴に話しかけています", 30);
            return;
        }

        if (!this.navigation.IsAvailable)
        {
            this.Finish(
                RestockStep.Error,
                $"呼び鈴まで {distance:F1} ありますが、vnavmesh が使えないため近寄れません");
            return;
        }

        // 経路は 1 度だけ頼む。毎フレーム頼み直すと積み上がって動かなくなる。
        if (!this.bellMoveIssued)
        {
            if (!this.navigation.BeginMove(bell.Position, BellInteractRange - 1f, out var failure))
            {
                this.Finish(RestockStep.Error, $"呼び鈴へ向かえません（{failure}）");
                return;
            }

            this.bellMoveIssued = true;
            this.Note($"呼び鈴へ向かいます（あと {distance:F1}）");
            return;
        }

        var status = this.navigation.Tick(bell.Position, BellInteractRange - 1f);

        switch (status)
        {
            case MoveStatus.Moving:
                this.StatusDetail = $"呼び鈴へ向かっています（あと {distance:F1}）";
                return;

            case MoveStatus.Arrived:
            case MoveStatus.ShortOfTarget:
                // 届く距離まで来たかどうかは、着いたという報告ではなく距離で確かめる。
                this.StopMoving();
                this.Move(RestockStep.InteractBell, "呼び鈴に話しかけています", 30);
                return;

            default:
                this.StopMoving();
                this.Finish(RestockStep.Error, $"呼び鈴まで行けませんでした（あと {distance:F1}）");
                return;
        }
    }

    private void TickWaitRetainerList()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) ||
            !GenericHelpers.IsAddonReady(addon))
        {
            if (this.Expired())
            {
                this.Finish(RestockStep.Error, "リテイナーの一覧が開きませんでした");
            }

            return;
        }

        // 初回だけ名前を集める。以後は上から順に開く。
        if (this.pendingRetainers.Count == 0 && string.IsNullOrEmpty(this.currentRetainer))
        {
            for (uint i = 0; i < 10; i++)
            {
                var retainer = RetainerManager.Instance()->GetRetainerBySortedIndex(i);

                if (retainer is null || retainer->RetainerId == 0)
                {
                    continue;
                }

                this.pendingRetainers.Add(retainer->NameString);
            }

            this.NarrowByKnownContents();

            this.anomalyLog.Info("Restock", $"リテイナー {this.pendingRetainers.Count} 人を順に見ます");
            this.Note($"回るリテイナー {this.pendingRetainers.Count} 人: {string.Join(" / ", this.pendingRetainers)}");
        }

        this.Move(RestockStep.SelectRetainer, "リテイナーを選んでいます", 30);
    }

    private void TickSelectRetainer()
    {
        // 取り出し終わっていれば、もう開かない。
        if (this.requests.All(x => x.Remaining <= 0))
        {
            this.Move(RestockStep.CloseList, "取り出しを終えました", 30);
            return;
        }

        if (this.pendingRetainers.Count == 0)
        {
            // 作れる素材が手に入らなかったなら、その素材を取りに行く。
            if (this.TryExpandFallback())
            {
                return;
            }

            // 飛ばした相手がいるなら、そう書く。「全員見た」と書くと、
            // 記録が古くて取り逃した場合に原因へ辿り着けない。
            this.Move(
                RestockStep.CloseList,
                this.skippedKnownEmpty > 0
                    ? $"見る相手を見終えました（持っていないと分かっている {this.skippedKnownEmpty} 人は開いていません）"
                    : "すべてのリテイナーを見終えました",
                30);
            return;
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) ||
            !GenericHelpers.IsAddonReady(addon))
        {
            if (this.Expired())
            {
                this.Finish(RestockStep.Error, "リテイナーの一覧が開いていません");
            }

            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.SelectRetainer", 1000))
        {
            return;
        }

        var name = this.pendingRetainers[0];

        var list = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.RetainerList(addon);

        foreach (var retainer in list.Retainers)
        {
            if (retainer.Name != name)
            {
                continue;
            }

            this.currentRetainer = name;
            this.pendingRetainers.RemoveAt(0);

            this.Note($"{name} を選びます");
            this.anomalyLog.Info("Restock", $"{name} を開きます");
            retainer.Select();

            this.Move(RestockStep.SelectEntrust, $"{name} の持ち物を開いています", 30);
            return;
        }

        // 一覧に見当たらない。次へ。
        this.anomalyLog.Warn("Restock", $"{name} が一覧に見つかりません。飛ばします");
        this.pendingRetainers.RemoveAt(0);
    }

    /// <summary>
    /// 覚えている持ち物から、回る相手と順番を絞る。
    ///
    /// **総当たりをやめるための要。**
    /// 実測では黒麦 1 種類のために 4 人を開閉して 14 秒かかっていた。
    ///
    /// 覚えていない場合や記録が古い場合は絞らない。
    /// 当てにならない記録で飛ばすと、あるはずのものを取り逃す。
    /// </summary>
    /// <summary>
    /// 回る相手を、覚えている持ち物で絞る。
    ///
    /// 相手を 3 つに分ける。
    ///
    /// | 区分 | 扱い |
    /// |---|---|
    /// | 持っていると分かっている | 回る（多い順に先へ） |
    /// | 持ち物を知らない・記録が古い | 回る（持っている人のあと） |
    /// | **持っていないと分かっている** | **飛ばす** |
    ///
    /// **飛ばすのが肝心。**
    /// 以前は「覚えている中に目的の品が無ければ全員を順に見る」としていたため、
    /// 誰も持っていないと分かりきっている場合でも全員のメニューを開いていた。
    /// 完成品から素材へ切り替えるときにもう一度全員を回るので、2 周ぶん無駄になる。
    ///
    /// 記録が古い相手は「知らない」側に入れる。開けばその場で覚え直す。
    /// </summary>
    private void NarrowByKnownContents()
    {
        var wanted = this.requests.Where(x => x.Remaining > 0).Select(x => x.ItemId).ToHashSet();

        if (wanted.Count == 0 || this.pendingRetainers.Count == 0)
        {
            return;
        }

        var holders = new List<(string Name, int Score)>();
        var unknown = new List<string>();
        var skipped = new List<string>();

        foreach (var name in this.pendingRetainers)
        {
            if (!this.inventoryStore.IsFresh(name))
            {
                unknown.Add(name);
                continue;
            }

            var score = wanted.Sum(itemId => this.inventoryStore.HeldBy(name, itemId));

            if (score > 0)
            {
                holders.Add((name, score));
            }
            else
            {
                skipped.Add(name);
            }
        }

        this.pendingRetainers.Clear();
        this.pendingRetainers.AddRange(holders.OrderByDescending(x => x.Score).Select(x => x.Name));
        this.pendingRetainers.AddRange(unknown);

        if (holders.Count > 0)
        {
            this.Note($"持っている人から回ります: {string.Join(" / ", holders.OrderByDescending(x => x.Score).Select(x => $"{x.Name}({x.Score})"))}");
        }

        if (unknown.Count > 0)
        {
            this.Note($"持ち物をまだ知らない人も見ます: {string.Join(" / ", unknown)}");
        }

        if (skipped.Count > 0)
        {
            this.skippedKnownEmpty = skipped.Count;
            this.Note($"持っていないと分かっている {skipped.Count} 人は飛ばします");
        }

        if (this.pendingRetainers.Count == 0)
        {
            this.Note("覚えている持ち物では、この素材を持っている人がいません");
        }
    }

    /// <summary>
    /// 手に入らなかった「作れる素材」を、その素材に置き換える。
    ///
    /// 黒麦粉がリテイナーに無ければ、黒麦を取りに行く。
    /// 1 段だけ。何段も辿ると取り出す量が読めなくなる。
    /// </summary>
    private bool TryExpandFallback()
    {
        if (this.expandedFallback)
        {
            return false;
        }

        var replacements = new List<RestockRequest>();

        foreach (var request in this.requests)
        {
            if (request.Remaining <= 0 || request.Fallback.Count == 0)
            {
                continue;
            }

            foreach (var fallback in request.Fallback)
            {
                if (fallback.Remaining > 0)
                {
                    replacements.Add(new RestockRequest
                    {
                        ItemId = fallback.ItemId,
                        Name = fallback.Name,
                        Remaining = fallback.Remaining,
                    });
                }
            }

            this.Note($"{request.Name} が {request.Remaining} 個足りません。素材を取りに行きます");
        }

        if (replacements.Count == 0)
        {
            return false;
        }

        this.expandedFallback = true;

        this.requests.Clear();
        this.requests.AddRange(replacements);

        this.Note($"取り出す対象を替えます: {string.Join(" / ", replacements.Select(x => $"{x.Name}×{x.Remaining}"))}");

        // もう一度すべてのリテイナーを回る。
        this.pendingRetainers.Clear();
        this.currentRetainer = string.Empty;
        this.skippedHere.Clear();

        for (uint i = 0; i < 10; i++)
        {
            var retainer = RetainerManager.Instance()->GetRetainerBySortedIndex(i);

            if (retainer is null || retainer->RetainerId == 0)
            {
                continue;
            }

            this.pendingRetainers.Add(retainer->NameString);
        }

        this.NarrowByKnownContents();
        return true;
    }

    private void TickSelectEntrust()
    {
        // 持ち物が読める状態になっていれば進む。
        if (IsRetainerInventoryReady())
        {
            this.Move(RestockStep.Withdraw, $"{this.currentRetainer} から取り出しています", 60);
            return;
        }

        if (this.Expired())
        {
            this.Finish(RestockStep.Error, "リテイナーの持ち物を開けませんでした");
            return;
        }

        if (!this.menu.IsMenuOpen() || !EzThrottler.Throttle("AutoCollector.Entrust", 800))
        {
            return;
        }

        // 「アイテムの受け渡し」。表記はゲームから引く。
        var text = AddonText(2378);

        if (string.IsNullOrEmpty(text))
        {
            this.anomalyLog.Warn("Restock", "アイテムの受け渡しの表記を引けませんでした");
            return;
        }

        if (!this.menu.TrySelectByText(text, out var failure))
        {
            this.Note($"「{text}」を選べません: {failure} / 選択肢: {string.Join(" / ", this.menu.ListEntries())}");
            this.anomalyLog.Warn("Restock", $"アイテムの受け渡しを選べませんでした: {failure}");
        }
        else
        {
            this.Note($"「{text}」を選びました");
        }
    }

    private void TickWithdraw()
    {
        // 開いたついでに持ち物を控える。次からは総当たりせずに済む。
        //
        // **何も持っていない相手も控える。**
        // 控えないと「知らない相手」のまま残り、取り出しのたびに開き直すことになる。
        if (IsRetainerInventoryReady()
            && !string.IsNullOrEmpty(this.currentRetainer)
            && TryReadOpenRetainerItems(out var contents))
        {
            this.inventoryStore.Remember(this.currentRetainer, contents);

            // クリスタルの入れ物だけ読めないことがあると、持っているのに
            // 「持っていない」と覚えて以後ずっと飛ばすことになる。1 度だけ書き残す。
            if (!this.notedCrystalContainer)
            {
                this.notedCrystalContainer = true;

                var crystals = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerCrystals);

                if (crystals is null || !crystals->IsLoaded)
                {
                    this.Note("クリスタルの入れ物を読めていません。クリスタルの記録は当てにできません");
                }
            }
        }

        if (!IsRetainerInventoryReady())
        {
            if (this.Expired())
            {
                this.Finish(RestockStep.Error, "リテイナーの持ち物を読めませんでした");
            }

            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.Withdraw", 1200))
        {
            return;
        }

        // このリテイナーにあって、まだ足りていない品を探す。
        //
        // 1 つ目が無かったら次を見る。ここを見落とすと、無い品を探し続けて進まなくなる。
        RestockRequest? request = null;
        var inventory = InventoryType.RetainerPage1;
        var slot = 0;
        var available = 0;

        foreach (var candidate in this.requests)
        {
            if (candidate.Remaining <= 0 || this.skippedHere.Contains(candidate.ItemId))
            {
                continue;
            }

            if (!TryFindInRetainer(candidate.ItemId, out inventory, out slot, out available))
            {
                continue;
            }

            request = candidate;
            break;
        }

        if (request is null)
        {
            this.Move(RestockStep.CloseRetainer, $"{this.currentRetainer} からは取り出し終えました", 30);
            return;
        }

        var take = Math.Min(request.Remaining, available);

        if (take <= 0)
        {
            this.skippedHere.Add(request.ItemId);
            return;
        }

        // 反映は鞄の所持数で確かめる。撃った回数では数えない。
        this.bagBefore = this.HeldOf(request.ItemId);
        this.activeRequest = request;

        this.Note($"{request.Name} を {take} 個取り出します（このリテイナーに {available} 個）");
        this.anomalyLog.Info("Restock", $"{request.Name} を {take} 個取り出します（{this.currentRetainer}）");

        // 開く操作と、開いたメニューから選ぶ操作は分ける。
        //
        // **同じフレームでは開いていない。**
        // 撃った直後に探して見つからず「メニューが出ませんでした」と諦めていたため、
        // 1 テンポ遅れて出たメニューが誰にも閉じられずに残っていた。
        // 画面上では、取り出し中にサブメニューが数秒ちらつく形で見えていた。
        this.pendingInventory = inventory;
        this.pendingSlot = slot;
        this.pendingTake = take;
        this.pendingAvailable = available;
        this.pendingRetrieveAll = request.RetrieveAll;
        this.contextMenuRequested = false;

        this.Move(RestockStep.OpenContextMenu, $"{request.Name} のメニューを開いています", 15);
    }

    /// <summary>
    /// 品を右クリックしたメニューを開き、出たら「受け取る」を選ぶ。
    ///
    /// **開いたら必ず片づける。**
    /// 選べずに諦めるときも閉じる。開きっぱなしにすると画面に残り、
    /// 次の操作にも被さる。
    /// </summary>
    private void TickOpenContextMenu()
    {
        // **自分で開く前に、出ているメニューへ触らない。**
        // 利用者が右クリックで開いたものに撃つと、意図しない操作になる。
        if (!this.contextMenuRequested)
        {
            this.contextMenuRequested = true;

            if (!this.RequestContextMenu(out var openFailure))
            {
                this.Note($"メニューを開けませんでした: {openFailure}");
                this.GiveUpOnCurrentItem();
            }

            return;
        }

        // 出ている。選ぶ。
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var contextMenu) &&
            GenericHelpers.IsAddonReady(contextMenu))
        {
            if (this.TrySelectRetrieve(contextMenu, out var selectFailure))
            {
                // **どちらを選んでも、必ず入力欄の段を通す。**
                // 出るかどうかは品によって変わる。決め打ちにすると、
                // 出たのに素通りして入力欄が開いたまま止まる。
                this.Move(RestockStep.InputQuantity, "個数を入れています", 15);
                return;
            }

            this.Note($"取り出しの操作に失敗: {selectFailure}");
            this.anomalyLog.Warn("Restock", $"取り出しの操作に失敗しました: {selectFailure}");

            this.CloseContextMenu(contextMenu);
            this.GiveUpOnCurrentItem();
            return;
        }

        // 撃ったのに出てこない。開きかけを残さないよう、閉じてから諦める。
        if (this.Expired())
        {
            this.Note("メニューが出ませんでした");
            this.CloseContextMenu(null);
            this.GiveUpOnCurrentItem();
        }
    }

    /// <summary>この品はこのリテイナーでは諦める。探し続けて進まなくなるのを防ぐ。</summary>
    private void GiveUpOnCurrentItem()
    {
        if (this.activeRequest is not null)
        {
            this.skippedHere.Add(this.activeRequest.ItemId);
        }

        this.activeRequest = null;
        this.Move(RestockStep.Withdraw, "取り出しを続けます", 60);
    }

    /// <summary>
    /// 開いてしまったメニューを閉じる。
    ///
    /// 選ばずに離れると画面に残る。次の操作にも被さるため、必ず通す。
    /// </summary>
    private void CloseContextMenu(AtkUnitBase* known)
    {
        try
        {
            var addon = known;

            if (addon is null)
            {
                if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out addon))
                {
                    return;
                }
            }

            if (addon is not null)
            {
                addon->Close(true);
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Restock", $"メニューを閉じられませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 個数の入力欄を待って、要る数を入れる。
    ///
    /// **入力欄が出るかどうかを決め打ちにしない。**
    /// 「すべて取る」を選んでも出ることがある。実測では、クリスタルは
    /// どちらを選んでも「いくつ受け取りますか？」が出た（2026-09-14）。
    /// 出ない前提で素通りしたため、入力欄が開いたまま 15 秒待って諦めていた。
    ///
    /// 出るか出ないかは**見て決める**。
    /// 出れば入れる。出ないまま所持数が増えていれば、そのまま通ったということ。
    /// </summary>
    private void TickInputQuantity()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var numeric) &&
            GenericHelpers.IsAddonReady(numeric))
        {
            if (!EzThrottler.Throttle("AutoCollector.Quantity", 600))
            {
                return;
            }

            // 相手が持っている数より多くは頼めない。
            var value = Math.Max(1, Math.Min(this.pendingTake, this.pendingAvailable));

            this.Note($"個数を入れます: {value}");
            Callback.Fire(numeric, true, value);
            this.Move(RestockStep.WaitWithdraw, "取り出しの反映を待っています", 15);
            return;
        }

        // 入力欄が出ないまま所持数が増えた。1 個だけの品などは聞かれずに渡される。
        if (this.HeldOf(this.activeRequest?.ItemId ?? 0) > this.bagBefore)
        {
            this.Move(RestockStep.WaitWithdraw, "取り出しの反映を待っています", 15);
            return;
        }

        if (this.Expired())
        {
            // 入力欄も出ず、増えてもいない。取り出せていない。
            this.Note("個数の入力欄が出ませんでした");
            this.anomalyLog.Warn("Restock", "個数の入力欄が出ませんでした");
            this.GiveUpOnCurrentItem();
        }
    }

    /// <summary>
    /// 鞄へ反映されるのを待つ。
    ///
    /// **撃った回数ではなく、実際に増えた数で数える。**
    /// 取り出せていないのに減らしてしまうと、足りないまま終わったことに気づけない。
    /// </summary>
    private void TickWaitWithdraw()
    {
        var request = this.activeRequest;

        if (request is null)
        {
            this.Move(RestockStep.Withdraw, "取り出しを続けます", 60);
            return;
        }

        var now = this.HeldOf(request.ItemId);
        var gained = now - this.bagBefore;

        if (gained > 0)
        {
            request.Remaining = Math.Max(0, request.Remaining - gained);
            this.Withdrawn += gained;

            this.StatusDetail = $"{request.Name} を {gained} 個取り出しました（残り {request.Remaining}）";
            this.anomalyLog.Info("Restock", this.StatusDetail);

            this.activeRequest = null;
            this.Move(RestockStep.Withdraw, "取り出しを続けます", 60);
            return;
        }

        if (this.Expired())
        {
            // 増えていない。この品はこのリテイナーでは諦める。
            this.anomalyLog.Warn("Restock", $"{request.Name} を取り出せませんでした");
            this.skippedHere.Add(request.ItemId);
            this.activeRequest = null;
            this.Move(RestockStep.Withdraw, "取り出しを続けます", 60);
        }
    }

    private void TickCloseRetainer()
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);

        if (agent is not null && agent->IsAgentActive())
        {
            if (!EzThrottler.Throttle("AutoCollector.CloseRetainer", 600))
            {
                return;
            }

            agent->Hide();
            return;
        }

        // 「やめる」を選んで一覧へ戻る。
        if (this.menu.IsMenuOpen())
        {
            if (!EzThrottler.Throttle("AutoCollector.Quit", 600))
            {
                return;
            }

            var text = AddonText(2383);

            if (!string.IsNullOrEmpty(text))
            {
                this.menu.TrySelectByText(text, out _);
            }

            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) &&
            GenericHelpers.IsAddonReady(addon))
        {
            this.currentRetainer = string.Empty;
            this.skippedHere.Clear();
            this.Move(RestockStep.SelectRetainer, "次のリテイナーへ移ります", 30);
            return;
        }

        if (this.Expired())
        {
            this.Finish(RestockStep.Error, "リテイナーを閉じられませんでした");
        }
    }

    private void TickCloseList()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) ||
            !GenericHelpers.IsAddonReady(addon))
        {
            var left = this.requests.Where(x => x.Remaining > 0).ToList();

            var detail = left.Count == 0
                ? $"必要なぶんをすべて取り出しました（{this.Withdrawn} 個）"
                : $"{string.Join(" / ", left.Take(3).Select(x => $"{x.Name} があと {x.Remaining}"))} 足りません";

            this.Finish(RestockStep.Done, detail);
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.CloseList", 600))
        {
            return;
        }

        // 一覧は -1 のコールバックで閉じる。
        Callback.Fire(addon, true, -1);
    }

    /// <summary>
    /// 持ち物の枠を右クリックして「取る」を選ぶ。
    ///
    /// 表示の並びは環境で変わるため、項目の位置を決め打ちにしない。
    /// 文字列と突き合わせて位置を求める。
    /// </summary>
    /// <remarks>
    /// **クリスタルには個数指定の項目が出ない。**
    /// Artisan の実装も、アイテム番号 19 以下（シャード・クリスタル・クラスター）と
    /// 1 個しかない品は「リテイナーから受け取る」だけを使っている
    /// （`Artisan/Tasks/TaskSelectRetainer.cs` の `OpenItemContextMenu`）。
    /// 個数指定を探して見つからず、取り出せずに次の相手へ進んでいた。
    /// </remarks>
    private bool RequestContextMenu(out string failure)
    {
        failure = string.Empty;

        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);

        if (retainerAgent is null)
        {
            failure = "リテイナーの画面が開いていません";
            return false;
        }

        var context = AgentInventoryContext.Instance();

        if (context is null)
        {
            failure = "メニューを開けません";
            return false;
        }

        context->OpenForItemSlot(this.pendingInventory, this.pendingSlot, 0, retainerAgent->GetAddonId());
        return true;
    }

    /// <summary>開いているメニューから「受け取る」を選ぶ。</summary>
    private bool TrySelectRetrieve(AtkUnitBase* contextMenu, out string failure)
    {
        failure = string.Empty;

        var context = AgentInventoryContext.Instance();

        if (context is null)
        {
            failure = "メニューを読み取れません";
            return false;
        }

        var take = this.pendingTake;
        var available = this.pendingAvailable;
        var preferAll = this.pendingRetrieveAll;

        // 98  すべて取る
        // 773 個数を指定して取る
        var retrieveAll = AddonText(98);
        var retrieveQuantity = AddonText(773);

        var indexAll = -1;
        var indexQuantity = -1;
        var position = 0;

        foreach (var parameter in context->EventParams)
        {
            if (parameter.Type != AtkValueType.String)
            {
                continue;
            }

            var label = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated(new IntPtr(parameter.String)).TextValue;

            if (label == retrieveAll)
            {
                indexAll = position;
            }

            if (label == retrieveQuantity)
            {
                indexQuantity = position;
            }

            position++;
        }

        // 全部取るなら「すべて取る」。一部なら「個数を指定して取る」。
        var useAll = preferAll || take >= available;
        var index = useAll ? indexAll : indexQuantity;

        // 望んだほうが無ければ、もう一方へ倒す。
        //
        // 品によっては個数指定が出ない。探して見つからないからと諦めると、
        // 目の前にあるのに取り出せずに次の相手へ進んでしまう。
        if (index < 0)
        {
            var alternative = useAll ? indexQuantity : indexAll;

            if (alternative < 0)
            {
                failure = "「リテイナーから受け取る」がメニューに見つかりません";
                return false;
            }

            this.Note(useAll
                ? "「すべて取る」が無いため、個数を指定して取ります"
                : "「個数を指定して取る」が無いため、すべて取ります");

            index = alternative;
            useAll = !useAll;
        }

        Callback.Fire(contextMenu, true, 0, index, 0, 0, 0);
        return true;
    }

    /// <summary>このリテイナーが持っているか。持っていれば場所と数を返す。</summary>
    private static bool TryFindInRetainer(uint itemId, out InventoryType inventory, out int slot, out int quantity)
    {
        inventory = InventoryType.RetainerPage1;
        slot = 0;
        quantity = 0;

        var manager = InventoryManager.Instance();

        if (manager is null)
        {
            return false;
        }

        foreach (var page in RetainerPages)
        {
            var container = manager->GetInventoryContainer(page);

            // **読み込み済みかどうかで飛ばさない。**
            // クリスタルの入れ物がそう報告しないことがあり、目の前にあるのに
            // 見つけられなくなる。Artisan の取り出しもここは見ていない
            // （`Artisan/Tasks/TaskSelectRetainer.cs` の `OpenItemContextMenu`）。
            // 枠ごとに中身を確かめるので、空の入れ物を読んでも害はない。
            if (container is null || container->Size <= 0)
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);

                if (item is null || item->ItemId != itemId || item->Quantity <= 0)
                {
                    continue;
                }

                inventory = page;
                slot = i;
                quantity = item->Quantity;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// いま開いているリテイナーの名前。呼び鈴の前にいるときだけ取れる。
    ///
    /// 開いているリテイナーは自分のすぐそばに立っている。Artisan も同じやり方で見ている。
    /// </summary>
    public static bool TryGetOpenRetainerName(out string name)
    {
        name = string.Empty;

        try
        {
            if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell] || !Player.Available)
            {
                return false;
            }

            var nearest = Svc.Objects
                .Where(x => x.ObjectKind == ObjectKind.Retainer)
                .OrderBy(x => Vector3.Distance(x.Position, Player.Position))
                .FirstOrDefault();

            if (nearest is null)
            {
                return false;
            }

            name = nearest.Name.ToString();
            return !string.IsNullOrEmpty(name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 開いているリテイナーの持ち物を全部読む。
    ///
    /// 取り出しのついでに控えておけば、次からは総当たりせずに済む。
    /// 読み取りだけで、状態は変えない。
    /// </summary>
    public static Dictionary<uint, int> ReadOpenRetainerItems()
        => TryReadOpenRetainerItems(out var items) ? items : [];

    /// <summary>
    /// 開いているリテイナーの持ち物を読む。**読めたかどうかを返す。**
    ///
    /// 中身が空であることと、まだ読めないことは別物。
    /// 区別しないと、何も持っていない相手をいつまでも「知らない」扱いにして、
    /// 取り出しのたびに開き直すことになる。
    /// </summary>
    public static bool TryReadOpenRetainerItems(out Dictionary<uint, int> items)
    {
        var totals = new Dictionary<uint, int>();
        items = totals;
        var readAny = false;

        try
        {
            var manager = InventoryManager.Instance();

            if (manager is null)
            {
                return false;
            }

            foreach (var page in RetainerPages)
            {
                var container = manager->GetInventoryContainer(page);

                if (container is null || !container->IsLoaded)
                {
                    continue;
                }

                readAny = true;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);

                    if (item is null || item->ItemId == 0 || item->Quantity <= 0)
                    {
                        continue;
                    }

                    var id = item->GetBaseItemId();
                    totals[id] = totals.GetValueOrDefault(id) + item->Quantity;
                }
            }
        }
        catch
        {
            return false;
        }

        return readAny;
    }

    /// <summary>
    /// リテイナーの持ち物の画面が開いているか。
    ///
    /// **入れ物が読めるかどうかで判定してはいけない。**
    /// RetainerPage1 は一度開くと読めるままになるため、
    /// 「アイテムの受け渡し」を選ぶ前から読める状態になっている。
    /// それを準備完了と見なしていたため、受け渡しを選ばずに素通りしていた。
    /// 実測では、選ぶ段階から取り出す段階まで 119 ミリ秒しか経っていなかった。
    ///
    /// 画面が開いているかで判定する。
    /// </summary>
    private static bool IsRetainerInventoryReady()
        => (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InventoryRetainer", out var small) &&
            GenericHelpers.IsAddonReady(small)) ||
           (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InventoryRetainerLarge", out var large) &&
            GenericHelpers.IsAddonReady(large));

    /// <summary>
    /// いまの呼び鈴の検出状況。画面に出して、押す前に分かるようにする。
    /// </summary>
    /// <summary>
    /// 話しかけられる呼び鈴が近くにあるか。
    ///
    /// **判定はここを使う。<see cref="DescribeBell"/> の戻り文字列で分岐しないこと。**
    /// 画面へ出す文言を直した瞬間に動作が変わる。実機でしか現れない壊れ方になる。
    /// </summary>
    public static bool IsBellNearby() => FindBell() is not null;

    public string DescribeBell()
    {
        if (!Player.Available)
        {
            return "プレイヤーの状態を読み取れません";
        }

        var bell = FindBell();

        if (bell is not null)
        {
            var distance = Vector3.Distance(bell.Position, Player.Position);
            return $"呼び鈴が見つかりました（{bell.Name}・距離 {distance:F1}）";
        }

        return $"呼び鈴が見つかりません（{DescribeNearby()}）";
    }

    /// <summary>
    /// 近くにある触れるものを並べる。
    /// 呼び鈴が見つからないとき、何が近くにあるのかが分からないと原因を追えない。
    /// </summary>
    private static string DescribeNearby()
    {
        try
        {
            var near = Svc.Objects
                .Where(x => x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject)
                .Select(x => (Name: x.Name.ToString(), Distance: Vector3.Distance(x.Position, Player.Position)))
                .Where(x => x.Distance <= 15f)
                .OrderBy(x => x.Distance)
                .Take(4)
                .Select(x => $"{x.Name} {x.Distance:F1}")
                .ToList();

            return near.Count == 0
                ? "近くに触れるものがありません"
                : $"近くにあるもの: {string.Join(" / ", near)}";
        }
        catch (Exception ex)
        {
            return $"周囲を読めません: {ex.Message}";
        }
    }

    /// <summary>近くの呼び鈴。無ければ null。</summary>
    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindBell()
    {
        var bellName = Svc.Data.GetExcelSheet<EObjName>()?.GetRowOrDefault(2000401)?.Singular.ExtractText() ?? string.Empty;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
            {
                continue;
            }

            var name = obj.Name.ToString();

            // 「呼び鈴」はシートから引く。英語環境や別表記も拾えるよう、含むかどうかで見る。
            var matches =
                (!string.IsNullOrEmpty(bellName) && name.Contains(bellName, StringComparison.Ordinal)) ||
                name.Contains("呼び鈴", StringComparison.Ordinal) ||
                name.Contains("Summoning Bell", StringComparison.OrdinalIgnoreCase);

            if (!matches || !obj.IsTargetable)
            {
                continue;
            }

            // 見えている範囲まで拾う。足りないぶんは歩いて近寄る。
            if (Vector3.Distance(obj.Position, Player.Position) <= BellSearchRange)
            {
                return obj;
            }
        }

        return null;
    }

    /// <summary>
    /// ゲームの表記を引く。日本語でも英語でも同じ番号で取れる。
    ///
    /// 角括弧から後ろは実行時に値が入る差し込み部分なので落とす。
    ///
    /// <code>
    /// 2378 「アイテムの受け渡し　[預託中：枠]」 → 「アイテムの受け渡し」
    /// </code>
    ///
    /// 落とさずに照合すると、実際の「アイテムの受け渡し　[預託中：15枠]」と
    /// 一致せず、選べないまま素通りする。
    /// </summary>
    private static string AddonText(uint rowId)
    {
        try
        {
            var text = Svc.Data.GetExcelSheet<Addon>()?.GetRowOrDefault(rowId)?.Text.ExtractText() ?? string.Empty;

            var bracket = text.IndexOfAny(['[', '［']);

            if (bracket > 0)
            {
                text = text[..bracket];
            }

            return text.Trim().Trim('　');
        }
        catch
        {
            return string.Empty;
        }
    }

    private void Move(RestockStep step, string detail, int seconds)
    {
        if (this.Step != step)
        {
            this.Note($"{step}: {detail}");
        }

        this.Step = step;
        this.StatusDetail = detail;
        this.stepDeadlineUtc = DateTime.UtcNow.AddSeconds(seconds);
    }

    private bool Expired() => DateTime.UtcNow > this.stepDeadlineUtc;

    /// <summary>終わる。抑制の解除はここでしか行わないので、必ず通す。</summary>
    private void Finish(RestockStep step, string detail)
    {
        this.Note($"終了: {detail}");

        // 自分が始めた移動を残さない。歩いたまま終わると、そのまま走り続ける。
        this.StopMoving();

        // 開きかけのメニューも残さない。画面に居座って次の操作に被さる。
        if (this.contextMenuRequested)
        {
            this.contextMenuRequested = false;
            this.CloseContextMenu(null);
        }

        this.Step = step;
        this.StatusDetail = detail;

        try
        {
            this.autoRetainer.Release();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Restock", $"AutoRetainer の抑制を解除できませんでした: {ex.Message}");
        }

        this.anomalyLog.Info("Restock", $"取り出しを終えます（{this.Withdrawn} 個）: {detail}");
    }
}
