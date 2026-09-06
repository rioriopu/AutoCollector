using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ECommons.DalamudServices;

namespace AutoCollector.Diagnostics;

public enum AnomalySeverity
{
    /// <summary>詳細ログ専用。UI の一覧には残さない。</summary>
    Trace,
    Info,
    Warning,
    Error,
}

public sealed record AnomalyEntry(DateTime At, AnomalySeverity Severity, string Category, string Message);

/// <summary>
/// 異常の記録先。ログへ流すだけだと気付かれないため、UI から見える形で保持する。
/// 「サイレントに誤動作するより、検出して止めて知らせる」という方針の受け皿。
/// </summary>
public sealed class AnomalyLog
{
    private const int MaxEntries = 200;

    private readonly List<AnomalyEntry> entries = [];
    private readonly Lock gate = new();

    private FileLogWriter? file;

    /// <summary>ファイルへの書き出し先を差し替える。null を渡すと書き出しを止める。</summary>
    public void SetFileWriter(FileLogWriter? writer) => this.file = writer;

    public FileLogWriter? File => this.file;

    public IReadOnlyList<AnomalyEntry> Snapshot()
    {
        lock (this.gate)
        {
            return this.entries.ToArray();
        }
    }

    public int ErrorCount
    {
        get
        {
            lock (this.gate)
            {
                return this.entries.Count(x => x.Severity == AnomalySeverity.Error);
            }
        }
    }

    /// <summary>
    /// 詳細ログ。ファイルにだけ残す。
    ///
    /// 状態遷移のように件数が多いものをここへ流す。
    /// UI の一覧に混ぜると、本当に見るべき警告が押し流されてしまう。
    /// </summary>
    public void Trace(string category, string message) => this.Add(AnomalySeverity.Trace, category, message);

    public void Info(string category, string message) => this.Add(AnomalySeverity.Info, category, message);

    public void Warn(string category, string message) => this.Add(AnomalySeverity.Warning, category, message);

    public void Error(string category, string message) => this.Add(AnomalySeverity.Error, category, message);

    public void Clear()
    {
        lock (this.gate)
        {
            this.entries.Clear();
        }
    }

    private void Add(AnomalySeverity severity, string category, string message)
    {
        var at = DateTime.Now;

        // Trace は件数が多い。UI の一覧に混ぜると本当に見るべき警告が押し流される。
        if (severity != AnomalySeverity.Trace)
        {
            lock (this.gate)
            {
                this.entries.Add(new AnomalyEntry(at, severity, category, message));
                if (this.entries.Count > MaxEntries)
                {
                    this.entries.RemoveRange(0, this.entries.Count - MaxEntries);
                }
            }
        }

        var line = $"[{category}] {message}";

        // ファイルへは全部残す。書けなくても本体の動作は変えない。
        this.file?.Write($"{at:yyyy-MM-dd HH:mm:ss.fff} [{Label(severity)}] {line}");

        switch (severity)
        {
            case AnomalySeverity.Error:
                Svc.Log.Error(line);
                break;
            case AnomalySeverity.Warning:
                Svc.Log.Warning(line);
                break;
            case AnomalySeverity.Trace:
                Svc.Log.Debug(line);
                break;
            default:
                Svc.Log.Information(line);
                break;
        }
    }

    private static string Label(AnomalySeverity severity) => severity switch
    {
        AnomalySeverity.Error => "ERR",
        AnomalySeverity.Warning => "WRN",
        AnomalySeverity.Trace => "TRC",
        _ => "INF",
    };
}
