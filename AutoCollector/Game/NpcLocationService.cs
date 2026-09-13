using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>背景で planner.lgb を読む処理。読み終わるまで結果は取り込まない。</summary>
    private Task<Dictionary<uint, List<NpcLocation>>>? plannerTask;

    private readonly CancellationTokenSource plannerCancel = new();

    /// <summary>背景走査を始めたか。1 度だけ走らせる。</summary>
    private bool plannerStarted;

    /// <summary>背景走査の結果を取り込んだか。取り込んでから索引を保存する。</summary>
    private bool plannerMerged;
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
        // 背景で読み終わっていれば取り込む。ここだけがゲーム側のスレッド。
        this.TryMergePlannerResults();

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

        // planner.lgb はここでは読まない。1 件で数秒かかることがあり、
        // フレームを止めてしまう。背景で読み、読み終わってから保存する。
        this.StartPlannerScan();

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

    /// <summary>
    /// planner.lgb を背景スレッドで読む。
    ///
    /// ここだけ Framework スレッドの外で動く。
    /// ゲームの状態には触らず、ゲームデータの読み出しだけを行う。
    /// vnavmesh も同じことをしている（NavmeshManager が Task.Run の中から
    /// SceneExtractor を作り、その中で DataManager.GetFile を呼ぶ）。
    ///
    /// 取り出しに数秒かかるファイルがあるため、フレームの中では読めない。
    /// 結果は自分の辞書に貯め、<see cref="TryMergePlannerResults"/> が
    /// Framework スレッドで索引へ取り込む。
    /// </summary>
    private void StartPlannerScan()
    {
        if (this.plannerStarted)
        {
            return;
        }

        this.plannerStarted = true;

        // 走査対象は planevent と同じ bg の一覧。ここで控えておく。
        // 背景スレッドからシートを引き直さずに済ませる。
        var targets = new List<(string Path, uint TerritoryId)>();

        try
        {
            var territories = Svc.Data.GetExcelSheet<TerritoryType>();
            if (territories is null)
            {
                return;
            }

            var seen = new HashSet<string>();

            foreach (var territory in territories)
            {
                var bg = territory.Bg.ExtractText();
                if (string.IsNullOrEmpty(bg) || !bg.Contains('/') || !seen.Add(bg))
                {
                    continue;
                }

                var separator = bg.LastIndexOf('/');
                if (separator <= 0)
                {
                    continue;
                }

                var path = $"bg/{bg[..separator]}/planner.lgb";

                // 無いファイルを取りに行かせない。存在確認はほぼ無料で、
                // 596 件すべて確かめても 28 ms 程度だった。
                if (!Svc.Data.FileExists(path))
                {
                    continue;
                }

                targets.Add((path, territory.RowId));
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("NpcLocation", $"背景走査の対象を作れませんでした: {ex.Message}");
            return;
        }

        if (targets.Count == 0)
        {
            this.plannerMerged = true;
            this.SaveIndex();
            return;
        }

        this.anomalyLog.Info("NpcLocation", $"追加の配置ファイル {targets.Count} 件を背景で読み込みます");

        var token = this.plannerCancel.Token;

        this.plannerTask = Task.Run(
            () =>
            {
                var found = new Dictionary<uint, List<NpcLocation>>();

                foreach (var (path, territoryId) in targets)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    LgbFile? lgb;
                    try
                    {
                        lgb = Svc.Data.GetFile<LgbFile>(path);
                    }
                    catch
                    {
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
                            if (instance.AssetType != LayerEntryType.EventNPC ||
                                instance.Object is not LayerCommon.ENPCInstanceObject npc)
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

                            if (!found.TryGetValue(baseId, out var list))
                            {
                                found[baseId] = list = [];
                            }

                            list.Add(new NpcLocation(territoryId, position, NpcLocationSource.LayerFile));
                        }
                    }
                }

                return found;
            },
            token);
    }

    /// <summary>
    /// 背景で読んだ結果を索引へ取り込む。
    /// 索引そのものは Framework スレッドからしか触らない。
    /// </summary>
    private void TryMergePlannerResults()
    {
        if (this.plannerMerged || this.plannerTask is not { IsCompleted: true } task)
        {
            return;
        }

        this.plannerMerged = true;

        try
        {
            if (task.IsFaulted)
            {
                this.anomalyLog.Warn(
                    "NpcLocation",
                    $"追加の配置ファイルを読めませんでした: {task.Exception?.GetBaseException().Message}");
                return;
            }

            if (task.IsCanceled)
            {
                return;
            }

            var before = this.index.Count;

            foreach (var (npcId, locations) in task.Result)
            {
                foreach (var location in locations)
                {
                    this.Add(npcId, location);
                }
            }

            this.anomalyLog.Info(
                "NpcLocation",
                $"追加の配置を取り込みました（{before} → {this.index.Count} 体）");

            this.SaveIndex();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("NpcLocation", $"追加の配置を取り込めませんでした: {ex.Message}");
        }
        finally
        {
            this.plannerTask = null;
        }
    }

    /// <summary>索引を保存する。両方の走査が終わってから呼ぶ。</summary>
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

    /// <summary>背景走査を打ち切る。プラグインの終了時に呼ぶ。</summary>
    public void Dispose()
    {
        try
        {
            this.plannerCancel.Cancel();
            this.plannerCancel.Dispose();
        }
        catch
        {
            // 終了処理なので握り潰す。
        }
    }
}
