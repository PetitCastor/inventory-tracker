namespace LogParser.Model;

/// <summary>
/// Which side of the game an inventory handle lives on. Parsed from the
/// "&lt;id&gt;:&lt;Kind&gt;:&lt;sub&gt;" strings the 4.9 inventory log emits.
/// </summary>
public enum InventoryKind
{
    /// <summary>Literal "INVALID" — the move has no counterpart on this side.</summary>
    Invalid,

    /// <summary>"&lt;playerId&gt;:Location:&lt;locationId&gt;" — a station/outpost local inventory.</summary>
    Location,

    /// <summary>"&lt;containerGeid&gt;:Container:0" — backpack, armor slot, lootable box, freight elevator.</summary>
    Container,

    /// <summary>"&lt;id&gt;:ClientOnly:&lt;n&gt;" — transient UI pseudo-inventory. Never a real holding.</summary>
    ClientOnly,

    /// <summary>
    /// Synthetic: the item was attached to the player's body (Type[Interaction] with an
    /// AttachItem caller). The log leaves Target Inventory as INVALID for these.
    /// </summary>
    Equipped,

    /// <summary>
    /// Synthetic: the item was dropped as a loose world entity (Type[Drop]). It has no
    /// inventory at all and may despawn, so this is the weakest holding there is.
    /// </summary>
    World,

    /// <summary>Anything we do not recognise. Kept so diagnostics can surface new formats.</summary>
    Unknown,
}

/// <summary>
/// A parsed inventory handle. <see cref="OwnerId"/> is the player id for
/// <see cref="InventoryKind.Location"/> and the container's geid for
/// <see cref="InventoryKind.Container"/>; <see cref="SubId"/> is the location id
/// and (always) "0" respectively.
/// </summary>
public readonly record struct InventoryRef(InventoryKind Kind, string OwnerId, string SubId, string Raw)
{
    public static readonly InventoryRef Invalid = new(InventoryKind.Invalid, "", "", "INVALID");
    public static readonly InventoryRef Equipped = new(InventoryKind.Equipped, "", "", "EQUIPPED");
    public static readonly InventoryRef World = new(InventoryKind.World, "", "", "WORLD");

    /// <summary>True when this handle can actually hold an item over time.</summary>
    public bool IsHolding =>
        Kind is InventoryKind.Location or InventoryKind.Container
             or InventoryKind.Equipped or InventoryKind.World;

    /// <summary>Location id for Location refs, container geid for Container refs, else empty.</summary>
    public string HoldingKey => Kind switch
    {
        InventoryKind.Location => SubId,
        InventoryKind.Container => OwnerId,
        _ => "",
    };

    public static InventoryRef Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Invalid;
        raw = raw.Trim();
        if (raw is "INVALID" or "NULL" or "NONE") return Invalid;

        var parts = raw.Split(':');
        if (parts.Length != 3) return new InventoryRef(InventoryKind.Unknown, raw, "", raw);

        var kind = parts[1] switch
        {
            "Location" => InventoryKind.Location,
            "Container" => InventoryKind.Container,
            "ClientOnly" => InventoryKind.ClientOnly,
            _ => InventoryKind.Unknown,
        };
        return new InventoryRef(kind, parts[0], parts[2], raw);
    }
}
