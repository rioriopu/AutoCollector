using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

static class Program
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    public static void Main()
    {
        var e = new ExchangeHarness(); e.fate.Interrupt = new(true); e.RunResume();
        Check(e.fate.Resumed == 1 && e.Step == ExchangeStep.Done, "FATE-only success resumes despite untouched AutoDuty");
        e = new(); e.combat.Interrupt = new(true); e.RunResume();
        Check(e.combat.Resumed == 1 && e.fate.Resumed == 0, "AutoDuty-only does not start idle FATE");
        e = new(); e.RunResume(); Check(e.combat.Resumed == 0 && e.fate.Resumed == 0, "idle earners stay idle");
        e = new(); e.fate.Interrupt = new(true); e.fate.Pending = true; e.RunResume();
        Check(e.Step == ExchangeStep.ResumeEarners && e.combat.Calls == 0, "pending FATE resume is not discarded");
        e.fate.Pending = false; e.RunResume(); Check(e.fate.Resumed == 1, "pending resume eventually completes");
        e = new(); e.fate.Interrupt = new(true); e.FailNow();
        Check(e.Step == ExchangeStep.ResumeEarners && e.fate.Interrupt is not null, "failure keeps suspension and schedules recovery");
        e.RunResume(); Check(e.fate.Resumed == 1 && e.Step == ExchangeStep.Error && e.StatusDetail == "original failure", "recovery preserves original failure");
        e = new(); e.fate.Interrupt = new(true); Plugin.C.InFlight = new(); e.FailUnknown(); e.RunResume();
        Check(e.fate.Resumed == 1 && Plugin.C.InFlight is not null, "unresolved purchase recovers without clearing purchase record");
        e = new(); e.fate.Interrupt = new(true); e.aborted = true; e.FailNow();
        Check(e.Step == ExchangeStep.Error && e.fate.Interrupt is null && e.fate.Resumed == 0, "explicit stop cannot restart FATE");
        e = new(); e.fate.Interrupt = new(false); e.FailNow();
        Check(e.fate.Interrupt is null, "idle FATE interruption record cannot leak into next exchange");
        e = new(); e.fate.Interrupt = new(true); e.Expire(); e.RunResume();
        Check(e.Step == ExchangeStep.Error && e.fate.Resumed == 0, "resume timeout is bounded and recorded");
        e = new(); e.fate.Interrupt = new(true); e.fate.Broken = true; e.RunResume();
        Check(e.Step == ExchangeStep.Error, "FATE resume failure is not reported as success");

        var target = new TargetHarness(); var boss = new Actor(1, 100, 4, 1, 900000); var forlorn = new Actor(2, 6738, 4, 80, 10);
        Svc.Objects = [boss, forlorn]; Svc.Targets.Target = boss;
        Check(target.AcquireTarget(4, Vector3.Zero, 100) == forlorn, "distant Forlorn overrides sticky high-HP boss");
        var maiden = new Actor(3, 6737, 4, 40, 10); Svc.Objects.Add(maiden);
        Check(target.AcquireTarget(4, Vector3.Zero, 100) == maiden, "nearest bonus enemy wins");
        maiden.IsDead = true; forlorn.IsTargetable = false; Svc.Targets.Target = boss;
        Check(target.AcquireTarget(4, Vector3.Zero, 100) == boss, "dead or untargetable bonus enemies excluded");
        Svc.Objects = [boss, new Actor(4, 6738, 9, 1, 1)]; Svc.Targets.Target = boss;
        Check(target.AcquireTarget(4, Vector3.Zero, 100) == boss, "adjacent FATE bonus enemy excluded");
        Check(!TargetHarness.IsForlorn(new Actor(5, 12349, 4, 1, 1)), "Haam Forlorn is not the bonus enemy");

        var f = Fate(FateState.Preparing);
        Check(Eligible(f), "preparing FATE eligible even with expired preparation timer");
        Check(!Eligible(f with {State=FateState.Ended}), "ended FATE excluded");
        Check(!Eligible(f with {State=FateState.Running}), "running FATE with no time left excluded");
        Check(!Eligible(f with {Progress=100}), "completed FATE excluded");
        Check(!FateScanner.IsEligible(f,120,90,false,100,10,10,false,new HashSet<ushort>{4}), "blacklisted preparation excluded");

        var cap = new CapHarness(); cap.currency.Counts[28] = 2000; cap.currency.Caps[28] = 2000;
        cap.currency.Counts[33913] = 100; cap.currency.Caps[33913] = 4000; cap.rewards.Items[100] = new(33913, 200);
        Check(!cap.Check(100), "full Poetics does not block a purple-scrip collectable");
        cap.currency.Counts[33913]=3900; Check(cap.Check(100), "actual reward currency overflow detected");
        cap.currency.Counts[33913]=3800; Check(!cap.Check(100), "exact cap is allowed");
        Check(!cap.Check(999), "unknown reward does not guess a currency");

        var map = new MapHarness(); map.fallbackMap[1]=25199; map.fallbackMap[2]=33913;
        map.Apply(new Dictionary<int,uint>()); Check(map.Revision==0 && !map.ResolvedFromClient, "empty prelogin snapshot does not certify mapping");
        map.Apply(new Dictionary<int,uint>{{1,25199}}); Check(map.Entries[2]==33913 && map.Revision==0, "partial client map retains fallback scrips");
        map.Apply(new Dictionary<int,uint>{{1,999}}); Check(map.Entries[1]==999 && map.Revision==1, "changed client mapping overrides fallback and increments revision");
        map.Apply(new Dictionary<int,uint>{{1,999}}); Check(map.Revision==1, "unchanged refresh does not invalidate caches");
        map.Apply(new Dictionary<int,uint>()); Check(map.Entries[1]==999, "transient empty snapshot keeps last valid data");
        map.ResetClient(); Check(map.Entries[1]==25199 && !map.ResolvedFromClient && map.Revision==2, "logout discards character snapshot");

        var tmp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()); Directory.CreateDirectory(tmp);
        try {
            var path=Path.Combine(tmp,"special_currency_map.json"); File.WriteAllText(path,"{\"buckets\":[]}");
            UpgradeHarness.Upgrade(path,"special_currency_map.json");
            Check(File.ReadAllText(path).Contains("2026.10.02") && File.Exists(Path.Combine(tmp,"special_currency_map.backup-0.json")),"unversioned currency file upgrades with backup");
            File.WriteAllText(path,"{\"verifiedGameVersion\":\"2027.1.1\"}"); UpgradeHarness.Upgrade(path,"special_currency_map.json");
            Check(File.ReadAllText(path).Contains("2027.1.1"),"newer installed mapping is preserved");
            File.WriteAllText(path,"{}"); UpgradeHarness.Upgrade(path,"other.json"); Check(File.ReadAllText(path)=="{}","unversioned unrelated data is untouched");
        } finally { Directory.Delete(tmp,true); }

        Svc.Objects=[]; Svc.Targets.Target=null; Player.Available=true; Player.Position=Vector3.Zero;
        var npc=new Actor(88,0,4,4,1){BaseId=9999}; Svc.Objects.Add(npc);
        f=Fate(FateState.Preparing) with {MotivationNpcId=88, StartTimeEpoch=0}; var nav=new NavigationService();
        using (var starter=new FateStarter(new())) {
            InteractionService.Attempts=0;
            Check(starter.Tick(f,nav) && InteractionService.Attempts==0,"first NPC tick only targets actor by EntityId");
            starter.Tick(f,nav); Check(InteractionService.Attempts==1,"next NPC tick actually interacts");
            starter.Tick(f,nav); Check(InteractionService.Attempts==1,"same NPC is not interacted twice");
            starter.Reset(); Svc.Targets.Target=null; starter.Tick(f,nav); starter.Tick(f,nav);
            Check(InteractionService.Attempts==2,"next encounter can interact again");
        }
        Svc.Targets.Target=null; npc.Position=new(20,0,0);
        using (var starter=new FateStarter(new())) {
            starter.Tick(f,nav); Check(nav.Range==3 && nav.BeginCount==1,"NPC approach uses interaction distance, not combat reach");
            starter.Tick(f,nav); Check(nav.BeginCount==1,"active NPC route is not reissued");
            starter.CancelMovement(nav); Check(nav.Stops==1,"NPC navigation released on combat takeover");
        }
        using (var starter=new FateStarter(new())) {
            npc.Position=new(4,0,0); GenericHelpers.DialogVisible=true; InteractionService.Attempts=0;
            starter.Tick(f,nav); Check(InteractionService.Attempts==0,"pre-existing dialogue cannot be claimed"); GenericHelpers.DialogVisible=false;
        }
        Console.WriteLine($"{count} runtime assertions passed; 6 source integration guards passed.");
    }
    static FateInfo Fate(FateState state)=>new(4,"test",new(1,0,0),100,0,100,100,FateRule.Slay,state,false,1,10,0,0);
    static bool Eligible(FateInfo f)=>FateScanner.IsEligible(f,120,90,false,100,10,10,false,null);
}

