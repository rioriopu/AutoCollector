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

    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly CollectablesShopService shop = shop;
    private readonly CurrencyService currency = currency;
    private readonly SpecialCurrencyMap currencyMap = currencyMap;

    /// <summary>品目ごとに観測した報酬。1 回目の納品で分かる。</summary>
    private readonly Dictionary<uint, (uint ScripItemId, int Amount)> observedReward = [];

    private CollectableOffer? target;
    private int ownedBefore;
    private IReadOnlyList<(uint ItemId, string Name, int Count)> scripBefore = [];
    private int tradeAttempts;
    private DateTime waitUntilUtc;

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

        foreach (var offer in offers)
        {
            if (!held.TryGetValue(offer.ItemId, out var owned) || owned <= 0)
            {
                continue;
            }

            // この品でスクリップが溢れるなら納品しない。
            // 溢れたぶんは捨てられるだけで、収集品を失うだけになる。
            if (this.WouldOverflow(offer.ItemId, out var overflowDetail))
            {
                this.Finish($"これ以上納品するとスクリップが溢れます（{overflowDetail}）");
                return;
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

        this.Finish("納品できる収集品がなくなりました");
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

            // 所持数への反映は同じフレームでは終わらない。
            this.waitUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
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
        if (DateTime.UtcNow < this.waitUntilUtc)
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

        if (ownedAfter >= this.ownedBefore || gainedAmount <= 0)
        {
            this.Fail(
                $"納品の結果を確認できませんでした（{offer.ItemName} {this.ownedBefore} → {ownedAfter} / " +
                $"スクリップの増加 {(gainedAmount > 0 ? gainedAmount.ToString() : "なし")}）");
            return;
        }

        this.Delivered++;
        this.observedReward[offer.ItemId] = (gainedScrip, gainedAmount);

        this.anomalyLog.Info(
            "Collect",
            $"納品しました: {offer.ItemName}（{this.ownedBefore} → {ownedAfter}）/ {gainedName} +{gainedAmount}");

        this.target = null;

        // 上限に届いたら、そこで終わる。
        // 報酬が観測できていない品でも、実際に届いた時点で止められる。
        if (this.IsAtCap(gainedScrip, out var capDetail))
        {
            this.Finish($"スクリップが上限に達しました（{capDetail}）");
            return;
        }

        this.Step = DeliveryStep.Select;
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
