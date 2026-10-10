using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCollector.Automation;

/// <summary>話しかける候補の NPC 1 人ぶん。</summary>
/// <param name="Id">GameObjectId。</param>
/// <param name="Distance">自分からの距離。</param>
/// <param name="Designated">ゲームが指定した開始役（FateContext.MotivationNpc）か。</param>
public readonly record struct StartCandidate(ulong Id, float Distance, bool Designated);

/// <summary>次にどうするか。</summary>
public enum StartStep
{
    /// <summary>話しかける相手が居ない（全員に話しかけた）。</summary>
    None,

    /// <summary>開始役が現れるのを待つ。</summary>
    Wait,

    /// <summary>この NPC へ近づいて話しかける。</summary>
    Talk,
}

/// <summary>
/// NPC に話しかけて始まる FATE で、誰に話しかけるかを決める（ゲームに触らない）。
///
/// <b>利用者の要件（2026-10-10）：</b>FATE の周りに話しかけられる NPC が 3〜4 人いることがある。
/// 話しかけて 3 秒以内に FATE が始まらなければ、別の NPC に話しかける。
/// 例：A・B・C がいるとき、A に話しかけて 3 秒変化なし → B に話しかけて 3 秒以内に開始 → C には話しかけない。
///
/// <b>ゲームが指定した開始役を最初に試す。</b>実機ではこれで始まっている（2026-10-10 12:48・12:50 の 2 件）。
/// 開始役がまだ見えない（読み込まれていない）うちは少しだけ待つ。AutoFATEGrind も現れるのを待ってから話しかける。
/// そのあとは近い順。
///
/// 3 秒は会話の窓が閉じてから数える（会話の途中で次の NPC へ行かない）。
/// FATE が始まったかは呼び出し側が FATE の状態で見る（始まれば、そもそもここを呼ばない）。
/// </summary>
public sealed class StartAttempts
{
    /// <summary>話し終えてから、FATE が始まるのを待つ時間（利用者の指定）。</summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(3);

    /// <summary>開始役が見えないとき、ほかの NPC へ行く前に待つ時間。</summary>
    public static readonly TimeSpan DesignatedGrace = TimeSpan.FromSeconds(5);

    private readonly HashSet<ulong> tried = [];
    private DateTime endedUtc;

    /// <summary>いま向かっている・話しかけた NPC。無ければ 0。</summary>
    public ulong Current { get; private set; }

    /// <summary>いまの NPC に話しかけたか（会話を始めたか）。</summary>
    public bool Talked { get; private set; }

    /// <summary>話しかけ終えた NPC の数（記録用）。</summary>
    public int TriedCount => this.tried.Count;

    /// <summary>
    /// 次に話しかける NPC を決める。
    /// </summary>
    /// <param name="candidates">FATE の周りで話しかけられる NPC。</param>
    /// <param name="sinceStart">この FATE の開始を試し始めてからの時間。</param>
    public (StartStep Step, ulong Id) Choose(IReadOnlyList<StartCandidate> candidates, TimeSpan sinceStart)
    {
        // 向かっている・話しかけた相手が居れば、その人のまま（歩いている間に近い順が入れ替わっても替えない）。
        if (this.Current != 0 && candidates.Any(x => x.Id == this.Current))
        {
            return (StartStep.Talk, this.Current);
        }

        // 見えなくなった。話しかけ済みとして次へ。
        if (this.Current != 0)
        {
            this.tried.Add(this.Current);
            this.Current = 0;
            this.Talked = false;
            this.endedUtc = default;
        }

        var left = candidates.Where(x => !this.tried.Contains(x.Id)).ToList();

        if (left.FirstOrDefault(x => x.Designated) is { Id: not 0 } designated)
        {
            this.Current = designated.Id;
            return (StartStep.Talk, designated.Id);
        }

        // 開始役にまだ話しかけておらず、見えてもいない。少し待つ。
        if (!candidates.Any(x => x.Designated) && this.tried.Count == 0 && sinceStart < DesignatedGrace)
        {
            return (StartStep.Wait, 0);
        }

        if (left.OrderBy(x => x.Distance).FirstOrDefault() is { Id: not 0 } nearest)
        {
            this.Current = nearest.Id;
            return (StartStep.Talk, nearest.Id);
        }

        return (StartStep.None, 0);
    }

    /// <summary>いまの NPC に話しかけた。</summary>
    public void MarkTalked() => this.Talked = this.Current != 0;

    /// <summary>会話の窓が閉じた。ここから 3 秒待つ。</summary>
    public void DialogEnded(DateTime now)
    {
        if (this.Talked)
        {
            this.endedUtc = now;
        }
    }

    /// <summary>話し終えて、FATE が始まるのを待っている最中か。</summary>
    public bool Waiting(DateTime now) => this.Talked && this.endedUtc != default && now - this.endedUtc < StartWait;

    /// <summary>話し終えて 3 秒たっても始まらなかったか。</summary>
    public bool Expired(DateTime now) => this.Talked && this.endedUtc != default && now - this.endedUtc >= StartWait;

    /// <summary>いまの NPC はもう試した。次の NPC へ。</summary>
    public void Next()
    {
        if (this.Current != 0)
        {
            this.tried.Add(this.Current);
        }

        this.Current = 0;
        this.Talked = false;
        this.endedUtc = default;
    }

    /// <summary>忘れる。別の FATE・別の湧きになったときに呼ぶ。</summary>
    public void Reset()
    {
        this.tried.Clear();
        this.Current = 0;
        this.Talked = false;
        this.endedUtc = default;
    }
}

/// <summary>
/// 指定外の NPC の確認の窓を押してよいか（ゲームに触らない）。
///
/// <b>ゲームが指定した開始役ではない NPC の「はい／いいえ」・選択肢は、むやみに押さない。</b>
/// 周りの NPC には運び屋（「〜へ移動しますか」）や店がいることがあり、押すと FATE と関係のないことが起きる。
/// FATE の開始を確かめる文には FATE の名前か「F.A.T.E」が入っていると見て、そのときだけ押す。
/// 開始の確認文は Addon 表には無かった（2026-10-10 に「F.A.T.E」を含む行を全部調べた。ボタン名「JOIN F.A.T.E.」だけ）。
/// FATE ごとの会話の側にあると見られるため、文の中身で見分ける。
/// </summary>
public static class StartPrompt
{
    public static bool MayAccept(string? text, string fateName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var plain = Normalize(text);
        var name = Normalize(fateName);

        return (name.Length > 0 && plain.Contains(name, StringComparison.OrdinalIgnoreCase))
            || plain.Contains("F.A.T.E", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("FATE", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
        => value.Replace(" ", string.Empty).Replace("　", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty).Trim();
}
