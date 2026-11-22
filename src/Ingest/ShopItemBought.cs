namespace InventoryTracker.Ingest;

/// <summary>
/// The player asked a shop kiosk for an item:
/// <c>&lt;CEntityComponentShopUIProvider::SendShopBuyRequest&gt; Sending SShopBuyRequest - playerId[..]
/// shopId[..] shopName[SCShop_..] kioskId[..] client_price[9083.000000] itemClassGUID[..]
/// itemName[behr_rifle_ballistic_03_mag] quantity[31]</c>.
/// <para>
/// The price is the total for the whole quantity. Whether the purchase went through comes on
/// the next line from the same shop (<see cref="ShopBuyAnswered"/>), about 0.3 s later.
/// </para>
/// <para>
/// No line says where the item is delivered. In every purchase logged since 2026-08-09 the
/// item is next moved out of the station inventory of the place the player stood in when they
/// bought it, the whole stack at once: 31 magazines bought at 12:50 on 2026-11-22 were moved
/// out of that inventory, all 31, at 12:56. The kiosk lets the player choose another
/// destination; no log yet shows one.
/// </para>
/// <para>
/// Only the kiosk's line is read. A bar or vending machine writes the same request from
/// <c>CEntityComponentShoppingProvider::SendStandardItemBuyRequest</c>, and the drink it sells
/// never appears in any inventory line afterwards.
/// </para>
/// </summary>
public sealed record ShopItemBought(
    DateTimeOffset Timestamp,
    string PlayerId,
    string ShopId,
    string ShopName,
    string ItemClass,
    int Quantity,
    decimal Price) : InventoryEvent(Timestamp);
