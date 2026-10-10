# SmoothNav の写し

移動の角を曲線に整える部品 SmoothNav（別リポジトリ）の写し。原本のコミットは `4548808`。

- 使い方は `Automation/SmoothMoveService.cs`（入れ物）と `Automation/NavigationService.cs`（移動）を見る。
- **ここのファイルは直接直さない。** 原本で直してから `python -X utf8 tools/sync_smoothnav.py` で写し直す
  （原本に未コミットの変更があると写さない）。写すときに、AutoCollector が暗黙に使わない using を足し、1 行目に原本を書く。

| 写し | 原本 | 原本の SHA256（先頭16文字・改行は LF） |
|---|---|---|
| Core/Collision.cs | SmoothNav.Core/Collision.cs | ba74bcab375d3c06 |
| Core/CurveBuilder.cs | SmoothNav.Core/CurveBuilder.cs | b181d1687eea0d64 |
| Core/FollowerSimulation.cs | SmoothNav.Core/FollowerSimulation.cs | d756c4e9b097282d |
| Core/FrameworkQueue.cs | SmoothNav.Core/FrameworkQueue.cs | 5fdf878e100d02f5 |
| Core/GroundSurface.cs | SmoothNav.Core/GroundSurface.cs | 66fcc1fc93c139a4 |
| Core/LoadedCoverage.cs | SmoothNav.Core/LoadedCoverage.cs | f41d171b54f2c99f |
| Core/MeshPointGround.cs | SmoothNav.Core/MeshPointGround.cs | f5ccdee396a65e16 |
| Core/Metrics.cs | SmoothNav.Core/Metrics.cs | fe3ae64035b2c68d |
| Core/MovementSession.cs | SmoothNav.Core/MovementSession.cs | 559b0cac1830ee5f |
| Core/PathMarker.cs | SmoothNav.Core/PathMarker.cs | 1bd7a394bf5b9e3b |
| Core/PathRefiner.cs | SmoothNav.Core/PathRefiner.cs | 95f869e30daa0f82 |
| Core/RoutePlanner.cs | SmoothNav.Core/RoutePlanner.cs | 846d51f073c633cf |
| Core/WindowMetrics.cs | SmoothNav.Core/WindowMetrics.cs | d9d4fd5dedf2d547 |
| Dalamud/GameCollisionProbe.cs | SmoothNav.Dalamud/GameCollisionProbe.cs | ca5763b051ea3081 |
