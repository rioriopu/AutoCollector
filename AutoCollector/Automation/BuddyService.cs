using System;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoCollector.Automation;

/// <summary>バディ（チョコボ）の面倒を見た結果。</summary>
public enum BuddyStep
{
    /// <summary>何もしていない。呼び出す必要が無い状態を含む。</summary>
    Idle,

    /// <summary>ギサールの野菜を使い、残り時間が増えるのを待っている。</summary>
    Summoning,

    /// <summary>ギサールの野菜が足りない。買いに行く必要がある。</summary>
    NeedsGreens,

    /// <summary>3 回試しても呼び出せなかった。周回は続ける。</summary>
    Failed,

    /// <summary>バディを持っていない。以後いっさい呼び出さない。</summary>
    NotOwned,

    /// <summary>厩舎に預けている。呼び出せないので見送る。</summary>
    Stabled,
}

/// <summary>
/// バディ（チョコボ）を呼び出し、ギサールの野菜の残量を見る。
///
/// <b>「使った」ことを成功としない。</b>
/// AgentInventoryContext.UseItem が成功を返しても、実際には呼び出されていないことがある。
/// 自作 ARankHuntTourAssistant での実測（AutomationController.cs:1150-1194）。
/// そのため、使ったあと 3 秒待って CompanionInfo.TimeLeft が
/// <b>実際に増えたこと</b>を確認し、増えていなければ最大 3 回まで試し直す。
/// これは docs/00_設計決定.md の共通原則 §3
/// 「UI 操作の実行をもって成功としない」と同じ考え方。
///
/// 3 秒の待ちは「固定時間待機の禁止」（同 §2）に反するように見えるが、
/// これは次へ進む合図ではなく<b>結果を確かめるための間</b>であり、
/// 待ったあとに必ず TimeLeft を読み直して判断している。
/// </summary>
public sealed unsafe class BuddyService(AnomalyLog anomalyLog)
{
    /// <summary>ギサールの野菜の ItemId。</summary>
    public const uint GysahlGreensItemId = 4868;

    /// <summary>使ってから残り時間を確かめるまでの間。</summary>
    private static readonly TimeSpan ConfirmWait = TimeSpan.FromSeconds(3);

    /// <summary>呼び出しを試す上限。</summary>
    private const int MaxAttempts = 3;

    private readonly AnomalyLog anomalyLog = anomalyLog;

    private DateTime requestedAtUtc = DateTime.MinValue;

    /// <summary>諦めた時刻。これから <see cref="RetryAfterFailure"/> のあいだは試さない。</summary>
    private DateTime failedAtUtc = DateTime.MinValue;

    /// <summary>諦めてから、もう一度試すまでの間。</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    private int attempts;
    private float timeLeftAtRequest;

    /// <summary>次にスタンスを見る時刻。毎フレーム撃たないために持つ。</summary>
    private DateTime nextStanceCheckUtc = DateTime.MinValue;

    /// <summary>引けた行を 1 度だけ記録したか。名前が合っているかの確認用。</summary>
    private bool stanceRowsLogged;

    /// <summary>いまの状態。</summary>
    public BuddyStep Step { get; private set; } = BuddyStep.Idle;

    /// <summary>画面に出す短い説明。</summary>
    public string StatusDetail { get; private set; } = string.Empty;

    /// <summary>バディの残り時間（秒）。呼び出していなければ 0。</summary>
    public static float TimeLeftSeconds
    {
        get
        {
            try
            {
                var ui = UIState.Instance();
                return ui is null ? 0f : ui->Buddy.CompanionInfo.TimeLeft;
            }
            catch
            {
                return 0f;
            }
        }
    }

