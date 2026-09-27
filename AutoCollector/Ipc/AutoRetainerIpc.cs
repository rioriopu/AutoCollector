using AutoCollector.Diagnostics;
using ECommons.Throttlers;

namespace AutoCollector.Ipc;

/// <summary>
/// AutoRetainer との協調。
///
/// 本プラグインは AutoRetainer に何かを依頼しない。軍票交換も自前で行う。
/// ここで行うのは競合の回避だけで、そのために抑制フラグを立てる。
///
/// SetSuppressed(true) は AutoRetainer の「新規開始」だけを止める。
/// 実行中のリテイナー処理は中断されない。
/// 具体的には MultiMode / スケジューラ / MiniTA / リテイナーセンス が止まる。
/// </summary>
public sealed class AutoRetainerIpc(AnomalyLog anomalyLog) : IpcGateBase("AutoRetainer", anomalyLog)
{
    /// <summary>
    /// いま抑制を握っている持ち主。空なら誰も握っていない。
    ///
    /// **bool 1 本にしない。**
    ///
    /// このプラグインの中に、抑制を立てる場所が 3 つある
    /// （交換・リテイナーからの取り出し・ベンチャー回収）。
    /// 「自分が立てたか」を bool 1 本で持つと、
    /// **交換が握っているものをベンチャー回収が横から外せてしまう。**
    /// 外されると交換の最中に AutoRetainer が動き出し、同じ呼び鈴で操作を取り合う。
    /// しかも交換が最後に解除しようとしても、旗が既に倒れているので空振りする。
    ///
    /// 持ち主を覚えておけば、立てた本人しか解除できない。
    /// </summary>
    private string suppressOwner = string.Empty;

    /// <summary>自分（このプラグイン）が抑制を立てているか。</summary>
    public bool SuppressedByUs => this.suppressOwner.Length > 0;

    /// <summary>いま抑制を握っている持ち主。記録に出す。</summary>
    public string SuppressOwner => this.suppressOwner;

    /// <summary>
    /// AutoRetainer が処理中か。取得できない場合は「処理中」とみなす。
    ///
    /// 例外を握り潰して false を返すと「AutoRetainer は暇」と誤判定して割り込むことになる。
    /// そのため取得失敗は必ず安全側に倒す。
    /// 未導入の場合だけは false（処理中ではない）とする。
    /// </summary>
    public bool IsBusyFailClosed()
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        if (!this.TryInvoke("PluginState.IsBusy", () => this.Func<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc(), out bool busy))
        {
            // **理由を添え、間引く。**
            //
            // これは毎フレーム呼ばれる。間引かずに書いていたため、
            // 取得できない状況では記録が同じ 1 行で埋まり、
            // ほかの警告が押し流されていた。
            //
            // しかも「取得できない」としか出ないので、IPC が未登録なのか、
            // 相手が例外を投げたのかが分からなかった。
            if (EzThrottler.Throttle("AutoCollector.ArBusyWarn", 10000))
            {
                var detail = string.IsNullOrEmpty(this.LastError) ? string.Empty : $"（{this.LastError}）";
                this.AnomalyLog.Warn(
                    "Ipc",
                    $"[AutoRetainer] 状態を取得できないため、処理中とみなして待機します{detail}");
            }

            return true;
        }

