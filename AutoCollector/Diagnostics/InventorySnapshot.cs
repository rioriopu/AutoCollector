using System;
using System.Collections.Generic;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoCollector.Diagnostics;

/// <summary>
/// 鞄の中身を数える。
///
/// 交換の前後で比べて「何と引き換えに何を得たのか」を出すために使う。
/// 通貨だけを見ていると、受け取った品が出てこない。
/// </summary>
public static unsafe class InventorySnapshot
{
    /// <summary>
    /// 数える入れ物。
    ///
    /// 装備や道具はアーマリーチェストへ入り、鞄には現れない。
    /// 鞄だけを見ていると、そうした品を受け取っても増減に出ない。
    /// </summary>
    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];

    /// <summary>鞄にある品を ItemId ごとにまとめて返す。</summary>
    public static IReadOnlyList<(uint ItemId, string Name, int Count)> ListBagItems()
    {
        var totals = new Dictionary<uint, int>();

        try
        {
            var manager = InventoryManager.Instance();
            if (manager is null)
            {
                return [];
            }

            foreach (var type in Bags)
            {
                var container = manager->GetInventoryContainer(type);
                if (container is null || !container->IsLoaded)
                {
                    continue;
                }

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot is null || slot->ItemId == 0)
                    {
                        continue;
                    }

                    var id = slot->GetBaseItemId();
                    totals[id] = totals.GetValueOrDefault(id) + slot->Quantity;
                }
            }
        }
        catch
        {
            return [];
        }

        var sheet = Svc.Data.GetExcelSheet<Item>();
        var list = new List<(uint, string, int)>(totals.Count);

        foreach (var (itemId, count) in totals)
        {
            var name = sheet?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"ItemId {itemId}";
            list.Add((itemId, name, count));
        }

        return list;
    }
}
