# 15. FATE 自動周回 — 設計

作成日: 2026-09-22
対象コミット: `d0a05000`（2026-09-22 21:50「納品が動かない 2 件を直す」）
位置づけ: `00_設計決定.md` を最上位の基準とし、本書はその下位に属する。矛盾があれば `00` を優先する。

---

## 0. この機能が何であるか

FATE を自動で探し、移動し、戦い、完了したら即座に次の FATE へ向かう。

### 既存機能との関係 — **独立している**

Auto Collector の既存機能（通貨監視 → 交換）と FATE 周回は**連動しない**。

```
  ┌──────────────────────┐    ┌──────────────────────┐
  │ 既存: 通貨監視→交換   │    │ 新規: FATE 周回       │
  │ AutoDuty/Artisan に   │    │ 単独で動く            │
  │ 相乗りする（従来どおり）│    │                      │
  └──────────────────────┘    └──────────────────────┘
              └────────┬───────────────┘
                       │ 共有するのは部品だけ
           ┌───────────▼────────────────────────┐
           │ VnavmeshIpc / LifestreamIpc        │
           │ AetheryteService / InteractionService│
           │ NpcLocationService / AutoRetainerIpc │
           │ AnomalyLog / IpcGateBase            │
           └────────────────────────────────────┘
```

**FATE 周回に AutoDuty は一切関係しない。** 移動は vnavmesh、
テレポートは Lifestream、戦闘は **BossModReborn（BMR）**。AutoDuty は登場しない。

### なぜ Auto Collector の中に入れるのか

**既存の部品を使い回すため**（ユーザー確認済み）。
移動・テレポート・NPC 操作・ベンチャー判定・異常ログといった土台が
すでに動作実績のある形で存在する。これを再実装しない。

プラグインとしては同じ `AutoCollector` に同居するが、
**機能としては独立**しており、片方を使わなくてももう片方は動く。

---

## 1. ユーザーの要望（2026-09-22 確認・原文の意図を保つ）

| # | 要望 | 備考 |
|---|---|---|
| 1 | FATE 自動周回（移動・**レベルシンク**・戦闘・FATE 探知） | §4.7 参照 |
| 2 | バディ（チョコボ）自動召喚 | |
| 3 | ギサールの野菜の数量検知、閾値以下なら購入 | |
| 4 | ベンチャー回収可能ならホームタウンへデジョンで戻る。リキャスト中ならテレポ | |
| 5 | 何らかの事情で街に戻ったら、用事が済んだら元いたエリアへテレポで戻る | |
| 6 | 死亡したら自動的に街に戻る（任意で選択可・**初期値は待ち**） | |
| 7 | パーティを組んでも FATE 周回可能 | **同期はしない**。各自が自由に動く |
| 8 | FATE 戦闘終了したら素早くその場から脱出 | **最重視** |
| 9 | バイカラージェムが閾値を超えたら、ベリルで納品証に交換して戻る | §5.5 参照・2026-09-22 追加 |

### 納品（Collect）FATE の扱い — ユーザー指定の挙動

```
討伐
  └─ 特定のアイテムを自動取得（鞄には増えない＝キーアイテム欄）
       └─ 取得数が 10 個溜まる
            └─ 指定 NPC へ自動移動   ※移動中は敵に攻撃されても無視
                 └─ アクセス（納品）
                      ├─ 達成度 100% → FATE が終わってなくても即離脱
                      └─ 100% 未満
                           ├─ 殴ってきている敵がいる → 殴り返す
                           └─ 敵がいなくなった → FATE 中心部へ戻って攻撃
```

- 目安として **25 個納品すれば終わる**（ユーザー実測）
- **地面に落ちているアイテムは拾わない**
- 納品 FATE は**やる**（スキップしない）

### 挙動の自然さについて（ユーザー方針）

> FATE に入ったら、即全力で BMR/RSR で攻撃する。100% になったら即 FATE を脱出すればいい。
> bot っぽい動きは、FATE 完了後にその場に 5 秒程度留まっている方が bot っぽいから、
> その挙動は避けたい。

したがって **戦闘中の人間らしさは追求しない**。
自然さ＝**完了後に棒立ちしないこと**。「ゆらぎを与える」類の機能は入れない。

---

## 2. 既存資産の棚卸し（何を作らないか）

作る前に、**Auto Collector が既に持っているもの**を確定させる。
ここを間違えると同じものを二重に作る。

### 2.1 そのまま使い回すもの

| 必要なもの | 使う既存部品 | 場所 |
|---|---|---|
| **エリア内の移動（到着判定つき）** | **`NavigationService`** | `Automation/NavigationService.cs` |
| 移動の生 IPC | `VnavmeshIpc.TryMoveCloseTo` / `TryStop` | `Ipc/VnavmeshIpc.cs` |
| テレポート | `LifestreamIpc.TryTeleport` | `Ipc/LifestreamIpc.cs` |
| テレポ先の解決 | `AetheryteService` | `Game/AetheryteService.cs` |
| NPC への接近・会話 | `InteractionService` | `Automation/InteractionService.cs` |
| NPC 座標の解決と学習 | `NpcLocationService` | `Game/NpcLocationService.cs` |
| **開始してよいかの判定** | **`SafetyGuard`** | `Automation/SafetyGuard.cs` |
| ベンチャー回収可否 | `AutoRetainerIpc.CheckCollectableVenture` | `Ipc/AutoRetainerIpc.cs` |
| ショップ購入 | `ShopService` | `Automation/ShopService.cs` |
| 異常の記録 | `AnomalyLog` | `Diagnostics/AnomalyLog.cs` |
| 新規 IPC の土台 | `IpcGateBase` | `Ipc/IpcGate.cs` |

#### 特に重要な 3 つ

**`IpcGateBase`** は **fail-closed**（取得に失敗したら「進めない」側に倒す）という
方針を持っている。新しく作る `BossModIpc` もこれを継承する。

**`NavigationService`** は生の `VnavmeshIpc` を直接使わずにこれを使う。
理由がコメントに明記されている:

> vnavmesh は経路探索に失敗しても例外を握り潰してログを出すだけで、
> 経路が空のまま終わる。つまり「移動失敗」と「移動完了」が IPC の状態としては同じになる。
> そのため距離とタイムアウトの確認が必須になる。

`MoveStatus` は `Moving` / `Arrived` / `Stuck` / `Unreachable` / `Failed` の 5 値。
**`Unreachable`（近づけるところまでは行った）を失敗として扱わない**という判断も入っている。
FATE 周回の移動はすべてこれを通す。**自前で距離判定を書かない。**

**`SafetyGuard`** は開始可否の判定を持つ。
ただし**そのままは使えない**（後述 §4.2 の注記）。

### 2.2 新規に必要なもの

| # | 要望 | 新規実装 | 規模 |
|---|---|---|---|
| 1 | 戦闘の起動・停止 | `Ipc/BossModIpc.cs` | 小 |
| 1 | FATE 探知・選択 | `Game/FateScanner.cs` | 中 |
| 1,8 | FATE 周回の状態機械 | `Automation/FateRunner.cs` | 大 |
| 2,3 | バディ召喚・ギサール | `Automation/BuddyService.cs` | 小（移植） |
| 4,5 | ベンチャー・街での用事 | `FateRunner` 内に内包 | 中 |
| - | 設定画面 | `Ui/FateTab.cs` | 中 |

**新規の IPC は BMR だけ。** 移動・テレポ・NPC 操作は既存で足りる。

### 2.3 外部の参考実装（コードは借りない・考え方だけ借りる）

`C:\ソース\FF14-FATE調査_20260922_185922\sources\XeldarAlz__FFXIV-AutoFATEGrind\` は
**AGPL-3.0** のため、Auto Collector に取り込むとライセンスが伝播する。

> **本設計ではコードを一切コピーしない。** 読んで得た「挙動の知識」だけを使う。
> 具体的には、FATE の状態遷移と報酬の扱い。
> （BMR の IPC は手元の実ソースで直接確認したので、こちらは参照していない）
> これらは事実であって著作物ではない。

一方、以下は**自分のコードなので移植してよい**:
- `C:\ソース\ARankHuntTourAssistant\`（バディ召喚）
- `C:\ソース\MogColleEndlessBattle\`（ベンチャー判定・既に AutoCollector にも同等品あり）

---

## 3. 設計決定

`00_設計決定.md` の D-1〜D-5 に続く番号を振る。

### D-6. FATE 周回は「もう 1 つの Runner」として実装する

**採用**: `Automation/FateRunner.cs` を新設し、既存 Runner 群
（`GoalRunner` / `CollectableCycleRunner` / `CraftRunner` / `RetainerRestockRunner`）と
同じ作法で実装する。

#### 根拠

既存 Runner はすべて同じ形をしている:

```csharp
public enum XxxStep { Idle, ..., Done, Error }

