using System;
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
    /// 指定エリアへのテレポート先を探す。
    /// アクセスしていないエーテライトは AetheryteList に含まれないため、その場合は false になる。
    /// </summary>
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
    public bool CanReach(uint territoryId) => this.TryFindTarget(territoryId, out _);

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
}
