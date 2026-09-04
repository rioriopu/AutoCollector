using System.Numerics;

namespace AutoCollector.Game;

/// <summary>NPC のショップへ辿り着くまでの経路。UI 表示とメニュー処理の分岐に使う。</summary>
public enum HandlerPath
{
    /// <summary>NPC に話しかけると直接ショップが開く。</summary>
    Direct,

    /// <summary>PreHandler を経由する（開放条件つきの入口）。</summary>
    PreHandler,

    /// <summary>TopicSelect を経由する（会話メニューから選ぶ）。</summary>
    TopicSelect,

    /// <summary>CustomTalk を経由する。構造が一定でないため best-effort。</summary>
    CustomTalk,
}

/// <summary>
/// 1 件の交換（この通貨をこれだけ払って、このアイテムをこれだけ得る）の定義。
/// すべてゲームデータから解決した値であり、ハードコードは含まない。
/// </summary>
public sealed record ExchangeDefinition
{
    /// <summary>SpecialShop.RowId。</summary>
    public required uint ShopId { get; init; }

    /// <summary>
    /// SpecialShop.Item[] 内の位置。
    /// デバッグ表示専用。実際の購入では、開いた画面から ItemId で検索して得た index を使う。
    /// </summary>
    public required int SheetEntryIndex { get; init; }

    public required uint CurrencyItemId { get; init; }

    public required uint CurrencyCost { get; init; }

    public required uint RewardItemId { get; init; }

    public required uint RewardQuantity { get; init; }

    public required bool RewardHq { get; init; }

    /// <summary>コストの表現方法。UI で「どう解決したか」を示すために保持する。</summary>
    public required byte CostType { get; init; }

    /// <summary>
    /// このエントリの報酬がちょうど 1 種類か。
    /// false のものは 1 通貨 1 アイテムのモデルで表せないため、交換を実行しない。
    /// </summary>
    public required bool SingleReward { get; init; }

    /// <summary>
    /// このエントリのコストがちょうど 1 種類か。
    /// false だと通貨以外も消費するため、「通貨が減った AND アイテムが増えた」では検証しきれない。
    /// </summary>
    public required bool SingleCost { get; init; }

    /// <summary>ENpcBase.RowId（＝ゲーム内オブジェクトの BaseId）。0 なら NPC 未解決。</summary>
    public uint NpcDataId { get; init; }

    public string NpcName { get; init; } = string.Empty;

    public uint TerritoryId { get; init; }

    public Vector3 NpcPosition { get; init; }

    public HandlerPath Path { get; init; } = HandlerPath.Direct;

    /// <summary>
    /// SpecialShop.Name。
    ///
    /// NPC が複数のショップを持つ場合、話しかけると選択肢としてこの名前が並ぶ。
    /// 会話メニューを通過する際の第一候補として使う。
    /// </summary>
    public string ShopName { get; init; } = string.Empty;

    /// <summary>TopicSelect.Name / CustomTalk.MainOption 由来のヒント。ショップ名で決まらないときの候補。</summary>
    public string? MenuHint { get; init; }

    /// <summary>NPC の座標が解決できているか。false のものは自動実行の対象にしない。</summary>
    public bool HasLocation => this.NpcDataId != 0 && this.TerritoryId != 0;
}

/// <summary>報酬アイテム単位でまとめた候補（同じアイテムを複数の NPC が扱うことがある）。</summary>
public sealed record ExchangeCandidateGroup(uint RewardItemId, string RewardName, System.Collections.Generic.IReadOnlyList<ExchangeDefinition> Definitions);
