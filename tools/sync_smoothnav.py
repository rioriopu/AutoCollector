# 移動をなめらかにする部品 SmoothNav の写しを AutoCollector/SmoothNav へ取り込む。
#
# SmoothNav は別リポジトリの部品。AutoCollector は原本を参照せず、必要なファイルだけを写して一緒に組み立てる
# （共同開発の相手の PC に原本が無くても組み立てられるように）。写しは直接直さず、原本で直してからこの台本で写し直す。
#
# 使い方: python -X utf8 tools/sync_smoothnav.py [原本のリポジトリ（既定 C:\ソース\SmoothNav-window-fixes）]
import hashlib
import os
import subprocess
import sys

source = sys.argv[1] if len(sys.argv) > 1 else r"C:\ソース\SmoothNav-window-fixes"
here = os.path.dirname(os.path.abspath(__file__))
destination = os.path.join(here, "..", "AutoCollector", "SmoothNav")
files = [
    ("SmoothNav.Core", ["Collision", "CurveBuilder", "FollowerSimulation", "FrameworkQueue", "GroundSurface", "LoadedCoverage",
                        "MeshPointGround", "Metrics", "MovementSession", "PathMarker", "PathRefiner", "RoutePlanner", "WindowMetrics"]),
    ("SmoothNav.Dalamud", ["GameCollisionProbe"]),
]
# AutoCollector は暗黙の using を使わないので、原本が暗黙に使っている名前空間を足す。
usings = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Threading", "System.Threading.Tasks"]

commit = subprocess.run(["git", "-C", source, "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
dirty = subprocess.run(["git", "-C", source, "status", "--porcelain", "--", "SmoothNav.Core", "SmoothNav.Dalamud"],
                       capture_output=True, text=True, check=True).stdout.strip()
if dirty:
    sys.exit("原本に未コミットの変更があります。コミットしてから写してください:\n" + dirty)

rows = []
for project, names in files:
    folder = "Core" if project.endswith("Core") else "Dalamud"
    os.makedirs(os.path.join(destination, folder), exist_ok=True)
    for name in names:
        path = os.path.join(source, project, name + ".cs")
        text = open(path, encoding="utf-8").read().replace("\r\n", "\n")
        digest = hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]
        lines = text.split("\n")
        present = {l.strip()[6:-1] for l in lines if l.startswith("using ") and l.strip().endswith(";")}
        added = [f"using {u};" for u in usings if u not in present]
        header = [f"// SmoothNav（{project}/{name}.cs・{commit}）の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。"]
        out = "\n".join(header + added + lines)
        with open(os.path.join(destination, folder, name + ".cs"), "w", encoding="utf-8", newline="\n") as f:
            f.write(out)
        rows.append(f"| {folder}/{name}.cs | {project}/{name}.cs | {digest} |")

readme = f"""# SmoothNav の写し

移動の角を曲線に整える部品 SmoothNav（別リポジトリ）の写し。原本のコミットは `{commit}`。

- 使い方は `Automation/SmoothMoveService.cs`（入れ物）と `Automation/NavigationService.cs`（移動）を見る。
- **ここのファイルは直接直さない。** 原本で直してから `python -X utf8 tools/sync_smoothnav.py` で写し直す
  （原本に未コミットの変更があると写さない）。写すときに、AutoCollector が暗黙に使わない using を足し、1 行目に原本を書く。

| 写し | 原本 | 原本の SHA256（先頭16文字・改行は LF） |
|---|---|---|
""" + "\n".join(rows) + "\n"
open(os.path.join(destination, "README.md"), "w", encoding="utf-8", newline="\n").write(readme)
print(f"{len(rows)} ファイルを写しました（原本 {commit}）→ {os.path.normpath(destination)}")
