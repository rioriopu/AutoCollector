# FateCombatPreset.FindMissing と同じ判定を、BMR が実際に書き出す JSON で検算する。
# 目的：既定値が省略された JSON を「不備」と誤判定しないことの確認。
import json

AT = "BossMod.Autorotation.MiscAI.AutoTarget"
FU = "BossMod.Autorotation.MiscAI.FateUtils"
NM = "BossMod.Autorotation.MiscAI.NormalMovement"
SAM = "BossMod.Autorotation.xan.SAM"

REQUIRED = [
    (FU, "Sync", "Enable"),
    (FU, "Collect", "Disabled"),
    (AT, "FATE", "Enabled"),
    (NM, "Destination", "Pathfind"),
]
FORBIDDEN = [
    (FU, "Handin", "Disabled"),
    (AT, "Retarget", "Hostiles"),
    (AT, "Retarget", "Always"),
    (AT, "General", "Passive"),
    (AT, "Everything", "Enabled"),
    (AT, "CollectFATE", "Enabled"),
]

def find_option(settings, track):
    for e in settings:
        if isinstance(e, dict) and e.get("Track") == track:
            return e.get("Option")
    return None

def short(m):
    return m.rsplit(".", 1)[-1]

def find_missing(text):
    try:
        root = json.loads(text)
    except json.JSONDecodeError:
        return "読める形をしていません"
    modules = root.get("Modules")
    if not isinstance(modules, dict):
        return "モジュールの一覧"
    if SAM not in modules:
        return "ジョブのローテーション"
    for mod, track, opt in REQUIRED:
        s = modules.get(mod)
        if not isinstance(s, list):
            return short(mod)
        if find_option(s, track) != opt:
            return f"{short(mod)} の {track} = {opt}"
    for mod, track, opt in FORBIDDEN:
        s = modules.get(mod)
        if not isinstance(s, list):
            continue
        if find_option(s, track) == opt:
            return f"{short(mod)} の {track} が {opt} になっています"
    return None

def build(at=None, fu=None, nm=None, with_sam=True):
    m = {}
    m[AT] = at if at is not None else []
    m[FU] = fu if fu is not None else []
    m[NM] = nm if nm is not None else []
    if with_sam:
        m[SAM] = []
    return json.dumps({"Name": "AutoCollector FATE", "Modules": m}, ensure_ascii=False)

ok = 0
def case(label, text, expect_none):
    global ok
    got = find_missing(text)
    good = (got is None) if expect_none else (got is not None)
    print(("OK  " if good else "NG  ") + label + (f"  → {got}" if got else "  → 不備なし"))
    assert good, f"想定外: {label} / {got}"
    ok += 1

print("=== BMR が保存し直した形（既定値が省略されている）===")
# これが一番大事。General=Aggressive / Everything=Disabled / Retarget=NoTarget は
# すべて enum の 0 番なので書き出されない。これを不備と読んではいけない。
saved = build(
    at=[{"Track": "FATE", "Option": "Enabled"}],
    fu=[{"Track": "Collect", "Option": "Disabled"}, {"Track": "Sync", "Option": "Enable"}],
    nm=[{"Track": "Destination", "Option": "Pathfind"}],
)
case("既定値が省略された正しいプリセット", saved, True)

print("\n=== こちらが書き出す形（既定値も明示）===")
ours = build(
    at=[{"Track": "General", "Option": "Aggressive"},
        {"Track": "Retarget", "Option": "NoTarget"},
        {"Track": "FATE", "Option": "Enabled"},
        {"Track": "Everything", "Option": "Disabled"},
        {"Track": "Hunt", "Option": "Disabled"},
        {"Track": "Treasure", "Option": "Disabled"}],
    fu=[{"Track": "Handin", "Option": "Enabled"},
        {"Track": "Collect", "Option": "Disabled"},
        {"Track": "Sync", "Option": "Enable"},
        {"Track": "Chocobo", "Option": "Disabled"}],
    nm=[{"Track": "Destination", "Option": "Pathfind"}],
)
case("既定値を明示した自作プリセット", ours, True)

print("\n=== 直すべきもの（不備を検出できるか）===")
case("CollectFATE=Enabled が残った古いプリセット",
     build(at=[{"Track":"FATE","Option":"Enabled"},{"Track":"CollectFATE","Option":"Enabled"}],
           fu=[{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[{"Track":"Destination","Option":"Pathfind"}]), False)
case("Retarget=Hostiles にされた",
     build(at=[{"Track":"FATE","Option":"Enabled"},{"Track":"Retarget","Option":"Hostiles"}],
           fu=[{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[{"Track":"Destination","Option":"Pathfind"}]), False)
case("General=Passive にされた",
     build(at=[{"Track":"FATE","Option":"Enabled"},{"Track":"General","Option":"Passive"}],
           fu=[{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[{"Track":"Destination","Option":"Pathfind"}]), False)
case("Handin=Disabled にされた（納品へ行かない）",
     build(at=[{"Track":"FATE","Option":"Enabled"}],
           fu=[{"Track":"Handin","Option":"Disabled"},{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[{"Track":"Destination","Option":"Pathfind"}]), False)
case("FATE トラックが無い（敵を狙わない）",
     build(at=[], fu=[{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[{"Track":"Destination","Option":"Pathfind"}]), False)
case("NormalMovement の Destination が無い（動かない）",
     build(at=[{"Track":"FATE","Option":"Enabled"}],
           fu=[{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[]), False)
case("ジョブのローテーションが無い",
     build(at=[{"Track":"FATE","Option":"Enabled"}],
           fu=[{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[{"Track":"Destination","Option":"Pathfind"}], with_sam=False), False)

print("\n=== 不備ではないもの（誤検知しないか）===")
# Retarget=Never は周回中に一時方針で入れる値。
# 狙う相手は AutoCollector が決めるので、BMR に選び直させない。
# これを不備と読むと、起動のたびにプリセットを作り直してしまう。
case("Retarget=Never（周回中に入れる値）",
     build(at=[{"Track":"FATE","Option":"Enabled"},{"Track":"Retarget","Option":"Never"}],
           fu=[{"Track":"Collect","Option":"Disabled"},{"Track":"Sync","Option":"Enable"}],
           nm=[{"Track":"Destination","Option":"Pathfind"}]), True)

print("\n=== モジュールの境目をまたがないか（旧実装の穴）===")
# 旧実装は文字列検索だったため、AutoTarget の Collect（存在しない）と
# FateUtils の Collect を区別できなかった。
# ここでは FateUtils の Collect を落とし、AutoTarget 側に同名を置く。
tricky = build(
    at=[{"Track":"FATE","Option":"Enabled"},{"Track":"Collect","Option":"Disabled"}],
    fu=[{"Track":"Sync","Option":"Enable"}],
    nm=[{"Track":"Destination","Option":"Pathfind"}],
)
got = find_missing(tricky)
print(f"AutoTarget 側に Collect、FateUtils 側に無し → {got}")
assert got is not None and "FateUtils" in got, "モジュールを区別できていない"
ok += 1
print("OK  モジュールを正しく区別した")

print("\n=== 壊れた JSON ===")
case("JSON として読めない", "{ぐちゃぐちゃ", False)
case("Modules が無い", '{"Name":"x"}', False)

print(f"\n{ok} 件すべて合格")