public sealed class XxxRunner(AnomalyLog anomalyLog, ...)
{
    public XxxStep Step { get; private set; }
    public void Start(...);
    public void Stop(...);
    public void Tick();      // Framework.Update から毎フレーム
}
```

`Plugin.cs` でインスタンス化し、`Framework.Update` で `Tick()` を回す。
FATE 周回も同じ形にすれば、既存の UI・ログ・停止処理の作法がそのまま乗る。

#### 破棄した案

- **AutoFATEGrind をフォークして別プラグインにする**:
  AGPL-3.0 が伝播し、Auto Collector 全体のソース公開義務が生じる。
  経緯は `C:\ソース\_設計\AutoFate\AutoFate_設計_2026-09-22.md` に保存。
- **SomethingNeedDoing の Lua で書く**:
  Auto Collector の安全条件・停止処理と連動できない。

### D-7. FATE 周回と交換機能は連動させない

**採用**: `ExternalAutomationGate` には FateRunner を**加えない**。
FATE 周回中に通貨が閾値に達しても、交換は発火しない。

#### 根拠

ユーザー確認（2026-09-22）で「完全に独立させる」と決定。
FATE 周回は FATE を回すことだけを行う。

#### 破棄した案

- **FateRunner を `ExternalAutomationGate` の 3 つめの相手にする**:
  中断・復帰の仕組みを流用できる利点はあるが、ユーザーの意図と異なる。
  また既存の交換処理（`ExchangeExecutor` 4,325 行）に手を入れることになり、
  動作実績のある部分を壊す危険がある。

#### 同時に動かした場合の扱い

両方を同時に有効にすることは**できる**が、互いを知らない。
`ExternalAutomationGate` は FATE 周回を「動作中の自動化」として認識しないため、
理屈のうえでは交換が割り込む余地がある。

**対策**: FATE 周回が動いている間は、`MonitorService` の発火を止める。
`ExternalAutomationGate` とは別の、単純な排他にする:

```
FateRunner.IsRunning が true の間:
    交換の新規発火を行わない（すでに進行中の交換は最後まで進める）
```

これは「連動」ではなく「**互いに邪魔をしない**」ための最小限の措置。
実装は `MonitorService` の発火判定に 1 行加えるだけで済む。

> **確認したい点**: この排他が不要（同時に走ってよい）なら削る。
> 実装時にユーザーへ確認する。

### D-8. 戦闘は BossModReborn（BMR）に委譲する（自前の戦闘 AI を作らない）

**採用**: `Ipc/BossModIpc.cs` を新設し、**BossModReborn** を対象にする。
ユーザー指定（2026-09-22）により、本家 BossMod ではなく **BMR を使う**。

`00_設計決定.md` D-4「独自のナビメッシュ・経路探索は実装しない」と同じ思想。
**戦闘 AI も作らない。**

#### IPC 接頭辞は `BossMod.`（BMR でも変わらない）— 実ソースで確認済み

`BossmodReborn/BossMod/Framework/IPCProvider.cs:578`:

```csharp
var p = Service.PluginInterface.GetIpcProvider<TRet>("BossMod." + name);
```

**BMR は接頭辞を `BossModReborn.` に変えていない。** `BossMod.` のまま。
これは既存の `reference_bossmod_ipc` メモの内容とも一致する。

ただし `IpcGateBase.IsLoaded` は `InternalName` で導入判定を行うため、
**InternalName は `BossModReborn`** を指定する必要がある。
接頭辞（`BossMod.`）と InternalName（`BossModReborn`）が**食い違う**点に注意。

```csharp
public sealed class BossModIpc(AnomalyLog anomalyLog)
    : IpcGateBase("BossModReborn", anomalyLog)   // ← 導入判定はこちら
{
    private const string Prefix = "BossMod.";     // ← IPC 名はこちら
}
```

#### 使う IPC（すべて `BossmodReborn` の実ソースで実在を確認済み・2026-09-22）

| 用途 | IPC 名 | シグネチャ |
|---|---|---|
| プリセット有効化 | `BossMod.Presets.SetActive` | `(string name) → bool` |
| プリセット解除 | `BossMod.Presets.ClearActive` | `() → bool` |
| 現在のプリセット | `BossMod.Presets.GetActive` | `() → string?` |
| 一時方針の追加 | `BossMod.Presets.AddTransientStrategy` | `(string preset, string module, string track, string value) → bool` |
| 一時方針の解除 | `BossMod.Presets.ClearTransientStrategy` | `(string preset, string module, string track) → bool` |
| **AI プリセット設定** | `BossMod.AI.SetPreset` | `(string name)` |
| **AI プリセット取得** | `BossMod.AI.GetPreset` | `() → string` |
| **移動の一時停止** | `BossMod.AI.PauseMovement` | `(bool pause)` |
| **移動中か** | `BossMod.AI.IsNavigating` | `() → bool` |

後半 4 つは初版の設計時に把握していなかったもの。
**`AI.PauseMovement` は §4.4 の納品トリップで使える**（BMR に移動させない）。

`IpcGateBase` を継承し、fail-closed にする。
**BMR が未導入なら FATE 周回機能ごと無効にする**（戦えないため）。

#### RotationSolverReborn / WrathCombo との関係

ユーザー環境には BossModReborn / RotationSolver / WrathCombo が導入済み。
**BMR を対象にする**（移動と回避まで面倒を見るため FATE 周回に適する）。
RSR / Wrath はコマンド経由で併用できるが、**F1 では扱わない**。

### D-9. BMR の `FateUtils` を使う（納品処理を自前で書かない）

**採用**: 納品 FATE の処理は、BMR が既に持っている `FateUtils` モジュールに任せる。

#### 実ソースで判明したこと（2026-09-22 確認・`BossMod/Autorotation/MiscAI/FateUtils.cs`）

BMR には **"FATE helper"** というモジュールがあり、4 つのトラックを持つ:

| トラック | 選択肢 | 意味 |
|---|---|---|
| **`Handin`** | Enabled / Disabled | **10 個以上で自動的に FATE アイテムを納品する** |
| **`Collect`** | Enabled / Disabled | **戦闘の代わりに地面の FATE アイテムを拾いに行く** |
| **`Sync`** | None / Enable / Disable | **レベルシンクを自動で入れる／切る** |
| **`Chocobo`** | Enabled / Disabled | **残り 60 秒未満ならチョコボを再召喚する** |

`TurnInGoldReq = 10` が定数として定義されている。
**ユーザー指定の「10 個溜まったら納品」と完全に一致する。**

#### これにより設計が大幅に変わる（初版からの重要な訂正）

初版では §4.4 として納品処理（NPC へ移動 → 話しかけ → 納品）を
**自前で実装する**設計だった。**それをやめる。**

| 初版の設計 | 訂正後 |
|---|---|
| 所持数を自分で数える | `FateUtils` の `Handin` に任せる |
| 納品 NPC へ自分で移動する | 同上（`Hints.InteractWithTarget` で BMR が行う） |
| `AutoTarget → Passive` を自分で立てる | 同上（`GetGoal` が `HandIn` のとき戦闘しない） |
| `FateUtils.Collect → Disabled` を立てる | **意味が違った**（後述） |

#### `Collect` トラックの意味を取り違えていた（初版の誤り）

初版では `Collect → Disabled` を「地面のアイテムを拾わない設定」と書いた。
**方向は合っていたが、理由の理解が誤っていた。**

実ソース（`GetGoal`）で判明した正しい意味:

```csharp
// pick up stuff
return strategy.Option(Track.Collect).As<Flag>() == Flag.Enabled && !Player.InCombat
    ? CollectFateGoal.Pickup : CollectFateGoal.None;
