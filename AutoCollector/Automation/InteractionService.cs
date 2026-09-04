using System;
using System.Linq;
using AutoCollector.Diagnostics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Automation;

/// <summary>
/// NPC を探してターゲットし、話しかける。
///
/// 接近距離はハードコードしない。AutoRetainer が持っている 4.6f などの値は
/// 召喚ベル専用のもので、一般の NPC には適用できない。
/// 到達判定は「ターゲット可能であること」と「実際にウィンドウが開いたこと」で行う。
/// </summary>
public sealed unsafe class InteractionService(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>指定した BaseId の NPC を探す。</summary>
    public bool TryFindNpc(uint baseId, out IGameObject? npc)
    {
        npc = null;

        if (baseId == 0)
        {
            return false;
        }

        try
        {
            // DataId は Dalamud で BaseId へ改名されている。
            npc = Svc.Objects.FirstOrDefault(x =>
                x.ObjectKind == ObjectKind.EventNpc &&
                x.BaseId == baseId &&
                x.IsTargetable);

            return npc is not null;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Interact", $"NPC の検索に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 対話を 1 段階進める。
    /// まだターゲットしていなければターゲットするだけで false を返し、次フレームへ回す。
    /// </summary>
    public bool StepInteract(IGameObject npc)
    {
        if (!npc.IsTargetable)
        {
            return false;
        }

        if (Player.IsAnimationLocked || GenericHelpers.IsOccupied())
        {
            return false;
        }

        if (Svc.Targets.Target?.Address != npc.Address)
        {
            Svc.Targets.Target = npc;
            return false;
        }

        if (!EzThrottler.Throttle("AutoCollector.Interact", 1000))
        {
            return false;
        }

        try
        {
            // 参照した全プラグインが checkLineOfSight に false を渡している。
            // 戻り値の意味はソース上に記述が無いため、成否判定には使わない。
            TargetSystem.Instance()->InteractWithObject(npc.Struct(), false);
            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Interact", $"NPC への対話に失敗しました: {ex.Message}");
            return false;
        }
    }
}

/// <summary>選択肢の絞り込みに失敗した理由。</summary>
public enum MenuSelectFailure
{
    None,

    /// <summary>選択肢が出ていない。</summary>
    NoMenu,

    /// <summary>候補が 1 つも一致しなかった。</summary>
    NotFound,

    /// <summary>候補が複数一致した。どれを選ぶべきか決められない。</summary>
    Ambiguous,
}

/// <summary>
/// NPC の会話メニュー（SelectString / SelectIconString）の処理。
///
/// 番号での選択はしない。並びはクエストの進行状況で変わる。
/// ゲームデータから得たヒント文字列と照合し、一致がちょうど 1 件のときだけ選ぶ。
/// </summary>
public sealed unsafe class MenuService(AnomalyLog anomalyLog)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>会話メニューが開いているか。</summary>
    public bool IsMenuOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var s) && GenericHelpers.IsAddonReady(s)
           || GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectIconString", out var i) && GenericHelpers.IsAddonReady(i);

    /// <summary>いま出ている選択肢を列挙する。UI 表示とヒントの学習に使う。</summary>
    public string[] ListEntries()
    {
        try
        {
            if (GenericHelpers.TryGetAddonMaster<AddonMaster.SelectString>(out var select) && select.IsAddonReady)
            {
                return [.. select.Entries.Select(x => x.Text ?? string.Empty)];
            }

            if (GenericHelpers.TryGetAddonMaster<AddonMaster.SelectIconString>(out var icon) && icon.IsAddonReady)
            {
                return [.. icon.Entries.Select(x => x.Text ?? string.Empty)];
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Menu", $"選択肢を読めませんでした: {ex.Message}");
        }

        return [];
    }

    /// <summary>
    /// ヒント文字列に一致する選択肢を 1 つだけ選ぶ。
    /// 一致が 0 件または 2 件以上なら選ばない。
    /// </summary>
    public bool TrySelectByText(string hint, out MenuSelectFailure failure)
    {
        failure = MenuSelectFailure.None;

        if (string.IsNullOrWhiteSpace(hint))
        {
            failure = MenuSelectFailure.NotFound;
            return false;
        }

        var entries = this.ListEntries();
        if (entries.Length == 0)
        {
            failure = MenuSelectFailure.NoMenu;
            return false;
        }

        if (!TryResolveIndex(entries, hint, out var index, out failure))
        {
            return false;
        }

        if (!EzThrottler.Throttle("AutoCollector.MenuSelect", 500))
        {
            return false;
        }

        try
        {
            if (GenericHelpers.TryGetAddonMaster<AddonMaster.SelectString>(out var select) && select.IsAddonReady)
            {
                select.Entries[index].Select();
                return true;
            }

            if (GenericHelpers.TryGetAddonMaster<AddonMaster.SelectIconString>(out var icon) && icon.IsAddonReady)
            {
                icon.Entries[index].Select();
                return true;
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Menu", $"選択肢を選べませんでした: {ex.Message}");
        }

        failure = MenuSelectFailure.NoMenu;
        return false;
    }

    /// <summary>完全一致を優先し、無ければ部分一致で 1 件に絞る。</summary>
    private static bool TryResolveIndex(string[] entries, string hint, out int index, out MenuSelectFailure failure)
    {
        index = -1;
        failure = MenuSelectFailure.None;

        static string Normalize(string value) => value.Replace(" ", string.Empty).Replace("　", string.Empty).Trim();

        var target = Normalize(hint);

        var exact = new System.Collections.Generic.List<int>();
        var partial = new System.Collections.Generic.List<int>();

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = Normalize(entries[i]);
            if (entry.Length == 0)
            {
                continue;
            }

            if (string.Equals(entry, target, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(i);
            }
            else if (entry.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                     target.Contains(entry, StringComparison.OrdinalIgnoreCase))
            {
                partial.Add(i);
            }
        }

        var candidates = exact.Count > 0 ? exact : partial;

        switch (candidates.Count)
        {
            case 1:
                index = candidates[0];
                return true;

            case 0:
                failure = MenuSelectFailure.NotFound;
                return false;

            default:
                failure = MenuSelectFailure.Ambiguous;
                return false;
        }
    }
}
