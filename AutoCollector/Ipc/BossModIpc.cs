using System;
using AutoCollector.Diagnostics;
using ECommons.DalamudServices;

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
/// 実ソースで確認した版: FFXIV-CombatReborn/BossmodReborn main <b>33456ced</b>（2026-10-09 に更新）。
/// ここで使う 9 件はすべて実在する。
///
/// <b>参照するクローンを間違えないこと。</b>
/// <c>TempRepos\BossmodReborn</c> は <c>ultimatepilot</c> ブランチ（UltimatePilot フォーク）で、
/// 本家には無い独自の IPC（<c>// --- Custom OmniDuty Endpoints ---</c>）が入っている。
/// 本家は <c>TempRepos\_src\BossmodReborn</c>（main）。
///
/// <b>登録が Action か Func かを、呼び出す前に必ず見ること。</b>
/// BMR の <c>Register</c> には Action と Func の多重定義があり、
/// ラムダの本体が値を持つと（代入式など）Func の側が選ばれる。
/// 見た目が「操作」でも <c>InvokeAction</c> では呼べない
/// （<see cref="TryPauseMovement"/> の説明を参照）。
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

    /// <summary>
    /// プリセットを作る。
    ///
    /// 渡すのは JSON。形式は BMR の JsonPresetConverter に従う
    /// （Modules のキーは型の FullName、Track と Option は enum の名前）。
    /// overwrite が false なら、同じ名前があるときは作らずに false を返す。
    /// </summary>
    public bool TryCreatePreset(string serialized, bool overwrite, out bool created)
        => this.TryInvoke(
            "Presets.Create",
            () => this.Func<string, bool, bool>(Prefix + "Presets.Create").InvokeFunc(serialized, overwrite),
            out created);

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
    /// BMR がいま「話しかけよう」としている相手。
    ///
    /// 納品 FATE では、FateUtils がここへ納品 NPC を立てる
    /// （FateUtils.cs:50 <c>Hints.InteractWithTarget = target</c>）。
    /// 納品が動き出しているかを外から確かめるのに使う。
    ///
    /// <b>名前は OID だが、返るのは InstanceID。</b>
    /// BMR 側は <c>hints.InteractWithTarget?.InstanceID ?? 0</c> を返している
    /// （Framework/IPCProvider.cs:195）。OID と読み替えて突き合わせると必ず外れる。
    ///
    /// <b>これが立っただけで納品できたことにしない。</b>
    /// 狙っているだけで、渡せたかどうかは別の話。
    /// 成立は所持数が減ったことで見る。
    /// </summary>
    public bool TryGetInteractTarget(out ulong instanceId)
        => this.TryInvoke(
            "Hints.InteractWithTargetOID",
            () => this.Func<ulong>(Prefix + "Hints.InteractWithTargetOID").InvokeFunc(),
            out instanceId);

    /// <summary>
    /// AI の移動を止める／再開する。
    ///
    /// true を渡すと BMR は移動しなくなる（戦闘は続ける）。
    /// こちらが vnavmesh で移動させたい場面で、BMR と取り合いにならないようにする。
    /// <b>立てたら必ず戻すこと。</b>戻し忘れると BMR が二度と動かない。
    ///
    /// 【これは Action ではなく Func で登録されている】
    ///
    /// BMR 側の登録は
    ///
    ///     Register("AI.PauseMovement", static (bool pause) =&gt;
    ///         Service.Config.Get&lt;AIConfig&gt;().ForbidMovement = pause);
    ///
    /// で、見た目は「値を返さない操作」だが、<b>代入式は代入した値を返す</b>。
    /// そのためラムダの戻り値型は bool と推論され、
    /// <c>Register&lt;T1&gt;(string, Action&lt;T1&gt;)</c> ではなく
    /// <c>Register&lt;T1, TRet&gt;(string, Func&lt;T1, TRet&gt;)</c> が選ばれる。
    /// 実体は <c>GetIpcProvider&lt;bool, bool&gt;(...).RegisterFunc(...)</c>。
    ///
    /// ここを <c>InvokeAction</c> で呼ぶと、Dalamud は
    /// 「IPC method BossMod.AI.PauseMovement has not been registered」を投げる
    /// （2026-10-09 実機で確認。警告が毎フレーム出ていた）。
    /// <c>InvokeFunc</c> で呼ぶこと。
    ///
    /// 実ソースで確認した版: FFXIV-CombatReborn/BossmodReborn main 33456ced
    /// （Framework/IPCProvider.cs:197、Register の多重定義は 575 行と 625 行）。
    /// </summary>
    /// <returns>送れたら true。戻り値そのものは渡した値がそのまま返るだけなので使わない。</returns>
    public bool TryPauseMovement(bool pause)
        => this.TryInvoke(
            "AI.PauseMovement",
            () => this.Func<bool, bool>(Prefix + "AI.PauseMovement").InvokeFunc(pause),
            out _);

    // ---- AI の有効化（IPC が無いのでコマンドで送る） ----

    /// <summary>
    /// AI そのものを有効・無効にする。
    ///
    /// <b>FATE 周回では AI を入れない（off を送るだけ）。</b>
    /// AI は有効なプリセットを自分の「AI 用プリセット」で上書きし、
    /// 動いている間はプリセットの移動（NormalMovement）を止める。
    /// on/off どちらも最初に有効なプリセットを null にするので、
    /// 送るならプリセットを入れる前に送ること（FateRunner.ApplyCombat を参照）。
    ///
    /// IPC には AI の ON/OFF が無いため、コマンドで送る。
    /// </summary>
    public bool TrySetAiEnabled(bool enabled)
        => this.TryProcessCommand($"/bmrai {(enabled ? "on" : "off")}");

    /// <summary>ターゲットを追いかけるか。これが無いと敵に近づかない。</summary>
    public bool TrySetFollowTarget(bool on)
        => this.TryProcessCommand($"/bmrai followtarget {(on ? "on" : "off")}");

    /// <summary>戦闘中も追いかけるか。</summary>
    public bool TrySetFollowCombat(bool on)
        => this.TryProcessCommand($"/bmrai followcombat {(on ? "on" : "off")}");

    /// <summary>
    /// 戦闘外でも追いかけるか。
    ///
    /// 近接職はこれを入れないと、戦闘が始まるまで動かない。
    /// </summary>
    public bool TrySetFollowOutOfCombat(bool on)
        => this.TryProcessCommand($"/bmrai followoutofcombat {(on ? "on" : "off")}");

    /// <summary>ターゲットへ近づく距離。近接なら短く、遠隔なら長く。</summary>
    public bool TrySetMaxDistanceToTarget(float meters)
        => this.TryProcessCommand($"/bmrai maxdistancetarget {meters:0.##}");

    /// <summary>
    /// 騎乗中は AI に動かせない。
    ///
    /// <b>入れないと飛べない。</b>BMR の既定（ForbidAIMovementMounted = false）では
    /// 騎乗していても AI が動かそうとする。こちらは vnavmesh で飛ばしているので、
    /// 両方が同時に動かそうとして取り合いになり、飛び上がれなくなる。
    /// </summary>
    public bool TrySetIdleWhileMounted(bool on)
        => this.TryProcessCommand($"/bmrai idlewhilemounted {(on ? "on" : "off")}");

    /// <summary>コマンドを送る。登録されていなければ記録に残して false。</summary>
    private bool TryProcessCommand(string command)
    {
        if (!this.IsLoaded)
        {
            return false;
        }

        try
        {
            if (Svc.Commands.ProcessCommand(command))
            {
                return true;
            }

            this.AnomalyLog.Warn("Ipc", $"[BossModReborn] コマンド {command} が登録されていません");
            return false;
        }
        catch (Exception ex)
        {
            this.AnomalyLog.Warn("Ipc", $"[BossModReborn] コマンド {command} に失敗しました: {ex.Message}");
            return false;
        }
    }
}
