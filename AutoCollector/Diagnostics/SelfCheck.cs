using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Game;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoCollector.Diagnostics;

public enum SelfCheckStatus
{
    Ok,
    Warning,
    Failed,
}

public sealed record SelfCheckItem(string Name, SelfCheckStatus Status, string Detail);

public sealed record SelfCheckReport(DateTime At, IReadOnlyList<SelfCheckItem> Items)
{
    /// <summary>1 つでも Failed があれば交換を開始してはならない。</summary>
    public bool CanExchange => this.Items.All(x => x.Status != SelfCheckStatus.Failed);

    public int WarningCount => this.Items.Count(x => x.Status == SelfCheckStatus.Warning);

    public int FailedCount => this.Items.Count(x => x.Status == SelfCheckStatus.Failed);
}

/// <summary>
/// 起動時・ゲームバージョン差分検知時・交換開始直前に走らせるセルフテスト。
///
/// 目的は「壊れていることに気付かないまま自動操作を続ける」のを防ぐこと。
/// 失敗したら黙って動き続けるのではなく、該当機能を止めてユーザーに見せる。
/// </summary>
public sealed class SelfCheck(Config config, AnomalyLog anomalyLog, TomestoneService tomestoneService, CurrencyService currencyService)
{
    /// <summary>SelfCheck が参照する連携プラグイン。バージョン変化の検知に使う。</summary>
    private static readonly string[] TrackedPlugins = ["AutoDuty", "AutoRetainer", "vnavmesh", "Lifestream"];

    public SelfCheckReport? Latest { get; private set; }

    public SelfCheckReport RunAll()
    {
        var items = new List<SelfCheckItem>
        {
            this.CheckGameVersion(),
            this.CheckModifiedGameData(),
            this.CheckTomestoneResolution(),
            this.CheckWeeklyConsistency(),
            this.CheckCurrencyApi(),
        };

        items.AddRange(this.CheckPluginVersions());

        var report = new SelfCheckReport(DateTime.Now, items);
        this.Latest = report;

        foreach (var item in items.Where(x => x.Status == SelfCheckStatus.Failed))
        {
            anomalyLog.Error("SelfCheck", $"{item.Name}: {item.Detail}");
        }

        foreach (var item in items.Where(x => x.Status == SelfCheckStatus.Warning))
        {
            anomalyLog.Warn("SelfCheck", $"{item.Name}: {item.Detail}");
        }

        return report;
    }

    private SelfCheckItem CheckGameVersion()
    {
        string? version;
        try
        {
            version = Svc.Data.GameData.Repositories.Values.FirstOrDefault()?.Version;
        }
        catch (Exception ex)
        {
            return new SelfCheckItem("ゲームバージョン", SelfCheckStatus.Warning, $"取得に失敗しました: {ex.Message}");
        }

        if (string.IsNullOrEmpty(version))
        {
            return new SelfCheckItem("ゲームバージョン", SelfCheckStatus.Warning, "バージョンを取得できませんでした");
        }

        var previous = config.LastSeenGameVersion;
        config.LastSeenGameVersion = version;

        if (previous is not null && previous != version)
        {
            return new SelfCheckItem(
                "ゲームバージョン",
                SelfCheckStatus.Warning,
                $"{previous} → {version} に更新されています。トームストーンの割り当てと UI の配置が変わっている可能性があるため、最初の交換は手動で確認してください");
        }

        return new SelfCheckItem("ゲームバージョン", SelfCheckStatus.Ok, version);
    }

    private SelfCheckItem CheckModifiedGameData()
    {
        try
        {
            return Svc.Data.HasModifiedGameDataFiles
                ? new SelfCheckItem("ゲームデータ改変", SelfCheckStatus.Warning, "改変されたゲームデータが検出されました。交換定義が実際の内容と異なる可能性があります")
                : new SelfCheckItem("ゲームデータ改変", SelfCheckStatus.Ok, "なし");
        }
        catch (Exception ex)
        {
            return new SelfCheckItem("ゲームデータ改変", SelfCheckStatus.Warning, $"判定できませんでした: {ex.Message}");
        }
    }

