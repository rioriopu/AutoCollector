# AutoRetainer 干渉調査 — 統合報告

**対象**: Auto Collector（AutoCollector）の交換状態機械に対する AutoRetainer の干渉
**根拠**: 実ソース照合済み。3 領域の調査結果に対する反証レビューで棄却された主張は採用していない。
**検証日時**: 2026-09-05。Auto Collector 側の行番号は本日時点の `ExchangeExecutor.cs`（2251 行）に合わせて再取得済み（調査中にファイルが編集され、旧版から約 +36〜+43 行ずれている。以下はすべて現行版の行番号）。

パス略記:

| 略記 | 実パス |
|---|---|
| `AR/` | `C:/Users/Administrator/TempRepos/_ac_refs/AutoRetainer/AutoRetainer/` |
| `EC/` | `C:/Users/Administrator/TempRepos/_ac_refs/AutoRetainer/ECommons/ECommons/` |
| `LS/` | `C:/Users/Administrator/TempRepos/_ac_refs/Lifestream/Lifestream/` |
| `AD/` | `C:/Users/Administrator/TempRepos/_ac_refs/AutoDuty/AutoDuty/` |
| `AC/` | `C:/Users/Administrator/TempRepos/Auto Collector/AutoCollector/` |

危険度の表記: **【実害】** = 通常運用で発生しうる / **【条件付き】** = 特定設定・特定状況でのみ発生 / **【理論上】** = コード上の経路は実在するが、Auto Collector の運用条件では成立が極めて狭い。

---

## 0. 結論の要約

1. `AutoRetainer.SetSuppressed(true)` が止めるのは **SchedulerMain / MultiMode.Tick / MiniTA / 新リテイナーセンス** の 4 系統の「新規開始」だけである。`IPC.Suppressed` を直接読むのは全ソースで 5 箇所（`AR/AutoRetainer.cs:593`, `:656`, `AR/Modules/MiniTA.cs:15`, `AR/Modules/Multi/MultiMode.cs:28`, `AR/Scheduler/SchedulerMain.cs:21`）だが、そのうち 2 つは伝播プロパティであり、実際の影響範囲は 20 箇所以上に及ぶ。**「抑制はほとんど効かない」という理解は誤りで、AutoRetainer の破壊的な動作の大半は抑制で止まる。**

2. 抑制で止まらない実害は 3 つに集約される。
   - **抑制時点で `P.TaskManager` に積まれていたタスク列**（`EC/Automation/NeoTaskManager/TaskManager.cs:88` で `Svc.Framework.Update` に直結。中断は timeout と `Abort()` のみ）。→ 既存の二段 IsBusy 確認でほぼ塞げている。
   - **Auto Collector 自身の抑制解除順序**（`AC/Automation/ExchangeExecutor.cs:1042` の `Release()` がショップを閉じる `:1064` より前にある）。→ **最大 20 秒の無防備な窓。実害あり。**
   - **抑制解除を通らずに Idle へ抜ける経路**（ユーザーが「確認したのでクリアする」を押した場合）。→ **抑制がリークし AutoRetainer が永久停止する。実害あり。**

3. Auto Collector 側の未実装が 3 つある。**キャラクタ同一性の検証が存在しない**、**抑制が実際に立っているかの再確認が存在しない**（`TryGetSuppressed` は UI 表示専用）、**開始前チェックが AutoRetainer の状態を一切見ない**（`AC/Automation/SafetyGuard.cs` は Condition のみ、`TryGetMultiModeStatus` は `AC/Ipc/AutoRetainerIpc.cs:44-45` に定義されているが呼び出し箇所ゼロ）。

---

## 1. 状態ごとの干渉点一覧

