using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;

namespace AutoCollector.Game;

/// <summary>F.A.T.E達成度の 1 マップ分。</summary>
/// <param name="TerritoryId">マップの TerritoryType 番号。</param>
/// <param name="CurrentRank">いまのランク。</param>
/// <param name="MaxRank">そのマップの上限ランク。</param>
public sealed record SharedFateZoneRank(uint TerritoryId, byte CurrentRank, byte MaxRank)
{
    /// <summary>このマップが上限に達しているか。</summary>
    public bool IsMaxed => this.MaxRank > 0 && this.CurrentRank >= this.MaxRank;
}

/// <summary>
/// F.A.T.E達成度を読み、都市の集約交換所が解放されているかを判断する。
///
/// <b>なぜ要るか。</b>
/// バイカラージェムの交換所には 2 種類ある。
///
///   ・各マップの広域交易商（18 人）……そのマップの品だけを扱う
///   ・都市の広域交易商（6 人）……その拡張の全マップの品をまとめて扱う
///
/// <b>都市の交易商は、その拡張の 6 マップすべてがランク最大でないと使えない。</b>
/// 条件を満たしていないのに都市へ飛ばすと、着いても交換できずに終わる。
/// 逆にすべて最大なら、各マップの交易商を使う理由はなくなる（都市で全部買える）。
///
/// <b>どこから読むか。</b>
/// マップの顔ぶれと上限ランクは <c>FateProgressUI</c> シートにある。
/// 18 行あり、列 0 が TerritoryType、列 1 が上限ランク。
/// ここから拡張ごとの区切りを作るので、マップ番号は埋め込まない
/// （docs/00_設計決定.md の D-2）。
///
/// いまのランクは <see cref="AgentFateProgress"/>（F.A.T.E達成度の画面）が持つ。
/// 3 タブ × 6 ゾーンの構造で、ゾーンごとに CurrentRank / MaxRank がある。
///
/// <b>画面を開いていないと読めないことがある。</b>
/// エージェントの中身は画面を開いた時に埋まる作りなので、
/// 一度も開いていない間は 0 のままになりうる。
/// そのため「読めた」と「読めていない」を区別し、
/// <b>読めていないときは解放されていない側に倒す</b>。
/// 誤って都市へ飛ばすと交換できずに終わるが、
/// 誤ってマップの交易商へ行っても交換自体はできるため、害が小さい。
/// </summary>
public sealed class SharedFateRankService(AnomalyLog anomalyLog)
{
    /// <summary>FateProgressUI の列。0 = TerritoryType、1 = 上限ランク。</summary>
    private const int ColumnTerritory = 0;
    private const int ColumnMaxRank = 1;

    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>拡張の区切り（シートの並び順）→ そのマップ群。</summary>
    private IReadOnlyList<IReadOnlyList<uint>>? zoneGroups;

    /// <summary>ランクを覚えておく時間。</summary>
    private const long CacheMilliseconds = 1000;

    private IReadOnlyDictionary<uint, SharedFateZoneRank>? cachedRanks;
    private long cachedAt;

    /// <summary>
    /// F.A.T.E達成度の画面に出てくるマップを、拡張ごとにまとめて返す。
    ///
    /// シートは拡張ごとに 6 件ずつ並んでいる（漆黒 / 暁月 / 黄金）。
    /// 並び順に 6 件ずつ切るのではなく、TerritoryType の番号帯で切る。
    /// 番号帯が離れているところが拡張の境目になる。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<uint>> ZoneGroups => this.zoneGroups ??= this.BuildGroups();

