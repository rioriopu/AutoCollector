using System;
using System.Collections.Generic;
using System.Numerics;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

public sealed record NpcLocation(uint TerritoryId, Vector3 Position, NpcLocationSource Source);

public enum NpcLocationSource
{
    /// <summary>Level シート由来。</summary>
    LevelSheet,

    /// <summary>マップ配置ファイル（planevent.lgb）由来。</summary>
    LayerFile,
}

/// <summary>
/// NPC の配置座標を引く。
///
/// Level シートだけでは足りない。実測で Level(Type==8) に載っているのは 24,210 体だが、
/// マップ配置ファイル（planevent.lgb）まで見ると 39,540 体になる。
/// 差の 16,441 体は Level に載っていない。実際、ファントムウェポン素材の交換 NPC は
/// Level に無く、LGB からしか座標を取れない。
///
/// 全 589 個の planevent.lgb を解析しても実測 113 ms で済むが、
/// 1 フレームで実行するとカクつくため、フレームあたりの件数を制限して進める。
/// </summary>
public sealed class NpcLocationService(AnomalyLog anomalyLog)
{
    private const byte EventNpcLevelType = 8;

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly Dictionary<uint, List<NpcLocation>> index = [];

    private List<string>? pendingBgPaths;
    private Dictionary<string, uint>? bgToTerritory;
    private int bgCursor;
    private bool levelScanDone;

    public bool IsReady { get; private set; }

    public float BuildProgress { get; private set; }

    public int KnownNpcCount => this.index.Count;

    /// <summary>索引構築を 1 フレーム分進める。true を返したら完了。</summary>
    public bool TickBuild(int lgbBudgetPerFrame = 60)
    {
        if (this.IsReady)
        {
            return true;
        }

        try
        {
            if (!this.levelScanDone)
            {
                this.ScanLevelSheet();
                this.PrepareBgList();
                this.levelScanDone = true;
                this.BuildProgress = 0.2f;
                return false;
            }

            return this.ScanLayerFiles(lgbBudgetPerFrame);
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("NpcLocation", $"NPC 配置の索引構築に失敗しました: {ex.Message}");
            this.IsReady = true;
            return true;
        }
    }

    private void ScanLevelSheet()
    {
        var levels = Svc.Data.GetExcelSheet<Level>();
        if (levels is null)
        {
            return;
        }

        foreach (var level in levels)
        {
            if (level.Type != EventNpcLevelType)
            {
                continue;
            }

            var objectId = level.Object.RowId;
            var territoryId = level.Territory.RowId;
            if (objectId == 0 || territoryId == 0)
            {
                continue;
            }

            this.Add(objectId, new NpcLocation(territoryId, new Vector3(level.X, level.Y, level.Z), NpcLocationSource.LevelSheet));
        }
    }

    private void PrepareBgList()
    {
        var territories = Svc.Data.GetExcelSheet<TerritoryType>();
        if (territories is null)
        {
            this.pendingBgPaths = [];
            this.bgToTerritory = [];
            return;
        }

        // Bg が同じ Territory は同じ配置ファイルを共有するため重複排除する。
        var map = new Dictionary<string, uint>();
        foreach (var territory in territories)
        {
            var bg = territory.Bg.ExtractText();
            if (string.IsNullOrEmpty(bg) || !bg.Contains('/'))
            {
                continue;
            }

            map.TryAdd(bg, territory.RowId);
        }

        this.bgToTerritory = map;
        this.pendingBgPaths = [.. map.Keys];
        this.bgCursor = 0;
    }

    private bool ScanLayerFiles(int budget)
    {
        if (this.pendingBgPaths is null || this.bgToTerritory is null)
        {
            this.IsReady = true;
            return true;
        }

        var processed = 0;
        while (this.bgCursor < this.pendingBgPaths.Count && processed < budget)
        {
            processed++;
            var bg = this.pendingBgPaths[this.bgCursor++];

            if (!this.bgToTerritory.TryGetValue(bg, out var territoryId))
            {
                continue;
            }

            var separator = bg.LastIndexOf('/');
            if (separator <= 0)
            {
                continue;
            }

            var path = $"bg/{bg[..separator]}/planevent.lgb";

            LgbFile? lgb;
            try
            {
                lgb = Svc.Data.GetFile<LgbFile>(path);
            }
            catch
            {
                // 配置ファイルが無いエリアは珍しくない。索引に足せないだけなので無視する。
                continue;
            }

            if (lgb is null)
            {
                continue;
            }

            foreach (var layer in lgb.Layers)
            {
                foreach (var instance in layer.InstanceObjects)
                {
                    if (instance.AssetType != LayerEntryType.EventNPC)
                    {
                        continue;
                    }

                    if (instance.Object is not LayerCommon.ENPCInstanceObject npc)
                    {
                        continue;
                    }

                    var baseId = npc.ParentData.ParentData.BaseId;
                    if (baseId == 0)
                    {
                        continue;
                    }

                    var position = new Vector3(
                        instance.Transform.Translation.X,
                        instance.Transform.Translation.Y,
                        instance.Transform.Translation.Z);

                    this.Add(baseId, new NpcLocation(territoryId, position, NpcLocationSource.LayerFile));
                }
            }
        }

        this.BuildProgress = 0.2f + (0.8f * this.bgCursor / Math.Max(1, this.pendingBgPaths.Count));

        if (this.bgCursor < this.pendingBgPaths.Count)
        {
            return false;
        }

        this.IsReady = true;
        this.BuildProgress = 1f;
        this.pendingBgPaths = null;
        this.bgToTerritory = null;
        this.anomalyLog.Info("NpcLocation", $"NPC 配置の索引を構築しました（{this.index.Count} 体）");
        return true;
    }

    private void Add(uint npcDataId, NpcLocation location)
    {
        if (!this.index.TryGetValue(npcDataId, out var list))
        {
            this.index[npcDataId] = list = [];
            list.Add(location);
            return;
        }

        // 同じエリアの同じ場所を二重に持たない（Level と LGB は同じ座標を返すことがある）。
        foreach (var existing in list)
        {
            if (existing.TerritoryId == location.TerritoryId &&
                Vector3.DistanceSquared(existing.Position, location.Position) < 1f)
            {
                return;
            }
        }

        list.Add(location);
    }

    /// <summary>既定の配置（最初に見つかったもの）を返す。</summary>
    public bool TryGet(uint npcDataId, out NpcLocation location)
    {
        if (this.index.TryGetValue(npcDataId, out var list) && list.Count > 0)
        {
            location = list[0];
            return true;
        }

        location = null!;
        return false;
    }

    /// <summary>同一 NPC の配置をすべて返す。複数エリアに存在する場合の選択に使う。</summary>
    public IReadOnlyList<NpcLocation> ListAll(uint npcDataId)
        => this.index.TryGetValue(npcDataId, out var list) ? list : [];

    /// <summary>ENpcResident から NPC 名を引く。</summary>
    public static string GetName(uint npcDataId)
    {
        var row = Svc.Data.GetExcelSheet<ENpcResident>()?.GetRowOrDefault(npcDataId);
        return row?.Singular.ExtractText() ?? string.Empty;
    }

    /// <summary>エリア名を引く。UI 表示用。</summary>
    public static string GetTerritoryName(uint territoryId)
    {
        var territory = Svc.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        if (territory is null)
        {
            return $"Territory {territoryId}";
        }

        var placeName = territory.Value.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(placeName) ? $"Territory {territoryId}" : placeName;
    }
}
