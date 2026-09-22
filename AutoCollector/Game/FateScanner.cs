using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace AutoCollector.Game;

/// <summary>FATE の種別。FateContext.Rule の値をそのまま持つ。</summary>
public enum FateRule : byte
{
    /// <summary>不明・未分類。</summary>
    None = 0,

    /// <summary>討伐。敵を倒すと達成度が上がる。</summary>
    Slay = 1,

    /// <summary>ボス討伐。</summary>
    Boss = 2,

    /// <summary>納品。アイテムを NPC へ渡すと達成度が上がる。</summary>
    Collect = 3,

    /// <summary>防衛。</summary>
    Defend = 4,

    /// <summary>護衛。</summary>
    Escort = 5,

    /// <summary>特殊。</summary>
    Special = 6,
}

/// <summary>
/// 画面に出す・選ぶために読み取った FATE 1 件。
///
/// <b>構造体のポインタを持ち歩かない。</b>
/// FateContext* はゲーム側の都合でいつでも無効になる。
/// 1 フレームの中で読み切って、以降はこの値だけを使う。
/// </summary>
/// <param name="Id">FateId。</param>
/// <param name="Name">表示名。</param>
/// <param name="Position">中心座標。</param>
/// <param name="Radius">円の半径。</param>
/// <param name="Progress">達成度（0〜100）。</param>
/// <param name="Level">FATE のレベル。</param>
/// <param name="MaxLevel">レベルシンクの上限。</param>
/// <param name="Rule">種別。</param>
/// <param name="State">進行状態。</param>
/// <param name="IsBonus">ボーナス付きか。</param>
/// <param name="StartTimeEpoch">開始時刻。同じ Id の再湧きと区別するために使う。</param>
/// <param name="Duration">制限時間（秒）。</param>
/// <param name="HandInCount">納品済み数（納品 FATE のみ）。</param>
/// <param name="TurnInEventItem">納品するアイテムの ItemId（納品 FATE のみ）。</param>
public sealed record FateInfo(
    ushort Id,
    string Name,
    Vector3 Position,
    float Radius,
    byte Progress,
    byte Level,
    byte MaxLevel,
    FateRule Rule,
    FateState State,
    bool IsBonus,
    int StartTimeEpoch,
    short Duration,
    byte HandInCount,
    uint TurnInEventItem)
{
    /// <summary>残り時間（秒）。負なら時間切れ。</summary>
    public float RemainingSeconds
    {
        get
        {
            if (this.StartTimeEpoch <= 0 || this.Duration <= 0)
            {
                return float.MaxValue;
            }

            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - this.StartTimeEpoch;
            return this.Duration - elapsed;
        }
    }

    /// <summary>納品 FATE か。</summary>
    public bool IsCollect => this.Rule == FateRule.Collect;

    /// <summary>同じ FATE の同じ湧きかを判定するための鍵。</summary>
    public (ushort Id, int Start) SpawnKey => (this.Id, this.StartTimeEpoch);
}

/// <summary>FATE の並べ替え基準。</summary>
public enum FateSortKey
{
    /// <summary>ボーナス付きを優先。</summary>
    Bonus,

    /// <summary>達成度が高いものを優先（早く終わる）。</summary>
    Progress,

    /// <summary>残り時間が短いものを優先（逃すともったいない）。</summary>
    TimeRemaining,

    /// <summary>近いものを優先。</summary>
    Distance,

    /// <summary>レベルが高いものを優先。</summary>
    Level,
}

