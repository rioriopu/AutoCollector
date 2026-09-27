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
    /// <summary>
    /// このマップが上限に達しているか。
    ///
    /// <b>MaxRank が 0 のことがある。</b>
    /// 実機（2026-09-27）で、全マップ COMPLETE・RANK4 の状態なのに
    /// 「0/6 マップ」と判定される不具合が出た。
    /// CurrentRank は正しく読めていたので、MaxRank 側が
    /// 埋まっていなかったことになる。
    ///
    /// <b>構造体の定義は正しい。</b>
    /// 本家 FFXIVClientStructs（2026-09-27 時点）と手元の定義が
    /// 完全に一致することを確認した。MaxRank のオフセット 0x71 も同じ。
    /// つまりオフセットの誤りではなく、エージェントが埋めていない。
    /// 調べた内容は C:\ソース\dalamud-docs-ja\シェアFATE_達成度の取得.md に置いた。
    ///
    /// <b>上限はシートからも取れない。</b>
    /// FateProgressUI の列 1 はどのマップも 6 だが、
    /// 実際の上限は 漆黒 3 / 暁月 3 / 黄金 4 で一致しない。
    ///
    /// そこで MaxRank が読めているときだけそれを信じ、
    /// 読めていないときは <see cref="SharedFateRankService"/> 側で
    /// 「その拡張でいちばん高いランク」を上限とみなして判定する。
    /// ここでは判断できないので、素直に false を返す。
    /// </summary>
    public bool IsMaxed => this.MaxRank > 0 && this.CurrentRank >= this.MaxRank;

    /// <summary>上限が読めているか。</summary>
    public bool HasMaxRank => this.MaxRank > 0;
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
/// 18 行あり、列 0 が TerritoryType、列 1〜3 が
/// 各ランクに要る FATE 数（0 ならそのランクは無い）。
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

    /// <summary>
    /// 各ランクに到達するのに要る FATE の数。0 なら、そのランクが存在しない。
    ///
    /// <b>列の意味を取り違えていた。</b>
    /// 以前は列 1 を「上限ランク」と読んでいたが、実際は
    /// <c>ReqFatesToRank2 / ReqFatesToRank3 / ReqFatesToRank4</c>（EXDSchema の定義）。
    /// どのマップも列 1 が 6 なのは「ランク2 に 6 件要る」という意味で、
    /// 上限が 6 ということではなかった。
    ///
    /// <b>0 でない列を数えれば上限ランクが出る。</b>実データ（ver 2026.09.15）：
    ///
    ///   漆黒・暁月 … 6 / 60 /  0  → RANK3
    ///   黄金       … 6 / 20 / 40  → RANK4
    ///
    /// 実機の画面表示（漆黒3 / 暁月3 / 黄金4）と一致する。
    /// </summary>
    private const int ColumnReqRank2 = 1;
    private const int ColumnReqRank3 = 2;
    private const int ColumnReqRank4 = 3;

    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>拡張の区切り（シートの並び順）→ そのマップ群。</summary>
    private IReadOnlyList<IReadOnlyList<uint>>? zoneGroups;

    /// <summary>
    /// マップ → 上限ランク。<c>FateProgressUI</c> シートから作る。
    ///
    /// エージェントの <c>MaxRank</c> は 0 のことがあるが、
    /// <b>こちらはシートから確実に取れる</b>のでいつでも使える。
    /// </summary>
    private readonly Dictionary<uint, byte> maxRankByZone = [];

    /// <summary>ランクを覚えておく時間。</summary>
    private const long CacheMilliseconds = 1000;

    private IReadOnlyDictionary<uint, SharedFateZoneRank>? cachedRanks;
    private long cachedAt;

    /// <summary>
    /// F.A.T.E達成度の画面に出てくるマップを、拡張ごとにまとめて返す。
    ///
    /// 列 0 = TerritoryType、列 1〜3 = ReqFatesToRank2/3/4（0 ならそのランクは無い）。
    ///
    /// シートは拡張ごとに 6 件ずつ並んでいる（漆黒 / 暁月 / 黄金）。
    /// 並び順に 6 件ずつ切るのではなく、TerritoryType の番号帯で切る。
    /// 番号帯が離れているところが拡張の境目になる。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<uint>> ZoneGroups
    {
        get
        {
            if (this.zoneGroups is { Count: > 0 })
            {
                return this.zoneGroups;
            }

            // **空は控えない。**
            //
            // 起動直後やエリア移動中は、シートが null か 0 件で返ることがある。
            // ??= だと空の結果まで控えてしまい、**二度と作り直されない。**
            // その 1 回で、交換エリアの画面がプラグインを読み込み直すまで出なくなる。
            //
            // 同じ罠を FateTokenService と CollectableSourceService でも避けている。
            var built = this.BuildGroups();

            if (built.Count > 0)
            {
                this.zoneGroups = built;
            }

            return built;
        }
    }

    /// <summary>
    /// このマップの上限ランク。読めなければ null。
    ///
    /// <b>上限を知りたいところは全部ここを通す。</b>
    /// 2 か所で別々に数えていたため、画面の文と解放の判定が食い違っていた。
    ///
    /// 順番は シート → エージェント。
    /// エージェントの MaxRank は 0 のままのことがあるので当てにしない
    /// （実機 2026-09-27 に確認）。
    /// </summary>
    private byte? MaxRankOf(SharedFateZoneRank zone)
    {
        if (this.maxRankByZone.TryGetValue(zone.TerritoryId, out var fromSheet) && fromSheet > 0)
        {
            return fromSheet;
        }

        return zone.HasMaxRank ? zone.MaxRank : null;
    }

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
                if (territory == 0)
                {
                    continue;
                }

                // 0 でないランク列を数える。ランク 1 は必ずあるので 1 から始める。
                byte maxRank = 1;
                foreach (var column in new[] { ColumnReqRank2, ColumnReqRank3, ColumnReqRank4 })
                {
                    if (Convert.ToInt64(row.ReadColumn(column)) > 0)
                    {
                        maxRank++;
                    }
                }

                zones.Add((territory, maxRank));
                this.maxRankByZone[territory] = maxRank;
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

                // **読めた値をそのまま記録に残す。**
                // 画面の表示と食い違ったとき、どの値がどう違うのかが
                // 分からないと直しようがない。
                //
                // AddonId も一緒に出す。AgentInterface.AddonId は
                // 対応するアドオンが開いていなければ 0 になるので、
                // 「画面を一度も開いていない」のか
                // 「開いたのに MaxRank だけ埋まらない」のかを区別できる。
                if (result.Count > 0
                    && ECommons.Throttlers.EzThrottler.Throttle("AutoCollector.RankDump", 10000))
                {
                    var missingMax = result.Values.Count(x => !x.HasMaxRank);

                    this.anomalyLog.Info(
                        "SharedFate",
                        $"読み取った達成度（AddonId={agent->AddonId} 上限が空={missingMax}/{result.Count}）: " +
                        string.Join(
                            " / ",
                            result.Values.Select(x => $"{x.TerritoryId}:{x.CurrentRank}/{x.MaxRank}")));
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

        // この拡張のぶんだけ取り出す。
        var inGroup = group
            .Where(ranks.ContainsKey)
            .Select(t => ranks[t])
            .ToList();

        if (inGroup.Count != group.Count)
        {
            // 1 つでも読めていないなら判断しない。
            return false;
        }

        // **上限はシートから取る。**
        //
        // FateProgressUI の ReqFatesToRank2/3/4 のうち 0 でない列を数えれば、
        // そのマップの上限ランクが出る（漆黒3 / 暁月3 / 黄金4）。
        // 実機の画面表示と一致することを確認済み。
        //
        // エージェントの MaxRank は 0 のことがあるので当てにしない。
        // シート側が引けたときは、そちらを優先する。
        var allFromSheet = inGroup.All(x => this.MaxRankOf(x) is not null);

        if (allFromSheet)
        {
            return inGroup.All(x => x.CurrentRank >= this.MaxRankOf(x)!.Value);
        }

        // シートもエージェントも上限を持っていない。
        // 分からないので解放されていない側に倒す。
        // （シート → エージェント の順は MaxRankOf が受け持っている）
        return false;
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

        // 判定と同じ数え方をする。食い違うと、
        // 「全マップ最大」と書いてあるのに選べない、といった形になる。
        var inGroup = group
            .Where(ranks.ContainsKey)
            .Select(t => ranks[t])
            .ToList();

        // **解放の判定とまったく同じ入口を通す。**
        //
        // 以前はここだけシートの上限を見ず、「到達しているいちばん高いランク」を
        // 上限とみなしていた。そのため全マップが同じランク（例: 全部 RANK2、
        // シート上限は 3）のとき、画面には「ランク最大 6/6 マップ」と出るのに
        // 解放判定は false になり、
        // 『達成度が足りないため選べません（いま ランク最大 6/6 マップ）』という
        // 自分で矛盾した文が出ていた。利用者からは不具合と区別がつかない。
        if (inGroup.Count != group.Count || inGroup.Any(x => this.MaxRankOf(x) is null))
        {
            return "達成度の上限を読めません";
        }

        var maxed = inGroup.Count(x => x.CurrentRank >= this.MaxRankOf(x)!.Value);

        return $"ランク最大 {maxed}/{group.Count} マップ";
    }
}
