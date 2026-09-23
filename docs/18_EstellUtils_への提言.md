# EstellUtils への提言

送り主: Auto Collector（FFXIV / Dalamud プラグイン、UI 約 6,300 行を移行中）
対象の版: `5c33e11`（2026-09-23「タブの折り返しとプログラムからの選択に対応、移行手引きの誤りを修正」）

## はじめに

先に挙げた 3 件（タブの選択・タブの折り返し・`migration.md` の矛盾）は
**`5c33e11` で対応済みでした。** ありがとうございます。速さに驚いています。

この文書は**その版に対して**、実際に窓を移してみて見つかったものです。
挙げる前に、一件ずつ「本当に無いのか」を実ソースで潰しています。
**当初 44 件出したうち 17 件は「実は手段があった」ので落としました。**
残った 27 件を、重要度の順に並べます。

以下、`EU/` は `src/EstellUtils/`、`AC/` は `AutoCollector/` を指します。

---

## A. 移行の土台（これが無いと中身を移せない）

### A-1. `EUi.RawImGui` がカーソルしか合わせない

**症状。** 移行の途中で生 ImGui を残すと、次の 3 つが順に起きます。
Auto Collector はこの 3 つを版を分けて実機で踏みました。

| 版 | 症状 |
|---|---|
| 0.1.3.2 | 窓枠だけ出て中身が何も無い |
| 0.1.3.3 | 右端で文字と表が切れる |
| 0.1.3.4 | 送りのつまみが 2 本並ぶ／長い説明が折り返さない |

**原因。** `EU/UI/EUi.Interop.cs:47-59` の `RawImGui` が ImGui へ渡しているのは
`ImGui.SetCursorScreenPos(area.Min)`（:56）の 1 行だけです。

`:53` で `width` を `resolved` へ解決していますが、その値は `:58` で
`RawImGuiScope` へ渡るだけで、**ImGui へは一度も渡りません。**
使い道は `:114` の `ctx.Allocate(new Vector2(this.width, consumed))` のみです。

つまり資料に「ImGui へ渡す幅を揃えたいときに指定する」と書いてある引数（`:44-46`）が、
**ImGui の見ている幅を一切変えていません。**

結果、ImGui 側の `GetContentRegionAvail` / `TextWrapped` / `SetNextItemWidth(-1)` /
幅 0 のテーブルは、EstellUtils の矩形ではなく **ImGui ウィンドウの右端**を基準に解決されます。

ずれは必ず起きます。`EU/UI/Windowing/EuWindow.cs:540-543` は
`EUi.Region(contentRect, padding)` の中で `Draw()` を呼び、その `padding` は
`EU/UI/Theming/ThemeMetrics.cs:21` の `EdgeInsets.All(14f)`。
一方 ImGui 側の窓は `EuWindow.cs:32-40` の `BaseFlags` に `NoTitleBar` を含むだけで
padding は ImGui 既定のままなので、**両者の右端が一致しません。**
`EUi.Row(...)` の列の中で開けば、ずれは列幅ぶん丸ごとになります。

**いまの回避策。** Auto Collector は `AC/Ui/MainWindow.cs:70-114` で、
`RawImGui` を開く前に `EUi.AvailableRect` を控え、その大きさで
`ImRaii.Child` を開き直し、さらに `ImGui.PushTextWrapPos(0f)` を自分で積んでいます。
加えて `EuWindow.AutoScroll = false` にしています（→ B-2）。
**移行する全プラグインがこの 4 点セットを書くことになります。**

**提案。**

```csharp
public static RawImGuiScope RawImGui(
    SizeSpec? width = null, float? height = null, bool child = true)
```

1. 解決した幅・高さで `ImGui.BeginChild` を開き、ImGui の「描いてよい領域」を
   EstellUtils の矩形へ合わせる
2. `child: false` のときも最低限 `ImGui.PushTextWrapPos(area.Min.X + resolved)` と
   `PushClipRect` を積む
3. `Dispose` で `EndChild` / `PopTextWrapPos` まで面倒を見る

あわせて `RawImGui` は `docs/widgets.md` に 1 行も載っていません
（`docs/` 内のヒットは `migration.md` の 6 箇所のみ）。ウィジェット一覧にも入れてほしいです。

