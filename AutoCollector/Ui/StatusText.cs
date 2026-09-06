using AutoCollector.Automation;
using AutoCollector.Game;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Ui;

/// <summary>
/// 画面に出す文言を組み立てる。
///
/// 内部の列挙子の名前をそのまま出さないためにここへ集める。
/// 利用者にとって enum の名前は意味を持たず、原因も次の一手も伝えない。
/// </summary>
public static class StatusText
{
    /// <summary>
    /// いまの手順を日本語で言い表す。
    ///
    /// StatusDetail は既に日本語で、エリア名や NPC 名まで含んでいる。
    /// こちらはそれを置き換えず、接頭辞として前に付ける。
    /// </summary>
    public static string StepLabel(ExchangeStep step) => step switch
    {
        ExchangeStep.WaitingSafeWindow => "始められる状態を待っています",
        ExchangeStep.SuppressExternal => "他のプラグインの新規処理を止めています",
        ExchangeStep.StopAutoDuty => "AutoDuty をいったん止めています",
        ExchangeStep.Teleport => "交換所のあるエリアへ移動しています",
        ExchangeStep.AethernetHop => "街の中を転送で移動しています",
        ExchangeStep.Navigate => "交換所まで歩いています",
        ExchangeStep.Interact => "交換所の担当者に話しかけています",
        ExchangeStep.SelectMenu => "会話を進めています",
        ExchangeStep.Armed => "交換の直前確認をしています",
        ExchangeStep.WaitOutcome => "交換の結果を確認しています",
        ExchangeStep.ConfirmDialog => "確認ダイアログに答えています",
        ExchangeStep.ResumeAutoDuty => "AutoDuty を再開しています",
        ExchangeStep.Done => "終わりました",
        ExchangeStep.Error => "止まりました",
        _ => "待機中",
    };

    /// <summary>
    /// 失敗に対する「次に何をすればよいか」。
    ///
    /// 原因は StatusDetail が述べているので、ここでは繰り返さない。
    /// 二重に書くと、どちらを読めばよいのか分からなくなる。
    /// </summary>
    public static string NextAction(ExchangeFailure failure) => failure switch
    {
        // 移動・到達
        ExchangeFailure.NavigationUnavailable =>
            "vnavmesh を導入すると、交換所まで自動で移動できるようになります。",
        ExchangeFailure.NavigationFailed =>
            "建物の中や乗り物の上だと経路を作れないことがあります。屋外に出てから再開してください。",
        ExchangeFailure.NpcNotFound =>
            "交換所の担当者が見つかりませんでした。近くまで移動してから再開してください。",
        ExchangeFailure.InteractFailed =>
            "話しかけられませんでした。少し離れた位置に立ってから再開してください。",
        ExchangeFailure.TeleportUnavailable =>
            "Lifestream を導入すると、別のエリアにある交換所も使えます。",
        ExchangeFailure.TeleportFailed =>
            "テレポートできませんでした。詠唱を妨げるもののない場所で再開してください。",
        ExchangeFailure.AetheryteNotAttuned =>
            "一度そのエリアのエーテライトに触れてから、もう一度お試しください。",

        // 会話
        ExchangeFailure.MenuResolutionFailed or ExchangeFailure.MenuAmbiguous =>
            "会話メニューから交換所を選べませんでした。プリセットタブで交換所（担当者）を選び直してください。",

        // 資源
        ExchangeFailure.InsufficientCurrency =>
            "プリセットの交換を始める条件を、1 回分のコストより大きい値にしてください。",
        ExchangeFailure.NoBagSpace =>
            "不要なアイテムを整理するか、設定タブの「交換後に残す所持枠」を小さくしてください。",

        // 未対応
        ExchangeFailure.InclusionShopUnsupported =>
            "この交換所（アイテム交換画面）はまだ自動実行に対応していません。手動で交換するか、" +
            "プリセットタブで別の担当者を選べる場合はそちらに変えてください。",

        // 撃つ前に止めた（データの食い違い）
        ExchangeFailure.ShopNotOpen or ExchangeFailure.BlockingAddonPresent
            or ExchangeFailure.MultiCostOrMultiRewardEntry or ExchangeFailure.ResolverNotReady
            or ExchangeFailure.ShopMismatch or ExchangeFailure.ExchangeItemNotFound
            or ExchangeFailure.ExchangeAmbiguous or ExchangeFailure.CostMismatch
            or ExchangeFailure.EntryCountMismatch or ExchangeFailure.IndexOutOfRange
            or ExchangeFailure.IndexDuplicated or ExchangeFailure.CurrencyMismatch =>
            "誤った交換を防ぐため、撃つ前に止めました。パッチ直後の可能性があります。" +
            "診断タブでセルフチェックを実行してください。",

        // 撃った後（結果が確定していない）
        ExchangeFailure.RewardCountUnreadable or ExchangeFailure.ExchangeNotApplied
            or ExchangeFailure.ExchangeUnexpectedDelta or ExchangeFailure.ExchangeUnresolved
            or ExchangeFailure.ConfirmDialogTimeout or ExchangeFailure.ConfirmDialogUnexpected =>
            "ゲーム内で所持数を確かめてください。増えていれば交換は成功しています。",

        // 外部連携
        ExchangeFailure.AutoDutyStopFailed =>
            "AutoDuty を手動で一度止めてから、状態をリセットしてください。",
        ExchangeFailure.AutoDutyResumeFailed =>
            "交換は終わっています。下のボタンから周回を再開できます。AutoDuty 側で開始しても構いません。",
        ExchangeFailure.AutoRetainerIpcBroken =>
            "AutoRetainer の抑制を解除できませんでした。AutoRetainer 側に抑制が残っていないか確認してください。",
        ExchangeFailure.ExternalPluginError =>
            "連携しているプラグインへ指示を送れませんでした。相手が読み込まれているか確認してください。",

        ExchangeFailure.PreviousExchangeUnresolved =>
            "前回の記録を消すまで、新しい交換は行いません。",
        ExchangeFailure.Aborted =>
            "停止しました。原因を確かめてから、状態をリセットしてください。",
        ExchangeFailure.None => string.Empty,

        // 値を足したときはここも直す。実行時に落とさないため既定を残す。
        _ => "診断タブの記録に詳細が残っています。",
    };

