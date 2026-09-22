using System;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;
using ECommons.DalamudServices;

namespace AutoCollector.Ui;

/// <summary>
/// FATE 自動周回の設定と操作。
///
/// 周回するマップは拡張単位でも選べるようにしているが、
/// <b>保存はマップ単位で行う</b>（Config.FateZones）。
/// 拡張 ID で保存すると、パッチでマップが追加されたときに
/// ユーザーが選んだ覚えのないマップで周回が始まってしまう。
/// </summary>
public sealed class FateTab(Plugin plugin)
{
    private readonly Plugin plugin = plugin;

    private string? lastStartFailure;

    public void Draw()
    {
        using var tab = ImRaii.TabItem("FATE 周回");
        if (!tab)
        {
            return;
        }

        var cfg = Plugin.C;
        var runner = this.plugin.FateRunner;

        this.DrawHeadline(cfg, runner);
        ImGui.Separator();

        this.DrawZoneSelection(cfg);
        ImGui.Separator();

        DrawConditions(cfg);
        ImGui.Separator();

        DrawCombat(cfg);
        ImGui.Separator();

        DrawBuddy(cfg);
        ImGui.Separator();

        DrawMisc(cfg);
    }

    // ---- いまの状態と操作 ----

    private void DrawHeadline(Config cfg, FateRunner runner)
    {
        var running = runner.IsRunning;

        if (running)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, $"● {StepLabel(runner.Step)}");
            ImGui.TextWrapped(runner.StatusDetail);
            ImGui.Text($"完了した FATE: {runner.Completed} 件");

