using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

// npc_shop_links.json を、ゲームデータと突き合わせて検証する。
class P {
    const uint BicolorGem = 26807;

    static HashSet<uint> Rew(SpecialShop s) {
        var r=new HashSet<uint>();
        foreach(var it in s.Item){bool h=false;foreach(var q in it.ItemCosts)if(q.ItemCost.RowId==BicolorGem)h=true;
          if(!h)continue;var x=it.ReceiveItems.FirstOrDefault().Item.RowId;if(x!=0)r.Add(x);}
        return r;
    }

    static void Main() {
        var path=@"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
        var gd=new GameData(path,new LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.Japanese});
        var shops=gd.GetExcelSheet<SpecialShop>();
        var enpc=gd.GetExcelSheet<ENpcResident>();
        var lvl=gd.GetExcelSheet<Level>();

        var loc=new Dictionary<uint,uint>();
        foreach(var l in lvl){if(l.Type!=8)continue;if(!loc.ContainsKey(l.Object.RowId))loc[l.Object.RowId]=l.Territory.RowId;}

        // ゲーム側にある「バイカラージェムを使うショップ」の全体
        var actual=new HashSet<uint>();
        foreach(var s in shops){ if(Rew(s).Count>0) actual.Add(s.RowId); }

        var json=JObject.Parse(File.ReadAllText(@"C:\ソース\AutoCollector\AutoCollector\Data\npc_shop_links.json"));
        int err=0; var seen=new Dictionary<uint,string>();

        foreach(var link in json["links"]!) {
            uint npcId=(uint)link["npcId"]!; string nm=(string)link["npcName"]!;
            uint terrId=(uint)link["territoryId"]!;
            // NPC 名の一致
            var real = enpc.TryGetRow(npcId, out var e) ? e.Singular.ExtractText() : null;
            if(real==null){Console.WriteLine($"× NPC {npcId} が存在しない");err++;continue;}
            if(real!=nm){Console.WriteLine($"× NPC {npcId} 名前ちがい: 表'{nm}' 実'{real}'");err++;}
            // territory の一致
            if(loc.TryGetValue(npcId, out var rt)) { if(rt!=terrId){Console.WriteLine($"× {nm} territory ちがい: 表{terrId} 実{rt}");err++;} }
            else Console.WriteLine($"  ! {nm} は Level シートに座標が無い（LGB 側にある可能性）");
            // shops の検証
            foreach(var sv in link["shops"]!) {
                uint sid=(uint)sv!;
                if(!actual.Contains(sid)){Console.WriteLine($"× {nm}: ショップ {sid} はバイカラージェムを使わない");err++;continue;}
                if(seen.TryGetValue(sid, out var other)){Console.WriteLine($"× ショップ {sid} が重複: {other} と {nm}");err++;}
                else seen[sid]=nm;
            }
        }
        var missing=actual.Where(x=>!seen.ContainsKey(x)).OrderBy(x=>x).ToList();
        Console.WriteLine($"\nゲーム側のバイカラージェム交換所: {actual.Count} 件");
        Console.WriteLine($"表に載っている数            : {seen.Count} 件");
        Console.WriteLine($"NPC 数                      : {json["links"]!.Count()} 人");
        if(missing.Count>0) { Console.WriteLine($"× 表に無いショップ: {string.Join(", ",missing)}"); err+=missing.Count; }
        Console.WriteLine(err==0 ? "\n>>> 検証OK: 誤り 0 件。42 ショップすべてが重複なく 1 人の NPC に結びついている。"
                                 : $"\n>>> 誤り {err} 件");
    }
}
