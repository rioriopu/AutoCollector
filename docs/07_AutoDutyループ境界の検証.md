## 結論

ユーザー提案の中核（**AutoDuty を最後まで走り切らせ、全周回終了後に Auto Collector が動く**）は実ソース上で成立する。ただし前提のうち 3 点に条件付き・不成立の部分がある。推奨は「提案の方向を採用し、`LoopTimes` の書き換えだけはやめる」形。一時停止方式は設計前提そのものが実ソースと矛盾しているため放棄する。

---

## 1. 提案の前提の検証

### 1-1.「最終周のあとループ間処理が行われない」→ **条件付きで成立する（既定設定では成立）**

`ClientState_TerritoryChanged` の最終周分岐（`AutoDuty.cs:825-838`）:

```csharp
825: else
827:     TaskManager.Enqueue(() => Svc.Log.Debug($"Loops Done"), "Loop-Debug");
828:     TaskManager.Enqueue(() => { States &= ~PluginState.Navigating; }, "Loop-RemoveNavigationState");
829:     TaskManager.Enqueue(() => PlayerHelper.IsReady, int.MaxValue, "Loop-WaitPlayerReady");
831:     TaskManager.Enqueue(() =>
833:         if (this.Configuration.ExecuteBetweenLoopActionLastLoop)
834:             this.LoopTasks(false);      // ← true ならループ間処理が走る
835:         else
836:             this.LoopsCompleteActions(); // ← false ならループ間処理は走らない
837:     }, "Loop-LoopCompleteActions");
```

`ExecuteBetweenLoopActionLastLoop` の既定は `false`（`Windows/Config.cs:1141`）。UI ラベルは "Run on last Loop"。したがって**既定のままなら最終周後に AutoRetainer / GC 納品は走らない**。ユーザーがこのチェックを入れている場合は走る。

`LoopsCompleteActions()`（`AutoDuty.cs:1165-1255` 全文確認）は AutoRetainer も GC も一切呼ばず、TerminationActions と後始末だけを行う。最後に:

```csharp
1252: States      &= ~PluginState.Looping;
1253: CurrentLoop =  0;
1254: TaskManager.Enqueue(() => SchedulerHelper.ScheduleAction("SetStageStopped", () => Stage = Stage.Stopped, 1));
```

つまり **`IsLooping()` が false → 少し遅れて `IsStopped()` が true** の順で遷移する。

### 1-2.「1→2 の境目で AutoRetainer と GC 納品が行われる」→ **ユーザーの AutoDuty 設定に依存する。既定値のままでは行われない**

| 機能 | 実行条件 | 既定値 |
|---|---|---|
| AutoRetainer 連携 | `RetainersAvailable()` = `EnableAutoRetainer` かつ 最短ベンチャー残り秒 < `AutoRetainer_RemainingTime`（`IPC/IPCSubscriber.cs:41-50`） | `EnableAutoRetainer = false`、`AutoRetainer_RemainingTime = 0L`（`Config.cs:1206,1208`）→ **常に false** |
| GC 納品 | `AutoGCTurnin` かつ GC 階級 > 5（`AutoDuty.cs:1028`） | `autoGCTurnin = false`（`Config.cs:1184`） |

既定のままだと 1→2 の境目でも何も走らない。提案が意図どおり動くには、ユーザーが AutoDuty 側でこの 2 つを有効化し、`AutoRetainer_RemainingTime` に 0 より大きい値を入れている必要がある。

### 1-3.「2→END で Auto Collector のプリセットが有効になる」→ **現状のコードでは有効にならない（自動交換が永久に発火しない）**

これが提案の最大の穴。Auto Collector 側のゲート:

- `Automation/ExternalAutomationGate.cs:25-28` — `TryIsStopped(out stopped) && !stopped` のときだけ "AutoDuty" を「動作中」に数える
- `Automation/MonitorService.cs:82-86` — `RequireExternalAutomationRunning` が true で動作中が 0 件なら `return`
- `Config.cs:133` — `RequireExternalAutomationRunning` の既定は `true`

