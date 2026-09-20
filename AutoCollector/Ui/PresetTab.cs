using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Automation;
using AutoCollector.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.Configuration;
using ECommons.GameHelpers;

namespace AutoCollector.Ui;

/// <summary>
/// 交換プリセットの設定。
///
/// 通貨はスロット番号で保持するため、パッチでトームストーンが入れ替わっても
/// 設定を作り直す必要がない。
/// </summary>
public sealed class PresetTab(Plugin plugin)
{
    /// <summary>製作するジョブを選んでいないときの表示。</summary>
    private const string UnsetJobLabel = "--選択して下さい--";

    /// <summary>コピーしたことを知らせておく時間。短すぎると気づけない。</summary>
    private static readonly TimeSpan CopyNoticeDuration = TimeSpan.FromSeconds(4);

    private static readonly string[] ThresholdModeNames = ["固定値", "上限に対する割合", "上限までの残り"];

    private static readonly string[] ExchangeModeNames =
    [
        "交換できる限り",
        "指定量の通貨を残すまで",
        "指定回数だけ",
        "目標所持数に達するまで",
    ];

    private readonly Plugin plugin = plugin;

    /// <summary>素材を開いて見せている中間素材。ItemId で覚える。</summary>
    private readonly HashSet<uint> expandedShortages = [];

    private string rewardSearch = string.Empty;

    /// <summary>直前に写した名前。押した手応えを画面へ返すために持つ。</summary>
    private string copiedName = string.Empty;

    /// <summary>写した時刻。しばらく経ったら知らせを消す。</summary>
    private DateTime copiedAtUtc = DateTime.MinValue;

    /// <summary>この画面で知らせを出したか。同じ行が 2 つ並ぶのを防ぐ。</summary>
    private bool copyNoticeDrawn;

    /// <summary>開始・停止を押した結果。押しても無反応に見えないよう画面へ返す。</summary>
    private string runControlNote = string.Empty;

    /// <summary>その結果が、どのプリセットのものか。別のプリセットへ持ち越さない。</summary>
    private Guid runControlPresetId = Guid.Empty;
    private Guid editingPresetId = Guid.Empty;

    /// <summary>選んでいる系統。ゲーム内の交換画面と同じ並び。</summary>
    private int categoryIndex;

    /// <summary>選んでいる種別。</summary>
    private int seriesIndex;

    /// <summary>名前で探した結果。入力が変わるまで使い回す。</summary>
    private IReadOnlyList<(InclusionCategory Category, InclusionSeries Series, InclusionOffer Offer)>? searchResults;

    /// <summary>逆算した結果の控え。計算は重いので 1 秒は使い回す。</summary>
    private ScripGoal? goalCache;
    private Guid goalCachePresetId;
    private DateTime goalCacheUntilUtc;

    /// <summary>いま打ち込み中の数値欄と、その文字列。打ち込みを邪魔しないために持つ。</summary>
    private string numberEditKey = string.Empty;
    private string numberEditText = string.Empty;

    /// <summary>
    /// 数値の入力欄。**全角数字を半角に直してから読む。**
    ///
    /// 日本語入力のまま打つと「５００」のような全角数字が入る。
    /// <c>InputInt</c> はこれを数として読めず、打ったのに値が変わらない。
    /// 打ち間違いにしか見えないので、こちらで直す。
    ///
    /// 数字以外は捨てる。打っている途中の空欄は 0 として扱わず、そのまま残す。
    /// 0 にしてしまうと、消して打ち直すことができなくなる。
    /// </summary>
    private bool DrawNumber(string key, string label, ref int value, float width = 160f)
    {
        var editing = this.numberEditKey == key;
        var text = editing ? this.numberEditText : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        ImGui.SetNextItemWidth(width);
        var edited = ImGui.InputText(label, ref text, 12);

        if (ImGui.IsItemActive())
        {
            this.numberEditKey = key;
        }
        else if (editing)
        {
            // 離れたら控えを捨てる。次に開いたときは実際の値から始める。
            this.numberEditKey = string.Empty;
            this.numberEditText = string.Empty;
        }

        if (!edited)
        {
            return false;
        }

        var normalized = NormalizeDigits(text);
        this.numberEditText = normalized;

        if (normalized.Length == 0)
        {
            // 全部消しただけ。値はまだ変えない。
            return false;
        }

        if (!int.TryParse(normalized, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed == value)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>全角数字を半角に直し、数字以外を捨てる。</summary>
    private static string NormalizeDigits(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            // 全角の ０〜９ は U+FF10〜U+FF19。半角との差はちょうど 0xFEE0。
            var normalized = c >= '０' && c <= '９' ? (char)(c - 0xFEE0) : c;

            if (normalized is >= '0' and <= '9')
            {
                builder.Append(normalized);
            }
        }

        // 桁が多すぎると int に収まらない。収まる長さで切る。
        return builder.Length > 10 ? builder.ToString(0, 10) : builder.ToString();
    }

    public void Draw(ref bool select)
    {
        var flags = select ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        select = false;

        using var tab = ImRaii.TabItem("プリセット", flags);
        if (!tab)
        {
            return;
        }

        // 写したことの知らせは 1 画面に 1 度だけ。
        // 出す場所が 2 か所あるため、ここで戻さないと同じ行が 2 つ並ぶ。
        this.copyNoticeDrawn = false;

        // 監視は常に動いている。有効なプリセットが閾値へ達したら自動で交換所へ向かう。
        ImGui.TextColored(ImGuiColors.DalamudGrey, "有効なプリセットが閾値に達したら、自動で交換所へ向かいます");

        if (!string.IsNullOrEmpty(this.plugin.MonitorService.LastDecision))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, this.plugin.MonitorService.LastDecision);
        }

        ImGui.Spacing();

        if (ImGui.Button("プリセットを追加"))
        {
            var preset = new ExchangePreset();
            Plugin.C.Presets.Add(preset);
            this.editingPresetId = preset.Id;
            EzConfig.Save();
        }

        ImGui.Separator();