**派生（未確認・机上）。** `Dispose`（`:104-115`）は
`consumed = MathF.Max(0f, end.Y - this.origin.Y)` で縦しか測っていません。
`ImGui.SameLine()` で終わる生ブロックは ImGui のカーソル Y が行頭へ戻るため
`consumed` が 0 になり、`:113` の `if (consumed > 0f)` を通らず領域を確保しません。
直後の EstellUtils ウィジェットが上へ重ね描きすることになります。
**これは実機では踏んでいません**（Auto Collector のスコープは該当しない形）。

### A-2. 見出し行を固定したまま中身だけ送る表が無い

`ImGui.TableSetupScrollFreeze` に当たるものがありません。
名前の揺れとして `ScrollFreeze` / `Sticky` / `StickyHeader` / `Frozen` / `Pinned` / `Fixed` を
全部探しましたが **0 件**でした。

**手段はありますが、列がずれます。**
`EUi.TableHeader` をスクロールの外、行だけを `EUi.Scroll(id, height)` の中に置けば
見出しは動きません。ただし `EU/UI/Layout/ScrollArea.cs:52-63` は、つまみが出ているとき
`contentInset = ScrollbarWidth + SpacingSm` ぶん内容の右端を削ります。
見出しは削られない全幅、行は削られた幅。両者へ同じ `TableColumn[]` を配るので、
`SizeSpec.Fill` の列がその差ぶん縮み、以降の固定幅列が左へずれます。

しかも `contentInset` はつまみが出るか否かで変わる（`:44` の `showBar`）ので、
**行が増えてつまみが出た瞬間に見出しだけズレる**という気づきにくい壊れ方をします。

**Auto Collector 側の必要性。** `AC/Ui/MainWindow.cs:332-343` と `:453-466` の 2 箇所。
どちらも数百行を送る一覧（ショップ照合・交換候補）で、見出しが流れると
どの列が何か読めなくなります。表そのものは `ImRaii.Table` が 10 箇所、
`TableSetupColumn` が 50 箇所あります。

**提案。** `EUi.Table(id, columns, height)` のスコープを足し、その中で `TableRow` を並べたら
見出しは自動で固定、本体だけ送る形にしてほしいです
（内部で `TableHeader` → `ScrollArea.Begin` → `TableRow` と繋ぎ、幅の基準を同じ矩形に揃えれば
 ずれは原理的に起きません）。

最小の対処なら次のどちらかでも足ります。

- `TableHeader` に `reserveScrollbar: bool` を足し、見出し側も同じだけ右端を削れるようにする
- `EUi.ScrollbarInset(id)` のような「その id のスクロール領域がいま何 px 取っているか」を公開する

---

## B. 移行の一歩目で必ず踏むのに、書かれていないこと

### B-1. `migration.md` に `EUi.Windows.Add` への登録替えが無い

`migration.md:26-69` の移行前／移行後は、基底を `Window` → `EuWindow` に変えるところしか
見せていません。しかし `EuWindow` は `EuWindowManager` に登録されないと**一切描かれません**
（`EU/UI/Windowing/EuWindowManager.cs:35-44` の `Add`、`:112-153` の `Draw` は
 `this.windows` だけを回す）。

移行前のコードは Dalamud の `WindowSystem.AddWindow` で登録されているはずなので、
そこを差し替えないと「クラスは変えたのに窓が出ない」になります。

`EUi.Windows.Add` は `getting-started.md:32` にはありますが、`migration.md` には一度も出てきません。
**移行手順だけを追う人は必ずここで止まります。**

**提案。**「2. ウィンドウの置き換え」へ 1 ブロック足してください。

```csharp
// 移行前
this.windowSystem.AddWindow(this.configWindow);

// 移行後
EUi.Windows.Add(this.configWindow);
```

あわせて後始末（`EuWindowManager.Dispose` が `IDisposable` な窓を解放すること、
`EuWindowManager.cs:156-171`）にも一行触れてほしいです。

### B-2. `AutoScroll`（既定 true）が `Draw()` をスクロール領域で包むことが書かれていない