    private IReadOnlyList<IReadOnlyList<uint>> BuildGroups()
    {
        try
        {
            var sheet = Svc.Data.Excel.GetSheet<RawRow>(name: "FateProgressUI");
            if (sheet is null || sheet.Count == 0)
            {
                this.anomalyLog.Warn("SharedFate", "FateProgressUI シートを読めませんでした");
                return [];
            }

            var zones = new List<(uint Territory, byte MaxRank)>();
            foreach (var row in sheet)
            {
                var territory = Convert.ToUInt32(row.ReadColumn(ColumnTerritory));
                var maxRank = Convert.ToByte(row.ReadColumn(ColumnMaxRank));
                if (territory != 0)
                {
                    zones.Add((territory, maxRank));
                }
            }

            // 番号帯で拡張を切る。漆黒は 813 台、暁月は 956 台、黄金は 1187 台と
            // まとまっているので、前の行から大きく飛んだところが境目。
            // 「6 件ずつ」と決め打つと、拡張ごとのマップ数が変わったときに崩れる。
            var groups = new List<List<uint>>();
            var current = new List<uint>();
            uint previous = 0;

            foreach (var (territory, _) in zones.OrderBy(x => x.Territory))
            {
                if (current.Count > 0 && territory - previous > 50)
                {
                    groups.Add(current);
                    current = [];
                }

                current.Add(territory);
                previous = territory;
            }

            if (current.Count > 0)
            {
                groups.Add(current);
            }

            this.anomalyLog.Info(
                "SharedFate",
                $"F.A.T.E達成度のマップを {zones.Count} 件、拡張 {groups.Count} 組として読みました");

            return groups;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("SharedFate", $"F.A.T.E達成度のマップ一覧を組み立てられませんでした: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// いまのランクをエージェントから読む。
    /// 画面を一度も開いていないと読めないことがあり、その場合は空を返す。
    /// </summary>
    public IReadOnlyDictionary<uint, SharedFateZoneRank> ReadCurrentRanks()
    {
        // 画面から毎フレーム呼ばれる。エージェントを読むたびに
        // 18 ゾーン分を走査すると描画の時間を食うため、少しだけ覚える。
        // ランクは F.A.T.E を回している最中にしか上がらないので、
        // 1 秒遅れて反映されても困らない。
        var now = Environment.TickCount64;
        if (this.cachedRanks is not null && now - this.cachedAt < CacheMilliseconds)
        {
            return this.cachedRanks;
        }

        var result = new Dictionary<uint, SharedFateZoneRank>();

        try
        {
            unsafe
            {
                var agent = AgentFateProgress.Instance();
                if (agent is null)
                {
                    return result;
                }

                foreach (ref var tab in agent->Tabs)
                {
                    foreach (ref var zone in tab.Zones)
                    {
                        if (zone.TerritoryTypeId == 0)
                        {
                            continue;
                        }

                        result[zone.TerritoryTypeId] = new SharedFateZoneRank(
                            zone.TerritoryTypeId,
                            zone.CurrentRank,
                            zone.MaxRank);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("SharedFate", $"F.A.T.E達成度を読めませんでした: {ex.Message}");
            return new Dictionary<uint, SharedFateZoneRank>();
        }

        // 読めなかった（画面をまだ開いていない）ときは覚えない。
        // 覚えてしまうと、開いた直後の 1 秒間だけ古い「読めない」が残る。
        if (result.Count > 0)
        {
            this.cachedRanks = result;
            this.cachedAt = now;
        }

        return result;
    }

    /// <summary>
    /// 指定したマップが属する拡張で、全マップがランク最大か。
    ///
    /// <b>読めなかったときは false を返す。</b>
    /// 「分からない」を「解放済み」と扱うと、交換できない都市へ飛ばしてしまう。
    /// </summary>
    public bool IsCityShopUnlocked(uint anyZoneInExpansion)
    {
        var group = this.ZoneGroups.FirstOrDefault(g => g.Contains(anyZoneInExpansion));
        if (group is null || group.Count == 0)
        {
            return false;
        }

        var ranks = this.ReadCurrentRanks();
        if (ranks.Count == 0)
        {
            // 画面をまだ開いていない。分からないので解放されていない側に倒す。
            return false;
        }

        foreach (var territory in group)
        {
            if (!ranks.TryGetValue(territory, out var rank) || !rank.IsMaxed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// そのマップのいまのランク。読めなければ 0。
    /// </summary>
    public byte CurrentRankOf(uint territoryId)
        => this.ReadCurrentRanks().TryGetValue(territoryId, out var rank) ? rank.CurrentRank : (byte)0;

    /// <summary>
    /// ランクを読める状態か（F.A.T.E達成度の画面が一度でも開かれたか）。
    /// </summary>
    public bool CanReadRanks() => this.ReadCurrentRanks().Count > 0;

    /// <summary>
    /// 進み具合を人が読める形で返す。画面表示と、記録に使う。
    /// </summary>
    public string DescribeProgress(uint anyZoneInExpansion)
    {
        var group = this.ZoneGroups.FirstOrDefault(g => g.Contains(anyZoneInExpansion));
        if (group is null || group.Count == 0)
        {
            return "マップの組が分かりません";
        }

        var ranks = this.ReadCurrentRanks();
        if (ranks.Count == 0)
        {
            return "F.A.T.E達成度の画面を一度開くと判定できます";
        }

        var maxed = group.Count(t => ranks.TryGetValue(t, out var r) && r.IsMaxed);
        return $"ランク最大 {maxed}/{group.Count} マップ";
    }
}