| 状態 | AutoRetainer が何をするか | 何が壊れるか | 抑制で防げるか | 既存の防御 |
|---|---|---|---|---|
| **WaitingSafeWindow**（無制限） | MultiMode がリログしてキャラを変える（`AR/Modules/Multi/MultiMode.cs:175` の `if(Active)` 内 → `:620-628` `Lifestream.ChangeCharacter`）。ベル移動、家/宿テレポート | **【実害】** 解決済みの `travelTarget`/`session`/`preset` は元キャラのもの。別キャラのまま交換へ進みうる | **防げる**（この段階では未抑制なので発生する） | `SafetyGuard.IsSafeToStart`（`AC/Automation/SafetyGuard.cs:96-100` + `EC/GenericHelpers/GenericHelpers.cs:512` の `OccupiedSummoningBell`）がベル操作中の開始を弾く。**キャラ変更は検出しない** |
| **SuppressExternal** | 抑制外の Tick（`AutoGCHandin` / `BailoutManager` / `VoyageMain`）と `AutoRetainer.GC.EnqueueInitiation` IPC（`AR/Modules/EzIPCManagers/IPC_GCContinuation.cs:12-16`、AutoDuty が `AD/Helpers/GCTurninHelper.cs:151` で呼ぶ）が新規タスクを積みうる | **【条件付き】** 「暇」を確認した直後にタスク列が生まれる。`IsBusy` は `P.TaskManager.IsBusy \|\| AutoGCHandin.Operation \|\| Lifestream.IsBusy()` だけ（`AR/Helpers/Utils.cs:1275`）で「これから積む」状態を表現しない | **一部**（`VoyageScheduler.Enabled` と `AutoGCHandin` は抑制外） | 二段 IsBusy 確認（`AC/…/ExchangeExecutor.cs:861` と `:887`）。1 段目と `Suppress()`（`:877`）は同一呼び出し内でフレームを跨がないため、その間に AutoRetainer は 1 命令も走らない |
| **StopAutoDuty** | 抑制前に積まれた `TaskDeliverItems` が `Lifestream.ExecuteCommand("gc …")` で GC へテレポート（`AR/Scheduler/Tasks/TaskDeliverItems.cs:87-95`） | **【理論上】** テレポート直後に `BeginTravel` の現在地判定が走ると誤分岐 | **防げない**（積み残し） | 二段 IsBusy 確認が抑制時点でタスク列が空であることを担保。`WaitForAutoDutyBetweenLoopActions`（`AC/Config.cs:103`、既定 true）と 120 秒待ち（`AC/Config.cs:106`） |
| **Teleport** | 積み残しタスクの Lifestream テレポート。`Lifestream.Teleport` は busy 判定なしで即 `Telepo->Teleport`（`LS/IPC/IPCProvider.cs:241-244` → `LS/Services/TeleportService.cs:12-32`） | **【理論上】** 別エリアへ飛ばされ 90 秒（`:986`）で `TeleportFailed` | **防げない**（積み残し） | `TryIsBusy` ガード、`teleportAttempts` 上限 3 |
| **AethernetHop** | `BailoutManager.RunStuckDetection` が `Lifestream.Abort()`（`AR/Modules/BailoutManager.cs:194`） | **【条件付き】** 中断されても「busy でない＋10 単位以上移動」で転送完了と誤判定し、別の場所から `BeginNavigation` する | **防げる**（`AR/Modules/BailoutManager.cs:44` の `MultiMode.Active` ブロック内） | 60 秒で徒歩フォールバック。**目的エリア/目的エーテライトの一致を見ていない** |
| **Navigate** | AutoRetainer の徒歩移動は vnavmesh ではなく `/lockon on` + `/automove on`（`AR/Modules/Multi/HouseEnterTask.cs:15,60,84`）。vnavmesh を直接触るのは `AR/Modules/BailoutManager.cs:192-193` の `Vnavmesh.Stop()` / `Vnavmesh.Reload()` のみ。ただし AutoRetainer が積む Lifestream タスクは Lifestream 内部で vnavmesh を駆動する（`LS/IPC/VnavmeshIPC.cs`, `LS/Tasks/Utility/TaskGeneratePath.cs`） | **【条件付き】** (a) `/automove` との競合走行で最大 180 秒（`:1128`）走ってから失敗 (b) `Nav.Reload` で経路点が消え Stuck 判定へ | **防げる**（移動タスクの投入元は SchedulerMain / MultiMode / VoyageScheduler、スタック検知も `MultiMode.Active` 内） | エリア変化検出（`AC/Automation/NavigationService.cs:161-165`）と 15 秒スタック検出（`:45`） |
| **Interact** | `Svc.Targets.Target` を書き換える箇所が 6 つ（`AR/Scheduler/Handlers/PlayerWorldHandlers.cs:20` / `HouseEnterTask.cs:69` / `AR/Modules/GcHandin/GCContinuation.cs:197` / `AR/Internal/InventoryManagement/NpcSaleManager.cs:154` / `AR/Modules/Voyage/VoyageScheduler.cs:117,181`） | **【条件付き】** ターゲット往復で 30 秒（`:1312`）を消費し `InteractFailed`。**誤った NPC に話しかけることはない**（`AC/Automation/InteractionService.cs:127` は `npc.Struct()` を渡す） | **一部**（`GCContinuation` は TaskManager 経由で抑制外） | 毎フレームのターゲット取り返し（`AC/Automation/InteractionService.cs:112-116`） |
| **SelectMenu** | (a) `BailoutManager` が SelectString を `Callback.Fire(addon, true, -1)` で閉じる（`AR/Modules/BailoutManager.cs:66-79`） (b) `TaskCleanupUi` が SelectString/SelectIconString/SelectYesno を -1 で閉じる (c) MiniTA が Talk を自動送り（`AR/Modules/MiniTA.cs:25-28`） | **【条件付き】** メニューが閉じられ Interact へ戻る → 30 秒で失敗。**誤選択はしない**（`TrySelectByText` は一意一致のみ、`AC/Automation/InteractionService.cs:279-292`） | **一部** (a) の第 1 分岐と (c) は抑制で塞がる。(a) の第 2 分岐は生の `MultiMode.Enabled` だが工房パネルをターゲット中でないと成立しない。(b) は抑制外 | 失敗どまりで安全側 |
| **Armed** | **同一フレーム内では何もできない**。`Framework.Update` は単一スレッド束縛の逐次 foreach（`dalamud-patch/Dalamud/Dalamud/Game/Framework.cs:387-391,465` + `Dalamud/Utility/EventHandlerExtensions.cs:117-128`）。AutoRetainer は `ShopExchangeCurrency` に AddonLifecycle を張っていない（購読は FreeCompanyCreditShop / GrandCompanySupplyList / Cabinet の 3 種のみ） | なし | 該当なし | **十分**。ただし成立根拠の後半は AutoRetainer の現実装依存 |
| **WaitOutcome**（15 秒） | (a) 委託・売却・分解・破棄・ベンチャー回収は **すべて `SchedulerMain.Tick()` 経由**（呼び出し元は `AR/AutoRetainer.cs:597` の 1 箇所のみ、`:593` の抑制ゲート内） (b) `AutoGCHandin` の軍票購入は抑制外 | (a) **抑制中は発生しない**。(b) **【理論上】** Auto Collector の通貨は必ずトームストーン（`AC/Game/TomestoneService.cs` の `TryResolveItemId` 一本で解決）なので、AutoRetainer 側の操作で `currencyOk` が成立することはない | **防げる**（(a)）／防げない（(b) だが成立せず） | 15 秒監視と `ExchangeUnexpectedDelta` 検出。判定は片側不等式 2 本のみ（`:1792-1793`）で帰属検証がないという設計上の弱さは残る |
| **ConfirmDialog / CancelDialog** | `GCContinuation.ConfirmExchange` が SelectYesno を走査し本文に "Exchange" / "よろしいですか？" を**含めば** Yes（`AR/Lang.cs:342`, `AR/Modules/GcHandin/GCContinuation.cs:111-118`）。`SelectExchange` が `ShopExchangeCurrencyDialog` の **id 17** を押す（`GCContinuation.cs:87-99`）。`CleanupUI` が SelectYesno と ShopExchangeCurrencyDialog を -1 で閉じる（`GCContinuation.cs:542-565`） | **【条件付き】** Auto Collector が「押さない」と決めたダイアログを押される／閉じられる。id 17 は Auto Collector が「絶対に触れない」と明記しているボタン（`AC/…/ExchangeExecutor.cs:2152`） | **防げない**（TaskManager 経由） | 成立条件は狭い: id 17 押下は `ContinuePurchase` で購入対象がベンチャーのときだけ（`GCContinuation.cs:494-497`）。起動には `EnqueueInitiation(true)` が必要で、その入口は `AutoGCHandin.cs:205` / IPC / デバッグ UI のみ。`AutoGCHandin.Operation` は `OccupiedInQuestEvent` を抜けた瞬間に毎フレーム強制 false（`AutoGCHandin.cs:81-85`）。さらに `AutoGCHandin.Operation` は `Utils.IsBusy` に含まれるため二段確認で待てる |
| **ResumeAutoDuty**（最大 20 秒） | `Release()` 直後に MultiMode.Tick が復活。`MultiMode.cs:318-324` の `Data.Enabled = false`（インベントリ空き判定）は `IsOccupied` でガードされていない | **【実害】** 大量交換直後は所持枠が減っているため、**キャラクタが AutoRetainer の処理対象から永続的に外される**（`AR/PluginData/Config.cs` の `OfflineData` に保存）。ユーザーには「Auto Collector を使ったらリテイナーが回らなくなった」と見える | **防げる**（`MultiMode.Tick` は `:175` の `if(Active)` 内 → **MultiMode 有効ユーザーに限る**） | **なし**。`Release()`（`:1042`）が `CloseOwned("ShopExchangeCurrency")`（`:1064`）より前にある |
| **失敗経路**（Fail / FailUnresolved / Abort） | — | **【実害】** `ReleaseHeldControl`（`:2208-2250`）は Cleanup と違いショップ・ダイアログを閉じず、Lifestream も止めず、`ownership.Clear()` もしない。Armed の検証失敗時に `ShopExchangeCurrency` が開いたまま抑制解除＋AutoDuty 再開 | — | 抑制解除自体は 3 経路とも到達している（`:2240` / `:478`）。漏れはない |
| **全状態共通** | ユーザーが AutoRetainer の UI で Cancel（`AR/UI/MainWindow/AutoRetainerWindow.cs:189-196`）、AutoRetainer のリロード、他プラグインの `SetSuppressed(false)` | **【実害】** `IPC.Suppressed` は参照カウントを持たない単一 static bool（`AR/Modules/IPC.cs:16`）。抑制が消えても Auto Collector は気づかない。さらに `Release()` が IPC 失敗すると `SuppressedByUs` が true に固着し（`AC/Ipc/AutoRetainerIpc.cs:95-107`）、以後 `Suppress()` がスキップされて**抑制なしで動き続ける** | **防げない**（仕様上の限界） | **なし**。`TryGetSuppressed` の参照は `AC/Ui/MainWindow.cs:996` の表示だけ |
| **逆方向（Auto Collector → AutoRetainer）** | — | **【条件付き】** `Cleanup` は Lifestream が busy なら理由を問わず `TryAbort()`（`:434-437`）、ターゲットを無条件 null（`:468`）。`AddonOwnershipTracker` は `IsClaiming` がセッション中ずっと true（`:617`）なので、その間に AutoRetainer が開いたウィンドウも「自分のもの」として記録され閉じられうる | — | `ownership` に 60 秒の失効あり（`AC/Automation/AddonOwnershipTracker.cs:32`） |

