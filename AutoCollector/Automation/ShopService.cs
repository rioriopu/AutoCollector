using System;
using System.Collections.Generic;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoCollector.Automation;

/// <summary>開いているショップから読み取った 1 エントリ。</summary>
public sealed record ShopEntry(int Slot, uint ItemId, string ItemName, uint CostAmount, uint Index);

/// <summary>シートの交換定義と、実際に開いた画面の内容との照合結果。</summary>
public enum ShopMatchKind
{
    /// <summary>ItemId が一致し、コストもシート値と一致した。交換してよい。</summary>
    Matched,

    /// <summary>目的のアイテムが画面に無い。別タブか、そもそも扱っていない。</summary>
    ItemNotFound,

    /// <summary>同じ ItemId が複数あり、どれを選ぶべきか決められない。</summary>
    Ambiguous,

    /// <summary>ItemId は見つかったが、コストがシート値と違う。</summary>
    CostMismatch,

    /// <summary>開いているショップを特定できないか、定義のショップと違う。</summary>
    ShopMismatch,
}

public sealed record ShopMatchResult(ShopMatchKind Kind, ShopEntry? Entry, string Detail);

/// <summary>
/// ShopExchangeCurrency の読み取りと照合。
///
/// この段階では交換を実行しない。読み取りと照合だけを行う。
/// 実際の購入は、ここで得た Index を使って別途行う。
/// </summary>
public sealed class ShopService(AnomalyLog anomalyLog, ShopAddonLayout layout)
{
    private const string AddonName = "ShopExchangeCurrency";

    private readonly AnomalyLog anomalyLog = anomalyLog;

    public ShopAddonLayout Layout { get; } = layout;

