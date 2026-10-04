using System;
using System.Linq;
using AutoCollector.Automation;
using ECommons.Configuration;
using ECommons.DalamudServices;
using EstellUtils.UI;
using EstellUtils.UI.Core;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Widgets;

namespace AutoCollector.Ui;

/// <summary>
/// 状況タブ。
///
/// 上から「いまの状態 / 注意 / 登録した交換 / はじめに / 連携プラグイン / 詳細」の順に置く。
/// 内部の値は最下段の折りたたみへ落とし、上の 3 段には利用者の行動を変える情報だけを出す。
///
/// 判定はここで書かない。MonitorService のスナップショットを描くだけにする。
/// 画面の条件と実際に発火する条件がずれると、最も説明しにくい壊れ方になる。
///
/// <b>EstellUtils へ移し終えたタブ。</b><c>RawTabScope</c> で囲まない。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>トームストーンの内訳の表の列。</summary>
    private static readonly TableColumn[] TomestoneColumns =
    [
        new("スロット", 70f, Align.End),
        new("通貨", SizeSpec.Weight(1f)),
        new("ItemId", 70f, Align.End),
        new("所持", 130f, Align.End),
        new("週上限", 90f, Align.End),
    ];

    /// <summary>
    /// 急停止。**この画面のいちばん上に、いつでも置く。**
    ///
    /// これまでの「中止する」は交換の実行中しか描いていなかった。
    /// 周回だけが回っているときや、製作・納品の最中には押すものが無く、
    /// 止めたいのに止められない場面があった。
    ///
    /// 止めるのは自動処理のすべて。周回の維持も、走っている周回も含む。
    /// </summary>
    private void DrawEmergencyStop()
    {
        var executor = this.plugin.ExchangeExecutor;

        // 何か動いているかを一目で分かるようにする。
        var running = executor.IsBusy ||
                      this.plugin.GoalRunner.IsRunning ||
                      this.plugin.RetainerRestock.IsRunning ||
                      this.plugin.CraftRunner.IsRunning ||
                      this.plugin.CollectableCycle.IsRunning ||
                      this.plugin.FateRunner.IsRunning ||
                      this.plugin.AutoDuty.IsRunningForDisplay() == true;

        using (EUi.HStack(wrap: true))
        {
            // 色を直接押し込むのはやめた。テーマの「破壊的な操作」で出す。
            // 何も動いていないときは目立たせない。
            if (EUi.Button(
                "すべて止める##emergencystop",
                running ? ButtonStyle.Danger : ButtonStyle.Normal,
                width: 160f))
            {
                this.plugin.EmergencyStop("状況タブから止められました");
            }

            // **止まっている旗は 2 本ある。どちらか 1 本でも立っていたら出す。**
            //
            // 周回の維持（Suspended）だけを見ていた。交換側の封鎖（IsAborted）が
            // 残っていても「止めています」が出ず、再開ボタンも出ない。
            // その状態では製作と取り出しだけが動き、納品と交換は弾かれ続ける。
            if (this.plugin.Combat.KeeperSuspended || executor.IsAborted)
            {
                EUi.TextColored("止めています", NoteKind.Warning);

                if (EUi.SmallButton("再開する##emergencyresume"))
                {
                    this.plugin.ResumeAfterStop();
                }
            }
            else if (running)
            {
                EUi.Muted("交換・製作・納品・周回のすべてを止めます");
            }
            else
            {
                EUi.Muted("いまは何も動いていません");
            }
        }

        EUi.Separator();
    }

    /// <summary>プリセットタブへ切り替える要求。立っているフレームだけ渡す。</summary>
    private bool jumpToPresetTab;

    private void DrawStatusTab()
    {
        var snap = this.plugin.MonitorService.Snapshot;

        this.DrawEmergencyStop();

        this.DrawHeadline(snap);
        this.DrawGoalRun();
        this.DrawAttention(snap);
        this.DrawPresetProgress(snap);

        EUi.Spacing();
        this.DrawSetupGuide();

        if (!this.plugin.AutoDuty.IsLoaded)
        {
            EUi.MutedParagraph("AutoDuty を使わない構成です。周回に相乗りする場合は AutoDuty を導入してください。");
        }

        EUi.Spacing();
        this.DrawPluginTable();

        EUi.Spacing();
        this.DrawInternals(snap);
    }

    /// <summary>
    /// 目標つきの周回。走っているあいだだけ出す。
    ///
    /// 素材の取り出し・製作・納品・交換を行き来するため、いまどの段にいるのかが
    /// 分からないと、止まっているのか進んでいるのか判断できない。
    /// 記録をそのまま出す。これまで不具合が見つかったのは、いつもこの記録からだった。
    /// </summary>
    private void DrawGoalRun()
    {
        var runner = this.plugin.GoalRunner;

        // **止まったあとも出し続ける。**
        // 走っているあいだだけ描いていたため、止まった瞬間に理由も記録も画面から消え、
        // 何段目で何が起きたのかを追えなくなっていた。
        var stoppedPresets = Plugin.C.Presets
            .Where(x => !string.IsNullOrEmpty(runner.BlockedReason(x.Id)))
            .ToList();

        if (!runner.IsRunning && stoppedPresets.Count == 0 && runner.Trace.Count == 0)
        {
            return;
        }

        EUi.Separator();

        if (runner.IsRunning)
        {
            EUi.WrapColored(
                $"目標つきの周回: {runner.Preset?.Name ?? "?"}（{Describe(runner.Step)} / {runner.Rounds} 回目）",
                NoteKind.Success);
            EUi.MutedParagraph($"  {runner.StatusDetail}");

            if (EUi.Button("止める##stopgoalstatus"))
            {
                runner.Stop("ユーザー操作");
            }
        }
        else if (stoppedPresets.Count > 0)
        {
            EUi.TextColored("目標つきの周回が止まっています", NoteKind.Warning);

            foreach (var preset in stoppedPresets)
            {
                var retry = runner.BlockedRetryInSeconds(preset.Id);

                EUi.MutedParagraph(
                    retry is null
                        ? $"  {preset.Name}: {runner.BlockedReason(preset.Id)}"
                        : $"  {preset.Name}: {runner.BlockedReason(preset.Id)}（{retry} 秒後にもう一度試します）");

                // 以前は PushId で区切っていた。ラベルへ直接混ぜるほうが、
                // どのプリセットのボタンかがコードからも読める。
                if (EUi.SmallButton($"いますぐもう一度試す##retry{preset.Id}"))
                {
                    runner.ClearBlock(preset.Id);
                }
            }
        }
        else
        {
            EUi.MutedParagraph($"前回の目標つきの周回: {runner.StatusDetail}");
        }

        if (runner.Trace.Count == 0)
        {
            return;
        }

        using var node = EUi.Section(
            $"進行の記録（{runner.Trace.Count} 行）", defaultOpen: false, id: "goaltrace");

        if (!node.IsVisible)
        {
            return;
        }

        using (EUi.Scroll("##goaltracelist", 180f))
        {
            foreach (var line in runner.Trace)
            {
                EUi.Label(line);
            }
        }
    }

    private static string Describe(GoalStep step) => step switch
    {
        GoalStep.Restocking => "素材の取り出し",
        GoalStep.Crafting => "製作",
        GoalStep.Cycling => "納品と交換",
        GoalStep.Exchanging => "交換",
        GoalStep.Done => "終了",
        GoalStep.Error => "失敗",
        _ => "待機",
    };

    // ------------------------------------------------------------------
    // ① いまの状態
    // ------------------------------------------------------------------

    /// <summary>
    /// いまの状態を 1 つだけ出す。
    ///
    /// 上から順に評価し、最初に当たったものだけを描く。複数を並べない。
    /// 否定形を避ける。設計どおりの正しい待ちを「壊れている」と読ませないため。
    /// </summary>
    private void DrawHeadline(MonitorSnapshot snap)
    {
        var executor = this.plugin.ExchangeExecutor;

        EUi.Separator();

        // H1 結果が未確認の交換が残っている
        if (executor.InFlight is { } attempt)
        {
            Head(NoteKind.Danger, "前回の交換の結果が確認できていません");
            Detail(StatusText.DescribeInFlight(attempt, this.plugin.CurrencyService));
            Detail("ゲーム内で所持数を確かめてから、記録を消してください。消すまで新しい交換は行いません。");

            if (EUi.Button("確認したのでクリアする##inflight"))
            {
                executor.ClearInFlight();
            }

            EUi.Separator();
            return;
        }

        // H2 失敗して止まっている
        if (executor.Step == ExchangeStep.Error)
        {
            Head(NoteKind.Danger, "交換を中止しました");
            EUi.Paragraph($"  {executor.StatusDetail}");
            Detail(StatusText.NextAction(executor.Failure));

            using (EUi.HStack(wrap: true))
            {
                if (EUi.Button("状態をリセットして再開する##reseterr"))
                {
                    executor.ResetAfterError();
                }

                this.DrawResumeAutoDutyButton();
            }

            EUi.Separator();
            return;
        }

        // H3 実行中
        if (executor.Step is not (ExchangeStep.Idle or ExchangeStep.Done))
        {
            var count = executor.SessionCompleted > 0 ? $"（{executor.SessionCompleted} 個目）" : string.Empty;
            Head(NoteKind.Warning, "交換しています");
            EUi.Paragraph($"  {StatusText.StepLabel(executor.Step)}: {executor.StatusDetail}{count}");
            this.DrawPhaseRow(executor.Step);

            if (EUi.Button("中止する##abort"))
            {
                this.plugin.EmergencyStop("ユーザー操作");
            }

            EUi.Separator();
            return;
        }

        // H4 プリセットが 1 件も無い
        if (snap.Presets.Count == 0)
        {
            Head(NoteKind.Info, "まだ何も登録されていません");
            Detail("どの通貨がいくつ貯まったら何と交換するかを 1 件登録すると、自動交換が始まります。");
            this.DrawJumpToPreset();
            EUi.Separator();
            return;
        }

        // H5 有効なものが無い
        if (snap.EnabledCount == 0)
        {
            Head(NoteKind.Info, "監視しているものがありません");
            Detail($"{snap.Presets.Count} 件ありますが、すべて止まっています。有効なものが 1 つも無いあいだは監視しません。");
            this.DrawJumpToPreset();
            EUi.Separator();
            return;
        }

        // H6 設定が途中
        if (snap.UsableCount == 0)
        {
            Head(NoteKind.Warning, "設定が途中です");
            Detail("監視する通貨は決まっていますが、何と交換するかが選ばれていません。");
            this.DrawJumpToPreset();
            EUi.Separator();
            return;
        }

        // H6.5 目標つきの周回が止まっている
        //
        // **「これで正常です」より先に出す。**
        // 止まっているのに「出番待ちです / これで正常です」と出していたため、
        // 利用者は異常に気づかず、AutoDuty を起動すれば動くと思って待ち続けた。
        // 目標つきの周回は外部の自動化を待たないので、H7 の説明も当てはまらない。
        if (this.plugin.GoalRunner.HasBlocked && !this.plugin.GoalRunner.IsRunning)
        {
            Head(NoteKind.Warning, "目標つきの周回が止まっています");
            Detail("下の「目標つきの周回が止まっています」に理由が出ています。");
            this.DrawJumpToPreset();
            EUi.Separator();
            return;
        }

        // H6.6 目標つきの周回が動いている
        if (this.plugin.GoalRunner.IsRunning)
        {
            Head(NoteKind.Success, "目標つきの周回を回しています");
            Detail(this.plugin.GoalRunner.StatusDetail);
            EUi.Separator();
            return;
        }

        // H7 外部の自動化を待っている
        if (Plugin.C.RequireExternalAutomationRunning && !snap.AutomationRunning)
        {
            Head(NoteKind.Info, "出番待ちです");
            Detail("これで正常です。AutoDuty か Artisan が動き出したら、その切れ目で交換します。");

            if (snap.ReachedCount > 0 && EUi.Button("いま 1 回だけ交換する##manual"))
            {
                if (!this.plugin.MonitorService.RequestManualRun(out var reason))
                {
                    this.plugin.AnomalyLog.Warn("Monitor", reason);
                }
            }

            EUi.Separator();
            return;
        }

        // H8 / H9 条件は満たしたが、いま始められない
        if (snap.ReachedCount > 0 && !snap.SafeToStart)
        {
            if (snap.SafetyKind == StartWaitKind.Transient)
            {
                Head(NoteKind.Warning, "まもなく交換します");
                Detail($"{snap.SafetyReason}。終わったら交換所へ向かいます。");
            }
            else
            {
                Head(NoteKind.Warning, "いまは始められません");
                Detail($"{snap.SafetyReason}。");
            }

            EUi.Separator();
            return;
        }

        // H10 条件を満たした
        if (snap.ReachedCount > 0)
        {
            Head(NoteKind.Warning, "まもなく交換します");
            Detail($"{NearestName(snap)}: 条件を満たしました。交換所へ向かいます。");
            EUi.Separator();
            return;
        }

        // H11 監視中
        Head(NoteKind.Success, "監視中");
        Detail(NearestText(snap));

        // 直前の交換が終わっている場合だけ、その結果を添える。
        if (executor.Step == ExchangeStep.Done && executor.Failure == ExchangeFailure.None &&
            executor.LastSessionCompleted > 0)
        {
            EUi.MutedParagraph(
                $"  直前の交換: {executor.LastSessionCompleted} 個 交換しました（{executor.LastFinishedAt:HH:mm}）");
        }

        EUi.Separator();

        // 以前は「●」と文を TextColored + SameLine で 2 回に分けて描いていた。
        // 1 つの文字列にして 1 回で描く。折り返しても印が行頭に残る。
        static void Head(NoteKind kind, string text) => EUi.WrapColored($"● {text}", kind);

        static void Detail(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                EUi.MutedParagraph($"  {text}");
            }
        }
    }

    /// <summary>いま進んでいる段を示す。</summary>
    private void DrawPhaseRow(ExchangeStep step)
    {
        var phase = step switch
        {
            ExchangeStep.WaitingSafeWindow or ExchangeStep.SuppressExternal or ExchangeStep.SuspendEarners => 0,
            ExchangeStep.Teleport or ExchangeStep.AethernetHop or ExchangeStep.Navigate => 1,
            ExchangeStep.ResumeEarners => 3,
            _ => 2,
        };

        // 以前は SameLine を 7 回積んでいた。色の違う断片を 1 行で描く。
        var names = new[] { "準備", "移動", "交換", "復帰" };
        var parts = new TextRun[(names.Length * 2) - 1 + 1];
        var at = 0;

        parts[at++] = new TextRun("  ");

        for (var i = 0; i < names.Length; i++)
        {
            parts[at++] = i == phase
                ? TextRun.Of(names[i], NoteKind.Success)
                : new TextRun(names[i], EUi.Colors.TextMuted);

            if (i < names.Length - 1)
            {
                parts[at++] = new TextRun(" › ", EUi.Colors.TextMuted);
            }
        }

        EUi.RichLabel(parts);
    }

    private static string NearestName(MonitorSnapshot snap)
    {
        foreach (var p in snap.Presets)
        {
            if (p.Enabled && p.Readiness == PresetReadiness.Reached)
            {
                return p.Name;
            }
        }

        return "プリセット";
    }

    private static string NearestText(MonitorSnapshot snap)
    {
        PresetProgress? nearest = null;
        var best = int.MaxValue;
        var allTargetReached = true;

        foreach (var p in snap.Presets)
        {
            if (!p.Enabled)
            {
                continue;
            }

            if (p.Readiness != PresetReadiness.TargetReached)
            {
                allTargetReached = false;
            }

            if (p.Readiness != PresetReadiness.Watching || p.Trigger is not { } trigger)
            {
                continue;
            }

            var remaining = trigger - p.Current;
            if (remaining < best)
            {
                best = remaining;
                nearest = p;
            }
        }

        if (nearest is not null)
        {
            return $"{nearest.Name}: あと {best:N0} で{nearest.ActionVerb}します";
        }

        return allTargetReached ? "すべて目標の所持数に達しています。" : "監視しています。";
    }

    // ------------------------------------------------------------------
    // ② 注意
    // ------------------------------------------------------------------

    /// <summary>
    /// 人がいま手を動かせば直るものだけを並べる。
    /// 行数が増えると①の効きが落ちるので、交換を止めない事象はここに出さない。
    /// </summary>
    private void DrawAttention(MonitorSnapshot snap)
    {
        var any = false;

        if (this.plugin.SelfCheck.Latest is { CanExchange: false })
        {
            Row(NoteKind.Danger, "セルフチェックに失敗した項目があります。交換はすべて止めています。");
        }

        if (!this.plugin.Vnavmesh.IsLoaded)
        {
            Row(NoteKind.Danger, "vnavmesh が入っていないため、交換所まで自動で移動できません。");
        }

        var executor = this.plugin.ExchangeExecutor;
        var autoDutyIdle = this.plugin.AutoDuty.IsLoaded &&
                           this.plugin.AutoDuty.TryIsStopped(out var stopped) && stopped;

        if (executor.LastResumeTerritoryId != 0 && autoDutyIdle && !this.plugin.Combat.KeeperGaveUp)
        {
            var name = Game.NpcLocationService.GetTerritoryName(executor.LastResumeTerritoryId);
            Row(NoteKind.Warning, $"AutoDuty が止まったままです（最後に周回していたのは {name}）。");
            this.DrawResumeAutoDutyButton();
        }

        if (this.plugin.Combat.KeeperGaveUp)
        {
            Row(NoteKind.Warning, "AutoDuty の周回維持をやめています（手動で止めたと判断しました）。");

            if (EUi.SmallButton("維持を再開##keeperresume"))
            {
                this.plugin.ResumeAfterStop();
            }
        }

        // **止めたことが画面に出ないと、戻し方が分からない。**
        // 止めたあと AutoDuty を手で動かしても、維持が止まったままなので 1 周で終わる。
        // その理由がどこにも出ていなかった。
        if (this.plugin.Combat.KeeperSuspended)
        {
            Row(NoteKind.Warning, "周回の維持を止めています。1 周したらそこで終わります。");

            if (EUi.SmallButton("維持を再開##keeperunsuspend"))
            {
                this.plugin.ResumeAfterStop();
            }
        }

        foreach (var p in snap.Presets)
        {
            if (!p.Enabled && p.DisabledReason is { } reason)
            {
                Row(NoteKind.Warning, $"「{p.Name}」を自動で無効にしました: {reason}");
                this.DrawJumpToPreset(small: true);
            }

            if (p.Enabled && p.Readiness == PresetReadiness.Unreadable)
            {
                Row(NoteKind.Danger, $"「{p.Name}」の所持数を読み取れないため、交換しません。");
            }
        }

        if (any)
        {
            EUi.Separator();
        }

        void Row(NoteKind kind, string text)
        {
            if (!any)
            {
                any = true;
                EUi.Heading("注意");
            }

            EUi.WrapColored($"・{text}", kind);
        }
    }

    // ------------------------------------------------------------------
    // ③ 登録した交換
    // ------------------------------------------------------------------

    private void DrawPresetProgress(MonitorSnapshot snap)
    {
        EUi.Heading("登録した交換");
        EUi.Separator();

        if (snap.Presets.Count == 0)
        {
            EUi.Muted("登録がありません。");
            return;
        }

        var activeId = this.plugin.ExchangeExecutor.ActivePresetId;
        var disabled = 0;

        foreach (var p in snap.Presets)
        {
            if (!p.Enabled)
            {
                disabled++;
                continue;
            }

            var color = p.Id == activeId
                ? EUi.NoteColor(NoteKind.Warning)
                : p.Readiness switch
                {
                    PresetReadiness.Reached => EUi.NoteColor(NoteKind.Success),
                    PresetReadiness.Watching => EUi.Colors.Text,
                    PresetReadiness.TargetReached => EUi.Colors.TextMuted,
                    _ => EUi.NoteColor(NoteKind.Danger),
                };

            // 印・名前・交換内容を 1 行で描く。
            EUi.RichLabel(
                new TextRun("● ", color),
                new TextRun(p.Name),
                new TextRun($"  {p.CurrencyName} → {p.RewardName}", EUi.Colors.TextMuted));

            this.DrawPresetGauge(p);

            EUi.MutedParagraph($"  {p.ModeText}");
            EUi.Spacing();
        }

        if (disabled > 0)
        {
            using var node = EUi.Section(
                $"停止中のもの（{disabled}）", defaultOpen: false, id: "disabledpresets");

            if (node.IsVisible)
            {
                foreach (var p in snap.Presets)
                {
                    if (p.Enabled)
                    {
                        continue;
                    }

                    EUi.Label(p.Name);

                    if (p.DisabledReason is { } reason)
                    {
                        EUi.WrapColored($"  {reason}", NoteKind.Danger);
                    }

                    if (EUi.SmallButton($"もう一度有効にする##reenable{p.Id}"))
                    {
                        foreach (var preset in Plugin.C.Presets)
                        {
                            if (preset.Id == p.Id)
                            {
                                preset.Enabled = true;
                                preset.DisabledReason = null;
                                EzConfig.Save();
                                break;
                            }
                        }
                    }
                }
            }
        }
    }

    private void DrawPresetGauge(PresetProgress p)
    {
        switch (p.Readiness)
        {
            case PresetReadiness.NeedsReward:
                using (EUi.HStack(wrap: true))
                {
                    EUi.TextColored("  交換して得るものが選ばれていません", NoteKind.Warning);

                    if (EUi.SmallButton($"選ぶ##pick{p.Id}"))
                    {
                        this.jumpToPresetTab = true;
                    }
                }

                return;

            case PresetReadiness.CurrencyUnresolved:
                using (EUi.HStack(wrap: true))
                {
                    EUi.TextColored("  この通貨をいま解決できません", NoteKind.Danger);

                    if (EUi.SmallButton($"選び直す##recur{p.Id}"))
                    {
                        this.jumpToPresetTab = true;
                    }
                }

                return;

            case PresetReadiness.Unreadable:
                EUi.TextColored("  所持数を読み取れません", NoteKind.Danger);
                return;

            case PresetReadiness.TargetReached:
                EUi.MutedParagraph(
                    $"  {p.RewardName} を {p.OwnedReward ?? 0:N0} 個 持っています（目標 {p.Trigger ?? 0:N0} ではなく所持目標）");
                return;
        }

        if (p.Trigger is not { } trigger || trigger <= 0)
        {
            EUi.WrapColored(
                $"  所持 {p.Current:N0}（所持上限を読めないため、発動する数を計算できません。条件を「固定値」にすると動きます）",
                NoteKind.Warning);
            return;
        }

        var fraction = Math.Clamp(p.Current / (float)trigger, 0f, 1f);

        // 拡大率は EstellUtils の寸法が面倒をみるので、GlobalScale は掛けない。
        using (EUi.HStack())
        {
            EUi.ProgressBar(fraction, $"{p.Current:N0} / {trigger:N0}", width: 240f);

            if (p.Readiness == PresetReadiness.Reached)
            {
                EUi.TextColored("条件を満たしました", NoteKind.Success);
            }
            else
            {
                EUi.Muted($"あと {trigger - p.Current:N0}");
            }
        }
    }

    // ------------------------------------------------------------------
    // 共通の小物
    // ------------------------------------------------------------------

    /// <summary>
    /// プリセットタブへ飛ぶボタン。
    ///
    /// いまの状態（①）から出すときは通常の大きさ、注意（②）の行に添えるときは
    /// 小さい版。注意の行に大きいボタンを並べると、行間が広がって一覧性が落ちる。
    /// </summary>
    private void DrawJumpToPreset(bool small = false)
    {
        var pressed = small
            ? EUi.SmallButton("プリセットタブを開く##jump")
            : EUi.Button("プリセットタブを開く##jump");

        if (pressed)
        {
            this.jumpToPresetTab = true;
        }
    }

    /// <summary>
    /// AutoDuty を最後の周回エリアで再開するボタン。
    ///
    /// 横に並べるかどうかは呼び出し側が <see cref="EUi.HStack"/> で決める。
    /// 以前の <c>sameLine</c> 引数は、ここで <c>ImGui.SameLine()</c> を
    /// 撃つためのものだったので要らなくなった。
    /// </summary>
    private void DrawResumeAutoDutyButton()
    {
        var territory = this.plugin.ExchangeExecutor.LastResumeTerritoryId;
        if (territory == 0 || !this.plugin.AutoDuty.IsLoaded)
        {
            return;
        }

        if (EUi.SmallButton($"{Game.NpcLocationService.GetTerritoryName(territory)} で再開##resumead"))
        {
            if (!this.plugin.ExchangeExecutor.TryResumeAutoDutyManually(out var reason))
            {
                this.plugin.AnomalyLog.Warn("AutoDuty", reason);
            }
        }
    }

    // ------------------------------------------------------------------
    // ⑥ 詳細
    // ------------------------------------------------------------------

    /// <summary>
    /// 内部の値。通常は畳んでおき、デバッグモードのときだけ開いて出す。
    /// 消してしまうと不具合報告の材料が失われるため、隠すだけにする。
    /// </summary>
    private void DrawInternals(MonitorSnapshot snap)
    {
        using var node = EUi.Section(
            "詳細（動作の確認用）", defaultOpen: Plugin.C.DebugMode, id: "internals");

        if (!node.IsVisible)
        {
            return;
        }

        var executor = this.plugin.ExchangeExecutor;
        EUi.Paragraph($"いまの手順: {executor.Step} / {executor.Failure} / {executor.StatusDetail}");
        EUi.Paragraph($"監視の判断: {this.plugin.MonitorService.LastDecision}");

        var resolver = this.plugin.ExchangeResolver;
        EUi.Paragraph(
            $"交換候補の索引: {resolver.Stage} {resolver.BuildProgress * 100:F0}% / 定義 {resolver.Results.Count} 件");

        EUi.Paragraph(
            snap.SafeToStart
                ? "安全判定: 開始できます"
                : $"安全判定: {snap.SafetyReason}（{snap.SafetyKind}）");

        EUi.Label(snap.FreeBagSlots is { } free ? $"所持枠の空き: {free}" : "所持枠の空き: 取得できません");

        // 呼び鈴は素材の取り出しに要る。覚えているかどうかを常に見えるところに出す。
        // プリセットを開かないと分からない状態だった。
        var territory = Svc.ClientState.TerritoryType;
        var knownBell = this.plugin.BellLocations.Get(territory);

        EUi.Paragraph(
            knownBell is { } bell
                ? $"呼び鈴: このエリア（{territory}）で覚えています {bell.X:F1}, {bell.Y:F1}, {bell.Z:F1}" +
                  $" / 全 {this.plugin.BellLocations.Count} エリア"
                : $"呼び鈴: このエリア（{territory}）ではまだ覚えていません" +
                  $" / 全 {this.plugin.BellLocations.Count} エリア");

        var combat = this.plugin.Combat;
        EUi.Paragraph($"周回の維持: 再開 {combat.RestartCount} 回 / {combat.KeeperStatus} / 維持停止={combat.KeeperGaveUp}");
        EUi.Paragraph($"スナップショット: 更新 {snap.AtUtc.ToLocalTime():HH:mm:ss} / 外部自動化 {snap.AutomationDetail}");

        EUi.Spacing();

        EUi.TableHeader(TomestoneColumns);

        for (var i = 0; i < snap.Slots.Count; i++)
        {
            var slot = snap.Slots[i];

            using (EUi.TableRow(TomestoneColumns, i))
            {
                EUi.TableCell(slot.TomestonesRowId.ToString())
                    .Tip("Tomestones シートの行番号です。パッチで中身が入れ替わっても、\n" +
                         "設定を作り直さずに済ませるための番号です。");

                EUi.TableCell(slot.Name);
                EUi.TableCell(slot.ItemId.ToString());

                if (this.plugin.CurrencyService.TryGetCount(slot.ItemId, out var count))
                {
                    var cap = slot.StackCap;
                    EUi.TableCell(cap > 0 ? $"{count:N0} / {cap:N0}" : $"{count:N0}");
                }
                else
                {
                    EUi.TableCell("取得不可", color: EUi.NoteColor(NoteKind.Danger));
                }

                EUi.TableCell(slot.SheetWeeklyLimit > 0 ? slot.SheetWeeklyLimit.ToString("N0") : "なし");
            }
        }

        var acquired = this.plugin.TomestoneService.GetWeeklyAcquired();
        var weeklyLimit = this.plugin.TomestoneService.GetWeeklyLimitRuntime();

        if (weeklyLimit > 0)
        {
            EUi.Label($"今週の取得量: {acquired:N0} / {weeklyLimit:N0}");
        }
        else
        {
            EUi.MutedParagraph("今週の取得量は取得できません（交換には影響しません）");
        }
    }
}