    /// <summary>ギサールの野菜の所持数。</summary>
    public static int GreensCount
    {
        get
        {
            try
            {
                var im = InventoryManager.Instance();
                return im is null ? 0 : im->GetInventoryItemCount(GysahlGreensItemId);
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// バディ（チョコボ）を持っているか。
    ///
    /// <b>持っていない人がいる。</b>持っていない状態で呼び出そうとすると、
    /// ギサールの野菜を使っても何も起きず、そのまま試行を繰り返して詰まる。
    ///
    /// 判定には CompanionInfo.Rank を使う。
    /// 未所持なら 0、所持していれば 1 以上になる。
    /// 呼び出していない状態でも Rank は残るので、
    /// 「いま出ているか」ではなく「持っているか」を見られる。
    /// </summary>
    public static bool HasBuddy
    {
        get
        {
            try
            {
                var ui = UIState.Instance();
                return ui is not null && ui->Buddy.CompanionInfo.Rank > 0;
            }
            catch
            {
                // 読めないときは「持っていない」に倒す。
                // 持っていないのに呼ぼうとして詰まるより、
                // 持っているのに呼ばないほうが害が小さい。
                return false;
            }
        }
    }

    /// <summary>
    /// 厩舎に預けているか。預けている間は呼び出せない。
    ///
    /// 預けているかどうかは PlayerState のフラグで分かる。
    /// </summary>
    public static bool IsStabled
    {
        get
        {
            try
            {
                var ps = PlayerState.Instance();
                return ps is not null && ps->IsPlayerStateFlagSet(PlayerStateFlag.IsBuddyInStable);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 状態を初期に戻す。周回の開始・停止で呼ぶ。
    ///
    /// 「持っていない」「預けている」は握り直す。
    /// 預け直したり、周回の合間に迎えに行ったりすることがあるため、
    /// 一度きりの判定にしてしまうと変化に追随できない。
    /// </summary>
    public void Reset()
    {
        this.Step = BuddyStep.Idle;
        this.StatusDetail = string.Empty;
        this.requestedAtUtc = DateTime.MinValue;
        this.attempts = 0;
        this.timeLeftAtRequest = 0f;
    }

    /// <summary>
    /// 必要ならバディを呼び出す。毎フレーム呼んでよい。
    ///
    /// 戻り値が true なら「いま何かしている（待っている）」という意味で、
    /// 呼び出し側は次の動作へ進まずに待つ。
    /// false なら「用が済んでいる」ので先へ進んでよい。
    /// </summary>
    /// <param name="minSecondsRemaining">残りがこれを切ったら呼び直す。</param>
    /// <param name="minGreensCount">野菜がこれを切ったら買いに行く必要があるとみなす。</param>
    public bool Tick(int minSecondsRemaining, int minGreensCount)
    {
        // 戦闘中・死亡中・騎乗中は使えない。用が無いのと同じ扱いにして先へ進ませる。
        if (!Player.Available
            || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]
            || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious]
            || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
        {
            return false;
        }

        // バディを持っていない人がいる。持っていないまま呼ぼうとすると、
        // 野菜を使っても何も起きず、試行を繰り返して詰まる。
        // 一度「持っていない」と分かったら、以後は黙って見送る。
        if (this.Step == BuddyStep.NotOwned)
        {
            return false;
        }

        if (!HasBuddy)
        {
            this.Step = BuddyStep.NotOwned;
            this.StatusDetail = "バディを持っていないため呼び出しません";
            this.anomalyLog.Info("Buddy", "バディ（チョコボ）を持っていないため、呼び出しは行いません");
            return false;
        }

        // 厩舎に預けている間は呼び出せない。預けたままでも周回は続ける。
        if (IsStabled)
        {
            if (this.Step != BuddyStep.Stabled)
            {
                this.Step = BuddyStep.Stabled;
                this.StatusDetail = "バディを厩舎に預けているため呼び出しません";
                this.anomalyLog.Info("Buddy", "バディを厩舎に預けているため、呼び出しは行いません");
            }

            return false;
        }

        var left = TimeLeftSeconds;

        // 足りている。何もしない。
        if (left >= minSecondsRemaining)
        {
            if (this.Step == BuddyStep.Summoning)
            {
                this.anomalyLog.Info("Buddy", $"バディを呼び出しました（残り {left / 60f:0.0} 分・試行 {this.attempts} 回）");
            }

            // **出ているあいだは、スタンスを見張る。**
            //
            // 以前は呼び出しに成功した直後にしか設定していなかった。
            // すでに出ているチョコボには一度も触れないので、
            // フリースタンスのままだった（2026-10-08 実機）。
            this.EnsureHealerStance();

            this.Reset();
            return false;
        }

        // 野菜が無い。買いに行く判断は呼び出し側に任せる。
        if (GreensCount <= 0)
        {
            this.Step = BuddyStep.NeedsGreens;
            this.StatusDetail = "ギサールの野菜がありません";
            return false;
        }

        // **諦めたあとは、しばらく試さない。**
        //
        // 以前はここが `Step != Summoning` だけだったため、
        // Failed へ落ちた次のフレームでこの分岐に入り直し、
        // attempts が 1 に戻って**また野菜を使った**。
        // 「3 回で諦める」が一度も効かず、持っているだけ使い切る。
        //
        // 厩舎から出し直すなど状況が変わることはあるので、
        // 永久にやめるのではなく、間を置いてから試し直す。
        if (this.Step == BuddyStep.Failed)
        {
            if (DateTime.UtcNow - this.failedAtUtc < RetryAfterFailure)
            {
                return false;
            }

            this.anomalyLog.Info("Buddy", "時間を置いたので、バディの呼び出しをもう一度試します");
            this.Step = BuddyStep.Idle;
        }

        // まだ一度も試していない。使う。
        if (this.Step != BuddyStep.Summoning)
        {
            this.attempts = 1;
            this.timeLeftAtRequest = left;
            this.requestedAtUtc = DateTime.UtcNow;
            this.Step = BuddyStep.Summoning;
            this.StatusDetail = $"バディを呼び出しています（残り {Math.Max(0f, left) / 60f:0.0} 分）";
            this.anomalyLog.Info("Buddy", $"ギサールの野菜を使います（残り {left:0} 秒・試行 1/{MaxAttempts}）");
            UseGreens();
            return true;
        }

        // 使った直後。結果が出るまで待つ。
        if (DateTime.UtcNow - this.requestedAtUtc < ConfirmWait)
        {
            this.StatusDetail = "バディの呼び出しを確認しています";
            return true;
        }

        // 待ったので読み直す。増えていれば成功。
        if (left > this.timeLeftAtRequest + 1f)
        {
            this.anomalyLog.Info("Buddy", $"バディを呼び出しました（残り {left / 60f:0.0} 分・試行 {this.attempts} 回）");
            this.EnsureHealerStance();
            this.Reset();
            return false;
        }

        // 増えていない。上限まで試し直す。
        if (this.attempts < MaxAttempts)
        {
            this.attempts++;
            this.timeLeftAtRequest = left;
            this.requestedAtUtc = DateTime.UtcNow;
            this.StatusDetail = $"ギサールの野菜を試し直しています（{this.attempts}/{MaxAttempts}）";
            this.anomalyLog.Warn("Buddy", $"バディの残り時間が増えていません。試し直します（残り {left:0} 秒・試行 {this.attempts}/{MaxAttempts}）");
            UseGreens();
            return true;
        }

        // 諦める。周回そのものは続ける。
        this.anomalyLog.Warn("Buddy", $"{MaxAttempts} 回試しましたがバディを呼び出せませんでした。周回は続けます（残り {left:0} 秒）");
        this.Step = BuddyStep.Failed;
        this.StatusDetail = "バディを呼び出せませんでした";
        this.requestedAtUtc = DateTime.MinValue;
        this.failedAtUtc = DateTime.UtcNow;
        return false;
    }

    /// <summary>野菜が足りているか。買い出しの判断に使う。</summary>
    public static bool NeedsRestock(int minGreensCount) => GreensCount < minGreensCount;

    /// <summary>
    /// スタンスをヒーラーにする。覚えていなければフリースタンス。
    ///
    /// <b>行番号は埋め込まない。</b>BuddyAction のシートから名前で引く。
    /// シートにはスタンスの種類を表す列が無いため、名前で見分けるしかない。
    /// 日本語と英語の両方を見るので、クライアントの言語が変わっても当たる。
    ///
    /// <b>撃ったことを成功としない。</b>
    /// いまのスタンスは <c>CompanionInfo.ActiveCommand</c> に BuddyAction の
    /// 行番号で入っている。読み直して、変わっていなければ次の呼び出しで撃ち直す。
    ///
    /// ヒーラーはチョコボのランクが足りないと覚えていない。
    /// その場合はフリースタンスにする。どちらも引けなければ何もしない。
    /// 周回そのものは続ける。
    /// </summary>
    private void EnsureHealerStance()
    {
        // 毎フレーム撃たない。変わるまで間を置いて試し直す。
        if (DateTime.UtcNow < this.nextStanceCheckUtc)
        {
            return;
        }

        this.nextStanceCheckUtc = DateTime.UtcNow.AddSeconds(3);

        try
        {
            var ui = UIState.Instance();
            if (ui is null)
            {
                return;
            }

            ref var companion = ref ui->Buddy.CompanionInfo;

            // 出ていないあいだは変えられない。
            if (companion.TimeLeft <= 0f)
            {
                return;
            }

            // _levels は 0=ディフェンダー 1=アタッカー 2=ヒーラー。
            // 0 なら、その型をまだ覚えていない。
            var levels = companion.Levels;
            var healerKnown = levels.Length > 2 && levels[2] > 0;

            var healerRow = FindStanceRow("ヒーラースタンス", "Healer Stance");
            var freeRow = FindStanceRow("フリースタンス", "Free Stance");

            // **引けた行を 1 度だけ残す。**
            // 名前で引いているので、合っていなければここを見れば分かる。
            if (!this.stanceRowsLogged)
            {
                this.stanceRowsLogged = true;
                this.anomalyLog.Info(
                    "Buddy",
                    $"スタンスの行: ヒーラー={healerRow?.ToString() ?? "引けず"} / " +
                    $"フリー={freeRow?.ToString() ?? "引けず"} / " +
                    $"覚えている段位={string.Join(",", levels.ToArray())} / " +
                    $"いま={companion.ActiveCommand}");
            }

            var wanted = healerKnown ? healerRow : null;
            var fellBack = false;

            if (wanted is null)
            {
                wanted = freeRow;
                fellBack = true;
            }

            if (wanted is not { } row)
            {
                this.anomalyLog.Warn("Buddy", "スタンスの行をシートから引けませんでした。スタンスは変えません");
                return;
            }

            if (companion.ActiveCommand == row)
            {
                return;
            }

            var manager = ActionManager.Instance();
            if (manager is null)
            {
                return;
            }

            var sent = manager->UseAction(ActionType.BuddyAction, row);

            this.anomalyLog.Info(
                "Buddy",
                $"スタンスを{(fellBack ? "フリー" : "ヒーラー")}へ変えます" +
                $"（行 {row} / いま {companion.ActiveCommand} / 送信 {sent}）");
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Buddy", $"スタンスを変えられませんでした: {ex.Message}");
        }
    }

    /// <summary>BuddyAction のシートから、名前でスタンスの行を引く。</summary>
    private static uint? FindStanceRow(params string[] names)
    {
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.BuddyAction>();
        if (sheet is null)
        {
            return null;
        }

        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            foreach (var want in names)
            {
                if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
                {
                    return row.RowId;
                }
            }
        }

        return null;
    }

    /// <summary>ギサールの野菜を使う。実際に呼び出されたかは別途確認すること。</summary>
    private void UseGreens()
    {
        try
        {
            var agent = AgentInventoryContext.Instance();
            if (agent is null)
            {
                this.anomalyLog.Warn("Buddy", "AgentInventoryContext を取得できませんでした");
                return;
            }

            agent->UseItem(GysahlGreensItemId, InventoryType.Invalid, 0, 0);
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Buddy", $"ギサールの野菜を使えませんでした: {ex.Message}");
        }
    }
}
