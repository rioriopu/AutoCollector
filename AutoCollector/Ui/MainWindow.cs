using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EstellUtils.UI;
using EstellUtils.UI.Core;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Windowing;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;
using ECommons.DalamudServices;

namespace AutoCollector.Ui;

/// <summary>
/// メイン画面。MVP 段階では「読み取り専用の状況表示」と「セルフチェック結果」が中心。
/// 交換の実行系は S5 以降で追加する。
/// </summary>
public sealed partial class MainWindow : EuWindow
{
    /// <summary>記録をコピーした結果。押しても無反応に見えないよう画面へ返す。</summary>
    private string anomalyCopyNote = string.Empty;

    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Auto Collector")
    {
        this.plugin = plugin;
        this.presetTab = new PresetTab(plugin);
        this.fateTab = new FateTab(plugin);

        // 画面の大きさ。交換候補の一覧と状況の表が入る幅を既定にする。
        this.Size = new Vector2(760f, 560f);
        this.MinSize = new Vector2(520f, 360f);

        // **送りは 1 つだけにする。**
        //
        // EuWindow の自動スクロールと、中の生 ImGui が持つ送りが二重になり、
        // つまみが 2 本並んで重なっていた。
        //
        // いまは RawImGui が残り高さをちょうど埋めるので外側は動かないが、
        // 中身が生 ImGui のあいだは手引きどおり、そちら側の送りに任せる。
        // 中身を EstellUtils へ移し終えたら、ここを true に戻す。
        this.AutoScroll = false;
    }

    private bool onlyWithLocation = true;
    private string rewardFilter = string.Empty;
    private int exchangeChoice;

    /// <summary>画面の読み取り結果を保持する。毎フレーム読み直すと描画だけで重くなる。</summary>
    private DateTime nextShopReadUtc = DateTime.MinValue;
    private IReadOnlyList<ShopEntry> cachedEntries = [];
    private ShopHeader cachedHeader = new(0, 0, 0, string.Empty, 0);
    private string cachedShopFailure = string.Empty;
    private List<(string Label, AtkValueProbe Probe)> cachedDiagnostics = [];
    private readonly PresetTab presetTab;

    /// <summary>FATE 周回の設定。稼ぎ方の 1 つなのでタブを分ける。</summary>
    private readonly FateTab fateTab;

    public override void Draw()
    {
        // **タブバーは EstellUtils が描く。**
        //
        // 入り切らないぶんは折り返すので、窓を狭めても
        // 後ろのタブ（寄付・デバッグ）へ辿り着ける。
        // 生の ImGui では省略されて行き先が消えていた。
        //
        // 中身はタブごとに RawTabScope を開いて、まだ生の ImGui で描く。
        // 移し終えたタブから、その囲みを外していく。
        var labels = new List<string> { "状況", "プリセット", "FATE 周回" };

        // 開発・調査用のタブはデバッグモードのときだけ出す。
        if (Plugin.C.DebugMode)
        {
            labels.Add("交換候補");
            labels.Add("ショップ照合");
        }

        labels.Add("診断");
        labels.Add("設定");
        labels.Add("製作計画");
        labels.Add("デバッグ");
        labels.Add("寄付");

        // **「プリセットへ飛ぶ」はラベルで指す。**
        //
        // 添字で指すと、デバッグモードでタブが 2 枚増減したときに
        // 飛び先がずれる。ラベルなら並びが変わっても当たる。
        if (this.jumpToPresetTab)
        {
            this.jumpToPresetTab = false;
            EUi.SelectTab(TabBarId, "プリセット");
        }

        var tabs = EUi.TabBar(TabBarId, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(labels));

        if (tabs.IsSelected("状況"))
        {
            this.DrawStatusTab();
        }
        else if (tabs.IsSelected("プリセット"))
        {
            this.presetTab.Draw();
        }
        else if (tabs.IsSelected("FATE 周回"))
        {
            this.fateTab.Draw();
        }
        else if (tabs.IsSelected("交換候補"))
        {
            this.DrawExchangeTab();
        }
        else if (tabs.IsSelected("ショップ照合"))
        {
            this.DrawShopTab();
        }
        else if (tabs.IsSelected("診断"))
        {
            this.DrawDiagnosticsTab();
        }
        else if (tabs.IsSelected("設定"))
        {
            this.DrawSettingsTab();
        }
        else if (tabs.IsSelected("製作計画"))
        {
            this.DrawCraftPlanTab();
        }
        else if (tabs.IsSelected("デバッグ"))
        {
            this.DrawDebugTab();
        }
        else if (tabs.IsSelected("寄付"))
        {
            this.DrawDonationTab();
        }
    }

    /// <summary>タブバーを覚えておくための名前。飛び先の指定にも使う。</summary>
    private const string TabBarId = "##autocollector_tabs";

    /// <summary>
    /// 開いたときは、プリセットのタブから始める。
    ///
    /// いちばん触るのはプリセットなので、開くたびにそこを出す（2026-09-28 利用者の要望）。
    /// 飛び先の旗を立てるだけにして、実際の切り替えは Draw の中の既存の経路に任せる。
    /// </summary>
    public override void OnOpen()
    {
        base.OnOpen();
        this.jumpToPresetTab = true;
    }

    /// <summary>
    /// 手動で開いた交換ショップの中身を読み取り、ゲームデータと照合する。
    ///
    /// このタブは読み取りと照合だけを行い、交換は一切実行しない。
    /// AtkValue の配置が現在のクライアントで正しいかを、交換を撃つ前に確認するための場所。
    /// </summary>
    /// <summary>AtkValue 配置の診断表の列。</summary>
    private static readonly TableColumn[] LayoutDiagColumns =
    [
        new("項目", 190f),
        new("型", 90f),
        new("値", 100f, Align.End),
        new("判定", SizeSpec.Weight(1f)),
    ];

    /// <summary>交換ショップの読み取り結果の表の列。</summary>
    private static readonly TableColumn[] ShopEntryColumns =
    [
        new("枠", 40f, Align.End),
        new("ItemId", 70f, Align.End),
        new("アイテム", SizeSpec.Weight(1f)),
        new("画面コスト", 80f, Align.End),
        new("index", 55f, Align.End),
        new("ゲームデータとの照合", 200f),
    ];

