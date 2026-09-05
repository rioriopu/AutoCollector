using System;
using System.Linq;
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

    public void Draw()
    {
        using var tab = ImRaii.TabItem("プリセット");
        if (!tab)
        {
            return;
        }

        var monitoring = Plugin.C.MonitoringEnabled;
        if (ImGui.Checkbox("通貨を監視して自動で交換する", ref monitoring))
        {
            Plugin.C.MonitoringEnabled = monitoring;
            EzConfig.Save();
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "  有効なプリセットが閾値に達したら、自動で交換所へ向かいます");

        if (!string.IsNullOrEmpty(this.plugin.MonitorService.LastDecision))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {this.plugin.MonitorService.LastDecision}");
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
        var currency = this.plugin.TomestoneService.TryResolveItemId(preset.TomestonesRowId, out var currencyItemId)
            ? this.plugin.TomestoneService.ListSlots().FirstOrDefault(x => x.ItemId == currencyItemId)?.Name ?? "?"
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
        var slots = this.plugin.TomestoneService.ListSlots().Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
        if (slots.Count > 0)
        {
            var names = slots.Select(x => x.Name).ToArray();
            var index = slots.FindIndex(x => x.TomestonesRowId == preset.TomestonesRowId);
            if (index < 0)
            {
                index = 0;
            }

            ImGui.SetNextItemWidth(280f);
            if (ImGui.Combo("監視する通貨", ref index, names, names.Length))
            {
                preset.TomestonesRowId = slots[index].TomestonesRowId;
                changed = true;
            }

            ImGui.TextColored(ImGuiColors.DalamudGrey, "  スロット番号で保存するため、パッチで通貨が入れ替わっても自動追従します");
        }

        // --- 交換対象 ---
        this.DrawRewardPicker(preset, ref changed);

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

        if (this.plugin.TomestoneService.TryResolveItemId(preset.TomestonesRowId, out var currencyId))
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
    private void DrawRewardPicker(ExchangePreset preset, ref bool changed)
    {
        if (!this.plugin.TomestoneService.TryResolveItemId(preset.TomestonesRowId, out var currencyItemId))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "通貨を解決できないため、交換対象を選べません");
            return;
        }

        ImGui.TextUnformatted($"交換対象: {(preset.RewardItemId == 0 ? "未設定" : ItemName(preset.RewardItemId))}");

        if (!this.plugin.ExchangeResolver.IsBuiltFor(currencyItemId))
        {
            if (ImGui.Button("この通貨の交換候補を読み込む"))
            {
                this.plugin.ExchangeResolver.BeginBuild(currencyItemId);
            }

            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "読み込むと一覧から選べます");
            return;
        }

        this.plugin.ExchangeResolver.BeginBuild(currencyItemId);

        ImGui.SetNextItemWidth(280f);
        ImGui.InputTextWithHint("##rewardsearch", "アイテム名で絞り込み", ref this.rewardSearch, 64);

        var groups = this.plugin.ExchangeResolver.GroupByReward(true);
        var filtered = string.IsNullOrWhiteSpace(this.rewardSearch)
            ? groups
            : groups.Where(g => g.RewardName.Contains(this.rewardSearch, StringComparison.OrdinalIgnoreCase)).ToList();

        using var child = ImRaii.Child("##rewardlist", new System.Numerics.Vector2(0, 140), true);
        if (!child)
        {
            return;
        }

        foreach (var group in filtered.Take(200))
        {
            var definition = group.Definitions[0];
            var selected = preset.RewardItemId == group.RewardItemId;

            if (ImGui.Selectable($"{group.RewardName}（{definition.CurrencyCost:N0}） — {definition.NpcName}", selected))
            {
                preset.RewardItemId = group.RewardItemId;
                preset.PreferredNpcDataId = definition.NpcDataId;
                changed = true;
            }
        }

        if (filtered.Count > 200)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"{filtered.Count - 200} 件は表示していません。絞り込んでください。");
        }
    }

    private static string ItemName(uint itemId)
        => ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText()
           ?? $"<{itemId}>";
}
