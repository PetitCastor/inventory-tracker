namespace InventoryTracker.Resolve;

/// <summary>
/// What one <see cref="LedgerReplay.Reset"/> did: how many units it gathered at the home location,
/// or wiped when the logs never named one.
/// </summary>
public sealed record ResetReport(DateTimeOffset At, int Build, int Gathered, int Wiped);
