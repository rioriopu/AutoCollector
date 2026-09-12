using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Hooking;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Diagnostics;

/// <summary>記録した callback 1 件分。</summary>
public sealed record CallbackRecord(DateTime At, string AddonName, bool UpdateState, IReadOnlyList<string> Values)
{
    public string Signature => $"Fire({string.Join(", ", this.Values)})";
}

/// <summary>
/// アドオンの FireCallback を監視して、引数を記録する。
///
/// 目的は「自分が撃つ前に、正しい引数を実測すること」。
/// 交換 callback の第 1 引数の意味は、参照したどのソースにも記述がない。
/// 推測で撃つのではなく、ユーザーが手動で 1 回交換したときの実際の引数を見てから実装する。
///
/// このクラスは記録専用で、callback の内容を書き換えることは一切しない。
/// </summary>
public sealed unsafe class CallbackRecorder : IDisposable
{
    private const int MaxRecords = 60;

    private readonly AnomalyLog anomalyLog;
    private readonly List<CallbackRecord> records = [];
    private readonly Lock gate = new();

    private Hook<AtkUnitBase.Delegates.FireCallback>? hook;

    public CallbackRecorder(AnomalyLog anomalyLog)
    {
        this.anomalyLog = anomalyLog;
    }

    /// <summary>記録対象のアドオン名。空なら全アドオン。</summary>
    public string AddonFilter { get; set; } = "ShopExchangeCurrency";

    /// <summary>
    /// 記録の開始時と停止時に通貨の所持数を控える差分表示のための入れ物。
    ///
    /// 「納品の直前と直後のスクリップ所持数」を人に数えさせないために持つ。
    /// 記録を止めた時点で、増えた通貨がそのまま出る。
    /// </summary>
    private IReadOnlyList<(uint ItemId, string Name, int Count)> currencyAtStart = [];

    private IReadOnlyList<(uint ItemId, string Name, int Count)> currencyAtStop = [];

    /// <summary>通貨の所持数を読む手段。差分を取るために外から渡す。</summary>
    public Func<IReadOnlyList<(uint ItemId, string Name, int Count)>>? CurrencySampler { get; set; }

    public bool IsRecording => this.hook?.IsEnabled == true;

    public void Start()
    {
        this.currencyAtStart = this.SampleCurrency();
        this.currencyAtStop = [];

        try
        {
            this.hook ??= Svc.Hook.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
                AtkUnitBase.MemberFunctionPointers.FireCallback,
                this.Detour);

            if (!this.hook.IsEnabled)
            {
                this.hook.Enable();
                this.anomalyLog.Info("Callback", "callback の記録を開始しました");
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Callback", $"記録を開始できませんでした: {ex.Message}");
        }
    }

    public void Stop()
    {
        this.currencyAtStop = this.SampleCurrency();


        try
        {
            if (this.hook?.IsEnabled == true)
            {
                this.hook.Disable();
                this.anomalyLog.Info("Callback", "callback の記録を停止しました");
            }
        }
        catch (Exception ex)
        {
            this.anomalyLog.Error("Callback", $"記録を停止できませんでした: {ex.Message}");
        }
    }

