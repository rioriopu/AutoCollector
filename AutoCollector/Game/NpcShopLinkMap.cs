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
}
