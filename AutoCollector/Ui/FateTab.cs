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

        this.DrawTestTools();
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

    /// <summary>
    /// 選んだマップを、画面に並んでいる順へ整える。
    ///
    /// <b>押した順のままにしない。</b>
    /// 設定は選ぶたびに末尾へ足されるので、先に 1 つ選んでから
    /// 拡張をまとめて選ぶと、その 1 つが先頭に残る。
    /// 巡回は保存の順で回るため、画面の並びと違う巡り方になっていた。
    /// </summary>
    private void NormalizeZoneOrder(Config cfg)
    {
        var sorted = this.plugin.FateZoneCatalog.SortByDisplayOrder(cfg.FateZones);

        cfg.FateZones.Clear();
        cfg.FateZones.AddRange(sorted);
    }

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

                this.NormalizeZoneOrder(cfg);
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

                    this.NormalizeZoneOrder(cfg);
                    EzConfig.Save();
                }
            }

            ImGui.Unindent();
        }

        var nearest = cfg.FateNearestFirst;
        if (ImGui.Checkbox("最寄りの FATE を最優先で狙う", ref nearest))
        {
            cfg.FateNearestFirst = nearest;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "距離だけで決めます。近いものから順に潰していく遊び方向けです。\n"
                + "\n"
                + "ボーナス・達成度・残り時間では割り込みません。\n"
                + "仲間と同じ FATE を狙う設定より、こちらが優先されます。\n"
                + "\n"
                + "切ると ボーナス → 達成度 → 残り時間 → 距離 の順で選びます。\n"
                + "距離はプレイヤーと FATE 中心の水平距離です。");
        }

        var follow = cfg.FateFollowParty;
        if (ImGui.Checkbox("パーティの仲間と同じ FATE を狙う", ref follow))
        {
            cfg.FateFollowParty = follow;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "仲間が入っている FATE が候補にあれば、そちらを選びます。\n"
                + "\n"
                + "同期はしません。仲間の居場所を見て選ぶだけなので、\n"
                + "相手がこのプラグインを使っている必要はありません。\n"
                + "\n"
                + "条件（残り時間・達成度など）は曲げません。\n"
                + "仲間の FATE が条件から外れていれば、ふつうに選び直します。");
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
                // **下限は 0。**待たずに次のマップへ移れるようにする。
                // 以前は 5 秒を下回れなかったため、FATE が無いマップで
                // 必ず 5 秒以上立ち止まっていた。
                cfg.FateZoneSwapWaitSeconds = Math.Clamp(wait, 0, 600);
                EzConfig.Save();
            }

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "  0 にすると、FATE が無いと分かった時点ですぐ次のマップへ移ります");

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

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "空欄のままで構いません。周回を始めるときに、こちらで専用のプリセットを");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            $"BossMod Reborn へ用意します（「{FateCombatPreset.Name}」）。");

        var preset = cfg.FateCombatPreset;
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputText("使うプリセット名（空欄 = 自動）", ref preset, 128))
        {
            cfg.FateCombatPreset = preset;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "空欄なら、こちらで作ったプリセットを使います。設定はこうなっています:\n"
                + "\n"
                + "  ・FATE の中のモンスターは自分から攻撃しに行く\n"
                + "  ・FATE 以外のモンスターには自分から絡まない\n"
                + "  ・ただし攻撃を受けたら殴り返す\n"
                + "\n"
                + "すでに同じ名前のプリセットがあれば作り直しません。\n"
                + "中身を変えたいときは BossMod Reborn 側で編集してください。");
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

        // 持っていない人がいる。持っていなければ呼び出しは行わない。
        if (!BuddyService.HasBuddy)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                "バディを持っていないため、呼び出しは行いません（周回はそのまま続きます）");
            ImGui.Unindent();
            return;
        }

        if (BuddyService.IsStabled)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                "バディを厩舎に預けているため、呼び出しは行いません");
            ImGui.Unindent();
            return;
        }

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

        var exactSpot = cfg.FateReturnToExactSpot;
        if (ImGui.Checkbox("交換のあと、離れた座標まで戻る", ref exactSpot))
        {
            cfg.FateReturnToExactSpot = exactSpot;
            EzConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "交換から戻ったあと、離れたときの座標まで移動します。\n"
                + "\n"
                + "入れなくてもエリアには必ず戻り、いちばん近い FATE から回り直します。\n"
                + "そのため、ふだんは入れなくて構いません。");
        }

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "交換との関係");
        ImGui.Indent();
        ImGui.TextWrapped(
            "バイカラージェムなどが設定した量に届くと、周回を中断して交換所へ向かい、"
            + "交換が済んだら元のエリアへ戻って周回を再開します。");
        ImGui.TextWrapped(
            "中断するのは FATE の切れ目です。戦っている最中や、"
            + "納品 FATE の報酬を待っている間は中断しません。");
        ImGui.Unindent();
    }

    /// <summary>
    /// 検証のための仕掛け。
    ///
    /// <b>マップの FATE が枯れる状況は、待っていても滅多に起きない。</b>
    /// 次のマップへ移る動きを確かめられないので、
    /// 「見つからない」と思い込ませる口を用意する。
    /// </summary>
    private void DrawTestTools()
    {
        ImGui.Spacing();

        if (!ImGui.CollapsingHeader("動作確認用"))
        {
            return;
        }

        ImGui.Indent();

        var pretend = this.plugin.FateScanner.PretendEmpty;
        if (ImGui.Checkbox("FATE が見つからないことにする", ref pretend))
        {
            this.plugin.FateScanner.PretendEmpty = pretend;

            // 記録に残す。あとでログを読むとき、本当に枯れていたのか
            // こちらが枯れたことにしたのかが分からないと判断を誤る。
            this.plugin.AnomalyLog.Warn(
                "Fate",
                pretend
                    ? "【動作確認】FATE が見つからないことにします。次のマップへ移る動きを確かめます"
                    : "【動作確認】FATE を通常どおり探します");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "このマップに狙える FATE が 1 つも無い、と思い込ませます。\n"
                + "次のマップへテレポするかを確かめるために使います。\n"
                + "\n"
                + "いま参加している FATE には効きません。\n"
                + "戦っている最中に切ると、後始末を通らずに離脱してしまうためです。\n"
                + "\n"
                + "設定には保存しません。読み込み直すと戻ります。");
        }

        if (pretend)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudOrange,
                "  FATE を探さない状態です。確認が済んだら外してください");
        }

        ImGui.Spacing();

        // **詰まったときの逃げ道を、手元にも置く。**
        // 周回が止まっている間は自動の脱出が働かないため、
        // 入り組んだ場所に取り残されたときに自力で戻れるようにする。
        if (ImGui.Button("いまの場所から脱出する"))
        {
            this.plugin.FateRunner.EscapeNow();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "地形に挟まって動けなくなったときに押してください。\n"
                + "\n"
                + "まず移動を止め、立てる場所を探して飛びます。\n"
                + "それでも動けなければ、帰還してホームポイントへ戻ります。\n"
                + "\n"
                + "周回中でなくても使えます。");
        }

        ImGui.Unindent();
    }

    private static readonly string[] DeathActionNames =
    [
        "レイズを待つ（時間切れで街へ戻る）",
        "すぐ街へ戻る",
        "ソロなら戻る・パーティなら待つ",
    ];
}
