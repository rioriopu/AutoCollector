using System;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Game;
using AutoCollector.Ui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.Schedulers;
using ECommons.SimpleGui;

namespace AutoCollector;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/autocollector";
    private const string ShortCommand = "/ac";

    internal static Plugin P = null!;
    internal static Config C = null!;

    private bool shortCommandRegistered;
    private MainWindow mainWindow = null!;

    internal AnomalyLog AnomalyLog { get; private set; } = null!;

    internal TomestoneService TomestoneService { get; private set; } = null!;

    internal CurrencyService CurrencyService { get; private set; } = null!;

    internal SelfCheck SelfCheck { get; private set; } = null!;

    internal NpcLocationService NpcLocationService { get; private set; } = null!;

    internal ExchangeResolver ExchangeResolver { get; private set; } = null!;

    internal ShopService ShopService { get; private set; } = null!;

    internal CallbackRecorder CallbackRecorder { get; private set; } = null!;

    internal ExchangeExecutor ExchangeExecutor { get; private set; } = null!;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        P = this;
        ECommonsMain.Init(pluginInterface, this);

        // 初期化本体は次フレームへ回す。コンストラクタ内でゲーム状態を触ると
        // 他プラグインのロード順によっては未初期化のものを参照してしまう。
        _ = new TickScheduler(this.Load);
    }

    private void Load()
    {
        C = EzConfig.Init<Config>();

        this.AnomalyLog = new AnomalyLog();
        this.TomestoneService = new TomestoneService(this.AnomalyLog);
        this.CurrencyService = new CurrencyService(this.AnomalyLog);
        this.SelfCheck = new SelfCheck(C, this.AnomalyLog, this.TomestoneService, this.CurrencyService);
        this.NpcLocationService = new NpcLocationService(this.AnomalyLog);
        this.ExchangeResolver = new ExchangeResolver(this.AnomalyLog, this.TomestoneService, this.NpcLocationService);
        this.ShopService = new ShopService(this.AnomalyLog, DataFileLoader.LoadShopLayout(this.AnomalyLog));
        this.CallbackRecorder = new CallbackRecorder(this.AnomalyLog);
        this.ExchangeExecutor = new ExchangeExecutor(this.AnomalyLog, this.ShopService, this.CurrencyService, this.ExchangeResolver);

        Svc.Framework.Update += this.OnFrameworkUpdate;

        this.mainWindow = new MainWindow(this);
        EzConfigGui.Init(this.mainWindow.Draw, null, "Auto Collector");

        EzCmd.Add(MainCommand, this.OnCommand, "Auto Collector を開く。/autocollector stop で緊急停止");
        this.TryRegisterShortCommand();

        this.SelfCheck.RunAll();

        if (C.InFlight is { } pending)
        {
            this.AnomalyLog.Error(
                "Exchange",
                $"前回の交換の結果が未確認のまま残っています（{pending.RewardName} / コスト {pending.CurrencyCost}）。" +
                "ゲーム内で実際の所持数を確認し、診断タブでクリアするまで新しい交換は行いません");
        }

        EzConfig.Save();
    }

    /// <summary>
    /// 短縮コマンドは他プラグインと衝突しうる。登録に失敗しても本体は動かし続け、
    /// 何が起きたかをログに残す。
    /// </summary>
    private void TryRegisterShortCommand()
    {
        if (!C.RegisterShortCommand)
        {
            return;
        }

        try
        {
            EzCmd.Add(ShortCommand, this.OnCommand, "Auto Collector を開く");
            this.shortCommandRegistered = true;
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Warn("Command", $"{ShortCommand} を登録できませんでした（他プラグインと衝突している可能性があります）。{MainCommand} は利用できます: {ex.Message}");
        }
    }

    private void OnCommand(string command, string arguments)
    {
        var argument = arguments.Trim();

        if (argument.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            this.EmergencyStop("ユーザーによる停止");
            return;
        }

        if (argument.Equals("check", StringComparison.OrdinalIgnoreCase))
        {
            var report = this.SelfCheck.RunAll();
            Svc.Chat.Print($"[Auto Collector] セルフチェック: 失敗 {report.FailedCount} / 警告 {report.WarningCount}");
            return;
        }

        if (EzConfigGui.Window is { } window)
        {
            window.IsOpen = !window.IsOpen;
        }
    }

    /// <summary>
    /// 毎フレームの処理。重い処理はここで budget を切って少しずつ進める。
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // 索引構築は 1 フレームあたりの処理量を制限して進める。
            // NPC 配置の索引が先に完成していないと、交換定義に座標を付けられない。
            if (!this.NpcLocationService.TickBuild())
            {
                return;
            }

            this.ExchangeResolver.TickBuild();
            this.ExchangeExecutor.Tick();
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Error("Framework", $"フレーム処理で例外が発生しました: {ex.Message}");
        }
    }

    /// <summary>
    /// 緊急停止。自分が握った制御だけを解放する。
    /// 外部プラグインそのものを止めることはしない。
    /// </summary>
    internal void EmergencyStop(string reason)
    {
        // 何よりも先に発火経路を封鎖する。inFlight はクリアしない（未解決として残す）。
        this.ExchangeExecutor?.Abort(reason);

        this.AnomalyLog.Warn("Stop", $"緊急停止しました: {reason}");
        Svc.Chat.Print($"[Auto Collector] 停止しました: {reason}");

        // S6 以降でここに移動停止・ショップ閉鎖・外部抑制の解除を追加する。
        // 自分が開いたウィンドウかどうかの判定を先に実装しないと、
        // 他プラグインやユーザーが開いたものまで閉じてしまうため、S5 では行わない。
    }

    public void Dispose()
    {
        // ハンドラの解除を最優先で行う。ここが漏れると
        // AutomaticReloading 時に古いインスタンスが動き続ける。
        Svc.Framework.Update -= this.OnFrameworkUpdate;

        try
        {
            this.CallbackRecorder?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] callback 記録の解放に失敗しました: {ex}");
        }

        try
        {
            this.EmergencyStop("プラグインのアンロード");
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] アンロード時のクリーンアップに失敗しました: {ex}");
        }

        if (this.shortCommandRegistered)
        {
            // EzCmd で登録したコマンドは ECommonsMain.Dispose が解除する。
            this.shortCommandRegistered = false;
        }

        ECommonsMain.Dispose();
    }
}