---

## 2. 抑制で防げるもの / 防げないもの

### 2-1. 抑制で確実に止まるもの（コードで確認済み）

| 系統 | 根拠 | 止まる内容 |
|---|---|---|
| SchedulerMain 全体 | `AR/AutoRetainer.cs:593`（`if(!IPC.Suppressed)`）内の `:597` が `SchedulerMain.Tick()` の唯一の呼び出し元。さらに `SchedulerMain.cs:21` が `PluginEnabledInternal && !IPC.Suppressed` | リテイナー処理、アイテム委託・売却・分解・破棄、ギル出納、ベンチャー回収、旧リテイナーセンス |
| MultiMode.Tick 全体 | `MultiMode.cs:28` `Active => Enabled && !IPC.Suppressed`、`:175` `if(Active)` | リログ／キャラ変更、家・アパート・宿テレポート、ベル操作、工房侵入、オート AFK 設定の書き換え、インベントリ満杯によるキャラ除外、`ShutdownOnSubExhaustion` |
| MiniTA | `MiniTA.cs:15` `if(!IPC.Suppressed)` | Talk 自動送り、SelectOk、売却系 SelectYesno の自動 Yes |
| 新リテイナーセンス | `AR/AutoRetainer.cs:656` | ベル自動アクセス（既定は無効: `RetainerSense = false`） |
| BailoutManager の主要ブロック | `BailoutManager.cs:44` `if(MultiMode.Active && …)` | 移動スタック検知（`Vnavmesh.Stop()`+`Reload()`+`Lifestream.Abort()`）、アドオンタイムアウト監視、他プラグイン（Wrath/BossMod/Questionable）の強制停止 |
| GC 納品完了後のテレポート | `AR/Modules/GcHandin/AutoGCHandin.cs:162` の条件は `C.TeleportAfterGCExchange && (!C.TeleportAfterGCExchangeMulti \|\| MultiMode.Active)`。既定は両方 true なので実質 `MultiMode.Active` が必要 | 交換後の自動テレポート |

