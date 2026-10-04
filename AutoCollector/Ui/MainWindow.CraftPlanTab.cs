using System;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Game;
using EstellUtils.UI;
using EstellUtils.UI.Core;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Widgets;

namespace AutoCollector.Ui;

/// <summary>
/// 「何を何個作るか」と「そのために何の素材が何個いるか」を出す。
///
/// リテイナーから素材を引き出す前に、何をどれだけ引き出そうとしているのかを
/// 目で確かめられるようにする。
/// リテイナーの中身を触る処理は、この計算が正しいと確認できてから足す。
///
/// <b>EstellUtils へ移し終えたタブ。</b><c>RawTabScope</c> で囲まない。
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

    /// <summary>区切りは <see cref="CraftPlanService.LevelBands"/> に置いてある。</summary>
    private static (int Min, int Max)[] LevelBands => CraftPlanService.LevelBands;

    /// <summary>要る素材の表の列。</summary>
    private static readonly TableColumn[] MaterialColumns =
    [
        new("素材", SizeSpec.Weight(1f)),
        new("1 回", 50f, Align.End),
        new("全部で", 60f, Align.End),
        new("持っている", 70f, Align.End),
        new("引き出す", 70f),
        new("リテイナー", 110f),
        new("要る枠", 55f, Align.End),
    ];

    private void DrawCraftPlanTab()
    {
        if (!Plugin.C.DebugMode)
        {
            return;
        }

        EUi.MutedParagraph("欲しいスクリップ → それを生む収集品 → 作る個数 → 要る素材、の順に決まります。");
        EUi.MutedParagraph("足りない素材はリテイナーから取り出せます。呼び鈴の近くで実行してください。");

        EUi.Separator();

        // --- 欲しいスクリップ ---
        var currencies = this.plugin.CurrencyCatalog.ListChoices()
            .Where(x => x.TomestonesRowId == 0)
            .ToList();

        if (currencies.Count == 0)
        {
            EUi.WrapColored("スクリップの一覧を作れませんでした", NoteKind.Danger);
            return;
        }

        if (this.craftPlanCurrencyItemId == 0)
        {
            this.craftPlanCurrencyItemId = currencies[0].ItemId;
        }

        // **見つからないときに 0 へ丸めない。**
        //
        // Math.Max(0, -1) で先頭へ落としていたが、そうすると
        // 画面には先頭のスクリップが出る一方、実際に使われるのは
        // craftPlanCurrencyItemId に残った古い値のままになる。
        // 「画面に出ているもの」と「実際に使われるもの」が食い違い、
        // 次に何か操作した瞬間、選んだ覚えのないもので確定する。
        //
        // 一覧から消えたのなら、選び直しにする。
        var currencyIndex = currencies.FindIndex(x => x.ItemId == this.craftPlanCurrencyItemId);

        if (currencyIndex < 0)
        {
            currencyIndex = 0;
            this.craftPlanCurrencyItemId = currencies[0].ItemId;
            this.craftPlanTargetItemId = 0;
        }

        {
            var names = currencies.Select(x => x.Name).ToArray();

            if (EUi.Combo("欲しいスクリップ##craftplancurrency", ref currencyIndex, names, width: 320f))
            {
                this.craftPlanCurrencyItemId = currencies[currencyIndex].ItemId;
                this.craftPlanTargetItemId = 0;
            }
        }

        // --- 作る収集品 ---
        var craftable = this.plugin.CraftPlanService.ListCraftable(this.craftPlanCurrencyItemId);

        if (craftable.Count == 0)
        {
            EUi.WrapColored("このスクリップを生む、作れる収集品が見つかりません", NoteKind.Warning);
            return;
        }

        // --- ジョブ ---
        var jobs = this.plugin.CraftPlanService.ListJobs();

        if (jobs.Count == 0)
        {
            EUi.WrapColored("ジョブの一覧を作れませんでした", NoteKind.Danger);
            return;
        }

        // 通貨と同じ理由で 0 へ丸めない。画面と実際が食い違う。
        var jobIndex = jobs.ToList().FindIndex(x => x.CraftType == this.craftPlanJob);

        if (jobIndex < 0)
        {
            jobIndex = 0;
            this.craftPlanJob = jobs[0].CraftType;
            this.craftPlanTargetItemId = 0;
            this.craftPlanLevelBand = 0;
        }

        {
            var jobNames = jobs.Select(x => x.Name).ToArray();

            if (EUi.Combo("ジョブ##craftplanjob", ref jobIndex, jobNames, width: 200f))
            {
                this.craftPlanJob = jobs[jobIndex].CraftType;
                this.craftPlanTargetItemId = 0;
                this.craftPlanLevelBand = 0;
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
            // 選んでいる帯は PushColor ではなく主要ボタンの見た目で示す。
            // 色を直接押し込むとテーマが効かない。
            using (EUi.HStack(wrap: true))
            {
                EUi.Muted("レベル:");

                foreach (var band in available)
                {
                    var selected = this.craftPlanLevelBand == band.Min;

                    if (EUi.Button(
                        $"{band.Min}-{band.Max}##band{band.Min}",
                        selected ? ButtonStyle.Primary : ButtonStyle.Normal))
                    {
                        this.craftPlanLevelBand = band.Min;
                        this.craftPlanTargetItemId = 0;
                    }
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

        EUi.Muted($"  作れる収集品 {filtered.Count} 件（製作手帳と同じ並び）");

        using (EUi.Scroll("##craftlist", 150f))
        {
            foreach (var item in filtered)
            {
                // 名前と補足を 1 行に並べる。列幅を宣言しておかないと、
                // 名前の長さで補足の位置が揃わない。
                using (EUi.Row(SizeSpec.Weight(1f), SizeSpec.Px(170f)))
                {
                    if (EUi.Selectable($"{item.Name}##c{item.ItemId}", this.craftPlanTargetItemId == item.ItemId))
                    {
                        this.craftPlanTargetItemId = item.ItemId;
                    }

                    EUi.Muted($"Lv{item.ClassJobLevel}  最大 {item.HighReward}");
                }
            }
        }

        if (this.craftPlanTargetItemId == 0)
        {
            EUi.Muted("作る収集品を選んでください");
            return;
        }

        // --- 残す空き枠 ---
        var keep = this.craftPlanKeepFree;
        using (EUi.Field("残す空き枠"))
        {
            if (EUi.InputInt("##craftplankeepfree", ref keep, min: 0, width: 160f))
            {
                this.craftPlanKeepFree = Math.Max(0, keep);
            }
        }

        EUi.Separator();

        // --- 計算結果 ---
        var plan = this.plugin.CraftPlanService.BuildPlan(this.craftPlanTargetItemId, this.craftPlanKeepFree);

        if (plan is null)
        {
            EUi.WrapColored("計画を作れませんでした", NoteKind.Danger);
            return;
        }

        EUi.Label($"{plan.Target.Name}（{plan.Target.JobName}）");
        EUi.Muted($"  鞄の空き {plan.FreeSlots} 枠 / 残す {plan.KeepFree} 枠");

        EUi.TextColored(
            $"  作る個数: {plan.Crafts} 個",
            plan.Crafts > 0 ? NoteKind.Success : NoteKind.Danger);

        // 終わりは所持数で見る。すでに持っているぶんが目標に乗ることを示しておく。
        if (plan.TargetHeld > 0)
        {
            EUi.MutedParagraph(
                $"  すでに {plan.TargetHeld} 個持っています" +
                $"（作り終えると {plan.TargetHeld + (plan.Crafts * Math.Max(1, plan.Target.AmountResult))} 個）");
        }

        if (plan.Crafts > 0)
        {
            EUi.MutedParagraph($"  納品して得られる見込み: 最大 {plan.Crafts * plan.Target.HighReward:N0}");
        }

        foreach (var note in plan.Notes)
        {
            EUi.WrapColored($"  {note}", NoteKind.Warning);
        }

        if (plan.Materials.Count == 0)
        {
            return;
        }

        EUi.Spacing();

        // --- リテイナーから取り出す ---
        var restock = this.plugin.RetainerRestock;

        if (restock.IsRunning)
        {
            EUi.WrapColored($"取り出し中: {restock.StatusDetail}（{restock.Withdrawn} 個）", NoteKind.Warning);

            if (EUi.Button("中止する##stoprestock"))
            {
                restock.Stop("ユーザー操作");
            }

            this.DrawRestockTrace(restock);
        }
        else
        {
            var shortfalls = plan.Materials.Where(x => x.Shortfall > 0).ToList();

            using (EUi.HStack())
            {
                if (EUi.Button("足りない素材をリテイナーから取り出す##restock", disabled: shortfalls.Count == 0))
                {
                    var requests = shortfalls
                        .Select(x => new RestockRequest
                        {
                            ItemId = x.ItemId,
                            Name = x.Name,
                            Remaining = x.Shortfall,

                            // クリスタルは個数指定が出ない。すべて受け取る。
                            RetrieveAll = x.IsCrystal,

                            // 作れる素材が手に入らなかったときは、その素材を取りに行く。
                            Fallback = x.SubMaterials
                                .Where(sub => sub.Shortfall > 0)
                                .Select(sub => new RestockRequest
                                {
                                    ItemId = sub.ItemId,
                                    Name = sub.Name,
                                    Remaining = sub.Shortfall,
                                    RetrieveAll = sub.IsCrystal,
                                })
                                .ToList(),
                        })
                        .ToList();

                    if (!restock.Start(requests, out var restockFailure))
                    {
                        this.plugin.AnomalyLog.Warn("Restock", restockFailure);
                    }
                }

                EUi.Muted(shortfalls.Count == 0 ? "足りない素材はありません" : $"{shortfalls.Count} 種類を取り出します");
            }

            // 押す前に、呼び鈴が見えているかを出す。
            // 押しても何も起きないとき、原因がここか別かを切り分けられるようにする。
            var bell = restock.DescribeBell();
            EUi.WrapColored(
                $"  {bell}",
                bell.StartsWith("呼び鈴が見つかりました", StringComparison.Ordinal)
                    ? NoteKind.Success
                    : NoteKind.Danger);

            if (!string.IsNullOrEmpty(restock.LastFailure))
            {
                EUi.WrapColored($"  始められませんでした: {restock.LastFailure}", NoteKind.Danger);
            }
            else if (!string.IsNullOrEmpty(restock.StatusDetail))
            {
                EUi.MutedParagraph($"  前回: {restock.StatusDetail}");
            }

            this.DrawRestockTrace(restock);
        }

        EUi.Spacing();

        // --- 作らせる ---
        var craft = this.plugin.CraftRunner;

        if (craft.IsRunning)
        {
            EUi.WrapColored(
                $"製作中: {craft.StatusDetail}（{craft.StepIndex + 1} / {craft.StepCount} 手順）",
                NoteKind.Warning);

            if (EUi.Button("中止する##stopcraft"))
            {
                craft.Stop("ユーザー操作");
            }
        }
        else
        {
            var steps = CraftRunner.BuildSteps(plan);
            var blocked = plan.Materials.Any(x => x.Shortfall > 0 && !x.IsIntermediate);

            if (EUi.Button(
                "この計画で作らせる##startcraft",
                disabled: steps.Count == 0 || blocked || !this.plugin.Artisan.IsLoaded))
            {
                if (!craft.Start(plan, out var craftFailure))
                {
                    this.plugin.AnomalyLog.Warn("Craft", craftFailure);
                }
            }

            // 手順の一覧は長い。以前は SameLine で横に付けていたが、
            // 折り返すと行末が決まらないので独立した行にする。
            if (!this.plugin.Artisan.IsLoaded)
            {
                EUi.WrapColored("Artisan が導入されていません", NoteKind.Danger);
            }
            else if (blocked)
            {
                EUi.WrapColored("素材が足りません。先に取り出してください", NoteKind.Warning);
            }
            else
            {
                EUi.MutedParagraph(
                    $"{steps.Count} 手順: {string.Join(" → ", steps.Select(x => $"{x.Name}×{x.Crafts}回"))}");
            }

            if (!string.IsNullOrEmpty(craft.LastFailure))
            {
                EUi.WrapColored($"  {craft.LastFailure}", NoteKind.Danger);
            }
            else if (!string.IsNullOrEmpty(craft.StatusDetail))
            {
                EUi.MutedParagraph($"  前回: {craft.StatusDetail}");
            }
        }

        if (craft.Trace.Count > 0)
        {
            using (EUi.Scroll("##crafttrace", 90f))
            {
                foreach (var line in craft.Trace)
                {
                    EUi.Label(line);
                }
            }
        }

        EUi.Spacing();

        // --- リテイナーの持ち物を覚えているか ---
        //
        // 覚えていれば、当たりのリテイナーへ直接行ける。
        // 覚えていないと総当たりになるため、その旨をここで知らせる。
        var inventory = this.plugin.RetainerInventory;

        if (inventory.IsUsable(out var inventoryReason))
        {
            EUi.WrapColored(
                $"リテイナーの持ち物: {inventory.Count} 人分を覚えています" +
                (inventory.OldestSeenAt is { } oldest ? $"（最も古い記録 {oldest:MM/dd HH:mm}）" : string.Empty),
                NoteKind.Success);

            using (EUi.HStack())
            {
                EUi.Muted("  持っていないと分かっている相手は開きません。");

                // 手で出し入れすると記録とずれる。ずれたときに覚え直させる手段を置く。
                if (EUi.SmallButton("覚えた持ち物を忘れる##forgetretainer")
                    .Tip("記録を消すと、次の取り出しで全員を順に開いて覚え直します。\n" +
                         "手で出し入れして記録とずれたときに使ってください。"))
                {
                    inventory.Clear();
                    this.plugin.AnomalyLog.Info("Retainer", "リテイナーの持ち物の記録を消しました");
                }
            }
        }
        else
        {
            EUi.WrapColored($"リテイナーの持ち物: {inventoryReason}", NoteKind.Warning);
            EUi.MutedParagraph("  呼び鈴からリテイナーを開くと自動で覚えます。覚えるまでは全員を順に開いて探します");
        }

        EUi.Spacing();

        using (EUi.HStack())
        {
            EUi.Heading("要る素材");
            EUi.Muted("（素材名をクリックするとコピーします）");
        }

        EUi.TableHeader(MaterialColumns);

        var row = 0;

        foreach (var material in plan.Materials)
        {
            using (EUi.TableRow(MaterialColumns, row++))
            {
                // 名前と印を 1 つのセルへ入れる。Cell が列を 1 つだけ消費するので、
                // 中に何個置いても以降の列はずれない。
                using (EUi.Cell())
                {
                    DrawCopyableName(material.Name, $"##planmat{material.ItemId}");

                    if (material.IsIntermediate)
                    {
                        EUi.TextColored("（作れる）", NoteKind.Warning);
                    }

                    // クリスタルは鞄ではなく専用の入れ物に入る。枠を使わないことを示す。
                    if (material.IsCrystal)
                    {
                        EUi.TextColored("（クリスタル）", NoteKind.Info);
                    }
                }

                EUi.TableCell(material.PerCraft.ToString());
                EUi.TableCell(material.Needed.ToString());
                EUi.TableCell(material.Held.ToString());

                EUi.TableCell(
                    material.Shortfall > 0 ? material.Shortfall.ToString() : "足りています",
                    color: EUi.NoteColor(material.Shortfall > 0 ? NoteKind.Warning : NoteKind.Success));

                DrawRetainerHolding(inventory, material.ItemId, material.Shortfall);

                EUi.TableCell(
                    material.IsCrystal ? "枠なし" : material.NewSlots.ToString(),
                    color: material.IsCrystal ? EUi.Colors.TextMuted : null);
            }

            // 作れる素材が足りないなら、その素材も出す。
            // リテイナーに完成品が無いときはこちらを取り出すことになる。
            foreach (var sub in material.SubMaterials)
            {
                using (EUi.TableRow(MaterialColumns, row++))
                {
                    using (EUi.Cell())
                    {
                        DrawCopyableName($"    └ {sub.Name}", $"##plansub{material.ItemId}_{sub.ItemId}", sub.Name);
                    }

                    EUi.TableCell(sub.PerCraft.ToString(), color: EUi.Colors.TextMuted);
                    EUi.TableCell(sub.Needed.ToString(), color: EUi.Colors.TextMuted);
                    EUi.TableCell(sub.Held.ToString(), color: EUi.Colors.TextMuted);

                    EUi.TableCell(
                        sub.Shortfall > 0 ? sub.Shortfall.ToString() : "足りています",
                        color: EUi.NoteColor(sub.Shortfall > 0 ? NoteKind.Warning : NoteKind.Success));

                    DrawRetainerHolding(inventory, sub.ItemId, sub.Shortfall);

                    EUi.TableCell("-", color: EUi.Colors.TextMuted);
                }
            }
        }
    }

    /// <summary>
    /// 素材の名前を、押せば写せる形で出す。
    ///
    /// 足りない素材をどこで手に入れるかは、たいてい外部の一覧で調べることになる。
    /// 名前を打ち直さずに済むようにしておく。
    ///
    /// 表の中から呼ぶ。右に印を添えることがあるので、<b>呼び出し側が
    /// <see cref="EUi.Cell"/> で囲む。</b>ここでは幅を取らず、文字の幅だけ使う。
    /// </summary>
    /// <param name="label">画面に出す文字。段差の記号を含むことがある。</param>
    /// <param name="id">ウィジェットの識別子。行ごとに変える。</param>
    /// <param name="copyText">写す文字。省くと <paramref name="label"/> をそのまま写す。</param>
    private static void DrawCopyableName(string label, string id, string? copyText = null)
    {
        var text = copyText ?? label;

        if (EUi.Selectable($"{label}{id}", false).Tip($"クリックで「{text}」をコピー"))
        {
            EUi.SetClipboard(text);
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

        EUi.Spacing();

        using (EUi.HStack())
        {
            EUi.Muted($"取り出しの記録（{restock.Trace.Count} 行）");

            if (EUi.SmallButton("コピー##copytrace"))
            {
                EUi.SetClipboard(string.Join(Environment.NewLine, restock.Trace));
            }
        }

        using (EUi.Scroll("##restocktrace", 120f))
        {
            foreach (var line in restock.Trace)
            {
                EUi.Label(line);
            }
        }
    }

    /// <summary>
    /// リテイナーが持っている数を出す。
    ///
    /// 覚えていない場合は「不明」と出す。取り出す前に、足りるかどうかが分かる。
    ///
    /// 表の中から呼ぶ。<b>セルをちょうど 1 つ消費する。</b>
    /// </summary>
    private static void DrawRetainerHolding(RetainerInventoryStore inventory, uint itemId, int shortfall)
    {
        if (!inventory.IsUsable(out _))
        {
            EUi.TableCell("不明", color: EUi.Colors.TextMuted);
            return;
        }

        var holders = inventory.WhoHas(itemId);
        var total = holders.Sum(x => x.Quantity);

        if (total == 0)
        {
            EUi.TableCell(
                "持っていません",
                color: shortfall > 0 ? EUi.NoteColor(NoteKind.Danger) : EUi.Colors.TextMuted);
            return;
        }

        EUi.TableCell(
            $"{total}（{holders.Count} 人）",
            color: EUi.NoteColor(shortfall > 0 && total < shortfall ? NoteKind.Danger : NoteKind.Success))
            .Tip(string.Join(
                Environment.NewLine,
                holders.Select(x => $"{x.Name}: {x.Quantity}（{x.SeenAt:MM/dd HH:mm}）")));
    }
}