    /// <summary>InclusionShop の読み取り結果の表の列。</summary>
    private static readonly TableColumn[] InclusionEntryColumns =
    [
        new("枠", 40f, Align.End),
        new("ItemId", 70f, Align.End),
        new("アイテム", SizeSpec.Weight(1f)),
        new("コスト", 80f, Align.End),
        new("通貨値", 70f, Align.End),
        new("index", 55f, Align.End),
    ];

    /// <summary>callback の記録の表の列。見出しは出さない。</summary>
    private static readonly TableColumn[] CallbackColumns =
    [
        new("時刻", 70f),
        new("内容", SizeSpec.Weight(1f), Wrap: true),
    ];

    /// <summary>
    /// ショップ照合タブ（デバッグモードのみ）。
    ///
    /// <b>EstellUtils へ移し終えたタブ。</b><c>RawTabScope</c> で囲まない。
    /// </summary>
    private void DrawShopTab()
    {
        EUi.MutedParagraph("交換ショップを手動で開いた状態で確認してください。このタブは交換を実行しません。");
        EUi.Spacing();

        var shop = this.plugin.ShopService;

        var layout = shop.Layout;
        EUi.Label($"配置: NumEntries={layout.NumEntries} / CurrencyAmount={layout.CurrencyAmount} / Cost={layout.EntryCost} / ItemId={layout.EntryItemId} / Index={layout.EntryIndex}")
            .Tip($"この数値は次の場所の JSON で変更できます:\n{DataFileLoader.GetDataDirectory()}\\atkvalue_layout.json");

        EUi.Separator();

        // InclusionShop（スクリップ交換など）は別アドオン。開いていればそちらを表示する。
        if (this.plugin.InclusionShopService.IsOpen())
        {
            this.DrawInclusionShop();
            EUi.Spacing();
            EUi.Separator();
            this.DrawCallbackRecorder();
            return;
        }

        if (!shop.IsShopOpen())
        {
            EUi.TextColored("交換ショップが開いていません。", NoteKind.Warning);
            return;
        }

        // 画面の読み取りは重い。表示のためだけに毎フレーム読み直さない。
        if (DateTime.UtcNow >= this.nextShopReadUtc)
        {
            this.nextShopReadUtc = DateTime.UtcNow.AddMilliseconds(250);
            this.cachedDiagnostics = shop.DiagnoseLayout();
            if (!shop.TryReadEntries(out var readEntries, out var readHeader, out var readFailure))
            {
                this.cachedEntries = [];
                this.cachedShopFailure = readFailure;
            }
            else
            {
                this.cachedEntries = readEntries;
                this.cachedHeader = readHeader;
                this.cachedShopFailure = string.Empty;
            }
        }

        var diagnostics = this.cachedDiagnostics;
        if (diagnostics.Count > 0)
        {
            // TreeNode は閉じた状態から始まるので defaultOpen は false。
            using var node = EUi.Section("AtkValue 配置の診断", defaultOpen: false);
            if (node.IsVisible)
            {
                EUi.TableHeader(LayoutDiagColumns);

                var row = 0;
                foreach (var (label, probe) in diagnostics)
                {
                    using (EUi.TableRow(LayoutDiagColumns, row++))
                    {
                        EUi.TableCell(label);
                        EUi.TableCell(probe.TypeName);
                        EUi.TableCell(probe.Usable ? probe.Value.ToString("N0") : "-");

                        if (!probe.InRange)
                        {
                            EUi.TableCell("範囲外", color: EUi.NoteColor(NoteKind.Danger));
                        }
                        else if (probe.Usable)
                        {
                            EUi.TableCell("読める", color: EUi.NoteColor(NoteKind.Success));
                        }
                        else
                        {
                            EUi.TableCell("整数として読めない", color: EUi.NoteColor(NoteKind.Warning));
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(this.cachedShopFailure))
        {
            EUi.WrapColored(this.cachedShopFailure, NoteKind.Danger);
            return;
        }

        var entries = this.cachedEntries;
        var header = this.cachedHeader;

        EUi.Label($"申告エントリ数: {header.DeclaredEntryCount} / 実際に読めた件数: {entries.Count}");
        if (header.UnreadableEntries > 0)
        {
            EUi.TextColored($"読み取れなかったエントリ: {header.UnreadableEntries} 件", NoteKind.Warning);
        }

        if (header.CurrencyIcon != 0)
        {
            EUi.Label($"画面上の所持通貨: {header.CurrencyAmount:N0}（アイコン ID {header.CurrencyIcon}）");
        }
        else
        {
            EUi.Label($"画面上の所持通貨: {header.CurrencyAmount:N0}");
            EUi.MutedParagraph($"  アイコン ID は読めませんでした（型 {header.CurrencyIconType}）。補助情報のため交換には影響しません");
        }

        // 監視中の通貨と画面の所持数が一致するかを見ると、配置が正しいかの強い裏付けになる。
        var resolver = this.plugin.ExchangeResolver;
        if (resolver.TargetCurrencyItemId != 0 &&
            this.plugin.CurrencyService.TryGetCount(resolver.TargetCurrencyItemId, out var actual))
        {
            if (actual == (int)header.CurrencyAmount)
            {
                EUi.WrapColored($"所持数の一致を確認しました（インベントリ {actual:N0} = 画面 {header.CurrencyAmount:N0}）。配置は正しいと判断できます。", NoteKind.Success);
            }
            else
            {
                EUi.WrapColored($"インベントリ {actual:N0} と画面 {header.CurrencyAmount:N0} が違います。別通貨のショップか、CurrencyAmount の位置がずれています。", NoteKind.Warning);
            }
        }

        EUi.Spacing();

        // どのショップが開いているかを先に特定する。
        // 同じアイテムが複数のショップに別の値段で載っているため、
        // ItemId だけで定義を引くと別のショップの値段と比較してしまう。
        var identification = shop.IdentifyShop(entries, resolver.LiveResults);

        if (identification.IsConfident)
        {
            EUi.WrapColored($"開いているショップを特定しました: Shop {identification.ShopId}（画面の {identification.TotalEntries} 件すべてが一致）", NoteKind.Success);
        }
        else if (identification.ShopId is not null)
        {
            EUi.WrapColored(identification.Detail, NoteKind.Warning);
        }
        else
        {
            EUi.WrapColored(identification.Detail, NoteKind.Danger);
        }

        // ショップを特定できていないときは照合しない。
        // ShopId が null のときに「null なら全件通す」書き方をすると、
        // 別ショップの定義と比べて誤った一致を出す。
        var definitionsByItem = new Dictionary<uint, ExchangeDefinition>();
        if (identification.IsConfident)
        {
            foreach (var def in resolver.LiveResults)
            {
                if (def.ShopId != identification.ShopId)
                {
                    continue;
                }

                definitionsByItem.TryAdd(def.RewardItemId, def);
            }
        }

        var matched = 0;
        var mismatched = 0;
        var unknown = 0;

        // **見出しは送り領域の外に置いて固定する。**
        //
        // EstellUtils に ScrollFreeze に当たるものが無いため（docs/18 A-2）、
        // 見出しを外、行を中に置く。送り領域はつまみが出ているとき内容の右端を削るので、
        // reserveScrollbar で見出し側も同じだけ空けないと、行数が増えた瞬間に列がずれる。
        EUi.TableHeader(ShopEntryColumns, reserveScrollbar: true);

        using (EUi.Scroll("##shopentries", 400f))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                using (EUi.TableRow(ShopEntryColumns, i))
                {
                    EUi.TableCell(entry.Slot.ToString());
                    EUi.TableCell(entry.ItemId.ToString());
                    EUi.TableCell(entry.ItemName);
                    EUi.TableCell(entry.CostAmount.ToString("N0"));
                    EUi.TableCell(entry.Index.ToString());

                    if (!definitionsByItem.TryGetValue(entry.ItemId, out var def))
                    {
                        unknown++;
                        EUi.TableCell("このショップの定義になし", color: EUi.Colors.TextMuted);
                    }
                    else if (def.CurrencyCost == entry.CostAmount)
                    {
                        matched++;
                        EUi.TableCell($"一致 ({def.CurrencyCost:N0})", color: EUi.NoteColor(NoteKind.Success));
                    }
                    else
                    {
                        mismatched++;
                        EUi.TableCell($"不一致 データ {def.CurrencyCost:N0}", color: EUi.NoteColor(NoteKind.Danger));
                    }
                }
            }
        }

        EUi.Spacing();
        if (resolver.Stage != ResolverBuildStage.Completed)
        {
            EUi.WrapColored("「交換候補」タブで通貨を選ぶと、ゲームデータとの照合結果が出ます。", NoteKind.Warning);
            return;
        }

        EUi.Label($"照合: 一致 {matched} / 不一致 {mismatched} / 対象外 {unknown}");

        if (mismatched > 0)
        {
            EUi.WrapColored("不一致があります。この状態では交換を実行してはいけません。", NoteKind.Danger);
        }
        else if (matched > 0)
        {
            EUi.WrapColored("コストがすべて一致しました。ID による照合が機能しています。", NoteKind.Success);
        }

        EUi.Spacing();
        EUi.Separator();
        this.DrawExchangeExecution(identification, definitionsByItem, entries);

        EUi.Spacing();
        EUi.Separator();
        this.DrawCallbackRecorder();
    }

    /// <summary>
    /// InclusionShop（スクリップ交換など）の内容を表示する。
    /// 2 段のドロップダウン（系統・種別）で絞ってから交換する構造のため、
    /// いまどこが選ばれているかも合わせて出す。
    /// </summary>
    private unsafe void DrawInclusionShop()
    {
        var service = this.plugin.InclusionShopService;

        EUi.TextColored("InclusionShop（アイテム交換）が開いています。", NoteKind.Success);

        if (service.TryGetSelection(out var selection) && selection is not null)
        {
            EUi.Label(
                $"InclusionShop {selection.InclusionShopId} / 系統 {selection.SelectedCategoryIndex + 1}・{selection.CategoryCount} " +
                $"(行 {selection.SelectedCategoryRowId} / シリーズ {selection.SelectedSeriesId})");
            EUi.Label($"種別 タブ {selection.SelectedSubCategoryTab} / 表示 {selection.VisibleSubCategoryCount}");
        }
        else
        {
            EUi.TextColored("選択状態を読めませんでした。", NoteKind.Warning);
        }

        if (!service.TryGetAddon(out var addon))
        {
            return;
        }

        if (!service.TryReadEntries(addon, out var entries, out var currency, out var failure))
        {
            EUi.WrapColored(failure, NoteKind.Danger);
            return;
        }

        EUi.Label($"画面上の通貨: {currency:N0} / エントリ {entries.Count} 件");

        if (entries.Count == 0)
        {
            EUi.WrapColored("種別が選ばれていないため、品目が表示されていません。", NoteKind.Warning);
            return;
        }

        // 見出しは送り領域の外。つまみのぶんを空けないと列がずれる（docs/18 A-2）。
        EUi.TableHeader(InclusionEntryColumns, reserveScrollbar: true);

        using (EUi.Scroll("##inclusionentries", 320f))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                using (EUi.TableRow(InclusionEntryColumns, i))
                {
                    EUi.TableCell(entry.Slot.ToString());
                    EUi.TableCell(entry.ItemId.ToString());
                    EUi.TableCell(entry.ItemName);
                    EUi.TableCell(entry.CostAmount.ToString("N0"));

                    // 8 未満なら特殊通貨のインデックス。ItemId ではない点が分かるように出す。
                    EUi.TableCell(entry.CostItemId < 8 ? $"idx {entry.CostItemId}" : entry.CostItemId.ToString());

                    EUi.TableCell(entry.Index.ToString());
                }
            }
        }
    }

