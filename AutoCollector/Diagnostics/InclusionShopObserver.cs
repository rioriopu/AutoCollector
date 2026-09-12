using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoCollector.Automation;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Diagnostics;

/// <summary>
/// アイテム交換画面（InclusionShop）を観測して、1 つのファイルへ追記する。
///
/// スクリップ交換は系統と種別の 2 段で絞ってから品を選ぶ。
/// どの組み合わせに何が並んでいるのかは、画面を切り替えながら見ないと分からない。
///
/// 1 回ごとにファイルを作ると数が増えて追いにくいので、
/// 起動ごとに 1 本のファイルへ、画面が変わったときだけ追記する。
///
/// **読み取りだけを行う。ゲームの状態は一切変更しない。**
/// </summary>
public sealed unsafe class InclusionShopObserver(AnomalyLog anomalyLog, InclusionShopService shop)
{
    private readonly AnomalyLog anomalyLog = anomalyLog;
    private readonly InclusionShopService shop = shop;

    private string? path;
    private string lastSignature = string.Empty;
    private bool wasOpen;
    private DateTime nextCheckUtc = DateTime.MinValue;

    /// <summary>観測するかどうか。</summary>
    public bool Enabled { get; set; }

    /// <summary>書き出し先。まだ何も書いていなければ空。</summary>
    public string FilePath => this.path ?? string.Empty;

    /// <summary>書き出した回数。</summary>
    public int Entries { get; private set; }

    public void Tick(string directory)
    {
        if (!this.Enabled)
        {
            return;
        }

        // 毎フレーム読む必要はない。画面の切り替えを拾えれば足りる。
        var now = DateTime.UtcNow;
        if (now < this.nextCheckUtc)
        {
            return;
        }

        this.nextCheckUtc = now.AddMilliseconds(400);

        try
        {
            var open = this.shop.IsOpen();

            if (!open)
            {
                this.wasOpen = false;
                return;
            }

            var text = this.Describe(out var signature);

            // 同じ画面を何度も書かない。切り替わったときだけ残す。
            if (signature == this.lastSignature)
            {
                return;
            }

            this.lastSignature = signature;
            this.Append(directory, text, !this.wasOpen);
            this.wasOpen = true;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Inclusion", $"アイテム交換画面を観測できませんでした: {ex.Message}");
        }
    }

    /// <summary>いまの画面を書き出す。署名が同じなら同じ画面とみなす。</summary>
    private string Describe(out string signature)
    {
        var sb = new StringBuilder();
        signature = string.Empty;

        if (!this.shop.TryGetAddon(out var addon))
        {
            return string.Empty;
        }

        this.shop.TryGetSelection(out var selection);

        sb.AppendLine();
        sb.AppendLine($"--- {DateTime.Now:HH:mm:ss} ---");

        if (selection is not null)
        {
            sb.AppendLine(
                $"系統: 行 {selection.SelectedCategoryRowId}（{selection.SelectedCategoryIndex + 1} / {selection.CategoryCount}）" +
                $"  種別: 系列 {selection.SelectedSeriesId}（タブ {selection.SelectedSubCategoryTab + 1} / {selection.VisibleSubCategoryCount}）");

            // 種別はタブ番号で表される。系列は系統の写しなので、これだけでは区別できない。
            // タブを入れないと、品数が同じ種別が同じ画面とみなされて記録されない。
            signature = $"{selection.SelectedCategoryRowId}/{selection.SelectedSubCategoryTab}";
        }

        if (!this.shop.TryReadEntries(addon, out var entries, out var currency, out var failure))
        {
            sb.AppendLine($"品の読み取りに失敗: {failure}");
            signature += "/失敗";
            return sb.ToString();
        }

        sb.AppendLine($"画面の所持通貨: {currency:N0}");
        sb.AppendLine($"品数: {entries.Count}");
        // Flags は生の値も出す。
        // 所持済みで交換できない品を見分けるビットがあるはずだが、
        // どのビットかは分かっていない。交換できる品とできない品を
        // 並べて比べられるようにしておく。
        sb.AppendLine("index  スロット  ItemId  品名  コスト通貨(値)  コスト  数量選択  Flags");

        foreach (var entry in entries)
        {
            sb.AppendLine(
                $"{entry.Index,5}  {entry.Slot,8}  {entry.ItemId,6}  {entry.ItemName}  " +
                $"{entry.CostItemId}(型 {entry.CostType})  {entry.CostAmount}  {entry.CanSelectAmount}  " +
                $"0x{entry.RawFlags:X}({Convert.ToString(entry.RawFlags, 2).PadLeft(8, '0')})");
        }

        signature += $"/{entries.Count}";
        return sb.ToString();
    }

    private void Append(string directory, string text, bool opened)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);

            this.path ??= Path.Combine(directory, $"InclusionShop_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            if (this.Entries == 0)
            {
                File.AppendAllText(
                    this.path,
                    $"=== アイテム交換画面の観測（{DateTime.Now:yyyy-MM-dd HH:mm:ss} 開始）===\n" +
                    "画面が切り替わったときだけ追記します。読み取りのみで、操作は行いません。\n",
                    Encoding.UTF8);
            }

            if (opened)
            {
                File.AppendAllText(this.path, "\n=== 画面を開きました ===\n", Encoding.UTF8);
            }

            File.AppendAllText(this.path, text, Encoding.UTF8);
            this.Entries++;
        }
        catch (Exception ex)
        {
            this.anomalyLog.Warn("Inclusion", $"観測を書き出せませんでした: {ex.Message}");
        }
    }
}
