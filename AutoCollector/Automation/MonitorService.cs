using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ui;
using ECommons.Configuration;
using ECommons.DalamudServices;

namespace AutoCollector.Automation;

/// <summary>プリセット 1 件がいまどういう状態か。</summary>
public enum PresetReadiness
{
    /// <summary>条件を満たした。あとは始められる状態になるのを待つだけ。</summary>
    Reached,

    /// <summary>監視中。まだ条件に届いていない。</summary>
    Watching,

    /// <summary>目標の所持数に達しているので交換しない。</summary>
    TargetReached,

    /// <summary>交換して得るものが選ばれていない。</summary>
    NeedsReward,

    /// <summary>指定された通貨をいま解決できない。</summary>
    CurrencyUnresolved,

    /// <summary>所持数を読み取れない。</summary>
    Unreadable,

    /// <summary>無効にされている。</summary>
    Disabled,
}

/// <summary>プリセット 1 件の進み具合。判定は MonitorService でだけ行い、UI は描くだけにする。</summary>
public sealed record PresetProgress(
    Guid Id,
    string Name,
    bool Enabled,
    string? DisabledReason,
    uint CurrencyItemId,
    string CurrencyName,
    uint RewardItemId,
    string RewardName,
    int Current,
    int? Cap,
    int? Trigger,
    int? OwnedReward,
    PresetReadiness Readiness,
    string ModeText,
    string ActionVerb);

/// <summary>
/// 画面表示用のまとまり。
///
/// UI から判定を書くと、画面に出ている条件と実際に発火する条件がずれる。
/// 判定はここでだけ行い、UI はこの値を描くだけにする。
/// </summary>
public sealed record MonitorSnapshot(
    DateTime AtUtc,
    IReadOnlyList<PresetProgress> Presets,
    int EnabledCount,
    int UsableCount,
    int ReachedCount,
    bool AutomationRunning,
    string AutomationDetail,
    bool SafeToStart,
    string SafetyReason,
    StartWaitKind SafetyKind,
    uint? FreeBagSlots,
    IReadOnlyList<TomestoneSlotInfo> Slots)
{
    public static readonly MonitorSnapshot Empty = new(
        DateTime.MinValue, [], 0, 0, 0, false, string.Empty,
        false, string.Empty, StartWaitKind.None, null, []);
}

