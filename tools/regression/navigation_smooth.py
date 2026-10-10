"""移動（NavigationService）と曲線に整える部品（SmoothNav の写し）を、偽物の vnavmesh・ゲームの上で組み立てて試す。ゲームの操作はしない。
実行: python -X utf8 tools/regression/navigation_smooth.py
"""
from pathlib import Path
import shutil
import subprocess
import tempfile

repo = Path(__file__).resolve().parents[2]
src = repo / "AutoCollector"
with tempfile.TemporaryDirectory(prefix="AutoCollector-navigation-") as tmp:
    tmp = Path(tmp)
    (tmp / "Navigation.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
        '<ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable><NoWarn>CS0649;CS0414</NoWarn></PropertyGroup></Project>',
        encoding="utf-8")
    # 本物：移動の部品と SmoothNav の写し（ゲームの当たり判定の窓口だけは Dalamud が要るので除く）。
    shutil.copy(src / "Automation" / "NavigationService.cs", tmp / "NavigationService.cs")
    for core in (src / "SmoothNav" / "Core").glob("*.cs"):
        shutil.copy(core, tmp / ("SmoothNav_" + core.name))
    shutil.copy(Path(__file__).with_name("NavigationHarness.cs"), tmp / "NavigationHarness.cs")
    result = subprocess.run(["dotnet", "run", "--project", str(tmp / "Navigation.csproj"), "-c", "Release", "--nologo"], cwd=repo)
    raise SystemExit(result.returncode)
