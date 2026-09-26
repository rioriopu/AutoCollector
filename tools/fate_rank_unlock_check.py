# -*- coding: utf-8 -*-
"""IsCityShopUnlocked / DescribeProgress の机上検証。"""
GROUPS = {'漆黒':[813,814,815,816,817,818], '暁月':[956,957,958,959,960,961],
          '黄金':[1187,1188,1189,1190,1191,1192]}

def unlocked(group, ranks):
    ing = [ranks[t] for t in group if t in ranks]
    if len(ing) != len(group): return False, '一部読めない'
    if all(m > 0 for _, m in ing):
        return all(c >= m for c, m in ing), 'MaxRankで判定'
    highest = max(c for c, _ in ing)
    if highest == 0: return False, '全部0'
    return all(c >= highest for c, _ in ing), '最高ランクを上限とみなす'

def describe(group, ranks):
    ing = [ranks[t] for t in group if t in ranks]
    if len(ing) == len(group) and all(m > 0 for _, m in ing):
        n = sum(1 for c, m in ing if c >= m)
    else:
        highest = max((c for c, _ in ing), default=0)
        n = 0 if highest == 0 else sum(1 for c, _ in ing if c >= highest)
    return f'{n}/{len(group)}'

cases = [
 # 実機で起きた状態：全マップ COMPLETE だが MaxRank が 0
 ('黄金 全部RANK4・MaxRank欠損', GROUPS['黄金'], {t:(4,0) for t in GROUPS['黄金']}, True),
 ('暁月 全部RANK3・MaxRank欠損', GROUPS['暁月'], {t:(3,0) for t in GROUPS['暁月']}, True),
 ('漆黒 全部RANK3・MaxRank欠損', GROUPS['漆黒'], {t:(3,0) for t in GROUPS['漆黒']}, True),
 # MaxRank が読める正常系
 ('黄金 全部4/4',              GROUPS['黄金'], {t:(4,4) for t in GROUPS['黄金']}, True),
 ('黄金 1つだけ3/4',           GROUPS['黄金'], {**{t:(4,4) for t in GROUPS['黄金']}, 1189:(3,4)}, False),
 # MaxRank欠損＋未達成（誤判定しうるケース）
 ('黄金 全部RANK2・欠損(誤判定)', GROUPS['黄金'], {t:(2,0) for t in GROUPS['黄金']}, True),
 ('黄金 バラつき・欠損',        GROUPS['黄金'], {**{t:(4,0) for t in GROUPS['黄金']}, 1189:(2,0)}, False),
 ('読めていない',               GROUPS['黄金'], {}, False),
]
bad = 0
for label, g, r, expect in cases:
    got, why = unlocked(g, r)
    ok = got == expect
    bad += not ok
    print(f"  {'OK ' if ok else '×  '}{label:<28} → {got}  {describe(g,r):>4}マップ  ({why})")
print(f"\n{'すべて期待どおり' if bad==0 else f'{bad}件ちがう'}")
print("\n※「全部RANK2・欠損」だけは true になるが、これは承知の上の割り切り。")
print("  上限が読めない以上、全マップ同ランクを最大と見なすしかない。")
print("  誤っても都市で空振りするだけで、逆（永久に解放されない）より実害が小さい。")
