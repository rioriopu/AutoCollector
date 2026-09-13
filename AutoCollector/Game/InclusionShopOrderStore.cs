using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.Configuration;
using ECommons.DalamudServices;

namespace AutoCollector.Game;

/// <summary>保存用。SpecialShop の行 → 画面に並んでいた順の ItemId。</summary>
public sealed class InclusionShopOrderFile
{
    public string GameVersion { get; set; } = string.Empty;

    public Dictionary<uint, List<uint>> Orders { get; set; } = [];
}

/// <summary>
/// アイテム交換画面の並び順を、実際の画面から覚える。
///
/// 並び順をシートから再現しようとしたが、装備以外で規則を特定できなかった。
///
/// <code>
/// 観測 41 画面での一致数
///   シート順そのまま                    8 / 41
///   Order 昇順                        25 / 41
///   Order 昇順 → 職 → 部位 → iLv 降    28 / 41   ← 最良
/// </code>
///
/// 外れた 13 画面はすべて装備を 1 つも含まない（素材・釣り餌・雑貨・マテリア）。
/// 装備の画面 28 件はすべて一致する。
///
/// **推測を重ねるより、実際に開いた画面から覚えるほうが確実。**
/// NPC の座標で同じ判断をしている。
///
/// 一度でもその画面を開けば、以後は設定画面でも同じ並びで出せる。
/// 覚えていない画面は最良の推測で並べる。
/// </summary>
public sealed class InclusionShopOrderStore(AnomalyLog anomalyLog)
{
    private const string FileName = "inclusion-order.json";

    private readonly AnomalyLog anomalyLog = anomalyLog;

    private Dictionary<uint, List<uint>>? orders;
    private bool dirty;
    private DateTime nextSaveUtc = DateTime.MinValue;

    /// <summary>覚えている画面の数。</summary>
    public int Count => this.Load().Count;

    private static string ResolvePath()
        => Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, FileName);

    private Dictionary<uint, List<uint>> Load()
    {
        if (this.orders is not null)
        {
            return this.orders;
        }

        this.orders = [];

        try
        {
            var path = ResolvePath();
            if (!File.Exists(path))
            {
                return this.orders;
            }

            var file = EzConfig.LoadConfiguration<InclusionShopOrderFile>(path, false);

            // ゲームが更新されたら品揃えが変わる。覚えた並びは捨てる。
            var version = NpcLocationCache.ResolveGameVersion();
            if (!string.IsNullOrEmpty(version) && file.GameVersion != version)
            {
                this.anomalyLog.Info("Inclusion", "ゲームが更新されたため、覚えていた並び順を捨てます");
                return this.orders;
            }

            this.orders = file.Orders;
            this.anomalyLog.Info("Inclusion", $"覚えていた並び順を読み込みました（{this.orders.Count} 画面）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Inclusion", $"並び順を読めませんでした: {ex.Message}");
            this.orders = [];
        }

        return this.orders;
    }

    /// <summary>覚えている並びを返す。無ければ false。</summary>
    public bool TryGet(uint specialShopId, out IReadOnlyList<uint> order)
    {
        if (this.Load().TryGetValue(specialShopId, out var list) && list.Count > 0)
        {
            order = list;
            return true;
        }

        order = [];
        return false;
    }

    /// <summary>
    /// 画面で見た並びを覚える。
    /// 中身が変わっていなければ何もしない。
    /// </summary>
    public void Learn(uint specialShopId, IReadOnlyList<uint> itemIds)
    {
        if (specialShopId == 0 || itemIds.Count == 0)
        {
            return;
        }

        var store = this.Load();

        if (store.TryGetValue(specialShopId, out var existing) && existing.SequenceEqual(itemIds))
        {
            return;
        }

        store[specialShopId] = [.. itemIds];
        this.dirty = true;

        this.anomalyLog.Info("Inclusion", $"並び順を覚えました（SpecialShop {specialShopId} / {itemIds.Count} 件）");
    }

    /// <summary>覚えたものがあれば保存する。書き込みは間隔を空ける。</summary>
    public void SaveIfDirty()
    {
        var now = DateTime.UtcNow;

        if (!this.dirty || now < this.nextSaveUtc)
        {
            return;
        }

        this.dirty = false;
        this.nextSaveUtc = now.AddSeconds(20);

        try
        {
            var file = new InclusionShopOrderFile
            {
                GameVersion = NpcLocationCache.ResolveGameVersion(),
                Orders = this.Load(),
            };

            EzConfig.SaveConfiguration(file, ResolvePath(), true, false);
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Inclusion", $"並び順を保存できませんでした: {ex.Message}");
        }
    }

    /// <summary>覚えたものを消す。並びがおかしくなったときのやり直し用。</summary>
    public void Clear()
    {
        this.orders = [];
        this.dirty = true;
        this.nextSaveUtc = DateTime.MinValue;
        this.SaveIfDirty();
    }
}
