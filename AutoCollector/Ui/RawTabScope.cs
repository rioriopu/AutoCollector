using System;
using Dalamud.Bindings.ImGui;
using EstellUtils.UI;

namespace AutoCollector.Ui;

/// <summary>
/// タブの中身を、まだ生の ImGui で描くための囲み。
///
/// <b>移行の途中だけ使う。</b>
///
/// タブバーは EstellUtils が描くので、タブの中身は EstellUtils のレイアウトの中に来る。
/// そこで生の ImGui を呼ぶには 2 つ要る。
///
/// <code>
/// EUi.RawImGui()          ImGui 側に、この領域と同じ大きさの場所を用意する
/// PushTextWrapPos(0f)     生 ImGui の文字は既定で折り返さないので、右端で折り返させる
/// </code>
///
/// どちらも対で閉じる必要があり、<c>try / finally</c> を毎回書くと読みにくい。
/// <c>using</c> 1 行で済むようにまとめた。
///
/// <b>タブを EstellUtils へ移し終えたら、そのタブからこの囲みを外す。</b>
/// 全部外れたらこのファイルごと消す。
/// </summary>
internal readonly struct RawTabScope : IDisposable
{
    private readonly RawImGuiScope raw;

    private RawTabScope(RawImGuiScope raw)
    {
        this.raw = raw;
        ImGui.PushTextWrapPos(0f);
    }

    /// <summary>囲みを開く。</summary>
    public static RawTabScope Open() => new(EUi.RawImGui());

    /// <inheritdoc/>
    public void Dispose()
    {
        // 開いた逆順で閉じる。
        ImGui.PopTextWrapPos();
        this.raw.Dispose();
    }
}
