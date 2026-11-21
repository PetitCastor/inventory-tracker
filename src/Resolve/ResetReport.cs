namespace InventoryTracker.Resolve;

/// <summary>
/// What one <see cref="LedgerReplay.Reset"/> did: how many units the ledger held, and wiped, when
/// it came.
/// </summary>
public sealed record ResetReport(DateTimeOffset At, int Build, int Wiped);
