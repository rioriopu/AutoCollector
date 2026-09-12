using System;
using System.Collections.Generic;

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
public sealed class CurrencyCatalog(TomestoneService tomestones, SpecialCurrencyMap specials)
{
    private readonly TomestoneService tomestones = tomestones;
    private readonly SpecialCurrencyMap specials = specials;

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

    /// <summary>選べる通貨の一覧。</summary>
    public IReadOnlyList<CurrencyChoice> ListChoices()
    {
        var list = new List<CurrencyChoice>();

        foreach (var slot in this.tomestones.ListSlots())
        {
            list.Add(new CurrencyChoice(slot.TomestonesRowId, slot.ItemId, slot.Name, "アラガントームストーン"));
        }

        foreach (var (itemId, name) in this.specials.ListCurrencies())
        {
            list.Add(new CurrencyChoice(0, itemId, name, "スクリップなど"));
        }

        return list;
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
