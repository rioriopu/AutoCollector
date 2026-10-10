"""FATE の戦いと開始の判断（ゲームに触らない部分）を試す。本物のファイルをそのまま組み立てる。ゲームの操作はしない。
  - 狙いを倒れるまで固定する（Game/TargetLock.cs）
  - 4 秒攻撃できなければ迂回する・迂回の行き先（Automation/AttackWatch.cs）
  - 開始の NPC を順に試す・指定外の NPC の確認の窓（Automation/StartAttempts.cs）
  - 飛行の経路をつなぐ（Automation/FlightJoin.cs）
実行: python -X utf8 tools/regression/fate_combat.py
"""
from pathlib import Path
import shutil
import subprocess
import tempfile

repo = Path(__file__).resolve().parents[2]
src = repo / "AutoCollector"
files = [src / "Game" / "TargetLock.cs", src / "Automation" / "AttackWatch.cs",
         src / "Automation" / "StartAttempts.cs", src / "Automation" / "FlightJoin.cs"]
with tempfile.TemporaryDirectory(prefix="AutoCollector-fatecombat-") as tmp:
    tmp = Path(tmp)
    (tmp / "FateCombat.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
        '<ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>', encoding="utf-8")
    for f in files:
        shutil.copy(f, tmp / f.name)
    shutil.copy(Path(__file__).with_name("FateCombatHarness.cs"), tmp / "FateCombatHarness.cs")
    result = subprocess.run(["dotnet", "run", "--project", str(tmp / "FateCombat.csproj"), "-c", "Release", "--nologo"], cwd=repo)
    raise SystemExit(result.returncode)
