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

/// <summary>
/// 交換リストの 1 行。
///
/// 装備や秘伝書のように「これとこれとこれを 1 個ずつ」欲しい場合、
/// プリセットを品の数だけ作るのは手間が大きい。1 つのプリセットに並べられるようにする。
/// </summary>
public sealed class ExchangeEntry
{
    public uint RewardItemId { get; set; }

    /// <summary>交換する個数。0 なら上限なし（通貨が尽きるか、他の終了条件まで）。</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// 旧設定。所持数がこの値に達していれば飛ばす、という二択だった。
    /// <see cref="OwnedLimit"/> へ移すためだけに残す。
    /// </summary>
    public int StopAtOwned { get; set; } = 1;

    /// <summary>
    /// 所持数の上限。ここまで持つように交換する。0 なら上限なし。
    ///
    /// 「持っていたら飛ばす」という二択ではない。
    /// 上限 5 で 3 個持っているなら、足りない 2 個だけを交換する。
    /// 持ちすぎないための条件であり、飛ばすのはすでに上限に達している場合だけ。
    /// </summary>
    public int OwnedLimit { get; set; } = 1;
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

    /// <summary>
    /// スロットの概念が無い通貨を監視する場合の ItemId。0 ならトームストーンのスロットを使う。
    ///
    /// スクリップは Tomestones シートに載っていないため、スロット参照では指せない。
    /// こちらは入れ替わりが無いので、ItemId を直接持っても追従の問題は起きない。
    /// </summary>
    public uint CurrencyItemId { get; set; }

    /// <summary>
    /// 交換して得るアイテムの ItemId。
    ///
    /// 交換リストへ移行したため、新しい設定では使わない。
    /// 既存の設定を読み込んだときに <see cref="Rewards"/> へ移すためだけに残す。
    /// </summary>
    public uint RewardItemId { get; set; }

    /// <summary>
    /// 交換する品の並び。上から順に交換する。
    ///
    /// 1 回の移動でまとめて交換する。品ごとに窓口が違う場合は、
    /// いま行っている窓口で扱えるものだけをその場で交換する。
    /// </summary>
    public List<ExchangeEntry> Rewards { get; set; } = [];

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

    /// <summary>
    /// スクリップが足りないぶんを、収集品を作って納品して賄うか。
    ///
    /// 入れると、このプリセットは「欲しいアイテムが揃うまで」を通しで回す。
    /// 素材の取り出し → 製作 → 納品 → 交換 を、目標に届くまで繰り返す。
    /// </summary>
    public bool CraftToEarn { get; set; }

    /// <summary>
    /// 製作するジョブ。CraftType の行番号。**負なら未設定。**
    ///
    /// 0 を未設定に使ってはいけない。**木工師の CraftType が 0**。
    /// 0 を未設定にしていたため、未設定のはずが「木工」と表示され、
    /// 木工を選んでも製作リストに切り替わらなかった（2026-09-14 実測）。
    /// </summary>
    public int CraftJob { get; set; } = -1;

    /// <summary>
    /// 作る収集品の ItemId。0 は未選択。
    ///
    /// 橙貨はジョブごとに 1 件しかないため、ジョブを決めれば自動で決まる。
    /// 紫貨はレベル帯ごとに複数あり、生むスクリップの量も違うため、選ぶ必要がある。
    /// </summary>
    public uint CraftCollectableItemId { get; set; }

    /// <summary>選んでいるレベル帯の下限。0 は未選択。表示を戻すためだけに持つ。</summary>
    public int CraftLevelBand { get; set; }