```

`Collect = Enabled` は「**戦闘の代わりに**地面の FATE アイテムを拾いに行く」。
つまり**討伐せずに収集で進める**モードであり、拾得の有無を切り替えるものではない。

ユーザー要望は「**アイテムを拾わず敵を倒す**」なので:

| トラック | 設定値 | 理由 |
|---|---|---|
| `Handin` | **Enabled** | 10 個溜まったら納品させる（要望どおり） |
| `Collect` | **Disabled** | 地面のアイテムを拾いに行かせない（要望どおり） |
| `Sync` | **None** | レベルシンクはゲーム任せ（§4.7） |
| `Chocobo` | **Disabled** | バディ管理は自前で行う（§5.1・理由は後述） |

**この 4 つを FATE 周回開始時に一度立てるだけでよい。**
納品トリップごとに立てたり解除したりする必要がない。
初版の「`try/finally` で必ず解除する」という設計も**不要になった**。

#### `Chocobo` トラックを使わない理由

BMR の `Chocobo` は「残り 60 秒未満なら再召喚」だけを行い、
**ギサールの野菜が尽きたときに買いに行かない**（在庫があるかだけ見る）。

```csharp
&& World.Client.GetInventoryItemQuantity(ActionDefinitions.IDMiscItemGreens.ID) > 0
```

ユーザー要望 3 は「**数量検知して閾値以下なら購入**」なので、
自前の `BuddyService`（§5.1）で行う。**BMR 側は Disabled にして二重動作を防ぐ。**

> **未確認**: BMR の `Chocobo` を Disabled にしても、
> 他のプリセットや設定で再召喚が走らないか。F1 で確認する（§8-⑪）。

### D-10. FATE 完了の検知は「進捗 100%」を使う

**採用**: `FateState` の変化やオブジェクト消滅を待たず、**進捗 100% の検知で離脱**する。

#### 根拠（要望 8 が最重視項目であるため）

検知点は早い順に 3 つある:

1. `Progress >= 100` に達した瞬間 ← **これを使う**
2. `FateState` が `Running` から外れた瞬間
3. FATE オブジェクトが消滅

2 と 3 は 1 より数秒遅い。ユーザーが避けたい「完了後にその場に留まる」挙動になる。

#### 報酬との両立（実装の要）

**ユーザー指定により確定した事実**（2026-09-22）:

> 納品 FATE は達成 100% になった瞬間、すぐには報酬をもらえない。
> 0% → 納品（全プレイヤーの納品合計 25 個くらい）→ 100% →
> **1 分の待機時間** → 0 秒になったら**報酬獲得 ＆ FATE 消失**

したがって **100% と報酬着地は別の時点**である。

| | 100% の瞬間 | その 1 分後 |
|---|---|---|
| 納品 FATE | まだ報酬は入っていない | **報酬獲得・FATE 消失** |

**通常 FATE はユーザー指定により確定した**（2026-09-22）:

> 通常 FATE は、敵を倒していくだけで達成度が溜まっていき、
> **100% になると即報酬獲得 ＋ FATE 消失**。

つまり納品 FATE と挙動が違う:

| | 100% の瞬間 | その後 |
|---|---|---|
| **通常 FATE** | **即座に報酬獲得・FATE 消失** | 待つ必要なし |
| **納品 FATE** | まだ報酬は入っていない | **1 分後**に報酬獲得・FATE 消失 |

**帰結**: 通常 FATE なら 100% 検知後に**すぐ別エリアへテレポートしてよい**。
エリアを跨ぐ制約がかかるのは**納品 FATE を回したときだけ**。

#### 本設計の方針

「即離脱」＝**FATE の円から出て次の FATE へ向かうこと**であり、
エリアを出ることではない。

```
100% 検知
  ├─ 円を出て、同じエリアの次の FATE へ向かう   ← これが「即離脱」
  └─ 報酬待ちとして記録し、着地を追跡する（§4.4）
       着地するまで別エリアへテレポートしない
```

これで**待ち時間ゼロ・取りこぼしゼロ**が両立する。
1 分間その場で棒立ちすることもない（ユーザーが避けたい挙動）。

詳細な追跡方法は §4.4 を参照。

### D-11. パーティ同期は実装しない

**採用**: パーティを組むことを妨げないが、足並みを揃える機能は持たない。

要望 7 は「組んでも周回できる」であり、「同期する」ではないと確認済み。
各キャラが独立して FATE を選び、独立して離脱する。

これにより、同期方式で生じる「離脱タイミングのずれによる分裂」問題が発生しない。
**要望 8（最速離脱）と完全に両立する。**

パーティ招待の自動拒否は**実装しない**（組むのを妨げないため）。

---

## 4. FateRunner の設計

### 4.1 状態

既存 Runner の作法に合わせ、`enum` + `Tick()` で実装する。

```csharp
public enum FateStep
{
    Idle,

    /// <summary>対象エリアにいない。テレポートする。</summary>
    Traveling,

    /// <summary>周回するエリアにいるが、狙える FATE が無い。待つ。</summary>
    Waiting,

    /// <summary>狙う FATE を決めた。そこへ移動している。</summary>
    MovingToFate,

    /// <summary>FATE の中で戦っている。</summary>
    Fighting,


    /// <summary>100% を検知した。次の FATE へ向かい始めている。</summary>
    Leaving,

    /// <summary>死亡した。設定に従って待つか戻る。</summary>
    Dead,

    /// <summary>街での用事（ベンチャー・買い物）へ抜けている。</summary>
    Errand,

    Done,
    Error,
}
```

### 4.2 毎 Tick の判定順序

**順序が仕様である。** 上から順に判定し、最初に当てはまったものを実行する。

#### 安全条件は `SafetyGuard` をそのままは使えない（重要）

`SafetyGuard.IsSafeToStart()` は**交換用**の判定であり、以下を「危険」と返す:

| 条件 | 交換では | FATE 周回では |
|---|---|---|
| `InCombat`（戦闘中） | 危険 | **正常**。戦うのが仕事 |
| `Casting`（詠唱中） | 危険 | **正常**。技を撃っている |
| `IsAnimationLocked` | 危険 | **正常**。技のモーション中 |
| `Unconscious`（戦闘不能） | 危険 | **専用処理へ**（Dead） |
| Duty 中 / カットシーン / ロード中 / 操作不能 / トレード中 | 危険 | **危険**（同じ） |

そのまま使うと**戦闘に入った瞬間に周回が止まる**。

**`SafetyGuard` に FATE 用の判定を追加する**（新しいクラスを作らない）:

```csharp
/// <summary>
/// FATE 周回を続けてよい状態か。
///
/// 交換用の IsSafeToStart とは判定が異なる。戦闘・詠唱・アニメーションロックは
/// FATE 周回では正常な状態なので弾かない。戦闘不能は専用処理へ回すため
/// ここでは弾かず、呼び出し側が先に判定する。
/// </summary>
public static bool IsSafeToRunFate(out string reason, out StartWaitKind kind)
```

弾くのは **Duty 中・カットシーン・ロード中・エリア移動中・操作不能・トレード中**のみ。
これは `00_設計決定.md` §7 の意図（自動操作が成立しない状況で動かさない）を保ったまま、
FATE 周回の性質に合わせたもの。

```
1. 安全条件を満たさない（SafetyGuard.IsSafeToRunFate が false）
       → 何もしない

2. 死亡している
       → Dead へ

3. 街での用事が必要 かつ 納品報酬の着地待ちが無い
       → Errand へ

4. 対象エリアにいない
       → Traveling へ

5. 交戦中の FATE がある
       ├─ 進捗 100%  → Leaving へ（★最優先）
       └─ それ以外    → Fighting を継続
            （納品 FATE の納品トリップは BMR が内部で行う。
              こちらは状態を持たない ─ D-9）

6. 狙える FATE がある
       → MovingToFate へ

7. 狙える FATE が無い
       → Waiting へ
```

**3 の「納品報酬の着地待ちが無い」が重要。**
報酬が着地する前にエリアを離れると報酬が消える（§4.4）。

### 4.3 最速離脱（要望 8・最重視）

速さの鍵は検知ではなく**先読み**にある。

```
Fighting 中、進捗が閾値（既定 70%）を超えたら
  └─ 次の FATE 候補を決めておく
  └─ その座標を控えておく

進捗 100% を検知した瞬間（Leaving）
  ├─ vnavmesh.TryStop()          いまの移動を即止める
  ├─ BossModIpc.ClearActive()    戦闘 AI を即解除  ★これが無いと敵を追い続ける
  └─ vnavmesh.TryMoveCloseTo(控えておいた座標)  すぐ動き出す
