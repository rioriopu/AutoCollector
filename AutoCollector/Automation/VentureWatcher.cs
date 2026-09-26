using System;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.Throttlers;

namespace AutoCollector.Automation;

/// <summary>ベンチャー回収のいまの段階。</summary>
public enum VentureStep
{
    /// <summary>見張っていない。</summary>
    Off,

    /// <summary>見張っている。回収の用事は無い。</summary>
    Watching,

    /// <summary>回収したいが、戦闘や納品の切れ目を待っている。</summary>
    WaitingForBreak,

    /// <summary>街へ移動している（デジョンまたはテレポ）。</summary>
    Traveling,

    /// <summary>呼び鈴で回収している。</summary>
    Collecting,

    /// <summary>回収が済んだ。呼び出し側が周回へ戻す。</summary>
    Done,
}

/// <summary>
/// 周回中にベンチャーを見張り、回収できるようになったら街へ戻って回収する。
///
/// <b>回収そのものは AutoRetainer に任せる。</b>
/// 呼び鈴を開けば AutoRetainer が自分で回収する。こちらの役目は
/// 「街へ行く」「呼び鈴を開く」「終わるのを待つ」「閉じる」だけ。
///
/// <b>デジョンとテレポを使い分ける。</b>
/// 行き先がホームタウンと同じならデジョン（無料）、違えばテレポ。
///
/// <b>切れ目まで待つ。</b>
/// 回収できるようになっても、戦闘中や納品中は抜けない。
/// 途中で抜けると、参加していた FATE の報酬を落とす。
/// GbrVentureRelay が採集ノードの切れ目を待つのと同じ考え方
/// （知見 3-4「止めてよいのは切れ目だけ」）。
///
/// この作りは <c>C:\ソース\GbrVentureRelay</c> を参考にしている。
/// あちらは GatherBuddyReborn の自動採集が相手で、実機で確認済み。
/// </summary>
public sealed class VentureWatcher(
    AnomalyLog anomalyLog,
    FateTrace trace,
    AutoRetainerIpc retainer,
    LifestreamIpc lifestream,
    HomeTownService towns,
    VentureBellRunner bells)
{
    /// <summary>ベンチャーの状態を聞く間隔。毎フレーム聞く必要はない。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>切れ目を待つ上限。過ぎたら戦闘中でも抜ける。</summary>
    private static readonly TimeSpan BreakPatience = TimeSpan.FromMinutes(3);

    /// <summary>街へ着くのを待つ上限。</summary>
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(90);

    /// <summary>回収が終わるのを待つ上限。</summary>
    private static readonly TimeSpan CollectTimeout = TimeSpan.FromMinutes(5);

    /// <summary>デジョン・テレポを撃ち直す間隔。</summary>
    private const int TravelRetryMs = 3000;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly FateTrace trace = trace;
    private readonly AutoRetainerIpc retainer = retainer;
    private readonly LifestreamIpc lifestream = lifestream;
    private readonly HomeTownService towns = towns;
    private readonly VentureBellRunner bells = bells;

    /// <summary>いまの段階。</summary>
    public VentureStep Step { get; private set; } = VentureStep.Off;

    /// <summary>画面に出す一言。分岐には使わない。</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>これまでに回収に行った回数。</summary>
    public int Collected { get; private set; }

    /// <summary>見張っているか。</summary>
    public bool Active => this.Step != VentureStep.Off;

    /// <summary>回収のために周回を離れている最中か。呼び出し側が周回を止めるのに使う。</summary>
    public bool Interrupting => this.Step is VentureStep.Traveling or VentureStep.Collecting;

    /// <summary>回収が済んで、周回へ戻してよい状態か。</summary>
    public bool ReadyToResume => this.Step == VentureStep.Done;

    private DateTime checkedUtc = DateTime.MinValue;
    private DateTime breakSinceUtc = DateTime.MinValue;
    private DateTime travelSinceUtc = DateTime.MinValue;
    private DateTime collectSinceUtc = DateTime.MinValue;

    /// <summary>行き先。見張り始めた時点で決める。</summary>
    private uint destinationTerritory;

    /// <summary>行き先へ向かう手立てを記録に残したか。</summary>
    private bool travelAnnounced;

    /// <summary>
    /// 見張りを始める。
    ///
    /// 周回の開始ボタンから呼ぶ。
    /// </summary>
    public void Start(Config cfg)
    {
        if (!cfg.FateVentureCollectEnabled)
        {
            this.Step = VentureStep.Off;
            this.Detail = string.Empty;
            return;
        }

        if (!this.retainer.IsLoaded)
        {
            this.Step = VentureStep.Off;
            this.Detail = string.Empty;
            this.anomalyLog.Info(
                "Venture",
                "AutoRetainer が導入されていないため、ベンチャーの見張りは行いません");

            return;
        }

        this.Step = VentureStep.Watching;
        this.Detail = "ベンチャーを見張っています";
        this.checkedUtc = DateTime.MinValue;
        this.Collected = 0;
        this.destinationTerritory = 0;
        this.travelAnnounced = false;

        this.anomalyLog.Info("Venture", "ベンチャーの見張りを始めました");
    }

    /// <summary>
    /// 見張りをやめる。
    ///
    /// <b>周回を止めたら必ず呼ぶ。</b>
    /// 止めたのに回収へ行き続けるのは、利用者から見て制御を失っている。
    /// </summary>
    public void Stop(string why)
    {
        if (this.Step == VentureStep.Off)
        {
            return;
        }

        // 回収の途中で止められることがある。呼び鈴の画面を閉じ、
        // 立てた抑制も外す。残すと AutoRetainer が動けなくなる。
        if (this.Step == VentureStep.Collecting)
        {
            this.bells.Cancel();
        }

        this.Step = VentureStep.Off;
        this.Detail = string.Empty;
        this.destinationTerritory = 0;
        this.travelAnnounced = false;

        this.anomalyLog.Info("Venture", $"ベンチャーの見張りをやめました: {why}");
    }

    /// <summary>回収が済んだことを受け取って、見張りへ戻す。</summary>
    public void ResumeWatching()
    {
        if (this.Step != VentureStep.Done)
        {
            return;
        }

        this.Step = VentureStep.Watching;
        this.Detail = "ベンチャーを見張っています";
        this.checkedUtc = DateTime.UtcNow;
        this.destinationTerritory = 0;
        this.travelAnnounced = false;
    }

    /// <summary>
    /// 1 フレーム進める。
    /// </summary>
    /// <param name="cfg">設定。</param>
    /// <param name="atBreak">
    /// いま抜けてよいか（戦闘外・納品中でない）。呼び出し側が判断して渡す。
    /// </param>
    public void Tick(Config cfg, bool atBreak)
    {
        switch (this.Step)
        {
            case VentureStep.Watching:
                this.TickWatching(cfg);
                return;

            case VentureStep.WaitingForBreak:
                this.TickWaitingForBreak(cfg, atBreak);
                return;

            case VentureStep.Traveling:
                this.TickTraveling(cfg);
                return;

            case VentureStep.Collecting:
                this.TickCollecting();
                return;
        }
    }

    /// <summary>ベンチャーの状態を見る。</summary>
    private void TickWatching(Config cfg)
    {
        var now = DateTime.UtcNow;

        if (now - this.checkedUtc < CheckInterval)
        {
            return;
        }

        this.checkedUtc = now;

        // 行き先が未設定なら、既定（リムサ）を当てはめる。
        // 一覧が読めないうちは何もしない。次に読めたときに試す。
        this.towns.TryApplyDefault(cfg);

        var state = this.retainer.CheckCollectableVenture();

        // **「分からない」を「無い」と読まない。**
        // 読めなかった一瞬のせいで回収を落とす。
        if (state != AutoRetainerIpc.VentureState.Collectable)
        {
            this.Detail = state == AutoRetainerIpc.VentureState.Unknown
                ? "ベンチャーの状態を確かめています"
                : "ベンチャーを見張っています";

            return;
        }

        // 行き先が決まっているか。
        if (cfg.FateVentureTownTerritory == 0)
        {
            if (EzThrottler.Throttle("AutoCollector.VentureNoTown", 60000))
            {
                this.anomalyLog.Warn(
                    "Venture",
                    "ベンチャーを回収できますが、行き先の街が決まっていません。" +
                    "FATE の設定で選んでください");
            }

            return;
        }

        this.destinationTerritory = cfg.FateVentureTownTerritory;
        this.breakSinceUtc = now;
        this.Step = VentureStep.WaitingForBreak;

        this.anomalyLog.Info(
            "Venture",
            "ベンチャーを回収できます。FATE の区切りを待って " +
            $"{NpcLocationService.GetTerritoryName(this.destinationTerritory)} へ向かいます");

        this.trace.Decision("ベンチャー回収へ", $"行き先={NpcLocationService.GetTerritoryName(this.destinationTerritory)}");
    }

    /// <summary>戦闘や納品の切れ目を待つ。</summary>
    private void TickWaitingForBreak(Config cfg, bool atBreak)
    {
        if (atBreak)
        {
            this.travelSinceUtc = DateTime.UtcNow;
            this.travelAnnounced = false;
            this.Step = VentureStep.Traveling;
            this.Detail = "街へ向かっています";
            return;
        }

        // **待ちすぎない。**
        // 敵が湧き続ける FATE では戦闘が切れないことがある。
        // ベンチャーは溜まったままなので、いずれ行く必要がある。
        if (DateTime.UtcNow - this.breakSinceUtc > BreakPatience)
        {
            this.anomalyLog.Warn(
                "Venture",
                $"{BreakPatience.TotalMinutes:F0} 分待っても FATE の区切りが来ないため、そのまま街へ向かいます");

            this.travelSinceUtc = DateTime.UtcNow;
            this.travelAnnounced = false;
            this.Step = VentureStep.Traveling;
            return;
        }

        this.Detail = "FATE の区切りを待っています（ベンチャー回収）";
    }

    /// <summary>街へ移動する。</summary>
    private void TickTraveling(Config cfg)
    {
        var here = Svc.ClientState.TerritoryType;

        // 着いた。
        if (here == this.destinationTerritory)
        {
            this.collectSinceUtc = DateTime.UtcNow;
            this.Step = VentureStep.Collecting;
            this.Detail = "呼び鈴へ向かっています";
            this.bells.Begin();
            return;
        }

        if (DateTime.UtcNow - this.travelSinceUtc > TravelTimeout)
        {
            this.anomalyLog.Warn(
                "Venture",
                $"{NpcLocationService.GetTerritoryName(this.destinationTerritory)} へ " +
                $"{TravelTimeout.TotalSeconds:F0} 秒で着けませんでした。回収を見送ります");

            this.Step = VentureStep.Done;
            return;
        }

        // 移動を撃てる状態か。詠唱中・戦闘中は待つ。
        if (!ECommons.GameHelpers.Player.Available
            || Svc.Condition[ConditionFlag.BetweenAreas]
            || Svc.Condition[ConditionFlag.BetweenAreas51]
            || Svc.Condition[ConditionFlag.Casting])
        {
            this.Detail = "街へ向かっています";
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            this.Detail = "戦闘が切れるのを待っています（ベンチャー回収）";
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.VentureTravel", TravelRetryMs))
        {
            return;
        }

        var method = this.towns.ChooseMethod(this.destinationTerritory);

        if (!this.travelAnnounced)
        {
            this.travelAnnounced = true;

            var home = this.towns.HomeTerritory();

            this.anomalyLog.Info(
                "Venture",
                $"{NpcLocationService.GetTerritoryName(this.destinationTerritory)} へ " +
                $"{(method == HomeTownService.TravelMethod.Return ? "デジョン" : "テレポ")}します" +
                $"（ホームタウン={(home == 0 ? "不明" : NpcLocationService.GetTerritoryName(home))}）");
        }

        switch (method)
        {
            case HomeTownService.TravelMethod.Return:
                // ホームタウンと同じなのでデジョンで行ける。料金がかからない。
                ReturnHome();
                this.Detail = "デジョンで街へ戻っています";
                return;

            case HomeTownService.TravelMethod.Teleport:
                if (this.towns.Resolve(this.destinationTerritory) is not { } town)
                {
                    // 一覧が読めないだけかもしれない。決めつけずに待つ。
                    this.Detail = "行き先のエーテライトを確かめています";
                    return;
                }

                if (!this.lifestream.TryTeleport(town.AetheryteId, town.SubIndex, out var accepted) || !accepted)
                {
                    this.Detail = "テレポできるのを待っています";
                    return;
                }

                this.Detail = $"{town.Name} へテレポしています";
                return;
        }
    }

    /// <summary>呼び鈴で回収する。</summary>
    private void TickCollecting()
    {
        if (DateTime.UtcNow - this.collectSinceUtc > CollectTimeout)
        {
            this.anomalyLog.Warn(
                "Venture",
                $"ベンチャーの回収が {CollectTimeout.TotalMinutes:F0} 分で終わりませんでした。周回へ戻ります");

            this.bells.Cancel();
            this.Step = VentureStep.Done;
            return;
        }

        this.bells.Tick();
        this.Detail = this.bells.Detail;

        if (!this.bells.IsFinished(out var ok, out var why))
        {
            return;
        }

        if (ok)
        {
            this.Collected++;
            this.anomalyLog.Info("Venture", $"ベンチャーを回収しました（{this.Collected} 回目）");
        }
        else
        {
            this.anomalyLog.Warn("Venture", $"ベンチャーを回収できませんでした: {why}");
        }

        this.Step = VentureStep.Done;
    }

    /// <summary>
    /// デジョン。
    ///
    /// ホームポイントへ戻る。経路も地形も高度も関係なく、どこに居ても戻れる。
    /// </summary>
    private static unsafe void ReturnHome()
    {
        try
        {
            FFXIVClientStructs.FFXIV.Client.Game.GameMain.ExecuteCommand(200, 8, 0, 0, 0);
        }
        catch
        {
            // 撃てなくても、次の機会に撃ち直す。
        }
    }
}
