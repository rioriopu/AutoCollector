# Auto Collector のプラグインアイコンを生成する。
# PIL が無い環境なので、zlib と struct だけで PNG を書き出す。
# アンチエイリアスは 3x3 のスーパーサンプリングで得る。
#
# 図案: 自動処理を表す環状の矢印の中に、通貨（トームストーン風の石板）と
#       交換で得るアイテム（薬瓶）を並べる。「通貨を自動でアイテムに換える」を表す。

import math
import struct
import sys
import zlib

SIZE = 512
SS = 3
W = SIZE * SS
S = SS

CX = CY = W / 2.0


def lerp(a, b, t):
    return a + (b - a) * t


def mix(c1, c2, t):
    t = max(0.0, min(1.0, t))
    return (lerp(c1[0], c2[0], t), lerp(c1[1], c2[1], t), lerp(c1[2], c2[2], t))


# ---- 色 ----
BG_TOP = (35, 46, 66)
BG_BOTTOM = (19, 26, 38)
RING = (77, 214, 168)
RING_DARK = (46, 160, 124)

FRAME_HI = (232, 186, 96)      # 石板の金枠
FRAME_LO = (150, 104, 28)
FACE_TOP = (38, 56, 92)        # 石板の面
FACE_BOTTOM = (20, 31, 54)
GLYPH = (255, 226, 150)        # 刻まれた紋様
GLYPH_GLOW = (255, 190, 90)

# ---- 寸法 ----
CORNER = 96 * S
RING_R = 176 * S
RING_T = 30 * S

# 石板（通貨）。環の内側に収まるよう左へ寄せる。
TAB_CX = CX - 78 * S
TAB_CY = CY + 4 * S
TAB_W = 112 * S
TAB_H = 152 * S
TAB_BEVEL = 30 * S
FRAME_T = 12 * S

# 薬瓶（交換で得るアイテム）。右へ寄せる。
POT_CX = CX + 78 * S
BULB_CY = CY + 30 * S
BULB_R = 50 * S
NECK_HALF = 17 * S
NECK_TOP = CY - 52 * S
CORK_TOP = CY - 74 * S
CORK_HALF = 24 * S
LIQUID_Y = CY - 4 * S           # ここより下が中身

GLASS = (196, 226, 236)
GLASS_DARK = (140, 176, 192)
# 中身は環（緑）と同化しないよう暖色にする。
LIQUID_HI = (255, 146, 120)
LIQUID_LO = (198, 56, 62)
CORK = (150, 104, 62)
CORK_DARK = (108, 70, 38)

GAP_START = math.radians(52)
GAP_END = math.radians(128)


def rounded_rect_alpha(x, y):
    r = CORNER
    for cx, cy in ((r, r), (W - r, r), (r, W - r), (W - r, W - r)):
        if (x < r or x > W - r) and (y < r or y > W - r):
            if (x - cx) ** 2 + (y - cy) ** 2 <= r * r:
                return 1.0
            # 角の外側かどうかは、対応する角だけで判定する
            if abs(x - cx) < r and abs(y - cy) < r:
                return 0.0
    return 1.0


def octagon(x, y, w, h, bevel, cx=None, cy=None):
    """面取りした矩形（八角形）の内側なら True。"""
    dx = abs(x - (TAB_CX if cx is None else cx))
    dy = abs(y - (TAB_CY if cy is None else cy))
    if dx > w / 2 or dy > h / 2:
        return False
    # 四隅を 45 度で落とす
    return dx + dy <= (w / 2 + h / 2) - bevel


def arrow_triangle():
    a = GAP_START
    px = CX + math.cos(a) * RING_R
    py = CY - math.sin(a) * RING_R
    tx, ty = math.sin(a), math.cos(a)
    nx, ny = math.cos(a), -math.sin(a)
    tip = (px + tx * RING_T * 1.9, py + ty * RING_T * 1.9)
    left = (px - nx * RING_T * 1.25, py - ny * RING_T * 1.25)
    right = (px + nx * RING_T * 1.25, py + ny * RING_T * 1.25)
    return tip, left, right


TIP, LEFT, RIGHT = arrow_triangle()


def in_triangle(x, y, a, b, c):
    def sign(p1, p2, p3):
        return (p1[0] - p3[0]) * (p2[1] - p3[1]) - (p2[0] - p3[0]) * (p1[1] - p3[1])

    d1 = sign((x, y), a, b)
    d2 = sign((x, y), b, c)
    d3 = sign((x, y), c, a)
    neg = (d1 < 0) or (d2 < 0) or (d3 < 0)
    pos = (d1 > 0) or (d2 > 0) or (d3 > 0)
    return not (neg and pos)


def in_ring(x, y):
    dx, dy = x - CX, y - CY
    d = math.hypot(dx, dy)
    if abs(d - RING_R) > RING_T / 2:
        return False
    ang = math.atan2(-dy, dx) % (2 * math.pi)
    if GAP_START <= ang <= GAP_END:
        return False
    return True


# ---- 石板に刻む紋様 ----
# 上に菱形、下に長さの違う横棒 2 本。石板に刻まれた印を表す。
# 円と縦線の組み合わせは性別記号に見えてしまうため使わない。
DIAMOND_C = (TAB_CX, TAB_CY - 36 * S)
DIAMOND_R = 22 * S             # 中心から頂点までの距離
BAR_H = 10 * S


