# UI を EstellUtils へ移す

対象: `AutoCollector`
ブランチ: `feature/estell-ui`
相手: `C:\Users\Administrator\TempRepos\EstellUtils`（`rioriopu/EstellUtils`）

## いまどこまで入っているか

| 段 | 内容 | 状態 |
|---|---|---|
| 1 | `EUi.Initialize` / `EUi.Shutdown` | 済・実機確認 |
| 2 | ウィンドウを `EuWindow` へ | 済・実機確認（0.1.3.5） |
| **3** | タブバーを `EUi.TabBar` へ | **済**（0.1.5.4） |
| **4** | 各タブの中身を移す | **済・10 / 10 枚**（0.1.5.7） |
| 5 | 設定の多い画面を属性バインディングへ | **ここから** |

**段 4 は終わった。**`RawTabScope.cs` は消し、`AutoScroll` も true へ戻した。
`AutoCollector/Ui/` の実コードに生の ImGui は 1 つも残っていない。

### 段 4 の進み

| タブ | 行数 | 状態 |
|---|---|---|
| 寄付 | 95 | 済（0.1.5.5） |
| 設定 | 157 | 済（0.1.5.5） |
| 診断 | 134 | 済（0.1.5.5） |
| 交換候補 | 約 230 | 済（0.1.5.5） |
| ショップ照合 | 約 490 | 済（0.1.5.5） |
| 製作計画 | 666 | 済（0.1.5.7） |
| デバッグ | 801 | 済（0.1.5.7） |
| 状況 | 880 | 済（0.1.5.7。`SetupGuide` と `DrawPluginTable` も同時） |
| FATE 周回 | 786 | 済（0.1.5.7） |
| プリセット | 3126 | 済（0.1.5.7） |

**移せる単位はタブ 1 枚。**メソッド単位では切れない。

`MainWindow.SetupGuide.cs`（198 行）は小さいので最初に移そうとしたが、
**状況タブの内側から呼ばれている**（`MainWindow.StatusTab.cs:104`）。
状況タブが `RawTabScope` で囲まれているため、その中で `EUi.*` を呼ぶと
レイアウトの持ち主が二人になる。**状況タブごと移すまで触れない。**

移したタブからは `RawTabScope.Open()` の 1 行を外す。
囲んだまま `EUi.*` を呼ぶと領域を二重に取る。

### 段 4 で分かった移し方

**入力欄はラベルを持たない。**`EUi.InputInt` / `TextInput` の第 1 引数は ID だけ。
ラベルは `EUi.Field` で出す。`SliderFloat` だけはラベルを直接取る。

```csharp
using (EUi.Field("再開までの待ち（秒）"))
    EUi.InputInt("##adrestartdelay", ref delay, min: 0, max: 120, width: 160f);
```

**折り返しは明示する。**`RawTabScope` を外すと `PushTextWrapPos` も消える。
`TextColored` のままにすると、長い説明文が右端で切れる。
`MutedParagraph` / `WrapColored` へ移すこと。

**表の行の高さは先に測る。**`EUi.TableRow` は高さを固定で取るので、
折り返して 2 行になる文を入れると次の行へはみ出す。

```csharp
// 最終列だけは「行の残り幅」がそのまま列幅になる（LayoutScope.PeekAvailable）。
// だから先に幅を出して測れるし、行の中で Paragraph を呼べば同じ幅で折り返す。
var width = EUi.AvailableWidth - fixedWidths - EUi.ColumnSpacing(columnCount);
var height = MathF.Max(EUi.LineHeight, EUi.MeasureWrapped(text, width).Y);
```

`TableCell` は 1 行で切ってツールチップに逃がす。
診断タブの詳細文は最長 85 字あり、切ると肝心の助言（「最初の交換は手動で確認してください」）が
隠れる。**折り返しが要る列は `TableCell` ではなく `Paragraph`。**

**見出しを固定する表は、見出しを送り領域の外へ出す。**
EstellUtils に `TableSetupScrollFreeze` に当たるものが無い（提言 A-2）。

```csharp
// つまみのぶんを空けないと、行数が増えた瞬間に見出しだけズレる
EUi.TableHeader(ShopEntryColumns, reserveScrollbar: true);

using (EUi.Scroll("##shopentries", 400f))
    for (var i = 0; i < entries.Count; i++)
        using (EUi.TableRow(ShopEntryColumns, i)) { ... }
```

**送り領域は残り高さを全部取る。**その後ろに置いたものは出る場所が無い。
交換候補タブの「N 件は表示していません」は、送り領域より**前**へ移した。

### 段 4 を終えて分かったこと

**移せる単位はタブ 1 枚。**`RawTabScope` はタブごとに開いていたので、
1 枚ずつ外していけた。`MainWindow.SetupGuide.cs` と `DrawPluginTable()` は
状況タブの内側から呼ばれるため、そのタブと一緒に移した。

