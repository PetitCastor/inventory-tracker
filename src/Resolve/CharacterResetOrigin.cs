namespace InventoryTracker.Resolve;

/// <summary>One place a character reset emptied, named, and how many units it held.</summary>
public sealed record CharacterResetOrigin(string Place, int Units);
