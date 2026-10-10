// SmoothNav（SmoothNav.Dalamud/GameCollisionProbe.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using SmoothNav.Core;

namespace SmoothNav.Dalamud;

// ゲームの当たり判定（BGCollision。地形と置き物の両方）で飛行の区間を照会する窓口。画面のスレッドでしか呼べないので、
// 整える処理（裏のスレッド）からは QueuedProbe を通して呼ぶ。
// 照会の形はゲームの外の窓口と同じ：足元 +1m から中心と上下左右 0.5m の 5 本（CollisionMeasurement.BodyOffsets）。
// 飛んで抜けられる材質（水面など。MaterialRules.FlyThrough）は越えて照会を続ける。
// 区間の下の地形・周りの物が読み込み中なら、「当たり無し」を通れる証拠にしない（Unknown）。
// 照会の手順は、改造版 vnavmesh の飛行の安全化で自作し、ゲームで動かしていたもの（2026-10-08）を写した。
public sealed unsafe class GameCollisionProbe(Func<bool> onFrameworkThread, ulong layerMask = 1, int maxPassThrough = 4) : ICollisionProbe
{
    public string Name => "ゲームの当たり判定（中心＋周囲4本）";

    public ProbeResult Segment(Vector3 from, Vector3 to, bool fly)
    {
        if (!fly) return new(Verdict.Unknown, "飛行専用の検査");
        if (!Metrics.Finite(from) || !Metrics.Finite(to)) return new(Verdict.Unknown, "座標が有限でない");
        var delta = to - from; var length = delta.Length();
        if (length < 1e-3f) return new(Verdict.Clear);
        var direction = delta / length;
        if (!Preconditions(out var scene, out var why)) return new(Verdict.Unknown, why);
        try
        {
            var pieces = CollectPieces(scene); var pending = CollectPending(scene);
            if (!LoadedCoverage.AllLoaded(pieces, pending, from + Vector3.UnitY, to + Vector3.UnitY, 1.5f, out var notLoaded))
                return new(Verdict.Unknown, notLoaded);
            ProbeResult? best = null;
            foreach (var offset in CollisionMeasurement.BodyOffsets(direction))
            {
                var hit = CastFiltered(scene, from + offset, direction, length);
                if (hit.Verdict == Verdict.Unknown) return hit;
                if (hit.Verdict == Verdict.Blocked && (best == null || hit.Distance < best.Distance)) best = hit;
            }
            return best ?? new(Verdict.Clear);
        }
        catch (Exception ex) { return new(Verdict.Unknown, "当たり判定の照会の例外: " + ex.GetType().Name); }
    }

    // 照会の前提：画面のスレッド・当たり判定の場面がある・終了処理中でない
    private bool Preconditions(out SceneWrapper* scene, out string why)
    {
        scene = null;
        if (!onFrameworkThread()) { why = "画面のスレッドの外から呼ばれた"; return false; }
        var framework = Framework.Instance();
        if (framework == null) { why = "Framework が無い"; return false; }
        var module = framework->BGCollisionModule;
        if (module == null) { why = "BGCollisionModule が無い"; return false; }
        if (module->ShuttingDown) { why = "当たり判定の終了処理中"; return false; }
        var manager = module->SceneManager;
        if (manager == null || manager->NumScenes <= 0 || manager->FirstScene == null) { why = "当たり判定の場面が無い"; return false; }
        scene = manager->FirstScene; why = "";
        return true;
    }

    private ProbeResult CastFiltered(SceneWrapper* scene, Vector3 origin, Vector3 direction, float length)
    {
        var start = origin; var traveled = 0f; var remaining = length;
        for (var i = 0; i <= maxPassThrough; i++)
        {
            if (!Cast(scene, start, direction, remaining, out var hit)) return new(Verdict.Clear);
            if (!MaterialRules.FlyThrough(hit.Material))
                return new(Verdict.Blocked, $"当たり判定の面と交差（材質 {hit.Material:X}）", traveled + hit.Distance, hit.Point, Normal(hit, direction), hit.Material);
            var advance = hit.Distance + .05f;
            traveled += advance; remaining -= advance; start += direction * advance;
            if (remaining <= 0) return new(Verdict.Clear);
        }
        return new(Verdict.Unknown, $"飛んで抜けられる面が {maxPassThrough} 枚より多い");
    }

