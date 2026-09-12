using System;
using System.Collections.Generic;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;
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
    // /ac は他プラグインが使っているため /acc を使う。
    private const string ShortCommand = "/acc";

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

    internal SpecialCurrencyMap SpecialCurrencyMap { get; private set; } = null!;

    internal ShopService ShopService { get; private set; } = null!;

    internal InclusionShopService InclusionShopService { get; private set; } = null!;

    internal CallbackRecorder CallbackRecorder { get; private set; } = null!;

    internal CollectablesShopReader CollectablesShopReader { get; private set; } = null!;

    internal CollectablesShopService CollectablesShopService { get; private set; } = null!;

    internal ExchangeExecutor ExchangeExecutor { get; private set; } = null!;

    internal AddonOwnershipTracker AddonOwnership { get; private set; } = null!;

    internal VnavmeshIpc Vnavmesh { get; private set; } = null!;

    internal MenuService MenuService { get; private set; } = null!;

    internal LifestreamIpc Lifestream { get; private set; } = null!;

    internal AetheryteService AetheryteService { get; private set; } = null!;

    internal AutoDutyIpc AutoDuty { get; private set; } = null!;

    internal AutoRetainerIpc AutoRetainer { get; private set; } = null!;

    internal ArtisanIpc Artisan { get; private set; } = null!;

    internal ExternalAutomationGate AutomationGate { get; private set; } = null!;

    internal MonitorService MonitorService { get; private set; } = null!;

    internal AutoDutyKeeper AutoDutyKeeper { get; private set; } = null!;

    internal AutoDutySetup AutoDutySetup { get; private set; } = null!;

    private FileLogWriter? fileLog;

    /// <summary>詳細ログの書き出し状態。UI から参照する。</summary>
    internal FileLogWriter? FileLog => this.fileLog;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        P = this;
        // DalamudReflector は AutoDuty の内部状態を読むために必要。
        // 初期化していないと呼び出しのたびに例外になる。
        ECommonsMain.Init(pluginInterface, this, Module.DalamudReflector);

        // 初期化本体は次フレームへ回す。コンストラクタ内でゲーム状態を触ると
        // 他プラグインのロード順によっては未初期化のものを参照してしまう。
        _ = new TickScheduler(this.Load);
    }

    /// <summary>
    /// 設定の移行。既定値を変えたときに、保存済みの古い値を揃え直す。
    /// </summary>
    private static void MigrateConfig()
    {
        if (C.ConfigVersion >= 1)
        {
            return;
        }

        // 詳細ログは当初 既定で有効にしていた。既定を無効へ変えたので合わせる。
        C.DetailedLogEnabled = false;

        C.ConfigVersion = 1;
        EzConfig.Save();
    }

    private void Load()
    {
        C = EzConfig.Init<Config>();
        MigrateConfig();

        this.AnomalyLog = new AnomalyLog();
        this.StartFileLog();
        this.TomestoneService = new TomestoneService(this.AnomalyLog);
        this.CurrencyService = new CurrencyService(this.AnomalyLog);
        this.SelfCheck = new SelfCheck(C, this.AnomalyLog, this.TomestoneService, this.CurrencyService);
        this.NpcLocationService = new NpcLocationService(this.AnomalyLog);
        this.SpecialCurrencyMap = new SpecialCurrencyMap(this.AnomalyLog);
        this.ExchangeResolver = new ExchangeResolver(this.AnomalyLog, this.TomestoneService, this.NpcLocationService, this.SpecialCurrencyMap);
        this.ShopService = new ShopService(this.AnomalyLog, DataFileLoader.LoadShopLayout(this.AnomalyLog));
        this.InclusionShopService = new InclusionShopService(this.AnomalyLog, this.SpecialCurrencyMap);
        this.CallbackRecorder = new CallbackRecorder(this.AnomalyLog);
        this.CollectablesShopReader = new CollectablesShopReader();
        this.CollectablesShopService = new CollectablesShopService(this.AnomalyLog);

        // スクリップの増減を人に数えさせないため、通貨の読み取り口を渡しておく。
        this.CollectablesShopReader.CurrencySampler = this.SampleSpecialCurrencies;
        this.CallbackRecorder.CurrencySampler = this.SampleSpecialCurrencies;
        this.AddonOwnership = new AddonOwnershipTracker(this.AnomalyLog);
        this.Vnavmesh = new VnavmeshIpc(this.AnomalyLog);
        this.MenuService = new MenuService(this.AnomalyLog);
        this.Lifestream = new LifestreamIpc(this.AnomalyLog);
        this.AetheryteService = new AetheryteService(this.AnomalyLog);
        this.AutoDuty = new AutoDutyIpc(this.AnomalyLog);
        this.Artisan = new ArtisanIpc(this.AnomalyLog);
        this.AutoRetainer = new AutoRetainerIpc(this.AnomalyLog);
        this.AutomationGate = new ExternalAutomationGate(this.AutoDuty, this.Artisan);
        this.ExchangeExecutor = new ExchangeExecutor(
            this.AnomalyLog,
            this.ShopService,
            this.CurrencyService,
            this.ExchangeResolver,
            new NavigationService(this.AnomalyLog, this.Vnavmesh),
            new InteractionService(this.AnomalyLog),
            this.MenuService,
            this.AddonOwnership,
            this.AetheryteService,
            this.Lifestream,
            this.AutoDuty,
            this.AutoRetainer,
            this.Artisan,
            this.InclusionShopService);
        this.MonitorService = new MonitorService(
            this.AnomalyLog,
            this.CurrencyService,
            this.TomestoneService,
            this.ExchangeResolver,
            this.ExchangeExecutor,
            this.AutomationGate);
        this.AutoDutySetup = new AutoDutySetup(this.AutoDuty, this.AnomalyLog);
        this.AutoDutyKeeper = new AutoDutyKeeper(
            this.AnomalyLog,
            this.AutoDuty,
            this.AutoRetainer,
            this.ExchangeExecutor);

        Svc.Framework.Update += this.OnFrameworkUpdate;

        this.mainWindow = new MainWindow(this);
        EzConfigGui.Init(this.mainWindow.Draw, null, "Auto Collector");

        EzCmd.Add(MainCommand, this.OnCommand, "Auto Collector を開く。/autocollector stop で緊急停止");
        this.TryRegisterShortCommand();

        // 特殊通貨の対応表はクライアントが持っている。ログイン後に取得し直す。
        this.SpecialCurrencyMap.RefreshFromClient();

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
    /// <summary>
    /// 状態機械を回す間隔。
    ///
    /// 毎フレーム回す必要はない。交換の発火は「読み取りから発火までを 1 回の呼び出しで
    /// 完結させる」ことが要件であって、呼ばれる頻度とは関係がない。
    /// 移動やダイアログの処理も、100 ミリ秒遅れて体感差は出ない。
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private DateTime nextTickUtc = DateTime.MinValue;

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // 索引構築だけは毎フレーム進める。1 フレームあたりの処理量を制限してあるため、
            // 呼ぶ回数を減らすとその分だけ完成が遅れる。完成後は即座に戻る。
            if (!this.NpcLocationService.TickBuild())
            {
                return;
            }

            if (!this.ExchangeResolver.TickBuild())
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now < this.nextTickUtc)
            {
                return;
            }

            this.nextTickUtc = now.Add(TickInterval);

            this.ExchangeExecutor.Tick();
            this.MonitorService.Tick();
            this.AutoDutyKeeper.Tick();

            // 納品画面が開いた瞬間を捉えて自動でダンプする。読み取りのみ。
            this.CollectablesShopReader.Tick(ResolveLogDirectory());
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

    /// <summary>
    /// 特殊通貨（スクリップ等）の所持数を並べる。
    /// 対応表はクライアントから実行時に取っているので、通貨が増えても追従する。
    /// </summary>
    internal IReadOnlyList<(uint ItemId, string Name, int Count)> SampleSpecialCurrencies()
    {
        var list = new List<(uint, string, int)>();

        foreach (var (itemId, name) in this.SpecialCurrencyMap.ListCurrencies())
        {
            list.Add((itemId, name, this.CurrencyService.GetCountOrZero(itemId)));
        }

        // 収集品そのものの増減も控える。
        // 1 回の納品で 1 個減るのか、スタックごと渡されるのかが、これで分かる。
        foreach (var (itemId, name, count) in CollectablesShopReader.ListHeldCollectables())
        {
            list.Add((itemId, $"[収集品] {name}", count));
        }

        return list;
    }

    /// <summary>
    /// 詳細ログの書き出しを始める。
    ///
    /// 保存先はネットワーク共有を想定しているため、ここでは到達確認をしない。
    /// 確認のために待つと読み込みが止まる。書けるかどうかは背景スレッドが判断する。
    /// </summary>
    internal void StartFileLog()
    {
        this.StopFileLog();

        if (!C.DetailedLogActive || string.IsNullOrWhiteSpace(C.LogDirectory))
        {
            return;
        }

        try
        {
            this.fileLog = new FileLogWriter(C.LogDirectory);
            this.AnomalyLog.SetFileWriter(this.fileLog);

            this.AnomalyLog.Info(
                "Log",
                $"詳細ログを記録します: {this.fileLog.FilePath}");

            this.AnomalyLog.Trace("Log", $"AutoCollector v{Svc.PluginInterface.Manifest.AssemblyVersion} / ECommons v3.2.1.17");

            foreach (var name in new[] { "AutoDuty", "AutoRetainer", "Artisan", "vnavmesh", "Lifestream" })
            {
                foreach (var installed in Svc.PluginInterface.InstalledPlugins)
                {
                    if (installed.InternalName == name)
                    {
                        this.AnomalyLog.Trace("Log", $"{name} v{installed.Version}（読み込み={installed.IsLoaded}）");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] 詳細ログを開始できませんでした: {ex}");
        }
    }

    /// <summary>ダンプの保存先。詳細ログと同じ場所へまとめる。</summary>
    internal static string ResolveLogDirectory()
        => string.IsNullOrWhiteSpace(C.LogDirectory)
            ? Svc.PluginInterface.ConfigDirectory.FullName
            : C.LogDirectory;

    internal void StopFileLog()
    {
        this.AnomalyLog?.SetFileWriter(null);

        try
        {
            this.fileLog?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Auto Collector] 詳細ログの停止に失敗しました: {ex.Message}");
        }

        this.fileLog = null;
    }

    public void Dispose()
    {
        // ハンドラの解除を最優先で行う。ここが漏れると
        // AutomaticReloading 時に古いインスタンスが動き続ける。
        Svc.Framework.Update -= this.OnFrameworkUpdate;

        // 抑制を立てたまま終了すると AutoRetainer が止まったままになる。最優先で解除する。
        try
        {
            this.AutoRetainer?.Release();
            this.Artisan?.Release();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] AutoRetainer の抑制解除に失敗しました: {ex}");
        }

        try
        {
            this.AddonOwnership?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] ウィンドウ追跡の解放に失敗しました: {ex}");
        }

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

        // ログは最後に閉じる。ここまでの後始末も記録に残したい。
        this.StopFileLog();

        ECommonsMain.Dispose();
    }
}
