using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace AutoCollector.Diagnostics;

/// <summary>
/// FATE 周回の動きを追えるように記録する。
///
/// <b>利用者に説明させないための記録。</b>
/// 「降りずにまた上がる」のような症状は、どの判断でそうなったかが
/// 分からないと直せない。状態と、そのときの条件を残す。
///
/// <b>毎フレーム同じ行を出さない。</b>
/// 60fps で流すと、読むどころか肝心の変化が埋もれる。
/// 内容が変わったときと、一定の間隔でだけ書く。
/// </summary>
public sealed class FateTrace(AnomalyLog anomalyLog)
{
    /// <summary>同じ内容でも、これだけ経ったら書き直す。止まっていることも情報になる。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly AnomalyLog anomalyLog = anomalyLog;

    private string lastLine = string.Empty;
    private DateTime lastWroteUtc = DateTime.MinValue;

    /// <summary>
    /// 記録を残すか。
    ///
    /// <b>周回が動いているあいだは常に残す。</b>
    /// 設定で切れるようにしていたが、それでは
    /// 「動かない」と言われたときに何も手がかりが無い。
    /// 間引いてあるので出続けても邪魔にならない。
    /// </summary>
    public static bool Enabled => true;

    /// <summary>
    /// いまの状態を書く。内容が前回と同じなら、しばらく黙る。
    /// </summary>
    /// <param name="step">周回の段階。</param>
    /// <param name="detail">その段階で何をしているか。</param>
    public void State(string step, string detail)
    {
        if (!Enabled)
        {
            return;
        }

        var line = $"[{step}] {detail} | {Describe()}";

        var now = DateTime.UtcNow;
        if (line == this.lastLine && now - this.lastWroteUtc < HeartbeatInterval)
        {
            return;
        }

        this.lastLine = line;
        this.lastWroteUtc = now;
        this.anomalyLog.Info("FateTrace", line);
    }

    /// <summary>
    /// 判断の分かれ目を書く。こちらは間引かない。
    ///
    /// 「なぜそちらへ進んだか」は 1 回しか起きないので、
    /// 埋もれる心配より取りこぼす心配のほうが大きい。
    /// </summary>
    public void Decision(string what, string why)
    {
        if (!Enabled)
        {
            return;
        }

        this.anomalyLog.Info("FateTrace", $"→ {what}: {why} | {Describe()}");
    }

    /// <summary>予定どおりに進まなかったことを書く。詳細ログを切っていても残す。</summary>
    public void Trouble(string what, string detail)
        => this.anomalyLog.Warn("Fate", $"{what}: {detail} | {Describe()}");

    /// <summary>
    /// いまのキャラクターの状態を 1 行にまとめる。
    ///
    /// <b>降りない・上がり直すといった症状は、ここを見れば分かる。</b>
    /// 乗っているか、飛んでいるか、どこに居るか、何に妨げられているか。
    /// </summary>
    public static string Describe()
    {
        try
        {
            if (!Player.Available)
            {
                return "プレイヤー不在";
            }

            var p = Player.Position;
            var pos = $"({p.X:F0},{p.Y:F0},{p.Z:F0})";

            var flags = string.Join(",", DescribeFlags());

            // **誰の記録かを必ず書く。**
            // ゲームを複数起動していると、記録は 1 つのファイルに混ざる。
            // 名前が無いと、どの画面で起きたことか分からない。
            return $"{Player.Name} {pos} {(flags.Length == 0 ? "地上" : flags)}";
        }
        catch (Exception ex)
        {
            return $"状態を読めず: {ex.Message}";
        }
    }

    /// <summary>目的地との距離を水平と垂直に分けて書く。飛行中の判断に効く。</summary>
    public static string DescribeDistance(Vector3 destination)
    {
        try
        {
            if (!Player.Available)
            {
                return "距離不明";
            }

            var p = Player.Position;
            var flat = Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(destination.X, destination.Z));
            var dy = p.Y - destination.Y;
            return $"水平{flat:F0}m 高度差{dy:+0;-0;0}m";
        }
        catch
        {
            return "距離不明";
        }
    }

    private static System.Collections.Generic.IEnumerable<string> DescribeFlags()
    {
        var c = Svc.Condition;

        if (c[ConditionFlag.Mounted]) yield return "騎乗";
        if (c[ConditionFlag.InFlight]) yield return "飛行";
        if (c[ConditionFlag.Mounting] || c[ConditionFlag.Mounting71]) yield return "騎乗中";
        if (c[ConditionFlag.InCombat]) yield return "戦闘";
        if (c[ConditionFlag.Unconscious]) yield return "戦闘不能";
        if (c[ConditionFlag.Jumping] || c[ConditionFlag.Jumping61]) yield return "跳躍";
        if (c[ConditionFlag.Casting]) yield return "詠唱";
        if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]) yield return "エリア移動";
        if (c[ConditionFlag.Diving]) yield return "潜水";
        if (c[ConditionFlag.Swimming]) yield return "遊泳";
        if (Player.IsAnimationLocked) yield return "動作中";
        if (!Player.Interactable) yield return "操作不可";
    }
}
