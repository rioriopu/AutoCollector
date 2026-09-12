using System;
using System.Collections.Generic;
using System.Reflection;
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

        // どのビルドが動いているかを最初に出す。
        // 配置したはずの機能が画面に無いとき、原因が「古い版が動いている」なのか
        // 「実装が出ていない」なのかを、これが無いと切り分けられない。
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"実行中のビルド: {BuildStamp()}");
        ImGui.Separator();

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

        var autoDump = reader.AutoDump;
        if (ImGui.Checkbox("納品画面を開いたら自動でダンプする", ref autoDump))
        {
            reader.AutoDump = autoDump;
        }

        if (!string.IsNullOrEmpty(reader.LastAutoDumpPath))
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, $"  最後の自動ダンプ: {reader.LastAutoDumpPath}");
        }

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

        this.DrawCollectablesOffers();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 納品の発火手段を実測するための 3 手順。押す順に上から並べる。
        ImGui.TextUnformatted("納品の操作を記録する");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "上から順に押してください。停止すると、記録と通貨の増減がファイルへ保存されます。");

        var recorder = this.plugin.CallbackRecorder;
        var allAddons = string.IsNullOrWhiteSpace(recorder.AddonFilter);

        using (ImRaii.Disabled(recorder.IsRecording))
        {
            if (ImGui.Button("1. 全アドオンを対象にする##recall"))
            {
                recorder.AddonFilter = string.Empty;
                recorder.Clear();
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(
            allAddons ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow,
            allAddons ? "対象: 全アドオン" : $"対象: {recorder.AddonFilter}");

        using (ImRaii.Disabled(recorder.IsRecording))
        {
            if (ImGui.Button("2. 記録を開始##recstart"))
            {
                recorder.Clear();
                recorder.Start();
            }
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!recorder.IsRecording))
        {
            if (ImGui.Button("3. 停止して保存##recstop"))
            {
                recorder.Stop();

                try
                {
                    var path = recorder.Save(Plugin.ResolveLogDirectory());
                    this.plugin.AnomalyLog.Info("Record", $"記録を保存しました: {path}");
                }
                catch (Exception ex)
                {
                    this.plugin.AnomalyLog.Error("Record", $"記録を保存できませんでした: {ex.Message}");
                }
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(
            recorder.IsRecording ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey,
            recorder.IsRecording ? $"記録中（{recorder.Snapshot().Count} 件）" : "停止中");

        ImGui.TextColored(ImGuiColors.DalamudGrey, $"  保存先: {Plugin.ResolveLogDirectory()}");

        if (changed)
        {
            EzConfig.Save();
        }
    }

    /// <summary>
    /// いま動いている DLL の版と、その作成時刻。
    /// 再読み込みを忘れたまま「表示されない」と追いかける事故を防ぐ。
    /// </summary>
    private static string BuildStamp()
    {
        try
        {
            var assembly = typeof(Plugin).Assembly;
            var version = assembly.GetName().Version?.ToString() ?? "?";

            // ファイルの更新時刻ではなく、アセンブリへ埋め込んだ値を読む。
            // ファイル時刻だと、古い DLL が読み込まれたままファイルだけ
            // 上書きされた場合に、新しい時刻を表示しながら古いコードが動く。
            foreach (var meta in assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>())
            {
                if (meta.Key == "BuildTime")
                {
                    return $"v{version} / {meta.Value}";
                }
            }

            return $"v{version} / ビルド時刻が埋め込まれていません";
        }
        catch
        {
            return "取得できません";
        }
    }

    /// <summary>
    /// 納品できる品の一覧と、1 個だけ納品する動作確認。
    ///
    /// 実装したばかりの経路を、周回に組み込む前に単体で確かめるための場所。
    /// </summary>
    private void DrawCollectablesOffers()
    {
        // 見出しは常に描く。
        // 条件を満たさないときに何も出さない作りにしていたため、
        // 表示されない理由が画面から分からなくなっていた。
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("納品できる品（動作確認用）");

        var service = this.plugin.CollectablesShopService;

        if (!service.IsOpen())
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "納品画面が開いていません。窓口に話しかけてください。");
            return;
        }

        unsafe
        {
            if (!service.TryGetAddon(out var addon))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "納品画面を掴めませんでした。");
                return;
            }

            if (!service.TryReadOffers(addon, out var offers, out var failure))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, failure);
                return;
            }

            var held = CollectablesShopReader.ListHeldCollectables();
            var counts = new Dictionary<uint, int>();
            foreach (var (itemId, _, count) in held)
            {
                counts[itemId] = count;
            }

            var shown = 0;
            foreach (var offer in offers)
            {
                if (counts.TryGetValue(offer.ItemId, out var owned) && owned > 0)
                {
                    shown++;
                }
            }

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"画面の一覧: {offers.Count} 件 / 手持ちのある品: {shown} 件 / " +
                $"所持している収集品: {held.Count} 種類");

            if (shown == 0)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    "手持ちの収集品が、いま開いている職業の一覧にありません。職業タブを切り替えてください。");
                return;
            }

            using var table = ImRaii.Table("##offers", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (!table)
            {
                return;
            }

            ImGui.TableSetupColumn("行", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("品目");
            ImGui.TableSetupColumn("手持ち", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableHeadersRow();

            foreach (var offer in offers)
            {
                if (!counts.TryGetValue(offer.ItemId, out var owned) || owned <= 0)
                {
                    continue;
                }

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(offer.RowIndex.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(offer.ItemName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(owned.ToString());

                ImGui.TableNextColumn();

                if (ImGui.SmallButton($"1 個納品する##deliver{offer.RowIndex}"))
                {
                    this.DeliverAndVerify(offer, owned);
                }
            }
        }
    }

    /// <summary>
    /// 1 個納品して、結果を自分で確かめる。
    /// 撃ったことをもって成功とせず、所持数の変化で判断する。
    /// </summary>
    private void DeliverAndVerify(Automation.CollectableOffer offer, int ownedBefore)
    {
        var scripBefore = this.plugin.SampleSpecialCurrencies();

        var service = this.plugin.CollectablesShopService;

        // 1 段目: 一覧から選ぶ。ここではまだ何も渡さない。
        if (!service.TrySelect(offer, out var failure))
        {
            this.plugin.AnomalyLog.Error("Collect", $"選べませんでした: {failure}");
            return;
        }

        // 2 段目: 納品ボタンが押せるようになるのを待ってから押す。
        // 選んだ直後は画面が切り替わっておらず、まだ押せない。
        // 何回目で押せたかを残し、待ち方が足りているかを後から判断できるようにする。
        var attempt = 0;

        void PressWhenReady()
        {
            attempt++;

            if (service.IsTradeReady())
            {
                if (!service.TryTrade(out var tradeFailure))
                {
                    this.plugin.AnomalyLog.Error("Collect", $"納品ボタンを押せませんでした: {tradeFailure}");
                    return;
                }

                this.plugin.AnomalyLog.Info("Collect", $"納品ボタンを押しました（{attempt} 回目の確認で押せました）");
                _ = new ECommons.Schedulers.TickScheduler(Verify, 1500);
                return;
            }

            if (attempt >= 20)
            {
                this.plugin.AnomalyLog.Error(
                    "Collect",
                    $"{offer.ItemName} を選びましたが、納品ボタンが押せる状態になりませんでした");
                return;
            }

            _ = new ECommons.Schedulers.TickScheduler(PressWhenReady, 150);
        }

        _ = new ECommons.Schedulers.TickScheduler(PressWhenReady, 150);
        return;

        void Verify()
        {
            {
                var after = CollectablesShopReader.ListHeldCollectables();
                var ownedAfter = 0;
                foreach (var (itemId, _, count) in after)
                {
                    if (itemId == offer.ItemId)
                    {
                        ownedAfter = count;
                        break;
                    }
                }

                var scripAfter = this.plugin.SampleSpecialCurrencies();
                var gained = string.Empty;

                foreach (var (itemId, name, count) in scripAfter)
                {
                    foreach (var (beforeId, _, beforeCount) in scripBefore)
                    {
                        if (beforeId == itemId && count > beforeCount)
                        {
                            gained = $"{name} +{count - beforeCount}";
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(gained))
                    {
                        break;
                    }
                }

                if (ownedAfter < ownedBefore && !string.IsNullOrEmpty(gained))
                {
                    this.plugin.AnomalyLog.Info(
                        "Collect",
                        $"納品しました: {offer.ItemName} {ownedBefore} → {ownedAfter} / {gained}");
                }
                else
                {
                    this.plugin.AnomalyLog.Error(
                        "Collect",
                        $"納品の結果を確認できませんでした: {offer.ItemName} {ownedBefore} → {ownedAfter} / 通貨の増加 {(string.IsNullOrEmpty(gained) ? "なし" : gained)}");
                }
            }
        }
    }
}