    /// <summary>
    /// 実際に 1 個だけ交換する。ボタンは予約を立てるだけで、発火は Framework.Update 内で行う。
    /// ImGui の Draw から直接撃つと、読み取ったフレームと発射するフレームがずれる。
    /// </summary>
    private void DrawExchangeExecution(
        ShopIdentification identification,
        Dictionary<uint, ExchangeDefinition> definitionsByItem,
        IReadOnlyList<ShopEntry> entries)
    {
        var executor = this.plugin.ExchangeExecutor;

        EUi.Heading("交換の実行（1 個のみ）");

        // 結果未確認の記録が残っている間は、新しい交換を一切受け付けない
        if (executor.InFlight is { } pending)
        {
            EUi.TextColored("前回の交換の結果が未確認です。", NoteKind.Danger);
            EUi.Label($"  {pending.RewardName} × {pending.RewardQuantity} / コスト {pending.CurrencyCost} / index {pending.CallbackIndex}");
            EUi.Label($"  発火時: 通貨 {pending.CurrencyBefore:N0} / 報酬 {pending.RewardBefore:N0}");
            if (!string.IsNullOrEmpty(pending.Outcome))
            {
                EUi.Paragraph($"  結果: {pending.Outcome}");
            }

            EUi.MutedParagraph("  ゲーム内で実際の所持数を確認してからクリアしてください。");
            if (EUi.Button("確認したのでクリアする##clearinflight"))
            {
                executor.ClearInFlight();
            }

            return;
        }

        if (!string.IsNullOrEmpty(executor.StatusDetail))
        {
            var kind = executor.Step switch
            {
                ExchangeStep.Done => NoteKind.Success,
                ExchangeStep.Error => NoteKind.Danger,
                _ => NoteKind.Warning,
            };
            EUi.WrapColored($"{StatusText.StepLabel(executor.Step)}: {executor.StatusDetail}", kind);
        }

        if (!identification.IsConfident)
        {
            EUi.WrapColored("ショップを特定できていないため、交換は実行できません。", NoteKind.Warning);
            return;
        }

        // 交換できる候補（照合が一致したものだけ）を出す
        var selectable = entries
            .Where(e => definitionsByItem.TryGetValue(e.ItemId, out var d) && d.CurrencyCost == e.CostAmount)
            .OrderBy(e => e.CostAmount)
            .ToList();

        if (selectable.Count == 0)
        {
            EUi.WrapColored("照合が一致したエントリがありません。", NoteKind.Warning);
            return;
        }

        var names = selectable.Select(e => $"{e.ItemName}（{e.CostAmount:N0}）").ToArray();
        this.exchangeChoice = Math.Clamp(this.exchangeChoice, 0, names.Length - 1);

        EUi.Combo("交換対象##exchangetarget", ref this.exchangeChoice, names, width: 320f);

        var chosen = selectable[this.exchangeChoice];
        var definition = definitionsByItem[chosen.ItemId];

        EUi.MutedParagraph(
            $"  {chosen.ItemName} を 1 回交換します（コスト {chosen.CostAmount:N0} / 取得 {definition.RewardQuantity} 個 / index {chosen.Index}）");

        if (!executor.CanRequest)
        {
            EUi.Button("交換する##doexchange", disabled: true);
            return;
        }

        using (EUi.HStack())
        {
            if (EUi.Button("交換する##doexchange"))
            {
                if (!executor.Request(definition, out var reason))
                {
                    this.plugin.AnomalyLog.Warn("Exchange", reason);
                }
            }

            EUi.Muted("通貨を消費します");
        }
    }

