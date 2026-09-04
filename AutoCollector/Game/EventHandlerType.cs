namespace AutoCollector.Game;

/// <summary>
/// ENpcBase.ENpcData[] などに入る ID は、上位 16bit が EventHandler の種別を表す。
/// 判定は必ず (value >> 16) == 種別 で行う。
/// ハンドラ値はそのまま該当シートの RowId になる。
///
/// 出典: FFXIVClientStructs FFXIV/Client/Game/Event/EventHandler.cs の EventHandlerContent。
/// </summary>
internal static class EventHandlerType
{
    public const ushort GilShop = 0x0004;
    public const ushort CustomTalk = 0x000B;
    public const ushort GrandCompanyShop = 0x0016;
    public const ushort SpecialShop = 0x001B;
    public const ushort TopicSelect = 0x0032;
    public const ushort PreHandler = 0x0036;
    public const ushort InclusionShop = 0x003A;
    public const ushort CollectablesShop = 0x003B;

    public static ushort Of(uint handlerId) => (ushort)(handlerId >> 16);

    public static bool Is(uint handlerId, ushort type) => handlerId != 0 && Of(handlerId) == type;
}

/// <summary>
/// SpecialShop の ItemCosts[].CostType。コストが何を指しているかを表す。
/// 実データのクロス集計で確認済み（CostType=0/1 は実 ItemId、2 は Tomestones 行、3 は特殊通貨バケット）。
///
/// SpecialShop.UseCurrencyType で分岐してはならない。UseCurrencyType==2 のショップが 463 件あり、
/// そこには詩学コストのエントリが含まれるため、UseCurrencyType だけで判定すると
/// ギルやシャードへ誤解決する。判定は必ずエントリ単位の CostType で行う。
/// </summary>
internal static class SpecialShopCostType
{
    /// <summary>ItemCost.RowId がそのまま ItemId。</summary>
    public const byte DirectItem = 0;

    /// <summary>ItemCost.RowId がそのまま ItemId（用途は DirectItem と同じに見えるが値が異なる）。</summary>
    public const byte DirectItemAlt = 1;

    /// <summary>ItemCost.RowId が Tomestones シートの行番号。TomestonesItem 経由で ItemId 化する。</summary>
    public const byte TomestoneSlot = 2;

    /// <summary>ItemCost.RowId が特殊通貨バケットのインデックス（スクリップ類）。対応表がシートに無い。</summary>
    public const byte SpecialCurrencyBucket = 3;
}
