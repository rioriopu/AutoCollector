# HomeTownService.ChooseMethod と同じ判定を検算する。
# 「行き先＝ホームタウンならデジョン、違えばテレポ」の確認。
#
# エリア番号は GbrVentureRelay が Aetheryte シートを実測したもの
# （IsAetheryte かつ AethernetGroup != 0 の行、ver 2026.09.15）。

LIMSA, GRIDANIA, ULDAH, CRYSTARIUM, KUGANE, SOLUTION9 = 129, 132, 130, 819, 628, 1186
NAMES = {
    LIMSA: "リムサ・ロミンサ", GRIDANIA: "グリダニア", ULDAH: "ウルダハ",
    CRYSTARIUM: "クリスタリウム", KUGANE: "クガネ", SOLUTION9: "ソリューション・ナイン",
    1187: "オルコ・パチャ(FATEのマップ)",
}

RETURN, TELEPORT, ALREADY = "デジョン", "テレポ", "もう着いている"

def choose(here, destination, home):
    """FateApproach 側と同じ順序で判定する。"""
    if here == destination:
        return ALREADY
    # home が 0（読めない）ならテレポに倒す。デジョンは行き先を選べないため、
    # ホームタウンが不明なまま撃つと意図しない街へ飛ぶ。
    return RETURN if home != 0 and home == destination else TELEPORT

def show(here, destination, home, expect):
    got = choose(here, destination, home)
    ok = got == expect
    print(("OK  " if ok else "NG  ")
          + f"いま={NAMES.get(here,here)} / 行き先={NAMES.get(destination,destination)}"
          + f" / ホーム={NAMES.get(home,'不明') if home else '不明'} → {got}")
    assert ok, f"期待 {expect} だが {got}"

print("=== ホームタウンと一致 → デジョン ===")
show(1187, LIMSA, LIMSA, RETURN)          # 既定の想定ケース
show(1187, ULDAH, ULDAH, RETURN)
show(1187, SOLUTION9, SOLUTION9, RETURN)

print("\n=== ホームタウンと不一致 → テレポ ===")
show(1187, LIMSA, ULDAH, TELEPORT)        # ホームがウルダハなのにリムサ指定
show(1187, GRIDANIA, LIMSA, TELEPORT)
show(1187, KUGANE, GRIDANIA, TELEPORT)

print("\n=== もう着いている ===")
show(LIMSA, LIMSA, LIMSA, ALREADY)        # デジョンより先に判定される
show(ULDAH, ULDAH, LIMSA, ALREADY)

print("\n=== ホームタウンが読めない → テレポに倒す ===")
# デジョンは行き先を選べない。ホームが不明なまま撃つと意図しない街へ飛ぶので、
# 必ずテレポ（行き先を指定できる）を選ぶ。
show(1187, LIMSA, 0, TELEPORT)
show(1187, SOLUTION9, 0, TELEPORT)

print("\n=== 既定値の当てはめ ===")
# 設定が 0（未設定）なら、一覧に出ていればリムサを当てはめる。
DEFAULT = LIMSA
def apply_default(configured, accessible):
    if configured != 0:
        return configured, "すでに設定済み"
    if not accessible:
        return 0, "一覧が読めない（コンテンツ内など）ので何もしない"
    if DEFAULT in accessible:
        return DEFAULT, "リムサを当てはめた"
    return 0, "リムサが一覧に無いので何もしない"

for configured, accessible, expect in [
    (0,     [LIMSA, ULDAH],  LIMSA),
    (ULDAH, [LIMSA, ULDAH],  ULDAH),   # 利用者の設定を上書きしない
    (0,     [],              0),       # 一覧が空 → 決めつけない
    (0,     [ULDAH, KUGANE], 0),       # リムサ未解放 → 飛べない値を書かない
]:
    got, why = apply_default(configured, accessible)
    ok = got == expect
    print(("OK  " if ok else "NG  ")
          + f"設定={NAMES.get(configured,'未設定') if configured else '未設定'}"
          + f" / 解放={[NAMES[a] for a in accessible]} → {NAMES.get(got,'未設定') if got else '未設定'}（{why}）")
    assert ok

print("\nすべて合格")
