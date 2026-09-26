using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Game;

/// <summary>ベンチャー回収で行く街 1 件ぶん。</summary>
/// <param name="AetheryteId">Aetheryte シートの行 ID。Lifestream.Teleport の第 1 引数。</param>
/// <param name="SubIndex">同じエーテライトの枝番。Lifestream.Teleport の第 2 引数。</param>
/// <param name="TerritoryId">飛んだ先のエリア番号。着いたかの判定に使う。</param>
/// <param name="Name">画面に出す名前。</param>
public readonly record struct HomeTown(
    uint AetheryteId,
    byte SubIndex,
    uint TerritoryId,
    string Name);

/// <summary>
/// ベンチャー回収で行く街を決める。
///
/// <b>行き先はアクセス済みの一覧から選ばせる。</b>
/// エーテライトの ID を設定に埋め込むと、アクセスしていない街も選べてしまい、
/// 実行時に「飛べません」で止まる。一覧から選べた時点で飛べることが保証される。
///
/// <b>デジョンとテレポを使い分ける。</b>
/// 行き先がホームタウンと同じならデジョン（無料）、違えばテレポ（有料）。
/// ホームタウンは <c>PlayerState.HomeAetheryteId</c> で読める。
///
/// <b>一覧が空でも「未アクセス」と決めつけない。</b>
/// コンテンツ（ID・レイド）の中では一覧が空になる。空を「1 つもアクセスして
/// いない」と読むと、行けるはずの街へ「エーテライトが無い」と言って止まる。
/// これは AutoCollector で実際に踏んだ
/// （討伐中に「ソリューション・ナイン へ行けません」で止まった）。
/// </summary>
public sealed unsafe class HomeTownService(AnomalyLog anomalyLog)
{
    /// <summary>
    /// 行き先に出す街のエリア番号。
    ///
    /// エーテライトの行 ID ではなくエリア番号で持つ。
    /// 同じ街でも行 ID は複数あり得る（宿屋・ハウジング等）。
    /// エリア番号で見れば、その街に属するものだけを拾える。
    ///
    /// 値は GbrVentureRelay で Aetheryte シートを実測して確かめたもの
    /// （IsAetheryte かつ AethernetGroup != 0 の行、ver 2026.09.15）。
    /// </summary>
    private static readonly uint[] AllowedTerritories =
    [
        129,  // リムサ・ロミンサ：下層甲板
        132,  // グリダニア：新市街
        130,  // ウルダハ：ナル回廊
        819,  // クリスタリウム
        628,  // クガネ
        1186, // ソリューション・ナイン
    ];

    /// <summary>
    /// 既定の行き先（リムサ・ロミンサ：下層甲板）。
    ///
    /// 一覧の先頭を既定にしない。アクセス状況によって既定の街が変わってしまう。
    /// </summary>
    public const uint DefaultTerritory = 129;

    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>一覧は毎フレーム引くと重い。少しだけ覚える。</summary>
    private List<HomeTown>? cache;
    private DateTime cacheExpiry = DateTime.MinValue;

    /// <summary>
    /// アクセス済みエーテライトの一覧を、いま信じてよいか。
    ///
    /// コンテンツ内では空になるため、空なら「まだ判断できない」とみなす。
    /// </summary>
    public bool IsListReady()
    {
        try
        {
            return Svc.AetheryteList.Any();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("HomeTown", $"エーテライトの一覧を読めません: {ex.Message}");
            return false;
        }
    }

