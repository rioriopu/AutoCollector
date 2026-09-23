namespace AutoCollector.Earning;

/// <summary>
/// 中断や復帰の進み具合。
///
/// **1 フレームでは終わらない。**
/// AutoDuty の停止は要求を送ってから実際に止まるまで間があり、
/// 1 秒ごとに送り直しながら待つ。復帰も同じ。
/// だから「やる」ではなく「進める」形にしてある。
/// </summary>
public enum EarnerProgress
{
    /// <summary>まだ途中。次のフレームでもう一度呼ぶ。</summary>
    InProgress,

    /// <summary>終わった。もう呼ばない。</summary>
    Done,

    /// <summary>できなかった。理由を <see cref="EarnerStepResult.Detail"/> に入れる。</summary>
    Failed,
}

/// <summary>
/// 中断や復帰を 1 フレーム進めた結果。
/// </summary>
/// <param name="Progress">進み具合。</param>
/// <param name="Acted">
/// 実際に手を出したか。
///
/// **「終わった」と「止めた」は違う。**
/// もともと動いていなかった稼ぎ手も「中断は終わった」を返すが、
/// 止めてはいない。止めていない相手を復帰させると、
/// 利用者が自分で止めていたものを勝手に動かすことになる。
/// 登録簿はこの値を見て、復帰を配る相手を決める。
/// </param>
/// <param name="Detail">画面と記録に出す短い説明。</param>
public readonly record struct EarnerStepResult(EarnerProgress Progress, bool Acted, string Detail)
{
    public static EarnerStepResult InProgress(string detail = "")
        => new(EarnerProgress.InProgress, false, detail);

    /// <summary>手を出さずに終わった（もともと動いていなかった、導入されていない、など）。</summary>
    public static EarnerStepResult Untouched(string detail = "")
        => new(EarnerProgress.Done, false, detail);

    /// <summary>実際に止めた／戻した。</summary>
    public static EarnerStepResult Handled(string detail = "")
        => new(EarnerProgress.Done, true, detail);

    public static EarnerStepResult Failed(string detail)
        => new(EarnerProgress.Failed, false, detail);
}

/// <summary>
/// 通貨を稼ぐ手段 1 つぶん。戦闘（AutoDuty）やクラフター（Artisan）がこれにあたる。
///
/// **分けるのは稼ぎ方だけ。交換は 1 本のまま。**
/// 交換の歯止め（<c>ExchangeLimits</c>）は 1 か所に集めてある。
/// 窓口ごとに分かれていたことが、所持の上限を無視して買い続ける不具合の原因だった。
/// ここを再び分けない。
///
/// <code>
/// 稼ぎ方（戦闘 / クラフター / 将来のギャザラー）  ← 分ける
///         ↓ 通貨が貯まる
/// 交換（窓口・区分・歯止め・検証）                ← 分けない
/// </code>
///
/// ## 守る約束
///
/// 1. **自分が止めたときだけ戻す。** 利用者が自分で止めていたものは戻さない
/// 2. **中断に成功した稼ぎ手だけが復帰を受ける。** 誰を止めたかは登録簿が持つ
/// 3. **稼ぎ手は自分の設定を自分で読む。** 共通側は稼ぎ手の設定を 1 つも読まない
/// 4. **中断・復帰・稼働判定は、導入されている稼ぎ手すべてに対して行う**
/// 5. **旗を 2 本に割らない。** <see cref="SuppliesCurrency"/> 1 つで賄う
/// </summary>
public interface IEarner
{
    /// <summary>記録に出す不変の名前。設定にも記録にも残るので変えない。</summary>
    string Id { get; }

    /// <summary>画面に出す名前。連携先のプラグイン名。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 稼ぎ方の名前。「戦闘」「クラフター」「ギャザラー」など。
    ///
    /// <see cref="DisplayName"/> とは別。あちらは連携先の名前（AutoDuty / Artisan）で、
    /// こちらは遊び方の呼び名。プリセットの見出しに使う。
    /// </summary>
    string KindName { get; }

    /// <summary>連携先が導入されているか。false のものは中断も復帰も呼ばれない。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// いま稼いでいるか。
    ///
    /// **判断がつかないときは false に倒す。**
    /// 動いているかどうか分からないまま自動交換を始める方が危ない。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 稼働中と見なしている理由を含めた表記。
    ///
    /// <see cref="IsRunning"/> が true のときに画面と記録へ出す。
    /// 「止まっているが猶予のうち」のように、ただの名前では足りない状態がある。
    /// </summary>
    string DescribeRunning();

    /// <summary>
    /// このプリセットで、自力で通貨を増やせるか。
    ///
    /// **プリセットごとに違う。**同じ稼ぎ手でも、
    /// 「製作で稼ぐ」を入れていないプリセットでは通貨を増やさない。
    ///
    /// 交換の歯止めで「終わりを決めずに回してよいか」の判断にも使う。
    /// 製作で稼ぐプリセットは素材が尽きるまで回すのが正規の遊び方なので、
    /// 終了条件が無いことだけを理由に弾いてはいけない。
    ///
    /// **旗を 2 本に割らないこと。** 用途が 2 つあるからといって
    /// 別々の値を持つと、片方だけ直して食い違う。
    /// </summary>
    bool SuppliesCurrencyFor(ExchangePreset preset);

    /// <summary>
    /// この通貨を、この稼ぎ方で増やせるか。
    ///
    /// **ゲームデータで決まる。**通貨の名前も種別もコードへ埋め込まない。
    /// プリセットを稼ぎ方ごとに分けて出すときの振り分けにも使う。
    ///
    /// <see cref="SuppliesCurrencyFor"/> との違い:
    /// あちらは「このプリセットの設定で、いま自力で増やすか」。
    /// こちらは「そもそもこの通貨はこの稼ぎ方で増えるものか」。
    /// </summary>
    bool CanEarn(uint currencyItemId);

    /// <summary>
    /// これから交換で中断する、という合図。
    ///
    /// 中断を始める前に 1 回だけ呼ぶ。
    /// 稼ぎ手はここで、止める前の状態（どのエリアを回っていたか等）を控える。
    /// </summary>
    void MarkInterrupting();

    /// <summary>
    /// いま中断してよい切れ目か。
    ///
    /// コンテンツの最中や製作の途中で止めると、途中の成果を落とす。
    /// </summary>
    bool IsAtSafeBreak(out string reason);

    /// <summary>
    /// 中断を 1 フレーム進める。<see cref="EarnerProgress.Done"/> を返すまで毎フレーム呼ばれる。
    ///
    /// もともと動いていなかったなら <see cref="EarnerStepResult.Untouched"/> を返す。
    /// 実際に止めたなら <see cref="EarnerStepResult.Handled"/> を返す。
    /// **この違いが、復帰を受け取れるかどうかを決める。**
    /// </summary>
    EarnerStepResult TickSuspend();

    /// <summary>
    /// 復帰を 1 フレーム進める。**自分が止めた稼ぎ手にしか呼ばれない。**
    /// </summary>
    EarnerStepResult TickResume();

    /// <summary>
    /// 緊急停止。待たずにその場で止める。
    ///
    /// 記録を残し、<see cref="ResumeAfterStop"/> で戻せる状態にしておく。
    /// **外部プラグインを強制停止しない。** 協調的な手段だけを使う。
    /// </summary>
    void StopForEmergency();

    /// <summary>緊急停止で自分が止めたぶんを戻す。**必ず解除すること。**</summary>
    void ResumeAfterStop();
}
