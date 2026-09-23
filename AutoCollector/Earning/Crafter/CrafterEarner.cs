using System;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;

namespace AutoCollector.Earning.Crafter;

/// <summary>
/// クラフターで稼ぐ（収集品を作って納品し、スクリップを得る）。
///
/// **Artisan を触るのはここだけ。**
/// 稼ぎの手順（製作・納品・リテイナーからの取り出し）は
/// <c>GoalRunner</c> に残っており、段 6 で移す。
/// </summary>
public sealed class CrafterEarner(ArtisanIpc artisan, AnomalyLog anomalyLog) : IEarner
{
    private readonly ArtisanIpc artisan = artisan;
    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>同じ待ちを記録に書き続けないための間引き。</summary>
    private DateTime lastWaitLogUtc = DateTime.MinValue;

    public string Id => "Crafter";

    public string DisplayName => "Artisan";

    public bool IsAvailable => this.artisan.IsLoaded;

    /// <summary>
    /// いま製作しているか。取得できない場合は false（＝動いていない）。
    /// </summary>
    public bool IsRunning => this.artisan.IsRunning();

    public string DescribeRunning() => "Artisan";

    /// <summary>
    /// 製作で稼ぐかどうかはプリセットごとの設定で決まる。
    ///
    /// **ここでは判断できない。** 段 5 で、プリセットの設定を見て答えられるようにする。
    /// それまでは共通側が <c>preset.CraftToEarn</c> を直接見ている。
    /// </summary>
    public bool SuppliesCurrency => false;

    public void MarkInterrupting() => this.lastWaitLogUtc = DateTime.MinValue;

    /// <summary>製作の途中で止めると、進行中の 1 個を落とす。</summary>
    public bool IsAtSafeBreak(out string reason)
    {
        if (this.artisan.IsLoaded && this.artisan.IsBusyFailClosed())
        {
            reason = "製作中です";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 交換の間だけ Artisan を止める。
    ///
    /// Artisan は停止時に動作中のモード（耐久モード / 製作リスト）を自分で記録し、
    /// 解除時にそのモードだけを戻す。何も動いていなければ何も起きない。
    /// </summary>
    public EarnerStepResult TickSuspend()
    {
        if (!this.artisan.IsLoaded || !Plugin.C.StopArtisan)
        {
            return EarnerStepResult.Untouched();
        }

        if (!this.artisan.StoppedByUs)
        {
            // **動いていないなら止める必要が無い。**
            //
            // 製作していない人の交換まで、Artisan の都合で止めていた。
            // 止めるのは操作を取り合わないためなので、相手が何もしていないなら用が無い。
            if (!this.artisan.IsRunning())
            {
                return EarnerStepResult.Untouched();
            }

            if (!this.artisan.Stop())
            {
                // **止められなかったことを、交換の失敗にしない。**
                //
                // ここで失敗にしていたため、2 回続くとプリセットが自動で無効化された。
                // 実際の報告では、製作と無関係な周回の交換がこれで丸ごと止まっていた。
                //
                // 相手が手を離すのを待つ。待ちの上限は呼ぶ側が持っており、
                // 超えれば中止として終わる。中止は設定の誤りではない。
                if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(60))
                {
                    this.lastWaitLogUtc = DateTime.UtcNow;
                    var detail = string.IsNullOrEmpty(this.artisan.LastError)
                        ? string.Empty
                        : $"（{this.artisan.LastError}）";

                    this.anomalyLog.Warn(
                        "Suppress",
                        $"Artisan へ停止を依頼できませんでした{detail}。手が空くのを待っています");
                }

                return EarnerStepResult.InProgress("Artisan へ停止を依頼できません。手が空くのを待っています");
            }

            this.anomalyLog.Info("Suppress", "交換の間、Artisan の製作を止めました");

            // 止めた直後は製作画面から抜ける処理が残っている。次の呼び出しで確認する。
            return EarnerStepResult.InProgress("Artisan の製作が止まるのを待っています");
        }

        // 製作画面から抜け終わるまで待つ。抜ける前に移動すると操作が噛み合わない。
        if (this.artisan.IsBusyFailClosed())
        {
            if (DateTime.UtcNow - this.lastWaitLogUtc > TimeSpan.FromSeconds(60))
            {
                this.lastWaitLogUtc = DateTime.UtcNow;
                this.anomalyLog.Info("Wait", "Artisan の製作が止まるのを待っています");
            }

            return EarnerStepResult.InProgress("Artisan の製作が止まるのを待っています");
        }

        return EarnerStepResult.Handled("Artisan を止めました");
    }

    /// <summary>
    /// 停止要求を解除する。
    ///
    /// <c>ArtisanIpc.Release</c> が「自分が止めた場合だけ」を見ている。
    /// 利用者が自分で止めたものは動かさない。
    /// </summary>
    public EarnerStepResult TickResume()
    {
        this.artisan.Release();
        return EarnerStepResult.Handled();
    }

    /// <summary>記録に出す状態。原因を追うときの手がかり。</summary>
    public string DescribeState()
        => this.artisan.IsLoaded
            ? $"Artisan(処理中={this.artisan.IsBusyFailClosed()} 本体停止={this.artisan.StoppedByUs})"
            : string.Empty;

    /// <summary>
    /// 緊急停止では Artisan を止めない。
    ///
    /// **戻す道筋が無いものを止めない。**
    /// 緊急停止は「こちらの自動処理をやめる」ためのもので、
    /// 利用者が自分で回している製作まで止める話ではない。
    /// ここで止めると、解除は交換の後始末を通らないと起きないため、
    /// 製作が止まったまま戻らなくなる（F-60 と同じ形）。
    ///
    /// 交換の間だけ止めるぶんは <see cref="TickSuspend"/> が行い、
    /// 必ず <see cref="ResumeAfterStop"/> か交換の後始末で解除される。
    /// </summary>
    public void StopForEmergency()
    {
    }

    /// <summary>停止要求を解除する。自分が止めた場合だけ効く。</summary>
    public void ResumeAfterStop() => this.artisan.Release();
}