案が交換を始めたい「全周回終了直後」はまさに `IsStopped == true` になった瞬間なので、既定設定のままではゲートで弾かれ続ける。**ゲート側の改修が必須**。

### 1-4.「1→2 のとき抑制されているとベンチャー更新がされない」→ **懸念は正しく、実害はもっと大きい。ただし提案の構成なら特別な制御は不要になる**

抑制中に AutoDuty の `AutoRetainerHelper` が起動した場合の実挙動（実ソース確認）:

- `AutoRetainer.IsBusy` は `P.TaskManager.IsBusy || AutoGCHandin.Operation || Lifestream.IsBusy()`（AutoRetainer `Helpers/Utils.cs:1275`）のみで、抑制中は常に false
- `RetainersAvailable()` は OfflineData を見るだけなので抑制中も true のまま
- そのため AutoDuty 側は `AutoRetainerHelper.cs:86-92` の「Retainers available, restarting」分岐に落ちて起動試行を繰り返す
- 解除されるのはヘルパーのタイムアウトのみ = `600_000 + AutoRetainer_RemainingTime*60` ms（`AutoRetainerHelper.cs:24`）

→ **ベンチャーが更新されないだけでなく、AutoDuty が 10 分以上ベルの前で固まる。**

したがって設計原則は「**AutoRetainer の抑制と AutoDuty の稼働を絶対に重ねない**」。提案の構成では抑制をかけるのは AutoDuty が完全停止したあとだけになるので、「初回 ID クリア時のみ抑制解除」のような特殊制御は**不要になる**（ただし現行コードには先行抑制の経路 `ExchangeExecutor.cs:915-932` が残っているので削除が必要）。

### 1-5. 提案が成立するために追加で必要な AutoDuty 側の条件

`AutoExitDuty`（既定 true、`Config.cs:1016`）が false だと、最終周は `CheckFinishing()` の else 側（`AutoDuty.cs:1614-1615`）で**ダンジョン内のまま** `Stage = Stopped` になる。この場合 `ClientState_TerritoryChanged` は冒頭 `AutoDuty.cs:767` で弾かれ、1-1 の分岐に入らない。案の起点（街で停止）が作れないので、`AutoExitDuty` は true 必須。

---

## 2. 一時停止方式が効かない理由

### 2-1. 確認できたこと（すべて実ソース）

**設計前提そのものが誤っている。** `Ipc/AutoDutyIpc.cs:82-90` と `Config.cs:115-124` のコメントは「一時停止なら TaskManager の予約が残るので、再開すればループ間処理が続きから実行される」と書いているが、これは**こちらが交換のためにテレポートした時点で崩れる**:

```
AutoDuty.cs:767  if (this.Stage == Stage.Stopped) return;     ← Paused は通過する
AutoDuty.cs:794  if (t != CurrentTerritoryContent.TerritoryType)
AutoDuty.cs:798      TaskManager.Abort();                      ← 保持していたはずの予約が全消去
AutoDuty.cs:799-822  ループ間処理を先頭から積み直す
```

`Abort()` は `Tasks` / `ImmediateTasks` / `CurrentTask` を全消去する（ECommons `TaskManager.cs:110-116`）。つまり一時停止方式が Stop 方式より優れているとした唯一の根拠が、実際の使い方では成立しない。

**そもそも /ad pause は「停止」ではない。** `Stage.Paused` の setter が行うのは `PreviousStage` の保存、vnav パス停止、Follow 解除、`SetStepMode(true)`、`States |= Paused` の 5 つ（`AutoDuty.cs:130-137`）。`SetStepMode` は `Svc.Framework.Update` から TaskManager 自身の `Tick` を外すだけ（ECommons `TaskManager.cs:66-73`）で、以下は動き続ける:

