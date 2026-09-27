using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoCollector.Diagnostics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace AutoCollector.Game;

/// <summary>
/// FATE の種別。<see cref="FateContext"/>.Rule の値をそのまま持つ。
///
/// <b>値の意味は実データで確かめてある（2026-09-27）。</b>
/// 以前は Boss=2 / Collect=3 / Escort=5 としていたが、これは誤りだった。
/// そのため納品 FATE（Rule=2）を納品と認識できず、
/// 納品の段取りが一度も動いていなかった。
///
/// 確かめた根拠は 3 つ。
///   1. Fate シートの Rule=2 は 96 件あり、<b>96/96 件が EventItem を持つ</b>。
///      Rule=3 は 21 件あり、<b>21/21 件が LGBGuardNPCLocation（護衛対象）を持つ</b>
///   2. Rule=2 の目的文は「〜に速やかに納品せよ」。
///      目的に「納品」を含む 92 件は<b>すべて Rule=2</b>で、他の Rule には 1 件も無い
///   3. 実際に動いている bozjalone も Rule==2 を納品判定に使っている
///      （bozjalone.Modules.Fates/Fate.cs の IsDeliveryFate）
///
/// 一覧は C:\ソース\FF14_FATE一覧\ にある。
/// </summary>
public enum FateRule : byte
{
    /// <summary>不明・未分類（演習など）。</summary>
    None = 0,

    /// <summary>討伐。敵を倒すと達成度が上がる。</summary>
    Slay = 1,

    /// <summary>納品。集めた品を NPC へ渡すと達成度が上がる。</summary>
    Collect = 2,

    /// <summary>護衛。対象の NPC に話しかけて始める。</summary>
    Escort = 3,

    /// <summary>防衛・迎撃。</summary>
    Defend = 4,

    /// <summary>季節イベント。</summary>
    Seasonal = 5,

    /// <summary>特殊討伐。</summary>
    Special = 6,

    /// <summary>蒼天街復興。</summary>
    Ishgard = 7,

    /// <summary>蒼天街復興祝祭。</summary>
    IshgardFestival = 8,

    /// <summary>コスモ探索（基地建設）。</summary>
    Cosmic = 9,
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

    /// <summary>
    /// 納品 FATE か。
    ///
    /// <b>Rule だけで決めない。</b>
    /// 集める品（EventItem）を持っているかも見る。
    /// 実データでは Rule=2 と EventItem の有無が 96/96 件で一致するので
    /// どちらでも同じ結果になるが、両方見ておけば
    /// 片方の解釈が外れても、もう片方で拾える。
    ///
    /// 個数を数える <see cref="FateScanner.CountHandInItems"/> は
    /// もともと EventItem を見ており、そちらは正しく動いていた。
    /// 入口のこの判定だけが Rule の取り違えで false になり、
    /// 納品の段取りへ進めなくなっていた。
    /// </summary>
    public bool IsCollect => this.Rule == FateRule.Collect || this.HasHandInItem;

    /// <summary>集める品を持つ FATE か（＝納品 FATE）。</summary>
    public bool HasHandInItem { get; init; }

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

    /// <summary>
    /// 検証用に、FATE が 1 つも無いことにする。
    ///
    /// <b>マップの FATE が枯れた状況は、待っていても滅多に起きない。</b>
    /// 次のマップへ移る動きを確かめるために、こちらから
    /// 「見つからない」と思い込ませる。
    ///
    /// 参加中の FATE（<see cref="GetCurrent"/>）には効かせない。
    /// 戦っている最中に切ると、戦闘の後始末を通らずに離脱してしまう。
    /// </summary>
    public bool PretendEmpty { get; set; }

