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
          { "Track": "Sync",    "Option": "None" },
          { "Track": "Chocobo", "Option": "Disabled" }
        ],
        "BossMod.Autorotation.MiscAI.NormalMovement": [
          { "Track": "Destination", "Option": "Pathfind" }
        ]
      }
    }
    """;

    /// <summary>
    /// プリセットを用意する。すでに同じ名前があれば作り直さない。
    ///
    /// <b>上書きしない。</b>利用者が中身を調整していることがあるため、
    /// こちらから毎回書き戻すと、その調整が消える。
    /// </summary>
    /// <returns>用意できたら true。</returns>
    public static bool Ensure(BossModIpc bossMod, AnomalyLog anomalyLog)
    {
        if (!bossMod.IsLoaded)
        {
            return false;
        }

        // すでにあるなら触らない。
        if (bossMod.TryGetPreset(Name, out var existing) && !string.IsNullOrEmpty(existing))
        {
            return true;
        }

        if (bossMod.TryCreatePreset(Serialized, overwrite: false, out var created) && created)
        {
            anomalyLog.Info("Fate", $"BossMod Reborn にプリセット「{Name}」を作成しました");
            return true;
        }

        anomalyLog.Warn("Fate", $"BossMod Reborn にプリセット「{Name}」を作成できませんでした");
        return false;
    }
}
