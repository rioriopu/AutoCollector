# -*- coding: utf-8 -*-
"""解放条件による絞り込みの机上検証。C# の FilterByUnlock / BuildCitySampleZones と同じ手順を踏む。"""
import json, io

links = json.load(io.open(r'C:\ソース\AutoCollector\AutoCollector\Data\npc_shop_links.json', encoding='utf-8'))['links']
city_npcs = {l['npcId'] for l in links if l['isCityShop']}
map_links = [l for l in links if not l['isCityShop'] and l['territoryId'] and l['shops']]

# BuildCitySampleZones と同じ：ショップ番号がいちばん近いマップを相棒にする
sample = {}
for c in (l for l in links if l['isCityShop'] and l['shops']):
    cs = c['shops'][0]
    nearest = min(map_links, key=lambda m: min(abs(s-cs) for s in m['shops']))
    sample[c['npcId']] = (nearest['territoryId'], nearest['npcName'])

print('=== 都市の交易商と、判定に使うマップ ===')
name = {l['npcId']: l['npcName'] for l in links}
# 拡張の正解（ショップ番号帯から）
exp_of = lambda s: '漆黒' if s < 1770000 else ('暁月' if s < 1770700 else '黄金')
ok = 0
for cid, (terr, mname) in sample.items():
    c = next(l for l in links if l['npcId'] == cid)
    m = next(l for l in map_links if l['territoryId'] == terr)
    same = exp_of(c['shops'][0]) == exp_of(m['shops'][0])
    ok += same
    print(f"  {'OK ' if same else '×  '}{name[cid]:<22} → {mname}({terr}) "
          f"[{exp_of(c['shops'][0])} / {exp_of(m['shops'][0])}]")
print(f"  同じ拡張になった: {ok}/{len(sample)}\n")

FATE_ZONES = {'漆黒':[813,814,815,816,817,818], '暁月':[956,957,958,959,960,961], '黄金':[1187,1188,1189,1190,1191,1192]}

def filter_by_unlock(cands, ranks):
    """FilterByUnlock と同じ。cands=[(npcId, territoryId)], ranks={terr:(cur,max)}"""
    city = [c for c in cands if c[0] in city_npcs]
    if not city: return cands, '都市が候補に無い→絞らない'
    def unlocked(terr):
        grp = next((z for z in FATE_ZONES.values() if terr in z), None)
        if grp is None: return False
        if not ranks: return False   # 読めていない→未解放側に倒す
        return all(terr2 in ranks and ranks[terr2][0] >= ranks[terr2][1] for terr2 in grp)
    unl = any(unlocked(c[1]) for c in cands if c[0] not in city_npcs)
    if not unl:
        unl = any(sample.get(c[0]) and unlocked(sample[c[0]][0]) for c in city)
    if unl: return city, '全マップ最大→都市だけ'
    others = [c for c in cands if c[0] not in city_npcs]
    return (others, '未達成→都市を外す') if others else (cands, '都市しか無い→絞らない')

# 検証ケース
CRYS, BERYL = 1027998, 1049082
LAKE, ORCO = 813, 1187
cases = [
    ('漆黒 全部最大・候補=マップ+都市', [(1027385,LAKE),(CRYS,819)], {z:(6,6) for z in FATE_ZONES['漆黒']}, [CRYS]),
    ('漆黒 1つ未達成',                  [(1027385,LAKE),(CRYS,819)], {**{z:(6,6) for z in FATE_ZONES['漆黒']}, 818:(5,6)}, [1027385]),
    ('黄金 全部最大',                   [(1048628,ORCO),(BERYL,1186)], {z:(6,6) for z in FATE_ZONES['黄金']}, [BERYL]),
    ('黄金 未達成',                     [(1048628,ORCO),(BERYL,1186)], {z:(3,6) for z in FATE_ZONES['黄金']}, [1048628]),
    ('画面未オープン(ranks空)',          [(1048628,ORCO),(BERYL,1186)], {}, [1048628]),
    ('都市でしか扱わない品・全部最大',    [(BERYL,1186)], {z:(6,6) for z in FATE_ZONES['黄金']}, [BERYL]),
    ('都市でしか扱わない品・未達成',      [(BERYL,1186)], {z:(1,6) for z in FATE_ZONES['黄金']}, [BERYL]),
    ('シェアFATE無関係の通貨',           [(9999,132)], {}, [9999]),
]
print('=== 絞り込みの検証 ===')
bad = 0
for label, cands, ranks, expect in cases:
    got, why = filter_by_unlock(cands, ranks)
    gids = [c[0] for c in got]
    good = gids == expect
    bad += not good
    print(f"  {'OK ' if good else '×  '}{label:<30} → {[name.get(i,i) for i in gids]}  ({why})")
print(f"\n{'すべて期待どおり' if bad==0 else f'{bad}件ちがう'}")
