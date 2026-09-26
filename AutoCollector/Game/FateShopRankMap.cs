using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>
/// バイカラージェムの交換品に「何ランクで解放されるか」を割り当てる。
///
/// <b>ゲームはこれを 2 通りの方法で持っている。</b>実測（ver 2026.09.15）。
///
/// <b>1. ショップを分ける方式（暁月・黄金）</b>
/// 同じ交易商に、ランクごとの SpecialShop が用意されている。
/// <c>FateShop</c> シートの 1 行が 1 人の交易商で、列にショップ番号が並ぶ。
/// <b>その並び順が、そのままランクの低い順。</b>
///
/// 品揃えは完全な包含関係になっている。ヤクテル樹海の例：
///   1770743 (4 件) ⊂ 1770744 (5 件) ⊂ 1770745 (9 件)
/// FateShop に載る 16 人すべてでこの関係を確認した。
///
/// よって「n 番目のショップにしか無い品」は「n 段階目で解放される品」。
///
/// <b>2. エントリごとに印をつける方式（漆黒）</b>
/// 漆黒は 1 マップ 1 ショップで、<c>SpecialShop.Item[].Quest</c> に
/// <b>Quest シートに存在しない小さな値</b>（80〜91）が入っている。
/// マップごとに 2 つずつ連番で振られている（レイクランド 80/81、
/// イル・メグ 82/83、ラケティカ 84/85、アム・アレーン 86/87、
/// コルシア島 88/89、テンペスト 90/91）。
///
/// 値が小さいほうが先に解放されると読んで段階を決める。
///
/// <b>ここは推測が残っている。</b>
/// 「1 段階目 = ランク 2」のように、段階と画面のランク数字の対応は
/// ゲームデータからは読めなかった。そこで段階の順序だけを使い、
/// <b>実際のランクとの対応は実機で確かめる前提の推定値</b>として扱う。
/// 推定であることは UI にも出す。
/// </summary>
public sealed class FateShopRankMap(AnomalyLog anomalyLog)
{
    /// <summary>SpecialShop を表す EventHandler の種類。</summary>
    private const uint SpecialShopHandlerType = 0x001B;

    /// <summary>
    /// 漆黒で使われている「ランク印」の上限。
    /// Quest シートの実データは 65000 以上の値なので、
    /// これより小さければクエストではなく印だと判断できる。
    /// </summary>
    private const uint RankMarkerMax = 1000;

    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>(ショップ番号, 報酬 ItemId) → 必要な段階（1 から始まる。0 は条件なし）。</summary>
    private Dictionary<(uint Shop, uint Reward), uint>? rankByEntry;

    /// <summary>ショップ番号 → そのショップ自体が何段階目か（1 から始まる）。</summary>
    private Dictionary<uint, uint>? stageByShop;

    /// <summary>この (ショップ, 報酬) に要る段階。0 なら条件なし。</summary>
    public uint RequiredStage(uint shopId, uint rewardItemId)
    {
        this.EnsureBuilt();
        return this.rankByEntry!.GetValueOrDefault((shopId, rewardItemId));
    }

    /// <summary>このショップ自体が何段階目か。0 なら段階分けされていない。</summary>
    public uint StageOfShop(uint shopId)
    {
        this.EnsureBuilt();
        return this.stageByShop!.GetValueOrDefault(shopId);
    }