    /// <summary>製作のときに残しておく鞄の空き枠。交換した品を入れる余地になる。</summary>
    public int CraftKeepFreeSlots { get; set; } = 10;
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
    /// 設定の移行に使う版数。
    ///
    /// 詳細ログは当初 既定で有効にしていたため、その値が保存されたままの設定が存在する。
    /// 既定を無効に変えたので、一度だけ揃え直す。
    /// </summary>
    public int ConfigVersion { get; set; }

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
    /// <remarks>
    /// **空にしておく。** 空ならプラグインの設定フォルダへ書く。
    ///
    /// 以前は開発機の共有（<c>\\rio-pc\DevPlugins\AutoCollectorLogs</c>）を既定にしていた。
    /// 配布版ではその共有に誰も届かず、詳細ログを入れても
    /// 「ネットワーク パスが見つかりません」で 1 行も残らない。
    /// 不具合の報告を受けても、こちらから見られる記録が無い状態だった。
    /// </remarks>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 外部の自動化プラグイン（AutoDuty / Artisan）が動作しているときだけ自動交換する。
    ///
    /// プリセットを有効にしただけで動くと、手動で遊んでいる最中に
    /// 勝手にテレポートして交換を始めてしまう。
    /// 手動の「行って交換」はこの設定に関わらず実行できる。
    /// </summary>
    public bool RequireExternalAutomationRunning { get; set; } = true;

    /// <summary>
    /// Artisan が導入されている場合、交換中は製作を止めるか。
    ///
    /// 製作の最中は SafetyGuard が弾くが、製作と製作の合間は素通りする。
    /// そこで交換に入ると Artisan が次の製作を始めようとして操作を取り合う。
    /// </summary>
    public bool StopArtisan { get; set; } = true;

    /// <summary>AutoRetainer が導入されている場合、交換中に SetSuppressed で抑制するか。</summary>
    public bool SuppressAutoRetainer { get; set; } = true;

    // 交換後に残す所持枠の設定は廃止した。
    // 残す枠はプリセットの「残す空き枠」だけで決める。
    // 2 つあると、片方を直しても交換の数が変わらず、理由が読めなくなる。

    /// <summary>
    /// 必要な AutoDuty の版。これを下回るあいだは戦闘の自動周回を使わせない。
    ///
    /// **AutoDuty は設定の持ち方を作り直した。**
    /// 古い版では設定の読み書きができず、ループ間処理の設定も確かめられない。
    /// その状態で周回を任せると、交換に入れないまま回り続けることになる。
    ///
    /// 空にすると制限しない。
    /// </summary>
    public string MinimumAutoDutyVersion { get; set; } = "0.0.0.340";

    /// <summary>NPC へ近づく際の許容距離。</summary>
    public float NpcApproachRange { get; set; } = 3.0f;

    /// <summary>
    /// 収集品の納品窓口を固定する場合の ENpcBase.RowId。0 なら自動で選ぶ。
    ///
    /// 窓口は 9 都市にあり、どれを使っても納品できる。
    /// 混み具合や周辺の動線の好みがあるため、選べるようにしておく。
    /// </summary>
    public uint PreferredCollectablesNpcDataId { get; set; }

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

    // ------------------------------------------------------------------
    // FATE 自動周回（docs/15_FATE自動周回仕様.md）
    //
    // 交換機能とは独立して動く。既存の設定には影響しない。
    // ここから下は、すべて新しい設定として追加したもの。
    // ------------------------------------------------------------------

    /// <summary>FATE 自動周回を使うか。</summary>
    public bool FateEnabled { get; set; }

    /// <summary>
    /// 周回するマップ（TerritoryType の RowId）。
    ///
    /// 画面では拡張（新生・蒼天・紅蓮・漆黒・暁月・黄金）単位でも選べるが、
    /// 保存はマップ単位で行う。拡張 ID で保存すると、パッチでマップが増えたときに
    /// ユーザーが選んだ覚えのないマップで周回が始まってしまう。
    /// </summary>
    public List<uint> FateZones { get; set; } = [];

    /// <summary>いまのマップに狙える FATE が無いとき、次のマップへ移るか。</summary>
    public bool FateSwapZoneWhenEmpty { get; set; } = true;

