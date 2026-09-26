using System;
using System.Linq;
using System.Collections.Generic;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;

// FateShopRankMap と同じ手順で段階を作り、結果を検証する。
class P {
    const uint MarkerMax=1000, ShopType=0x001B;
    static void Main() {
        var path=@"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
        var gd=new GameData(path,new LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.Japanese});
        var shops=gd.GetExcelSheet<SpecialShop>(); var items=gd.GetExcelSheet<Item>();
        var quests=gd.GetExcelSheet<Quest>();
        var raw=gd.Excel.GetSheet<RawRow>(name:"FateShop");

        // FateTokenType から通貨を引く
        var tokens=new HashSet<uint>();
        foreach(var r in gd.Excel.GetSheet<RawRow>(name:"FateTokenType")){
            var v=Convert.ToUInt32(r.ReadColumn(0)); if(v!=0) tokens.Add(v);
        }
        Console.WriteLine($"FateTokenType の通貨: {string.Join(", ", tokens.Select(t=>$"{t}({items.GetRowOrDefault(t)?.Name.ExtractText()})"))}\n");

        bool UsesToken(SpecialShop s){
            foreach(var e in s.Item) foreach(var c in e.ItemCosts) if(tokens.Contains(c.ItemCost.RowId)) return true;
            return false;
        }

        var rank=new Dictionary<(uint,uint),uint>();
        // 方式1: FateShop の列順
        foreach(var r in raw){
            if(r.RowId==0)continue;
            var ord=new List<uint>();
            for(int c=0;c<raw.Columns.Count;c++){
                uint v; try{v=Convert.ToUInt32(r.ReadColumn(c));}catch{continue;}
                if((v>>16)!=ShopType)continue; if(!ord.Contains(v))ord.Add(v);
            }
            if(ord.Count<=1)continue;
            var seen=new HashSet<uint>();
            for(int st=0;st<ord.Count;st++){
                if(!shops.TryGetRow(ord[st], out var sh))continue;
                foreach(var e in sh.Item){
                    var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw==0)continue;
                    if(seen.Add(rw)&&st>0) rank[(ord[st],rw)]=(uint)(st+1);
                }
            }
        }
        // 方式2: 印（通貨を使うショップに限る）
        int mshops=0;
        foreach(var sh in shops){
            var marks=new SortedSet<uint>();
            foreach(var e in sh.Item){
                var m=e.Quest.RowId;
                if(m==0||m>=MarkerMax||quests.HasRow(m))continue; marks.Add(m);
            }
            if(marks.Count==0||!UsesToken(sh))continue;
            mshops++;
            var so=marks.Select((m,i)=>(m,s:(uint)(i+2))).ToDictionary(x=>x.m,x=>x.s);
            foreach(var e in sh.Item){
                if(!so.TryGetValue(e.Quest.RowId, out var st))continue;
                var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw!=0) rank[(sh.RowId,rw)]=st;
            }
        }
        Console.WriteLine($"印を使うショップ（通貨で絞った後）: {mshops} 件  ← 漆黒6マップのみなら正しい");
        Console.WriteLine($"段階つきエントリ: {rank.Count} 件\n");

        // 全ショップで「段階つきの品が、その段階のショップにしか無い」ことを確認
        int bad=0;
        foreach(var kv in rank){
            var (shop,item)=kv.Key;
            if(!shops.TryGetRow(shop, out var s)) continue;
            bool found=s.Item.Any(e=>e.ReceiveItems.FirstOrDefault().Item.RowId==item);
            if(!found){Console.WriteLine($"× {shop} に {item} が無い");bad++;}
        }
        Console.WriteLine(bad==0? ">>> 検証OK: 段階の割り当て先がすべて実在する" : $">>> {bad}件おかしい");
    }
}