    /// <summary>
    /// F.A.T.E達成度の通貨（バイカラージェムなど）を使うショップか。
    ///
    /// 通貨の ItemId は <c>FateTokenType</c> シートから引く。
    /// バイカラージェムの番号をコードへ埋め込まない
    /// （docs/00_設計決定.md の D-2）。
    /// </summary>
    private static bool UsesFateToken(SpecialShop shop)
    {
        var tokens = FateTokenItemIds;
        if (tokens.Count == 0)
        {
            return false;
        }

        foreach (var entry in shop.Item)
        {
            foreach (var cost in entry.ItemCosts)
            {
                if (tokens.Contains(cost.ItemCost.RowId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<uint>? fateTokenItemIds;

    /// <summary>F.A.T.E達成度の通貨の ItemId。FateTokenType シートから引く。</summary>
    private static HashSet<uint> FateTokenItemIds
    {
        get
        {
            if (fateTokenItemIds is not null)
            {
                return fateTokenItemIds;
            }

            fateTokenItemIds = [];

            try
            {
                var sheet = Svc.Data.Excel.GetSheet<RawRow>(name: "FateTokenType");
                if (sheet is not null)
                {
                    foreach (var row in sheet)
                    {
                        var itemId = Convert.ToUInt32(row.ReadColumn(0));
                        if (itemId != 0)
                        {
                            fateTokenItemIds.Add(itemId);
                        }
                    }
                }
            }
            catch
            {
                // 読めなければ空のまま。段階が付かないだけで、動作は止まらない。
            }

            return fateTokenItemIds;
        }
    }

    private void EnsureBuilt()
    {
        if (this.rankByEntry is not null)
        {
            return;
        }

        this.rankByEntry = [];
        this.stageByShop = [];

        try
        {
            this.BuildFromFateShop();
            this.BuildFromQuestMarkers();

            this.anomalyLog.Info(
                "FateRank",
                $"F.A.T.E達成度の条件を {this.rankByEntry.Count} 件読みました" +
                $"（段階分けされたショップ {this.stageByShop.Count} 件）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("FateRank", $"ランク条件を組み立てられませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 暁月・黄金：FateShop の列の並び順から段階を割り出す。
    /// </summary>
    private void BuildFromFateShop()
    {
        var sheet = Svc.Data.Excel.GetSheet<RawRow>(name: "FateShop");
        if (sheet is null)
        {
            return;
        }

        var shops = Svc.Data.GetExcelSheet<SpecialShop>();
        if (shops is null)
        {
            return;
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
            {
                continue;
            }

            // その交易商が持つショップを、列の並び順に集める。
            // 同じ番号が続けて入っていることがあるので、重複は落とす。
            var ordered = new List<uint>();
            for (var column = 0; column < sheet.Columns.Count; column++)
            {
                uint value;
                try
                {
                    value = Convert.ToUInt32(row.ReadColumn(column));
                }
                catch
                {
                    continue;
                }

                if ((value >> 16) != SpecialShopHandlerType)
                {
                    continue;
                }

                if (!ordered.Contains(value))
                {
                    ordered.Add(value);
                }
            }

            if (ordered.Count <= 1)
            {
                // 段階分けされていない（都市の交易商など）。
                continue;
            }

            // 段階ごとに「前の段階に無かった品」を拾う。
            var seen = new HashSet<uint>();
            for (var stage = 0; stage < ordered.Count; stage++)
            {
                var shopId = ordered[stage];
                this.stageByShop![shopId] = (uint)(stage + 1);

                if (!shops.TryGetRow(shopId, out var shop))
                {
                    continue;
                }

                foreach (var entry in shop.Item)
                {
                    var reward = entry.ReceiveItems.FirstOrDefault().Item.RowId;
                    if (reward == 0)
                    {
                        continue;
                    }

                    if (seen.Add(reward))
                    {
                        // この段階で初めて出てきた品。
                        // 1 段階目（stage==0）は最初から買えるので条件なし。
                        if (stage > 0)
                        {
                            this.rankByEntry![(shopId, reward)] = (uint)(stage + 1);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 漆黒：エントリの Quest 列に入った印から段階を割り出す。
    ///
    /// 印は Quest シートに存在しない小さな値。マップごとに連番なので、
    /// そのマップの中で小さい順に 2 段階目・3 段階目と割り当てる。
    /// </summary>
    private void BuildFromQuestMarkers()
    {
        var shops = Svc.Data.GetExcelSheet<SpecialShop>();
        var quests = Svc.Data.GetExcelSheet<Quest>();
        if (shops is null || quests is null)
        {
            return;
        }

        foreach (var shop in shops)
        {
            // このショップで使われている印を集める。
            var markers = new SortedSet<uint>();
            foreach (var entry in shop.Item)
            {
                var marker = entry.Quest.RowId;
                if (marker == 0 || marker >= RankMarkerMax)
                {
                    continue;
                }

                // Quest シートに実在するならクエスト条件。印ではない。
                if (quests.HasRow(marker))
                {
                    continue;
                }

                markers.Add(marker);
            }

            if (markers.Count == 0)
            {
                continue;
            }

            // **印の意味はショップによって違う。**
            // 実測（ver 2026.09.15）すると、印を持つショップ 9 件のうち
            // 6 件がバイカラージェムの漆黒マップだが、残りは
            // 「チョコボ教本の交換」のように、ランクとは無関係のものだった。
            // F.A.T.E達成度の条件として扱ってよいのは、
            // その通貨を使うショップだけ。
            if (!UsesFateToken(shop))
            {
                continue;
            }

            // 小さい順に 2 段階目から割り当てる。
            // 印の無いエントリ（Quest==0）は最初から買えるので 1 段階目。
            var stageOf = markers
                .Select((marker, index) => (marker, stage: (uint)(index + 2)))
                .ToDictionary(x => x.marker, x => x.stage);

            foreach (var entry in shop.Item)
            {
                var marker = entry.Quest.RowId;
                if (!stageOf.TryGetValue(marker, out var stage))
                {
                    continue;
                }

                var reward = entry.ReceiveItems.FirstOrDefault().Item.RowId;
                if (reward != 0)
                {
                    this.rankByEntry![(shop.RowId, reward)] = stage;
                }
            }
        }
    }
}