            if (ImGui.Button("止める"))
            {
                runner.Stop("画面から停止");
            }
        }
        else
        {
            var ready = this.DescribeReadiness(cfg, out var color);
            ImGui.TextColored(color, ready);

            if (runner.StoppedReason is { Length: > 0 } reason)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"前回: {reason}（完了 {runner.Completed} 件）");
            }

            if (ImGui.Button("周回を始める"))
            {
                this.lastStartFailure = runner.Start(out var why) ? null : why;
            }

            if (this.lastStartFailure is { Length: > 0 } failure)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudRed, failure);
            }
        }
    }

    private string DescribeReadiness(Config cfg, out System.Numerics.Vector4 color)
    {
        if (!this.plugin.BossMod.IsLoaded)
        {
            color = ImGuiColors.DalamudRed;
            return "● BossMod Reborn が導入されていません";
        }

        if (!this.plugin.Vnavmesh.IsLoaded)
        {
            color = ImGuiColors.DalamudRed;
            return "● vnavmesh が導入されていません";
        }

        if (cfg.FateZones.Count == 0)
        {
            color = ImGuiColors.DalamudYellow;
            return "● 周回するマップを選んでください";
        }

        color = ImGuiColors.DalamudGrey;
        return $"● 待機中（マップ {cfg.FateZones.Count} 件）";
    }

    private static string StepLabel(FateStep step) => step switch
    {
        FateStep.Traveling => "マップへ移動中",
        FateStep.Waiting => "FATE を探しています",
        FateStep.MovingToFate => "FATE へ向かっています",
        FateStep.Fighting => "戦闘中",
        FateStep.Leaving => "次の FATE へ",
        FateStep.Dead => "戦闘不能",
        FateStep.Error => "停止（異常）",
        _ => "待機中",
    };

    // ---- 周回するマップ ----

    private void DrawZoneSelection(Config cfg)
    {
        ImGui.Text("周回するマップ");
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"（{cfg.FateZones.Count} 件を選択中）");

        var here = Svc.ClientState.TerritoryType;

        foreach (var ex in this.plugin.FateZoneCatalog.ListExpansions())
        {
            var selectedInEx = ex.Zones.Count(z => cfg.FateZones.Contains(z.TerritoryId));
            var allSelected = selectedInEx == ex.Zones.Count && ex.Zones.Count > 0;

            var label = FateZoneCatalog.YieldsBicolorGems(ex.ExVersionId)
                ? $"{ex.Name}（{selectedInEx}/{ex.Zones.Count}・バイカラージェム）"
                : $"{ex.Name}（{selectedInEx}/{ex.Zones.Count}）";

            using var node = ImRaii.TreeNode($"{label}###fate_ex_{ex.ExVersionId}");
            if (!node)
            {
                continue;
            }

            // 拡張ごとの一括切り替え。押した時点の配下だけを対象にする。
            var toggleAll = allSelected;
            if (ImGui.Checkbox($"この拡張をまとめて選ぶ###fate_all_{ex.ExVersionId}", ref toggleAll))
            {
                foreach (var z in ex.Zones)
                {
                    if (toggleAll)
                    {
                        if (!cfg.FateZones.Contains(z.TerritoryId))
                        {
                            cfg.FateZones.Add(z.TerritoryId);
                        }
                    }
                    else
                    {
                        cfg.FateZones.Remove(z.TerritoryId);
                    }
                }

                EzConfig.Save();
            }

            ImGui.Indent();

            foreach (var z in ex.Zones)
            {
                var chosen = cfg.FateZones.Contains(z.TerritoryId);
                var name = z.TerritoryId == here ? $"{z.Name}（いまここ）" : z.Name;

                if (ImGui.Checkbox($"{name}###fate_zone_{z.TerritoryId}", ref chosen))
                {
                    if (chosen)
                    {
                        if (!cfg.FateZones.Contains(z.TerritoryId))
                        {
                            cfg.FateZones.Add(z.TerritoryId);
                        }
                    }
                    else
                    {
                        cfg.FateZones.Remove(z.TerritoryId);
                    }

                    EzConfig.Save();
                }
            }

            ImGui.Unindent();
        }

        var swap = cfg.FateSwapZoneWhenEmpty;
        if (ImGui.Checkbox("FATE が無ければ次のマップへ移る", ref swap))
        {
            cfg.FateSwapZoneWhenEmpty = swap;
            EzConfig.Save();
        }

        if (cfg.FateSwapZoneWhenEmpty)
        {
            ImGui.Indent();
            var wait = cfg.FateZoneSwapWaitSeconds;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("移るまでに待つ秒数", ref wait))
            {
                cfg.FateZoneSwapWaitSeconds = Math.Clamp(wait, 5, 600);
                EzConfig.Save();
            }

            ImGui.Unindent();
        }
    }

    // ---- 狙う FATE の条件 ----

    private static void DrawConditions(Config cfg)
    {
        ImGui.Text("狙う FATE の条件");

        var minTime = cfg.FateMinTimeRemainingSec;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("残り時間がこれ未満なら狙わない（秒）", ref minTime))
        {
            cfg.FateMinTimeRemainingSec = Math.Clamp(minTime, 0, 1800);
            EzConfig.Save();
        }

        var maxProgress = cfg.FateMaxProgressPct;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("達成度がこれを超えたら狙わない（%）", ref maxProgress))
        {
            cfg.FateMaxProgressPct = Math.Clamp(maxProgress, 0, 100);
            EzConfig.Save();
        }

        var collect = cfg.FateCollectEnabled;
        if (ImGui.Checkbox("納品 FATE も回す", ref collect))
        {
            cfg.FateCollectEnabled = collect;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "納品 FATE は達成度 100% の時点では報酬が入っていません。\n"
                + "1 分後に FATE が消えるときに入るため、それまで同じマップに留まります。\n"
                + "（円から出て次の FATE を回すことはできます）");
        }

        var levelFilter = cfg.FateLevelFilterEnabled;
        if (ImGui.Checkbox("レベル差で絞る", ref levelFilter))
        {
            cfg.FateLevelFilterEnabled = levelFilter;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "既定では絞りません。\n"
                + "レベルシンクが働くため、高レベルでも低レベルの FATE を完了できます。\n"
                + "絞ると、選んだマップの FATE が一つも対象にならないことがあります。");
        }

        if (cfg.FateLevelFilterEnabled)
        {
            ImGui.Indent();

            var below = cfg.FateMaxLevelBelow;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("自分より下に許す差", ref below))
            {
                cfg.FateMaxLevelBelow = Math.Clamp(below, 0, 100);
                EzConfig.Save();
            }

            var above = cfg.FateMaxLevelAbove;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("自分より上に許す差", ref above))
            {
                cfg.FateMaxLevelAbove = Math.Clamp(above, 0, 100);
                EzConfig.Save();
            }

            ImGui.Unindent();
        }
    }

    // ---- 戦闘と離脱 ----

    private static void DrawCombat(Config cfg)
    {
        ImGui.Text("戦闘と離脱");

        var preset = cfg.FateCombatPreset;
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputText("BossMod Reborn のプリセット名", ref preset, 128))
        {
            cfg.FateCombatPreset = preset;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "FATE の中で有効にするプリセットの名前です。\n"
                + "空にすると切り替えません（すでに有効なものをそのまま使います）。");
        }

        var prefetch = cfg.FatePrefetchPct;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("次の FATE を決め始める達成度（%）", ref prefetch))
        {
            cfg.FatePrefetchPct = Math.Clamp(prefetch, 0, 100);
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "達成度がこれを超えたら、次に向かう FATE を先に決めておきます。\n"
                + "100% を見てから探し始めると、その間その場に立ち尽くすことになります。");
        }
    }

    // ---- バディ ----

    private static void DrawBuddy(Config cfg)
    {
        ImGui.Text("バディ（チョコボ）");

        var enabled = cfg.FateBuddyEnabled;
        if (ImGui.Checkbox("自動で呼び出す", ref enabled))
        {
            cfg.FateBuddyEnabled = enabled;
            EzConfig.Save();
        }

        if (!cfg.FateBuddyEnabled)
        {
            return;
        }

        ImGui.Indent();

        var left = (int)BuddyService.TimeLeftSeconds;
        var greens = BuddyService.GreensCount;
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            $"いまの残り {left / 60}分{left % 60:00}秒 ／ ギサールの野菜 {greens} 個");

        var minSec = cfg.FateBuddyMinSecondsRemaining;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("残りがこれを切ったら呼び直す（秒）", ref minSec))
        {
            cfg.FateBuddyMinSecondsRemaining = Math.Clamp(minSec, 0, 3600);
            EzConfig.Save();
        }

        var minGreens = cfg.FateGysahlMinCount;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("野菜がこれを切ったら買う", ref minGreens))
        {
            cfg.FateGysahlMinCount = Math.Clamp(minGreens, 0, 999);
            EzConfig.Save();
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "買い出しは未実装です。いまは残量の表示と、在庫があるときの呼び出しだけを行います。");

        ImGui.Unindent();
    }

    // ---- その他 ----

    private static void DrawMisc(Config cfg)
    {
        ImGui.Text("その他");

        var deathIndex = (int)cfg.FateDeathAction;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("戦闘不能になったら", ref deathIndex, DeathActionNames, DeathActionNames.Length))
        {
            cfg.FateDeathAction = (FateDeathAction)deathIndex;
            EzConfig.Save();
        }

        if (cfg.FateDeathAction != FateDeathAction.Return)
        {
            ImGui.Indent();
            var wait = cfg.FateRaiseWaitSeconds;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("レイズを待つ秒数", ref wait))
            {
                cfg.FateRaiseWaitSeconds = Math.Clamp(wait, 0, 600);
                EzConfig.Save();
            }

            ImGui.Unindent();
        }

        var blocks = cfg.FateBlocksExchange;
        if (ImGui.Checkbox("FATE 周回中は交換を始めない", ref blocks))
        {
            cfg.FateBlocksExchange = blocks;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "交換は周回を止めてテレポートするため、FATE の最中に割り込むと\n"
                + "いま戦っている FATE を取りこぼします。\n"
                + "すでに始まっている交換が途中で止まることはありません。");
        }
    }

    private static readonly string[] DeathActionNames =
    [
        "レイズを待つ（時間切れで街へ戻る）",
        "すぐ街へ戻る",
        "ソロなら戻る・パーティなら待つ",
    ];
}