### 2-2. 抑制していても起きるもの

| # | 事象 | 根拠 | 危険度 | 補足 |
|---|---|---|---|---|
| 1 | 抑制時点で積まれていた `P.TaskManager` のタスク列が最後まで走る | `EC/Automation/NeoTaskManager/TaskManager.cs:88,124-` | 【実害】 | 既存の二段 IsBusy 確認（`Utils.IsBusy` に `P.TaskManager.IsBusy` が含まれる）でほぼ塞げている |
| 2 | `FPSManager.Tick` がゲーム設定 `FPSInActive` / `Fps` を書き換え、ChillFrames に停止要求を出す | `AR/Modules/FPSManager.cs:10-34`, `:49-55`。起動条件は `Utils.IsBusy` | 【条件付き】 | **Auto Collector が Lifestream を使うだけで発火する**（`Utils.IsBusy` に `Lifestream.IsBusy()` が含まれる）。既定 `UnlockFPS` の値次第。AutoRetainer 側が busy 解除時に復元する |
| 3 | YesAlready / TextAdvance に停止要求が出入りする | `AR/Modules/NewYesAlreadyManager.cs:10,27,32-38`, `AR/Modules/TextAdvanceManager.cs:10,27,32-38` | 【実害・ただし有利】 | 同じく `Utils.IsBusy` 起点。Auto Collector にとってはむしろ暴発防止になる |
| 4 | `Utils.IsBusy` が true のとき Trade ウィンドウを問答無用で閉じる | `AR/AutoRetainer.cs:682-685` | 【理論上】 | 交換中に Trade を開いていなければ無関係 |
| 5 | `Shutdown.Tick` が抑制の外で回る。しかも `Shutdown.cs:35` のガード `!SchedulerMain.PluginEnabled` は**抑制すると満たされやすくなる** | `AR/AutoRetainer.cs:615`, `AR/Modules/Shutdown.cs:16-51` | 【条件付き】 | **抑制が逆効果になる唯一の箇所**。ただし `if(ShutdownAt != 0)` が外側にあり、ユーザーが `/autoretainer shutdown` で予約したときだけ到達する |
| 6 | `ExitOnSubCompletion` の `TickScheduler` 予約（30 秒後の `/shutdown`、5 分後の `Environment.Exit(0)`） | `AR/Modules/Multi/MultiMode.cs:214-220`, `:238`。`TickScheduler` は `Svc.Framework.Update` を直接張る | 【条件付き】 | 予約は抑制で取り消されない。`IsBusy` にも現れないため二段確認でも検出できない。**これが「二段構えに残っている本当の隙間」**。Dalamud 通知のクリックで取り消せる（`:231-235`, `:249-253`） |
| 7 | ログイン画面での自動復帰（`_CharaSelectReturn` のボタン押下、接続エラーダイアログの OK） | `AR/Modules/BailoutManager.cs:86-116`, `:118-137`。条件は生の `MultiMode.Enabled` | 【条件付き】 | 既定 `EnableCharaSelectBailout = true` / `ResolveConnectionErrors = true`。**MultiMode を有効にしたまま Auto Collector を回すと、切断時に抑制と無関係に再ログインが走る** |
| 8 | Mordion Gaol でのプロセス kill | `AR/AutoRetainer.cs:634-643`（条件に生の `MultiMode.Enabled` を含む） | 【理論上】 | Auto Collector 側で防ぎようがない |
| 9 | `VoyageMain.Tick` が抑制を一切見ずに工房・潜水艦タスクを積む | `AR/AutoRetainer.cs:193` → `AR/Modules/Voyage/VoyageMain.cs:20-26`, `:113-116` | 【理論上】 | `VoyageScheduler.Enabled` が必要。自動有効化は「工房パネルを初めてターゲットした瞬間」のエッジトリガ（`VoyageMain.cs:62-93`）で、交換 NPC では成立しない。**唯一の現実的経路は UI チェックボックス**（`AR/UI/MainWindow/AutoRetainerWindow.cs:144` `ImGui.Checkbox($"Deployables", ref VoyageScheduler.Enabled)`）— これは `IsInVoyagePanel` を経由しないため `:96-99` の自動解除が効かず立ちっぱなしになる |
| 10 | `ArtisanManager` / `ConditionChange` が抑制中でも `SchedulerMain.PluginEnabledInternal` を true にできる | `AR/Scheduler/ArtisanManager.cs:12-34`, `AR/AutoRetainer.cs:782-873` | 【理論上】 | `ArtisanManager` は `ArtisanIntegration`（既定 false）＋**到達可能なリテイナーベルの傍に立っていること**（`ArtisanManager.cs:20-21`）が必要。`ConditionChange` はベルをターゲットして開いた場合のみ。交換 NPC の前では成立しない。ただし成立した場合、抑制解除の瞬間に走り出す |
| 11 | `FCPointsUpdater` がテリトリ変更で `/freecompanycmd` を送信 | `AR/Modules/FCPointsUpdater.cs:17-19,52-58` | 【理論上】 | 既定 `UpdateStaleFCData = false`。加えて自宅ワールド／FCID != 0／前回から 30 時間が必要 |
| 12 | ログイン時の家入りタスク | `AR/Modules/Multi/MultiMode.cs:96-99`（`CanHETRaw` は `Active` を含まない） | 【理論上】 | 既定 `HETWhenDisabled = false` |
| 13 | 抑制フラグそのものが外部から消される | `AR/Modules/IPC.cs:16,159-162`, `AR/UI/MainWindow/AutoRetainerWindow.cs:189-196` | 【実害】 | 参照カウントがないため仕様上の限界。Auto Collector 側で検出する仕組みが必要 |

