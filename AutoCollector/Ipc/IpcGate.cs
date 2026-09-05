using System;
using System.Linq;
using AutoCollector.Diagnostics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace AutoCollector.Ipc;

/// <summary>
/// 外部プラグイン IPC の共通土台。
///
/// 重要な方針として、状態の問い合わせは fail-closed にする。
/// 取得に失敗したら「進めてよい」ではなく「進めない」側に倒す。
///
/// ECommons の SafeWrapper は例外を握り潰して default(T) を返すため使わない。
/// たとえば AutoRetainer の IsBusy が例外ではなく false を返すと、
/// 「AutoRetainer は暇」と誤判定して割り込むことになる。
/// </summary>
public abstract class IpcGateBase(string internalName, AnomalyLog anomalyLog)
{
    private DateTime lastErrorLogUtc = DateTime.MinValue;

    protected AnomalyLog AnomalyLog { get; } = anomalyLog;

    public string InternalName { get; } = internalName;

    private bool loadedCache;
    private DateTime loadedCacheExpiry = DateTime.MinValue;

    /// <summary>
    /// プラグインが読み込まれているか。未導入なら該当機能を無効にする。
    ///
    /// この判定は IPC 呼び出しのたびに走る。導入済みプラグイン一覧の走査は
    /// 呼ぶたびに行うには重いので、短時間キャッシュする。
    /// 導入状態が数秒遅れて反映されても実害はない。
    /// </summary>
    public bool IsLoaded
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now <= this.loadedCacheExpiry)
            {
                return this.loadedCache;
            }

            try
            {
                this.loadedCache = Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == this.InternalName && x.IsLoaded);
            }
            catch
            {
                this.loadedCache = false;
            }

            this.loadedCacheExpiry = now.AddSeconds(5);
            return this.loadedCache;
        }
    }

    protected ICallGateSubscriber<TRet> Func<TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<TRet>(name);

    protected ICallGateSubscriber<T1, TRet> Func<T1, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, TRet>(name);

    protected ICallGateSubscriber<T1, T2, TRet> Func<T1, T2, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, T2, TRet>(name);

    protected ICallGateSubscriber<T1, T2, T3, TRet> Func<T1, T2, T3, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, T2, T3, TRet>(name);

    /// <summary>
    /// 値を取得する。失敗したら false を返す。呼び出し側は false を「進めない」として扱うこと。
    /// </summary>
    protected bool TryInvoke<TRet>(string name, Func<TRet> call, out TRet value)
    {
        value = default!;

        if (!this.IsLoaded)
        {
            return false;
        }

        try
        {
            value = call();
            return true;
        }
        catch (IpcNotReadyError)
        {
            this.LogThrottled($"{name}: まだ登録されていません");
            return false;
        }
        catch (IpcTypeMismatchError ex)
        {
            this.LogThrottled($"{name}: 型が一致しません（相手側の仕様変更の可能性があります）: {ex.Message}");
            return false;
        }
        catch (IpcLengthMismatchError ex)
        {
            this.LogThrottled($"{name}: 引数の数が一致しません（相手側の仕様変更の可能性があります）: {ex.Message}");
            return false;
        }
        catch (IpcValueNullError ex)
        {
            this.LogThrottled($"{name}: null が返りました: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            this.LogThrottled($"{name}: 呼び出しに失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>操作を送る。失敗したらログに残して false を返す。</summary>
    protected bool TryAction(string name, Action call)
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        try
        {
            call();
            return true;
        }
        catch (Exception ex)
        {
            this.LogThrottled($"{name}: 呼び出しに失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>同じ内容を毎フレーム記録しないよう間引く。</summary>
    private void LogThrottled(string message)
    {
        var now = DateTime.UtcNow;
        if (now - this.lastErrorLogUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        this.lastErrorLogUtc = now;
        this.AnomalyLog.Warn("Ipc", $"[{this.InternalName}] {message}");
    }
}
