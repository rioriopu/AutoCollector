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
/// 絞り込みは TerritoryIntendedUse == 1（通常のフィールド）。
/// ただしこの条件には、ウルヴズジェイルのように FATE が湧かない
/// フィールドも混ざる。<b>「フィールドである」ことと「FATE が湧く」ことは別。</b>
/// シートから後者を判定する方法が見つかっていないため、
/// 一覧には出したうえで、実際に周回して FATE が無ければ
/// 次のマップへ移る（FateSwapZoneWhenEmpty）ことで吸収する。
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
