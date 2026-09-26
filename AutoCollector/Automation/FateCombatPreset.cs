using System;
using System.Text.Json;
using System.Text.Json.Nodes;
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
          { "Track": "Retarget",   "Option": "NoTarget" },
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
    private const string ModuleAutoTarget = "BossMod.Autorotation.MiscAI.AutoTarget";
    private const string ModuleFateUtils = "BossMod.Autorotation.MiscAI.FateUtils";
    private const string ModuleNormalMovement = "BossMod.Autorotation.MiscAI.NormalMovement";

    private static readonly (string Module, string Track, string Option)[] Forbidden =
    [
        (ModuleFateUtils, "Handin", "Disabled"),

        // **的を勝手に変えさせない。**
        // NoTarget（既定・0 番）以外が書かれていたら直す。
        // 既定値は書き出されないため、「入っていない」＝正しい。
        (ModuleAutoTarget, "Retarget", "Hostiles"),
        (ModuleAutoTarget, "Retarget", "Always"),

        // **納品前から討伐を止めさせない。**
        //
        // CollectFATE=Enabled は、納品 FATE で targetFateMobs を
        // 無条件に false にする（AutoTarget.cs:167-168）。所持数は見ない。
        // 0 個の段階から自分では敵を拾わなくなるため、集まらない。
        //
        // Disabled（既定・0 番）が正しい。162 行で
        // targetFateMobs = Progress < 100 となり、171 行は |= なので
        // false へ落とす力が無い。つまり討伐は止まらない。
        // 10 個たまったあとの納品は、こちらの TickHandIn が面倒を見る。
        (ModuleAutoTarget, "CollectFATE", "Enabled"),
    ];

    private static readonly (string Module, string Track, string Option)[] Required =
    [
        (ModuleFateUtils, "Sync", "Enable"),
        (ModuleFateUtils, "Collect", "Disabled"),

        // 敵を自分から狙いに行く。Passive では反撃もしない。
        (ModuleAutoTarget, "General", "Aggressive"),

        // FATE の敵は自分から狙う。
        (ModuleAutoTarget, "FATE", "Enabled"),

        // FATE 以外の敵には自分から絡まない。
        (ModuleAutoTarget, "Everything", "Disabled"),

        // 移動はプリセットに任せる。これが無いと敵へ近づかない。
        (ModuleNormalMovement, "Destination", "Pathfind"),

        // **撃破するまで的を変えない。**
        // Hostiles は「味方を狙っていなければ切り替えてよい」なので、
        // 生きている敵と戦っている最中でも、より優先度の高い敵が現れると
        // そちらへ移る。NoTarget は「的が無いときだけ選び直す」。
        // AutoTarget.cs の changeTarget を参照。
        //
        // <b>NoTarget は enum の 0 番なので、必須に入れられない。</b>
        // 既定値のトラックは JSON に書き出されないため、
        // 正しく NoTarget になっていても「入っていない」と読めてしまう。
        // そのため Required からは外し、Forbidden 側で
        // 「Hostiles や Always になっていないか」を見る。
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
    /// <b>JSON として解析する。</b>
    /// 以前は文字列として含まれるかだけを見ていた。そのため
    /// <list type="bullet">
    /// <item>どのモジュールのトラックなのかを区別できない
    ///       （AutoTarget の Collect と FateUtils の Collect が同じに見える）</item>
    /// <item>"Track" の次の "Track" までを 1 件と数えるため、
    ///       モジュールの境目をまたいで照合してしまう</item>
    /// </list>
    /// という穴があった。
    ///
    /// <b>既定値は書き出されないことを前提にする。</b>
    /// BMR のプリセット編集画面は、既定値に戻したトラックを
    /// 設定一覧から外す（UIPresetEditor.cs:269-271）。
    /// そのため「書かれていない」＝「既定値」であり、不備ではない。
    /// 必須に入れてよいのは、既定値と違う値だけ。
    /// </summary>
    private static string? FindMissing(string serialized)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(serialized);
        }
        catch (JsonException)
        {
            // 読めないものは直しようがない。作り直す。
            return "読める形をしていません";
        }

        if (root?["Modules"] is not JsonObject modules)
        {
            return "モジュールの一覧";
        }

        // ジョブのローテーションが無いと、狙うだけで技を撃たない。
        if (!modules.ContainsKey(RequiredModule))
        {
            return "ジョブのローテーション";
        }

        foreach (var (module, track, option) in Required)
        {
            if (!modules.TryGetPropertyValue(module, out var node) || node is not JsonArray settings)
            {
                return $"{Short(module)}";
            }

            if (FindOption(settings, track) != option)
            {
                return $"{Short(module)} の {track} = {option}";
            }
        }

        // **「入っていてはいけない値」も見る。**
        //
        // FateUtils の Flag は { Enabled, Disabled } で Enabled が 0 番、
        // つまり既定値。正しい設定では Handin は JSON に現れない。
        // そのため必須に入れても照合できない。
        // ところが明示的に Disabled で保存されていれば現れるので、こちらで拾う。
        //
        // 拾えないと、納品 FATE で BMR が納品へ向かわないまま
        // 「プリセットは正しい」と判断してしまう。
        foreach (var (module, track, option) in Forbidden)
        {
            if (!modules.TryGetPropertyValue(module, out var node) || node is not JsonArray settings)
            {
                continue;
            }

            if (FindOption(settings, track) == option)
            {
                return $"{Short(module)} の {track} が {option} になっています";
            }
        }

        return null;
    }

    /// <summary>
    /// そのモジュールの設定一覧から、トラックの値を取り出す。
    ///
    /// 書かれていなければ null。<b>null は「既定値」を意味する。</b>
    /// 不備とは限らない。
    /// </summary>
    private static string? FindOption(JsonArray settings, string track)
    {
        foreach (var setting in settings)
        {
            if (setting is not JsonObject entry)
            {
                continue;
            }

            if (entry["Track"]?.GetValue<string>() != track)
            {
                continue;
            }

            return entry["Option"]?.GetValue<string>();
        }

        return null;
    }

    /// <summary>モジュールの型名から、末尾の短い名前だけを取る。</summary>
    private static string Short(string module)
    {
        var at = module.LastIndexOf('.');
        return at >= 0 && at < module.Length - 1 ? module[(at + 1)..] : module;
    }
}
