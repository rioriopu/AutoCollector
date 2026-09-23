using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCollector.Earning;

/// <summary>
/// 稼ぎ手の登録簿。
///
/// **誰を止めたかを覚えているのはここだけ。**
/// 呼ぶ側は「全員を中断して」「止めたぶんを戻して」としか言わない。
/// 誰が対象かを呼ぶ側が判断すると、条件が散って食い違う。
///
/// 過去に出た不具合はいずれもその形だった。
///
/// <code>
/// F-60  止めるとき立てた旗を、戻す側が全部下ろしていない
/// F-61  周回を止める側が「まだ渡せていない交換」を知らない
/// F-68  止めたあと、製作だけが動いて納品が弾かれる（旗が 2 本あって片方しか見ていなかった）
/// </code>
///
/// ## 使い方
///
/// <code>
/// BeginSuspendAll()          中断を始める（1 回だけ）
/// TickSuspendAll()           Done を返すまで毎フレーム
///   … 交換 …
/// BeginResumeAll()           復帰を始める（1 回だけ）
/// TickResumeAll()            Done を返すまで毎フレーム
/// </code>
///
/// ## まとめて中断する口は、まだ交換から呼んでいない
///
/// **止める順序が載荷条件になっているため。**
///
/// <code>
/// Artisan を止める → AutoRetainer を抑制する → AutoDuty を止める
/// </code>
///
/// 真ん中の AutoRetainer は稼ぎ手ではない（協調的な抑制の相手）。
/// まとめて中断すると AutoDuty が先に止まり、
/// 抑制が立つ前にリテイナー処理が始まる隙間ができる。
///
/// いまは交換の側が稼ぎ手を 1 つずつ呼び、この順序を保っている。
/// <see cref="StopAllForEmergency"/> と <see cref="ResumeAllAfterStop"/> は使っている。
/// </summary>
public sealed class EarnerRegistry
{
    private readonly List<IEarner> earners = [];

    /// <summary>自分が止めた稼ぎ手。**ここに入った相手にしか復帰を配らない。**</summary>
    private readonly HashSet<string> suspendedByUs = [];

    /// <summary>まだ中断が終わっていない稼ぎ手。終わったものを呼び続けない。</summary>
    private readonly HashSet<string> pendingSuspend = [];

    /// <summary>まだ復帰が終わっていない稼ぎ手。</summary>
    private readonly HashSet<string> pendingResume = [];

    public void Register(IEarner earner)
    {
        if (this.earners.Any(x => x.Id == earner.Id))
        {
            throw new InvalidOperationException($"稼ぎ手 {earner.Id} が二重に登録されています");
        }

        this.earners.Add(earner);
    }

    public IReadOnlyList<IEarner> All => this.earners;

    /// <summary>導入されている稼ぎ手だけ。中断も復帰も稼働判定もこれを相手にする。</summary>
    public IEnumerable<IEarner> Available => this.earners.Where(x => x.IsAvailable);

    /// <summary>いま稼いでいる稼ぎ手の表示名。1 つも無ければ空。</summary>
    public IReadOnlyList<string> GetRunning()
        => this.Available.Where(x => x.IsRunning).Select(x => x.DescribeRunning()).ToList();

    /// <summary>1 つでも稼いでいれば true。名前を detail に返す。</summary>
    public bool IsAnyRunning(out string detail)
    {
        var running = this.GetRunning();
        detail = running.Count == 0 ? string.Empty : string.Join(" / ", running);
        return running.Count > 0;
    }

    /// <summary>
    /// このプリセットで、自力で通貨を増やせる稼ぎ手がいるか。
    /// 交換の歯止めで「終わりを決めずに回してよいか」の判断に使う。
    ///
    /// **ここだけは導入されていない稼ぎ手も見る。**
    /// 「このプリセットは自分で稼ぐ設定か」を聞いているのであって、
    /// いま動かせるかを聞いているのではない。
    /// 連携先が入っていないことを理由に設定の意味を変えると、
    /// 相手プラグインを入れ直すまで交換が止まる。
    /// </summary>
    public bool AnySuppliesCurrencyFor(ExchangePreset preset)
        => this.earners.Any(x => x.SuppliesCurrencyFor(preset));

