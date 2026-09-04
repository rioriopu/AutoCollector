using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ECommons.DalamudServices;

namespace AutoCollector.Diagnostics;

public enum AnomalySeverity
{
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
        lock (this.gate)
        {
            this.entries.Add(new AnomalyEntry(DateTime.Now, severity, category, message));
            if (this.entries.Count > MaxEntries)
            {
                this.entries.RemoveRange(0, this.entries.Count - MaxEntries);
            }
        }

        var line = $"[{category}] {message}";
        switch (severity)
        {
            case AnomalySeverity.Error:
                Svc.Log.Error(line);
                break;
            case AnomalySeverity.Warning:
                Svc.Log.Warning(line);
                break;
            default:
                Svc.Log.Information(line);
                break;
        }
    }
}
