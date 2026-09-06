using System;
using System.Collections.Generic;
using System.Text;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Diagnostics;

/// <summary>
/// 収集品納品画面（CollectablesShop）の中身を読み出す。
///
/// この画面には型付きの構造体定義が無く、一覧の構成も納品の発火手段も
/// 確認できていない。推測で書いたコードは撃ってはいけないので、
/// まず実機で何が入っているかを読み出して確かめるための道具を用意する。
///
/// **ここは読み取りだけを行う。ゲームの状態は一切変更しない。**
/// ボタンも押さないし、コールバックも送らない。
/// </summary>
public sealed unsafe class CollectablesShopReader
{
    public const string AddonName = "CollectablesShop";

    /// <summary>納品画面が開いているか。</summary>
    public bool IsOpen()
    {
        try
        {
            return GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) &&
                   GenericHelpers.IsAddonReady(addon);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 画面と手持ちの収集品をまとめて書き出す。
    /// 得られた文字列をそのまま報告に使えるようにする。
    /// </summary>
    public string Dump()
    {
        var sb = new StringBuilder();

        try
        {
            sb.AppendLine($"=== 収集品納品画面のダンプ（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）===");

            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) ||
                !GenericHelpers.IsAddonReady(addon))
            {
                sb.AppendLine("納品画面が開いていません。窓口に話しかけてから実行してください。");
                return sb.ToString();
            }

            DumpAddon(sb, addon);
            DumpAtkValues(sb, addon);
            DumpCollectables(sb);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"読み出しに失敗しました: {ex}");
        }

        return sb.ToString();
    }

    private static void DumpAddon(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine();
        sb.AppendLine("-- ノード --");

        // 一覧のノード ID が分からないため、候補を総当たりで見る。
        // 触らずに種類と可視状態だけを読む。
        foreach (var id in new uint[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 27, 28, 29, 30, 31, 32, 50, 51, 52 })
        {
            try
            {
                var node = addon->GetNodeById(id);
                if (node is null)
                {
                    continue;
                }

                sb.Append($"node {id,3}: type={node->Type} visible={node->IsVisible()}");

                var component = addon->GetComponentByNodeId(id);
                if (component is not null)
                {
                    sb.Append($" component={component->GetComponentType()}");

                    // 一覧の実体がどの型かは未確認。両方の見え方を出して判断材料にする。
                    var type = component->GetComponentType();

                    if (type == ComponentType.List)
                    {
                        var list = (AtkComponentList*)component;
                        sb.Append($" listLength={list->ListLength} selected={list->SelectedItemIndex}");
                    }
                    else if (type == ComponentType.TreeList)
                    {
                        var tree = (AtkComponentTreeList*)component;
                        sb.Append($" treeItems={tree->Items.LongCount} selected={tree->SelectedItemIndex}");
                    }
                }

                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"node {id,3}: 読み出しに失敗 {ex.Message}");
            }
        }
    }

    private static void DumpAtkValues(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine();
        sb.AppendLine($"-- AtkValues（{addon->AtkValuesCount} 件のうち先頭 120）--");

        var count = Math.Min(addon->AtkValuesCount, 120u);
        for (var i = 0u; i < count; i++)
        {
            try
            {
                var v = addon->AtkValues[i];
                if (v.Type == 0)
                {
                    continue;
                }

                sb.AppendLine($"[{i,3}] {v.Type,-14} {Describe(v)}");
            }
            catch
            {
                sb.AppendLine($"[{i,3}] 読み出しに失敗");
            }
        }
    }

    private static string Describe(AtkValue value)
    {
        try
        {
            return value.Type switch
            {
                AtkValueType.Int => value.Int.ToString(),
                AtkValueType.UInt => $"{value.UInt}u",
                AtkValueType.Bool => value.Byte != 0 ? "true" : "false",
                AtkValueType.Float => value.Float.ToString("0.###"),
                AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8 =>
                    value.String.Value is null ? "\"\"" : $"\"{value.String}\"",
                _ => $"[{value.Type}]",
            };
        }
        catch
        {
            return "[読めません]";
        }
    }

    /// <summary>
    /// 手持ちの収集品を書き出す。
    /// シートのしきい値と画面の収集価値が同じ単位かを確かめるために使う。
    /// </summary>
    private static void DumpCollectables(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("-- 所持している収集品 --");

        try
        {
            var manager = InventoryManager.Instance();
            if (manager is null)
            {
                sb.AppendLine("インベントリを取得できません。");
                return;
            }

            var found = 0;
            var types = new[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
            };

            foreach (var type in types)
            {
                var container = manager->GetInventoryContainer(type);
                if (container is null || !container->IsLoaded)
                {
                    continue;
                }

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot is null || slot->ItemId == 0 || !slot->IsCollectable())
                    {
                        continue;
                    }

                    found++;
                    sb.AppendLine(
                        $"{type}[{i,2}] ItemId={slot->GetBaseItemId(),6} 個数={slot->Quantity,3} " +
                        $"収集価値={slot->GetCollectability(),5} 生値={slot->SpiritbondOrCollectability,5} " +
                        $"Flags={slot->Flags}");
                }
            }

            if (found == 0)
            {
                sb.AppendLine("収集品を持っていません。");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"インベントリの読み出しに失敗しました: {ex.Message}");
        }
    }

    /// <summary>ダンプをファイルへ書き出す。長いので画面ではなくファイルで渡す。</summary>
    public string Save(string directory)
    {
        var path = System.IO.Path.Combine(directory, $"CollectablesShop_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(path, this.Dump(), Encoding.UTF8);
        return path;
    }
}
