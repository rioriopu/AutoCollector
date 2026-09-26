# FateApproach.IsDescentAcceptable と同じ判定を、vnavmesh が返しうる
# 経路の形で検算する。C05/C06（垂直落下を合格にしない）の確認。
import math

LANDING_HEIGHT = 3.0
LANDING_FLAT = 2.0
MAX_TAIL = 12.0
MAX_TAIL_FLAT = 3.0

def flat(a, b):
    return math.hypot(a[0]-b[0], a[2]-b[2])

def acceptable(player, waypoints, landing):
    drop = player[1] - landing[1]
    tflat = flat(player, landing)
    if drop <= LANDING_HEIGHT or tflat <= LANDING_FLAT:
        return True, "近いので形を問わない"
    tail = 0.0
    at = 0.0
    for p in waypoints:
        fl = flat(p, landing)
        dl = p[1] - landing[1]
        if fl <= MAX_TAIL_FLAT and dl > tail:
            tail, at = dl, fl
    if tail > MAX_TAIL:
        return False, f"中央上空から {tail:.0f}m の垂直落下（水平の残り {at:.1f}m）"
    return True, f"垂直落下は {tail:.0f}m まで"

L = (0.0, 0.0, 0.0)
P = (60.0, 21.84, 0.0)   # 外周上空点

print("=== 合格させたい形：斜めに降りる ===")
diag = [(45,16.38,0),(30,10.92,0),(15,5.46,0),(0,0,0)]
ok, why = acceptable(P, [tuple(map(float,w)) for w in diag], L)
print(f"  斜め（指示書の例）→ {'合格' if ok else '不合格'}: {why}")
assert ok

print("\n=== 弾きたい形：中央上空まで水平 → 垂直落下 ===")
# vnavmesh が返しうる最悪の形。水平に飛んでから真下へ落ちる。
vert = [(40,21.84,0),(20,21.84,0),(0,21.84,0),(0,0,0)]
ok, why = acceptable(P, [tuple(map(float,w)) for w in vert], L)
print(f"  水平→垂直（21.8m 落下）→ {'合格' if ok else '不合格'}: {why}")
assert not ok, "垂直落下を弾けていない"

# もっと高いところから
P2 = (60.0, 60.0, 0.0)
vert2 = [(30,60,0),(0,60,0),(0,0,0)]
ok, why = acceptable(P2, [tuple(map(float,w)) for w in vert2], L)
print(f"  水平→垂直（60m 落下）→ {'合格' if ok else '不合格'}: {why}")
assert not ok

print("\n=== 境目 ===")
# 末尾の垂直落下がちょうど 12m
b1 = [(30,20,0),(0,12,0),(0,0,0)]
ok, why = acceptable((60.0,20.0,0.0), [tuple(map(float,w)) for w in b1], L)
print(f"  末尾の落下 12m（上限ちょうど）→ {'合格' if ok else '不合格'}: {why}")
assert ok, "12m は許す"
b2 = [(30,20,0),(0,13,0),(0,0,0)]
ok, why = acceptable((60.0,20.0,0.0), [tuple(map(float,w)) for w in b2], L)
print(f"  末尾の落下 13m（上限超え）→ {'合格' if ok else '不合格'}: {why}")
assert not ok

print("\n=== 誤検知しないか ===")
# 地表すぐ上まで斜めに来て、最後の数メートルだけ落ちる（ふつうの着地）
land = [(30,10,0),(10,4,0),(2,2.5,0),(0,0,0)]
ok, why = acceptable((60.0,21.84,0.0), [tuple(map(float,w)) for w in land], L)
print(f"  最後 2.5m だけ落下（ふつうの着地）→ {'合格' if ok else '不合格'}: {why}")
assert ok

# もともと近い（形を問う意味がない）
ok, why = acceptable((1.0, 2.0, 0.0), [(0.0,0.0,0.0)], L)
print(f"  もともと着地点の近く → {'合格' if ok else '不合格'}: {why}")
assert ok

# 経路が1点だけ（目的地のみ）で、遠くから
ok, why = acceptable(P, [(0.0,0.0,0.0)], L)
print(f"  目的地1点だけ（遠方）→ {'合格' if ok else '不合格'}: {why}")
# 目的地自身は dl=0 なので tail=0。Validate 側の「1点は弾く」で落ちる想定。
print("    ※ これは Validate の『1点だけは弾く』が拾う（形の検査では拾えない）")

print("\n=== 段分割の中継点 ===")
for t in (0.25, 0.5, 0.75, 1.0):
    x = P[0] + (L[0]-P[0])*t
    y = P[1] + (L[1]-P[1])*t
    z = P[2] + (L[2]-P[2])*t
    print(f"  t={t:.2f} → ({x:.1f}, {y:.2f}, {z:.1f})")

print("\nすべて合格")
