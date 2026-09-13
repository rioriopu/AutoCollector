using System;
using System.Collections.Generic;
using System.Numerics;
using AutoCollector.Diagnostics;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
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

    /// <summary>実際にその場で見かけた NPC 由来。配置ファイルに無いものを補う。</summary>
    World,
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

    /// <summary>
    /// フレームを使って読む配置ファイル。
    ///
    /// **planner.lgb をここに足してはいけない。**
    /// 実測（2026-09-13）では、コストは中身の解析ではなく
    /// ファイルの取り出しそのものにある。
    ///
    /// <code>
    /// 全 596 bg の FileExists     :      28 ms   ← ほぼ無料
    /// 街 29 bg の planner を取得  :  53,069 ms   ← 1 件で数秒かかるものがある
    /// リムサ下甲板層 / グリダニア旧市街 :  1 ms 程度
    /// </code>
    ///
    /// 1 件が数秒かかることがあるため、時間で区切っても 1 フレームを止めてしまう。
    /// planner.lgb は <see cref="ScanPlannerInBackground"/> で背景スレッドから読む。
    /// </summary>
    private static readonly string[] LayerFileNames = ["planevent.lgb"];

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly Dictionary<uint, List<NpcLocation>> index = [];

    private List<string>? pendingBgPaths;
    private Dictionary<string, uint>? bgToTerritory;

    /// <summary>保存済みの索引を確認したか。起動ごとに 1 度だけ見る。</summary>
    private bool cacheChecked;

    /// <summary>索引を作ったときのゲームの版。保存と照合に使う。</summary>
    private string gameVersion = string.Empty;

    /// <summary>世界から覚えた件数。保存すべき変化があるかの判断に使う。</summary>
    private int learnedCount;

    /// <summary>次に世界を見る時刻。毎フレーム見る必要はない。</summary>
    private DateTime nextLearnUtc = DateTime.MinValue;

    /// <summary>保存すべき変化があるか。</summary>
    private bool dirty;

    /// <summary>次に保存してよい時刻。書き込みを続けざまに行わない。</summary>
    private DateTime nextSaveUtc = DateTime.MinValue;
    private int bgCursor;
    private bool levelScanDone;

    public bool IsReady { get; private set; }

    public float BuildProgress { get; private set; }

    public int KnownNpcCount => this.index.Count;

    /// <summary>
    /// 索引構築を 1 フレーム分進める。true を返したら完了。
    ///
    /// 配置ファイルは数が多く、1 つ 1 つの大きさもばらつく。
    /// 件数で区切ると、たまたま大きいファイルが並んだフレームだけが極端に遅くなる。
    /// そのため**時間で区切る**。指定したミリ秒を超えたらそのフレームは打ち切る。
    ///
    /// 索引の中身はゲームが更新されるまで変わらないため、
    /// 一度作ったらファイルへ保存し、次回からは読むだけにする。
    /// </summary>
    public bool TickBuild(int frameBudgetMilliseconds = 6)
    {
        if (this.IsReady)
        {
            return true;
        }

        try
        {
            // 保存済みのものがあればそれを使う。走査そのものを行わない。
            if (!this.cacheChecked)
            {
                this.cacheChecked = true;
                this.gameVersion = NpcLocationCache.ResolveGameVersion();

                if (NpcLocationCache.TryLoad(this.gameVersion, this.index, out var loadFailure))
                {
                    this.IsReady = true;
                    this.BuildProgress = 1f;
                    this.anomalyLog.Info(
                        "NpcLocation",
                        $"保存済みの NPC 配置を読み込みました（{this.index.Count} 体）");
                    return true;
                }

                // 読めなかった場合は、途中まで入った可能性があるので捨ててから作り直す。
                this.index.Clear();
                this.anomalyLog.Info("NpcLocation", $"{loadFailure}。作り直します");
                return false;
            }

            if (!this.levelScanDone)
            {
                this.ScanLevelSheet();
                this.PrepareBgList();
                this.levelScanDone = true;
                this.BuildProgress = 0.2f;
                return false;
            }

            return this.ScanLayerFiles(frameBudgetMilliseconds);
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

    private bool ScanLayerFiles(int frameBudgetMilliseconds)
    {
        if (this.pendingBgPaths is null || this.bgToTerritory is null)
        {
            this.IsReady = true;
            return true;
        }

        // 1 フレームで使ってよい時間。超えたら途中で抜けて次のフレームへ回す。
        var watch = System.Diagnostics.Stopwatch.StartNew();

        while (this.bgCursor < this.pendingBgPaths.Count)
        {
            // 1 件も処理せずに抜けると先へ進まないため、判定はループの先頭ではなく
            // 1 件処理したあとに行う。
            if (watch.ElapsedMilliseconds >= frameBudgetMilliseconds && this.bgCursor > 0)
            {
                break;
            }

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

            // 配置ファイルは 1 つとは限らない。planevent に無い NPC が planner にいることがある。
            // 片方しか読まないと、その NPC が無言で候補から落ちる。
            foreach (var fileName in LayerFileNames)
            {
                LgbFile? lgb;
                try
                {
                    lgb = Svc.Data.GetFile<LgbFile>($"bg/{bg[..separator]}/{fileName}");
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

                this.CollectFromLayers(lgb, territoryId);
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

        // planner.lgb は読まない。背景スレッドで読む案も試したが、
        // ゲームデータの読み出しは内部で排他がかかるため本体スレッドと
        // 取り合いになり、操作できないほどカクついた。
        //
        // 代わりに、実際にその場へ行ったときに世界から覚える。
        // 詳しくは LearnFromWorld を見ること。
        this.SaveIndex();

        return true;
    }

    private void CollectFromLayers(LgbFile lgb, uint territoryId)
    {
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

    /// <summary>索引を保存する。</summary>
    private void SaveIndex()
    {
        if (NpcLocationCache.TrySave(this.gameVersion, this.index, out var saveFailure))
        {
            this.anomalyLog.Info("NpcLocation", "次回のために NPC 配置を保存しました");
        }
        else
        {
            this.anomalyLog.Warn("NpcLocation", saveFailure);
        }
    }

    /// <summary>
    /// いま見えている NPC の位置を覚える。
    ///
    /// 配置ファイルの planevent.lgb だけでは足りない。
    /// リムサ・ロミンサとグリダニアの窓口は planner.lgb にしかおらず、
    /// そちらは読み出しが重すぎてフレームの中でも背景スレッドでも扱えなかった。
    ///
    /// **ファイルから引けないなら、実際にその場へ行ったときに覚えればよい。**
    /// 一度でもその街を通れば、以後はテレポート先として選べるようになる。
    /// 座標は配置ファイルより正確で、パッチで NPC が動いても追従する。
    ///
    /// 見ているのは現在のエリアのオブジェクト表だけなので負荷は無い。
    /// </summary>
    public void LearnFromWorld()
    {
        if (!this.IsReady)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (now < this.nextLearnUtc)
        {
            this.SaveIfDirty(now);
            return;
        }

        this.nextLearnUtc = now.AddSeconds(2);

        try
        {
            var territoryId = Svc.ClientState.TerritoryType;
            if (territoryId == 0 || !Player.Available)
            {
                return;
            }

            foreach (var obj in Svc.Objects)
            {
                if (obj.ObjectKind != ObjectKind.EventNpc)
                {
                    continue;
                }

                var baseId = obj.DataId;
                if (baseId == 0)
                {
                    continue;
                }

                // すでにこのエリアの位置を持っているなら触らない。
                if (this.index.TryGetValue(baseId, out var known) &&
                    known.Exists(x => x.TerritoryId == territoryId))
                {
                    continue;
                }

                this.Add(baseId, new NpcLocation(territoryId, obj.Position, NpcLocationSource.World));
                this.learnedCount++;
                this.dirty = true;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("NpcLocation", $"NPC の位置を覚えられませんでした: {ex.Message}");
        }

        this.SaveIfDirty(now);
    }

    /// <summary>覚えたものがあれば保存する。書き込みは間隔を空ける。</summary>
    private void SaveIfDirty(DateTime now)
    {
        if (!this.dirty || now < this.nextSaveUtc)
        {
            return;
        }

        this.dirty = false;
        this.nextSaveUtc = now.AddSeconds(60);

        if (NpcLocationCache.TrySave(this.gameVersion, this.index, out var failure))
        {
            this.anomalyLog.Info("NpcLocation", $"世界で見つけた NPC の位置を保存しました（累計 {this.learnedCount} 件）");
        }
        else
        {
            this.anomalyLog.Warn("NpcLocation", failure);
        }
    }

    /// <summary>終了時の後始末。いまは覚えたぶんを書き出すだけ。</summary>
    public void Dispose()
    {
        if (this.dirty)
        {
            NpcLocationCache.TrySave(this.gameVersion, this.index, out _);
        }
    }
}
