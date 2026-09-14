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

    private static readonly string[] ThresholdModeNames = ["固定値", "上限に対する割合", "上限までの残り"];

    private static readonly string[] ExchangeModeNames =
    [
        "交換できる限り",
        "指定量の通貨を残すまで",
        "指定回数だけ",
        "目標所持数に達するまで",
    ];

    private readonly Plugin plugin = plugin;

    private string rewardSearch = string.Empty;
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

                break;
            }

            case ExchangeMode.MaxExchange:
                ImGui.TextColored(ImGuiColors.DalamudYellow, "  通貨か所持枠が尽きるまで交換します");
                break;
        }

        ImGui.Spacing();

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

        if (categories.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "交換の一覧を作れませんでした");
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

        if (group is null || group.Definitions.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "  この品を扱う交換所が見つかりません");
            return;
        }

        // 同じ NPC が複数のショップで同じ品を扱うことがある。1 行にまとめる。
        var npcs = group.Definitions
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

        if (goal.Achieved)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "目標に届いています");
        }

        // 要るスクリップ。届いていない品だけを並べる。
        foreach (var item in goal.Items.Where(x => !x.Unlimited && x.Remaining > 0))
        {
            // 1 回で 2 個以上もらえる品があるので、回数で書く。
            var trade = item.PerTrade > 1
                ? $"{item.Trades} 回（1 回 {item.PerTrade} 個）× {item.Cost:N0}"
                : $"{item.Trades} 個 × {item.Cost:N0}";

            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                $"  {item.Name}: {item.Want} 個まで（いま {item.Held} 個）" +
                $" → あと {item.Remaining} 個 = {trade} = {item.Subtotal:N0}");
        }

        ImGui.TextUnformatted($"要る{goal.CurrencyName}: {goal.RequiredScrips:N0}");
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"  いま {goal.HeldScrips:N0}");
        ImGui.SameLine();
        ImGui.TextColored(
            goal.MissingScrips > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
            goal.MissingScrips > 0 ? $"  あと {goal.MissingScrips:N0}" : "  足りています");

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
            return;
        }

        // 届かずに止まったなら、理由を出す。出さないと、有効なのに動かない理由が分からない。
        var stopped = runner.BlockedReason(preset.Id);

        if (!string.IsNullOrEmpty(stopped))
        {
            var retry = runner.BlockedRetryInSeconds(preset.Id);

            ImGui.TextColored(ImGuiColors.DalamudYellow, $"止まっています: {stopped}");

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

        ImGui.SameLine();

        if (ImGui.SmallButton("忘れる##forgetbell"))
        {
            this.plugin.BellLocations.Forget(territory);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("呼び鈴が動いた・別の場所を覚えさせたいときに押してください。次にこのエリアへ入り直すと探し直します。");
        }
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
            $"  作れる収集品 {filtered.Count} 件（製作手帳と同じ並び）");

        using (var child = ImRaii.Child("##presetcraftlist", new Vector2(0, 150), true))
        {
            if (child)
            {
                foreach (var item in filtered)
                {
                    if (ImGui.Selectable($"{item.Name}##pc{item.ItemId}", preset.CraftCollectableItemId == item.ItemId))
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
                            "1 回の移動でこの数まで交換します。0 にすると上限なし（通貨か所持枠が尽きるまで）。\n" +
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

                    // 上限まで何個足りないかを出す。実行前に結果が分かるようにする。
                    if (entry.OwnedLimit > 0)
                    {
                        var shortfall = entry.OwnedLimit - have;

                        ImGui.TextColored(
                            shortfall <= 0 ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey,
                            shortfall <= 0
                                ? $"{ownedText} / 上限 {entry.OwnedLimit} → 飛ばします"
                                : $"{ownedText} / 上限 {entry.OwnedLimit} → あと {Math.Min(shortfall, entry.Quantity > 0 ? entry.Quantity : shortfall)} 個");
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
                        ImGui.TextColored(ImGuiColors.DalamudYellow, "1 回で交換できる限り");
                    }
                }
            }
        }

        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            "  一括交換する個数 … 1 回の移動でこの数まで交換する。0 で上限なし");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "  所持の上限 … この数まで持つように交換する。足りないぶんだけ。0 で上限なし");

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
