namespace InventoryTracker.Tests;

/// <summary>
/// What Game.log does to an over-long line (issue #49): it keeps 1023 characters from the
/// severity tag on — the first 1020 and then "...". Lets a test write the full line the game
/// meant to log and get the one it actually wrote.
/// </summary>
internal static class GameLogCut
{
    public const int Limit = 1023;

    public static string Apply(string full)
    {
        var severity = full.IndexOf('[');
        return full.Length - severity <= Limit ? full : full[..(severity + Limit - 3)] + "...";
    }

    /// <summary>A multi-select drag line, as the game would write it before any cut.</summary>
    public static string FullBatchLine(int request, string target, IEnumerable<string> classes) =>
        $"<2026-11-08T16:13:09.012Z> [Notice] <Add Inventory Management Move> New Request[{request}] " +
        $"Player[Pilot] Type[Move] SourceInventory[204094442207:Location:888812681] TargetInventory[{target}] " +
        $"ItemClass[{string.Join(" ", classes.Select(c => $"[{c}]"))} ] StoredEntity[NULL] LocallyDetached[No] " +
        $"LocalAttached[NULL, ] PendingMoves[1, {string.Join(" ", Enumerable.Range(1, 80))}] " +
        "Caller[CSCLocalPlayerPersonalThoughtComponent::MoveItems] [Team_CoreGameplayFeatures][Inventory]";

    public static IReadOnlyList<string> Classes(int count) =>
        Enumerable.Range(1, count).Select(i => $"test_item_class_{i:00}").ToList();
}
