using System;
using System.Numerics;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace AutoCollector.Automation;

public enum MoveStatus
{
    /// <summary>移動中。</summary>
    Moving,

    /// <summary>到着した。</summary>
    Arrived,

    /// <summary>同じ場所から動いていない。</summary>
    Stuck,

    /// <summary>失敗した。エリアが変わった、vnavmesh が使えない等。</summary>
    Failed,
}

/// <summary>
/// vnavmesh を使ったエリア内の移動。
///
/// vnavmesh は経路探索に失敗しても例外を握り潰してログを出すだけで、
/// 経路が空のまま終わる。つまり「移動失敗」と「移動完了」が IPC の状態としては同じになる。
/// そのため距離とタイムアウトの確認が必須になる。
///
/// また vnavmesh 側の設定でスタック時に自動再試行が働くため、
/// 走行フラグは false と true を往復する。1 回 false を見ただけで完了と判定してはいけない。
/// </summary>
public sealed class NavigationService(AnomalyLog anomalyLog, VnavmeshIpc vnavmesh)
{
    /// <summary>到着したと認める前に、条件を満たし続ける必要のあるフレーム数。</summary>
    private const int RequiredStableFrames = 3;

    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(15);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly VnavmeshIpc vnavmesh = vnavmesh;

    private bool moveIssued;
    private int stableFrames;
    private uint startTerritory;
    private Vector3 lastPosition;
    private DateTime lastMovementUtc;
    private bool retriedAfterStuck;

    public bool IsAvailable => this.vnavmesh.IsLoaded;

    /// <summary>
    /// 目的地をナビメッシュ上の床へスナップする。
    /// 配置ファイル由来の座標はメッシュに乗っていないことがあり、経路探索が失敗しやすい。
    /// </summary>
    public bool TrySnapToFloor(Vector3 position, out Vector3 snapped)
    {
        snapped = position;

        if (!this.vnavmesh.TryPointOnFloor(position, out var result) || result is null)
        {
            return false;
        }

        // 大きく離れた場所を返された場合は採用しない。別の階層の床を拾うことがある。
        if (Vector3.Distance(result.Value, position) > 10f)
        {
            return false;
        }

        snapped = result.Value;
        return true;
    }

    /// <summary>移動を開始する。1 回だけ発行し、以降は状態を監視するだけにする。</summary>
    public bool BeginMove(Vector3 destination, float range, out string failureReason)
    {
        this.moveIssued = false;
        this.stableFrames = 0;
        this.retriedAfterStuck = false;
        this.startTerritory = Svc.ClientState.TerritoryType;
        this.lastPosition = Player.Available ? Player.Position : default;
        this.lastMovementUtc = DateTime.UtcNow;

        if (!this.vnavmesh.IsLoaded)
        {
            failureReason = "vnavmesh が導入されていないため移動できません";
            return false;
        }

        if (!this.vnavmesh.TryIsReady(out var ready))
        {
            failureReason = "vnavmesh の状態を取得できません";
            return false;
        }

        if (!ready)
        {
            failureReason = "このエリアのナビメッシュがまだ利用できません";
            return false;
        }

        if (!this.vnavmesh.TryMoveCloseTo(destination, false, range, out var accepted))
        {
            failureReason = "vnavmesh へ移動を依頼できませんでした";
            return false;
        }

        if (!accepted)
        {
            failureReason = "vnavmesh が別の経路探索を実行中です";
            return false;
        }

        this.moveIssued = true;
        failureReason = string.Empty;
        return true;
    }

    /// <summary>毎フレーム呼ぶ。到着・スタック・失敗を判定する。</summary>
    public MoveStatus Tick(Vector3 destination, float range)
    {
        if (!this.moveIssued)
        {
            return MoveStatus.Failed;
        }

        if (!Player.Available)
        {
            return MoveStatus.Moving;
        }

        // エリアが変わったら経路は破棄されている。続行してはいけない。
        if (Svc.ClientState.TerritoryType != this.startTerritory)
        {
            this.anomalyLog.Warn("Navigation", "移動中にエリアが変わりました");
            return MoveStatus.Failed;
        }

        var position = Player.Position;

        // 走行状態を 3 つとも見る。1 つでも進行中なら移動継続とみなす。
        if (!this.vnavmesh.TryPathIsRunning(out var running) ||
            !this.vnavmesh.TryNavPathfindInProgress(out var navPathfinding) ||
            !this.vnavmesh.TrySimpleMovePathfindInProgress(out var simplePathfinding))
        {
            return MoveStatus.Failed;
        }

        var idle = !running && !navPathfinding && !simplePathfinding;
        var distance = Vector3.Distance(position, destination);

        if (idle && distance <= range + 2f)
        {
            this.stableFrames++;
            if (this.stableFrames >= RequiredStableFrames)
            {
                return MoveStatus.Arrived;
            }

            return MoveStatus.Moving;
        }

        this.stableFrames = 0;

        // スタック判定。座標がほとんど動いていない状態が続いたら 1 度だけ再発行する。
        if (Vector3.DistanceSquared(position, this.lastPosition) > 0.01f)
        {
            this.lastPosition = position;
            this.lastMovementUtc = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - this.lastMovementUtc > StuckWindow)
        {
            if (this.retriedAfterStuck)
            {
                return MoveStatus.Stuck;
            }

            this.anomalyLog.Warn("Navigation", "移動が止まったため、経路を引き直します");
            this.retriedAfterStuck = true;
            this.lastMovementUtc = DateTime.UtcNow;

            this.vnavmesh.TryStop();
            if (!this.vnavmesh.TryMoveCloseTo(destination, false, range, out var accepted) || !accepted)
            {
                return MoveStatus.Stuck;
            }
        }

        return MoveStatus.Moving;
    }

    /// <summary>移動を止める。自分が開始していない場合は何もしない。</summary>
    public void Stop()
    {
        if (!this.moveIssued)
        {
            return;
        }

        this.vnavmesh.TryStop();
        this.moveIssued = false;
    }
}