    /// <summary>いまのエリアに湧いている FATE をすべて読む。読めなければ空を返す。</summary>
    public IReadOnlyList<FateInfo> ListAll()
    {
        // 検証用。見つからないことにする。
        if (this.PretendEmpty)
        {
            return [];
        }

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

    /// <summary>
    /// 納品 FATE で、納品できるだけの品を持っているか。
    ///
    /// <b>持っていれば BMR が納品へ向かう。</b>
    /// BMR の FateUtils は 10 個たまると納品 NPC を狙い、移動を強制する。
    /// そのあいだにこちらから経路を積むと引っ張り合いになるので、
    /// 見分けて手を引くために使う。
    ///
    /// 品の番号は Fate シートの EventItem から引く（BMR と同じ）。
    /// 集めた品は鞄ではなくキーアイテム欄に入る。
    /// </summary>
    public bool HasHandInItems(ushort fateId, int required = 10)
        => this.CountHandInItems(fateId) is { } held && held >= required;

    /// <summary>FateId → 集める品を持つか。シートは変わらないので覚えておく。</summary>
    private static readonly Dictionary<ushort, bool> EventItemCache = [];

    /// <summary>
    /// この FATE が「集める品」を持つか（＝納品 FATE か）。
    ///
    /// Rule の値とは別に、シートの EventItem を直接見る。
    /// 実データでは Rule=2 の 96 件すべてが EventItem を持ち、
    /// 他の Rule は 1 件も持たない。
    /// </summary>
    private static bool HasEventItem(ushort fateId)
    {
        if (EventItemCache.TryGetValue(fateId, out var cached))
        {
            return cached;
        }

        var has = false;

        try
        {
            has = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Fate>()
                ?.GetRowOrDefault(fateId)?.EventItem.RowId is > 0;
        }
        catch
        {
            // 読めなければ false。Rule のほうで拾う。
        }

        EventItemCache[fateId] = has;
        return has;
    }

    /// <summary>
    /// 納品 FATE で集めた品の数。
    ///
    /// <b>読めなかったときは 0 ではなく null を返す。</b>
    /// 0 として扱うと「まだ集まっていない」と誤って判断し、
    /// 納品の途中で討伐へ戻ってしまう。読めないことと
    /// 持っていないことは別の話。
    /// </summary>
    /// <returns>個数。読めなければ null。</returns>
    public int? CountHandInItems(ushort fateId)
    {
        try
        {
            var item = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Fate>()?.GetRowOrDefault(fateId)?.EventItem.RowId ?? 0;
            if (item == 0)
            {
                // 納品 FATE ではない。集める品がそもそも無い。
                return 0;
            }

            var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inventory is null)
            {
                return null;
            }

            // **FATE の収集品はキーアイテムに入る。鞄には入らない。**
            //
            // GetInventoryItemCount は通常の持ち物とアーマリーを見るので、
            // ここで数えると**何個集めても必ず 0 が返る**。
            // その結果「まだ集まっていない」と判断し続け、
            // 納品の段取りへ一度も入れなかった。
            //
            // BossMod Reborn も InventoryType.KeyItems を直接見ている
            // （WorldStateGameSync.cs:1027）。
            return inventory->GetItemCountInContainer(
                item, FFXIVClientStructs.FFXIV.Client.Game.InventoryType.KeyItems);
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// いま参加中の FATE にレベルシンクが入っているか。
    ///
    /// <b>シンクは自動では入らない。</b>「LEVEL SYNC」を押す必要があり、
    /// 押さないと参加した扱いにならず、敵も狙えない。
    /// 押すのは BossMod Reborn に任せてあるので、ここでは入ったかを見る。
    /// </summary>
    public bool IsPlayerSyncedToFate()
    {
        try
        {
            var manager = FateManager.Instance();
            if (manager is null)
            {
                return false;
            }

            var current = manager->CurrentFate;
            return current is not null && manager->IsSyncedToFate(current);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// この FATE の敵のうち、いちばん近いものを探す。
    ///
    /// <b>敵が見えていないと BMR は何もしない。</b>
    /// 円の端に降りると、敵が遠くて棒立ちになる。
    /// そのときに歩いて近づくため、どこに居るかを知る必要がある。
    /// </summary>
    /// <param name="fateId">対象の FATE。</param>
    /// <param name="from">距離を測る基準。</param>
    /// <returns>見つかれば座標と距離。居なければ null。</returns>
    public (Vector3 Position, float Distance)? FindNearestMob(ushort fateId, Vector3 from)
    {
        try
        {
            Vector3 nearest = default;
            var nearestDistance = float.MaxValue;
            var found = false;

            foreach (var obj in Svc.Objects)
            {
                if (obj is not IBattleNpc npc)
                {
                    continue;
                }

                if (!npc.IsTargetable || npc.CurrentHp == 0)
                {
                    continue;
                }

                var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address;
                if (native is null || native->FateId != fateId)
                {
                    continue;
                }

                // 討伐の対象になる敵だけを見る。
                // NPC や設置物は同じ FateId を持つことがある。
                if (native->BattleNpcSubKind != FFXIVClientStructs.FFXIV.Client.Game.Object.BattleNpcSubKind.Combatant)
                {
                    continue;
                }

                // 高さは見ない。段差の上下で距離が水増しされる。
                var distance = Vector2.Distance(
                    new Vector2(from.X, from.Z),
                    new Vector2(npc.Position.X, npc.Position.Z));

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = npc.Position;
                    found = true;
                }
            }

            return found ? (nearest, nearestDistance) : null;
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
        IReadOnlyList<FateSortKey>? sortOrder,
        bool followParty = false)
    {
        var candidates = this.ListAll().Where(f => IsEligible(
            f, minTimeRemainingSec, maxProgressPct, levelFilter,
            playerLevel, maxLevelBelow, maxLevelAbove, skipCollect, blacklist))
            .ToList();

        // **仲間が入っている FATE を優先する。**
        //
        // 同じ FATE に集まったほうが早く終わる。
        // ただし条件（残り時間・達成度・見送り中など）は曲げない。
        // 仲間の FATE が条件から外れているなら、ふつうに選び直す。
        if (followParty && this.FindPartyFateId() is { } partyFate)
        {
            var shared = candidates.FirstOrDefault(f => f.Id == partyFate);
            if (shared is not null)
            {
                return shared;
            }
        }

        return Sort(candidates, sortOrder ?? DefaultSortOrder, playerPos).FirstOrDefault();
    }

    /// <summary>
    /// パーティの誰かが参加している FATE。いなければ null。
    ///
    /// <b>やり取りはしない。</b>相手の座標から、その場に湧いている FATE の
    /// 円に入っているかを見るだけ。相手が同じプラグインを使っている必要は無い。
    ///
    /// 何人もいれば、いちばん多く入っている FATE を採る。
    /// </summary>
    public ushort? FindPartyFateId()
    {
        try
        {
            if (Svc.Party.Length == 0)
            {
                return null;
            }

            var fates = this.ListAll();
            if (fates.Count == 0)
            {
                return null;
            }

            var me = Player.Available ? Player.Object?.GameObjectId ?? 0 : 0;
            var counts = new Dictionary<ushort, int>();

            foreach (var member in Svc.Party)
            {
                if (member.GameObject is null || member.GameObject.GameObjectId == me)
                {
                    continue;
                }

                var at = member.Position;

                foreach (var fate in fates)
                {
                    if (fate.State != FateState.Running)
                    {
                        continue;
                    }

                    var flat = Vector2.Distance(
                        new Vector2(at.X, at.Z),
                        new Vector2(fate.Position.X, fate.Position.Z));

                    if (flat <= fate.Radius)
                    {
                        counts[fate.Id] = counts.GetValueOrDefault(fate.Id) + 1;
                        break;
                    }
                }
            }

            if (counts.Count == 0)
            {
                return null;
            }

            return counts.OrderByDescending(x => x.Value).First().Key;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            return null;
        }
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
    /// <summary>
    /// 最寄りを第一にする並べ替え。
    ///
    /// <b>距離だけで決める。</b>ボーナス・達成度・残り時間で割り込ませない。
    /// 「近いものから順に潰していく」という遊び方のための並び。
    ///
    /// 距離はプレイヤーと FATE 中心の<b>水平距離</b>で測る。
    /// 飛行の経路長ではないので、高低差の大きい地形では
    /// 実際の移動距離と一致しないことがある。
    /// </summary>
    public static readonly IReadOnlyList<FateSortKey> NearestFirstSortOrder =
    [
        FateSortKey.Distance,
    ];

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

            // **水平距離で測る。**
            // 高さを混ぜると、真下や真上の FATE が実際より遠く見える。
            // 飛んで向かうので、上下差は移動のしやすさとほぼ関係がない。
            FateSortKey.Distance => (f => Vector2.DistanceSquared(
                new Vector2(f.Position.X, f.Position.Z),
                new Vector2(playerPos.X, playerPos.Z)), false),
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
            TurnInEventItem: ctx->TurnInEventItem)
        {
            // 集める品を持つかは、シートを引いて覚えておく。
            // 毎フレーム引くと重いので、読み取った時点で 1 回だけ。
            HasHandInItem = HasEventItem(id),
        };

        return true;
    }
}