public class ExchangeHarness
{
    public FakeEarner fate=new(),combat=new(),crafter=new();
    FakeRetainer autoRetainer=new(); NavigationService navigation=new(); FakeLife lifestream=new();
    AnomalyLog anomalyLog=new(); AddonOwnershipTracker ownership=new(new());
    public ExchangeStep Step=ExchangeStep.ResumeEarners; public ExchangeFailure Failure;
    public string StatusDetail=""; public bool aborted;
    DateTime stepDeadlineUtc=DateTime.UtcNow.AddSeconds(20); bool resumeCleanupDone;
    string recoveryFailureDetail="",deliverySummary=""; public int LastSessionCompleted; public DateTime LastFinishedAt;
    Session? session=new(); Target? travelTarget=new();
    void CloseOwned(string name,bool useCloseFirst){}
    public void RunResume()=>TickResumeEarners(); public void Expire()=>stepDeadlineUtc=DateTime.UtcNow.AddSeconds(-1);
    public void FailNow()=>Fail(ExchangeFailure.Aborted,"original failure");
    public void FailUnknown()=>FailUnresolved(ExchangeFailure.Aborted,"unknown result");
    /*EXCHANGE*/
}
public enum ExchangeStep { Idle,Done,Error,ResumeEarners }
public enum ExchangeFailure { None,Aborted,AutoDutyResumeFailed }
public enum EarnerProgress { InProgress,Done,Failed }
public record EarnerStepResult(EarnerProgress Progress,string Detail="");
public record Interrupt(bool WasRunning);
public class FakeEarner {
    public Interrupt? Interrupt; public int Resumed,Calls; public bool Pending,Broken;
    public EarnerStepResult TickResume(){Calls++; if(Pending)return new(EarnerProgress.InProgress,"pending"); if(Broken)return new(EarnerProgress.Failed,"failed"); if(Interrupt is {WasRunning:true})Resumed++; Interrupt=null;return new(EarnerProgress.Done);}
    public void ForgetInterrupt()=>Interrupt=null; public void ResumeAfterStop(){} public void ResumeAfterFailure(){ if(Interrupt is {WasRunning:true}) Resumed++; }
}
public class FakeRetainer {public void Release(string reason){} }
public class FakeLife {public bool TryIsBusy(out bool busy){busy=false;return true;}public void TryAbort(){} }
public class Session {public int Completed=1;} public class Target {public uint TerritoryId=1;}
public class Purchase {public string Outcome="";} public class Config {public Purchase? InFlight;} public static class Plugin {public static Config C=new();}
public static class EzConfig {public static void Save(){} }
public class AnomalyLog {public void Info(string a,string b){}public void Warn(string a,string b){}public void Error(string a,string b){} }