`EuWindow.cs:530-537` — `AutoScroll` が true のとき `Draw()` は
`EUi.Region(...)` と `ScrollArea.Begin(...)` の内側で呼ばれます。
さらに ImGui ウィンドウ自体は `EuWindow.cs:32-40` の `BaseFlags` で
`NoScrollbar | NoScrollWithMouse` を付けて開かれています。

`docs/` 全体で `AutoScroll` は `windows.md:133` の表に 1 行あるだけで、
`migration.md` には一度も出てきません。

**中身がまだ生 ImGui だと、外の `ScrollArea` と内側の送りが二重になり、つまみが 2 本並びます。**
（`ScrollArea.cs:116-132` の `HandleWheel` は EstellUtils のスクロール領域どうしを
 調停しますが、ImGui 側の子領域は勘定に入っていません）

Auto Collector は `AC/Ui/MainWindow.cs:39-45` でこれを踏み、`AutoScroll = false` にしました。
逆に false のままだと ImGui 側も `NoScrollbar` なので、今度は送りが一つも無くなります
（`MainWindow.cs:91` の `ImRaii.Child` が実質その代わりです）。

**提案。**「中身が生の ImGui のままの場合」へ次を明記してください。

> 移行の途中で中身が生 ImGui のうちは `AutoScroll = false` にして ImGui 側の送りに任せ、
> EstellUtils のウィジェットへ移し終えたら true に戻す。

実装で解けるならそのほうがよく、`RawImGui` のスコープが開いているあいだは
外側の `ScrollArea` を抑制する（スクロール量を 0 に固定してバーを出さない）ようにすれば、
呼び出し側が意識しなくて済みます。

---

## C. API の挙動が、説明と違うところ

### C-1. `EUi.PushId` がポップアップ／メニューの ID を分けない

`EUi.Popups.cs:203` の `ImGui.OpenPopup(id)` と `:347` の `ImGui.BeginPopup(id, ...)` は
**生の ImGui の ID スタック**で解決されます。
一方 `EUi.PushId` は `EU/UI/Core/UiContext.cs:212-215` で EstellUtils 自身の `idStack` に積むだけで、
ImGui の ID スタックには何もしません（`EUi.cs:148-151`）。

つまり次は全行が同じ ImGui ID になり、1 行分しか開きません。

```csharp
using (EUi.PushId(i))
    EUi.ContextMenu("rowMenu", row, ...);
```

ところが `widgets.md:337-339` と `EUi.Popups.cs:188-190` は
**「`EUi.PushId(index)` の中で呼ぶのが確実です」と勧めています。**

**提案。** Popup 系の id を `ctx.GetId(id)` 経由の値から作る
（例: `ImGui.OpenPopup($"##eu{euId.Value:X}")`）。
当面直せないなら、資料から「`PushId` の中で呼ぶのが確実」を削り、
「行ごとに違う文字列 id を渡すこと（`$"rowMenu{index}"`）」に直してほしいです。

### C-2. `Vector2` で領域を取るウィジェットが、宣言した列幅を無視する

`EU/UI/Layout/LayoutScope.cs:188-206` の `Allocate(SizeSpec, float)` は
列が宣言されていれば `columnWidths[index]` を使いますが、
`:179-185` の `Allocate(Vector2)` は**素通し**で、`:221-257` の `AllocateHorizontal` も
`size.X` をクランプしません（`:250` で `Cursor = rect.Max.X`）。

`Vector2` で確保しているのは `EUi.Text.cs:215`（`Paragraph` → `WrapColored` /
`MutedParagraph` / `Muted(wrap:true)` も同じ経路）、`:257`（`Note`）ほかです。
**列を宣言した行の中でこれらを使うと、行がずれるかはみ出します。**

**提案。** `AllocateHorizontal` で列が宣言されているとき `size.X` を
`columnWidths[ColumnIndex]` へクランプし、カーソルは列境界へ進める。
折り返し系は `AvailableWidth` ではなく「この列に割り当てられた幅」を見るようにする
（`LayoutScope` に `CurrentColumnWidth` を公開するのが素直だと思います）。

### C-3. `TableColumn.Align` がセルに効かない

