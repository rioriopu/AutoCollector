using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using SmoothNav.Core;
using SmoothNav.Dalamud;

namespace AutoCollector.Automation;

/// <summary>
/// 移動の角を曲線に整える部品（SmoothNav の写し・<c>SmoothNav/</c>）の入れ物。
///
/// vnavmesh の経路は角で向きを急に変える。ここで作る <see cref="RoutePlanner"/> は、
/// vnavmesh に頼んだ経路の角を曲線にして、人が歩くように角を回る経路を返す。
/// 整えられない・間に合わない・経路が無いときは公式の経路を返す（今までと同じ動きより悪くしない）。
///
/// 新しく作った曲線の線は、地上は vnavmesh の点の照会（歩ける面から外れないか）、
/// 飛行はゲームの当たり判定（地形や物に当たらないか）で確かめてから使う。
/// ゲームの当たり判定は画面のスレッドでしか呼べないため、待ち行列に積み、毎フレーム 1 ミリ秒まで処理する。
///
/// 移動の部品（<see cref="NavigationService"/>）は 4 つあるが、待ち行列と整える部品は 1 つを共有する。
/// </summary>
public sealed class SmoothMoveService : IDisposable
{
    private static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(1);

    private readonly FrameworkQueue queue = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly IPathRefiner refiner;
    private bool disposed;

    public SmoothMoveService()
    {
        var collision = new GameCollisionProbe(() => Svc.Framework.IsInFrameworkUpdateThread);
        var points = new MeshPoints();

        // 飛行の窓口は要求ごとに作る（取り消しの合図を受け取るため）。設定で切ってあれば窓口を渡さない＝飛行は整えない。
        this.refiner = new CurvePathRefiner(
            cancel => Plugin.C.SmoothFlight
                ? [new QueuedProbe(collision, this.queue, TimeSpan.FromSeconds(0.5), cancel)]
                : [],
            () => new MeshPointGround(points));
        Svc.Framework.Update += this.OnUpdate;
    }

    /// <summary>設定で曲線にする移動を有効にしているか。</summary>
    public bool Enabled => Plugin.C.SmoothMovement && !this.disposed;

    /// <summary>経路を整える部品の時計（秒）。<see cref="RoutePlanner"/> へ渡す時刻はこれを使う。</summary>
    public double Now => this.clock.Elapsed.TotalSeconds;

    /// <summary>移動の部品ごとに 1 つ作る。整える部品と待ち行列は共有する。</summary>
    public RoutePlanner CreatePlanner() => new(this.refiner);

    /// <summary>記録に残す 1 行。どう整えたか（整えた・公式・途中から乗り換え）と理由と時間。</summary>
    public static string Describe(RoutePlan plan)
    {
        var changed = plan.Refined?.Decisions.Count(d => d.Changed) ?? 0;
        var kind = plan.Fallback ? "vnavmesh 任せへ戻す" : plan.Splice ? "途中から曲線へ乗り換え" : plan.Smoothed ? "曲線に整えた経路" : "公式の経路";
        return $"{kind}（{plan.Reason}・変えた区間 {changed}・点 {plan.Waypoints.Count}・{plan.Seconds:F2} 秒）";
    }

    private void OnUpdate(IFramework framework)
    {
        if (this.disposed)
        {
            return;
        }

        this.queue.Pump(FrameBudget);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        Svc.Framework.Update -= this.OnUpdate;

        // 待っている照会は捨てる（待っている側は「分からない」として公式の経路を使う）。
        this.queue.Abandon();
    }

    /// <summary>
    /// vnavmesh の点の照会（Query.Mesh.NearestPoint・IPCProvider.cs で登録）。
    /// 整える処理は裏のスレッドで動くので、IPC を直接呼ぶ（点の照会は地図を読むだけ）。
    /// 呼べない（vnavmesh が無い・地図が無い）ときは「面が無い」＝曲線にしない。
    /// </summary>
    private sealed class MeshPoints : IMeshPointQuery
    {
        public Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY)
        {
            try
            {
                return Svc.PluginInterface
                    .GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint")
                    .InvokeFunc(point, halfExtentXZ, halfExtentY);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
