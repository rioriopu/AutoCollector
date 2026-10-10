// SmoothNav（SmoothNav.Core/PathMarker.cs・4548808）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace SmoothNav.Core;

public static class PathMarker
{
    // 要求IDから同じ規則で0.01m以内の印を選ぶ。暗号学的な所有証明ではない。
    public static Vector3 For(Vector3 destination, string requestId)
    {
        uint hash = 2166136261;
        foreach (var ch in requestId) hash = (hash ^ ch) * 16777619;
        var angle = (hash % 65536) * (2 * MathF.PI / 65536);
        return destination + new Vector3(MathF.Cos(angle) * .006f, 0, MathF.Sin(angle) * .006f);
    }
}