---

## 3. 抑制していない時間帯とリスク

| 窓 | 区間 | 長さ | AutoRetainer が動ける範囲 | 危険度 |
|---|---|---|---|---|
| **窓1** | WaitingSafeWindow の全域（`AC/…/ExchangeExecutor.cs:693-`） | **無制限**（意図的に時間制限なし） | 全機能 | **【実害】** リログでキャラが変わっても Auto Collector は気づかない。`travelTarget`/`session`/`preset` は元キャラの通貨・所持数で解決済みのまま進む。Armed の検証（通貨一致・残高）は通ってしまう |
| **窓2** | AutoDuty ループ間処理の待ち（`:748` 以降、`AC/Config.cs:106` = 120 秒） | 既定 120 秒 | 全機能 | 意図した譲り合い。ベル操作中は `OccupiedSummoningBell` で `SafetyGuard` が false を返すため二重に守られている。ただし 120 秒で打ち切って先へ進む |
| **窓3** | 安全判定通過 → `Suppress()` 実行まで | **1 tick ≒ 100 ms**（`AC/Plugin.cs:196`）。60fps なら AutoRetainer は約 6 回 Tick | 全機能 | **実害なし**。何か積まれても 1 段目 IsBusy（`:861`）で待たされるだけ。Auto Collector はまだ何も掴んでいない |
| **窓4** | ベンチャー譲り待ち（`:840-858`、`AC/Config.cs:122` = 180 秒。条件が続く限り繰り返す） | 既定 180 秒 × N | 全機能 | **【実害】** この待機中に `SafetyGuard` もエリアもキャラも再評価されない。リログ・家への移動が起きても検出できず、その後の Teleport / Navigate を別の場所・別のキャラから開始する（窓1 と同じ帰結） |
| **窓5** | 1 段目 IsBusy 待ち（`:861-872`） | **無制限** | 進行中の処理のみ | 意図どおり。ただし `IsBusyFailClosed` は IPC 取得失敗時も true を返すため（`AC/Ipc/AutoRetainerIpc.cs:27-41`）、IPC が壊れると無期限に待つ。`:748` で設定した 60 秒の `stepDeadlineUtc` は `TickSuppressExternal` から一度も読まれない（死んだコード） |
| **窓6** | `ResumeAutoDuty` の `Release()`（`:1042`）→ `FinishAfterExchange` の `CloseOwned("ShopExchangeCurrency")`（`:1064`） | **最大 20 秒**（`:1831`） | 全機能（ショップを開いたまま） | **【実害】** `MultiMode.cs:318-324` の `Data.Enabled = false` は `IsOccupied` に守られていないため、交換直後の所持枠減少でキャラが恒久的に除外される。加えてベル移動・家テレポート・リログも起動しうる |
| **窓7** | ClearInFlight → Idle（Release を通らない） | **無制限**（抑制されたまま） | なし（抑制が残るので AutoRetainer が動けない） | **【実害・逆方向】** Auto Collector は Idle に見えるのに AutoRetainer は抑制されたまま。プラグイン再読込か AutoRetainer の UI で Cancel を押すまで AutoRetainer が永久に止まる |

**窓7 の再現手順（確認済み）**: `WaitOutcome` または `ConfirmDialog` の最中に状況タブの「確認したのでクリアする」を押す → `ClearInFlight`（`:370-387`）が `Plugin.C.InFlight = null`（`:378`）にする。Step が `Error` でないため `:381` の分岐に入らず Step は据え置き → 次 tick で `:1723-1727`（WaitOutcome）または `:2093-2097`（ConfirmDialog）が `Release()` を通さず `Step = Idle`。ボタンは `AC/Ui/MainWindow.cs:426-428` と `:909-911` の **2 箇所**あり、どちらも Step でゲートされていない。