    /// <summary>
    /// いま全員が中断してよい切れ目にいるか。
    /// 1 人でも切れ目でなければ false を返し、その理由を出す。
    /// </summary>
    public bool AllAtSafeBreak(out string reason)
    {
        foreach (var earner in this.Available)
        {
            if (!earner.IsAtSafeBreak(out var why))
            {
                reason = $"{earner.DisplayName}: {why}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 中断を始める。**1 回だけ呼ぶ。**
    ///
    /// 前回の記憶はここで捨てる。持ち越すと、今回止めていない相手へ復帰を配ってしまう。
    /// </summary>
    public void BeginSuspendAll()
    {
        this.suspendedByUs.Clear();
        this.pendingResume.Clear();
        this.pendingSuspend.Clear();

        foreach (var earner in this.Available)
        {
            this.pendingSuspend.Add(earner.Id);
            earner.MarkInterrupting();
        }
    }

    /// <summary>
    /// 中断を 1 フレーム進める。<see cref="EarnerProgress.Done"/> を返すまで毎フレーム呼ぶ。
    ///
    /// 終わった稼ぎ手は対象から外す。止め終わった相手へ停止要求を送り続けない。
    /// 1 人でも失敗したらその場で失敗を返す。
    /// </summary>
    public EarnerStepResult TickSuspendAll()
    {
        var detail = string.Empty;

        foreach (var earner in this.Available.ToList())
        {
            if (!this.pendingSuspend.Contains(earner.Id))
            {
                continue;
            }

            var result = earner.TickSuspend();

            switch (result.Progress)
            {
                case EarnerProgress.Failed:
                    return EarnerStepResult.Failed($"{earner.DisplayName}: {result.Detail}");

                case EarnerProgress.InProgress:
                    detail = $"{earner.DisplayName}: {result.Detail}";
                    continue;

                case EarnerProgress.Done:
                    this.pendingSuspend.Remove(earner.Id);

                    // **実際に止めた相手だけを覚える。**
                    // もともと動いていなかった相手を覚えると、
                    // 交換のあとに勝手に動かすことになる。
                    if (result.Acted)
                    {
                        this.suspendedByUs.Add(earner.Id);
                    }

                    continue;
            }
        }

        return this.pendingSuspend.Count == 0
            ? EarnerStepResult.Handled(this.DescribeSuspended())
            : EarnerStepResult.InProgress(detail);
    }

    /// <summary>
    /// 復帰を始める。**1 回だけ呼ぶ。**
    /// 自分が止めた相手だけが対象になる。ここで条件を書かなくても、止めていない相手は動かない。
    /// </summary>
    public void BeginResumeAll()
    {
        this.pendingResume.Clear();
        foreach (var id in this.suspendedByUs)
        {
            this.pendingResume.Add(id);
        }
    }

    /// <summary>
    /// 復帰を 1 フレーム進める。<see cref="EarnerProgress.Done"/> を返すまで毎フレーム呼ぶ。
    ///
    /// **復帰できなくても交換そのものは終わっている。**
    /// 失敗は返すが、呼ぶ側は止まらずに終いまで進めること。
    /// </summary>
    public EarnerStepResult TickResumeAll()
    {
        var detail = string.Empty;
        var failure = string.Empty;

        foreach (var earner in this.Available.ToList())
        {
            if (!this.pendingResume.Contains(earner.Id))
            {
                continue;
            }

            var result = earner.TickResume();

            switch (result.Progress)
            {
                case EarnerProgress.Failed:
                    // 戻せなかった相手も対象から外す。
                    // 残すと毎フレーム失敗を返し続けて先へ進めない。
                    this.pendingResume.Remove(earner.Id);
                    this.suspendedByUs.Remove(earner.Id);
                    failure = $"{earner.DisplayName}: {result.Detail}";
                    continue;

                case EarnerProgress.InProgress:
                    detail = $"{earner.DisplayName}: {result.Detail}";
                    continue;

                case EarnerProgress.Done:
                    this.pendingResume.Remove(earner.Id);
                    this.suspendedByUs.Remove(earner.Id);
                    continue;
            }
        }

        if (this.pendingResume.Count > 0)
        {
            return EarnerStepResult.InProgress(detail);
        }

        return failure.Length > 0
            ? EarnerStepResult.Failed(failure)
            : EarnerStepResult.Handled();
    }

    /// <summary>
    /// 緊急停止。導入されている稼ぎ手すべてを、待たずにその場で止める。
    ///
    /// **1 人が投げても残りを止める。** 途中で抜けると止め残しが出る。
    /// </summary>
    public IReadOnlyList<string> StopAllForEmergency()
    {
        var errors = new List<string>();

        foreach (var earner in this.Available.ToList())
        {
            try
            {
                earner.StopForEmergency();
            }
            catch (Exception ex)
            {
                errors.Add($"{earner.DisplayName}: {ex.Message}");
            }
        }

        return errors;
    }

    /// <summary>
    /// 緊急停止で止めたぶんを戻す。
    ///
    /// **必ず全員に配る。** 抑制を返さないまま終えると、
    /// 利用者の AutoRetainer が止まったままになる。
    /// </summary>
    public IReadOnlyList<string> ResumeAllAfterStop()
    {
        var errors = new List<string>();

        foreach (var earner in this.Available.ToList())
        {
            try
            {
                earner.ResumeAfterStop();
            }
            catch (Exception ex)
            {
                errors.Add($"{earner.DisplayName}: {ex.Message}");
            }
        }

        this.suspendedByUs.Clear();
        this.pendingSuspend.Clear();
        this.pendingResume.Clear();

        return errors;
    }

    /// <summary>
    /// 中断の記憶を捨てる。交換を取りやめたときに呼ぶ。
    /// 捨てないと、次の交換で「前回止めた相手」へ復帰を配ってしまう。
    /// </summary>
    public void ForgetSuspended()
    {
        this.suspendedByUs.Clear();
        this.pendingSuspend.Clear();
        this.pendingResume.Clear();
    }

    /// <summary>
    /// 導入されている稼ぎ手の名前を並べた文。画面の案内に埋める。
    ///
    /// **1 つも導入されていないときは、そう書く。**
    /// 名前を並べるだけだと空文字になり、文が壊れる。
    /// </summary>
    public string DescribeAvailable()
    {
        var names = this.Available.Select(x => x.DisplayName).ToList();
        return names.Count == 0
            ? "連携できる稼ぎ手（AutoDuty / Artisan など）"
            : string.Join(" や ", names);
    }

    /// <summary>
    /// この通貨を増やせる稼ぎ手。いなければ null。
    ///
    /// **導入されていない稼ぎ手も見る。**
    /// 「どの遊び方の通貨か」はゲームデータで決まっていて、
    /// 連携先が入っているかとは関係が無い。
    /// 入っていないことを理由に見出しが変わると、一覧が並べ替わって驚く。
    /// </summary>
    public IEarner? FindFor(uint currencyItemId)
        => this.earners.FirstOrDefault(x => x.CanEarn(currencyItemId));

    /// <summary>
    /// この通貨の稼ぎ方の名前。分からなければ「その他」。
    ///
    /// ギャザラーの稼ぎ手を足すまで、ギャザラースクリップはここへ落ちる。
    /// **落ちること自体は正しい。**いまは採集で稼ぐ段取りを持っていない。
    /// </summary>
    public string KindNameFor(uint currencyItemId)
        => this.FindFor(currencyItemId)?.KindName ?? "その他";

    /// <summary>稼ぎ方の名前を、登録した順に並べたもの。最後に「その他」を足す。</summary>
    public IReadOnlyList<string> ListKindNames()
    {
        var names = this.earners.Select(x => x.KindName).ToList();
        names.Add("その他");
        return names;
    }

    /// <summary>いま自分が止めている稼ぎ手の表示名。記録と画面のため。</summary>
    public string DescribeSuspended()
    {
        var names = this.earners
            .Where(x => this.suspendedByUs.Contains(x.Id))
            .Select(x => x.DisplayName)
            .ToList();

        return names.Count == 0 ? "（止めた稼ぎ手はありません）" : string.Join(" / ", names);
    }

    /// <summary>自分がこの稼ぎ手を止めているか。</summary>
    public bool IsSuspendedByUs(string id) => this.suspendedByUs.Contains(id);
}