```

**`ClearActive()` を呼ばないと戦闘 AI が敵を追い続け、離脱が目に見えて遅れる。**
これが「完了後に留まらない」を実現する要。

同じエリアに次の FATE が無い場合のみ、別エリアへのテレポートを考える。
ただし**納品報酬の着地待ちがあるうちはエリアを出ない**。

### 4.4 納品 FATE — BMR に任せる（初版から全面改訂）

#### 納品 FATE の時間の流れ（ユーザー指定・2026-09-22）

> 0% → 納品（全プレイヤーの納品合計数が 25 個くらい）→ 100% →
> **1 分の待機時間** → 0 秒になったら**報酬獲得 ＆ FATE 消失**

```
  0%  ─────納品─────→ 100% ─────1分の待機─────→ 0秒
                        ↑                        ↑
                    ここで即離脱              ここで報酬が入る
                    （FATE はまだ消えない）    （FATE が消える）
```

**重要な帰結が 2 つある。**

**① 100% の瞬間には報酬が入っていない。**
初版は「100% で報酬は確定済み」と書いていたが**誤り**。
100% から実際に報酬が入るまで**約 1 分の空白がある**。

**② その 1 分の間、報酬はまだ手元に無い。**
この間にエリアを離れると報酬が消える
（FATE のウィンドウが閉じるときに配られるため、その場に居ないと受け取れない）。

#### 本設計の方針 — 待たずに次へ行くが、エリアは出ない

ユーザー要望は「100% になったら即離脱」であり、これは**円から出ること**を指す。
1 分間その場に立ち尽くすのを避けたい、という意図（§1 の「棒立ちが bot っぽい」）。

したがって:

| 行動 | 本設計 |
|---|---|
| 100% 検知後、円の中で待つ | **しない**（棒立ちを避ける） |
| 100% 検知後、同じエリアの次の FATE へ行く | **する**（これが「即離脱」） |
| 報酬が入る前に別エリアへテレポートする | **しない**（報酬を捨てることになる） |

つまり「**同じエリア内で次の FATE を回しながら、1 分後の報酬を受け取る**」。
待ち時間ゼロ・取りこぼしゼロが両立する。

#### 報酬の着地を追跡する

100% を検知したら、その FATE を「報酬待ち」として記録する。

```csharp
private sealed record PendingFateReward(
    uint     FateId,
    int      StartEpoch,      // 同じ FateId の再湧きと区別するため
    DateTime DeadlineUtc);    // 1分 + 余裕（既定 90 秒）
```

毎 Tick で確認する:

| 観測 | 判定 |
|---|---|
| FATE が消えた | **報酬が入った**。記録を消す |
| 別エリアへ移った | **報酬を失った**。記録を消し、ログに残す |
| 期限を過ぎた | 記録を消す（異常として `AnomalyLog` に残す） |

**この記録がある間は、別エリアへのテレポートを行わない**（§4.2 の判定順序 3）。

> **注意**: 報酬が実際に入ったかを「所持金・経験値が増えたか」で
> 検証する方法もあるが、**FATE 以外の要因でも増える**ため確実ではない。
> 本設計では「FATE が消えた」を報酬着地の合図とする。

#### 納品処理そのものは BMR に任せる（D-9）

初版では納品 NPC への移動・会話を自前で実装する設計だったが、
**BMR の `FateUtils` が既に持っている**ことが実ソースで判明した（D-9）。

FATE 周回の開始時に、一度だけ以下を立てる:

| モジュール | トラック | 値 | 意味 |
|---|---|---|---|
| `FateUtils` | `Handin` | **Enabled** | 10 個溜まったら自動で納品しに行く |
| `FateUtils` | `Collect` | **Disabled** | 地面のアイテムを拾いに行かない |
| `FateUtils` | `Sync` | **None** | レベルシンクはゲーム任せ |
| `FateUtils` | `Chocobo` | **Disabled** | バディ管理は自前で行う（§5.1） |
| `AutoTarget` | `FATE` | **Enabled** | FATE 内の敵を優先する |

**`FateStep.DeliveringFateItems` は不要になった。**
納品は BMR が戦闘の合間に勝手に行うため、状態機械に専用の状態を持たない。

ユーザー指定の細かい挙動（移動中は敵を無視する／納品後に殴り返す／
敵がいなくなったら中心部へ戻る）は、**すべて BMR の `FateUtils` と
`AutoTarget` が内部で行っている**。こちらで再実装しない。

#### 進捗と所持数の読み取り（監視のためだけに使う）

納品そのものは BMR に任せるが、**100% の検知**は自前で行う（最速離脱のため）。

納品アイテムの所持数を見る必要があれば、キーアイテム欄を走査する
（`GetInventoryItemCount` では取れない）:

```csharp
var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.KeyItems);
// container->Size ぶん走査し、GetItemId() == itemId の GetQuantity() を合計
```

対象アイテム ID は `Fate` シートから取る（D-2 の Lumina 解決に従う）。

**実測で確定した列名**（2026-09-22 / ゲームデータ ver 2026.09.15.0000.0000）:

```csharp
var row = Svc.Data.GetExcelSheet<Fate>()?.GetRowOrDefault(fateId);
var itemId = row?.TurnInEventItem.RowId ?? 0u;   // ← TurnInEventItem
```

`Fate` シートには `ReqEventItem` と `TurnInEventItem` の **2 つ**がある。
納品するのは `TurnInEventItem` のほう。**`EventItem` という列は存在しない。**

BMR 側も同じものを使っている（`FateUtils.GetGoal` の `Utils.GetFateItem`）。

### 4.5 FATE の選び方

```
除外するもの:
  - 残り時間が閾値未満（既定 120 秒）
  - 進捗が閾値超（既定 90%）… 到着前に終わる
  - レベル差が範囲外（設定で有効化）
  - このセッションで詰まった FATE（ブラックリスト）
  - ユーザーが除外した FATE

並べ替え（上から優先）:
  1. ボーナス有（「運命の悪戯」が付いていない場合）
  2. 進捗が高い     … 早く終わる
  3. 残り時間が短い … 逃すともったいない
  4. 距離が近い
```

**優先順位は設定で変えられるようにする。** 既定値は上記。

### 4.6 周回するエリアの選び方（2 階層）

ユーザー指定（2026-09-22）:

> 周回するエリアは自由に任意で決めたい。大きな括りだと
> 「新生エオルゼア」「蒼天エリア」「紅蓮エリア」「漆黒エリア」「暁月エリア」「黄金エリア」
> の 6 エリアに分かれており、各エリアには大体 6〜7 マップがある。
> 大枠としてこれら 6 エリアを選択もできて、細かく各マップを指定して
> マップをぐるぐる周回することを考えている。

したがって**拡張単位とマップ単位の 2 階層**で選べるようにする。

#### データ源（実測で確定）

`TerritoryType.ExVersion` が実在する（**実測日 2026-09-22 / ゲームデータ ver 2026.09.15.0000.0000**）。
`00_設計決定.md` D-2「Lumina 実行時解決」に従い、**一覧をハードコードしない**。

```
絞り込み条件:
    TerritoryType.IsInUse == true
    TerritoryType.TerritoryIntendedUse.RowId == 1   （通常フィールド）
    PlaceName が空でない
グループ化:
    TerritoryType.ExVersion.RowId
```

これで新しい拡張が来ても**コード修正なしに**選択肢へ現れる。

#### 実測結果（2026-09-22 時点・確認用。コードには埋め込まない）

| ExVersion | 名称 | 件数 | TerritoryId |
|---|---|---|---|
| 0 | A Realm Reborn（新生） | 18 | 134,135,137,138,139,140,141,145,146,147,148,152,153,154,155,156,180,250 |
| 1 | Heavensward（蒼天） | 6 | 397〜402 |
| 2 | Stormblood（紅蓮） | 6 | 612,613,614,620,621,622 |
| 3 | Shadowbringers（漆黒） | 6 | 813〜818 |
| 4 | Endwalker（暁月） | 6 | 956〜961 |
| 5 | Dawntrail（黄金） | 6 | 1187〜1192 |

黄金エリアの 6 件はユーザーの挙げた
「オルコ・パチャ / コザマル・カ / ヤクテル樹海 / シャーローニ荒野 / ヘリテージファウンド / リビング・メモリー」
と一致する。

> **注意**: 新生の 18 件には `250 Wolves' Den Pier`（ウルヴズジェイル）など
> FATE が発生しないマップが混ざる。**「フィールドである」ことと
> 「FATE が湧く」ことは別**。実際に FATE が定義されているマップだけに
> 絞る方法は未確定（§8-⑩）。
> 当面は一覧に出したうえで、**周回して FATE が見つからなければ次のマップへ移る**
> 挙動（`FateSwapZoneWhenEmpty`）で吸収する。

