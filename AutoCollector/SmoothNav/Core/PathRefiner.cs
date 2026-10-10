// SmoothNav（SmoothNav.Core/PathRefiner.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

// 公式の経路を整える部品。組み込み先の移動（MovementSession）が裏のスレッドで呼ぶ。
// 打ち切りの合図を受けたら、残りの区間を公式のまま返す。
public interface IPathRefiner
{
    CurveResult Refine(IReadOnlyList<Vector3> official, bool fly, CancellationToken cancel);
}

// CurveBuilder で整える。窓口は要求ごとに作る（窓口の中の覚えを要求の間・スレッドの間で共有しない）。
// flightProbes：飛行の必須の窓口（打ち切りの合図を受け取る）。空なら飛行は整えない。ground：地上の歩ける面の窓口（地上の必須の窓口はこれだけ）。
public sealed class CurvePathRefiner(Func<CancellationToken, IReadOnlyList<ICollisionProbe>> flightProbes, Func<IGroundSurface?> ground,
    CurveSettings? settings = null) : IPathRefiner
{
    public CurveResult Refine(IReadOnlyList<Vector3> official, bool fly, CancellationToken cancel) =>
        fly ? CurveBuilder.Build(official, true, flightProbes(cancel), settings, null, cancel)
            : CurveBuilder.Build(official, false, [], settings, ground(), cancel);
}