    private SelfCheckItem CheckTomestoneResolution()
    {
        var slots = tomestoneService.ListSlots();
        var named = slots.Where(x => !string.IsNullOrEmpty(x.Name)).ToList();

        if (named.Count == 0)
        {
            return new SelfCheckItem("トームストーン解決", SelfCheckStatus.Failed, "Tomestones シートからトームストーンを 1 件も解決できませんでした");
        }

        // 前回起動時と割り当てが変わっていたら知らせる。プリセットの監視対象が実質変わっているため。
        var snapshot = tomestoneService.SnapshotSlotToItemId();
        var changes = new List<string>();
        foreach (var (slot, itemId) in snapshot)
        {
            if (config.LastSeenTomestoneSlots.TryGetValue(slot, out var previous) && previous != itemId)
            {
                var previousName = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(previous)?.Name.ExtractText() ?? previous.ToString();
                var currentName = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? itemId.ToString();
                changes.Add($"スロット {slot}: {previousName} → {currentName}");
            }
        }

        config.LastSeenTomestoneSlots = snapshot;

        if (changes.Count > 0)
        {
            return new SelfCheckItem(
                "トームストーン解決",
                SelfCheckStatus.Warning,
                $"割り当てが変わりました。監視対象の通貨が入れ替わっています: {string.Join(" / ", changes)}");
        }

        var summary = string.Join(" / ", named.Select(x => $"{x.TomestonesRowId}={x.Name}"));
        return new SelfCheckItem("トームストーン解決", SelfCheckStatus.Ok, summary);
    }

    private SelfCheckItem CheckWeeklyConsistency()
    {
        // 週制限を持つスロットについてのみ検査する。
        var slots = tomestoneService.ListSlots().Where(x => x.SheetWeeklyLimit > 0).ToList();
        if (slots.Count == 0)
        {
            return new SelfCheckItem("週上限の整合", SelfCheckStatus.Ok, "週制限つきトームストーンはありません");
        }

        foreach (var slot in slots)
        {
            if (!tomestoneService.ValidateWeeklyConsistency(slot.TomestonesRowId, out var reason))
            {
                return new SelfCheckItem("週上限の整合", SelfCheckStatus.Warning, $"{slot.Name}: {reason}");
            }
        }

        var acquired = tomestoneService.GetWeeklyAcquired();
        var limit = tomestoneService.GetWeeklyLimitRuntime();
        return new SelfCheckItem("週上限の整合", SelfCheckStatus.Ok, $"今週 {acquired} / {limit}");
    }

    private SelfCheckItem CheckCurrencyApi()
    {
        // ギル(1) は必ず存在するので、API が生きているかの試験に使える。
        return currencyService.TryGetCount(1, out var gil)
            ? new SelfCheckItem("通貨 API", SelfCheckStatus.Ok, $"所持ギル {gil:N0} を取得できました")
            : new SelfCheckItem("通貨 API", SelfCheckStatus.Failed, "InventoryManager から所持数を取得できません。交換は行いません");
    }

    private List<SelfCheckItem> CheckPluginVersions()
    {
        var results = new List<SelfCheckItem>();

        Dictionary<string, string> current;
        try
        {
            current = Svc.PluginInterface.InstalledPlugins
                .Where(x => x.IsLoaded && TrackedPlugins.Contains(x.InternalName))
                .GroupBy(x => x.InternalName)
                .ToDictionary(g => g.Key, g => g.First().Version.ToString());
        }
        catch (Exception ex)
        {
            results.Add(new SelfCheckItem("連携プラグイン", SelfCheckStatus.Warning, $"一覧の取得に失敗しました: {ex.Message}"));
            return results;
        }

        foreach (var name in TrackedPlugins)
        {
            if (!current.TryGetValue(name, out var version))
            {
                results.Add(new SelfCheckItem($"連携: {name}", SelfCheckStatus.Ok, "未導入（該当機能は無効）"));
                continue;
            }

            results.Add(config.LastSeenPluginVersions.TryGetValue(name, out var previous) && previous != version
                ? new SelfCheckItem($"連携: {name}", SelfCheckStatus.Warning, $"{previous} → {version} に更新されています。IPC が変わっている可能性があります")
                : new SelfCheckItem($"連携: {name}", SelfCheckStatus.Ok, version));
        }

        config.LastSeenPluginVersions = current;
        return results;
    }
}