**EstellUtils へ 6 件を要望し、すべて入った。**要望前は次のことができず、
入るまで妥協していた（入ったあと全部戻した）。

| 要望 | 入ったもの | 戻したもの |
|---|---|---|
| 表のセルに複数置けない | `EUi.Cell` / `CellStack` | 素材の印の色・適用ボタンの位置 |
| 入力の第 1 引数が不揃い | `ラベル##id` へ統一 | `EUi.Field` の囲み 7 箇所 |
| 編集確定の合図が無い | `WidgetResult.Committed` | 記録の保存先の開き直し |
| 送り領域が残り高さを全部取る | `Scroll(id, SizeSpec, reserveBelow)` | 交換候補の件数行 |
| 折り返す列の高さ | `TableColumn.Wrap` ＋ `NextItemWidth` 化 | 自前の高さ計算 |
| 小さいボタンが無い | `EUi.SmallButton` | 13 箇所 |
| 中身を自分で描くコンボ | `EUi.ComboBody` | 通貨・交換先・製作ジョブの 3 つ |
| 選択行に中身を置く | `EUi.SelectableRow` | 重ね描きの仕掛け |
| 選択行に色 | `Selectable(color:)` | 「薄いが押せる」項目 |

**`Deactivated` を編集の確定として使わない。**中身は `interaction.Released`
（マウスを離した）で、入力の確定ではない。確定は `Committed`。
一度これで実装して、「欄を押しただけで開き直し、他所をクリックして
確定しても開き直さない」という動きになっていた。

**色は押し込まず、テーマの種類で指す。**
`PushColor` で橙や青を入れていた 3 箇所は `ButtonStyle.Primary` / `Danger` へ。
`NoteKind.Success / Warning / Danger` と `EUi.Colors.TextMuted` で、
`ImGuiColors` の直指定もほぼ消えた。

**重ね描きの仕掛けが消えた。**通貨の一覧は「幅ゼロの選択行を敷いて、
名前を `SetCursorPos` で上から重ねる」形だった。
`SelectableRow` なら行全体が当たり判定なので、`GetCursorPos` /
`SetCursorPos` / `GetTextLineHeight` が全部要らなくなった。
「数理しか選べない」の原因だった構造そのものが無くなっている。

### `MainWindow.cs` も移し終わった

4 タブ（ショップ照合・交換候補・診断・設定）と、状況タブから呼ばれる
`DrawPluginTable()` まで、すべて `EUi` になった。

段 2 まで入れた時点で、**枠・タイトルバー・スクロールバーは EstellUtils の描画**になる。

### 踏んだ罠: `Draw()` ごと `RawImGui` で囲む

**窓枠だけ出て、中身が何も無い状態になった。**

移行手引きの「2. ウィンドウの置き換え」に

> `Draw()` の中身が生の ImGui のままでも動作します。
> 違いは、ウィンドウの枠とタイトルバーが EstellUtils の描画になることだけです。

とあるが、**そのままでは動かない。**同じ文書の「生 ImGui との混在」にある

> 囲まないと ImGui 側のカーソルが合わず、見えない場所へ描かれて何も出ていないように見えます。

が起きる。`EuWindow.Draw()` の中も EstellUtils のレイアウトなので、
**生 ImGui を書くなら最上段から囲む必要がある。**

```csharp
public override void Draw()
{
    using var raw = EUi.RawImGui();   // ← これが要る

    using var tabs = ImRaii.TabBar("##autocollector_tabs");
    ...
}
```

中身をひとつずつ移していくあいだ、まだ移していない部分はこのスコープの中に置く。

**手引きの 2 節と「生 ImGui との混在」節が食い違っている。**
2 節のほうを直してもらうと、次に移す人が同じ穴に落ちない。

### 実機で確認が取れるまでに 3 版かかった

| 版 | 症状 | 直したこと |
|---|---|---|
| 0.1.3.2 | 窓枠だけ出て中身が無い | `Draw()` を `RawImGui` で囲む |
| 0.1.3.3 | 右端で文字と表が切れる | 領域の大きさで子領域を開く |
| 0.1.3.4 | 送りのつまみが 2 本／折り返さない | 窓側の送りを止める／折り返し位置を指定 |
| **0.1.3.5** | — | **実機で確認が取れた** |

**どれも実機でしか分からなかった。**
ビルドは 4 版とも通っており、静的には何の問題も無い。
ここを移すときは、1 段ごとにゲームで見ること。

### 回避策は 2a7ac93 で全部要らなくなった

提言を出したところ、EstellUtils 側が `2a7ac93` で直してくれた。
**こちらが手で書いていた 4 点セットのうち 3 つが落とせた。**