    /// <summary>
    /// 手動で交換したときの callback 引数を実測するための記録機能。
    ///
    /// 交換 callback の第 1 引数の意味は、参照したどのソースにも記述がない。
    /// 推測で撃たないために、実際の値をここで確認してから実装する。
    /// </summary>
    private void DrawCallbackRecorder()
    {
        var recorder = this.plugin.CallbackRecorder;

        EUi.Heading("callback の記録（S5 実装前の実測用）");
        EUi.MutedParagraph("記録を開始してから、手動で 1 個だけ交換してください。押した操作の引数がそのまま出ます。");

        // AddonFilter を空にすると全アドオンを記録する。
        // ショップ以外（確認ダイアログ等）が飛んでいるかを調べるには空にする必要がある。
        var filter = recorder.AddonFilter;
        if (EUi.TextInput(
            "記録対象アドオン##callbackfilter", ref filter,
            hint: "空にすると全アドオンを記録", maxLength: 64, width: 260f))
        {
            recorder.AddonFilter = filter;
        }

        using (EUi.HStack())
        {
            var recording = recorder.IsRecording;
            if (EUi.Checkbox("記録する##callbackrec", ref recording))
            {
                if (recording)
                {
                    recorder.Start();
                }
                else
                {
                    recorder.Stop();
                }
            }

            if (EUi.Button("記録を消去##callbackclear"))
            {
                recorder.Clear();
            }
        }

        var records = recorder.Snapshot();
        if (records.Count == 0)
        {
            EUi.Muted("まだ記録はありません。");
            return;
        }

        using (EUi.Scroll("##callbackrecords", 160f))
        {
            // 新しいものから。LINQ の Reverse で毎フレーム作り直さず、添字で逆から読む。
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[records.Count - 1 - i];
                var text = $"{record.AddonName}  updateState={record.UpdateState}  {record.Signature}";

                using (EUi.TableRow(CallbackColumns, i))
                {
                    EUi.TableCell($"{record.At:HH:mm:ss}", color: EUi.Colors.TextMuted);
                    EUi.Paragraph(text);
                }
            }
        }
    }

    /// <summary>交換候補の表の列。全セルが 1 行なので TableCell で足りる。</summary>
    private static readonly TableColumn[] CandidateColumns =
    [
        new("コスト", 60f, Align.End),
        new("個数", 45f, Align.End),
        new("NPC", SizeSpec.Weight(1f)),
        new("エリア", SizeSpec.Weight(1f)),
        new("座標", 150f),
        new("経路", 90f),
        new("実行", 130f),
    ];

    /// <summary>
    /// 交換候補タブ（デバッグモードのみ）。
    /// 通貨を選ぶと、その通貨で買えるものをゲームデータから解決して一覧表示する。
    /// ここではゲーム状態を変更しない（「行って交換」を押したときだけ予約を立てる）。
    ///
    /// <b>EstellUtils へ移し終えたタブ。</b><c>RawTabScope</c> で囲まない。
    /// </summary>
    private void DrawExchangeTab()
    {
        var slots = this.plugin.TomestoneService.ListSlots();
        if (slots.Count == 0)
        {
            EUi.TextColored("トームストーンを解決できませんでした。", NoteKind.Danger);
            return;
        }

        EUi.Label("通貨を選ぶと、ゲームデータから交換候補を解決します。");
        EUi.Spacing();

        // 以前は SameLine を並べて最後に NewLine を打っていた。
        // 幅に入り切らないと押せない通貨が出るので、折り返す横並びにする。
        using (EUi.HStack(wrap: true))
        {
            foreach (var slot in slots)
            {
                if (string.IsNullOrEmpty(slot.Name))
                {
                    continue;
                }

                if (EUi.Button($"{slot.Name}##build{slot.TomestonesRowId}"))
                {
                    this.plugin.ExchangeResolver.BeginBuild(slot.ItemId);
                }
            }
        }

        EUi.Separator();

        var resolver = this.plugin.ExchangeResolver;

        switch (resolver.Stage)
        {
            case ResolverBuildStage.NotStarted:
                EUi.Label("通貨を選択してください。");
                return;

            case ResolverBuildStage.Failed:
                EUi.WrapColored("索引の構築に失敗しました。診断タブを確認してください。", NoteKind.Danger);
                return;

            case ResolverBuildStage.Completed:
                break;

            default:
                EUi.ProgressBar(
                    resolver.BuildProgress,
                    $"{resolver.Stage} {resolver.BuildProgress * 100:F0}%");
                return;
        }

        // 表示の切り替えだけ。再構築は要らないので戻り値は見ない。
        EUi.Checkbox("座標を解決できたものだけ表示", ref this.onlyWithLocation);

        var groups = resolver.GroupByReward(this.onlyWithLocation);
        EUi.Label($"報酬アイテム {groups.Count} 種 / 定義 {resolver.Results.Count} 件");

        EUi.TextInput("##filter", ref this.rewardFilter, hint: "アイテム名で絞り込み", maxLength: 100);

        var filtered = string.IsNullOrWhiteSpace(this.rewardFilter)
            ? groups
            : groups.Where(g => g.RewardName.Contains(this.rewardFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var hidden = filtered.Count - 300;

        // 送り領域は残り高さを全部取る。下に 1 行置くので、その分を伝えておく。
        // 伝えないと、件数の行が出る場所を失う。
        using (EUi.Scroll(
            "##candidates",
            SizeSpec.Fill,
            reserveBelow: hidden > 0 ? EUi.LineHeight : 0f))
        {
            foreach (var group in filtered.Take(300))
            {
                // TreeNode は閉じた状態から始まるので defaultOpen は false。
                using var node = EUi.Section(
                    group.RewardName, defaultOpen: false, id: $"cand{group.RewardItemId}");

                if (!node.IsVisible)
                {
                    continue;
                }

                EUi.TableHeader(CandidateColumns);

                var definitions = group.Definitions;

                for (var i = 0; i < definitions.Count; i++)
                {
                    var def = definitions[i];

                    using (EUi.TableRow(CandidateColumns, i))
                    {
                        EUi.TableCell(def.CurrencyCost.ToString("N0"));
                        EUi.TableCell(def.RewardQuantity.ToString());
                        EUi.TableCell(string.IsNullOrEmpty(def.NpcName) ? $"<{def.NpcDataId}>" : def.NpcName);

                        if (def.TerritoryId == 0)
                        {
                            EUi.TableCell("未解決", color: EUi.NoteColor(NoteKind.Danger));
                        }
                        else
                        {
                            EUi.TableCell(NpcLocationService.GetTerritoryName(def.TerritoryId));
                        }

                        EUi.TableCell(def.HasLocation
                            ? $"{def.NpcPosition.X:F1}, {def.NpcPosition.Y:F1}, {def.NpcPosition.Z:F1}"
                            : "-");

                        EUi.TableCell(def.Path.ToString())
                            .TipIf(
                                !string.IsNullOrEmpty(def.MenuHint),
                                $"選択肢ヒント: {def.MenuHint}\nShopId: {def.ShopId}");

                        this.DrawTravelButton(def);
                    }
                }
            }
        }

        // 送り領域の外。reserveBelow でこの 1 行ぶんを空けてある。
        if (hidden > 0)
        {
            EUi.TextColored($"{hidden} 件は表示していません。絞り込んでください。", NoteKind.Warning);
        }
    }

    /// <summary>
    /// NPC のところまで移動して交換する。
    ///
    /// 表の「実行」列から呼ぶ。どの経路を通っても <b>セルをちょうど 1 つ</b>
    /// 消費する。消費しないと以降の列がずれる。
    /// </summary>
    private void DrawTravelButton(ExchangeDefinition definition)
    {
        var executor = this.plugin.ExchangeExecutor;

        if (!definition.HasLocation)
        {
            EUi.TableCell("座標未解決", color: EUi.Colors.TextMuted);
            return;
        }

        var sameArea = Svc.ClientState.TerritoryType == definition.TerritoryId;
        if (!sameArea)
        {
            // 別エリアならテレポートが必要になる。
            // Lifestream が無いかエーテライト未アクセスなら、押せても失敗するので理由を出す。
            if (!this.plugin.Lifestream.IsLoaded)
            {
                EUi.TableCell("Lifestream 未導入", color: EUi.Colors.TextMuted)
                    .Tip("別エリアへの移動には Lifestream が必要です。");
                return;
            }

            if (!this.plugin.AetheryteService.CanReach(definition.TerritoryId))
            {
                EUi.TableCell("未アクセス", color: EUi.Colors.TextMuted)
                    .Tip($"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} のエーテライトにアクセスしていないため、テレポートできません。");
                return;
            }
        }

        var label = $"行って交換##travel{definition.ShopId}_{definition.RewardItemId}_{definition.NpcDataId}";

        if (!executor.CanRequest)
        {
            EUi.Button(label, disabled: true);
            return;
        }

        var tip = sameArea
            ? $"{definition.NpcName} まで移動して 1 個交換します。通貨を消費します。"
            : $"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} へテレポートし、{definition.NpcName} まで移動して 1 個交換します。通貨を消費します。";

        if (EUi.Button(label).Tip(tip))
        {
            if (!executor.RequestWithTravel(definition, out var reason))
            {
                this.plugin.AnomalyLog.Warn("Exchange", reason);
            }
        }
    }

    /// <summary>
    /// 連携プラグインの一覧。
    ///
    /// 問題が無いときは畳んでおく。5 行を常に並べると、
    /// 使っていないものまで壊れているように見える。
    ///
    /// 状況タブの内側から呼ぶ。そのタブと同じく EstellUtils で描く。
    /// </summary>
    private void DrawPluginTable()
    {
        var missing = 0;
        if (!this.plugin.Vnavmesh.IsLoaded)
        {
            missing++;
        }

        if (!this.plugin.Lifestream.IsLoaded)
        {
            missing++;
        }

        if (Plugin.C.RequireExternalAutomationRunning && !this.plugin.AutoDuty.IsLoaded)
        {
            missing++;
        }

        using var node = EUi.Section(
            missing == 0 ? "連携プラグイン: 問題ありません" : $"連携プラグイン: {missing} 件に注意",
            defaultOpen: missing != 0,
            id: "plugins");

        if (!node.IsVisible)
        {
            return;
        }

        var combat = this.plugin.Combat;
        if (combat.RestartCount > 0 && !combat.KeeperGaveUp)
        {
            var status = string.IsNullOrEmpty(combat.KeeperStatus) ? string.Empty : $" / {combat.KeeperStatus}";
            EUi.MutedParagraph($"周回の維持: 再開 {combat.RestartCount} 回{status}");
        }

        var adPaused = this.plugin.AutoDuty.TryIsPaused(out var reportedPaused) && reportedPaused;

        // 再開ボタンを出すのは、止まっているときだけ。
        // Cell なら中に何個置いても列は 1 つなので、行の中へ戻せた。
        var showResume = this.plugin.AutoDuty.IsLoaded &&
                         (adPaused ||
                          (this.plugin.ExchangeExecutor.LastResumeTerritoryId != 0 &&
                           this.plugin.AutoDuty.TryIsStopped(out var adIdle) && adIdle));

        EUi.TableHeader(PluginColumns);

        var row = 0;

        DrawRow(
            ref row, "AutoDuty", "周回に相乗りするなら必須", this.plugin.AutoDuty.IsLoaded,
            "周回に相乗りできません。設定タブで「AutoDuty や Artisan が動作しているときだけ交換する」を切ると、プリセットだけで動きます",
            () =>
            {
                if (!this.plugin.AutoDuty.TryIsStopped(out var stopped))
                {
                    return [TextRun.Of("状態を取得できません", NoteKind.Danger)];
                }

                if (stopped)
                {
                    return [new TextRun("停止中")];
                }

                if (adPaused)
                {
                    return [TextRun.Of("一時停止中", NoteKind.Warning)];
                }

                this.plugin.AutoDuty.TryIsLooping(out var looping);
                this.plugin.AutoDuty.TryIsNavigating(out var navigating);

                return [TextRun.Of($"動作中（周回={looping} / 移動={navigating}）", NoteKind.Warning)];
            },
            withResume: showResume);

        DrawRow(
            ref row, "AutoRetainer", "任意", this.plugin.AutoRetainer.IsLoaded, "使いません（問題ありません）",
            () =>
            {
                var busy = this.plugin.AutoRetainer.IsBusyFailClosed();
                this.plugin.AutoRetainer.TryGetSuppressed(out var suppressed);

                var state = busy
                    ? TextRun.Of("処理中", NoteKind.Warning)
                    : new TextRun("待機中");

                if (!suppressed)
                {
                    return [state];
                }

                return
                [
                    state,
                    this.plugin.AutoRetainer.SuppressedByUs
                        ? TextRun.Of("（本プラグインが抑制中）", NoteKind.Success)
                        : new TextRun("（他が抑制中）", EUi.Colors.TextMuted),
                ];
            });

        DrawRow(
            ref row, "Artisan", "任意", this.plugin.Artisan.IsLoaded, "使いません（問題ありません）",
            () =>
            {
                var endurance = this.plugin.Artisan.TryGetEnduranceStatus(out var e) && e;
                var list = this.plugin.Artisan.TryIsListRunning(out var l) && l;

                var state = endurance || list
                    ? TextRun.Of(endurance ? "耐久モード実行中" : "製作リスト実行中", NoteKind.Warning)
                    : new TextRun("待機中");

                if (this.plugin.Artisan.StoppedByUs)
                {
                    return [state, TextRun.Of("（本プラグインが停止中）", NoteKind.Success)];
                }

                if (this.plugin.Artisan.TryGetStopRequest(out var stopped) && stopped)
                {
                    return [state, new TextRun("（他が停止中）", EUi.Colors.TextMuted)];
                }

                return [state];
            });

        DrawRow(
            ref row, "vnavmesh", "必須", this.plugin.Vnavmesh.IsLoaded, "交換所まで自動で移動できません",
            () => !this.plugin.Vnavmesh.TryIsReady(out var ready)
                ? [TextRun.Of("状態を取得できません", NoteKind.Danger)]
                : [new TextRun(ready ? "このエリアで利用可能" : "このエリアのメッシュが未準備")]);

        DrawRow(
            ref row, "Lifestream", "別エリアの交換所を使うなら必須", this.plugin.Lifestream.IsLoaded,
            "別エリアの交換所へテレポートできません。同じエリアの交換所だけが使えます",
            () => !this.plugin.Lifestream.TryIsBusy(out var busy)
                ? [TextRun.Of("状態を取得できません", NoteKind.Danger)]
                : [new TextRun(busy ? "処理中" : "待機中")]);

        // 状態の列は「断片の並び」で受け取る。色の違う文を 1 行へ並べるため。
        // ボタンを添える行だけ Cell で囲み、列の数を変えずに済ませる。
        void DrawRow(
            ref int row, string name, string necessity, bool loaded, string missingText,
            Func<TextRun[]> state, bool withResume = false)
        {
            using (EUi.TableRow(PluginColumns, row++))
            {
                EUi.TableCell(name);
                EUi.TableCell(necessity, color: EUi.Colors.TextMuted);

                EUi.TableCell(
                    loaded ? "あり" : "なし",
                    color: loaded ? EUi.NoteColor(NoteKind.Success) : EUi.Colors.TextMuted);

                if (!loaded)
                {
                    // 入っていないときの説明文は長い。列の Wrap に任せる。
                    EUi.Paragraph(missingText);
                    return;
                }

                if (!withResume)
                {
                    EUi.RichLabel(state());
                    return;
                }

                using (EUi.Cell())
                {
                    if (EUi.SmallButton("再開##resumead"))
                    {
                        if (!this.plugin.ExchangeExecutor.TryResumeAutoDutyManually(out var resumeReason))
                        {
                            this.plugin.AnomalyLog.Warn("AutoDuty", resumeReason);
                        }
                    }

                    EUi.RichLabel(false, state());
                }
            }
        }
    }

    /// <summary>
    /// 連携プラグインの表の列。
    ///
    /// 「状態」は入っていないときの説明文が長いので <c>Wrap</c> を立てる。
    /// 行の高さは <see cref="EUi.TableRow"/> が前フレームの実測から決める。
    /// </summary>
    private static readonly TableColumn[] PluginColumns =
    [
        new("プラグイン", 110f),
        new("必要度", 210f),
        new("導入", 50f),
        new("状態", SizeSpec.Weight(1f), Wrap: true),
    ];

    /// <summary>セルフチェックの表の列。見出しと行へ同じものを渡す。</summary>
    private static readonly TableColumn[] SelfCheckColumns =
    [
        new("状態", 60f),
        new("項目", 160f),
        new("詳細", SizeSpec.Weight(1f), Wrap: true),
    ];

    /// <summary>記録の表の列。見出しは出さず、行だけを並べる。</summary>
    private static readonly TableColumn[] AnomalyColumns =
    [
        new("時刻", 150f),
        new("内容", SizeSpec.Weight(1f), Wrap: true),
    ];

    /// <summary>
    /// 診断タブ。
    ///
    /// <b>EstellUtils へ移し終えたタブ。</b><c>RawTabScope</c> で囲まない。
    /// </summary>
    private void DrawDiagnosticsTab()
    {
        var report = this.plugin.SelfCheck.Latest;

        using (EUi.HStack())
        {
            if (EUi.Button("セルフチェックを実行"))
            {
                this.plugin.SelfCheck.RunAll();
                EzConfig.Save();
            }

            if (report is not null)
            {
                EUi.Muted($"（{report.At:HH:mm:ss} 実行）");
            }
        }

        if (report is null)
        {
            EUi.Label("まだ実行されていません。");
        }
        else
        {
            if (!report.CanExchange)
            {
                EUi.WrapColored("失敗項目があるため、交換は実行できません。", NoteKind.Danger);
            }

            EUi.TableHeader(SelfCheckColumns);

            for (var i = 0; i < report.Items.Count; i++)
            {
                var item = report.Items[i];

                var kind = item.Status switch
                {
                    SelfCheckStatus.Ok => NoteKind.Success,
                    SelfCheckStatus.Warning => NoteKind.Warning,
                    _ => NoteKind.Danger,
                };

                var label = item.Status switch
                {
                    SelfCheckStatus.Ok => "OK",
                    SelfCheckStatus.Warning => "警告",
                    _ => "失敗",
                };

                using (EUi.TableRow(SelfCheckColumns, i))
                {
                    EUi.TableCell(label, color: EUi.NoteColor(kind));
                    EUi.TableCell(item.Name);
                    EUi.Paragraph(item.Detail);
                }
            }
        }

        EUi.Spacing();
        EUi.Separator();
        EUi.Heading("記録");

        var entries = this.plugin.AnomalyLog.Snapshot();

        using (EUi.HStack())
        {
            if (EUi.Button("記録を消去"))
            {
                this.plugin.AnomalyLog.Clear();
            }

            // **不具合の報告に、こちらの状態遷移を添えられるようにする。**
            //
            // これまでは画面で読むことしかできなかった。報告を受けても
            // どの段で止まったのかが分からず、callback の記録から推測するしかなかった。
            // 詳細ログをファイルへ出す設定は既定で切ってあるため、なおさら届かない。
            if (EUi.Button("記録をコピー##anomalycopy", disabled: entries.Count == 0))
            {
                var text = string.Join(
                    Environment.NewLine,
                    entries.Select(x => $"{x.At:HH:mm:ss.fff} [{x.Severity}] {x.Category}: {x.Message}"));

                try
                {
                    var header = string.Join(
                        Environment.NewLine,
                        $"=== Auto Collector の記録（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）===",
                        $"版: {BuildStamp()}",
                        $"いまの手順: {this.plugin.ExchangeExecutor.Step} / {this.plugin.ExchangeExecutor.StatusDetail}",
                        $"監視の判断: {this.plugin.MonitorService.LastDecision}",
                        string.Empty);

                    EUi.SetClipboard(header + Environment.NewLine + text);

                    this.anomalyCopyNote = $"{entries.Count} 件をコピーしました";
                }
                catch (Exception ex)
                {
                    this.anomalyCopyNote = $"コピーできませんでした: {ex.Message}";
                }
            }

            if (!string.IsNullOrEmpty(this.anomalyCopyNote))
            {
                EUi.Muted(this.anomalyCopyNote);
            }
        }

        if (entries.Count == 0)
        {
            EUi.Label("記録はありません。");
            return;
        }

        using (EUi.Scroll("##anomalies", 200f))
        {
            // 新しいものから出す。Snapshot は IReadOnlyList を返すので、
            // LINQ の Reverse で毎フレーム作り直さず、添字で逆から読む。
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[entries.Count - 1 - i];

                var color = entry.Severity switch
                {
                    AnomalySeverity.Error => EUi.NoteColor(NoteKind.Danger),
                    AnomalySeverity.Warning => EUi.NoteColor(NoteKind.Warning),
                    _ => EUi.Colors.TextMuted,
                };

                using (EUi.TableRow(AnomalyColumns, i))
                {
                    EUi.TableCell($"{entry.At:HH:mm:ss} [{entry.Category}]", color: color);
                    EUi.Paragraph(entry.Message);
                }
            }
        }
    }

    /// <summary>
    /// 設定タブ。
    ///
    /// <b>EstellUtils へ移し終えたタブ。</b><c>RawTabScope</c> で囲まない。
    ///
    /// 補足説明は <see cref="EUi.MutedParagraph"/> で出している。
    /// 以前は <c>TextColored</c> で、折り返しは囲みの <c>PushTextWrapPos</c> に
    /// 頼っていた。囲みを外すので、折り返す版を明示する必要がある。
    /// 行頭の空白 2 つは、入れ子に見せるための従来どおりの字下げ。
    /// </summary>
    private void DrawSettingsTab()
    {
        var changed = false;

        var keepLooping = Plugin.C.KeepAutoDutyLooping;
        if (EUi.Checkbox("AutoDuty が周回を終えたら再開させる", ref keepLooping))
        {
            Plugin.C.KeepAutoDutyLooping = keepLooping;
            changed = true;
        }

        EUi.MutedParagraph("  周回数を 1 にしていると、交換が起きなかった周回で AutoDuty が止まったままになります");
        EUi.MutedParagraph("  周回数の設定は書き換えません。再開時に渡すのは 0 です");
        EUi.MutedParagraph("  交換を行った周回は、交換の完了後にこちらから再開させます");

        if (keepLooping)
        {
            // 入力欄はラベルを持たないので、ラベルは Field 側で出す。
            // 上下限は入力欄に渡してあるが、Clamp は残す。
            // 範囲の判断を画面側だけに任せると、設定ファイルを手で書き換えた値が通る。
            var restartDelay = Plugin.C.AutoDutyRestartDelaySeconds;
            if (EUi.InputInt("再開までの待ち（秒）##adrestartdelay", ref restartDelay, min: 0, max: 120, width: 160f))
            {
                Plugin.C.AutoDutyRestartDelaySeconds = Math.Clamp(restartDelay, 0, 120);
                changed = true;
            }

            if (Plugin.C.LastDutyTerritoryId != 0)
            {
                EUi.Muted($"  再開先: {NpcLocationService.GetTerritoryName(Plugin.C.LastDutyTerritoryId)}");
            }
            else
            {
                EUi.TextColored("  周回中のエリアをまだ記録していません", NoteKind.Warning);
            }
        }

        EUi.Spacing();

        var resumeOnFailure = Plugin.C.ResumeAutoDutyOnFailure;
        if (EUi.Checkbox("交換に失敗した場合も AutoDuty を再開する", ref resumeOnFailure))
        {
            Plugin.C.ResumeAutoDutyOnFailure = resumeOnFailure;
            changed = true;
        }

        EUi.MutedParagraph("  オフにすると、失敗時は停止したままになります");

        var debugMode = Plugin.C.DebugMode;
        if (EUi.Checkbox("デバッグモードを有効にする", ref debugMode))
        {
            Plugin.C.DebugMode = debugMode;
            changed = true;

            // 詳細ログはデバッグモードと連動させる。切ったのに書き続けないようにする。
            this.plugin.StartFileLog();
        }

        EUi.MutedParagraph("  交換候補・ショップ照合・デバッグの各タブと、詳細ログの設定が表示されます");

        EUi.Separator();
        EUi.Spacing();

        var requireExternal = Plugin.C.RequireExternalAutomationRunning;
        if (EUi.Checkbox("AutoDuty や Artisan が動作しているときだけ自動交換する", ref requireExternal))
        {
            Plugin.C.RequireExternalAutomationRunning = requireExternal;
            changed = true;
        }

        EUi.MutedParagraph("  オフにすると、プリセットを有効にしただけで交換を始めます（手動操作中でも動きます）");

        if (requireExternal)
        {
            // 以前は SameLine で説明文の右へ付けていたが、説明文が折り返すと
            // 行末がどこになるか決まらない。独立した 1 行にする。
            var snapshot = this.plugin.MonitorService.Snapshot;
            if (snapshot.AutomationRunning)
            {
                EUi.TextColored($"  いま: {snapshot.AutomationDetail}", NoteKind.Success);
            }
            else
            {
                EUi.Muted("  いま: なし");
            }
        }

        EUi.Spacing();

        var suppress = Plugin.C.SuppressAutoRetainer;
        if (EUi.Checkbox("交換中は AutoRetainer の新規処理を抑制する", ref suppress))
        {
            Plugin.C.SuppressAutoRetainer = suppress;
            changed = true;
        }

        EUi.MutedParagraph("  実行中のリテイナー処理は中断しません。交換が終わると自動的に解除します");

        var stopArtisan = Plugin.C.StopArtisan;
        if (EUi.Checkbox("交換中は Artisan の製作を止める", ref stopArtisan))
        {
            Plugin.C.StopArtisan = stopArtisan;
            changed = true;
        }

        EUi.MutedParagraph("  製作の合間に交換へ入ると操作を取り合うため、その間だけ止めます。交換が終わると元のモードへ戻します");

        EUi.Spacing();

        var minAd = Plugin.C.MinimumAutoDutyVersion;
        if (EUi.TextInput("必要な AutoDuty の版##minadversion", ref minAd, maxLength: 32, width: 160f))
        {
            Plugin.C.MinimumAutoDutyVersion = minAd;
            changed = true;
        }

        EUi.MutedParagraph("  これを下回るあいだは戦闘の自動周回を使えません。空にすると制限しません");

        // 更新をうながすのは状況タブの「はじめに」。設定はここで数を決めるだけ。
        // 両方に出すと、どちらで直すのか分からなくなる。
        if (this.plugin.AutoDuty.IsLoaded &&
            this.plugin.AutoDutySetup.InstalledVersion is { } adVersion)
        {
            EUi.Muted($"  いま入っているのは {adVersion} です");
        }

        EUi.Spacing();

        var range = Plugin.C.NpcApproachRange;
        if (EUi.SliderFloat("NPC への接近距離", ref range, 1.0f, 6.0f, decimals: 1))
        {
            Plugin.C.NpcApproachRange = range;
            changed = true;
        }

        if (changed)
        {
            EzConfig.Save();
        }
    }
}
