namespace InventoryTracker.Naming;

/// <summary>How far a place's name has got towards being shown as fact.</summary>
public enum PlaceNameStatus
{
    /// <summary>Nothing proposed a name; the place is shown as the game logged it.</summary>
    Unresolved,

    /// <summary>A name was proposed but not corroborated, or two sources disagree. Shown,
    /// flagged as unconfirmed, until the player confirms or corrects it.</summary>
    Pending,

    /// <summary>Two independent sources agree, or the player said so. Shown as is.</summary>
    Confirmed,
}
