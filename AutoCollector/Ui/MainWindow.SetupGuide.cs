using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;

namespace AutoCollector.Ui;

/// <summary>
/// 初めて使うときの案内。
///
/// 周回の途中には割り込めないため、AutoDuty を 1 周で終わらせ、その終わりに交換する。
/// そのために AutoDuty 側で何を設定すればよいかを、順番に示す。
/// </summary>
public sealed partial class MainWindow
{
    private void DrawSetupGuide()
    {
        if (!this.plugin.AutoDuty.IsLoaded)
        {
            return;
        }

        var setup = this.plugin.AutoDutySetup;
        var pending = setup.PendingCount;

        if (pending == 0)
        {
            // 整っているときは 1 行にたたむ。毎回読ませるものではない。
            using var done = ImRaii.TreeNode("AutoDuty の設定は整っています##setup");
            if (done)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "1 周ごとに交換する構成になっています。");
                ImGui.Spacing();
                this.DrawSetupItems(setup);
            }

            return;
        }

        // 直すべきものがあるときは開いた状態で出す。
        using var node = ImRaii.TreeNode(
            $"はじめに: AutoDuty 側であと {pending} 件の設定が必要です##setup",
            ImGuiTreeNodeFlags.DefaultOpen);

        if (!node)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "ID クリア → リテイナー → GC 納品 → 交換 → 次の ID の流れにするための設定です。");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "周回の途中には割り込めないため、AutoDuty を 1 周で終わらせ、その切れ目で交換します。");

        ImGui.Spacing();

        if (ImGui.Button("AutoDuty の設定を開く", new Vector2(200, 30)))
        {
            setup.OpenAutoDutyConfig();
        }

        if (setup.HasApplicable)
        {
            ImGui.SameLine();

            if (ImGui.Button("推奨設定をまとめて適用", new Vector2(220, 30)))
            {
                setup.ApplyAll();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("AutoDuty の設定を変更します。変更内容はログに残ります。");
            }
        }

        ImGui.Spacing();
        this.DrawSetupItems(setup);
    }

    private void DrawSetupItems(Automation.AutoDutySetup setup)
    {
        using var table = ImRaii.Table(
            "##setupitems",
            4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);

        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 26f);
        ImGui.TableSetupColumn("設定", ImGuiTableColumnFlags.WidthFixed, 250f);
        ImGui.TableSetupColumn("現在 / 推奨", ImGuiTableColumnFlags.WidthFixed, 170f);
        ImGui.TableSetupColumn("場所と理由");
        ImGui.TableHeadersRow();

        foreach (var item in setup.Items)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (!item.Readable)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "?");
            }
            else if (item.Ok)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, "OK");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "!");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Title);

            ImGui.TableNextColumn();
            if (!item.Readable)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "読み取れません");
            }
            else
            {
                ImGui.TextColored(
                    item.Ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow,
                    item.Current);
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"/ {item.ExpectedLabel}");
            }

            ImGui.TableNextColumn();
            ImGui.TextWrapped(item.Where);
            ImGui.TextColored(ImGuiColors.DalamudGrey, item.Why);

            if (!item.Ok && item.Readable && item.CanApply)
            {
                if (ImGui.SmallButton($"この設定を適用##apply{item.Key}"))
                {
                    setup.Apply(item);
                }
            }
        }
    }
}
