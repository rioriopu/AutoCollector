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
    SpecialCurrencyMap currencyMap,
    CollectableRewardService rewards,
    CollectablesShopReader reader)
{
    /// <summary>
    /// 納品できる状態になるのを待つ上限。
    ///
    /// **回数ではなく時間で持つ。** 呼ばれる間隔は場面によって変わるため、
    /// 回数で持つと、速く呼ばれるほど待ち時間が短くなってしまう。
    /// </summary>
    private static readonly TimeSpan TradeReadyLimit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// ボタンを待つのをやめて、選択を確かめて納品へ進むまでの猶予。
    ///
    /// うまくいっている環境では最初の確認で押せるようになるため、ここまで来ない。
    /// </summary>
    private static readonly TimeSpan TradeReadyFallbackAfter = TimeSpan.FromSeconds(1);

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
    private readonly CollectableRewardService rewards = rewards;
    private readonly CollectablesShopReader reader = reader;

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

    /// <summary>
    /// 撃つ前の手持ちの収集品。狙っていない品が減っていないかを見るために控える。
    ///
    /// 納品はいま選ばれている品に対して行われる。選択の確認を通しているとはいえ、
    /// 別の品が渡されたことに気づかないまま続けると、被害が積み上がる。
    /// </summary>
    private IReadOnlyDictionary<uint, int> heldBefore = new Dictionary<uint, int>();
    private IReadOnlyList<(uint ItemId, string Name, int Count)> scripBefore = [];

    /// <summary>何回確かめたか。待ち方が足りているかを後から読むために残す。</summary>
    private int tradeAttempts;

    /// <summary>選んだ時刻。納品できる状態になるのを待つ起点。</summary>
    private DateTime selectedAtUtc;

    /// <summary>最後に読んだ納品ボタンの状態。納品できなかったときの理由に入れる。</summary>
    private string lastButtonDetail = string.Empty;

    /// <summary>最後に読んだ選択の状態。納品できなかったときの理由に入れる。</summary>
    private string lastSelectionDetail = string.Empty;

    /// <summary>
    /// 選んだあとの画面を書き出したか。
    ///
    /// 納品できないとき、画面が選択後にどうなっているのかが分からないと原因を追えない。
    /// 自動で取っているダンプは画面が開いた瞬間のもので、選ぶ前の姿しか残らない。
    /// 1 回の納品につき 1 度だけ、選んだあとの姿を残す。
    /// </summary>
    private bool dumpedAfterSelect;

    /// <summary>反映を待つ上限。ここを過ぎても変わらなければ原因を切り分ける。</summary>
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
        this.blocked.Clear();
        this.retried.Clear();
        this.dumpedAfterSelect = false;
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
        var belowTier = string.Empty;

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

            // 収集価値が下限に届かない品は、窓口にあっても渡せない。
            // 選んでから納品できる状態にならず、待って諦めるだけになる。
            if (!this.MeetsCollectability(offer, out var tierDetail))
            {
                if (string.IsNullOrEmpty(belowTier))
                {
                    belowTier = tierDetail;
                }

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
            this.heldBefore = held;
            this.scripBefore = this.SampleScrips();
            this.tradeAttempts = 0;
            this.selectedAtUtc = DateTime.UtcNow;
            this.lastButtonDetail = "未読";
            this.lastSelectionDetail = "未確認";

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

        var reason = !string.IsNullOrEmpty(skipped)
            ? $"残りはスクリップが溢れるため納品していません（{skipped}）"
            : !string.IsNullOrEmpty(belowTier)
                ? $"残りは収集価値が足りないため納品できません（{belowTier}）"
                : "納品できる収集品がなくなりました";

        this.Finish($"{reason}{blockedNote}");
    }

    /// <summary>
    /// 手持ちのどれかが納品の下限に届いているか。
    ///
    /// 下限はシート（CollectablesShopRefine）から引く。引けない場合は止めない。
    /// 判断材料が無いだけで、納品してみれば分かる。
    /// </summary>
    private bool MeetsCollectability(CollectableOffer offer, out string detail)
    {
        detail = string.Empty;

        if (!this.rewards.TryResolve(offer.ItemId, out var reward) || reward.LowCollectability == 0)
        {
            return true;
        }

        var best = 0;
        foreach (var value in CollectablesShopReader.ListCollectability(offer.ItemId))
        {
            if (value > best)
            {
                best = value;
            }
        }

        if (reward.Accepts(best))
        {
            return true;
        }

        detail = $"{offer.ItemName} は収集価値 {best} で、納品の下限 {reward.LowCollectability} に届いていません";
        return false;
    }

    /// <summary>
    /// 選択が効いたことを確かめて納品する。時間ではなく状態で判断する。
    ///
    /// 本来は納品ボタン（node 51）が現れたことを合図にしていた。
    /// ところが実機で、選択は効いているのにボタンが現れない状態が出た。
    /// ボタンが出ないまま 2 秒待って諦め、窓口へ行き直すだけを繰り返していた。
    ///
    /// ボタンの可視状態は「選択が効いた」ことの代わりに見ていたにすぎない。
    /// 選択そのものは一覧（node 28）から直接読めるので、そちらで確かめる。
    /// 確かめられたら、実測で確定している納品の発火をそのまま渡す。
    /// </summary>
    private void TickWaitTrade()
    {
        this.tradeAttempts++;

        var offer = this.target;
        if (offer is null)
        {
            this.Fail("納品対象を見失いました");
            return;
        }

        var button = this.shop.ReadTradeButton();
        this.lastButtonDetail = button.Detail;

        if (button.Ready)
        {
            this.FireDelivery(offer, $"納品ボタンが押せる状態になりました（{this.tradeAttempts} 回目）");
            return;
        }

        var waited = DateTime.UtcNow - this.selectedAtUtc;

        // ボタンが現れない。選択が狙いどおりなら、ボタンを待たずに納品する。
        if (waited >= TradeReadyFallbackAfter)
        {
            this.DumpAfterSelect();

            var confirmed = this.shop.TryConfirmSelection(offer, out var selectionDetail);
            this.lastSelectionDetail = selectionDetail;

            if (confirmed)
            {
                this.FireDelivery(
                    offer,
                    $"納品ボタンが現れません（{button.Detail}）が、選択は合っています（{selectionDetail}）");
                return;
            }
        }

        if (waited >= TradeReadyLimit)
        {
            this.Fail(
                $"{offer.ItemName} を選びましたが納品できる状態になりませんでした" +
                $"（ボタン: {this.lastButtonDetail} / 選択: {this.lastSelectionDetail}）");
        }
    }

    /// <summary>
    /// 選んだあとの画面を書き出す。
    ///
    /// 納品ボタンが現れないとき、選択が効いているのかどうかを
    /// あとから確かめられるようにする。書き出しに失敗しても納品は止めない。
    /// </summary>
    private void DumpAfterSelect()
    {
        if (this.dumpedAfterSelect)
        {
            return;
        }

        this.dumpedAfterSelect = true;

        try
        {
            var path = this.reader.Save(Plugin.ResolveLogDirectory(), "選んだあと");
            this.anomalyLog.Info("Collect", $"納品ボタンが現れないため、選んだあとの画面を書き出しました: {path}");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Collect", $"選んだあとの画面を書き出せませんでした: {ex.Message}");
        }
    }

    /// <summary>納品を撃って、検証待ちへ移る。</summary>
    private void FireDelivery(CollectableOffer offer, string note)
    {
        this.anomalyLog.Info("Collect", $"{offer.ItemName} を納品します。{note}");

        if (!this.shop.TryDeliver(out var failure))
        {
            this.Fail(failure);
            return;
        }

        this.Step = DeliveryStep.Verify;

        // **固定で待たない。反映されたかどうかで進む。**
        //
        // 以前は一律 1200 ミリ秒待っていた。実測ではスクリップは
        // 撃ってから 0.2 秒ほどで入っており、残りの 1 秒は無駄に待っていた。
        //
        // 読みにいく前の間合い（250 ミリ秒）も外した。
        // 変化を見るまで待ち続ける形になった時点で、早く読んでも
        // 「まだ変わっていない」と見て次の呼び出しへ回るだけになっている。
        // 誤判定が起きるのは下の上限まで変わらなかったときだけで、
        // そこは 2500 ミリ秒のまま変えていない。
        this.waitUntilUtc = DateTime.UtcNow.Add(VerifyLimit);
    }

    /// <summary>撃ったことではなく、所持数の変化で成否を判断する。</summary>
    private void TickVerify()
    {
        var offer = this.target;
        if (offer is null)
        {
            this.Fail("納品対象を見失いました");
            return;
        }

        var ownedAfter = 0;
        var heldAfter = new Dictionary<uint, int>();
        foreach (var (itemId, _, count) in CollectablesShopReader.ListHeldCollectables())
        {
            heldAfter[itemId] = count;

            if (itemId == offer.ItemId)
            {
                ownedAfter = count;
            }
        }

        // 狙っていない収集品が減っていたら、そこで止める。
        //
        // 納品はいま選ばれている品に対して行われる。選択がずれていた場合、
        // 気づかずに続けると手持ちを別の品から削っていくことになる。
        // 何が起きたかは分かっているので、やり直さずに止める。
        if (this.TryFindUnexpectedLoss(heldAfter, offer.ItemId, out var lossDetail))
        {
            this.Fail($"狙っていない収集品が減りました（{lossDetail}）。取り違えの恐れがあるため納品を止めます");
            return;
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
                $"収集価値 {DescribeCollectability(offer.ItemId)} / ボタン: {this.lastButtonDetail} / " +
                $"選択: {this.lastSelectionDetail}）。この品は飛ばして続けます");

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
    /// 狙った品以外に減ったものがあるか。
    ///
    /// 減っていれば、納品が別の品に対して行われたことになる。
    /// </summary>
    private bool TryFindUnexpectedLoss(
        IReadOnlyDictionary<uint, int> heldAfter,
        uint expectedItemId,
        out string detail)
    {
        detail = string.Empty;

        // 1 件も読めなかった場合は、読めなかっただけかもしれない。
        // 読めない値を根拠に「全部減った」と判断して止めない。
        if (heldAfter.Count == 0)
        {
            return false;
        }

        foreach (var (itemId, before) in this.heldBefore)
        {
            if (itemId == expectedItemId)
            {
                continue;
            }

            var now = heldAfter.GetValueOrDefault(itemId);
            if (now >= before)
            {
                continue;
            }

            detail = $"{Ui.StatusText.ItemName(itemId)} {before} → {now}";
            return true;
        }

        return false;
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
