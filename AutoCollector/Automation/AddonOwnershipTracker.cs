using System;
using System.Collections.Generic;
using AutoCollector.Diagnostics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Automation;

/// <summary>
/// 自分の操作が開かせたウィンドウを記録する。
///
/// 名前でアドオンを引くだけでは、誰が開いたのか分からない。
/// 「名前で見つかったから閉じる」をやると、他プラグインの自動化や
/// ユーザーが手動で開いた買い物を壊す。
///
/// 記録するのはアドレス値だけで、逆参照はしない。
/// PreFinalize はウィンドウが非表示になっただけでは発火しないため、
/// 古いアドレスが別のアドオンに再利用される可能性がある。
/// そのため一定時間で失効させ、判定時は「アドレス一致」かつ
/// 「いまその名前で取得したアドオンが操作可能」の両方を要求する。
/// </summary>
public sealed unsafe class AddonOwnershipTracker : IDisposable
{
    private static readonly string[] Tracked =
    [
        "ShopExchangeCurrency", "ShopExchangeCurrencyDialog", "SelectYesno", "SelectString", "SelectIconString", "Talk",
    ];

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly AnomalyLog anomalyLog;
    private readonly Dictionary<nint, DateTime> owned = [];
    private bool registered;

    public AddonOwnershipTracker(AnomalyLog anomalyLog)
    {
        this.anomalyLog = anomalyLog;

        try
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Tracked, this.OnPostSetup);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, Tracked, this.OnPreFinalize);
            this.registered = true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Addon", $"ウィンドウの所有権を追跡できません: {ex.Message}");
        }
    }

    /// <summary>
    /// 自分の操作が進行中かどうか。
    /// これが true の間に開いたウィンドウだけを「自分のもの」として記録する。
    /// </summary>
    public bool IsClaiming { get; set; }

    private void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!this.IsClaiming)
        {
            return;
        }

        this.owned[args.Addon.Address] = DateTime.UtcNow;
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        this.owned.Remove(args.Addon.Address);
    }

    /// <summary>
    /// 指定した名前のウィンドウが開いていて、かつ自分が開いたものなら true。
    /// 自分のものでなければ触ってはいけない。
    /// </summary>
    public bool TryGetOwned(string addonName, out AtkUnitBase* addon)
    {
        addon = null;

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var candidate) || !GenericHelpers.IsAddonReady(candidate))
        {
            return false;
        }

        var address = (nint)candidate;
        if (!this.owned.TryGetValue(address, out var claimedAt))
        {
            return false;
        }

        if (DateTime.UtcNow - claimedAt > Lifetime)
        {
            // 古い記録は、アドレスが別のアドオンに再利用されている可能性がある。
            this.owned.Remove(address);
            return false;
        }

        addon = candidate;
        return true;
    }

    /// <summary>記録をすべて破棄する。交換処理の完了時に呼ぶ。</summary>
    public void Clear()
    {
        this.owned.Clear();
        this.IsClaiming = false;
    }

    public int OwnedCount => this.owned.Count;

    public void Dispose()
    {
        if (!this.registered)
        {
            return;
        }

        try
        {
            Svc.AddonLifecycle.UnregisterListener(this.OnPostSetup);
            Svc.AddonLifecycle.UnregisterListener(this.OnPreFinalize);
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] ウィンドウ追跡の解除に失敗しました: {ex}");
        }

        this.registered = false;
        this.owned.Clear();
    }
}
