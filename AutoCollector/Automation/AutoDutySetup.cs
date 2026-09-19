using System;
using System.Collections.Generic;
using System.Linq;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;
using ECommons.DalamudServices;

namespace AutoCollector.Automation;

/// <summary>点検項目 1 件。</summary>
public sealed record SetupItem(
    string Key,
    string Title,
    string Where,
    string Why,
    string ExpectedLabel,
    string ApplyValue,
    string Current,
    bool Ok,
    bool Readable,
    bool CanApply);

/// <summary>
/// 「ID クリア → リテイナー → GC 納品 → 交換 → 次の ID」を成立させるための
/// AutoDuty 側の設定を点検する。
///
/// 周回の途中には割り込めないため、AutoDuty を 1 周で終わらせ、その終わりに交換する。
/// 最終周のあともループ間処理を実行する設定が要になる。
///
/// 読み取りは IPC 呼び出しなので、毎フレーム走らせずに短時間キャッシュする。
/// 書き込みはユーザーが押したときだけ行い、こちらから勝手には変更しない。
/// </summary>
public sealed class AutoDutySetup(AutoDutyIpc autoDuty, AnomalyLog anomalyLog)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private readonly AutoDutyIpc autoDuty = autoDuty;
    private readonly AnomalyLog anomalyLog = anomalyLog;

    private DateTime cacheExpiry = DateTime.MinValue;
    private List<SetupItem> cached = [];

    /// <summary>点検結果。短時間キャッシュする。</summary>
    public IReadOnlyList<SetupItem> Items
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now <= this.cacheExpiry)
            {
                return this.cached;
            }

            this.cacheExpiry = now.Add(CacheDuration);
            this.cached = this.Read();
            return this.cached;
        }
    }

    /// <summary>直すべき項目の数。</summary>
    public int PendingCount
    {
        get
        {
            var n = 0;
            foreach (var item in this.Items)
            {
                if (!item.Ok)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>自動で直せる項目が残っているか。</summary>
    public bool HasApplicable
    {
        get
        {
            foreach (var item in this.Items)
            {
                if (!item.Ok && item.CanApply && item.Readable)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// AutoDuty の版が古くて、戦闘の周回を任せられない状態か。
    ///
    /// **古い版では設定の読み書きができない。**
    /// AutoDuty は設定の持ち方を作り直しており、古い版ではループ間処理の
    /// 設定を確かめられない。その状態で周回を任せると、
    /// 交換に入れないまま延々と回り続けることになる。
    /// </summary>
    public bool NeedsAutoDutyUpdate => this.RequiredVersion is { } required &&
                                       this.autoDuty.IsLoaded &&
                                       (this.autoDuty.GetInstalledVersion() is not { } installed ||
                                        installed < required);

    /// <summary>必要な版。設定が空か読めなければ null（制限しない）。</summary>
    public Version? RequiredVersion
        => Version.TryParse(Plugin.C.MinimumAutoDutyVersion, out var v) ? v : null;

    /// <summary>いま入っている版。読めなければ null。</summary>
    public Version? InstalledVersion => this.autoDuty.GetInstalledVersion();

    /// <summary>Dalamud のプラグイン一覧を開く。更新はそこから行ってもらう。</summary>
    public void OpenPluginInstaller() => this.autoDuty.TryOpenPluginInstaller();

    /// <summary>設定を 1 件も読めなかったか。相手の版が想定と違う可能性がある。</summary>
    public bool NothingReadable => this.Items.Count > 0 && this.Items.All(x => !x.Readable);

    private List<SetupItem> Read()
    {
        var items = new List<SetupItem>();

        Add("Meta.LoopTimes", "周回数を 1 にする",
            "AutoDuty のメイン画面「Loops」",
            "1 周ごとに交換の機会ができます。値を大きくすると、その周回数ごとの交換になります。",
            "1", "1", v => v == "1");

        Add("Loop.Between.Enabled", "ループ間処理を有効にする",
            "AutoDuty の設定「Between Loop Actions」",
            "リテイナー・GC 納品などをまとめて行う枠です。ここが無効だと何も実行されません。",
            "有効", "true", IsTrue);

        Add("Loop.Between.ExecuteLastLoop", "「Run on last Loop」を有効にする",
            "AutoDuty の設定「Between Loop Actions」の先頭",
            "これが要です。無効だと最終周のあとループ間処理が行われず、1 周設定では一度も実行されません。",
            "有効", "true", IsTrue);

        Add("Loop.Between.Actions.[AutoRetainerLoopActionConfig].Enabled", "リテイナー連携を有効にする",
            "AutoDuty の設定「Between Loop Actions」→ AutoRetainer",
            "ダンジョン後にリテイナーへアクセスします。",
            "有効", "true", IsTrue);

        Add("Loop.Between.Actions.[AutoRetainerLoopActionConfig].AutoRetainerRemainingTime", "リテイナーへ向かう猶予を入れる",
            "同じ欄の「Waiting up to ... seconds」",
            "0 のままだとリテイナーへ行きません。ベンチャーの完了がこの秒数以内なら向かいます。",
            "0 より大きい値（推奨 300）", "300",
            v => long.TryParse(v, out var n) && n > 0);

        Add("Loop.Between.Actions.[GCTurnInLoopActionConfig].Enabled", "GC 納品を有効にする",
            "AutoDuty の設定「Between Loop Actions」→ GC Turnin",
            "補給担当官での軍票交換と、希少品の納品を行います。",
            "有効", "true", IsTrue);

        Add("DutyConfig.AutoExitDuty", "コンテンツから自動で出る",
            "AutoDuty の設定「Duty」",
            "無効だとダンジョン内で停止し、交換に入れません。手動停止との区別にも使っています。",
            "有効", "true", IsTrue);

        // これは要件ではなく注意喚起。勝手に書き換えると、意図した終了処理を壊す。
        Add("Loop.Termination.TerminationMethodEnum", "終了時の動作を確認する",
            "AutoDuty の設定「Termination Actions」",
            "1 周ごとに終了処理を通ります。ログアウトやクライアント終了を設定していると毎周回それが走ります。",
            "Do_Nothing", string.Empty,
            v => string.Equals(v, "Do_Nothing", StringComparison.OrdinalIgnoreCase),
            canApply: false);

        return items;

        static bool IsTrue(string v) => bool.TryParse(v, out var b) && b;

        void Add(
            string key,
            string title,
            string where,
            string why,
            string expected,
            string applyValue,
            Func<string, bool> check,
            bool canApply = true)
        {
            // **新旧どちらのキーでも引けるようにする。**
            //
            // AutoDuty は設定をプロファイル方式へ作り直した（2026 年前半）。
            // 旧: Configuration のフィールドを名前で引く（LoopTimes など）
            // 新: プロファイルのプロパティを入れ子のパスで引く
            //     （Loop.Between.Enabled、Loop.Between.Actions.[型名].Enabled）
            //
            // 引く側は GetFields から GetProperties に変わっているため、
            // 旧キーは新しい AutoDuty では 1 つも解決できない。
            // 実際、8 項目すべてが「読み取れません」になっていた。
            //
            // どちらを使っているかは相手の版で決まる。両方試して、
            // 先に引けたほうを使う。
            var resolved = this.Resolve(key, out var current);

            items.Add(new SetupItem(
                resolved,
                title,
                where,
                why,
                expected,
                applyValue,
                current,
                current.Length > 0 && check(current),
                current.Length > 0,
                canApply && !string.IsNullOrEmpty(applyValue)));
        }
    }

    /// <summary>
    /// 新しいキーと古いキーの対応。
    ///
    /// AutoDuty が設定をプロファイル方式へ作り直したため、同じ設定でも
    /// 版によって指し方が違う。対応表は相手の移行コード
    /// （ConfigurationMain の旧→新の写し替え）から取った。
    /// </summary>
    private static readonly (string Modern, string Legacy)[] KeyPairs =
    [
        ("Meta.LoopTimes", "LoopTimes"),
        ("Loop.Between.Enabled", "EnableBetweenLoopActions"),
        ("Loop.Between.ExecuteLastLoop", "ExecuteBetweenLoopActionLastLoop"),
        ("Loop.Between.Actions.[AutoRetainerLoopActionConfig].Enabled", "EnableAutoRetainer"),
        ("Loop.Between.Actions.[AutoRetainerLoopActionConfig].AutoRetainerRemainingTime", "AutoRetainer_RemainingTime"),
        ("Loop.Between.Actions.[GCTurnInLoopActionConfig].Enabled", "AutoGCTurnin"),
        ("DutyConfig.AutoExitDuty", "AutoExitDuty"),
        ("Loop.Termination.TerminationMethodEnum", "TerminationMethodEnum"),
    ];

    /// <summary>
    /// 設定を引く。新しいキーで引けなければ古いキーで引く。
    /// 実際に引けたキーを返す。書き込みにも同じものを使う。
    /// </summary>
    private string Resolve(string key, out string value)
    {
        value = string.Empty;

        foreach (var candidate in Candidates(key))
        {
            if (this.autoDuty.TryGetConfig(candidate, out var current) && !string.IsNullOrEmpty(current))
            {
                value = current;
                return candidate;
            }
        }

        return key;
    }

    /// <summary>そのキーで試す候補を、新しいほうから順に返す。</summary>
    private static IEnumerable<string> Candidates(string key)
    {
        foreach (var (modern, legacy) in KeyPairs)
        {
            if (modern == key || legacy == key)
            {
                yield return modern;

                if (legacy != modern)
                {
                    yield return legacy;
                }

                yield break;
            }
        }

        yield return key;
    }

    /// <summary>1 件だけ適用する。</summary>
    public void Apply(SetupItem item)
    {
        if (!item.CanApply || string.IsNullOrEmpty(item.ApplyValue))
        {
            return;
        }

        if (!this.autoDuty.TrySetConfig(item.Key, item.ApplyValue))
        {
            this.anomalyLog.Warn("Setup", $"{item.Title}: AutoDuty へ設定を渡せませんでした");
            return;
        }

        this.anomalyLog.Info("Setup", $"AutoDuty の設定を変更しました: {item.Key} = {item.ApplyValue}（{item.Title}）");
        this.Invalidate();
    }

    /// <summary>直せる項目をまとめて適用する。</summary>
    public void ApplyAll()
    {
        var applied = 0;

        foreach (var item in this.Items)
        {
            if (item.Ok || !item.CanApply || !item.Readable)
            {
                continue;
            }

            if (this.autoDuty.TrySetConfig(item.Key, item.ApplyValue))
            {
                this.anomalyLog.Info("Setup", $"AutoDuty の設定を変更しました: {item.Key} = {item.ApplyValue}（{item.Title}）");
                applied++;
            }
            else
            {
                this.anomalyLog.Warn("Setup", $"{item.Title}: AutoDuty へ設定を渡せませんでした");
            }
        }

        if (applied > 0)
        {
            Svc.Chat.Print($"[Auto Collector] AutoDuty の設定を {applied} 件変更しました");
        }

        this.Invalidate();
    }

    /// <summary>AutoDuty の設定画面を開く。</summary>
    public void OpenAutoDutyConfig() => this.autoDuty.TryOpenConfig();

    private void Invalidate() => this.cacheExpiry = DateTime.MinValue;
}
