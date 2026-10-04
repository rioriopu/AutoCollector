using EstellUtils.UI;
using EstellUtils.UI.Layout;

namespace AutoCollector.Ui;

/// <summary>
/// 初めて使うときの案内。
///
/// 周回の途中には割り込めないため、AutoDuty を 1 周で終わらせ、その終わりに交換する。
/// そのために AutoDuty 側で何を設定すればよいかを、順番に示す。
///
/// 状況タブの内側から呼ぶ。そのタブと同じく EstellUtils で描く。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// AutoDuty の設定一覧の表の列。
    ///
    /// 「場所と理由」は 2 色で 2 段に積むため <c>CellStack</c> を使う。
    /// 行の高さは <c>TableRow</c> が前フレームの実測から決めるので指定しない。
    /// </summary>
    private static readonly TableColumn[] SetupItemColumns =
    [
        new(string.Empty, 26f),
        new("設定", 250f),
        new("現在 / 推奨", 170f),
        new("場所と理由", SizeSpec.Weight(1f)),
        new(string.Empty, 130f),
    ];

    private void DrawSetupGuide()
    {
        if (!this.plugin.AutoDuty.IsLoaded)
        {
            return;
        }

        var setup = this.plugin.AutoDutySetup;

        // **版が足りないなら、設定の話より先にそれを出す。**
        //
        // 古い AutoDuty は設定の持ち方が違うため、そもそも読み書きできない。
        // その状態で「あと N 件の設定が必要です」と並べても直しようがない。
        // 設定の話は、版が足りてから。
        if (setup.NeedsAutoDutyUpdate)
        {
            using var stale = EUi.Section(
                "はじめに: AutoDuty の更新が必要です", defaultOpen: true, id: "setupversion");

            if (!stale.IsVisible)
            {
                return;
            }

            var installed = setup.InstalledVersion?.ToString() ?? "読み取れません";

            EUi.WrapColored($"いま {installed} / 必要 {setup.RequiredVersion}", NoteKind.Danger);

            EUi.MutedParagraph("AutoDuty は設定の持ち方を作り直しました。古い版では設定を読み書きできません。");
            EUi.MutedParagraph("そのまま任せると、ループ間処理を確かめられないまま周回だけを繰り返します。");

            EUi.Spacing();

            using (EUi.HStack(wrap: true))
            {
                if (EUi.Button("AutoDuty を更新する", width: 200f))
                {
                    setup.OpenPluginInstaller();
                }

                EUi.Muted("更新可能な一覧を AutoDuty で絞って開きます");
            }

            EUi.MutedParagraph("  必要な版は 設定タブ で変えられます。空にすると制限しません");

            return;
        }

        var pending = setup.PendingCount;

        if (pending == 0)
        {
            // 整っているときは 1 行にたたむ。毎回読ませるものではない。
            using var done = EUi.Section(
                "AutoDuty の設定は整っています", defaultOpen: false, id: "setup");

            if (done.IsVisible)
            {
                EUi.MutedParagraph("1 周ごとに交換する構成になっています。");
                EUi.Spacing();
                this.DrawSetupItems(setup);
            }

            return;
        }

        // 直すべきものがあるときは開いた状態で出す。
        using var node = EUi.Section(
            $"はじめに: AutoDuty 側であと {pending} 件の設定が必要です", defaultOpen: true, id: "setup");

        if (!node.IsVisible)
        {
            return;
        }

        EUi.MutedParagraph("ID クリア → リテイナー → GC 納品 → 交換 → 次の ID の流れにするための設定です。");
        EUi.MutedParagraph("周回の途中には割り込めないため、AutoDuty を 1 周で終わらせ、その切れ目で交換します。");

        EUi.Spacing();

        using (EUi.HStack(wrap: true))
        {
            if (EUi.Button("AutoDuty の設定を開く", width: 200f))
            {
                setup.OpenAutoDutyConfig();
            }

            if (setup.HasApplicable &&
                EUi.Button("推奨設定をまとめて適用", width: 220f)
                    .Tip("AutoDuty の設定を変更します。変更内容はログに残ります。"))
            {
                setup.ApplyAll();
            }
        }

        EUi.Spacing();
        this.DrawSetupItems(setup);
    }

    private void DrawSetupItems(Automation.AutoDutySetup setup)
    {
        EUi.TableHeader(SetupItemColumns);

        var row = 0;

        foreach (var item in setup.Items)
        {
            var canApply = !item.Ok && item.Readable && item.CanApply;

            using (EUi.TableRow(SetupItemColumns, row++))
            {
                if (!item.Readable)
                {
                    EUi.TableCell("?", color: EUi.Colors.TextMuted);
                }
                else if (item.Ok)
                {
                    EUi.TableCell("OK", color: EUi.NoteColor(NoteKind.Success));
                }
                else
                {
                    EUi.TableCell("!", color: EUi.NoteColor(NoteKind.Warning));
                }

                EUi.TableCell(item.Title);

                // 現在の値だけ状態色を付け、推奨は控えめに添える。
                // 1 セルに 2 つ置けるようになったので、色分けを戻した。
                if (!item.Readable)
                {
                    EUi.TableCell("読み取れません", color: EUi.Colors.TextMuted);
                }
                else
                {
                    using (EUi.Cell())
                    {
                        EUi.TextColored(item.Current, item.Ok ? NoteKind.Success : NoteKind.Warning);
                        EUi.Muted($"/ {item.ExpectedLabel}");
                    }
                }

                // 場所は通常色、理由は控えめ。元の 2 色へ戻した。
                using (EUi.CellStack())
                {
                    EUi.Paragraph(item.Where);

                    if (!string.IsNullOrEmpty(item.Why))
                    {
                        EUi.MutedParagraph(item.Why);
                    }
                }

                // 適用ボタンは専用の列へ。表の外へ出していたときは、
                // どの行のボタンなのかが分からなかった。
                if (canApply)
                {
                    using (EUi.Cell())
                    {
                        if (EUi.SmallButton($"この設定を適用##apply{item.Key}"))
                        {
                            setup.Apply(item);
                        }
                    }
                }
                else
                {
                    EUi.TableCell(string.Empty);
                }
            }
        }
    }
}
