# 退避の時間配分が破綻していないか検算する。
#
# 退避は3段で進む：
#   ① 乗れるのを待つ（RetreatMountPatience / CollectRetreatMountPatience）
#   ② 乗れたが飛べないとき、離陸の合図を送り直す（RetreatLiftRetry × 回数）
#   ③ それでも駄目なら歩いて円の外へ出る
# ①+② が全体の上限（RetreatTimeout / CollectRetreatTimeout）を食い潰すと、
# ③へ落ちる前に時間切れになり「円の中から次を探す」で終わってしまう。

MOUNT = {"通常の FATE": 20, "納品 FATE": 8}   # RetreatMountPatience / Collect...
TOTAL = {"通常の FATE": 30, "納品 FATE": 15}   # RetreatTimeout / Collect...
LIFT_RETRY = 2   # RetreatLiftRetry（秒）
LIFT_MAX   = 1   # MaxRetreatLiftAttempts

# 歩きに最低これだけは残したい（円の外まで数十m走る猶予）
MIN_WALK = 3

print("区分          乗る待ち  離陸の粘り  最悪の合計  全体上限  歩く猶予")
ok = True
for name in MOUNT:
    lift  = LIFT_RETRY * (LIFT_MAX + 1)
    worst = MOUNT[name] + lift
    walk  = TOTAL[name] - worst
    good  = walk >= MIN_WALK
    ok = ok and good
    print(f"{name:<14}{MOUNT[name]:>6}秒{lift:>10}秒{worst:>10}秒{TOTAL[name]:>9}秒{walk:>8}秒  "
          + ("OK" if good else f"NG（{MIN_WALK}秒未満）"))

print()
for name in MOUNT:
    assert MOUNT[name] < TOTAL[name], f"{name}: 乗る待ちが全体の上限以上だと待った意味が無い"
assert ok, "歩きに落ちる前に全体の上限が来る"
print("すべて整合")
