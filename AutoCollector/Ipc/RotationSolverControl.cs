using System;
using System.Linq;
using AutoCollector.Diagnostics;
using ECommons.Automation;
using ECommons.DalamudServices;

namespace AutoCollector.Ipc;

/// <summary>
/// Rotation Solver Reborn（RSR）の自動実行を入り切りする。
///
/// <b>IPC ではなくコマンドで操作する。</b>
/// RSR の 7.5.6.16 を調べたが、外から呼べる IPC の口は持っていない
/// （持っているのは <c>BMRPlan_IPCSubscriber</c>、BossMod を購読する側だけ）。
/// 一方で本体のヘルプに
/// 「<c>/rotation Auto</c>, <c>/rotation Manual</c>, <c>/rotation Off</c> で切り替える」
/// と書かれており、こちらが正式な入口になっている。
///
/// <b>なぜ要るか。</b>
/// BossMod Reborn は「どこへ動き、誰を狙うか」を決めるが、
/// <b>技を撃つのは RSR</b>。そのため BMR の用意だけでは、
/// FATE に着いて敵を狙ったまま棒立ちになる。
///
/// <b>自分が入れたときだけ切る。</b>
/// 利用者が自分で入れていた場合に勝手に切ると、周回を止めたあと
/// 手動で遊ぶときに技が出なくなる。これは
/// プロジェクト指示の「外部プラグインを強制停止しない」と同じ考え方で、
/// AutoRetainer の抑制に持ち主を持たせているのと揃えてある。
/// </summary>
public sealed class RotationSolverControl(AnomalyLog anomalyLog)
{
    /// <summary>RSR の内部名。導入されているかの判定に使う。</summary>
    private const string InternalName = "RotationSolver";

    private readonly AnomalyLog anomalyLog = anomalyLog;

    /// <summary>自分が入れたか。切ってよいかの判断に使う。</summary>
    public bool EnabledByUs { get; private set; }

    /// <summary>RSR が導入されているか。</summary>
    public bool IsLoaded
    {
        get
        {
            try
            {
                return Svc.PluginInterface.InstalledPlugins
                    .Any(x => x.IsLoaded && string.Equals(x.InternalName, InternalName, StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 自動実行を入れる。入っていたかどうかは読めないので、
    /// <b>自分が入れたことだけを覚える。</b>
    /// </summary>
    public void Enable(string reason)
    {
        if (!this.IsLoaded || this.EnabledByUs)
        {
            return;
        }

        if (!this.Send("/rotation Auto"))
        {
            return;
        }

        this.EnabledByUs = true;
        this.anomalyLog.Info("RSR", $"自動実行を入れました（{reason}）");
    }

    /// <summary>
    /// 自分が入れたぶんを戻す。入れていなければ何もしない。
    /// </summary>
    public void Release(string reason)
    {
        if (!this.EnabledByUs)
        {
            return;
        }

        this.EnabledByUs = false;

        if (!this.IsLoaded)
        {
            return;
        }

        if (this.Send("/rotation Off"))
        {
            this.anomalyLog.Info("RSR", $"自動実行を戻しました（{reason}）");
        }
    }

    private bool Send(string command)
    {
        try
        {
            Chat.ExecuteCommand(command);
            return true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("RSR", $"{command} を送れませんでした: {ex.Message}");
            return false;
        }
    }
}
