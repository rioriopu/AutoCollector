using AutoCollector.Game;

namespace AutoCollector.Earning.Gatherer;

/// <summary>
/// ギャザラーで稼ぐ（採集した収集品を納品して、スクリップを得る）。
///
/// **いまは振り分けにしか使っていない。**
/// 採集を回す連携先をまだ持っていないため、止める・戻すの対象にしない。
/// <see cref="IsAvailable"/> を false にしてあるので、
/// 稼働判定にも中断にも復帰にも呼ばれない。
///
/// それでも登録しているのは、**ギャザラースクリップのプリセットを
/// 「ギャザラー」の枠へ並べるため。**
/// 枠が無いと「その他」へ落ち、クラフターの設定と見分けがつかない。
///
/// 採集を回せるようにするには段 6（稼ぎの手順を稼ぎ手へ移す）が要る。
/// そこまで来たら <see cref="IsAvailable"/> を連携先の有無で答えるようにする。
/// </summary>
public sealed class GathererEarner(CollectableSourceService source) : IEarner
{
    private readonly CollectableSourceService source = source;

    public string Id => "Gatherer";

    public string DisplayName => "ギャザラー";

    public string KindName => "ギャザラー";

    /// <summary>
    /// 採集を回す連携先をまだ持っていない。
    ///
    /// **false にしておくのは手抜きではなく、約束を守るため。**
    /// 「中断・復帰・稼働判定は導入されている稼ぎ手すべてに行う」ので、
    /// 動かせないものを true にすると、止められないものを止めたことにしてしまう。
    /// </summary>
    public bool IsAvailable => false;

    public bool IsRunning => false;

    public string DescribeRunning() => "ギャザラー";

    /// <summary>
    /// いまは自力で増やせない。採集を回す段取りを持っていないため。
    /// 段 6 のあと、プリセットの設定を見て答えるようにする。
    /// </summary>
    public bool SuppliesCurrencyFor(ExchangePreset preset) => false;

    /// <summary>
    /// この通貨を採集で稼げるか。
    ///
    /// **名前では判断しない。**その通貨を生む収集品に採集地があるかで決める。
    /// </summary>
    public bool CanEarn(uint currencyItemId) => this.source.IsGather(currencyItemId);

    public void MarkInterrupting()
    {
    }

    public bool IsAtSafeBreak(out string reason)
    {
        reason = string.Empty;
        return true;
    }

    // 以下は IsAvailable が false のあいだ呼ばれない。
    // 呼ばれても何もしないほうが安全なので、例外は投げない。
    public EarnerStepResult TickSuspend() => EarnerStepResult.Untouched();

    public EarnerStepResult TickResume() => EarnerStepResult.Untouched();

    public void StopForEmergency()
    {
    }

    public void ResumeAfterStop()
    {
    }
}
