using System;
using System.Linq;
using System.Collections.Generic;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;

class P {
    const uint Gem=26807, ShopType=0x001B, MarkerMax=1000;
    static void Main() {
        var path=@"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
        var gd=new GameData(path,new LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.Japanese});
        var shops=gd.GetExcelSheet<SpecialShop>(); var items=gd.GetExcelSheet<Item>();
        var quests=gd.GetExcelSheet<Quest>();
        var raw=gd.Excel.GetSheet<RawRow>(name:"FateShop");

        var rank=new Dictionary<(uint,uint),uint>();
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
                uint req=(uint)(st+1);
                if(!shops.TryGetRow(ord[st], out var sh))continue;
                foreach(var e in sh.Item){
                    var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw==0)continue;
                    if(seen.Add(rw) && req>1) rank[(ord[st],rw)]=req;
                }
            }
        }
        foreach(var sh in shops){
            var marks=new SortedSet<uint>();
            foreach(var e in sh.Item){
                var m=e.Quest.RowId;
                if(m==0||m>=MarkerMax||quests.HasRow(m))continue; marks.Add(m);
            }
            if(marks.Count==0)continue;
            bool tok=false; foreach(var e in sh.Item) foreach(var c in e.ItemCosts) if(c.ItemCost.RowId==Gem) tok=true;
            if(!tok)continue;
            var so=marks.Select((m,i)=>(m,s:(uint)(i+2))).ToDictionary(x=>x.m,x=>x.s);
            foreach(var e in sh.Item){
                if(!so.TryGetValue(e.Quest.RowId, out var st))continue;
                var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw!=0) rank[(sh.RowId,rw)]=st;
            }
        }

        // サイトの記述と照合する代表例
        var checks = new (uint shop,string item,uint expect,string src)[]{
            (1770461,"詳細地図:サベネア島",2,"暁月サイト:ランク2"),
            (1770461,"オーケストリオン譜:神々の訓え",2,"暁月サイト:ランク3(※同居)"),
            (1770460,"ガジャの粗皮",0,"暁月サイト:ランク1=条件なし"),
            (1769959,"信力のメガマテリジャ",2,"漆黒サイト:ランク2"),
            (1769959,"オーケストリオン譜:始まりの湖",3,"漆黒サイト:ランク3"),
            (1769959,"コーディアル",0,"漆黒サイト:ランク1=条件なし"),
            (1769959,"ディープシャドウ・バード",3,"漆黒サイト:ランク3"),
        };
        int ok=0,ng=0;
        foreach(var (shop,nm,expect,src) in checks){
            shops.TryGetRow(shop, out var s);
            uint found=0; bool exists=false;
            foreach(var e in s.Item){
                var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw==0)continue;
                if(items.GetRowOrDefault(rw)?.Name.ExtractText()!=nm)continue;
                exists=true; found=rank.GetValueOrDefault((shop,rw)); break;
            }
            bool good = exists && found==expect;
            if(good)ok++; else ng++;
            Console.WriteLine($"{(good?"OK ":"×  ")}{nm,-32} 実装={found} 期待={expect}  [{src}]");
        }
        Console.WriteLine($"\n一致 {ok} / 不一致 {ng}");
    }
}
