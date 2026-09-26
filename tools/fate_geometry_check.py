# 指示書24 の C01（外周幾何）と C05（降下形状）を、実装と同じ式で検算する。
# FateApproach.cs の PlanEntryPoint / HeightForEntry と同じ計算をなぞる。
import math

ENTRY_MARGIN = 10.0
DESCENT_ANGLE = 20.0
CLEARANCE = 8.0

def entry_point(centre, radius_raw, player, replan=0, floor_y=None, landing=None, ceiling=None):
    radius = max(radius_raw, 20.0)
    angles = [0.0, 40.0, -40.0, 90.0, -90.0]
    ax = player[0] - centre[0]
    az = player[2] - centre[2]
    if ax*ax + az*az > 1.0:
        ln = math.hypot(ax, az)
        base = (ax/ln, az/ln)
    else:
        base = (1.0, 0.0)   # 向きが決まらないときの既定
    turns = min(replan, len(angles)-1)
    for i in range(turns, len(angles)):
        r = math.radians(angles[i])
        c, s = math.cos(r), math.sin(r)
        d = (base[0]*c - base[1]*s, base[0]*s + base[1]*c)
        dist = radius + ENTRY_MARGIN
        ex = centre[0] + d[0]*dist
        ez = centre[2] + d[1]*dist
        if floor_y is None:
            continue
        horiz = math.hypot(ex - landing[0], ez - landing[2])
        slope = math.tan(math.radians(DESCENT_ANGLE))
        y = max(floor_y + CLEARANCE, landing[1] + slope*horiz)
        if ceiling is not None and y > ceiling:
            if ceiling <= landing[1] + CLEARANCE:
                continue          # 抑えると着地点より低い。この向きは使えない
            y = ceiling
        return (ex, y, ez), angles[i], horiz
    return None, None, None

print("=== C01 外周幾何 ===")
# 指示書の数値例：半径50m、中心と床を原点、角度20度 → E=(60, 21.84, 0)
centre = (0.0, 0.0, 0.0); landing = (0.0, 0.0, 0.0)
E, ang, horiz = entry_point(centre, 50.0, (100.0, 0.0, 0.0), floor_y=0.0, landing=landing)
print(f"半径50/margin10 → 外周点 水平{math.hypot(E[0]-centre[0], E[2]-centre[2]):.2f}m 高さ{E[1]:.3f} 角{ang}")
assert abs(math.hypot(E[0], E[2]) - 60.0) < 1e-6, "外周は60mでなければならない"
assert abs(E[1] - 60.0*math.tan(math.radians(20))) < 1e-3, "高さが20度の式と合わない"
print(f"  指示書の期待値 21.838 との差: {abs(E[1]-21.838):.4f}")

# 方向ゼロ（中心に立っている）
E0, a0, _ = entry_point(centre, 50.0, (0.0, 0.0, 0.0), floor_y=0.0, landing=landing)
print(f"中心に立つ → 外周点 水平{math.hypot(E0[0], E0[2]):.2f}m 角{a0}（既定の向き）")
assert abs(math.hypot(E0[0], E0[2]) - 60.0) < 1e-6

# 半径が小さい（20m下限が効くか）
Es, _, _ = entry_point(centre, 5.0, (50.0,0,0), floor_y=0.0, landing=landing)
print(f"半径5→下限20適用 → 外周点 水平{math.hypot(Es[0], Es[2]):.2f}m（期待30）")
assert abs(math.hypot(Es[0], Es[2]) - 30.0) < 1e-6

# 着地点が中心からずれている
land2 = (15.0, -3.0, 5.0)
E2, _, h2 = entry_point(centre, 50.0, (100.0,0,0), floor_y=-3.0, landing=land2)
print(f"着地点ずれ({land2}) → 外周点{tuple(round(v,2) for v in E2)} 着地まで水平{h2:.2f}m")
assert all(math.isfinite(v) for v in E2), "有限値でなければならない"

# 高低差：床が高い場所
E3, _, _ = entry_point(centre, 50.0, (100.0,0,0), floor_y=40.0, landing=landing)
print(f"外周の床が40 → 高さ{E3[1]:.2f}（床+8=48 と 21.84 の大きい方）")
assert abs(E3[1] - 48.0) < 1e-6

print("\n=== 高度上限のクランプ ===")
E4, a4, _ = entry_point(centre, 50.0, (100.0,0,0), floor_y=0.0, landing=landing, ceiling=31.0)
print(f"上限31 → 高さ{E4[1]:.2f}（21.84 < 31 なので抑えない）")
assert abs(E4[1] - 21.838) < 0.01
E5, a5, _ = entry_point(centre, 50.0, (100.0,0,0), floor_y=0.0, landing=landing, ceiling=15.0)
print(f"上限15 → 高さ{E5[1]:.2f}（21.84 を 15 へ抑える）")
assert abs(E5[1] - 15.0) < 1e-6
# 上限が着地点+clearance以下 → その向きは使えず、次の向きへ
E6, a6, _ = entry_point(centre, 50.0, (100.0,0,0), floor_y=0.0, landing=landing, ceiling=5.0)
print(f"上限5（着地点+8=8 以下）→ 全向き不可: {E6 is None}")
assert E6 is None, "抑えると着地点より低い向きは使えない"

print("\n=== C05 降下形状（補間点） ===")
E, _, _ = entry_point(centre, 50.0, (100.0,0,0), floor_y=0.0, landing=landing)
prev_h, prev_y = math.hypot(E[0], E[2]), E[1]
print(f"  t=0.00  水平{prev_h:6.2f}  高さ{prev_y:6.2f}")
for t in (0.25, 0.5, 0.75, 1.0):
    x = E[0] + (landing[0]-E[0])*t
    y = E[1] + (landing[1]-E[1])*t
    z = E[2] + (landing[2]-E[2])*t
    h = math.hypot(x-landing[0], z-landing[2])
    print(f"  t={t:.2f}  水平{h:6.2f}  高さ{y:6.2f}", end="")
    assert h <= prev_h + 1e-9, "水平距離が増えている"
    assert y <= prev_y + 1e-9, "高度が増えている"
    print("  ✓ 両方とも減少")
    prev_h, prev_y = h, y
assert abs(prev_h) < 1e-9 and abs(prev_y - landing[1]) < 1e-9

print("\n=== F01 退避距離の整合 ===")
RETREAT_MARGIN = 10.0
ARRIVAL = 5.0
def retreat(radius_raw):
    success = max(radius_raw, 20.0) + RETREAT_MARGIN
    target = success + ARRIVAL + RETREAT_MARGIN
    # 経路は目標から ARRIVAL まで近づいた時点で終わりうる
    worst = target - ARRIVAL
    return success, target, worst
for r in (20.0, 50.0, 80.0):
    s, t, w = retreat(r)
    print(f"半径{r:.0f} → 成功基準{s:.0f}m 目標{t:.0f}m 経路終点の最悪値{w:.0f}m", end="")
    assert w >= s, f"経路が終わった地点({w})が成功基準({s})に届かない"
    print("  ✓ 基準を満たす")

print("\nすべて合格")
