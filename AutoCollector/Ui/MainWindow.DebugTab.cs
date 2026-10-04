using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using ECommons.Configuration;
using ECommons.DalamudServices;
using EstellUtils.UI;
using EstellUtils.UI.Core;
using EstellUtils.UI.Layout;

namespace AutoCollector.Ui;

/// <summary>
/// デバッグタブ。設定でデバッグモードを有効にしたときだけ表示する。
///
/// 通常の運用では触る必要がなく、出しておくと設定タブが読みにくくなるものを集める。
///
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>納品できる品の表の列。</summary>
    private static readonly TableColumn[] OfferColumns =
    [
        new("行", 40f, Align.End),
        new("品目", SizeSpec.Weight(1f)),
        new("手持ち", 70f, Align.End),
        new(string.Empty, 120f),
    ];

    private void DrawDebugTab()
    {
        if (!Plugin.C.DebugMode)
        {
            return;
        }

        var changed = false;

        // どのビルドが動いているかを最初に出す。
        // 配置したはずの機能が画面に無いとき、原因が「古い版が動いている」なのか
        // 「実装が出ていない」なのかを、これが無いと切り分けられない。
        EUi.Muted($"実行中のビルド: {BuildStamp()}");
        EUi.Separator();

        EUi.Heading("詳細ログ");
        EUi.MutedParagraph("交換の手順が変わるたびに、そのときの外部プラグインの状態を記録します。");
        EUi.MutedParagraph("不具合の報告時にこのファイルを渡すと、状況を言葉で説明する必要がなくなります。");

        EUi.Spacing();

        var detailedLog = Plugin.C.DetailedLogEnabled;
        if (EUi.Checkbox("状態遷移をファイルへ記録する", ref detailedLog))
        {
            Plugin.C.DetailedLogEnabled = detailedLog;
            changed = true;
            this.plugin.StartFileLog();
        }

        var logDir = Plugin.C.LogDirectory;

        using (EUi.HStack())
        {
            // **1 文字ごとに開き直さない。**
            // Changed は打つたびに立つので、保存だけに使う。
            // 開き直すのは Committed（焦点が外れたか Enter、かつ変更あり）のとき。
            var result = EUi.TextInput("保存先##logdir", ref logDir, maxLength: 260, width: 420f);

            if (result.Changed)
            {
                Plugin.C.LogDirectory = logDir;
                changed = true;
            }

            if (result.Committed)
            {
                this.plugin.StartFileLog();
            }

            if (EUi.SmallButton("開き直す##restartlog"))
            {
                this.plugin.StartFileLog();
            }
        }

        // 空のときにどこへ書かれるかを出す。入力欄が空のままだと
        // 「記録されないのでは」と読めてしまう。
        if (string.IsNullOrWhiteSpace(Plugin.C.LogDirectory))
        {
            EUi.MutedParagraph($"  空のときはここへ書きます: {Plugin.ResolveLogDirectory()}");
        }

        var writer = this.plugin.FileLog;
        if (!detailedLog)
        {
            EUi.Muted("  記録していません");
        }
        else if (writer is null)
        {
            EUi.TextColored("  記録を開始できていません", NoteKind.Danger);
        }
        else if (writer.Failed)
        {
            EUi.WrapColored($"  書き込めないため記録を諦めました: {writer.LastError}", NoteKind.Danger);
        }
        else
        {
            EUi.WrapColored($"  記録中: {writer.FilePath}", NoteKind.Success);

            if (writer.DroppedLines > 0)
            {
                EUi.WrapColored($"  書き込みが追いつかず {writer.DroppedLines} 行を捨てました", NoteKind.Warning);
            }
        }

        EUi.MutedParagraph("  ネットワーク共有を指定できます。書き込みは背景で行うため、共有が落ちてもゲームは止まりません");

        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // 一時停止で割り込む方式は AutoDuty のコマンドに依存する。
        // 実際に効くかどうかをここで確かめられるようにしておく。
        if (this.plugin.AutoDuty.IsLoaded)
        {
            EUi.Heading("AutoDuty の一時停止（動作確認用）");

            using (EUi.HStack(wrap: true))
            {
                if (EUi.Button("一時停止##adpause"))
                {
                    this.plugin.AutoDuty.TryPause();
                }

                if (EUi.Button("解除##adresume"))
                {
                    this.plugin.AutoDuty.TryResume();
                }

                if (this.plugin.AutoDuty.TryIsPaused(out var reportedPaused))
                {
                    EUi.TextColored(
                        reportedPaused ? "AutoDuty の状態: 一時停止中" : "AutoDuty の状態: 停止していません",
                        reportedPaused ? NoteKind.Warning : NoteKind.Success);
                }
                else
                {
                    EUi.TextColored("AutoDuty の一時停止状態を読み取れません", NoteKind.Danger);
                }
            }

            EUi.MutedParagraph("  本プラグインは一時停止を使いません。AutoDuty 側の挙動を確認するためのボタンです");
        }

        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // 収集品納品は、画面の構成も納品の発火手段も確認できていない。
        // 推測で撃たないために、まず実機で中身を読み出す。
        EUi.Heading("収集品納品画面の調査（読み取りのみ）");
        EUi.MutedParagraph("窓口に手動で話しかけ、何も押さずに実行してください。ゲームの状態は変更しません。");

        var reader = this.plugin.CollectablesShopReader;
        var open = reader.IsOpen();

        var autoDump = reader.AutoDump;
        if (EUi.Checkbox("納品画面を開いたら自動でダンプする", ref autoDump))
        {
            reader.AutoDump = autoDump;
        }

        if (!string.IsNullOrEmpty(reader.LastAutoDumpPath))
        {
            EUi.WrapColored($"  最後の自動ダンプ: {reader.LastAutoDumpPath}", NoteKind.Success);
        }

        using (EUi.HStack(wrap: true))
        {
            if (EUi.Button("ダンプをファイルへ保存##dumpcollect", disabled: !open))
            {
                try
                {
                    var dir = string.IsNullOrWhiteSpace(Plugin.C.LogDirectory)
                        ? Svc.PluginInterface.ConfigDirectory.FullName
                        : Plugin.C.LogDirectory;

                    var path = reader.Save(dir);
                    this.plugin.AnomalyLog.Info("Collect", $"納品画面のダンプを保存しました: {path}");
                    EUi.SetClipboard(path);
                }
                catch (Exception ex)
                {
                    this.plugin.AnomalyLog.Error("Collect", $"ダンプを保存できませんでした: {ex.Message}");
                }
            }

            if (EUi.Button("ダンプをコピー##copycollect", disabled: !open))
            {
                EUi.SetClipboard(reader.Dump());
            }

            EUi.TextColored(
                open ? "納品画面が開いています" : "納品画面が開いていません",
                open ? EUi.NoteColor(NoteKind.Success) : EUi.Colors.TextMuted);
        }

        this.DrawCollectablesOffers();

        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // 納品と交換を交互に回す。理想の流れの ③④⑤ に当たる。
        EUi.Heading("納品と交換を繰り返す");
        EUi.MutedParagraph("納品 → スクリップが上限 → 交換 → 納品へ戻る、を収集品が尽きるまで繰り返します。");

        var cycle = this.plugin.CollectableCycle;

        if (cycle.IsRunning)
        {
            EUi.WrapColored(
                $"実行中: {cycle.StatusDetail}（{cycle.Cycles} 周 / 納品 {cycle.Deliveries} 回 / 交換 {cycle.Exchanges} 回）",
                NoteKind.Warning);

            if (EUi.Button("中止する##stopcycle"))
            {
                cycle.Stop("ユーザー操作");
            }
        }
        else
        {
            if (EUi.Button("納品と交換の繰り返しを始める##startcycle", disabled: this.plugin.ExchangeExecutor.IsBusy))
            {
                if (!cycle.Start(out var cycleFailure))
                {
                    this.plugin.AnomalyLog.Warn("Cycle", cycleFailure);
                }
            }

            if (!string.IsNullOrEmpty(cycle.StatusDetail))
            {
                EUi.MutedParagraph($"  前回: {cycle.StatusDetail}");
            }
        }

        EUi.MutedParagraph("  交換の設定が無いスクリップしか生まない収集品は、納品しても上限で止まるため対象にしません");

        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // スクリップ交換は系統と種別の 2 段で絞る。
        // どの組み合わせに何が並ぶのかは、画面を切り替えながら見ないと分からない。
        EUi.Heading("アイテム交換画面の観測（読み取りのみ）");
        EUi.MutedParagraph("有効にしてから手動で交換してください。画面が切り替わるたびに 1 つのファイルへ追記します。");

        var observer = this.plugin.InclusionShopObserver;
        var observing = observer.Enabled;

        if (EUi.Checkbox("アイテム交換画面を観測する", ref observing))
        {
            observer.Enabled = observing;
        }

        if (observer.Entries > 0)
        {
            EUi.WrapColored($"  {observer.Entries} 件を記録: {observer.FilePath}", NoteKind.Success);
        }
        else if (observing)
        {
            EUi.MutedParagraph("  まだ記録していません。交換画面を開いてください");
        }

        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // 並び順はシートから再現できなかったため、実際の画面から覚えている。
        // 覚えたかどうかが画面から分からないと、確認のしようがない。
        EUi.Heading("アイテム交換画面の並び順（自動で覚えます）");
        EUi.MutedParagraph("交換窓口を開いて種別を切り替えるだけで、その並びを覚えます。操作は行いません。");

        var orderStore = this.plugin.InclusionShopOrderStore;
        var learned = orderStore.Count;

        using (EUi.HStack(wrap: true))
        {
            if (learned > 0)
            {
                EUi.TextColored($"  覚えた種別: {learned} 件", NoteKind.Success);
            }
            else
            {
                EUi.Muted("  まだ覚えていません。交換窓口を開いて種別を順に切り替えてください");
            }

            if (this.plugin.InclusionShopService.IsOpen())
            {
                EUi.TextColored("（交換画面を検出しています）", NoteKind.Warning);
            }
        }

        if (orderStore.UnmatchedScreens > 0)
        {
            EUi.WrapColored(
                $"  種別を特定できなかった画面: {orderStore.UnmatchedScreens} 件" +
                $"（直近は {orderStore.LastUnmatchedCount} 品）",
                NoteKind.Warning);
        }

        var fileState = orderStore.DescribeFile();
        EUi.WrapColored(
            $"  {fileState}",
            fileState.StartsWith("保存済み", StringComparison.Ordinal) ? NoteKind.Success : NoteKind.Danger);

        using (EUi.HStack(wrap: true))
        {
            EUi.Muted($"  保存先: {orderStore.FilePath}");

            if (EUi.SmallButton("いま保存する##saveorder"))
            {
                orderStore.ForceSave();
            }
        }

        using (EUi.HStack(wrap: true))
        {
            if (EUi.Button("覚えた並びを消す##clearorder"))
            {
                orderStore.Clear();
                this.plugin.AnomalyLog.Info("Inclusion", "覚えていた並び順を消しました");
            }

            EUi.Muted("並びがおかしいときに押して、もう一度窓口を回ってください");
        }

        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        // 発火手段を実測するための 3 手順。押す順に上から並べる。
        //
        // 納品だけでなく交換の確認にも使う。見出しを納品に限ると
        // 交換のときに使う道具だと分からなくなる。
        EUi.Heading("操作を記録する（納品・交換の確認用）");
        EUi.MutedParagraph("上から順に押してください。停止すると、記録と通貨・所持品の増減がファイルへ保存されます。");

        var recorder = this.plugin.CallbackRecorder;
        var allAddons = string.IsNullOrWhiteSpace(recorder.AddonFilter);

        using (EUi.HStack(wrap: true))
        {
            if (EUi.Button("1. 全アドオンを対象にする##recall", disabled: recorder.IsRecording))
            {
                recorder.AddonFilter = string.Empty;
                recorder.Clear();
            }

            EUi.TextColored(
                allAddons ? "対象: 全アドオン" : $"対象: {recorder.AddonFilter}",
                allAddons ? NoteKind.Success : NoteKind.Warning);
        }

        using (EUi.HStack(wrap: true))
        {
            if (EUi.Button("2. 記録を開始##recstart", disabled: recorder.IsRecording))
            {
                recorder.Clear();
                recorder.Start();
            }

            if (EUi.Button("3. 停止して保存##recstop", disabled: !recorder.IsRecording))
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

            EUi.TextColored(
                recorder.IsRecording ? $"記録中（{recorder.Snapshot().Count} 件）" : "停止中",
                recorder.IsRecording ? EUi.NoteColor(NoteKind.Warning) : EUi.Colors.TextMuted);
        }

        EUi.MutedParagraph($"  保存先: {Plugin.ResolveLogDirectory()}");

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
        EUi.Spacing();
        EUi.Separator();
        EUi.Spacing();

        EUi.Heading("収集品の納品");

        // 窓口までの移動。画面が開いていなくても押せる必要があるため、
        // 開いているかの判定より前に置く。
        this.DrawDeliveryTrip();

        EUi.Spacing();

        var service = this.plugin.CollectablesShopService;

        if (!service.IsOpen())
        {
            EUi.MutedParagraph("納品画面が開いていません。上のボタンで向かうか、窓口に話しかけてください。");
            return;
        }

        unsafe
        {
            if (!service.TryGetAddon(out var addon))
            {
                EUi.WrapColored("納品画面を掴めませんでした。", NoteKind.Danger);
                return;
            }

            if (!service.TryReadOffers(addon, out var offers, out var failure))
            {
                EUi.WrapColored(failure, NoteKind.Danger);
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

            EUi.MutedParagraph(
                $"画面の一覧: {offers.Count} 件 / 手持ちのある品: {shown} 件 / " +
                $"所持している収集品: {held.Count} 種類");

            var runner = this.plugin.CollectableDelivery;

            if (runner.IsRunning)
            {
                EUi.WrapColored($"納品中: {runner.StatusDetail}（{runner.Delivered} 個）", NoteKind.Warning);

                if (EUi.Button("中止する##stopdeliver"))
                {
                    runner.Stop("ユーザー操作");
                }
            }
            else
            {
                using (EUi.HStack(wrap: true))
                {
                    if (EUi.Button("手持ちをまとめて納品する##deliverall"))
                    {
                        if (!runner.Start(out var startFailure))
                        {
                            this.plugin.AnomalyLog.Warn("Collect", startFailure);
                        }
                    }

                    if (!string.IsNullOrEmpty(runner.StatusDetail))
                    {
                        EUi.TextColored(
                            $"{runner.StatusDetail}（{runner.Delivered} 個）",
                            runner.Step == DeliveryStep.Error
                                ? EUi.NoteColor(NoteKind.Danger)
                                : EUi.Colors.TextMuted);
                    }
                }
            }

            EUi.Spacing();

            if (shown == 0)
            {
                EUi.WrapColored(
                    "手持ちの収集品が、いま開いている職業の一覧にありません。職業タブを切り替えてください。",
                    NoteKind.Warning);
                return;
            }

            EUi.TableHeader(OfferColumns);

            var row = 0;

            foreach (var offer in offers)
            {
                if (!counts.TryGetValue(offer.ItemId, out var owned) || owned <= 0)
                {
                    continue;
                }

                using (EUi.TableRow(OfferColumns, row++))
                {
                    EUi.TableCell(offer.RowIndex.ToString());
                    EUi.TableCell(offer.ItemName);
                    EUi.TableCell(owned.ToString());

                    if (EUi.SmallButton($"1 個納品する##deliver{offer.RowIndex}"))
                    {
                        this.DeliverAndVerify(offer, owned);
                    }
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
            using (EUi.HStack(wrap: true))
            {
                if (EUi.Button("納品窓口を探す##findcollectnpc"))
                {
                    // 走査はここで初めて行う。有効化の直後を重くしないため。
                    var list = npcService.List();
                    this.plugin.AnomalyLog.Info("Collectables", $"納品窓口を {list.Count} 件見つけました");
                }

                EUi.Muted("まだ探していません");
            }

            return;
        }

        var npcs = npcService.List();
        var withLocation = npcs.Where(x => x.HasLocation).ToList();

        EUi.MutedParagraph($"窓口 {npcs.Count} 件 / 場所が分かるもの {withLocation.Count} 件");

        if (withLocation.Count == 0)
        {
            EUi.WrapColored("場所の分かる窓口がありません。", NoteKind.Danger);
            return;
        }

        var destination = npcService.ChooseDestination(Plugin.C.PreferredCollectablesNpcDataId);

        if (destination is not null)
        {
            EUi.MutedParagraph(
                $"行き先: {destination.DisplayName} — {NpcLocationService.GetTerritoryName(destination.TerritoryId)}（{destination.NpcName}）");
        }

        // 行き先を選べるようにしておく。都市によって混み具合が違う。
        var names = withLocation
            .Select(x => $"{x.DisplayName} — {NpcLocationService.GetTerritoryName(x.TerritoryId)}（{x.NpcName}）")
            .ToArray();
        var index = withLocation.FindIndex(x => x.NpcDataId == Plugin.C.PreferredCollectablesNpcDataId);
        var comboIndex = index < 0 ? 0 : index;

        using (EUi.HStack(wrap: true))
        {
            if (EUi.Combo("行き先を固定##collectnpc", ref comboIndex, names, width: 360f))
            {
                Plugin.C.PreferredCollectablesNpcDataId = withLocation[comboIndex].NpcDataId;
                EzConfig.Save();
            }

            if (index >= 0 && EUi.Button("固定を解除##clearcollectnpc"))
            {
                Plugin.C.PreferredCollectablesNpcDataId = 0;
                EzConfig.Save();
            }
        }

        using (EUi.HStack(wrap: true))
        {
            if (EUi.Button(
                "窓口へ移動してまとめて納品する##deliverytrip",
                disabled: destination is null || executor.IsBusy) &&
                destination is not null)
            {
                if (!executor.RequestDeliveryTrip(destination, out var reason))
                {
                    this.plugin.AnomalyLog.Warn("Collectables", reason);
                }
            }

            if (executor.IsBusy)
            {
                EUi.TextColored("実行中です", NoteKind.Warning);
            }
        }
    }
}
