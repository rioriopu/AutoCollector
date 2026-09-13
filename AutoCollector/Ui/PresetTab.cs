using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Automation;
using AutoCollector.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;

namespace AutoCollector.Ui;

/// <summary>
/// 交換プリセットの設定。
///
/// 通貨はスロット番号で保持するため、パッチでトームストーンが入れ替わっても
/// 設定を作り直す必要がない。
/// </summary>
public sealed class PresetTab(Plugin plugin)
{
    private static readonly string[] ThresholdModeNames = ["固定値", "上限に対する割合", "上限までの残り"];

    private static readonly string[] ExchangeModeNames =
    [
        "交換できる限り",
        "指定量の通貨を残すまで",
        "指定回数だけ",
        "目標所持数に達するまで",
    ];

    private readonly Plugin plugin = plugin;

    private string rewardSearch = string.Empty;
    private Guid editingPresetId = Guid.Empty;

    /// <summary>選んでいる系統。ゲーム内の交換画面と同じ並び。</summary>
    private int categoryIndex;

    /// <summary>選んでいる種別。</summary>
    private int seriesIndex;

    /// <summary>名前で探した結果。入力が変わるまで使い回す。</summary>
    private IReadOnlyList<(InclusionCategory Category, InclusionSeries Series, InclusionOffer Offer)>? searchResults;

