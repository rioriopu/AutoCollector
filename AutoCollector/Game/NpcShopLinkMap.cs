using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using Newtonsoft.Json;

namespace AutoCollector.Game;

/// <summary>npc_shop_links.json の 1 件。</summary>
public sealed class NpcShopLink
{
    [JsonProperty("npcId")]
    public uint NpcId { get; set; }

    [JsonProperty("npcName")]
    public string NpcName { get; set; } = string.Empty;

    [JsonProperty("territoryId")]
    public uint TerritoryId { get; set; }

    [JsonProperty("note")]
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// 都市の集約交易商か。
    ///
    /// true の交易商は、その拡張の 6 マップすべてがシェアF.A.T.E の
    /// ランク最大でないと利用できない。条件を満たさないうちに行っても
    /// 交換できないため、選択肢から外す必要がある。
    /// </summary>
    [JsonProperty("isCityShop")]
    public bool IsCityShop { get; set; }

    [JsonProperty("shops")]
    public List<uint> Shops { get; set; } = [];
}

public sealed class NpcShopLinkFile
{
    [JsonProperty("verifiedGameVersion")]
    public string? VerifiedGameVersion { get; set; }

    [JsonProperty("links")]
    public List<NpcShopLink> Links { get; set; } = [];
}

/// <summary>
/// ゲームのシートから NPC へ辿れない SpecialShop を、NPC に結びつける。
///
/// <b>なぜ要るか。</b>
/// SpecialShop へは ふつう <c>ENpcBase.ENpcData</c> から
/// 直接 / PreHandler / TopicSelect / CustomTalk / InclusionShop と辿れる。
/// ところが一部のショップは、どの経路からも辿れない。
///
/// 実測（ver 2026.09.15）した例：
/// 広域交易商 ベリル（ENpc 1049082・ソリューション・ナイン）の
/// <c>ENpcData</c> には CustomTalk <c>0x000B02D4</c> しか無く、その CustomTalk は
/// <c>Script</c> も <c>SpecialLinks</c> も空。PreHandler / TopicSelect /
/// InclusionShopSeries のいずれにも該当ショップは載っていない。
///
/// <b>シートに情報が無いので、外部データで補うしかない。</b>
/// これは <see cref="SpecialCurrencyMap"/> と同じ考え方
/// （シートに無いものだけを JSON で持つ）。
///
/// <b>座標はここに書かない。</b>
/// NPC の番号さえ分かれば、<see cref="NpcLocationService"/> が
/// Level シートや LGB から引ける。二重に持つと食い違う。
/// </summary>
public sealed class NpcShopLinkMap
{
    private const string FileName = "npc_shop_links.json";

    private readonly AnomalyLog anomalyLog;

    /// <summary>ショップ番号 → それを開く NPC の番号。</summary>
    private readonly Dictionary<uint, List<uint>> shopToNpcs = [];

    /// <summary>都市の集約交易商の NPC 番号。</summary>
    private readonly HashSet<uint> cityNpcs = [];

    /// <summary>都市の交易商 → 同じ拡張のマップ 1 つの territory。</summary>
    private readonly Dictionary<uint, uint> citySampleZone = [];

    public NpcShopLinkMap(AnomalyLog anomalyLog)
    {
        this.anomalyLog = anomalyLog;
        this.Load();
    }

    /// <summary>読み込んだ結びつきの数（ショップ単位）。</summary>
    public int Count => this.shopToNpcs.Count;

    /// <summary>
    /// このショップを開く NPC。結びつきが無ければ空。
    /// </summary>
    public IReadOnlyList<uint> NpcsFor(uint shopRowId)
        => this.shopToNpcs.TryGetValue(shopRowId, out var npcs) ? npcs : [];

    /// <summary>
    /// この NPC は都市の集約交易商か。
    /// true なら、その拡張の 6 マップすべてがランク最大でないと利用できない。
    /// </summary>
    public bool IsCityNpc(uint npcId) => this.cityNpcs.Contains(npcId);

    /// <summary>
    /// 都市の交易商と同じ拡張にある、マップ 1 つの territory を返す。
    ///
    /// <b>なぜ要るか。</b>
    /// 解放条件は「その拡張の 6 マップすべてがランク最大か」で決まる。
    /// ところが都市そのもの（クリスタリウム 819 や ラザハン 963）は
    /// シェアF.A.T.E のマップ 18 件に入っていないため、
    /// 都市の territory を渡しても、どの拡張か分からない。
    ///
    /// そこで同じ拡張のマップを 1 つ借りて渡す。
    ///
    /// <b>拡張の見分け方。</b>
    /// ショップ番号は拡張ごとにまとまっている（漆黒 176995x、暁月 177045x、
    /// 黄金 177073x）。番号が近いものを同じ拡張とみなす。
    /// マップ側の交易商の territory を返せば、それで判定できる。
    /// 番号そのものは書かない（新しい拡張でも同じ仕組みで動く）。
    /// </summary>
    public uint SampleZoneForCityNpc(uint cityNpcId)
        => this.citySampleZone.GetValueOrDefault(cityNpcId);

    private void Load()
    {
        try
        {
            var file = DataFileLoader.Load<NpcShopLinkFile>(FileName, this.anomalyLog);

            if (file.Links.Count == 0)
            {
                // 空でも異常ではない。結びつきが要る NPC がいないだけ。
                return;
            }

            foreach (var link in file.Links)
            {
                if (link.NpcId == 0)
                {
                    continue;
                }

                if (link.IsCityShop)
                {
                    this.cityNpcs.Add(link.NpcId);
                }

                foreach (var shop in link.Shops)
                {
                    if (shop == 0)
                    {
                        continue;
                    }

                    if (!this.shopToNpcs.TryGetValue(shop, out var npcs))
                    {
                        npcs = [];
                        this.shopToNpcs[shop] = npcs;
                    }

                    if (!npcs.Contains(link.NpcId))
                    {
                        npcs.Add(link.NpcId);
                    }
                }
            }

            this.BuildCitySampleZones(file.Links);

            var names = string.Join(
                "、",
                file.Links.Where(x => !string.IsNullOrEmpty(x.NpcName)).Select(x => x.NpcName));

            this.anomalyLog.Info(
                "NpcShop",
                $"シートから辿れない交換所の結びつきを {this.shopToNpcs.Count} 件読みました" +
                (string.IsNullOrEmpty(names) ? string.Empty : $"（{names}）"));
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("NpcShop", $"{FileName} を読めませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 都市の交易商ごとに、同じ拡張のマップを 1 つ選んでおく。
    ///
    /// ショップ番号は拡張ごとにまとまっているので、
    /// 都市のショップ番号にいちばん近いマップの交易商を相棒にする。
    /// 拡張の番号帯を書かずに済むので、新しい拡張でもそのまま動く。
    /// </summary>
    private void BuildCitySampleZones(List<NpcShopLink> links)
    {
        var mapLinks = links
            .Where(x => !x.IsCityShop && x.TerritoryId != 0 && x.Shops.Count > 0)
            .ToList();

        if (mapLinks.Count == 0)
        {
            return;
        }

        foreach (var city in links.Where(x => x.IsCityShop && x.Shops.Count > 0))
        {
            var cityShop = city.Shops[0];

            // ショップ番号の差がいちばん小さいマップを、同じ拡張とみなす。
            var nearest = mapLinks
                .OrderBy(m => m.Shops.Min(s => s > cityShop ? s - cityShop : cityShop - s))
                .First();

            this.citySampleZone[city.NpcId] = nearest.TerritoryId;
        }
    }
}
