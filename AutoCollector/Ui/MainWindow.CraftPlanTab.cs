using System;
using System.Linq;
using System.Numerics;
using AutoCollector.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;

namespace AutoCollector.Ui;

/// <summary>
/// 「何を何個作るか」と「そのために何の素材が何個いるか」を出す。
///
/// リテイナーから素材を引き出す前に、何をどれだけ引き出そうとしているのかを
/// 目で確かめられるようにする。
/// リテイナーの中身を触る処理は、この計算が正しいと確認できてから足す。
/// </summary>
public sealed partial class MainWindow
{
    private uint craftPlanCurrencyItemId;
    private uint craftPlanTargetItemId;
    private int craftPlanKeepFree = 10;
    private string craftPlanJobFilter = string.Empty;

    private void DrawCraftPlanTab()
    {
        if (!Plugin.C.DebugMode)
        {
            return;
        }

        using var tab = ImRaii.TabItem("製作計画");
        if (!tab)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "欲しいスクリップ → それを生む収集品 → 作る個数 → 要る素材、の順に決まります。");
        ImGui.TextColored(
            ImGuiColors.DalamudYellow,
            "いまは計算して出すだけです。リテイナーからの引き出しはまだ行いません。");

        ImGui.Separator();

        // --- 欲しいスクリップ ---
        var currencies = this.plugin.CurrencyCatalog.ListChoices()
            .Where(x => x.TomestonesRowId == 0)
            .ToList();

        if (currencies.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "スクリップの一覧を作れませんでした");
            return;
        }

        if (this.craftPlanCurrencyItemId == 0)
        {
            this.craftPlanCurrencyItemId = currencies[0].ItemId;
        }

        var currencyIndex = Math.Max(0, currencies.FindIndex(x => x.ItemId == this.craftPlanCurrencyItemId));

        ImGui.SetNextItemWidth(320f);
        using (var combo = ImRaii.Combo("欲しいスクリップ", currencies[currencyIndex].Name))
        {
            if (combo)
            {
                for (var i = 0; i < currencies.Count; i++)
                {
                    if (ImGui.Selectable($"{currencies[i].Name}##cur{i}", i == currencyIndex))
                    {
                        this.craftPlanCurrencyItemId = currencies[i].ItemId;
                        this.craftPlanTargetItemId = 0;
                    }
                }
            }
        }

        // --- 作る収集品 ---
        var craftable = this.plugin.CraftPlanService.ListCraftable(this.craftPlanCurrencyItemId);

        if (craftable.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "このスクリップを生む、作れる収集品が見つかりません");
            return;
        }

        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##jobfilter", "ジョブで絞る（調理 など）", ref this.craftPlanJobFilter, 32);

        var filtered = string.IsNullOrWhiteSpace(this.craftPlanJobFilter)
            ? craftable
            : craftable.Where(x => x.JobName.Contains(this.craftPlanJobFilter, StringComparison.Ordinal)).ToList();

        ImGui.TextColored(ImGuiColors.DalamudGrey, $"  作れる収集品 {filtered.Count} 件（もらえる量の多い順）");

        using (var child = ImRaii.Child("##craftlist", new Vector2(0, 150), true))
        {
            if (child)
            {
                foreach (var item in filtered.OrderByDescending(x => x.HighReward).Take(200))
                {
                    if (ImGui.Selectable($"{item.Name}##c{item.ItemId}", this.craftPlanTargetItemId == item.ItemId))
                    {
                        this.craftPlanTargetItemId = item.ItemId;
                    }

                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {item.JobName}  最大 {item.HighReward}");
                }
            }
        }

        if (this.craftPlanTargetItemId == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "作る収集品を選んでください");
            return;
        }

        // --- 残す空き枠 ---
        var keep = this.craftPlanKeepFree;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputInt("残す空き枠", ref keep))
        {
            this.craftPlanKeepFree = Math.Max(0, keep);
        }

        ImGui.Separator();

        // --- 計算結果 ---
        var plan = this.plugin.CraftPlanService.BuildPlan(this.craftPlanTargetItemId, this.craftPlanKeepFree);

        if (plan is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "計画を作れませんでした");
            return;
        }

        ImGui.TextUnformatted($"{plan.Target.Name}（{plan.Target.JobName}）");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            $"  鞄の空き {plan.FreeSlots} 枠 / 残す {plan.KeepFree} 枠");

        ImGui.TextColored(
            plan.Crafts > 0 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            $"  作る個数: {plan.Crafts} 個");

        if (plan.Crafts > 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  納品して得られる見込み: 最大 {plan.Crafts * plan.Target.HighReward:N0}");
        }

        foreach (var note in plan.Notes)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {note}");
        }

        if (plan.Materials.Count == 0)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("要る素材");

        using var table = ImRaii.Table("##materials", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("素材");
        ImGui.TableSetupColumn("1 回", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupColumn("全部で", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("鞄にある", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("引き出す", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("要る枠", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableHeadersRow();

        foreach (var material in plan.Materials)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.Name);

            if (material.IsIntermediate)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudYellow, "（作れる）");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.PerCraft.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.Needed.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.Held.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(
                material.Shortfall > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
                material.Shortfall > 0 ? material.Shortfall.ToString() : "足りています");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.NewSlots.ToString());
        }
    }
}
