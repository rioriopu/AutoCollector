using System;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using ECommons.Configuration;
using ECommons.DalamudServices;

namespace AutoCollector.Automation;

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

    public void Tick()
    {
        if (!Plugin.C.MonitoringEnabled)
        {
            return;
        }

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

        this.nextCheckUtc = DateTime.UtcNow.Add(CheckInterval);

        // 自動交換は周回の相乗りとして動かす。
        // プリセットを有効にしただけで動くと、手動で遊んでいる最中に
        // 勝手にテレポートして交換を始めてしまう。
        if (Plugin.C.RequireExternalAutomationRunning && !this.automationGate.IsAnyRunning(out _))
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

    /// <summary>閾値に達している有効なプリセットを 1 件選ぶ。</summary>
    private ExchangePreset? SelectPreset()
    {
        foreach (var preset in Plugin.C.Presets.Where(x => x.Enabled))
        {
            if (!this.tomestoneService.TryResolveItemId(preset.TomestonesRowId, out var currencyItemId))
            {
                this.LastDecision = $"{preset.Name}: 通貨を解決できません";
                continue;
            }

            if (preset.RewardItemId == 0)
            {
                this.LastDecision = $"{preset.Name}: 交換対象が設定されていません";
                continue;
            }

            if (!this.currencyService.IsThresholdReached(preset.Threshold, currencyItemId, out var current, out var cap))
            {
                var trigger = this.currencyService.CalculateTriggerAmount(preset.Threshold, currencyItemId);
                this.LastDecision = trigger is null
                    ? $"{preset.Name}: 監視中（現在 {current:N0}）"
                    : $"{preset.Name}: 監視中（{current:N0} / 発動 {trigger:N0}）";
                continue;
            }

            // 目標所持数に達していれば、閾値を超えていても交換しない
            if (preset.Mode == ExchangeMode.UntilTargetQuantity &&
                this.currencyService.TryGetCount(preset.RewardItemId, out var owned, includeEquipped: true, includeArmory: true) &&
                owned >= preset.Quantity)
            {
                this.LastDecision = $"{preset.Name}: 目標の {preset.Quantity} 個に達しています";
                continue;
            }

            return preset;
        }

        return null;
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
