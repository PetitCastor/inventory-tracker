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
public sealed class TrackerState(TrackerDb db, WikiNameService names, TrackerOptions options)
{
    private readonly Lock _gate = new();
    private Snapshot _current = Snapshot.Empty;

    public event Action? Changed;

    public sealed record Snapshot(
        IReadOnlyList<NamedHolding> Holdings,
        HoldingResolver? Resolver,
        DateTimeOffset BuiltAt,
        IngestHealth Health)
    {
        public static readonly Snapshot Empty = new([], null, DateTimeOffset.MinValue, IngestHealth.Empty);
    }

    public Snapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public WikiNameService Names => names;

    /// <summary>Re-reads the store and re-resolves every item, then notifies subscribers.</summary>
    public void Rebuild()
    {
        var resolver = HoldingResolver.Load(db);
        var holdings = resolver.ResolveAll()
            .Select(h => new NamedHolding(h, names.Resolve(h.ItemClass)))
            .ToList();

        // Read fresh each time rather than captured at construction: Settings can change
        // the log directory or inception date while the app is running.
        var health = IngestHealth.Load(db, options.LogDir, options.InceptionDate);

        lock (_gate)
        {
            _current = new Snapshot(holdings, resolver, DateTimeOffset.UtcNow, health);
        }

        Changed?.Invoke();
    }
}