    private bool Cast(SceneWrapper* scene, Vector3 start, Vector3 direction, float maxDistance, out RaycastHit hit)
    {
        RaycastHit h = default;
        var origin = new Vector4(start, 0); var d = direction; var distance = maxDistance;
        var filter = new RaycastMaterialFilter();
        var args = new RaycastParams { Algorithm = 0, Origin = &origin, Direction = &d, MaxDistance = &distance, MaterialFilter = &filter };
        var ok = scene->Raycast(&h, layerMask, &args);
        hit = h;
        return ok;
    }

    // RaycastHit.Normal は埋まらないことがあるので、短ければ三角形から求め、照会の向きに逆らう側へ向ける。
    private static Vector3 Normal(in RaycastHit hit, Vector3 direction)
    {
        var n = hit.Normal;
        if (n.Length() < .5f || float.IsNaN(n.X))
        {
            n = Vector3.Cross(hit.V2 - hit.V1, hit.V3 - hit.V1);
            var len = n.Length();
            if (len < 1e-6f || float.IsNaN(len)) return default;
            n /= len;
        }
        else n = Vector3.Normalize(n);
        return Vector3.Dot(n, direction) > 0 ? -n : n;
    }

    // 地形の分割読み込みの部品。部品の一覧が壊れている（エリア切替中）ときは、範囲全体を未読み込みとして返す。
    private static List<StreamedPiece> CollectPieces(SceneWrapper* scene)
    {
        var list = new List<StreamedPiece>();
        try
        {
            if (scene == null || scene->Scene == null) return list;
            foreach (var collider in scene->Scene->Colliders)
            {
                if (collider == null || collider->GetColliderType() != ColliderType.Streamed) continue;
                var s = (ColliderStreamed*)collider;
                if (!s->Loaded || s->Header == null || s->Elements == null || s->Header->NumMeshes is < 0 or > 100000)
                {
                    list.Add(new(s->StreamedMinX, s->StreamedMinZ, s->StreamedMaxX, s->StreamedMaxZ, false, -1));
                    continue;
                }
                for (var i = 0; i < s->Header->NumMeshes; i++)
                {
                    var e = s->Elements + i;
                    list.Add(new(e->MinX, e->MinZ, e->MaxX, e->MaxZ, e->Mesh != null && e->Mesh->Loaded, e->MeshId));
                }
            }
        }
        catch (NullReferenceException) { return [new(-1e9f, -1e9f, 1e9f, 1e9f, false, -1)]; }
        return list;
    }
    // 読み込みが終わっていない地形以外の物（範囲の分かるもの）。範囲の分からないものは数えない。
    private static List<PendingCollider> CollectPending(SceneWrapper* scene)
    {
        var list = new List<PendingCollider>();
        try
        {
            if (scene == null || scene->Scene == null) return list;
            foreach (var collider in scene->Scene->Colliders)
            {
                if (collider == null || collider->GetColliderType() != ColliderType.Mesh) continue;
                var m = (ColliderMesh*)collider;
                if (m->Loaded && !collider->LoadInProgress()) continue;
                var bb = m->WorldBoundingBox;
                if (bb.Max.X > bb.Min.X && bb.Max.Y > bb.Min.Y && bb.Max.Z > bb.Min.Z && float.IsFinite(bb.Min.X + bb.Min.Y + bb.Min.Z + bb.Max.X + bb.Max.Y + bb.Max.Z))
                    list.Add(new(bb.Min, bb.Max));
            }
        }
        catch (NullReferenceException) { return [new(new(-1e9f), new(1e9f))]; }
        return list;
    }
}
