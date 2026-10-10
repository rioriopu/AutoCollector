// SmoothNav（SmoothNav.Core/LoadedCoverage.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

// 地形の分割読み込みの1部品（水平の範囲と、読み込み済みか）。ゲームの ColliderStreamed の部品から作る。
public readonly record struct StreamedPiece(float MinX, float MinZ, float MaxX, float MaxZ, bool Loaded, int MeshId);
// 読み込みが終わっていない、地形以外の当たり判定の物（ColliderMesh）の範囲。
public readonly record struct PendingCollider(Vector3 Min, Vector3 Max);

// 照会する区間の下の地形・区間の周りの物が読み込まれているか。読み込まれていない所では、ゲームの当たり判定が
// 「当たり無し」と答えても通れる証拠にならないので、照会の答えを Unknown にする（整えずに公式の区間を使う）。
public static class LoadedCoverage
{
    public static bool AllLoaded(IEnumerable<StreamedPiece> pieces, IEnumerable<PendingCollider> pending, Vector3 a, Vector3 b, float margin, out string reason)
    {
        var min = Vector3.Min(a, b) - new Vector3(margin);
        var max = Vector3.Max(a, b) + new Vector3(margin);
        foreach (var p in pieces)
        {
            if (p.Loaded || p.MaxX < min.X || p.MinX > max.X || p.MaxZ < min.Z || p.MinZ > max.Z) continue;
            reason = p.MeshId >= 0 ? $"区間の下の地形メッシュ tr{p.MeshId:d4} がまだ読み込まれていない" : "区間の下の地形がまだ読み込まれていない";
            return false;
        }
        foreach (var o in pending)
        {
            if (o.Max.X < min.X || o.Min.X > max.X || o.Max.Y < min.Y || o.Min.Y > max.Y || o.Max.Z < min.Z || o.Min.Z > max.Z) continue;
            reason = "区間の周りの当たり判定の物がまだ読み込まれていない";
            return false;
        }
        reason = "";
        return true;
    }
}
