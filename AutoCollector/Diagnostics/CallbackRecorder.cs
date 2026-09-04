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

    public bool IsRecording => this.hook?.IsEnabled == true;

    public void Start()
    {
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
        var decoded = new List<string>((int)Math.Min(valueCount, 32u));

        for (var i = 0u; i < valueCount && i < 32u; i++)
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
            0 => "(なし)",
            _ => $"[{value.Type}]",
        };
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
