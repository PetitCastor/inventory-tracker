namespace InventoryTracker.Tests.Reliability;

/// <summary>
/// One number the replay study tracks, and what "good" means for it.
/// </summary>
/// <param name="Key">Stable id used in the baseline file.</param>
/// <param name="HigherIsBetter">Direction of improvement; the ratchet only fails a move the wrong way.</param>
/// <param name="Target">The improvement objective. Not enforced — the report shows the gap.</param>
/// <param name="Limit">
/// A hard invariant, enforced on every corpus: a floor when higher is better, a ceiling
/// otherwise. Reserved for properties that are either right or a bug.
/// </param>
public sealed record MetricDef(
    string Key, string Label, bool HigherIsBetter, double Target, double? Limit, bool IsRate, string Why);

/// <summary>
/// The catalogue of reliability metrics, in the order the report shows them. Each stage of
/// the pipeline gets its own, so a regression points at the stage that caused it: a drop in
/// capture is the parser, in enrichment the ingestor's correlation, in source hits the
/// ledger's history, and in holdings what the user actually sees.
/// </summary>
public static class ReliabilityMetrics
{
    public static readonly IReadOnlyList<MetricDef> All =
    [
        new("capture.request_rate", "Relocating requests captured as a move", true, 0.98, null, true,
            "A request the game logged but the ledger never saw is an item whose position silently goes stale."),
        new("capture.unrecognised_lines", "Move request lines the parser could not read", false, 0, 0, false,
            "Non-zero means the game changed its log format. Always a bug."),

        new("enrich.geid_coverage", "Moves that name the exact entity", true, 0.85, null, true,
            "A named move places one specific item; a class-level one has to guess which."),
        new("enrich.confirmed_rate", "Moves the game confirmed succeeded", true, 0.95, null, true,
            "An unconfirmed move halves the confidence of whatever it placed."),

        new("ledger.accounted_rate", "Class-level units the ledger can account for", true, 0.95, null, true,
            "Found at the source, found on the player, or the class's first appearance. The rest are duplicate risks."),
        new("ledger.duplicate_risk_rate", "Class-level units that may be counted twice", false, 0.02, null, true,
            "Credited without a source while the same class was still recorded elsewhere: one of the two is wrong."),

        new("holdings.high_share", "Holdings rated High confidence", true, 0.80, null, true,
            "What the user sees: the share of rows worth trusting outright."),
        new("holdings.low_share", "Holdings rated Low confidence", false, 0.05, null, true,
            "Rows the tracker itself does not believe."),
        new("holdings.mean_score", "Mean holding score", true, 0.85, null, false,
            "Overall confidence, weighted the way the resolver weighs its caveats."),
        new("holdings.inferred_share", "Holdings that may be counted twice", false, 0.05, null, true,
            "Counts credited without a source while the same class was recorded elsewhere — the phantom-duplicate risk."),
        new("holdings.first_seen_share", "Holdings first seen without a known source", false, 0.15, null, true,
            "Loot, purchases and pre-existing stock: real, but with no history before the move that revealed them."),

        new("placement.within_build_same_place", "Placements confirmed within one game build", true, 0.95, null, true,
            "A later named move out of the place the ledger had the item. Checks the ledger itself, and that age alone does not stale a placement."),
        new("placement.brier", "Calibration error of placement trust (Brier score)", false, 0.05, null, false,
            "Mean squared gap between the trust put in a placement and whether the game then confirmed it. 0 is perfect; trusting everything fully scores the share that was wrong."),
        new("placement.across_update_same_place", "Placements confirmed across a game update", true, 0.90, null, true,
            "How often the ledger's belief held across an update, once it has moved what the update moved to where the player spawned."),

        new("live.parity", "Live tailing matches a single pass", true, 1.0, 1.0, true,
            "Following Game.log as it grows must reach the same answer as reading it once. Anything less is a bug."),
        new("live.cold_parity", "Tailing with a restart before every pass matches a single pass", true, 0.95, null, true,
            "How much survives the tracker being restarted mid-session, where only the store carries state over."),
    ];
}