def glyph_distance(x, y):
    """紋様までの距離。0 以下なら紋様の内側。"""
    # 菱形（塗り）。L1 距離で表す。
    best = (abs(x - DIAMOND_C[0]) + abs(y - DIAMOND_C[1]) - DIAMOND_R) * 0.7071

    # 横棒 2 本
    for by, half in ((TAB_CY + 14 * S, 30 * S), (TAB_CY + 42 * S, 19 * S)):
        dx = max(0.0, abs(x - TAB_CX) - half)
        dy = abs(y - by) - BAR_H / 2
        d = math.hypot(dx, max(0.0, dy)) if dx > 0 or dy > 0 else max(dx, dy)
        best = min(best, d)

    return best


def potion_part(x, y):
    """薬瓶の色を返す。瓶の外なら None。"""
    dx = x - POT_CX

    # コルク
    if CORK_TOP <= y <= NECK_TOP + 6 * S and abs(dx) <= CORK_HALF:
        return mix(CORK, CORK_DARK, (dx / CORK_HALF) * 0.5 + 0.5)

    # 首
    if NECK_TOP <= y <= BULB_CY and abs(dx) <= NECK_HALF:
        inside_bulb = dx * dx + (y - BULB_CY) ** 2 <= BULB_R * BULB_R
        if not inside_bulb:
            base = GLASS if y < LIQUID_Y else mix(LIQUID_HI, LIQUID_LO, 0.4)
            return mix(base, GLASS_DARK, (dx / NECK_HALF) * 0.5 + 0.5) if y < LIQUID_Y else base

    # 胴
    d2 = dx * dx + (y - BULB_CY) ** 2
    if d2 <= BULB_R * BULB_R:
        if y >= LIQUID_Y:
            # 中身。下へ行くほど濃くする。
            t = (y - LIQUID_Y) / (BULB_CY + BULB_R - LIQUID_Y)
            col = mix(LIQUID_HI, LIQUID_LO, t)
        else:
            col = GLASS

        # 左上の照り返し
        hx, hy = POT_CX - 20 * S, BULB_CY - 22 * S
        if (x - hx) ** 2 + (y - hy) ** 2 <= (13 * S) ** 2:
            col = mix(col, (255, 255, 255), 0.55)

        # 縁を少し暗くして丸みを出す
        edge = math.sqrt(d2) / BULB_R
        if edge > 0.82:
            col = mix(col, GLASS_DARK, (edge - 0.82) / 0.18 * 0.5)

        return col

    return None


rows = []
for py in range(SIZE):
    row = bytearray()
    for px in range(SIZE):
        rs = gs = bs = as_ = 0.0

        for sy in range(SS):
            for sx in range(SS):
                x = px * SS + sx + 0.5
                y = py * SS + sy + 0.5

                if rounded_rect_alpha(x, y) == 0.0:
                    continue

                col = mix(BG_TOP, BG_BOTTOM, y / W)

                # 環と矢じり
                if in_ring(x, y) or in_triangle(x, y, TIP, LEFT, RIGHT):
                    dd = math.hypot(x - CX, y - CY)
                    shade = (dd - (RING_R - RING_T)) / (RING_T * 2)
                    col = mix(RING, RING_DARK, shade)

                # 石板（通貨）
                if octagon(x, y, TAB_W, TAB_H, TAB_BEVEL):
                    inner = octagon(
                        x, y,
                        TAB_W - FRAME_T * 2,
                        TAB_H - FRAME_T * 2,
                        max(0.0, TAB_BEVEL - FRAME_T * 1.4),
                    )

                    if not inner:
                        # 金枠。左上を明るく、右下を暗くして厚みを出す。
                        t = ((x - TAB_CX) / TAB_W + (y - TAB_CY) / TAB_H) + 0.5
                        col = mix(FRAME_HI, FRAME_LO, t)
                    else:
                        # 石板の面
                        t = (y - (TAB_CY - TAB_H / 2)) / TAB_H
                        col = mix(FACE_TOP, FACE_BOTTOM, t)

                        # 紋様と、その周囲のにじみ
                        gd = glyph_distance(x, y)
                        if gd <= 0:
                            col = GLYPH
                        elif gd < 7 * S:
                            col = mix(GLYPH_GLOW, col, gd / (7 * S))

                # 薬瓶（交換で得るアイテム）
                pot = potion_part(x, y)
                if pot is not None:
                    col = pot

                rs += col[0]
                gs += col[1]
                bs += col[2]
                as_ += 255.0

        n = SS * SS
        row += bytes((
            int(rs / n + 0.5),
            int(gs / n + 0.5),
            int(bs / n + 0.5),
            int(as_ / n + 0.5),
        ))
    rows.append(bytes(row))


def png(path, width, height, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    out = b"\x89PNG\r\n\x1a\n"
    out += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    out += chunk(b"IDAT", zlib.compress(raw, 9))
    out += chunk(b"IEND", b"")
    open(path, "wb").write(out)
    return len(out)


size = png(sys.argv[1], SIZE, SIZE, rows)
print(f"generated: {sys.argv[1]} ({size} bytes, {SIZE}x{SIZE})")
