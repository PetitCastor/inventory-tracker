namespace InventoryTracker.Ingest;

/// <summary>
/// A shop kiosk's answer to a purchase:
/// <c>&lt;CEntityComponentShopUIProvider::RmShopFlowResponse&gt; Received ShopFlowResponse - playerId[..]
/// shopId[..] shopName[..] kioskId[..] kioskState[BuyRequestProcessing] result[Success] type[Buying]</c>.
/// <para>
/// It names the shop but not the item: it answers the newest <see cref="ShopItemBought"/> from
/// the same shop. Every answer logged so far reads <c>result[Success]</c>.
/// </para>
/// </summary>
public sealed record ShopBuyAnswered(
    DateTimeOffset Timestamp,
    string ShopId,
    string Result) : InventoryEvent(Timestamp)
{
    public bool Succeeded => Result.Equals("Success", StringComparison.OrdinalIgnoreCase);
}
