using LogParser.Data;
using LogParser.Naming;
using LogParser.Resolve;

namespace LogParser.App;

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

    /// <summary>
    /// Items whose display name, class name, or manufacturer match the query. Falls back
    /// to a plain substring match so items missing from the wiki catalogue stay findable.
    /// </summary>
    public IReadOnlyList<NamedHolding> Find(string query)
    {
        var all = Current.Holdings;
        query = query.Trim();
        if (query.Length == 0) return all;

        var fromCatalogue = names.Search(query).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return all
            .Where(h =>
                fromCatalogue.Contains(h.ClassName) ||
                h.Display.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                h.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
