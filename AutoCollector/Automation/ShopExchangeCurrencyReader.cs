using System.Collections.Generic;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoCollector.Automation;

/// <summary>AtkValue を 1 つ読んだ結果。診断表示にも使う。</summary>
public sealed record AtkValueProbe(int Index, bool InRange, string TypeName, uint Value, bool Usable);

/// <summary>
/// AtkValue の整数読み取り。
///
/// ECommons の AddonMaster は <c>Addon-&gt;AtkValues[n].UInt</c> と型を確認せずに読んでいる。
/// AtkValue は共用体なので、実際の型が Int でも同じ 4 バイトを UInt として読めてしまい、
/// 結果的に動作している。一方 ECommons の AtkReader は型が UInt でないと例外を投げる。
///
/// 実機で確認したところ、ShopExchangeCurrency の値には Int 型のものが混ざっていた。
/// そのため「Int と UInt はどちらも整数として受け付ける。それ以外は失敗として扱う」という
/// 方針にする。型を無視するのではなく、受け付ける型を明示し、想定外の型は必ず検出する。
/// </summary>
internal static unsafe class AtkValueIntReader
{
    public static AtkValueProbe Probe(AtkUnitBase* addon, int index)
    {
        if (addon is null || index < 0 || index >= addon->AtkValuesCount)
        {
            return new AtkValueProbe(index, false, "範囲外", 0, false);
        }

        var value = addon->AtkValues[index];
        var typeName = value.Type.ToString();

        return value.Type switch
        {
            AtkValueType.UInt => new AtkValueProbe(index, true, typeName, value.UInt, true),

            // 負値は「整数として読めるが、ID や個数としては使えない」ため Usable=false にする。
            AtkValueType.Int => value.Int >= 0
                ? new AtkValueProbe(index, true, typeName, (uint)value.Int, true)
                : new AtkValueProbe(index, true, $"{typeName}({value.Int})", 0, false),

            AtkValueType.Bool => new AtkValueProbe(index, true, typeName, value.Byte != 0 ? 1u : 0u, true),

            _ => new AtkValueProbe(index, true, typeName, 0, false),
        };
    }

    public static bool TryRead(AtkUnitBase* addon, int index, out uint value, out AtkValueProbe probe)
    {
        probe = Probe(addon, index);
        value = probe.Value;
        return probe.Usable;
    }
}

/// <summary>
/// ShopExchangeCurrency の 1 エントリ分。
///
/// コスト・ItemId・index は 3 本の並列配列に分かれて格納されているため、
/// 最も手前の配列を基点にして相対位置で読む。
/// </summary>
public sealed unsafe class ShopEntryReader : AtkReader
{
    private readonly AtkUnitBase* addon;
    private readonly int beginOffset;
    private readonly ShopAddonLayout layout;

    public ShopEntryReader(nint addonPtr, int beginOffset, ShopAddonLayout layout)
        : base(addonPtr, beginOffset)
    {
        this.addon = (AtkUnitBase*)addonPtr;
        this.beginOffset = beginOffset;
        this.layout = layout;
    }

    public AtkValueProbe ItemId => AtkValueIntReader.Probe(this.addon, this.beginOffset + this.layout.ItemIdRelative);

    public AtkValueProbe CostAmount => AtkValueIntReader.Probe(this.addon, this.beginOffset + this.layout.CostRelative);

    /// <summary>
    /// 交換 callback に渡す index。
    /// 画面上の並び順でもシート上の位置でもなく、この値でなければならない。
    /// </summary>
    public AtkValueProbe Index => AtkValueIntReader.Probe(this.addon, this.beginOffset + this.layout.IndexRelative);
}

/// <summary>
/// ShopExchangeCurrency アドオンの読み取り。
///
/// FFXIVClientStructs にこのアドオンの型付き構造体は存在しないため、AtkValues の
/// 生インデックスに依存せざるを得ない。ただし範囲外・型違いを黙って通すことは絶対にしない。
/// </summary>
public sealed unsafe class ShopExchangeCurrencyReader : AtkReader
{
    private readonly AtkUnitBase* addon;
    private readonly ShopAddonLayout layout;

    public ShopExchangeCurrencyReader(AtkUnitBase* addon, ShopAddonLayout layout)
        : base(addon)
    {
        this.addon = addon;
        this.layout = layout;
    }

    /// <summary>ショップが持つエントリ数。</summary>
    public AtkValueProbe NumEntries => AtkValueIntReader.Probe(this.addon, this.layout.NumEntries);

    /// <summary>画面が表示している所持通貨量。交換前後の検証に使う。</summary>
    public AtkValueProbe CurrencyAmount => AtkValueIntReader.Probe(this.addon, this.layout.CurrencyAmount);

    /// <summary>
    /// 通貨アイコン ID。補助情報であり、読めなくても交換の可否には影響しない。
    /// </summary>
    public AtkValueProbe CurrencyIcon => AtkValueIntReader.Probe(this.addon, this.layout.CurrencyIcon);

    /// <summary>指定位置のエントリを読む。</summary>
    public ShopEntryReader GetEntry(int entryIndex)
    {
        var (ptr, _) = this.AtkReaderParams;
        var offset = this.layout.EntryBase + (entryIndex * this.layout.EntryStride);
        return new ShopEntryReader(ptr, offset, this.layout);
    }

    /// <summary>
    /// 配置が正しいかを目視で確かめるための診断。
    /// 各オフセットが実際にどの型で何の値を持っているかを返す。
    /// </summary>
    public List<(string Label, AtkValueProbe Probe)> DiagnoseLayout()
    {
        var first = this.GetEntry(0);
        return
        [
            ("AtkValues の総数", new AtkValueProbe(-1, true, "—", this.addon->AtkValuesCount, true)),
            ($"NumEntries [{this.layout.NumEntries}]", this.NumEntries),
            ($"CurrencyAmount [{this.layout.CurrencyAmount}]", this.CurrencyAmount),
            ($"CurrencyIcon [{this.layout.CurrencyIcon}]", this.CurrencyIcon),
            ($"1件目 Cost [{this.layout.EntryCost}]", first.CostAmount),
            ($"1件目 ItemId [{this.layout.EntryItemId}]", first.ItemId),
            ($"1件目 Index [{this.layout.EntryIndex}]", first.Index),
        ];
    }
}