| 手で書いていたもの | いま |
|---|---|
| `EUi.AvailableRect` を控えて `ImRaii.Child` を開き直す | `EUi.RawImGui()` が既定で領域を作る |
| 大きさが取れないときの退避 | 要らない |
| `UiBuilder.OpenMainUi` / `OpenConfigUi` へ手で繋ぐ | `EUi.Windows.Add(w, mainUi: true, configUi: true)` |
| `ImGui.PushTextWrapPos(0f)` | **残す**（下記） |

折り返しだけは残してある。**これはライブラリの不足ではない。**
生 ImGui の文字は `TextWrapped` 以外もともと折り返さない。
以前の窓は横に送れたので端まで読めたが、領域に収まるようになったぶん、
はみ出した文字が読めなくなる。中身を EstellUtils へ移し終えたら要らなくなる。

`AutoScroll = false` も残してある。
`RawImGui` が残り高さをちょうど埋めるので外側は動かないはずだが、
手引きが「中身が生 ImGui のあいだは false」と言っているのでそれに従う。

### 続き: 囲むだけでは足りない（2a7ac93 より前の話）

`RawImGui` は**カーソルの位置は合わせるが、幅と高さは合わせない。**
ImGui 側が見ている「描いてよい幅」は窓いっぱいのままになる。

そのため、こうなった。

| 症状 | 原因 |
|---|---|
| 右端で文字と表が切れる | ImGui が EstellUtils の余白を知らない |
| 送りのつまみが 2 本重なる | `EuWindow.AutoScroll` と ImGui 側の送りが二重になる |
| 長い説明が読めない | 生 ImGui の文字は既定で折り返さない |

移行中は、次の 3 つを最上段でやる。

```csharp
public override void Draw()
{
    // ① EstellUtils が空けてくれた領域の大きさを取る
    var area = EUi.AvailableRect;

    using var raw = EUi.RawImGui();

    // ② その大きさの子領域に閉じ込める（幅・高さ・送りが正しくなる）
    using var child = ImRaii.Child("##raw", new Vector2(area.Width, area.Height), false);
    if (!child) return;

    // ③ 折り返す位置を決める。0 は「右端で折り返す」
    ImGui.PushTextWrapPos(0f);
    try { this.DrawTabs(); }
    finally { ImGui.PopTextWrapPos(); }
}
```

さらに**窓側の送りを止める。**止めないと二重になる。

```csharp
this.AutoScroll = false;   // 中身を移し終えたら true に戻す
```

**この 3 点セットを手引きに書いておくと、移行がぐっと楽になる。**
`RawImGui` が幅と折り返しまで面倒を見てくれるなら、それが一番よい。

## EzConfigGui をやめた

ECommons の `EzConfigGui` が握っていた 3 つを自分で持つようにした。

| 以前 | いま |
|---|---|
| `EzConfigGui.Init(..., WindowType.Both)` | `EUi.Windows.Add` ＋ `UiBuilder.OpenMainUi` / `OpenConfigUi` へ自分で接続 |
| `EzConfigGui.Window.IsOpen` | `MainWindow.IsOpen` |
| 破棄は ECommons 任せ | `Dispose` で接続を外し `EUi.Shutdown()` |

**接続を外し忘れないこと。**
外し忘れると、読み込み直したあとに古い画面が描かれ続ける。

`WindowType.Both` をやめて手で 2 つ繋いでいるのは、
**主画面が無いと Dalamud の検査に指摘されるため**（0.1.2.4 で直した箇所）。
片方だけにすると同じ指摘が戻る。

## 段 3 が止まっている理由（API へ要望）

### ① タブを他所から選べない

`EUi.TabBar` は選択状態を内部（`ctx.Store`）に持ち、外から指定する口が無い。

このプラグインには**状況タブから「プリセットへ飛ぶ」**動きが 3 か所ある。
不足している設定を見つけたとき、その場からプリセットの画面へ送っている。
いまは `ImGuiTabItemFlags.SetSelected` で実現している。

```csharp
// いま
var flags = select ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
using var tab = ImRaii.TabItem("プリセット", flags);
```

欲しい形（案）:

```csharp
var tabs = EUi.TabBar("##tabs", labels, select: jumpTo);   // jumpTo は string? か int?
```

### ② 入り切らないタブが黙って消える

```csharp
// EUi.Tabs.cs
if (x + width > rowRect.Max.X && i > 0)
    break;          // ← 描かないだけ。行き先が無くなる
```

このプラグインはタブが **7 枚（デバッグモードで 9 枚）**ある。
幅を狭めた利用者の画面で、後ろのタブ（寄付・デバッグ）へ**辿り着けなくなる。**
消えたことも分からない。

