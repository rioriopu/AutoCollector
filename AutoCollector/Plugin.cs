using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Automation;
using AutoCollector.Diagnostics;
using AutoCollector.Earning;
using AutoCollector.Earning.Combat;
using AutoCollector.Earning.Crafter;
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

    internal BellLocationStore BellLocations { get; private set; } = null!;

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

    /// <summary>稼ぎ手の登録簿。戦闘・クラフター（将来はギャザラー）を束ねる。</summary>
    internal EarnerRegistry Earners { get; private set; } = null!;

    /// <summary>戦闘で稼ぐ（AutoDuty）。AutoDuty を触るのはここだけ。</summary>
    internal CombatEarner Combat { get; private set; } = null!;

    /// <summary>クラフターで稼ぐ（Artisan）。Artisan を触るのはここだけ。</summary>
    internal CrafterEarner Crafter { get; private set; } = null!;

    internal MonitorService MonitorService { get; private set; } = null!;

    internal AutoDutyKeeper AutoDutyKeeper { get; private set; } = null!;

    internal AutoDutySetup AutoDutySetup { get; private set; } = null!;

    /// <summary>
    /// エリアを移ってから呼び鈴を探し続ける時間。
    ///
    /// 着いた瞬間に 1 回だけでは足りない。呼び鈴が遠ければ、
    /// その時点ではまだ読み込まれていない。歩いているうちに読み込まれる。
    /// </summary>
    private static readonly TimeSpan BellScanWindow = TimeSpan.FromMinutes(3);

    /// <summary>いま探索の対象にしているエリア。変わったら探索をやり直す。</summary>
    private uint bellScanTerritory;

    /// <summary>この時刻まで呼び鈴を探す。</summary>
    private DateTime bellScanUntilUtc = DateTime.MinValue;

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
    /// 呼び鈴の場所を探して覚える。
    ///
    /// **街へ着いたときだけでは足りない。**
    /// 呼び鈴は <c>Svc.Objects</c> に載っているものしか見えない。
    /// 着いた地点から遠ければ、その時点ではまだ読み込まれていない。
    /// 1 回だけ探しても見つからないことがある。
    ///
    /// そこで、エリアを移ったあとしばらくのあいだ、間隔を空けて探し続ける。
    /// 歩いているうちに読み込まれた時点で覚える。
    ///
    /// **覚えていても探し直す。** 呼び鈴が動いても追随できるようにするため。
    /// 場所が変わっていなければ何もしないので、保存は走らない。
    ///
    /// エリアの種類では絞らない。呼び鈴が無い場所では見つからないだけで、
    /// 探す手間はオブジェクト一覧を 1 周するだけなので害が無い。
    /// </summary>
    private void LearnBellLocation()
    {
        var territory = Svc.ClientState.TerritoryType;

        // エリアが変わったら、そのエリアぶんの探索をやり直す。
        if (territory != this.bellScanTerritory)
        {
            this.bellScanTerritory = territory;
            this.bellScanUntilUtc = DateTime.UtcNow.Add(BellScanWindow);
        }

        this.BellLocations.SaveIfDirty();

        // **覚えていても探し直す。**
        // 覚えたら二度と見ない作りにしていたため、呼び鈴が動いても古い場所を
        // 使い続け、直す手段が「忘れる」ボタンしかなかった。
        // エリアへ入るたびに確かめれば、そのボタン自体が要らなくなる。
        //
        // 場所が変わっていなければ Remember が何もしないので、
        // 何度通っても保存は走らない。

        // 移ってからしばらくのあいだだけ探す。ずっと探し続けはしない。
        if (DateTime.UtcNow > this.bellScanUntilUtc)
        {
            return;
        }

        if (!EzThrottler.Throttle("AutoCollector.ScanBell", 2000))
        {
            return;
        }

        try
        {
            var bell = RetainerRestockRunner.ScanForBell();

            if (bell is not null)
            {
                this.BellLocations.Remember(territory, bell.Position, bell.Name.ToString());
            }
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Warn("Bell", $"呼び鈴を探せませんでした: {ex.Message}");
        }
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

            // 何も持っていない相手も控える。控えないと「知らない相手」のまま残り、
            // 取り出しのたびに開き直すことになる。
            if (RetainerRestockRunner.TryReadOpenRetainerItems(out var contents))
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

        if (C.ConfigVersion < 4)
        {
            // 詳細ログの保存先を、開発機の共有から各自の設定フォルダへ移す。
            //
            // 既定のまま保存されている設定だけを直す。
            // 自分で別の場所を入れている人の設定は触らない。
            const string oldDefault = @"\\rio-pc\DevPlugins\AutoCollectorLogs";

            if (string.Equals(C.LogDirectory, oldDefault, StringComparison.OrdinalIgnoreCase))
            {
                C.LogDirectory = string.Empty;
            }

            C.ConfigVersion = 4;
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
            this.AnomalyLog,
            this.CollectablesShopService,
            this.CurrencyService,
            this.SpecialCurrencyMap,
            this.CollectableRewardService,
            this.CollectablesShopReader);

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
        // **稼ぎ手の登録簿。** 誰が動いているか・誰を止めたかはここが持つ。
        //
        // 登録の順は画面に出る順。AutoDuty を先に出す（従来と同じ並び）。
        this.Earners = new EarnerRegistry();
        this.Combat = new CombatEarner(this.AutoDuty, this.AnomalyLog);
        this.Crafter = new CrafterEarner(this.Artisan, this.AnomalyLog);
        this.Earners.Register(this.Combat);
        this.Earners.Register(this.Crafter);
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
            this.Combat,
            this.Earners,
            this.AutoRetainer,
            this.Crafter,
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
            this.Earners);
        this.CollectableCycle = new CollectableCycleRunner(
            this.AnomalyLog,
            this.ExchangeExecutor,
            this.MonitorService,
            this.CollectablesNpcService,
            this.CollectableRewardService,
            this.CurrencyService,
            this.SpecialCurrencyMap);
        this.RetainerInventory = new RetainerInventoryStore(this.AnomalyLog);
        this.BellLocations = new BellLocationStore(this.AnomalyLog);
        this.RetainerRestock = new RetainerRestockRunner(
            this.AnomalyLog,
            this.CurrencyService,
            this.MenuService,
            this.AutoRetainer,
            this.RetainerInventory,

            // 呼び鈴まで歩くための足。交換の移動とは別物にする。
            // 同時には走らない（GoalRunner が順番に動かす）ので取り合わない。
            new NavigationService(this.AnomalyLog, this.Vnavmesh),
            this.BellLocations);
        this.CraftRunner = new CraftRunner(this.AnomalyLog, this.CurrencyService, this.Artisan);

        // 目標から逆算する側。交換費用の取得に交換画面の一覧が要る。
        this.ScripGoalService = new ScripGoalService(
            this.AnomalyLog,
            this.CurrencyService,
            this.InclusionShopCatalog,
            this.CraftPlanService,
            this.CurrencyCatalog,
            this.ExchangeResolver);

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
            this.CollectableRewardService,
            this.Earners);

        this.AutoDutySetup = new AutoDutySetup(this.AutoDuty, this.AnomalyLog);
        this.AutoDutyKeeper = new AutoDutyKeeper(
            this.AnomalyLog,
            this.AutoDuty,
            this.AutoRetainer,
            this.ExchangeExecutor,
            this.AutoDutySetup,
            this.MonitorService);

        Svc.Framework.Update += this.OnFrameworkUpdate;

        this.mainWindow = new MainWindow(this);

        // 設定と主画面の両方に繋ぐ。
        //
        // このプラグインの画面は 1 つしかなく、設定も操作もそこで行う。
        // 既定（設定だけ）にしていたため、プラグイン一覧から「開く」で
        // 辿り着けず、Dalamud の検査でも主画面が無いと指摘されていた。
        EzConfigGui.Init(
            this.mainWindow.Draw,
            null,
            "Auto Collector",
            EzConfigGui.WindowType.Both);

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

            // **納品だけは間引かない。**
            //
            // 納品は「選ぶ → 撃つ → 反映を見る」を 1 個ごとに繰り返す。
            // 手順そのものは一瞬で終わるので、100 ミリ秒ごとに 1 手しか進まないと
            // 1 個あたり 0.5 秒以上が待ち時間で占められる。40 個なら 20 秒以上になる。
            //
            // ここを毎フレームにすると 1 個あたり 0.1 秒台で回る。
            // 待つ・諦めるの判断はすべて時刻で持たせてあるので、
            // 呼ばれる回数が変わっても待ち時間は変わらない。
            //
            // 走っていなければ即座に戻るため、ふだんの負荷は増えない。
            this.CollectableDelivery.Tick();

            var now = DateTime.UtcNow;
            if (now < this.nextTickUtc)
            {
                return;
            }

            this.nextTickUtc = now.Add(TickInterval);

            this.ExchangeExecutor.Tick();
            this.MonitorService.Tick();
            this.AutoDutyKeeper.Tick();
            this.CollectableCycle.Tick();
            this.RetainerRestock.Tick();
            this.CraftRunner.Tick();

            // 束ねる側は、束ねられる側を全部動かしたあとに見る。
            // 先に見ると、いま終わったばかりの処理を「まだ動いている」と数える。
            this.GoalRunner.Tick();
            this.LearnRetainerInventory();
            this.LearnBellLocation();

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
    /// <param name="stopExternalAutomation">
    /// 走っている AutoDuty の周回も止めるか。
    ///
    /// **利用者が止めたときだけ true。**
    /// アンロードや更新で通すと、こちらを入れ替えただけで相手の周回が消える。
    /// AutoDuty の Stop は TaskManager ごと畳むため、ダンジョン後に積まれた
    /// ループ間処理（リテイナー・GC 納品・修理）の予約まで巻き添えになる。
    /// </param>
    /// <summary>
    /// 止めたものを全部戻す。
    ///
    /// **止めるときに立てた旗は、1 か所で全部下ろす。**
    /// 停止は 2 つの旗を立てる。周回の維持（AutoDutyKeeper.Suspended）と、
    /// 交換の封鎖（ExchangeExecutor の aborted）。
    ///
    /// 戻す側が維持しか見ていなかったため、止めたあとに周回だけが動き出し、
    /// 交換は「停止中です」で弾かれ続けた。周回が回るのに一度も交換されない、
    /// という直したはずの症状がそのまま再現していた。
    ///
    /// しかも封鎖を下ろす手段が画面に無かった。交換が実行中に止めた場合は
    /// 状況タブの「状態をリセットして再開する」で戻せるが、
    /// 何も動いていないときに止めるとその画面自体が出ない。
    /// </summary>
    internal void ResumeAfterStop()
    {
        this.AutoDutyKeeper?.Resume();

        // **止めたぶんは必ず戻す。**
        // 立てた旗を下ろし損なうと、周回は回るのに交換が弾かれ続ける（F-60）。
        // 誰を止めたかは登録簿が持っているので、ここで条件を書かない。
        foreach (var error in this.Earners?.ResumeAllAfterStop() ?? [])
        {
            this.AnomalyLog.Warn("Stop", $"戻せませんでした: {error}");
        }

        this.ExchangeExecutor?.ClearAbort();
    }

    internal void EmergencyStop(string reason, bool stopExternalAutomation = true)
    {
        // 束ねている側から先に止める。
        //
        // ここを通さないと、交換を止めた直後に目標つき周回が次の動作
        // （製作や、リテイナーからの取り出し）を始めてしまう。
        // 利用者は止めたつもりでいるのに、すぐ別の自動処理が動き出すことになる。
        this.GoalRunner?.Stop(reason);

        // **子の処理も直接止める。**
        //
        // GoalRunner.Stop は自分が走っていなければ即座に戻るため、
        // 製作計画タブから手で始めた取り出しや製作は止まらなかった。
        // それでいて次の Abort が AutoRetainer の抑制だけ剥がすので、
        // 取り出しが続いたまま AutoRetainer が同じ呼び鈴へ来て操作を取り合う。
        //
        // **Abort より先に通す。** 取り出し側の後始末が先に抑制を返さないと、
        // あとから Cleanup 側が旗を倒して、取り出しの後始末が空振りする。
        this.RetainerRestock?.Stop(reason);
        this.CraftRunner?.Stop(reason);
        this.CollectableCycle?.Stop(reason);

        // 納品も直接止める。
        //
        // 納品は毎フレーム進むようにしてあるため、止め損なうと
        // 画面が開いているあいだ撃ち続けることになる。
        // 束ねている側を止めただけでは、納品そのものは止まらない。
        this.CollectableDelivery?.Stop(reason);

        // 何よりも先に発火経路を封鎖する。inFlight はクリアしない（未解決として残す）。
        this.ExchangeExecutor?.Abort(reason);

        // **周回の維持も止める。**
        //
        // ここを通していなかったため、止めても AutoDuty が周回を終えるたびに
        // こちらから再開させ続けていた。利用者から見ると
        // 「オートコレクターを止めても周回が止まらない」。
        this.AutoDutyKeeper?.Suspend(reason);

        // **いま走っている周回も止める。**
        //
        // 維持を止めるだけでは足りない。AutoDuty には渡した周回数ぶんを
        // 自走する力があるため、止めたつもりで回り続ける。
        //
        // 利用者が明示的に止めたときだけ通す。協調的な抑制で済む相手ではない。
        //
        // **順序を変えない。**ここは載荷条件になっている。
        if (stopExternalAutomation)
        {
            foreach (var error in this.Earners.StopAllForEmergency())
            {
                this.AnomalyLog.Warn("Stop", $"止められませんでした: {error}");
            }
        }

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

        if (!C.DetailedLogActive)
        {
            return;
        }

        // 保存先が空なら設定フォルダへ書く。
        // 以前は空だと何も記録しなかった。既定を空にしたため、それでは
        // 詳細ログを入れても 1 行も残らなくなる。
        try
        {
            this.fileLog = new FileLogWriter(ResolveLogDirectory());
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

        // 覚えた呼び鈴の場所を書き残す。
        try
        {
            this.BellLocations?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] 呼び鈴の場所を保存できませんでした: {ex}");
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
            this.EmergencyStop("プラグインのアンロード", stopExternalAutomation: false);
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
