using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;

namespace AutoCollector.Ui;

/// <summary>
/// 寄付タブ。描画が独立しているため partial で分けている。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>支援ページ。外部サービスであり、本プラグインは決済に一切関与しない。</summary>
    private const string DonationUrl = "https://www.patreon.com/c/SuppotToEstell";

    private void DrawDonationTab()
    {
        using var tab = ImRaii.TabItem("寄付");
        if (!tab)
        {
            return;
        }

        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.9f, 0.5f, 1f));
        ImGui.TextWrapped("Auto Collector をご利用いただき、誠にありがとうございます");
        ImGui.PopStyleColor();

        ImGui.Spacing();

        ImGui.TextWrapped(
            "皆さまの温かいご支援が、本プラグインの開発・メンテナンスを支える大きな力となっております。\n" +
            "頂いたサポートは新機能の開発、不具合修正、FFXIV のメジャーパッチへの追従に大切に使わせていただきます。\n" +
            "今後ともどうぞよろしくお願いいたします。");

        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.85f, 1f, 0.85f, 1f));
        ImGui.TextWrapped("いつもご支援くださり、心より感謝申し上げます。");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Patreon で支援する##openpatreon", new Vector2(240, 36)))
        {
            OpenDonationPage();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("ブラウザで Patreon ページを開きます。");
        }

        ImGui.SameLine();

        if (ImGui.Button("URL をコピー##copypatreon", new Vector2(160, 36)))
        {
            ImGui.SetClipboardText(DonationUrl);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Patreon の URL をクリップボードにコピーします。");
        }

        ImGui.Spacing();
        ImGui.TextDisabled(DonationUrl);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "※ Patreon サイトの利用は外部サービスとして行われます。Auto Collector は寄付処理には一切関与しません。");
    }

    private static void OpenDonationPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DonationUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Auto Collector] 支援ページを開けませんでした: {ex.Message}");
        }
    }
}
