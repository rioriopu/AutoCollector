using System;
using System.Linq;
using System.Numerics;
using AutoCollector.Automation;
using AutoCollector.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;

namespace AutoCollector.Ui;

/// <summary>
/// 「何を何個作るか」と「そのために何の素材が何個いるか」を出す。
///
/// リテイナーから素材を引き出す前に、何をどれだけ引き出そうとしているのかを
/// 目で確かめられるようにする。
/// リテイナーの中身を触る処理は、この計算が正しいと確認できてから足す。
/// </summary>
public sealed partial class MainWindow
{
    private uint craftPlanCurrencyItemId;
    private uint craftPlanTargetItemId;
    private int craftPlanKeepFree = 10;

    /// <summary>選んでいるジョブ。CraftType の行番号。</summary>
    private uint craftPlanJob;

    /// <summary>選んでいる Lv 帯の下限。</summary>
    private int craftPlanLevelBand;

    /// <summary>
    /// 製作手帳の「RECIPE LEVEL」と同じ区切り。
    ///
    /// **50-60 は Lv60 を含む。** 10 で割った刻みではない。
    /// 実際の手帳（木工 50-60）には Lv50・52・54・56・58・60 の 6 件が並ぶ。
    /// </summary>
    private static readonly (int Min, int Max)[] LevelBands =
    [
        (50, 60),
        (61, 70),
        (71, 80),
        (81, 90),
        (91, 100),
    ];