#### 設定の持ち方

```csharp
/// <summary>周回するマップ。拡張で選んでも、最終的にはここへ展開して保存する。</summary>
public List<uint> FateZones = [];
```

**拡張単位の選択は UI の入力補助**とし、設定には**マップ ID の一覧として保存**する。

理由: 拡張 ID で保存すると、パッチで同じ拡張にマップが追加されたとき、
ユーザーが選んだ覚えのないマップで勝手に周回が始まる。
`00_設計決定.md` §1「ID 優先」の考え方とも合う。

UI では「拡張を選ぶ → その配下のマップに一括でチェックが入る → 個別に外せる」
という形にする。

#### 周回の順序

選んだマップを**順に回る**。1 つのマップで FATE が尽きたら次へ移る
（`FateSwapZoneWhenEmpty` が true のとき）。
最後まで行ったら先頭へ戻る。

### 4.7 レベルシンクへの対応

ユーザー指定（2026-09-22）:

> レベルシンクとは、高レベルのキャラクターが低レベルのコンテンツ
> （FATE やダンジョンなど）に参加する際、レベルやステータスを
> 自動でその場に合わせた適正値に一時的に引き下げるシステム。

**これはゲームが自動で行うものであり、プラグインが実装するものではない。**
FATE の円に入ると自動でかかり、出ると戻る。

#### 設計上の意味 — 考慮すべき制約が 3 つある

| 項目 | 影響 |
|---|---|
| **FATE の選び方** | 高レベルのキャラが低レベル FATE に入ると、シンクで弱体化する。効率は落ちるが**完了はできる**。§4.5 のレベル差フィルタで除外するかはユーザーの選択 |
| **戦闘プリセット** | シンク後のレベルで使えない技がある。BMR / RSR 側が判断するので**こちらは何もしない** |
| **所要時間の見積もり** | シンクの有無で討伐速度が変わる。§4.3 の先読み閾値（既定 70%）はレベル差によって最適値が変わる可能性がある |

#### 実装すること

**`FateLevelFilterEnabled` を既定 `false` にする。**
高レベルキャラが低レベル FATE を回すのは正常な使い方であり、
既定で弾くとユーザーの意図しない挙動になる。

弱体化を避けたいユーザーだけがフィルタを有効にする。

> **本項は新規の実装をほとんど伴わない。** レベルシンクはゲームの仕組みであり、
> プラグイン側は「シンクされる前提で FATE を選ぶ」だけでよい。

### 4.8 詰まり対策

`00_設計決定.md` §2「固定時間待機の禁止」
「タイムアウトは失敗であって次へ進む合図ではない」に従う。

| 事象 | 対処 |
|---|---|
| 移動が 60 秒進まない | その FATE をセッション内ブラックリストに入れ、次を選ぶ |
| 同じ FATE で 2 回詰まった | 恒久的に飛ばす |
| 5 分間まったく前進しない | **安全停止して通知**（自動復帰しない） |
| エリアへテレポできない | 2 回でエリアを替え、3 回で停止 |

「前進」の定義: FATE を 1 つ完了した／エリアが変わった／実際に移動した。

---

## 5. 周辺機能

### 5.1 バディ召喚とギサールの野菜（要望 2・3）

`Automation/BuddyService.cs` を新設。
**自作 `ARankHuntTourAssistant` から移植する**（自分のコードなのでライセンス問題なし）。

| 用途 | API |
|---|---|
| 残り時間（秒） | `UIState.Instance()->Buddy.CompanionInfo.TimeLeft` |
| 召喚 | `AgentInventoryContext.Instance()->UseItem(4868, InventoryType.Invalid, 0, 0)` |
| 所持数 | `InventoryManager.Instance()->GetInventoryItemCount(4868)` |

- ギサールの野菜 = **アイテム ID 4868**
- 効果 = **30 分召喚、上限 60 分**（召喚中なら 30 分加算）
- 価格 = **36 ギル**（NPC 購入）

出典: https://universalis.app/market/4868 , https://ffxiv.gamerescape.com/wiki/Gysahl_Greens

#### 移植元に書かれている知見（必ず守る）

> ギサール使用後は 3 秒待って `CompanionInfo.TimeLeft` が**実際に増えたこと**を確認する。
> 増えていなければ最大 3 回再試行する。

`UseItem()` が成功を返しても召喚されないことがある、という実測。
**戻り値だけを信じてはいけない。**
これは `00_設計決定.md` §3「UI 操作の実行をもって成功としない」と同じ原則。

#### 動かすタイミング

戦闘外でのみ実行する。**100% 離脱直後には挟まない**（離脱が遅れるため）。
次の FATE へ移動を開始したあとに行う。

#### 購入

既存の `ShopService` を使う。ギサールの野菜はギル購入なので
`ShopExchangeCurrency` ではなく `Shop` アドオン。

**購入 NPC はユーザー指定により確定**（2026-09-22）:

> リムサのエーテライト付近にいる「ブルゲール商会 バンゴ・ザンゴ」から購入できる。
> アクセス → アイテムの購入 → ギサールの野菜。座標は X:-62.1 Y:18.0 Z:9.4。

**ゲームデータで裏取り済み**（ver 2026.09.15.0000.0000）:

| 項目 | 値 |
|---|---|
| NPC | **ENpcResident 1001787** 'Bango Zango'（title: Brugaire Consortium） |
| エリア | **territory 129**（Limsa Lominsa Lower Decks / リムサ・ロミンサ：下層） |
| 座標 | **X=-62.1 Y=18.0 Z=9.4** |

**ユーザーの申告した座標と、ゲームデータの Level シートの値が完全に一致した。**

`Level` シートに座標があるため、既存の `NpcLocationService` で解決できる
（実地学習を待つ必要がない）。

会話の手順は「アクセス → **アイテムの購入** → ギサールの野菜」。
最初の選択肢を通す必要があるので、`ExchangePreset.MenuHint` と同じ考え方で
メニュー文字列のヒントを持たせる。

> **未確認**: `Shop` アドオンの構造（数量指定の仕方）は実機でダンプが必要（§8-④）。
> 既存の `CallbackRecorder` / `CollectablesShopReader` と同じ手法で採取できる。

### 5.2 ベンチャー回収と帰還（要望 4）

判定は既存の `AutoRetainerIpc` をそのまま使う。

```csharp
autoRetainer.CheckCollectableVenture() == VentureState.Collectable
```

#### 帰還手段の選択

```
デジョン（ActionID 6）が使えるか
    ActionManager.Instance()->GetActionStatus(ActionType.Action, 6) == 0
  ├─ 使える     → UseAction(ActionType.Action, 6)
  └─ リキャスト中 → LifestreamIpc.TryTeleport でホームタウンへ
```

`00_設計決定.md` D-4 に従い、テレポートは Lifestream に一本化する。

> **未確認**: デジョンのアクション ID は 6 と広く言われているが実機未確認（§8-③）。

#### AutoRetainer との協調 — 交換とは事情が違う

`00_設計決定.md` D-1 は「交換中に AutoRetainer を**抑制する**」という規則だった。
**FATE 周回のベンチャー帰還では抑制しない。逆に、動いてもらう。**

```
FATE 周回:  街へ帰る（ここまでが仕事）
AutoRetainer: ベンチャーを回収する（本来の仕事をさせる）
FATE 周回:  IsBusy が false になったら周回へ戻る
```

`SetSuppressed(true)` を立てると**ベンチャー回収そのものが止まる**
（`06_AutoRetainer干渉点.md` §0-1 のとおり SchedulerMain が止まるため）。
帰ってきた意味が無くなる。**ここで抑制してはいけない。**

#### 06_AutoRetainer干渉点.md から引き継ぐ注意

同文書が挙げた実害のうち、FATE 周回にも当てはまるもの:

