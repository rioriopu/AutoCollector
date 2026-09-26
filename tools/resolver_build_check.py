# ExchangeResolver.BeginBuild を毎フレーム呼んだときに索引が完成するかを確かめる。
#
# 画面（PresetTab）は Draw のたびに BeginBuild を呼ぶ。
# 早期 return が効かないと、毎フレーム作りかけを捨てて最初からやり直し、
# 永久に完成しない。

NOT_STARTED, SCAN_SHOPS, SCAN_NPCS, RESOLVE_LOC, COMPLETED, FAILED = range(6)
NAMES = {NOT_STARTED:"NotStarted", SCAN_SHOPS:"ScanningShops", SCAN_NPCS:"ScanningNpcs",
         RESOLVE_LOC:"ResolvingLocations", COMPLETED:"Completed", FAILED:"Failed"}

class Resolver:
    def __init__(self, with_fix):
        self.with_fix = with_fix
        self.stage = NOT_STARTED
        self.target = 0
        self.cache = set()
        self.restarts = 0

    def begin(self, currency):
        if currency != 0 and currency in self.cache:
            if self.target == currency and self.stage == COMPLETED:
                return
            self.target = currency; self.stage = COMPLETED
            return
        # ここが今回入れた早期 return
        if (self.with_fix and currency != 0 and self.target == currency
                and self.stage in (SCAN_SHOPS, SCAN_NPCS, RESOLVE_LOC)):
            return
        self.restarts += 1
        self.target = currency
        self.stage = FAILED if currency == 0 else SCAN_SHOPS

    def tick(self):
        """1フレームで1段だけ進む（実装と同じ）。"""
        if self.stage == SCAN_SHOPS:   self.stage = SCAN_NPCS
        elif self.stage == SCAN_NPCS:  self.stage = RESOLVE_LOC
        elif self.stage == RESOLVE_LOC:
            self.stage = COMPLETED; self.cache.add(self.target)

CURRENCY = 41784  # クラフタースクリップ:紫貨 のつもり

for with_fix in (False, True):
    r = Resolver(with_fix)
    label = "修正後" if with_fix else "修正前"
    for frame in range(30):          # 30フレーム＝約0.5秒
        r.begin(CURRENCY)            # 画面が毎フレーム呼ぶ
        r.tick()
        if r.stage == COMPLETED:
            print(f"{label}: {frame+1} フレームで完成（やり直し {r.restarts} 回）")
            break
    else:
        print(f"{label}: 30 フレーム回しても完成せず（やり直し {r.restarts} 回・いまの段階 {NAMES[r.stage]}）")

print()
print("=== 複数プリセットを交互に開く場合 ===")
# 実際の PresetTab は、構築済みでない通貨に対しては IsBuiltFor で弾き、
# 構築中なら進捗バーを出して return する（BeginBuild を呼ばない）。
# つまり「別の通貨を開いたせいで作りかけが捨てられる」ことは起きない。
# ここではその作法を再現して確かめる。
A, B = 41784, 33913

def draw(r, currency):
    """PresetTab の作法を再現する。"""
    if currency in r.cache:
        r.begin(currency)        # 構築済み → 向け直すだけ
        return
    if r.target == currency and r.stage in (SCAN_SHOPS, SCAN_NPCS, RESOLVE_LOC):
        return                   # 構築中 → 進捗を出して何もしない
    # ここは「読み込む」ボタンを押したときだけ。自動では呼ばない。

for with_fix in (False, True):
    r = Resolver(with_fix); label = "修正後" if with_fix else "修正前"
    r.begin(A)                   # A のプリセットで読み込みボタンを押した
    done = None
    for frame in range(60):
        draw(r, A if frame % 2 == 0 else B)
        r.tick()
        if A in r.cache:
            done = frame + 1; break
    print(f"{label}: " + (f"A は {done} フレームで完成（やり直し {r.restarts} 回）"
          if done else f"60 フレームでも完成せず（やり直し {r.restarts} 回）"))

print()
print("※ 実際の画面は IsBuiltFor で弾くため、交互に開いても作りかけは捨てられない。")
