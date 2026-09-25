using System;
using AutoCollector.Diagnostics;
using AutoCollector.Ipc;

namespace AutoCollector.Automation;

/// <summary>
/// FATE 周回に使う BossMod Reborn のプリセットを用意する。
///
/// <b>利用者にプリセットを作らせない。</b>
/// どれを選べばよいか分からないのが普通なので、こちらで作って設定する。
///
/// 目指す挙動:
/// <list type="bullet">
/// <item>FATE の中のモンスターは自分から攻撃しに行く</item>
/// <item>FATE 以外のモンスターには自分から絡まない</item>
/// <item>ただし攻撃を受けたら殴り返す</item>
/// </list>
///
/// <b>この 3 つは BMR の AutoTarget でそのまま表現できる。</b>
/// 実ソース（BossModule/AIHintsBuilder.cs:148）で、敵の優先度が
/// 次のように決まることを確認した:
///
/// <code>
/// actor.AggroPlayer ? (0, "aggro table")   // 敵視リストに載っている = 優先度 0
/// : ...
/// : (priorityPassive, "passive")           // それ以外 = -3（PriorityUndesirable）
/// </code>
///
/// AutoTarget の Execute（MiscAI/AutoTarget.cs:180〜）は、
/// Everything が無効なら優先度 -3 の敵を狙わず、
/// 最後に <c>target.Priority &gt;= 0</c> の敵だけを拾う。
///
/// つまり <c>General=Aggressive</c> ＋ <c>FATE=Enabled</c> ＋ <c>Everything=Disabled</c> で
/// 「FATE の敵は自分から、それ以外は絡まれたら反撃」になる。
///
/// <b>General=Passive は使えない。</b>
/// Passive は Execute の冒頭で即 return するため、反撃もしない。
/// </summary>
public static class FateCombatPreset
{
    /// <summary>こちらで作るプリセットの名前。</summary>
    public const string Name = "AutoCollector FATE";

    /// <summary>
    /// プリセットの中身。
    ///
    /// 形式は BMR の JsonPresetConverter（Autorotation/Preset.cs:89）に合わせてある。
    /// Modules のキーは型の FullName、Track と Option は enum の名前そのもの。
    ///
    /// <b>ここに書いていないトラックは BMR の既定のままになる。</b>
    /// 触る必要のないものは書かない。
    /// </summary>
    private const string Serialized = """
    {
      "Name": "AutoCollector FATE",
      "Modules": {
        "BossMod.Autorotation.MiscAI.AutoTarget": [
          { "Track": "General",    "Option": "Aggressive" },
          { "Track": "Retarget",   "Option": "Hostiles" },
          { "Track": "FATE",       "Option": "Enabled" },
          { "Track": "Everything", "Option": "Disabled" },
          { "Track": "Hunt",       "Option": "Disabled" },
          { "Track": "Treasure",   "Option": "Disabled" }
        ],
        "BossMod.Autorotation.MiscAI.FateUtils": [
          { "Track": "Handin",  "Option": "Enabled" },
          { "Track": "Collect", "Option": "Disabled" },
          { "Track": "Sync",    "Option": "Enable" },
          { "Track": "Chocobo", "Option": "Disabled" }
        ],
        "BossMod.Autorotation.MiscAI.NormalMovement": [
          { "Track": "Destination", "Option": "Pathfind" }
        ]
      }
    }
    """;

    /// <summary>
    /// このプリセットが必ず持っていなければならない設定。
    ///
    /// <b>ここが欠けていると FATE 周回が成り立たない。</b>
    /// 古い版で作ったプリセットが残っていると、直したはずの設定が
    /// 効かないまま動く。実際にそうなった（Sync を None で作っていた版が
    /// 残り、レベルシンクが入らなかった・2026-09-25）。
    /// </summary>
    private static readonly (string Track, string Option)[] Required =
    [
        ("Sync", "Enable"),
        ("Handin", "Enabled"),
        ("Collect", "Disabled"),
    ];

    /// <summary>
    /// プリセットを用意する。
    ///
    /// <b>中身が古ければ作り直す。</b>
    /// 利用者の調整を消さないよう、ふだんは触らない。ただし
    /// 周回に欠かせない設定が入っていなければ、そのままでは動かないので
    /// 作り直して記録に残す。
    /// </summary>
    /// <returns>用意できたら true。</returns>
    public static bool Ensure(BossModIpc bossMod, AnomalyLog anomalyLog)
    {
        if (!bossMod.IsLoaded)
        {
            return false;
        }

        var hasExisting = bossMod.TryGetPreset(Name, out var existing) && !string.IsNullOrEmpty(existing);

        if (hasExisting)
        {
            var missing = FindMissing(existing!);
            if (missing is null)
            {
                return true;
            }

            anomalyLog.Warn(
                "Fate",
                $"プリセット「{Name}」に {missing} が入っていないため作り直します");
        }

        if (bossMod.TryCreatePreset(Serialized, overwrite: true, out var created) && created)
        {
            anomalyLog.Info("Fate", $"BossMod Reborn にプリセット「{Name}」を{(hasExisting ? "作り直しました" : "作成しました")}");
            return true;
        }

        // 作り直せなくても、すでにあるなら止めない。
        // 足りない設定があると伝えたうえで、動くところまでは動かす。
        if (hasExisting)
        {
            anomalyLog.Warn("Fate", $"プリセット「{Name}」を作り直せませんでした。BossMod Reborn 側で確認してください");
            return true;
        }

        anomalyLog.Warn("Fate", $"BossMod Reborn にプリセット「{Name}」を作成できませんでした");
        return false;
    }

    /// <summary>
    /// 欠けている設定を 1 つ返す。すべて揃っていれば null。
    ///
    /// 文字列として含まれるかだけを見る。BMR が返すのは JSON なので、
    /// 解析せずとも「そのトラックがその値になっているか」は判る。
    /// </summary>
    private static string? FindMissing(string serialized)
    {
        foreach (var (track, option) in Required)
        {
            // "Track": "Sync" のすぐ後ろに "Option": "Enable" が来る形。
            // 間の空白は BMR の書き方次第なので、両方が含まれることと、
            // 並びが逆転していないことだけを見る。
            var trackAt = serialized.IndexOf($"\"{track}\"", StringComparison.Ordinal);
            if (trackAt < 0)
            {
                return $"{track}";
            }

            var optionAt = serialized.IndexOf($"\"{option}\"", trackAt, StringComparison.Ordinal);
            var nextTrackAt = serialized.IndexOf("\"Track\"", trackAt + 1, StringComparison.Ordinal);

            if (optionAt < 0 || (nextTrackAt >= 0 && optionAt > nextTrackAt))
            {
                return $"{track} = {option}";
            }
        }

        return null;
    }
}
