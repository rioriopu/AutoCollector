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
    /// <summary>
    /// デバッグモード。開発・不具合調査のためのタブと機能を出す。
    ///
    /// 通常の運用では触る必要がないものを隠しておくための切り替えで、既定は無効。
    /// </summary>
    public bool DebugMode { get; set; }

    public List<ExchangePreset> Presets { get; set; } = [];

    /// <summary>
    /// 交換に失敗した場合も AutoDuty を再開するか。
    /// 止めっぱなしにすると周回が止まったまま放置されるため既定は有効。
    /// </summary>
    public bool ResumeAutoDutyOnFailure { get; set; } = true;

    /// <summary>
    /// AutoDuty が周回を終えて停止したら、こちらから再開させるか。
    ///
    /// 1 周ごとに交換する構成では AutoDuty の周回数を 1 にする。
    /// 交換が起きた周回は交換後の再開処理が動かし直すが、
    /// 閾値に達していない周回では誰も再開させず 1 周で止まってしまう。
    /// </summary>
    public bool KeepAutoDutyLooping { get; set; } = true;

    /// <summary>停止を確認してから再開させるまでの待ち時間。終了処理の残りを踏まないようにする。</summary>
    public int AutoDutyRestartDelaySeconds { get; set; } = 5;

    /// <summary>
    /// 直近に周回していたコンテンツのエリア。再開時に AutoDuty へ渡す。
    /// 停止後は現在地が街になっているため、周回中に記録しておく必要がある。
    /// </summary>
    public uint LastDutyTerritoryId { get; set; }

    /// <summary>
    /// 詳細ログをファイルへ書き出すか。
    ///
    /// 状態遷移や外部プラグインの状態を逐一記録する。
    /// 開発中の不具合追跡用で、通常の運用では不要。
    /// </summary>
    public bool DetailedLogEnabled { get; set; }

    /// <summary>
    /// 実際に詳細ログを記録するか。
    /// デバッグモードを切ったときに書き続けないよう、両方を条件にする。
    /// </summary>
    public bool DetailedLogActive => this.DebugMode && this.DetailedLogEnabled;

    /// <summary>
    /// 詳細ログの保存先。
    ///
    /// ネットワーク共有を指定できる。共有が落ちていても本体の動作には影響しない
    /// （書き込みは背景スレッドで行い、失敗しても諦めるだけ）。
    /// </summary>
    public string LogDirectory { get; set; } = @"\\rio-pc\DevPlugins\AutoCollectorLogs";

    /// <summary>
    /// 外部の自動化プラグイン（AutoDuty / Artisan）が動作しているときだけ自動交換する。
    ///
    /// プリセットを有効にしただけで動くと、手動で遊んでいる最中に
    /// 勝手にテレポートして交換を始めてしまう。
    /// 手動の「行って交換」はこの設定に関わらず実行できる。
    /// </summary>
    public bool RequireExternalAutomationRunning { get; set; } = true;

    /// <summary>AutoRetainer が導入されている場合、交換中に SetSuppressed で抑制するか。</summary>
    public bool SuppressAutoRetainer { get; set; } = true;

    /// <summary>
    /// 交換後に残しておく所持枠の数。
    ///
    /// AutoRetainer は所持枠が空いていないキャラクタを処理対象から外し、
    /// その設定を保存する（MultiMode.cs の Data.Enabled = false）。
    /// 交換で枠を埋め切ると、リテイナーが回らなくなったように見える。
    /// </summary>
    public int KeepFreeInventorySlots { get; set; } = 5;

    /// <summary>NPC へ近づく際の許容距離。</summary>
    public float NpcApproachRange { get; set; } = 3.0f;

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