| 干渉 | FATE 周回での影響 | 対処 |
|---|---|---|
| **MultiMode のキャラクタ変更**（`Lifestream.ChangeCharacter`） | 街に居る間に別キャラへログインされると、周回へ戻る文脈が別人のものになる | **帰還前にキャラクタを記録し、戻る前に同一性を検証する**。違っていれば安全停止 |
| **`BailoutManager` の `Vnavmesh.Stop()` / `Reload()`** | 街での移動中に経路を消される | `NavigationService` の `Stuck` 判定で検知できる。リトライする |
| `AutoGCHandin` が抑制外で動く | 街での用事と競合しうる | `IsBusy` を見て待つ（既存の二段確認と同じ作法） |

**キャラクタ同一性の検証は既存コードに無い**（同文書 §0-3 が「未実装」と明記）。
FATE 周回で街へ帰る機能を作る以上、ここは**新規に実装する**。

```csharp
// 帰還前に記録
private ulong errandContentId;   // Svc.ClientState.LocalContentId

// 周回へ戻る前に検証
if (Svc.ClientState.LocalContentId != this.errandContentId)
{
    // 別キャラになっている。周回へ戻らず安全停止する。
}
```

### 5.3 街での用事と復帰（要望 5）

FateRunner 内に専用の文脈を持つ。**既存の `ReturnContext` は流用しない**
（`ExchangeExecutor` 専用の状態が多く、切り離したほうが安全なため）。

```csharp
private sealed record FateReturnContext(
    uint    TerritoryId,   // 周回していたエリア
    Vector3 Position,      // 離脱したときの座標
    uint?   FateId);       // 追いかけていた FATE
```

用事が済んだら:
1. `TerritoryId` へ `LifestreamIpc` でテレポート
2. 必要なら `Position` へ vnavmesh で移動（設定で切替・既定はしない）
3. 文脈を破棄して通常の周回へ戻る

### 5.4 死亡時（要望 6）

```csharp
public enum FateDeathAction
{
    /// <summary>レイズを待つ。既定。</summary>
    Wait,

    /// <summary>すぐ街へ戻る。</summary>
    Return,

    /// <summary>ソロなら戻る、パーティなら待つ。</summary>
    Auto,
}
```

**既定は `Wait`**（ユーザー指定）。
待ち時間も設定値にする（既定 30 秒）。時間切れ後にどうするかも設定で選べる。

---

### 5.5 バイカラージェムの自動交換（要望 9・新規追加）

ユーザー指定（2026-09-22）:

> 「漆黒エリア」「暁月エリア」「黄金エリア」の FATE だけだが、FATE 攻略報酬で
> 「バイカラージェム」が得られる。鞄に入るアイテムではなく、スクリップや
> トームストーンに近い扱いのポイント。**最大所持限界が 1500 まで**で、
> それ以上入手しても持ち切れず 1500 のまま増えない。
>
> 任意の数値量（例えば 1400 以上）溜まったら、ソリューション・ナインの
> 広域交易商ベリルまでテレポ＆自動移動して、「バイカラージェム納品証【黄金】」を
> 最大数量交換して、交換後に先程のマップに戻る機能が欲しい。

**この機能は、既存の交換機能がほぼそのまま使える。**

#### 実測で確定した値（2026-09-22 / ゲームデータ ver 2026.09.15.0000.0000）

| 項目 | 値 |
|---|---|
| バイカラージェム | **ItemId 26807** / `StackSize = 1500` |
| 納品証【漆黒】 | ItemId 35833（Bicolor Gemstone Voucher） |
| **納品証【黄金】** | **ItemId 43961**（Turali Bicolor Gemstone Voucher） |
| 交換レート | **ジェム 100 個 → 納品証 1 枚** |
| 交換ショップ（黄金） | SpecialShop **1770736** / **1770746**（`UseCurrencyType = 4`） |
| 交換ショップ（漆黒） | SpecialShop 1770470 / 1770471 |
| **広域交易商ベリル** | **ENpcResident 1049082**（Gemstone Trader） |
| ベリルの座標 | **territory 1186（Solution Nine）X=-198.8 Y=0.9 Z=-4.3** |

**`StackSize = 1500` がそのまま所持上限**になっている。
既存の `CurrencyService` は上限を `Item.StackSize` から取る実装なので
（`06_AutoRetainer干渉点.md` の事実表にも記載）、**そのまま動く**。

#### 既存の交換機能で賄える部分（ユーザー指摘のとおり）

`ExchangePreset` は以下を既に持っている:

| 必要なもの | 既存のフィールド |
|---|---|
| 監視する通貨 | **`CurrencyItemId`**（スロット概念の無い通貨用。ここに 26807 を入れる） |
| 閾値（1400 以上で発火） | `Threshold`（`ThresholdMode.Fixed` / `Percentage` / `BeforeCap`） |
| 交換して得る品 | `Rewards`（`ExchangeEntry` の並び。ここに 43961 を入れる） |
| 交換所 NPC の指定 | **`PreferredNpcDataId`**（ベリルを指定する） |
| 最大数量まで交換 | `ExchangeMode.MaxOut`（交換可能な限り交換する） |
| 交換後に元の場所へ戻る | `ExchangeExecutor` の `ReturnContext` |

**つまり、新しい交換の仕組みは作らない。** UI も既存のプリセット画面がそのまま使える。

`ThresholdMode.BeforeCap`（上限までの残りが指定値を下回ったら）を使えば
「1400 以上で発火」は「上限 1500 まで残り 100 を切ったら」と表現できる。
ユーザーが分かりやすいほうを選べばよい。

#### ただし D-7（交換と FATE 周回は連動しない）と矛盾する

**ここが設計上の要注意点。**

D-7 では「FATE 周回中は交換を発火させない」と決めた。
しかしこの要望は**まさに「FATE 周回中に交換へ行く」**ことを求めている。

| | D-7 の決定 | 要望 9 |
|---|---|---|
| FATE 周回中の交換 | 発火させない | **バイカラージェムだけは発火させたい** |

##### 解決案: バイカラージェムだけを例外にする

FATE 周回が自分で面倒を見る。**既存の交換機能を「呼び出す」のではなく、
FATE 周回の「街での用事」（§5.3）の一種として扱う。**

```
FateStep.Errand の用事に 3 つめを加える:
  ① ベンチャー回収（§5.2）
  ② ギサールの野菜の購入（§5.1）
  ③ バイカラージェムの交換（本節）   ← 追加
```

こうすれば:

- D-7（交換機能と連動しない）は保たれる。`MonitorService` は関与しない
- 「元のマップに戻る」は §5.3 の `FateReturnContext` がそのまま使える
- 交換の実処理だけ `ExchangeExecutor` に依頼する形にできる

> **未確認・要判断**: 交換の実処理を `ExchangeExecutor` に依頼する場合、
> 既存コードに手を入れないという方針（§9「既存機能への変更点」）と両立するか。
> `ExchangeExecutor` は「外部周回を止めて→交換して→再開する」前提で
> 書かれているため、FATE 周回から呼ぶと想定外の経路に入る可能性がある。
>
> **代案**: バイカラージェムの交換だけを FateRunner 内に自前で実装する。
> 交換 1 種類・NPC 1 人・レート固定（100 → 1）なので、
> 既存の汎用交換ほど複雑にはならない。
>
> **F5 の着手時にユーザーへ確認する。**

#### 対象エリアの限定

バイカラージェムが手に入るのは **漆黒・暁月・黄金の 3 拡張のみ**
（ExVersion 3 / 4 / 5・§4.6 の表を参照）。

新生・蒼天・紅蓮のマップを周回しているときは、この機能を発火させない。
判定は `TerritoryType.ExVersion.RowId >= 3` で足りる。

> **未確認**: 漆黒より前の拡張の FATE でもバイカラージェムが出ないか。
> ユーザーの指定どおり 3 拡張に限定するが、実機で確認する（§8-⑬）。

---

## 6. 設定項目

既存の `Config.cs` に追記する。
FATE 周回は 1 セットだけなので、プリセット方式にはしない。

