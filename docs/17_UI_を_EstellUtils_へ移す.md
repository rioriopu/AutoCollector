# UI を EstellUtils へ移す

対象: `AutoCollector`
ブランチ: `feature/estell-ui`
相手: `C:\Users\Administrator\TempRepos\EstellUtils`（`rioriopu/EstellUtils`）

## いまどこまで入っているか

| 段 | 内容 | 状態 |
|---|---|---|
| 1 | `EUi.Initialize` / `EUi.Shutdown` | 済 |
| 2 | ウィンドウを `EuWindow` へ | 済 |
| 3 | タブバーを `EUi.TabBar` へ | **API 待ち** |
| 4 | 各タブの中身を移す | 3 の後 |
| 5 | 設定の多い画面を属性バインディングへ | 4 の後 |

段 2 まで入れた時点で、**枠・タイトルバー・スクロールバーは EstellUtils の描画**になる。
中身は生 ImGui のまま動く（移行手引きのとおり）。

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