**プラグイン/ゲームのクラッシュ**: 抑制は残らない。`IPC.Suppressed` は非永続の static（`AR/Modules/IPC.cs:16`）で、AutoRetainer の Config に保存経路がない。Auto Collector の通常アンロードでも `AC/Plugin.cs:259` の `Release()` が Framework ハンドラ解除の直後にあるため残らない。

---

## 4. 足りていない防御と対策（優先度順）

### P0 — 実害があり、修正が小さい

**P0-1. `ResumeAutoDuty` の抑制解除順序を直す**
現状 `AC/…/ExchangeExecutor.cs:1042` の `Release()` が `AutoDuty.TryRun`（`:1056`）とショップクローズ（`:1064`）の両方より前にある。`:1042` を削除し、`FinishAfterExchange` の `CloseOwned` 後にある `:1066` の `Release()` だけに寄せれば窓6 が消える。コメント（`:1039-1041`）は「AutoDuty がループ間処理で AutoRetainer を呼ぶため先に解く」と説明しているが、AutoDuty のループ間処理は次の周回まで走らないため、`TryRun` の直前で足りる。

**P0-2. `ClearInFlight` の抑制リークを塞ぐ**
`ClearInFlight`（`:370-387`）を Step が `Error` のときだけ受け付けるようにするか、`:1725` / `:2095` の Idle 遷移を `ReleaseHeldControl()` 経由にする。入口は `AC/Ui/MainWindow.cs:426-428` と `:909-911` の 2 箇所なので、UI 側で Step を見てボタンを出し分ける手もある。

**P0-3. キャラクタ同一性の検証を入れる**
`TryGetContentId` は既にあるが、唯一の参照は `:842`（ベンチャー残り秒数の問い合わせ）だけである。`RequestWithTravel` の時点で ContentId を記録し、`TickSuppressExternal` / `TickStopAutoDuty` / `TickArmed` の入口で照合して不一致なら `Abort` する。これで窓1・窓4 の実害が消える。

### P1 — 実害があるが、影響範囲は限定的

**P1-4. 抑制が実際に立っているかを再確認する**
`AC/Ipc/AutoRetainerIpc.cs:71` の `TryGetSuppressed` は現在 `AC/Ui/MainWindow.cs:996` の表示専用。Armed の直前と WaitOutcome の周期処理で確認し、`SuppressedByUs == true` なのに `GetSuppressed()` が false を返したら立て直す（または Fail する）。1 セッション最大 200 回（`:151`）× 1.2 秒（`:211`）で 6 分以上抑制を保持するため、その全期間が無防備になりうる。

**P1-5. `Release()` 失敗時に `SuppressedByUs` を落とす／再試行する**
`AC/Ipc/AutoRetainerIpc.cs:95-107` は成功時にしか `SuppressedByUs = false` にしない。AutoRetainer のリロード中に `Release()` を呼ぶと true に固着し、以後 `:875` の分岐で `Suppress()` がスキップされ、**抑制していないのに抑制済みと誤認したまま動き続ける**。

**P1-6. `ReleaseHeldControl` でも自分が開いた UI を閉じる**
`:2208-2250` は `Cleanup`（`:419-487`）と違い `CloseOwned` 群を持たない。Armed の検証失敗（ShopMismatch / CostMismatch / CurrencyMismatch）で `ShopExchangeCurrency` が開いたまま残り、AutoDuty も同時に再開される。AutoRetainer 側の `BailoutManager.MonitoredAddons`（`AR/Modules/BailoutManager.cs:18-39`）に `ShopExchangeCurrency` は含まれないため、誰も掃除しない。

**P1-7. 待機ループに再評価と打ち切りを入れる**
窓4（`:840-858`）と窓5（`:861-872`）のループ内で `SafetyGuard.IsSafeToStart` とエリア・キャラの一致を毎回確認する。あわせて `:748` で設定した 60 秒の `stepDeadlineUtc` を `TickSuppressExternal` で実際に読み、超過したら `AutoRetainerIpcBroken` で Fail する。

### P2 — 予防的、または実害の成立条件が狭い

**P2-8. `AethernetHop` の完了判定に目的地の一致を足す**
現在の完了条件は「Lifestream が暇＋10 単位以上移動＋画面準備完了＋操作可能」（`TickAethernetHop`、`:1134-`）で、目的エーテライトも `TerritoryType` も見ていない。テレポートで別エリアへ飛ばされた場合も「移動 10 単位以上」は成立する。

**P2-9. 開始前チェックに AutoRetainer の状態を加える**
`AC/Automation/SafetyGuard.cs` は Condition しか見ておらず、`MultiMode.Enabled` もベル近傍も判定しない。`TryGetMultiModeStatus`（`AC/Ipc/AutoRetainerIpc.cs:44-45`）は定義済みだが呼び出し箇所がゼロである。MultiMode が有効な場合、上表 2-2 の #7（ログイン画面での自動復帰）と #8（Mordion Gaol）は抑制で防げないため、開始時に警告して MultiMode の無効化を促すのが現実的な対処になる。

