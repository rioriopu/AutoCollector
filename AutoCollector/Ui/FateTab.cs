using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Game;
using ECommons.Configuration;
using ECommons.DalamudServices;
using EstellUtils.UI;

namespace AutoCollector.Ui;

/// <summary>
/// FATE 自動周回の設定と操作。
///
/// 周回するマップは拡張単位でも選べるようにしているが、
/// <b>保存はマップ単位で行う</b>（Config.FateZones）。
/// 拡張 ID で保存すると、パッチでマップが追加されたときに
/// ユーザーが選んだ覚えのないマップで周回が始まってしまう。
///
/// <b>EstellUtils へ移し終えたタブ。</b><c>RawTabScope</c> で囲まない。
/// </summary>
public sealed class FateTab(Plugin plugin)
{
    private readonly Plugin plugin = plugin;

    private string? lastStartFailure;

    public void Draw()
    {
        var cfg = Plugin.C;
        var runner = this.plugin.FateRunner;

        this.DrawHeadline(cfg, runner);
        EUi.Separator();

        this.DrawZoneSelection(cfg);
        EUi.Separator();

        DrawConditions(cfg);
        EUi.Separator();

        DrawCombat(cfg);
        EUi.Separator();

        DrawBuddy(cfg);
        EUi.Separator();

        this.DrawMisc(cfg);

        this.DrawTestTools();
    }

    // ---- いまの状態と操作 ----

    private void DrawHeadline(Config cfg, FateRunner runner)
    {
        var running = runner.IsRunning;

        if (running)
        {
            EUi.TextColored($"● {StepLabel(runner.Step)}", NoteKind.Success);
            EUi.Paragraph(runner.StatusDetail);
            EUi.Label($"完了した FATE: {runner.Completed} 件");

            if (EUi.Button("止める"))
            {
                runner.Stop("画面から停止");
            }
        }
        else
        {
            var ready = this.DescribeReadiness(cfg, out var kind);
            EUi.WrapColored(ready, kind);

            if (runner.StoppedReason is { Length: > 0 } reason)
            {
                EUi.MutedParagraph($"前回: {reason}（完了 {runner.Completed} 件）");
            }

            using (EUi.HStack(wrap: true))
            {
                if (EUi.Button("周回を始める"))
                {
                    this.lastStartFailure = runner.Start(out var why) ? null : why;
                }

                if (this.lastStartFailure is { Length: > 0 } failure)
                {
                    EUi.TextColored(failure, NoteKind.Danger);
                }
            }
        }
    }

