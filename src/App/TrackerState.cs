using InventoryTracker.Data;
using InventoryTracker.Naming;
using InventoryTracker.Resolve;

namespace InventoryTracker.App;

/// <summary>What the UI shows for one item: its placement plus its human-readable name.</summary>
public sealed record NamedHolding(ItemHolding Holding, ResolvedName Name)
{
    public string Display => Name.Display;
    public string ClassName => Holding.ItemClass;
}

/// <summary>
/// The current view of the world, shared by every connected page. Rebuilt after each
/// ingest pass; pages subscribe to <see cref="Changed"/> so tailing the live log updates
/// an open browser without a refresh.
/// </summary>
public sealed class TrackerState(TrackerDb db, WikiNameService names)
{
    private readonly Lock _gate = new();
    private Snapshot _current = Snapshot.Empty;

    public event Action? Changed;

    public sealed record Snapshot(
        IReadOnlyList<NamedHolding> Holdings,
        HoldingResolver? Resolver,
        DateTimeOffset BuiltAt)
    {
        public static readonly Snapshot Empty = new([], null, DateTimeOffset.MinValue);
    }

    public Snapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public WikiNameService Names => names;

    /// <summary>
    /// Move request lines the parser missed or only caught by shape since launch (or since
    /// the last rebuild), by tag. Non-empty unparsed counts mean the game changed its log
    /// format and item positions are going stale — the layout shows a banner for it.
    /// </summary>
    public sealed record LogDrift(int UnparsedLines, int FallbackLines, IReadOnlyDictionary<string, int> Tags)
    {
        public static readonly LogDrift None = new(0, 0, new Dictionary<string, int>());
    }

    private LogDrift _drift = LogDrift.None;

    public LogDrift Drift
    {
        get { lock (_gate) return _drift; }
    }

    public void RecordDrift(Ingest.IngestStats stats)
    {
        lock (_gate)
        {
            var tags = new Dictionary<string, int>(_drift.Tags, StringComparer.Ordinal);
            foreach (var (tag, count) in stats.DriftTags) tags[tag] = tags.GetValueOrDefault(tag) + count;

            _drift = new LogDrift(
                _drift.UnparsedLines + stats.UnparsedRequestLines,
                _drift.FallbackLines + stats.FallbackRequestLines,
                tags);
        }
    }

    /// <summary>A rebuild re-reads everything, which would count every line twice.</summary>
    public void ResetHealth()
    {
        lock (_gate) _drift = LogDrift.None;
    }

    /// <summary>Re-reads the store and re-resolves every item, then notifies subscribers.</summary>
    public void Rebuild()
    {
        var resolver = HoldingResolver.Load(db);
        var holdings = resolver.ResolveAll()
            .Select(h => new NamedHolding(h, names.Resolve(h.ItemClass)))
            .ToList();

        lock (_gate)
        {
            _current = new Snapshot(holdings, resolver, DateTimeOffset.UtcNow);
        }

        Changed?.Invoke();
    }
}