    private IReadOnlyList<(uint ItemId, string Name, int Count)> SampleCurrency()
    {
        try
        {
            return this.CurrencySampler?.Invoke() ?? [];
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Record", $"通貨の所持数を控えられませんでした: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 記録した内容をファイルへ書き出す。
    /// 通貨の増減も一緒に出すので、手で数える必要がない。
    /// </summary>
    public string Save(string directory)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== callback の記録（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）===");
        sb.AppendLine($"記録対象: {(string.IsNullOrWhiteSpace(this.AddonFilter) ? "全アドオン" : this.AddonFilter)}");
        sb.AppendLine();

        var list = this.Snapshot();
        sb.AppendLine($"-- 記録した callback（{list.Count} 件）--");

        if (list.Count == 0)
        {
            sb.AppendLine("1 件も記録されていません。");
            sb.AppendLine("これ自体が結論になります。ボタンを物理的に押す経路か、");
            sb.AppendLine("FireCallback とは別の関数を通っていることを意味します。");
        }
        else
        {
            foreach (var record in list)
            {
                sb.AppendLine($"{record.At:HH:mm:ss.fff} {record.AddonName} updateState={record.UpdateState} {record.Signature}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("-- 通貨の増減（記録の開始時 → 停止時）--");

        if (this.currencyAtStart.Count == 0 || this.currencyAtStop.Count == 0)
        {
            sb.AppendLine("所持数を控えられませんでした。");
        }
        else
        {
            var before = this.currencyAtStart.ToDictionary(x => x.ItemId, x => x);
            var changed = 0;

            foreach (var after in this.currencyAtStop)
            {
                if (!before.TryGetValue(after.ItemId, out var start) || start.Count == after.Count)
                {
                    continue;
                }

                changed++;
                sb.AppendLine($"{after.Name}（ItemId {after.ItemId}）: {start.Count:N0} → {after.Count:N0}（{after.Count - start.Count:+#;-#;0}）");
            }

            if (changed == 0)
            {
                sb.AppendLine("増減した通貨はありません。");
            }
        }

        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"Callback_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
        return path;
    }

    public IReadOnlyList<CallbackRecord> Snapshot()
    {
        lock (this.gate)
        {
            return this.records.ToArray();
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.records.Clear();
        }
    }

    private bool Detour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool updateState)
    {
        // 何よりも先に本来の処理を通す。記録が失敗しても操作を壊さない。
        var result = this.hook!.Original(addon, valueCount, values, updateState);

        try
        {
            if (addon is not null)
            {
                var name = GenericHelpers.Read(addon->Name);
                if (string.IsNullOrEmpty(this.AddonFilter) || name == this.AddonFilter)
                {
                    this.Record(name, valueCount, values, updateState);
                }
            }
        }
        catch
        {
            // 記録の失敗でゲーム側の処理に影響を出さない。
        }

        return result;
    }

    private void Record(string addonName, uint valueCount, AtkValue* values, bool updateState)
    {
        // 上限は 64。収集品納品のように値が多いアドオンで、途中で切れると判断材料にならない。
        const uint Max = 64u;
        var decoded = new List<string>((int)Math.Min(valueCount, Max));

        for (var i = 0u; i < valueCount && i < Max; i++)
        {
            decoded.Add(Describe(values[i]));
        }

        lock (this.gate)
        {
            this.records.Add(new CallbackRecord(DateTime.Now, addonName, updateState, decoded));
            if (this.records.Count > MaxRecords)
            {
                this.records.RemoveRange(0, this.records.Count - MaxRecords);
            }
        }
    }

    private static string Describe(AtkValue value)
    {
        return value.Type switch
        {
            AtkValueType.Int => value.Int.ToString(),
            AtkValueType.UInt => $"{value.UInt}u",
            AtkValueType.Bool => value.Byte != 0 ? "true" : "false",
            AtkValueType.Float => value.Float.ToString("0.###"),
            AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8 => ReadString(value),
            0 => "(なし)",
            _ => $"[{value.Type}]",
        };
    }

    /// <summary>
    /// 文字列の値を読む。どの行が何を指しているかは、文字列が読めないと判断できない。
    /// </summary>
    private static unsafe string ReadString(AtkValue value)
    {
        try
        {
            if (value.String.Value is null)
            {
                return "\"\"";
            }

            var text = value.String.ToString();
            return string.IsNullOrEmpty(text) ? "\"\"" : $"\"{text}\"";
        }
        catch
        {
            return "[String]";
        }
    }

    public void Dispose()
    {
        try
        {
            this.hook?.Dispose();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[Auto Collector] callback フックの解放に失敗しました: {ex}");
        }

        this.hook = null;
    }
}