    public void Draw(ref bool select)
    {
        var flags = select ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        select = false;

        using var tab = ImRaii.TabItem("プリセット", flags);
        if (!tab)
        {
            return;
        }

        // 監視は常に動いている。有効なプリセットが閾値へ達したら自動で交換所へ向かう。
        ImGui.TextColored(ImGuiColors.DalamudGrey, "有効なプリセットが閾値に達したら、自動で交換所へ向かいます");

        if (!string.IsNullOrEmpty(this.plugin.MonitorService.LastDecision))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, this.plugin.MonitorService.LastDecision);
        }

        ImGui.Spacing();

        if (ImGui.Button("プリセットを追加"))
        {
            var preset = new ExchangePreset();
            Plugin.C.Presets.Add(preset);
            this.editingPresetId = preset.Id;
            EzConfig.Save();
        }

        ImGui.Separator();

        if (Plugin.C.Presets.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "プリセットがありません。");
            return;
        }

        ExchangePreset? toRemove = null;

        foreach (var preset in Plugin.C.Presets.ToList())
        {
            using var id = ImRaii.PushId(preset.Id.ToString());

            var enabled = preset.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                preset.Enabled = enabled;
                if (enabled)
                {
                    preset.DisabledReason = null;
                }

                EzConfig.Save();
            }

            ImGui.SameLine();

            var label = string.IsNullOrEmpty(preset.Name) ? "（名前なし）" : preset.Name;
            var expanded = this.editingPresetId == preset.Id;
            if (ImGui.Selectable(this.BuildSummary(preset, label), expanded))
            {
                this.editingPresetId = expanded ? Guid.Empty : preset.Id;
            }

            if (!string.IsNullOrEmpty(preset.DisabledReason))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  無効化されています: {preset.DisabledReason}");
            }

            if (this.editingPresetId != preset.Id)
            {
                continue;
            }

            using var indent = ImRaii.PushIndent();
            this.DrawEditor(preset, ref toRemove);
            ImGui.Separator();
        }

        if (toRemove is not null)
        {
            Plugin.C.Presets.Remove(toRemove);
            EzConfig.Save();
        }
    }

    private string BuildSummary(ExchangePreset preset, string label)
    {
        var currency = this.plugin.CurrencyCatalog.TryResolve(preset, out var currencyItemId)
            ? this.plugin.CurrencyCatalog.ListChoices().FirstOrDefault(x => x.ItemId == currencyItemId)?.Name ?? "?"
            : "?";

        var reward = preset.RewardItemId == 0
            ? "未設定"
            : ItemName(preset.RewardItemId);

        return $"{label}  [{currency} → {reward}]";
    }

    private void DrawEditor(ExchangePreset preset, ref ExchangePreset? toRemove)
    {
        var changed = false;

        var name = preset.Name;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.InputText("プリセット名", ref name, 64))
        {
            preset.Name = name;
            changed = true;
        }

        // --- 通貨 ---
        var choices = this.plugin.CurrencyCatalog.ListChoices()
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .ToList();

        if (choices.Count > 0)
        {
            // 見出しを添えて、トームストーンとスクリップを見分けられるようにする。
            var names = choices.Select(x => $"[{x.Group}] {x.Name}").ToArray();
            var index = CurrencyCatalog.IndexOf(choices, preset);
            if (index < 0)
            {
                index = 0;
            }

            ImGui.SetNextItemWidth(320f);
            if (ImGui.Combo("監視する通貨", ref index, names, names.Length))
            {
                CurrencyCatalog.Apply(preset, choices[index]);

                // 通貨が変われば買えるものも変わる。前の通貨で選んだ品は残さない。
                preset.RewardItemId = 0;
                changed = true;
            }

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "  トームストーンはスロット番号で保存するため、パッチで入れ替わっても自動追従します");
        }

        // --- 交換対象 ---
        this.DrawRewardPicker(preset, ref changed);

        if (this.plugin.CurrencyCatalog.TryResolve(preset, out var pickerCurrency))
        {
            this.DrawNpcPicker(preset, pickerCurrency, ref changed);
        }

        // --- 閾値 ---
        var thresholdMode = (int)preset.Threshold.Mode;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.Combo("交換を始める条件", ref thresholdMode, ThresholdModeNames, ThresholdModeNames.Length))
        {
            preset.Threshold.Mode = (ThresholdMode)thresholdMode;
            changed = true;
        }

        var thresholdValue = preset.Threshold.Value;
        ImGui.SetNextItemWidth(160f);
        var thresholdLabel = preset.Threshold.Mode switch
        {
            ThresholdMode.Fixed => "この所持数以上で開始",
            ThresholdMode.Percentage => "上限に対する割合 (%)",
            _ => "上限までの残りがこの値以下で開始",
        };

        if (ImGui.InputInt(thresholdLabel, ref thresholdValue))
        {
            preset.Threshold.Value = Math.Max(0, thresholdValue);
            changed = true;
        }

        if (this.plugin.CurrencyCatalog.TryResolve(preset, out var currencyId))
        {
            var trigger = this.plugin.CurrencyService.CalculateTriggerAmount(preset.Threshold, currencyId);
            var current = this.plugin.CurrencyService.GetCountOrZero(currencyId);
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                trigger is null
                    ? $"  現在 {current:N0}（発動条件を計算できません）"
                    : $"  現在 {current:N0} / 発動 {trigger:N0}");
        }

        // --- 交換モード ---
        var exchangeMode = (int)preset.Mode;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.Combo("どこまで交換するか", ref exchangeMode, ExchangeModeNames, ExchangeModeNames.Length))
        {
            preset.Mode = (ExchangeMode)exchangeMode;
            changed = true;
        }

        switch (preset.Mode)
        {
            case ExchangeMode.UntilCurrencyReserve:
            {
                var reserve = preset.CurrencyReserve;
                ImGui.SetNextItemWidth(160f);
                if (ImGui.InputInt("残す通貨量", ref reserve))
                {
                    preset.CurrencyReserve = Math.Max(0, reserve);
                    changed = true;
                }

                break;
            }

            case ExchangeMode.FixedQuantity:
            {
                var quantity = preset.Quantity;
                ImGui.SetNextItemWidth(160f);
                if (ImGui.InputInt("交換する回数", ref quantity))
                {
                    preset.Quantity = Math.Clamp(quantity, 1, ExchangeSession.HardLimit);
                    changed = true;
                }

                break;
            }

            case ExchangeMode.UntilTargetQuantity:
            {
                var target = preset.Quantity;
                ImGui.SetNextItemWidth(160f);
                if (ImGui.InputInt("目標の所持数", ref target))
                {
                    preset.Quantity = Math.Max(1, target);
                    changed = true;
                }

                if (preset.RewardItemId != 0 &&
                    this.plugin.CurrencyService.TryGetCount(preset.RewardItemId, out var owned, includeEquipped: true, includeArmory: true))
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"  現在 {owned:N0} / 目標 {preset.Quantity:N0}");
                }

                break;
            }

            case ExchangeMode.MaxExchange:
                ImGui.TextColored(ImGuiColors.DalamudYellow, "  通貨か所持枠が尽きるまで交換します");
                break;
        }

        ImGui.Spacing();

        if (ImGui.Button("このプリセットを削除"))
        {
            toRemove = preset;
        }

        if (changed)
        {
            EzConfig.Save();
        }
    }

    /// <summary>交換対象を、その通貨で実際に買えるものから選ばせる。</summary>
    /// <summary>
    /// 交換対象を選ぶ。
    ///
    /// ゲーム内のアイテム交換画面と同じ形にしてある。
    /// 系統（装備品／秘伝書・素材・雑貨／マテリア）を選び、
    /// その中の種別（Lv90～向け素材 など）を選んでから品が並ぶ。
    ///
    /// **品は種別を選んだときに初めて読む。**
    /// 数百件を五十音順に並べても探せないうえ、全部を先に読むと開いた瞬間に固まる。
    ///
    /// 名前しか分からない場合のために、横断検索も残してある。
    /// </summary>
    private void DrawRewardPicker(ExchangePreset preset, ref bool changed)
    {
        if (!this.plugin.CurrencyCatalog.TryResolve(preset, out var currencyItemId))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "通貨を解決できないため、交換対象を選べません");
            return;
        }

        ImGui.TextUnformatted($"交換対象: {(preset.RewardItemId == 0 ? "未設定" : ItemName(preset.RewardItemId))}");

        var catalog = this.plugin.InclusionShopCatalog;
        // 選んでいる通貨で買えるものだけを出す。
        var categories = catalog.ListCategories(currencyItemId);

        if (categories.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "交換の一覧を作れませんでした");
            return;
        }

        // --- 名前で探す（横断） ---
        ImGui.SetNextItemWidth(280f);
        if (ImGui.InputTextWithHint("##rewardsearch", "アイテム名で探す（系統をまたいで探します）", ref this.rewardSearch, 64))
        {
            // 入力が変わったときだけ探す。毎フレーム全種別を読むと重い。
            this.searchResults = null;
        }

        if (!string.IsNullOrWhiteSpace(this.rewardSearch))
        {
            this.searchResults ??= catalog.Search(this.rewardSearch, currencyItemId);

            using var searchChild = ImRaii.Child("##searchlist", new Vector2(0, 150), true);
            if (searchChild)
            {
                if (this.searchResults.Count == 0)
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "見つかりませんでした");
                }

                foreach (var (category, series, offer) in this.searchResults)
                {
                    if (ImGui.Selectable($"{offer.RewardName}  （{offer.CurrencyCost:N0}）##s{series.SpecialShopId}_{offer.RewardItemId}",
                            preset.RewardItemId == offer.RewardItemId))
                    {
                        preset.RewardItemId = offer.RewardItemId;
                        preset.PreferredNpcDataId = 0;
                        changed = true;
                    }

                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {category.DisplayName} / {series.DisplayName}");
                }
            }

            ImGui.TextColored(ImGuiColors.DalamudGrey, "  検索欄を空にすると、系統から辿る表示に戻ります");
            return;
        }

        // --- 系統 ---
        var categoryNames = categories.Select(x => x.DisplayName).ToArray();
        if (this.categoryIndex >= categoryNames.Length)
        {
            this.categoryIndex = 0;
        }

        ImGui.SetNextItemWidth(360f);
        if (ImGui.Combo("系統", ref this.categoryIndex, categoryNames, categoryNames.Length))
        {
            // 系統が変われば種別の並びも変わる。選び直しになる。
            this.seriesIndex = 0;
        }

        var selectedCategory = categories[this.categoryIndex];

        // --- 種別 ---
        var seriesNames = selectedCategory.Series.Select(x => x.DisplayName).ToArray();
        if (this.seriesIndex >= seriesNames.Length)
        {
            this.seriesIndex = 0;
        }

        ImGui.SetNextItemWidth(360f);
        ImGui.Combo("種別", ref this.seriesIndex, seriesNames, seriesNames.Length);

        var selectedSeries = selectedCategory.Series[this.seriesIndex];

        // --- 品（ここで初めて読む） ---
        var offers = catalog.ListOffers(selectedSeries.SpecialShopId, currencyItemId);

        if (offers.Count == 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "この種別に、いま選んでいる通貨で買えるものはありません");
            return;
        }

        using var child = ImRaii.Child("##rewardlist", new Vector2(0, 170), true);
        if (!child)
        {
            return;
        }

        foreach (var offer in offers)
        {
            var label = offer.RewardQuantity > 1
                ? $"{offer.RewardName} ×{offer.RewardQuantity}"
                : offer.RewardName;

            if (ImGui.Selectable($"{label}##r{offer.RewardItemId}", preset.RewardItemId == offer.RewardItemId))
            {
                preset.RewardItemId = offer.RewardItemId;

                // 品が変われば扱う窓口も変わる。前の指定は持ち越さない。
                preset.PreferredNpcDataId = 0;
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {offer.CurrencyCost:N0}");
        }
    }

    /// <summary>
    /// 交換所を指定する。
    ///
    /// 同じ品を複数の窓口が扱う。指定しなければ、アクセス済みのエーテライトがある
    /// エリアから自動で選ぶ。行きつけの街がある場合はここで固定できる。
    ///
    /// 窓口の一覧を作るにはゲームデータ全体の走査が要るため、押されたときだけ行う。
    /// </summary>
    private void DrawNpcPicker(ExchangePreset preset, uint currencyItemId, ref bool changed)
    {
        if (preset.RewardItemId == 0)
        {
            return;
        }

        var resolver = this.plugin.ExchangeResolver;

        if (!resolver.IsBuiltFor(currencyItemId))
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                preset.PreferredNpcDataId == 0
                    ? "  交換所: 自動で選びます"
                    : $"  交換所: {NpcLocationService.GetName(preset.PreferredNpcDataId)}（指定中）");

            if (ImGui.SmallButton("交換所を指定する##buildnpc"))
            {
                resolver.BeginBuild(currencyItemId);
            }

            return;
        }

        // 構築済みの索引をこの通貨へ切り替える。同じ通貨なら作り直しは起きない。
        resolver.BeginBuild(currencyItemId);

        var group = resolver.GroupByReward(true).FirstOrDefault(x => x.RewardItemId == preset.RewardItemId);

        if (group is null || group.Definitions.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "  この品を扱う交換所が見つかりません");
            return;
        }

        // 同じ NPC が複数のショップで同じ品を扱うことがある。1 行にまとめる。
        var npcs = group.Definitions
            .GroupBy(x => x.NpcDataId)
            .Select(g => g.First())
            .ToList();

        var labels = new List<string> { "自動で選ぶ" };
        labels.AddRange(npcs.Select(x =>
            $"{x.NpcName} — {NpcLocationService.GetTerritoryName(x.TerritoryId)}"));

        var index = 0;
        for (var i = 0; i < npcs.Count; i++)
        {
            if (npcs[i].NpcDataId == preset.PreferredNpcDataId)
            {
                index = i + 1;
                break;
            }
        }

        ImGui.SetNextItemWidth(360f);
        if (ImGui.Combo("交換所", ref index, labels.ToArray(), labels.Count))
        {
            preset.PreferredNpcDataId = index == 0 ? 0 : npcs[index - 1].NpcDataId;
            changed = true;
        }
    }

    /// <summary>系統名は長いので、検索結果では後半だけを出す。</summary>
    private static string ShortCategory(string name)
    {
        var separator = name.LastIndexOf('：');
        return separator >= 0 && separator + 1 < name.Length ? name[(separator + 1)..] : name;
    }

    private static string ItemName(uint itemId)
        => ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText()
           ?? $"<{itemId}>";
}
