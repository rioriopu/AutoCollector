using System;
using System.Collections.Generic;
using System.Linq;
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
using ECommons.Throttlers;
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

    internal CurrencyCatalog CurrencyCatalog { get; private set; } = null!;

    internal CollectablesNpcService CollectablesNpcService { get; private set; } = null!;

    internal InclusionShopCatalog InclusionShopCatalog { get; private set; } = null!;

    internal InclusionShopOrderStore InclusionShopOrderStore { get; private set; } = null!;

    internal CollectableRewardService CollectableRewardService { get; private set; } = null!;

    internal CraftPlanService CraftPlanService { get; private set; } = null!;

    internal RetainerRestockRunner RetainerRestock { get; private set; } = null!;

    internal CraftRunner CraftRunner { get; private set; } = null!;

    internal ScripGoalService ScripGoalService { get; private set; } = null!;

    internal GoalRunner GoalRunner { get; private set; } = null!;

    internal RetainerInventoryStore RetainerInventory { get; private set; } = null!;

    internal CollectableCycleRunner CollectableCycle { get; private set; } = null!;

    internal ShopService ShopService { get; private set; } = null!;

    internal InclusionShopService InclusionShopService { get; private set; } = null!;

    internal CallbackRecorder CallbackRecorder { get; private set; } = null!;

    internal CollectablesShopReader CollectablesShopReader { get; private set; } = null!;

    internal CollectablesShopService CollectablesShopService { get; private set; } = null!;

    internal CollectableDeliveryRunner CollectableDelivery { get; private set; } = null!;

    internal InclusionShopObserver InclusionShopObserver { get; private set; } = null!;

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
    /// アイテム交換画面が開いていれば、並んでいる品の順を覚える。
    ///
    /// 並び順をシートから再現しようとしたが、装備以外で規則を特定できなかった。
    /// 実際に開いた画面から覚えるほうが確実で、覚えれば設定画面でも同じ並びで出せる。
    ///
    /// 画面は SpecialShop の行番号を持っていないため、品の顔ぶれで照合する。
    /// **読み取りだけを行う。ゲームの状態は変更しない。**
    /// </summary>
    private unsafe void LearnInclusionShopOrder()
    {
        // 毎フレーム読む必要はない。タブの切り替えを拾えれば足りる。
        if (!EzThrottler.Throttle("AutoCollector.LearnOrder", 500))
        {
            this.InclusionShopOrderStore.SaveIfDirty();
            return;
        }

        try
        {
            if (!this.InclusionShopService.IsOpen() ||
                !this.InclusionShopService.TryGetAddon(out var addon) ||
                !this.InclusionShopService.TryReadEntries(addon, out var entries, out _, out _) ||
                entries.Count == 0)
            {
                this.InclusionShopOrderStore.SaveIfDirty();
                return;
            }

            // 画面に出ている順（スロット順）で ItemId を並べる。
            var itemIds = entries.OrderBy(x => x.Slot).Select(x => x.ItemId).ToList();

            if (this.InclusionShopCatalog.TryFindSeriesByItems(itemIds, out var specialShopId))
            {
                this.InclusionShopOrderStore.Learn(specialShopId, itemIds);
            }
            else
            {
                // 特定できないと覚えられない。黙って落とすと原因が追えない。
                this.InclusionShopOrderStore.NoteUnmatched(itemIds.Count);
            }
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Warn("Inclusion", $"並び順を覚えられませんでした: {ex.Message}");
        }

        this.InclusionShopOrderStore.SaveIfDirty();
    }

    /// <summary>
    /// リテイナーの持ち物を覚える。
    ///
    /// 自分で開いたときだけでなく、人が手で開いたときも控える。
    /// 普段の出し入れで勝手に埋まっていき、総当たりせずに済むようになる。
    ///
    /// **読み取りだけを行う。ゲームの状態は変更しない。**
    /// </summary>
    private void LearnRetainerInventory()
    {
        if (!EzThrottler.Throttle("AutoCollector.LearnRetainer", 1000))
        {
            this.RetainerInventory.SaveIfDirty();
            return;
        }

        try
        {
            if (!RetainerRestockRunner.TryGetOpenRetainerName(out var name))
            {
                this.RetainerInventory.SaveIfDirty();
                return;
            }

            var contents = RetainerRestockRunner.ReadOpenRetainerItems();

            if (contents.Count > 0)
            {
                this.RetainerInventory.Remember(name, contents);
            }
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Warn("Retainer", $"リテイナーの持ち物を覚えられませんでした: {ex.Message}");
        }

        this.RetainerInventory.SaveIfDirty();
    }

    /// <summary>
    /// 設定の移行。既定値を変えたときに、保存済みの古い値を揃え直す。
    /// </summary>
    private static void MigrateConfig()
    {
        var changed = false;

        if (C.ConfigVersion < 1)
        {
            // 詳細ログは当初 既定で有効にしていた。既定を無効へ変えたので合わせる。
            C.DetailedLogEnabled = false;
            C.ConfigVersion = 1;
            changed = true;
        }

        if (C.ConfigVersion < 2)
        {
            // 交換対象は 1 件だけだった。交換リストへ移す。
            foreach (var preset in C.Presets)
            {
                if (preset.Rewards.Count == 0 && preset.RewardItemId != 0)
                {
                    preset.Rewards.Add(new ExchangeEntry
                    {
                        RewardItemId = preset.RewardItemId,

                        // 旧設定の個数の考え方をそのまま引き継ぐ。
                        // 個数指定でなければ上限なしとして扱う。
                        Quantity = preset.Mode == ExchangeMode.FixedQuantity ? Math.Max(1, preset.Quantity) : 0,

                        // 旧設定には所持の上限に当たるものが無い。
                        // 勝手に打ち切ると動きが変わるため、上限なしにする。
                        StopAtOwned = 0,
                        OwnedLimit = 0,
                    });
                }
            }

            C.ConfigVersion = 2;
            changed = true;
        }

        if (C.ConfigVersion < 3)
        {
            // 「持っていたら飛ばす」を「所持の上限」に変えた。
            // 飛ばすか否かの二択ではなく、足りないぶんだけ交換する。
            foreach (var entry in C.Presets.SelectMany(x => x.Rewards))
            {
                if (entry.OwnedLimit == 0 && entry.StopAtOwned != 0)
                {
                    entry.OwnedLimit = entry.StopAtOwned;
                }
            }

            C.ConfigVersion = 3;
            changed = true;
        }

        if (changed)
        {
            EzConfig.Save();
        }
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
        this.InclusionShopObserver = new InclusionShopObserver(this.AnomalyLog, this.InclusionShopService);
        this.CallbackRecorder = new CallbackRecorder(this.AnomalyLog);
        this.CollectablesShopReader = new CollectablesShopReader();
        this.CollectablesShopService = new CollectablesShopService(this.AnomalyLog);
        this.CollectablesNpcService = new CollectablesNpcService(this.AnomalyLog, this.NpcLocationService);
        this.InclusionShopOrderStore = new InclusionShopOrderStore(this.AnomalyLog);
        this.CollectableRewardService = new CollectableRewardService(this.AnomalyLog, this.SpecialCurrencyMap);
        this.CraftPlanService = new CraftPlanService(this.AnomalyLog, this.CollectableRewardService, this.CurrencyService);
        this.InclusionShopCatalog = new InclusionShopCatalog(
            this.AnomalyLog, this.TomestoneService, this.SpecialCurrencyMap, this.InclusionShopOrderStore);

        // 通貨の一覧は、交換に使えるものだけに絞る。先に交換の一覧が要る。
        this.CurrencyCatalog = new CurrencyCatalog(
            this.TomestoneService, this.SpecialCurrencyMap, this.InclusionShopCatalog);
        this.CollectableDelivery = new CollectableDeliveryRunner(
            this.AnomalyLog, this.CollectablesShopService, this.CurrencyService, this.SpecialCurrencyMap);

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
            this.InclusionShopService,
            this.CollectablesShopService,
            this.CollectableDelivery);
        this.MonitorService = new MonitorService(
            this.AnomalyLog,
            this.CurrencyService,
            this.TomestoneService,
            this.CurrencyCatalog,
            this.ExchangeResolver,
            this.ExchangeExecutor,
            this.AutomationGate);
        this.CollectableCycle = new CollectableCycleRunner(
            this.AnomalyLog,
            this.ExchangeExecutor,
            this.MonitorService,
            this.CollectablesNpcService,
            this.CollectableRewardService,
            this.CurrencyService,
            this.SpecialCurrencyMap);
        this.RetainerInventory = new RetainerInventoryStore(this.AnomalyLog);
        this.RetainerRestock = new RetainerRestockRunner(
            this.AnomalyLog,
            this.CurrencyService,
            this.MenuService,
            this.AutoRetainer,
            this.RetainerInventory,

            // 呼び鈴まで歩くための足。交換の移動とは別物にする。
            // 同時には走らない（GoalRunner が順番に動かす）ので取り合わない。
            new NavigationService(this.AnomalyLog, this.Vnavmesh));
        this.CraftRunner = new CraftRunner(this.AnomalyLog, this.CurrencyService, this.Artisan);

        // 目標から逆算する側。交換費用の取得に交換画面の一覧が要る。
        this.ScripGoalService = new ScripGoalService(
            this.AnomalyLog,
            this.CurrencyService,
            this.InclusionShopCatalog,
            this.CraftPlanService,
            this.CurrencyCatalog);

        // ①〜⑤ を 1 本に束ねる。ここより先に、束ねる相手が全部そろっている必要がある。
        this.GoalRunner = new GoalRunner(
            this.AnomalyLog,
            this.ScripGoalService,
            this.CraftPlanService,
            this.RetainerRestock,
            this.CraftRunner,
            this.CollectableCycle,
            this.MonitorService,
            this.ExchangeExecutor,
            this.CurrencyService,
            this.CollectableRewardService);

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

            // 配置ファイルから引けない NPC を、実際に見かけたときに覚える。
            // リムサとグリダニアの窓口はこれでしか位置を取れない。
            this.NpcLocationService.LearnFromWorld();

            // 交換画面が開いていれば、品の並び順を覚える。
            // シートからは装備以外の並びを再現できなかった。
            this.LearnInclusionShopOrder();

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
            this.CollectableDelivery.Tick();
            this.CollectableCycle.Tick();
            this.RetainerRestock.Tick();
            this.CraftRunner.Tick();

            // 束ねる側は、束ねられる側を全部動かしたあとに見る。
            // 先に見ると、いま終わったばかりの処理を「まだ動いている」と数える。
            this.GoalRunner.Tick();
            this.LearnRetainerInventory();

            // 納品画面が開いた瞬間を捉えて自動でダンプする。読み取りのみ。
            this.CollectablesShopReader.Tick(ResolveLogDirectory());
            this.InclusionShopObserver.Tick(ResolveLogDirectory());
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
        // 束ねている側から先に止める。
        //
        // ここを通さないと、交換を止めた直後に目標つき周回が次の動作
        // （製作や、リテイナーからの取り出し）を始めてしまう。
        // 利用者は止めたつもりでいるのに、すぐ別の自動処理が動き出すことになる。
        this.GoalRunner?.Stop(reason);

        // 何よりも先に発火経路を封鎖する。inFlight はクリアしない（未解決として残す）。
        this.ExchangeExecutor?.Abort(reason);

        this.AnomalyLog.Warn("Stop", $"緊急停止しました: {reason}");
        Svc.Chat.Print($"[Auto Collector] 停止しました: {reason}");

        // S6 以降でここに移動停止・ショップ閉鎖・外部抑制の解除を追加する。
        // 自分が開いたウィンドウかどうかの判定を先に実装しないと、
        // 他プラグインやユーザーが開いたものまで閉じてしまうため、S5 では行わない。
    }

    /// <summary>
    /// 所持品の一覧を控える。記録の前後で比べ、何が増えて何が減ったかを出すために使う。
    ///
    /// 特殊通貨だけを見ていると、交換で受け取った品が出てこない。
    /// 何と引き換えに何を得たのかは、両方を並べないと分からない。
    /// </summary>
    internal IReadOnlyList<(uint ItemId, string Name, int Count)> SampleSpecialCurrencies()
    {
        var list = new List<(uint, string, int)>();
        var seen = new HashSet<uint>();

        foreach (var (itemId, name) in this.SpecialCurrencyMap.ListCurrencies())
        {
            list.Add((itemId, name, this.CurrencyService.GetCountOrZero(itemId)));
            seen.Add(itemId);
        }

        // 収集品は、1 回の納品で何個渡されるかを見るために個別に出す。
        var collectables = new HashSet<uint>();
        foreach (var (itemId, name, count) in CollectablesShopReader.ListHeldCollectables())
        {
            list.Add((itemId, $"[収集品] {name}", count));
            collectables.Add(itemId);
        }

        // 鞄の中身。交換で受け取った品はここに入る。
        foreach (var (itemId, name, count) in InventorySnapshot.ListBagItems())
        {
            if (seen.Contains(itemId) || collectables.Contains(itemId))
            {
                continue;
            }

            list.Add((itemId, name, count));
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

        // リテイナーの持ち物を書き残す。
        try
        {
            this.RetainerInventory?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] リテイナーの持ち物を保存できませんでした: {ex}");
        }

        // 覚えた並び順を書き残す。
        try
        {
            this.InclusionShopOrderStore?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] 並び順を保存できませんでした: {ex}");
        }

        // 背景で配置ファイルを読んでいる場合は打ち切る。
        try
        {
            this.NpcLocationService?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] NPC 配置の背景走査を止められませんでした: {ex}");
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
