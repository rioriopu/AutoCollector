using System;
using System.Numerics;
using AutoCollector.Automation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;

namespace AutoCollector.Ui;

/// <summary>
/// 状況タブ。
///
/// 上から「いまの状態 / 注意 / 登録した交換 / はじめに / 連携プラグイン / 詳細」の順に置く。
/// 内部の値は最下段の折りたたみへ落とし、上の 3 段には利用者の行動を変える情報だけを出す。
///
/// 判定はここで書かない。MonitorService のスナップショットを描くだけにする。
/// 画面の条件と実際に発火する条件がずれると、最も説明しにくい壊れ方になる。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>プリセットタブへ切り替える要求。立っているフレームだけ渡す。</summary>
    private bool jumpToPresetTab;

    private void DrawStatusTab()
    {
        using var tab = ImRaii.TabItem("状況");
        if (!tab)
        {
            return;
        }

        var snap = this.plugin.MonitorService.Snapshot;

        this.DrawHeadline(snap);
        this.DrawAttention(snap);
        this.DrawPresetProgress(snap);

        ImGui.Spacing();
        this.DrawSetupGuide();

        if (!this.plugin.AutoDuty.IsLoaded)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "AutoDuty を使わない構成です。周回に相乗りする場合は AutoDuty を導入してください。");
        }

        ImGui.Spacing();
        this.DrawPluginTable();

        ImGui.Spacing();
        this.DrawInternals(snap);
    }

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

        ImGui.Separator();

        // H1 結果が未確認の交換が残っている
        if (executor.InFlight is { } attempt)
        {
            Head(ImGuiColors.DalamudRed, "前回の交換の結果が確認できていません");
            Detail(StatusText.DescribeInFlight(attempt, this.plugin.CurrencyService));
            Detail("ゲーム内で所持数を確かめてから、記録を消してください。消すまで新しい交換は行いません。");

            if (ImGui.Button("確認したのでクリアする##inflight"))
            {
                executor.ClearInFlight();
            }

            ImGui.Separator();
            return;
        }

        // H2 失敗して止まっている
        if (executor.Step == ExchangeStep.Error)
        {
            Head(ImGuiColors.DalamudRed, "交換を中止しました");
            ImGui.TextWrapped($"  {executor.StatusDetail}");
            Detail(StatusText.NextAction(executor.Failure));

            if (ImGui.Button("状態をリセットして再開する##reseterr"))
            {
                executor.ResetAfterError();
            }

            this.DrawResumeAutoDutyButton(sameLine: true);

            ImGui.Separator();
            return;
        }

        // H3 実行中
        if (executor.Step is not (ExchangeStep.Idle or ExchangeStep.Done))
        {
            var count = executor.SessionCompleted > 0 ? $"（{executor.SessionCompleted} 個目）" : string.Empty;
            Head(ImGuiColors.DalamudYellow, "交換しています");
            ImGui.TextWrapped($"  {StatusText.StepLabel(executor.Step)}: {executor.StatusDetail}{count}");
            this.DrawPhaseRow(executor.Step);

            if (ImGui.Button("中止する##abort"))
            {
                this.plugin.EmergencyStop("ユーザー操作");
            }

            ImGui.Separator();
            return;
        }

        // H4 プリセットが 1 件も無い
        if (snap.Presets.Count == 0)
        {
            Head(ImGuiColors.DalamudGrey, "まだ何も登録されていません");
            Detail("どの通貨がいくつ貯まったら何と交換するかを 1 件登録すると、自動交換が始まります。");
            this.DrawJumpToPreset();
            ImGui.Separator();
            return;
        }

        // H5 有効なものが無い
        if (snap.EnabledCount == 0)
        {
            Head(ImGuiColors.DalamudGrey, "監視しているものがありません");
            Detail($"{snap.Presets.Count} 件ありますが、すべて止まっています。有効なものが 1 つも無いあいだは監視しません。");
            this.DrawJumpToPreset();
            ImGui.Separator();
            return;
        }

        // H6 設定が途中
        if (snap.UsableCount == 0)
        {
            Head(ImGuiColors.DalamudYellow, "設定が途中です");
            Detail("監視する通貨は決まっていますが、何と交換するかが選ばれていません。");
            this.DrawJumpToPreset();
            ImGui.Separator();
            return;
        }

        // H7 外部の自動化を待っている
        if (Plugin.C.RequireExternalAutomationRunning && !snap.AutomationRunning)
        {
            Head(ImGuiColors.DalamudGrey, "出番待ちです");
            Detail("これで正常です。AutoDuty か Artisan が動き出したら、その切れ目で交換します。");

            if (snap.ReachedCount > 0 && ImGui.Button("いま 1 回だけ交換する##manual"))
            {
                if (!this.plugin.MonitorService.RequestManualRun(out var reason))
                {
                    this.plugin.AnomalyLog.Warn("Monitor", reason);
                }
            }

            ImGui.Separator();
            return;
        }

        // H8 / H9 条件は満たしたが、いま始められない
        if (snap.ReachedCount > 0 && !snap.SafeToStart)
        {
            if (snap.SafetyKind == StartWaitKind.Transient)
            {
                Head(ImGuiColors.DalamudYellow, "まもなく交換します");
                Detail($"{snap.SafetyReason}。終わったら交換所へ向かいます。");
            }
            else
            {
                Head(ImGuiColors.DalamudYellow, "いまは始められません");
                Detail($"{snap.SafetyReason}。");
            }

            ImGui.Separator();
            return;
        }

        // H10 条件を満たした
        if (snap.ReachedCount > 0)
        {
            Head(ImGuiColors.DalamudYellow, "まもなく交換します");
            Detail($"{NearestName(snap)}: 条件を満たしました。交換所へ向かいます。");
            ImGui.Separator();
            return;
        }

        // H11 監視中
        Head(ImGuiColors.HealerGreen, "監視中");
        Detail(NearestText(snap));

        // 直前の交換が終わっている場合だけ、その結果を添える。
        if (executor.Step == ExchangeStep.Done && executor.Failure == ExchangeFailure.None &&
            executor.LastSessionCompleted > 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  直前の交換: {executor.LastSessionCompleted} 個 交換しました（{executor.LastFinishedAt:HH:mm}）");
        }

        ImGui.Separator();

        static void Head(Vector4 color, string text)
        {
            ImGui.TextColored(color, "●");
            ImGui.SameLine();
            ImGui.TextColored(color, text);
        }

        static void Detail(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {text}");
            }
        }
    }

    /// <summary>いま進んでいる段を示す。</summary>
    private void DrawPhaseRow(ExchangeStep step)
    {
        var phase = step switch
        {
            ExchangeStep.WaitingSafeWindow or ExchangeStep.SuppressExternal or ExchangeStep.StopAutoDuty => 0,
            ExchangeStep.Teleport or ExchangeStep.AethernetHop or ExchangeStep.Navigate => 1,
            ExchangeStep.ResumeAutoDuty => 3,
            _ => 2,
        };

        ImGui.TextUnformatted("  ");

        var names = new[] { "準備", "移動", "交換", "復帰" };
        for (var i = 0; i < names.Length; i++)
        {
            ImGui.SameLine();
            ImGui.TextColored(i == phase ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey, names[i]);

            if (i < names.Length - 1)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, "›");
            }
        }
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
            Row(ImGuiColors.DalamudRed, "セルフチェックに失敗した項目があります。交換はすべて止めています。");
        }

        if (!this.plugin.Vnavmesh.IsLoaded)
        {
            Row(ImGuiColors.DalamudRed, "vnavmesh が入っていないため、交換所まで自動で移動できません。");
        }

        var executor = this.plugin.ExchangeExecutor;
        var autoDutyIdle = this.plugin.AutoDuty.IsLoaded &&
                           this.plugin.AutoDuty.TryIsStopped(out var stopped) && stopped;

        if (executor.LastResumeTerritoryId != 0 && autoDutyIdle && !this.plugin.AutoDutyKeeper.GaveUp)
        {
            var name = Game.NpcLocationService.GetTerritoryName(executor.LastResumeTerritoryId);
            Row(ImGuiColors.DalamudYellow, $"AutoDuty が止まったままです（最後に周回していたのは {name}）。");
            this.DrawResumeAutoDutyButton(sameLine: false);
        }

        if (this.plugin.AutoDutyKeeper.GaveUp)
        {
            Row(ImGuiColors.DalamudYellow, "AutoDuty の周回維持をやめています（手動で止めたと判断しました）。");

            if (ImGui.SmallButton("維持を再開##keeperresume"))
            {
                this.plugin.AutoDutyKeeper.Resume();
            }
        }

        if (snap.FreeBagSlots is { } free && free <= Plugin.C.KeepFreeInventorySlots)
        {
            Row(
                ImGuiColors.DalamudYellow,
                $"所持枠の空きが {free} しかないため交換できません（交換後に {Plugin.C.KeepFreeInventorySlots} 枠を残す設定です）。");
        }

        foreach (var p in snap.Presets)
        {
            if (!p.Enabled && p.DisabledReason is { } reason)
            {
                Row(ImGuiColors.DalamudYellow, $"「{p.Name}」を自動で無効にしました: {reason}");
                this.DrawJumpToPreset(small: true);
            }

            if (p.Enabled && p.Readiness == PresetReadiness.Unreadable)
            {
                Row(ImGuiColors.DalamudRed, $"「{p.Name}」の所持数を読み取れないため、交換しません。");
            }
        }

        if (any)
        {
            ImGui.Separator();
        }

        void Row(Vector4 color, string text)
        {
            if (!any)
            {
                any = true;
                ImGui.TextUnformatted("注意");
            }

            ImGui.TextColored(color, $"・{text}");
        }
    }

    // ------------------------------------------------------------------
    // ③ 登録した交換
    // ------------------------------------------------------------------

    private void DrawPresetProgress(MonitorSnapshot snap)
    {
        ImGui.TextUnformatted("登録した交換");
        ImGui.Separator();

        if (snap.Presets.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "登録がありません。");
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
                ? ImGuiColors.DalamudYellow
                : p.Readiness switch
                {
                    PresetReadiness.Reached => ImGuiColors.HealerGreen,
                    PresetReadiness.Watching => ImGuiColors.DalamudWhite,
                    PresetReadiness.TargetReached => ImGuiColors.DalamudGrey,
                    _ => ImGuiColors.DalamudRed,
                };

            ImGui.TextColored(color, "●");
            ImGui.SameLine();
            ImGui.TextUnformatted(p.Name);
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"{p.CurrencyName} → {p.RewardName}");

            this.DrawPresetGauge(p);

            ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {p.ModeText}");
            ImGui.Spacing();
        }

        if (disabled > 0)
        {
            using var node = ImRaii.TreeNode($"停止中のもの（{disabled}）##disabledpresets");
            if (node)
            {
                foreach (var p in snap.Presets)
                {
                    if (p.Enabled)
                    {
                        continue;
                    }

                    ImGui.TextUnformatted(p.Name);

                    if (p.DisabledReason is { } reason)
                    {
                        ImGui.TextColored(ImGuiColors.DalamudRed, $"  {reason}");
                    }

                    if (ImGui.SmallButton($"もう一度有効にする##reenable{p.Id}"))
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
        ImGui.TextUnformatted("  ");
        ImGui.SameLine();

        switch (p.Readiness)
        {
            case PresetReadiness.NeedsReward:
                ImGui.TextColored(ImGuiColors.DalamudYellow, "交換して得るものが選ばれていません");
                ImGui.SameLine();
                if (ImGui.SmallButton($"選ぶ##pick{p.Id}"))
                {
                    this.jumpToPresetTab = true;
                }

                return;

            case PresetReadiness.CurrencyUnresolved:
                ImGui.TextColored(ImGuiColors.DalamudRed, "この通貨をいま解決できません");
                ImGui.SameLine();
                if (ImGui.SmallButton($"選び直す##recur{p.Id}"))
                {
                    this.jumpToPresetTab = true;
                }

                return;

            case PresetReadiness.Unreadable:
                ImGui.TextColored(ImGuiColors.DalamudRed, "所持数を読み取れません");
                return;

            case PresetReadiness.TargetReached:
                ImGui.TextColored(
                    ImGuiColors.DalamudGrey,
                    $"{p.RewardName} を {p.OwnedReward ?? 0:N0} 個 持っています（目標 {p.Trigger ?? 0:N0} ではなく所持目標）");
                return;
        }

        if (p.Trigger is not { } trigger || trigger <= 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"所持 {p.Current:N0}（所持上限を読めないため、発動する数を計算できません。条件を「固定値」にすると動きます）");
            return;
        }

        var fraction = Math.Clamp(p.Current / (float)trigger, 0f, 1f);
        ImGui.ProgressBar(
            fraction,
            new Vector2(240f * ImGuiHelpers.GlobalScale, 0),
            $"{p.Current:N0} / {trigger:N0}");

        ImGui.SameLine();

        if (p.Readiness == PresetReadiness.Reached)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "条件を満たしました");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"あと {trigger - p.Current:N0}");
        }
    }

    // ------------------------------------------------------------------
    // 共通の小物
    // ------------------------------------------------------------------

    private void DrawJumpToPreset(bool small = false)
    {
        var pressed = small
            ? ImGui.SmallButton("プリセットタブを開く##jump")
            : ImGui.Button("プリセットタブを開く##jump");

        if (pressed)
        {
            this.jumpToPresetTab = true;
        }
    }

    private void DrawResumeAutoDutyButton(bool sameLine)
    {
        var territory = this.plugin.ExchangeExecutor.LastResumeTerritoryId;
        if (territory == 0 || !this.plugin.AutoDuty.IsLoaded)
        {
            return;
        }

        if (sameLine)
        {
            ImGui.SameLine();
        }

        if (ImGui.SmallButton($"{Game.NpcLocationService.GetTerritoryName(territory)} で再開##resumead"))
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
        using var node = ImRaii.TreeNode(
            "詳細（動作の確認用）##internals",
            Plugin.C.DebugMode ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);

        if (!node)
        {
            return;
        }

        var executor = this.plugin.ExchangeExecutor;
        ImGui.TextUnformatted($"いまの手順: {executor.Step} / {executor.Failure} / {executor.StatusDetail}");
        ImGui.TextUnformatted($"監視の判断: {this.plugin.MonitorService.LastDecision}");

        var resolver = this.plugin.ExchangeResolver;
        ImGui.TextUnformatted(
            $"交換候補の索引: {resolver.Stage} {resolver.BuildProgress * 100:F0}% / 定義 {resolver.Results.Count} 件");

        ImGui.TextUnformatted(
            snap.SafeToStart
                ? "安全判定: 開始できます"
                : $"安全判定: {snap.SafetyReason}（{snap.SafetyKind}）");

        ImGui.TextUnformatted(
            snap.FreeBagSlots is { } free ? $"所持枠の空き: {free}" : "所持枠の空き: 取得できません");

        var keeper = this.plugin.AutoDutyKeeper;
        ImGui.TextUnformatted($"周回の維持: 再開 {keeper.RestartCount} 回 / {keeper.Status} / 維持停止={keeper.GaveUp}");
        ImGui.TextUnformatted($"スナップショット: 更新 {snap.AtUtc.ToLocalTime():HH:mm:ss} / 外部自動化 {snap.AutomationDetail}");

        ImGui.Spacing();

        using (var table = ImRaii.Table("##tomestones", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("スロット", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("通貨");
                ImGui.TableSetupColumn("ItemId", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("所持", ImGuiTableColumnFlags.WidthFixed, 130f);
                ImGui.TableSetupColumn("週上限", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableHeadersRow();

                foreach (var slot in snap.Slots)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(slot.TomestonesRowId.ToString());
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "Tomestones シートの行番号です。パッチで中身が入れ替わっても、\n" +
                            "設定を作り直さずに済ませるための番号です。");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(slot.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(slot.ItemId.ToString());

                    ImGui.TableNextColumn();
                    if (this.plugin.CurrencyService.TryGetCount(slot.ItemId, out var count))
                    {
                        var cap = slot.StackCap;
                        ImGui.TextUnformatted(cap > 0 ? $"{count:N0} / {cap:N0}" : $"{count:N0}");
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudRed, "取得不可");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(slot.SheetWeeklyLimit > 0 ? slot.SheetWeeklyLimit.ToString("N0") : "なし");
                }
            }
        }

        var acquired = this.plugin.TomestoneService.GetWeeklyAcquired();
        var weeklyLimit = this.plugin.TomestoneService.GetWeeklyLimitRuntime();

        if (weeklyLimit > 0)
        {
            ImGui.TextUnformatted($"今週の取得量: {acquired:N0} / {weeklyLimit:N0}");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "今週の取得量は取得できません（交換には影響しません）");
        }
    }
}
