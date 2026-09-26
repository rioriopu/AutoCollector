using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>監視できる通貨 1 件。</summary>
/// <param name="TomestonesRowId">
/// トームストーンの場合のスロット番号。0 ならスロット参照ではない。
/// </param>
/// <param name="ItemId">実際の ItemId。</param>
/// <param name="Name">表示名。</param>
/// <param name="Group">一覧での見出し。</param>
public sealed record CurrencyChoice(uint TomestonesRowId, uint ItemId, string Name, string Group);

/// <summary>
/// 監視できる通貨をまとめる。
///
/// トームストーンはパッチで中身が入れ替わるため、ItemId ではなくスロット番号で持つ。
/// スクリップなどはスロットの概念が無いので ItemId で持つ。
/// この違いを 1 か所に閉じ込め、呼び出し側は ItemId だけを受け取る。
/// </summary>
public sealed class CurrencyCatalog(
    TomestoneService tomestones,
    SpecialCurrencyMap specials,
    InclusionShopCatalog inclusionShops,
    Diagnostics.AnomalyLog anomalyLog)
{
    private readonly TomestoneService tomestones = tomestones;
    private readonly SpecialCurrencyMap specials = specials;
    private readonly InclusionShopCatalog inclusionShops = inclusionShops;
    private readonly Diagnostics.AnomalyLog anomalyLog = anomalyLog;

    /// <summary>
    /// 一覧の中身を記録したときの顔ぶれ。変わったときだけ書く。
    ///
    /// 画面は毎フレーム描かれるので、無条件に書くと記録が埋まる。
    /// 「何件になったか」が変わった瞬間だけ残す。
    /// </summary>
    private string lastLogged = string.Empty;

    /// <summary>プリセットが監視する通貨の ItemId を求める。</summary>
    public bool TryResolve(ExchangePreset preset, out uint itemId)
    {
        // ItemId を直接持っている場合はそれを使う。
        // スクリップのようにスロットの概念が無い通貨はこちら。
        if (preset.CurrencyItemId != 0)
        {
            itemId = preset.CurrencyItemId;
            return true;
        }

        return this.tomestones.TryResolveItemId(preset.TomestonesRowId, out itemId);
    }

    /// <summary>
    /// 直前に作った一覧と、作った時刻。
    ///
    /// <b>画面は毎フレーム呼ぶ。</b>
    /// この中では特殊通貨の一覧（Distinct → シート引き → 並べ替え）を
    /// 2 回作り、交換の一覧を総なめし、記録用の長い文字列まで組み立てている。
    /// 毎フレームやる意味は無い。
    ///
    /// <b>ただし長く持たない。</b>
    /// ログイン直後は中身が育っていく。長く持つと、育ったことに
    /// 気づくのが遅れる。人が操作する速さから見れば 1 秒で十分。
    /// </summary>
    private IReadOnlyList<CurrencyChoice>? choiceCache;
    private DateTime choiceCacheUntil = DateTime.MinValue;

    private static readonly TimeSpan ChoiceCacheDuration = TimeSpan.FromSeconds(1);

    /// <summary>選べる通貨の一覧。</summary>
    public IReadOnlyList<CurrencyChoice> ListChoices()
    {
        if (this.choiceCache is { } cached && DateTime.UtcNow < this.choiceCacheUntil)
        {
            return cached;
        }

        var list = new List<CurrencyChoice>();

        foreach (var slot in this.tomestones.ListSlots())
        {
            list.Add(new CurrencyChoice(slot.TomestonesRowId, slot.ItemId, slot.Name, "アラガントームストーン"));
        }

        // 交換に使えるスクリップだけを出す。
        //
        // 特殊通貨の一覧にはクラフタースクリップ:白貨 のような、
        // すでに交換所から消えたものも残っている。選べても意味がない。
        //
        // <b>ただし絞り込めなかったときは、絞らない。</b>
        // 交換の一覧は初回に作られるが、シートを読めない・まだログイン直後で
        // 作れないといった理由で 0 件になることがある。
        // そのとき「使える通貨が 1 つも無い」と読むと、スクリップが全部消えて
        // トームストーンしか選べなくなる。絞り込みの材料が無いだけで、
        // 実際に使えないと分かったわけではない（2026-09-25 実測）。
        var usable = this.UsableCurrencies();

        foreach (var (itemId, name) in this.specials.ListCurrencies())
        {
            if (usable.Count > 0 && !usable.Contains(itemId))
            {
                continue;
            }

            list.Add(new CurrencyChoice(0, itemId, name, "スクリップ"));
        }

        // **アイテムそのものを通貨にする交換もある。**
        //
        // バイカラージェムのように、SpecialShop のコストが
        // 特殊通貨バケット（CostType==3）ではなく、ふつうのアイテム
        // （ItemCost に ItemId がそのまま入る）になっているものがある。
        // 特殊通貨の表には載らないので、これまで一覧に出てこなかった。
        //
        // <b>種類は決め打ちしない。</b>
        // 交換所のコストとして実際に使われているアイテムのうち、
        // 「通貨として扱うもの」を選び出す。判断は ItemCurrency に任せる。
        foreach (var (itemId, name) in this.ListItemCurrencies())
        {
            if (list.Any(x => x.ItemId == itemId))
            {
                continue;
            }

            list.Add(new CurrencyChoice(0, itemId, name, "そのほかの通貨"));
        }

        // **何が並んだかを必ず残す。**
        // 「1 件しか選べない」という報告を、推測ではなく記録で追えるようにする。
        // 中身が変わったときだけ書くので、出続けて邪魔になることはない。
        var summary =
            $"通貨の一覧: トームストーン {list.Count(x => x.TomestonesRowId != 0 || x.Group == "アラガントームストーン")} 件 / " +
            $"スクリップ {list.Count(x => x.Group == "スクリップ")} 件 / " +
            $"そのほか {list.Count(x => x.Group == "そのほかの通貨")} 件 " +
            $"（交換に使える通貨 {usable.Count} 件 / 特殊通貨の表 {this.specials.Entries.Count} 件 " +
            $"クライアント由来={this.specials.ResolvedFromClient}）" +
            $" [{string.Join(", ", list.Select(x => x.Name))}]";

        if (summary != this.lastLogged)
        {
            this.lastLogged = summary;
            this.anomalyLog.Info("Currency", summary);
        }

        // **空は控えない。**
        //
        // ログイン直後はシートを読めず、一覧が 0 件になることがある。
        // 控えると、その 1 秒のあいだ「通貨が 1 つも無い」画面になる。
        // 空でなければ控えて、毎フレームの作り直しをやめる。
        if (list.Count > 0)
        {
            this.choiceCache = list;
            this.choiceCacheUntil = DateTime.UtcNow + ChoiceCacheDuration;
        }

        return list;
    }

    /// <summary>
    /// 選べる通貨の一覧に、そのプリセットがいま選んでいるものを必ず含めたもの。
    ///
    /// <b>選択中のものが一覧に無いと、黙って別の通貨に書き換わる。</b>
    /// 画面は一覧から番号で選ばせており、見つからないときは先頭
    /// （＝トームストーン）を指す。その状態で何か操作すると、
    /// 利用者が選んだ覚えのない通貨が保存されてしまう。
    ///
    /// 一覧から消えるのは、交換所から無くなった通貨のほかに、
    /// 対応表や交換の一覧をまだ作れていない場合もある。
    /// いずれにせよ、選択中のものを黙って捨ててよい理由にはならない。
    /// </summary>
    public IReadOnlyList<CurrencyChoice> ListChoicesIncluding(ExchangePreset preset)
    {
        var list = this.ListChoices();

        if (!this.TryResolve(preset, out var currentItemId) || currentItemId == 0)
        {
            return list;
        }

        if (IndexOf(list, preset) >= 0)
        {
            return list;
        }

        // 名前は特殊通貨の表から引く。トームストーンはそこに載っていないので、
        // 見つからなければアイテムのシートから引き直す。
        // どちらでも引けなければ ItemId をそのまま出す。名前が空だと
        // 画面側の絞り込み（名前が空のものを落とす）で、また消えてしまう。
        var name = this.specials.ListCurrencies().FirstOrDefault(x => x.ItemId == currentItemId).Name;

        if (string.IsNullOrEmpty(name))
        {
            name = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(currentItemId)?.Name.ExtractText() ?? string.Empty;
        }

        var result = new List<CurrencyChoice>(list)
        {
            new(
                preset.CurrencyItemId != 0 ? 0 : preset.TomestonesRowId,
                currentItemId,
                string.IsNullOrEmpty(name) ? $"ItemId {currentItemId}" : name,
                "選択中（一覧に無い）"),
        };

        return result;
    }

    /// <summary>通貨の名前。見つからなければ ItemId をそのまま返す。</summary>
    public string NameOf(uint itemId)
        => this.ListChoices().FirstOrDefault(x => x.ItemId == itemId)?.Name ?? $"ItemId {itemId}";

    /// <summary>
    /// アイテムそのものを通貨として使う交換の、その通貨。
    ///
    /// <b>バイカラージェムのような通貨は、特殊通貨の表に載らない。</b>
    /// SpecialShop のコストには 2 つの形があり、
    /// <list type="bullet">
    /// <item>特殊通貨バケット（CostType==3）— スクリップなど。
    ///       入っているのは通貨そのものではなくバケットの番号で、
    ///       <see cref="SpecialCurrencyMap"/> が ItemId へ直す</item>
    /// <item>ふつうのアイテム — ItemCost に ItemId がそのまま入る。
    ///       バイカラージェム、モブハントの戦利品、各蛮族の貨幣など</item>
    /// </list>
    /// 後者は特殊通貨の表に載らないため、これまで一覧へ出てこなかった。
    ///
    /// <b>種類は決め打ちしない。</b>
    /// 交換所のコストとして実際に使われているアイテムのうち、
    /// ゲームが「通貨」に分類しているもの（ItemUICategory==100）を採る。
    /// 実測では 33 種で、バイカラージェム・モブハントの戦利品・
    /// トロフィークリスタル・各蛮族の貨幣などが並ぶ（ver 2026.09.15）。
    /// パッチで増えても、こちらを直さずに追従する。
    /// </summary>
    private IReadOnlyList<(uint ItemId, string Name)> ListItemCurrencies()
    {
        if (this.itemCurrencyCache is { } cached)
        {
            return cached;
        }

        var found = new List<(uint ItemId, string Name)>();

        try
        {
            var shops = Svc.Data.GetExcelSheet<SpecialShop>();
            var items = Svc.Data.GetExcelSheet<Item>();

            if (shops is null || items is null)
            {
                // **空を控えない。** 読めなかっただけで、無いとは限らない。
                return found;
            }

            var seen = new HashSet<uint>();

            foreach (var shop in shops)
            {
                foreach (var entry in shop.Item)
                {
                    foreach (var cost in entry.ItemCosts)
                    {
                        var costItemId = cost.ItemCost.RowId;

                        if (costItemId == 0 || !seen.Add(costItemId))
                        {
                            continue;
                        }

                        var row = items.GetRowOrDefault(costItemId);

                        // ゲームが「通貨」に分類しているものだけ。
                        // 素材や装備をコストにする交換も多いので、絞らないと
                        // 一覧が数百件になって選べなくなる。
                        if (row is null || row.Value.ItemUICategory.RowId != CurrencyUiCategory)
                        {
                            continue;
                        }

                        var name = row.Value.Name.ExtractText();

                        if (!string.IsNullOrEmpty(name))
                        {
                            found.Add((costItemId, name));
                        }
                    }
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Currency", $"アイテム通貨の一覧を作れませんでした: {ex.Message}");
            return found;
        }

        // 空なら控えない。次に呼ばれたときにやり直す。
        if (found.Count > 0)
        {
            this.itemCurrencyCache = found;
        }

        return found;
    }

    /// <summary>ItemUICategory の「通貨」。スクリップもバイカラージェムもここに入る。</summary>
    private const uint CurrencyUiCategory = 100;

    /// <summary>
    /// アイテム通貨の一覧。シートを総なめするので一度だけ作る。
    ///
    /// パッチを跨ぐとプラグインごと読み込み直されるため、
    /// 実行中に中身が変わることはない。
    /// </summary>
    private IReadOnlyList<(uint ItemId, string Name)>? itemCurrencyCache;

    /// <summary>
    /// いま交換に使える通貨。
    ///
    /// アイテム交換画面に並ぶ品のコストとして実際に使われているものだけを採る。
    /// </summary>
    private HashSet<uint> UsableCurrencies()
    {
        var set = new HashSet<uint>();

        foreach (var category in this.inclusionShops.ListCategories())
        {
            foreach (var series in category.Series)
            {
                foreach (var currencyItemId in series.Currencies)
                {
                    set.Add(currencyItemId);
                }
            }
        }

        return set;
    }

    /// <summary>プリセットへ選択を書き込む。</summary>
    public static void Apply(ExchangePreset preset, CurrencyChoice choice)
    {
        if (choice.TomestonesRowId != 0)
        {
            // スロット参照にしておくと、パッチで中身が入れ替わっても追従できる。
            preset.TomestonesRowId = choice.TomestonesRowId;
            preset.CurrencyItemId = 0;
            return;
        }

        // **スロット番号は消さない。**
        //
        // 消したくなるが、消してはいけない。
        // <b>スロット 0 は「未設定」ではなく、哲学トームストーンを指す。</b>
        // 0 にすると TryResolve と IndexOf が、スクリップを選んでいるのに
        // 哲学を選んでいると読む余地が生まれる。
        //
        // 残っていても CurrencyItemId が優先されるので害は無い。
        preset.CurrencyItemId = choice.ItemId;
    }

    /// <summary>いま選ばれているものが一覧の何番目かを返す。無ければ -1。</summary>
    public static int IndexOf(IReadOnlyList<CurrencyChoice> choices, ExchangePreset preset)
    {
        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];

            if (preset.CurrencyItemId != 0)
            {
                if (choice.TomestonesRowId == 0 && choice.ItemId == preset.CurrencyItemId)
                {
                    return i;
                }

                continue;
            }

            if (choice.TomestonesRowId == preset.TomestonesRowId)
            {
                return i;
            }
        }

        return -1;
    }
}
