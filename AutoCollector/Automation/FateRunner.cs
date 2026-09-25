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
    AetheryteService aetherytes)
{
    /// <summary>FATE の円へ入ったとみなす距離の余裕。</summary>
    private const float FateArrivalSlack = 5f;

    /// <summary>移動が進まないまま経過したら、その FATE を諦める。</summary>
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>同じ FATE で詰まってよい回数。超えたら二度と狙わない。</summary>
    private const int MaxStuckPerFate = 2;

    /// <summary>テレポートが終わるのを待つ上限。</summary>
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(60);

    /// <summary>降りるのを待つ上限。空からの降下は数秒かかる。</summary>
    private static readonly TimeSpan LandingTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>このセッションで詰まった FATE。もう狙わない。</summary>
    private readonly HashSet<ushort> blacklist = [];

    /// <summary>FATE ごとの詰まり回数。</summary>
    private readonly Dictionary<ushort, int> stuckCounts = [];

    /// <summary>いま狙っている FATE。</summary>
    private FateInfo? target;

    /// <summary>次に狙う FATE。達成度が閾値を超えた時点で決めておく。</summary>
    private FateInfo? prefetched;

    /// <summary>報酬待ちの納品 FATE。着地するまでマップを離れない。</summary>
    private (ushort Id, int Start, DateTime DeadlineUtc)? pendingReward;

    private DateTime moveStartedUtc = DateTime.MinValue;
    private DateTime teleportStartedUtc = DateTime.MinValue;
    private DateTime waitingSinceUtc = DateTime.MinValue;
    private DateTime deadSinceUtc = DateTime.MinValue;

    /// <summary>降り始めた時刻。降りられないまま続くのを打ち切るのに使う。</summary>
    private DateTime landingSinceUtc = DateTime.MinValue;
    private uint travelTargetTerritory;
    private int zoneIndex;
    /// <summary>いまの目的地へ経路を引いたか。乗ってから引くので旗で覚える。</summary>
    private bool moveIssued;

    /// <summary>経路を引いたとき飛んでいたか。地面に触れて解けたのを見分ける。</summary>
    private bool flyingWhenIssued;

    /// <summary>交換から戻ったときに向かう座標。設定で座標まで戻す場合だけ入る。</summary>
    private Vector3? resumePosition;

    private bool presetApplied;
    private string appliedPresetName = string.Empty;

    /// <summary>いまの段階。</summary>
    public FateStep Step { get; private set; } = FateStep.Idle;

    /// <summary>画面に出す短い説明。</summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>完了した FATE の数。</summary>
    public int Completed { get; private set; }

    /// <summary>動いているか。</summary>
    public bool IsRunning => this.Step is not (FateStep.Idle or FateStep.Done or FateStep.Error);

    /// <summary>止めた理由。</summary>
    public string? StoppedReason { get; private set; }

    private static Config C => Plugin.C;

    /// <summary>周回を始める。始められない場合は理由を返す。</summary>
    public bool Start(out string reason)
    {
        if (this.IsRunning)
        {
            reason = "すでに動いています";
            return false;
        }

        var cfg = C;

        if (cfg.FateZones.Count == 0)
        {
            reason = "周回するマップが選ばれていません";
            return false;
        }

        if (!this.bossMod.IsLoaded)
        {
            reason = "BossMod Reborn が導入されていません";
            return false;
        }

        if (!this.navigation.IsAvailable)
        {
            reason = "vnavmesh が導入されていません";
            return false;
        }

        // 利用者がプリセット名を指定していなければ、こちらで用意する。
        // どれを選べばよいか分からないのが普通なので、名前を知らなくても動くようにする。
        if (string.IsNullOrWhiteSpace(cfg.FateCombatPreset)
            && !FateCombatPreset.Ensure(this.bossMod, this.anomalyLog))
        {
            reason = $"BossMod Reborn にプリセット「{FateCombatPreset.Name}」を用意できませんでした";
            return false;
        }

        this.blacklist.Clear();
        this.stuckCounts.Clear();
        this.target = null;
        this.prefetched = null;
        this.pendingReward = null;
        this.Completed = 0;
        this.StoppedReason = null;
        this.presetApplied = false;
        this.appliedPresetName = string.Empty;
        this.buddy.Reset();

        // いまいるマップが選択に含まれていれば、そこから始める。
        var here = Svc.ClientState.TerritoryType;
        var index = cfg.FateZones.IndexOf(here);
        this.zoneIndex = index >= 0 ? index : 0;

        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
        this.anomalyLog.Info("Fate", $"FATE 周回を開始しました（マップ {cfg.FateZones.Count} 件）");

        reason = string.Empty;
        return true;
    }

    /// <summary>周回を止める。戦闘とプリセットを必ず元に戻す。</summary>
    public void Stop(string reason)
    {
        // **解除は状態を見ずに先に行う。**
        //
        // 止まっている状態でも、プリセットを有効にしたまま何らかの理由で
        // 段階だけが進んでいることがありうる。そのまま戻ると BMR が
        // 動き続けてしまい、利用者からは「止めたのに戦い続ける」に見える。
        // ReleaseCombat は適用していなければ即座に戻るので、余分な害は無い。
        this.ReleaseCombat();

        if (this.Step is FateStep.Idle or FateStep.Done)
        {
            return;
        }

        this.navigation.Stop();

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
    public bool HasPendingReward => this.pendingReward is not null;

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
        var index = Plugin.C.FateZones.IndexOf(territoryId);
        if (index >= 0)
        {
            this.zoneIndex = index;
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

        if (this.Step == FateStep.Dead)
        {
            // 生き返った。
            this.deadSinceUtc = DateTime.MinValue;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
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
        if (!cfg.FateZones.Contains(Svc.ClientState.TerritoryType))
        {
            this.TickTraveling(cfg);
            return;
        }

        // マップに着いたので、移動状態は畳む。
        if (this.Step == FateStep.Traveling)
        {
            this.teleportStartedUtc = DateTime.MinValue;
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

        // **降りている最中はここで完結させる。**
        // この下には vnavmesh を触る処理がある。降下中に触ると、
        // ゲームの降下がプレイヤーの操作として読まれ、経路が捨てられる。
        if (this.Step == FateStep.Landing)
        {
            this.TickLanding(cfg);
            return;
        }

        // 5. いま参加している FATE があるか。
        var current = this.scanner.GetCurrent();
        if (current is not null && current.State == FateState.Running)
        {
            this.TickInFate(cfg, current);
            return;
        }

        // 参加していないのに Fighting のままなら、FATE が終わったか離れた。
        if (this.Step is FateStep.Fighting or FateStep.Leaving)
        {
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
            var waited = DateTime.UtcNow - this.deadSinceUtc;
            var remaining = cfg.FateRaiseWaitSeconds - (int)waited.TotalSeconds;
            if (remaining > 0)
            {
                this.StatusDetail = $"レイズを待っています（残り {remaining} 秒）";
                return;
            }

            this.anomalyLog.Info("Fate", $"{cfg.FateRaiseWaitSeconds} 秒待ちましたがレイズされませんでした。街へ戻ります");
        }

        this.StatusDetail = "街へ戻っています";
        ReturnHome();
    }

    private void TickTraveling(Config cfg)
    {
        var destination = cfg.FateZones[Math.Clamp(this.zoneIndex, 0, cfg.FateZones.Count - 1)];

        // 納品 FATE の報酬を待っている間はマップを離れない。離れると報酬が消える。
        if (this.pendingReward is not null)
        {
            this.StatusDetail = "納品 FATE の報酬を待っています";
            return;
        }

        if (this.Step != FateStep.Traveling || this.travelTargetTerritory != destination)
        {
            this.travelTargetTerritory = destination;
            this.teleportStartedUtc = DateTime.UtcNow;
            this.SetStep(FateStep.Traveling, $"{NpcLocationService.GetTerritoryName(destination)} へ移動しています");

            if (!this.TryTeleportTo(destination))
            {
                this.anomalyLog.Warn("Fate", $"{NpcLocationService.GetTerritoryName(destination)} へテレポートできませんでした");
                this.AdvanceZone(cfg);
            }

            return;
        }

        if (DateTime.UtcNow - this.teleportStartedUtc > TeleportTimeout)
        {
            this.anomalyLog.Warn("Fate", $"{NpcLocationService.GetTerritoryName(destination)} へのテレポートが終わりませんでした");
            this.teleportStartedUtc = DateTime.MinValue;
            this.AdvanceZone(cfg);
        }
    }

    private void TickSeek(Config cfg)
    {
        // 移動中なら続ける。
        if (this.Step == FateStep.MovingToFate && this.target is not null)
        {
            this.TickMoving(cfg);
            return;
        }

        var next = this.prefetched ?? this.PickNext(cfg);
        this.prefetched = null;

        if (next is null)
        {
            this.TickWaiting(cfg);
            return;
        }

        this.BeginMoveTo(next);
    }

    private void TickWaiting(Config cfg)
    {
        if (this.Step != FateStep.Waiting)
        {
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
        }

        // 報酬待ちの間はマップを離れない。
        if (this.pendingReward is not null)
        {
            this.StatusDetail = "納品 FATE の報酬を待っています";
            return;
        }

        if (!cfg.FateSwapZoneWhenEmpty || cfg.FateZones.Count <= 1)
        {
            this.StatusDetail = "FATE が湧くのを待っています";
            return;
        }

        var waited = (int)(DateTime.UtcNow - this.waitingSinceUtc).TotalSeconds;
        var remaining = cfg.FateZoneSwapWaitSeconds - waited;

        if (remaining > 0)
        {
            this.StatusDetail = $"FATE が湧くのを待っています（{remaining} 秒後に次のマップへ）";
            return;
        }

        this.AdvanceZone(cfg);
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
        this.target = fate;
        this.moveStartedUtc = DateTime.UtcNow;
        this.moveIssued = false;

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
        if (live is null || live.State != FateState.Running || live.Progress >= 100)
        {
            this.navigation.Stop();
            this.target = null;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        var range = Math.Max(1f, live.Radius - FateArrivalSlack);

        // **着いていたら、乗る判断より先に降りる。**
        //
        // 先に乗る判断をすると、着いた直後に降りて、また乗って、を繰り返す。
        //
        // 高さは見ない。飛んでいる最中は真上に数十メートルの差があり、
        // そのまま測ると「まだ遠い」と判断してしまう。
        var arrived = Player.Available
                   && Vector2.Distance(
                          new Vector2(Player.Position.X, Player.Position.Z),
                          new Vector2(live.Position.X, live.Position.Z)) <= range + FateArrivalSlack;

        if (!arrived)
        {
            // 先にマウントへ乗る。遠い FATE へ歩いて向かうと、着く前に終わる。
            // 飛べるエリアなら飛び上がるところまで面倒を見る。
            if (this.mount.TickPrepare(live.Position))
            {
                this.StatusDetail = $"{live.Name} へ向かう準備をしています";
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

            // 目的地を持ち上げる。これが離陸の条件（次の点が自分より高い）
            // を満たすことにもなり、経路が地面を擦るのも防ぐ。
            var destination = flying
                ? live.Position with { Y = live.Position.Y + MountService.FlightLift }
                : live.Position;
            var moveRange = flying ? MountService.FlightLift : range;

            if (!this.navigation.BeginMove(destination, moveRange, flying, out var failure))
            {
                this.anomalyLog.Warn("Fate", $"{live.Name} へ移動できませんでした: {failure}");
                this.MarkStuck(live.Id);
                this.target = null;
                return;
            }

            this.moveIssued = true;
            this.flyingWhenIssued = flying;
            this.moveStartedUtc = DateTime.UtcNow;
        }

        // **真上まで来たら、そこで飛行をやめて降りる。**
        //
        // 持ち上げた座標を目的地にしているので、そのままでは真上で止まる。
        // 水平の距離だけで見て、真上まで来たら降りる判断へ移る。
        if (this.flyingWhenIssued && Player.Available)
        {
            var flat = Vector2.Distance(
                new Vector2(Player.Position.X, Player.Position.Z),
                new Vector2(live.Position.X, live.Position.Z));

            if (flat <= range)
            {
                this.navigation.Stop();
                this.EnterFate(cfg, live);
                return;
            }
        }

        var status = this.navigation.Tick(
            this.flyingWhenIssued ? MountService.LiftForFlight(live.Position) : live.Position,
            this.flyingWhenIssued ? MountService.FlightLift : range);

        switch (status)
        {
            case MoveStatus.Arrived:
            case MoveStatus.ShortOfTarget:
                // 近くまで来た。圏内に入っていれば戦い始める。
                this.EnterFate(cfg, live);
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

                this.StatusDetail = $"{live.Name} へ向かっています（{live.Progress}%）";
                this.trace.State(
                    "移動中",
                    $"{live.Name} {live.Progress}% {FateTrace.DescribeDistance(live.Position)} " +
                    $"経路={(this.flyingWhenIssued ? "飛行" : "地上")}");
                return;
        }
    }

    /// <summary>
    /// FATE に着いた。降りる段階へ移る。
    ///
    /// <b>ここで経路を止め、以後 vnavmesh に触らない。</b>
    /// 降下はゲーム側の動きで、それがプレイヤーの操作として
    /// vnavmesh に読まれる。触り続けると経路を積んでは捨てるを
    /// 繰り返し、着地したあとも暴れて降りられない。
    /// </summary>
    private void EnterFate(Config cfg, FateInfo fate)
    {
        this.navigation.Stop();
        this.moveIssued = false;
        this.target = fate;

        // 乗っていなければ降りる必要がない。そのまま戦う。
        if (!MountService.IsMounted)
        {
            this.ApplyCombat(cfg);
            this.SetStep(FateStep.Fighting, $"{fate.Name} と戦っています");
            return;
        }

        this.landingSinceUtc = DateTime.UtcNow;
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
            this.SetStep(FateStep.Fighting, $"{fate.Name} と戦っています");
            return;
        }

        // 降りられないまま時間が過ぎた。
        if (DateTime.UtcNow - this.landingSinceUtc > LandingTimeout)
        {
            this.trace.Trouble("着地できない", $"{LandingTimeout.TotalSeconds:F0}秒たっても降りられませんでした");
            this.mount.ClearDismounting();
            this.MarkStuck(fate.Id);
            this.target = null;
            this.SetStep(FateStep.Waiting, "FATE を探しています");
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
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

        if (this.Step != FateStep.Fighting)
        {
            this.ApplyCombat(cfg);
            this.target = current;
            this.SetStep(FateStep.Fighting, $"{current.Name} と戦っています");
        }

        // 達成度が閾値を超えたら、次に向かう FATE を先に決めておく。
        // 100% を見てから探し始めると、その間その場に立ち尽くすことになる。
        if (this.prefetched is null && current.Progress >= cfg.FatePrefetchPct)
        {
            this.prefetched = this.PickNext(cfg, exclude: current.Id);
        }

        this.StatusDetail = $"{current.Name}（{current.Progress}%）";
        this.trace.State(
            "戦闘中",
            $"{current.Name} {current.Progress}% " +
            $"シンク={(this.scanner.IsPlayerSyncedToFate() ? "済" : "未")} " +
            $"プリセット={(this.presetApplied ? this.appliedPresetName : "未適用")}");
    }

    private void LeaveFate(Config cfg, FateInfo finished)
    {
        // 戦闘 AI を先に解除する。これを呼ばないと敵を追い続けて離れられない。
        this.ReleaseCombat();
        this.navigation.Stop();

        this.Completed++;

        // 納品 FATE は 100% の時点ではまだ報酬が入っていない。
        // 1 分後に FATE が消えるときに入る。それまでマップを離れない。
        if (finished.IsCollect)
        {
            this.pendingReward = (finished.Id, finished.StartTimeEpoch, DateTime.UtcNow + CollectRewardWindow);
            this.anomalyLog.Info("Fate", $"{finished.Name} が 100% になりました。報酬が入るまでこのマップに留まります");
        }
        else
        {
            this.anomalyLog.Info("Fate", $"{finished.Name} が 100% になりました（完了 {this.Completed} 件）");
        }

        this.target = null;
        this.SetStep(FateStep.Leaving, "次の FATE へ向かっています");

        // 先に決めてあった FATE があれば、そのまま動き出す。
        var next = this.prefetched ?? this.PickNext(cfg, exclude: finished.Id);
        this.prefetched = null;

        if (next is not null)
        {
            this.BeginMoveTo(next);
            return;
        }

        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
    }

    private void FinishCurrentFate()
    {
        this.ReleaseCombat();
        this.target = null;
        this.SetStep(FateStep.Waiting, "FATE を探しています");
        this.waitingSinceUtc = DateTime.UtcNow;
    }

    // ---- 部品 ----

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
            FateScanner.DefaultSortOrder);
    }

    private void MarkStuck(ushort fateId)
    {
        this.stuckCounts.TryGetValue(fateId, out var count);
        count++;
        this.stuckCounts[fateId] = count;

        if (count >= MaxStuckPerFate)
        {
            this.blacklist.Add(fateId);
            this.anomalyLog.Warn("Fate", $"FATE {fateId} は {count} 回続けて辿り着けなかったため、今回の周回では狙いません");
        }
    }

    private void AdvanceZone(Config cfg)
    {
        if (cfg.FateZones.Count <= 1)
        {
            this.waitingSinceUtc = DateTime.UtcNow;
            return;
        }

        this.zoneIndex = (this.zoneIndex + 1) % cfg.FateZones.Count;
        var next = cfg.FateZones[this.zoneIndex];

        // マップを移るので、そのマップ固有の記録は捨てる。
        this.blacklist.Clear();
        this.stuckCounts.Clear();
        this.target = null;
        this.prefetched = null;

        this.travelTargetTerritory = 0;
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

    /// <summary>納品 FATE の報酬が着地したかを見る。</summary>
    private void RefreshPendingReward()
    {
        if (this.pendingReward is not { } pending)
        {
            return;
        }

        var live = this.scanner.GetById(pending.Id);

        // FATE が消えた = 報酬が入った。
        if (live is null || live.StartTimeEpoch != pending.Start)
        {
            this.anomalyLog.Info("Fate", "納品 FATE の報酬が入りました");
            this.pendingReward = null;
            return;
        }

        // マップを離れてしまった。報酬は失われる。
        if (!C.FateZones.Contains(Svc.ClientState.TerritoryType) && this.Step == FateStep.Traveling)
        {
            this.anomalyLog.Warn("Fate", "報酬が入る前にマップを離れました。この納品 FATE の報酬は失われます");
            this.pendingReward = null;
            return;
        }

        if (DateTime.UtcNow > pending.DeadlineUtc)
        {
            this.anomalyLog.Warn("Fate", "納品 FATE の報酬を待ちましたが、確認できませんでした");
            this.pendingReward = null;
        }
    }

    // BMR のモジュール名。RotationModuleRegistry は「名前空間 + 型名」で引く
    // （BossMod.SourceGen の RuntimeFullName）。
    private const string ModuleFateUtils = "BossMod.Autorotation.MiscAI.FateUtils";
    private const string ModuleAutoTarget = "BossMod.Autorotation.MiscAI.AutoTarget";

    // トラック名と選択肢名は enum の名前そのもの
    // （RotationModule.Define が expectedIndex.ToString() を InternalName にする）。
    private const string TrackHandin = "Handin";
    private const string TrackCollect = "Collect";
    private const string TrackSync = "Sync";
    private const string TrackChocobo = "Chocobo";
    private const string TrackFate = "FATE";
    private const string OptionEnabled = "Enabled";
    private const string OptionDisabled = "Disabled";
    private const string OptionNone = "None";

    /// <summary>レベルシンクを入れる。FateSync.Enable の名前。</summary>
    private const string OptionSyncEnable = "Enable";

    /// <summary>
    /// 戦闘プリセットを有効にする。
    ///
    /// 設定が空なら、こちらで用意したプリセット（<see cref="FateCombatPreset"/>）を使う。
    /// 利用者が名前を知らなくても、FATE の敵だけを狙い、
    /// 絡まれたら反撃する設定で動き出せる。
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

        if (!this.bossMod.TrySetActivePreset(name, out var accepted) || !accepted)
        {
            this.anomalyLog.Warn("Fate", $"BossMod Reborn のプリセット「{name}」を有効にできませんでした");
            return;
        }

        this.presetApplied = true;
        this.appliedPresetName = name;
        this.ApplyFateStrategies(cfg, name);
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
        TrySet(ModuleFateUtils, TrackHandin, cfg.FateCollectEnabled ? OptionEnabled : OptionDisabled);

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
        if (!this.presetApplied)
        {
            return;
        }

        this.bossMod.TryClearActivePreset(out _);

        if (!string.IsNullOrEmpty(this.appliedPresetName))
        {
            this.bossMod.TryClearTransientPresetStrategies(this.appliedPresetName, out _);
        }

        this.presetApplied = false;
        this.appliedPresetName = string.Empty;
    }

    /// <summary>ホームポイントへ戻る（戦闘不能からの復帰）。</summary>
    private static unsafe void ReturnHome()
    {
        try
        {
            // ExecuteCommand 200 / param 8 が「帰還」。
            FFXIVClientStructs.FFXIV.Client.Game.GameMain.ExecuteCommand(200, 8, 0, 0, 0);
        }
        catch
        {
            // 失敗しても次のフレームで呼び直される。
        }
    }

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