- `Framework_Update` と `SchedulerHelper.ScheduleInvoker`（`AutoDuty.cs:456-457`）
- 全 ActiveHelper（`ActiveHelperBase.cs:112-113` が自前で `Framework.Update` を購読）— GCTurnin・Repair・Goto 系・AutoRetainerHelper・QueueHelper。`UpdateBase` の中断条件は Navigating / InDungeon / GotoHelper 実行中だけで、Paused を見ていない（`ActiveHelperBase.cs:151-169`）
- 特に QueueHelper は一時停止中でも DutyPop を承諾して**次の周回に入場する**（`QueueHelper.cs:258-263`, `65-73`）

**Stage が Paused から書き換わると復帰不能になる。** `/ad resume` は `Stage == Stage.Paused` が条件（`AutoDuty.cs:308-316`）。一方 Paused 中も `PreStageChecks` の `InteractablesCheck`（`AutoDuty.cs:1833-1848`）、`Condition_ConditionChange` の Unconscious / BetweenAreas 分岐（`AutoDuty.cs:844-868`）が Stage を上書きしうる。しかも Auto Collector の `TryIsPaused` は `States` のビット 4 を読んでいる（`AutoDutyIpc.cs:121-131`）が、`States` の Paused ビットが落ちるのは resume と `StopAndResetALL` だけなので、**Stage だけ外れた状態では「一時停止中」と誤報告し、resume は空振りする**（`Enums.cs:212-225` Stage.Paused=7 / `Enums.cs:260-268` PluginState.Paused=4）。

**緊急停止で一時停止が解除されないまま残るバグがある（実ソースで確認）:**

```
ExchangeExecutor.cs:438   this.Cleanup();
ExchangeExecutor.cs:522       this.returnContext = null;   ← Cleanup 内。resume は呼ばない
ExchangeExecutor.cs:442   this.Fail(...) → 2500 ReleaseHeldControl()
ExchangeExecutor.cs:2536      if (this.returnContext is { PausedAutoDuty: true } ...)  ← 既に null
```

`Abort()` 経由では AutoDuty が一時停止のまま放置される。

### 2-2. 分かっていないこと

- **ユーザーが実際に観測した不具合が上のどれだったかは特定できていない。** 実機ログを見ていない。ソース上の失敗経路として確認しただけである。
- コード内のコメント（`ExchangeExecutor.cs:750-756`, `788-791`）に「交換とリテイナー処理が周回ごとに交互になっていた」「全周回が終わるまで交換にたどり着けなかった」という過去の症状が記録されているが、これが上のどの経路によるものかの対応付けは推測であり、確認していない。
- `/ad resume` 送信後に実際に周回が再開したかを検証するコードが存在しない（`AutoDutyIpc.cs:94` は送りっぱなし）ため、過去の実行で解除が成功していたのかどうかも記録が残っていない。

---

## 3. 推奨方式

**ユーザー提案の方向（サイクル完了待ち方式）を採用する。ただし `LoopTimes` は Auto Collector から一切書き換えない。** 周回数はユーザーが AutoDuty の UI で設定した値をそのまま使い、Auto Collector は「AutoDuty が全周回を終えて停止した」ことを検知して交換し、終わったら `Run(territoryType, loops: 0)` で再開する。

理由:

1. **`Stage.Stopped` は AutoDuty が唯一「静止」を保証する状態である。** `StopAndResetALL`（`AutoDuty.cs:1919-1963`）で `States = None`、`TaskManager.Abort()`、全 ActiveHelper の `StopIfRunning()` が実行され、以後 `TerritoryChanged` も冒頭で弾かれる（`:767`）。こちらがテレポートしても AutoDuty は一切反応しない。一時停止方式で塞ぐ必要があった穴（ActiveHelper・QueueHelper・Stage 上書き・TerritoryChanged 再入）が**全部消える**。

2. **`IsStopped()` は合流点として理想的な意味を持つ。** 最終周にループ間処理を走らせる設定（`ExecuteBetweenLoopActionLastLoop = true`）であっても、`LoopsCompleteActions` は必ずその後に呼ばれ、`Stage = Stopped` はタスク列の最後（`AutoDuty.cs:1254`）。つまり **`IsStopped == true` は「ループ間処理も終了処理も全部終わった」を意味する**。Auto Collector 側でループ間処理の進捗を推測する必要がなくなり、`AutoDutySettleWaitSeconds` のような時間ベースの当て推量を全廃できる。

