using System;
using System.Collections.Generic;

namespace AutoCollector;

/// <summary>閾値の判定方式。</summary>
public enum ThresholdMode
{
    /// <summary>固定値以上で交換する（例: 1800 以上）。</summary>
    Fixed,

    /// <summary>所持上限に対する割合で交換する（例: 上限の 90%）。推奨。</summary>
    Percentage,

    /// <summary>上限までの残りが指定値を下回ったら交換する（例: 上限 - 200）。</summary>
    BeforeCap,
}

/// <summary>交換をどこまで続けるか。</summary>
public enum ExchangeMode
{
    /// <summary>交換可能な限り交換する。</summary>
    MaxExchange,

    /// <summary>通貨が指定残高になるまで交換する。</summary>
    UntilCurrencyReserve,

    /// <summary>指定個数だけ交換する。</summary>
    FixedQuantity,

    /// <summary>報酬アイテムの所持数が目標に達するまで交換する。</summary>
    UntilTargetQuantity,
}

/// <summary>閾値設定。</summary>
public sealed class ThresholdSetting
{
    public ThresholdMode Mode { get; set; } = ThresholdMode.Percentage;

    /// <summary>Fixed なら所持数、Percentage なら 0〜100、BeforeCap なら上限からの残り。</summary>
    public int Value { get; set; } = 90;
}

/// <summary>1 件の交換設定。</summary>
public sealed class ExchangePreset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新しいプリセット";

    public bool Enabled { get; set; }

    /// <summary>
    /// 監視する通貨を Tomestones シートの行番号で保持する。
    /// ItemId を直接保存すると、パッチでトームストーンが入れ替わったときに
    /// 旧通貨を監視し続けてしまうため、必ずスロット参照にする。
    /// </summary>
    public uint TomestonesRowId { get; set; } = 2;

    /// <summary>交換して得るアイテムの ItemId。</summary>
    public uint RewardItemId { get; set; }

    /// <summary>ユーザーが交換所 NPC を明示指定した場合の ENpcBase.RowId。0 なら自動選択。</summary>
    public uint PreferredNpcDataId { get; set; }

    /// <summary>SelectString 等の選択肢を絞り込むためのヒント文字列。ユーザー操作で確定した値を保存する。</summary>
    public string? MenuHint { get; set; }

    public ThresholdSetting Threshold { get; set; } = new();

    public ExchangeMode Mode { get; set; } = ExchangeMode.UntilCurrencyReserve;

    /// <summary>UntilCurrencyReserve のときに残す通貨量。</summary>
    public int CurrencyReserve { get; set; } = 500;

    /// <summary>FixedQuantity / UntilTargetQuantity のときの個数。</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>連続失敗して自動的に無効化されたときの理由。UI に表示する。</summary>
    public string? DisabledReason { get; set; }
}

public sealed class Config
{
    /// <summary>閾値監視を行うかどうか。オフの間は手動実行のみ。</summary>
    public bool MonitoringEnabled { get; set; }

    public List<ExchangePreset> Presets { get; set; } = [];

    /// <summary>交換前に AutoDuty が動いていた場合、交換後に再開するか。</summary>
    public bool ResumeAutoDuty { get; set; } = true;

    /// <summary>AutoRetainer が導入されている場合、交換中に SetSuppressed で抑制するか。</summary>
    public bool SuppressAutoRetainer { get; set; } = true;

    /// <summary>
    /// 緊急停止時に AutoRetainer の実行中タスクも中断するか。
    /// 既定は false（設計決定 D-1「実行中タスクを中断しない」）。
    /// </summary>
    public bool AbortAutoRetainerTasksOnStop { get; set; }

    /// <summary>NPC へ近づく際の許容距離。</summary>
    public float NpcApproachRange { get; set; } = 3.0f;

    /// <summary>短縮コマンド /ac を登録するか。他プラグインと衝突する場合はオフにする。</summary>
    public bool RegisterShortCommand { get; set; } = true;

    /// <summary>SelfCheck でゲームバージョン差分を検知するために、前回起動時のバージョンを保持する。</summary>
    public string? LastSeenGameVersion { get; set; }

    /// <summary>連携プラグインのバージョン。変化したら SelfCheck を強制再実行する。</summary>
    public Dictionary<string, string> LastSeenPluginVersions { get; set; } = [];

    /// <summary>前回起動時のトームストーンのスロット割り当て。差分検知に使う。</summary>
    public Dictionary<uint, uint> LastSeenTomestoneSlots { get; set; } = [];

    /// <summary>
    /// 結果が確定していない交換の記録。
    /// プラグインのリロードやクラッシュを跨いでも残す必要があるため設定に持つ。
    /// null でない間は新しい交換を受け付けない。
    /// </summary>
    public AutoCollector.Automation.PurchaseAttempt? InFlight { get; set; }
}
