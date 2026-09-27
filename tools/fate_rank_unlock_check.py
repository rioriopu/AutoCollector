# -*- coding: utf-8 -*-
"""修正後の IsCityShopUnlocked の机上検証。シートの上限を使う。"""
SHEET_MAX = {**{z:3 for z in [813,814,815,816,817,818,956,957,958,959,960,961]},
             **{z:4 for z in [1187,1188,1189,1190,1191,1192]}}
G = {'漆黒':[813,814,815,816,817,818],'暁月':[956,957,958,959,960,961],
     '黄金':[1187,1188,1189,1190,1191,1192]}

def unlocked(group, ranks):
    ing=[(t,ranks[t]) for t in group if t in ranks]
    if len(ing)!=len(group): return False,'一部読めない'
    if all(t in SHEET_MAX for t,_ in ing):
        return all(c>=SHEET_MAX[t] for t,(c,_) in ing),'シートの上限で判定'
    if all(m>0 for _,(_,m) in ing):
        return all(c>=m for _,(c,m) in ing),'エージェントのMaxRank'
    return False,'どちらも取れない'

cases=[
 ('黄金 全部RANK4・MaxRank欠損', G['黄金'], {t:(4,0) for t in G['黄金']}, True),
 ('暁月 全部RANK3・MaxRank欠損', G['暁月'], {t:(3,0) for t in G['暁月']}, True),
 ('漆黒 全部RANK3・MaxRank欠損', G['漆黒'], {t:(3,0) for t in G['漆黒']}, True),
 ('黄金 全部RANK3(未達)・欠損',   G['黄金'], {t:(3,0) for t in G['黄金']}, False),   # ★旧版は誤ってTrue
 ('黄金 1つだけRANK3',          G['黄金'], {**{t:(4,0) for t in G['黄金']}, 1189:(3,0)}, False),
 ('黄金 全部RANK2・欠損',        G['黄金'], {t:(2,0) for t in G['黄金']}, False),   # ★旧版は誤ってTrue
 ('読めていない',                G['黄金'], {}, False),
]
bad=0
for label,g,r,want in cases:
    got,why=unlocked(g,r); ok=(got==want); bad+=not ok
    print(f'  {"OK " if ok else "×  "}{label:<28} → {got}  ({why})')
print(f'\n{"すべて期待どおり" if bad==0 else f"{bad}件ちがう"}')
print('\n★ 旧版（最高ランクを上限とみなす近似）では、')
print('   「全部RANK3(未達)」「全部RANK2」を誤って解放と判定していた。')
print('   シートの上限を使うことで、この誤りが消えた。')