3. **AutoRetainer 抑制と AutoDuty 稼働が構造的に重ならない。** 1-4 で示した「抑制中に AutoRetainerHelper が起動して 10 分固まる」が原理的に起こらなくなる。ユーザーが心配した「初回 ID クリア時のみ抑制解除」という条件分岐も不要。

4. **公開 IPC だけで組める。** `IsStopped` / `Run` / `ContentHasPath` / `GetConfig` はすべて `IPC/IPCProvider.cs:43-49` の公開面。非公開フィールドのリフレクションは `CurrentLoop` の 1 つだけに減らせる（既存の `TryIsPaused` と同じ手口、`AutoDutyIpc.cs:116-131`）。

**`LoopTimes` を書き換えない理由**（提案の「2 回実施」を Auto Collector が指定しない理由）: `AutoDuty.Run` は `loops > 0` のとき `Configuration.LoopTimes = loops` を実行し（`AutoDuty.cs:897-898`）、`Configuration` は `EzConfig.Save()` でファイル全体を保存する（`Config.cs:1231-1234`）。AutoDuty 側の別経路（UI 操作、`SetConfig`、`TerminationKeepActive=false` の終了処理 `AutoDuty.cs:1188-1192`）で Save が走った瞬間に**ユーザーの周回設定がディスク上で潰れる**。現行の `AutoDutyIpc.TryRun` が `loops: 0` を渡している判断（`AutoDutyIpc.cs:180-187`）は正しいので、そのまま維持する。

---

## 4. 実装手順（Auto Collector 側）

### 4-1. 新規: `AutoCollector/Automation/AutoDutyCycleWatcher.cs`

毎フレーム軽量に AutoDuty の状態を追跡し、「サイクルが自然完了した」を検出する。

- `Tick()` で保持する状態:
  - `LastDutyTerritoryId` — `Player.IsInDuty` の間の `Svc.ClientState.TerritoryType` をラッチ（再開時に `Run` へ渡す。現行の `observedDutyTerritoryId` は `TickWaitingSafeWindow` 内でしか更新されず、本方式では使えない）
  - `lastSeenCurrentLoop` — AutoDuty 稼働中の `CurrentLoop`
  - `lastSeenLoopTimes` — `GetConfig("LoopTimes")`（数十秒間隔で十分）
- 遷移検出: 直前が `IsStopped == false` で今回 `true` になった瞬間に、`lastSeenCurrentLoop >= lastSeenLoopTimes` なら `CycleCompletedAtUtc = UtcNow` を記録する。**この比較が「ユーザーが手で /ad stop した」と「周回を走り切った」の唯一の識別子**（`Stage.Stopped` 自体は両者で同一）。
- 公開: `bool CycleJustCompleted(TimeSpan window)` / `uint LastDutyTerritoryId` / `void ConsumeCycle()`
- 呼び出しは `Plugin.cs` の Framework 更新から。現行の 100ms スロットル（`Plugin.cs:203, 224-231`）の**外側**に置くこと。100ms 間隔では `CurrentLoop` が 0 にリセットされる（`AutoDuty.cs:1253`）前に読み損ねる可能性がある。

### 4-2. `AutoCollector/Ipc/AutoDutyIpc.cs`

- **追加** `TryGetCurrentLoop(out int loop)` — `DalamudReflector.TryGetDalamudPlugin` → `GetField("CurrentLoop", Instance|NonPublic|Public)`。対象は `internal int CurrentLoop = 0;`（`AutoDuty.cs:75`）。既存の `TryIsPaused`（`:105-139`）と同じ形で書ける。
- **追加** `TryGetConfigInt(string key, out int value)` — `"LoopTimes"` 用。既存 `TryGetConfig`（`:33`）の薄いラッパ。
- **削除** `TryPause`（`:91`）/ `TryResume`（`:94`）/ `TryIsPaused`（`:105-139`）と `TryProcessCommand`（`:147`、他に用途が無ければ）。
- **維持** `TryRun`（`:186-187`、`loops: 0` のまま）、`TryStop`、`TryContentHasPath`。

