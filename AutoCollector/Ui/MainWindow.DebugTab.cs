using System;
using AutoCollector.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;
using ECommons.DalamudServices;

namespace AutoCollector.Ui;

/// <summary>
/// デバッグタブ。設定でデバッグモードを有効にしたときだけ表示する。
///
/// 通常の運用では触る必要がなく、出しておくと設定タブが読みにくくなるものを集める。
/// </summary>
public sealed partial class MainWindow
{
    private void DrawDebugTab()
    {
        if (!Plugin.C.DebugMode)
        {
            return;
        }

        using var tab = ImRaii.TabItem("デバッグ");
        if (!tab)
        {
            return;
        }

        var changed = false;

        ImGui.TextUnformatted("詳細ログ");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "交換の手順が変わるたびに、そのときの外部プラグインの状態を記録します。");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "不具合の報告時にこのファイルを渡すと、状況を言葉で説明する必要がなくなります。");

        ImGui.Spacing();

        var detailedLog = Plugin.C.DetailedLogEnabled;
        if (ImGui.Checkbox("状態遷移をファイルへ記録する", ref detailedLog))
        {
            Plugin.C.DetailedLogEnabled = detailedLog;
            changed = true;
            this.plugin.StartFileLog();
        }

        var logDir = Plugin.C.LogDirectory;
        ImGui.SetNextItemWidth(420f);
        if (ImGui.InputText("保存先", ref logDir, 260))
        {
            Plugin.C.LogDirectory = logDir;
            changed = true;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.plugin.StartFileLog();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("開き直す##restartlog"))
        {
            this.plugin.StartFileLog();
        }

        var writer = this.plugin.FileLog;
        if (!detailedLog)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  記録していません");
        }
        else if (writer is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "  記録を開始できていません");
        }
        else if (writer.Failed)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, $"  書き込めないため記録を諦めました: {writer.LastError}");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, $"  記録中: {writer.FilePath}");

            if (writer.DroppedLines > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"  書き込みが追いつかず {writer.DroppedLines} 行を捨てました");
            }
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  ネットワーク共有を指定できます。書き込みは背景で行うため、共有が落ちてもゲームは止まりません");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 一時停止で割り込む方式は AutoDuty のコマンドに依存する。
        // 実際に効くかどうかをここで確かめられるようにしておく。
        if (this.plugin.AutoDuty.IsLoaded)
        {
            ImGui.TextUnformatted("AutoDuty の一時停止（動作確認用）");

            if (ImGui.Button("一時停止##adpause"))
            {
                this.plugin.AutoDuty.TryPause();
            }

            ImGui.SameLine();

            if (ImGui.Button("解除##adresume"))
            {
                this.plugin.AutoDuty.TryResume();
            }

            ImGui.SameLine();

            if (this.plugin.AutoDuty.TryIsPaused(out var reportedPaused))
            {
                ImGui.TextColored(
                    reportedPaused ? ImGuiColors.DalamudOrange : ImGuiColors.HealerGreen,
                    reportedPaused ? "AutoDuty の状態: 一時停止中" : "AutoDuty の状態: 停止していません");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "AutoDuty の一時停止状態を読み取れません");
            }

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "  本プラグインは一時停止を使いません。AutoDuty 側の挙動を確認するためのボタンです");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 収集品納品は、画面の構成も納品の発火手段も確認できていない。
        // 推測で撃たないために、まず実機で中身を読み出す。
        ImGui.TextUnformatted("収集品納品画面の調査（読み取りのみ）");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "窓口に手動で話しかけ、何も押さずに実行してください。ゲームの状態は変更しません。");

        var reader = this.plugin.CollectablesShopReader;
        var open = reader.IsOpen();

        using (ImRaii.Disabled(!open))
        {
            if (ImGui.Button("ダンプをファイルへ保存##dumpcollect"))
            {
                try
                {
                    var dir = string.IsNullOrWhiteSpace(Plugin.C.LogDirectory)
                        ? Svc.PluginInterface.ConfigDirectory.FullName
                        : Plugin.C.LogDirectory;

                    var path = reader.Save(dir);
                    this.plugin.AnomalyLog.Info("Collect", $"納品画面のダンプを保存しました: {path}");
                    ImGui.SetClipboardText(path);
                }
                catch (Exception ex)
                {
                    this.plugin.AnomalyLog.Error("Collect", $"ダンプを保存できませんでした: {ex.Message}");
                }
            }
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!open))
        {
            if (ImGui.Button("ダンプをコピー##copycollect"))
            {
                ImGui.SetClipboardText(reader.Dump());
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(
            open ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey,
            open ? "納品画面が開いています" : "納品画面が開いていません");

        if (changed)
        {
            EzConfig.Save();
        }
    }
}
