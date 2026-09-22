using AutoCollector.Diagnostics;

namespace AutoCollector.Ipc;

/// <summary>
/// BossMod Reborn（BMR）との連携。FATE 周回の戦闘を任せる。
///
/// 【IPC 名の前置詞と InternalName が食い違う】
///
/// BMR の IPC 登録は BossmodReborn/BossMod/Framework/IPCProvider.cs:578 で
///
///     Service.PluginInterface.GetIpcProvider&lt;TRet&gt;("BossMod." + name)
///
/// となっており、派生版でも前置詞は <b>BossMod.</b> のまま。
/// 一方、導入判定に使う InternalName は <b>BossModReborn</b>。
/// この 2 つが食い違うため、基底クラスへ渡す名前（InternalName）と
/// IPC 名の前置詞を別々に持つ必要がある。
///
/// 本家 BossMod（InternalName: BossMod）も同じ IPC を公開しているが、
/// ユーザー指定により BMR を対象にする。
///
/// 実ソースで確認した版: 615b38b54（2026-09-21）。
/// 公開 IPC は 62 件で、ここで使う 8 件はすべて実在する。
/// </summary>
public sealed class BossModIpc(AnomalyLog anomalyLog) : IpcGateBase("BossModReborn", anomalyLog)
{
    /// <summary>IPC 名の前置詞。InternalName とは違う値になる（クラス説明を参照）。</summary>
    private const string Prefix = "BossMod.";

    // ---- プリセット（戦闘の有効・無効） ----

    /// <summary>いま有効な自動回転プリセットの名前。無効なら null が返る。</summary>
    public bool TryGetActivePreset(out string? name)
        => this.TryInvoke("Presets.GetActive", () => this.Func<string?>(Prefix + "Presets.GetActive").InvokeFunc(), out name);

    /// <summary>
    /// プリセットを有効にする。戻り値の accepted は「そのプリセットが見つかって設定できたか」。
    ///
    /// 名前が存在しない場合は false が返る。こちらで名前を検証してから呼ぶこと。
    /// </summary>
    public bool TrySetActivePreset(string name, out bool accepted)
        => this.TryInvoke("Presets.SetActive", () => this.Func<string, bool>(Prefix + "Presets.SetActive").InvokeFunc(name), out accepted);

    /// <summary>
    /// プリセットを解除して戦闘を止める。
    ///
    /// FATE 完了時の離脱で必ず呼ぶ。これを呼ばないと BMR が敵を追い続け、
    /// その場から離れられない。
    /// </summary>
    public bool TryClearActivePreset(out bool cleared)
        => this.TryInvoke("Presets.ClearActive", () => this.Func<bool>(Prefix + "Presets.ClearActive").InvokeFunc(), out cleared);

    /// <summary>プリセットの定義（シリアライズされた文字列）。存在しなければ null。</summary>
    public bool TryGetPreset(string name, out string? serialized)
        => this.TryInvoke("Presets.Get", () => this.Func<string, string?>(Prefix + "Presets.Get").InvokeFunc(name), out serialized);

    // ---- 一時方針（プリセットを書き換えずにモジュールの挙動を変える） ----

    /// <summary>
    /// プリセットへ一時的な方針を足す。プリセット本体は書き換わらない。
    ///
    /// FATE 周回では FateUtils（FATE helper）と AutoTarget の挙動をここで決める。
    /// 引数は BMR 側の型名・トラック名・選択肢名をそのまま文字列で渡す。
    /// </summary>
    public bool TryAddTransientStrategy(string preset, string module, string track, string value, out bool accepted)
        => this.TryInvoke(
            "Presets.AddTransientStrategy",
            () => this.Func<string, string, string, string, bool>(Prefix + "Presets.AddTransientStrategy")
                      .InvokeFunc(preset, module, track, value),
            out accepted);

    /// <summary>一時方針を 1 件外す。</summary>
    public bool TryClearTransientStrategy(string preset, string module, string track, out bool cleared)
        => this.TryInvoke(
            "Presets.ClearTransientStrategy",
            () => this.Func<string, string, string, bool>(Prefix + "Presets.ClearTransientStrategy")
                      .InvokeFunc(preset, module, track),
            out cleared);

    /// <summary>あるプリセットに付いた一時方針をすべて外す。停止処理で使う。</summary>
    public bool TryClearTransientPresetStrategies(string preset, out bool cleared)
        => this.TryInvoke(
            "Presets.ClearTransientPresetStrategies",
            () => this.Func<string, bool>(Prefix + "Presets.ClearTransientPresetStrategies").InvokeFunc(preset),
            out cleared);

    // ---- AI（移動と索敵） ----

    /// <summary>AI が移動先を持っているか。移動が終わったかの判定に使う。</summary>
    public bool TryIsNavigating(out bool navigating)
        => this.TryInvoke("AI.IsNavigating", () => this.Func<bool>(Prefix + "AI.IsNavigating").InvokeFunc(), out navigating);

    /// <summary>
    /// AI の移動を止める／再開する。
    ///
    /// true を渡すと BMR は移動しなくなる（戦闘は続ける）。
    /// こちらが vnavmesh で移動させたい場面で、BMR と取り合いにならないようにする。
    /// <b>立てたら必ず戻すこと。</b>戻し忘れると BMR が二度と動かない。
    /// </summary>
    public bool TryPauseMovement(bool pause)
        => this.TryAction("AI.PauseMovement", () => this.Func<bool, object>(Prefix + "AI.PauseMovement").InvokeAction(pause));
}