### 4-3. `AutoCollector/Automation/ExternalAutomationGate.cs`

- コンストラクタに `AutoDutyCycleWatcher` を追加。
- `GetRunning()`（`:19-36`）は現状表示用にそのまま残す。
- **追加** `bool IsExchangeWindowOpen(out string detail)` — 「いずれかが動作中」**または**「AutoDuty のサイクルが直近 `AutoDutyCycleWindowSeconds` 以内に完了した」で true。

### 4-4. `AutoCollector/Automation/MonitorService.cs`

- `:76` の `automationRunning` を `this.automationGate.IsExchangeWindowOpen(out _)` に置き換える。
- `:82-86` のゲート文言を「AutoDuty の周回が終わるのを待っています」に更新。
- `:77` のポーリング間隔切り替え（`ActiveCheckInterval` 1 秒）はそのままで良い。窓が `AutoDutyCycleWindowSeconds` 幅になるので取りこぼさない。

### 4-5. `AutoCollector/Automation/ExchangeExecutor.cs`

- `RequestWithTravel`（`:582-664`）: `pauseSettleUntilUtc` / `stopAttempts` / `retainerWorkSeen` の初期化のうち一時停止関連を整理。
- `TickWaitingSafeWindow`（`:732-802`）:
  - `WaitForAutoDutyBetweenLoop()` の呼び出し（`:781`）と `UsePause` 分岐（`:794-797`）を削除。
  - 代わりに **(a)** `autoDuty.TryIsStopped(out var stopped) && stopped`、**(b)** `States` に `PluginState.Other`（= 8、`Enums.cs:267`）が立っていないこと、を待つ。(b) は ActiveHelper が残っていないことの保険。`States` を読む reflection は 4-2 で `TryIsPaused` を消す代わりに `TryGetStates(out int)` として残す。
- `PauseAutoDutyForExchange`（`:810-856`）と `WaitForAutoDutyBetweenLoop`（`:861-` 以降、先行抑制 `:915-932` を含む）を**削除**。
- `TickStopAutoDuty`: 「本来停止しているはずだが、万一動いていたら `TryStop()`」というフォールバックだけに縮小。一時停止の検証（700ms 落ち着き待ち・`TryIsPaused` 確認・3 回再送）を削除。
- `TickResumeAutoDuty`（`:1242-1330`）: `PausedAutoDuty` 分岐（`:1248-1273`）を削除。`context.WasAutoDutyRunning`（`:1275`）を `context.ShouldRestartAutoDuty` に置換し、値は watcher の判定から入れる。`autoRetainer.Release()` → `TryContentHasPath` → `TryRun` の順序（`:1315-1329`）は**必ず維持**（1-4 の 10 分ハングを防ぐため）。
- `Cleanup()`（`:456-524`）: `this.returnContext = null;`（`:522`）を削除し、`returnContext` の破棄は `ReleaseHeldControl`（`:2596`）と `FinishAfterExchange`（`:1355`）に一本化する。これで `Abort()`（`:430-444`）経由でも再開判断が生きる。**この修正は方式に関係なく必要**。
- `ReleaseHeldControl()`（`:2527-2597`）: `handledByPause` 系（`:2532-2552`）を削除し、`ResumeAutoDutyOnFailure` による `TryRun` 再開だけを残す。
- `FinishAfterExchange()`（`:1333-1365`）: 一時停止解除の取りこぼし対策（`:1341-1352`）を削除。

### 4-6. `AutoCollector/Config.cs`

