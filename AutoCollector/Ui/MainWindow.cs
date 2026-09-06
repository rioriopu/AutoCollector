using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;
using ECommons.DalamudServices;

namespace AutoCollector.Ui;

/// <summary>
/// メイン画面。MVP 段階では「読み取り専用の状況表示」と「セルフチェック結果」が中心。
/// 交換の実行系は S5 以降で追加する。
/// </summary>
public sealed partial class MainWindow(Plugin plugin)
{
    private readonly Plugin plugin = plugin;

    private bool onlyWithLocation = true;
    private string rewardFilter = string.Empty;
    private int exchangeChoice;

    /// <summary>画面の読み取り結果を保持する。毎フレーム読み直すと描画だけで重くなる。</summary>
    private DateTime nextShopReadUtc = DateTime.MinValue;
    private IReadOnlyList<ShopEntry> cachedEntries = [];
    private ShopHeader cachedHeader = new(0, 0, 0, string.Empty, 0);
    private string cachedShopFailure = string.Empty;
    private List<(string Label, AtkValueProbe Probe)> cachedDiagnostics = [];
    private readonly PresetTab presetTab = new(plugin);

    public void Draw()
    {
        using var tabs = ImRaii.TabBar("##autocollector_tabs");
        if (!tabs)
        {
            return;
        }

        this.DrawStatusTab();
        this.presetTab.Draw(ref this.jumpToPresetTab);

        // 開発・調査用のタブはデバッグモードのときだけ出す。
        if (Plugin.C.DebugMode)
        {
            this.DrawExchangeTab();
            this.DrawShopTab();
        }

        this.DrawDiagnosticsTab();
        this.DrawSettingsTab();
        this.DrawDebugTab();
        this.DrawDonationTab();
    }