`EUi.Table.cs:15` の XML は「Align: **セルの中身**の寄せ方」（`:14`）と書いています。
しかし `Align` を読むのは `:74` の**見出し描画だけ**です。
`TableRow`（`:90-114`）は `Width` しか取り出さず、`TableCell`（`:121-143`）は
`columns` を受け取りもしないので、列の `Align` はセルへ一切伝わりません。

**提案。** `TableRow` が列定義を保持し、`TableCell` が「今の列の Align」を既定値として使う
（明示指定があればそちら優先）。すぐ直せないなら XML を「見出しの寄せ方」に直し、
`widgets.md` に「セルの寄せは `TableCell` 側で毎回指定する」と書いてください。

### C-4. `Derive` の中で `Metrics` を書き換えると、`Scale` を設定した時点で消える

`EU/UI/Theming/Theme.cs:22-23` は `baseMetrics` と公開 `Metrics` を別に持ち、
コンストラクタ `:31` で `this.Metrics = metrics.Clone()`、
`Clone()` `:97-103` でも `clone.Metrics = this.baseMetrics.Scaled(this.scale)` と、
**両者は常に別オブジェクト**です。

`Scale` の setter `:62-74` は `this.Metrics = this.baseMetrics.Scaled(clamped)` で
`baseMetrics` から作り直すため、`Derive` の中で `Metrics` を直接書き換えた寸法は
**あとで `Scale` を設定した瞬間に消えます。**

`SetMetrics`（`Theme.cs:80`）が正しい入口ですが、**どの文書にも載っていません。**

**提案。** `Derive` の最後に `clone.SetMetrics(clone.Metrics)` を入れるのが最小の修正です。
あわせて `theming.md` に `SetMetrics` を載せてください。

---

## D. 足りない口

### D-1. `UiBuilder.OpenMainUi` / `OpenConfigUi` へ繋ぐ口が無い

`EUi.cs:84-108` の `Initialize` が触るのは `pluginInterface.UiBuilder.Draw` だけ（`:106`）。
`Shutdown` も `Draw` しか外しません（`:123`）。
`src/` 全体で `OpenMainUi` / `OpenConfigUi` は **0 件**です。

実例は `samples/EstellUtils.Demo/Plugin.cs:54-55`（購読）と `:61-62`（解除）に
手書きされているだけで、文書にはありません。

**Dalamud は主画面が無いプラグインを検査で指摘します**（`NoMainUiProblem`）。
つまり**全プラグインがこの定型を書く**ことになります。
Auto Collector も `Plugin.cs` で自分で繋ぎ、`Dispose` で外しています
（外し忘れると、読み込み直したあと古い画面が描かれ続けます）。

**提案。** `EuWindowManager` 側で面倒を見てほしいです。

```csharp
EUi.Windows.Add(window, mainUi: true, configUi: true);
```

あるいは `EuWindow` に `IsMainUi` / `IsConfigUi` を持たせて `Add` が見る形でも。
内部の挙動は「トグルではなく開く、既に開いていれば前面へ」に固定するのが親切です。
解除は `EUi.Shutdown()` が責任を持つ形で。
`WindowBuilder` にも `.AsMainUi()` があると揃います。

### D-2. 1 行の中に色違いの断片を並べる書き方が無い

`SameLine` は `src/` に 1 件もありません（`migration.md:293` も「`HStack` を使ってください」と明言）。
`HStack` は使えますが、**最初の要素より前に開く**必要があるため、
既存の「`TextColored` → `SameLine` → `TextColored`」を 1 行ずつ機械的に置き換えられず、
**毎回ブロックを囲み直す書き換え**になります。

色付き断片を連ねる API（`RichLabel` / `Segment` / `Markup` / `TextRun` / `Inline`）は
いずれも 0 件でした。

**Auto Collector 側の規模。** `ImGui.TextColored` が **300 箇所**、`ImGui.SameLine` が **81 箇所**。
その多くが「1 行の中で 2〜3 色」の形です。

```
現在値を緑か黄 → SameLine → 灰色で「/ 推奨値」
「選択中:」 → SameLine(0f, 0f) → 色付きの通貨名
素材名 →「あと N」→「（自分で作れます）」を 3 色
```

**提案。**

```csharp
EUi.RichLabel(params (string Text, uint? Color)[] parts)
```