    /// <summary>ショップが開いていて操作可能か。</summary>
    public unsafe bool IsShopOpen()
    {
        return GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon)
               && GenericHelpers.IsAddonReady(addon);
    }

    /// <summary>配置が正しいかを目視確認するための診断情報を返す。</summary>
    public unsafe List<(string Label, AtkValueProbe Probe)> DiagnoseLayout()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) || !GenericHelpers.IsAddonReady(addon))
        {
            return [];
        }

        try
        {
            return new ShopExchangeCurrencyReader(addon, this.Layout).DiagnoseLayout();
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Shop", $"配置診断に失敗しました: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 開いているショップのエントリを読む。
    /// 読めなかった場合は理由を返す。理由が分からないまま先へ進めない。
    /// </summary>
    public unsafe bool TryReadEntries(out IReadOnlyList<ShopEntry> entries, out ShopHeader header, out string failureReason)
    {
        entries = [];
        header = new ShopHeader(0, 0, 0, string.Empty, 0);

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) || !GenericHelpers.IsAddonReady(addon))
        {
            failureReason = "交換ショップが開いていません";
            return false;
        }

        return this.TryReadEntriesFrom(addon, out entries, out header, out failureReason);
    }

    /// <summary>
    /// 指定したアドオンから読む。
    ///
    /// 交換の実行時は、読み取りと発火で同じアドオンを使わなければならない。
    /// 名前から二度解決すると、両者が同一インスタンスである保証がなくなる。
    /// </summary>
    public unsafe bool TryReadEntriesFrom(AtkUnitBase* addon, out IReadOnlyList<ShopEntry> entries, out ShopHeader header, out string failureReason)
    {
        entries = [];
        header = new ShopHeader(0, 0, 0, string.Empty, 0);

        if (addon is null)
        {
            failureReason = "交換ショップが開いていません";
            return false;
        }

        try
        {
            var reader = new ShopExchangeCurrencyReader(addon, this.Layout);

            var numEntries = reader.NumEntries;
            if (!numEntries.Usable)
            {
                failureReason = $"エントリ数を読めません（位置 {this.Layout.NumEntries} の型は {numEntries.TypeName}）。atkvalue_layout.json の配置が現在のクライアントと合っていない可能性があります";
                this.anomalyLog.Error("Shop", failureReason);
                return false;
            }

            var currencyProbe = reader.CurrencyAmount;
            if (!currencyProbe.Usable)
            {
                failureReason = $"所持通貨量を読めません（位置 {this.Layout.CurrencyAmount} の型は {currencyProbe.TypeName}）。交換前後の検証ができないため中止します";
                this.anomalyLog.Error("Shop", failureReason);
                return false;
            }

            // アイコン ID は補助情報。読めなくても交換の可否には影響しないので止めない。
            var iconProbe = reader.CurrencyIcon;

            // 読み取る件数は宣言値と上限の小さい方に抑える。
            // 宣言値が壊れていても暴走させない。
            var declared = numEntries.Value;
            var count = (int)Math.Min(declared, (uint)this.Layout.MaxEntries);
            if (declared > (uint)this.Layout.MaxEntries)
            {
                this.anomalyLog.Warn("Shop", $"エントリ数の申告値が {declared} と異常です。{this.Layout.MaxEntries} 件までに制限して読み取ります");
            }

            var itemSheet = Svc.Data.GetExcelSheet<Item>();
            var result = new List<ShopEntry>(count);
            var unreadable = 0;

            for (var i = 0; i < count; i++)
                {
                var entry = reader.GetEntry(i);

                var itemProbe = entry.ItemId;
                if (!itemProbe.Usable)
                {
                    unreadable++;
                    continue;
                }

                if (itemProbe.Value == 0)
                {
                    // 空きスロット。ここで打ち切らず、後ろにも実体があるものとして見る。
                    continue;
                }

                var costProbe = entry.CostAmount;
                var indexProbe = entry.Index;
                if (!costProbe.Usable || !indexProbe.Usable)
                {
                    // 値が揃わないエントリは、交換の対象として採用してはいけない。
                    unreadable++;
                    continue;
                }

                var name = itemSheet?.GetRowOrDefault(itemProbe.Value)?.Name.ExtractText() ?? $"<{itemProbe.Value}>";
                result.Add(new ShopEntry(i, itemProbe.Value, name, costProbe.Value, indexProbe.Value));
            }

            if (unreadable > 0)
            {
                this.anomalyLog.Warn("Shop", $"{unreadable} 件のエントリを読み取れませんでした。配置がずれている可能性があります");
            }

            entries = result;
            header = new ShopHeader(declared, currencyProbe.Value, iconProbe.Usable ? iconProbe.Value : 0, iconProbe.TypeName, unreadable);
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"ショップの読み取りに失敗しました: {ex.Message}";
            this.anomalyLog.Error("Shop", failureReason);
            return false;
        }
    }

    /// <summary>
    /// 開いているショップがどの SpecialShop 行かを、読み取った内容から特定する。
    ///
    /// 同じアイテムが複数のショップに別の値段で載っているため、ItemId だけで
    /// 定義を引くと別のショップの値段と比較してしまう。実際、数理の「その他」交換では
    /// 現行ショップが 10 なのに対し、過去パッチのショップが同じアイテムを 20 で持っている。
    ///
    /// そこで (ItemId, コスト) の組がすべて含まれるショップを探す。
    /// </summary>
    public ShopIdentification IdentifyShop(IReadOnlyList<ShopEntry> entries, IReadOnlyList<ExchangeDefinition> definitions)
        => this.IdentifyShop(entries, definitions, null);

    /// <summary>
    /// 期待するショップを手がかりに特定する。
    ///
    /// 同じ品揃え・同じ値段のショップが複数存在することがある
    /// （スクリップ交換では同一内容のショップが複数のカテゴリにぶら下がる）。
    /// その場合、内容だけではどれか決められないが、
    /// **候補全部が同じ品揃えと値段である以上、どれであっても照合結果は変わらない**。
    ///
    /// そこで「自分が向かったショップが候補に含まれているか」を最後の手がかりにする。
    /// 含まれていなければ従来どおり特定できないものとして扱う。
    /// </summary>
    public ShopIdentification IdentifyShop(
        IReadOnlyList<ShopEntry> entries,
        IReadOnlyList<ExchangeDefinition> definitions,
        uint? expectedShopId)
    {
        if (entries.Count == 0)
        {
            return new ShopIdentification(null, 0, 0, "ショップのエントリを読めていません");
        }

        // ShopId ごとに (ItemId, コスト) の集合を作る
        var byShop = new Dictionary<uint, HashSet<(uint ItemId, uint Cost)>>();
        foreach (var definition in definitions)
        {
            if (!byShop.TryGetValue(definition.ShopId, out var set))
            {
                byShop[definition.ShopId] = set = [];
            }

            set.Add((definition.RewardItemId, definition.CurrencyCost));
        }

        uint? best = null;
        var bestScore = -1;
        var perfect = new List<uint>();

        foreach (var (shopId, set) in byShop)
        {
            var score = 0;
            foreach (var entry in entries)
            {
                if (set.Contains((entry.ItemId, entry.CostAmount)))
                {
                    score++;
                }
            }

            if (score == entries.Count)
            {
                perfect.Add(shopId);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = shopId;
            }
        }

        if (best is null || bestScore <= 0)
        {
            return new ShopIdentification(null, 0, entries.Count, "画面の内容に一致するショップがゲームデータ内に見つかりません");
        }

        if (bestScore < entries.Count)
        {
            return new ShopIdentification(
                best,
                bestScore,
                entries.Count,
                $"最も近いのは Shop {best} ですが、画面 {entries.Count} 件中 {bestScore} 件しか一致しません");
        }

        if (perfect.Count > 1)
        {
            // 自分が向かったショップが候補に含まれているなら、それを採用してよい。
            // 候補はすべて同じ品揃え・同じ値段なので、どれであっても照合結果は変わらない。
            if (expectedShopId is { } expected && perfect.Contains(expected))
            {
                return new ShopIdentification(
                    expected,
                    bestScore,
                    entries.Count,
                    $"内容が同一のショップが {perfect.Count} 件ありますが、目的の Shop {expected} が含まれるため採用します");
            }

            // 手がかりが無ければ特定できない。ShopId を返してはならない。
            return new ShopIdentification(
                null,
                bestScore,
                entries.Count,
                $"内容が完全一致するショップが {perfect.Count} 件あり、どれか特定できません",
                Ambiguous: true);
        }

        return new ShopIdentification(best, bestScore, entries.Count, $"Shop {best} と完全一致しました");
    }

    /// <summary>
    /// 交換定義と、画面のエントリを ItemId で照合する。
    ///
    /// 一致したエントリの Index が、交換 callback に渡すべき値になる。
    /// シート上の位置やループ変数を渡してはならない。
    /// </summary>
    public ShopMatchResult Match(
        ExchangeDefinition definition,
        ShopIdentification identification,
        IReadOnlyList<ShopEntry> entries)
    {
        // ショップの同一性を最初の必須条件にする。
        // ItemId だけで照合すると、同じアイテムを別の値段で持つ他のショップの定義と比べてしまう。
        if (!identification.IsConfident || identification.ShopId != definition.ShopId)
        {
            return new ShopMatchResult(
                ShopMatchKind.ShopMismatch,
                null,
                $"開いているショップを特定できないか、定義のショップ {definition.ShopId} と一致しません（{identification.Detail}）");
        }

        ShopEntry? found = null;
        var duplicates = 0;

        foreach (var entry in entries)
        {
            if (entry.ItemId != definition.RewardItemId)
            {
                continue;
            }

            if (found is null)
            {
                found = entry;
            }
            else
            {
                duplicates++;
            }
        }

        if (found is null)
        {
            return new ShopMatchResult(
                ShopMatchKind.ItemNotFound,
                null,
                $"ItemId {definition.RewardItemId} がショップ内に見つかりません（読み取れたのは {entries.Count} 件）");
        }

        if (duplicates > 0)
        {
            return new ShopMatchResult(
                ShopMatchKind.Ambiguous,
                null,
                $"ItemId {definition.RewardItemId} が {duplicates + 1} 件見つかりました。どれを選ぶべきか決められません");
        }

        if (found.CostAmount != definition.CurrencyCost)
        {
            return new ShopMatchResult(
                ShopMatchKind.CostMismatch,
                found,
                $"コストが一致しません。ゲームデータ {definition.CurrencyCost} に対して画面は {found.CostAmount} です");
        }

        return new ShopMatchResult(ShopMatchKind.Matched, found, $"一致（callback へ渡す index = {found.Index}）");
    }
}

/// <summary>
/// 開いているショップがどの SpecialShop 行かの特定結果。
/// ShopId が null、または MatchedEntries が TotalEntries に満たない場合は交換してはいけない。
/// </summary>
public sealed record ShopIdentification(
    uint? ShopId,
    int MatchedEntries,
    int TotalEntries,
    string Detail,
    bool Ambiguous = false)
{
    public bool IsConfident =>
        this.ShopId is not null
        && this.TotalEntries > 0
        && this.MatchedEntries == this.TotalEntries
        && !this.Ambiguous;
}

/// <summary>ショップ画面のヘッダ情報。</summary>
public sealed record ShopHeader(
    uint DeclaredEntryCount,
    uint CurrencyAmount,
    uint CurrencyIcon,
    string CurrencyIconType,
    int UnreadableEntries);
