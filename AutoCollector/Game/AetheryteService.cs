using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>テレポート先のエーテライト。</summary>
public sealed record TeleportTarget(uint AetheryteId, byte SubIndex, uint TerritoryId, string Name);

/// <summary>
/// 目的エリアに直接飛べないときの、都市の玄関口を経由する経路。
///
/// ウルダハ：ザル回廊のように、エーテライトが無くエーテライト網でしか行けないエリアがある。
/// そこへは「同じ網の親エーテライトへ飛び、そこから網で移動する」必要がある。
/// </summary>
/// <param name="Hub">親エーテライト。ここへテレポートする。</param>
/// <param name="ShardAetheryteRowId">目的エリアにあるエーテライト網の出口。Lifestream へ渡す。</param>
/// <param name="ShardName">出口の名前。表示とログに使う。</param>
/// <param name="TargetTerritoryId">最終的に着きたいエリア。</param>
public sealed record AethernetRoute(
    TeleportTarget Hub,
    uint ShardAetheryteRowId,
    string ShardName,
    uint TargetTerritoryId);

/// <summary>
/// テレポート先のエーテライトを決める。
///
/// Aetheryte シートの Level コレクションは空であることが多く、
/// エーテライトの座標をシートから引くことはできない。
/// そのため「NPC に一番近いエーテライト」を計算する方式は取らない。
///
/// 代わりに Svc.AetheryteList（キャラクターがアクセス済みのエーテライト）から
/// 目的エリアのものを選ぶ。到達後の移動は vnavmesh に任せる。
/// </summary>
public sealed class AetheryteService(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>
    /// テレポート可能なエリアの集合。
    /// UI が行ごとに毎フレーム問い合わせるため、都度エーテライト一覧を走査すると重い。
    /// </summary>
    private HashSet<uint>? reachableCache;
    private DateTime reachableCacheExpiry = DateTime.MinValue;

    /// <summary>
    /// 指定エリアへのテレポート先を探す。
    /// アクセスしていないエーテライトは AetheryteList に含まれないため、その場合は false になる。
    /// </summary>
    /// <summary>
    /// アクセス済みエーテライトの一覧を、いま信じてよいか。
    ///
    /// **コンテンツの中では一覧が空になる。**
    /// 空を「1 つもアクセスしていない」と読むと、行けるはずの街へ
    /// 「エーテライトが無い」と言って失敗する。
    ///
    /// 実際、討伐の最中に閾値へ達したとき、
    /// 「ソリューション・ナイン へ行けません」と出て止まっていた。
    /// その日のうちに何度もテレポートできている街だった。
    ///
    /// 空のときは「まだ判断できない」として扱い、判定を先送りする。
    /// </summary>
    public bool IsListReady()
    {
        try
        {
            return Svc.AetheryteList.Any();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Aetheryte", $"エーテライト一覧を読めません: {ex.Message}");
            return false;
        }
    }

    public bool TryFindTarget(uint territoryId, out TeleportTarget? target)
    {
        target = null;

        if (territoryId == 0)
        {
            return false;
        }

        try
        {
            // 同一エリアに複数エントリがある場合（ハウジングなど）は SubIndex が小さいものを優先する。
            var entry = Svc.AetheryteList
                .Where(x => x.TerritoryId == territoryId)
                .OrderBy(x => x.SubIndex)
                .FirstOrDefault();

            if (entry is null)
            {
                return false;
            }

            target = new TeleportTarget(
                entry.AetheryteId,
                entry.SubIndex,
                territoryId,
                GetName(entry.AetheryteId));

            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Aetheryte", $"エーテライト一覧を取得できませんでした: {ex.Message}");
            return false;
        }
    }

    /// <summary>そのエリアへテレポートできるか（アクセス済みか）。</summary>
    public bool CanReach(uint territoryId)
    {
        if (territoryId == 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (this.reachableCache is null || now > this.reachableCacheExpiry)
        {
            var set = new HashSet<uint>();
            try
            {
                foreach (var entry in Svc.AetheryteList)
                {
                    set.Add(entry.TerritoryId);
                }
            }
            catch (Exception ex)
            {
                this.anomalyLog.Warn("Aetheryte", $"エーテライト一覧を取得できませんでした: {ex.Message}");
            }

            this.reachableCache = set;
            this.reachableCacheExpiry = now.AddSeconds(5);
        }

        return this.reachableCache.Contains(territoryId);
    }

    /// <summary>
    /// いまいるエリアで、目的地に一番近いエーテライト網の転送先を探す。
    ///
    /// エーテライトの座標はシートから引けない（Aetheryte.Level が空）ため、
    /// 実行時にゲーム内オブジェクトとして探す。
    ///
    /// 戻り値は「そこへ飛んだ方が十分に近づける場合」のみ true。
    /// わずかな短縮のために転送するとかえって遅くなる。
    /// </summary>
    public bool TryFindAethernetShortcut(Vector3 destination, float minimumGain, out uint aetheryteRowId, out string name, out float gain)
    {
        aetheryteRowId = 0;
        name = string.Empty;
        gain = 0f;

        if (!Player.Available)
        {
            return false;
        }

        try
        {
            var currentDistance = Vector3.Distance(Player.Position, destination);

            uint bestId = 0;
            var bestDistance = currentDistance;

            foreach (var obj in Svc.Objects)
            {
                if (obj.ObjectKind != ObjectKind.Aetheryte)
                {
                    continue;
                }

                var distance = Vector3.Distance(obj.Position, destination);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestId = obj.DataId;
            }

            if (bestId == 0)
            {
                return false;
            }

            gain = currentDistance - bestDistance;
            if (gain < minimumGain)
            {
                return false;
            }

            aetheryteRowId = bestId;
            name = GetAethernetName(bestId);
            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Aetheryte", $"エーテライト網の探索に失敗しました: {ex.Message}");
            return false;
        }
    }

    private static string GetAethernetName(uint aetheryteId)
    {
        var row = Svc.Data.GetExcelSheet<Aetheryte>()?.GetRowOrDefault(aetheryteId);
        var name = row?.AethernetName.ValueNullable?.Name.ExtractText();
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        name = row?.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? $"Aetheryte {aetheryteId}" : name;
    }

    private static string GetName(uint aetheryteId)
    {
        var row = Svc.Data.GetExcelSheet<Aetheryte>()?.GetRowOrDefault(aetheryteId);
        var name = row?.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? $"Aetheryte {aetheryteId}" : name;
    }

    /// <summary>
    /// 目的エリアに直接飛べない場合の、エーテライト網を使う経路を探す。
    ///
    /// Aetheryte シートを使う。同じ都市のエーテライトは AethernetGroup が同じで、
    /// そのうち IsAetheryte が立っている 1 件が親（テレポートで行ける方）になる。
    ///
    /// <code>
    /// group 3 の親 = Aetheryte 9（ウルダハ：ナル回廊）
    ///   └ Aetheryte 125（ウルダハ：ザル回廊）  ← エーテライト網でしか行けない
    /// </code>
    ///
    /// 親エーテライトにアクセスしていなければ経路は成立しない。
    /// </summary>
    public bool TryFindAethernetRoute(uint targetTerritoryId, out AethernetRoute? route)
    {
        route = null;

        if (targetTerritoryId == 0)
        {
            return false;
        }

        try
        {
            var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
            if (sheet is null)
            {
                return false;
            }

            // 目的エリアにあるエーテライト網の出口を探す。
            uint shardRowId = 0;
            byte group = 0;

            foreach (var row in sheet)
            {
                if (row.Territory.RowId != targetTerritoryId || row.AethernetGroup == 0)
                {
                    continue;
                }

                // 親そのものがここにあるなら、この関数の出番ではない。
                if (row.IsAetheryte)
                {
                    return false;
                }

                shardRowId = row.RowId;
                group = row.AethernetGroup;
                break;
            }

            if (shardRowId == 0)
            {
                return false;
            }

            // 同じ網の親を探す。
            foreach (var row in sheet)
            {
                if (row.AethernetGroup != group || !row.IsAetheryte)
                {
                    continue;
                }

                var hubTerritory = row.Territory.RowId;
                if (hubTerritory == 0)
                {
                    continue;
                }

                // 親にアクセスしていなければ飛べない。
                if (!this.TryFindTarget(hubTerritory, out var hub) || hub is null)
                {
                    this.anomalyLog.Warn(
                        "Aetheryte",
                        $"{NpcLocationService.GetTerritoryName(targetTerritoryId)} へはエーテライト網でしか行けませんが、" +
                        $"玄関口の {NpcLocationService.GetTerritoryName(hubTerritory)} にアクセスしていません");
                    return false;
                }

                var shardName = sheet.GetRowOrDefault(shardRowId)?.AethernetName.ValueNullable?.Name.ExtractText()
                                ?? string.Empty;

                route = new AethernetRoute(hub, shardRowId, shardName, targetTerritoryId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Aetheryte", $"エーテライト網の経路を探せませんでした: {ex.Message}");
            return false;
        }
    }
}