- **廃止**（読み込み互換のためフィールドは残し、UI から外して未使用にする）: `UseAutoDutyPause`（`:124`）、`WaitForAutoDutyBetweenLoopActions`（`:103`）、`AutoDutySettleWaitSeconds`（`:106`）、`SkipExchangeWhenBetweenLoopWaitExpires`（`:112`）。
- **追加**: `AutoDutyCycleWindowSeconds`（既定 180）、`RestartAutoDutyAfterCycle`（既定 true。`ResumeAutoDuty` `:91` を流用しても良いが意味が変わるので新設が明確）。
- **追加（起動時チェック）**: `TryGetConfig` で AutoDuty の `TerminationMethodEnum` / `ExecuteCommandsTermination` / `AutoExitDuty` を読み、`TerminationMethodEnum != "Do_Nothing"` なら設定画面と `AnomalyLog` に警告を出す。**この方式ではサイクルごとに TerminationActions が実行される**ため（`AutoDuty.cs:1169-1248`：ログアウト・`/xlkill`・`shutdown.exe -s -t 20`・`/ays multi e` を含む）、ここを黙って通してはいけない。

### 4-7. ドキュメント

`docs/` 側の裁定（一時停止で予約が保持されるという前提）を差し替える。根拠は本レポート 2-1 の `AutoDuty.cs:767 / 794 / 798`。

---

## 5. 確認してほしいこと

1. **AutoDuty の `LoopTimes` を今いくつにしているか。** この方式では「1 サイクル = LoopTimes 周」で交換が入る。1 のままだと毎周回ごとに交換に入る（動作はするが再起動コストが毎回かかる）。
2. **`TerminationMethodEnum` が `Do_Nothing` か。** `Logout` / `Kill_Client` / `Kill_PC` / `Start_AR_Multi_Mode` を設定している場合、この方式ではサイクルごとにそれが実行される。`ExecuteCommandsTermination` と `PlayEndSound` も同様。
3. **`AutoExitDuty` を true のままにできるか。** false だとダンジョン内で停止し、この方式の起点が作れない（1-5）。
4. **`EnableAutoRetainer` / `AutoRetainer_RemainingTime` / `AutoGCTurnin` の現在値。** 既定のままだと 1→2 の境目でも AutoRetainer も GC 納品も走らない（1-2）。提案の意図を満たすには AutoDuty 側で有効化が必要。
5. **`ExecuteBetweenLoopActionLastLoop`（UI 名 "Run on last Loop"）をどちらにするか。** false（既定）なら最終周後は AR/GC なしで即停止、true なら最終周後も AR/GC を実行してから停止する。どちらでも `IsStopped` で待てば正しく合流できるが、後者は交換開始が数分遅れる。
6. **Playlist モードを使っているか。** 使っている場合、`Run` はプレイリストを復元できず（引数は territoryType のみ、`IPCProvider.cs:43`）、この方式は非対応。
7. **サイクルごとに PreLoopActions が再走することを許容できるか。** `Run` は毎回、消費アイテム・推奨装備・修理・宿屋/兵舎帰還を積み直す（`AutoDuty.cs:929-965`）。
8. **最終周の途中で手動 `/ad stop` した場合の扱い。** 4-1 の `CurrentLoop >= LoopTimes` 判定では、最終周中の手動停止だけは自然完了と区別できない。この場合は交換が始まり、その後 AutoDuty が再起動される。許容できないなら `RestartAutoDutyAfterCycle` を false にして手動再開にする。
9. **（任意・未検証）** AutoDuty 側の `StopItemQty` / `StopItemQtyItemDictionary`（`AutoDuty.cs:731-737`）に交換対象の通貨と閾値を設定すると、閾値到達時点の周回の切れ目で AutoDuty 自身が停止する（`AutoDuty.cs:815-819`）。相性は良さそうだが、トームストーン類が `InventoryManager.GetInventoryItemCount` で数えられるかは未確認。試す場合は AutoDuty の UI から設定してほしい（Auto Collector から `SetConfig` で書くと `Plugin.Configuration.Save()` が走り設定が永続化されるため、こちらからは触らない方針にしたい）。