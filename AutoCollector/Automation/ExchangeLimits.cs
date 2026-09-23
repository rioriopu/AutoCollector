using System;
using System.Collections.Generic;

namespace AutoCollector.Automation;

/// <summary>
/// 交換をどこまで許すかの設定。
///
/// **窓口の種類に依らない。** トームストーンの窓口でもアイテム交換画面でも、
/// 利用者が決める歯止めは同じもの。窓口ごとに違うのは撃ち方だけ。
/// </summary>
/// <param name="Unlimited">個数の上限を設けないか。</param>
/// <param name="RemainingItems">残りの**個数**。<paramref name="Unlimited"/> が true なら見ない。</param>
/// <param name="OwnedLimit">所持の上限（個数）。0 なら上限なし。</param>
/// <param name="Mode">どこまで交換するか。</param>
/// <param name="CurrencyReserve">残す通貨量。<see cref="ExchangeMode.UntilCurrencyReserve"/> のときだけ見る。</param>
/// <param name="RemainingTrades">残りの交換**回数**。<see cref="ExchangeMode.FixedQuantity"/> のときだけ見る。</param>
/// <param name="TargetQuantity">目標の所持数（個数）。<see cref="ExchangeMode.UntilTargetQuantity"/> のときだけ見る。</param>
public sealed record ExchangeLimitSet(
    bool Unlimited,
    int RemainingItems,
    int OwnedLimit,
    ExchangeMode Mode,
    int CurrencyReserve,
    int RemainingTrades,
    int TargetQuantity)
{
    /// <summary>
    /// 1 回だけ交換する。手動の「交換する」など、セッションを持たない実行のため。
    ///
    /// **「1 回」は回数の欄で表す。**
    /// 個数の欄に 1 を入れると、1 回で 2 個以上もらえる品が
    /// 「あと 1 個なので撃てない」と判断されて永久に交換できない。
    /// </summary>
    /// <summary>
    /// 終わりを利用者が承知のうえで回す。
    ///
    /// 「所持の上限 0 ＝ 素材が尽きるまで」は、画面でもそう案内している
    /// 正規の遊び方。終了条件が無いことを理由に撃たないと、その設定が死ぬ。
    /// </summary>
    public bool AllowOpenEnded { get; init; }

    public static ExchangeLimitSet SingleTrade() => new(
        Unlimited: true,
        RemainingItems: 0,
        OwnedLimit: 0,
        Mode: ExchangeMode.FixedQuantity,
        CurrencyReserve: 0,
        RemainingTrades: 1,
        TargetQuantity: 0);

    /// <summary>
    /// 実行中のセッションから組み立てる。
    ///
    /// 交換リストを使う場合は品ごとの設定、使わない場合はセッション全体の設定を見る。
    /// </summary>
    public static ExchangeLimitSet ForRun(
        bool? targetUnlimited, int targetRemainingItems, int targetOwnedLimit,
        ExchangeMode mode, int currencyReserve, int remainingTrades, int targetQuantity,
        bool allowOpenEnded = false) => new(
        // 品ごとの設定が無い（交換リストを使わない）なら、個数では縛らない。
        // モードと回数が歯止めになる。
        Unlimited: targetUnlimited ?? true,
        RemainingItems: targetRemainingItems,
        OwnedLimit: targetOwnedLimit,
        Mode: mode,
        CurrencyReserve: currencyReserve,
        RemainingTrades: remainingTrades,
        TargetQuantity: targetQuantity)
    { AllowOpenEnded = allowOpenEnded };

    /// <summary>
    /// 出発してよいかを下見する。
    ///
    /// 出発の時点では所持枠も残り回数も当てにならない（周回の最中で、
    /// 着く頃には変わっている）。そこは撃つ直前の判断に任せ、
    /// ここでは「品の設定として撃てるか」だけを見る。
    /// </summary>
    public static ExchangeLimitSet ForScouting(
        bool unlimited, int remainingItems, int ownedLimit,
        ExchangeMode mode, int currencyReserve, int targetQuantity,
        bool allowOpenEnded = false) => new(
        Unlimited: unlimited,
        RemainingItems: remainingItems,
        OwnedLimit: ownedLimit,
        Mode: mode,
        CurrencyReserve: currencyReserve,

        // 回数の残りは出発時には分からない。撃つ直前に見る。
        RemainingTrades: int.MaxValue,
        TargetQuantity: targetQuantity)
    { AllowOpenEnded = allowOpenEnded };
}