`TextPainter.TextIn` を横へ送りながら呼ぶだけで、`HStack` を開かずに 1 行で書けます。
折り返しつきの版（`Paragraph` の色付き断片版）まであると、説明文の置き換えもそのまま済みます。

**これが無いと、300 箇所の `TextColored` がすべて `HStack` のブロック化を伴う書き換えになり、
移行の手数の大半をここが占めます。**

---

## E. 文書の誤り

### E-1. `widgets.md` のキー割り当て例がコンパイルできない

`widgets.md:201` で自動プロパティを宣言し、`:204` で `ref` で渡しています。

```csharp
public KeyBinding ToggleKey { get; set; } = new(...);   // :201 自動プロパティ
if (EUi.KeyBind("切り替えキー", ref this.config.ToggleKey))   // :204 → CS0206
```

`EUi.KeyBind.cs:61-63` の実引数は `ref KeyBinding binding` です。
**プロパティは `ref` で渡せません。**

`:201` をフィールドに直すか、`:204` を一時変数経由にしてください。
あわせて「`ref` を取るウィジェットにはプロパティを渡せない。
プロパティ主体の設定クラスは `Bind` か一時変数を使う」という注意を 1 ブロック置いてほしいです。

### E-2. `binding.md` の対応型表が、同じ文書の中で矛盾している

`Binding/ConfigModel.cs:249-275` は `Vector4` のとき
`[EuColor]` が付いている場合だけ `ColorVectorBinding` を返し、それ以外は数値入力です。

- `binding.md:74` の表: `| Vector4 | カラーピッカー |`
- `binding.md:196-204`: 「`Vector4` を色として扱うのは `[EuColor]` を付けたときだけです」

`:74` を `| Vector4 | 数値 4 つ（[EuColor] でカラーピッカー） |` に直し、
`:25` の冒頭例に `[EuColor]` を足してください。

### E-3. `migration.md` が `SetNextItemWidth` と `width:` を 1 対 1 のように見せている

`migration.md:104-115` は `ImGui.SetNextItemWidth(160)` の置き換えとして
`EUi.SliderInt(..., width: 160f)` を並べています。
しかし `EUi.Values.cs:121-125` では `width` は**バーの幅**で、
値の欄（既定 58px）とラベルは別に取られます。
数字をそのまま移すと全体は広くなります。1 行添えてほしいです。

### E-4. どの文書にも載っていない公開 API

`docs/` 全体を grep して 0 件でした。

| API | 場所 | 載せる先 |
|---|---|---|
| `ExtraFlags` | `Windowing/EuWindow.cs:210` | `windows.md` |
| `IsCollapsed` | `EuWindow.cs:101` | `windows.md` |
| `SetMetrics` | `Theming/Theme.cs:80` | `theming.md`（→ C-4） |
| `ButtonAt` | `EUi.Buttons.cs:74` | `widgets.md` |
| `EuTipFromAttribute` | `Binding/Attributes.cs:149-150` | `binding.md:160` 付近 |
| `Section(label, ref bool open, ...)` | `EUi.Containers.cs:164-166` | `widgets.md` |

`ButtonAt` は**反証の過程で「ボタンの高さを指定できない」という指摘を潰した API** です。
載っていれば、そもそも指摘が出ませんでした。

---

## F. タブまわりの残り（`5c33e11` で直った部分の、その先）

### F-1. `ref` 版と `SelectTab` が食い違う（コメントは「食い違わない」）

手段は 2 つあります。

1. 呼び出し側で持つ `EUi.Tabs.cs:68-85` の `TabBar(id, ref int selected, params labels)`
2. ライブラリ側に持たせたまま変える `:100-106` の `SelectTab(id, index)`

`ref` 版は `selected` を Clamp して描画し `:83` で Store へ**書き戻すだけ**で、
**Store を一度も読みません。**
したがって `SelectTab` を呼んでも `ref` 版には届かず、次のフレームで Store が上書きされて消えます。

にもかかわらず `:82` のコメントは
**「内部の記憶も合わせておく。`SelectTab` と混ぜて使っても食い違わない」**と書いています。
片方向にしか合っていません。