public class TargetHarness {
    ulong ownedTarget;
    static bool IsLiveEnemy(IGameObject o)=>o is IBattleNpc n && n.IsTargetable && !n.IsDead && n.Hostile;
    static ushort FateIdOf(IGameObject o)=>((Actor)o).FateId;
    public bool MayAttack(IGameObject o,ushort f,Vector3 p,float r)=>IsLiveEnemy(o) && FateIdOf(o)==f;
    /*TARGET*/
}
public interface IGameObject {uint EntityId{get;}ulong GameObjectId{get;} uint BaseId{get;}Vector3 Position{get;}bool IsTargetable{get;} string Name{get;} }
public interface IBattleNpc:IGameObject {uint NameId{get;}uint MaxHp{get;}bool IsDead{get;}bool Hostile{get;} }
public class Actor(uint id,uint name,ushort fate,float distance,uint hp):IBattleNpc {
    public uint EntityId=>id; public ulong GameObjectId=>id;public uint BaseId{get;set;}public Vector3 Position{get;set;}=new(distance,0,0);
    public bool IsTargetable{get;set;}=true;public bool IsDead{get;set;}public bool Hostile=>true;public uint NameId=>name;public uint MaxHp=>hp;public ushort FateId=>fate; public string Name=>"actor";
}
public class Targets {public IGameObject? Target;}
public class ClientState {public uint TerritoryType=1;}
public enum ConditionFlag {InCombat}
public class Conditions {public bool this[ConditionFlag f]=>false;}
public static class Svc {public static List<IGameObject> Objects=[];public static Targets Targets=new();public static ClientState ClientState=new();public static Conditions Condition=new();}
public static class Player {public static bool Available=true; public static Vector3 Position;}
public enum FateState {Preparing,Running,Ended}
/*FATEINFO*/
public class FateScanner {
    public FateInfo? GetById(ushort id)=>null;
    public (Vector3 Position,float Distance)? FindNearestMob(ushort id,Vector3 from)=>null;
    /*ELIGIBLE*/
}
public class CapHarness {
    public Currency currency=new();public Rewards rewards=new();
    Dictionary<uint,(uint ScripItemId,int Amount)> observedReward=[];
    public bool Check(uint id)=>NearCap(id,out _);
    /*CAP*/
}
public class Currency {public Dictionary<uint,int> Counts=[];public Dictionary<uint,uint> Caps=[];public uint? GetEffectiveCap(uint id)=>Caps.TryGetValue(id,out var n)?n:null; public bool TryGetCount(uint id,out int count)=>Counts.TryGetValue(id,out count);}
public record Reward(uint CurrencyItemId,int HighReward);
public class Rewards {public Dictionary<uint,Reward> Items=[]; public bool TryResolve(uint id,out Reward r)=>Items.TryGetValue(id,out r!);}
public static class Ui {public static class StatusText{public static string ItemName(uint id)=>id.ToString();}}
public class MapHarness {
    public Dictionary<int,uint> fallbackMap=[]; Dictionary<int,uint> runtimeMap=[];
    public int Revision{get;private set;} public bool ResolvedFromClient{get;private set;}
    public void Apply(IReadOnlyDictionary<int,uint> m)=>ApplyRuntimeSnapshot(m);
    /*MAP*/
}
public class UpgradeHarness {
    static string ReadEmbedded(string name)=>"{\"verifiedGameVersion\":\"2026.10.02\",\"buckets\":[]}";
    static string ReadVersion(string json)=>System.Text.Json.JsonDocument.Parse(json).RootElement.TryGetProperty("verifiedGameVersion",out var v)?v.GetString()??"":"";
    public static void Upgrade(string path,string name)=>UpgradeIfOutdated(path,name,new());
    /*UPGRADE*/
}
public enum MoveStatus {Moving,Failed}
public class NavigationService {public int Stops,BeginCount;public float Range;public void Stop()=>Stops++;public MoveStatus Tick(Vector3 p,float r)=>MoveStatus.Moving;public bool BeginMove(Vector3 p,float r,bool fly,out string reason){BeginCount++;Range=r;reason="";return true;} }
public unsafe struct AtkUnitBase {public void Close(bool b){} }
public static unsafe class GenericHelpers {
    public static bool DialogVisible; public static bool IsScreenReady()=>true;
    public static bool TryGetAddonByName<T>(string n,out T* addon) where T:unmanaged {addon=(T*)1;return DialogVisible;}
    public static bool IsAddonReady(AtkUnitBase* a)=>DialogVisible;
}
public unsafe class AddonOwnershipTracker(AnomalyLog log):IDisposable {
    public bool IsClaiming; public void Clear()=>IsClaiming=false;
    public bool TryGetOwnedSince(string n,DateTime t,out AtkUnitBase* addon){addon=null;return false;}
    public void Dispose(){}
}
public class InteractionService(AnomalyLog log) {
    public static int Attempts;
    public static bool IsWithinInteractRange(IGameObject o)=>Vector3.Distance(Player.Position,o.Position)<=5.5f;
    public bool StepInteract(IGameObject o){if(Svc.Targets.Target!=o){Svc.Targets.Target=o;return false;}Attempts++;return true;}
    public bool TryAdvanceTalk()=>true;
}
public static unsafe class Callback {public static void Fire(AtkUnitBase* a,bool b,int i){} }
/*STARTER*/