    private void DrawCraftPlanTab()
    {
        if (!Plugin.C.DebugMode)
        {
            return;
        }

        using var tab = ImRaii.TabItem("製作計画");
        if (!tab)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "欲しいスクリップ → それを生む収集品 → 作る個数 → 要る素材、の順に決まります。");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "足りない素材はリテイナーから取り出せます。呼び鈴の近くで実行してください。");

        ImGui.Separator();

        // --- 欲しいスクリップ ---
        var currencies = this.plugin.CurrencyCatalog.ListChoices()
            .Where(x => x.TomestonesRowId == 0)
            .ToList();

        if (currencies.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "スクリップの一覧を作れませんでした");
            return;
        }

        if (this.craftPlanCurrencyItemId == 0)
        {
            this.craftPlanCurrencyItemId = currencies[0].ItemId;
        }

        var currencyIndex = Math.Max(0, currencies.FindIndex(x => x.ItemId == this.craftPlanCurrencyItemId));

        ImGui.SetNextItemWidth(320f);
        using (var combo = ImRaii.Combo("欲しいスクリップ", currencies[currencyIndex].Name))
        {
            if (combo)
            {
                for (var i = 0; i < currencies.Count; i++)
                {
                    if (ImGui.Selectable($"{currencies[i].Name}##cur{i}", i == currencyIndex))
                    {
                        this.craftPlanCurrencyItemId = currencies[i].ItemId;
                        this.craftPlanTargetItemId = 0;
                    }
                }
            }
        }

        // --- 作る収集品 ---
        var craftable = this.plugin.CraftPlanService.ListCraftable(this.craftPlanCurrencyItemId);

        if (craftable.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "このスクリップを生む、作れる収集品が見つかりません");
            return;
        }

        // --- ジョブ ---
        var jobs = this.plugin.CraftPlanService.ListJobs();

        if (jobs.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "ジョブの一覧を作れませんでした");
            return;
        }

        var jobIndex = Math.Max(0, jobs.ToList().FindIndex(x => x.CraftType == this.craftPlanJob));

        ImGui.SetNextItemWidth(200f);
        using (var jobCombo = ImRaii.Combo("ジョブ", jobs[jobIndex].Name))
        {
            if (jobCombo)
            {
                for (var i = 0; i < jobs.Count; i++)
                {
                    if (ImGui.Selectable($"{jobs[i].Name}##job{i}", i == jobIndex))
                    {
                        this.craftPlanJob = jobs[i].CraftType;
                        this.craftPlanTargetItemId = 0;
                        this.craftPlanLevelBand = 0;
                    }
                }
            }
        }

        var filtered = craftable.Where(x => x.CraftType == this.craftPlanJob).ToList();

        // --- Lv 帯 ---
        //
        // 製作手帳の「RECIPE LEVEL」と同じ区切りにする。
        // 50-60 は Lv60 を含む。10 で割った刻みではない。
        var available = LevelBands
            .Where(band => filtered.Any(x => x.ClassJobLevel >= band.Min && x.ClassJobLevel <= band.Max))
            .ToList();

        // 帯が 1 つしかないなら絞る意味がない。橙貨は各ジョブ 1 件なので出ない。
        if (available.Count > 1)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  レベル:");

            foreach (var band in available)
            {
                ImGui.SameLine();

                var selected = this.craftPlanLevelBand == band.Min;

                using var color = ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.ParsedBlue, selected);

                if (ImGui.SmallButton($"{band.Min}-{band.Max}##band{band.Min}"))
                {
                    this.craftPlanLevelBand = band.Min;
                    this.craftPlanTargetItemId = 0;
                }
            }
        }

        // 帯が選ばれていなければ、いちばん上の帯にしておく。
        if (available.Count > 0 && !available.Any(x => x.Min == this.craftPlanLevelBand))
        {
            this.craftPlanLevelBand = available[^1].Min;
        }

        if (available.Count > 1)
        {
            var current = LevelBands.FirstOrDefault(x => x.Min == this.craftPlanLevelBand);

            if (current.Max > 0)
            {
                filtered = filtered
                    .Where(x => x.ClassJobLevel >= current.Min && x.ClassJobLevel <= current.Max)
                    .ToList();
            }
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, $"  作れる収集品 {filtered.Count} 件（製作手帳と同じ並び）");

        using (var child = ImRaii.Child("##craftlist", new Vector2(0, 150), true))
        {
            if (child)
            {
                foreach (var item in filtered)
                {
                    if (ImGui.Selectable($"{item.Name}##c{item.ItemId}", this.craftPlanTargetItemId == item.ItemId))
                    {
                        this.craftPlanTargetItemId = item.ItemId;
                    }

                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"  Lv{item.ClassJobLevel}  最大 {item.HighReward}");
                }
            }
        }

        if (this.craftPlanTargetItemId == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "作る収集品を選んでください");
            return;
        }

        // --- 残す空き枠 ---
        var keep = this.craftPlanKeepFree;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputInt("残す空き枠", ref keep))
        {
            this.craftPlanKeepFree = Math.Max(0, keep);
        }

        ImGui.Separator();

        // --- 計算結果 ---
        var plan = this.plugin.CraftPlanService.BuildPlan(this.craftPlanTargetItemId, this.craftPlanKeepFree);

        if (plan is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "計画を作れませんでした");
            return;
        }

        ImGui.TextUnformatted($"{plan.Target.Name}（{plan.Target.JobName}）");
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            $"  鞄の空き {plan.FreeSlots} 枠 / 残す {plan.KeepFree} 枠");

        ImGui.TextColored(
            plan.Crafts > 0 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            $"  作る個数: {plan.Crafts} 個");

        if (plan.Crafts > 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  納品して得られる見込み: 最大 {plan.Crafts * plan.Target.HighReward:N0}");
        }

        foreach (var note in plan.Notes)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {note}");
        }

        if (plan.Materials.Count == 0)
        {
            return;
        }

        ImGui.Spacing();

        // --- リテイナーから取り出す ---
        var restock = this.plugin.RetainerRestock;

        if (restock.IsRunning)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"取り出し中: {restock.StatusDetail}（{restock.Withdrawn} 個）");

            if (ImGui.Button("中止する##stoprestock"))
            {
                restock.Stop("ユーザー操作");
            }

            this.DrawRestockTrace(restock);
        }
        else
        {
            var shortfalls = plan.Materials.Where(x => x.Shortfall > 0).ToList();

            using (ImRaii.Disabled(shortfalls.Count == 0))
            {
                if (ImGui.Button("足りない素材をリテイナーから取り出す##restock"))
                {
                    var requests = shortfalls
                        .Select(x => new RestockRequest
                        {
                            ItemId = x.ItemId,
                            Name = x.Name,
                            Remaining = x.Shortfall,

                            // 作れる素材が手に入らなかったときは、その素材を取りに行く。
                            Fallback = x.SubMaterials
                                .Where(sub => sub.Shortfall > 0)
                                .Select(sub => new RestockRequest
                                {
                                    ItemId = sub.ItemId,
                                    Name = sub.Name,
                                    Remaining = sub.Shortfall,
                                })
                                .ToList(),
                        })
                        .ToList();

                    if (!restock.Start(requests, out var restockFailure))
                    {
                        this.plugin.AnomalyLog.Warn("Restock", restockFailure);
                    }
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                shortfalls.Count == 0 ? "足りない素材はありません" : $"{shortfalls.Count} 種類を取り出します");

            // 押す前に、呼び鈴が見えているかを出す。
            // 押しても何も起きないとき、原因がここか別かを切り分けられるようにする。
            var bell = restock.DescribeBell();
            ImGui.TextColored(
                bell.StartsWith("呼び鈴が見つかりました", StringComparison.Ordinal)
                    ? ImGuiColors.HealerGreen
                    : ImGuiColors.DalamudRed,
                $"  {bell}");

            if (!string.IsNullOrEmpty(restock.LastFailure))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  始められませんでした: {restock.LastFailure}");
            }
            else if (!string.IsNullOrEmpty(restock.StatusDetail))
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"  前回: {restock.StatusDetail}");
            }

            this.DrawRestockTrace(restock);
        }

        ImGui.Spacing();

        // --- 作らせる ---
        var craft = this.plugin.CraftRunner;

        if (craft.IsRunning)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"製作中: {craft.StatusDetail}（{craft.StepIndex + 1} / {craft.StepCount} 手順）");

            if (ImGui.Button("中止する##stopcraft"))
            {
                craft.Stop("ユーザー操作");
            }
        }
        else
        {
            var steps = CraftRunner.BuildSteps(plan);
            var blocked = plan.Materials.Any(x => x.Shortfall > 0 && !x.IsIntermediate);

            using (ImRaii.Disabled(steps.Count == 0 || blocked || !this.plugin.Artisan.IsLoaded))
            {
                if (ImGui.Button("この計画で作らせる##startcraft"))
                {
                    if (!craft.Start(plan, out var craftFailure))
                    {
                        this.plugin.AnomalyLog.Warn("Craft", craftFailure);
                    }
                }
            }

            ImGui.SameLine();

            if (!this.plugin.Artisan.IsLoaded)
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "Artisan が導入されていません");
            }
            else if (blocked)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "素材が足りません。先に取り出してください");
            }
            else
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudGrey,
                    $"{steps.Count} 手順: {string.Join(" → ", steps.Select(x => $"{x.Name}×{x.Crafts}回"))}");
            }

            if (!string.IsNullOrEmpty(craft.LastFailure))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  {craft.LastFailure}");
            }
            else if (!string.IsNullOrEmpty(craft.StatusDetail))
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"  前回: {craft.StatusDetail}");
            }
        }

        if (craft.Trace.Count > 0)
        {
            using var craftChild = ImRaii.Child("##crafttrace", new Vector2(0, 90), true);
            if (craftChild)
            {
                foreach (var line in craft.Trace)
                {
                    ImGui.TextUnformatted(line);
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("要る素材");

        using var table = ImRaii.Table("##materials", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("素材");
        ImGui.TableSetupColumn("1 回", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupColumn("全部で", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("鞄にある", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("引き出す", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("要る枠", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableHeadersRow();

        foreach (var material in plan.Materials)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.Name);

            if (material.IsIntermediate)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudYellow, "（作れる）");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.PerCraft.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.Needed.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.Held.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(
                material.Shortfall > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
                material.Shortfall > 0 ? material.Shortfall.ToString() : "足りています");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(material.NewSlots.ToString());

            // 作れる素材が足りないなら、その素材も出す。
            // リテイナーに完成品が無いときはこちらを取り出すことになる。
            foreach (var sub in material.SubMaterials)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"    └ {sub.Name}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.DalamudGrey, sub.PerCraft.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.DalamudGrey, sub.Needed.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.DalamudGrey, sub.Held.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(
                    sub.Shortfall > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
                    sub.Shortfall > 0 ? sub.Shortfall.ToString() : "足りています");

                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.DalamudGrey, "-");
            }
        }
    }

    /// <summary>
    /// 取り出しの記録を出す。
    ///
    /// 押しても何も起きないとき、どこまで進んだのかが分からないと原因を追えない。
    /// 詳細ログは既定で無効なので、画面にそのまま出す。
    /// </summary>
    private void DrawRestockTrace(AutoCollector.Automation.RetainerRestockRunner restock)
    {
        if (restock.Trace.Count == 0)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"取り出しの記録（{restock.Trace.Count} 行）");

        ImGui.SameLine();
        if (ImGui.SmallButton("コピー##copytrace"))
        {
            ImGui.SetClipboardText(string.Join(Environment.NewLine, restock.Trace));
        }

        using var child = ImRaii.Child("##restocktrace", new Vector2(0, 120), true);
        if (!child)
        {
            return;
        }

        foreach (var line in restock.Trace)
        {
            ImGui.TextUnformatted(line);
        }
    }
}
