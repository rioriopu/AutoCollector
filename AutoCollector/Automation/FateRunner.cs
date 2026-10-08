using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ipc;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace AutoCollector.Automation;

/// <summary>FATE 周回のいまの段階。</summary>
public enum FateStep
{
    /// <summary>止まっている。</summary>
    Idle,

    /// <summary>周回するマップにいない。テレポートする。</summary>
    Traveling,

    /// <summary>マップにいるが、狙える FATE が無い。待つ。</summary>
    Waiting,

    /// <summary>狙う FATE を決めた。そこへ移動している。</summary>
    MovingToFate,

    /// <summary>FATE の中で戦っている。</summary>
    Fighting,

    /// <summary>達成度 100% を見た。次の FATE へ向かい始めている。</summary>
    Leaving,

    /// <summary>
    /// FATE に着いた。マウントから降りている。
    ///
    /// <b>この間は vnavmesh に一切触らない。</b>
    /// 空中からの降下はゲーム側が行うもので、その動きが
    /// プレイヤーの操作として vnavmesh に読まれる。経路を積んでも
    /// その場で捨てられ、積んでは捨てるを繰り返して暴れる。
    /// </summary>
    Landing,

    /// <summary>戦闘不能。設定に従って待つか戻る。</summary>
    Dead,

    /// <summary>止めた。</summary>
    Done,

    /// <summary>続けられない状態になった。</summary>
    Error,
}

/// <summary>
/// FATE がどう終わったか。
///
/// <b>「終わった」と「達成した」を混ぜない。</b>
/// 以前はどちらも完了として数え、「100% になりました」と記録していた。
/// 進捗 40% で時間切れになっても完了 1 件が増えるため、
/// 件数が実績を表さなくなっていた。
/// </summary>
public enum FateOutcome
{
    /// <summary>達成度 100% を見た。</summary>
    Success,

    /// <summary>達成しないまま終わった。時間切れ・失敗・消失。</summary>
    Failed,
}

