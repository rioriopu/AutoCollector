using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>周回できるマップ 1 件。</summary>
/// <param name="TerritoryId">TerritoryType の RowId。</param>
/// <param name="Name">表示名。</param>
/// <param name="ExVersionId">拡張の番号。</param>
public sealed record FateZone(uint TerritoryId, string Name, uint ExVersionId);

/// <summary>拡張 1 件と、その配下のマップ。</summary>
/// <param name="ExVersionId">拡張の番号。</param>
/// <param name="Name">拡張の名前。</param>
/// <param name="Zones">配下のマップ。</param>
public sealed record FateExpansion(uint ExVersionId, string Name, IReadOnlyList<FateZone> Zones);

/// <summary>
/// FATE が湧きうるマップの一覧を、ゲームデータから組み立てる。
///
/// docs/00_設計決定.md の D-2 に従い、一覧を埋め込まずシートから引く。
/// 新しい拡張が来ても、こちらを直さずに選択肢へ現れる。
///
/// 絞り込みは TerritoryIntendedUse == 1（通常のフィールド）かつ Mount 可。
///
/// <b>IntendedUse だけでは足りない。</b>
/// この条件には「ウルヴズジェイル係船場」（250）のような
/// PvP の待機場所が混ざる。フィールド扱いだが FATE は湧かない。
///
/// シートで見分けられる。この場所だけ <c>Mount</c> が false で、
/// Bg も <c>.../pvp/...</c> になっている。
/// 実データ（ver 2026.09.15）で確かめたところ、
/// IntendedUse==1 の 48 件のうち Mount==false はこの 1 件だけで、
/// 残る 47 件はすべて騎乗できる通常のフィールドだった。
///
/// 騎乗できないフィールドを周回対象にする意味は無い
/// （FATE の間を移動できない）ため、この条件で落とす。
/// 番号は埋め込まない。docs/00_設計決定.md の D-2 に従う。
/// </summary>
public sealed class FateZoneCatalog(AnomalyLog anomalyLog)
{
    /// <summary>通常のフィールドを表す TerritoryIntendedUse の値。</summary>
    private const uint FieldIntendedUse = 1;

    private readonly AnomalyLog anomalyLog = anomalyLog;

    private IReadOnlyList<FateExpansion>? cache;

    /// <summary>拡張ごとにまとめたマップの一覧。初回だけ組み立てて使い回す。</summary>
    public IReadOnlyList<FateExpansion> ListExpansions()
    {
        if (this.cache is not null)
        {
            return this.cache;
        }

        try
        {
            var territories = Svc.Data.GetExcelSheet<TerritoryType>();
            var versions = Svc.Data.GetExcelSheet<ExVersion>();

            if (territories is null)
            {
                return this.cache = [];
            }

            var zones = territories
                .Where(t => t.IsInUse
                         && t.TerritoryIntendedUse.RowId == FieldIntendedUse
                         && t.Mount
                         && !string.IsNullOrWhiteSpace(t.PlaceName.ValueNullable?.Name.ExtractText()))
                .Select(t => new FateZone(
                    t.RowId,
                    t.PlaceName.ValueNullable?.Name.ExtractText() ?? $"territory {t.RowId}",
                    t.ExVersion.RowId))
                .ToList();

            this.cache = zones
                .GroupBy(z => z.ExVersionId)
                .OrderBy(g => g.Key)
                .Select(g => new FateExpansion(
                    g.Key,
                    versions?.GetRowOrDefault(g.Key)?.Name.ExtractText() is { Length: > 0 } n ? n : $"拡張 {g.Key}",
                    g.OrderBy(z => z.TerritoryId).ToList()))
                .ToList();

            return this.cache;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Fate", $"マップの一覧を作れませんでした: {ex.Message}");
            return this.cache = [];
        }
    }

    /// <summary>
    /// 選んだマップを、画面に並んでいる順へ整える。
    ///
    /// <b>保存の順は、押した順になっている。</b>
    /// 設定は `FateZones.Add` で足していくので、先にコザマル・カを選んでから
    /// 拡張をまとめて選ぶと、コザマル・カが先頭に残る。
    /// 巡回は保存の順で回るため、画面に並べた順とは違う巡り方になっていた。
    ///
    /// 画面の順（拡張の番号順 → その中は TerritoryId 順）へ揃える。
    /// 一覧に無いマップ（選択後に対象から外れたもの）は落とす。
    /// 重複も取り除く。
    /// </summary>
    public IReadOnlyList<uint> SortByDisplayOrder(IEnumerable<uint> chosen)
    {
        var want = new HashSet<uint>(chosen);
        var ordered = new List<uint>(want.Count);

        foreach (var ex in this.ListExpansions())
        {
            foreach (var zone in ex.Zones)
            {
                if (want.Remove(zone.TerritoryId))
                {
                    ordered.Add(zone.TerritoryId);
                }
            }
        }

        return ordered;
    }

    /// <summary>TerritoryId から表示名を引く。</summary>
    public string NameOf(uint territoryId)
    {
        foreach (var ex in this.ListExpansions())
        {
            foreach (var z in ex.Zones)
            {
                if (z.TerritoryId == territoryId)
                {
                    return z.Name;
                }
            }
        }

        return NpcLocationService.GetTerritoryName(territoryId);
    }

    /// <summary>
    /// バイカラージェムが手に入る拡張か。
    ///
    /// 漆黒（3）以降の FATE でだけ手に入る。
    /// </summary>
    public static bool YieldsBicolorGems(uint exVersionId) => exVersionId >= 3;
}
