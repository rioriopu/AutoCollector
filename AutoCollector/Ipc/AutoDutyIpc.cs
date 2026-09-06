using System;
using System.Reflection;
using AutoCollector.Diagnostics;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Reflection;

namespace AutoCollector.Ipc;

/// <summary>
/// AutoDuty との連携。
///
/// 公開されている IPC は状態の取得と Start / Stop / Run だけで、
/// 「いま Duty の途中か」を直接返すものは無い。
/// そのため Duty 中かどうかは ConditionFlag と組み合わせて判断する。
/// </summary>
public sealed class AutoDutyIpc(AnomalyLog anomalyLog) : IpcGateBase("AutoDuty", anomalyLog)
{
    /// <summary>完全に停止しているか。取得できない場合は false（停止と断定しない）。</summary>
    public bool TryIsStopped(out bool stopped)
        => this.TryInvoke("IsStopped", () => this.Func<bool>("AutoDuty.IsStopped").InvokeFunc(), out stopped);

    public bool TryIsNavigating(out bool navigating)
        => this.TryInvoke("IsNavigating", () => this.Func<bool>("AutoDuty.IsNavigating").InvokeFunc(), out navigating);

    public bool TryIsLooping(out bool looping)
        => this.TryInvoke("IsLooping", () => this.Func<bool>("AutoDuty.IsLooping").InvokeFunc(), out looping);

    /// <summary>
    /// AutoDuty の設定値を読む。フィールド名をそのまま渡す。
    /// 見つからない場合は空文字が返る。
    /// </summary>
    public bool TryGetConfig(string key, out string value)
        => this.TryInvoke("GetConfig", () => this.Func<string, string>("AutoDuty.GetConfig").InvokeFunc(key), out value);

    /// <summary>
    /// 設定を書き換える。
    ///
    /// AutoDuty 側は値を型に合わせて変換したうえで保存まで行う。
    /// ユーザーの設定を変えることになるため、こちらから自動では呼ばず、
    /// 画面のボタンを押されたときだけ使う。
    /// </summary>
    public bool TrySetConfig(string key, string value)
        => this.TryAction("SetConfig", () => this.Func<string, object, object>("AutoDuty.SetConfig").InvokeAction(key, value));

    /// <summary>AutoDuty の設定画面を開く。</summary>
    public bool TryOpenConfig() => this.TryProcessCommand("/ad config");

    /// <summary>設定を真偽値として読む。読めない場合は既定値を返す。</summary>
    public bool GetConfigBool(string key, bool fallback)
    {
        if (!this.TryGetConfig(key, out var raw) || string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    public bool TryContentHasPath(uint territoryType, out bool hasPath)
        => this.TryInvoke("ContentHasPath", () => this.Func<uint, bool>("AutoDuty.ContentHasPath").InvokeFunc(territoryType), out hasPath);

    /// <summary>
    /// 動作中か。取得できない場合は「動作中」とみなす。
    /// 停止していないものを停止済みと誤認して割り込む方が危険なため。
    /// </summary>
    public bool IsRunningFailClosed()
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        if (!this.TryIsStopped(out var stopped))
        {
            this.AnomalyLog.Warn("Ipc", "[AutoDuty] 状態を取得できないため、動作中とみなします");
            return true;
        }

        return !stopped;
    }

    /// <summary>
    /// 停止する。
    ///
    /// AutoDuty 側では Stage の設定が同期的に全体停止処理を走らせるため、
    /// 必ず Framework スレッドから呼ぶこと。
    /// </summary>
    public bool TryStop()
        => this.TryAction("Stop", () => this.Func<object>("AutoDuty.Stop").InvokeAction());

    /// <summary>
    /// 一時停止する。
    ///
    /// Stop は Stage.Stopped 経由で TaskManager.Abort を呼ぶため、
    /// ダンジョン後に積まれたループ間処理（リテイナー・GC 納品・修理など）の
    /// 予約ごと消えてしまう。
    ///
    /// 一時停止は TaskManager をステップモードにするだけで予約はそのまま残るため、
    /// 交換のために割り込むときはこちらを使う。再開すれば続きから実行される。
    ///
    /// IPC には無いのでコマンドとして送る。
    /// </summary>
    public bool TryPause() => this.TryProcessCommand("/ad pause");

    /// <summary>一時停止から復帰する。</summary>
    public bool TryResume() => this.TryProcessCommand("/ad resume");

    /// <summary>
    /// いま一時停止しているか。
    ///
    /// 一時停止の状態を返す IPC は無いため、AutoDuty のインスタンスから
    /// States フィールド（PluginState のビット集合）を読む。
    /// 読み取り専用で、相手の状態は一切変更しない。
    ///
    /// 読めなかった場合は false を返す。呼び出し側は「確認できなかった」として扱うこと。
    /// </summary>
    public bool TryIsPaused(out bool paused)
    {
        paused = this.pausedCache;

        if (!this.IsLoaded)
        {
            return false;
        }

        // UI から毎フレーム呼ばれる。リフレクションは安くないので短時間だけ使い回す。
        var now = DateTime.UtcNow;
        if (now <= this.pausedCacheExpiry)
        {
            return this.pausedCacheValid;
        }

        this.pausedCacheExpiry = now.AddMilliseconds(500);

        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin(this.InternalName, out var instance, suppressErrors: true))
            {
                this.pausedCacheValid = false;
                return false;
            }

            var field = instance.GetType().GetField(
                "States",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field?.GetValue(instance) is not { } value)
            {
                this.pausedCacheValid = false;
                return false;
            }

            // PluginState.Paused = 4
            paused = (Convert.ToInt32(value) & 4) != 0;
            this.pausedCache = paused;
            this.pausedCacheValid = true;
            return true;
        }
        catch (Exception ex)
        {
            this.pausedCacheValid = false;

            // ここは UI から毎フレーム呼ばれる。間引かないとログが埋まる。
            this.LogThrottled($"一時停止の状態を読めませんでした: {ex.Message}");
            return false;
        }
    }

    private bool pausedCache;
    private bool pausedCacheValid;
    private DateTime pausedCacheExpiry = DateTime.MinValue;

    /// <summary>
    /// コマンドを送る。
    ///
    /// まず Dalamud のコマンド処理へ直接渡す。受け付けられなかった場合に限り、
    /// ゲームのチャット経路からも送る。どちらか一方でしか届かない環境に備えている。
    /// </summary>
    private bool TryProcessCommand(string command)
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        try
        {
            if (Svc.Commands.ProcessCommand(command))
            {
                return true;
            }

            this.AnomalyLog.Warn("Ipc", $"[AutoDuty] コマンド {command} が登録されていません。チャット経由で送ります");
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Warn("Ipc", $"[AutoDuty] コマンド {command} に失敗しました: {ex.Message}");
        }

        try
        {
            Chat.ExecuteCommand(command);
            return true;
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Error("Ipc", $"[AutoDuty] コマンド {command} をチャット経由でも送れませんでした: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 周回を再開する。
    ///
    /// loops には必ず 0 を渡す。0 以外を渡すと AutoDuty 側の設定 LoopTimes が恒久的に書き換わり、
    /// ユーザーの設定を壊す。
    /// </summary>
    public bool TryRun(uint territoryType)
        => this.TryAction("Run", () => this.Func<uint, int, bool, object>("AutoDuty.Run").InvokeAction(territoryType, 0, false));
}