        return busy;
    }

    /// <summary>マルチモードが有効か。すぐに処理が始まりうるかの判断材料。</summary>
    public bool TryGetMultiModeStatus(out bool enabled)
        => this.TryInvoke("PluginState.GetMultiModeStatus", () => this.Func<bool>("AutoRetainer.PluginState.GetMultiModeStatus").InvokeFunc(), out enabled);

    /// <summary>ベンチャーを回収できるかの判定結果。</summary>
    public enum VentureState
    {
        /// <summary>まだ判断できない。時間を置いてもう一度聞く。</summary>
        Unknown,

        /// <summary>回収できるベンチャーがある。</summary>
        Collectable,

        /// <summary>回収できるベンチャーは無い。</summary>
        None,
    }

    /// <summary>
    /// このキャラクターに、いま回収できるベンチャーを持つリテイナーが居るか。
    ///
    /// <b>残り秒数を聞く方（GetClosestRetainerVentureSecondsRemaining）は使わない。</b>
    /// GbrVentureRelay で実機確認した結果、こちらを選ぶ理由が3つある。
    /// <list type="bullet">
    /// <item>戻り値が素の bool。<c>long?</c> のような Nullable を跨がないので、
    ///       値の受け渡しで失敗する余地が無い。秒数を聞く方は実機で
    ///       「取得に失敗しました」になった</item>
    /// <item>キャラクターの判定を AutoRetainer 側が行う。こちらが ContentId を
    ///       読める状態かに左右されない（エリア移動中は 0 になる）</item>
    /// <item>「回収できる」の線引きが AutoRetainer 本体と完全に同じになる
    ///       （内部は UnsyncCompensation を使った判定。既定 -5 秒）。
    ///       自分で「残り0秒」と線を引くと本体とずれる</item>
    /// </list>
    /// </summary>
    public bool TryAnyRetainersAvailable(out bool available)
        => this.TryInvoke(
            "PluginState.AreAnyRetainersAvailableForCurrentChara",
            () => this.Func<bool>("AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara").InvokeFunc(),
            out available);

    /// <summary>
    /// 回収できるベンチャーがあるか。
    ///
    /// <b>「無い」と「まだ分からない」を区別して返す。</b>
    /// 一緒にすると、読めなかった一瞬のせいで
    /// 実際は回収できるのに「ベンチャーがありません」と誤判定する
    /// （GbrVentureRelay の知見 2-3）。
    /// </summary>
    public VentureState CheckCollectableVenture()
    {
        if (!this.IsLoaded)
        {
            return VentureState.Unknown;
        }

        if (!this.TryAnyRetainersAvailable(out var available))
        {
            return VentureState.Unknown;
        }

        return available ? VentureState.Collectable : VentureState.None;
    }

    public bool TryGetSuppressed(out bool suppressed)
        => this.TryInvoke("GetSuppressed", () => this.Func<bool>("AutoRetainer.GetSuppressed").InvokeFunc(), out suppressed);

    /// <summary>
    /// 抑制を立てる。成功したら**誰が立てたか**を覚える。
    ///
    /// すでに別の持ち主が握っているなら、横取りせずに true を返す
    /// （抑制はもう立っているので、呼んだ側の目的は果たされている）。
    /// </summary>
    /// <param name="owner">立てる側の名前。解除できるのはこの名前だけ。</param>
    public bool Suppress(string owner)
    {
        if (!this.IsLoaded)
        {
            return true;
        }

        // 誰かが既に握っている。立て直さず、持ち主も変えない。
        if (this.suppressOwner.Length > 0)
        {
            return true;
        }

        if (!this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true)))
        {
            return false;
        }

        this.suppressOwner = owner;
        return true;
    }

    /// <summary>
    /// 抑制を解除する。**立てた本人だけが解除できる。**
    ///
    /// 他プラグインやユーザーが立てた抑制を勝手に外さないのはもちろん、
    /// このプラグインの中でも、別の持ち主が握っているものは外さない。
    /// </summary>
    /// <param name="owner">解除する側の名前。持ち主と違えば何もしない。</param>
    public void Release(string owner)
    {
        if (!this.IsLoaded || this.suppressOwner.Length == 0)
        {
            return;
        }

        if (this.suppressOwner != owner)
        {
            // **黙って見送らない。**
            // 外せなかったことが分からないと、
            // 「解除したつもりなのに抑制が残っている」を追えない。
            this.AnomalyLog.Info(
                "Ipc",
                $"[AutoRetainer] {owner} が抑制を解除しようとしましたが、持ち主は {this.suppressOwner} です。触りません");
            return;
        }

        if (this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false)))
        {
            this.suppressOwner = string.Empty;
            this.AnomalyLog.Info("Ipc", $"[AutoRetainer] 抑制を解除しました（{owner}）");
        }
    }

    /// <summary>
    /// 持ち主に関わらず解除する。**プラグインの終了と緊急停止だけで使う。**
    ///
    /// 抑制を立てたまま終わると、利用者の AutoRetainer が止まったままになる。
    /// 持ち主の確認より、確実に戻すことを優先する場面がここ。
    /// </summary>
    public void ReleaseForShutdown()
    {
        if (!this.IsLoaded || this.suppressOwner.Length == 0)
        {
            return;
        }

        if (this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false)))
        {
            this.AnomalyLog.Info("Ipc", $"[AutoRetainer] 抑制を解除しました（終了時 / 持ち主 {this.suppressOwner}）");
            this.suppressOwner = string.Empty;
        }
    }
}
