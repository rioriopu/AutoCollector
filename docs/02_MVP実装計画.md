# Auto Collector — MVP 実装計画

作成日: 2026-08-25
対象: MVP（Phase 1）= アラガントームストーン 1 通貨・1 アイテムの交換ループ
上位基準: `docs/00_設計決定.md` → `docs/01_交換対応範囲.md` → 本書

本書に登場する API・IPC・シート名・フィールド名は、すべてローカルの実ソースで行番号まで確認したもののみ。確認できなかったものは §6 に隔離した。

---

## 0. MVP のスコープ定義

| 含む | 含まない |
|---|---|
| 系統 B（トームストーン、`SpecialShop` + `Tomestones`/`TomestonesItem`） | 系統 A / C / D / E（Phase 2 以降） |
| 通貨 1 種 × 報酬アイテム 1 種のプリセット 1 件 | 複数プリセット・交換 Queue・キャラ別プロファイル |
| Lifestream テレポート + vnavmesh エリア内移動 | 都市内エーテネット（`AethernetTeleport`）の自動判断 |
| AutoRetainer `SetSuppressed` 協調 | AutoRetainer への交換委譲 |
| AutoDuty `Stop()` → 交換 → `Run()` 再開（設定で無効化可） | 周回カウンタの完全復元（IPC で取得不能、§6-Q1） |
| `ShopExchangeCurrency`（通貨→アイテム） | `ShopExchangeItem` / `InclusionShop` / `GrandCompanyExchange` / `CollectablesShop` |
| 1 回 1 個ずつの購入 | まとめ買い、`ShopExchangeCurrencyDialog` 経由の数量指定 |

---

## 1. プロジェクト構成

### 1.1 前提環境（実測）