```csharp
// ---- FATE 周回 ----
public bool   FateEnabled              = false;

/// <summary>
/// 周回するマップ。拡張単位で選んでも、ここへ展開して保存する（§4.6）。
/// 拡張 ID では保存しない。パッチでマップが増えたときに
/// 選んだ覚えのないマップで周回が始まるのを防ぐため。
/// </summary>
public List<uint> FateZones            = [];
public bool   FateSwapZoneWhenEmpty    = true;    // FATE が無ければ次のマップへ

// 選び方
public int    FateMinTimeRemainingSec  = 120;
public int    FateMaxProgressPct       = 90;

/// <summary>
/// レベル差でFATEを絞るか。**既定は false**（§4.7）。
/// 高レベルキャラが低レベルFATEを回すのは正常な使い方であり、
/// レベルシンクで弱体化はするが完了はできる。
/// 弱体化を避けたいユーザーだけが有効にする。
/// </summary>
public bool   FateLevelFilterEnabled   = false;
public int    FateMaxLevelBelow        = 5;
public int    FateMaxLevelAbove        = 5;
public List<FateSortKey> FateSortOrder = [...];   // 既定は §4.5 の順

// 納品 FATE
public bool   FateCollectEnabled       = true;
public int    FateCollectHandInBatch   = 10;      // ユーザー指定
public bool   FateNeverPickUpGround    = true;    // 地面のアイテムを拾わない

// 離脱
public int    FatePrefetchPct          = 70;      // 何%から次を先読みするか

// 戦闘
public string FateCombatPreset         = "";      // BMR のプリセット名

// バディ
public bool   BuddyEnabled             = false;
public int    BuddyMinSecondsRemaining = 300;
public int    GysahlMinCount           = 10;
public int    GysahlBuyQuantity        = 99;
public bool   GysahlAutoBuy            = true;

// ベンチャー
public bool   FateVentureEnabled       = false;
public bool   FateVentureReturnHome    = true;

// 街での用事
public bool   FateErrandReturnToZone   = true;
public bool   FateErrandReturnToSpot   = false;

// 死亡時
public FateDeathAction FateDeathAction = FateDeathAction.Wait;
public int    FateRaiseWaitSeconds     = 30;

// 交換機能との排他（D-7）
public bool   FateBlocksExchange       = true;    // 周回中は交換を発火させない
```

---

## 7. 実装の段階（Phase）

既存の Phase 1〜4（交換機能）とは別系統。**F** を接頭辞にする。

| 段階 | 内容 | 検証 |
|---|---|---|
| **F1** | `BossModIpc` + `FateScanner` + `FateRunner` の骨格（探知・移動・戦闘・最速離脱） | **通常 FATE を 1 つ完了して離脱するまでの秒数を実測** |
| **F2** | 納品 FATE（§4.4） | **報酬が入るか実測**（§8-①②が確定する） |
| **F3** | バディ・ギサール（§5.1） | 召喚と購入が動くこと |
| **F4** | ベンチャー・街での用事・死亡時（§5.2〜5.4） | 街へ行って戻ること |
| **F5** | バイカラージェムの自動交換（§5.5） | 閾値で発火し、ベリルで交換して元のマップへ戻ること |
| **F6** | 設定画面（`Ui/FateTab.cs`）の作り込み | 設定が保存・反映されること |

**F1 と F2 を最優先にした。** ユーザーが最も重視する機能であり、
未確認事項（§8-①②）がここで解消するため。

F1 の前に **§8-⑤（BMR の IPC が実際に購読できるか）**を確認する。
ここが違うと F1 が丸ごと成り立たない。

---

## 8. 未確認事項

確認していないことを確認したように書かないため、ここに明示する。

1. ~~通常 FATE の報酬タイミング~~ → **ユーザー指定により解決**（下の確定情報を参照）

2. **報酬着地の検知方法が正しいか**
   §4.4 では「FATE が消えた」を報酬着地の合図としている。
   FATE が消える前に報酬が入る、あるいは消えても報酬が入らない場合がないか未検証。
   F2 で確認する。

3. **デジョンのアクション ID** — 6 と広く言われているが実機未確認。

4. **`Shop` アドオンの構造（数量指定の仕方）** — 実機でダンプが必要。
   購入 NPC と座標はユーザー指定＋ゲームデータで確定済み（§5.1）。

5. **BMR の IPC が実際に購読できるか**
   IPC 名とシグネチャは `C:\ソース\BossmodReborn` の実ソースで確認済み（D-8）だが、
   **実際に購読して呼んだわけではない**。
   特に **InternalName が `BossModReborn` で正しいか**（`IsLoaded` 判定に使う）。
   **F1 の最初に確認する。**

6. **最速離脱の実効速度** — 先読みがどれだけ効くかは未計測。F1 で実測する。

7. **交換機能との排他（D-7）が必要かどうか**
   同時に有効にしたとき、交換が FATE 周回に割り込んでよいのか。
   本設計では「割り込ませない」（`FateBlocksExchange = true`）としたが、
   **ユーザーの意図を実装時に確認する。**

8. **`SafetyGuard.IsSafeToRunFate` の判定漏れ**
   §4.2 で「戦闘・詠唱・アニメーションロックは弾かない」としたが、
   これ以外に FATE 周回中に起こりうる `ConditionFlag` を網羅できているかは未検証。
   F1 で実際に回して、想定外の停止が起きないか確認する。

9. **街に居る間のキャラクタ変更（§5.2）が実際に起こるか**
   `06_AutoRetainer干渉点.md` が理論上の経路として挙げているもので、
   FATE 周回の文脈で実際に踏むかは未検証。
   検証は難しいので、**起こる前提で対策だけ入れる**（安全側）。

10. **FATE が実際に湧くマップだけに絞る方法**（§4.6）
    `TerritoryIntendedUse == 1` で通常フィールドは取れるが、
    その中に FATE が発生しないマップ（`250 Wolves' Den Pier` など）が混ざる。
    `Fate` シートから territory を逆引きできるかは**未確認**。
    当面は `FateSwapZoneWhenEmpty` で吸収する。F1 で調べる。

11. **BMR の `FateUtils.Chocobo` を Disabled にしても再召喚が走らないか**（D-9）
    バディ管理は自前で行う（§5.1）ため BMR 側は止めたいが、
    他のプリセットや設定から再召喚が走る経路がないかは未検証。F1 で確認する。

12. **BMR の `FateUtils` が期待どおり納品してくれるか**（D-9）
    実ソースで `Handin` トラックの存在と `TurnInGoldReq = 10` は確認したが、
    **実機で動かしていない**。
    特にユーザー指定の細部
    （納品へ向かう途中は敵を無視する／納品後に殴り返す／敵が居なくなったら中心へ戻る）
    が BMR の実装で満たされるか。**F2 で最優先に確認する。**
    満たされないなら、その部分だけ自前で補う。

13. **バイカラージェムが漆黒より前の拡張でも出ないか**（§5.5）
    ユーザー指定では漆黒・暁月・黄金の 3 拡張のみ。
    判定は `TerritoryType.ExVersion.RowId >= 3` とするが、実機で確認する。

14. **バイカラージェムの交換を `ExchangeExecutor` に依頼するか、自前で実装するか**（§5.5）
    既存の交換処理は「外部周回を止めて→交換して→再開する」前提で書かれており、
    FATE 周回から呼ぶと想定外の経路に入る可能性がある。
    交換 1 種類・NPC 1 人・レート固定（100 → 1）なので自前実装も現実的。
    **F5 の着手時にユーザーへ確認する。**

### 実測で確定した事項（未確認から降格）

#### ゲームデータから（2026-09-22 / ver 2026.09.15.0000.0000）

`C:\ソース\exd-analyze` を流用した検証ツールによる実測。

| 事項 | 結果 |
|---|---|
| `TerritoryType.ExVersion` の実在 | **実在する**。拡張単位の選択がシートから引ける |
| `ExVersion` の内容 | 0=新生 / 1=蒼天 / 2=紅蓮 / 3=漆黒 / 4=暁月 / 5=黄金 の 6 件 |
| 各拡張のフィールドマップ | §4.6 の表のとおり。黄金 6 件はユーザーの挙げたマップと一致 |
| 納品 FATE のアイテム列名 | **`Fate.TurnInEventItem`**。`EventItem` という列は存在しない（初版の誤りを訂正） |
| バイカラージェム | **ItemId 26807** / `StackSize = 1500`（＝所持上限） |
| 納品証【黄金】 | **ItemId 43961**。ジェム **100 個 → 1 枚** |
| 納品証【漆黒】 | ItemId 35833。同じく 100 個 → 1 枚 |
| 交換ショップ（黄金） | SpecialShop 1770736 / 1770746（`UseCurrencyType = 4`） |
| 広域交易商ベリル | **ENpcResident 1049082** / territory 1186 / X=-198.8 Y=0.9 Z=-4.3 |
| バンゴ・ザンゴ | **ENpcResident 1001787** / territory 129 / X=-62.1 Y=18.0 Z=9.4（ユーザー申告と一致） |