    /// <summary>
    /// 結果が未確認の交換を 1 行で言い表す。
    /// 発火時と現在の値を並べて利用者に引き算させない。
    /// </summary>
    public static string DescribeInFlight(PurchaseAttempt attempt, CurrencyService currency)
    {
        var currencyName = ItemName(attempt.CurrencyItemId);

        if (!currency.TryGetCount(attempt.CurrencyItemId, out var now) ||
            !currency.TryGetCount(attempt.RewardItemId, out var reward, includeEquipped: true, includeArmory: true))
        {
            return $"{attempt.RewardName} × {attempt.RewardQuantity} を交換しました。" +
                   "いまの所持数を読み取れないため、ゲーム内で確かめてください。";
        }

        var currencyDelta = now - attempt.CurrencyBefore;
        var rewardDelta = reward - attempt.RewardBefore;

        if (currencyDelta == -(int)attempt.CurrencyCost && rewardDelta == (int)attempt.RewardQuantity)
        {
            return $"交換は成立しているように見えます（{currencyName} {attempt.CurrencyBefore:N0} → {now:N0}、" +
                   $"{attempt.RewardName} {attempt.RewardBefore:N0} → {reward:N0}）。";
        }

        if (currencyDelta == 0 && rewardDelta == 0)
        {
            return $"交換されていないように見えます（{currencyName}・{attempt.RewardName} とも変わっていません）。";
        }

        return $"増減が想定と違います（{currencyName} {currencyDelta:+#;-#;0} / " +
               $"{attempt.RewardName} {rewardDelta:+#;-#;0}）。ゲーム内で確かめてください。";
    }

    /// <summary>ItemId から名前を引く。引けない場合は番号を返す。</summary>
    public static string ItemName(uint itemId)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Item>();
            if (sheet is not null && sheet.TryGetRow(itemId, out var row))
            {
                var name = row.Name.ExtractText();
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }
        catch
        {
            // 名前が引けないだけで表示を止めない。
        }

        return $"ItemId {itemId}";
    }
}