    /// <summary>マップを移るまでに待つ時間（秒）。すぐ湧くことがあるため少し待つ。</summary>
    public int FateZoneSwapWaitSeconds { get; set; } = 30;

    /// <summary>残り時間がこれ未満の FATE は狙わない（秒）。着く前に終わるため。</summary>
    public int FateMinTimeRemainingSec { get; set; } = 120;

    /// <summary>達成度がこれを超えている FATE は狙わない（%）。</summary>
    public int FateMaxProgressPct { get; set; } = 90;

    /// <summary>
    /// レベル差で FATE を絞るか。
    ///
    /// <b>既定は false。</b>高レベルのキャラが低レベルの FATE を回すのは
    /// 正常な使い方で、レベルシンクで弱体化はするが完了はできる。
    /// 既定で弾くと、選んだマップの FATE が一つも対象にならないことがある。
    /// </summary>
    public bool FateLevelFilterEnabled { get; set; }

    /// <summary>自分より下に許すレベル差。</summary>
    public int FateMaxLevelBelow { get; set; } = 5;

    /// <summary>自分より上に許すレベル差。</summary>
    public int FateMaxLevelAbove { get; set; } = 5;

    /// <summary>納品 FATE も回すか。</summary>
    public bool FateCollectEnabled { get; set; } = true;

    /// <summary>
    /// 達成度がこれを超えたら、次の FATE を先に決めておく（%）。
    ///
    /// 100% を見てから探し始めると、その間その場に立ち尽くすことになる。
    /// 先に決めておけば、100% の瞬間に動き出せる。
    /// </summary>
    public int FatePrefetchPct { get; set; } = 70;

    /// <summary>戦闘に使う BossMod Reborn のプリセット名。空なら切り替えない。</summary>
    public string FateCombatPreset { get; set; } = string.Empty;

    /// <summary>バディ（チョコボ）を自動で呼び出すか。</summary>
    public bool FateBuddyEnabled { get; set; }

    /// <summary>バディの残りがこれを切ったら呼び直す（秒）。</summary>
    public int FateBuddyMinSecondsRemaining { get; set; } = 300;

    /// <summary>ギサールの野菜がこれを切ったら買いに行く。</summary>
    public int FateGysahlMinCount { get; set; } = 10;

    /// <summary>一度に買うギサールの野菜の数。</summary>
    public int FateGysahlBuyQuantity { get; set; } = 99;

    /// <summary>ギサールの野菜を自動で買うか。</summary>
    public bool FateGysahlAutoBuy { get; set; } = true;

    /// <summary>ベンチャーを回収できるとき、ホームタウンへ戻るか。</summary>
    public bool FateVentureEnabled { get; set; }

    /// <summary>街での用事が済んだら、元のマップへ戻るか。</summary>
    public bool FateErrandReturnToZone { get; set; } = true;

    /// <summary>元のマップへ戻ったあと、離れた座標まで移動するか。</summary>
    public bool FateErrandReturnToSpot { get; set; }

    /// <summary>死亡したときの動き。</summary>
    public FateDeathAction FateDeathAction { get; set; } = FateDeathAction.Wait;

    /// <summary>レイズを待つ上限（秒）。</summary>
    public int FateRaiseWaitSeconds { get; set; } = 30;

    /// <summary>
    /// FATE 周回中は交換を始めないか。
    ///
    /// 交換は周回を止めてテレポートするため、周回の最中に割り込むと
    /// FATE を取りこぼす。既定では割り込ませない。
    /// </summary>
    public bool FateBlocksExchange { get; set; } = true;
}

/// <summary>FATE 周回中に死亡したときの動き。</summary>
public enum FateDeathAction
{
    /// <summary>レイズを待つ。時間切れで街へ戻る。既定。</summary>
    Wait,

    /// <summary>すぐ街へ戻る。</summary>
    Return,

    /// <summary>ソロなら戻り、パーティなら待つ。</summary>
    Auto,
}