#### BMR の実ソースから（2026-09-22 / `C:\ソース\BossmodReborn`）

> **上流の最新版に更新済み（2026-09-22）**
>
> 手元のソースは **`615b38b54`（2026-09-21）** に更新した。上流
> https://github.com/FFXIV-CombatReborn/BossmodReborn の main と同一。
>
> 更新前は `e3f1c8e92`（2026-09-16）で 58 コミット遅れていたが、
> 本設計が依拠する 3 ファイルは**更新の前後でハッシュが変わらなかった**:
>
> | ファイル | blob ハッシュ |
> |---|---|
> | `BossMod/Framework/IPCProvider.cs` | `ecb0d55b64fbcc6aad956de5ed726b041f3982df` |
> | `BossMod/Autorotation/MiscAI/FateUtils.cs` | `c940d1c94cf72cd1c70badc1487a80524590351b` |
> | `BossMod/Autorotation/MiscAI/AutoTarget.cs` | `53aeacc33b93a0cc59dd3d4a3ca3ba204e3cce36` |
>
> 公開 IPC も **62 件すべて同一**。`TurnInGoldReq = 10` と
> `Track { Handin, Collect, Sync, Chocobo }` も最新版で変わっていない。
>
> 58 コミットの中身はボスモジュール（UCOB / Crucible of the Unbroken /
> Dawntrail Savage など）の修正が大半。`Framework/Utils.cs` に変更があるが、
> `IsUnsynced` への `world.CurrentCFCID > 0` ガード追加と
> for 文の `i++` → `++i` の 2 点のみで、**FateUtils が使う
> `GetFateItem` / `IsPlayerSyncedToFate` は無変更**。
>
> **本設計に影響する変更は無い。**

| 事項 | 結果 |
|---|---|
| IPC 接頭辞 | **`BossMod.`**（BMR でも変わらない）。`IPCProvider.cs:578` |
| `Presets.*` の実在 | SetActive / ClearActive / GetActive / AddTransientStrategy / ClearTransientStrategy すべて**実在** |
| `AI.*` の実在 | SetPreset / GetPreset / PauseMovement / IsNavigating が**実在**（初版で把握していなかった） |
| `FateUtils` モジュール | **実在**。`Handin` / `Collect` / `Sync` / `Chocobo` の 4 トラック |
| 自動納品の閾値 | **`TurnInGoldReq = 10`**。ユーザー指定の「10 個」と一致 |
| `Collect` トラックの意味 | 「**戦闘の代わりに**地面のアイテムを拾いに行く」。拾得の有無を切り替えるものではない（初版の理解を訂正） |
| `AutoTarget` の `Passive` | `GeneralStrategy.Passive` として**実在**。`FATE` トラックもある |

#### ユーザーからの確定情報（2026-09-22）

| 事項 | 内容 |
|---|---|
| 納品 FATE の進行 | 0% → 納品（全員の合計 25 個くらい）→ 100% → **1 分待機** → 0 秒で**報酬獲得 ＆ FATE 消失** |
| 「レベシング」の意味 | **レベルシンク**（ゲームが自動でかける仕組み）。レベル上げではない |
| エリア選択 | 拡張 6 つの大枠と、配下マップの個別指定の**2 階層** |
| **通常 FATE の進行** | 敵を倒すだけで達成度が溜まり、**100% で即報酬獲得 ＋ FATE 消失**（納品 FATE と違い待ち時間なし） |
| バイカラージェム | 漆黒・暁月・黄金の FATE 報酬。上限 1500。閾値を超えたらベリルで納品証に交換したい |
| ギサール購入 NPC | リムサ下層のブルゲール商会 バンゴ・ザンゴ。X:-62.1 Y:18.0 Z:9.4 |

---

## 9. 既存文書との整合

| 文書 | 関係 |
|---|---|
| `00_設計決定.md` | **最上位**。D-6〜D-11 はその続き。§共通原則 1〜7 はすべて適用される |
| `06_AutoRetainer干渉点.md` | ベンチャー回収（§5.2）はここの規則に従う。新しい規則は作らない |
| `09_状況タブ再設計仕様.md` | FATE 周回の状態表示はここの方式に合わせる |
| `13_他プラグインの設定を変更する.md` | BMR のプリセットを触る場合はここの規則に従う |
| `14_呼び鈴へ自動で移動して話しかける.md` | 街での用事（§5.3）で呼び鈴を使う場合に参照 |

`07_AutoDutyループ境界の検証.md` は **FATE 周回とは無関係**（交換機能側の文書）。

### 特に注意すべき整合点

`00_設計決定.md` の共通原則は FATE 周回にもすべて適用される。
中でも見落としやすいもの:

- **§2 固定時間待機の禁止** — 「FATE に着いたら 3 秒待つ」のような実装をしない。
  状態の変化（戦闘に入った・進捗が動いた）を確認して遷移する。
  ただし §5.1 のギサール確認は「3 秒待って増えたか見る」であり、
  これは**固定時間待機ではなく結果の検証**なので原則に反しない。
- **§5 同期的な無限ループの禁止** — `Tick()` の中でループを回さない。
- **§6 緊急停止** — `/acc stop` で FATE 周回も止まること。
  BMR のプリセットと一時方針を**必ず解除**してから止める。
- **§7 安全条件** — カットシーン・ロード中・Duty 中・操作不能中は開始しない。
  ただし「戦闘中は開始しない」は FATE 周回には**適用しない**（戦うのが仕事のため）。
  詳細と根拠は §4.2 を参照。`SafetyGuard` に専用の判定を追加する。

### 既存機能への変更点（副作用の明示）

本設計は既存コードに以下の変更を加える。**それ以外は触らない。**

| 変更先 | 内容 | 既存機能への影響 |
|---|---|---|
| `Automation/SafetyGuard.cs` | `IsSafeToRunFate()` を**追加** | 無し（既存の `IsSafeToStart` は変更しない） |
| `Automation/MonitorService.cs` | 発火判定に 1 行追加（D-7 の排他） | `FateBlocksExchange = false` なら従来と同一 |
| `Config.cs` | FATE 用の設定を追加 | 無し（既定値で従来どおり） |
| `Plugin.cs` | `FateRunner` / `BossModIpc` / `BuddyService` の生成と `Tick` 登録 | 無し（`FateEnabled = false` なら何もしない） |
| `Ui/MainWindow.cs` | FATE タブの追加 | 無し（タブが 1 つ増えるだけ） |

**`ExchangeExecutor.cs`（4,325 行）には一切手を入れない。**
動作実績のある交換処理を壊さないための方針。

---

## 10. 参照元

| 用途 | 場所 |
|---|---|
| 本体 | `C:\ソース\AutoCollector\`（コミット `d0a05000`） |
| **戦闘・納品を委譲する先（実ソース確認済み）** | **`C:\ソース\BossmodReborn\`** |
| BMR の IPC 定義 | `BossmodReborn\BossMod\Framework\IPCProvider.cs` |
| BMR の FATE モジュール | `BossmodReborn\BossMod\Autorotation\MiscAI\FateUtils.cs` |
| BMR の自動ターゲット | `BossmodReborn\BossMod\Autorotation\MiscAI\AutoTarget.cs` |
| ゲームデータ検証ツール | `C:\ソース\_設計	oolsateprobe\` |
| バディ召喚（移植元・自作） | `C:\ソース\ARankHuntTourAssistant\GameActions.cs:126-157` |
| バディ再試行（移植元・自作） | `C:\ソース\ARankHuntTourAssistant\AutomationController.cs:1150-1194` |
| FATE の挙動を学んだ先（**コードは使わない**・AGPL-3.0） | `C:\ソース\FF14-FATE調査_20260922_185922\sources\XeldarAlz__FFXIV-AutoFATEGrind\` |
| 構造体定義 | `C:\ソース\FF14-FATE調査_20260922_185922\sources\aers__FFXIVClientStructs\` |
| 単体プラグイン案（不採用・経緯として保存） | `C:\ソース\_設計\AutoFate\AutoFate_設計_2026-09-22.md` |
