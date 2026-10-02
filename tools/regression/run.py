"""Compile production methods against fake game/IPC boundaries; no live game actions.
Run: python -X utf8 tools/regression/run.py
"""
from pathlib import Path
import re, subprocess, tempfile
repo = Path(__file__).resolve().parents[2]
src = repo / 'AutoCollector'
def read(p): return (src/p).read_text(encoding='utf-8-sig')
def method(p, signature):
    s=read(p); a=s.index('    '+signature); b=s.index('\n    }',a)+6
    return s[a:b]
def block(p, start, end):
    s=read(p); return s[s.index(start):s.index(end,s.index(start))]
exchange='Automation/ExchangeExecutor.cs'
parts={
 'EXCHANGE':'\n'.join(method(exchange,x) for x in [
 'private void TickResumeEarners()', 'private void FinishAfterExchange()',
 'private void RecoverAfterFailure()', 'private void Fail(ExchangeFailure failure, string detail)',
 'private void FailUnresolved(ExchangeFailure failure, string detail)',
 'public void Cleanup(bool preserveInterrupts = false)', 'private void ReleaseHeldControl()']),
 'ELIGIBLE':method('Game/FateScanner.cs','public static bool IsEligible('),
 'FATEINFO':block('Game/FateScanner.cs','public enum FateRule','/// <summary>FATE の並べ替え基準。'),
 'TARGET':method('Game/FateTargetService.cs','public IGameObject? AcquireTarget(')+ '\n'+block('Game/FateTargetService.cs','    public static bool IsForlorn','    public IGameObject? AcquireTarget'),
 'CAP':method('Automation/CollectableDeliveryRunner.cs','private bool NearCap('),
 'MAP':method('Game/SpecialCurrencyMap.cs','private void ApplyRuntimeSnapshot(')+'\n'+method('Game/SpecialCurrencyMap.cs','public void ResetClient()')+'\n'+block('Game/SpecialCurrencyMap.cs','    public IReadOnlyDictionary<int, uint> Entries','    /// <summary>\n    /// クライアントから対応表'),
 'UPGRADE':method('Automation/ShopAddonLayout.cs','private static void UpgradeIfOutdated(')+'\n'+method('Automation/ShopAddonLayout.cs','private static int CompareVersions('),
 'STARTER':re.sub(r'^using .*;\n|^namespace .*;\n','',read('Automation/FateStarter.cs'),flags=re.M),
}
template=Path(__file__).with_name('Harness.cs').read_text(encoding='utf-8')
for key,value in parts.items(): template=template.replace('/*'+key+'*/',value)
assert 'or ExchangeStep.SuppressExternal or ExchangeStep.ResumeEarners)' in read(exchange), 'trip timeout must not preempt recovery'
assert '!preset.CraftToEarn ||' in read('Automation/GoalRunner.cs'), 'FATE must not enter crafting goal runner'
assert 'this.CurrencyCatalog.Invalidate();' in read('Plugin.cs')
assert '"itemId": 25199' in read('Data/special_currency_map.json')
with tempfile.TemporaryDirectory(prefix='AutoCollector-regression-') as tmp:
    tmp=Path(tmp)
    (tmp/'Regression.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks><NoWarn>CS0649;CS0414</NoWarn></PropertyGroup></Project>')
    (tmp/'Program.cs').write_text(template,encoding='utf-8')
    result=subprocess.run(['dotnet','run','--project',str(tmp/'Regression.csproj'),'-c','Release','--nologo'],cwd=repo)
    raise SystemExit(result.returncode)