| 項目 | 値 | 根拠 |
|---|---|---|
| Dalamud ランタイム | 15.0.3.2（`Hooks/dev`, commit `5ee22e18d`） | `%appdata%\XIVLauncher\addon\Hooks\` ディレクトリ名 / `dev\commit_hash.txt` |
| API レベル | 15（= Dalamud アセンブリのメジャー番号） | `Dalamud/Plugin/Internal/PluginManager.cs:83` |
| .NET SDK | 10.0.203（唯一） | `dotnet --list-sdks` |
| Dalamud.NET.Sdk | 15.0.0（NuGet キャッシュ最新） | `~/.nuget/packages/dalamud.net.sdk/` |
| ECommons | 3.2.1.17（NuGet キャッシュ最新） | `~/.nuget/packages/ecommons/` |
| 連携先 API レベル | AutoDuty 0.0.0.320 / AutoRetainer 4.6.1.27 / vnavmesh 1.2.3.13 / Lifestream 2.5.4.16 いずれも 15 | 各 `installedPlugins/*/​*.json` |

**API レベルの制約（誤解しやすい点）**

- 通常プラグイン（非 dev）は `manifest.DalamudApiLevel < 現行 API` だと **例外でロード拒否**される（`Dalamud/Plugin/Internal/Types/LocalPlugin.cs:331-333`）。「1 世代猶予」はインストーラ一覧に *outdated* として表示するための適格判定（`PluginManager.cs:1143-1153`）であって、ロード可否ではない。
- dev プラグインは `!this.IsDev` によって API レベル下限の制約が無い代わりに、`StartOnBoot` は API 完全一致時のみ有効になる（`LocalDevPlugin.cs:69`）。開発中に「起動時に自動ロードされない」場合はここを疑う。
- リポジトリ経由の更新配信は **API レベル完全一致**が条件（`PluginManager.cs:1776-1780`）。

### 1.2 `AutoCollector.csproj`（実内容）

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Dalamud.NET.Sdk/15.0.0">

  <PropertyGroup>
    <Version>0.1.0.0</Version>
    <RootNamespace>AutoCollector</RootNamespace>
    <AssemblyName>AutoCollector</AssemblyName>
    <Description>通貨が閾値に到達したら自動処理を停止し、交換所へ移動して交換し、復帰する</Description>
    <Authors>rioriopu</Authors>
  </PropertyGroup>

  <ItemGroup>
    <!-- manifest 本体。DalamudPackager が ProjectDir から読み、
         InternalName / AssemblyVersion / DalamudApiLevel を補完して出力する -->
    <Content Include="AutoCollector.json" />
    <!-- 補助データ。出力へコピーせず埋め込みリソースとして持ち、
         初回起動時にプラグイン設定ディレクトリへ展開してユーザーが編集できるようにする -->
    <EmbeddedResource Include="Data\special_currency_map.json" />
    <EmbeddedResource Include="Data\atkvalue_layout.json" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ECommons" Version="3.2.1.17" />
  </ItemGroup>

  <!-- Debug ビルドを dev プラグイン置き場へ直接吐く（Artisan と同じ方式） -->
  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <OutputPath>C:\DevPlugins\AutoCollector\</OutputPath>
  </PropertyGroup>

</Project>
```

**この csproj について明示しておくこと**

- `TargetFramework` / `LangVersion` / `Platforms` / `Nullable` / `AllowUnsafeBlocks` / `DalamudLibPath` / `DalamudPackager` の `PackageReference` は **すべて SDK 既定**なので書かない（`dalamud.net.sdk/15.0.0/Sdk/Sdk.props:34-70`：`net10.0-windows`, `LangVersion 14.0`, `x64`, `Nullable enable`, `AllowUnsafeBlocks true`, `RestorePackagesWithLockFile true`, `PackageVersion_DalamudPackager 15.0.0`）。ICE のように `TargetFramework` を上書きしない。
- SDK が自動参照するのは **Dalamud / Dalamud.Bindings.ImGui / ImPlot / ImGuizmo / FFXIVClientStructs / InteropGenerator.Runtime / Newtonsoft.Json / Lumina / Lumina.Excel / Serilog / Microsoft.Extensions.ObjectPool**（`Sdk.props:54-88`）。**`Dalamud.Common` は自動参照に含まれない** ので、もし `GameVersion` 等を使うなら vnavmesh のように `<Reference Include="Dalamud.Common" Private="false" />` を明示する必要がある（`ffxiv_navmesh/vnavmesh/vnavmesh.csproj:10`）。MVP では使わない方針。
- `RestorePackagesWithLockFile` が既定 true のため `packages.lock.json` が生成される。ECommons や Dalamud 側が動いたときは `dotnet restore --force-evaluate` が要る。
- `DalamudPackager` は Debug で `MakeZip=false`、Release で `MakeZip=true`（`dalamudpackager/15.0.0/build/DalamudPackager.targets:9-34`）。Release 出力は `$(OutputPath)AutoCollector/` 配下に `AutoCollector.json` と `latest.zip` の 2 ファイル。
- `ECommons.IPC` は **使わない**。理由: (a) ローカル NuGet キャッシュに存在しない、(b) `ECommons.IPC.csproj:54` が ECommons 3.2.0.11 に依存しており固定したい 3.2.1.17 とずれる、(c) 競合回避の要である `AutoRetainer.SetSuppressed` がラッパに含まれていない、(d) `AutoRetainerIPC.EnqueueHET` のように提供側とシグネチャが食い違う実例がある（`AutoRetainerIPC.cs:31` が `Action<bool,bool>` ／ 本体 `IPC_PluginState.cs:78-82` は `EnqueueHET(Action onFailure)`）。IPC は Dalamud 素の `GetIpcSubscriber` を自前で薄く包む（§4.6）。

### 1.3 `AutoCollector.json`（manifest ソース）

```json
{
  "Author": "rioriopu",
  "Name": "Auto Collector",
  "Punchline": "通貨が貯まったら自動で交換所へ行って交換する",
  "Description": "通貨が閾値へ到達したら実行中の自動処理を安全に停止し、交換所へ移動して交換し、元の処理へ復帰します。",
  "ApplicableVersion": "any",
  "RepoUrl": "https://github.com/rioriopu/AutoCollector",
  "Tags": ["automation", "currency", "exchange"]
}
```

- `InternalName` / `AssemblyVersion` / `DalamudApiLevel` は DalamudPackager が出力時に補完する（ICE のソース `ICE.json` 全 10 行 → 出力 `ICE.json:4-5,11-15` で確認）。`InternalName` は `AssemblyName` と一致する必要がある（devPlugin ロード時に空だと拒否: `PluginManager.cs:568-572`）。
- `ApplicableVersion` を検証済みゲームバージョンに固定すると、パッチ当日に Dalamud がロード自体を拒否して誤動作をゼロにできる（`LocalPlugin.cs:328-329`）。ただしその場合 **設定 UI すら開けずユーザーに事情を伝えられない**。MVP では `any` のままとし、代わりに実行時のゲームバージョン差分検知（§4.9 `SelfCheck`）で自己防衛する。

### 1.4 dev プラグイン登録

現行 Dalamud は `%appdata%\XIVLauncher\devPlugins` フォルダを**自動スキャンしない**（`PluginManager.cs:539-544` は `DalamudConfiguration.DevPluginLoadLocations` を列挙するだけ）。設定 →「Dev Plugin Locations」で **DLL の絶対パス**を登録する（`DevPluginsSettingsEntry.cs:264-265` が `Path.IsPathRooted(path) && Path.GetExtension(path) == ".dll"` を要求）。

登録パス: `C:\DevPlugins\AutoCollector\AutoCollector.dll`（既存の他 dev プラグインと同じ流儀）。`AutomaticReloading` は有効にしてよいが、`Dispose` 漏れがあるとフック / IPC / `Framework.Update` ハンドラが残留するので §4.10 のクリーンアップを厳守する。

### 1.5 ディレクトリ構成

```
AutoCollector/
├─ AutoCollector.csproj
├─ AutoCollector.json                 manifest ソース
├─ Plugin.cs                          IDalamudPlugin 実装（エントリ）
├─ Config.cs                          EzConfig 対象の設定クラス
├─ Data/
│  ├─ special_currency_map.json       UseCurrencyType==16 用（Phase 4 で使用。MVP は自己検証のみ）
│  └─ atkvalue_layout.json            ShopExchangeCurrency の AtkValue オフセット（§4.5）
├─ Game/                              ゲームデータ読み取り（副作用なし）
│  ├─ CurrencyService.cs
│  ├─ TomestoneService.cs
│  ├─ ExchangeResolver.cs
│  ├─ ExchangeDefinition.cs
│  ├─ NpcLocationService.cs
│  └─ AetheryteService.cs
├─ Automation/                        ゲーム状態を変える操作
│  ├─ ExchangeStateMachine.cs
│  ├─ ExchangeContext.cs
│  ├─ SafetyGuard.cs
│  ├─ NavigationService.cs
│  ├─ InteractionService.cs
│  ├─ MenuService.cs
│  ├─ ShopService.cs
│  └─ ShopExchangeCurrencyReader.cs   AtkReader 派生（境界・型チェック付き）
├─ Ipc/
│  ├─ IpcGate.cs                      可用性判定と fail-closed の共通土台
│  ├─ AutoDutyIpc.cs
│  ├─ VnavmeshIpc.cs
│  ├─ LifestreamIpc.cs
│  └─ AutoRetainerIpc.cs
├─ Diagnostics/
│  ├─ SelfCheck.cs                    起動時セルフテスト
│  └─ AnomalyLog.cs                   異常の蓄積と UI 表示
└─ Ui/
   ├─ MainWindow.cs
   └─ DebugWindow.cs
```

---

## 2. 使用する API / IPC 確定一覧

すべて実ソースで確認済み。行番号は確認箇所。

### 2.1 ECommons 基盤

```csharp
// ECommons/ECommonsMain.cs:51, :123
static void ECommonsMain.Init(IDalamudPluginInterface pluginInterface, IDalamudPlugin instance, params Module[] modules);
static void ECommonsMain.Dispose();
// ECommons/Module.cs:5 — All, DalamudReflector, ObjectFunctions, ObjectLife, SplatoonAPI, VfxTracking

// ECommons/DalamudServices/Svc.cs:15-58（[PluginService] 44 個）
Svc.PluginInterface / Framework / ClientState / Condition / Objects / Targets
  / Data / Chat / Commands / Log / GameGui / AddonLifecycle / PlayerState / AetheryteList

// ECommons/Configuration/EzConfig.cs:88, :125
static T EzConfig.Init<T>() where T : new();
static void EzConfig.Save();

// ECommons/SimpleGui/EzConfigGui.cs:28, :13
static void EzConfigGui.Init(Action draw, IPluginConfiguration config = null, string nameOverride = null, WindowType windowType = WindowType.Config);
static WindowSystem EzConfigGui.WindowSystem { get; }

// ECommons/EzCmd.cs:15
static void EzCmd.Add(string command, IReadOnlyCommandInfo.HandlerDelegate action, string helpMessage = null, int displayOrder = -1);

// ECommons/Throttlers/EzThrottler.cs:14, :18 / FrameThrottler.cs:11
static bool EzThrottler.Throttle(string name, int miliseconds = 500, bool rethrottle = false);
static bool EzThrottler.Check(string name);
static bool FrameThrottler.Throttle(string name, int frames = 60, bool rethrottle = false);
```

### 2.2 ECommons 安全条件・プレイヤー状態

```csharp
// ECommons/GenericHelpers/GenericHelpers.cs:492-533
static bool GenericHelpers.IsOccupied();   // ★ InCombat は含まれない
// ECommons/GenericHelpers/AddonHelpers.cs:95-101
static bool GenericHelpers.IsScreenReady(); // NowLoading / FadeMiddle / FadeBack が可視なら false

// ECommons/GameHelpers/Player.cs
static bool    Player.Available;         // :37
static bool    Player.Interactable;      // :38
static bool    Player.IsBusy;            // :40（IsOccupied + Casting + Moving + AnimLock + InCombat + TerritoryLoadState != 2）
static bool    Player.IsDead;            // :110（ConditionFlag.Unconscious）
static bool    Player.IsMoving;          // :98
static bool    Player.IsAnimationLocked; // :108
static bool    Player.IsInDuty;          // :86（GameMain.CurrentContentFinderConditionId != 0）
static Vector3 Player.Position;          // :96
static float   Player.DistanceTo(Vector3 other);  // :113
```

> **注意**: `Player.DistanceTo(IGameObject other)`（:115）は `other` の null チェックが無く `NullReferenceException` を投げる。`Player` は「可能な範囲で null 安全」であって全メンバ保証ではない（`Player.cs:27-29` のコメント）。`Vector3` オーバーロードのみ使う。

```csharp
// Dalamud/Game/ClientState/Conditions/ConditionFlag.cs（値は実測）
Unconscious = 2, Mounted = 4, InThatPosition = 11, Occupied = 25, InCombat = 26, Casting = 27,
Occupied30 = 30, OccupiedInEvent = 31, OccupiedInQuestEvent = 32, Occupied33 = 33,
BoundByDuty = 34, OccupiedInCutSceneEvent = 35, Occupied38 = 38, Occupied39 = 39,
BetweenAreas = 45, Jumping = 48, BetweenAreas51 = 51, BoundByDuty56 = 56,
WatchingCutscene = 58, Jumping61 = 61, WatchingCutscene78 = 78, BoundByDuty95 = 95
// Dalamud/Plugin/Services/ICondition.cs:42, :61
bool ICondition.this[ConditionFlag flag];
bool ICondition.Any(params ConditionFlag[] flags);
```

### 2.3 FFXIVClientStructs（通貨・プレイヤー）

```csharp
// FFXIV/Client/Game/InventoryManager.cs
static InventoryManager* InventoryManager.Instance();                                  // :9-10
int  GetInventoryItemCount(uint itemId, bool isHq = false, bool checkEquipped = true,
                           bool checkArmory = true, short minCollectability = 0);      // :63-64
uint GetTomestoneCount(uint tomestoneItemId);                                          // :125
static int GetLimitedTomestoneWeeklyLimit();                                           // :133-135
int  GetWeeklyAcquiredTomestoneCount();                                                // :156
uint GetEmptySlotsInBag();                                                             // :88-89
uint GetCompanySeals(byte grandcompanyId);   // Phase 2                                 // :118-119
uint GetMaxCompanySeals(byte grandcompanyId);// Phase 2                                 // :121-122

// FFXIV/Client/Game/UI/PlayerState.cs
static PlayerState* PlayerState.Instance();
ulong ContentId;        // :19（FieldOffset 0x68）
byte  GrandCompany;     // :71（FieldOffset 0x2D0、0=未所属/1=黒渦団/2=双蛇党/3=不滅隊）
byte  GetGrandCompanyRank();  // :290-291（Phase 2）

// Dalamud/Plugin/Services/IPlayerState.cs:39 — 推奨経路
ulong IPlayerState.ContentId { get; }   // Svc.PlayerState.ContentId
```

> `IClientState.LocalContentId` は現行 Dalamud に **存在しない**（ソース・`Dalamud.xml` とも 0 件）。キャラ識別は `Svc.PlayerState.ContentId` を使う。
> `GetWeeklyAcquiredTomestoneCount` / `GetLimitedTomestoneWeeklyLimit` の導入済みビルドでの実在は `addon/Hooks/dev/FFXIVClientStructs.xml:14814-14819` で確認済み。
> `CurrencyManager.GetItemCount` 等は導入済み DLL での実在を確認できていないため **MVP では使わない**（§6-Q7）。

### 2.4 FFXIVClientStructs（UI・対話）

```csharp
// FFXIV/Client/Game/Control/TargetSystem.cs:10-11, :71-72
static TargetSystem* TargetSystem.Instance();
ulong InteractWithObject(GameObject* obj, bool checkLineOfSight = true);   // 実プラグインはすべて false を渡す

// FFXIV/Component/GUI/AtkUnitBase.cs
bool FireCallback(uint valueCount, AtkValue* values, bool close = false);  // :218
bool Close(bool fireCallback);                                            // :287
AtkComponentButton* GetComponentButtonById(uint id);                      // :199
AtkResNode* GetNodeById(uint nodeId);                                     // :190
// AtkValues / AtkValuesCount（ECommons AtkReader.cs:34,45,109 が使用）
```

### 2.5 ECommons UI ヘルパ

```csharp
// ECommons/GenericHelpers/AddonHelpers.cs:110-119, :121-130, :139, :12-13
static bool TryGetAddonMaster<T>(string addon, out T addonMaster) where T : IAddonMasterBase;
static bool TryGetAddonMaster<T>(out T addonMaster) where T : IAddonMasterBase;  // 型名 = addon 名の前提
static bool TryGetAddonByName<T>(string Addon, out T* AddonPtr) where T : unmanaged;
static bool IsAddonReady(AtkUnitBase* Addon);

// ECommons/UIHelpers/AddonMasterImplementations/
AddonMaster.SelectString      — Entry[] Entries; Entry.Text; Entry.Index; Entry.Select()   // :41-52, :56-74
AddonMaster.SelectIconString  — Entry[] Entries; Entry.Text; Entry.Select()                // :37-46, :59-62
AddonMaster.SelectYesno       — string Text; Yes(); No(); Third(); RespectDisabledButtons  // :29, :37-49, :54, :55
AddonMaster.SelectOk          — Ok()                                                       // :21
AddonMaster.Talk              — Click()                                                    // :24-38
AddonMaster.ShopExchangeCurrencyDialog — ExchangeButton(id 17); CancelButton(id 18);
                                          Exchange(); Cancel()                             // :14-20
AddonMasterBase<T>            — AtkUnitBase* Base; bool IsVisible; bool IsAddonReady       // :24-26

// ECommons/UIHelpers/AtkReader.cs（★ 境界・型チェックあり）
protected uint?     ReadUInt(int n);     // :41-52   範囲外 → ArgumentOutOfRangeException / 型違い → InvalidCastException
protected int?      ReadInt(int n);      // :54-66
protected bool?     ReadBool(int n);     // :68-79
protected SeString  ReadSeString(int n); // :81-92
public    List<T>   Loop<T>(int Offset, int Size, int MaxLength, bool IgnoreNull = false); // :14-24
public    bool      IsNull;              // :30-40
// ctor: AtkReader(AtkUnitBase* UnitBase, int BeginOffset = 0) / AtkReader(nint, int)
// 読み取りは常に絶対 index = n + BeginOffset で境界チェックされる（:43-44, :107-110）

// ECommons/Automation/Callback.cs:19, :74, :21
static readonly AtkValue Callback.ZeroAtkValue;                    // { Type = 0, Int = 0 }
static void Callback.Fire(AtkUnitBase* Base, bool updateState, params object[] values);
static void Callback.InstallHook();  // 開発時に手動操作の callback 引数を採取する用
```

> `SelectYesno.Yes()` は既定で **無効化された Yes ボタンのフラグを書き換えて強制クリックする**（`SelectYesno.cs:37-49`）。Auto Collector は `RespectDisabledButtons = true` を必ず設定する。

### 2.6 Lumina シート（実在確認済みフィールドのみ）

```csharp
IDataManager.GetExcelSheet<T>(ClientLanguage? language = null, string name = null);
IDataManager.GetSubrowExcelSheet<T>(ClientLanguage? language = null, string name = null);
ExcelSheet<T>.GetRow(uint) / GetRowOrDefault(uint) / TryGetRow(uint, out T) / HasRow(uint) / Count
SubrowExcelSheet<T>.GetSubrowOrDefault(uint rowId, ushort subrowId)
SubrowExcelSheet<T>.GetSubrowCount(uint rowId) / TryGetSubrowCount(uint, out ushort)   // ★ 無限ループより安全
RowRef<T>.RowId / .IsValid / .Value / .ValueNullable / .TryGetValue(out T)
```

| シート | 使用フィールド | 根拠 |
|---|---|---|
| `Tomestones` | `WeeklyLimit`（**唯一のフィールド。行番号 = スロット**） | `EXDSchema/Tomestones.yml` |
| `TomestonesItem` | `Item : RowRef<Item>` / `Tomestones : RowRef<Tomestones>` / `CurrencyInventorySlot` | `EXDSchema/TomestonesItem.yml` |
| `SpecialShop` | `Name` / `UseCurrencyType` / `Item`(60 要素) | `EXDSchema/SpecialShop.yml` |
| `SpecialShop.ItemStruct` | `ReceiveItems{ Item, ReceiveCount, ReceiveHq }`(×2) / `ItemCosts{ ItemCost, CurrencyCost, CostType, CollectabilityCost }`(×3) / `Category`(×2) / `Quest` / `PatchNumber` / `Order` | 同上 relations |
| `Item` | `Name` / `StackSize`(uint) / `IsUnique` / `ItemUICategory` | `EXDSchema/Item.yml:7,86,167,184` |
| `ENpcBase` | `ENpcData : Collection<RowRef>`（**32 要素**） | `EXDSchema/ENpcBase.yml` / 実データ計測 |
| `ENpcResident` | `Singular` | `ItemVendorLocation/ItemLookup.cs:184` 系 |
| `PreHandler` | `Target : RowRef`（型なし） | `ItemLookup.AddItem.cs:151-179` |
| `TopicSelect` | `Name` / `Shop : Collection<RowRef>`（型なし） | `ItemLookup.AddItem.cs:181-213` |
| `CustomTalk` | `MainOption` / `Script : Collection<{ ScriptInstruction, ScriptArg }>` / `SpecialLinks : RowRef`（型なし） | `ItemLookup.cs:250, 267-320` |
| `CustomTalkNestHandlers`（subrow） | `NestHandler : RowRef`（型なし） | 同上 |
| `Level` | `X` / `Y` / `Z` / `Object : RowRef` / `Territory : RowRef<TerritoryType>` / `Type`（EventNPC は 8） | `ItemLookup.cs:512-519` |
| `TerritoryType` | `Map : RowRef<Map>` / `PlaceName` / `Bg` | `MapHelper.cs:32` |
| `Map` | `SizeFactor`(ushort) / `OffsetX` / `OffsetY`（short） | `NpcLocation.cs:18-33` |
| `Aetheryte` | `IsAetheryte` / `Territory : RowRef<TerritoryType>` / `PlaceName` | `MapHelper.cs:58-84` |
| `MapMarker`（subrow） | `DataType`(==3 がエーテライト) / `DataKey` | `MapHelper.cs:70-72` |

```csharp
// Dalamud/Utility/MapUtil（AutoDuty MapHelper.cs:22 で使用実績）
static Vector2 MapUtil.WorldToMap(Vector2 coords, short offsetX, short offsetY, ushort sizeFactor);
// Dalamud/Game/ClientState/Aetherytes/AetheryteEntry.cs:72-78
uint IAetheryteEntry.AetheryteId; uint TerritoryId; byte SubIndex;   // Svc.AetheryteList を列挙
```

### 2.7 EventHandler 種別（間接参照の解決キー）

`ENpcData` などの値は **上位 16bit が EventHandler 種別**。判定は `(value >> 16) == 種別` の一択（`ItemVendorLocation/ItemLookup.cs:558-561`）。

| 種別 | 値 | MVP 使用 |
|---|---|---|
| `Shop`(GilShop) | `0x0004` | × |
| `CustomTalk` | `0x000B` | ○（best-effort） |
| `GrandCompanyShop` | `0x0016` | Phase 2 |
| `SpecialShop` | `0x001B` | ○ |
| `TopicSelect` | `0x0032` | ○ |
| `PreHandler` | `0x0036` | ○ |
| `InclusionShop` | `0x003A` | Phase 4 |
| `CollectablesShop` | `0x003B` | Phase 4 |

出典: `FFXIVClientStructs/FFXIV/Client/Game/Event/EventHandler.cs:407-501`（`EventHandlerContent : ushort`）。ハンドラ値は**そのまま該当シートの RowId**（例: `GCShop` 行 1441792..1441795 = `0x16<<16 + 0..3` を実データで確認）。

> **重要**: `ENpcData` の走査は `RowId == 0` で `break` してはならない。実データで先頭が 0 の NPC が 657 体存在し、うち 31 体は 0 の後ろにショップ系ハンドラを持つ（例: `ENpcBase 1001379 'Hunt billmaster'` = `[0, 67101, 721076, 1769516, 1769517, 1769518, 1769811, 0]`）。**32 要素すべてを `continue` で走査する。**

### 2.8 IPC（全件、プレフィックス確認済み）

**AutoDuty**（`AutoDuty/IPC/IPCProvider.cs:14` `EzIPC.Init(this)` → prefix = InternalName = `AutoDuty`）— 公開 IPC は **11 個のみ**。`Pause`/`Resume` は存在しない。

```
AutoDuty.ListConfig()                                    void          ← MVP 未使用
AutoDuty.GetConfig(string config)                        string        ← 使用（LoopTimes 退避）
AutoDuty.SetConfig(string config, object setting)        void          ← MVP 未使用（値は string / string[] のみ）
AutoDuty.Run(uint territoryType, int loops, bool bareMode) void        ← 使用（loops は必ず 0）
AutoDuty.Start(bool startFromZero)                       void          ← MVP 未使用（§6-Q1）
AutoDuty.Stop()                                          void          ← 使用
AutoDuty.IsNavigating()                                  bool          ← 使用
AutoDuty.IsLooping()                                     bool          ← 使用
AutoDuty.IsStopped()                                     bool          ← 使用
AutoDuty.ContentHasPath(uint territoryType)              bool          ← 使用（再開可否の事前判定）
AutoDuty.WrathComboCallback(int, string)                 void          ← 未使用
```

- `Stop()` は `Plugin.Stage = Stage.Stopped` を設定するだけだが、Stage セッターが `StopAndResetALL()` を**同期実行**する（`AutoDuty.cs:127-128, 1919-1962`）。`TaskManager.Abort()` / vnav `Path_Stop()` / BossMod プリセット解除 / Wrath ライセンス返却まで一括。IPC は**呼び出し元と同じスレッドで実行される**（`CallGatePubSubBase.cs:125`）ので、必ず `Framework.Update` 上から呼ぶ。
- `Stop()` 後も `CurrentTerritoryContent` / `Actions` / `PathFile` は残り、ダンジョンから退出もしない。
- `Run(loops > 0)` は `Configuration.LoopTimes` を**恒久的に書き換える**（`AutoDuty.cs:897-898`）。**必ず `loops = 0` を渡す。**
- `Stage.Paused` は IPC に露出しておらず、しかも Paused でも `PluginState.Navigating` は落ちないため、外部から一時停止を検知できない（`AutoDuty.cs:130-137`）。「完全アイドル」の唯一確実な判定は `IsStopped()`。

**vnavmesh**（`ffxiv_navmesh/vnavmesh/IPCProvider.cs:70-72` の `RegisterFunc` が `"vnavmesh." + name` で登録）

```
vnavmesh.Nav.IsReady                                     Func<bool>
vnavmesh.Nav.BuildProgress                               Func<float>          // 未実行時は負値
vnavmesh.Nav.PathfindInProgress                          Func<bool>
vnavmesh.SimpleMove.PathfindAndMoveCloseTo(Vector3 dest, bool fly, float range)  Func<..., bool>
vnavmesh.SimpleMove.PathfindInProgress                   Func<bool>
vnavmesh.Path.IsRunning                                  Func<bool>
vnavmesh.Path.NumWaypoints                               Func<int>
vnavmesh.Path.Stop                                       Action
vnavmesh.Path.GetTolerance / SetTolerance(float)         Func<float> / Action<float>
vnavmesh.Query.Mesh.PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ)  Func<..., Vector3?>
```

- `PathfindAndMoveCloseTo` の戻り値 `true` は「経路探索をキューに積んだ」だけの意味。`false` は「別の経路探索が進行中」（`AsyncMoveRequest.cs:62-77`）。
- 経路探索失敗時は例外が握り潰されログのみ、`Waypoints` は空のまま（`AsyncMoveRequest.cs:46-59`）。**「移動失敗」と「移動完了」は IPC 状態が同一**になるため、距離検証とタイムアウトが必須。
- `Path.IsRunning` は `followPath.Waypoints.Count > 0` の別名（`IPCProvider.cs:43`）。
- vnavmesh 側設定でパスが黙って中断されうる（スタック検出 `FollowPath.cs:124-130`、ユーザー入力 `:135-139`、ゾーン変更で `Waypoints.Clear()` `:228-232`）。`RetryOnStuck` は既定 true（`Config.cs:25`）なので `IsRunning` が false→true と往復する。**1 回 false を見ただけで完了と判定してはならない。**
- **バージョンを返す IPC は無い**（登録全件を確認）。
- 補助: `Svc.PluginInterface.TryGetData<bool[]>("vnav.PathIsRunning", out var d)` で DataShare からも走行状態を読める（`FollowPath.cs:39,44,59` / `IDalamudPluginInterface.cs:222`）。冗長チェックとして併用可。

**Lifestream**（`Lifestream/IPC/IPCProvider.cs:21-26` `EzIPC.Init(this)` → prefix = `Lifestream`）

```
Lifestream.Teleport(uint destination, byte subIndex)     Func<uint, byte, bool>   // :241
Lifestream.IsBusy()                                      Func<bool>               // :65
Lifestream.Abort()                                       Action                   // :71
Lifestream.ExecuteCommand(string arguments)              Action<string>           // :35
Lifestream.AethernetTeleport(string destination)         Func<string, bool>       // :139（Phase 2）
```

- `subIndex` は Aetheryte シートの行 ID ではなく **`Svc.AetheryteList` エントリの `SubIndex`**（`TeleportService.cs:19-23`）。
- `false` を返す条件は「未アクセス（`AetheryteList` に無い）」または `CanTeleport` 失敗（操作不能／テレポアクション使用不可／アニメーションロック中）（`TeleportService.cs:14-18, 34-35, 74-93`）。
- **IPC 経由の `Teleport` は `wait=false` パスなので TaskManager に何も積まない**（`TeleportService.cs:12, 24-30`）。したがって `Lifestream.IsBusy()` は true にならない。詠唱完了・エリア遷移は呼び出し側で待つ。
- 独自型を跨ぐ IPC（`AddressBookEntryTuple` / `HousePathData` / `ErrorCode` 等）は **一切使わない**。

**AutoRetainer**（生 CallGate 固定名 = `Modules/IPC.cs:22-23` ／ EzIPC prefix = `IPC_PluginState.cs:11` の `$"{InternalName}.PluginState"`）

```
AutoRetainer.GetSuppressed                                Func<bool>     // 生 CallGate
AutoRetainer.SetSuppressed(bool)                          Action<bool>   // 生 CallGate
AutoRetainer.PluginState.IsBusy()                         Func<bool>
AutoRetainer.PluginState.GetMultiModeStatus()             Func<bool>
AutoRetainer.PluginState.AbortAllTasks()                  Action         // 緊急時のみ・既定は呼ばない
```

- `SetSuppressed(true)` が実際に止めるのは **5 箇所**: メイン Tick の `SchedulerMain.Tick` 呼び出し（`AutoRetainer.cs:593`）、リテイナーセンスの自動ベル接近（`:656`）、`SchedulerMain.PluginEnabled`（`SchedulerMain.cs:21`）、`MultiMode.Active`（`MultiMode.cs:28`）、`MiniTA`（`MiniTA.cs:15`）。
- **新規開始のみを止め、実行中タスクは中断しない**（`SetSuppressed` はフラグ代入のみ、`Modules/IPC.cs:159-162`）。設計決定 D-1 の要件を満たす。
- `IsBusy` の実体は `P.TaskManager.IsBusy || AutoGCHandin.Operation || Lifestream.IsBusy()`（`Utils.cs:1275`）。
- **抜け穴**: `VoyageMain.Tick` は `Svc.Framework.Update` に独立登録され `IPC.Suppressed` を一切見ない（`VoyageMain.cs:20-26, 62-118`）。工房のボイジャーパネルをターゲットしていると抑制中でも動きうる。MVP の動線は工房を通らないため許容し、§6-Q9 に記録。

### 2.9 Dalamud IPC 基盤（自前ラッパで使用）

```csharp
// Dalamud/Plugin/IDalamudPluginInterface.cs:266-290
ICallGateSubscriber<TRet> GetIpcSubscriber<TRet>(string name);
ICallGateSubscriber<T1, TRet> GetIpcSubscriber<T1, TRet>(string name);
ICallGateSubscriber<T1, T2, TRet> GetIpcSubscriber<T1, T2, TRet>(string name);
ICallGateSubscriber<T1, T2, T3, TRet> GetIpcSubscriber<T1, T2, T3, TRet>(string name);

// Dalamud/Plugin/Ipc/ICallGateSubscriber.cs:10-33
bool ICallGateSubscriber.HasAction { get; }     // invoke せずに登録有無を判定できる
bool ICallGateSubscriber.HasFunction { get; }
void InvokeAction(...); TRet InvokeFunc(...);

// Dalamud/Plugin/IDalamudPluginInterface.cs:171 / InstalledPluginState.cs:14,19,24,70
IEnumerable<IExposedPlugin> InstalledPlugins { get; }   // Name / InternalName / IsLoaded / Version
bool TryGetData<T>(string tag, out T? data) where T : class;   // :222

// 例外階層（すべて abstract IpcError の直接派生。互いに継承関係なし）
IpcError ← IpcNotReadyError / IpcLengthMismatchError / IpcTypeMismatchError / IpcValueNullError
// 未登録 → IpcNotReadyError（CallGateChannel.cs:140-141）
// 引数個数違い → IpcLengthMismatchError（:192-193）
// 型変換失敗 → IpcTypeMismatchError（:246-251 で Newtonsoft 往復変換）
```

```csharp
// Dalamud/Plugin/Services/IDataManager.cs:22-37
GameData GameData { get; }             // GameData.Repositories.Values.FirstOrDefault()?.Version でゲームバージョン
bool HasModifiedGameDataFiles { get; } // TexTools 等による改変検知
```

---

## 3. State Machine

### 3.1 状態と遷移

```csharp
internal enum ExchangeState
{
    Idle,               // 監視のみ
    Queued,             // 交換要求が立った
    WaitingSafeWindow,  // 危険条件が解けるのを待つ
    SuppressExternal,   // AutoRetainer を協調抑制
    StopAutoDuty,       // AutoDuty を停止
    Teleport,           // 目的テリトリへテレポート
    Navigate,           // NPC 付近まで移動
    Interact,           // NPC へ対話
    SelectMenu,         // SelectString / SelectIconString を通過
    VerifyShop,         // ショップの中身を読み戻して照合
    Purchase,           // callback 送信
    ConfirmDialog,      // SelectYesno 処理
    VerifyResult,       // 通貨減 AND アイテム増 を検証
    CloseShop,          // ショップを閉じる
    ResumeAutoDuty,     // AutoDuty 再開
    ReleaseExternal,    // SetSuppressed(false)
    Done,               // → Idle
    Error               // 停止・通知。自動リトライしない
}

internal enum StepResult { Continue, Advance, Fail }
```

```
Idle ──(閾値到達 / 手動要求)──> Queued
Queued ──> WaitingSafeWindow
WaitingSafeWindow ──(安全条件成立)──> SuppressExternal
SuppressExternal ──> StopAutoDuty
StopAutoDuty ──(目的テリトリ != 現在)──> Teleport ──> Navigate
StopAutoDuty ──(目的テリトリ == 現在)──> Navigate
Navigate ──> Interact ──> SelectMenu ──> VerifyShop ──> Purchase
Purchase ──> ConfirmDialog ──> VerifyResult
VerifyResult ──(継続条件あり)──> VerifyShop        ※ 1 回の入店で複数回購入
VerifyResult ──(完了)──> CloseShop ──> ResumeAutoDuty ──> ReleaseExternal ──> Done ──> Idle

任意の状態 ──(Fail / ユーザー緊急停止)──> [Cleanup] ──> Error
[Cleanup] = ショップを閉じる → Path.Stop → SetSuppressed(false) → ターゲット解除
```

### 3.2 各状態の成功条件とタイムアウト

| 状態 | 毎フレームの動作 | 成功条件（Advance） | タイムアウト | 失敗コード |
|---|---|---|---|---|
| `Queued` | 交換定義を再解決し、通貨・報酬・NPC・座標がすべて解決できるか確認 | 定義が完全に解決できた | 5 s | `ExchangeNotResolvable` |
| `WaitingSafeWindow` | `SafetyGuard.IsSafeToStart()` を評価 | 全条件成立 | **なし**（60 s ごとに待機理由をログ） | — |
| `SuppressExternal` | AutoRetainer 未導入 → 即 Advance。導入済みなら `IsBusy()` を確認 → false なら `SetSuppressed(true)` → 再度 `IsBusy()` を確認（設計決定 D-1 の二段構え） | `Suppressed == true` かつ `IsBusy() == false` | `IsBusy()==true` の間は**待ち続ける**（外部プラグイン稼働中を時間で打ち切らない）。IPC が例外／未登録の状態が **60 s** 継続したら失敗 | `AutoRetainerIpcBroken` |
| `StopAutoDuty` | AutoDuty 未導入 or `IsStopped()==true` → 即 Advance。それ以外は `GetConfig("LoopTimes")` を退避し `Stop()` を 1 回だけ送信 | `IsStopped() == true` | 10 s | `AutoDutyStopFailed` |
| `Teleport` | `Player.IsCasting`/`IsBusy` でなければ `Lifestream.Teleport(aetheryteId, subIndex)` を 3 s throttle で送信（最大 3 回） | `Svc.ClientState.TerritoryType == 目的地` **かつ** `IsScreenReady()` **かつ** `Player.Interactable` | 90 s | `TeleportFailed` |
| `Navigate` | `Nav.IsReady()` を待ち、`SimpleMove.PathfindAndMoveCloseTo(pos, fly:false, range)` を 1 回送信。以後は状態監視のみ | `Path.IsRunning()==false` **かつ** `SimpleMove.PathfindInProgress()==false` **かつ** `Nav.PathfindInProgress()==false` **かつ** `Player.DistanceTo(pos) <= range + 2f` を **連続 3 フレーム**満たす | 全体 180 s。加えて 15 s 座標不変（±0.1f）でスタック判定 → `Path.Stop()` して 1 回だけ再発行 | `NavigationFailed` |
| `Interact` | `ShopExchangeCurrency` / `SelectString` / `SelectIconString` が Ready ならそこへ Advance。なければ `Svc.Targets.Target = obj` → 次フレーム `InteractWithObject(obj.Struct(), false)` を 1 s throttle | いずれかの addon が `IsAddonReady` | 30 s | `InteractFailed` |
| `SelectMenu` | `ShopExchangeCurrency` が開いたら即 Advance。`SelectString`/`SelectIconString` ならテキスト照合で 1 件だけ選択（§4.4） | `ShopExchangeCurrency` が `IsAddonReady` | 45 s | `MenuResolutionFailed` / `MenuAmbiguous` |
| `VerifyShop` | 自前 Reader でエントリ一覧を読む | 報酬 ItemId のエントリが **ちょうど 1 件**見つかり、その `CostAmount` がシートの `CurrencyCost` と一致 | 10 s | `ExchangeItemNotFound` / `ShopMismatch` |
| `Purchase` | 購入前の `通貨所持数` `報酬所持数` `AtkValues[CurrencyAmount]` を記録し、`Callback.Fire(base, true, 0, index, 1)` を 1 回だけ送信 | 送信完了（送信自体は成功扱いにしない） | 送信は 1 回のみ | — |
| `ConfirmDialog` | `SelectYesno` が Ready なら `Text` をログに残して `Yes()`（`RespectDisabledButtons = true`）。`ShopExchangeCurrencyDialog` が出た場合は `Cancel()` して失敗（MVP は数量ダイアログ非対応、§6-Q4） | `SelectYesno` を処理した、または `SelectYesno` が出ないまま `VerifyResult` の条件が満たされた | 15 s | `ConfirmDialogTimeout` |
| `VerifyResult` | 所持数を再取得 | `通貨所持数 == 記録値 - CurrencyCost` **かつ** `報酬所持数 == 記録値 + ReceiveCount` | 15 s | `ExchangeVerificationFailed` |
| `CloseShop` | `AtkUnitBase.Close(true)` を 1 s throttle で送信 | `ShopExchangeCurrency` が非表示 | 10 s | `ShopCloseFailed`（ただし Cleanup は続行） |
| `ResumeAutoDuty` | 再開設定が off / AutoDuty 未導入 / 停止前に稼働していなかった → 即 Advance。それ以外は `ContentHasPath(t)` を確認し `Run(t, 0, false)` を 1 回送信 | `IsNavigating() == true` または `IsLooping() == true` | 20 s | `AutoDutyResumeFailed`（**自動リトライしない**） |
| `ReleaseExternal` | `SetSuppressed(false)` を送信 | `GetSuppressed() == false`（取得できなければ 3 回送信して Advance） | 10 s | `SuppressReleaseFailed`（Error に落とすが、以後も毎フレーム解除を試みる） |

### 3.3 `SafetyGuard.IsSafeToStart()` の判定内容

以下がすべて false であること。1 つでも true なら `WaitingSafeWindow` に留まる。

```csharp
Player.Available == false
Player.Interactable == false
GenericHelpers.IsScreenReady() == false
GenericHelpers.IsOccupied() == true
Svc.Condition[ConditionFlag.InCombat]           // IsOccupied に含まれないので個別に見る
Svc.Condition[ConditionFlag.Unconscious]
Svc.Condition[ConditionFlag.BoundByDuty]
   || Svc.Condition[ConditionFlag.BoundByDuty56]
   || Svc.Condition[ConditionFlag.BoundByDuty95]
Player.IsInDuty == true                          // GameMain.CurrentContentFinderConditionId != 0
Player.IsAnimationLocked == true
Svc.Condition[ConditionFlag.Casting]
Svc.Condition[ConditionFlag.TradeOpen]
```

**不変条件**: Duty 中は絶対に AutoDuty を停止しない。`BoundByDuty` 系が立っている間は `WaitingSafeWindow` で待ち続ける（設計決定「外部プラグインを強制停止しない」の実装形）。

### 3.4 緊急停止（`/ac stop`）と Cleanup

どの状態からでも即座に `Cleanup → Error(UserAborted)`。Cleanup は `try/finally` ではなく**べき等な逐次処理**として実装し、各手順の失敗が次の手順を止めないようにする。

1. 自分が開いた `ShopExchangeCurrency` / `ShopExchangeCurrencyDialog` / `SelectYesno` を `Close(true)` で閉じる（他プラグインが開いたものは触らない = 自分が開いた記録があるものだけ）
2. `vnavmesh.Path.Stop` を送信（自分が移動を開始していた場合のみ）
3. `AutoRetainer.SetSuppressed(false)` を送信（**自分が true にしていた場合のみ**。他者が立てた抑制を勝手に解除しない）
4. `Svc.Targets.Target = null`
5. `AnomalyLog` に理由を記録し、`Svc.Chat` + `Svc.Log.Warning` で通知

> `AutoRetainer.PluginState.AbortAllTasks()` は既定では呼ばない（設計決定 D-1「実行中タスクを中断しない」）。UI の明示的なチェックボックスでのみ有効化。

### 3.5 連続失敗の扱い

同一プリセットで **2 回連続失敗**したら `Preset.Enabled = false` にして `EzConfig.Save()`。次回起動でも勝手に再開しない。UI に赤字で理由を残す。

---

## 4. サービスクラスの責務と主要シグネチャ

### 4.1 `Game/TomestoneService`

トームストーンのスロット（`Tomestones` 行番号）→ 現行 ItemId の解決。**序数依存の解決は絶対に使わない**（`TomestonesItem` の行が増減すると全スロットがずれ、例外なく別通貨を監視する）。

```csharp
internal sealed class TomestoneService
{
    // Tomestones の RowId（1=詩学 / 2=数理 / 3=記憶 / 4=天道 が現行）から ItemId を直引きする。
    // 実装は TomestonesItem を Tomestones.RowId 一致で検索する（ItemVendorLocation/Utilities.cs:268 と同方式）。
    public bool TryResolveItemId(uint tomestonesRowId, out uint itemId);

    // Tomestones.WeeklyLimit（シート値）。0 なら週上限なし。
    public uint GetWeeklyLimitFromSheet(uint tomestonesRowId);

    // 現在の週上限（実行時値）。InventoryManager.GetLimitedTomestoneWeeklyLimit()
    public int  GetWeeklyLimitRuntime();

    // 今週の取得済み量。InventoryManager.Instance()->GetWeeklyAcquiredTomestoneCount()
    public int  GetWeeklyAcquired();

    // シート値と実行時値の整合検査。0 <= acquired <= limit、かつシート側 WeeklyLimit と実行時 limit が矛盾しないか。
    public bool ValidateWeeklyConsistency(uint tomestonesRowId, out string reason);

    // 解決結果のスナップショット（前回起動時と比較して差分検知に使う）
    public IReadOnlyDictionary<uint, uint> SnapshotSlotToItemId();
}
```

### 4.2 `Game/CurrencyService`

```csharp
internal sealed class CurrencyService
{
    // HQ / NQ を必ず合算する。GetInventoryItemCount の isHq 既定は false なので 2 回呼ぶ。
    // 袋のみを数えたい場合は checkEquipped / checkArmory を明示的に false にする。
    public int GetCount(uint itemId, bool includeEquipped = false, bool includeArmory = false);

    // Item.StackSize（uint）を所持上限として使う。行が引けなければ null。
    public uint? GetStackCap(uint itemId);

    public uint GetEmptyBagSlots();   // InventoryManager.GetEmptySlotsInBag()

    // 閾値判定。Fixed / Percentage / BeforeCap の 3 モード（docs/01 §5）。
    public bool IsThresholdReached(ThresholdSetting setting, uint currencyItemId, out int current, out int cap);

    // 例外を出さないラッパ（MemberFunction 解決失敗時の InvalidOperationException を捕捉してログ）
    public bool TryGetCount(uint itemId, out int count);
}
```

> `InventoryManager` の各メソッドは `MemberFunction` によるシグネチャ走査で解決されるため、パッチ直後に FFXIVClientStructs が未追従だと `InvalidOperationException`（`ThrowHelper.ThrowNullAddress`、シグネチャ文字列付き）になる。**すべての呼び出しを try/catch で包み、例外が出たら「通貨読み取り不能」として交換を開始しない**（fail-closed）。

### 4.3 `Game/ExchangeResolver`

```csharp
internal sealed record ExchangeDefinition
{
    public uint     ShopId          { get; init; }   // SpecialShop.RowId
    public int      SheetEntryIndex { get; init; }   // SpecialShop.Item[] 内の位置（デバッグ表示用のみ）
    public uint     CurrencyItemId  { get; init; }
    public uint     CurrencyCost    { get; init; }
    public uint     RewardItemId    { get; init; }
    public uint     RewardQuantity  { get; init; }
    public bool     RewardHq        { get; init; }
    public uint     NpcDataId       { get; init; }   // ENpcBase.RowId
    public string   NpcName         { get; init; }   // ENpcResident.Singular
    public uint     TerritoryId     { get; init; }
    public Vector3  NpcPosition     { get; init; }
    public uint?    RequiredQuestId { get; init; }
    public HandlerPath Path         { get; init; }   // Direct / PreHandler / TopicSelect / CustomTalk
    public string?  MenuHint        { get; init; }   // SelectString 照合用の候補文字列（SpecialShop.Name 等）
}

internal sealed class ExchangeResolver
{
    // インデックス構築。Framework.Update 上で 1 フレームあたり N 行ずつ処理する
    // （ENpcBase 59851 行 × ENpcData 32 要素。一括処理はフレーム落ちの原因になる）。
    public void   BeginBuild(uint currencyItemId);
    public bool   TickBuild(int rowBudgetPerFrame = 2000);   // true = 完了
    public float  BuildProgress { get; }

    // 通貨で買えるすべての交換定義。
    public IReadOnlyList<ExchangeDefinition> ListByCurrency(uint currencyItemId);

    // 通貨 + 報酬アイテムから 1 件へ絞る（候補が複数なら NPC 選択規則を適用）。
    public ExchangeDefinition? Resolve(uint currencyItemId, uint rewardItemId, uint? preferredNpcDataId);
}
```

**コスト通貨の判定規則（MVP）** — `SpecialShop.UseCurrencyType` ではなく **エントリ単位の `ItemCosts[].CostType`** を主軸にする。

| `CostType` | `ItemCost.RowId` の意味 | MVP |
|---|---|---|
| `0` | 実 ItemId（≧8） | Phase 5 で使用 |
| `2` | `Tomestones` シートの RowId → `TomestonesItem` 経由で ItemId 化 | **○ 使用** |
| `3` | 特殊通貨バケットインデックス（`special_currency_map.json`） | Phase 4 |
| その他（1 / 5 等） | 不明 | スキップしてログ |

実データのクロス集計で `CostType==2` のコスト RowId は常に 1/2/3、`CostType==3` は 2/4/6/7 に収まっている。`UseCurrencyType` のみで分岐すると `UseCurrencyType==2`（463 ショップ / コスト 1353 件）が default に落ち、ギル・ファイアシャード・アイスシャードに誤解決される（例: `SpecialShop 1770982` は 500×`#3`=記憶が正解だが Ice Shard になる）。

**間接参照の走査経路（MVP）**

```
ENpcBase.RowId（= NPC の DataId）
 └ ENpcData[0..31]（0 でも continue。break しない）
     ├ (v>>16)==0x1B → SpecialShop.GetRow(v)
     ├ (v>>16)==0x36 → PreHandler.GetRow(v).Target
     │                    └ (t>>16)==0x1B → SpecialShop.GetRow(t)
     ├ (v>>16)==0x32 → TopicSelect.GetRow(v).Shop[*]（0 は continue）
     │                    ├ (s>>16)==0x1B → SpecialShop.GetRow(s)
     │                    └ (s>>16)==0x36 → PreHandler 経由（1 段だけ再帰）
     └ (v>>16)==0x0B → CustomTalk.GetRow(v)            ※ best-effort
                          ├ SpecialLinks != 0 → CustomTalkNestHandlers を
                          │    GetSubrowCount で件数取得して走査 → NestHandler が 0x1B なら採用
                          └ Script[*].ScriptArg が 0x1B なら採用
```

- サブロウ走査は `GetSubrowCount(uint)` / `TryGetSubrowCount(uint, out ushort)` を使い、「例外が出るまで無限ループ」は書かない。
- 各 SpecialShop の候補 RowId は事前に `HashSet<uint>` にしておき、ENpcBase 走査では集合参照だけ行う。

**NPC 選択規則**（同一 SpecialShop を持つ NPC が複数のとき）

1. `NpcLocationService` で座標が解決できること（必須）
2. `Svc.AetheryteList` に同一 `TerritoryId` のアクセス済みエーテライトがあること（必須。無ければテレポート不能）
3. 上記を満たす候補のうち、エーテライト↔NPC のマップ座標距離が最短のもの
4. ユーザーが UI で明示指定した `NpcDataId` があればそれを最優先

### 4.4 `Automation/MenuService`

```csharp
internal sealed class MenuService
{
    // ShopExchangeCurrency が開いていれば true。
    public bool IsShopOpen();

    // SelectString / SelectIconString のエントリを列挙（テキストのみ）。
    public IReadOnlyList<string> ListMenuEntries();

    // hint（SpecialShop.Name / TopicSelect.Name / ユーザーが保存した文字列）と
    // 正規化一致（空白除去・大小無視・完全一致 → 部分一致の順）するエントリを 1 件だけ選ぶ。
    // 一致が 0 件 or 2 件以上なら false を返し、選択しない。
    public bool TrySelectByText(string hint, out MenuSelectFailure failure);
}
```

エントリ番号の固定（`Entries[0]` 等）は使わない。並びはクエスト進行状況で変わる。`Entry.Select()` は内部で `Callback.Fire(addon, true, Index)` を送る（`SelectString.cs:65`）。

一致 0 件 / 複数一致のときは `MenuAmbiguous` で安全停止し、UI に候補一覧を表示してユーザーに 1 つ選ばせる。選ばれた文字列はプリセットに保存し、次回から `hint` として使う（ハードコードではなくユーザーデータ）。

### 4.5 `Automation/ShopExchangeCurrencyReader` と `ShopService`

`ShopExchangeCurrency` には FFXIVClientStructs の型付き Addon 構造体も Agent 構造体も存在しない（リポジトリ全体を `ShopExchangeCurrency` で grep して 0 件）。したがって `AtkValues` の生インデックス依存は避けられない。**境界・型チェックの無い読み取りを絶対に書かない**ため、ECommons の `AddonMaster.ShopExchangeCurrency` は使わず `AtkReader` 派生を自前で持つ。

```csharp
// Data/atkvalue_layout.json（既定値）
// {
//   "ShopExchangeCurrency": {
//     "NumEntries": 4, "CurrencyAmount": 86, "CurrencyIcon": 87,
//     "EntryCost": 456, "EntryItemId": 1066, "EntryIndex": 1310, "EntryStride": 1
//   }
// }

internal sealed unsafe class ShopExchangeCurrencyReader : AtkReader
{
    public ShopExchangeCurrencyReader(AtkUnitBase* addon, int beginOffset = 0) : base(addon, beginOffset) { }
    public ShopExchangeCurrencyReader(nint addon, int beginOffset = 0) : base(addon, beginOffset) { }

    public uint NumEntries      => ReadUInt(L.NumEntries)     ?? 0;
    public uint CurrencyAmount  => ReadUInt(L.CurrencyAmount) ?? 0;

    // 3 本の並列配列（Cost=456+i / ItemId=1066+i / Index=1310+i）を読む。
    // Loop の BeginOffset は最小基点（EntryCost）を使い、相対オフセットは全て非負にする:
    //   Cost = 0, ItemId = EntryItemId - EntryCost (=610), Index = EntryIndex - EntryCost (=854)
    // itemId==0 の空スロットで打ち切らないよう IgnoreNull:true にして自分で除外する。
    public List<Entry> ReadEntries();

    internal sealed class Entry : AtkReader
    {
        public uint ItemId     { get; }
        public uint CostAmount { get; }
        public uint Index      { get; }   // ★ callback に渡す絶対 index
    }
}

internal sealed class ShopService
{
    // 読む前に AtkValuesCount が必要な最大 index を超えているか確認する（超えていなければ即失敗）。
    public bool TryReadEntries(out IReadOnlyList<ShopEntry> entries, out string failureReason);

    // ID 照合 → 一致した Entry の Index / CostAmount を返す。
    public bool TryMatch(ExchangeDefinition def, out ShopEntry matched, out ShopMismatchKind kind);

    // Callback.Fire(base, true, 0, matched.Index, amount)
    public void SendPurchase(ShopEntry matched, int amount = 1);

    // AtkUnitBase.Close(true)
    public bool TryClose();
}
```

**なぜオフセットを JSON に外出しするか**: `ShopExchangeCurrency` の AtkValue 配置は ECommons の版で実際に +2 ずれた実績がある（Artisan 同梱 ECommons 3.2.0.4 / 2026-04-30 は `84 / 85 / 1064 / 454 / 1308`、AutoRetainer・Lifestream 同梱 ECommons 3.2.1.17 / 2026-08-08 は `86 / 87 / 1066 / 456 / 1310`）。既定値には後者を採用する。ずれても `AtkReader` が例外を出すか、ID 照合が不一致になって安全停止するので、**誤った index で別アイテムを購入することはない**。ユーザーは JSON を差し替えるだけで復旧できる。

**シート index を callback に渡さない**: ICE のソースが「シート由来の Index は購入では未使用・表示用」と明記しており（`Shop_Cosmocredits.cs:5-6`）、ECommons 側も「一覧の見た目の位置ではなく `AtkValues[1310+i]` に入っている絶対 index を渡す必要がある」と判明して修正した履歴がある（commit `9d5c645`, 2026-02-17）。`ExchangeDefinition.SheetEntryIndex` はデバッグ表示専用とし、実操作は **開いた画面から ItemId で検索して得た Index** を使う。これは `docs/01` §3 の「ID → index 逆引き → 読み戻して照合」を、より安全側に倒した実装である。

**タブ切替に依存しない**: タブ切替 callback `Callback.Fire(base, true, 4, -1, 1, tab)` は ICE に実使用例があるが、引数 `-1` と `1` の意味がソース上どこにも説明されておらず、変わっても例外が出ない。MVP は**切替なしで目的 ItemId が見つからなければ `ExchangeItemNotFound` で停止**する（総当たり切替は行わない）。

### 4.6 `Ipc/IpcGate` と各ラッパ

```csharp
internal enum IpcAvailability { NotInstalled, Installed_NoFunction, Ready, Broken }

internal sealed class IpcGate
{
    // Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == name && x.IsLoaded)
    public static bool IsPluginLoaded(string internalName);

    // SafeWrapper は使わず、常に例外を投げる素の Subscriber を保持する。
    public static ICallGateSubscriber<TRet> Func<TRet>(string ipcName);

    // invoke せずに登録有無を確認（ICallGateSubscriber.HasFunction / HasAction）
    public static IpcAvailability Probe(string internalName, string ipcName, bool isAction);
}

internal sealed class AutoRetainerIpc
{
    public const string InternalName = "AutoRetainer";
    public bool IsLoaded { get; }

    // 取得できない・例外・未登録は「Busy とみなす」= fail-closed。
    // ただし IsLoaded == false のときだけは false（未導入は Busy ではない）。
    public bool IsBusyFailClosed();

    public bool TryGetSuppressed(out bool suppressed);
    public bool TrySetSuppressed(bool value);
    public bool TryGetMultiModeStatus(out bool enabled);
}

internal sealed class AutoDutyIpc
{
    public const string InternalName = "AutoDuty";
    public bool IsLoaded { get; }
    public bool TryIsStopped(out bool stopped);
    public bool TryIsNavigating(out bool navigating);
    public bool TryIsLooping(out bool looping);
    public bool TryContentHasPath(uint territoryType, out bool hasPath);
    public bool TryGetConfig(string key, out string value);
    public bool TryStop();                                   // Framework スレッドからのみ
    public bool TryRun(uint territoryType);                  // 内部で loops:0, bareMode:false 固定
}

internal sealed class VnavmeshIpc
{
    public const string InternalName = "vnavmesh";
    public bool IsLoaded { get; }
    public bool TryNavIsReady(out bool ready);
    public bool TryPathfindInProgress(out bool inProgress);        // Nav.PathfindInProgress
    public bool TrySimpleMovePathfindInProgress(out bool inProgress);
    public bool TryPathIsRunning(out bool running);
    public bool TryMoveCloseTo(Vector3 dest, bool fly, float range, out bool accepted);
    public bool TryPathStop();
}

internal sealed class LifestreamIpc
{
    public const string InternalName = "Lifestream";
    public bool IsLoaded { get; }
    public bool TryTeleport(uint aetheryteId, byte subIndex, out bool accepted);
    public bool TryIsBusy(out bool busy);
    public bool TryAbort();
}
```

**fail-closed の原則**（`ECommons.EzIpcManager.SafeWrapper` を使わない理由）

`SafeWrapper.IPCException` は `IpcNotReadyError` を握り潰して `default(T)` を返す（`SafeWrapperIPC.cs:26-36`）。`SafeWrapper.AnyException` は全例外を握り潰す（`SafeWrapperAny.cs:27-38`）。すると `AutoRetainer.PluginState.IsBusy()` が **例外ではなく `false` を返す** = 「AutoRetainer は暇」と誤判定して割り込む。これが設計決定 D-1 を静かに破る最短経路である。

したがって:
- **状態問い合わせ**（`IsBusy` / `IsStopped` / `Path.IsRunning` など）は取得失敗を「進めない側」に倒す。
- **制御操作**（`Stop` / `SetSuppressed` / `Path.Stop`）は失敗をログして、握った制御を必ず解放してから停止する。
- 各 `Try*` は `IpcError` の種別ごとにログを分ける（`IpcNotReadyError` / `IpcLengthMismatchError` / `IpcTypeMismatchError` / `IpcValueNullError` は互いに継承関係がないので個別に catch）。

### 4.7 `Automation/NavigationService`

```csharp
internal sealed class NavigationService
{
    public bool  BeginMove(Vector3 dest, float range, bool fly = false);   // 1 回だけ発行
    public MoveStatus Tick(Vector3 dest, float range);                     // Moving / Arrived / Stuck / Failed
    public void  Stop();
    public bool  IsIdle { get; }   // Path.IsRunning==false && *.PathfindInProgress==false
}
```

- 到達判定は「3 つの状態フラグがすべて false」+「距離条件」+「連続 3 フレーム」の三重条件。`RetryOnStuck` 既定 true により `IsRunning` が false→true と往復するため、1 回の false では判定しない。
- スタック検出は `Player.Position` を 15 s 記録して ±0.1f 未満なら Stuck（AutoRetainer の 15 s 判定と同じ閾値、`BailoutManager.cs:187-197`）。
- 移動中に `Svc.ClientState.TerritoryType` が変わったら即 Failed（vnavmesh はゾーン変更で `Waypoints.Clear()` する）。
- 目的地の Y 座標は `Level.Y` をそのまま使う。`Query.Mesh.PointOnFloor` による補正は Phase 2（§6-Q6）。

### 4.8 `Automation/InteractionService`

```csharp
internal sealed class InteractionService
{
    // Svc.Objects から BaseId 一致 & IsTargetable & ObjectKind == EventNpc で探す。
    // DataId は Dalamud で [Obsolete("Renamed to BaseId")]（GameObject.cs:37）なので BaseId を使う。
    public bool TryFindNpc(uint baseId, out IGameObject? npc);

    // 未ターゲットならターゲット設定して false（次フレームへ）、
    // ターゲット済みなら InteractWithObject(obj.Struct(), false) を送って true。
    public bool StepInteract(IGameObject npc);
}
```

- ターゲット設定は `Svc.Targets.Target = obj`（`ITargetManager.cs:15`）。`Svc.Targets.SetTarget()` は ECommons の拡張（`LegacyHelpers.cs:15-18`）なので使わない。
- `IGameObject → GameObject*` は `obj.Struct()`（`ECommons/GameFunctions/ObjectFunctions.cs:21-24`、`using ECommons.GameFunctions;`）。
- 対話前に `!IsOccupied()` `!Player.IsAnimationLocked` `npc.IsTargetable` を確認する。
- **接近距離のハードコードはしない。** AutoRetainer の 4.6f / 4.75f / 6.5f は召喚ベル専用の値（`GetValidInteractionDistance(IGameObject bell)`、唯一の呼び出し元は `InteractWithTargetedBell`）で EventNpc には適用できない。MVP は `PathfindAndMoveCloseTo` の `range` を設定値（既定 3.0f）とし、到達後は `IsTargetable` と対話成功（addon 出現）で判定する。届かなければ `range` を 1 段縮めて 1 回だけ再移動する。

### 4.9 `Diagnostics/SelfCheck`

起動時と「ゲームバージョン差分検知時」と交換開始直前に走らせる。結果は UI に緑/赤で常時表示。

```csharp
internal sealed record SelfCheckReport(bool Ok, IReadOnlyList<SelfCheckItem> Items);

internal sealed class SelfCheck
{
    public SelfCheckReport RunAll();
}
```

| 検査項目 | 内容 | 失敗時 |
|---|---|---|
| ゲームバージョン差分 | `Svc.Data.GameData.Repositories.Values.FirstOrDefault()?.Version` を前回起動値と比較 | 警告表示。トームストーン解決結果とオフセットを「未検証」扱いにする |
| ゲームデータ改変 | `Svc.Data.HasModifiedGameDataFiles` | 警告表示（停止はしない） |
| Dalamud API レベル | 自 manifest の `DalamudApiLevel` と現行値の差を表示 | 表示のみ |
| トームストーン解決 | `Tomestones` 行 1〜4 が `TomestonesItem` 経由で ItemId に解決でき、`Item.Name` が空でない | 該当プリセットを無効化 |
| 週上限整合 | `0 <= GetWeeklyAcquiredTomestoneCount() <= GetLimitedTomestoneWeeklyLimit()`、かつ `Tomestones.WeeklyLimit` と矛盾しない | 週上限機能のみ無効化 |
| 通貨 API | `GetInventoryItemCount` が例外を出さずに返る | 交換を開始しない |
| IPC 存在確認 | 使用する全 IPC 名について `HasFunction`/`HasAction` を invoke なしで確認 | 該当機能を無効化 |
| 連携先バージョン | 4 プラグインの `IExposedPlugin.Version` を記録し、前回起動から変化していたら SelfCheck を強制再実行 | 表示のみ |
| 特殊通貨表 | `special_currency_map.json` の各 index の ItemId が現行 `Item` シートに存在し `Name` が空でない | Phase 4 機能のみ無効化（**自動補正しない**） |

> `docs/00` D-2 は特殊通貨表の不一致時に「自動補正」と記しているが、自動補正は**意図しない別通貨での交換**につながる。本計画では**検出 → 停止 → 通知**に倒し、補正は UI からユーザーが承認したときのみ行う。この差分は明示的な変更点として記録する。

### 4.10 `Plugin.cs`（エントリ）

```csharp
public sealed class Plugin : IDalamudPlugin
{
    internal static Plugin P = null!;
    internal static Config C = null!;

    public Plugin(IDalamudPluginInterface pi)
    {
        P = this;
        ECommonsMain.Init(pi, this);                 // MVP は追加 Module を使わない
        new ECommons.Schedulers.TickScheduler(Load); // 初期化本体は次フレームへ（ICE と同じ流儀）
    }

    private void Load()
    {
        C = EzConfig.Init<Config>();
        EzConfigGui.Init(MainWindow.Draw);
        EzCmd.Add("/autocollector", OnCommand, "Auto Collector を開く");
        TryAddShortCommand("/ac");                   // 失敗しても続行し、ログに出す（docs/00 D-3）
        Svc.Framework.Update += OnFrameworkUpdate;
        _selfCheck.RunAll();
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;   // ★ 最優先で外す
        _stateMachine.EmergencyCleanup();            // SetSuppressed(false) を必ず含む
        ECommonsMain.Dispose();                      // EzConfig.Save と登録コマンド解除は ECommons が行う
    }
}
```

`AutomaticReloading` 有効時に `Framework.Update` ハンドラや抑制フラグが残らないよう、`Dispose` の順序（ハンドラ解除 → Cleanup → ECommons 破棄）を厳守する。

---

## 5. 実装順序

各ステップは**単独で検証可能**であり、前のステップが動かないと次に進めない依存関係になっている。S0〜S5 はゲーム状態をほとんど変えないため、安全に反復できる。

| # | 内容 | 完了条件（検証方法） |
|---|---|---|
| **S0** | scaffold。csproj / manifest / `Plugin.cs` / 空 UI / コマンド登録 / `Dispose` | ビルドが通り、dev プラグインとしてロードされ、`/autocollector` でウィンドウが開き、アンロードで残留物なし |
| **S1** | `TomestoneService` + `CurrencyService` + `SelfCheck` + デバッグ UI（読み取り専用） | UI に「詩学 / 数理 / 記憶 / 天道 の ItemId と現在所持数」「週取得量 / 週上限」が正しく表示される。ゲーム状態を一切変えない |
| **S2** | `ExchangeResolver`（フレーム分割ビルド）。UI に「この通貨で買えるもの一覧」（ShopId / 報酬名 / コスト / CostType）を表示 | 数理・記憶で交換可能なアイテム一覧が実際のゲーム内容と一致する。ビルド中にフレーム落ちしない |
| **S3** | `NpcLocationService` + `AetheryteService`。一覧に NPC 名 / TerritoryId / 座標 / 最寄りアクセス済みエーテライトを追加 | 実際に交換所へ行き、UI の座標とゲーム内座標が一致する。座標未解決の定義が明示的に除外表示される |
| **S4** | `ShopExchangeCurrencyReader` + `ShopService.TryReadEntries` / `TryMatch`。**callback は撃たない** | 手動で交換ショップを開いた状態でデバッグ UI を見ると、エントリ一覧（ItemId / CostAmount / Index）が画面と一致し、S2 のシート値と照合結果が「一致」になる |
| **S5** | 購入 1 回（`Purchase` → `ConfirmDialog` → `VerifyResult`）。手動でショップを開いた状態からデバッグ UI のボタンで実行 | 1 個だけ交換され、通貨が `CurrencyCost` 分だけ減り、報酬が `ReceiveCount` 分だけ増える。わざと違う ItemId を指定すると `ExchangeItemNotFound` で止まる |
| **S6** | `InteractionService` + `MenuService` + `NavigationService`。**同一エリア内のみ**（テレポートなし） | 交換所と同じエリアの離れた地点から `/ac` で NPC まで歩き、メニューを通過してショップが開き、S5 の購入まで通る |
| **S7** | `LifestreamIpc` + `Teleport` 状態 | 別エリアから開始して、テレポート → 移動 → 交換 → 検証まで通る。未アクセスのエーテライトを指定すると `TeleportFailed` で止まる |
| **S8** | `AutoRetainerIpc`（Suppress 二段構え）+ `AutoDutyIpc`（Stop / Run）+ Cleanup | AutoRetainer が MultiMode 稼働中に `/ac` を叩くと待機し、暇になった瞬間に抑制が立って交換が走り、終了後に抑制が解除される。AutoDuty 周回中に叩くと Duty 外に出るまで待ち、停止 → 交換 → 再開する。`/ac stop` でどの段階からでも抑制が解除される |
| **S9** | `ExchangeStateMachine` 統合（全状態のタイムアウト・失敗コード・連続失敗カウンタ・`AnomalyLog`） | 各状態で意図的に妨害（ショップを手で閉じる / 移動を手で止める / AutoRetainer を手動起動）しても、必ずタイムアウトして Cleanup が走り、外部プラグインの状態が元に戻る |
| **S10** | 設定 UI・プリセット 1 件・閾値監視（Fixed / Percentage / BeforeCap）・自動起動 | 通貨が閾値を超えた瞬間に自動で `Queued` へ入り、一連の流れが自動実行される。`Percentage` が既定 |

**S5 の前に必ずやること**: `Callback.InstallHook()`（`ECommons/Automation/Callback.cs:21-27`）を有効にして、**自分が手動で 1 回交換したときの callback 引数を実測**し、`0 / index / amount` という並びを確認する。第 1 引数 `0` の意味はソース上どこにも記述がないため、実測なしに撃たない。

---

## 6. 未解決事項と暫定対応

| # | 未解決事項 | 根拠の状況 | MVP での暫定対応 |
|---|---|---|---|
| **Q1** | AutoDuty 復帰の正確な意味論。`Run(territoryType, 0, false)` が「ダンジョン申請まで含めて再開する」かは未検証。周回カウンタ `CurrentLoop` は IPC に露出しておらず復元不可能（`AutoDuty.cs:1933-1934` で `if (!InDungeon) CurrentLoop = 0;`）。`Start(bool)` は現在テリトリが対応コンテンツ辞書に無いと何もせず return する（`AutoDuty.cs:1538-1550`） | Stop 側の副作用は `StopAndResetALL`（`:1919-1962`）で確認済み。Run 側の到達範囲は未確認 | (a) 停止前に `{TerritoryType, IsLooping(), IsNavigating(), GetConfig("LoopTimes")}` をスナップショット。(b) 復帰は `ContentHasPath(t)==true` を確認して `Run(t, 0, false)` のみ。**`loops` は必ず 0**（`LoopTimes` の恒久書き換え回避）。(c) 20 s 以内に `IsNavigating()` または `IsLooping()` が立たなければ `AutoDutyResumeFailed` で停止し**自動リトライしない**。(d) UI に「周回カウンタは 0 から再カウントされます」と明示。(e) 設定で「再開しない（通知のみ）」を選べる。S8 で実機検証し、結果を本表に反映する |
| **Q2** | `CostType==2` のインデックス空間が `TomestonesItem.Tomestones` の RowId と厳密に同一かはコード上の裏取りがない（実データの整合からの推定） | Lumina の doc も「3 = SpecialBucketId, rest items?」までしか書いていない（`Lumina.Excel.xml:513-517`） | 交換前に必ず読み戻し照合する（`VerifyShop`）。推定が外れていれば `CostAmount` かエントリ有無が一致せず安全停止する。外れた `ShopId` はログに残しユーザーが報告できるようにする |
| **Q3** | `ShopExchangeCurrency` の AtkValue オフセットが現行クライアントで正しいか。ECommons 3.2.1.17 の NuGet バイナリ（2026-08-01 付）と、`86` 系を持つソースツリー（commit `e6be8f0`, 2026-08-08）が同一ビルドである保証がない | ソース版の対応（`3.2.0.4`→84 系 / `3.2.1.17`→86 系）は `ECommons.csproj:5` の `BaseVersion` で確認済み | 自前 Reader + `atkvalue_layout.json` で外出し。既定は 86 系。`AtkReader` が範囲外/型不一致で例外を出す + ID 照合で不一致を検出するため、誤購入は起きない。S4 で実測して確定する |
| **Q4** | `ShopExchangeCurrencyDialog`（数量指定ダイアログ）が `ShopExchangeCurrency` からどの条件で開くか不明。ECommons のクラスコメントは "Custom input numeric-esque addon for venture exchanges"、`AddonDescription` は "Venture purchase window" とあり、ベンチャー交換に限られるかどうかも未確定 | 実働例として確認できたのは AutoRetainer の GC 交換（`GrandCompanyExchange` 経由）のみ | MVP は **1 回 1 個**固定。`ShopExchangeCurrencyDialog` が開いたら `Cancel()`（id 18）して `ConfirmDialogUnexpected` で安全停止し、UI に「まとめ買い未対応」と表示。Phase 2 で対応 |
| **Q5** | `ShopExchangeCurrency` の `BasicShopItems` 相当がタブでフィルタされるかは未確認（ECommons の実装コードにタブへの言及はない）。ICE が「見つからなければタブ切替」という防御的処理を書いているだけ | 実データによる裏取りなし | MVP はタブ切替を行わない。目的 ItemId が見つからなければ `ExchangeItemNotFound` で停止し、UI に「別タブの可能性」を表示。S4 で複数タブのあるショップを開いて実測する |
| **Q6** | `Level(Type==8)` の座標で対象 NPC が解決できるか（トームストーン交換 NPC で未検証）。`Level` に無い NPC は `planevent.lgb` パースか手書き補正が必要（実測で `1005423 'scrip exchange'` / `1019100` / `1033775` は `Level` に無い） | `Level(Type==8)` は 24300 行、ユニーク Object 24165 件。`1001617 'scrip exchange'` / `1002387 'storm quartermaster'` は `Level` から取得可能 | S3 で対象 NPC を実測。`Level` から取れない場合は当該定義を「座標未解決」として一覧から除外し、UI に理由を表示する。LGB パースは Phase 2（初期化コストが大きい） |
| **Q7** | `CurrencyManager.GetItemCount` / `GetItemMaxCount` / `IsItemLimited` が導入済み FFXIVClientStructs.dll に存在するか未確認（doc コメントが無いため `FFXIVClientStructs.xml` に現れない） | `CurrencyManager` 型自体は XML に 12 メンバがあり存在する | MVP では使わない。所持数は `InventoryManager.GetInventoryItemCount`、上限は `Item.StackSize` で賄う。Phase 2 で必要になったら実機で確認する |
| **Q8** | `TargetSystem.InteractWithObject` の戻り値 `ulong` の意味が不明（コメントなし。参照した全プラグインが戻り値を無視） | — | 戻り値は成功判定に使わない。「addon が出たか」で判定する |
| **Q9** | AutoRetainer の `VoyageMain.Tick` は `IPC.Suppressed` を見ずに独立動作する（`VoyageMain.cs:20-26, 62-118`）。工房ボイジャーパネルをターゲットしていると抑制中でも動きうる | 実ソースで確認済み | MVP の動線は工房を通らないため許容。`AnomalyLog` に「抑制中に AutoRetainer が動いた形跡（`IsBusy()` が予期せず true）」を記録して可視化する |
| **Q10** | `IExposedPlugin.IsLoaded == true` と IPC 登録完了の間に競合ウィンドウがあるか未確認 | AutoDuty / vnavmesh / Lifestream の各 `IPCProvider` はプラグインのコンストラクタ内で生成されることは確認済み | `IsLoaded` だけを信じず、必ず `HasFunction`/`HasAction` を併用する。それでも `IpcNotReadyError` が出たら 1 s 後に再試行（最大 3 回）してから失敗扱い |
| **Q11** | Lumina の `ExcelSheet<T>.GetRow(uint)` が行欠損時に投げる例外の型が未特定 | `GetRowOrDefault` が null を返す nullable 版として実在することは確認済み | 全シートアクセスで `GetRowOrDefault` / `TryGetRow` / `RowRef.ValueNullable` を使い、`GetRow` は使わない |
| **Q12** | `Data/*.json` を出力へコピーするか埋め込みリソースにするかの最適解（`Content Include` の挙動差は未検証） | ICE は `<Content Include="ICE.json" />` で manifest を扱っている実例のみ確認 | manifest は `Content Include`（ICE に倣う）。`Data/*.json` は `EmbeddedResource` にして初回起動時にプラグイン設定ディレクトリへ展開する（コピー挙動に依存しない） |
| **Q13** | `docs/00` D-2 が定める「特殊通貨表の不一致時に自動補正」の是非 | — | **本計画では自動補正しない**（検出 → 停止 → 通知 → ユーザー承認で補正）。誤った通貨での交換は不可逆なため。設計決定への変更提案としてオーナー確認を要する |

---

## 7. 設計上の不変条件（実装中に破ってはならないもの）

1. **Duty 中は AutoDuty を停止しない。** `BoundByDuty` 系または `Player.IsInDuty` が真の間は `WaitingSafeWindow` に留まる。
2. **交換 1 回ごとに事前照合（ItemId / CostAmount の一致）と事後検証（通貨減 AND アイテム増）を必ず挟む。** どちらか失敗したら即中止。
3. **`ENpcData` は 32 要素すべてを走査する。** `RowId == 0` で `break` しない。
4. **トームストーンは `Tomestones` RowId 直引きで解決する。** 序数（並べ替えて 1..N を振る）方式は使わない。
5. **IPC の状態問い合わせは fail-closed。** 取得失敗＝「進めない」に倒す。`SafeWrapper` による握り潰しを使わない。
6. **`SetSuppressed(true)` を立てたら、成功・失敗・緊急停止のいずれでも必ず `false` に戻す。** 他者が立てた抑制は解除しない。
7. **AtkValues は必ず `AtkReader` 経由で読む。** 生の `Addon->AtkValues[n]` を書かない。
8. **callback に渡す index は開いた画面から読み戻した値のみ。** シート上の位置やループ変数を渡さない。
9. **固定時間待機で次へ進まない。** タイムアウトは失敗であって前進の合図ではない。
10. **AutoDuty の `Run` には常に `loops = 0` を渡す。** ユーザーの `LoopTimes` 設定を書き換えない。