    /// <summary>選べる街の一覧。アクセス済みのものだけを返す。</summary>
    public IReadOnlyList<HomeTown> List()
    {
        var now = DateTime.UtcNow;

        if (this.cache is not null && now < this.cacheExpiry)
        {
            return this.cache;
        }

        var list = new List<HomeTown>();

        try
        {
            foreach (var entry in Svc.AetheryteList)
            {
                // ハウジングの区画は行き先として使わない。
                if (entry.IsApartment || entry.IsSharedHouse)
                {
                    continue;
                }

                if (Array.IndexOf(AllowedTerritories, entry.TerritoryId) < 0)
                {
                    continue;
                }

                list.Add(new HomeTown(
                    entry.AetheryteId,
                    entry.SubIndex,
                    entry.TerritoryId,
                    GetName(entry.AetheryteId)));
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("HomeTown", $"エーテライトの一覧を取得できませんでした: {ex.Message}");
        }

        // 並び順は AllowedTerritories に揃える。
        // 名前順にすると言語で並びが変わり、いつも同じ位置に無くなる。
        list.Sort(static (a, b) =>
            Array.IndexOf(AllowedTerritories, a.TerritoryId)
                .CompareTo(Array.IndexOf(AllowedTerritories, b.TerritoryId)));

        this.cache = list;
        this.cacheExpiry = now.AddSeconds(5);
        return list;
    }

    /// <summary>
    /// いまのキャラクターのホームタウンのエリア番号。読めなければ 0。
    ///
    /// <c>PlayerState.HomeAetheryteId</c> はエーテライトの行 ID なので、
    /// シートを引いてエリア番号に直す。
    /// </summary>
    public uint HomeTerritory()
    {
        try
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps is null)
            {
                return 0;
            }

            var aetheryteId = ps->HomeAetheryteId;
            if (aetheryteId == 0)
            {
                return 0;
            }

            var row = Svc.Data.GetExcelSheet<Aetheryte>()?.GetRowOrDefault(aetheryteId);
            return row?.Territory.RowId ?? 0;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("HomeTown", $"ホームタウンを読めません: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 設定に保存されている行き先を解決する。
    ///
    /// 一覧が空（＝判断できない）ときは null を返す。
    /// 「見つからない」と「まだ読めない」は <see cref="IsListReady"/> と
    /// 組み合わせて呼び出し側で区別する。
    /// </summary>
    public HomeTown? Resolve(uint territoryId)
    {
        if (territoryId == 0)
        {
            return null;
        }

        foreach (var entry in this.List())
        {
            if (entry.TerritoryId == territoryId)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// 行き先が未設定なら、既定（リムサ）を選ぶ。
    ///
    /// <b>設定ファイルに固定値を書き込まない。</b>
    /// リムサにアクセスしていないキャラクターでは飛べない値が入ってしまい、
    /// 実行時に止まることになる。実際に一覧へ出ている（＝飛べる）ときだけ選ぶ。
    ///
    /// 一覧はコンテンツ内では空になるため、読めないうちは何もしない。
    /// 次に読めたときに改めて試す。
    /// </summary>
    /// <returns>既定を当てはめたら true。</returns>
    public bool TryApplyDefault(Config cfg)
    {
        if (cfg.FateVentureTownTerritory != 0)
        {
            return false;
        }

        if (!this.IsListReady())
            {
            return false;
        }

        if (this.Resolve(DefaultTerritory) is not { } found)
        {
            return false;
        }

        cfg.FateVentureTownTerritory = found.TerritoryId;
        ECommons.Configuration.EzConfig.Save();

        this.anomalyLog.Info(
            "HomeTown",
            $"ベンチャー回収の行き先を {found.Name} にしました（既定）");

        return true;
    }

    /// <summary>街へ行く手立て。</summary>
    public enum TravelMethod
    {
        /// <summary>デジョン。ホームタウンと同じなので無料で行ける。</summary>
        Return,

        /// <summary>テレポ。ホームタウンと違うので料金がかかる。</summary>
        Teleport,

        /// <summary>もう着いている。</summary>
        AlreadyThere,
    }

    /// <summary>
    /// その街へ行くのに、デジョンとテレポのどちらを使うか。
    ///
    /// <b>ホームタウンと一致すればデジョン。</b>無料で、エーテライトの
    /// アクセス状況にも左右されない。違えばテレポ。
    /// </summary>
    public TravelMethod ChooseMethod(uint destinationTerritory)
    {
        if (Svc.ClientState.TerritoryType == destinationTerritory)
        {
            return TravelMethod.AlreadyThere;
        }

        var home = this.HomeTerritory();

        return home != 0 && home == destinationTerritory
            ? TravelMethod.Return
            : TravelMethod.Teleport;
    }

    /// <summary>エーテライトの名前。シートから引く（表示名を埋め込まない）。</summary>
    private static string GetName(uint aetheryteId)
    {
        var row = Svc.Data.GetExcelSheet<Aetheryte>()?.GetRowOrDefault(aetheryteId);
        var name = row?.PlaceName.ValueNullable?.Name.ExtractText();

        return string.IsNullOrEmpty(name) ? $"エーテライト {aetheryteId}" : name;
    }
}
