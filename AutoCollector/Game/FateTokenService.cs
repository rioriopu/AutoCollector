using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>FATE の通貨を扱う交換所 1 件。</summary>
/// <param name="NpcDataId">窓口の ENpcBase 行番号。FateShop の行番号と同じ値になる。</param>
/// <param name="SpecialShopIds">その窓口がぶら下げている SpecialShop の行番号。</param>
public sealed record FateShopEntry(uint NpcDataId, IReadOnlyList<uint> SpecialShopIds);

/// <summary>
/// FATE で稼げる通貨と、その交換窓口をゲームデータから引く。
///
/// <b>アイテム ID も NPC ID も埋め込まない。</b>
/// docs/16 の 2-2（交換情報をハードコードしない）に従う。
///
/// <code>
/// FateTokenType.Currency   → FATE で得られる通貨（バイカラージェム等）
/// FateShop.RowId           → 窓口の ENpcBase 行番号
/// FateShop.SpecialShop[]   → その窓口の SpecialShop
/// </code>
///
/// <b>FateShop の行番号は ENpcBase の行番号と同じ。</b>
/// 実データで確認した（ベリル = ENpcBase 1049082 = FateShop 1049082 → SpecialShop 1770746）。
/// この対応があるため、NPC からシートを引くのに CustomTalk を辿る必要がない。
///
/// <b>ベリルの CustomTalk は中身が空だった。</b>
/// ExchangeResolver が窓口を辿る経路（CustomTalk → SpecialShop）では
/// この窓口へ到達できない。FateShop を直接見るしかない。
/// </summary>
public sealed class FateTokenService(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>FATE で得られる通貨の ItemId。</summary>
    private HashSet<uint>? currencies;

    /// <summary>通貨 ItemId → その通貨を扱う窓口。</summary>
    private Dictionary<uint, List<FateShopEntry>>? shopsByCurrency;

    /// <summary>この通貨は FATE で稼げるか。</summary>
    public bool IsFateCurrency(uint currencyItemId)
        => currencyItemId != 0 && this.Currencies().Contains(currencyItemId);

    /// <summary>FATE で得られる通貨の一覧。</summary>
    public IReadOnlySet<uint> Currencies()
    {
        if (this.currencies is not null)
        {
            return this.currencies;
        }

        var set = new HashSet<uint>();

        try
        {
            var sheet = Svc.Data.GetExcelSheet<FateTokenType>();
            if (sheet is null)
            {
                // 読み込みの途中は空で返ることがある。控えずに次の呼び出しで引き直す。
                return set;
            }

            foreach (var row in sheet)
            {
                var id = row.Currency.RowId;
                if (id != 0)
                {
                    set.Add(id);
                }
            }

            if (set.Count == 0)
            {
                // 1 件も取れないのは読み込みが済んでいないとき。控えると空のまま固定される。
                return set;
            }

            this.currencies = set;
            return set;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Fate", $"FATE の通貨一覧を作れませんでした: {ex.Message}");
            return set;
        }
    }

    /// <summary>
    /// この通貨を扱う交換窓口。見つからなければ空。
    ///
    /// 同じ通貨の窓口が複数あることがある（バイカラージェムは拡張ごとに窓口が違う）。
    /// どれを使うかは呼び出し側が決める。
    /// </summary>
    public IReadOnlyList<FateShopEntry> ShopsFor(uint currencyItemId)
        => this.Build().TryGetValue(currencyItemId, out var list) ? list : [];

    /// <summary>この NPC は FATE の通貨を扱う窓口か。</summary>
    public bool IsFateShopNpc(uint npcDataId)
    {
        if (npcDataId == 0)
        {
            return false;
        }

        foreach (var list in this.Build().Values)
        {
            foreach (var entry in list)
            {
                if (entry.NpcDataId == npcDataId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>この NPC がぶら下げている SpecialShop。窓口でなければ空。</summary>
    public IReadOnlyList<uint> SpecialShopsOf(uint npcDataId)
    {
        if (npcDataId == 0)
        {
            return [];
        }

        try
        {
            var sheet = Svc.Data.GetExcelSheet<FateShop>();
            var row = sheet?.GetRowOrDefault(npcDataId);
            if (row is null)
            {
                return [];
            }

            var ids = new List<uint>();
            foreach (var s in row.Value.SpecialShop)
            {
                if (s.RowId != 0 && !ids.Contains(s.RowId))
                {
                    ids.Add(s.RowId);
                }
            }

            return ids;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Fate", $"ENpc {npcDataId} の FateShop を引けませんでした: {ex.Message}");
            return [];
        }
    }

    /// <summary>作り直す。ゲームデータを読み直したときに呼ぶ。</summary>
    public void Invalidate()
    {
        this.currencies = null;
        this.shopsByCurrency = null;
    }

    /// <summary>通貨 → 窓口の索引を作る。</summary>
    private Dictionary<uint, List<FateShopEntry>> Build()
    {
        if (this.shopsByCurrency is not null)
        {
            return this.shopsByCurrency;
        }

        var map = new Dictionary<uint, List<FateShopEntry>>();

        try
        {
            var fateShops = Svc.Data.GetExcelSheet<FateShop>();
            var specialShops = Svc.Data.GetExcelSheet<SpecialShop>();
            if (fateShops is null || specialShops is null)
            {
                return map;
            }

            var fateCurrencies = this.Currencies();
            if (fateCurrencies.Count == 0)
            {
                // 通貨が引けていないうちは索引を作っても空になる。控えない。
                return map;
            }

            foreach (var shopRow in fateShops)
            {
                var npcId = shopRow.RowId;
                var shopIds = new List<uint>();

                foreach (var s in shopRow.SpecialShop)
                {
                    if (s.RowId != 0 && !shopIds.Contains(s.RowId))
                    {
                        shopIds.Add(s.RowId);
                    }
                }

                if (shopIds.Count == 0)
                {
                    continue;
                }

                // その窓口が実際に使う通貨を、SpecialShop のコストから拾う。
                // FateShop にぶら下がっていても、扱う通貨は窓口ごとに違う。
                var used = new HashSet<uint>();

                foreach (var shopId in shopIds)
                {
                    var shop = specialShops.GetRowOrDefault(shopId);
                    if (shop is null)
                    {
                        continue;
                    }

                    foreach (var item in shop.Value.Item)
                    {
                        foreach (var cost in item.ItemCosts)
                        {
                            var id = cost.ItemCost.RowId;
                            if (id != 0 && fateCurrencies.Contains(id))
                            {
                                used.Add(id);
                            }
                        }
                    }
                }

                if (used.Count == 0)
                {
                    continue;
                }

                var entry = new FateShopEntry(npcId, shopIds);
                foreach (var currency in used)
                {
                    if (!map.TryGetValue(currency, out var list))
                    {
                        list = [];
                        map[currency] = list;
                    }

                    list.Add(entry);
                }
            }

            if (map.Count == 0)
            {
                return map;
            }

            this.shopsByCurrency = map;
            return map;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Fate", $"FATE の交換窓口を作れませんでした: {ex.Message}");
            return map;
        }
    }
}
