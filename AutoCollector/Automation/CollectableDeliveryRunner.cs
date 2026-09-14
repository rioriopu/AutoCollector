using System;
using System.Collections.Generic;
using AutoCollector.Diagnostics;
using AutoCollector.Game;

namespace AutoCollector.Automation;

/// <summary>納品ループの手順。</summary>
public enum DeliveryStep
{
    Idle,
    Select,
    WaitTrade,
    Verify,
    Done,
    Error,
}

/// <summary>
/// 手持ちの収集品をまとめて納品する。
///
/// 1 回の納品で渡されるのは 1 個だけなので、1 個ずつ撃って毎回確かめる。
/// まとめて撃つと、失敗したときに何個成立したのか分からなくなる。
///
/// スクリップには所持上限があり、溢れたぶんは捨てられる。
/// 品目ごとの報酬量はやってみるまで分からないため、1 回目で観測し、
/// 2 回目以降はその値で溢れるかどうかを判断する。
/// </summary>
public sealed class CollectableDeliveryRunner(
    AnomalyLog anomalyLog,
    CollectablesShopService shop,
    CurrencyService currency,
    SpecialCurrencyMap currencyMap)
{
    /// <summary>納品ボタンが押せるようになるまでの確認回数の上限。</summary>
    private const int TradeReadyAttempts = 20;

    /// <summary>
    /// 撃ってから読みにいくまでの間。
    ///
    /// **ここだけは待つ。** 反映の途中を読むと、減る前の所持数を見て
    /// 「納品できなかった」と誤判定し、同じ品をもう一度撃つことになる。
    ///
    /// 実測ではスクリップは撃ってから 0.2 秒ほどで入る。
    /// 少し余裕を取り、それ以降は「変わったか」で進む。
    /// </summary>
    private static readonly TimeSpan VerifySettle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 反映を待つ上限。
    ///
    /// 以前はここまで一律で待っていた（1200 ミリ秒）。
    /// いまは変わったのを見た時点で進むので、これは「変わらなかった」と
    /// 判断するまでの猶予でしかない。長めに取ってよい。
    /// </summary>
    private static readonly TimeSpan VerifyLimit = TimeSpan.FromMilliseconds(2500);

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CollectablesShopService shop = shop;
    private readonly CurrencyService currency = currency;
    private readonly SpecialCurrencyMap currencyMap = currencyMap;

    /// <summary>品目ごとに観測した報酬。1 回目の納品で分かる。</summary>
    private readonly Dictionary<uint, (uint ScripItemId, int Amount)> observedReward = [];

    /// <summary>
    /// 撃っても納品されなかった品。この実行では以後試さない。
    ///
    /// 納品できない理由はいくつもあり（条件を満たさない、受け付けられない等）、
    /// こちらから理由を確定できないものもある。
    /// 1 品が納品できないだけで残り全部を諦めるのは行き過ぎなので、飛ばして続ける。
    /// </summary>
    private readonly HashSet<uint> blocked = [];

    /// <summary>取りこぼしの可能性があるので、1 度だけ撃ち直す。</summary>
    private readonly HashSet<uint> retried = [];

    private CollectableOffer? target;
    private int ownedBefore;
    private IReadOnlyList<(uint ItemId, string Name, int Count)> scripBefore = [];
    private int tradeAttempts;
    /// <summary>反映を待つ上限。ここを過ぎても変わらなければ原因を切り分ける。</summary>
    private DateTime waitUntilUtc;

    /// <summary>この時刻までは読みにいかない。反映の途中を読むと取り違える。</summary>
    private DateTime verifyFromUtc;

    public DeliveryStep Step { get; private set; } = DeliveryStep.Idle;

    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>この実行で納品した数。</summary>
    public int Delivered { get; private set; }

    public bool IsRunning => this.Step is not (DeliveryStep.Idle or DeliveryStep.Done or DeliveryStep.Error);

    /// <summary>
    /// 納品を始める。納品画面が開いていることが前提。
    /// </summary>
    public bool Start(out string reason)
    {
        if (this.IsRunning)
        {
            reason = "すでに実行中です";
            return false;
        }

        if (!this.shop.IsOpen())
        {
            reason = "納品画面が開いていません";
            return false;
        }

        this.Delivered = 0;
        this.observedReward.Clear();
        this.blocked.Clear();
        this.retried.Clear();
        this.Step = DeliveryStep.Select;
        this.StatusDetail = "納品する品を選んでいます";
        reason = string.Empty;
        return true;
    }

    public void Stop(string reason)
    {
        if (this.IsRunning)
        {
            this.anomalyLog.Info("Collect", $"納品を終了します（{this.Delivered} 個）: {reason}");
        }

        this.target = null;
        this.Step = DeliveryStep.Idle;
        this.StatusDetail = reason;
    }

    public void Tick()
    {
        if (!this.IsRunning)
        {
            return;
        }

        // 画面が閉じたら、そこで終わる。閉じたまま撃ち続けない。
        if (!this.shop.IsOpen())
        {
            this.Finish("納品画面が閉じました");
            return;
        }

        switch (this.Step)
        {
            case DeliveryStep.Select:
                this.TickSelect();
                break;

            case DeliveryStep.WaitTrade:
                this.TickWaitTrade();
                break;

            case DeliveryStep.Verify:
                this.TickVerify();
                break;
        }
    }

    /// <summary>次に納品する品を決めて、選ぶ。</summary>
    private unsafe void TickSelect()
    {
        if (!this.shop.TryGetAddon(out var addon))
        {
            this.Fail("納品画面を掴めませんでした");
            return;
        }

        if (!this.shop.TryReadOffers(addon, out var offers, out var readFailure))
        {
            this.Fail(readFailure);
            return;
        }

        var held = new Dictionary<uint, int>();
        foreach (var (itemId, _, count) in CollectablesShopReader.ListHeldCollectables())
        {
            held[itemId] = count;
        }

        // どのスクリップにも余裕が無ければ、そこで終わる。
        //
        // 報酬の種類は品目ごとに違い、まだ納品したことのない品では分からない。
        // 全部が一杯なら、どれを納品しても捨てることになる。
        if (this.AllScripsFull(out var fullDetail))
        {
            this.Finish($"スクリップがすべて上限です（{fullDetail}）");
            return;
        }

        var skipped = string.Empty;

        foreach (var offer in offers)
        {
            if (!held.TryGetValue(offer.ItemId, out var owned) || owned <= 0)
            {
                continue;
            }

            if (this.blocked.Contains(offer.ItemId))
            {
                continue;
            }

            // この品でスクリップが溢れるなら飛ばす。
            // 溢れたぶんは捨てられるだけで、収集品を失うことになる。
            //
            // ここで全体を止めない。スクリップは種類ごとに上限が別なので、
            // 片方が一杯でも、もう片方をもらえる品はまだ納品できる。
            if (this.WouldOverflow(offer.ItemId, out var overflowDetail))
            {
                if (string.IsNullOrEmpty(skipped))
                {
                    skipped = overflowDetail;
                }

                continue;
            }

            this.target = offer;
            this.ownedBefore = owned;
            this.scripBefore = this.SampleScrips();
            this.tradeAttempts = 0;

            if (!this.shop.TrySelect(offer, out var selectFailure))
            {
                this.Fail(selectFailure);
                return;
            }

            this.Step = DeliveryStep.WaitTrade;
            this.StatusDetail = $"{offer.ItemName} を納品しています";
            return;
        }

        var blockedNote = this.blocked.Count > 0 ? $"。納品できなかった品が {this.blocked.Count} 種類あります" : string.Empty;

        this.Finish(
            string.IsNullOrEmpty(skipped)
                ? $"納品できる収集品がなくなりました{blockedNote}"
                : $"残りはスクリップが溢れるため納品していません（{skipped}）{blockedNote}");
    }

    /// <summary>納品ボタンが押せるようになるのを待って押す。時間ではなく状態で判断する。</summary>
    private void TickWaitTrade()
    {
        this.tradeAttempts++;

        if (this.shop.IsTradeReady())
        {
            if (!this.shop.TryTrade(out var tradeFailure))
            {
                this.Fail(tradeFailure);
                return;
            }

            this.Step = DeliveryStep.Verify;

            // **固定で待たない。反映されたかどうかで進む。**
            //
            // 以前は一律 1200 ミリ秒待っていた。実測ではスクリップは
            // 撃ってから 0.2 秒ほどで入っており、残りの 1 秒は無駄に待っていた。
            // 納品 1 個あたり約 1.5 秒かかっていたのはこれが原因。
            //
            // 撃った直後は読みにいかない。反映の途中を読むと、
            // 減る前の所持数を見て「納品できなかった」と誤判定する。
            this.verifyFromUtc = DateTime.UtcNow.Add(VerifySettle);
            this.waitUntilUtc = DateTime.UtcNow.Add(VerifyLimit);
            return;
        }

        if (this.tradeAttempts >= TradeReadyAttempts)
        {
            this.Fail($"{this.target?.ItemName} を選びましたが、納品ボタンが押せる状態になりませんでした");
        }
    }

    /// <summary>撃ったことではなく、所持数の変化で成否を判断する。</summary>
    private void TickVerify()
    {
        // 反映の途中を読まないぶんだけは待つ。
        if (DateTime.UtcNow < this.verifyFromUtc)
        {
            return;
        }

        var offer = this.target;
        if (offer is null)
        {
            this.Fail("納品対象を見失いました");
            return;
        }

        var ownedAfter = 0;
        foreach (var (itemId, _, count) in CollectablesShopReader.ListHeldCollectables())
        {
            if (itemId == offer.ItemId)
            {
                ownedAfter = count;
                break;
            }
        }

        var after = this.SampleScrips();
        uint gainedScrip = 0;
        var gainedAmount = 0;
        var gainedName = string.Empty;

        foreach (var (itemId, name, count) in after)
        {
            foreach (var (beforeId, _, beforeCount) in this.scripBefore)
            {
                if (beforeId == itemId && count > beforeCount)
                {
                    gainedScrip = itemId;
                    gainedAmount = count - beforeCount;
                    gainedName = name;
                    break;
                }
            }

            if (gainedAmount > 0)
            {
                break;
            }
        }

        // まだ反映されていないだけかもしれない。上限まではもう少し待つ。
        //
        // 変わったのを見た時点で進むので、速い環境ほど速く回る。
        // 上限を過ぎても変わらなければ、以下のとおり原因を切り分ける。
        if ((ownedAfter >= this.ownedBefore || gainedAmount <= 0) && DateTime.UtcNow < this.waitUntilUtc)
        {
            return;
        }

        if (ownedAfter >= this.ownedBefore || gainedAmount <= 0)
        {
            // まず上限を疑う。
            //
            // まだ納品したことのない品は報酬が分からないため、事前に溢れを判定できない。
            // 上限際でそういう品を撃つと、ゲーム側が受け付けずに何も起きない。
            // これは異常ではなく、上限に達したという結果である。
            if (this.NearCap(out var nearDetail))
            {
                this.blocked.Add(offer.ItemId);

                this.anomalyLog.Info(
                    "Collect",
                    $"{offer.ItemName} はスクリップが上限に近いため納品できませんでした（{nearDetail}）。この品は飛ばして続けます");

                this.target = null;
                this.Step = DeliveryStep.Select;
                return;
            }

            // 取りこぼしの可能性があるので、1 度だけ撃ち直す。
            if (this.retried.Add(offer.ItemId))
            {
                this.anomalyLog.Info("Collect", $"{offer.ItemName} が納品されなかったため、もう一度試します");
                this.target = null;
                this.Step = DeliveryStep.Select;
                return;
            }

            // 2 度試しても変わらないなら、この品は納品できない。
            // 理由をこちらから確定できないため断定はせず、飛ばして他を続ける。
            this.blocked.Add(offer.ItemId);

            this.anomalyLog.Warn(
                "Collect",
                $"{offer.ItemName} は納品できませんでした（所持 {this.ownedBefore} のまま / スクリップの増加なし。" +
                $"収集価値 {DescribeCollectability(offer.ItemId)}）。この品は飛ばして続けます");

            this.target = null;
            this.Step = DeliveryStep.Select;
            return;
        }

        this.Delivered++;
        this.observedReward[offer.ItemId] = (gainedScrip, gainedAmount);

        this.anomalyLog.Info(
            "Collect",
            $"納品しました: {offer.ItemName}（{this.ownedBefore} → {ownedAfter}）/ {gainedName} +{gainedAmount}");

        this.target = null;

        // 上限に届いたら、その種類の報酬になる品はもう納品しない。
        // 観測済みの報酬として控えてあるので、次の選択で飛ばされる。
        if (this.IsAtCap(gainedScrip, out var capDetail))
        {
            this.anomalyLog.Info("Collect", $"スクリップが上限に達しました（{capDetail}）");
        }

        this.Step = DeliveryStep.Select;
    }

    /// <summary>
    /// すべてのスクリップが上限に達しているか。
    ///
    /// 報酬の種類が分からない品を納品してよいかの判断に使う。
    /// 1 つでも余裕があれば、そこへ入る可能性があるので納品を試す。
    /// </summary>
    private bool AllScripsFull(out string detail)
    {
        detail = string.Empty;

        var names = new List<string>();
        var known = 0;

        foreach (var (itemId, name) in this.currencyMap.ListCurrencies())
        {
            var cap = this.currency.GetEffectiveCap(itemId);
            if (cap is not { } limit || limit == 0)
            {
                continue;
            }

            if (!this.currency.TryGetCount(itemId, out var current))
            {
                continue;
            }

            known++;

            if (current < limit)
            {
                return false;
            }

            names.Add($"{name} {current:N0} / {limit:N0}");
        }

        if (known == 0)
        {
            // 上限を 1 つも読めない場合は判断しない。止める根拠がない。
            return false;
        }

        detail = string.Join(" / ", names);
        return true;
    }

    /// <summary>
    /// どれかのスクリップが上限際か。
    ///
    /// 「あと少しで一杯」の判断には、この実行で観測した報酬のうち最大のものを使う。
    /// まだ 1 つも観測していない場合は控えめな値で見る。
    /// </summary>
    private bool NearCap(out string detail)
    {
        detail = string.Empty;

        var margin = 0;
        foreach (var reward in this.observedReward.Values)
        {
            if (reward.Amount > margin)
            {
                margin = reward.Amount;
            }
        }

        if (margin == 0)
        {
            margin = 200;
        }

        foreach (var (itemId, name) in this.currencyMap.ListCurrencies())
        {
            var cap = this.currency.GetEffectiveCap(itemId);
            if (cap is not { } limit || limit == 0)
            {
                continue;
            }

            if (!this.currency.TryGetCount(itemId, out var current))
            {
                continue;
            }

            if (limit - current <= margin)
            {
                detail = $"{name} {current:N0} / {limit:N0}";
                return true;
            }
        }

        return false;
    }

    /// <summary>手持ちの収集価値を並べる。納品できない理由を追うための材料。</summary>
    private static string DescribeCollectability(uint itemId)
    {
        var values = new List<int>();

        foreach (var value in CollectablesShopReader.ListCollectability(itemId))
        {
            values.Add(value);
        }

        return values.Count == 0 ? "不明" : string.Join(" / ", values);
    }

    /// <summary>その通貨が上限に達しているか。</summary>
    private bool IsAtCap(uint scripItemId, out string detail)
    {
        detail = string.Empty;

        if (scripItemId == 0)
        {
            return false;
        }

        var cap = this.currency.GetEffectiveCap(scripItemId);
        if (cap is not { } limit || limit == 0)
        {
            return false;
        }

        if (!this.currency.TryGetCount(scripItemId, out var current) || current < limit)
        {
            return false;
        }

        detail = $"{Ui.StatusText.ItemName(scripItemId)} {current:N0} / {limit:N0}";
        return true;
    }

    /// <summary>
    /// この品を納品するとスクリップが上限を超えるか。
    ///
    /// 報酬量は品目ごとに違い、やってみるまで分からない。
    /// 一度でも観測できていればその値で判断し、まだなら判断しない。
    /// </summary>
    private bool WouldOverflow(uint itemId, out string detail)
    {
        detail = string.Empty;

        if (!this.observedReward.TryGetValue(itemId, out var reward))
        {
            return false;
        }

        var cap = this.currency.GetEffectiveCap(reward.ScripItemId);
        if (cap is not { } limit || limit == 0)
        {
            return false;
        }

        if (!this.currency.TryGetCount(reward.ScripItemId, out var current))
        {
            return false;
        }

        if (current + reward.Amount <= limit)
        {
            return false;
        }

        var name = Ui.StatusText.ItemName(reward.ScripItemId);
        detail = $"{name} {current:N0} / {limit:N0}、次の納品で +{reward.Amount}";
        return true;
    }

    private IReadOnlyList<(uint ItemId, string Name, int Count)> SampleScrips()
    {
        var list = new List<(uint, string, int)>();

        foreach (var (itemId, name) in this.currencyMap.ListCurrencies())
        {
            list.Add((itemId, name, this.currency.GetCountOrZero(itemId)));
        }

        return list;
    }

    private void Finish(string reason)
    {
        this.anomalyLog.Info("Collect", $"納品を終えました（{this.Delivered} 個）: {reason}");
        this.target = null;
        this.Step = DeliveryStep.Done;
        this.StatusDetail = reason;
    }

    private void Fail(string reason)
    {
        this.anomalyLog.Error("Collect", $"納品を中止しました（{this.Delivered} 個まで完了）: {reason}");
        this.target = null;
        this.Step = DeliveryStep.Error;
        this.StatusDetail = reason;
    }
}