欲しいのは次のどれか。

1. 入り切らないぶんを「≫」のような送りで出す
2. タブ行を横スクロールさせる
3. 折り返して 2 行にする
4. せめて、入り切らなかった数を出す

**1 が使いやすいと思うが、3 が一番作りやすいはず。**

## 中身を移すときに使うもの（確認済み）

| いま使っているもの | 件数 | 移り先 |
|---|---|---|
| `ImGui.TextColored` | 300 | `EUi.TextColored` |
| `ImGui.TextUnformatted` | 95 | `EUi.Text` |
| `ImGui.SameLine` | 81 | `EUi.HStack()` |
| 表（`TableNextColumn` ほか） | 120 | `EUi.TableHeader` / `TableRow` / `TableCell` |
| `ImGui.Button` / `SmallButton` | 71 | `EUi.Button`（`ButtonStyle` で小さくできる） |
| `ImGui.Separator` | 43 | `EUi.Separator(label)` |
| `SetTooltip` / `IsItemHovered` | 36 | `.Tip()` |
| `ImGui.Selectable` | 14 | `EUi.Selectable` |
| `ImGui.Checkbox` | 12 | `EUi.Checkbox` |
| `ImGui.Combo` | 7 | `EUi.Combo` |
| `InputText` / `InputTextWithHint` | 8 | `EUi.TextInput`（`hint` あり） |
| `IsItemClicked(Right)` | 5 | `WidgetResult.RightClicked` |
| `Begin/EndDisabled` | 4 | `EUi.Disabled()` |
| `ImGui.ProgressBar` | 3 | `EUi.ProgressBar` |

**足りないものは上の 2 つだけ。**ほかは揃っている。

### 確認できていないもの

`ImGui.TableSetupScrollFreeze`（見出し行を固定したまま中身だけ送る）に当たるものが
`EUi.TableHeader` / `TableRow` にあるか。交換候補の一覧で使っている。
無くても移せるが、長い一覧で見出しが流れる。

## 段 3 を入れた（0.1.5.4）

### 何が変わったか

| | 以前 | いま |
|---|---|---|
| タブバー | `ImRaii.TabBar`（生 ImGui） | `EUi.TabBar` |
| 入り切らないタブ | 見出しが省略され、後ろへ行けない | **折り返して全部出る** |
| 中身の囲み | `Draw()` で 1 回まとめて | **タブごとに開く** |
| プリセットへ飛ぶ | `ImGuiTabItemFlags.SetSelected` | `EUi.SelectTab(id, "プリセット")` |

### 囲みをタブごとにした理由

**中身を 1 枚ずつ移せるようにするため。**

以前は `Draw()` の先頭で `RawImGui` を 1 回開き、その中で全タブを描いていた。
この形だと、あるタブだけ EstellUtils へ移そうとしても、
そのタブも生 ImGui の囲みの中に居続けることになる。

いまは各タブが自分で `RawTabScope.Open()` を呼ぶ。

```csharp
private void DrawStatusTab()
{
    // 中身はまだ生の ImGui。移し終えたらこの 1 行を外す。
    using var raw = RawTabScope.Open();
    ...
}
```

**移し終えたタブから、この 1 行を外していける。**
全部外れたら `RawTabScope.cs` ごと消す。

### `RawTabScope` を作った理由

生 ImGui を描くには 2 つ要り、どちらも対で閉じる必要がある。

```
EUi.RawImGui()        ImGui 側に同じ大きさの領域を用意する
PushTextWrapPos(0f)   生 ImGui の文字は既定で折り返さない
```

9 枚のタブそれぞれに `try / finally` を書くと読みにくい。
`using` 1 行で済むようにまとめた。

### 飛び先をラベルで指す

「プリセットへ飛ぶ」は状況タブに 3 か所ある。

添字で指すと、**デバッグモードでタブが 2 枚増減したときに飛び先がずれる。**
`EUi.SelectTab(id, "プリセット")` のラベル版を使う
（提言 F-2 で足してもらったもの）。

### 次（段 4）

中身を 1 枚ずつ移す。`RichLabel` が入ったので、
`TextColored` → `SameLine` → `TextColored` の連なりが 1 行で書ける。

| ファイル | `TextColored` | `SameLine` |
|---|---|---|
| `PresetTab.cs` | 103 | 27 |
| `MainWindow.cs` | 72 | 13 |
| `MainWindow.DebugTab.cs` | 46 | 15 |
| `MainWindow.CraftPlanTab.cs` | 44 | 9 |
| `MainWindow.StatusTab.cs` | 30 | 12 |
| `FateTab.cs` | 24 | 2 |
| `MainWindow.SetupGuide.cs` | 15 | 3 |

**`FateTab.cs` は当面触らない。**別マシンで作業中のため。
