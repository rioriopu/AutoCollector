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
///   → 呼び鈴に話しかける
///     → リテイナーを選ぶ
///       → アイテムの受け渡し
///         → 目的の品を右クリック → 個数を指定して取る
///           → 閉じる → 次のリテイナーへ
///             → 一覧を閉じる → 抑制を解除
/// </code>
///
/// **Allagan Tools は使わない。**
/// リテイナーを開けば持ち物は <c>InventoryType.RetainerPage1..7</c> から直接読める。
/// どのリテイナーが何を持っているかを事前に知る必要はなく、
/// 順に開いて、あるものを取り出し、足りたらやめればよい。
/// 依存するプラグインを増やさずに済む。
///
/// **呼び鈴の近くにいることが前提。** 呼び鈴まで移動する処理は持たない。
/// </summary>
public sealed unsafe class RetainerRestockRunner(
    AnomalyLog anomalyLog,
    CurrencyService currency,
    MenuService menu,
    AutoRetainerIpc autoRetainer)
{
    /// <summary>リテイナーの持ち物が入る入れ物。クリスタルは扱わない。</summary>
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>この時間で終わらなければ諦める。取り残しても、握ったままにしない。</summary>
    private static readonly TimeSpan OverallLimit = TimeSpan.FromMinutes(5);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CurrencyService currency = currency;
    private readonly MenuService menu = menu;
    private readonly AutoRetainerIpc autoRetainer = autoRetainer;

    private readonly List<RestockRequest> requests = [];

    /// <summary>まだ見ていないリテイナーの名前。上から順に開く。</summary>
    private readonly List<string> pendingRetainers = [];

    private string currentRetainer = string.Empty;
    private DateTime deadlineUtc = DateTime.MinValue;
    private DateTime stepDeadlineUtc = DateTime.MinValue;

    /// <summary>数量を入れる対象。入力欄が出たときに使う。</summary>
    private int pendingQuantity;

    /// <summary>いま取り出そうとしている品。反映を確かめるまで覚えておく。</summary>
    private RestockRequest? activeRequest;

    /// <summary>取り出す前の鞄の所持数。増えた数で取り出せた数を数える。</summary>
    private int bagBefore;

    /// <summary>
    /// このリテイナーで取り出せなかった品。
    /// 同じ品を探し続けて進まなくなるのを防ぐ。リテイナーを移るたびに空にする。
    /// </summary>
    private readonly HashSet<uint> skippedHere = [];

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
        this.pendingQuantity = 0;
        this.activeRequest = null;
        this.skippedHere.Clear();

        this.deadlineUtc = DateTime.UtcNow.Add(OverallLimit);

        // AutoRetainer が同じ呼び鈴を使おうとすると操作を取り合う。先に抑制する。
        this.autoRetainer.Suppress();

        this.anomalyLog.Info("Restock", $"リテイナーから取り出します（{targets.Count} 種）");
        this.Note($"AutoRetainer を抑制しました。{string.Join(" / ", targets.Select(x => $"{x.Name}×{x.Remaining}"))}");

        this.Move(RestockStep.InteractBell, "呼び鈴に話しかけています", 30);
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

        this.Note($"呼び鈴に話しかけます（{bell.Name}）");
        Svc.Targets.Target = bell;
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)bell.Address, false);
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

            this.anomalyLog.Info("Restock", $"リテイナー {this.pendingRetainers.Count} 人を順に見ます");
            this.Note($"リテイナー {this.pendingRetainers.Count} 人: {string.Join(" / ", this.pendingRetainers)}");
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
            this.Move(RestockStep.CloseList, "すべてのリテイナーを見終えました", 30);
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
        this.bagBefore = this.currency.TryGetCount(request.ItemId, out var before) ? before : 0;
        this.activeRequest = request;

        this.Note($"{request.Name} を {take} 個取り出します（このリテイナーに {available} 個）");
        this.anomalyLog.Info("Restock", $"{request.Name} を {take} 個取り出します（{this.currentRetainer}）");

        if (!this.OpenContextAndRetrieve(inventory, slot, take, available, out var contextFailure))
        {
            this.Note($"取り出しの操作に失敗: {contextFailure}");
            this.anomalyLog.Warn("Restock", $"取り出しの操作に失敗しました: {contextFailure}");
            this.skippedHere.Add(request.ItemId);
            this.activeRequest = null;
            return;
        }

        // 全部取る場合は入力欄が出ない。一部だけ取る場合に出る。
        this.pendingQuantity = take < available ? take : 0;

        if (this.pendingQuantity > 0)
        {
            this.Move(RestockStep.InputQuantity, "個数を入れています", 15);
            return;
        }

        this.Move(RestockStep.WaitWithdraw, "取り出しの反映を待っています", 15);
    }

    private void TickInputQuantity()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var numeric) ||
            !GenericHelpers.IsAddonReady(numeric))
        {
            if (this.Expired())
            {
                // 入力欄が出ないまま時間切れ。取り出せていない可能性がある。
                this.anomalyLog.Warn("Restock", "個数の入力欄が出ませんでした");

                if (this.activeRequest is not null)
                {
                    this.skippedHere.Add(this.activeRequest.ItemId);
                }

                this.activeRequest = null;
                this.Move(RestockStep.Withdraw, "取り出しを続けます", 60);
            }

            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.Quantity", 600))
        {
            return;
        }

        Callback.Fire(numeric, true, this.pendingQuantity);
        this.pendingQuantity = 0;

        this.Move(RestockStep.WaitWithdraw, "取り出しの反映を待っています", 15);
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

        var now = this.currency.TryGetCount(request.ItemId, out var have) ? have : this.bagBefore;
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
    private bool OpenContextAndRetrieve(InventoryType inventory, int slot, int take, int available, out string failure)
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

        context->OpenForItemSlot(inventory, slot, 0, retainerAgent->GetAddonId());

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var contextMenu) ||
            !GenericHelpers.IsAddonReady(contextMenu))
        {
            failure = "メニューが出ませんでした";
            return false;
        }

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
        var useAll = take >= available;
        var index = useAll ? indexAll : indexQuantity;

        if (index < 0)
        {
            failure = useAll ? "「すべて取る」が見つかりません" : "「個数を指定して取る」が見つかりません";
            return false;
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

            if (container is null || !container->IsLoaded)
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

    /// <summary>リテイナーの持ち物が読める状態か。</summary>
    private static bool IsRetainerInventoryReady()
    {
        var manager = InventoryManager.Instance();

        if (manager is null)
        {
            return false;
        }

        var container = manager->GetInventoryContainer(InventoryType.RetainerPage1);
        return container is not null && container->IsLoaded;
    }

    /// <summary>
    /// いまの呼び鈴の検出状況。画面に出して、押す前に分かるようにする。
    /// </summary>
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

            // 話しかけられる距離はものによって違う。
            // 広めに取り、実際に届くかはゲームの応答で判断する。
            if (Vector3.Distance(obj.Position, Player.Position) <= 10f)
            {
                return obj;
            }
        }

        return null;
    }

    /// <summary>ゲームの表記を引く。日本語でも英語でも同じ番号で取れる。</summary>
    private static string AddonText(uint rowId)
    {
        try
        {
            return Svc.Data.GetExcelSheet<Addon>()?.GetRowOrDefault(rowId)?.Text.ExtractText() ?? string.Empty;
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