        if (Plugin.C.Presets.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "プリセットがありません。");
            return;
        }

        ExchangePreset? toRemove = null;

        foreach (var preset in Plugin.C.Presets.ToList())
        {
            using var id = ImRaii.PushId(preset.Id.ToString());

            var enabled = preset.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                preset.Enabled = enabled;
                if (enabled)
                {
                    preset.DisabledReason = null;
                }

                EzConfig.Save();
            }

            ImGui.SameLine();

            var label = string.IsNullOrEmpty(preset.Name) ? "（名前なし）" : preset.Name;
            var expanded = this.editingPresetId == preset.Id;
            if (ImGui.Selectable(this.BuildSummary(preset, label), expanded))
            {
                this.editingPresetId = expanded ? Guid.Empty : preset.Id;

                // 別のプリセットを開いたら、探し途中と選び途中は持ち越さない。
                this.categoryIndex = 0;
                this.seriesIndex = 0;
                this.rewardSearch = string.Empty;
                this.searchResults = null;
                this.goalCacheUntilUtc = DateTime.MinValue;
            }

            // 止まっていることは、畳んだままでも分かるようにする。
            var blockedReason = this.plugin.GoalRunner.BlockedReason(preset.Id);

            if (!string.IsNullOrEmpty(blockedReason))
            {
                var retry = this.plugin.GoalRunner.BlockedRetryInSeconds(preset.Id);

                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    retry is null
                        ? $"  止まっています: {blockedReason}"
                        : $"  止まっています: {blockedReason}（{retry} 秒後にもう一度試します）");
            }

            if (!string.IsNullOrEmpty(preset.DisabledReason))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, $"  無効化されています: {preset.DisabledReason}");
            }

            if (this.editingPresetId != preset.Id)
            {
                continue;
            }

            using var indent = ImRaii.PushIndent();
            this.DrawEditor(preset, ref toRemove);
            ImGui.Separator();
        }

        if (toRemove is not null)
        {
            Plugin.C.Presets.Remove(toRemove);
            EzConfig.Save();
        }
    }

    private string BuildSummary(ExchangePreset preset, string label)
    {
        var currency = this.plugin.CurrencyCatalog.TryResolve(preset, out var currencyItemId)
            ? this.plugin.CurrencyCatalog.ListChoices().FirstOrDefault(x => x.ItemId == currencyItemId)?.Name ?? "?"
            : "?";

        var reward = preset.Rewards.Count switch
        {
            0 => "未設定",
            1 => ItemName(preset.Rewards[0].RewardItemId),
            _ => $"{ItemName(preset.Rewards[0].RewardItemId)} ほか {preset.Rewards.Count - 1} 件",
        };

        return $"{label}  [{currency} → {reward}]";
    }

    private void DrawEditor(ExchangePreset preset, ref ExchangePreset? toRemove)
    {
        var changed = false;

        var name = preset.Name;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.InputText("プリセット名", ref name, 64))
        {
            preset.Name = name;
            changed = true;
        }

        // --- 通貨 ---
        var choices = this.plugin.CurrencyCatalog.ListChoices()
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .ToList();

        if (choices.Count > 0)
        {
            var index = CurrencyCatalog.IndexOf(choices, preset);
            if (index < 0)
            {
                index = 0;
            }

            ImGui.SetNextItemWidth(320f);

            // 色を付けたいので、既定の Combo ではなく自前で開く。
            // 既定の Combo は行ごとに色を変えられない。
            using (var combo = ImRaii.Combo("監視する通貨", choices[index].Name))
            {
                if (combo)
                {
                    for (var i = 0; i < choices.Count; i++)
                    {
                        using var id = ImRaii.PushId($"cur{i}");

                        if (ImGui.Selectable("##row", i == index))
                        {
                            CurrencyCatalog.Apply(preset, choices[i]);

                            // 通貨が変われば買えるものも変わる。前の通貨で選んだ品は残さない。
                            preset.Rewards.Clear();
                            preset.RewardItemId = 0;
                            preset.PreferredNpcDataId = 0;

                            // 作る収集品も通貨ごとに違う。橙貨用の物で紫貨は貯まらない。
                            ClearCraftChoice(preset);

                            // 通貨が変われば系統も種別も別物。選び直しにする。
                            // 番号だけ残すと、無関係な系統の品が並んで「何も買えない」と読める。
                            this.categoryIndex = 0;
                            this.seriesIndex = 0;

                            // 前の通貨で探した結果も捨てる。
                            // 残っていると、別通貨の品をそのまま交換リストへ入れられてしまう。
                            this.rewardSearch = string.Empty;
                            this.searchResults = null;

                            changed = true;
                        }

                        ImGui.SameLine(0f, 0f);
                        DrawCurrencyName(choices[i].Name);
                    }
                }
            }

            // 閉じているときは色が付けられないため、選んでいるものを下に色付きで出す。
            ImGui.TextUnformatted("  選択中:");
            ImGui.SameLine(0f, 0f);
            DrawCurrencyName(choices[index].Name);

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "  トームストーンはスロット番号で保存するため、パッチで入れ替わっても自動追従します");
        }

        // --- 交換対象 ---
        this.DrawRewardPicker(preset, ref changed);

        if (this.plugin.CurrencyCatalog.TryResolve(preset, out var pickerCurrency))
        {
            this.DrawNpcPicker(preset, pickerCurrency, ref changed);
        }

        // --- 閾値 ---
        var thresholdMode = (int)preset.Threshold.Mode;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.Combo("交換を始める条件", ref thresholdMode, ThresholdModeNames, ThresholdModeNames.Length))
        {
            preset.Threshold.Mode = (ThresholdMode)thresholdMode;
            changed = true;
        }

        var thresholdValue = preset.Threshold.Value;
        var thresholdLabel = preset.Threshold.Mode switch
        {
            ThresholdMode.Fixed => "この所持数以上で開始",
            ThresholdMode.Percentage => "上限に対する割合 (%)",
            _ => "上限までの残りがこの値以下で開始",
        };

        if (this.DrawNumber($"th{preset.Id}", thresholdLabel, ref thresholdValue))
        {
            preset.Threshold.Value = Math.Max(0, thresholdValue);
            changed = true;
        }

        if (this.plugin.CurrencyCatalog.TryResolve(preset, out var currencyId))
        {
            var trigger = this.plugin.CurrencyService.CalculateTriggerAmount(preset.Threshold, currencyId);
            var current = this.plugin.CurrencyService.GetCountOrZero(currencyId);
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                trigger is null
                    ? $"  現在 {current:N0}（発動条件を計算できません）"
                    : $"  現在 {current:N0} / 発動 {trigger:N0}");
        }

        // --- 交換モード ---
        var exchangeMode = (int)preset.Mode;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.Combo("どこまで交換するか", ref exchangeMode, ExchangeModeNames, ExchangeModeNames.Length))
        {
            preset.Mode = (ExchangeMode)exchangeMode;
            changed = true;
        }

        switch (preset.Mode)
        {
            case ExchangeMode.UntilCurrencyReserve:
            {
                var reserve = preset.CurrencyReserve;
                if (this.DrawNumber($"reserve{preset.Id}", "残す通貨量", ref reserve))
                {
                    preset.CurrencyReserve = Math.Max(0, reserve);
                    changed = true;
                }

                break;
            }

            case ExchangeMode.FixedQuantity:
            {
                var quantity = preset.Quantity;
                if (this.DrawNumber($"qty{preset.Id}", "交換する回数", ref quantity))
                {
                    preset.Quantity = Math.Clamp(quantity, 1, ExchangeSession.HardLimit);
                    changed = true;
                }

                break;
            }

            case ExchangeMode.UntilTargetQuantity:
            {
                var target = preset.Quantity;
                if (this.DrawNumber($"target{preset.Id}", "目標の所持数", ref target))
                {
                    preset.Quantity = Math.Max(1, target);
                    changed = true;
                }

                if (preset.Rewards.Count > 0 &&
                    this.plugin.CurrencyService.TryGetCount(preset.Rewards[0].RewardItemId, out var owned, includeEquipped: true, includeArmory: true))
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"  現在 {owned:N0} / 目標 {preset.Quantity:N0}");
                }

                // **同じ「いくつ持つか」を 2 か所で決めている。**
                //
                // ここの「目標の所持数」と、交換リストの行の「所持の上限」は
                // どちらも同じことを指す。食い違うと厳しいほうが勝つため、
                // 目標 5 と入れても行の上限が 1 なら 1 で止まる。
                //
                // 黙って厳しいほうを使うと、なぜ止まったのか読めない。
                if (preset.Rewards.Any(x => x.OwnedLimit > 0 && x.OwnedLimit != preset.Quantity))
                {
                    ImGui.TextColored(
                        ImGuiColors.DalamudGrey,
                        "  この数が優先されます。交換リストの「所持の上限」は見ません");
                }

                break;
            }

            case ExchangeMode.MaxExchange:
                ImGui.TextColored(ImGuiColors.DalamudYellow, "  通貨か所持枠が尽きるまで交換します");
                break;
        }

        ImGui.Spacing();

        this.DrawRunControls(preset);

        ImGui.Separator();

        if (ImGui.Button("このプリセットを削除"))
        {
            toRemove = preset;
        }

        if (changed)
        {
            EzConfig.Save();
        }
    }

    /// <summary>交換対象を、その通貨で実際に買えるものから選ばせる。</summary>
    /// <summary>
    /// 交換対象を選ぶ。
    ///
    /// ゲーム内のアイテム交換画面と同じ形にしてある。
    /// 系統（装備品／秘伝書・素材・雑貨／マテリア）を選び、
    /// その中の種別（Lv90～向け素材 など）を選んでから品が並ぶ。
    ///
    /// **品は種別を選んだときに初めて読む。**
    /// 数百件を五十音順に並べても探せないうえ、全部を先に読むと開いた瞬間に固まる。
    ///
    /// 名前しか分からない場合のために、横断検索も残してある。
    /// </summary>
    private void DrawRewardPicker(ExchangePreset preset, ref bool changed)
    {
        if (!this.plugin.CurrencyCatalog.TryResolve(preset, out var currencyItemId))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "通貨を解決できないため、交換対象を選べません");
            return;
        }

        this.DrawRewardList(preset, ref changed);
        this.DrawGoalSummary(preset, ref changed);

        var catalog = this.plugin.InclusionShopCatalog;
        // 選んでいる通貨で買えるものだけを出す。
        var categories = catalog.ListCategories(currencyItemId);

        // **「アイテム交換」窓口に載らない通貨がある。**
        //
        // この一覧の元は InclusionShop（スクリップ交換所の画面）。
        // トームストーンは SpecialShop に直接ぶら下がっており、そこには載らない
        // （docs/01 の系統 B と系統 C）。
        //
        // 以前はここで「交換の一覧を作れませんでした」と出していたが、
        // 一覧づくりは成功していて、この通貨に該当する系統が無いだけだった。
        // 失敗と読めるうえ、品を選ぶ手段が無くなっていた。
        if (categories.Count == 0)
        {
            this.DrawSpecialShopRewardPicker(preset, currencyItemId, ref changed);
            return;
        }

        // --- 名前で探す（横断） ---
        ImGui.SetNextItemWidth(280f);
        if (ImGui.InputTextWithHint("##rewardsearch", "アイテム名で探す（系統をまたいで探します）", ref this.rewardSearch, 64))
        {
            // 入力が変わったときだけ探す。毎フレーム全種別を読むと重い。
            this.searchResults = null;
        }

        if (!string.IsNullOrWhiteSpace(this.rewardSearch))
        {
            this.searchResults ??= catalog.Search(this.rewardSearch, currencyItemId);

            // using を宣言のまま置くと、この関数の最後まで枠が閉じない。
            // 枠の外に出したい案内文まで、検索結果の中に描かれてしまう。
            using (var searchChild = ImRaii.Child("##searchlist", new Vector2(0, 150), true))
            {
                if (searchChild)
                {
                    if (this.searchResults.Count == 0)
                    {
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "見つかりませんでした");
                    }

                    foreach (var (category, series, offer) in this.searchResults)
                    {
                        var inList = preset.Rewards.Any(x => x.RewardItemId == offer.RewardItemId);

                        if (ImGui.Selectable($"{offer.RewardName}  （{offer.CurrencyCost:N0}）##s{series.SpecialShopId}_{offer.RewardItemId}", inList))
                        {
                            Toggle(preset, offer.RewardItemId);
                            changed = true;
                        }

                        ImGui.SameLine();

                        // 系統名は長い。後半だけにして、右端が切れないようにする。
                        ImGui.TextColored(
                            ImGuiColors.DalamudGrey,
                            $"  {ShortCategory(category.DisplayName)} / {series.DisplayName}");
                    }
                }
            }

            ImGui.TextColored(ImGuiColors.DalamudGrey, "  検索欄を空にすると、系統から辿る表示に戻ります");
            return;
        }

        // --- 系統 ---
        var categoryNames = categories.Select(x => x.DisplayName).ToArray();
        if (this.categoryIndex >= categoryNames.Length)
        {
            this.categoryIndex = 0;
        }

        ImGui.SetNextItemWidth(360f);
        if (ImGui.Combo("系統", ref this.categoryIndex, categoryNames, categoryNames.Length))
        {
            // 系統が変われば種別の並びも変わる。選び直しになる。
            this.seriesIndex = 0;

            // 系統を選び直すのは、欲しいアイテムを選び直す場面。
            // 表を装備品へ戻すため、製作するジョブは未設定に戻す。
            ClearCraftChoice(preset);
            changed = true;
        }

        // --- 製作するジョブ（系統の右） ---
        ImGui.SameLine();
        this.DrawCraftJobCombo(preset, ref changed);

        // ジョブを選んでいるあいだは、表を製作リストに差し替える。
        if (preset.CraftJob >= 0)
        {
            this.DrawCraftPicker(preset, currencyItemId, ref changed);
            return;
        }

        // --- 種別 ---
        var selectedCategory = categories[this.categoryIndex];
        var seriesNames = selectedCategory.Series.Select(x => x.DisplayName).ToArray();
        if (this.seriesIndex >= seriesNames.Length)
        {
            this.seriesIndex = 0;
        }

        ImGui.SetNextItemWidth(360f);
        ImGui.Combo("種別", ref this.seriesIndex, seriesNames, seriesNames.Length);

        var selectedSeries = selectedCategory.Series[this.seriesIndex];

        // --- 品（ここで初めて読む） ---
        var offers = catalog.ListOffers(selectedSeries.SpecialShopId, currencyItemId);

        if (offers.Count == 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "この種別に、いま選んでいる通貨で買えるものはありません");
            return;
        }

        using var child = ImRaii.Child("##rewardlist", new Vector2(0, 170), true);
        if (!child)
        {
            return;
        }

        foreach (var offer in offers)
        {
            var label = offer.RewardQuantity > 1
                ? $"{offer.RewardName} ×{offer.RewardQuantity}"
                : offer.RewardName;

            var already = preset.Rewards.Any(x => x.RewardItemId == offer.RewardItemId);

            if (ImGui.Selectable($"{label}##r{offer.RewardItemId}", already))
            {
                Toggle(preset, offer.RewardItemId);
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {offer.CurrencyCost:N0}");
        }
    }

    /// <summary>
    /// 周回と交換をここから始める・止める。
    ///
    /// **プリセットを有効にしただけでは AutoDuty は始まらない。**
    /// 自動交換は周回への相乗りとして動く設計で、こちらから周回を起こすことはない
    /// （<c>RequireExternalAutomationRunning</c>）。
    /// そのため「有効にしたのに何も起きない」と見える。始める操作をここに置く。
    ///
    /// 止めるほうも要る。周回の維持はプリセットとは別に動いていたため、
    /// プリセットを無効にしても AutoDuty が回り続けていた。
    /// </summary>
    private void DrawRunControls(ExchangePreset preset)
    {
        ImGui.Separator();

        var keeper = this.plugin.AutoDutyKeeper;
        var autoDuty = this.plugin.AutoDuty;

        // 画面用の問い合わせを使う。判定用のものは読めないたびに警告を書くため、
        // 毎フレーム呼ぶと記録が埋まる。
        var runState = autoDuty.IsRunningForDisplay();
        var running = runState == true;

        // 交換や周回の処理が動いているあいだは押させない。
        // 押せると移動が二重になる。
        var busy = this.plugin.ExchangeExecutor.IsBusy ||
                   this.plugin.GoalRunner.IsRunning ||
                   this.plugin.RetainerRestock.IsRunning ||
                   this.plugin.CraftRunner.IsRunning ||
                   this.plugin.CollectableCycle.IsRunning;

        // コンテンツの中では押させない。
        // AutoDuty へ行き先を渡すと、いま選んでいるコンテンツを書き換えてしまう。
        //
        // **分からないときは押させない。**
        // Player.Available はエリア移動のロード中に false になるが、
        // AutoDuty 側の判定はエリア番号で行うため、その間もコンテンツ扱いになる。
        // Available を AND すると、ロード中だけ判定が緩んで押せてしまう。
        var inDuty = !Player.Available || Player.IsInDuty;

        // 古い AutoDuty では周回を任せない。設定を確かめられないため。
        var setup = this.plugin.AutoDutySetup;
        var needsUpdate = setup.NeedsAutoDutyUpdate;

        var canStart = preset.Enabled && !running && !busy && !inDuty && !needsUpdate;

        // --- 開始 ---
        using (ImRaii.Disabled(!canStart))
        {
            if (ImGui.Button("周回を開始する##start"))
            {
                this.StartLoop(preset);
            }
        }

        ImGui.SameLine();

        // --- 停止 ---
        if (ImGui.Button("止める##stop"))
        {
            this.plugin.EmergencyStop($"「{preset.Name}」から止められました");
            this.runControlPresetId = preset.Id;
            this.runControlNote = "止めました。周回の維持も止めています";
        }

        ImGui.SameLine();

        // --- いまの状態 ---
        if (needsUpdate)
        {
            var installed = setup.InstalledVersion?.ToString() ?? "読み取れません";
            ImGui.TextColored(
                ImGuiColors.DalamudRed,
                $"  AutoDuty の更新が必要です（いま {installed} / 必要 {setup.RequiredVersion}）");
        }
        else if (!preset.Enabled)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  このプリセットが無効です");
        }
        else if (!autoDuty.IsLoaded)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  AutoDuty が導入されていません");
        }
        else if (runState is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  AutoDuty の状態を読み取れません");
        }
        else if (running)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "  周回中です");
        }
        else if (inDuty)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  コンテンツの中では始められません");
        }
        else if (busy)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  ほかの処理が動いています");
        }
        else if (keeper.Suspended)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "  止めています。「周回を開始する」で戻せます");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  止まっています");
        }

        if (this.runControlPresetId == preset.Id && !string.IsNullOrEmpty(this.runControlNote))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {this.runControlNote}");
        }

        if (needsUpdate)
        {
            // 詳しい案内と更新ボタンは状況タブの「はじめに」に置いてある。
            // 同じ案内を 2 か所に出すと、どちらで直すのか分からなくなる。
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "  更新のしかたは 状況タブ の「はじめに」に出しています");

            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  交換は周回に相乗りして行います。周回が動いていないあいだは交換も待機します");
    }

    /// <summary>
    /// 周回を起こす。
    ///
    /// AutoDuty には行き先が要る。どこを回っていたかを覚えていないと始められない。
    /// その場合は利用者に 1 度だけ手で始めてもらう。
    /// </summary>
    private void StartLoop(ExchangePreset preset)
    {
        this.runControlPresetId = preset.Id;
        this.runControlNote = string.Empty;

        var autoDuty = this.plugin.AutoDuty;

        if (!autoDuty.IsLoaded)
        {
            this.runControlNote = "AutoDuty が導入されていません";
            return;
        }

        // 止めた記録が残っていると、始めてもすぐ押し返される。先に戻す。
        // 交換の封鎖も一緒に下ろす。片方だけだと周回だけが回り、交換は弾かれ続ける。
        this.plugin.ResumeAfterStop();

        var territory = Plugin.C.LastDutyTerritoryId;

        if (territory == 0)
        {
            this.runControlNote =
                "前に回っていた場所が分かりません。AutoDuty で 1 度だけ手で始めてください。以後はここから始められます";
            return;
        }

        if (!autoDuty.TryContentHasPath(territory, out var hasPath) || !hasPath)
        {
            this.runControlNote =
                $"{NpcLocationService.GetTerritoryName(territory)} の経路を AutoDuty が持っていません";
            return;
        }

        // loops には必ず 0 を渡す。0 以外は AutoDuty の設定を恒久的に書き換える。
        if (!autoDuty.TryRun(territory))
        {
            this.runControlNote = "AutoDuty へ開始を伝えられませんでした";
            return;
        }

        this.runControlNote = $"{NpcLocationService.GetTerritoryName(territory)} の周回を始めました";
    }

    /// <summary>
    /// 実際に交換できるエントリか。
    ///
    /// 報酬やコストが複数あるものは「通貨が減った AND アイテムが増えた」で
    /// 検証しきれないため、<c>ExchangeExecutor</c> が実行を拒む。
    /// 選ばせてはいけない。
    /// </summary>
    private static bool IsExecutable(ExchangeDefinition definition)
        => definition.SingleReward && definition.SingleCost;

    /// <summary>
    /// 「アイテム交換」窓口に載らない通貨の品を選ぶ。
    ///
    /// トームストーンのように、系統も種別も持たず SpecialShop へ直接ぶら下がる交換がある。
    /// その場合は交換所を走査して、買える品を平らに並べる。
    ///
    /// **走査は押されたときだけ行う。**
    /// ゲームデータ全体を見るため、画面を開いただけで始めると重い。
    /// 一度作れば通貨ごとに覚えるので、次からはすぐ出る。
    ///
    /// **交換の最中に新しく走査を始めない。**
    /// 索引づくりは毎フレーム数千行を読む。交換や周回が動いている横で始めると重くなる。
    /// すでに作ってある通貨は、そのまま出してよい（解決は
    /// <see cref="MonitorService"/> が使う直前に対象を向け直すため、表示で壊れない）。
    /// </summary>
    private void DrawSpecialShopRewardPicker(ExchangePreset preset, uint currencyItemId, ref bool changed)
    {
        // 一覧そのものが空なのか、この通貨に該当が無いだけなのかを分けて出す。
        // 同じ見た目にすると、シートを読めていない不具合を見逃す。
        if (this.plugin.InclusionShopCatalog.ListCategories().Count == 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                "  「アイテム交換」窓口の一覧を作れていません。交換所を直接調べます");
        }
        else
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "  この通貨は「アイテム交換」窓口では扱われないため、交換所を直接調べます");
        }

        var resolver = this.plugin.ExchangeResolver;

        if (!resolver.IsBuiltFor(currencyItemId))
        {
            // 作っている最中なら進み具合を出す。押しても反応が無いように見せない。
            if (resolver.TargetCurrencyItemId == currencyItemId &&
                resolver.Stage is not (ResolverBuildStage.NotStarted or ResolverBuildStage.Failed))
            {
                ImGui.ProgressBar(resolver.BuildProgress, new Vector2(280f, 0f));
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, "交換所を調べています");
                return;
            }

            if (this.plugin.ExchangeExecutor.IsBusy)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    "  いま交換を実行中です。終わってから読み込んでください");
                return;
            }

            if (ImGui.Button("この通貨の交換候補を読み込む"))
            {
                resolver.BeginBuild(currencyItemId);
            }

            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "一度読み込めば、次からはすぐ出ます");
            return;
        }

        // 構築済みの索引をこの通貨へ向ける。同じ通貨なら作り直しは起きない。
        resolver.BeginBuild(currencyItemId);

        ImGui.SetNextItemWidth(280f);
        ImGui.InputTextWithHint("##rewardsearch", "アイテム名で絞り込み", ref this.rewardSearch, 64);

        // **撃てないものを一覧に出さない。**
        //
        // 索引には報酬やコストが複数あるエントリも入っている。
        // ExchangeResolver は「定義としては残し、実行の事前条件で拒む」方針のため、
        // 絞らずに出すとここだけが実行できない品を選択肢として並べることになる。
        //
        // 選ばれると、テレポートも移動も会話も全部こなしたあとで
        // ExchangeExecutor の P-6 が拒み、2 回続けて失敗するとプリセットが
        // 自動で無効化されて設定に保存される。移動し切ってから落ちるのが最悪の形。
        //
        // 「アイテム交換」窓口の一覧は ReadOffers が同じ条件で落としている
        // （報酬が複数／コストが複数）。こちらも同じ判断に揃える。
        var groups = resolver.GroupByReward(true)
            .Where(x => x.Definitions.Any(IsExecutable))
            .ToList();

        var filtered = string.IsNullOrWhiteSpace(this.rewardSearch)
            ? groups
            : groups
                .Where(x => x.RewardName.Contains(this.rewardSearch, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (filtered.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  見つかりませんでした");
            return;
        }

        using (var child = ImRaii.Child("##rewardlist", new Vector2(0, 170), true))
        {
            if (!child)
            {
                return;
            }

            // 数千件になることがある。描き切ると開いた瞬間に固まる。
            foreach (var group in filtered.Take(200))
            {
                // Definitions の並びは経路の単純さが先で、値段は同じ経路の中でしか揃っていない。
                // 値段を出すなら、ここで安いものを選び直す。
                //
                // 撃てるものだけを見る。撃てないエントリの値段を出すと、
                // 実際に交換したときの額と食い違う。
                // 上で撃てるものがある品だけに絞ってあるので、ここは必ず 1 件以上ある。
                var cheapest = group.Definitions.Where(IsExecutable).MinBy(x => x.CurrencyCost)!;

                var label = cheapest.RewardQuantity > 1
                    ? $"{group.RewardName} ×{cheapest.RewardQuantity}"
                    : group.RewardName;

                var already = preset.Rewards.Any(x => x.RewardItemId == group.RewardItemId);

                if (ImGui.Selectable($"{label}##r{group.RewardItemId}", already))
                {
                    Toggle(preset, group.RewardItemId);
                    changed = true;
                }

                ImGui.SameLine();

                var area = NpcLocationService.GetTerritoryName(cheapest.TerritoryId);
                ImGui.TextColored(
                    ImGuiColors.DalamudGrey,
                    $"  {cheapest.CurrencyCost:N0}  （{cheapest.NpcName} / {area}）");
            }
        }

        if (filtered.Count > 200)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"  {filtered.Count - 200} 件は出していません。名前で絞り込んでください");
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "  値段と交換所は、いちばん安い窓口のものを出しています。交換所は下で選べます");
    }

    /// <summary>
    /// 交換所を指定する。
    ///
    /// 同じ品を複数の窓口が扱う。指定しなければ、アクセス済みのエーテライトがある
    /// エリアから自動で選ぶ。行きつけの街がある場合はここで固定できる。
    ///
    /// 窓口の一覧を作るにはゲームデータ全体の走査が要るため、押されたときだけ行う。
    /// </summary>
    private void DrawNpcPicker(ExchangePreset preset, uint currencyItemId, ref bool changed)
    {
        if (preset.Rewards.Count == 0)
        {
            return;
        }

        var firstReward = preset.Rewards[0].RewardItemId;
        var resolver = this.plugin.ExchangeResolver;

        if (!resolver.IsBuiltFor(currencyItemId))
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                preset.PreferredNpcDataId == 0
                    ? "  交換所: 自動（最寄り）で選びます"
                    : $"  交換所: {NpcLocationService.GetName(preset.PreferredNpcDataId)}（指定中）");

            if (ImGui.SmallButton("交換所を指定する##buildnpc"))
            {
                resolver.BeginBuild(currencyItemId);
            }

            return;
        }

        // 構築済みの索引をこの通貨へ切り替える。同じ通貨なら作り直しは起きない。
        resolver.BeginBuild(currencyItemId);

        var group = resolver.GroupByReward(true).FirstOrDefault(x => x.RewardItemId == firstReward);

        // 撃てない窓口は出さない。品は選べても、その窓口のエントリだけ
        // 報酬やコストが複数ということがある。選ぶと移動し切ってから拒まれる。
        var usable = group?.Definitions.Where(IsExecutable).ToList() ?? [];

        if (usable.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "  この品を扱う交換所が見つかりません");
            return;
        }

        // 同じ NPC が複数のショップで同じ品を扱うことがある。1 行にまとめる。
        var npcs = usable
            .GroupBy(x => x.NpcDataId)
            .Select(g => g.First())
            .ToList();

        var labels = new List<string> { "自動（最寄り）で選ぶ" };
        labels.AddRange(npcs.Select(x =>
            $"{x.NpcName} — {NpcLocationService.GetTerritoryName(x.TerritoryId)}"));

        var index = 0;
        for (var i = 0; i < npcs.Count; i++)
        {
            if (npcs[i].NpcDataId == preset.PreferredNpcDataId)
            {
                index = i + 1;
                break;
            }
        }

        ImGui.SetNextItemWidth(360f);
        if (ImGui.Combo("交換所", ref index, labels.ToArray(), labels.Count))
        {
            preset.PreferredNpcDataId = index == 0 ? 0 : npcs[index - 1].NpcDataId;
            changed = true;
        }
    }

    /// <summary>
    /// 欲しいアイテムから逆算した結果を出す。
    ///
    /// <code>
    /// 欲しいアイテムと個数
    ///   → 残りぶんに要るスクリップ
    ///     → 手持ちを引いて、あと稼ぐスクリップ
    ///       → 作る収集品の個数
    /// </code>
    ///
    /// **計算は毎フレーム行わない。** 所持数をひととおり数えるため重い。
    /// </summary>
    private void DrawGoalSummary(ExchangePreset preset, ref bool changed)
    {
        if (preset.Rewards.Count == 0)
        {
            // 作る収集品だけ選んで欲しいアイテムを選び忘れると、目標が立たない。
            // 黙って何も出さないと、有効にしても動かない理由が分からなくなる。
            if (preset.CraftCollectableItemId != 0)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    "欲しいアイテムが選ばれていません。下の一覧から選ぶと、必要な個数を計算します");
            }

            return;
        }

        var now = DateTime.UtcNow;

        // 設定を書き換えた直後は作り直す。間引いていると、収集品を選んでも
        // 「作る収集品が選ばれていません」が 1 秒残り、押せていないように見える。
        if (changed)
        {
            this.goalCacheUntilUtc = DateTime.MinValue;
        }

        if (this.goalCachePresetId != preset.Id || now > this.goalCacheUntilUtc)
        {
            this.goalCache = this.plugin.ScripGoalService.Build(preset);
            this.goalCachePresetId = preset.Id;
            this.goalCacheUntilUtc = now.AddSeconds(1);
        }

        var goal = this.goalCache;

        if (goal is null)
        {
            return;
        }

        ImGui.Separator();

        // 上限なしは目標が無い。素材が尽きるまで回る形なので、書き方も変える。
        if (goal.Endless)
        {
            this.DrawEndlessSummary(preset, goal);

            // 注意書きは上限なしでも必ず出す。
            // 交換費用が読めない・設定が食い違う、といった話は終わり方に関係なく効く。
            foreach (var note in goal.Notes)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {note}");
            }

            if (preset.CraftCollectableItemId != 0 && preset.Mode == ExchangeMode.UntilCurrencyReserve)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    $"  「指定量の通貨を残すまで」のため、{preset.CurrencyReserve:N0} ぶんは交換されずに残ります");
            }

            this.DrawKeepFreeSlots(preset, ref changed);
            this.DrawGoalRunState(preset);
            ImGui.Separator();
            return;
        }

        // 設定として交換できない品があるなら、達成より先に出す。
        foreach (var note in goal.Notes.Where(x => x.Contains("1 回も交換できません", StringComparison.Ordinal)))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {note}");
        }

        if (goal.Achieved)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "目標に届いています");

            // **何の目標に届いたのかを言う。**
            //
            // ここが見ているのは交換リストの「所持の上限」。
            // 「どこまで交換するか」で別の目標を入れていると、
            // そちらに届いていなくてもここは緑になる。
            // どちらで止まったのかが分からないと、設定を直しようがない。
            // どの数に届いたのかを言う。モードで目標を決めているならそちらが親。
            if (preset.Mode == ExchangeMode.UntilTargetQuantity)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudGrey,
                    $"  「目標の所持数」{preset.Quantity} に届いています");
            }
        }

        // 要るスクリップ。**まだ交換できる品だけ**を並べる。
        //
        // 以前は「残りが 1 個以上」で絞っていたため、端数だけ残った品が
        // 「あと 1 個 = 0 回 = 0」という読めない行になっていた。
        foreach (var item in goal.Items.Where(x => !x.Unlimited && x.Trades > 0))
        {
            // **費用は 1 回あたり。数えるのは回数。**
            // 1 回 1 個の品でも「個」と書くと、Trades を個数と読ませてしまう。
            var trade = item.PerTrade > 1
                ? $"{item.Trades} 回（1 回 {item.PerTrade} 個）× {item.Cost:N0}"
                : $"{item.Trades} 回 × {item.Cost:N0}";

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  {item.Name}: {item.Want} 個まで（いま {item.Held} 個）" +
                $" → あと {item.Remaining} 個 = {trade} = {item.Subtotal:N0}");
        }

        // 端数だけ残った品は、別に書く。買えないことを明示する。
        foreach (var item in goal.Items.Where(x => !x.Unlimited && x.Trades <= 0 && x.Remaining > 0))
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  {item.Name}: あと {item.Remaining} 個ですが、" +
                $"1 回で {item.PerTrade} 個入るため交換できません");
        }

        // **費用を引けない品があるなら、合計を断言しない。**
        //
        // 引けなかった品は 0 として足されるため、合計が本当より小さくなる。
        // それを「足りています」と緑で出していた。9 品すべて引けていないのに
        // 「要る 0 / 足りています」と出て、交換が進まない理由を隠していた。
        var unknownCost = goal.Items.Any(x => x.Cost == 0);

        ImGui.TextUnformatted($"要る{goal.CurrencyName}: {goal.RequiredScrips:N0}");
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"  いま {goal.HeldScrips:N0}");
        ImGui.SameLine();

        if (unknownCost)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "  費用を引けない品があるため、この合計は当てになりません");
        }
        else
        {
            ImGui.TextColored(
                goal.MissingScrips > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
                goal.MissingScrips > 0 ? $"  あと {goal.MissingScrips:N0}" : "  足りています");
        }

        if (goal.MissingScrips > 0)
        {
            if (goal.Collectable is null)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    "  作る収集品が選ばれていません。下の「製作するジョブ」から選んでください");
            }
            else
            {
                ImGui.TextColored(
                    ImGuiColors.HealerGreen,
                    $"  作る収集品: {goal.Collectable.Name} を {goal.CollectablesNeeded} 個" +
                    $"（1 個あたり最大 {goal.Collectable.HighReward}）");
            }
        }

        foreach (var note in goal.Notes)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {note}");
        }

        // 所持の上限で目標を立てたら、終了条件はそれだけで決まる。
        // ほかの終了条件が残っていると、目標ぶんが貯まっても最後まで交換できない。
        if (preset.CraftCollectableItemId != 0)
        {
            if (preset.Mode != ExchangeMode.MaxExchange)
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    "  「どこまで交換するか」を『交換できる限り』に戻してください。" +
                    "ほかの条件だと、目標ぶんが貯まっても最後まで交換できません");
            }

            if (preset.Rewards.Any(x => x.Quantity > 0))
            {
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    "  「一括交換する個数」を 0 にしてください。個数は「所持の上限」で決まります");
            }
        }

        this.DrawKeepFreeSlots(preset, ref changed);
        this.DrawGoalRunState(preset);

        ImGui.Separator();
    }

    /// <summary>
    /// 所持の上限を入れていないときの書き方。
    ///
    /// **終わりが無い。** 素材が尽きるまで、作って納品して交換し続ける。
    /// 目標が無いので「あと何個」も「要るスクリップ」も出せない。
    /// 代わりに、何を何個ずつ交換し続けるのかを出す。
    /// </summary>
    private void DrawEndlessSummary(ExchangePreset preset, ScripGoal goal)
    {
        ImGui.TextColored(ImGuiColors.HealerGreen, "上限なし: 素材が尽きるまで作って納品し、交換し続けます");

        foreach (var item in goal.Items)
        {
            var entry = preset.Rewards.FirstOrDefault(x => x.RewardItemId == item.RewardItemId);
            var batch = entry is null || entry.Quantity <= 0
                ? "交換できる限り"
                : $"1 回の移動で {entry.Quantity} 個ずつ";

            // 上限ありの品が混ざっていても分かるようにする。
            var cap = item.Unlimited ? "上限なし" : $"{item.Want} 個まで";
            var unit = item.PerTrade > 1
                ? $"1 回 {item.Cost:N0} で {item.PerTrade} 個"
                : $"1 個 {item.Cost:N0}";

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  {item.Name}: {cap} / {batch}（{unit} / いま {item.Held} 個）");
        }

        ImGui.TextUnformatted($"いまの{goal.CurrencyName}: {goal.HeldScrips:N0}");

        if (goal.Collectable is null)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                "  作る収集品が選ばれていません。下の「製作するジョブ」から選んでください");
            return;
        }

        ImGui.TextColored(
            ImGuiColors.HealerGreen,
            $"  作る収集品: {goal.Collectable.Name}（1 個あたり最大 {goal.Collectable.HighReward}）" +
            $" … 鞄の空き枠いっぱいまで作ります");
    }

    /// <summary>製作のときに空けておく枠。作った物と交換した物の両方が鞄に入る。</summary>
    private void DrawKeepFreeSlots(ExchangePreset preset, ref bool changed)
    {
        if (preset.CraftCollectableItemId == 0)
        {
            return;
        }

        var keep = preset.CraftKeepFreeSlots;

        if (this.DrawNumber($"keep{preset.Id}", "残す空き枠", ref keep))
        {
            preset.CraftKeepFreeSlots = Math.Max(0, keep);
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("製作のときに空けておく鞄の枠。交換で受け取る品の置き場になります。");
        }
    }

    /// <summary>いま回っているかどうかを出す。止める手段も一緒に置く。</summary>
    private void DrawGoalRunState(ExchangePreset preset)
    {
        var runner = this.plugin.GoalRunner;

        if (runner.IsRunning && runner.Preset?.Id == preset.Id)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"回しています: {runner.StatusDetail}");

            if (ImGui.Button("止める##stopgoal"))
            {
                runner.Stop("ユーザー操作");
            }

            return;
        }

        if (preset.CraftCollectableItemId == 0)
        {
            return;
        }

        if (!preset.Enabled)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "このプリセットを有効にすると、素材の取り出しから交換までを通しで回します");

            // **無効でも呼び鈴の状況は出す。**
            // 有効にする前に「取りに行けるのか」を確かめたい場面なのに、
            // ここで返していたため、覚えているかどうかを見る手段が無かった。
            this.DrawKnownBell();
            return;
        }

        // 届かずに止まったなら、理由を出す。出さないと、有効なのに動かない理由が分からない。
        var stopped = runner.BlockedReason(preset.Id);

        if (!string.IsNullOrEmpty(stopped))
        {
            var retry = runner.BlockedRetryInSeconds(preset.Id);

            ImGui.TextColored(ImGuiColors.DalamudYellow, $"止まっています: {stopped}");

            this.DrawShortages(runner.BlockedShortages(preset.Id));

            if (retry is not null)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {retry} 秒後にもう一度試します");
            }

            if (ImGui.Button("いますぐもう一度試す##retrygoal"))
            {
                runner.ClearBlock(preset.Id);
            }

            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "（チェックを入れ直しても、この理由を忘れてやり直します）");

            // 呼び鈴が要るのは取り出しのときだけ。**止まっているときこそ出す。**
            // ここで return していたため、原因を示す唯一の行が隠れていた。
            var bellNow = this.plugin.RetainerRestock.DescribeBell();
            ImGui.TextColored(
                bellNow.StartsWith("呼び鈴が見つかりました", StringComparison.Ordinal)
                    ? ImGuiColors.DalamudGrey
                    : ImGuiColors.DalamudYellow,
                $"  {bellNow}");

            this.DrawKnownBell();
            return;
        }

        // 呼び鈴が要るのは素材を取り出すときだけ。押す前に分かるようにしておく。
        var bell = this.plugin.RetainerRestock.DescribeBell();
        ImGui.TextColored(
            bell.StartsWith("呼び鈴が見つかりました", StringComparison.Ordinal)
                ? ImGuiColors.DalamudGrey
                : ImGuiColors.DalamudYellow,
            $"  {bell}");

        this.DrawKnownBell();
    }

    /// <summary>
    /// 足りない素材を 1 件ずつ出す。
    ///
    /// 止まった理由の文にも素材名は入っているが、文のままだと
    /// 「どこで手に入るのか」を調べるために名前を打ち直すことになる。
    /// 名前を押せばそのまま写せるようにしておく。
    ///
    /// **中間素材は、その素材をすぐには出さない。**
    /// ウトォームチリソースが足りないと言われても、直したいのは
    /// その素材（ウトォームトマトとドラゴンペッパー）のほうである。
    /// ただし最初から末端まで並べると、実際には足りている物まで含めて
    /// 一覧が長くなり、何を見ればよいのか分からなくなる。
    /// 右クリックで開く形にして、見たいときだけ出す。
    /// </summary>
    private void DrawShortages(IReadOnlyList<PlanMaterial> shortages)
    {
        if (shortages.Count == 0)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  足りない素材（左クリックで名前をコピー / 中間素材は右クリックで素材を開く）");

        foreach (var material in shortages)
        {
            this.DrawShortageRow(material, depth: 1);
        }

        this.DrawCopyNotice();
    }

    /// <summary>足りない素材 1 行。中間素材なら、開いているあいだその素材も続けて出す。</summary>
    private void DrawShortageRow(PlanMaterial material, int depth)
    {
        // 自分で作れて、素材まで辿れているものだけ開ける。
        // 辿れていないものを開けるように見せると、押しても何も出ずに戸惑う。
        var canExpand = material.IsIntermediate && material.SubMaterials.Count > 0;
        var expanded = canExpand && this.expandedShortages.Contains(material.ItemId);

        var mark = canExpand ? (expanded ? "▼ " : "▶ ") : "  ";
        var indent = new string('　', depth);

        ImGui.Selectable(
            $"{indent}{mark}{material.Name}##shortage{depth}_{material.ItemId}",
            false,
            ImGuiSelectableFlags.None,
            new Vector2(320f, 0f));

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ImGui.SetClipboardText(material.Name);
            this.copiedName = material.Name;
            this.copiedAtUtc = DateTime.UtcNow;
        }

        if (canExpand && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if (!this.expandedShortages.Remove(material.ItemId))
            {
                this.expandedShortages.Add(material.ItemId);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                canExpand
                    ? $"左クリック: 「{material.Name}」をコピー\n" +
                      $"右クリック: この素材を作るのに要る物を{(expanded ? "閉じる" : "開く")}"
                    : $"左クリック: 「{material.Name}」をコピー");
        }

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"あと {material.Shortfall}");

        if (canExpand)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "（自分で作れます）");
        }

        if (!expanded)
        {
            return;
        }

        // 開いたときは、足りている素材も出す。
        // 足りている物まで見えていないと「これだけ集めればよい」が判断できない。
        foreach (var sub in material.SubMaterials)
        {
            this.DrawShortageSubRow(sub, depth + 1);
        }
    }

    /// <summary>中間素材の素材 1 行。ここから先は辿らない。</summary>
    private void DrawShortageSubRow(PlanMaterial sub, int depth)
    {
        var indent = new string('　', depth);

        ImGui.Selectable(
            $"{indent}  {sub.Name}##shortagesub{depth}_{sub.ItemId}",
            false,
            ImGuiSelectableFlags.None,
            new Vector2(320f, 0f));

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ImGui.SetClipboardText(sub.Name);
            this.copiedName = sub.Name;
            this.copiedAtUtc = DateTime.UtcNow;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"左クリック: 「{sub.Name}」をコピー");
        }

        ImGui.SameLine();
        ImGui.TextColored(
            sub.Shortfall > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
            sub.Shortfall > 0 ? $"あと {sub.Shortfall}" : "足りています");

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"（要 {sub.Needed} / 手持ち {sub.Held}）");
    }

    /// <summary>
    /// 直前に写した名前を短く知らせる。
    ///
    /// クリップボードは目に見えない。押した手応えが無いと、
    /// 効いたのかどうか分からず何度も押すことになる。
    /// </summary>
    private void DrawCopyNotice()
    {
        if (this.copyNoticeDrawn ||
            string.IsNullOrEmpty(this.copiedName) ||
            DateTime.UtcNow - this.copiedAtUtc > CopyNoticeDuration)
        {
            return;
        }

        this.copyNoticeDrawn = true;
        ImGui.TextColored(ImGuiColors.HealerGreen, $"  「{this.copiedName}」をコピーしました");
    }

    /// <summary>
    /// このエリアで覚えている呼び鈴の場所を出す。
    ///
    /// 近くに無くても、覚えていれば歩いて行ける。
    /// 覚えているかどうかが分からないと、なぜ動くのか／動かないのかが読めない。
    /// </summary>
    private void DrawKnownBell()
    {
        var territory = ECommons.DalamudServices.Svc.ClientState.TerritoryType;
        var known = this.plugin.BellLocations.Get(territory);

        if (known is null)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "  このエリアの呼び鈴はまだ覚えていません（エリアに入ってしばらく探します）");
            return;
        }

        ImGui.TextColored(
            ImGuiColors.HealerGreen,
            $"  覚えている呼び鈴: {known.Value.X:F1}, {known.Value.Y:F1}, {known.Value.Z:F1}" +
            $"（全 {this.plugin.BellLocations.Count} エリア）");
    }

    /// <summary>
    /// 製作するジョブを選ぶ。系統の右に置く。
    ///
    /// 「未設定」のあいだは、表には交換で手に入る装備品が並ぶ。
    /// ジョブを選ぶと、表はそのジョブで作れる収集品に切り替わる。
    /// 欲しいアイテムを選ぶ場面と、その元手を作る場面は別なので、表も分ける。
    /// </summary>
    private void DrawCraftJobCombo(ExchangePreset preset, ref bool changed)
    {
        var jobs = this.plugin.CraftPlanService.ListJobs();

        if (jobs.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "ジョブの一覧を作れませんでした");
            return;
        }

        var current = preset.CraftJob >= 0
            ? jobs.FirstOrDefault(x => x.CraftType == (uint)preset.CraftJob)
            : null;

        var label = current?.Name ?? UnsetJobLabel;

        ImGui.SetNextItemWidth(180f);

        using var combo = ImRaii.Combo("製作するジョブ", label);
        if (!combo)
        {
            return;
        }

        // 一覧の中の「--選択して下さい--」は見出しであって選択肢ではない。
        // 押しても意味が無いので、押せないことが分かるよう灰色で出す。
        using (ImRaii.Disabled())
        {
            ImGui.Selectable($"{UnsetJobLabel}##jobunset", preset.CraftJob < 0);
        }

        foreach (var job in jobs)
        {
            var selected = preset.CraftJob >= 0 && job.CraftType == (uint)preset.CraftJob;

            if (!ImGui.Selectable($"{job.Name}##job{job.CraftType}", selected))
            {
                continue;
            }

            preset.CraftJob = (int)job.CraftType;

            // ジョブが変われば作れる物も変わる。選び直しになる。
            preset.CraftCollectableItemId = 0;
            preset.CraftLevelBand = 0;
            preset.CraftToEarn = false;
            changed = true;
        }
    }

    /// <summary>
    /// 作る収集品を選ぶ。製作計画タブと同じ構造にしてある。
    ///
    /// **レベルのボタンは、帯が 2 つ以上あるときだけ出す。**
    /// 橙貨はジョブごとに 1 件しかないため、絞る意味がない。
    /// 紫貨はレベル帯ごとに複数あり、生むスクリップの量も違うため、選ぶ必要がある。
    /// </summary>
    private void DrawCraftPicker(ExchangePreset preset, uint currencyItemId, ref bool changed)
    {
        var craftable = this.plugin.CraftPlanService.ListCraftable(currencyItemId)
            .Where(x => x.CraftType == (uint)preset.CraftJob)
            .ToList();

        if (craftable.Count == 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                "このジョブで作れる、この通貨を生む収集品が見つかりません");
            return;
        }

        // --- レベル帯 ---
        var available = CraftPlanService.LevelBands
            .Where(band => craftable.Any(x => x.ClassJobLevel >= band.Min && x.ClassJobLevel <= band.Max))
            .ToList();

        if (available.Count > 1)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "レベル:");

            foreach (var band in available)
            {
                ImGui.SameLine();

                var selected = preset.CraftLevelBand == band.Min;
                using var color = ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.ParsedBlue, selected);

                if (ImGui.SmallButton($"{band.Min}-{band.Max}##pband{band.Min}"))
                {
                    preset.CraftLevelBand = band.Min;
                    preset.CraftCollectableItemId = 0;
                    preset.CraftToEarn = false;
                    changed = true;
                }
            }
        }

        // 帯が選ばれていなければ、いちばん上の帯にしておく。
        // 書き換えたら保存する。保存しないと、開き直すたびにここへ戻ってくる。
        if (available.Count > 0 && !available.Any(x => x.Min == preset.CraftLevelBand))
        {
            preset.CraftLevelBand = available[^1].Min;
            changed = true;
        }

        var filtered = craftable;

        if (available.Count > 1)
        {
            var band = CraftPlanService.LevelBands.FirstOrDefault(x => x.Min == preset.CraftLevelBand);

            if (band.Max > 0)
            {
                filtered = craftable
                    .Where(x => x.ClassJobLevel >= band.Min && x.ClassJobLevel <= band.Max)
                    .ToList();
            }
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            $"  作れる収集品 {filtered.Count} 件（製作手帳と同じ並び / 右クリックで名前をコピー）");

        using (var child = ImRaii.Child("##presetcraftlist", new Vector2(0, 150), true))
        {
            if (child)
            {
                foreach (var item in filtered)
                {
                    var selected = ImGui.Selectable(
                        $"{item.Name}##pc{item.ItemId}",
                        preset.CraftCollectableItemId == item.ItemId);

                    // 右クリックは選択を変えない。**何を作るかの設定は左クリックだけで動かす。**
                    // 名前を調べたいだけのときに設定が変わると、気づかないまま別の物を作る。
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.SetClipboardText(item.Name);
                        this.copiedName = item.Name;
                        this.copiedAtUtc = DateTime.UtcNow;
                    }

                    if (selected)
                    {
                        preset.CraftCollectableItemId = item.ItemId;
                        preset.CraftToEarn = true;

                        // 終了条件は「所持の上限」に一本化する。
                        //
                        // 「指定量の通貨を残すまで」のままだと、目標ぶんのスクリップが
                        // 貯まっても残す設定にひっかかって最後の 1 個を交換できず、
                        // 目標に届かないまま進まなくなる。
                        preset.Mode = ExchangeMode.MaxExchange;

                        // 「交換する数」は 1 回の移動で何個買うかの上限。
                        // 既定の 1 のままだと、1 個買うたびに窓口へ往復することになる。
                        // 個数は「所持の上限」で決まるので、こちらは外す。
                        foreach (var reward in preset.Rewards)
                        {
                            reward.Quantity = 0;
                        }

                        changed = true;
                    }

                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"  Lv{item.ClassJobLevel}  最大 {item.HighReward}");
                }
            }
        }

        if (preset.CraftCollectableItemId == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "作る収集品を選ぶと、必要な個数を計算します");
        }

        this.DrawCopyNotice();
    }

    /// <summary>作る物の選択を解く。表を装備品へ戻すときに通す。</summary>
    private static void ClearCraftChoice(ExchangePreset preset)
    {
        preset.CraftJob = -1;
        preset.CraftCollectableItemId = 0;
        preset.CraftLevelBand = 0;
        preset.CraftToEarn = false;
    }

    /// <summary>系統名は長いので、検索結果では後半だけを出す。</summary>
    private static string ShortCategory(string name)
    {
        var separator = name.LastIndexOf('：');
        return separator >= 0 && separator + 1 < name.Length ? name[(separator + 1)..] : name;
    }

    /// <summary>
    /// 通貨の名前を描く。貨の色が名前で分かるようにする。
    ///
    /// 「クラフタースクリップ:橙貨」なら、橙貨 の部分だけを橙色にする。
    /// 一覧に並んだときに、どの階層のスクリップかを一目で選べるようにするため。
    /// </summary>
    private static void DrawCurrencyName(string name)
    {
        var separator = name.LastIndexOf(':');

        if (separator < 0 || separator + 1 >= name.Length)
        {
            ImGui.TextUnformatted(name);
            return;
        }

        var head = name[..(separator + 1)];
        var tail = name[(separator + 1)..];

        ImGui.TextUnformatted(head);
        ImGui.SameLine(0f, 0f);
        ImGui.TextColored(CurrencyColor(tail), tail);
    }

    /// <summary>貨の名前から色を決める。分からないものは既定の色にする。</summary>
    private static Vector4 CurrencyColor(string tail) => tail switch
    {
        "橙貨" => new Vector4(1.00f, 0.55f, 0.15f, 1f),
        "紫貨" => new Vector4(0.72f, 0.45f, 0.95f, 1f),
        "黄貨" => new Vector4(0.95f, 0.85f, 0.25f, 1f),
        "白貨" => new Vector4(0.90f, 0.90f, 0.90f, 1f),
        "赤貨" => new Vector4(0.95f, 0.35f, 0.35f, 1f),
        "青貨" => new Vector4(0.40f, 0.65f, 1.00f, 1f),
        _ => ImGuiColors.DalamudWhite,
    };

    private static string ItemName(uint itemId)
        => ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText()
           ?? $"<{itemId}>";

    /// <summary>交換リストに入れる・外す。</summary>
    private static void Toggle(ExchangePreset preset, uint rewardItemId)
    {
        var existing = preset.Rewards.FirstOrDefault(x => x.RewardItemId == rewardItemId);

        if (existing is not null)
        {
            preset.Rewards.Remove(existing);
            return;
        }

        preset.Rewards.Add(new ExchangeEntry { RewardItemId = rewardItemId });
    }

    /// <summary>
    /// 交換リスト。上から順に交換する。
    ///
    /// 品ごとに「何個まで」と「いくつ持っていたら飛ばすか」を持つ。
    /// 秘伝書のように 1 冊あれば足りるものを毎回買い直さないため。
    /// </summary>
    private void DrawRewardList(ExchangePreset preset, ref bool changed)
    {
        ImGui.TextUnformatted($"交換リスト（{preset.Rewards.Count} 件）");

        if (preset.Rewards.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  下の一覧から品を選ぶと、ここに並びます");
            return;
        }

        ExchangeEntry? remove = null;
        var moveUp = -1;

        using (var child = ImRaii.Child("##rewardlist_selected", new Vector2(0, Math.Min(200f, 34f + (preset.Rewards.Count * 28f))), true))
        {
            if (child)
            {
                for (var i = 0; i < preset.Rewards.Count; i++)
                {
                    var entry = preset.Rewards[i];
                    using var id = ImRaii.PushId($"entry{i}");

                    if (ImGui.SmallButton("×"))
                    {
                        remove = entry;
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(i == 0))
                    {
                        if (ImGui.SmallButton("↑"))
                        {
                            moveUp = i;
                        }
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(ItemName(entry.RewardItemId));

                    ImGui.SameLine();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);

                    // 2 つの数値は役割が違う。ラベルだけでは取り違えるため、
                    // それぞれに説明を付ける。
                    var quantity = entry.Quantity;
                    if (this.DrawNumber($"eq{i}{entry.RewardItemId}", "一括交換する個数##qty", ref quantity, 90f))
                    {
                        entry.Quantity = Math.Max(0, quantity);
                        changed = true;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "1 回の移動でこの数まで交換します。0 なら 1 回で交換できる最大数を交換します。\n" +
                            "所持の上限が 0 なら、交換所へ行くたびにこの数ずつ交換し続けます。");
                    }

                    ImGui.SameLine();

                    var limit = entry.OwnedLimit;
                    if (this.DrawNumber($"el{i}{entry.RewardItemId}", "所持の上限##own", ref limit, 90f))
                    {
                        entry.OwnedLimit = Math.Max(0, limit);
                        changed = true;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "この数まで持つように交換します。足りないぶんだけ交換します。" +
                            "上限 5 で 3 個持っているなら 2 個だけ交換します。" +
                            "0 にすると上限なし。");
                    }

                    ImGui.SameLine();

                    // いま何個持っているかを添える。飛ばされる理由が見えるようにする。
                    var ownedText = this.plugin.CurrencyService.TryGetCount(
                        entry.RewardItemId, out var have, includeEquipped: true, includeArmory: true)
                        ? $"所持 {have}"
                        : "所持 ?";

                    // モードで目標を決めているなら、この欄は親ではない。
                    // 入っている数のせいで止まっていると誤解させない。
                    if (preset.Mode == ExchangeMode.UntilTargetQuantity)
                    {
                        ImGui.TextColored(
                            ImGuiColors.DalamudGrey,
                            $"{ownedText} / 目標 {preset.Quantity}（上の「目標の所持数」で決まります）");
                    }
                    else if (entry.OwnedLimit > 0)
                    {
                        var shortfall = entry.OwnedLimit - have;

                        if (shortfall <= 0)
                        {
                            ImGui.TextColored(
                                ImGuiColors.DalamudYellow,
                                $"{ownedText} / 上限 {entry.OwnedLimit} → 飛ばします");
                        }
                        else
                        {
                            // **2 つの数を混ぜない。**
                            // 「あと N 個」に 1 回ぶんの数を入れていたため、
                            // 上限 50・一括 10 のとき「あと 10 個」と出て、
                            // 目標まであと何個なのかが読み取れなかった。
                            var perTrip = entry.Quantity > 0 ? Math.Min(shortfall, entry.Quantity) : shortfall;

                            ImGui.TextColored(
                                ImGuiColors.DalamudGrey,
                                entry.Quantity > 0 && perTrip < shortfall
                                    ? $"{ownedText} / 上限 {entry.OwnedLimit} → 目標まで {shortfall} 個（1 回の移動で {perTrip} 個ずつ）"
                                    : $"{ownedText} / 上限 {entry.OwnedLimit} → 目標まで {shortfall} 個");
                        }
                    }
                    else
                    {
                        // 上限 0 は「終わりを決めない」。素材が尽きるまで繰り返す。
                        // ここを「所持 0」とだけ出していたため、1 回で止まるのか
                        // 繰り返すのかが読み取れなかった。
                        ImGui.TextColored(
                            ImGuiColors.DalamudGrey,
                            $"{ownedText} / 上限なし → 素材が尽きるまで繰り返します");
                    }

                    if (entry.Quantity == 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ImGuiColors.DalamudYellow, "1 回で交換できる最大数を交換します");
                    }
                }
            }
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  一括交換する個数 … スクリップ交換窓口で交換する数量。0 なら 1 回で交換できる最大数");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "  所持の上限 … この数まで持つように交換する。0 で上限なし");

        if (remove is not null)
        {
            preset.Rewards.Remove(remove);
            changed = true;
        }

        if (moveUp > 0)
        {
            (preset.Rewards[moveUp - 1], preset.Rewards[moveUp]) = (preset.Rewards[moveUp], preset.Rewards[moveUp - 1]);
            changed = true;
        }
    }
}