/// <summary>
/// FATE を自動で回す。
///
/// <b>稼ぎ方の 1 つとして動く。</b>
/// 交換で中断され、交換が済んだら元のエリアへ戻って再開する。
/// その受け渡しは <c>Earning.Fate.FateEarner</c> が IEarner として受け持ち、
/// ここは周回そのものだけを行う。
///
/// <b>戦闘は BossMod Reborn に任せる。</b>
/// 自前の戦闘 AI は持たない。プリセットを有効にして、
/// 離脱するときに解除するだけ。
///
/// <b>達成度 100% を見たら即座に離れる。</b>
/// FATE が消えるのを待つと、その場に立ち尽くす時間が生まれる。
/// 次の FATE は達成度が閾値を超えた時点で決めておき、
/// 100% を見た瞬間に動き出せるようにする。
/// </summary>
public sealed class FateRunner(
    AnomalyLog anomalyLog,
    FateScanner scanner,
    NavigationService navigation,
    BossModIpc bossMod,
    BuddyService buddy,
    MountService mount,
    FateTrace trace,
    LifestreamIpc lifestream,
    AetheryteService aetherytes,
    VnavmeshIpc vnavmesh,
    FateZoneCatalog zoneCatalog,
    VentureWatcher ventures,
    RotationSolverControl rotation)
{
    /// <summary>FATE の円へ入ったとみなす距離の余裕。</summary>
    private const float FateArrivalSlack = 5f;

    /// <summary>移動が進まないまま経過したら、その FATE を諦める。</summary>
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 飛ぶために、乗れるのを待つ上限。
    ///
    /// これを過ぎても乗れないなら、地上の経路で向かう。
    /// 乗騎が解放されていない、直前の戦闘で行動が塞がれているなど、
    /// 待っても乗れない事情がありうる。待ち続けて一歩も進まないより、
    /// 走ってでも着いたほうがよい。
    /// </summary>
    private static readonly TimeSpan MountWaitBeforeGroundPath = TimeSpan.FromSeconds(5);

    /// <summary>同じ FATE で詰まってよい回数。超えたら二度と狙わない。</summary>
    private const int MaxStuckPerFate = 2;

    /// <summary>
    /// 納品に必要な個数。
    ///
    /// BMR の <c>FateUtils.TurnInGoldReq</c> と同じ 10。
    /// これを下回ると FateUtils は納品へ向かわない。
    /// </summary>
    private const int HandInRequired = 10;

    /// <summary>納品が進まないと判断するまでの猶予。</summary>
    private static readonly TimeSpan HandInPatience = TimeSpan.FromSeconds(30);

    /// <summary>納品をやり直す上限。超えたら討伐へ戻る。</summary>
    private const int MaxHandInAttempts = 3;

    /// <summary>
    /// これより近い FATE へは、飛んで入る段取りを組まない。
    ///
    /// 乗って離陸して降りるほうが、走るより時間を食う。
    /// </summary>
    private const float FlyApproachMinMeters = 40f;

    /// <summary>
    /// 敵がこれより遠ければ歩いて近づく。
    ///
    /// BMR は見えている敵と戦うだけで、遠くの敵を探しには行かない。
    /// 遠距離職でも届く範囲より少し内側にしてある。
    /// </summary>
    private const float MobReachMeters = 15f;

    /// <summary>戦闘中にプリセットが有効なままかを確かめる間隔。</summary>
    private static readonly TimeSpan PresetCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 追いかけている敵がこれだけ動いたら、経路を引き直す。
    ///
    /// 小さくすると敵に吸い付くが、引き直しが増えて足が止まる。
    /// 敵が多少動いても、近づいていれば BMR が拾うので、粗くてよい。
    /// </summary>
    private const float ApproachRepathMeters = 5f;

    /// <summary>
    /// 近づくのをやめる距離と、始める距離の差。
    ///
    /// 同じ距離で判定すると、境目で「やめる」「始める」を往復して
    /// ガクガク動く。始めるほうを遠くに置いて、行き来を防ぐ。
    /// </summary>
    private const float ApproachHysteresisMeters = 5f;

    /// <summary>
    /// FATE に入ってから、参加扱いになるのを待つ猶予。
    ///
    /// CurrentFate は円に入っただけでは埋まらない。レベルシンクが
    /// 入るまでの短い間、参加していないように見える。
    /// </summary>
    private static readonly TimeSpan FateJoinGrace = TimeSpan.FromSeconds(5);

    /// <summary>近づく経路を引き直す間隔。経路探索は重いので続けて投げない。</summary>
    private static readonly TimeSpan ApproachRepathInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 進んでいるかを見る間隔。
    ///
    /// <b>短くしすぎない。</b>遠い FATE への飛行経路は、探すのに時間がかかる。
    /// 1000m 先だと十数秒かかることがあり、その間は当然 1m も進まない。
    /// 3 秒で見ていたため、経路が出来る前に「詰まった」と判断して
    /// 引き直しを繰り返し、いつまでも出発できなかった（2026-09-25 実測）。
    ///
    /// 逆に長すぎると、壁に当たったまま何十秒も押し続けることになる。
    /// 出発前は経路の本数で見送るようにしたので、ここは
    /// 「飛んでいるのに進んでいない」を拾える長さでよい。
    /// </summary>
    private static readonly TimeSpan BlockCheckInterval = TimeSpan.FromSeconds(6);

    /// <summary>この間隔でこれだけ進んでいなければ、詰まっているとみなす。</summary>
    private const float BlockProgressMeters = 5f;

    /// <summary>詰まったときに避ける球の半径。迂回するたびに広げる。</summary>
    private const float DetourRadiusMeters = 15f;

    /// <summary>
    /// 狭い通路で経路をなぞらせるときの許容値。
    ///
    /// vnavmesh の既定は 0.25。これだけ経路から外れてよいため、
    /// 曲がり角を端折って壁に寄る。詰まったときはここまで詰める。
    /// </summary>
    private const float TightPathTolerance = 0.05f;

    /// <summary>vnavmesh の既定の許容値。詰めたあとはここへ戻す。</summary>
    private const float DefaultPathTolerance = 0.25f;

    /// <summary>降りられる場所を探すとき、中心から何周ぶん見るか。</summary>
    private const int LandableSearchRings = 4;

    /// <summary>辿り着けなかった FATE を見送る長さ。過ぎたらもう一度試す。</summary>
    private static readonly TimeSpan BlacklistDuration = TimeSpan.FromMinutes(5);

    /// <summary>1 つの移動で迂回を試す上限。これを超えたら力づくで離れる。</summary>
    private const int MaxDetours = 2;

    /// <summary>詰まりから抜け出そうとする上限。これを超えたら諦める。</summary>
    private const int MaxEscapeAttempts = 4;

    /// <summary>抜け出す動きに与える時間。この間は測り直さない。</summary>
    private static readonly TimeSpan EscapeSettleTime = TimeSpan.FromSeconds(4);

    /// <summary>手動の脱出を「もう一度押した」とみなす間隔。</summary>
    private static readonly TimeSpan EscapeNowRetryWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 止めたあと、動き出さないか見張る長さ。
    ///
    /// 経路探索は遠い目的地だと十数秒かかる。
    /// 止めた時点で走っていた探索が終わるまで見張る。
    /// </summary>
    private static readonly TimeSpan StopGuardWindow = TimeSpan.FromSeconds(20);

    /// <summary>見張りが「動いた」と認める距離。</summary>
    private const float WatchdogProgressMeters = 3f;

    /// <summary>
    /// 動くはずの段階で、これだけ動かなければ詰まりとみなす。
    ///
    /// 経路探索や着地には時間がかかるので、短くしすぎない。
    /// </summary>
    private static readonly TimeSpan WatchdogPatience = TimeSpan.FromSeconds(25);

    /// <summary>
    /// 敵が 1 匹も見えないとき、中心からこれ以上離れていたら寄る。
    ///
    /// 円の端では敵が湧いても見えないことがある。
    /// </summary>
    private const float CentreSeekMeters = 20f;

    /// <summary>
    /// 飛んできたとき、中心からこれだけの範囲に入ってから降りる。
    ///
    /// <b>端で降りると高低差で詰む。</b>
    /// 円に入った時点で降りていたため、低い場所に降りて高所の敵へ
    /// 近づけなくなった（2026-09-25 実測）。
    /// 中心付近なら、たいてい敵の居る高さに降りられる。
    /// </summary>
    private const float LandNearCentreMeters = 15f;

    /// <summary>
    /// 着地したい地点の真上と認める水平距離。
    ///
    /// 狭くすると、狙った場所にきちんと降りる。
    /// 広いと手前で降りてしまい、外周に降りるのと変わらなくなる。
    /// </summary>
    private const float LandOnSpotMeters = 5f;

    /// <summary>
    /// この高さまで下りてから降りる。
    ///
    /// <b>高いうちに降りると垂直落下になる。</b>
    /// 斜めに下りきってから降りることで、落下する距離を短くする。
    /// </summary>
    private const float LandFromHeightMeters = 8f;

    /// <summary>
    /// 着地点より下にいてよい距離。
    ///
    /// 自分が着地点より下にいると「降りた」の判定が負になる。
    /// 下限を置かないと、洞窟や下層など階層の違う場所でも
    /// FATE に入ったことにしてしまう。
    /// </summary>
    private const float LandBelowMeters = 2f;

    /// <summary>
    /// 降りる判定と、降りるのをやめる判定の差。
    ///
    /// 同じ値で往復させないために置く。敵は動くので、
    /// これが無いと Landing と MovingToFate を行き来し続ける。
    /// </summary>
    private const float LandHysteresisMeters = 5f;

    /// <summary>終えた FATE の円から出るとき、半径にどれだけ足して離れるか。</summary>
    private const float RetreatMarginMeters = 10f;

    /// <summary>
    /// 退避の移動を「着いた」と認める距離。
    ///
    /// <b>これを足したぶん、目標は成功基準より外に置く。</b>
    /// vnavmesh はこの距離まで近づいた時点で経路を切り上げるので、
    /// 目標をちょうど成功基準に置くと、いつまでも基準に届かない。
    /// </summary>
    private const float RetreatArrivalRange = 5f;

    /// <summary>退避の向きを変えて試す上限。</summary>
    private const int MaxRetreatAttempts = 3;

    /// <summary>
    /// 退避でマウントに乗れるのを待つ上限（納品 FATE 以外）。
    ///
    /// <b>その場で待ってよい。</b>
    /// 乗れない理由はたいてい一時的（戦闘が切れる直前、直前の操作の硬直、
    /// 降りた直後のリキャスト）で、少し待てば乗れる。
    /// 待たずに歩き出すと、何もない場所へ走る動きになる。
    ///
    /// 納品 FATE では短くする（<see cref="CollectRetreatMountPatience"/>）。
    /// あちらは報酬を受け取るまでマップを離れられないため、
    /// ここで長く待つと次の FATE へ向かう時間を削ることになる。
    /// </summary>
    private static readonly TimeSpan RetreatMountPatience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 納品 FATE で、退避でマウントに乗れるのを待つ上限。
    ///
    /// 納品 FATE は 100% のあと報酬が入るまで最大 90 秒マップに留まる。
    /// その間に次の FATE を回りたいので、ここでは長く待たない。
    /// </summary>
    private static readonly TimeSpan CollectRetreatMountPatience = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 退避のとき、真上へどれだけ上がるか。
    ///
    /// 離陸の合図になればよいので、わずかで足りる
    /// （vnavmesh は「次の点が自分より高い」だけを見る）。
    /// </summary>
    private const float RetreatLiftMeters = 8f;

    /// <summary>
    /// 離陸の合図を送り直す間隔。
    ///
    /// 乗れているのに飛べない（屋根の下、高度上限、ジャンプが弾かれた、
    /// 渡した点に着いて経路が終わった）ことがある。送ったきりにすると
    /// 何も操作しないまま時間切れまで固まる。
    ///
    /// <b>全体の上限に収める。</b>
    /// 間隔 × 回数が長いと、歩いて出る余地を食い潰す。
    /// 納品 FATE は上限が 15 秒しかないので、そこに収まる長さにする
    /// （乗る待ち 8 秒 + 離陸 2 秒 × 2 回 = 12 秒、歩きに 3 秒残る）。
    /// </summary>
    private static readonly TimeSpan RetreatLiftRetry = TimeSpan.FromSeconds(2);

    /// <summary>離陸の合図を送り直す上限。超えたら歩いて出る。</summary>
    private const int MaxRetreatLiftAttempts = 1;

    /// <summary>
    /// 納品 FATE の報酬を待つとき、どれだけ上がるか。
    ///
    /// 敵の攻撃が届かない高さで待つのが目的。
    /// 高すぎると飛行の上限に頭を打つので、ほどほどにする。
    /// </summary>
    private const float HoverHeightMeters = 30f;

    /// <summary>
    /// 退避で「飛べた」と認めるまでに、続けて確認する回数。
    ///
    /// ジャンプ直後の一瞬だけ InFlight が立つことがある。1 回で信じると、
    /// 実際には浮いていないのに成功として記録され、あとから気づけない。
    /// </summary>
    private const int RetreatFlyingStableFrames = 3;

    /// <summary>
    /// 円の外へ出るのを待つ上限。出られなくても周回は続ける。
    ///
    /// <b>乗るのを待つ時間より長くする。</b>
    /// 短いと、乗れるのを待っている最中にこちらが先に切れてしまい、
    /// 待った意味が無くなる。
    /// </summary>
    private static readonly TimeSpan RetreatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>納品 FATE で円の外へ出るのを待つ上限。報酬待ちがあるので短くする。</summary>
    private static readonly TimeSpan CollectRetreatTimeout = TimeSpan.FromSeconds(15);

    /// <summary>テレポートが終わるのを待つ上限。</summary>
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 移れる別のマップが無いまま待つ上限。
    ///
    /// <b>永久に待たない。</b>1 つしか選ばれていないマップで FATE を
    /// 狙えないなら、待っても状況は変わらない。以前は時刻を書き換える
    /// だけだったので、黙って止まったように見えていた。
    /// </summary>
    private static readonly TimeSpan NoAlternativeZonePatience = TimeSpan.FromMinutes(10);

    /// <summary>
    /// テレポを撃ち直す間隔。
    ///
    /// <b>毎フレーム撃たない。</b>以前は失敗するとその場で旗を倒して
    /// いたため、断られている間ずっと毎フレーム撃っていた。
    /// ログだけ 5 秒に間引いていたので、記録からは気づけなかった。
    /// </summary>
    private static readonly TimeSpan TeleportRetryInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 降りるのを待つ上限。空からの降下は数秒かかる。
    ///
    /// 長く取りすぎない。降りられない場所（水面の上など）に来てしまったときは、
    /// 待っても降りられない。早めに諦めて別の FATE へ移るほうがよい。
    /// </summary>
    private static readonly TimeSpan LandingTimeout = TimeSpan.FromSeconds(12);

    /// <summary>納品 FATE の報酬が着地するまでの猶予。1 分 + 余裕。</summary>
    private static readonly TimeSpan CollectRewardWindow = TimeSpan.FromSeconds(90);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly FateScanner scanner = scanner;
    private readonly NavigationService navigation = navigation;
    private readonly BossModIpc bossMod = bossMod;
    private readonly BuddyService buddy = buddy;
    private readonly MountService mount = mount;
    private readonly FateTrace trace = trace;
    private readonly LifestreamIpc lifestream = lifestream;
    private readonly AetheryteService aetherytes = aetherytes;
    private readonly VnavmeshIpc vnavmesh = vnavmesh;
    private readonly FateZoneCatalog zoneCatalog = zoneCatalog;

    /// <summary>
    /// FATE ごとに 1 度だけ求めた、降りられる座標。
    ///
    /// <b>エリアも一緒に覚える。</b>FATE の番号はマップをまたぐと使い回されるため、
    /// 番号だけで覚えると、テレポした先で別マップの座標を使ってしまう。
    /// </summary>
    private (uint TerritoryId, ushort Id, Vector3 Point)? landable;

    /// <summary>降りられずに移っている先。着くまで降下を試さない。</summary>
    private Vector3? landingRefuge;

    /// <summary>進み具合を最後に見た時刻と、そのときの位置。</summary>
    private DateTime blockCheckedUtc = DateTime.MinValue;
    private Vector3 blockCheckPosition;

    /// <summary>この移動で迂回を試した回数。</summary>
    private int blockDetours;

    /// <summary>この移動で、詰まりから抜け出そうとした回数。</summary>
    private int escapeAttempts;

    /// <summary>この移動で、経路追従の許容値を詰めたか。戻すときに使う。</summary>
    private bool tightenedPath;

    /// <summary>手動の脱出を最後に押した時刻と場所。二度目は帰還に切り替える。</summary>
    private DateTime lastEscapeNowUtc = DateTime.MinValue;
    private Vector3 lastEscapeNowFrom;

    /// <summary>止めたあとに動き出さないか見張る期限。</summary>
    private DateTime stopGuardUntil = DateTime.MinValue;

    /// <summary>動けていない状態がいつから続いているか。段階をまたいで見張る。</summary>
    private DateTime watchdogSince = DateTime.MinValue;
    private Vector3 watchdogPosition;

    /// <summary>頼んである迂回の経路探索。出来るまで待つ。</summary>
    private System.Threading.Tasks.Task<System.Collections.Generic.List<Vector3>>? detourTask;

    /// <summary>このセッションで詰まった FATE。もう狙わない。</summary>
    private readonly FateStarter starter = new(anomalyLog);
    private bool startingFate;

    public void DisposeStarter() => this.starter.Dispose();

    private readonly HashSet<ushort> blacklist = [];

    /// <summary>見送りを解く時刻。入り組んだ地形でも、置いてから再挑戦する。</summary>
    private readonly Dictionary<ushort, DateTime> blacklistUntil = [];

    /// <summary>FATE ごとの詰まり回数。</summary>
    private readonly Dictionary<ushort, int> stuckCounts = [];

    /// <summary>いま狙っている FATE。</summary>
    private FateInfo? target;

    /// <summary>次に狙う FATE。達成度が閾値を超えた時点で決めておく。</summary>
    private FateInfo? prefetched;

    /// <summary>完了として数えた湧き。同じものを二度数えないために覚える。</summary>
    private (ushort Id, int Start)? countedSpawn;

    /// <summary>いま円の外へ出ようとしている FATE。出られたら消す。</summary>
    private FateInfo? retreatFrom;

    /// <summary>退避を始めた時刻。出られなくても待ち続けないために使う。</summary>
    private DateTime retreatSinceUtc = DateTime.MinValue;

    /// <summary>
    /// 退避の行き先。
    ///
    /// <b>渡した先と監視する先を同じにするために覚える。</b>
    /// 以前は監視のときだけ FATE の中心を渡していたため、
    /// 円の中に立っていることが「到着」になっていた。
    /// </summary>
    private Vector3? retreatTo;

    /// <summary>退避の向きを変えて試した回数。</summary>
    private int retreatAttempts;

    /// <summary>退避で離陸の合図を送ったか。毎フレーム送り直さないために持つ。</summary>
    private bool retreatLiftIssued;

    /// <summary>離陸の合図を送った時刻。飛べないまま固まらないよう、送り直すのに使う。</summary>
    private DateTime retreatLiftUtc = DateTime.MinValue;

    /// <summary>離陸の合図を送り直した回数。</summary>
    private int retreatLiftAttempts;

    /// <summary>退避で飛行を続けて確認した回数。1 フレームでは信じない。</summary>
    private int retreatFlyingFrames;

    /// <summary>報酬待ちで上昇の合図を送ったか。</summary>
    private bool hoverLiftIssued;

    /// <summary>
    /// 報酬待ちの納品 FATE。着地するまでマップを離れない。
    ///
    /// <b>1 件だけでは足りない。</b>
    /// 90 秒のうちに納品 FATE を 2 つ終えることがある。
    /// 単一の値だと、後の 1 件が前の 1 件を上書きし、
    /// 前の報酬を待たずにマップを離れてしまう。
    ///
    /// 鍵はエリア・FATE 番号・開始時刻。同じ番号が湧き直しても区別できる。
    /// </summary>
    private readonly List<(uint Territory, ushort Id, int Start, DateTime DeadlineUtc)> pendingRewards = [];

    /// <summary>
    /// もう離脱を済ませた FATE。
    ///
    /// <b>離れたことを覚えていないと、同じ FATE で無限に離脱し続ける。</b>
    /// FateManager.CurrentFate は、達成度 100% になっても円の中に立っている限り
    /// その FATE を指したままになる。覚えていないと毎フレーム
    /// 「100% の FATE に参加している」と読んで LeaveFate を呼び、
    /// 次の FATE への経路を引いた直後に引き直す、を延々と繰り返す。
    /// その場から一歩も動けない（2026-09-25 実測・18 ミリ秒ごとに往復していた）。
    /// </summary>
    private ushort? leftFateId;

    private DateTime moveStartedUtc = DateTime.MinValue;
    private DateTime teleportStartedUtc = DateTime.MinValue;

    /// <summary>この行き先へテレポを撃ったか。毎フレーム撃ち直さないための旗。</summary>
    private bool teleportIssued;

    /// <summary>テレポを断られた時刻。撃ち直す間隔を空けるのに使う。</summary>
    private DateTime teleportAcceptedUtc;

    private DateTime teleportRejectedUtc = DateTime.MinValue;

    /// <summary>
    /// ベンチャー回収から戻る先のマップ。
    ///
    /// 回収のために街へ行く前に控える。済んだらここへ戻って周回を続ける。
    /// </summary>
    private uint? ventureReturnTo;
    private DateTime waitingSinceUtc = DateTime.MinValue;
    private DateTime deadSinceUtc = DateTime.MinValue;

    /// <summary>降り始めた時刻。降りられないまま続くのを打ち切るのに使う。</summary>
    private DateTime landingSinceUtc = DateTime.MinValue;
    private uint travelTargetTerritory;

    /// <summary>
    /// 狙ってよい敵を決める。
    ///
    /// <b>狙う相手はこちらで決め、BMR には戦うことだけをさせる。</b>
    /// BMR の設定だけでは「FATE 以外に自分から絡まない」を表現できない
    /// （AIHintsBuilder.cs:148 が敵視のある敵を FateId に関係なく
    /// 優先度 0 にし、AutoTarget.cs:224 がそれを候補に入れる）。
    /// </summary>
    private readonly FateTargetService targets = new(anomalyLog);

    /// <summary>
    /// 飛んで FATE へ入る段取り。
    ///
    /// <b>飛行の間はこれが移動を持つ。</b>
    /// 段階ごとに経路を引き直すので、ここで同時に経路を積まない。
    /// </summary>
    private readonly FateApproach approach = new(anomalyLog, trace, vnavmesh, mount);

    /// <summary>
    /// 開始したときに固定した巡回ルート。
    ///
    /// <b>生きた設定の添字を進めない。</b>
    /// 設定画面は描画のたびに並べ替えるため、周回中にチェックを
    /// 外すと添字が別のマップを指し、マップを飛ばす。
    /// 設定の変更は、次に開始したときから効く。
    /// </summary>
    private readonly List<uint> route = [];

    private int zoneIndex;
    /// <summary>いまの目的地へ経路を引いたか。乗ってから引くので旗で覚える。</summary>
    private bool moveIssued;

    /// <summary>止めた時点で向かっていた場所。見張りが「自分の経路か」を見分けるために使う。</summary>
    private Vector3? stopGuardDestination;

    /// <summary>最後に自分が vnavmesh へ頼んだ行き先。</summary>
    private Vector3? lastMoveDestination;

    /// <summary>経路の終点が行き先とどれだけ離れていても同じとみなすか。</summary>
    private const float StopGuardMatchRadius = 15f;

    /// <summary>経路を引いたとき飛んでいたか。地面に触れて解けたのを見分ける。</summary>
    private bool flyingWhenIssued;

    /// <summary>
    /// 飛んで入れなかった FATE。
    ///
    /// これを覚えておかないと、地上の経路へ切り替えた次のフレームで
    /// また飛ぼうとして、失敗を繰り返す。
    /// </summary>
    private ushort? groundOnlyFate;

    /// <summary>敵へ近づいている最中か。戦闘に入ったら下ろす。</summary>
    private bool approaching;

    /// <summary>納品の段階にいるか。この間は敵へ近づかない。</summary>
    private bool handingIn;

    /// <summary>納品の段階に入った時刻。進まないときに打ち切るのに使う。</summary>
    private DateTime handInSinceUtc = DateTime.MinValue;

    /// <summary>
    /// 納品の段階に入ったときの所持数。
    ///
    /// <b>これが減ったことを成立の証拠にする。</b>
    /// 進捗の上昇では判断できない。他の人が納品したのかもしれない。
    /// </summary>
    private int handInHeldAtStart;

    /// <summary>納品をやり直した回数。</summary>
    private int handInAttempts;

    /// <summary>納品へ向かう間、的の抑止を入れているか。</summary>
    private bool handInParked;

    /// <summary>直前に狙うと決めた相手。同じ相手を何度も記録しないために持つ。</summary>
    private ulong lastTargetId;

    /// <summary>戦闘の段階へ入った時刻。参加扱いになるのを待つのに使う。</summary>
    private DateTime fightingSinceUtc = DateTime.MinValue;

    /// <summary>いま経路を引いている先。敵が動いても、ここから離れるまでは引き直さない。</summary>
    private Vector3 approachTarget;

    /// <summary>近づく経路を最後に引いた時刻。続けて引き直さないための間隔に使う。</summary>
    private DateTime approachIssuedUtc = DateTime.MinValue;

    /// <summary>近づく経路を引いた回数。増え続けるなら引き直しすぎている。</summary>
    private int approachIssues;

    /// <summary>vnavmesh で歩かせるためにプリセットの移動を止めているか。止めたぶんは必ず戻す。</summary>
    private bool movementParked;

    /// <summary>プリセットが有効かを最後に確かめた時刻。</summary>
    private DateTime presetCheckedUtc = DateTime.MinValue;

    /// <summary>交換から戻ったときに向かう座標。設定で座標まで戻す場合だけ入る。</summary>
    private Vector3? resumePosition;

    private bool presetApplied;
    private string appliedPresetName = string.Empty;

    /// <summary>いまの段階。</summary>
    public FateStep Step { get; private set; } = FateStep.Idle;

    /// <summary>画面に出す短い説明。</summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>達成した FATE の数。達成度 100% を見たものだけ。</summary>
    public int Completed { get; private set; }

    /// <summary>
    /// 達成しないまま終わった FATE の数。
    ///
    /// <b>完了と分けて数える。</b>混ぜると、件数が実績を表さなくなる。
    /// ここが伸びるなら、戦えていないか、着くのが遅い。
    /// </summary>
    public int Abandoned { get; private set; }

    /// <summary>動いているか。</summary>
    public bool IsRunning => this.Step is not (FateStep.Idle or FateStep.Done or FateStep.Error);

    /// <summary>止めた理由。</summary>
    public string? StoppedReason { get; private set; }

    /// <summary>
    /// いま実際に回っているルート。止まっていれば空。
    ///
    /// 画面で「設定を変えたが、この周回には効かない」ことを
    /// 伝えるために見せる。
    /// </summary>
    public IReadOnlyList<uint> RunningRoute => this.IsRunning ? this.route : [];

    private static Config C => Plugin.C;

    /// <summary>周回を始める。始められない場合は理由を返す。</summary>
    public bool Start(out string reason)
    {
        this.trace.Decision("開始を押した", $"段階={this.Step}");

        if (this.IsRunning)
        {
            reason = "すでに動いています";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        var cfg = C;

        if (cfg.FateZones.Count == 0)
        {
            reason = "周回するマップが選ばれていません";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        if (!this.bossMod.IsLoaded)
        {
            reason = "BossMod Reborn が導入されていません";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        if (!this.navigation.IsAvailable)
        {
            reason = "vnavmesh が導入されていません";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        // **テレポの手段を、始める前に確かめる。**
        //
        // 以前は何も見ずに始め、最初のマップへ移ろうとして初めて
        // 「テレポできない」と分かっていた。60 秒待ってから次のマップへ、
        // それも駄目でまた 60 秒、と静かに時間を捨てることになる。
        if (!this.lifestream.IsLoaded)
        {
            reason = "Lifestream が導入されていません（マップの移動に必要です）";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        // 利用者がプリセット名を指定していなければ、こちらで用意する。
        // どれを選べばよいか分からないのが普通なので、名前を知らなくても動くようにする。
        if (string.IsNullOrWhiteSpace(cfg.FateCombatPreset)
            && !FateCombatPreset.Ensure(this.bossMod, this.anomalyLog))
        {
            reason = $"BossMod Reborn にプリセット「{FateCombatPreset.Name}」を用意できませんでした";
            this.trace.Trouble("開始できない", reason);
            return false;
        }

        this.blacklist.Clear();
        this.blacklistUntil.Clear();
        this.stuckCounts.Clear();
        this.target = null;
        this.prefetched = null;
        this.pendingRewards.Clear();
        this.countedSpawn = null;
        this.retreatFrom = null;
        this.leftFateId = null;
        this.landable = null;
        this.landingRefuge = null;
        this.travelTargetTerritory = 0;
        this.teleportIssued = false;

        // **止めたあとの見張りを解く。**
        //
        // Stop が 20 秒の見張りを立てる。解かずに再開すると、
        // その間ずっと「止めたあとに積まれた経路」とみなして捨て続け、
        // 動き出せない。交換から戻るときは 2 秒後に再開するため、
        // 毎回 18 秒ほど棒立ちになっていた。
        this.stopGuardUntil = DateTime.MinValue;

        // 移動まわりの状態も、前回の周回から引き継がない。
        this.flyingWhenIssued = false;
        this.moveIssued = false;
        this.approaching = false;
        this.approachIssues = 0;
        this.escapeAttempts = 0;
        this.blockDetours = 0;
        this.detourTask = null;
        this.blockCheckedUtc = DateTime.MinValue;
        this.watchdogSince = DateTime.MinValue;
        this.RestorePathTolerance();

        this.Completed = 0;
        this.Abandoned = 0;
        this.retreatTo = null;
        this.retreatAttempts = 0;
        this.retreatLiftIssued = false;
        this.retreatLiftAttempts = 0;
        this.retreatFlyingFrames = 0;
        this.groundOnlyFate = null;
        this.teleportRejectedUtc = DateTime.MinValue;
        this.ventureReturnTo = null;
        this.handingIn = false;
        this.handInParked = false;
        this.handInAttempts = 0;
        this.handInHeldAtStart = 0;
        this.lastTargetId = 0;
        this.targets.Reset();
        this.approach.Cancel("周回を始め直します");
        this.StoppedReason = null;
        this.presetApplied = false;
        this.appliedPresetName = string.Empty;
        this.buddy.Reset();

        // **一覧の上から順に回る。**
        //
        // 以前は「いまいるマップが一覧にあれば、そこから始める」としていた。
        // そのため始めた場所によって巡る順番が変わり、
        // 画面に並べた順とは違う動きになっていた。
        //
        // 並べた順がそのまま巡る順になるほうが、見たとおりで分かりやすい。
        //
        // **開始時に、保存の並びを画面の並びへ揃え直す。**
        // 設定は選んだ順に足されるため、以前に選んだものは押した順のまま。
        // ここで直せば、古い設定でも画面どおりの順で回る。
        var ordered = this.zoneCatalog.SortByDisplayOrder(cfg.FateZones);

        if (!ordered.SequenceEqual(cfg.FateZones))
        {
            cfg.FateZones.Clear();
            cfg.FateZones.AddRange(ordered);
            ECommons.Configuration.EzConfig.Save();

            this.anomalyLog.Info("Fate", "周回するマップの並びを、画面の順へ整えました");
        }

        if (cfg.FateZones.Count == 0)
        {
            reason = "周回するマップを選んでください";
            return false;
        }

        // **回る順を、開始した時点で固定する。**
        //
        // 以前は生きた設定の添字を進めていた。設定画面は描画のたびに
        // 並べ替えるので、周回中にマップのチェックを外すと、
        // 添字が別のマップを指す。A/B/C/D を並べて B を回っている最中に
        // A を外すと、一覧は B/C/D になるが添字は 1 のまま。
        // 次に進めると 2 になり、C を飛ばして D へ飛ぶ。
        //
        // 固定したルートを持てば、途中で設定が変わっても順番が崩れない。
        // 変更は次に開始したときから効く。
        this.route.Clear();
        this.route.AddRange(cfg.FateZones);
        this.zoneIndex = 0;

        // **テレポ先が見つかるかを、始める前に確かめる。**
        //
        // 見つからないマップは、行こうとして 60 秒待ってから諦めることになる。
        // 事前に分かるなら伝える。
        //
        // **ただし一覧が空のときは判断しない。**
        //
        // Svc.AetheryteList はコンテンツ（ID・レイド）の中では空になる。
        // 空を「1 つもアクセスしていない」と読むと、行けるはずのマップへ
        // 「エーテライトが無い」と言って開始を拒否してしまう。
        // この罠はこのプラグインで一度踏んでいる
        // （AetheryteService.IsListReady のコメントを参照。
        //  討伐中に「ソリューション・ナイン へ行けません」で止まった）。
        //
        // 読めないときは黙って通す。行こうとした時点で分かるし、
        // そのときは 60 秒のタイムアウトが面倒を見る。
        if (this.aetherytes.IsListReady())
        {
            var unreachable = this.route
                .Where(t => !this.aetherytes.TryFindTarget(t, out var found) || found is null)
                .ToList();

            if (unreachable.Count > 0)
            {
                var names = string.Join("、", unreachable.Select(NpcLocationService.GetTerritoryName));

                if (unreachable.Count == this.route.Count)
                {
                    reason = $"選んだマップへのテレポ先が見つかりません（{names}）。エーテライトを解放してください";
                    this.trace.Trouble("開始できない", reason);
                    return false;
                }

                this.anomalyLog.Warn(
                    "Fate",
                    $"テレポ先が見つからないマップがあります（{names}）。" +
                    "そのマップは飛ばして回ります");
            }
        }
        else
        {
            this.trace.State(
                "テレポ先を確かめない",
                "エーテライトの一覧が空です（コンテンツの中かもしれません）。行こうとした時点で判断します");
        }

        // 先頭のマップに居なければ、まずそこへ向かう。
        // 行き先を控えておかないと「一覧のマップに居る」と見なされ、
        // いま居るマップで回り始めてしまう。
        var here = Svc.ClientState.TerritoryType;
        var first = this.route[0];

        this.travelTargetTerritory = here == first ? 0 : first;
        this.teleportIssued = false;

        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
        // **ベンチャーの見張りも一緒に始める。**
        // 周回と生死を揃える。止めたのに回収へ行き続けるのは制御を失っている。
        ventures.Start(cfg);

        // **実際に回る順を残す。**
        // 画面の並びと巡回順が食い違っていないかを、あとから確かめられるようにする。
        var describedRoute = string.Join(" → ", this.route.Select(NpcLocationService.GetTerritoryName));
        this.anomalyLog.Info("Fate", $"FATE 周回を開始しました（巡回順: {describedRoute}）");
        this.trace.Decision("開始した", $"マップ {this.route.Count} 件 いまのエリア={Svc.ClientState.TerritoryType}");

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 止めたあとに動き出していないかを見張り、動き出していたら止める。
    ///
    /// <b>1 回止めるだけでは足りない。</b>
    /// vnavmesh の Path.Stop は、積んである経路点を捨てるだけ
    /// （FollowPath.Stop）で、探索中の経路までは取り消さない。
    /// 止めた直後に探索が終わると、その結果が積まれてまた歩き出す
    /// （AsyncMoveRequest.Update が _follow.Move を呼ぶ）。
    ///
    /// 探索は数秒かかることがあるので、しばらく見張って捨て続ける。
    /// </summary>
    private void TickStopGuard()
    {
        if (this.stopGuardUntil == DateTime.MinValue)
        {
            return;
        }

        if (DateTime.UtcNow > this.stopGuardUntil)
        {
            this.stopGuardUntil = DateTime.MinValue;
            return;
        }

        if (!this.vnavmesh.TryNumWaypoints(out var waypoints) || waypoints <= 0)
        {
            return;
        }

        // **自分が積んだ経路だけ捨てる。**
        //
        // vnavmesh の経路は 1 本しかなく、交換の移動・リテイナーの呼び鈴への移動・
        // 納品窓口への移動・AutoDuty のダンジョン内の移動が、同じ 1 本を使う。
        //
        // 以前は持ち主を見ずに捨てていた。そのため FATE を止めた直後の 20 秒間、
        // **他の処理の移動が毎フレーム止められ**、「歩き出してすぐ止まる」を
        // 繰り返して移動そのものが失敗した。
        //
        // 終点が、止めたとき向かっていた場所と合うものだけを捨てる。
        if (!this.IsOurPath())
        {
            // **捨てなかったことを記録へ出す。**
            //
            // 黙って見送ると、「見張りが効いていない」のか
            // 「他人の経路だから残した」のかが追えない。
            // 毎フレーム通るので間引く。
            if (EzThrottler.Throttle("AutoCollector.FateStopGuardKeep", 5000))
            {
                this.trace.Decision("止めたあとの経路を残した", $"自分の経路ではありません（経路点 {waypoints} 個）");
            }

            return;
        }

        this.vnavmesh.TryStop();
        this.trace.Decision("止めたあとの経路を捨てた", $"経路点 {waypoints} 個");
    }

    /// <summary>
    /// いま積まれている経路が、自分が止める直前に頼んだものか。
    ///
    /// 終点を比べる。判断がつかないときは <b>false</b>（＝捨てない）に倒す。
    /// 他人の経路を捨てるほうが、自分の経路を捨て損なうより害が大きい。
    /// </summary>
    private bool IsOurPath()
    {
        if (this.stopGuardDestination is not { } mine)
        {
            return false;
        }

        if (!this.vnavmesh.TryListWaypoints(out var points) || points is not { Count: > 0 })
        {
            return false;
        }

        // 終点どうしを比べる。経路は目的地の近くで終わるので、多少の幅を持たせる。
        return Vector3.DistanceSquared(points[^1], mine) <= StopGuardMatchRadius * StopGuardMatchRadius;
    }

    /// <summary>最後に自分が向かおうとした場所。分からなければ null。</summary>
    private Vector3? LastRequestedDestination()
        => this.starter.Destination ?? (this.approaching ? this.approachTarget :
            this.moveIssued ? this.lastMoveDestination : null);

    /// <summary>
    /// 段階に関係なく、動けなくなっていないかを見張る。
    ///
    /// <b>「動くつもりなのに動いていない」を捕まえる。</b>
    /// 待っているだけの段階（FATE が湧くのを待つ、報酬を待つ）は
    /// 動かなくて当たり前なので見ない。
    ///
    /// 動くはずの段階で座標が変わらない状態が続いたら、
    /// 地形に挟まっているとみなして脱出へ進む。
    /// </summary>
    /// <returns>脱出の処理をしたなら true。その場合この Tick は先へ進めない。</returns>
    private bool TickStuckWatchdog()
    {
        // 動くはずの段階かどうか。
        // **テレポ中は見張らない。**
        //
        // テレポには詠唱があり、そのあいだ足は止まる。
        // 動いていないからと引っ張ると詠唱が中断され、
        // 撃ち直してはまた中断される、を繰り返して永久に飛べない
        // （2026-09-26 実測。Zard が同じ場所で 4 回引っ張られていた）。
        //
        // テレポが終わらない場合は、TickTraveling のタイムアウトが面倒を見る。
        var shouldMove = this.Step is FateStep.MovingToFate
                                   or FateStep.Landing
                      || (this.Step == FateStep.Fighting && this.approaching);

        if (!shouldMove || !Player.Available)
        {
            this.watchdogSince = DateTime.MinValue;
            return false;
        }

        // **ベンチャー回収で動いている間は見張らない。**
        //
        // デジョンにも詠唱があり、そのあいだ足は止まる。
        // 動いていないからと引っ張ると詠唱が中断され、撃ち直しては
        // また中断される、を繰り返して永久に飛べない。
        // テレポで同じことを踏んでいる（上のコメント）。
        //
        // 呼び鈴へ歩く移動も、こちらの見張りの管轄ではない。
        // 進まないときは VentureWatcher 側の上限が面倒を見る。
        if (ventures.Interrupting)
        {
            this.watchdogSince = DateTime.MinValue;
            return false;
        }

        // **報酬待ちを理由に、見張りを丸ごと止めない。**
        //
        // 以前は報酬待ちが 1 件でもあれば、どの段階でも見張りを止めていた。
        // 納品 FATE を 1 つ終えると約 1 分間、次の FATE へ向かう移動中も
        // 着地中も詰まりに気づけなかった。
        //
        // ここへ来ている時点で、段階は「動くはずの段階」に限られている。
        // 報酬を待つのは足を止めてよいが、それは待っているマップに
        // 居るあいだの話で、移動中は別。
        //
        // 待つなら段階は Waiting になるので、その段階はそもそも
        // 上の shouldMove で除かれている。ここで止める必要は無い。
        if (this.pendingRewards.Count > 0 && this.Step is not FateStep.MovingToFate)
        {
            this.watchdogSince = DateTime.MinValue;
            return false;
        }

        var now = DateTime.UtcNow;
        var here = Player.Position;

        if (this.watchdogSince == DateTime.MinValue ||
            Vector3.Distance(here, this.watchdogPosition) > WatchdogProgressMeters)
        {
            this.watchdogSince = now;
            this.watchdogPosition = here;
            return false;
        }

        if (now - this.watchdogSince < WatchdogPatience)
        {
            return false;
        }

        // 動くはずなのに、ずっと同じ場所にいる。
        this.trace.Trouble(
            "動けていない",
            $"段階={this.Step} {WatchdogPatience.TotalSeconds:F0}秒間 " +
            $"({here.X:F0},{here.Y:F0},{here.Z:F0}) から動いていません");

        this.watchdogSince = now;
        this.watchdogPosition = here;

        // 狙っている FATE があればそれを、無ければ番号なしで脱出へ。
        this.EscapeStuckSpot(this.target, now, here);
        return true;
    }

    /// <summary>
    /// 詰めていた経路追従の許容値を、vnavmesh の既定へ戻す。
    ///
    /// <b>詰めたままにしない。</b>この設定は vnavmesh 全体のもので、
    /// 他のプラグインの移動にも効いてしまう。
    /// </summary>
    private void RestorePathTolerance()
    {
        if (!this.tightenedPath)
        {
            return;
        }

        this.tightenedPath = false;
        this.vnavmesh.TrySetPathTolerance(DefaultPathTolerance);
    }

    /// <summary>
    /// いまの場所から脱出する。画面のボタンから呼ぶ。
    ///
    /// <b>周回していなくても使える。</b>
    /// 止めた状態で入り組んだ場所に取り残されることがあるため、
    /// 手元にも逃げ道を置いておく。
    /// </summary>
    public void EscapeNow()
    {
        this.navigation.Stop();
        this.vnavmesh.TryStop();
        this.moveIssued = false;

        // **止めたあとの見張りを解く。**
        // 解かないと、これから積む脱出の経路を見張りが即座に捨ててしまい、
        // ボタンを押しても動かない。このボタンは「止めた直後」に
        // 使われることが多いので、必ずここで解く。
        this.stopGuardUntil = DateTime.MinValue;

        if (!Player.Available)
        {
            return;
        }

        var here = Player.Position;

        // 立てる場所を探す。まず真下、次に周り。
        Vector3? found = null;

        if (this.vnavmesh.TryIsReady(out var ready) && ready)
        {
            if (this.vnavmesh.TryPointOnFloor(here, out var floor) && floor is not null)
            {
                found = floor;
            }
            else if (this.vnavmesh.TryNearestPointReachable(here, 60f, 200f, out var near) && near is not null)
            {
                found = near;
            }
            else if (this.vnavmesh.TryNearestPoint(here, 150f, 300f, out var any) && any is not null)
            {
                found = any;
            }
        }

        // **2 回目は帰還する。**
        //
        // 1 回押して動かなかったのなら、動かして出せる場所ではない。
        // 同じことを繰り返しても結果は同じなので、座標ごと外へ出す。
        var repeated = DateTime.UtcNow - this.lastEscapeNowUtc < EscapeNowRetryWindow
                    && Vector3.Distance(here, this.lastEscapeNowFrom) < 5f;

        this.lastEscapeNowUtc = DateTime.UtcNow;
        this.lastEscapeNowFrom = here;

        if (found is { } spot && !repeated)
        {
            this.anomalyLog.Info(
                "Fate",
                $"({here.X:F0},{here.Y:F0},{here.Z:F0}) から " +
                $"({spot.X:F0},{spot.Y:F0},{spot.Z:F0}) へ脱出します。" +
                "動かなければ、もう一度押すと帰還します");

            this.vnavmesh.TryMoveAlong([spot], true);
            return;
        }

        // 動かして出せない。帰還で外へ出す。
        this.anomalyLog.Warn(
            "Fate",
            repeated
                ? "動かして出られないため、帰還して脱出します"
                : "立てる場所が見つからないため、帰還して脱出します");

        ReturnHome();
    }

    /// <summary>周回を止める。戦闘とプリセットを必ず元に戻す。</summary>
    public void Stop(string reason)
    {
        var ownedMovement = this.IsRunning || this.approach.Active || this.approaching;
        var previousDestination = this.LastRequestedDestination();
        this.starter.CancelMovement(this.navigation);
        this.starter.Reset();
        this.startingFate = false;
        // **解除は状態を見ずに先に行う。**
        //
        // 止まっている状態でも、プリセットを有効にしたまま何らかの理由で
        // 段階だけが進んでいることがありうる。そのまま戻ると BMR が
        // 動き続けてしまい、利用者からは「止めたのに戦い続ける」に見える。
        // ReleaseCombat は適用していなければ即座に戻るので、余分な害は無い。
        this.ReleaseCombat();

        // **ベンチャーの見張りも止める。**
        // 止めたのに回収へ行き続けるのは、利用者から見て制御を失っている。
        // 回収の途中なら呼び鈴の画面も閉じる。
        ventures.Stop(reason);

        // **進入の段取りも止める。**
        // 世代を進めて、走っている経路探索の結果を捨てさせる。
        // 止めないと、探索が終わった時点で経路を渡してまた動き出す。
        this.approach.Cancel($"止めました: {reason}");

        // **移動は段階を見ずに必ず止める。**
        //
        // 詰まりからの脱出は vnavmesh へ直接 Path.MoveTo を送っている。
        // これは段階（Step）と関係なく走り続けるため、
        // Idle や Done で先に return していると止まらない。
        // 利用者から見ると「止めたのに勝手に飛び続ける」になる
        // （2026-09-25 実測）。
        this.navigation.Stop();
        if (ownedMovement) this.vnavmesh.TryStop();

        // **止めたあとに動き出さないよう、しばらく見張る。**
        //
        // vnavmesh の Path.Stop は、積んである経路点を捨てるだけ
        // （FollowPath.Stop）。探索中の経路までは取り消さない。
        // そのため、止めた直後に探索が終わると
        // AsyncMoveRequest.Update がその結果を積み、また歩き出す。
        //
        // 探索が終わるのを待って、積まれたら捨てる。
        this.stopGuardUntil = ownedMovement ? DateTime.UtcNow + StopGuardWindow : DateTime.MinValue;

        // **どこへ向かっていたかを控える。**
        // 見張りはこの行き先へ向かう経路だけを捨てる。
        // 控えないと、他の処理が積んだ経路まで巻き添えにする。
        this.stopGuardDestination = previousDestination;

        // 頼んである経路探索の結果も捨てる。
        // 残しておくと、止めたあとに出来上がって積まれてしまう。
        this.detourTask = null;
        this.landingRefuge = null;
        this.escapeAttempts = 0;
        this.blockDetours = 0;
        this.moveIssued = false;
        this.watchdogSince = DateTime.MinValue;
        this.RestorePathTolerance();

        if (this.Step is FateStep.Idle or FateStep.Done)
        {
            return;
        }

        // 降下の途中で止められることがある。そのままだと降下が続き、
        // 次に誰かが経路を積んだときに捨てさせてしまう。
        this.mount.ClearDismounting();

        this.StoppedReason = reason;
        this.target = null;
        this.prefetched = null;
        this.SetStep(FateStep.Done, reason);
        this.anomalyLog.Info("Fate", $"FATE 周回を止めました: {reason}（完了 {this.Completed} 件）");
    }

    // ---- 交換のための中断と復帰（FateEarner から呼ばれる） ----

    /// <summary>
    /// この環境で周回を使えるか。
    ///
    /// 周回には BossMod Reborn と vnavmesh の両方が要る。
    /// 片方でも欠けていれば、稼ぎ方の一覧にも出さない。
    /// </summary>
    public bool IsUsable => this.bossMod.IsLoaded && this.navigation.IsAvailable;

    /// <summary>いま FATE の中で戦っているか。中断してよい切れ目かの判断に使う。</summary>
    /// <remarks>
    /// 着地の途中も含める。空中で中断されると、降りきらないまま
    /// 放り出されて空に取り残される。
    /// </remarks>
    public bool IsInFate => this.Step is FateStep.Fighting or FateStep.Leaving or FateStep.Landing;

    /// <summary>
    /// 納品 FATE の報酬を待っているか。
    ///
    /// 待っている間にエリアを離れると報酬が消える。
    /// 交換のための中断もこの間は見送る。
    /// </summary>
    public bool HasPendingReward => this.pendingRewards.Count > 0;

    /// <summary>
    /// 交換のために一時的に止める。
    ///
    /// <see cref="Stop"/> と違い、<see cref="StoppedReason"/> を
    /// 「利用者が止めた」とは扱わない。戻すのは FateEarner の役目。
    /// </summary>
    public void Suspend(string reason)
    {
        if (!this.IsRunning)
        {
            return;
        }

        this.Stop(reason);
    }

    /// <summary>
    /// 中断していた周回を戻す。
    ///
    /// エリアが違っていれば、そのエリアへ向かうところから始める。
    /// 座標まで戻すかどうかは設定で決まる（既定は戻さない）。
    /// 着いてしまえば、いちばん近い FATE から回り直すので支障はない。
    /// </summary>
    public bool Resume(uint territoryId, Vector3 position, out string reason)
    {
        if (this.IsRunning)
        {
            reason = string.Empty;
            return true;
        }

        if (!this.Start(out reason))
        {
            return false;
        }

        // 戻る先が選択に含まれていれば、そこから再開する。
        //
        // **行き先も一緒に直す。**
        // Start は「先頭のマップへ向かう」を立てる。添字だけ直しても
        // 行き先は先頭のままなので、いったん先頭へ向かう状態を通ってしまう。
        var index = this.route.IndexOf(territoryId);
        if (index >= 0)
        {
            this.zoneIndex = index;

            // すでに戻り先に居るなら移動は要らない。
            this.travelTargetTerritory =
                Svc.ClientState.TerritoryType == territoryId ? 0 : territoryId;
            this.teleportIssued = false;
        }

        // 座標まで戻すかは設定次第。エリアへは必ず戻るので、
        // 入れていなくても、いちばん近い FATE から回り直せる。
        this.resumePosition = null;

        if (Plugin.C.FateReturnToExactSpot
            && position != Vector3.Zero
            && Svc.ClientState.TerritoryType == territoryId
            && this.navigation.BeginMove(position, 10f, out _))
        {
            this.resumePosition = position;
        }

        return true;
    }

    /// <summary>毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        // **止めたあとも、しばらくは見張る。**
        //
        // 止めた時点で探索中だった経路は、終わった瞬間に積まれて
        // また歩き出す（vnavmesh の AsyncMoveRequest.Update）。
        // Path.Stop は積んである経路点を捨てるだけで、
        // 探索そのものは取り消せないため、ここで拾って捨てる。
        this.TickStopGuard();

        if (!this.IsRunning)
        {
            return;
        }

        try
        {
            this.TickCore();
        }
        catch (Exception ex)
        {
            // 1 回の例外で周回ごと落とさない。記録して次のフレームで続ける。
            this.anomalyLog.Warn("Fate", $"処理中に例外が出ました: {ex.Message}");
        }
    }

    private void TickCore()
    {
        var cfg = C;

        // 報酬待ちの状態はどの段階でも更新する。マップを離れてよいかの判断に使う。
        this.RefreshPendingReward();

        // 飛んでいるあいだ、届いた高さを覚える。
        // そのマップで飛べる高さの上限を知る手段が他に無い。
        MountService.ObserveCeiling();

        // 見送りの期限が切れた FATE を戻す。
        this.ExpireBlacklist();

        // 自分が開始した会話は、会話中の操作不可判定より先に進める。
        if (this.starter.TickDialog(this.scanner)) return;

        // 1. 自動操作が成立しない状況では何もしない。
        //    戦闘・詠唱・動作中は FATE 周回では正常なので弾かれない。
        if (!SafetyGuard.IsSafeToRunFate(out var blockReason, out _))
        {
            this.StatusDetail = blockReason;
            return;
        }

        // 2. 戦闘不能。
        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            this.TickDead(cfg);
            return;
        }

        // **どの段階でも、動けなくなっていないかを見張る。**
        //
        // 詰まりの検出は FATE へ向かっている最中にしか置いていなかった。
        // そのため着地の途中や、敵へ近づいている最中に地形へ挟まると、
        // 誰も気づかないまま止まり続けていた。
        // 段階に関係なく見張り、抜け出せなければ帰還まで持っていく。
        if (this.TickStuckWatchdog())
        {
            return;
        }

        if (this.Step == FateStep.Dead)
        {
            // 生き返った。
            this.deadSinceUtc = DateTime.MinValue;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
        }

        // 2.5 ベンチャーの回収。
        //
        // **回収へ行っている最中は、周回の処理をどれも走らせない。**
        // 走らせると、こちらは街へ向かい、周回は FATE へ向かうことになって
        // 経路を取り合う。
        if (this.TickVentures(cfg))
        {
            return;
        }

        // 3. バディの面倒を見る。戦闘外でだけ動く。
        if (cfg.FateBuddyEnabled && this.Step is FateStep.Waiting or FateStep.MovingToFate)
        {
            if (this.buddy.Tick(cfg.FateBuddyMinSecondsRemaining, cfg.FateGysahlMinCount))
            {
                this.StatusDetail = this.buddy.StatusDetail;
                return;
            }
        }

        // 4. 周回するマップにいるか。
        //
        // **「一覧に載っているマップにいるか」だけで判断しない。**
        //
        // 一覧のマップはどれも載っているので、FATE が尽きて次のマップへ
        // 移ろうと決めた直後も「いま載っているマップにいる」が成立する。
        // そのため移動の段階が次のフレームで取り消され、
        // テレポが一度も実行されなかった（2026-09-25 実測。
        // Traveling に入って 18 ミリ秒で Waiting に戻っていた）。
        //
        // 行き先を決めているあいだは、そちらへ着くまで移動を続ける。
        var inZone = this.route.Contains(Svc.ClientState.TerritoryType);
        var heading = this.travelTargetTerritory != 0
                   && this.travelTargetTerritory != Svc.ClientState.TerritoryType;

        if (!inZone || heading)
        {
            // **別のマップへ移るなら、進入の段取りを畳む。**
            //
            // 畳まないと、進入は Tick が呼ばれないまま Active のまま残る。
            // 段階の中でエリアの食い違いを見ているが、その判定にも
            // 辿り着けない。経路探索の Task も残ったままになる。
            //
            // いまは vnavmesh がメッシュの読み込み直しで探索を
            // 取り消してくれるので壊れにくいが、それは他のプラグインの
            // 都合に頼っているだけで、こちらの筋が通っていない。
            if (this.approach.Active)
            {
                this.approach.Cancel("別のマップへ移ります");
                this.groundOnlyFate = null;
            }

            this.TickTraveling(cfg);
            return;
        }

        // 行き先に着いた。移動状態は畳む。
        if (this.Step == FateStep.Traveling)
        {
            this.teleportStartedUtc = DateTime.MinValue;
            this.travelTargetTerritory = 0;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
        }

        // 交換から戻ったときの座標へ寄る。
        //
        // **FATE があればそちらを優先する。**
        // 戻る途中で FATE が湧いていたら、わざわざ元の場所まで行く意味がない。
        // ここで足を止めるのは、狙える FATE が無いあいだだけ。
        if (this.resumePosition is { } spot)
        {
            if (this.PickNext(cfg) is not null || !Player.Available)
            {
                this.resumePosition = null;
            }
            else if (Vector3.Distance(Player.Position, spot) < 15f)
            {
                this.resumePosition = null;
                this.navigation.Stop();
            }
            else
            {
                this.StatusDetail = "交換前にいた場所へ戻っています";
                var status = this.navigation.Tick(spot, 10f);
                if (status is MoveStatus.Arrived or MoveStatus.ShortOfTarget or MoveStatus.Stuck or MoveStatus.Failed)
                {
                    this.resumePosition = null;
                    this.navigation.Stop();
                }
                else if (status == MoveStatus.Moving)
                {
                    return;
                }
            }
        }

        // **達成度 100% は、どの段階より先に見る。**
        //
        // 以前は降下（Landing）の処理が先にあり、降りている最中は
        // 100% の判定を通らなかった。他の人が終わらせた FATE へ
        // 降り続け、着地してから初めて「もう終わっている」と気づく。
        //
        // 降りている最中でも、終わったと分かった時点で離脱へ移る。
        if (this.Step is FateStep.Landing or FateStep.Fighting
            && this.target is { } landingTarget
            && this.scanner.GetById(landingTarget.Id) is { } liveTarget
            && liveTarget.Id != this.leftFateId
            && (liveTarget.Progress >= 100 || !liveTarget.IsActive))
        {
            // **達成と、失敗・時間切れを分ける。**
            //
            // どちらもここへ来るが、同じ扱いにしてはいけない。
            // 以前は区別せず完了として数え、「100% になりました」と
            // 書いていた。進捗 40% で失敗しても完了 1 件が増えていた。
            var outcome = liveTarget.Progress >= 100
                ? FateOutcome.Success
                : FateOutcome.Failed;

            this.trace.Decision(
                outcome == FateOutcome.Success ? "終わったので離れる" : "達成できなかったので離れる",
                $"{liveTarget.Name} 進捗{liveTarget.Progress}% 状態={liveTarget.State} 段階={this.Step}");

            this.LeaveFate(cfg, liveTarget, outcome);
            return;
        }

        // **降りている最中はここで完結させる。**
        // この下には vnavmesh を触る処理がある。降下中に触ると、
        // ゲームの降下がプレイヤーの操作として読まれ、経路が捨てられる。
        if (this.Step == FateStep.Landing)
        {
            this.TickLanding(cfg);
            return;
        }

        // **円の外へ出ている最中は、それを見届ける。**
        // ここを作らないと、積んだ退避の経路を誰も監視しない。
        if (this.Step == FateStep.Leaving && this.retreatFrom is not null)
        {
            this.TickLeaving(cfg);
            return;
        }

        // 5. いま参加している FATE があるか。
        //
        // **もう離れた FATE は見ない。**
        // 円の中に立っている限り CurrentFate はその FATE を指したままなので、
        // これが無いと 100% の FATE を毎フレーム拾い直し、
        // 離脱と経路引きを往復して一歩も動けなくなる。
        var current = this.scanner.GetCurrent();

        if (this.leftFateId is { } left)
        {
            if (current is not null && current.Id == left)
            {
                // まだその FATE の円の中にいる。見なかったことにする。
                current = null;
            }
            else
            {
                // 円から出た。もう拾われないので、印は要らない。
                // ここで消さないと、同じ番号の FATE が後から湧いたときに
                // 二度と入れなくなる。
                this.leftFateId = null;
            }
        }

        // **進入している最中は、横入りさせない。**
        //
        // CurrentFate は円の端に触れた時点で埋まる。そのまま TickInFate へ
        // 入ると、進入の段階が組んだ経路と降車の判断を上書きしてしまう。
        // 地上に立つ（ReadyForCombat）まではこちらが持ち続ける。
        if (this.approach.Active && this.Step == FateStep.MovingToFate)
        {
            this.TickMoving(cfg);
            return;
        }

        // 準備中は CurrentFate がまだ空の場合もある。到着済みの対象を使う。
        if (current is null && this.Step == FateStep.Fighting && this.target is { } pending)
            current = this.scanner.GetById(pending.Id);

        if (current is not null && current.IsActive)
        {
            this.TickInFate(cfg, current);
            return;
        }

        // 参加していないのに Fighting のままなら、FATE が終わったか離れた。
        //
        // **ただし、入った直後は待つ。**
        //
        // CurrentFate は、円に入っただけでは埋まらない。レベルシンクが
        // 入って参加扱いになるまで、少しのあいだ null のままになる。
        // そこで畳んでしまうと、入った 17 ミリ秒後に「参加していない」と
        // 見て離脱し、同じ FATE をまた狙って乗り直す、を繰り返す
        // （2026-09-26 実測。Zard が同じ FATE で乗り降りを繰り返していた）。
        if (this.Step is FateStep.Fighting or FateStep.Leaving)
        {
            if (DateTime.UtcNow - this.fightingSinceUtc < FateJoinGrace)
            {
                this.StatusDetail = "FATE に参加するのを待っています";
                return;
            }

            this.FinishCurrentFate();
        }

        // 6. 狙う FATE を決めて向かう。
        this.TickSeek(cfg);
    }

    // ---- 各段階 ----

    private void TickDead(Config cfg)
    {
        if (this.Step != FateStep.Dead)
        {
            this.ReleaseCombat();
            this.navigation.Stop();
            this.deadSinceUtc = DateTime.UtcNow;
            this.SetStep(FateStep.Dead, "戦闘不能です");
            this.anomalyLog.Info("Fate", "戦闘不能になりました");
        }

        var action = cfg.FateDeathAction;
        if (action == FateDeathAction.Auto)
        {
            action = Svc.Party.Length == 0 ? FateDeathAction.Return : FateDeathAction.Wait;
        }

        if (action == FateDeathAction.Wait)
        {
            // **いまレイズが飛んできているなら、時間切れでも帰らない。**
            //
            // 詠唱の途中で帰ると、かけてくれた人の詠唱を無駄にしたうえで
            // 街から戻り直すことになる。かかるのを待つほうが早い。
            if (IsBeingRaised())
            {
                this.StatusDetail = "レイズが飛んできています。かかるのを待ちます";
                return;
            }

            var waited = DateTime.UtcNow - this.deadSinceUtc;
            var remaining = cfg.FateRaiseWaitSeconds - (int)waited.TotalSeconds;
            if (remaining > 0)
            {
                this.StatusDetail = $"レイズを待っています（残り {remaining} 秒）";
                return;
            }

            if (EzThrottler.Throttle("AutoCollector.FateRaiseTimeout", 30000))
                this.anomalyLog.Info("Fate", $"{cfg.FateRaiseWaitSeconds} 秒待ちましたがレイズされませんでした。街へ戻ります");
        }

        this.StatusDetail = "戦闘不能画面からホームポイントへ戻っています";
        ReturnAfterDeath();
    }

    private void TickTraveling(Config cfg)
    {
        // **周回中にマップの選択を全部外されることがある。**
        //
        // その場合 Count - 1 が -1 になり、Math.Clamp が例外を投げる。
        // 例外は Tick が握って記録するだけなので、利用者からは
        // 「何も起きないまま止まっている」ように見える。
        if (this.route.Count == 0)
        {
            this.StatusDetail = "周回するマップが選ばれていません";
            this.Stop("周回するマップが無くなりました");
            return;
        }

        var destination = this.route[Math.Clamp(this.zoneIndex, 0, this.route.Count - 1)];

        // 納品 FATE の報酬を待っている間はマップを離れない。離れると報酬が消える。
        if (this.pendingRewards.Count > 0)
        {
            this.StatusDetail = "納品 FATE の報酬を待っています";
            return;
        }

        // 行き先が決まっていなければ、ここで決める。
        // （周回外のマップに居るときは、この経路で戻ってくる）
        if (this.Step != FateStep.Traveling || this.travelTargetTerritory != destination)
        {
            this.travelTargetTerritory = destination;
            this.teleportStartedUtc = DateTime.UtcNow;
            this.teleportIssued = false;
            this.SetStep(FateStep.Traveling, $"{NpcLocationService.GetTerritoryName(destination)} へ移動しています");
        }

        // 受理後に詠唱を取り消された場合も、連打せず再試行する。
        if (this.teleportIssued && DateTime.UtcNow - this.teleportAcceptedUtc > TimeSpan.FromSeconds(15) &&
            Player.Object is { IsCasting: false } &&
            this.lifestream.TryIsBusy(out var teleportBusy) && !teleportBusy)
        {
            this.teleportIssued = false;
            this.teleportRejectedUtc = DateTime.UtcNow;
        }

        // **テレポは 1 度だけ撃つ。**
        // 毎フレーム撃つと、詠唱が始まるたびに撃ち直して一生飛べない。
        if (!this.teleportIssued)
        {
            // **撃ち直す間隔を空ける。**
            //
            // 断られたときに旗を倒して撃ち直すようにしたが、間隔を
            // 空けていなかったため、断られている間は毎フレーム撃っていた。
            // ログは 5 秒に間引いていたので、記録の上では
            // 「5 秒に 1 回試している」ように見えていた。
            if (this.teleportRejectedUtc != DateTime.MinValue &&
                DateTime.UtcNow - this.teleportRejectedUtc < TeleportRetryInterval)
            {
                this.StatusDetail =
                    $"{NpcLocationService.GetTerritoryName(destination)} へテレポートできるのを待っています";

                // 待っている間もタイムアウトは進める。
                this.GiveUpTeleport(cfg, destination);

                return;
            }

            this.teleportIssued = true;

            if (!this.TryTeleportTo(destination))
            {
                this.teleportRejectedUtc = DateTime.UtcNow;

                // **失敗しても、すぐ次のマップへ送らない。**
                //
                // 戦闘中や詠唱中はテレポが弾かれる。そこで次のマップへ送ると、
                // その行き先でも同じ理由で弾かれ、一覧の端から端まで
                // 一瞬で駆け抜けてしまう（2026-09-26 実測。1 秒のあいだに
                // 67 回マップを送り、たまたま止まった先へ飛んでいた）。
                //
                // 撃ち直せるようにして、猶予のあいだ待つ。
                // 猶予を過ぎたら、下のタイムアウトが次のマップへ送る。
                this.teleportIssued = false;

                if (EzThrottler.Throttle("AutoCollector.FateTeleportWarn", 5000))
                {
                    this.anomalyLog.Warn(
                        "Fate",
                        $"{NpcLocationService.GetTerritoryName(destination)} へテレポートできません。落ち着くまで待ちます");
                }

                // **待ち続けない。**
                //
                // ここで return していたため、下のタイムアウト判定に
                // 一度も届かなかった。拒否され続けると永久に待つ。
                // 猶予を過ぎたら、次のマップへ送る。
                this.GiveUpTeleport(cfg, destination);

                return;
            }

            this.teleportRejectedUtc = DateTime.MinValue;
            this.teleportAcceptedUtc = DateTime.UtcNow;
            this.trace.Decision("テレポを撃った", NpcLocationService.GetTerritoryName(destination));
            return;
        }

        this.StatusDetail = $"{NpcLocationService.GetTerritoryName(destination)} へ移動しています";

        this.GiveUpTeleport(cfg, destination, "テレポートが終わりませんでした");
    }

    /// <summary>
    /// テレポを諦めて次のマップへ送る。時間切れのときだけ動く。
    ///
    /// **番兵に <c>DateTime.MinValue</c> を使わない。**
    ///
    /// 以前は諦めたあと <c>teleportStartedUtc</c> に <c>MinValue</c> を入れていた。
    /// <c>AdvanceZone</c> が「移れる別のマップがありません」で戻ってくると、
    /// 次のフレームも <c>UtcNow - MinValue</c> が時間切れになり、
    /// **毎フレーム テレポを撃ち直し、間引きの無い警告を出し続けた。**
    /// 記録は 200 件の輪なので、数秒で本来見るべき警告が全部押し出される。
    ///
    /// 諦めたら必ず現在時刻へ測り直す。次の判定まで猶予の時間が空く。
    /// </summary>
    private void GiveUpTeleport(Config cfg, uint destination, string reason = "テレポートできないため、次のマップへ移ります")
    {
        if (DateTime.UtcNow - this.teleportStartedUtc <= TeleportTimeout)
        {
            return;
        }

        this.anomalyLog.Warn("Fate", $"{NpcLocationService.GetTerritoryName(destination)} へ{reason}");

        // **測り直す。**MinValue に戻すと次のフレームも時間切れになる。
        this.teleportStartedUtc = DateTime.UtcNow;
        this.teleportRejectedUtc = DateTime.MinValue;

        // 移れたなら AdvanceZone 側が控え直す。
        this.AdvanceZone(cfg);
    }

    private void TickSeek(Config cfg)
    {
        // 移動中なら続ける。
        if (this.Step == FateStep.MovingToFate && this.target is not null)
        {
            this.TickMoving(cfg);
            return;
        }

        var next = this.ChooseDeparture(cfg);

        if (next is null)
        {
            this.TickWaiting(cfg);
            return;
        }

        this.BeginMoveTo(next);
    }

    /// <summary>
    /// 狙える FATE が無い理由を、利用者が対処できる形で書く。
    ///
    /// <b>「湧いていない」と「条件に合わない」は違う。</b>
    /// 前者は待つしかないが、後者は条件をゆるめれば狙える。
    /// 同じ文言だと、設定を見直せばよいことに気づけない。
    /// </summary>
    private string DescribeNoCandidates(Config cfg)
    {
        var all = this.scanner.ListAll();
        var running = all.Count(f => f.IsActive);

        if (running == 0)
        {
            return "FATE が湧くのを待っています";
        }

        // 湧いてはいる。何で外したかを数える。
        var tooFar = 0;
        var tooDone = 0;
        var skipped = 0;

        foreach (var fate in all)
        {
            if (!fate.IsActive)
            {
                continue;
            }

            if (this.blacklist.Contains(fate.Id))
            {
                skipped++;
            }
            else if (fate.Progress >= 100 || fate.Progress > cfg.FateMaxProgressPct)
            {
                tooDone++;
            }
            else if (fate.RemainingSeconds < cfg.FateMinTimeRemainingSec)
            {
                tooFar++;
            }
        }

        var detail = new List<string>();
        if (tooDone > 0) detail.Add($"進みすぎ {tooDone} 件");
        if (tooFar > 0) detail.Add($"残り時間不足 {tooFar} 件");
        if (skipped > 0) detail.Add($"見送り中 {skipped} 件");

        return detail.Count > 0
            ? $"条件に合う FATE がありません（{running} 件のうち {string.Join("・", detail)}）"
            : $"条件に合う FATE がありません（{running} 件を確認）";
    }

    private void TickWaiting(Config cfg)
    {
        if (this.Step != FateStep.Waiting)
        {
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
        }

        // 報酬待ちの間はマップを離れない。
        //
        // **敵に届かない高さで待つ。**
        // 地上で待つと、終わった FATE の敵や野良に絡まれる。
        // ここへ来るのは「他に狙える FATE が無い」ときだけなので
        // （TickSeek が先に次の FATE を選ぶ）、待つ場所は空の上でよい。
        if (this.pendingRewards.Count > 0)
        {
            this.TickHoverForReward();
            return;
        }

        // **「無い」と「条件に合わない」を分けて出す。**
        //
        // 湧いていないのか、湧いているが残り時間や達成度で外したのかで、
        // 利用者がすべきことが変わる。同じ文言では判断できない。
        var reason = this.DescribeNoCandidates(cfg);

        if (!cfg.FateSwapZoneWhenEmpty || this.route.Count <= 1)
        {
            this.StatusDetail = reason;
            return;
        }

        var waited = (int)(DateTime.UtcNow - this.waitingSinceUtc).TotalSeconds;
        var remaining = cfg.FateZoneSwapWaitSeconds - waited;

        if (remaining > 0)
        {
            this.StatusDetail = $"{reason}（{remaining} 秒後に次のマップへ）";
            return;
        }

        this.AdvanceZone(cfg);
    }

    /// <summary>
    /// 納品 FATE の報酬が入るまで、敵に届かない高さで待つ。
    ///
    /// <b>納品 FATE でしか起きない。</b>
    /// 報酬は 100% の時点では入らず、FATE が消えるとき（最大 90 秒後）に入る。
    /// その間マップを離れられないので、どこかで待つしかない。
    ///
    /// <b>地上で待たない。</b>
    /// 終わった FATE の敵や野良に絡まれる。空の上なら絡まれない。
    ///
    /// <b>他に狙える FATE があればそちらへ行く。</b>
    /// ここへ来るのは TickSeek が次の候補を見つけられなかったときだけなので、
    /// この段階では「待つしかない」ことが確定している。
    /// </summary>
    private void TickHoverForReward()
    {
        var waiting = "納品 FATE の報酬を待っています";

        if (!Player.Available)
        {
            this.StatusDetail = waiting;
            return;
        }

        // **ここへ来るのは異常。**
        //
        // 周回するのは風脈を解放済みのマップなので、ふだん飛べないことはない
        // （実測でも 3 キャラ全員、全エリアで PlayerState.CanFly=True）。
        // 解放していないマップを選んだか、読み取りに失敗している。
        // 黙って地上で待つと気づけないので、理由を残す。
        if (!MountService.CanFlyHere)
        {
            if (EzThrottler.Throttle("AutoCollector.HoverCannotFly", 30000))
            {
                this.anomalyLog.Warn(
                    "Fate",
                    $"このエリアで飛べないため、報酬待ちのあいだ地上に留まります" +
                    $"（{MountService.DescribeFlightStatus()}）。風脈を解放してください");
            }

            this.StatusDetail = $"{waiting}（飛べないため地上で待っています）";
            return;
        }

        // 飛んでいる。これでよい。経路は積まない。
        //
        // **高さを保つために何かを送り続けない。**
        // ゲームは飛行中に高度を落とさない。触らないのがいちばん静か。
        if (MountService.IsFlying)
        {
            this.StatusDetail = $"{waiting}（上空で待機中・高さ {Player.Position.Y:F0}）";

            // 上がりきったら合図を止める。積んだままだと上昇し続ける。
            if (this.hoverLiftIssued && this.vnavmesh.TryNumWaypoints(out var left) && left == 0)
            {
                this.hoverLiftIssued = false;
            }

            return;
        }

        // 戦闘中は乗れない。狙いを外して切れるのを待つ。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            this.targets.ReleaseTarget();
            this.targets.StopAutoAttack();
            this.StatusDetail = $"{waiting}（戦闘が切れるのを待っています）";
            return;
        }

        this.mount.ClearDismounting();

        // 乗る。
        if (!MountService.IsMounted)
        {
            if (this.mount.TickPrepareAlways())
            {
                this.StatusDetail = $"{waiting}（上空へ上がる準備をしています）";
            }
            else
            {
                // 乗れない。報酬待ちは長いので、急がずに次の機会を待つ。
                this.StatusDetail = $"{waiting}（{MountService.DescribeMountBlocker()}）";
            }

            return;
        }

        // 乗れた。敵に届かない高さまで上がる。
        if (!this.hoverLiftIssued)
        {
            var up = Player.Position with { Y = Player.Position.Y + HoverHeightMeters };

            if (this.vnavmesh.TryMoveAlong([up], fly: true))
            {
                this.hoverLiftIssued = true;
                this.trace.State("報酬待ちで上がる", $"真上 {up.Y:F0} へ（敵に届かない高さで待ちます）");
            }
        }

        this.StatusDetail = $"{waiting}（上空へ上がっています）";
    }

    /// <summary>
    /// FATE へ向かい始める。
    ///
    /// <b>移動そのものは次のフレーム以降に始める。</b>
    /// 先にマウントへ乗る必要があり、乗るには数フレームかかる。
    /// ここで経路を引いてしまうと、乗る前に走り出してしまう。
    /// </summary>
    private void BeginMoveTo(FateInfo fate)
    {
        this.starter.CancelMovement(this.navigation);
        this.starter.Reset();
        this.target = fate;
        this.moveStartedUtc = DateTime.UtcNow;
        this.moveIssued = false;

        // 詰まり判定をやり直す。前の移動の記録を引き継がない。
        this.blockCheckedUtc = DateTime.MinValue;
        this.blockDetours = 0;
        this.detourTask = null;
        this.escapeAttempts = 0;
        this.watchdogSince = DateTime.MinValue;
        this.RestorePathTolerance();

        // **別の FATE なら、飛べなかった記録は引き継がない。**
        // ある FATE で飛んで入れなかったからといって、
        // 次の FATE でも入れないとは限らない。
        if (this.groundOnlyFate != fate.Id)
        {
            this.groundOnlyFate = null;
        }

        // 前の進入が残っていれば捨てる。
        this.approach.Cancel("別の FATE へ向かいます");

        // **離れた FATE の印は、ここで消さない。**
        //
        // 消していたため、次の FATE へ向かうと決めた瞬間に
        // さっき終えた FATE がまた「参加中」として拾われ、
        // 100% なので離脱、また次を決める、を繰り返していた。
        //
        // 利用者からは、終わった FATE の場所へわざわざ飛んで戻り、
        // 不自然に動いてから次へ向かうように見える（2026-09-25 実測）。
        //
        // 印は、その FATE の円から出たときに消す（TickCore を参照）。

        // 別の FATE へ向かうので、降りる途中だった記録は捨てる。
        // 残すと二度と飛ばなくなる。
        this.mount.ClearDismounting();

        this.trace.Decision(
            "次の FATE へ",
            $"{fate.Name} 進捗{fate.Progress}% {FateTrace.DescribeDistance(fate.Position)}");
        this.SetStep(FateStep.MovingToFate, $"{fate.Name} へ向かっています");
    }

    private void TickMoving(Config cfg)
    {
        var fate = this.target!;

        // 狙っていた FATE が消えた・終わった。
        var live = this.scanner.GetById(fate.Id);
        if (live is null || !live.IsActive || live.Progress >= 100)
        {
            this.approach.Cancel("狙っていた FATE が終わりました");
            this.navigation.Stop();
            this.target = null;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        var range = Math.Max(1f, live.Radius - FateArrivalSlack);

        // **飛んで入る段取りは、専用の状態機械に任せる。**
        //
        // 乗る・離陸・外周の上空へ・斜めに降りる・接地の確認・降車を
        // 段階に分け、段階ごとに経路を引き直す。ここで全部を見ていたため、
        // 経路の終点と監視する点が食い違い、斜めに降りる保証も無かった。
        if (this.approach.Active)
        {
            this.approach.Tick(live);
            this.StatusDetail = this.approach.Detail;

            switch (this.approach.Phase)
            {
                case ApproachPhase.ReadyForCombat:
                    // 地上に立った。戦闘へ渡す。
                    this.trace.Decision("進入できた", $"{live.Name} の地上に降りました");
                    this.EnterFate(cfg, live, alreadyLanded: true);
                    return;

                case ApproachPhase.Failed:
                    // **飛んで入れなかった。地上の経路で向かう。**
                    //
                    // 飛べないエリア、風脈未解放、高度が頭打ち、
                    // 進入できる空間が無い、などがここへ来る。
                    // 飛べなかったことを、飛べたことにしない。
                    this.anomalyLog.Warn(
                        "Fate",
                        $"{live.Name} へ飛んで入れなかったため、地上の経路で向かいます" +
                        $"（{this.approach.FailureReason}）");

                    this.approach.Cancel("地上の経路へ切り替えます");
                    this.groundOnlyFate = live.Id;
                    this.moveIssued = false;
                    this.flyingWhenIssued = false;
                    return;
            }

            return;
        }

        // 進入を始められるか。
        //
        // 近ければ飛ばない。乗り降りのほうが時間を食う。
        // 一度飛んで入れなかった FATE には、また飛ぼうとしない。
        if (Player.Available
            && this.groundOnlyFate != live.Id
            && MountService.CanFlyHere
            && !this.moveIssued
            && Vector2.Distance(
                   new Vector2(Player.Position.X, Player.Position.Z),
                   new Vector2(live.Position.X, live.Position.Z)) > FlyApproachMinMeters)
        {
            if (this.approach.Begin(live))
            {
                this.StatusDetail = $"{live.Name} への進入を組み立てています";
                return;
            }
        }

        // **着いていたら、乗る判断より先に降りる。**
        //
        // 先に乗る判断をすると、着いた直後に降りて、また乗って、を繰り返す。
        //
        // 高さは見ない。飛んでいる最中は真上に数十メートルの差があり、
        // そのまま測ると「まだ遠い」と判断してしまう。
        // 飛んでいるあいだは中心付近まで、歩きなら円に入れば着いたとみなす。
        var arriveWithin = this.flyingWhenIssued ? LandNearCentreMeters : range + FateArrivalSlack;
        var arrived = Player.Available
                   && Vector2.Distance(
                          new Vector2(Player.Position.X, Player.Position.Z),
                          new Vector2(live.Position.X, live.Position.Z)) <= arriveWithin;

        if (!arrived)
        {
            // 先にマウントへ乗る。遠い FATE へ歩いて向かうと、着く前に終わる。
            // 飛べるエリアなら飛び上がるところまで面倒を見る。
            if (this.mount.TickPrepare(live.Position))
            {
                this.StatusDetail = $"{live.Name} へ向かう準備をしています";
                return;
            }

            // **乗る前に地上の経路を引かない。**
            //
            // 乗る動作には 1 秒ほどかかる。その間に経路を引くと地上の経路になり、
            // 乗り終わっても地面を走り続けることになる。
            // まだ乗っておらず、これから乗って飛べるのなら、経路は引かずに待つ。
            //
            // GatherBuddyReborn も既定ではこうしている
            // （AutoGather.Movement.cs の Navigate: 乗ると決めたら
            // MoveWhileMounting が無ければ StopNavigation して抜ける）。
            //
            // ただし待ち続けない。乗れない事情（乗騎が解放されていない、
            // 直前の戦闘で行動が塞がれている等）があるなら、地上で向かう。
            if (!this.moveIssued
                && !MountService.IsMounted
                && MountService.CanFlyHere
                && DateTime.UtcNow - this.moveStartedUtc < MountWaitBeforeGroundPath)
            {
                this.StatusDetail = $"{live.Name} へ飛ぶ準備をしています";
                this.trace.State("経路を待つ", "乗ってから飛ぶ経路を引く");
                return;
            }
        }

        // **飛行が解けたら経路を引き直す。**
        //
        // 地面に触れると飛行が解ける。そのまま走って向かうと遅いので、
        // 乗り直し・飛び直しのあとに経路も引き直す。
        if (this.moveIssued && this.flyingWhenIssued && !MountService.IsFlying)
        {
            this.navigation.Stop();
            this.moveIssued = false;
        }

        // **乗れたら経路を引き直す。**
        //
        // 乗るには 1 秒ほどかかる。その間に地上の経路を引いてしまうと、
        // 乗り終わってもそのまま地面を走り続ける。引き直す口がここに
        // 無かったため、一度地上で引かれたら二度と飛ばなかった
        // （2026-09-25 実測。48.615 に 騎乗=False で地上の経路を引き、
        // 49.232 に乗れたが、最後まで地上のままだった）。
        if (this.moveIssued && !this.flyingWhenIssued && MountService.IsMounted && MountService.CanFlyHere)
        {
            this.trace.Decision("経路を引き直す", "乗れたので飛ぶ経路にする");
            this.navigation.Stop();
            this.moveIssued = false;
        }

        // 乗ったので経路を引く。飛べる状態なら飛ぶ経路になる。
        if (!this.moveIssued)
        {
            // **まだ飛んでいなくても、飛ぶつもりなら fly: true で引く。**
            //
            // vnavmesh は「次の経路点が自分より高い」「騎乗中」
            // 「まだ飛んでいない」「ignoreDeltaY でない」の 4 つが揃ったとき
            // 自分でジャンプを連打して離陸する
            // （FollowPath.cs:144）。ignoreDeltaY は fly の反転なので、
            // 飛んでから fly: true にしたのでは離陸してくれない。
            //
            // 乗っていて、そのエリアで飛べるなら、飛ぶつもりで引く。
            var flying = MountService.IsMounted && MountService.CanFlyHere;

            // **敵が見えているなら、敵を目指す。**
            //
            // FATE の中心は、ただの円の中心であって敵が居る場所とは限らない。
            // 中心の上空まで飛んでから真下へ降りると、降りるあいだ
            // 空に浮いたままになり、不自然に見える。
            //
            // 敵が見えていれば、そこは必ず立てる場所で、しかも降りた先が
            // そのまま戦う場所になる。FATE の敵は FateId で見分けられる。
            var spotted = this.scanner.FindNearestMob(live.Id, Player.Available ? Player.Position : live.Position);
            var aimedAtMob = spotted is not null;

            var ground = spotted is { } mob
                ? mob.Position
                : this.ResolveLandablePoint(live);

            // 目的地を持ち上げる。これが離陸の条件（次の点が自分より高い）
            // を満たすことにもなり、経路が地面を擦るのも防ぐ。
            //
            // 飛べる高さの上限は LiftForFlight が抑える。
            var destination = flying
                ? MountService.LiftForFlight(ground)
                : ground;

            // **経路は最後まで辿らせる。**
            //
            // ここで渡す range は、vnavmesh では DestinationTolerance になる
            // （AsyncMoveRequest.MoveTo → FollowPath）。これが 0 より大きいと、
            // 目的地からその距離まで近づいた時点で<b>残りの経路点を全部捨てる</b>
            // （FollowPath.cs:73）。
            //
            // 以前は飛行時に 15m を渡していた。そのため入り組んだ地形では、
            // せっかく引けた「通路を抜ける経路」の最後の部分が捨てられ、
            // 手前で止まって空に浮いたままになっていた。
            //
            // 狭い通路でも、経路を最後まで辿れば抜けられる。
            // 着いたかどうかはこちらで測っているので、ここでは切り上げない。
            var moveRange = flying ? 0f : range;

            if (!this.navigation.BeginMove(destination, moveRange, flying, out var failure))
            {
                // **経路探索の最中なら、ただ待つ。**
                // これは失敗ではないので、辿り着けなかったと数えない。
                // 数えていたため、向かい始めた直後に 2 回断られただけで
                // その FATE を候補から外していた（2026-09-25 実測）。
                if (this.navigation.Busy)
                {
                    // **待つあいだも時間制限は進める。**
                    //
                    // ここで無条件に return していたため、経路探索が
                    // ずっと受け付けられない状態では、下にある 90 秒の
                    // 判定へ一度も届かなかった。
                    if (DateTime.UtcNow - this.moveStartedUtc > MoveTimeout)
                    {
                        this.anomalyLog.Warn(
                            "Fate",
                            $"{live.Name} へ向かう経路を {MoveTimeout.TotalSeconds:F0} 秒待ちましたが、受け付けられませんでした");

                        this.navigation.Stop();
                        this.MarkStuck(live.Id);
                        this.target = null;
                        return;
                    }

                    this.StatusDetail = $"{live.Name} へ向かう経路を待っています";
                    return;
                }

                this.anomalyLog.Warn("Fate", $"{live.Name} へ移動できませんでした: {failure}");
                this.MarkStuck(live.Id);
                this.target = null;
                return;
            }

            // 何を頼んだかを残す。飛ばないときに、条件のどれが
            // 欠けているのかをここから辿れる。
            this.trace.Decision(
                "経路を引いた",
                $"{live.Name} fly={flying} " +
                $"騎乗={MountService.IsMounted} 飛行={MountService.IsFlying} " +
                $"{MountService.DescribeFlightStatus()} " +
                $"目的地の高さ={destination.Y:F0}（本来{live.Position.Y:F0}） " +
                $"自分の高さ={(Player.Available ? Player.Position.Y : 0):F0} " +
                $"飛べる高さ={(MountService.KnownCeiling is { } c ? $"{c:F0}" : "未確認")} " +
                $"狙い={(aimedAtMob ? "敵" : "中心")}");

            this.moveIssued = true;
            this.flyingWhenIssued = flying;
            this.moveStartedUtc = DateTime.UtcNow;

            // 止めたあとの見張りが「自分の経路か」を見分けるために控える。
            this.lastMoveDestination = destination;
        }

        // **外周では降りない。中心の地上へ斜めに降りる。**
        //
        // 円に入った端で降りると、敵から遠く、高低差にも阻まれて
        // 詰まりやすい（2026-09-26 の報告）。
        //
        // また、中心の真上まで飛んでから垂直に落ちるのも不自然で、
        // 落ちている間は何もできない。
        //
        // そこで、着地したい地点を「持ち上げずに」目的地として渡す。
        // vnavmesh は空中の現在地から地上の目的地へ経路を引くので、
        // 斜めに高度を下げながら近づく形になる。
        var landing = this.flyingWhenIssued && Player.Available
            ? this.ResolveLandingTarget(live)
            : live.Position;

        if (this.flyingWhenIssued && Player.Available)
        {
            var flat = Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(landing.X, landing.Z));

            // 真上に来て、かつ十分下りていたら降りる。
            // 高いうちに降りると、そこから垂直落下になってしまう。
            var height = Player.Position.Y - landing.Y;

            // **下限も見る。**
            //
            // height は「自分 − 着地点」なので、自分が着地点より**下**にいると
            // 負になる。上限しか見ていなかったため、洞窟や下層など
            // 着地点の真下にいるときも「降りた」と判断していた。
            // 階層の違う場所で FATE に入ったことにしてしまう。
            if (flat <= LandOnSpotMeters
                && height <= LandFromHeightMeters
                && height >= -LandBelowMeters)
            {
                this.navigation.Stop();
                this.EnterFate(cfg, live);
                return;
            }
        }

        var status = this.navigation.Tick(
            landing,
            this.flyingWhenIssued ? LandOnSpotMeters : range);

        switch (status)
        {
            case MoveStatus.Arrived:
                this.EnterFate(cfg, live);
                return;

            case MoveStatus.ShortOfTarget:
                // **経路が終わっただけで、着いたことにしない。**
                //
                // 以前は Arrived と同じ扱いにしていた。vnavmesh が
                // 「近づけるところまで行って終わった」状態なので、
                // 円の外でも上空でも、そのまま戦闘へ進んでいた。
                //
                // 円の中に入れているなら進む。入れていないなら届いていない。
                if (Player.Available &&
                    Vector2.Distance(
                        new Vector2(Player.Position.X, Player.Position.Z),
                        new Vector2(live.Position.X, live.Position.Z)) <= live.Radius)
                {
                    this.trace.State(
                        "経路は終わったが円の中",
                        $"{live.Name} 目的地まで届いていないが、円に入れているので戦い始めます");

                    this.EnterFate(cfg, live);
                    return;
                }

                this.anomalyLog.Warn(
                    "Fate",
                    $"{live.Name} は経路の終点まで行っても円に入れませんでした");

                this.navigation.Stop();
                this.MarkStuck(live.Id);
                this.target = null;
                return;

            case MoveStatus.Stuck:
            case MoveStatus.Failed:
                this.anomalyLog.Warn("Fate", $"{live.Name} へ辿り着けませんでした（{status}）");
                this.navigation.Stop();
                this.MarkStuck(live.Id);
                this.target = null;
                return;

            default:
                if (DateTime.UtcNow - this.moveStartedUtc > MoveTimeout)
                {
                    this.anomalyLog.Warn("Fate", $"{live.Name} への移動に時間がかかりすぎました");
                    this.navigation.Stop();
                    this.MarkStuck(live.Id);
                    this.target = null;
                    return;
                }

                // 進まなくなっていないかを見る。壁に押し付けられている場合がある。
                this.CheckBlocked(
                    live,
                    this.flyingWhenIssued ? MountService.LiftForFlight(live.Position) : live.Position);

                this.StatusDetail = $"{live.Name} へ向かっています（{live.Progress}%）";
                this.trace.State(
                    "移動中",
                    $"{live.Name} {live.Progress}% {FateTrace.DescribeDistance(live.Position)} " +
                    $"経路={(this.flyingWhenIssued ? "飛行" : "地上")}");
                return;
        }
    }

    /// <summary>
    /// 進まなくなっていないかを見て、詰まっていれば避けて引き直す。
    ///
    /// <b>経路そのものは地形を貫いていない。</b>
    /// vnavmesh は飛行時に voxel の空間（PathfindVolume）で探すため、
    /// 岩や建物の内側を通る経路は引かない。
    ///
    /// それでも壁に張り付くのは、目的地を 30m 持ち上げているせい。
    /// 持ち上げた先が崖や岩の内側に入ると、そこへ行こうとして
    /// 手前の面に押し付けられる。実際、目的地の高さ 27（本来 -3）で
    /// 1121m まで詰めたあと 1156m まで押し戻されていた（2026-09-25）。
    ///
    /// 進んでいないと分かったら、いまの場所を避ける球として指定し、
    /// 迂回する経路を引き直す。
    /// </summary>
    private void CheckBlocked(FateInfo fate, Vector3 destination)
    {
        if (!Player.Available)
        {
            return;
        }

        // 頼んでおいた迂回の経路が出来ていたら、それを積む。
        if (this.detourTask is { IsCompleted: true } done)
        {
            this.detourTask = null;

            if (done.IsCompletedSuccessfully && done.Result is { Count: > 0 } points)
            {
                this.vnavmesh.TryMoveAlong(points, true);
                this.trace.Decision("迂回の経路を積んだ", $"経路点 {points.Count} 個");

                // 積み直したので、進み具合の基準も取り直す。
                this.blockCheckedUtc = DateTime.UtcNow;
                this.blockCheckPosition = Player.Position;
                return;
            }

            this.trace.Trouble("迂回の経路が引けなかった", $"{fate.Name} へ普通に引き直します");
            this.navigation.Stop();
            this.moveIssued = false;
            return;
        }

        // 頼んだ経路を待っている間は、二重に頼まない。
        if (this.detourTask is not null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var here = Player.Position;

        // **「経路を探している」を理由に見送らない。**
        //
        // vnavmesh は詰まると自分で経路を引き直す
        // （AsyncMoveRequest の OnStuck → MoveTo。設定 RetryOnStuck が既定で有効）。
        // そのため壁に当たっている間こそ「探している」が立ち続ける。
        // これで見送っていたら、詰まりを一度も検出できなかった
        // （2026-09-25 実測。探している 2305 回に対し、検出は 1 回だけ）。
        //
        // 経路がまだ 1 本も無いあいだ（出発前）だけ待つ。
        // 経路を持っているなら、進んでいるかどうかで判断してよい。
        if (this.vnavmesh.TryNumWaypoints(out var waypoints) && waypoints == 0)
        {
            this.blockCheckedUtc = now;
            this.blockCheckPosition = here;
            this.trace.State("経路を待っている", $"{fate.Name} まで {FateTrace.DescribeDistance(destination)}");
            return;
        }

        // 初めて見るときは基準を置くだけ。
        if (this.blockCheckedUtc == DateTime.MinValue)
        {
            this.blockCheckedUtc = now;
            this.blockCheckPosition = here;
            return;
        }

        if (now - this.blockCheckedUtc < BlockCheckInterval)
        {
            return;
        }

        var advanced = Vector3.Distance(here, this.blockCheckPosition);
        this.blockCheckedUtc = now;
        this.blockCheckPosition = here;

        // 進んでいるなら何もしない。
        if (advanced >= BlockProgressMeters)
        {
            this.blockDetours = 0;
            return;
        }

        // 迂回で駄目なら、経路を捨ててその場から力づくで離れる。
        if (this.blockDetours >= MaxDetours)
        {
            this.EscapeStuckSpot(fate, now, here);
            return;
        }

        this.blockDetours++;

        // **まず経路をなぞらせ、引き直させる。**
        //
        // vnavmesh は経路から Tolerance（既定 0.25）まで外れてよい作りで、
        // 曲がり角を端折って進む。狭い通路ではそれが壁への接触になる。
        // 引っかかったら、まず許容値を詰めて経路をなぞらせる。
        // 通路そのものは通れるので、なぞれば抜けられることが多い。
        //
        // あわせて経路を捨てる。次のフレームで、いまの場所から引き直される。
        // 壁に寄った状態から引き直すと、別の抜け道が見つかることが多い。
        // ICE も詰まったときは「経路を止めて引き直させる」だけで抜けている
        // （逆コンパイルして確認。CheckIfIsStuck の RetargetIfStuck）。
        if (this.blockDetours == 1)
        {
            if (!this.tightenedPath)
            {
                this.tightenedPath = true;
                this.vnavmesh.TrySetPathTolerance(TightPathTolerance);
            }

            this.trace.Decision(
                "経路をなぞらせて引き直す",
                $"許容値 {TightPathTolerance:0.00}／{advanced:F1}m しか進めず");

            this.navigation.Stop();
            this.moveIssued = false;

            // 跳ねてみる。段差に引っかかっているだけなら、これで外れる。
            // 乗っているときは跳ばない。跳躍中は降車が弾かれ、
            // 降りようとしては跳ぶ、を繰り返すことになる（2026-09-26 実測）。
            if (!MountService.IsMounted)
            {
                TryJump();
            }

            this.blockCheckedUtc = now;
            this.blockCheckPosition = here;
            return;
        }

        // いる場所を中心に、その周りを避けて引き直す。
        // 半径は詰まりを抜け出せる程度に取る。大きすぎると経路が見つからない。
        var radius = DetourRadiusMeters * this.blockDetours;

        this.trace.Decision(
            "遮蔽物を避けて引き直す",
            $"{fate.Name} {BlockCheckInterval.TotalSeconds:F0}秒で {advanced:F1}m しか進めず " +
            $"（{this.blockDetours} 回目・半径 {radius:F0}m）");

        // **避ける球は、進もうとしている先に置く。**
        //
        // いる場所を中心にすると、そこから出る経路まで避けてしまい、
        // 「どこへも行けない」経路しか引けないことがある。
        // ぶつかっているのは進行方向の先なので、そちらに置く。
        var forward = Vector3.Normalize(new Vector3(
            destination.X - here.X,
            0f,
            destination.Z - here.Z));

        var avoidAt = float.IsNaN(forward.X)
            ? here
            : here + (forward * radius);

        // **経路探索は非同期。**その場では結果が出ない。
        // 頼んでおいて、出来たら積む。出来るまでは今の経路のまま進む。
        if (this.vnavmesh.TryPathfindAvoid(here, destination, true, avoidAt, radius, out var task) &&
            task is not null)
        {
            this.detourTask = task;
            this.trace.Decision("迂回の経路を頼んだ", $"半径 {radius:F0}m");
            return;
        }

        // 頼めなかった。いったん止めて普通に引き直させる。
        this.trace.Trouble("迂回の経路が頼めない", $"{fate.Name} へ普通に引き直します");
        this.navigation.Stop();
        this.moveIssued = false;
    }

    /// <summary>
    /// 詰まった場所から力づくで離れる。
    ///
    /// <b>経路探索に頼らない。</b>
    /// 地形の内側や狭い隙間に入り込むと、そこを起点にした経路は
    /// どう引いても出口が無く、迂回もメッシュの引き直しも効かない。
    ///
    /// そこへ連れてきたのはこちらなので、こちらで出す。
    /// vnavmesh の Path.MoveTo は経路探索を通さず、渡した点へそのまま向かう。
    /// これで真上や斜め上へ動かし、詰まりから抜け出す。
    ///
    /// GatherBuddyReborn も同じ考え方で、詰まったときは
    /// ナビメッシュを介さず直接movementを奪って 25m 先へ動かしている
    /// （AutoGather/Helpers/AdvancedUnstuck.cs の Start）。
    /// </summary>
    private void EscapeStuckSpot(FateInfo? fate, DateTime now, Vector3 here)
    {
        this.escapeAttempts++;

        // **進入の段取りを先に畳む。**
        //
        // 畳まないと、これから積む脱出の経路を進入側が
        // 「自分の経路がまだ走っている」と読んでしまう
        // （RunPath は Path.NumWaypoints が 0 より大きければ見守るだけ）。
        // 脱出点へ向かうあいだ、段階は FlyToEntry や Descending のまま
        // 何もせず止まる。
        //
        // 「進入中に vnavmesh を触るのは進入側だけ」という約束を、
        // ここで破ってしまっていた。
        if (this.approach.Active)
        {
            this.approach.Cancel("詰まったので脱出します");

            // 飛んで入り直すのはやめる。詰まった地形でもう一度
            // 同じ経路を引いても、同じ場所で詰まる。
            if (fate is not null)
            {
                this.groundOnlyFate = fate.Id;
            }
        }

        // **最後は帰還する。**
        //
        // 動かして抜けられないなら、座標ごと外へ出すしかない。
        // 帰還（デジョン＝ActionType.Action の 6 番）は経路も地形も高度も
        // 関係なく、どこに居てもホームポイントへ運んでくれる。
        // 入り組んだ地形に入り込んでしまったときの、確実な逃げ道。
        //
        // ただし詠唱があり、戦闘中やリキャスト中は撃てない。
        // 撃てたかどうかは ReturnHome の戻り値で見る。
        //
        // 手で助けてもらうことはしない。連れてきたのはこちらなので、
        // こちらで出す。
        if (this.escapeAttempts > MaxEscapeAttempts)
        {
            this.trace.Trouble(
                "動いて抜け出せない",
                $"{MaxEscapeAttempts} 回試しても動けないため、帰還して脱出します");

            this.navigation.Stop();
            this.vnavmesh.TryStop();
            this.moveIssued = false;
            if (fate is not null)
            {
                this.MarkStuck(fate.Id);
            }

            this.target = null;

            // 帰還は詠唱がある。終わるまで周回を進めない。
            // **行き先を「いまのマップ以外」にしておく。**
            //
            // 0 にすると TickCore の heading が false になり、
            // まだ FATE のマップに居るので「移動は要らない」と畳まれる。
            // その直後に次の FATE を選んで経路を積むため、
            // 帰還の詠唱を自分で中断していた。
            //
            // 帰るのは街なので、いまのマップとは必ず違う。
            // 巡回の先頭を仮の行き先にしておけば、着くまで移動を続ける扱いになり、
            // 詠唱が終わるまで誰も経路を積まない。
            this.SetStep(FateStep.Traveling, "詰まったため帰還しています");
            this.travelTargetTerritory = this.route.Count > 0
                ? this.route[0]
                : Svc.ClientState.TerritoryType;
            this.teleportIssued = true;
            this.teleportStartedUtc = now;
            this.escapeAttempts = 0;

            // **撃てたかどうかを、そのまま書く。**
            //
            // 以前は結果を見ずに「帰還しました」と書いていた。
            // そのため、実際には何も起きていないのに記録だけが残り、
            // 効いていないことに気づけなかった（2026-09-26 実測）。
            if (ReturnHome())
            {
                this.anomalyLog.Warn(
                    "Fate",
                    "地形から抜け出せなかったため帰還します。周回は続けます");
            }
            else
            {
                this.anomalyLog.Warn(
                    "Fate",
                    "地形から抜け出せず、帰還も撃てませんでした（戦闘中・リキャスト中など）。" +
                    "次の機会に撃ち直します");

                // 撃てなかったので、詰まりの回数は戻す。次のフレームでまた試す。
                this.escapeAttempts = MaxEscapeAttempts;
            }

            return;
        }

        // 積んである経路を捨てる。残っていると、また同じ壁へ押し付けられる。
        this.navigation.Stop();
        this.moveIssued = false;

        // **逃げ先はメッシュに聞く。**
        //
        // やみくもに動かすと、また壁の中へ突っ込むことがある。
        // ナビメッシュに載っている点は、そこに立てることが保証されている。
        // まず真下の床、次に周りの立てる場所を探し、見つかればそこへ。
        //
        // 上は当てにしない。飛行には高度の上限があり、そこに張り付いていると
        // 上へ向かわせても 1m も上がらない（2026-09-25 実測。Y=58 で頭打ち。
        // ゲームが「高度上限付近です」と出していた）。
        Vector3? found = null;

        if (this.vnavmesh.TryIsReady(out var ready) && ready)
        {
            // 真下の床。入り組んだ地形でも、下は空いていることが多い。
            if (this.vnavmesh.TryPointOnFloor(here, out var floor) && floor is not null)
            {
                found = floor;
            }
            else
            {
                // 探す範囲を、試行のたびに広げる。
                var reach = 30f * this.escapeAttempts;

                if (this.vnavmesh.TryNearestPointReachable(here, reach, 200f, out var near) && near is not null)
                {
                    found = near;
                }
                else if (this.vnavmesh.TryNearestPoint(here, reach * 2f, 200f, out var any) && any is not null)
                {
                    found = any;
                }
            }
        }

        // メッシュから答えが出なければ、下と横へ振る。
        // 向きは黄金角でずらし、毎回ちがう方向へ出る。
        var angle = this.escapeAttempts * 2.39996f;
        var spread = 15f * this.escapeAttempts;

        var away = found ?? new Vector3(
            here.X + (MathF.Cos(angle) * spread),
            here.Y - (15f * this.escapeAttempts),
            here.Z + (MathF.Sin(angle) * spread));

        this.trace.Trouble(
            "詰まった場所から離れる",
            $"{fate?.Name ?? "移動"} ({here.X:F0},{here.Y:F0},{here.Z:F0}) → " +
            $"({away.X:F0},{away.Y:F0},{away.Z:F0}) {this.escapeAttempts} 回目 " +
            $"{(found is null ? "（当て推量）" : "（メッシュ上の点）")}");

        // 経路探索を通さずに、その点へ直接向かわせる。
        this.vnavmesh.TryMoveAlong([away], true);

        // 動く時間を与えてから測り直す。
        this.blockCheckedUtc = now + EscapeSettleTime;
        this.blockCheckPosition = here;
        this.blockDetours = 0;
        this.detourTask = null;
    }

    /// <summary>
    /// FATE に着いた。降りる段階へ移る。
    ///
    /// <b>ここで経路を止め、以後 vnavmesh に触らない。</b>
    /// 降下はゲーム側の動きで、それがプレイヤーの操作として
    /// vnavmesh に読まれる。触り続けると経路を積んでは捨てるを
    /// 繰り返し、着地したあとも暴れて降りられない。
    /// </summary>
    /// <param name="alreadyLanded">
    /// 進入の段階で、地上に立ったことまで確認できているか。
    /// true なら降りる段階を通さず、そのまま戦闘へ入る。
    /// </param>
    private void EnterFate(Config cfg, FateInfo fate, bool alreadyLanded = false)
    {
        this.navigation.Stop();
        this.moveIssued = false;
        this.target = fate;

        // 詰めていた経路追従の許容値を戻す。
        // この設定は vnavmesh 全体のものなので、詰めたままにすると
        // 他のプラグインの移動にも効いてしまう。
        this.RestorePathTolerance();

        // 乗っていなければ降りる必要がない。そのまま戦う。
        if (alreadyLanded || !MountService.IsMounted)
        {
            this.ApplyCombat(cfg);
            this.fightingSinceUtc = DateTime.UtcNow;
            this.SetStep(FateStep.Fighting, $"{fate.Name} と戦っています");
            return;
        }

        this.landingSinceUtc = DateTime.UtcNow;
        this.landingRefuge = null;
        this.SetStep(FateStep.Landing, $"{fate.Name} に着きました（降りています）");
    }

    /// <summary>
    /// 降りきるのを待つ。
    ///
    /// <b>vnavmesh を呼ばない。</b>降下中に経路を積むと、ゲームの降下が
    /// プレイヤーの操作として読まれ、積んだ経路がその場で捨てられる。
    /// これを繰り返すのが「暴れる」正体だった（2026-09-25 実測）。
    /// </summary>
    private void TickLanding(Config cfg)
    {
        if (this.target is not { } fate)
        {
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        // 降りられた。戦いに入る。
        if (!MountService.IsMounted)
        {
            this.mount.ClearDismounting();
            this.ApplyCombat(cfg);
            this.fightingSinceUtc = DateTime.UtcNow;
            this.SetStep(FateStep.Fighting, $"{fate.Name} と戦っています");
            return;
        }

        // **戦闘が始まったら、降りるのを待たない。**
        //
        // 敵に絡まれると降車が弾かれることがある。そのまま待ち続けると、
        // 乗ったまま殴られるだけで何もしない
        // （2026-09-26。FATE 範囲に入ったのに降りず、戦わない報告）。
        //
        // 戦闘を始めてしまえば BMR が動き、降りるのも BMR 側で面倒を見る。
        if (Svc.Condition[ConditionFlag.InCombat] && !MountService.IsFlying)
        {
            this.trace.Decision("降りるのを待たない", "戦闘が始まったので先に戦う");
            this.mount.ClearDismounting();
            this.ApplyCombat(cfg);
            this.fightingSinceUtc = DateTime.UtcNow;
            this.SetStep(FateStep.Fighting, $"{fate.Name} と戦っています");
            return;
        }

        // 降りられないまま時間が過ぎた。
        if (DateTime.UtcNow - this.landingSinceUtc > LandingTimeout)
        {
            this.trace.Trouble("着地できない", $"{LandingTimeout.TotalSeconds:F0}秒たっても降りられませんでした");
            this.mount.ClearDismounting();

            // **その場に浮いたまま放り出さない。**
            //
            // 水面の上など、降りられない場所で諦めると、空中に止まったまま
            // 次の判断へ進むことになる。そこから次の FATE を目指しても、
            // 出発点が宙に浮いたままなので経路が引けないことがある。
            //
            // 立てる場所を聞いて、そこまで飛んでから降りる。
            // 立てる場所を探す。空にいるので、まず真下の床を見る。
            // 見つからなければ、周りを広く探す。
            Vector3? refuge = null;

            if (Player.Available && this.vnavmesh.TryIsReady(out var meshReady) && meshReady)
            {
                if (this.vnavmesh.TryPointOnFloor(Player.Position, out var below) && below is not null)
                {
                    refuge = below;
                }
                else if (this.vnavmesh.TryNearestPointReachable(Player.Position, 50f, 200f, out var near) && near is not null)
                {
                    refuge = near;
                }
                else if (this.vnavmesh.TryNearestPoint(Player.Position, 100f, 200f, out var any) && any is not null)
                {
                    refuge = any;
                }
            }

            if (refuge is { } spot)
            {
                this.trace.Decision(
                    "降りられる所まで移る",
                    $"({Player.Position.X:F0},{Player.Position.Y:F0},{Player.Position.Z:F0}) → " +
                    $"({spot.X:F0},{spot.Y:F0},{spot.Z:F0})");

                // **経路探索を通さずに向かう。**
                //
                // 降りられない場所は、たいてい経路も引けない場所でもある。
                // BeginMove で頼むと経路が見つからず、その場に浮いたままになる。
                // Path.MoveTo は渡した点へそのまま向かうので、ここを抜けられる。
                //
                // 途中で引っかからないよう、いったん上へ出てから向かう。
                var overhead = Player.Position with { Y = Player.Position.Y + 20f };
                this.vnavmesh.TryMoveAlong([overhead, MountService.LiftForFlight(spot), spot], true);

                this.landingSinceUtc = DateTime.UtcNow;
                this.landingRefuge = spot;
                return;
            }

            // 立てる場所すら分からない。力づくで上へ出てから考え直す。
            if (Player.Available)
            {
                var up = Player.Position with { Y = Player.Position.Y + 30f };
                this.trace.Trouble("降りられる所が無い", $"いったん上へ出ます ({up.Y:F0})");
                this.vnavmesh.TryMoveAlong([up], true);
            }

            this.MarkStuck(fate.Id);
            this.target = null;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        // 立てる場所へ移っている最中は、着くまで降りようとしない。
        // 降下と移動が競合して、また同じ所で止まる。
        if (this.landingRefuge is { } moving)
        {
            var flat = Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(moving.X, moving.Z));

            if (flat > 5f)
            {
                this.trace.State("移動中（着地先）", $"あと {flat:F0}m");
                return;
            }

            this.navigation.Stop();
            this.landingRefuge = null;
        }

        this.mount.TickDismount();
        this.trace.State("着地", $"{fate.Name} {FateTrace.DescribeDistance(fate.Position)}");
    }

    private void TickInFate(Config cfg, FateInfo current)
    {
        // 達成度 100%。ここが最優先。待たずに離れる。
        if (current.Progress >= 100)
        {
            this.LeaveFate(cfg, current);
            return;
        }

        // **乗ったままでは戦えない。**
        //
        // FATE の円へ入った時点でここへ来るので、移動の段階を通らずに
        // 戦闘へ入ることがある。降りる処理は移動の側に置いてあったため、
        // 乗ったまま戦おうとして何もできなかった（2026-09-25 実測）。
        //
        // 降りるまでは戦闘を始めない。Landing はこの下にある
        // 「経路を触らない」段階へ移す。
        if (MountService.IsMounted)
        {
            this.target = current;

            // **飛んでいる最中は、中心の近くまで進んでから降りる。**
            //
            // 円に入った端で降りると、そこが低い場所だと高所の敵へ
            // 近づけない。移動の段階に戻して、中心まで飛ばせる。
            if (MountService.IsFlying && Player.Available)
            {
                var landing = this.ResolveLandingTarget(current);

                var flat = Vector2.Distance(
                    new Vector2(Player.Position.X, Player.Position.Z),
                    new Vector2(landing.X, landing.Z));

                var height = Player.Position.Y - landing.Y;

                // 真上に来て、下りきるまでは移動を続ける。
                // ここで降りると、残った高さぶん垂直に落ちることになる。
                // **戻る側は緩くする。**
                // 降りる判定と同じ値で戻すと、敵が少し動いただけで
                // Landing と MovingToFate を往復し、いつまでも降りられない。
                if (flat > LandOnSpotMeters + LandHysteresisMeters
                    || height > LandFromHeightMeters + LandHysteresisMeters)
                {
                    if (this.Step != FateStep.MovingToFate)
                    {
                        this.SetStep(FateStep.MovingToFate, $"{current.Name} の中心へ向かっています");
                    }

                    this.TickMoving(cfg);
                    return;
                }
            }

            // **「敵の上空へ飛ぶ」はやめた。**
            //
            // 敵の真上へ持ち上げた点を目指していたため、
            // 敵の頭上まで飛んでから垂直に落ちる形になっていた。
            // いまは上の判定が、持ち上げていない着地点（敵か中心の地上）を
            // そのまま目指す。空中から地上へ経路が引かれるので、
            // 斜めに高度を下げながら近づく。

            if (this.Step != FateStep.Landing)
            {
                this.landingSinceUtc = DateTime.UtcNow;
                this.landingRefuge = null;
                this.navigation.Stop();
                this.SetStep(FateStep.Landing, $"{current.Name} に入りました（降りています）");
            }

            return;
        }

        if (this.Step != FateStep.Fighting)
        {
            this.ApplyCombat(cfg);
            this.target = current;
            this.fightingSinceUtc = DateTime.UtcNow;
            this.SetStep(FateStep.Fighting, $"{current.Name} と戦っています");
        }

        this.EnsurePresetActive(cfg);

        // 達成度が閾値を超えたら、次に向かう FATE を先に決めておく。
        // 100% を見てから探し始めると、その間その場に立ち尽くすことになる。
        if (this.prefetched is null && current.Progress >= cfg.FatePrefetchPct)
        {
            this.prefetched = this.PickNext(cfg, exclude: current.Id);
        }

        // **狙う相手は、こちらで決める。**
        //
        // BMR の設定だけでは「FATE 以外に自分から絡まない」を表現できない。
        // AIHintsBuilder.cs:148 が敵視のある敵を FateId に関係なく優先度 0 にし、
        // AutoTarget.cs:224 がそれを候補に入れる。Everything=Disabled は
        // 「新しく引っ張らない」だけなので、一度絡まれた FATE 外の敵は残る。
        //
        // そこでハードターゲットをこちらで置き、BMR には
        // 「いま狙っている相手と戦う」ことだけをさせる。
        if (!Svc.Condition[ConditionFlag.InCombat] &&
            this.scanner.FindNearestMob(current.Id, Player.Position) is null &&
            this.starter.Tick(current, this.navigation))
        {
            this.startingFate = true;
            this.ParkPresetMovement();
            this.StatusDetail = this.starter.Detail;
            return;
        }
        if (this.startingFate)
        {
            this.startingFate = false;
            this.starter.CancelMovement(this.navigation);
            this.ResumePresetMovement();
        }
        if (current.State == FateState.Preparing && this.starter.TimedOut)
        {
            this.blacklist.Add(current.Id);
            this.blacklistUntil[current.Id] = DateTime.UtcNow + BlacklistDuration;
            this.anomalyLog.Warn("Fate", $"{current.Name} の開始を確認できないため、一時的に見送ります");
            this.starter.Reset();
            this.LeaveFate(cfg, current, FateOutcome.Failed);
            return;
        }
        this.TickAcquireTarget(current);

        // **納品の段階に入っていれば、敵へ近づかない。**
        //
        // 10 個たまると BMR の FateUtils が納品 NPC を狙って移動を強制する。
        // そこへこちらから敵への経路を積むと引っ張り合いになる。
        if (this.TickHandIn(cfg, current))
        {
            return;
        }

        // **敵が遠ければ歩いて近づく。**
        //
        // BMR は見えている敵と戦うだけで、遠くの敵を探しには行かない。
        // 円の端に降りると、敵が遠くて棒立ちになる（2026-09-25 実測）。
        this.TickApproachMob(current);

        this.StatusDetail = $"{current.Name}（{current.Progress}%）";
        this.trace.State(
            "戦闘中",
            $"{current.Name} {current.Progress}% " +
            $"シンク={(this.scanner.IsPlayerSyncedToFate() ? "済" : "未")} " +
            $"プリセット={(this.bossMod.TryGetActivePreset(out var nowActive) ? nowActive ?? "なし" : "読めず")}");
    }

    /// <summary>
    /// ベンチャーの回収を進める。
    ///
    /// <b>回収へ行くと決めたら、周回の処理は止める。</b>
    /// 両方が動くと、こちらは街へ、周回は FATE へ向かって経路を取り合う。
    ///
    /// <b>戻る先を控えておく。</b>
    /// 回収が済んだら、離れたマップへ戻って周回を続ける。
    /// 交換のための中断（FateEarner）と同じ考え方。
    /// </summary>
    /// <returns>回収の処理を進めたら true。呼び出し側は周回の処理へ進まない。</returns>
    private bool TickVentures(Config cfg)
    {
        if (!ventures.Active)
        {
            return false;
        }

        // 回収が済んだ。離れたマップへ戻る。
        if (ventures.ReadyToResume)
        {
            ventures.ResumeWatching();

            if (this.ventureReturnTo is { } back && back != Svc.ClientState.TerritoryType)
            {
                this.anomalyLog.Info(
                    "Venture",
                    $"回収が済んだので {NpcLocationService.GetTerritoryName(back)} へ戻って周回を続けます");

                // 戻り先を行き先に立てる。テレポは TickTraveling が撃つ。
                var index = this.route.IndexOf(back);
                if (index >= 0)
                {
                    this.zoneIndex = index;
                }

                this.travelTargetTerritory = back;
                this.teleportStartedUtc = DateTime.UtcNow;
                this.teleportIssued = false;
                this.teleportRejectedUtc = DateTime.MinValue;
                this.SetStep(FateStep.Traveling, $"{NpcLocationService.GetTerritoryName(back)} へ戻っています");
            }
            else
            {
                this.SetStep(FateStep.Waiting, "FATE を探しています");
                this.waitingSinceUtc = DateTime.UtcNow;
            }

            this.ventureReturnTo = null;
            return true;
        }

        // **いま抜けてよいか。**
        //
        // 戦闘中と納品中は抜けない。抜けると参加していた FATE の報酬を落とす。
        // 「動くはずの段階」で足が止まっているだけなら抜けてよい。
        var atBreak = !Svc.Condition[ConditionFlag.InCombat]
                   && !this.handingIn
                   && this.Step is not (FateStep.Landing or FateStep.Dead or FateStep.Traveling);

        ventures.Tick(cfg, atBreak);

        if (!ventures.Interrupting)
        {
            // 見張っているだけ。周回はふつうに続ける。
            return false;
        }

        // ここから回収へ向かう。周回は畳む。
        if (this.ventureReturnTo is null)
        {
            // **戻る先を控える。** いま周回しているマップ。
            this.ventureReturnTo = Svc.ClientState.TerritoryType;

            this.anomalyLog.Info(
                "Venture",
                $"ベンチャー回収のため周回を中断します。" +
                $"戻る先として {NpcLocationService.GetTerritoryName(this.ventureReturnTo.Value)} を控えました");

            // 戦闘とターゲットを畳む。畳まないとマウントに乗れず、街へ行けない。
            this.ReleaseCombat();
            this.approach.Cancel("ベンチャー回収へ向かいます");
            this.navigation.Stop();
            this.target = null;
            this.prefetched = null;
            this.moveIssued = false;

            // **段階を移す。**
            //
            // MovingToFate のままにしていたため、画面には
            // 「FATE へ向かっています」と出たまま街へ向かっていた。
            // 見張りも「FATE へ移動中なのに動かない」と読んで引っ張り、
            // デジョンの詠唱を中断させていた（2026-09-26 実測）。
            this.SetStep(FateStep.Traveling, "ベンチャー回収のため街へ向かっています");
        }

        this.StatusDetail = ventures.Detail;
        return true;
    }

    /// <summary>
    /// 狙う相手を決めて、ハードターゲットに置く。
    ///
    /// <b>自分から仕掛けるのは FATE の敵だけ。絡まれたら反撃する。</b>
    /// 判定は <see cref="FateTargetService"/> が持つ。
    /// 「絡まれたか」はゲームの敵視リスト（<c>UIState.Hater</c>）で見る。
    /// BMR も同じものを読んでいる（WorldStateGameSync.cs:258-265）。
    ///
    /// <b>納品中は呼ばない。</b>納品へ向かう最中に敵を狙うと、
    /// BMR がそちらへ走って納品に行かない。
    /// </summary>
    private void TickAcquireTarget(FateInfo current)
    {
        if (this.handingIn)
        {
            return;
        }

        var picked = this.targets.AcquireTarget(current.Id, current.Position, current.Radius);

        if (picked is null)
        {
            // 狙う相手が居ない。残っているターゲットを外しておく。
            // 外さないと、円の外の敵を狙ったままオートアタックが続く。
            this.targets.ReleaseTarget();
            return;
        }

        if (this.lastTargetId == picked.GameObjectId)
        {
            return;
        }

        this.lastTargetId = picked.GameObjectId;

        this.trace.Decision(
            "狙う相手を決めた",
            $"{picked.Name} ({picked.GameObjectId:X}) " +
            $"{Vector3.Distance(Player.Position, picked.Position):F0}m " +
            $"敵視={this.targets.CountAggroOnMe()}件");
    }

    /// <summary>
    /// 納品の最中に絡まれた敵を狙う。
    ///
    /// 納品へ向かう間は新しい敵を狙わないが、絡まれたら振り払う。
    /// ダイアログは戦闘中に開かないので、片付けないと納品できない。
    /// </summary>
    private void TickAcquireTargetDuringHandIn(FateInfo current)
    {
        var picked = this.targets.AcquireTarget(current.Id, current.Position, current.Radius);

        if (picked is null)
        {
            this.targets.ReleaseTarget();
            return;
        }

        if (this.lastTargetId == picked.GameObjectId)
        {
            return;
        }

        this.lastTargetId = picked.GameObjectId;
        this.trace.State("納品前に振り払う相手", $"{picked.Name} ({picked.GameObjectId:X})");
    }

    /// <summary>
    /// 納品へ向かう間、新しい敵を狙わせない。
    ///
    /// <b>プリセットを差し替えず、一時方針で上書きする。</b>
    /// 差し替えると技のローテーションごと入れ替わり、戻すときに取りこぼす。
    /// 一時方針なら戦闘力はそのままで、狙う・動くだけを止められる。
    /// AutoFATEGrind も同じ作法（AutoFate.Collect.cs:159-161）。
    /// </summary>
    private void ParkTargetingForHandIn()
    {
        if (this.handInParked || !this.presetApplied)
        {
            return;
        }

        // 新しい敵を狙わない。Passive は Execute の冒頭で戻るだけなので、
        // すでに狙っている相手への攻撃は続く（AutoTarget.cs:110-111）。
        if (this.bossMod.TryAddTransientStrategy(
                this.appliedPresetName, ModuleAutoTarget, TrackGeneral, OptionPassive, out var ok) && ok)
        {
            this.handInParked = true;
            return;
        }

        this.anomalyLog.Warn("Fate", "納品へ向かう間の的の抑止を設定できませんでした");
    }

    /// <summary>納品の抑止を戻す。</summary>
    private void ResumeTargetingAfterHandIn()
    {
        if (!this.handInParked)
        {
            return;
        }

        this.handInParked = false;

        if (!this.presetApplied)
        {
            return;
        }

        if (!this.bossMod.TryClearTransientStrategy(
                this.appliedPresetName, ModuleAutoTarget, TrackGeneral, out var ok) || !ok)
        {
            this.anomalyLog.Warn("Fate", "納品の抑止を戻せませんでした（AutoTarget.General）");
        }
    }

    /// <summary>
    /// 納品 FATE の納品を進める。
    ///
    /// <b>討伐と納品を切り替える。</b>
    /// 0〜9 個のあいだは討伐。10 個たまったら納品へ移り、
    /// 納品が成立したら討伐へ戻る。
    ///
    /// 納品そのものは BMR の FateUtils に任せる。10 個持っていれば
    /// 納品 NPC を <c>Hints.InteractWithTarget</c> に立て、必要なら
    /// 移動も強制する（FateUtils.cs:47-55）。主ターゲットは要らない。
    ///
    /// <b>こちらの役目は「邪魔をしないこと」と「成立を見届けること」。</b>
    /// 納品のあいだ敵への経路を積まない。そして
    /// 個数が減ったことを見て、初めて成立とみなす。
    /// </summary>
    /// <returns>納品の段階を進めたら true。呼び出し側は戦闘の処理へ進まない。</returns>
    private bool TickHandIn(Config cfg, FateInfo current)
    {
        if (!current.IsCollect)
        {
            return false;
        }

        var held = this.scanner.CountHandInItems(current.Id);

        // **読めなかったら、何も決めない。**
        //
        // 0 として扱うと「まだ集まっていない」と誤り、納品の途中で
        // 討伐へ戻って会話を中断する。読めないことは持っていないことと違う。
        if (held is not { } count)
        {
            if (this.handingIn)
            {
                this.StatusDetail = $"{current.Name} 納品中（所持数を読めません）";
                return true;
            }

            return false;
        }

        // 納品の段階に入っていない。10 個たまったら入る。
        if (!this.handingIn)
        {
            if (count < HandInRequired)
            {
                return false;
            }

            this.handingIn = true;
            this.handInSinceUtc = DateTime.UtcNow;
            this.handInHeldAtStart = count;
            this.handInAttempts = 0;

            // **敵への経路を畳む。**
            // 残すと、FateUtils の移動強制と引っ張り合いになる。
            this.StopApproach();

            // **納品へ向かう間は、新しい敵を狙わない。**
            //
            // AutoTarget.General を Passive にすると、AutoTarget は
            // Execute の冒頭で戻り、優先度を一切触らなくなる
            // （AutoTarget.cs:110-111）。技は撃てるので、
            // 絡まれたときの反撃はできる。
            //
            // AutoFATEGrind も納品へ歩く間は同じ上書きをしている
            // （AutoFate.Collect.cs:160）。
            this.ParkTargetingForHandIn();

            // 狙っていた敵も外す。残すと BMR がそちらへ走る。
            this.targets.ReleaseTarget();
            this.lastTargetId = 0;

            this.anomalyLog.Info(
                "Fate",
                $"{current.Name} で納品の品が {count} 個たまりました。納品へ向かいます");

            this.trace.Decision("納品へ移る", $"{current.Name} 所持 {count} 個");

            this.StatusDetail = $"{current.Name} 納品へ向かっています（{count} 個）";
            return true;
        }

        // 納品の段階にいる。

        // **交戦中は納品できない。**
        //
        // 納品 NPC のダイアログは戦闘中に開かない。AutoFATEGrind が
        // 実測で確かめている（AutoFate.Collect.cs:85「whose dialog refuses
        // to open in combat」、同 88 行で InCombat を弾いている）。
        //
        // 追ってきた敵を倒してからでないと納品できないので、
        // 一時的に狙う許可を戻して戦わせる。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            if (this.handInParked)
            {
                this.trace.State("納品の前に振り払う", $"{current.Name} 交戦中なので、先に戦います");
                this.ResumeTargetingAfterHandIn();
            }

            // 絡んできた敵を狙う。納品へ向かう前に片付ける。
            this.TickAcquireTargetDuringHandIn(current);

            this.StatusDetail = $"{current.Name} 納品の前に交戦を解いています（{count} 個）";
            return true;
        }

        // 戦闘が切れた。狙う許可を畳んで納品へ戻る。
        if (!this.handInParked)
        {
            this.ParkTargetingForHandIn();
            this.targets.ReleaseTarget();
            this.lastTargetId = 0;
        }

        // **減ったら成立。** 進捗の上昇だけでは判断しない。他人の納品かもしれない。
        if (count < this.handInHeldAtStart)
        {
            this.anomalyLog.Info(
                "Fate",
                $"{current.Name} へ納品できました（{this.handInHeldAtStart} → {count} 個／進捗 {current.Progress}%）");

            this.trace.Decision("納品できた", $"{current.Name} {this.handInHeldAtStart} → {count} 個");

            this.handingIn = false;

            // **的の抑止を戻す。** 戻さないと討伐へ帰っても敵を狙わない。
            this.ResumeTargetingAfterHandIn();

            // まだ 10 個あるなら、続けて納品する。
            // 足りなければ討伐へ戻る。次のフレームで判断させる。
            return true;
        }

        // 減っていない。まだ向かっている、あるいは会話の途中。
        if (DateTime.UtcNow - this.handInSinceUtc <= HandInPatience)
        {
            var npc = this.bossMod.TryGetInteractTarget(out var target) && target != 0
                ? "NPC を狙っています"
                : "NPC を探しています";

            this.StatusDetail = $"{current.Name} 納品中（{count} 個・{npc}）";

            this.trace.State(
                "納品中",
                $"{current.Name} 所持 {count} 個 進捗 {current.Progress}% " +
                $"BMRの対象={(target == 0 ? "なし" : target.ToString())} " +
                $"{(DateTime.UtcNow - this.handInSinceUtc).TotalSeconds:F0}秒経過");

            return true;
        }

        // **有限回で諦める。** 納品できないまま留まり続けない。
        this.handInAttempts++;

        if (this.handInAttempts < MaxHandInAttempts)
        {
            this.anomalyLog.Warn(
                "Fate",
                $"{current.Name} の納品が {HandInPatience.TotalSeconds:F0} 秒進みません。" +
                $"やり直します（{this.handInAttempts} 回目）");

            this.handInSinceUtc = DateTime.UtcNow;
            return true;
        }

        this.anomalyLog.Warn(
            "Fate",
            $"{current.Name} へ納品できませんでした（所持 {count} 個）。討伐へ戻ります");

        this.handingIn = false;
        this.handInAttempts = 0;

        // 的の抑止を戻す。戻さないと討伐へ帰っても敵を狙わない。
        this.ResumeTargetingAfterHandIn();
        return false;
    }

    /// <summary>
    /// 敵が遠ければ近づく。
    ///
    /// <b>近ければ何もしない。</b>BMR が戦っている最中に経路を積むと、
    /// 回避の動きと取り合いになる。
    /// </summary>
    private void TickApproachMob(FateInfo fate)
    {
        if (!Player.Available)
        {
            return;
        }

        // 戦闘に入っていれば BMR に任せる。近づく必要はない。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            if (this.approaching)
            {
                this.StopApproach();
                this.trace.Decision("近づくのをやめた", "戦闘に入った");
            }

            return;
        }

        // **納品 FATE の納品中は、こちらから動かさない。**
        //
        // BMR の FateUtils は、集めた品が 10 個たまると納品 NPC を狙い、
        // そこへ向けて移動を強制する（Hints.ForcedMovement）。
        // こちらが同時に経路を積むと、2 つの力が引っ張り合って
        // NPC の前でガクガク動き、何度も話しかけることになる
        // （2026-09-26 実測。「彼らの想いで」でアンロスト・セントリーGX に
        // 繰り返し話しかけていた）。
        //
        // 納品は BMR のほうが段取りを知っている。任せる。
        if (fate.IsCollect && this.scanner.HasHandInItems(fate.Id))
        {
            if (this.approaching)
            {
                this.StopApproach();
                this.trace.Decision("近づくのをやめた", "納品は BossMod Reborn に任せる");
            }

            return;
        }

        var nearest = this.scanner.FindNearestMob(fate.Id, Player.Position);

        if (nearest is not { } mob)
        {
            // 敵が 1 匹も見えない。FATE の中心へ寄って湧くのを待つ。
            var toCentre = Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(fate.Position.X, fate.Position.Z));

            if (toCentre > CentreSeekMeters)
            {
                this.BeginApproach(fate.Position, "敵が見えないので中心へ寄る");
            }

            return;
        }

        // 十分近い。BMR が拾うはずなので任せる。
        //
        // **やめる距離と、始める距離を変える。**
        //
        // 同じ 15m で判定すると、その境目で「近いからやめる」と
        // 「遠いから始める」を毎フレーム往復する。やめるたびに
        // approaching が下りるので、引き直しの間隔も効かなくなり、
        // 150 ミリ秒ごとに経路を引き直してガクガク動く
        // （2026-09-26 実測。距離 15m のまま「経路 1 回目」を繰り返していた）。
        //
        // いったん近づいたら、少し離れるまでは近づき直さない。
        var stopWithin = MobReachMeters;
        var startBeyond = MobReachMeters + ApproachHysteresisMeters;

        if (mob.Distance <= stopWithin)
        {
            if (this.approaching)
            {
                this.StopApproach();
            }

            return;
        }

        // 追いかけている最中でなければ、少し離れるまで始めない。
        if (!this.approaching && mob.Distance <= startBeyond)
        {
            return;
        }

        this.BeginApproach(mob.Position, $"最寄りの敵まで {mob.Distance:F0}m");
    }

    /// <summary>近づく移動を始める。すでに向かっていれば何もしない。</summary>
    private void BeginApproach(Vector3 destination, string why)
    {
        if (this.approaching)
        {
            // **追いかける先は、動いた分だけ引き直す。**
            //
            // 敵は動く。毎フレーム新しい座標を渡すと、そのたびに
            // 「目的地が変わった」と見えて経路を引き直すことになる。
            // 経路を引き直すたびに足が止まるので、進んでは止まりを
            // 繰り返してガクガク動く（2026-09-25 実測。170 ミリ秒ごとに
            // 引き直していて、その間ほとんど進んでいなかった）。
            //
            // 少し動いたくらいでは引き直さない。最後に狙った場所から
            // 大きく離れたときだけ引き直す。
            var moved = Vector3.Distance(destination, this.approachTarget);

            // **間隔は、動いているかに関わらず空ける。**
            //
            // 以前は「動いているなら引き直さない」としていた。
            // ところが引っかかって動けていないときこそ Tick が Moving を返さず、
            // 毎フレーム引き直すことになっていた。
            // 引き直すたびに足が止まるので、いつまでも動き出せない
            // （2026-09-26 実測。160 ミリ秒ごとに「経路 1 回目」を繰り返し、
            // その間 1m しか進んでいなかった）。
            //
            // 進んでいないなら、なおさら間を置く。
            if (DateTime.UtcNow - this.approachIssuedUtc < ApproachRepathInterval)
            {
                this.navigation.Tick(this.approachTarget, MobReachMeters);
                return;
            }

            // 間隔が空いていても、狙う先がほとんど動いていないなら引き直さない。
            // 同じ場所へ何度も引き直しても結果は変わらない。
            if (moved <= ApproachRepathMeters &&
                this.navigation.Tick(this.approachTarget, MobReachMeters) is MoveStatus.Moving)
            {
                return;
            }

            this.approaching = false;
        }

        if (!this.navigation.BeginMove(destination, MobReachMeters, false, out var failure))
        {
            // 経路探索の最中なら、ただ待つ。失敗ではない。
            if (this.navigation.Busy)
            {
                return;
            }

            // 歩かせられないので、止めていたプリセットの移動を戻す。
            this.ResumePresetMovement();
            this.trace.Trouble("近づけない", failure);

            // **歩いて行けないなら飛ぶ。**
            //
            // 敵が崖の上にいると、地上の経路では辿り着けない。
            // 乗り直して飛べば、高さを越えられる。
            if (!MountService.IsMounted && MountService.CanFlyHere)
            {
                this.trace.Decision("飛んで近づく", "歩いて行ける経路が無い");
                this.mount.ClearDismounting();
            }

            return;
        }

        // 歩いている間はプリセットの移動を止める。止めないと vnavmesh と取り合う。
        this.ParkPresetMovement();
        this.approaching = true;
        this.approachTarget = destination;
        this.approachIssuedUtc = DateTime.UtcNow;

        // 引き直した回数を出す。ガクガクするときはここが増え続ける。
        this.approachIssues++;
        this.trace.Decision("敵へ近づく", $"{why}（経路 {this.approachIssues} 回目）");
    }

    /// <summary>
    /// その FATE で実際に降りられる座標を求める。
    ///
    /// <b>FATE の中心は、立てる場所とは限らない。</b>
    /// 石緑湖の「石緑湖の主「オアンネス」」のように、中心が湖の上にある FATE がある。
    /// 中心をそのまま目指すと水面の上まで飛んで、降りようとしても降りられず、
    /// 空中で止まったままになる（2026-09-25 実測。高さ 17 で 13 秒動けなかった）。
    ///
    /// vnavmesh に「その座標に最も近いナビメッシュ上の点」を聞く。
    /// ナビメッシュに載っている点は、定義上そこに立てる。
    /// GatherBuddyReborn も採集地点をこの方法で補正している
    /// （AutoGather.cs の NearestPoint(pos, 10, 10000)）。
    ///
    /// 1 つの FATE につき 1 度だけ聞いて覚える。毎フレーム聞くと重い。
    /// </summary>
    /// <summary>
    /// ある地点のまわりから、降りられる場所を探す。
    ///
    /// <b>1 点だけ聞かない。</b>
    /// 中心が湖や崖の上だと、そこを起点に聞いても答えが出ない。
    /// 中心から外へ向かって渦を描くように候補を並べ、近い順に試す。
    /// 近いものから返すので、なるべく中心の近くに降りられる。
    ///
    /// ICE（Cosmic Exploration の自動化）も同じ形で、
    /// 格子状に並べた点を近い順に試している
    /// （逆コンパイルして確認。FishingUtil.TryFindStand）。
    /// </summary>
    private Vector3? FindLandableAround(Vector3 centre, float radius)
    {
        // 刻み幅。細かすぎると問い合わせが増えるだけなので、半径から決める。
        var step = MathF.Max(radius / 4f, 5f);

        for (var ring = 0; ring <= LandableSearchRings; ring++)
        {
            for (var dx = -ring; dx <= ring; dx++)
            {
                for (var dz = -ring; dz <= ring; dz++)
                {
                    // その輪の縁だけを見る。内側はもう見ている。
                    if (ring != 0 && Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                    {
                        continue;
                    }

                    var probe = new Vector3(
                        centre.X + (dx * step),
                        centre.Y,
                        centre.Z + (dz * step));

                    // 辿り着ける点を優先する。ただのメッシュ上の最近傍だと、
                    // 湖の向こうの小島のように「そこには行けない」点が返りうる。
                    if (this.vnavmesh.TryNearestPointReachable(probe, step * 2f, 100f, out var reachable) &&
                        reachable is { } ok)
                    {
                        return ok;
                    }
                }
            }
        }

        // 辿り着ける点が 1 つも無い。せめてメッシュ上の点を返す。
        return this.vnavmesh.TryNearestPoint(centre, radius, 100f, out var any) ? any : null;
    }

    /// <summary>
    /// 着地したい地点を決める。
    ///
    /// <b>外周ではなく、中心の地上を狙う。</b>
    /// 敵が見えていればそちらを優先する。降りた先がそのまま戦う場所になり、
    /// 敵まで走る距離も無くなる。
    ///
    /// 見えていなければ中心。どちらも「立てる場所」に寄せてから返す。
    /// </summary>
    private Vector3 ResolveLandingTarget(FateInfo fate)
    {
        // 敵が見えているなら、そこへ。敵が立っている場所は必ず立てる。
        if (this.scanner.FindNearestMob(fate.Id, Player.Position) is { } mob)
        {
            return mob.Position;
        }

        // 見えていなければ中心。湖や崖の上なら、立てる場所へ寄せる。
        return this.ResolveLandablePoint(fate);
    }

    private Vector3 ResolveLandablePoint(FateInfo fate)
    {
        var here = Svc.ClientState.TerritoryType;

        if (this.landable is { } cached && cached.Id == fate.Id && cached.TerritoryId == here)
        {
            return cached.Point;
        }

        // **メッシュが出来ていないうちは聞かない。**
        //
        // エリアを移った直後は読み込みが終わっておらず、vnavmesh 側は
        // Query が null のまま何を聞いても null を返す
        // （IPCProvider.cs の navmeshManager.Query?.～）。
        // 誤った座標が返るわけではないが、補正できないまま
        // 「中心でよい」と覚えてしまうと、読み込みが終わっても直らない。
        // 覚えずに、次のフレームで聞き直す。
        if (!this.vnavmesh.TryIsReady(out var ready) || !ready)
        {
            this.trace.State("メッシュ待ち", $"{fate.Name} の降りられる場所を、まだ求められません");
            return fate.Position;
        }

        // **中心の 1 点だけを聞かない。**
        //
        // 中心が湖や崖の上だと、そこを起点に聞いても答えが出ないか、
        // 出ても遠くの行けない場所になる。
        // 中心から渦を描くように候補を並べ、近い順に聞いていく。
        //
        // ICE（Cosmic Exploration の自動化）も同じやり方をしている。
        // 中心の周りを格子状に並べ、辿り着ける点が見つかるまで試す
        // （逆コンパイルして確認。FishingUtil.TryFindStand）。
        var resolved = this.FindLandableAround(fate.Position, Math.Max(20f, fate.Radius));

        if (resolved is not { } found)
        {
            // 聞けなかった。中心のまま向かう。行けないと決まったわけではない。
            // ここも覚えない。次に聞けば答えが返るかもしれない。
            this.trace.Trouble("降りられる場所が分からない", $"{fate.Name} は中心をそのまま目指します");
            return fate.Position;
        }

        var moved = Vector3.Distance(found, fate.Position);

        // 大きく動いたときだけ残す。数メートルの補正はふつうのこと。
        if (moved > 3f)
        {
            this.trace.Decision(
                "降りられる場所へ補正",
                $"{fate.Name} 中心({fate.Position.X:F0},{fate.Position.Y:F0},{fate.Position.Z:F0}) → " +
                $"({found.X:F0},{found.Y:F0},{found.Z:F0}) {moved:F0}m ずらした");
        }

        this.landable = (here, fate.Id, found);
        return found;
    }

    /// <summary>近づく移動をやめ、プリセットの移動を戻す。</summary>
    private void StopApproach()
    {
        this.approaching = false;
        this.approachIssues = 0;
        this.navigation.Stop();
        this.ResumePresetMovement();
    }

    /// <param name="outcome">
    /// どう終わったか。<b>完了として数えるのは達成したときだけ。</b>
    /// </param>
    private void LeaveFate(Config cfg, FateInfo finished, FateOutcome outcome = FateOutcome.Success)
    {
        // **同じ湧きを二度数えない。**
        //
        // 100% は毎フレーム見えるので、離脱の処理が続けて呼ばれうる。
        // 数えた湧きを覚えておき、二度目は数だけ飛ばす
        // （離脱そのものは、呼ばれたぶんだけやり直してよい）。
        var alreadyCounted = this.countedSpawn == finished.SpawnKey;

        // 戦闘 AI を先に解除する。これを呼ばないと敵を追い続けて離れられない。
        this.ReleaseCombat();
        this.navigation.Stop();

        // **達成したものだけ数える。**
        // 失敗・時間切れ・消失を完了に混ぜると、件数が実績を表さなくなる。
        if (!alreadyCounted && outcome == FateOutcome.Success)
        {
            this.countedSpawn = finished.SpawnKey;
            this.Completed++;
        }
        else if (!alreadyCounted)
        {
            this.countedSpawn = finished.SpawnKey;
            this.Abandoned++;
        }

        // 納品 FATE は 100% の時点ではまだ報酬が入っていない。
        // 1 分後に FATE が消えるときに入る。それまでマップを離れない。
        //
        // **達成していないなら、報酬は待たない。** 入るものが無い。
        if (finished.IsCollect && outcome == FateOutcome.Success)
        {
            this.pendingRewards.RemoveAll(x => x.Id == finished.Id && x.Start == finished.StartTimeEpoch);
            this.pendingRewards.Add((
                Svc.ClientState.TerritoryType,
                finished.Id,
                finished.StartTimeEpoch,
                DateTime.UtcNow + CollectRewardWindow));
            this.anomalyLog.Info("Fate", $"{finished.Name} が 100% になりました。報酬が入るまでこのマップに留まります");
        }
        else if (outcome == FateOutcome.Success)
        {
            this.anomalyLog.Info("Fate", $"{finished.Name} が 100% になりました（完了 {this.Completed} 件）");
        }
        else
        {
            this.anomalyLog.Warn(
                "Fate",
                $"{finished.Name} は達成できませんでした" +
                $"（進捗 {finished.Progress}% 状態 {finished.State}／未達成 {this.Abandoned} 件）");
        }

        // この FATE はもう離れた。円の中に立っていても、二度と拾わない。
        this.leftFateId = finished.Id;

        this.target = null;
        this.SetStep(FateStep.Leaving, "次の FATE へ向かっています");

        // 先に決めてあった FATE があれば、そのまま動き出す。
        var next = this.ChooseDeparture(cfg, exclude: finished.Id);

        if (next is not null)
        {
            this.BeginMoveTo(next);
            return;
        }

        // **次が無くても、円の外へは出る。**
        //
        // 以前はその場で待っていた。終わった FATE の円の中に立ったままなので、
        // 利用者からは「終わったのに動かない」に見える。
        // 納品 FATE の報酬を待つ場合も、待つ場所は円の外でよい。
        this.retreatFrom = finished;
        this.retreatSinceUtc = DateTime.UtcNow;
        this.retreatLiftIssued = false;
        this.retreatLiftAttempts = 0;
        this.retreatFlyingFrames = 0;
        this.retreatTo = null;
        this.SetStep(FateStep.Leaving, $"{finished.Name} の範囲外へ退避しています");

        // **飛べるなら、地上の経路は積まない。**
        //
        // 以前はここで必ず経路を積んでいた。そのため飛ぶ前に歩き出し、
        // 円の外の何もない地点へ走って行く動きが出ていた。
        // 飛べるなら TickLeaving が乗せて飛ばすので、経路は要らない。
        if (MountService.CanFlyHere)
        {
            return;
        }

        // 飛べないエリアだけ、歩いて出る経路を積む。
        // Leaving のまま見届ける。ここで Waiting にすると誰も監視しない。
        if (this.RetreatFromCircle(finished))
        {
            return;
        }

        // 経路を積めなかった。退避は諦めて探索へ戻る。
        this.retreatFrom = null;
        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 円の外へ出るのを見届ける。
    ///
    /// <b>経路を積んだだけでは出られたことにならない。</b>
    /// 以前はここを作らず、積んだ直後に Waiting へ移していた。
    /// Waiting は移動を監視しないので、経路が引けなくても、
    /// 途中で引っかかっても、誰も気づかないままだった。
    /// </summary>
    private void TickLeaving(Config cfg)
    {
        if (this.retreatFrom is not { } finished || !Player.Available)
        {
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            this.retreatFrom = null;
            return;
        }

        // 円の外へ出られたか。出られていれば、それでよい。
        var flat = Vector2.Distance(
            new Vector2(Player.Position.X, Player.Position.Z),
            new Vector2(finished.Position.X, finished.Position.Z));

        var success = this.RetreatSuccessDistance(finished);

        if (flat >= success)
        {
            this.trace.Decision("円の外へ出た", $"{finished.Name} 中心から {flat:F0}m");
            this.navigation.Stop();
            this.FinishRetreat(escaped: true);
            return;
        }

        // 待ちすぎない。出られなくても周回は続ける。
        //
        // **納品 FATE では短く切り上げる。**
        // 報酬が入るまで最大 90 秒マップに留まるので、
        // ここで長く粘ると次の FATE を回る時間を削ることになる。
        var retreatLimit = finished.IsCollect ? CollectRetreatTimeout : RetreatTimeout;

        if (DateTime.UtcNow - this.retreatSinceUtc > retreatLimit)
        {
            this.trace.Trouble(
                "円の外へ出られない",
                $"{finished.Name} 中心から {flat:F0}m（{success:F0}m まで離れたかった）");

            this.navigation.Stop();
            this.FinishRetreat(escaped: false);
            return;
        }

        // **戦闘を切って、乗って飛ぶ。**
        //
        // 100% になったら敵は無視してよい。ターゲットを外し、
        // オートアタックを止めないと戦闘が切れず、戦闘中はマウントに
        // 乗れないので飛べない。
        //
        // ターゲットは LeaveFate の ReleaseCombat で外しているが、
        // 退避の最中に絡まれて狙い直すことがあるため、ここでも見る。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            this.targets.ReleaseTarget();
            this.targets.StopAutoAttack();
        }

        // **まず乗って飛ぶ。地面を走らせない。**
        //
        // 以前は円の外の地上の点（中心から 75m ほど）へ歩かせていた。
        // そこは「円の外」という条件だけで選んだ点で、何もない場所になる。
        // 利用者からは「FATE が終わったあと、何もない所へ走って行く」
        // 不審な動きに見えていた（2026-09-26 の報告）。
        //
        // 飛べるなら、その場で真上へ上がれば円から出られる。
        // 次の FATE が決まればそこへ飛ぶので、地上を走る意味が無い。
        if (MountService.CanFlyHere)
        {
            if (this.TickFlyOutOfCircle(finished))
            {
                return;
            }
        }

        // **飛べないので歩いて出る。**
        //
        // 風脈を未解放のエリア、あるいは待っても乗れなかった場合。
        // 経路をまだ積んでいなければ、ここで積む。
        if (this.retreatTo is null)
        {
            if (!this.RetreatFromCircle(finished))
            {
                this.trace.Trouble("退避の経路を積めない", $"{finished.Name}");
                this.FinishRetreat(escaped: false);
                return;
            }
        }

        if (!MountService.IsMounted &&
            !Svc.Condition[ConditionFlag.InCombat] &&
            this.retreatTo is { } destination &&
            Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(destination.X, destination.Z)) > 20f)
        {
            if (this.mount.TickPrepare(destination))
            {
                this.StatusDetail = $"{finished.Name} の範囲外へ出る準備をしています";
                return;
            }
        }

        // **移動を見届ける。渡すのは退避先。**
        //
        // 以前は FATE の中心を渡していた。許容距離が「半径 + 余裕」なので
        // 「中心からその距離以内なら到着」＝<b>円の中に立っていると到着</b>になり、
        // しかもスタック時の引き直しが中心へ向かう経路になっていた。
        var watching = this.retreatTo ?? finished.Position;
        var status = this.navigation.Tick(watching, RetreatArrivalRange);

        // **ShortOfTarget も拾う。**
        //
        // 「近づけるところまで行って終わった」状態で、放置しても二度と動かない
        // （NavigationService の MoveStatus の説明）。拾っていなかったため、
        // 画面の文字を書き換えるだけで時間切れまで棒立ちになっていた。
        if (status is MoveStatus.Failed or MoveStatus.Stuck or MoveStatus.ShortOfTarget)
        {
            // **別の向きへ出直す。** 1 回引けなかっただけで諦めない。
            if (this.retreatAttempts < MaxRetreatAttempts)
            {
                this.retreatAttempts++;
                this.trace.Trouble(
                    "退避できない",
                    $"{finished.Name}（{status}）別の向きで {this.retreatAttempts} 回目を試します");

                this.navigation.Stop();

                if (this.RetreatFromCircle(finished, turnDegrees: this.retreatAttempts * 90f))
                {
                    this.retreatSinceUtc = DateTime.UtcNow;
                    this.retreatLiftIssued = false;
                    this.retreatLiftAttempts = 0;
                    this.retreatFlyingFrames = 0;
                    return;
                }
            }

            this.trace.Trouble("退避を諦めた", $"{finished.Name}（{status}）");
            this.navigation.Stop();
            this.FinishRetreat(escaped: false);
            return;
        }

        this.StatusDetail = $"{finished.Name} の範囲外へ退避しています（中心から {flat:F0}m）";
    }

    /// <summary>
    /// 円の外へ出たと認める、中心からの距離。
    ///
    /// 出発と到着で別の式を使わないよう、1 箇所にまとめる。
    /// </summary>
    private float RetreatSuccessDistance(FateInfo finished)
        => MathF.Max(finished.Radius, 20f) + RetreatMarginMeters;

    /// <summary>
    /// 乗って飛んで、その場から離れる。
    ///
    /// <b>地面を走らせない。</b>
    /// 以前は円の外の地上の点へ歩かせていたが、そこは「円の外」という
    /// 条件だけで選んだ点で、何もない場所になる。利用者からは
    /// 「FATE が終わったあと、何もない所へ走って行く」動きに見えていた。
    ///
    /// <b>飛び上がれば、それで退避は済んだものとする。</b>
    /// 次の FATE が決まればそこへ飛ぶので、水平に離れておく意味が無い。
    /// 空中に居れば、終わった FATE の敵にも絡まれない。
    /// </summary>
    /// <returns>この段階で処理を終えたら true。</returns>
    private bool TickFlyOutOfCircle(FateInfo finished)
    {
        // **飛べた。退避はここで終わり。**
        //
        // 円の中の上空でも構わない。地上に居ないので敵に絡まれず、
        // 次の行き先が決まれば、そのまま飛んで向かえる。
        //
        // **1 フレームで信じない。**
        // ジャンプした直後の一瞬だけ InFlight が立つことがある。
        // それを拾うと、実際には浮いていないのに「飛んで離れた」ことにして
        // 円の中に立ったまま次へ進む。しかも記録には成功と残るので、
        // あとから気づけない。進入側も同じ理由で 3 回続けて確かめている
        // （FateApproach の FlyingStableFrames）。
        if (MountService.IsFlying)
        {
            this.retreatFlyingFrames++;

            if (this.retreatFlyingFrames >= RetreatFlyingStableFrames)
            {
                this.trace.Decision(
                    "飛んで離れた",
                    $"{finished.Name} 高さ {Player.Position.Y:F0}。次の FATE へはここから飛びます");

                // **vnavmesh を直接止める。**
                // 離陸の合図は NavigationService を通さず TryMoveAlong で
                // 送っているため、navigation.Stop() は moveIssued が false で
                // 素通りする（NavigationService.Stop の冒頭）。
                this.vnavmesh.TryStop();
                this.navigation.Stop();
                this.FinishRetreat(escaped: true);
            }
            else
            {
                this.StatusDetail = $"{finished.Name} から飛んで離れています";
            }

            return true;
        }

        this.retreatFlyingFrames = 0;

        // 戦闘中は乗れない。切れるのを待つ。
        // 上で狙いを外しているので、じきに切れる。
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            this.StatusDetail = $"{finished.Name} 戦闘が切れるのを待っています";
            return true;
        }

        // 乗る。距離では決めさせない（その場で飛ぶので目的地が無い）。
        //
        // **降りる途中の記録を消しておく。**
        // 着地して降りた直後なので、これが残っていると二度と乗らない。
        this.mount.ClearDismounting();

        if (!MountService.IsMounted)
        {
            if (this.mount.TickPrepareAlways())
            {
                this.StatusDetail = $"{finished.Name} から離れる準備をしています";
                return true;
            }

            // **乗れるまで、その場で待つ。**
            //
            // 乗れない理由はたいてい一時的（戦闘が切れる直前、直前の操作の
            // 硬直、降りた直後のリキャスト）で、少し待てば乗れる。
            // 待たずに歩き出すと、何もない場所へ走る動きになる。
            //
            // 納品 FATE だけは短く切り上げる。報酬が入るまで最大 90 秒
            // マップに留まるので、ここで長く粘ると次を回る時間が減る。
            var mountPatience = finished.IsCollect
                ? CollectRetreatMountPatience
                : RetreatMountPatience;

            if (DateTime.UtcNow - this.retreatSinceUtc < mountPatience)
            {
                this.StatusDetail =
                    $"{finished.Name} マウントに乗れるのを待っています（{MountService.DescribeMountBlocker()}）";

                return true;
            }

            this.trace.Trouble(
                "乗れないので歩いて出る",
                $"{finished.Name} {MountService.DescribeMountBlocker()}");

            return false;
        }

        // 乗れた。真上へ上がって離陸する。
        //
        // vnavmesh は「次の経路点が自分より高い」「騎乗中」「まだ飛んでいない」
        // が揃うとジャンプを連打して離陸する（FollowPath.cs:142-154）。
        // 少し上の点を渡して、その条件を作る。

        // **合図を送ったきりにしない。**
        //
        // 屋根の下、飛行の高度上限、ジャンプが弾かれた、渡した点に着いて
        // 経路が終わった——などで、乗れているのに飛べないことがある。
        // 以前は旗を立てたきり送り直さなかったため、その状態になると
        // 毎フレーム画面に文字を書くだけで何も操作せず、
        // 時間切れまで完全に固まっていた。
        //
        // 送り直しても飛べないなら、歩いて出るほうへ落とす。
        if (this.retreatLiftIssued &&
            DateTime.UtcNow - this.retreatLiftUtc > RetreatLiftRetry)
        {
            this.retreatLiftIssued = false;
            this.retreatLiftAttempts++;

            this.trace.Trouble(
                "離陸できない",
                $"{finished.Name} 合図を送り直します（{this.retreatLiftAttempts} 回目・" +
                $"{MountService.DescribeFlightStatus()}）");
        }

        if (this.retreatLiftAttempts > MaxRetreatLiftAttempts)
        {
            this.trace.Trouble(
                "飛べないので歩いて出る",
                $"{finished.Name} {MaxRetreatLiftAttempts} 回試しても飛べませんでした" +
                $"（{MountService.DescribeFlightStatus()}）");

            return false;
        }

        if (!this.retreatLiftIssued)
        {
            var up = Player.Position with { Y = Player.Position.Y + RetreatLiftMeters };

            if (this.vnavmesh.TryMoveAlong([up], fly: true))
            {
                this.retreatLiftIssued = true;
                this.retreatLiftUtc = DateTime.UtcNow;
                this.trace.State("離陸させる", $"{finished.Name} 真上 {up.Y:F0} へ");
            }
        }

        this.StatusDetail = $"{finished.Name} から飛んで離れています";
        return true;
    }

    /// <summary>
    /// 退避を終えて、次を探す段階へ戻す。
    ///
    /// <b>出られなかったことを、出られたことにしない。</b>
    /// どちらでも次を探す段階へ戻るが、記録には残す。
    /// 残さないと、円の中で次を探し続けていることに気づけない。
    /// </summary>
    private void FinishRetreat(bool escaped)
    {
        if (!escaped)
        {
            this.anomalyLog.Warn(
                "Fate",
                this.retreatFrom is { } stuck
                    ? $"{stuck.Name} の円の外へ出られませんでした。円の中から次を探します"
                    : "円の外へ出られませんでした");
        }

        this.retreatFrom = null;
        this.retreatTo = null;
        this.retreatAttempts = 0;
        this.retreatLiftIssued = false;
        this.retreatLiftAttempts = 0;
        this.retreatFlyingFrames = 0;
        this.moveIssued = false;
        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 終えた FATE の円から出る。
    ///
    /// <b>次の FATE が無くても、その場に留まらない。</b>
    /// 円の中に立っていると、終わったのに動いていないように見える。
    /// 納品 FATE の報酬待ちも、円の外で待てばよい。
    ///
    /// 出る先はメッシュに聞く。聞けなければ、中心から離れる向きへ素直に出る。
    /// </summary>
    /// <param name="finished">出たい円。</param>
    /// <param name="turnDegrees">
    /// 出る向きを回す角度。前の向きで引けなかったときに使う。
    /// </param>
    private bool RetreatFromCircle(FateInfo finished, float turnDegrees = 0f)
    {
        if (!Player.Available)
        {
            return false;
        }

        var here = Player.Position;

        // 中心から自分へ向かう向き。円の外はその延長線上。
        var away = new Vector3(here.X - finished.Position.X, 0f, here.Z - finished.Position.Z);
        var length = away.Length();

        // 中心とほぼ同じ場所にいるなら、向きが決まらない。適当な向きで出る。
        var direction = length > 1f
            ? away / length
            : new Vector3(1f, 0f, 0f);

        // 前の向きで出られなかったので回す。
        if (turnDegrees != 0f)
        {
            var radians = turnDegrees * MathF.PI / 180f;
            var cos = MathF.Cos(radians);
            var sin = MathF.Sin(radians);

            direction = new Vector3(
                (direction.X * cos) - (direction.Z * sin),
                0f,
                (direction.X * sin) + (direction.Z * cos));
        }

        // **成功の基準より、さらに外に目標を置く。**
        //
        // 経路は目標から RetreatArrivalRange まで近づいた時点で終わる。
        // 目標をちょうど基準に置くと、経路が終わった地点がまだ基準の内側で、
        // 円の外に出ているのに時間切れまで待つことになる
        // （半径50m → 目標60m、55m で経路が終わり、基準の60mに届かない）。
        var success = this.RetreatSuccessDistance(finished);
        var target = success + RetreatArrivalRange + RetreatMarginMeters;
        var outside = finished.Position + (direction * target);

        // 立てる場所へ寄せる。
        //
        // **寄せた先が円の中に戻っていないかを見る。**
        // 寄せる幅は最大 30m ある。円の外が崖や水の向こうだと、
        // 「辿り着ける最近傍」は円の内側になる。そのまま目標にすると
        // 円から出るための移動が、円の中へ向かう移動になる。
        if (this.vnavmesh.TryIsReady(out var ready) && ready &&
            this.vnavmesh.TryNearestPointReachable(outside, 30f, 100f, out var onMesh) &&
            onMesh is { } spot)
        {
            var snapped = Vector2.Distance(
                new Vector2(spot.X, spot.Z),
                new Vector2(finished.Position.X, finished.Position.Z));

            if (snapped >= success + RetreatArrivalRange)
            {
                outside = spot;
            }
            else
            {
                this.trace.Trouble(
                    "退避先の補正を断った",
                    $"{finished.Name} メッシュに寄せると中心から {snapped:F0}m で、" +
                    $"{success:F0}m の外に出られません。補正せずに向かいます");
            }
        }

        this.retreatTo = outside;

        this.trace.Decision(
            "円の外へ出る",
            $"{finished.Name} 半径{finished.Radius:F0}m " +
            $"目標({outside.X:F0},{outside.Y:F0},{outside.Z:F0}) " +
            $"成功基準={success:F0}m 目標距離={target:F0}m");

        // **飛んでいなくても、乗って出る。**
        //
        // 以前は IsFlying をそのまま fly に渡していた。戦闘のあとは
        // 地上に立っているので、必ず false になり、徒歩で退避していた。
        // 退避の距離は半径 + 余裕で、20m を超えることがふつうにある。
        var fly = MountService.IsMounted && MountService.CanFlyHere;

        return this.navigation.BeginMove(outside, RetreatArrivalRange, fly, out _);
    }

    private void FinishCurrentFate()
    {
        this.starter.CancelMovement(this.navigation);
        this.starter.Reset();
        this.startingFate = false;
        this.ReleaseCombat();
        this.target = null;
        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
    }

    // ---- 部品 ----

    /// <summary>
    /// 出発するときの行き先を決める。
    ///
    /// <b>先に決めておいた候補を、そのままは使わない。</b>
    /// 達成度 70% の時点で決めた候補は、出発するころには
    /// 終わっていたり、条件から外れていたり、もっと近いものが
    /// 湧いていたりする。出発位置で選び直し、先に決めた候補は
    /// 「まだ有効ならそれでよい」という程度に扱う。
    /// </summary>
    private FateInfo? ChooseDeparture(Config cfg, ushort? exclude = null)
    {
        var fresh = this.PickNext(cfg, exclude);
        var reserved = this.prefetched;
        this.prefetched = null;

        if (reserved is null)
        {
            return fresh;
        }

        // 先に決めた候補が、いまも同じ湧きのまま選ばれたなら、それでよい。
        if (fresh is not null && fresh.SpawnKey == reserved.SpawnKey)
        {
            return fresh;
        }

        // 選び直した結果が違うなら、そちらを採る。
        if (fresh is not null)
        {
            this.trace.Decision(
                "先に決めた候補を取り消す",
                $"{reserved.Name} → {fresh.Name}（出発時に選び直し）");
            return fresh;
        }

        // 選び直して何も無いなら、先に決めた候補も無効になっている。
        return null;
    }

    private FateInfo? PickNext(Config cfg, ushort? exclude = null)
    {
        if (!Player.Available)
        {
            return null;
        }

        var skip = this.blacklist;
        if (exclude is { } id)
        {
            skip = [.. this.blacklist, id];
        }

        return this.scanner.PickNext(
            Player.Position,
            cfg.FateMinTimeRemainingSec,
            cfg.FateMaxProgressPct,
            cfg.FateLevelFilterEnabled,
            Player.Level,
            cfg.FateMaxLevelBelow,
            cfg.FateMaxLevelAbove,
            skipCollect: !cfg.FateCollectEnabled,
            skip,
            // 最寄り優先なら距離だけで決める。
            cfg.FateNearestFirst
                ? FateScanner.NearestFirstSortOrder
                : FateScanner.DefaultSortOrder,
            // 最寄り優先のときは仲間追従で割り込ませない。
            // 「いちばん近いものへ行く」という約束が崩れるため。
            cfg.FateFollowParty && !cfg.FateNearestFirst);
    }

    /// <summary>見送りの期限が切れた FATE を、また狙えるようにする。</summary>
    private void ExpireBlacklist()
    {
        if (this.blacklistUntil.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        List<ushort>? done = null;

        foreach (var (id, until) in this.blacklistUntil)
        {
            if (now >= until)
            {
                (done ??= []).Add(id);
            }
        }

        if (done is null)
        {
            return;
        }

        foreach (var id in done)
        {
            this.blacklistUntil.Remove(id);
            this.blacklist.Remove(id);
            this.stuckCounts.Remove(id);
            this.anomalyLog.Info("Fate", $"FATE {id} の見送りを解きました。もう一度狙います");
        }
    }

    private void MarkStuck(ushort fateId)
    {
        // **諦めるなら、いま走っている経路も止める。**
        //
        // 止めないと vnavmesh は前の目的地へ向かって飛び続ける。
        // こちらは「探している」つもりでも体は元の FATE へ進み、
        // 次の FATE を決めた瞬間に向きが変わるため、
        // 空中でくるりと反転したように見える
        // （2026-09-25 実測。諦めてから 6 秒間に 111m 進んでいた）。
        this.navigation.Stop();
        this.moveIssued = false;

        // 詰めていた許容値を戻す。諦めるときも必ず通す。
        this.RestorePathTolerance();

        this.stuckCounts.TryGetValue(fateId, out var count);
        count++;
        this.stuckCounts[fateId] = count;

        if (count >= MaxStuckPerFate)
        {
            // **見送るのは一時的にする。**
            //
            // 入り組んだ地形の FATE でも、通路さえ辿れば行ける。
            // 辿り着けなかったのは、こちらの経路の追い方が悪かっただけの
            // ことが多い。周回のあいだずっと除け続けると、
            // そのマップで稼げる FATE が減っていく。
            //
            // 少し置いてから、もう一度試す。
            this.blacklist.Add(fateId);
            this.blacklistUntil[fateId] = DateTime.UtcNow + BlacklistDuration;

            this.anomalyLog.Warn(
                "Fate",
                $"FATE {fateId} は {count} 回続けて辿り着けなかったため、" +
                $"{BlacklistDuration.TotalMinutes:F0} 分ほど見送ります");
        }
    }

    private void AdvanceZone(Config cfg)
    {
        // **固定したルートで進める。**
        // 生きた設定を見ると、周回中にチェックを外されたときに
        // 添字が別のマップを指し、マップを飛ばす。
        if (this.route.Count <= 1)
        {
            // **移れるマップが無いことを、黙って待たない。**
            //
            // 1 つしか選ばれていないのにそのマップで回れないなら、
            // 待っても何も変わらない。以前はここで時刻を書き換えるだけで、
            // 永久に待ち続けていた。
            if (DateTime.UtcNow - this.waitingSinceUtc > NoAlternativeZonePatience)
            {
                this.Stop(
                    this.route.Count == 0
                        ? "周回するマップが無くなりました"
                        : $"{NpcLocationService.GetTerritoryName(this.route[0])} で FATE を狙えず、" +
                          "移れる別のマップもありません");

                return;
            }

            this.StatusDetail = "移れる別のマップがありません";
            return;
        }

        // **並べた順に、次のマップへ移る。**
        //
        // 一覧の末尾まで行ったら先頭へ戻る。飛ばさない。
        // ただし「次がいまいるマップ」のときだけは、移っても何も変わらないので
        // もう 1 つ進める（一覧に同じマップが並んでいる場合など）。
        var here = Svc.ClientState.TerritoryType;

        this.zoneIndex = (this.zoneIndex + 1) % this.route.Count;

        if (this.route[this.zoneIndex] == here)
        {
            this.zoneIndex = (this.zoneIndex + 1) % this.route.Count;
        }

        var next = this.route[this.zoneIndex];

        if (next == here)
        {
            // 一覧がいまいるマップだけだった。移りようがない。
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        // マップを移るので、そのマップ固有の記録は捨てる。
        this.blacklist.Clear();
        this.blacklistUntil.Clear();
        this.stuckCounts.Clear();
        this.target = null;
        this.prefetched = null;

        // **行き先を控える。**
        // これが入っていると、いま一覧のマップにいても移動を続ける。
        // TickTraveling は「段階が Traveling で、控えた行き先と一致する」
        // ときだけテレポを撃つので、ここでは段階も一緒に立てる。
        this.travelTargetTerritory = next;
        this.teleportStartedUtc = DateTime.UtcNow;
        this.teleportIssued = false;
        this.SetStep(FateStep.Traveling, $"{NpcLocationService.GetTerritoryName(next)} へ移動しています");
        this.anomalyLog.Info("Fate", $"次のマップ {NpcLocationService.GetTerritoryName(next)} へ移ります");
    }

    private bool TryTeleportTo(uint territoryId)
    {
        if (!this.aetherytes.TryFindTarget(territoryId, out var teleport) || teleport is null)
        {
            return false;
        }

        return this.lifestream.TryTeleport(teleport.AetheryteId, teleport.SubIndex, out var accepted) && accepted;
    }

    /// <summary>
    /// 納品 FATE の報酬待ちを見直す。
    ///
    /// <b>FATE が消えたことを「報酬が入った」と書かない。</b>
    /// 消えたのは終わったからで、受け取れたかどうかは別の話。
    /// こちらから受領を直接確かめる手段が無いので、
    /// 「終わったのを見た（受領は未確認）」と正直に記録する。
    ///
    /// 待ちは複数持つ。90 秒のうちに 2 つ終えることがあるため。
    /// </summary>
    private void RefreshPendingReward()
    {
        if (this.pendingRewards.Count == 0)
        {
            // 待ちが無くなった。上昇の合図の記録も捨てる。
            // 残すと、次に報酬を待つとき上がらなくなる。
            this.hoverLiftIssued = false;
            return;
        }

        var here = Svc.ClientState.TerritoryType;
        var now = DateTime.UtcNow;

        for (var i = this.pendingRewards.Count - 1; i >= 0; i--)
        {
            var pending = this.pendingRewards[i];

            // 覚えたときとは別のマップにいる。そのマップの報酬はもう待てない。
            if (pending.Territory != here)
            {
                this.anomalyLog.Warn(
                    "Fate",
                    $"{NpcLocationService.GetTerritoryName(pending.Territory)} を離れたため、" +
                    "納品 FATE の報酬を待てなくなりました");

                this.pendingRewards.RemoveAt(i);
                continue;
            }

            var live = this.scanner.GetById(pending.Id);

            // 消えた、または別の湧きに入れ替わった。終わったことは確かめられたが、
            // 受け取れたかどうかは、こちらからは分からない。
            if (live is null || live.StartTimeEpoch != pending.Start)
            {
                this.anomalyLog.Info("Fate", "納品 FATE が終了しました（報酬の受領は未確認）");
                this.pendingRewards.RemoveAt(i);
                continue;
            }

            if (now > pending.DeadlineUtc)
            {
                this.anomalyLog.Warn(
                    "Fate",
                    $"納品 FATE の終了を {CollectRewardWindow.TotalSeconds:F0} 秒待ちましたが、確認できませんでした");

                this.pendingRewards.RemoveAt(i);
            }
        }
    }

    // BMR のモジュール名。RotationModuleRegistry は「名前空間 + 型名」で引く
    // （BossMod.SourceGen の RuntimeFullName）。
    private const string ModuleFateUtils = "BossMod.Autorotation.MiscAI.FateUtils";
    private const string ModuleAutoTarget = "BossMod.Autorotation.MiscAI.AutoTarget";
    private const string ModuleNormalMovement = "BossMod.Autorotation.MiscAI.NormalMovement";
    private const string TrackDestination = "Destination";

    // トラック名と選択肢名は enum の名前そのもの
    // （RotationModule.Define が expectedIndex.ToString() を InternalName にする）。
    private const string TrackHandin = "Handin";
    private const string TrackCollect = "Collect";
    private const string TrackSync = "Sync";
    private const string TrackChocobo = "Chocobo";
    private const string TrackFate = "FATE";

    /// <summary>AutoTarget の General トラック。狙いに行くかどうか。</summary>
    private const string TrackGeneral = "General";

    /// <summary>AutoTarget の Retarget トラック。的を選び直すかどうか。</summary>
    private const string TrackRetarget = "Retarget";

    /// <summary>
    /// 的を選び直さない。
    ///
    /// AutoTarget は優先度の並べ替えまでは行い、ForcedTarget を
    /// 書く前に戻る（AutoTarget.cs:232-237）。
    /// つまり、こちらが置いた的は残り、ジョブのモジュールは
    /// その相手に技を撃てる。
    /// </summary>
    private const string OptionNever = "Never";

    private const string OptionEnabled = "Enabled";
    private const string OptionDisabled = "Disabled";
    private const string OptionNone = "None";

    /// <summary>
    /// 自分から狙いに行かない。
    ///
    /// AutoTarget は Execute の冒頭で戻るだけなので（AutoTarget.cs:110-111）、
    /// 優先度を触らない。すでに狙っている相手への攻撃は続く。
    /// </summary>
    private const string OptionPassive = "Passive";

    /// <summary>レベルシンクを入れる。FateSync.Enable の名前。</summary>
    private const string OptionSyncEnable = "Enable";

    /// <summary>
    /// 戦闘プリセットを有効にする。
    ///
    /// 設定が空なら、こちらで用意したプリセット（<see cref="FateCombatPreset"/>）を使う。
    /// 利用者が名前を知らなくても、FATE の敵だけを狙い、
    /// 絡まれたら反撃する設定で動き出せる。
    ///
    /// <b>BMR の AI（/bmrai）は入れない。プリセットだけで戦わせる。</b>
    ///
    /// プリセットの中で、狙う（AutoTarget）・撃つ（ジョブのモジュール）・
    /// 寄る（ジョブのモジュールが間合いを GoalZones に出し、NormalMovement が動く）・
    /// シンクする（FateUtils）がすべて揃う。AutoFATEGrind も AI を使わずこの形で動く。
    ///
    /// AI を入れると、この仕組みが 3 か所で壊れる（BMR 7.5.6.19 で確認）。
    /// <list type="number">
    /// <item><c>/bmrai on</c> は最初に SwitchToIdle を通り、有効なプリセットを null にする
    /// （AI/AIManager.cs SwitchToIdle）。直前に入れたプリセットが消える。</item>
    /// <item>AI は毎回「ターゲットがあれば AI 用プリセット、無ければ null」で有効プリセットを
    /// 上書きする（AI/AIBehaviour.cs）。AI 用プリセットは利用者の BMR 設定
    /// （AIAutorotPresetName）で、既定は空。空だと技を撃つモジュールも
    /// シンクする FateUtils も動かない。</item>
    /// <item>AI が動いている間、NormalMovement は何もしない
    /// （Autorotation/MiscAI/NormalMovement.cs 冒頭）。</item>
    /// </list>
    /// 「プリセットだけでは敵を追わない」と見えたのは、当時レベルシンクが入っていなかったため。
    /// シンクしていないと BMR は FATE の敵を攻撃対象から外す
    /// （Framework/Utils.cs IsPlayerSyncedToFate・BossModule/AIHintsBuilder.cs FillEnemies）。
    /// </summary>
    private void ApplyCombat(Config cfg)
    {
        if (this.presetApplied)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(cfg.FateCombatPreset)
            ? FateCombatPreset.Name
            : cfg.FateCombatPreset;

        // **プリセットを入れる前に AI を切る。**
        //
        // 以前の版が入れた AI や、利用者が自分で入れていた AI が残っていると、
        // 上の 3 つの理由でプリセットが働かない。/bmrai off もプリセットを null にするので、
        // 必ず SetActive より先に送る。
        this.bossMod.TrySetAiEnabled(false);

        if (!this.bossMod.TrySetActivePreset(name, out var accepted) || !accepted)
        {
            this.anomalyLog.Warn("Fate", $"BossMod Reborn のプリセット「{name}」を有効にできませんでした");
            return;
        }

        this.presetApplied = true;
        this.appliedPresetName = name;
        this.presetCheckedUtc = DateTime.UtcNow;
        this.ApplyFateStrategies(cfg, name);

        // **技を撃つのは RSR。**
        //
        // BMR は「どこへ動き、誰を狙うか」までしか決めない。
        // RSR が切れていると、FATE に着いて敵を狙ったまま棒立ちになる。
        rotation.Enable("F.A.T.E 周回");

        // 本当に有効になったかを確かめる。SetActive が true を返しても、
        // 別の機能があとから解除していることがある。
        this.bossMod.TryGetActivePreset(out var active);
        this.trace.Decision(
            "戦闘の用意ができた",
            $"要求={name} 実際={active ?? "なし"}");
    }

    /// <summary>
    /// 戦っている間、プリセットが有効なままかを見張る。外れていたら入れ直す。
    ///
    /// <b>1 度入れたきりにしない。</b>
    /// 利用者や別のプラグインが BMR の AI を入れる・プリセットを切り替えると、
    /// こちらのプリセットは黙って外れ、技も移動もシンクも止まる。
    /// AutoFATEGrind も戦闘中は毎回プリセットを確かめ直している。
    /// </summary>
    private void EnsurePresetActive(Config cfg)
    {
        if (DateTime.UtcNow - this.presetCheckedUtc < PresetCheckInterval)
        {
            return;
        }

        this.presetCheckedUtc = DateTime.UtcNow;

        // 最初に入れられなかった（BMR の起動待ちなど）なら、ここで入れ直す。
        if (!this.presetApplied)
        {
            this.ApplyCombat(cfg);
            return;
        }

        if (!this.bossMod.TryGetActivePreset(out var active)
            || string.Equals(active, this.appliedPresetName, StringComparison.Ordinal))
        {
            return;
        }

        this.trace.Trouble("プリセットが外れていた", $"期待={this.appliedPresetName} 実際={active ?? "なし"}。入れ直します");

        // 外した犯人が AI なら、切らないと次の瞬間にまた外される。
        this.bossMod.TrySetAiEnabled(false);
        this.bossMod.TrySetActivePreset(this.appliedPresetName, out _);
    }

    /// <summary>
    /// vnavmesh で歩かせる間、プリセットの移動（NormalMovement）を止める。
    ///
    /// <b>両方が同時に動かすと取り合いになる。</b>
    /// プリセットは戦闘を続けたまま、移動だけを vnavmesh に明け渡す。
    /// AutoFATEGrind の ParkBossModMovement と同じ作法。
    /// </summary>
    private void ParkPresetMovement()
    {
        if (!this.presetApplied || this.movementParked)
        {
            return;
        }

        if (this.bossMod.TryAddTransientStrategy(this.appliedPresetName, ModuleNormalMovement, TrackDestination, OptionNone, out var ok) && ok)
        {
            this.movementParked = true;
        }
    }

    /// <summary>止めていたプリセットの移動を戻す。</summary>
    private void ResumePresetMovement()
    {
        if (!this.movementParked)
        {
            return;
        }

        this.movementParked = false;

        if (!this.presetApplied)
        {
            return;
        }

        if (!this.bossMod.TryClearTransientStrategy(this.appliedPresetName, ModuleNormalMovement, TrackDestination, out var ok) || !ok)
        {
            this.anomalyLog.Warn("Fate", "BossMod Reborn の移動を戻せませんでした（NormalMovement.Destination）");
        }
    }

    /// <summary>
    /// FATE 向けの一時方針を立てる。
    ///
    /// <b>納品は BMR の FATE helper に任せる。</b>
    /// 自前で NPC へ歩いて話しかける処理は書かない。
    /// BMR 側は 10 個溜まった時点で自動的に納品へ向かい、
    /// 向かう間は新しい敵に絡まず、着いたら話しかける。
    /// 戻ってきたら通常の戦闘に戻る。要望の挙動をそのまま満たす。
    ///
    /// 立てた方針はプリセット本体を書き換えない。
    /// 解除は ReleaseCombat でまとめて行う。
    /// </summary>
    private void ApplyFateStrategies(Config cfg, string preset)
    {
        // 納品 FATE のアイテムを 10 個溜めたら自動で納品しに行く。
        //
        // **Enabled は送らない。**
        // FateUtils の Flag は { Enabled, Disabled } で Enabled が 0 番、
        // つまりその enum の既定値。BMR は既定値への一時方針を受け付けず、
        // 毎回「設定できませんでした」と返す（着地のたびに警告が出ていた）。
        //
        // 既定値なので、送らなければ Enabled のまま。切りたいときだけ送る。
        if (!cfg.FateCollectEnabled)
        {
            TrySet(ModuleFateUtils, TrackHandin, OptionDisabled);
        }

        // 地面に落ちているアイテムは拾わない。
        //
        // この選択肢は「拾うかどうか」ではなく
        // 「戦闘の代わりに拾いに行くか」を決めるもの。
        // 討伐で進めたいので必ず Disabled にする。
        TrySet(ModuleFateUtils, TrackCollect, OptionDisabled);

        // **レベルシンクを入れさせる。**
        //
        // ゲームが自動でかけるものだと思っていたが、実際は
        // 「LEVEL SYNC」を押す必要がある。押さないと FATE に
        // 参加した扱いにならず、敵も狙わない（2026-09-25 実測）。
        //
        // BMR 側は IsSyncedToFate を見て、入っていなければ
        // FateManager.LevelSync() を 0.5 秒ごとに呼ぶ。
        TrySet(ModuleFateUtils, TrackSync, OptionSyncEnable);

        // バディの面倒は BuddyService が見る。二重に動かさない。
        // BMR 側は在庫があるかしか見ず、切れたときに買いに行かない。
        TrySet(ModuleFateUtils, TrackChocobo, OptionDisabled);

        // FATE 内の敵を優先して狙う。
        TrySet(ModuleAutoTarget, TrackFate, OptionEnabled);

        // **こちらが置いた的を、BMR に変えさせない。**
        //
        // 狙う相手は FateTargetService が決めている。BMR に選び直させると、
        // FATE 外の敵（敵視を持っているだけのもの）へ移ることがある。
        // AIHintsBuilder.cs:148 が敵視のある敵を FateId に関係なく
        // 優先度 0 にするため、Everything=Disabled でも防げない。
        //
        // Never は Execute の冒頭近くで戻り、ForcedTarget を書かない
        // （AutoTarget.cs:236-237）。優先度の計算だけは行うので、
        // ジョブのモジュールは狙っている相手に技を撃てる。
        TrySet(ModuleAutoTarget, TrackRetarget, OptionNever);

        void TrySet(string module, string track, string value)
        {
            if (this.bossMod.TryAddTransientStrategy(preset, module, track, value, out var ok) && ok)
            {
                return;
            }

            // 失敗しても周回は続ける。BMR の版によって
            // トラック名が変わっている可能性があるため、記録だけ残す。
            this.anomalyLog.Warn("Fate", $"BossMod Reborn の方針を設定できませんでした: {module}.{track} = {value}");
        }
    }

    /// <summary>
    /// 戦闘プリセットを解除する。
    ///
    /// <b>離脱のたびに必ず呼ぶ。</b>解除しないと BossMod が敵を追い続け、
    /// その場から離れられない。
    /// </summary>
    private void ReleaseCombat()
    {
        // **ターゲットを外すのは、プリセットの有無に関わらず行う。**
        //
        // 以前はここで presetApplied を見て早期に戻っていた。
        // プリセットを入れる前に離脱すると、ターゲットが残ったままになる。
        //
        // <b>ターゲットが残ると脱出できない。</b>
        // 残っているとオートアタックが続き、戦闘中はマウントに乗れないので、
        // 「100% になったら飛んで離れる」が成立しない。
        //
        // BMR 側にターゲットを外す手立ては無い。Plugin.SetTarget は
        // null を渡されると何もせずに戻る（Plugin.cs:432-437）。
        // つまり自分で外すしかない。
        this.targets.ReleaseTarget();
        this.targets.StopAutoAttack();

        // 自分が入れたぶんだけ戻す。利用者が自分で入れていたものは触らない。
        rotation.Release("F.A.T.E 周回の終了");

        if (!this.presetApplied)
        {
            return;
        }

        // **BMR の AI も止める。**
        //
        // こちらは AI を使わない方針だが、以前の版や利用者の操作で
        // 入っていることがある。入ったままだと、プリセットを外しても
        // BMR が敵を追って動き続け、止めたのに動いて見える。
        this.bossMod.TrySetAiEnabled(false);

        this.bossMod.TryClearActivePreset(out _);

        // 一時方針はまとめて外す。移動を止めていた分（NormalMovement）もここで戻る。
        if (!string.IsNullOrEmpty(this.appliedPresetName))
        {
            this.bossMod.TryClearTransientPresetStrategies(this.appliedPresetName, out _);
        }

        this.presetApplied = false;
        this.appliedPresetName = string.Empty;
        this.movementParked = false;
        this.approaching = false;

        // 納品の段階も畳む。次の FATE へ持ち込むと、
        // 所持数の比較の起点が前の FATE のものになってしまう。
        this.handingIn = false;
        this.handInAttempts = 0;
        this.handInHeldAtStart = 0;

        // プリセットごと外したので、抑止の記録も捨てる。
        // 外す先が無いのに戻そうとしても警告が出るだけ。
        this.handInParked = false;
        this.lastTargetId = 0;
    }

    /// <summary>
    /// 跳ぶ。段差や small な引っかかりは、これだけで外れる。
    ///
    /// 飛んでいる最中は跳べないので何もしない。
    /// ICE も詰まったときの手当てとして同じことをしている
    /// （逆コンパイルして確認。CheckIfIsStuck の JumpIfStuck）。
    /// </summary>
    private static unsafe void TryJump()
    {
        try
        {
            if (MountService.IsFlying || Svc.Condition[ConditionFlag.Jumping] || Svc.Condition[ConditionFlag.Jumping61])
            {
                return;
            }

            var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            if (am is null)
            {
                return;
            }

            // GeneralAction 2 が「ジャンプ」。
            if (am->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2) == 0)
            {
                am->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2);
            }
        }
        catch
        {
            // 跳べなくても進行は止めない。
        }
    }

    /// <summary>
    /// 戦闘不能の画面からホームポイントへ戻る。
    ///
    /// <b>倒れている間は帰還（アクション 6）を撃てない。</b>戦闘不能の画面（DeathScreen）の
    /// 「戻る」を押す（callback 1。bozjalone の DeathHelper.ReturnButtonValue と同じ値）。
    ///
    /// <b>押すと確認（SelectYesno）が出る。</b>「はい」を押さないと戻らない。
    /// bozjalone の DeathHandler は確認があれば先に承諾し、無ければ「戻る」を押す。
    /// ここでも同じ順にする。確認を押さずに「戻る」だけを押し続けると、
    /// 確認が開いたまま倒れ続ける。
    ///
    /// <b>押してよい確認は、帰還の担当（AgentReturn）が開いたものだけ。</b>
    /// 別の処理が出した はい/いいえ を承諾しない。担当の見分け方は
    /// bundleoftweaks の InstantReturn と同じ（agent の AddonId と窓の Id を比べる）。
    /// </summary>
    /// <summary>
    /// いま誰かがレイズをかけてくれているか。
    ///
    /// <see cref="FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRevive"/> が
    /// かけ手の ID を持っている。0 でなければ詠唱が飛んできている。
    /// </summary>
    private static unsafe bool IsBeingRaised()
    {
        try
        {
            var revive = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRevive.Instance();
            return revive is not null && revive->ResurrectingPlayerId != 0;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe void ReturnAfterDeath()
    {
        if (!EzThrottler.Throttle("AutoCollector.FateDeathReturn", 3000)) return;

        // **帰還の窓は AgentRevive が出す。**
        //
        // 以前は "DeathScreen" という名前のアドオンを探していたが、
        // ゲームにその名前のアドオンは無い。したがって押す相手が見つからず、
        // 待ち時間が過ぎても何も起きないまま倒れ続けていた。
        //
        // 正しい手順は AutoDuty の DeathHelper と同じ:
        //   窓が出ていなければ AgentRevive.ShowAddon() で出す
        //   出ている SelectYesno を承諾する
        var revive = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRevive.Instance();

        var hasYesno =
            ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yesno) &&
            ECommons.GenericHelpers.IsAddonReady(yesno);

        if (hasYesno)
        {
            // **押してよい確認を見分ける。**
            //
            // 帰還の確認は AgentRevive か AgentReturn のどちらかが出す。
            // どちらでもないものは別の処理が出した確認なので触らない。
            var returnAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance()->GetAgentByInternalId(
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.Return);

            var mine = (revive is not null && revive->AddonId == yesno->Id) ||
                       (returnAgent is not null && returnAgent->AddonId == yesno->Id);

            if (mine)
            {
                ECommons.Automation.Callback.Fire(yesno, true, 0);
                return;
            }
        }

        if (revive is not null && !revive->IsAddonShown())
        {
            revive->ShowAddon();
        }
    }

    /// <summary>
    /// 帰還（デジョン）を撃つ。
    ///
    /// <b>ExecuteCommand(200, 8) では飛べない。</b>
    /// 以前はそれを使っていたが、記録を見ると座標が 1m も動いていなかった
    /// （2026-09-26 実測。12:52 の詰まり脱出も 18:20 のベンチャー回収も、
    ///  撃ったあと同じ座標に留まっていた。「帰還しました」と書いていたが
    ///  実際には何も起きていなかった）。
    ///
    /// 帰還は<b>詠唱のある通常アクション（ActionType.Action の 6 番）</b>。
    /// AutoDuty も同じものを使っている（AutoDuty.cs:1500-1509）。
    ///
    /// <b>撃てるか先に確かめる。</b>
    /// GetActionStatus が 0 以外なら、いまは撃てない（リキャスト中、
    /// 戦闘中、詠唱中など）。送っても弾かれるだけ。
    /// </summary>
    /// <returns>撃てたら true。</returns>
    private static unsafe bool ReturnHome()
    {
        try
        {
            var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            if (am is null)
            {
                return false;
            }

            // すでに詠唱している。重ねて撃たない。
            if (am->CastActionId == ReturnActionId)
            {
                return true;
            }

            if (am->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, ReturnActionId) != 0)
            {
                return false;
            }

            am->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, ReturnActionId);
            return true;
        }
        catch
        {
            // 失敗しても次のフレームで呼び直される。
            return false;
        }
    }

    /// <summary>帰還（デジョン）のアクション ID。</summary>
    private const uint ReturnActionId = 6;

    private void SetStep(FateStep step, string detail)
    {
        if (this.Step != step)
        {
            this.trace.Decision($"{this.Step} → {step}", detail);
            this.Step = step;
        }

        this.StatusDetail = detail;
    }
}