/// <summary>
/// 「いま何回まで交換してよいか」という答え。
/// </summary>
/// <param name="Trades">許される交換回数。0 なら撃ってはいけない。</param>
/// <param name="Reason">0 のときの理由。画面と記録にそのまま出す。</param>
/// <param name="EndsSession">
/// この理由が、この品だけでなく**移動そのものの終わり**を意味するか。
///
/// 「この品はもう買わない」と「この移動はもう終わり」は別物。
/// 混ぜると、高い品で通貨を使い切ったときに、まだ買える安い品まで見送る。
/// </param>
public readonly record struct ExchangeAllowance(int Trades, string Reason, bool EndsSession)
{
    public bool Allowed => this.Trades > 0;

    public static ExchangeAllowance Ok(int trades) => new(Math.Max(0, trades), string.Empty, false);

    public static ExchangeAllowance Block(string reason, bool endsSession = false)
        => new(0, reason, endsSession);
}

/// <summary>
/// 利用者が決めた歯止めを、1 か所で判断する。
///
/// **ここが唯一の判断場所。**
/// 以前は窓口ごとに別々の実装があり、アイテム交換画面の経路にだけ歯止めがあって、
/// トームストーンの経路には無かった。その結果、所持の上限 10 を指定しても
/// 通貨が尽きるまで買い続けた。取り返しがつかない不具合だった。
///
/// 直したあとも、出発を決める側と撃つ側でコピーして揃えていたため、
/// 片方だけ厳しくした途端に「出かけては弾かれる」往復が起きた。
///
/// 判断は 1 つにする。呼ぶ場所は次の 3 つで、どれも同じ答えを得る。
///
/// <code>
/// 出発の前   MonitorService   … 撃てない品では出かけない
/// 撃つ直前   ExchangeExecutor … 最後の関門
/// 繰り返し   ExchangeExecutor … 続けてよいかの判断
/// </code>
///
/// **個数と回数を取り違えないこと。**
/// 利用者が入れる数（所持の上限・一括交換する個数・目標の所持数）はすべて個数。
/// ここが返すのは交換の回数。1 回の交換で受け取る個数は品によって違う。
/// 端数は切り捨てる。1 回撃つと超えてしまうなら撃たない。
/// </summary>
/// <summary>
/// 通貨のほかに払うもの 1 種類ぶんの、判断に要る値。
/// </summary>
/// <param name="Name">画面に出す名前。</param>
/// <param name="Quantity">1 回の交換で払う個数。</param>
/// <param name="Held">いまの所持数。</param>
public sealed record ExtraCostCheck(string Name, int Quantity, int Held);

