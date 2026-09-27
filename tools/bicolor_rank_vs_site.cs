using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

class P {
    const uint Gem=26807, ShopType=0x001B, MarkerMax=1000;
    static Dictionary<uint,uint> achCache=new();
    static ExcelSheet<Achievement> achSheet;

    static uint RankFromAch(uint id){
        if(id==0)return 0;
        if(achCache.TryGetValue(id, out var c))return c;
        uint rank=0;
        if(achSheet.TryGetRow(id, out var row)){
            var t=row.Description.ExtractText();
            var m=t.IndexOf("RANK",StringComparison.OrdinalIgnoreCase);
            if(m>=0){ var d=""; for(int i=m+4;i<t.Length&&char.IsDigit(t[i]);i++) d+=t[i];
                if(d.Length>0) uint.TryParse(d, out rank); }
        }
        achCache[id]=rank; return rank;
    }

    static void Main() {
        var path=@"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
        var gd=new GameData(path,new LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.Japanese});
        var shops=gd.GetExcelSheet<SpecialShop>(); var items=gd.GetExcelSheet<Item>();
        var quests=gd.GetExcelSheet<Quest>(); achSheet=gd.GetExcelSheet<Achievement>();
        var raw=gd.Excel.GetSheet<RawRow>(name:"FateShop");

        var rank=new Dictionary<(uint,uint),uint>();
        foreach(var r in raw){
            if(r.RowId==0)continue;
            var ord=new List<uint>();
            for(int c=0;c<raw.Columns.Count;c++){
                uint v; try{v=Convert.ToUInt32(r.ReadColumn(c));}catch{break;}
                if((v>>16)!=ShopType) break;
                ord.Add(v);
            }
            if(ord.Count<=1)continue;
            var seen=new HashSet<uint>();
            for(int st=0;st<ord.Count;st++){
                uint req=(uint)(st+1);
                if(!shops.TryGetRow(ord[st], out var sh))continue;
                foreach(var e in sh.Item){
                    var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw==0)continue;
                    var fa=RankFromAch(e.AchievementUnlock.RowId);
                    if(fa>0){ rank[(ord[st],rw)]=fa; seen.Add(rw); continue; }
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

        var byName=new Dictionary<string,uint>();
        foreach(var kv in rank){
            var nm=items.GetRowOrDefault(kv.Key.Item2)?.Name.ExtractText(); if(nm==null)continue;
            if(!byName.TryGetValue(nm, out var cur)||kv.Value<cur) byName[nm]=kv.Value;
        }
        foreach(var s in shops){
            bool tok=false; foreach(var e in s.Item) foreach(var c in e.ItemCosts) if(c.ItemCost.RowId==Gem) tok=true;
            if(!tok)continue;
            foreach(var e in s.Item){
                bool h=false; foreach(var q in e.ItemCosts) if(q.ItemCost.RowId==Gem)h=true;
                if(!h)continue;
                var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw==0)continue;
                var nm=items.GetRowOrDefault(rw)?.Name.ExtractText(); if(nm==null)continue;
                if(!byName.ContainsKey(nm)) byName[nm]=1;
            }
        }

        var json=JObject.Parse(File.ReadAllText(@"site_tables.json"));
        int ok=0,ng=0,skip=0; var mis=new List<string>();
        // 都市の品は「全マップRANK3で解放」の意味なので除外する
        var cityShops=new HashSet<uint>{1769957,1769958,1770470,1770471,1770736,1770746};
        var cityItems=new HashSet<string>();
        foreach(var sid in cityShops){
            if(!shops.TryGetRow(sid, out var s))continue;
            foreach(var e in s.Item){
                var rw=e.ReceiveItems.FirstOrDefault().Item.RowId; if(rw==0)continue;
                bool onlyCity=true;
                foreach(var kv in rank) if(kv.Key.Item2==rw && !cityShops.Contains(kv.Key.Item1)) onlyCity=false;
                if(onlyCity){ var nm=items.GetRowOrDefault(rw)?.Name.ExtractText(); if(nm!=null) cityItems.Add(nm); }
            }
        }

        foreach(var exp in new[]{"暁月","漆黒","黄金"}){
            foreach(JArray tbl in json[exp]){
                var hdr=tbl[0].ToObject<List<string>>();
                int ci=hdr.FindIndex(h=>h.Contains("ランク")); if(ci<0)continue;
                int ni=hdr.FindIndex(h=>h.Contains("アイテム名")); if(ni<0)continue;
                foreach(JArray row in tbl.Skip(1)){
                    var cells=row.ToObject<List<string>>();
                    if(cells.Count<=Math.Max(ci,ni))continue;
                    var nm=cells[ni].Trim(); var rk=cells[ci].Trim();
                    if(!uint.TryParse(rk, out var want)){skip++;continue;}
                    foreach(var one in nm.Split(new[]{' ','　'}, StringSplitOptions.RemoveEmptyEntries)){
                        var key=byName.Keys.FirstOrDefault(k=>k==one || one.StartsWith(k) || k.StartsWith(one.Split('（','(')[0]));
                        if(key==null){skip++;continue;}
                        if(cityItems.Contains(key)){skip++;break;}   // 都市品は別概念
                        var got=byName[key];
                        if(got==want)ok++; else{ng++; mis.Add($"{exp} {key,-28} 実装={got} サイト={want}");}
                        break;
                    }
                }
            }
        }
        Console.WriteLine($"一致 {ok} / 不一致 {ng} / 対象外 {skip}（都市品・ランク未記載を含む）");
        foreach(var m in mis.Take(15)) Console.WriteLine("  × "+m);
    }
}