**P2-10. `Cleanup` の逆干渉を絞る**
`Lifestream.TryAbort()`（`:434-437`）は自分が AethernetHop を発行した場合に限定し、`Svc.Targets.Target = null`（`:468`）は自分が設定した NPC がターゲットの場合に限定する。`Lifestream.Abort()` は `P.TaskManager.Abort()` + `followPath.Stop()` の全体中断（`LS/IPC/IPCProvider.cs:71-75`）なので、AutoRetainer 側のテレポートを巻き添えにする。

**P2-11. `AddonOwnershipTracker` の所有権を「自分の操作で開いたか」に近づける**
`IsClaiming` はセッション中ずっと true（`:617`）で、`AC/Automation/AddonOwnershipTracker.cs:61-69` は「claim 中に開いたアドレス」を記録するだけである。**なお、`Callback` や `AtkValue` の変化を監視するフックは一つもないため、現在の実装では「他プラグインが `Callback.Fire` した」という外部改変を検出することは原理的にできない。**「外部改変を検出して Fail する」防御案を採るなら新規実装が必要である。

**P2-12. `EvaluateOutcome` の帰属検証（低優先）**
`:1792-1793` は上限のない片側不等式 2 本のみで、変化の原因が自分の交換であることを検証していない。ただし現時点では実害の成立経路がない — Auto Collector の通貨は必ずトームストーン（`AC/Game/TomestoneService.cs` の `TryResolveItemId` 一本）であり、AutoRetainer にトームストーンを減らすコードは存在せず、報酬アイテムを減らす経路（委託・売却・分解・破棄）は `SchedulerMain` 経由で抑制で止まる。**抑制を外すと直ちに実害化する**ため、抑制の維持が実質的な防御になっている。

---

## 5. 「抑制を使わない」選択をした場合に何が起きるか

`AC/Config.cs:125` の `SuppressAutoRetainer` を false にする（または AutoRetainer 未導入）と、`TickSuppressExternal` は `:830-835` で丸ごとスキップされ `StopAutoDuty` へ直行する。**二段 IsBusy 確認もベンチャー譲りも行われなくなる**点に注意が必要である。

その結果:

**a) WaitOutcome が最も重い被害を受ける — 【実害】**
`SchedulerMain` が生きるため、15 秒の監視窓の中でアイテム委託（`TaskEntrustDuplicates`）・売却（`TaskVendorItems`）・分解（`TaskDesynthItems`）・破棄（`TaskRecursiveItemDiscard`）・ベンチャー報酬回収が走りうる。**正常に成功した交換で `rewardAfter` が届かなくなり、`ExchangeUnexpectedDelta` → `FailUnresolved`。`InFlight` が未解決のまま残り、`CanRequest` が false になって MonitorService まで含めた全機能が停止する**（ユーザーが手動でクリアするまで）。これが抑制を維持する最大の理由である。

**b) ConfirmDialog の安全設計が迂回される — 【条件付き】**
MiniTA の `SkipItemConfirmations`（`AR/PluginData/Config.cs:147` 既定 true）が売却系 SelectYesno に Yes を押し、`GCContinuation.ConfirmExchange` が本文の**部分一致**（`AR/Lang.cs:342` に "Exchange" / "よろしいですか？"）で Yes を押す。Auto Collector が `RespectDisabledButtons = true` と本文照合で「押さない」と判断したダイアログを押される可能性が生じる。

**c) Navigate が競合する — 【実害】**
`/automove` + `/lockon` による競合走行で最大 180 秒走ってから失敗する。さらに `BailoutManager` のスタック検知（15 秒）が `Vnavmesh.Stop()` + `Vnavmesh.Reload()` を呼ぶと、Auto Collector の経路が無言で消える。`Nav.Reload` はナビメッシュの再構築なので復帰にも時間がかかる。**Auto Collector 自身のスタック判定も 15 秒**（`AC/Automation/NavigationService.cs:45`）で、ほぼ同時に発火する。

**d) Interact / SelectMenu が失敗を繰り返す — 【条件付き】**
ターゲットの奪い合いで `InteractFailed`、SelectString を `-1` で閉じられて `SelectMenu` が進まない。ただし**誤った NPC への対話や誤った選択肢の選択は起きない**（設計で守られている）。

**e) Armed だけは影響を受けない**
`Framework.Update` の単一スレッド逐次実行と、AutoRetainer が `ShopExchangeCurrency` に AddonLifecycle を張っていないことにより、抑制の有無にかかわらず 1 呼び出し完結は保たれる。

**f) 抑制を使わないことで得られるもの（トレードオフ）**
AutoDuty のループ間処理は `AD/AutoDuty.cs:1005-1013` で `AutoRetainer_IPCSubscriber.IsBusy()` を `int.MaxValue` タイムアウトで待つ。抑制中は `SchedulerMain.PluginEnabled` が false のままなので `IsBusy` が true にならず、**この待ちが `AutoRetainerHelper` のタイムアウト（`AD/Helpers/AutoRetainerHelper.cs:24`、10 分強）まで解けない**（ヘルパの `ActiveHelperBase` が自動 Stop するため、Auto Collector の `Release()` を待たずに最終的には復帰する）。Auto Collector 側は `WaitForAutoDutyBetweenLoopActions`（既定 true）でこれを回避しているが、リテイナーが多いキャラでは 120 秒の打ち切りが足りない可能性がある。