/// <summary>
/// いま湧いている FATE を読み、狙うものを 1 つ選ぶ。
///
/// <b>読み取りしかしない。</b>ゲームの状態を変えないので、
/// 既存機能へ影響を与えることはない。
///
/// FateManager.Fates は StdVector&lt;Pointer&lt;FateContext&gt;&gt;。
/// 要素が null のことがあるため、必ず確認してから読む。
/// </summary>
public sealed unsafe class FateScanner(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>直近の読み取りで例外が出たか。UI の表示に使う。</summary>
    public string? LastError { get; private set; }

    /// <summary>いまのエリアに湧いている FATE をすべて読む。読めなければ空を返す。</summary>
    public IReadOnlyList<FateInfo> ListAll()
    {
        try
        {
            var manager = FateManager.Instance();
            if (manager is null)
            {
                return [];
            }

            var list = new List<FateInfo>();
            var fates = manager->Fates;
            var count = (long)fates.LongCount;

            for (var i = 0L; i < count; i++)
            {
                var ptr = fates[i].Value;
                if (ptr is null)
                {
                    continue;
                }

                if (TryRead(ptr, out var info))
                {
                    list.Add(info);
                }
            }

            this.LastError = null;
            return list;
        }
        catch (Exception ex)
        {
            // 毎フレーム呼ばれるため、記録は控えめにして落とさないことを優先する。
            this.LastError = ex.Message;
            return [];
        }
    }

    /// <summary>いま参加している FATE。参加していなければ null。</summary>
    public FateInfo? GetCurrent()
    {
        try
        {
            var manager = FateManager.Instance();
            if (manager is null)
            {
                return null;
            }

            var current = manager->CurrentFate;
            if (current is null)
            {
                return null;
            }

            return TryRead(current, out var info) ? info : null;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            return null;
        }
    }

    /// <summary>FateId から 1 件読む。消えていれば null。</summary>
    public FateInfo? GetById(ushort fateId)
    {
        try
        {
            var manager = FateManager.Instance();
            if (manager is null)
            {
                return null;
            }

            var ctx = manager->GetFateById(fateId);
            return ctx is not null && TryRead(ctx, out var info) ? info : null;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            return null;
        }
    }

    /// <summary>プレイヤーが FATE の圏内にいるか。</summary>
    public bool IsPlayerInFateRadius()
    {
        try
        {
            var manager = FateManager.Instance();
            if (manager is null || !Player.Available)
            {
                return false;
            }

            var pos = Player.Position;
            return manager->IsInFateRadius(&pos);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 狙う FATE を 1 つ選ぶ。条件に合うものが無ければ null。
    /// </summary>
    /// <param name="playerPos">距離の基準。</param>
    /// <param name="minTimeRemainingSec">残り時間がこれ未満なら選ばない。</param>
    /// <param name="maxProgressPct">達成度がこれを超えていたら選ばない。</param>
    /// <param name="levelFilter">レベル差で絞るか。</param>
    /// <param name="playerLevel">レベル差の基準。</param>
    /// <param name="maxLevelBelow">下に許す差。</param>
    /// <param name="maxLevelAbove">上に許す差。</param>
    /// <param name="skipCollect">納品 FATE を避けるか。</param>
    /// <param name="blacklist">このセッションで詰まった FATE。</param>
    /// <param name="sortOrder">並べ替えの優先順位。</param>
    public FateInfo? PickNext(
        Vector3 playerPos,
        int minTimeRemainingSec,
        int maxProgressPct,
        bool levelFilter,
        int playerLevel,
        int maxLevelBelow,
        int maxLevelAbove,
        bool skipCollect,
        IReadOnlySet<ushort>? blacklist,
        IReadOnlyList<FateSortKey>? sortOrder)
    {
        var candidates = this.ListAll().Where(f => IsEligible(
            f, minTimeRemainingSec, maxProgressPct, levelFilter,
            playerLevel, maxLevelBelow, maxLevelAbove, skipCollect, blacklist));

        return Sort(candidates, sortOrder ?? DefaultSortOrder, playerPos).FirstOrDefault();
    }

    /// <summary>狙う対象になりうるか。</summary>
    public static bool IsEligible(
        FateInfo f,
        int minTimeRemainingSec,
        int maxProgressPct,
        bool levelFilter,
        int playerLevel,
        int maxLevelBelow,
        int maxLevelAbove,
        bool skipCollect,
        IReadOnlySet<ushort>? blacklist)
    {
        // 進行中のものだけを狙う。Preparing は NPC に話しかけて始めるものがあるが、
        // その扱いは実機で確認してから足す。いまは確実に戦えるものに絞る。
        if (f.State != FateState.Running)
        {
            return false;
        }

        if (blacklist is not null && blacklist.Contains(f.Id))
        {
            return false;
        }

        if (skipCollect && f.IsCollect)
        {
            return false;
        }

        // 達成度 100% のものは、もう貢献できない。
        if (f.Progress >= 100 || f.Progress > maxProgressPct)
        {
            return false;
        }

        // 着く前に終わるものを避ける。
        if (f.RemainingSeconds < minTimeRemainingSec)
        {
            return false;
        }

        // 座標が取れていないものは目的地にできない。
        if (f.Position == Vector3.Zero)
        {
            return false;
        }

        if (levelFilter && playerLevel > 0 && f.Level > 0)
        {
            if (f.Level < playerLevel - maxLevelBelow || f.Level > playerLevel + maxLevelAbove)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>既定の並べ替え。</summary>
    public static readonly IReadOnlyList<FateSortKey> DefaultSortOrder =
    [
        FateSortKey.Bonus,
        FateSortKey.Progress,
        FateSortKey.TimeRemaining,
        FateSortKey.Distance,
    ];

    /// <summary>残り時間がこれを切ったら「急ぎ」とみなす。</summary>
    private const float UrgentSeconds = 240f;

    private static IOrderedEnumerable<FateInfo> Sort(
        IEnumerable<FateInfo> source,
        IReadOnlyList<FateSortKey> order,
        Vector3 playerPos)
    {
        IOrderedEnumerable<FateInfo>? sorted = null;

        foreach (var key in order.Count > 0 ? order : DefaultSortOrder)
        {
            var (selector, descending) = KeyOf(key, playerPos);
            sorted = sorted is null
                ? (descending ? source.OrderByDescending(selector) : source.OrderBy(selector))
                : (descending ? sorted.ThenByDescending(selector) : sorted.ThenBy(selector));
        }

        return sorted ?? source.OrderBy(_ => 0);
    }

    private static (Func<FateInfo, IComparable> Selector, bool Descending) KeyOf(FateSortKey key, Vector3 playerPos)
        => key switch
        {
            FateSortKey.Bonus => (f => f.IsBonus, true),
            FateSortKey.Progress => (f => f.Progress, true),

            // 急ぎのものだけ残り時間で比べる。急ぎでないものは同じ値にして、
            // 次の基準（距離など）で並ぶようにする。
            FateSortKey.TimeRemaining => (f => f.RemainingSeconds < UrgentSeconds ? f.RemainingSeconds : UrgentSeconds, false),

            FateSortKey.Distance => (f => Vector3.DistanceSquared(f.Position, playerPos), false),
            FateSortKey.Level => (f => f.Level, true),
            _ => (_ => 0, false),
        };

    /// <summary>FateContext を 1 件読む。読めなければ false。</summary>
    private static bool TryRead(FateContext* ctx, out FateInfo info)
    {
        info = null!;

        if (ctx is null)
        {
            return false;
        }

        var id = ctx->FateId;
        if (id == 0)
        {
            return false;
        }

        info = new FateInfo(
            Id: id,
            Name: ctx->Name.ToString(),
            Position: ctx->Location,
            Radius: ctx->Radius,
            Progress: ctx->Progress,
            Level: ctx->Level,
            MaxLevel: ctx->MaxLevel,
            Rule: (FateRule)ctx->Rule,
            State: ctx->State,
            IsBonus: ctx->IsBonus,
            StartTimeEpoch: ctx->StartTimeEpoch,
            Duration: ctx->Duration,
            HandInCount: ctx->HandInCount,
            TurnInEventItem: ctx->TurnInEventItem);

        return true;
    }
}