**提案。** コメントを事実に合わせる（「`ref` 版は呼び出し側の値を正とする。`SelectTab` は効かない」）。
できれば `SelectTab` が Store へ「今フレーム外から指定された」印を立て、
`ref` 版はその印があるときだけ Store を優先するようにすると、
資料の「呼び出しの順序は問わない」（`:98`）が両方の版で本当になります。

### F-2. `SelectTab` が添字しか受け取らない

`:100-106` の `SelectTab` は `index` だけを取り、ラベルを見ません。
一方 `TabBarResult` は `:201-202` でラベル判定 `IsSelected(label)` を提供していて、
**選ぶときは添字、判定はラベル**と非対称です。

条件付きタブがあると番号がずれます。
Auto Collector のタブは `Plugin.C.DebugMode` で 2 枚出たり消えたりするので、
それ以降のタブの番号が 2 ずれます。**並びを変えただけで飛び先が壊れる**書き方になります。

Fluent ビルダー経路はさらに届きません。`Fluent/WindowBuilder.cs:229` が内部で
`EUi.TabBar("##euFluentTabs", ...)` を呼んでおり、この id は
`EuWindow.cs:413` の `ctx.ScopedId(this.imguiId)` の内側でしか解決できません。

**提案。** `EUi.SelectTab(id, ReadOnlySpan<char> label)` のラベル版を足してほしいです。
見つからなければ現在の選択を保つ（条件付きタブが今フレーム隠れている場合に暴れない）。
`WindowBuilder` / `FluentWindow` にも `SelectTab(label)` を公開してほしいです。

### F-3. 行数の基準幅が、実際の行幅と食い違う経路が残る

`:119` の行数計算は `EUi.AvailableWidth` を基準にしていますが、
`:120` の実際の行矩形は `ctx.Allocate(SizeSpec.Fill, ...)` が返す幅で決まります。

縦積みスコープでは一致しますが、**横並びスコープの中ではズレます。**
とくに `EUi.Row(...)` のように列を宣言した行の中で `TabBar` を描くと、
`LayoutScope.Allocate`（`Layout/LayoutScope.cs:188-206`）は `:196-197` で列幅を優先するため、
`AvailableWidth`（＝残り幅）とはまったく別の値になります。
行数が足りなければタブが下へはみ出し、多ければ空行ぶんの余白が空きます。

**`tests/EstellUtils.SelfCheck` の `CheckTabWrapping` は、実装を呼ばずに
同じ計算をローカルへ写して検証している**ので、この食い違いは検出できません。

**提案。** 行矩形を先に確保してから `rowRect.Width` を基準に数え直すか、
前フレームの実測幅を Store に持つ二段構えに。
手早く済ませるなら `CountTabRows` へ渡す値を「これから Allocate される幅」にする
ヘルパーを 1 つ挟むだけでも足ります。
あわせて **SelfCheck が実装の `CountTabRows` を直接呼ぶ**ようにすれば、
写し間違いで検証が空振りする形を避けられます。

### F-4. `TabBar` がラベルの `##` / `###` を落とさない

`:141` はタブの ID を `euId.Child(i)` という添字ベースで作っており、
**ラベルを ID 解決に通していません。**
そのため `:147` の `DrawTab(visual, label)` と `:160` の `TabWidth(label)` には
生のラベルがそのまま渡り、`"設定##main"` は画面に `"設定##main"` と出ます。

ところが `migration.md:294` は
「ラベルの `##` / `###` の扱いは ImGui と同じです。既存のラベルをそのまま使えます」
と書いています。**`TabBar` については成り立ちません。**

さらに `:201-202` の `IsSelected(label)` は全文一致で比べるため、
判定側にも `##` 付きの全文を渡す必要があり、書き味が揃いません。

**提案。** `DrawTabBar` の中で各ラベルを `ctx.GetId(label, out var display)` に通し、
描画と幅計算には `display` を使う。ID は今の添字ベースのままで構いません。
やらない判断なら、`migration.md:294` に
「ただし `TabBar` のラベルは `##` を解釈しない」と但し書きを足してほしいです。

---

## G. 窓まわり（細かいもの）

### G-1. 同名の窓を 2 つ作ると、ImGui の ID と保存キーが衝突する

