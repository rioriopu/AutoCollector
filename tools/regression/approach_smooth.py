"""FATE の接近（FateApproach）で、整えた経路を使うか決める所を試す。本物のメソッドを抜き出し、偽物の上で組み立てる。ゲームの操作はしない。
実行: python -X utf8 tools/regression/approach_smooth.py
"""
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

repo = Path(__file__).resolve().parents[2]
src = repo / "AutoCollector"
text = (src / "Automation" / "FateApproach.cs").read_text(encoding="utf-8-sig")


def method(signature):
    a = text.index("    " + signature)
    body = text.index("\n", a)
    # 式で書いたメソッド（=> …;）は ; まで、ふつうのメソッドは閉じ括弧まで。
    if text[body:body + 40].lstrip().startswith("=>"):
        return text[a:text.index(";\n", a) + 1]
    b = text.index("\n    }", a) + 6
    return text[a:b]


parts = [method(s) for s in ["private List<Vector3> ChooseSmoothed(", "private List<Vector3> ChoosePrePlan(",
                             "private bool Validate(List<Vector3>? waypoints, Vector3 destination, out string why)",
                             "private bool Validate(List<Vector3>? waypoints, Vector3 destination, Vector3 from, out string why)",
                             "private bool IsDescentAcceptable(List<Vector3> waypoints, Vector3 landingPoint, out string why)",
                             "private bool IsDescentAcceptable(List<Vector3> waypoints, Vector3 landingPoint, Vector3 from, out string why)",
                             "private static float Flat("]]
consts = [line for line in text.splitlines() if re.match(r"\s+private const float (LandingFlatMeters|LandingHeightMeters|MaxVerticalTailMeters|MaxVerticalTailFlatMeters) ", line)]
assert len(consts) == 4, consts
enum_start = text.index("public enum ApproachPhase")
enum = text[enum_start:text.index("\n}", enum_start) + 2]
harness = Path(__file__).with_name("ApproachHarness.cs").read_text(encoding="utf-8")
harness = harness.replace("/*ENUM*/", enum).replace("/*MEMBERS*/", "\n".join(consts) + "\n" + "\n".join(parts))
with tempfile.TemporaryDirectory(prefix="AutoCollector-approach-") as tmp:
    tmp = Path(tmp)
    (tmp / "Approach.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
        '<ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>', encoding="utf-8")
    for core in (src / "SmoothNav" / "Core").glob("*.cs"):
        shutil.copy(core, tmp / ("SmoothNav_" + core.name))
    (tmp / "ApproachHarness.cs").write_text(harness, encoding="utf-8")
    result = subprocess.run(["dotnet", "run", "--project", str(tmp / "Approach.csproj"), "-c", "Release", "--nologo"], cwd=repo)
    raise SystemExit(result.returncode)
