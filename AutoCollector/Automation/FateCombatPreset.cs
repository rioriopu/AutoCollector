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
/// <b>ジョブのローテーションも入れる。</b>
/// AutoTarget は的を決めるだけ、NormalMovement は動くだけで、
/// 技を撃つのはジョブごとのモジュール。これが無いと、敵を狙ったまま
/// 何もしない。
///
/// 全ジョブ分を並べてあるが、BMR は自分のジョブに合わないモジュールを
/// 自動で外す（RotationModuleManager.RebuildActiveModules が
/// def.Classes[player.Class] で弾く）。そのため全部書いて構わない。
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
        ],
        "BossMod.Autorotation.xan.PLD": [],
        "BossMod.Autorotation.VeynWAR": [],
        "BossMod.Autorotation.xan.DRK": [],
        "BossMod.Autorotation.xan.GNB": [],
        "BossMod.Autorotation.xan.WHM": [],
        "BossMod.Autorotation.xan.SCH": [],
        "BossMod.Autorotation.xan.AST": [],
        "BossMod.Autorotation.xan.SGE": [],
        "BossMod.Autorotation.xan.MNK": [],
        "BossMod.Autorotation.xan.DRG": [],
        "BossMod.Autorotation.xan.NIN": [],
        "BossMod.Autorotation.xan.SAM": [],
        "BossMod.Autorotation.xan.RPR": [],
        "BossMod.Autorotation.xan.VPR": [],
        "BossMod.Autorotation.xan.BRD": [],
        "BossMod.Autorotation.xan.MCH": [],
        "BossMod.Autorotation.xan.DNC": [],
        "BossMod.Autorotation.xan.BLM": [],
        "BossMod.Autorotation.xan.SMN": [],
        "BossMod.Autorotation.xan.RDM": [],
        "BossMod.Autorotation.xan.PCT": [],
        "BossMod.Autorotation.xan.BLU": [],
        "BossMod.Autorotation.xan.BST": [],
        "BossMod.Autorotation.xan.TankAI": [],
        "BossMod.Autorotation.xan.HealerAI": [],
        "BossMod.Autorotation.xan.MeleeAI": [],
        "BossMod.Autorotation.xan.RangedAI": [],
        "BossMod.Autorotation.xan.Caster": []
      }
    }
    """;

    /// <summary>
    /// このプリセットが必ず持っていなければならない設定。
    ///
    /// <b>既定値のトラックはここに入れない。</b>
    /// BMR は「その enum の既定値（＝0 番）」のトラックを書き出さない。
    /// FateUtils の Flag は <c>{ Enabled, Disabled }</c> で <b>Enabled が 0</b> なので、
    /// Handin = Enabled は保存された JSON に現れない。
    /// これを必須に入れていたため、毎回「入っていない」と判定して
    /// 起動のたびにプリセットを作り直していた（2026-09-25 実測）。
    ///
    /// Collect = Disabled（1 番）と Sync = Enable（None が 0 番なので 1 番）は
    /// 既定値ではないため、書き出される。
    /// </summary>
    /// <summary>
    /// 入っていてはいけない設定。
    ///
    /// 既定値のトラックは、正しい設定なら JSON に現れない。
    /// 現れているということは、明示的に既定と違う値が保存されている。
    /// </summary>
    private static readonly (string Track, string Option)[] Forbidden =
    [
        ("Handin", "Disabled"),
    ];

    private static readonly (string Track, string Option)[] Required =
    [
        ("Sync", "Enable"),
        ("Collect", "Disabled"),
    ];

    /// <summary>
    /// 入っていなければならないモジュール。
    ///
    /// <b>ジョブのローテーションが無いと、狙うだけで技を撃たない。</b>
    /// AutoTarget は的を決めるだけ、NormalMovement は動くだけ。
    /// 技を撃つのはジョブごとのモジュール。
    /// これが抜けていて、敵を狙ったまま何もしなかった（2026-09-25 実測）。
    ///
    /// 代表として侍を見る。1 つでも入っていれば、他も同時に書き込まれている。
    /// </summary>
    private const string RequiredModule = "BossMod.Autorotation.xan.SAM";

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
        if (!serialized.Contains(RequiredModule, StringComparison.Ordinal))
        {
            return "ジョブのローテーション";
        }

        // **「入っていてはいけない値」も見る。**
        //
        // Handin は既定値（Enabled）なので、正しい設定では JSON に現れない。
        // そのため Required に入れても照合できない。
        // ところが明示的に Disabled で保存されたプリセットは
        // JSON に現れるため、こちらで拾える。
        //
        // 拾えないと、納品 FATE で BMR が納品へ向かわないまま
        // 「プリセットは正しい」と判断してしまう。
        foreach (var (track, option) in Forbidden)
        {
            var trackAt = serialized.IndexOf($"\"{track}\"", StringComparison.Ordinal);
            if (trackAt < 0)
            {
                continue;
            }

            var optionAt = serialized.IndexOf($"\"{option}\"", trackAt, StringComparison.Ordinal);
            var nextTrackAt = serialized.IndexOf("\"Track\"", trackAt + 1, StringComparison.Ordinal);

            if (optionAt >= 0 && (nextTrackAt < 0 || optionAt < nextTrackAt))
            {
                return $"{track} が {option} になっています";
            }
        }

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