    /// <summary>
    /// 手動で開いた交換ショップの中身を読み取り、ゲームデータと照合する。
    ///
    /// このタブは読み取りと照合だけを行い、交換は一切実行しない。
    /// AtkValue の配置が現在のクライアントで正しいかを、交換を撃つ前に確認するための場所。
    /// </summary>
    private void DrawShopTab()
    {
        using var tab = ImRaii.TabItem("ショップ照合");
        if (!tab)
        {
            return;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "交換ショップを手動で開いた状態で確認してください。このタブは交換を実行しません。");
        ImGui.Spacing();

        var shop = this.plugin.ShopService;

        var layout = shop.Layout;
        ImGui.TextUnformatted($"配置: NumEntries={layout.NumEntries} / CurrencyAmount={layout.CurrencyAmount} / Cost={layout.EntryCost} / ItemId={layout.EntryItemId} / Index={layout.EntryIndex}");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"この数値は次の場所の JSON で変更できます:\n{DataFileLoader.GetDataDirectory()}\\atkvalue_layout.json");
        }

        ImGui.Separator();

        // InclusionShop（スクリップ交換など）は別アドオン。開いていればそちらを表示する。
        if (this.plugin.InclusionShopService.IsOpen())
        {
            this.DrawInclusionShop();
            ImGui.Spacing();
            ImGui.Separator();
            this.DrawCallbackRecorder();
            return;
        }

        if (!shop.IsShopOpen())
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "交換ショップが開いていません。");
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
            using var node = ImRaii.TreeNode("AtkValue 配置の診断");
            if (node)
            {
                using var table = ImRaii.Table("##layoutdiag", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
                if (table)
                {
                    ImGui.TableSetupColumn("項目", ImGuiTableColumnFlags.WidthFixed, 190f);
                    ImGui.TableSetupColumn("型", ImGuiTableColumnFlags.WidthFixed, 90f);
                    ImGui.TableSetupColumn("値", ImGuiTableColumnFlags.WidthFixed, 100f);
                    ImGui.TableSetupColumn("判定");
                    ImGui.TableHeadersRow();

                    foreach (var (label, probe) in diagnostics)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(label);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(probe.TypeName);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(probe.Usable ? probe.Value.ToString("N0") : "-");

                        ImGui.TableNextColumn();
                        if (!probe.InRange)
                        {
                            ImGui.TextColored(ImGuiColors.DalamudRed, "範囲外");
                        }
                        else if (probe.Usable)
                        {
                            ImGui.TextColored(ImGuiColors.HealerGreen, "読める");
                        }
                        else
                        {
                            ImGui.TextColored(ImGuiColors.DalamudYellow, "整数として読めない");
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(this.cachedShopFailure))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, this.cachedShopFailure);
            return;
        }

        var entries = this.cachedEntries;
        var header = this.cachedHeader;

        ImGui.TextUnformatted($"申告エントリ数: {header.DeclaredEntryCount} / 実際に読めた件数: {entries.Count}");
        if (header.UnreadableEntries > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"読み取れなかったエントリ: {header.UnreadableEntries} 件");
        }

        if (header.CurrencyIcon != 0)
        {
            ImGui.TextUnformatted($"画面上の所持通貨: {header.CurrencyAmount:N0}（アイコン ID {header.CurrencyIcon}）");
        }
        else
        {
            ImGui.TextUnformatted($"画面上の所持通貨: {header.CurrencyAmount:N0}");
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"  アイコン ID は読めませんでした（型 {header.CurrencyIconType}）。補助情報のため交換には影響しません");
        }

        // 監視中の通貨と画面の所持数が一致するかを見ると、配置が正しいかの強い裏付けになる。
        var resolver = this.plugin.ExchangeResolver;
        if (resolver.TargetCurrencyItemId != 0 &&
            this.plugin.CurrencyService.TryGetCount(resolver.TargetCurrencyItemId, out var actual))
        {
            if (actual == (int)header.CurrencyAmount)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, $"所持数の一致を確認しました（インベントリ {actual:N0} = 画面 {header.CurrencyAmount:N0}）。配置は正しいと判断できます。");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"インベントリ {actual:N0} と画面 {header.CurrencyAmount:N0} が違います。別通貨のショップか、CurrencyAmount の位置がずれています。");
            }
        }

        ImGui.Spacing();

        // どのショップが開いているかを先に特定する。
        // 同じアイテムが複数のショップに別の値段で載っているため、
        // ItemId だけで定義を引くと別のショップの値段と比較してしまう。
        var identification = shop.IdentifyShop(entries, resolver.LiveResults);

        if (identification.IsConfident)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, $"開いているショップを特定しました: Shop {identification.ShopId}（画面の {identification.TotalEntries} 件すべてが一致）");
        }
        else if (identification.ShopId is not null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, identification.Detail);
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, identification.Detail);
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

        using (var table = ImRaii.Table("##shopentries", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY, new System.Numerics.Vector2(0, 400)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("枠", ImGuiTableColumnFlags.WidthFixed, 40f);
                ImGui.TableSetupColumn("ItemId", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("アイテム");
                ImGui.TableSetupColumn("画面コスト", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("index", ImGuiTableColumnFlags.WidthFixed, 55f);
                ImGui.TableSetupColumn("ゲームデータとの照合", ImGuiTableColumnFlags.WidthFixed, 200f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var entry in entries)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.Slot.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.ItemId.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.ItemName);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.CostAmount.ToString("N0"));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.Index.ToString());

                    ImGui.TableNextColumn();
                    if (!definitionsByItem.TryGetValue(entry.ItemId, out var def))
                    {
                        unknown++;
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "このショップの定義になし");
                    }
                    else if (def.CurrencyCost == entry.CostAmount)
                    {
                        matched++;
                        ImGui.TextColored(ImGuiColors.HealerGreen, $"一致 ({def.CurrencyCost:N0})");
                    }
                    else
                    {
                        mismatched++;
                        ImGui.TextColored(ImGuiColors.DalamudRed, $"不一致 データ {def.CurrencyCost:N0}");
                    }
                }
            }
        }

        ImGui.Spacing();
        if (resolver.Stage != ResolverBuildStage.Completed)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "「交換候補」タブで通貨を選ぶと、ゲームデータとの照合結果が出ます。");
            return;
        }

        ImGui.TextUnformatted($"照合: 一致 {matched} / 不一致 {mismatched} / 対象外 {unknown}");

        if (mismatched > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "不一致があります。この状態では交換を実行してはいけません。");
        }
        else if (matched > 0)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "コストがすべて一致しました。ID による照合が機能しています。");
        }

        ImGui.Spacing();
        ImGui.Separator();
        this.DrawExchangeExecution(identification, definitionsByItem, entries);

        ImGui.Spacing();
        ImGui.Separator();
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

        ImGui.TextColored(ImGuiColors.HealerGreen, "InclusionShop（アイテム交換）が開いています。");

        if (service.TryGetSelection(out var selection) && selection is not null)
        {
            ImGui.TextUnformatted(
                $"InclusionShop {selection.InclusionShopId} / 系統 {selection.SelectedCategoryIndex + 1}・{selection.CategoryCount} " +
                $"(行 {selection.SelectedCategoryRowId} / シリーズ {selection.SelectedSeriesId})");
            ImGui.TextUnformatted($"種別 タブ {selection.SelectedSubCategoryTab} / 表示 {selection.VisibleSubCategoryCount}");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "選択状態を読めませんでした。");
        }

        if (!service.TryGetAddon(out var addon))
        {
            return;
        }

        if (!service.TryReadEntries(addon, out var entries, out var currency, out var failure))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, failure);
            return;
        }

        ImGui.TextUnformatted($"画面上の通貨: {currency:N0} / エントリ {entries.Count} 件");

        if (entries.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "種別が選ばれていないため、品目が表示されていません。");
            return;
        }

        using var table = ImRaii.Table("##inclusionentries", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY, new System.Numerics.Vector2(0, 320));
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("枠", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("ItemId", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("アイテム");
        ImGui.TableSetupColumn("コスト", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("通貨値", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("index", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Slot.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.ItemId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.ItemName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.CostAmount.ToString("N0"));

            ImGui.TableNextColumn();
            // 8 未満なら特殊通貨のインデックス。ItemId ではない点が分かるように出す。
            ImGui.TextUnformatted(entry.CostItemId < 8 ? $"idx {entry.CostItemId}" : entry.CostItemId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Index.ToString());
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

        ImGui.TextUnformatted("交換の実行（1 個のみ）");

        // 結果未確認の記録が残っている間は、新しい交換を一切受け付けない
        if (executor.InFlight is { } pending)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "前回の交換の結果が未確認です。");
            ImGui.TextUnformatted($"  {pending.RewardName} × {pending.RewardQuantity} / コスト {pending.CurrencyCost} / index {pending.CallbackIndex}");
            ImGui.TextUnformatted($"  発火時: 通貨 {pending.CurrencyBefore:N0} / 報酬 {pending.RewardBefore:N0}");
            if (!string.IsNullOrEmpty(pending.Outcome))
            {
                ImGui.TextWrapped($"  結果: {pending.Outcome}");
            }

            ImGui.TextColored(ImGuiColors.DalamudGrey, "  ゲーム内で実際の所持数を確認してからクリアしてください。");
            if (ImGui.Button("確認したのでクリアする##clearinflight"))
            {
                executor.ClearInFlight();
            }

            return;
        }

        if (!string.IsNullOrEmpty(executor.StatusDetail))
        {
            var color = executor.Step switch
            {
                ExchangeStep.Done => ImGuiColors.HealerGreen,
                ExchangeStep.Error => ImGuiColors.DalamudRed,
                _ => ImGuiColors.DalamudYellow,
            };
            ImGui.TextColored(color, $"{StatusText.StepLabel(executor.Step)}: {executor.StatusDetail}");
        }

        if (!identification.IsConfident)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "ショップを特定できていないため、交換は実行できません。");
            return;
        }

        // 交換できる候補（照合が一致したものだけ）を出す
        var selectable = entries
            .Where(e => definitionsByItem.TryGetValue(e.ItemId, out var d) && d.CurrencyCost == e.CostAmount)
            .OrderBy(e => e.CostAmount)
            .ToList();

        if (selectable.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "照合が一致したエントリがありません。");
            return;
        }

        var names = selectable.Select(e => $"{e.ItemName}（{e.CostAmount:N0}）").ToArray();
        this.exchangeChoice = Math.Clamp(this.exchangeChoice, 0, names.Length - 1);

        ImGui.SetNextItemWidth(320f);
        ImGui.Combo("交換対象##exchangetarget", ref this.exchangeChoice, names, names.Length);

        var chosen = selectable[this.exchangeChoice];
        var definition = definitionsByItem[chosen.ItemId];

        ImGui.TextColored(ImGuiColors.DalamudGrey,
            $"  {chosen.ItemName} を 1 回交換します（コスト {chosen.CostAmount:N0} / 取得 {definition.RewardQuantity} 個 / index {chosen.Index}）");

        if (!executor.CanRequest)
        {
            ImGui.BeginDisabled();
            ImGui.Button("交換する##doexchange");
            ImGui.EndDisabled();
            return;
        }

        if (ImGui.Button("交換する##doexchange"))
        {
            if (!executor.Request(definition, out var reason))
            {
                this.plugin.AnomalyLog.Warn("Exchange", reason);
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "通貨を消費します");
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

        ImGui.TextUnformatted("callback の記録（S5 実装前の実測用）");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "記録を開始してから、手動で 1 個だけ交換してください。押した操作の引数がそのまま出ます。");

        // AddonFilter を空にすると全アドオンを記録する。
        // ショップ以外（確認ダイアログ等）が飛んでいるかを調べるには空にする必要がある。
        var filter = recorder.AddonFilter;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputTextWithHint("記録対象アドオン##callbackfilter", "空にすると全アドオンを記録", ref filter, 64))
        {
            recorder.AddonFilter = filter;
        }

        var recording = recorder.IsRecording;
        if (ImGui.Checkbox("記録する##callbackrec", ref recording))
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

        ImGui.SameLine();
        if (ImGui.Button("記録を消去##callbackclear"))
        {
            recorder.Clear();
        }

        var records = recorder.Snapshot();
        if (records.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "まだ記録はありません。");
            return;
        }

        using var child = ImRaii.Child("##callbackrecords", new System.Numerics.Vector2(0, 160), true);
        if (!child)
        {
            return;
        }

        foreach (var record in records.Reverse())
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"{record.At:HH:mm:ss}");
            ImGui.SameLine();
            ImGui.TextUnformatted($"{record.AddonName}  updateState={record.UpdateState}  {record.Signature}");
        }
    }

    /// <summary>
    /// 通貨を選ぶと、その通貨で買えるものをゲームデータから解決して一覧表示する。
    /// この段階ではゲーム状態を一切変更しない（読み取り専用）。
    /// </summary>
    private void DrawExchangeTab()
    {
        using var tab = ImRaii.TabItem("交換候補");
        if (!tab)
        {
            return;
        }

        var slots = this.plugin.TomestoneService.ListSlots();
        if (slots.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "トームストーンを解決できませんでした。");
            return;
        }

        ImGui.TextUnformatted("通貨を選ぶと、ゲームデータから交換候補を解決します。");
        ImGui.Spacing();

        foreach (var slot in slots)
        {
            if (string.IsNullOrEmpty(slot.Name))
            {
                continue;
            }

            if (ImGui.Button($"{slot.Name}##build{slot.TomestonesRowId}"))
            {
                this.plugin.ExchangeResolver.BeginBuild(slot.ItemId);
            }

            ImGui.SameLine();
        }

        ImGui.NewLine();
        ImGui.Separator();

        var resolver = this.plugin.ExchangeResolver;

        switch (resolver.Stage)
        {
            case ResolverBuildStage.NotStarted:
                ImGui.TextUnformatted("通貨を選択してください。");
                return;

            case ResolverBuildStage.Failed:
                ImGui.TextColored(ImGuiColors.DalamudRed, "索引の構築に失敗しました。診断タブを確認してください。");
                return;

            case ResolverBuildStage.Completed:
                break;

            default:
                ImGui.ProgressBar(resolver.BuildProgress, new System.Numerics.Vector2(-1, 0), $"{resolver.Stage} {resolver.BuildProgress * 100:F0}%");
                return;
        }

        if (ImGui.Checkbox("座標を解決できたものだけ表示", ref this.onlyWithLocation))
        {
            // 表示切り替えのみ。再構築は不要。
        }

        var groups = resolver.GroupByReward(this.onlyWithLocation);
        ImGui.TextUnformatted($"報酬アイテム {groups.Count} 種 / 定義 {resolver.Results.Count} 件");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "アイテム名で絞り込み", ref this.rewardFilter, 100);

        var filtered = string.IsNullOrWhiteSpace(this.rewardFilter)
            ? groups
            : groups.Where(g => g.RewardName.Contains(this.rewardFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        using var child = ImRaii.Child("##candidates", new System.Numerics.Vector2(0, 0), true);
        if (!child)
        {
            return;
        }

        foreach (var group in filtered.Take(300))
        {
            using var node = ImRaii.TreeNode($"{group.RewardName}##{group.RewardItemId}");
            if (!node)
            {
                continue;
            }

            using var table = ImRaii.Table($"##defs{group.RewardItemId}", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (!table)
            {
                continue;
            }

            ImGui.TableSetupColumn("コスト", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("個数", ImGuiTableColumnFlags.WidthFixed, 45f);
            ImGui.TableSetupColumn("NPC");
            ImGui.TableSetupColumn("エリア");
            ImGui.TableSetupColumn("座標", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("経路", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("実行", ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableHeadersRow();

            foreach (var def in group.Definitions)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(def.CurrencyCost.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(def.RewardQuantity.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(def.NpcName) ? $"<{def.NpcDataId}>" : def.NpcName);

                ImGui.TableNextColumn();
                if (def.TerritoryId == 0)
                {
                    ImGui.TextColored(ImGuiColors.DalamudRed, "未解決");
                }
                else
                {
                    ImGui.TextUnformatted(NpcLocationService.GetTerritoryName(def.TerritoryId));
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(def.HasLocation
                    ? $"{def.NpcPosition.X:F1}, {def.NpcPosition.Y:F1}, {def.NpcPosition.Z:F1}"
                    : "-");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(def.Path.ToString());
                if (!string.IsNullOrEmpty(def.MenuHint) && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"選択肢ヒント: {def.MenuHint}\nShopId: {def.ShopId}");
                }

                ImGui.TableNextColumn();
                this.DrawTravelButton(def);
            }
        }

        if (filtered.Count > 300)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"{filtered.Count - 300} 件は表示していません。絞り込んでください。");
        }
    }

    /// <summary>
    /// NPC のところまで移動して交換する。同じエリアにいる必要がある（テレポート未実装）。
    /// </summary>
    private void DrawTravelButton(ExchangeDefinition definition)
    {
        var executor = this.plugin.ExchangeExecutor;

        if (!definition.HasLocation)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "座標未解決");
            return;
        }

        var sameArea = Svc.ClientState.TerritoryType == definition.TerritoryId;
        if (!sameArea)
        {
            // 別エリアならテレポートが必要になる。
            // Lifestream が無いかエーテライト未アクセスなら、押せても失敗するので理由を出す。
            if (!this.plugin.Lifestream.IsLoaded)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "Lifestream 未導入");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("別エリアへの移動には Lifestream が必要です。");
                }

                return;
            }

            if (!this.plugin.AetheryteService.CanReach(definition.TerritoryId))
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "未アクセス");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} のエーテライトにアクセスしていないため、テレポートできません。");
                }

                return;
            }
        }

        if (!executor.CanRequest)
        {
            ImGui.BeginDisabled();
            ImGui.Button($"行って交換##travel{definition.ShopId}_{definition.RewardItemId}_{definition.NpcDataId}");
            ImGui.EndDisabled();
            return;
        }

        if (ImGui.Button($"行って交換##travel{definition.ShopId}_{definition.RewardItemId}_{definition.NpcDataId}"))
        {
            if (!executor.RequestWithTravel(definition, out var reason))
            {
                this.plugin.AnomalyLog.Warn("Exchange", reason);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(sameArea
                ? $"{definition.NpcName} まで移動して 1 個交換します。通貨を消費します。"
                : $"{NpcLocationService.GetTerritoryName(definition.TerritoryId)} へテレポートし、{definition.NpcName} まで移動して 1 個交換します。通貨を消費します。");
        }
    }

    /// <summary>
    /// 連携プラグインの一覧。
    ///
    /// 問題が無いときは畳んでおく。5 行を常に並べると、
    /// 使っていないものまで壊れているように見える。
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

        using var node = ImRaii.TreeNode(
            missing == 0 ? "連携プラグイン: 問題ありません##plugins" : $"連携プラグイン: {missing} 件に注意##plugins",
            missing == 0 ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen);

        if (!node)
        {
            return;
        }

        var keeper = this.plugin.AutoDutyKeeper;
        if (keeper.RestartCount > 0 && !keeper.GaveUp)
        {
            var status = string.IsNullOrEmpty(keeper.Status) ? string.Empty : $" / {keeper.Status}";
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"周回の維持: 再開 {keeper.RestartCount} 回{status}");
        }

        using var table = ImRaii.Table("##automation", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("プラグイン", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("必要度", ImGuiTableColumnFlags.WidthFixed, 210f);
        ImGui.TableSetupColumn("導入", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupColumn("状態");
        ImGui.TableHeadersRow();

        DrawRow("AutoDuty", "周回に相乗りするなら必須", this.plugin.AutoDuty.IsLoaded, "周回に相乗りできません。設定タブで「AutoDuty や Artisan が動作しているときだけ交換する」を切ると、プリセットだけで動きます", () =>
        {
            // 交換後に再開できなかった場合の受け皿。棒立ちのまま気付かないのを避ける。
            // 一時停止は本プラグインからは使わないが、ユーザーが手動で止めている場合に備えて表示する。
            var paused = this.plugin.AutoDuty.TryIsPaused(out var reported) && reported;

            if (paused ||
                (this.plugin.ExchangeExecutor.LastResumeTerritoryId != 0 &&
                 this.plugin.AutoDuty.TryIsStopped(out var idle) && idle))
            {
                if (ImGui.SmallButton($"再開##resumead"))
                {
                    if (!this.plugin.ExchangeExecutor.TryResumeAutoDutyManually(out var resumeReason))
                    {
                        this.plugin.AnomalyLog.Warn("AutoDuty", resumeReason);
                    }
                }

                ImGui.SameLine();
            }

            if (!this.plugin.AutoDuty.TryIsStopped(out var stopped))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "状態を取得できません");
                return;
            }

            if (stopped)
            {
                ImGui.TextUnformatted("停止中");
                return;
            }

            if (paused)
            {
                ImGui.TextColored(ImGuiColors.DalamudOrange, "一時停止中");
                return;
            }

            this.plugin.AutoDuty.TryIsLooping(out var looping);
            this.plugin.AutoDuty.TryIsNavigating(out var navigating);
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"動作中（周回={looping} / 移動={navigating}）");
        });

        DrawRow("AutoRetainer", "任意", this.plugin.AutoRetainer.IsLoaded, "使いません（問題ありません）", () =>
        {
            var busy = this.plugin.AutoRetainer.IsBusyFailClosed();
            this.plugin.AutoRetainer.TryGetSuppressed(out var suppressed);

            if (busy)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "処理中");
            }
            else
            {
                ImGui.TextUnformatted("待機中");
            }

            if (suppressed)
            {
                ImGui.SameLine();
                ImGui.TextColored(
                    this.plugin.AutoRetainer.SuppressedByUs ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey,
                    this.plugin.AutoRetainer.SuppressedByUs ? "（本プラグインが抑制中）" : "（他が抑制中）");
            }
        });

        DrawRow("Artisan", "任意", this.plugin.Artisan.IsLoaded, "使いません（問題ありません）", () =>
        {
            var endurance = this.plugin.Artisan.TryGetEnduranceStatus(out var e) && e;
            var list = this.plugin.Artisan.TryIsListRunning(out var l) && l;

            if (endurance || list)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, endurance ? "耐久モード実行中" : "製作リスト実行中");
            }
            else
            {
                ImGui.TextUnformatted("待機中");
            }

            if (this.plugin.Artisan.StoppedByUs)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.HealerGreen, "（本プラグインが停止中）");
            }
            else if (this.plugin.Artisan.TryGetStopRequest(out var stopped) && stopped)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, "（他が停止中）");
            }
        });

        DrawRow("vnavmesh", "必須", this.plugin.Vnavmesh.IsLoaded, "交換所まで自動で移動できません", () =>
        {
            if (!this.plugin.Vnavmesh.TryIsReady(out var ready))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "状態を取得できません");
                return;
            }

            ImGui.TextUnformatted(ready ? "このエリアで利用可能" : "このエリアのメッシュが未準備");
        });

        DrawRow("Lifestream", "別エリアの交換所を使うなら必須", this.plugin.Lifestream.IsLoaded, "別エリアの交換所へテレポートできません。同じエリアの交換所だけが使えます", () =>
        {
            if (!this.plugin.Lifestream.TryIsBusy(out var busy))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "状態を取得できません");
                return;
            }

            ImGui.TextUnformatted(busy ? "処理中" : "待機中");
        });

        static void DrawRow(string name, string necessity, bool loaded, string missingText, Action drawState)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);

            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudGrey, necessity);

            ImGui.TableNextColumn();
            if (loaded)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, "あり");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "なし");
            }

            ImGui.TableNextColumn();
            if (loaded)
            {
                drawState();
            }
            else
            {
                ImGui.TextWrapped(missingText);
            }
        }
    }

    private void DrawDiagnosticsTab()
    {
        using var tab = ImRaii.TabItem("診断");
        if (!tab)
        {
            return;
        }

        if (ImGui.Button("セルフチェックを実行"))
        {
            this.plugin.SelfCheck.RunAll();
            EzConfig.Save();
        }

        var report = this.plugin.SelfCheck.Latest;
        if (report is null)
        {
            ImGui.TextUnformatted("まだ実行されていません。");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"（{report.At:HH:mm:ss} 実行）");

            if (!report.CanExchange)
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "失敗項目があるため、交換は実行できません。");
            }

            using var table = ImRaii.Table("##selfcheck", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (table)
            {
                ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("項目", ImGuiTableColumnFlags.WidthFixed, 160f);
                ImGui.TableSetupColumn("詳細");
                ImGui.TableHeadersRow();

                foreach (var item in report.Items)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var (color, label) = item.Status switch
                    {
                        SelfCheckStatus.Ok => (ImGuiColors.HealerGreen, "OK"),
                        SelfCheckStatus.Warning => (ImGuiColors.DalamudYellow, "警告"),
                        _ => (ImGuiColors.DalamudRed, "失敗"),
                    };
                    ImGui.TextColored(color, label);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(item.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(item.Detail);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("記録");

        if (ImGui.Button("記録を消去"))
        {
            this.plugin.AnomalyLog.Clear();
        }

        var entries = this.plugin.AnomalyLog.Snapshot();
        if (entries.Count == 0)
        {
            ImGui.TextUnformatted("記録はありません。");
            return;
        }

        using var child = ImRaii.Child("##anomalies", new System.Numerics.Vector2(0, 200), true);
        if (!child)
        {
            return;
        }

        foreach (var entry in entries.Reverse())
        {
            var color = entry.Severity switch
            {
                AnomalySeverity.Error => ImGuiColors.DalamudRed,
                AnomalySeverity.Warning => ImGuiColors.DalamudYellow,
                _ => ImGuiColors.DalamudGrey,
            };
            ImGui.TextColored(color, $"{entry.At:HH:mm:ss} [{entry.Category}]");
            ImGui.SameLine();
            ImGui.TextWrapped(entry.Message);
        }
    }

    private void DrawSettingsTab()
    {
        using var tab = ImRaii.TabItem("設定");
        if (!tab)
        {
            return;
        }

        var changed = false;

        var keepLooping = Plugin.C.KeepAutoDutyLooping;
        if (ImGui.Checkbox("AutoDuty が周回を終えたら再開させる", ref keepLooping))
        {
            Plugin.C.KeepAutoDutyLooping = keepLooping;
            changed = true;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  周回数を 1 にしていると、交換が起きなかった周回で AutoDuty が止まったままになります");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  周回数の設定は書き換えません。再開時に渡すのは 0 です");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  交換を行った周回は、交換の完了後にこちらから再開させます");

        if (keepLooping)
        {
            var restartDelay = Plugin.C.AutoDutyRestartDelaySeconds;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.InputInt("  再開までの待ち（秒）", ref restartDelay))
            {
                Plugin.C.AutoDutyRestartDelaySeconds = Math.Clamp(restartDelay, 0, 120);
                changed = true;
            }

            if (Plugin.C.LastDutyTerritoryId != 0)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudGrey,
                    $"  再開先: {NpcLocationService.GetTerritoryName(Plugin.C.LastDutyTerritoryId)}");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "  周回中のエリアをまだ記録していません");
            }
        }

        ImGui.Spacing();

        var resumeOnFailure = Plugin.C.ResumeAutoDutyOnFailure;
        if (ImGui.Checkbox("交換に失敗した場合も AutoDuty を再開する", ref resumeOnFailure))
        {
            Plugin.C.ResumeAutoDutyOnFailure = resumeOnFailure;
            changed = true;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "  オフにすると、失敗時は停止したままになります");

        var debugMode = Plugin.C.DebugMode;
        if (ImGui.Checkbox("デバッグモードを有効にする", ref debugMode))
        {
            Plugin.C.DebugMode = debugMode;
            changed = true;

            // 詳細ログはデバッグモードと連動させる。切ったのに書き続けないようにする。
            this.plugin.StartFileLog();
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  交換候補・ショップ照合・デバッグの各タブと、詳細ログの設定が表示されます");

        ImGui.Separator();
        ImGui.Spacing();

        var requireExternal = Plugin.C.RequireExternalAutomationRunning;
        if (ImGui.Checkbox("AutoDuty や Artisan が動作しているときだけ自動交換する", ref requireExternal))
        {
            Plugin.C.RequireExternalAutomationRunning = requireExternal;
            changed = true;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  オフにすると、プリセットを有効にしただけで交換を始めます（手動操作中でも動きます）");

        if (requireExternal)
        {
            ImGui.SameLine();
            var snapshot = this.plugin.MonitorService.Snapshot;
            if (snapshot.AutomationRunning)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, $"いま: {snapshot.AutomationDetail}");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "いま: なし");
            }
        }

        ImGui.Spacing();

        var suppress = Plugin.C.SuppressAutoRetainer;
        if (ImGui.Checkbox("交換中は AutoRetainer の新規処理を抑制する", ref suppress))
        {
            Plugin.C.SuppressAutoRetainer = suppress;
            changed = true;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "  実行中のリテイナー処理は中断しません。交換が終わると自動的に解除します");

        var stopArtisan = Plugin.C.StopArtisan;
        if (ImGui.Checkbox("交換中は Artisan の製作を止める", ref stopArtisan))
        {
            Plugin.C.StopArtisan = stopArtisan;
            changed = true;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  製作の合間に交換へ入ると操作を取り合うため、その間だけ止めます。交換が終わると元のモードへ戻します");

        ImGui.Spacing();

        var keepFree = Plugin.C.KeepFreeInventorySlots;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputInt("交換後に残す所持枠", ref keepFree))
        {
            Plugin.C.KeepFreeInventorySlots = Math.Clamp(keepFree, 0, 50);
            changed = true;
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "  AutoRetainer は所持枠が空いていないキャラクタを処理対象から外して保存します");

        ImGui.Spacing();

        var range = Plugin.C.NpcApproachRange;
        if (ImGui.SliderFloat("NPC への接近距離", ref range, 1.0f, 6.0f, "%.1f"))
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