    private string DescribeReadiness(Config cfg, out NoteKind kind)
    {
        if (!this.plugin.BossMod.IsLoaded)
        {
            kind = NoteKind.Danger;
            return "● BossMod Reborn が導入されていません";
        }

        if (!this.plugin.Vnavmesh.IsLoaded)
        {
            kind = NoteKind.Danger;
            return "● vnavmesh が導入されていません";
        }

        if (cfg.FateZones.Count == 0)
        {
            kind = NoteKind.Warning;
            return "● 周回するマップを選んでください";
        }

        kind = NoteKind.Info;
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
        using (EUi.HStack())
        {
            EUi.Heading("周回するマップ");
            EUi.Muted($"（{cfg.FateZones.Count} 件を選択中）");
        }

        var here = Svc.ClientState.TerritoryType;

        // **実際に回る順を出す。**
        // 画面のチェックの並びと巡回順が同じであることを、目で確かめられるようにする。
        if (cfg.FateZones.Count > 0)
        {
            var route = string.Join(
                " → ",
                cfg.FateZones.Select(id => this.plugin.FateZoneCatalog.NameOf(id)));

            EUi.MutedParagraph($"  巡回順: {route} → （先頭へ戻る）");

            // **周回中の変更は、次に始めるときから効く。**
            //
            // 回る順は開始した時点で固定される。そうしないと、
            // 周回中にチェックを外したときに添字がずれてマップを飛ばす。
            // ただし黙って効かないのは分からないので、ここで伝える。
            var running = this.plugin.FateRunner.RunningRoute;

            if (running.Count > 0 && !running.SequenceEqual(cfg.FateZones))
            {
                var current = string.Join(
                    " → ",
                    running.Select(id => this.plugin.FateZoneCatalog.NameOf(id)));

                EUi.WrapColored($"  いま回っているのは: {current}", NoteKind.Warning);
                EUi.MutedParagraph("  （周回中の変更は、次に開始したときから効きます）");
            }
        }

        foreach (var ex in this.plugin.FateZoneCatalog.ListExpansions())
        {
            var selectedInEx = ex.Zones.Count(z => cfg.FateZones.Contains(z.TerritoryId));
            var allSelected = selectedInEx == ex.Zones.Count && ex.Zones.Count > 0;

            var label = FateZoneCatalog.YieldsBicolorGems(ex.ExVersionId)
                ? $"{ex.Name}（{selectedInEx}/{ex.Zones.Count}・バイカラージェム）"
                : $"{ex.Name}（{selectedInEx}/{ex.Zones.Count}）";

            // 見出しは件数で変わるので、開閉を覚える id は別に渡す。
            using var node = EUi.Section(label, defaultOpen: false, id: $"fate_ex_{ex.ExVersionId}");
            if (!node.IsVisible)
            {
                continue;
            }

            // 拡張ごとの一括切り替え。押した時点の配下だけを対象にする。
            var toggleAll = allSelected;
            if (EUi.Checkbox($"この拡張をまとめて選ぶ##fate_all_{ex.ExVersionId}", ref toggleAll))
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

            using (EUi.Indent())
            {
                foreach (var z in ex.Zones)
                {
                    var chosen = cfg.FateZones.Contains(z.TerritoryId);
                    var name = z.TerritoryId == here ? $"{z.Name}（いまここ）" : z.Name;

                    if (EUi.Checkbox($"{name}##fate_zone_{z.TerritoryId}", ref chosen))
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
            }
        }

        var nearest = cfg.FateNearestFirst;
        if (EUi.Checkbox("最寄りの FATE を最優先で狙う", ref nearest)
            .Tip("距離だけで決めます。近いものから順に潰していく遊び方向けです。\n"
                 + "\n"
                 + "ボーナス・達成度・残り時間では割り込みません。\n"
                 + "仲間と同じ FATE を狙う設定より、こちらが優先されます。\n"
                 + "\n"
                 + "切ると ボーナス → 達成度 → 残り時間 → 距離 の順で選びます。\n"
                 + "距離はプレイヤーと FATE 中心の水平距離です。"))
        {
            cfg.FateNearestFirst = nearest;
            EzConfig.Save();
        }

        var follow = cfg.FateFollowParty;
        if (EUi.Checkbox("パーティの仲間と同じ FATE を狙う", ref follow)
            .Tip("仲間が入っている FATE が候補にあれば、そちらを選びます。\n"
                 + "\n"
                 + "同期はしません。仲間の居場所を見て選ぶだけなので、\n"
                 + "相手がこのプラグインを使っている必要はありません。\n"
                 + "\n"
                 + "条件（残り時間・達成度など）は曲げません。\n"
                 + "仲間の FATE が条件から外れていれば、ふつうに選び直します。"))
        {
            cfg.FateFollowParty = follow;
            EzConfig.Save();
        }

        var swap = cfg.FateSwapZoneWhenEmpty;
        if (EUi.Checkbox("FATE が無ければ次のマップへ移る", ref swap))
        {
            cfg.FateSwapZoneWhenEmpty = swap;
            EzConfig.Save();
        }

        if (cfg.FateSwapZoneWhenEmpty)
        {
            using (EUi.Indent())
            {
                var wait = cfg.FateZoneSwapWaitSeconds;
                if (EUi.InputInt("移るまでに待つ秒数##fatezoneswapwait", ref wait, min: 0, max: 600, width: 120f))
                {
                    // **下限は 0。**待たずに次のマップへ移れるようにする。
                    // 以前は 5 秒を下回れなかったため、FATE が無いマップで
                    // 必ず 5 秒以上立ち止まっていた。
                    cfg.FateZoneSwapWaitSeconds = Math.Clamp(wait, 0, 600);
                    EzConfig.Save();
                }

                EUi.MutedParagraph("  0 にすると、FATE が無いと分かった時点ですぐ次のマップへ移ります");
            }
        }
    }

    // ---- 狙う FATE の条件 ----

    private static void DrawConditions(Config cfg)
    {
        EUi.Heading("狙う FATE の条件");

        var minTime = cfg.FateMinTimeRemainingSec;
        if (EUi.InputInt("残り時間がこれ未満なら狙わない（秒）##fatemintime", ref minTime, min: 0, max: 1800, width: 120f))
        {
            cfg.FateMinTimeRemainingSec = Math.Clamp(minTime, 0, 1800);
            EzConfig.Save();
        }

        var maxProgress = cfg.FateMaxProgressPct;
        if (EUi.InputInt("達成度がこれを超えたら狙わない（%）##fatemaxprogress", ref maxProgress, min: 0, max: 100, width: 120f))
        {
            cfg.FateMaxProgressPct = Math.Clamp(maxProgress, 0, 100);
            EzConfig.Save();
        }

        var collect = cfg.FateCollectEnabled;
        if (EUi.Checkbox("納品 FATE も回す", ref collect)
            .Tip("納品 FATE は達成度 100% の時点では報酬が入っていません。\n"
                 + "1 分後に FATE が消えるときに入るため、それまで同じマップに留まります。\n"
                 + "（円から出て次の FATE を回すことはできます）"))
        {
            cfg.FateCollectEnabled = collect;
            EzConfig.Save();
        }

        var levelFilter = cfg.FateLevelFilterEnabled;
        if (EUi.Checkbox("レベル差で絞る", ref levelFilter)
            .Tip("既定では絞りません。\n"
                 + "レベルシンクが働くため、高レベルでも低レベルの FATE を完了できます。\n"
                 + "絞ると、選んだマップの FATE が一つも対象にならないことがあります。"))
        {
            cfg.FateLevelFilterEnabled = levelFilter;
            EzConfig.Save();
        }

        if (cfg.FateLevelFilterEnabled)
        {
            using (EUi.Indent())
            {
                var below = cfg.FateMaxLevelBelow;
                if (EUi.InputInt("自分より下に許す差##fatelvbelow", ref below, min: 0, max: 100, width: 100f))
                {
                    cfg.FateMaxLevelBelow = Math.Clamp(below, 0, 100);
                    EzConfig.Save();
                }

                var above = cfg.FateMaxLevelAbove;
                if (EUi.InputInt("自分より上に許す差##fatelvabove", ref above, min: 0, max: 100, width: 100f))
                {
                    cfg.FateMaxLevelAbove = Math.Clamp(above, 0, 100);
                    EzConfig.Save();
                }
            }
        }
    }

    // ---- 戦闘と離脱 ----

    private static void DrawCombat(Config cfg)
    {
        EUi.Heading("戦闘と離脱");

        EUi.MutedParagraph("空欄のままで構いません。周回を始めるときに、こちらで専用のプリセットを");
        EUi.MutedParagraph($"BossMod Reborn へ用意します（「{FateCombatPreset.Name}」）。");

        var preset = cfg.FateCombatPreset;
        if (EUi.TextInput("使うプリセット名（空欄 = 自動）##fatecombatpreset", ref preset, maxLength: 128, width: 240f)
            .Tip("空欄なら、こちらで作ったプリセットを使います。設定はこうなっています:\n"
                 + "\n"
                 + "  ・FATE の中のモンスターは自分から攻撃しに行く\n"
                 + "  ・FATE 以外のモンスターには自分から絡まない\n"
                 + "  ・ただし攻撃を受けたら殴り返す\n"
                 + "\n"
                 + "すでに同じ名前のプリセットがあれば作り直しません。\n"
                 + "中身を変えたいときは BossMod Reborn 側で編集してください。")
            .Changed)
        {
            cfg.FateCombatPreset = preset;
            EzConfig.Save();
        }

        var prefetch = cfg.FatePrefetchPct;
        if (EUi.InputInt("次の FATE を決め始める達成度（%）##fateprefetch", ref prefetch, min: 0, max: 100, width: 120f)
            .Tip("達成度がこれを超えたら、次に向かう FATE を先に決めておきます。\n"
                 + "100% を見てから探し始めると、その間その場に立ち尽くすことになります。"))
        {
            cfg.FatePrefetchPct = Math.Clamp(prefetch, 0, 100);
            EzConfig.Save();
        }
    }

    // ---- バディ ----

    private static void DrawBuddy(Config cfg)
    {
        EUi.Heading("バディ（チョコボ）");

        var enabled = cfg.FateBuddyEnabled;
        if (EUi.Checkbox("自動で呼び出す", ref enabled))
        {
            cfg.FateBuddyEnabled = enabled;
            EzConfig.Save();
        }

        if (!cfg.FateBuddyEnabled)
        {
            return;
        }

        // 以前はここから先の 3 つの出口それぞれで Unindent を呼んでいた。
        // using なら、どこから返っても閉じる。
        using var indent = EUi.Indent();

        // 持っていない人がいる。持っていなければ呼び出しは行わない。
        if (!BuddyService.HasBuddy)
        {
            EUi.WrapColored(
                "バディを持っていないため、呼び出しは行いません（周回はそのまま続きます）",
                NoteKind.Warning);
            return;
        }

        if (BuddyService.IsStabled)
        {
            EUi.WrapColored("バディを厩舎に預けているため、呼び出しは行いません", NoteKind.Warning);
            return;
        }

        var left = (int)BuddyService.TimeLeftSeconds;
        var greens = BuddyService.GreensCount;
        EUi.MutedParagraph($"いまの残り {left / 60}分{left % 60:00}秒 ／ ギサールの野菜 {greens} 個");

        var minSec = cfg.FateBuddyMinSecondsRemaining;
        if (EUi.InputInt("残りがこれを切ったら呼び直す（秒）##fatebuddymin", ref minSec, min: 0, max: 3600, width: 120f))
        {
            cfg.FateBuddyMinSecondsRemaining = Math.Clamp(minSec, 0, 3600);
            EzConfig.Save();
        }

        var minGreens = cfg.FateGysahlMinCount;
        if (EUi.InputInt("野菜がこれを切ったら買う##fategysahlmin", ref minGreens, min: 0, max: 999, width: 120f))
        {
            cfg.FateGysahlMinCount = Math.Clamp(minGreens, 0, 999);
            EzConfig.Save();
        }

        EUi.MutedParagraph("買い出しは未実装です。いまは残量の表示と、在庫があるときの呼び出しだけを行います。");
    }

    // ---- その他 ----

    private void DrawMisc(Config cfg)
    {
        EUi.Heading("その他");

        var deathIndex = (int)cfg.FateDeathAction;
        if (EUi.Combo("戦闘不能になったら##fatedeath", ref deathIndex, DeathActionNames, width: 220f))
        {
            cfg.FateDeathAction = (FateDeathAction)deathIndex;
            EzConfig.Save();
        }

        if (cfg.FateDeathAction != FateDeathAction.Return)
        {
            using (EUi.Indent())
            {
                var wait = cfg.FateRaiseWaitSeconds;
                if (EUi.InputInt("レイズを待つ秒数##fateraisewait", ref wait, min: 0, max: 600, width: 120f))
                {
                    cfg.FateRaiseWaitSeconds = Math.Clamp(wait, 0, 600);
                    EzConfig.Save();
                }
            }
        }

        var exactSpot = cfg.FateReturnToExactSpot;
        if (EUi.Checkbox("交換のあと、離れた座標まで戻る", ref exactSpot)
            .Tip("交換から戻ったあと、離れたときの座標まで移動します。\n"
                 + "\n"
                 + "入れなくてもエリアには必ず戻り、いちばん近い FATE から回り直します。\n"
                 + "そのため、ふだんは入れなくて構いません。"))
        {
            cfg.FateReturnToExactSpot = exactSpot;
            EzConfig.Save();
        }

        EUi.Spacing();
        EUi.Muted("ベンチャー回収");

        using (EUi.Indent())
        {
            this.DrawVentureSettings(cfg);
        }

        EUi.Spacing();
        EUi.Muted("交換との関係");

        using (EUi.Indent())
        {
            EUi.Paragraph(
                "バイカラージェムなどが設定した量に届くと、周回を中断して交換所へ向かい、"
                + "交換が済んだら元のエリアへ戻って周回を再開します。");
            EUi.Paragraph(
                "中断するのは FATE の切れ目です。戦っている最中や、"
                + "納品 FATE の報酬を待っている間は中断しません。");
        }
    }

    /// <summary>
    /// ベンチャー回収の設定。
    ///
    /// <b>行き先はアクセス済みの一覧から選ばせる。</b>
    /// エーテライトの ID を直接入れさせると、アクセスしていない街も選べてしまい、
    /// 実行時に「飛べません」で止まる。一覧から選べた時点で飛べる。
    /// </summary>
    private void DrawVentureSettings(Config cfg)
    {
        var enabled = cfg.FateVentureCollectEnabled;
        if (EUi.Checkbox("ベンチャーが回収できたら街へ戻って回収する", ref enabled)
            .Tip("周回を始めるとベンチャーを見張り、回収できるようになったら\n"
                 + "街へ戻って回収し、元のマップへ戻って周回を続けます。\n"
                 + "\n"
                 + "中断するのは FATE の切れ目です。戦っている最中や、\n"
                 + "納品している間は中断しません。\n"
                 + "\n"
                 + "周回を止めると、見張りも止まります。\n"
                 + "回収そのものは AutoRetainer が行います。"))
        {
            cfg.FateVentureCollectEnabled = enabled;
            EzConfig.Save();
        }

        // **切ってあることを画面に出す。**
        //
        // 黙って既定を変えると、入れていた人には「設定が消えた」に見える。
        // なぜ切ってあるのかと、戻せることを書いておく。
        if (!enabled)
        {
            EUi.WrapColored("  いまは切ってあります（周回そのものが安定するまでの措置です）", NoteKind.Warning);
            EUi.MutedParagraph("  周回の開始を奪う不具合と、街で棒立ちになる不具合が実機で出たため。上の印で戻せます");
        }

        if (!cfg.FateVentureCollectEnabled)
        {
            return;
        }

        var towns = this.plugin.HomeTownService;

        if (!towns.IsListReady())
        {
            // **一覧が空でも「未アクセス」と決めつけない。**
            // コンテンツの中では空になる。
            EUi.MutedParagraph("エーテライトの一覧を読めません（コンテンツの中かもしれません）");
            return;
        }

        var list = towns.List();

        if (list.Count == 0)
        {
            EUi.WrapColored("回収に行ける街がありません。エーテライトを解放してください", NoteKind.Warning);
            return;
        }

        var home = towns.HomeTerritory();
        var current = cfg.FateVentureTownTerritory;

        // 先頭に「（未設定）」を置く。まだ選んでいない状態を表す行が要る。
        var names = new List<string>(list.Count + 1) { "（未設定）" };

        foreach (var town in list)
        {
            names.Add(home != 0 && town.TerritoryId == home
                ? $"{town.Name}（ホームタウン・デジョン）"
                : town.Name);
        }

        var picked = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].TerritoryId == current)
            {
                picked = i;
                break;
            }
        }

        var comboIndex = picked < 0 ? 0 : picked + 1;

        if (EUi.Combo("回収に行く街##fateventuretown", ref comboIndex, names.ToArray(), width: 220f))
        {
            cfg.FateVentureTownTerritory = comboIndex == 0 ? 0 : list[comboIndex - 1].TerritoryId;
            EzConfig.Save();
        }

        // どちらで行くかを見せる。料金がかかるかが分かる。
        if (current != 0)
        {
            var method = towns.ChooseMethod(current);

            var describe = method switch
            {
                HomeTownService.TravelMethod.Return => "デジョンで行きます（ホームタウンと同じなので無料）",
                HomeTownService.TravelMethod.Teleport => "テレポで行きます（ホームタウンと違うので料金がかかります）",
                _ => "いまその街にいます",
            };

            EUi.MutedParagraph($"  {describe}");
        }

        if (home == 0)
        {
            EUi.MutedParagraph("  ホームタウンを読めないため、テレポで行きます");
        }
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
        EUi.Spacing();

        using var node = EUi.Section("動作確認用", defaultOpen: false, id: "fatetesttools");
        if (!node.IsVisible)
        {
            return;
        }

        var pretend = this.plugin.FateScanner.PretendEmpty;
        if (EUi.Checkbox("FATE が見つからないことにする", ref pretend)
            .Tip("このマップに狙える FATE が 1 つも無い、と思い込ませます。\n"
                 + "次のマップへテレポするかを確かめるために使います。\n"
                 + "\n"
                 + "いま参加している FATE には効きません。\n"
                 + "戦っている最中に切ると、後始末を通らずに離脱してしまうためです。\n"
                 + "\n"
                 + "設定には保存しません。読み込み直すと戻ります。"))
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

        if (pretend)
        {
            EUi.WrapColored("  FATE を探さない状態です。確認が済んだら外してください", NoteKind.Warning);
        }

        EUi.Spacing();

        // **詰まったときの逃げ道を、手元にも置く。**
        // 周回が止まっている間は自動の脱出が働かないため、
        // 入り組んだ場所に取り残されたときに自力で戻れるようにする。
        if (EUi.Button("いまの場所から脱出する")
            .Tip("地形に挟まって動けなくなったときに押してください。\n"
                 + "\n"
                 + "まず移動を止め、立てる場所を探して飛びます。\n"
                 + "それでも動けなければ、帰還してホームポイントへ戻ります。\n"
                 + "\n"
                 + "周回中でなくても使えます。"))
        {
            this.plugin.FateRunner.EscapeNow();
        }
    }

    private static readonly string[] DeathActionNames =
    [
        "レイズを待つ（時間切れで街へ戻る）",
        "すぐ街へ戻る",
        "ソロなら戻る・パーティなら待つ",
    ];
}
