using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using EstellUtils.UI;

namespace AutoCollector.Ui;

/// <summary>
/// 寄付タブ。描画が独立しているため partial で分けている。
///
/// <b>EstellUtils へ移し終えたタブ。</b>生の ImGui は残っていないので
/// <c>RawTabScope</c> で囲まない。囲むと二重に領域を取ることになる。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>支援ページ。外部サービスであり、本プラグインは決済に一切関与しない。</summary>
    private const string DonationUrl = "https://www.patreon.com/c/SuppotToEstell";

    private void DrawDonationTab()
    {
        EUi.Spacing();

        EUi.WrapColored(
            "Auto Collector をご利用いただき、誠にありがとうございます",
            new Vector4(1f, 0.9f, 0.5f, 1f));

        EUi.Spacing();

        EUi.Paragraph(
            "皆さまの温かいご支援が、本プラグインの開発・メンテナンスを支える大きな力となっております。\n" +
            "頂いたサポートは新機能の開発、不具合修正、FFXIV のメジャーパッチへの追従に大切に使わせていただきます。\n" +
            "今後ともどうぞよろしくお願いいたします。");

        EUi.Spacing();

        EUi.WrapColored(
            "いつもご支援くださり、心より感謝申し上げます。",
            new Vector4(0.85f, 1f, 0.85f, 1f));

        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // 横並びは SameLine ではなく HStack で囲む。
        // 高さは EstellUtils が決めるので、以前の 36px 指定は渡さない。
        using (EUi.HStack())
        {
            if (EUi.Button("Patreon で支援する##openpatreon", width: 240f)
                .Tip("ブラウザで Patreon ページを開きます。"))
            {
                OpenDonationPage();
            }

            if (EUi.Button("URL をコピー##copypatreon", width: 160f)
                .Tip("Patreon の URL をクリップボードにコピーします。"))
            {
                EUi.SetClipboard(DonationUrl);
            }
        }

        EUi.Spacing();
        EUi.Muted(DonationUrl);
        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // 折り返す版を明示する。色付きの 1 行テキストは折り返さない。
        EUi.WrapColored(
            "※ Patreon サイトの利用は外部サービスとして行われます。Auto Collector は寄付処理には一切関与しません。",
            ImGuiColors.DalamudGrey);
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