**総合判断**: 抑制を外す選択は推奨できない。(a) の全機能停止が最も重く、しかも復旧にユーザーの手動操作を要する。逆に抑制で残る問題（窓6 の `Data.Enabled = false`、窓7 のリーク、キャラ同一性）はすべて **Auto Collector 側の 3 つの小さな修正（P0-1〜P0-3）で解消できる**。

---

## 付録 A. 調査中に棄却した主張（採用していないもの）

以下は 3 領域の調査で報告されたが、実ソースと突き合わせて誤りと判定されたものである。再調査の重複を避けるために記録する。

1. 「AutoRetainer は vnavmesh を一切使わない（grep 0 件）」→ **誤**。`AR/Modules/BailoutManager.cs:192-193` に `Vnavmesh.Stop(); Vnavmesh.Reload();` が実在する。grep パターンが大文字小文字を区別していたことによる誤検出。
2. 「GC 印章交換が『通貨減＋報酬増』を同時に満たして誤成功を作る」→ **誤**。Auto Collector の通貨は `TomestoneService.TryResolveItemId` 一本で決まり、GC 印章やギルを通貨に選ぶ経路が UI・監視のどちらにも存在しない。
3. 「委託・売却・分解・破棄は抑制で止まらない」→ **誤**。これらの enqueue は全て `SchedulerMain.cs` 内にあり、`SchedulerMain.Tick()` の唯一の呼び出し元は `AR/AutoRetainer.cs:597`（抑制ゲート内）である。
4. 「GC 納品完了後に必ずテレポートする」→ **誤**。`AR/PluginData/Config.cs` の `TeleportAfterGCExchangeMulti = true`（既定）により実条件は `MultiMode.Active` になり、抑制で止まる。
5. 「`FPSManager.Tick` はゲーム状態を変更しない」→ **誤**。`FPSInActive` / `Fps` を実際に書き換え、ChillFrames に停止要求を出す。抑制と無関係に発火する。
6. 「`ShutdownOnSubExhaustion` が `TickScheduler` で予約される」→ **誤**。`MultiMode.cs:258-275` は Tick 内で直接実行しており、`if(Active)` の内側なので抑制で完全に止まる。予約を作るのは `ExitOnSubCompletion` だけ。
7. 「窓3（安全判定〜抑制）は 200 ms で、抑制では防げない」→ **誤**。実際は 1 tick ≒ 100 ms であり、そこで動きうる MultiMode / SchedulerMain / RetainerSense はいずれも抑制対象である（この窓では「まだ抑制していない」ことが問題なのであって「抑制で防げない」わけではない）。
8. 「`Suppress()` が `IsLoaded` の 5 秒キャッシュのせいで抑制せずに成功ログを出す」→ **到達不能**。`:830` で同一 tick 内に先に `IsLoaded` を読んでおり、キャッシュ期限も同時に更新されるため `:830` true / `:877` false という状態は作れない。
9. 「`AddonOwnershipTracker` が外部改変を検出できるか未確認」→ **検出できない**。登録しているのは `PostSetup` / `PreFinalize` だけで、`Callback` や `AtkValue` を監視するフックがない。
10. 「`SafetyGuard` が MultiMode 有効時に開始を拒否しているかもしれない」→ **していない**。Condition しか見ておらず、`TryGetMultiModeStatus` も未使用である。

## 付録 B. 実測が必要な未解決事項

1. **`ShopExchangeCurrency` 表示中に `ConditionFlag.Occupied` 系が立つか**。立つなら窓6 で MultiMode のリログ・ベル移動・GC 納品は `IsOccupied()` に阻まれ、残る実害は `MultiMode.cs:322` の `Data.Enabled = false`（`IsOccupied` 非依存）だけになる。立たない場合、窓6 の危険度は大きく上がる。`EC/GenericHelpers/GenericHelpers.cs:502-527` の `IsOccupied` はアドオン名を見ておらず Condition のみなので、ソースからは断定できない。
2. **英語クライアントの交換確認ダイアログ本文に "Exchange" が含まれるか**。含まれる場合、抑制を外した状態で `AR/Lang.cs:342` の部分一致が確実に誤爆する。日本語クライアントの実測例（`AC/…/ExchangeExecutor.cs` の TryFindConfirmDialog 周辺のコメント）しか手元にない。
3. **`Callback.Fire(ShopExchangeCurrency, …)` が同期的に `ShopExchangeCurrencyDialog` / `SelectYesno` の生成まで進むか**。同期生成なら Auto Collector 自身の `OnPostSetup` が `TickArmed` の内側で走る（安全側だが挙動の理解として要確認）。
4. **AutoRetainer の `SellItemHook` / `RetainerItemCommandHook`（`AR/Internal/Memory.cs`）がネットワークスレッドで発火してインベントリ関連の状態を書き換えるか**。Armed の単一スレッド保証はゲームメインスレッドの `Framework.Update` についてのものであり、パケットディスパッチ経路のフックは対象外である。
5. **`VoyageScheduler.Enabled` の現在値を外部から読む IPC が存在しない**。UI チェックボックス経由で立ちっぱなしになった場合、Auto Collector から確認する手段がない。`AutoRetainer.PluginState.DisableAllFunctions` で強制的に落とすことは可能だが、ユーザー設定を破壊するため採用可否は要判断。