public static class ExchangeLimits
{
    /// <summary>
    /// いま何回まで交換してよいかを決める。
    /// </summary>
    /// <param name="perTrade">1 回の交換で受け取る個数。</param>
    /// <param name="currencyCost">1 回の交換に使う通貨。</param>
    /// <param name="owned">いまの所持数（装備中とアーマリーも数えた値）。</param>
    /// <param name="currency">いまの通貨。</param>
    /// <param name="freeSlots">所持枠の空き。</param>
    /// <param name="keepFree">残しておく所持枠。</param>
    /// <param name="limits">利用者が決めた歯止め。</param>
    /// <param name="maxBatch">1 回の発火で撃てる上限。窓口が数量を選べないなら 1。</param>
    /// <param name="extraCosts">通貨のほかに払うもの（名前・1 回あたりの必要数・いまの所持数）。無ければ null。</param>
    public static ExchangeAllowance Evaluate(
        int perTrade,
        int currencyCost,
        int owned,
        int currency,
        int freeSlots,
        int keepFree,
        ExchangeLimitSet limits,
        int maxBatch,
        IReadOnlyList<ExtraCostCheck>? extraCosts = null)
    {
        perTrade = Math.Max(1, perTrade);
        maxBatch = Math.Max(1, maxBatch);

        // **終わりを決める条件が 1 つも無いなら撃たない。**
        //
        // 個数も所持の上限も置かず、どこまで交換するかも「交換できる限り」だと、
        // 通貨か所持枠が尽きるまで買い続けることになる。
        // 判断材料が欠けたら撃たない、という方針に倒す。
        // **承知のうえで上限なしにしている場合は通す。**
        //
        // 「所持の上限 0 ＝ 素材が尽きるまで回す」は画面で案内している遊び方で、
        // ScripGoalService も Endless として正規に扱う。
        // 終了条件が無いことだけを理由に弾くと、その設定が動かなくなる。
        if (!limits.AllowOpenEnded &&
            limits.Unlimited && limits.OwnedLimit <= 0 && limits.Mode == ExchangeMode.MaxExchange)
        {
            return ExchangeAllowance.Block(
                "終わりを決める設定がありません（個数・所持の上限・どこまで交換するか のいずれかを設定してください）",
                endsSession: true);
        }

        // --- 所持枠。移動そのものの終わり ---
        var bagRoom = freeSlots - keepFree;
        if (bagRoom <= 0)
        {
            return ExchangeAllowance.Block(
                $"所持枠の空きが {freeSlots} しかありません（{keepFree} 枠は残します）",
                endsSession: true);
        }

        // --- 通貨。この品の値段に対する判断なので、移動の終わりではない ---
        if (currencyCost > 0 && currency < currencyCost)
        {
            return ExchangeAllowance.Block($"通貨が足りません（所持 {currency} / 必要 {currencyCost}）");
        }

        // --- 通貨以外に払うもの。武器の交換で要る強化素材など ---
        //
        // 通貨と同じ扱いにする。足りなければこの品は交換できないが、
        // 別の品は交換できるかもしれないので、移動そのものは終わらせない。
        if (extraCosts is not null)
        {
            foreach (var extra in extraCosts)
            {
                if (extra.Quantity > 0 && extra.Held < extra.Quantity)
                {
                    return ExchangeAllowance.Block(
                        $"{extra.Name} が足りません（所持 {extra.Held} / 必要 {extra.Quantity}）");
                }
            }
        }

        // --- 所持の上限 ---
        var trades = maxBatch;

        // **「どこまで交換するか」で目標を決めているなら、そちらが親。**
        //
        // 行の「所持の上限」は既定が 1。プリセットで目標 5 と入れても、
        // 行を触っていなければ 1 で止まる。利用者から見ると、
        // 自分で入れた 5 が黙って無視される。
        //
        // どちらも「いくつ持つか」を決める設定なので、親を決めておく。
        // モードで目標を決めているときは、行の上限は見ない。
        var ownedLimitGoverns = limits.Mode != ExchangeMode.UntilTargetQuantity;

        if (ownedLimitGoverns && limits.OwnedLimit > 0)
        {
            var room = (limits.OwnedLimit - owned) / perTrade;
            if (room <= 0)
            {
                return ExchangeAllowance.Block(
                    $"所持の上限に達しています（所持 {owned} / 上限 {limits.OwnedLimit}）");
            }

            trades = Math.Min(trades, room);
        }

        // --- 一括交換する個数 ---
        if (!limits.Unlimited)
        {
            var wanted = limits.RemainingItems / perTrade;
            if (wanted <= 0)
            {
                return ExchangeAllowance.Block(
                    limits.RemainingItems <= 0
                        ? "指定した個数まで交換しました"
                        : $"あと {limits.RemainingItems} 個ですが、1 回で {perTrade} 個入るため交換しません");
            }

            trades = Math.Min(trades, wanted);
        }

        // --- どこまで交換するか ---
        switch (limits.Mode)
        {
            case ExchangeMode.FixedQuantity:
                if (limits.RemainingTrades <= 0)
                {
                    return ExchangeAllowance.Block("指定回数を交換しました", endsSession: true);
                }

                trades = Math.Min(trades, limits.RemainingTrades);
                break;

            case ExchangeMode.UntilCurrencyReserve:
                if (currencyCost > 0)
                {
                    var spendable = currency - limits.CurrencyReserve;
                    var affordable = spendable > 0 ? spendable / currencyCost : 0;

                    if (affordable <= 0)
                    {
                        return ExchangeAllowance.Block(
                            $"残す通貨量 {limits.CurrencyReserve} を割り込みます（所持 {currency}）");
                    }

                    trades = Math.Min(trades, affordable);
                }

                break;

            case ExchangeMode.UntilTargetQuantity:
                var toGoal = (limits.TargetQuantity - owned) / perTrade;
                if (toGoal <= 0)
                {
                    // **品単位の判断なので、移動そのものは終わらせない。**
                    // owned は「いま扱っている品」の所持数。所持の上限と同じ性質。
                    // 全体の旗を立てると、交換リストの残りが 1 件も試されなくなる。
                    return ExchangeAllowance.Block(
                        $"目標の {limits.TargetQuantity} 個に達しています（所持 {owned}）");
                }

                trades = Math.Min(trades, toGoal);
                break;
        }

        // --- 通貨で買える回数 ---
        if (currencyCost > 0)
        {
            trades = Math.Min(trades, currency / currencyCost);
        }

        // --- 通貨以外に払うもので何回ぶん賄えるか ---
        if (extraCosts is not null)
        {
            foreach (var extra in extraCosts)
            {
                if (extra.Quantity > 0)
                {
                    trades = Math.Min(trades, extra.Held / extra.Quantity);
                }
            }
        }

        // --- 所持枠。品が重なるかどうかは分からないので 1 回 1 枠として見る ---
        trades = Math.Min(trades, bagRoom);

        return trades > 0
            ? ExchangeAllowance.Ok(trades)
            : ExchangeAllowance.Block("交換できる条件を満たしていません");
    }
}
