using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        // **IsItem* は直前に積んだものを見る。**
        // 入力欄と、この判定のあいだに別のものを描いてはいけない。
        // 説明文を挟んだところ、欄を空にして確定しても開き直されなくなった。
        var logDirEdited = ImGui.IsItemDeactivatedAfterEdit();

        ImGui.SameLine();
        if (ImGui.SmallButton("開き直す##restartlog"))
        {
            this.plugin.StartFileLog();
        }

        if (logDirEdited)
        {
            this.plugin.StartFileLog();
        }

        // 空のときにどこへ書かれるかを出す。入力欄が空のままだと
        // 「記録されないのでは」と読めてしまう。
        //
        // 描くのは判定とボタンを積んだあと。ここを上へ動かすと両方が壊れる。
        if (string.IsNullOrWhiteSpace(Plugin.C.LogDirectory))
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  空のときはここへ書きます: {Plugin.ResolveLogDirectory()}");
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

        // 納品と交換を交互に回す。理想の流れの ③④⑤ に当たる。
        ImGui.TextUnformatted("納品と交換を繰り返す");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "納品 → スクリップが上限 → 交換 → 納品へ戻る、を収集品が尽きるまで繰り返します。");

        var cycle = this.plugin.CollectableCycle;

        if (cycle.IsRunning)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"実行中: {cycle.StatusDetail}（{cycle.Cycles} 周 / 納品 {cycle.Deliveries} 回 / 交換 {cycle.Exchanges} 回）");

            if (ImGui.Button("中止する##stopcycle"))
            {
                cycle.Stop("ユーザー操作");
            }
        }
        else
        {
            using (ImRaii.Disabled(this.plugin.ExchangeExecutor.IsBusy))
            {
                if (ImGui.Button("納品と交換の繰り返しを始める##startcycle"))
                {
                    if (!cycle.Start(out var cycleFailure))
                    {
                        this.plugin.AnomalyLog.Warn("Cycle", cycleFailure);
                    }
                }
            }

            if (!string.IsNullOrEmpty(cycle.StatusDetail))
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"  前回: {cycle.StatusDetail}");
            }
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  交換の設定が無いスクリップしか生まない収集品は、納品しても上限で止まるため対象にしません");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // スクリップ交換は系統と種別の 2 段で絞る。
        // どの組み合わせに何が並ぶのかは、画面を切り替えながら見ないと分からない。
        ImGui.TextUnformatted("アイテム交換画面の観測（読み取りのみ）");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "有効にしてから手動で交換してください。画面が切り替わるたびに 1 つのファイルへ追記します。");

        var observer = this.plugin.InclusionShopObserver;
        var observing = observer.Enabled;

        if (ImGui.Checkbox("アイテム交換画面を観測する", ref observing))
        {
            observer.Enabled = observing;
        }

        if (observer.Entries > 0)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, $"  {observer.Entries} 件を記録: {observer.FilePath}");
        }
        else if (observing)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  まだ記録していません。交換画面を開いてください");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 並び順はシートから再現できなかったため、実際の画面から覚えている。
        // 覚えたかどうかが画面から分からないと、確認のしようがない。
        ImGui.TextUnformatted("アイテム交換画面の並び順（自動で覚えます）");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "交換窓口を開いて種別を切り替えるだけで、その並びを覚えます。操作は行いません。");

        var orderStore = this.plugin.InclusionShopOrderStore;
        var learned = orderStore.Count;

        ImGui.TextColored(
            learned > 0 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey,
            learned > 0
                ? $"  覚えた種別: {learned} 件"
                : "  まだ覚えていません。交換窓口を開いて種別を順に切り替えてください");

        if (this.plugin.InclusionShopService.IsOpen())
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, "（交換画面を検出しています）");
        }

        if (orderStore.UnmatchedScreens > 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"  種別を特定できなかった画面: {orderStore.UnmatchedScreens} 件" +
                $"（直近は {orderStore.LastUnmatchedCount} 品）");
        }

        var fileState = orderStore.DescribeFile();
        ImGui.TextColored(
            fileState.StartsWith("保存済み", StringComparison.Ordinal) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            $"  {fileState}");
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"  保存先: {orderStore.FilePath}");

        ImGui.SameLine();
        if (ImGui.SmallButton("いま保存する##saveorder"))
        {
            orderStore.ForceSave();
        }

        if (ImGui.Button("覚えた並びを消す##clearorder"))
        {
            orderStore.Clear();
            this.plugin.AnomalyLog.Info("Inclusion", "覚えていた並び順を消しました");
        }

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "並びがおかしいときに押して、もう一度窓口を回ってください");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 発火手段を実測するための 3 手順。押す順に上から並べる。
        //
        // 納品だけでなく交換の確認にも使う。見出しを納品に限ると
        // 交換のときに使う道具だと分からなくなる。
        ImGui.TextUnformatted("操作を記録する（納品・交換の確認用）");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "上から順に押してください。停止すると、記録と通貨・所持品の増減がファイルへ保存されます。");

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

        ImGui.TextUnformatted("収集品の納品");

        // 窓口までの移動。画面が開いていなくても押せる必要があるため、
        // 開いているかの判定より前に置く。
        this.DrawDeliveryTrip();

        ImGui.Spacing();

        var service = this.plugin.CollectablesShopService;

        if (!service.IsOpen())
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "納品画面が開いていません。上のボタンで向かうか、窓口に話しかけてください。");
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

            var runner = this.plugin.CollectableDelivery;

            if (runner.IsRunning)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"納品中: {runner.StatusDetail}（{runner.Delivered} 個）");

                if (ImGui.Button("中止する##stopdeliver"))
                {
                    runner.Stop("ユーザー操作");
                }
            }
            else
            {
                if (ImGui.Button("手持ちをまとめて納品する##deliverall"))
                {
                    if (!runner.Start(out var startFailure))
                    {
                        this.plugin.AnomalyLog.Warn("Collect", startFailure);
                    }
                }

                if (!string.IsNullOrEmpty(runner.StatusDetail))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(
                        runner.Step == DeliveryStep.Error ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey,
                        $"{runner.StatusDetail}（{runner.Delivered} 個）");
                }
            }

            ImGui.Spacing();

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

        // 2 段目: 納品できる状態になったら撃つ。
        //
        // 納品ボタン（node 51）が現れるのを合図にしていたが、
        // 選択は効いているのにボタンが現れない状態が実機で出た。
        // ボタンは「選択が効いた」ことの代わりに見ていたにすぎないので、
        // 選択そのものを一覧から読んで確かめる方へ切り替える。
        var attempt = 0;

        void DeliverWhenReady()
        {
            attempt++;

            var button = service.ReadTradeButton();
            var confirmed = service.TryConfirmSelection(offer, ownedBefore, out var selectionDetail);

            if (confirmed || button.Ready)
            {
                if (!service.TryDeliver(out var deliverFailure))
                {
                    this.plugin.AnomalyLog.Error("Collect", $"納品を撃てませんでした: {deliverFailure}");
                    return;
                }

                this.plugin.AnomalyLog.Info(
                    "Collect",
                    $"納品を撃ちました（{attempt} 回目 / ボタン: {button.Detail} / 選択: {selectionDetail}）");
                _ = new ECommons.Schedulers.TickScheduler(Verify, 1500);
                return;
            }

            if (attempt >= 20)
            {
                this.plugin.AnomalyLog.Error(
                    "Collect",
                    $"{offer.ItemName} を選びましたが納品できる状態になりませんでした" +
                    $"（ボタン: {button.Detail} / 選択: {selectionDetail}）");
                return;
            }

            _ = new ECommons.Schedulers.TickScheduler(DeliverWhenReady, 150);
        }

        _ = new ECommons.Schedulers.TickScheduler(DeliverWhenReady, 150);
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

    /// <summary>
    /// 納品窓口へ向かう。
    ///
    /// 窓口はゲームデータから探す。ENpcData の CustomTalk を辿り、
    /// その SpecialLinks が CollectablesShop を指している NPC が窓口になる。
    /// ID は埋め込まない。
    /// </summary>
    private void DrawDeliveryTrip()
    {
        var npcService = this.plugin.CollectablesNpcService;
        var executor = this.plugin.ExchangeExecutor;

        if (!npcService.IsBuilt)
        {
            if (ImGui.Button("納品窓口を探す##findcollectnpc"))
            {
                // 走査はここで初めて行う。有効化の直後を重くしないため。
                var list = npcService.List();
                this.plugin.AnomalyLog.Info("Collectables", $"納品窓口を {list.Count} 件見つけました");
            }

            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "まだ探していません");
            return;
        }

        var npcs = npcService.List();
        var withLocation = npcs.Where(x => x.HasLocation).ToList();

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            $"窓口 {npcs.Count} 件 / 場所が分かるもの {withLocation.Count} 件");

        if (withLocation.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "場所の分かる窓口がありません。");
            return;
        }

        var destination = npcService.ChooseDestination(Plugin.C.PreferredCollectablesNpcDataId);

        if (destination is not null)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"行き先: {destination.DisplayName} — {NpcLocationService.GetTerritoryName(destination.TerritoryId)}（{destination.NpcName}）");
        }

        // 行き先を選べるようにしておく。都市によって混み具合が違う。
        var names = withLocation
            .Select(x => $"{x.DisplayName} — {NpcLocationService.GetTerritoryName(x.TerritoryId)}（{x.NpcName}）")
            .ToArray();
        var index = withLocation.FindIndex(x => x.NpcDataId == Plugin.C.PreferredCollectablesNpcDataId);
        var comboIndex = index < 0 ? 0 : index;

        ImGui.SetNextItemWidth(360f);
        if (ImGui.Combo("行き先を固定##collectnpc", ref comboIndex, names, names.Length))
        {
            Plugin.C.PreferredCollectablesNpcDataId = withLocation[comboIndex].NpcDataId;
            EzConfig.Save();
        }

        if (index >= 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("固定を解除##clearcollectnpc"))
            {
                Plugin.C.PreferredCollectablesNpcDataId = 0;
                EzConfig.Save();
            }
        }

        using (ImRaii.Disabled(destination is null || executor.IsBusy))
        {
            if (ImGui.Button("窓口へ移動してまとめて納品する##deliverytrip") && destination is not null)
            {
                if (!executor.RequestDeliveryTrip(destination, out var reason))
                {
                    this.plugin.AnomalyLog.Warn("Collectables", reason);
                }
            }
        }

        if (executor.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, "実行中です");
        }
    }
}