/// <summary>
/// 通貨を監視して、閾値に達したら交換を開始する。
///
/// 判定は軽い間隔で行い、重い索引構築は必要になったときだけ走らせる。
/// 交換の実行そのものは ExchangeExecutor に任せ、ここでは「いつ始めるか」だけを決める。
/// </summary>
public sealed class MonitorService(
    AnomalyLog anomalyLog,
    CurrencyService currencyService,
    TomestoneService tomestoneService,
    ExchangeResolver resolver,
    ExchangeExecutor executor,
    ExternalAutomationGate automationGate)
{
    /// <summary>同じプリセットで連続してこの回数失敗したら、そのプリセットを無効化する。</summary>
    private const int FailureLimit = 2;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 外部の自動化が動いているときの確認間隔。
    ///
    /// 閾値に達したのが周回の切れ目だった場合、予約が遅れるとその周回を取り逃がす。
    /// 判定自体は所持数を数えるだけなので、この頻度でも負荷にならない。
    /// </summary>
    private static readonly TimeSpan ActiveCheckInterval = TimeSpan.FromSeconds(1);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CurrencyService currencyService = currencyService;
    private readonly TomestoneService tomestoneService = tomestoneService;
    private readonly ExchangeResolver resolver = resolver;
    private readonly ExchangeExecutor executor = executor;
    private readonly ExternalAutomationGate automationGate = automationGate;

    private DateTime nextCheckUtc = DateTime.MinValue;
    private Guid buildingForPreset;
    private Guid lastRunPreset;
    private int consecutiveFailures;

    public string LastDecision { get; private set; } = string.Empty;

    /// <summary>UI 表示用のまとまり。1 秒ごとに更新する。</summary>
    public MonitorSnapshot Snapshot { get; private set; } = MonitorSnapshot.Empty;

    private DateTime nextSnapshotUtc = DateTime.MinValue;

    public void Tick()
    {
        // どの早期 return よりも先に更新する。
        // ここを下げると、実行中・結果未確認・外部自動化の待ちのあいだ画面が空になる。
        // 既定の設定でいちばん多い状態が「外部自動化の待ち」なので、下げると
        // 新規利用者の画面がほとんどの時間 空のままになる。
        this.UpdateSnapshot();

        // 交換が進行中なら何もしない。結果が確定するまで次を始めない。
        if (this.executor.Step != ExchangeStep.Idle && this.executor.Step != ExchangeStep.Done)
        {
            this.TrackFailure();
            return;
        }

        if (this.executor.InFlight is not null)
        {
            this.LastDecision = "前回の交換の結果が未確認のため、監視を停止しています";
            return;
        }

        if (DateTime.UtcNow < this.nextCheckUtc)
        {
            return;
        }

        // 外部自動化の観測はスナップショットの更新で 1 回だけ行う。
        // ここで呼ぶと猶予タイマーを二重に動かすことになる。
        var automationRunning = this.Snapshot.AutomationRunning;
        this.nextCheckUtc = DateTime.UtcNow.Add(automationRunning ? ActiveCheckInterval : CheckInterval);

        // 自動交換は周回の相乗りとして動かす。
        // プリセットを有効にしただけで動くと、手動で遊んでいる最中に
        // 勝手にテレポートして交換を始めてしまう。
        if (Plugin.C.RequireExternalAutomationRunning && !automationRunning)
        {
            this.LastDecision = "AutoDuty や Artisan が動作していないため、自動交換は待機しています";
            return;
        }

        // 直前の実行結果を反映してから次を選ぶ
        this.TrackFailure();

        var preset = this.SelectPreset();
        if (preset is null)
        {
            return;
        }

        this.TryStart(preset);
    }

    /// <summary>
    /// 条件を満たしている有効なプリセットを 1 件選ぶ。
    ///
    /// スナップショットをそのまま使う。最大 1 秒古いが、発火の直前に
    /// ExchangeExecutor が所持数と画面を読み直して検証するため判断材料としては足りる。
    /// 得られる保証のほうが大きい。画面に「条件を満たしました」と出ているものだけが実際に発火する。
    /// </summary>
    private ExchangePreset? SelectPreset()
    {
        foreach (var progress in this.Snapshot.Presets)
        {
            if (progress.Enabled && progress.Readiness == PresetReadiness.Reached)
            {
                return Plugin.C.Presets.FirstOrDefault(x => x.Id == progress.Id);
            }
        }

        return null;
    }

    /// <summary>
    /// 外部自動化が動いていない条件だけを迂回して 1 回実行する。
    /// 安全判定は迂回しない。押してもコンテンツ中なら待機に入る。
    /// </summary>
    public bool RequestManualRun(out string reason)
    {
        var preset = this.SelectPreset();
        if (preset is null)
        {
            reason = "条件を満たしているプリセットがありません";
            return false;
        }

        this.TryStart(preset);
        reason = this.LastDecision;
        return true;
    }

    /// <summary>索引を用意し、交換先を決めて開始する。</summary>
    private void TryStart(ExchangePreset preset)
    {
        if (!this.tomestoneService.TryResolveItemId(preset.TomestonesRowId, out var currencyItemId))
        {
            return;
        }

        // 索引ができていなければ構築を始める。完了は次回以降の呼び出しで拾う。
        if (!this.resolver.IsBuiltFor(currencyItemId))
        {
            if (this.buildingForPreset != preset.Id)
            {
                this.buildingForPreset = preset.Id;
                this.resolver.BeginBuild(currencyItemId);
                this.LastDecision = $"{preset.Name}: 交換候補を構築しています";
            }

            return;
        }

        this.buildingForPreset = Guid.Empty;
        this.resolver.BeginBuild(currencyItemId);

        var definition = this.resolver.Resolve(currencyItemId, preset.RewardItemId, preset.PreferredNpcDataId);
        if (definition is null)
        {
            this.DisablePreset(preset, "交換先を解決できませんでした");
            return;
        }

        var session = new ExchangeSession
        {
            Mode = preset.Mode,
            CurrencyReserve = preset.CurrencyReserve,
            RemainingCount = preset.Mode == ExchangeMode.FixedQuantity ? Math.Max(1, preset.Quantity) : 0,
            TargetQuantity = preset.Quantity,
            PresetId = preset.Id,
        };

        if (!this.executor.RequestWithTravel(definition, session, out var reason))
        {
            this.LastDecision = $"{preset.Name}: {reason}";
            return;
        }

        this.lastRunPreset = preset.Id;
        this.LastDecision = $"{preset.Name}: 交換を開始しました";
        this.anomalyLog.Info("Monitor", $"{preset.Name}: 閾値に達したため交換を開始します");
    }

    /// <summary>
    /// 画面表示用のまとまりを作り直す。
    ///
    /// ここは毎フレーム呼ばれるので自前で間引く。
    /// ListSlots はシートを走査するため、1 回にまとめて呼ぶ。
    /// </summary>
    private void UpdateSnapshot()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextSnapshotUtc)
        {
            return;
        }

        this.nextSnapshotUtc = now.AddSeconds(1);

        var automationRunning = this.automationGate.IsAnyRunning(out var automationDetail);
        var safe = SafetyGuard.IsSafeToStart(out var safetyReason, out var safetyKind);
        uint? freeSlots = this.currencyService.TryGetEmptyBagSlots(out var slots) ? slots : null;

        var list = new List<PresetProgress>();
        var enabled = 0;
        var usable = 0;
        var reached = 0;

        foreach (var preset in Plugin.C.Presets)
        {
            var progress = this.BuildProgress(preset);
            list.Add(progress);

            if (!progress.Enabled)
            {
                continue;
            }

            enabled++;

            if (progress.Readiness is PresetReadiness.NeedsReward or PresetReadiness.CurrencyUnresolved)
            {
                continue;
            }

            usable++;

            if (progress.Readiness == PresetReadiness.Reached)
            {
                reached++;
            }
        }

        this.Snapshot = new MonitorSnapshot(
            now,
            list,
            enabled,
            usable,
            reached,
            automationRunning,
            automationDetail,
            safe,
            safetyReason,
            safetyKind,
            freeSlots,
            this.tomestoneService.ListSlots());
    }

    private PresetProgress BuildProgress(ExchangePreset preset)
    {
        var currencyResolved = this.tomestoneService.TryResolveItemId(preset.TomestonesRowId, out var currencyItemId);
        var currencyName = currencyResolved ? StatusText.ItemName(currencyItemId) : "（解決できません）";
        var rewardName = preset.RewardItemId == 0 ? "（未選択）" : StatusText.ItemName(preset.RewardItemId);

        var hasReward = this.currencyService.TryGetCount(
            preset.RewardItemId, out var owned, includeEquipped: true, includeArmory: true);
        int? ownedReward = preset.RewardItemId != 0 && hasReward ? owned : null;

        var modeText = ModeText(preset, rewardName, ownedReward);

        // 交換の対象が通貨を得る側になる系統に備えて動詞を持たせておく。いまは常に「交換」。
        const string verb = "交換";

        if (!preset.Enabled)
        {
            return New(0, null, null, PresetReadiness.Disabled);
        }

        if (!currencyResolved)
        {
            return New(0, null, null, PresetReadiness.CurrencyUnresolved);
        }

        if (preset.RewardItemId == 0)
        {
            return New(0, null, null, PresetReadiness.NeedsReward);
        }

        if (!this.currencyService.TryGetCount(currencyItemId, out var current))
        {
            return New(0, null, null, PresetReadiness.Unreadable);
        }

        var cap = (int?)this.currencyService.GetEffectiveCap(currencyItemId);
        var trigger = this.currencyService.CalculateTriggerAmount(preset.Threshold, currencyItemId);

        if (preset.Mode == ExchangeMode.UntilTargetQuantity && ownedReward is { } have && have >= preset.Quantity)
        {
            return New(current, cap, trigger, PresetReadiness.TargetReached);
        }

        var atThreshold = this.currencyService.IsThresholdReached(preset.Threshold, currencyItemId, out _, out _);
        return New(current, cap, trigger, atThreshold ? PresetReadiness.Reached : PresetReadiness.Watching);

        PresetProgress New(int current, int? cap, int? trigger, PresetReadiness readiness) => new(
            preset.Id, preset.Name, preset.Enabled, preset.DisabledReason,
            currencyItemId, currencyName, preset.RewardItemId, rewardName,
            current, cap, trigger, ownedReward, readiness, modeText, verb);
    }

    private static string ModeText(ExchangePreset preset, string rewardName, int? owned) => preset.Mode switch
    {
        ExchangeMode.MaxExchange => "通貨か所持枠が尽きるまで交換します",
        ExchangeMode.UntilCurrencyReserve => $"{preset.CurrencyReserve:N0} 残るまで交換します",
        ExchangeMode.FixedQuantity => $"1 回に {preset.Quantity} 個だけ交換します",
        ExchangeMode.UntilTargetQuantity => owned is { } n
            ? $"{rewardName} が {preset.Quantity} 個になるまで交換します（いま {n:N0}）"
            : $"{rewardName} が {preset.Quantity} 個になるまで交換します",
        _ => string.Empty,
    };

    /// <summary>
    /// 直前の実行が失敗していたら数える。
    /// 同じプリセットで続けて失敗する場合、原因が解消しないまま繰り返すことになるので止める。
    /// </summary>
    private void TrackFailure()
    {
        if (this.lastRunPreset == Guid.Empty)
        {
            return;
        }

        if (this.executor.Step == ExchangeStep.Error)
        {
            var preset = Plugin.C.Presets.FirstOrDefault(x => x.Id == this.lastRunPreset);
            this.lastRunPreset = Guid.Empty;
            this.consecutiveFailures++;

            if (preset is not null && this.consecutiveFailures >= FailureLimit)
            {
                this.DisablePreset(preset, $"{this.consecutiveFailures} 回続けて失敗しました（{this.executor.Failure}: {this.executor.StatusDetail}）");
                this.consecutiveFailures = 0;
            }

            return;
        }

        if (this.executor.Step == ExchangeStep.Done)
        {
            this.lastRunPreset = Guid.Empty;
            this.consecutiveFailures = 0;
        }
    }

    /// <summary>プリセットを止める。理由を残し、次回起動でも勝手に再開しない。</summary>
    private void DisablePreset(ExchangePreset preset, string reason)
    {
        preset.Enabled = false;
        preset.DisabledReason = reason;
        EzConfig.Save();

        this.LastDecision = $"{preset.Name}: 無効化しました（{reason}）";
        this.anomalyLog.Error("Monitor", $"{preset.Name} を無効化しました: {reason}");
        Svc.Chat.Print($"[Auto Collector] {preset.Name} を無効化しました: {reason}");
    }
}
