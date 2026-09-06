using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ECommons.DalamudServices;

namespace AutoCollector.Diagnostics;

/// <summary>
/// ログをファイルへ書き出す。
///
/// 保存先はネットワーク共有を想定している。共有が落ちていたり応答が遅かったりすると
/// 書き込みが数秒単位で止まることがあるため、Framework スレッドからは絶対に書かない。
/// 呼び出し側はキューへ積むだけで即座に戻り、実際の書き込みは背景スレッドが行う。
///
/// 書けなくてもプラグイン本体の動作は一切変えない。
/// ログが取れないことを理由に交換を止めるのは本末転倒だからである。
/// </summary>
public sealed class FileLogWriter : IDisposable
{
    /// <summary>キューに溜められる上限。共有が落ちている間に無限に溜まるのを防ぐ。</summary>
    private const int MaxQueued = 20000;

    /// <summary>続けてこの回数書き込みに失敗したら諦める。毎回試すと落ちた共有を叩き続けることになる。</summary>
    private const int FailureLimit = 5;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentQueue<string> queue = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;

    private int dropped;
    private int consecutiveFailures;

    public FileLogWriter(string directory)
    {
        this.Directory = directory;
        this.FilePath = Path.Combine(
            directory,
            $"AutoCollector_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        this.worker = Task.Run(() => this.RunAsync(this.cancellation.Token));
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>書き込みを諦めた状態か。UI へ出して、ログが残っていないことに気付けるようにする。</summary>
    public bool Failed => Volatile.Read(ref this.consecutiveFailures) >= FailureLimit;

    /// <summary>直近の失敗理由。空なら失敗していない。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>キューが溢れて捨てた行数。</summary>
    public int DroppedLines => Volatile.Read(ref this.dropped);

    /// <summary>1 行積む。呼び出し側のスレッドは待たされない。</summary>
    public void Write(string line)
    {
        if (this.Failed)
        {
            return;
        }

        if (this.queue.Count >= MaxQueued)
        {
            // 溢れた分は捨てる。古い方を残す（問題の始まりが知りたいため）。
            Interlocked.Increment(ref this.dropped);
            return;
        }

        this.queue.Enqueue(line);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(FlushInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                this.Flush();
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] ログ書き込みスレッドが停止しました: {ex}");
        }

        // 終了時の取りこぼしを残す。
        this.Flush();
    }

    private void Flush()
    {
        if (this.queue.IsEmpty || this.Failed)
        {
            return;
        }

        var batch = new List<string>();
        while (batch.Count < MaxQueued && this.queue.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(this.Directory);
            File.AppendAllLines(this.FilePath, batch, Encoding.UTF8);

            Volatile.Write(ref this.consecutiveFailures, 0);
            this.LastError = string.Empty;
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref this.consecutiveFailures);
            this.LastError = ex.Message;

            if (failures == 1 || failures == FailureLimit)
            {
                Svc.Log.Warning(
                    failures >= FailureLimit
                        ? $"[Auto Collector] ログを {this.FilePath} へ書き込めないため、記録を諦めます: {ex.Message}"
                        : $"[Auto Collector] ログを {this.FilePath} へ書き込めませんでした: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            this.cancellation.Cancel();

            // 落ちている共有を待ち続けないよう、待つ時間に上限を設ける。
            this.worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Auto Collector] ログ書き込みの停止で例外が出ました: {ex.Message}");
        }
        finally
        {
            this.cancellation.Dispose();
        }
    }
}