`EuWindow.cs:56-60` で `imguiId = $"###EstellUtils_{name}"` が生成時に固定される一方、
`Name` は public set（`:63`）。`Add` の重複判定は参照一致のみ（`EuWindowManager.cs:39`）。
`AttachState` は `layout.GetOrCreate(window.Name)`（`:83`）で名前を保存キーにします。

したがって同名の窓 2 つは、同じ ImGui ウィンドウ ID と同じ `EuWindowState` を共有します。
**検出も警告もありません。**

**提案。** `Add` で同名を検出したら警告ログ。`Name` を読み取り専用にして、
表示名の変更は `GetTitle()` のオーバーライドへ一本化（既に仕組みがあります: `:223`）。
保存キーを `Name` と切り離して任意の `StateKey` を持てると、将来の改名にも耐えます。

### G-2. 「本体を閉じたまま小窓だけ」が復元されない

`state.CompanionOpen` を読むのは `EuWindow.cs:774-791` の `RestoreState` だけですが、
その `RestoreState` は本体の `!IsOpen` 早期 return（`:336`）より**後ろ**にあるため、
本体が閉じていると一度も走りません。

**提案。** 状態の復元を `Render()` の先頭（`IsOpen` 判定より前、小窓を描くより前）へ移す。
あるいは `Add` / `BindLayout` の時点で一度だけ復元してしまう（描画フレームに依存させない）。

### G-3. 入口の文書が「位置は手で保存しろ」と教えている

`getting-started.md:97-112` の「4. 位置とサイズを保存する」は、`OnClose` で
`config.WindowPos` を自分で書く**手動の方法しか示していません。**
`EuWindowLayout` にも `EUi.Windows.BindLayout` にも触れていません
（自動の仕組みが書かれているのは `windows.md:154-180` だけ）。

入口の文書だけを読んだ人は「自前で書く必要がある」と受け取ります。

**提案。** 4 章を `EUi.Windows.BindLayout(config.WindowLayout, config.Save)` の 2 行に差し替え、
手動の書き方は「窓ごとに別の入れ物を使いたい特別な場合」として `windows.md` へ送る。

---

## 付録: 挙げなかったもの（反証で落とした 17 件）

送る前に「本当に無いのか」を潰した結果、**次は手段がありました。**
同じ誤解をする人が出るかもしれないので、参考までに。

| 一度は「無い」と思ったもの | 実際 |
|---|---|
| 小さめのボタン／ボタンの高さ指定 | `EUi.ButtonAt(id, rect, ...)` で幅も高さも指定できる（`EUi.Buttons.cs:74`）。ただし**未文書**（→ E-4） |
| 中身を自分で描くドロップダウン | `EUi.Popup` ＋ `PopupAnchor.BelowLastItem`、枠は `WidgetPainter.DrawInputFrame`（public） |
| スクロール領域に枠を付ける | `EUi.Card`（`EUi.Containers.cs:35`） |
| スコープで色を差し替える | `EUi.PushTheme` ほか |
| データの木を開く（`TreeNode`） | `EUi.Section` の入れ子 |
| 表の行の高さ・セルの折り返し | `TableRow(..., float? height)` で任意の高さ |
| 「確定したとき」だけ反応する入力 | `WidgetResult.Deactivated` |
| 窓を前面へ出す | 手段あり |
| 値ウィジェットにプロパティを渡せない | `EUi.Bind` 経由で渡せる |
| 「残りの見えている高さ」 | スクロール領域内で `AvailableHeight` が巨大になるのは仕様の範囲 |

**`ButtonAt` のように「あるのに文書に無い」ものが、そのまま「無い」という指摘になります。**
E-4 の 6 件を載せるだけで、この種の誤解はかなり減ると思います。

---

## まとめ

急ぎで効くのは次の 3 つだと思っています。

1. **A-1 `RawImGui`** — 移行する全員が同じ 4 点セットを書くことになる
2. **B-1 / B-2 の 2 行** — 移行の一歩目で必ず止まる
3. **D-2 `RichLabel`** — Auto Collector だけで 300 箇所の書き換え量が変わる

A-2（表の固定見出し）は Auto Collector にとっては要件ですが、
**回避策（`TableHeader` を外に出す）で当面しのげます。**
ずれの件だけ `widgets.md` に書いてもらえれば、それで進められます。
