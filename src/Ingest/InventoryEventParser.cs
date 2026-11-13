using System.Text.RegularExpressions;
using InventoryTracker.Model;
using InventoryTracker.Naming;

namespace InventoryTracker.Ingest;

/// <summary>
/// Turns a <see cref="LogLine"/> into a typed <see cref="InventoryEvent"/>.
/// Every pattern here was verified against post-4.9 logs; pre-4.9 files use a
/// different event vocabulary entirely and must be excluded before parsing.
/// <para>
/// Build 12519617 (late Aug 2026) reshaped two of those patterns' anchors — see
/// <see cref="QueuedRequest"/>'s dispatch in <see cref="Parse"/> and the comment on
/// <see cref="AddMove"/> — without touching anything else in either line. Both eras parse
/// side by side; <see cref="LooksLikeRequest"/> and <see cref="UnrecognisedMove"/> exist so
/// the *next* such change is a counter going up rather than a silent parse failure.
/// </para>
/// </summary>
public static partial class InventoryEventParser
{
    /// <summary>4.9 went live on this date. Earlier logs are not parseable by these rules.</summary>
    public static readonly DateTimeOffset Cutoff = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Bump whenever a change here would extract something different from a log already
    /// ingested. Rotated files are never reopened once complete, so without this a parser
    /// fix would only ever apply to lines written after the upgrade — see
    /// <see cref="Data.TrackerDb.Initialize"/>.
    /// <para>3 -> 4: attachment lines keep their status, and every sighting is stored.</para>
    /// <para>4 -> 5: a closed connection voids the requests the server never processed.</para>
    /// <para>5 -> 6: a drop records the location it fell in.</para>
    /// <para>6 -> 7: ship lockers are named, and ships flown and retrieved are recorded.</para>
    /// <para>7 -> 8: a multi-select drag whose line the game cut short is still read.</para>
    /// </summary>
    public const int Version = 8;

    /// <summary>
    /// Move types that never change where an item is held: pure reads, and the
    /// re-organise operations whose source and target inventory are the same grid.
    /// </summary>
    public static bool IsNonRelocating(string moveType) =>
        moveType is "QueryInventory" or "OpenNestedInventory" or "Sort" or "StackAll";

    /// <summary>
    /// The one non-trivial <c>action[]</c> a Queued line carries: an Interaction that picks an
    /// item out of a grid into the player's hand — to eat, drink, or hand it in. Its target
    /// is INVALID and its caller is a grid callback rather than AttachItem, so without this it
    /// was dropped, and the item stayed in its container forever.
    /// </summary>
    public const string CarryAction = "Carry";

    /// <summary>Callers that mean the item went onto the player's body rather than into a grid.</summary>
    private static bool IsAttachCaller(string? caller) =>
        caller is not null && caller.Contains("AttachItem", StringComparison.Ordinal);

    [GeneratedRegex(
        @"^Queued Request\[(?<req>\d+)\] Type\[(?<type>[^\]]*)\] for '(?<player>[^']*)' \[(?<pid>\d+)\] " +
        @"Source Inventory\[(?<srcinv>[^\]]*)\] Target Inventory\[(?<tgtinv>[^\]]*)\]\. " +
        @"Source\[(?<srcitem>[^\]]*)\] amount\[(?<srcamt>\d+)\] rank\[[^\]]*\]\. " +
        @"Target\[(?<tgtitem>[^\]]*)\] amount\[\d+\] rank\[[^\]]*\]\. " +
        @"Item\[(?<item>[^\]]*)\] action\[(?<action>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex QueuedRequest();

    // ItemClass is matched up to the following StoredEntity[ rather than to the first ']',
    // because a multi-select drag writes it as a bracketed list: ItemClass[[a] [b] [c] ].
    // Stopping at the first ']' would silently truncate that to "[a".
    //
    // "New [Rr]equest[" rather than a literal "New request[": build 12519617 capitalised it
    // to "New Request[" without changing anything else on the line. Everything past that
    // token stays case-sensitive — only the one word the game actually changed is loosened.
    [GeneratedRegex(
        @"^New [Rr]equest\[(?<req>\d+)\] Player\[[^\]]*\] Type\[(?<type>[^\]]*)\] " +
        @"SourceInventory\[(?<srcinv>[^\]]*)\] TargetInventory\[(?<tgtinv>[^\]]*)\] " +
        @"ItemClass\[(?<class>.*?)\] StoredEntity\[.*?Caller\[(?<caller>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddMove();

    /// <summary>Fallback for an Add-move line that does not carry the StoredEntity anchor.</summary>
    [GeneratedRegex(
        @"^New [Rr]equest\[(?<req>\d+)\] Player\[[^\]]*\] Type\[(?<type>[^\]]*)\] " +
        @"SourceInventory\[(?<srcinv>[^\]]*)\] TargetInventory\[(?<tgtinv>[^\]]*)\] " +
        @"ItemClass\[(?<class>[^\]]*)\].*?Caller\[(?<caller>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddMoveLoose();

    /// <summary>
    /// An Add-move line the game cut short. Game.log keeps at most 1023 characters of a line
    /// from its <c>[Notice]</c> onward: past that it writes the first 1020 and appends
    /// <c>...</c>, with no setting to raise the limit. A multi-select drag of about 25 items is
    /// enough to push <c>Caller[...]</c>, and often <c>StoredEntity[...]</c>, off the end, and
    /// one of about 30 cuts into the class list itself. Only a bracketed list is accepted
    /// here; the class text runs to the cut and <see cref="ParseCutAddMove"/> decides how much
    /// of it survived.
    /// </summary>
    [GeneratedRegex(
        @"^New [Rr]equest\[(?<req>\d+)\] Player\[[^\]]*\] Type\[(?<type>[^\]]*)\] " +
        @"SourceInventory\[(?<srcinv>[^\]]*)\] TargetInventory\[(?<tgtinv>[^\]]*)\] " +
        @"ItemClass\[(?<class>\[.*)\.\.\.\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddMoveCut();

    /// <summary>
    /// The canary for grammar drift: any line that carries every visible sign of being an
    /// inventory move request — the "Queued Request[" / "New request[" or "New Request["
    /// prefix — regardless of which event tag it hangs off or how the rest of the line has
    /// changed underneath us. Case-insensitive because the one confirmed drift so far
    /// (build 12519617) was exactly a capitalisation change to this token.
    /// </summary>
    [GeneratedRegex(
        @"^(?:Queued|New) request\[\d+\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LooksLikeRequest();

    /// <summary>One entry of a bracketed ItemClass list.</summary>
    [GeneratedRegex(@"\[(?<cls>[^\[\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BatchClass();

    // <UnstowPendingEntities> Unstow Request[12] for 'X' [id] finalized spawn of 'cls_geid' [geid], N remaining
    [GeneratedRegex(
        @"^Unstow Request\[(?<req>\d+)\].*?finalized spawn of '(?<entity>[^']*)' \[(?<geid>\d{6,})\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnstowSpawn();

    // <OnInventoryStoreItem> Entity[cls_geid - Class(cls) - ...] Inventory[204821708183:Location:4005457614]
    [GeneratedRegex(
        @"^Entity\[(?<entity>[^ \]]+) - Class\((?<cls>[^)]*)\).*?\] Inventory\[(?<inv>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex InventoryStore();

    // <AttachmentReceived> Player[X] Attachment[cls_geid, cls, geid] Status[persistent] Port[Armor_Undersuit]
    // Any length of geid: a Status[local] line carries a short client-side one (10721), and it
    // is the ingestor's call to ignore it, not an accident of this pattern.
    [GeneratedRegex(
        @"^Player\[[^\]]*\] Attachment\[[^,]+, (?<cls>[^,]+), (?<geid>\d+)\] " +
        @"Status\[(?<status>[^\]]*)\] Port\[(?<port>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex Attachment();

    // <Calculate Route> ... |Projected Start Location is Stanton Gateway for route to destination X
    [GeneratedRegex(
        @"Projected Start Location is (?<name>.+?) for route to destination",
        RegexOptions.CultureInvariant)]
    private static partial Regex RouteStart();

    // <Player Selected Quantum Target - Local> ...|Player has selected point ab_mine_stanton3_sml_003 as their destination, routing locally
    [GeneratedRegex(@"has selected point (?<point>\S+) as their destination", RegexOptions.CultureInvariant)]
    private static partial Regex QuantumTarget();

    // <Player Inventory Request Complete> Request[N] for 'X' [id] Type[..] Result[Succeed] ...
    [GeneratedRegex(
        @"^Request\[(?<req>\d+)\] for '[^']*' \[\d+\] Type\[[^\]]*\] Result\[(?<result>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlayerRequestComplete();

    // <Inventory Request Completed> Request[N] Player[X] Result[succeed] ...
    [GeneratedRegex(
        @"^Request\[(?<req>\d+)\] Player\[[^\]]*\] Result\[(?<result>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex RequestCompleted();

    // ... on Inventory[qrt_utility_heavy_backpack_01_01_03_745829874584, 745829874584]
    // ... on Inventory[PersonalStorage_GLSN_Shiv-000, 876737713105]
    [GeneratedRegex(
        @"on Inventory\[(?<entity>[A-Za-z0-9_\-]+), (?<geid>\d{6,})\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenFlow();

    // Source[0:ClientOnly:1] Capacity[196000] ... | Target[681562156430:Container:0] Capacity[2000000] ...
    [GeneratedRegex(
        @"^Source\[(?<srcinv>[^\]]*)\] Capacity\[(?<srccap>-?\d+)\].*?\| " +
        @"Target\[(?<tgtinv>[^\]]*)\] Capacity\[(?<tgtcap>-?\d+)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex DragCapacity();

    [GeneratedRegex(
        @"Landing \[\d+\] -> \[(?<landing>\d+)\]\. Location \[\d+\] -> \[(?<location>\d+)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex LocationChange();

    [GeneratedRegex(@"Location\[(?<name>[^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex LocationName();

    // ... releasing control token for 'GLSN_Shiv_876737712932' [876737712932]
    [GeneratedRegex(@"releasing control token for '(?<entity>[^']+)' \[(?<geid>\d{6,})\]", RegexOptions.CultureInvariant)]
    private static partial Regex VehicleFlownLine();

    // SetVehicleSpawningInformations - VehicleEntityId: [876737712932], LandingArea: ...
    [GeneratedRegex(@"VehicleEntityId: \[(?<geid>\d{6,})\]", RegexOptions.CultureInvariant)]
    private static partial Regex VehicleRetrievedLine();

    // <Channel Disconnected> cause=30016 reason="Remote Disconnect - Player requested disconnect" frame=...
    [GeneratedRegex(@"^cause=(?<cause>\d+) reason=""(?<reason>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Disconnect();

    public static InventoryEvent? Parse(LogLine line)
    {
        var ev = line.EventName switch
        {
            "InventoryManagementRequest" or "Inventory Mgmt Request Queued" => ParseQueued(line),
            "Add Inventory Management Move" => ParseAddMove(line),
            "Player Inventory Request Complete" => ParseComplete(line, PlayerRequestComplete()),
            "Inventory Request Completed" => ParseComplete(line, RequestCompleted()),
            "Inventory Token Flow" => ParseTokenFlow(line),
            "OnDragInventoryItemModifyTarget" => ParseDragCapacity(line),
            "Update Inventory Location" => ParseLocationChange(line),
            "RequestLocationInventory" => ParseLocationName(line),
            "UnstowPendingEntities" => ParseSpawn(line),
            "OnInventoryStoreItem" => ParseInventoryStore(line),
            "AttachmentReceived" => ParseAttachment(line),
            "Calculate Route" => ParseRouteStart(line),
            "Player Selected Quantum Target - Local" => ParseQuantumTarget(line),
            "Quantum Drive Arrived - Arrived at Final Destination" => new QuantumArrived(line.Timestamp),
            "Channel Disconnected" => ParseDisconnect(line),
            "Vehicle Control Flow" => ParseVehicleFlown(line),
            "CEntityComponentShipListProvider::SetVehicleSpawningInformations" => ParseVehicleRetrieved(line),

            // An event tag we have never seen dispatch to a move parser at all — most likely
            // the queued-tag rename happening again under a name this build doesn't know
            // about yet. Only worth flagging when the body still looks like a request line;
            // an unrelated new tag is just an unrelated new tag.
            _ when LooksLikeRequest().IsMatch(line.Rest) => new UnrecognisedMove(line.Timestamp, line.EventName),
            _ => null,
        };

        // Asset paths are not tied to one event: the same socpak shows up under a dozen
        // streaming and error tags, several of which have unparseable "<lambda_1>" names.
        // Only lines nothing else claimed are worth the scan.
        return ev ?? ParseAssetPath(line);
    }

    /// <summary>
    /// Applies the caller-dependent normalisations that only make sense once both halves
    /// of a request are known. Returns null when the move does not relocate anything.
    /// </summary>
    public static ItemMoved? Normalize(ItemMoved move, string? caller)
    {
        // Equipping leaves Target Inventory as INVALID; the destination is the player. So does
        // carrying an item in hand: it is on the player until it is stored, dropped or used up.
        if (move.Target.Kind == InventoryKind.Invalid && (IsAttachCaller(caller) || move.Action == CarryAction))
        {
            return move with { Target = InventoryRef.Equipped };
        }

        // Anything else without a real destination tells us nothing about where it ended up.
        if (!move.Target.IsHolding) return null;

        // Same grid in and out: AmmoRepool consolidating magazines, a Split, a re-stack.
        // The item never went anywhere, so recording it would only add noise to the timeline.
        if (move.Source.IsHolding && move.Source.Raw == move.Target.Raw) return null;

        return move;
    }

    private static InventoryEvent? ParseQueued(LogLine line)
    {
        // The same event name also carries "Attempting queuing of..." chatter.
        if (!line.Rest.StartsWith("Queued Request[", StringComparison.Ordinal)) return null;

        var m = QueuedRequest().Match(line.Rest);

        // The prefix matched but the body didn't: something inside the line changed shape.
        // This is the case that matters most — a Queued line is the authoritative move
        // record, so silently dropping it here is exactly how items go missing.
        if (!m.Success) return new UnrecognisedMove(line.Timestamp, line.EventName);

        var moveType = m.Groups["type"].Value;
        if (IsNonRelocating(moveType)) return null;

        // Store names the exact entity; every other type reports only a class in Source[].
        string itemClass;
        string? geid = null;
        var item = m.Groups["item"].Value;
        if (EntityId.TrySplit(item, out var cls, out var g))
        {
            itemClass = cls;
            geid = g;
        }
        else if (!IsBlank(m.Groups["srcitem"].Value))
        {
            itemClass = m.Groups["srcitem"].Value;
        }
        else if (!IsBlank(item))
        {
            // A Store of an entity the game never gave an id — a tool attachment picked off
            // a ship's fittings, say — writes the bare class into Item[] and nothing into
            // Source[]. It still went into the target; which one it was is simply unknown.
            itemClass = item;
        }
        else
        {
            return null;
        }

        return new ItemMoved(
            line.Timestamp,
            int.Parse(m.Groups["req"].Value),
            m.Groups["player"].Value,
            m.Groups["pid"].Value,
            moveType,
            InventoryRef.Parse(m.Groups["srcinv"].Value),
            InventoryRef.Parse(m.Groups["tgtinv"].Value),
            itemClass,
            geid,
            int.Parse(m.Groups["srcamt"].Value),
            m.Groups["action"].Value is "" or "None" ? null : m.Groups["action"].Value);
    }

    private static bool IsBlank(string value) =>
        value is "" or "NULL" or "NONE" or "null" or "none";

    /// <summary>
    /// The request number and move type of any move request line, read loosely enough to
    /// survive the rewordings <see cref="LooksLikeRequest"/> is there to catch.
    /// </summary>
    [GeneratedRegex(
        @"^(?<kind>New|Queued) Request\[(?<req>\d+)\].*?Type\[(?<type>[^\]]*)\]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RequestShape();

    /// <summary>What <see cref="ReadRequestShape"/> saw on a move request line.</summary>
    /// <param name="IsAddMove">The "New Request" half of the handshake rather than "Queued Request".</param>
    public readonly record struct RequestShapeInfo(int RequestNo, string MoveType, bool IsAddMove);

    /// <summary>
    /// Recognises a move request line by its body alone, whatever tag it carries and whether
    /// or not <see cref="Parse"/> can still read it. Used to measure how much of the log the
    /// parser actually captures.
    /// </summary>
    public static RequestShapeInfo? ReadRequestShape(LogLine line)
    {
        var m = RequestShape().Match(line.Rest);
        if (!m.Success || !int.TryParse(m.Groups["req"].Value, out var requestNo)) return null;

        return new RequestShapeInfo(
            requestNo, m.Groups["type"].Value,
            m.Groups["kind"].Value.Equals("New", StringComparison.OrdinalIgnoreCase));
    }

    private static InventoryEvent? ParseAddMove(LogLine line)
    {
        var m = AddMove().Match(line.Rest);
        if (!m.Success) m = AddMoveLoose().Match(line.Rest);
        if (!m.Success) return ParseCutAddMove(line);

        var moveType = m.Groups["type"].Value;
        if (IsNonRelocating(moveType)) return null;

        var itemClass = m.Groups["class"].Value;
        var caller = m.Groups["caller"].Value;
        var requestNo = int.Parse(m.Groups["req"].Value);
        var source = InventoryRef.Parse(m.Groups["srcinv"].Value);

        // A bracketed list is a multi-select drag, and this line is its only record: the
        // Queued counterpart degenerates to Source[NULL] / Item[NONE]. Type[Store] is
        // excluded because its inventories are inverted here relative to the Queued line.
        if (itemClass.StartsWith('[') && moveType != "Store")
        {
            var classes = BatchClasses(itemClass);
            if (classes.Count > 0)
            {
                return new MoveBatchRequested(
                    line.Timestamp, requestNo, moveType, source,
                    InventoryRef.Parse(m.Groups["tgtinv"].Value), classes, caller);
            }
        }

        return new MoveRequested(line.Timestamp, requestNo, moveType, source, itemClass, caller);
    }

    /// <summary>
    /// The second chance for an Add-move line neither full shape could read: one the game cut
    /// short (see <see cref="AddMoveCut"/>). Only a multi-select batch into a real inventory is
    /// recovered: Caller only matters when the target is INVALID, where AttachItem turns the
    /// batch into an equip, so losing it there loses nothing. Every other cut line is still
    /// flagged: a batch onto the body or a single-class line needs its Caller to tell an equip
    /// from a drag, and a Store has its own Queued lines.
    /// </summary>
    private static InventoryEvent? ParseCutAddMove(LogLine line)
    {
        var m = AddMoveCut().Match(line.Rest);
        if (!m.Success)
        {
            // Every shape failed. If the line still opens with "New request[" / "New
            // Request[" it is a request line whose body changed underneath us, not some
            // unrelated line dispatched here by mistake.
            return LooksLikeRequest().IsMatch(line.Rest)
                ? new UnrecognisedMove(line.Timestamp, line.EventName)
                : null;
        }

        // A line that merely ends in "..." is not a cut one: only a line the game actually cut
        // is as long as the limit. A shorter one failed the full shapes for some other reason,
        // which is what the canary is for. (A whole Caller[...] needs no check here:
        // AddMoveLoose already reads any line that still has one.)
        if (!WasCutByGame(line)) return new UnrecognisedMove(line.Timestamp, line.EventName);

        var moveType = m.Groups["type"].Value;
        if (IsNonRelocating(moveType)) return null;

        // INVALID is an equip only when Caller says AttachItem, and the cut took Caller: no way
        // to tell. Any other non-holding target is dropped, as Normalize drops it on a full line.
        var target = InventoryRef.Parse(m.Groups["tgtinv"].Value);
        if (moveType == "Store" || target.Kind == InventoryKind.Invalid)
            return new UnrecognisedMove(line.Timestamp, line.EventName);
        if (!target.IsHolding) return null;

        // The list closes with "] ]" (or "]]"): the last entry's bracket, then the list's own.
        // Without it the cut fell inside the list, and only the entries before it are known.
        // A half-written last entry has no closing bracket, so BatchClass never reads it.
        var text = m.Groups["class"].Value;
        var close = BatchListEnd().Match(text);
        var classes = BatchClasses(close.Success ? text[..close.Index] : text);
        if (classes.Count == 0) return new UnrecognisedMove(line.Timestamp, line.EventName);

        return new MoveBatchRequested(
            line.Timestamp, int.Parse(m.Groups["req"].Value), moveType,
            InventoryRef.Parse(m.Groups["srcinv"].Value), target,
            classes, Caller: null, ListCut: !close.Success);
    }

    /// <summary>
    /// The game keeps 1023 characters of a line from its severity tag on. Measured with a
    /// little slack, in case the limit is in bytes and a player name is not plain ASCII.
    /// </summary>
    private const int MinCutLength = 1000;

    private static bool WasCutByGame(LogLine line)
    {
        var raw = line.Raw.TrimEnd();
        var severity = raw.IndexOf('[');
        return severity >= 0 && raw.Length - severity >= MinCutLength;
    }

    /// <summary>Where a bracketed ItemClass list ends: an entry's ']' and then the list's own.</summary>
    [GeneratedRegex(@"(?<=\])\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BatchListEnd();

    private static List<string> BatchClasses(string list) =>
        BatchClass().Matches(list)
            .Select(x => x.Groups["cls"].Value.Trim())
            .Where(x => x.Length > 0 && x is not ("NULL" or "NONE"))
            .ToList();

    private static InventoryEvent? ParseSpawn(LogLine line)
    {
        var m = UnstowSpawn().Match(line.Rest);
        if (!m.Success) return null;

        var geid = m.Groups["geid"].Value;
        if (!EntityId.TrySplit(m.Groups["entity"].Value, out var cls, out var entityGeid)) return null;
        if (entityGeid != geid) return null;

        return new EntitySpawned(line.Timestamp, int.Parse(m.Groups["req"].Value), geid, cls);
    }

    private static InventoryEvent? ParseInventoryStore(LogLine line)
    {
        var m = InventoryStore().Match(line.Rest);
        if (!m.Success) return null;

        if (!EntityId.TrySplit(m.Groups["entity"].Value, out _, out var geid)) return null;

        var target = InventoryRef.Parse(m.Groups["inv"].Value);
        if (!target.IsHolding) return null;

        return new ItemStored(line.Timestamp, geid, m.Groups["cls"].Value, target);
    }

    private static InventoryEvent? ParseAttachment(LogLine line)
    {
        var m = Attachment().Match(line.Rest);
        if (!m.Success) return null;

        return new AttachmentSeen(
            line.Timestamp, m.Groups["geid"].Value, m.Groups["cls"].Value, m.Groups["port"].Value,
            m.Groups["status"].Value);
    }

    private static InventoryEvent? ParseRouteStart(LogLine line)
    {
        var m = RouteStart().Match(line.Rest);
        if (!m.Success) return null;

        var name = m.Groups["name"].Value.Trim();
        return name.Length == 0 ? null : new PlaceNamed(line.Timestamp, name);
    }

    private static InventoryEvent? ParseQuantumTarget(LogLine line)
    {
        var m = QuantumTarget().Match(line.Rest);
        return m.Success ? new QuantumTargetSelected(line.Timestamp, m.Groups["point"].Value) : null;
    }

    private static InventoryEvent? ParseAssetPath(LogLine line)
    {
        if (!line.Raw.Contains("objectcontainers/pu/loc/", StringComparison.OrdinalIgnoreCase)) return null;

        var system = Systems.FromAssetPath(line.Raw);
        return system is null ? null : new PlaceAssetSeen(line.Timestamp, system);
    }

    private static InventoryEvent? ParseComplete(LogLine line, Regex pattern)
    {
        var m = pattern.Match(line.Rest);
        if (!m.Success) return null;

        return new MoveCompleted(
            line.Timestamp,
            int.Parse(m.Groups["req"].Value),
            m.Groups["result"].Value);
    }

    private static InventoryEvent? ParseTokenFlow(LogLine line)
    {
        var m = TokenFlow().Match(line.Rest);
        if (!m.Success) return null;

        var geid = m.Groups["geid"].Value;
        var entity = m.Groups["entity"].Value;

        // A ship's locker is named by its class alone, with no geid in the name.
        if (ShipStorage.TryShipClass(entity, out _)) return new ContainerIdentified(line.Timestamp, geid, entity);

        // The entity is "<class>_<geid>"; the trailing geid must agree with the standalone one.
        if (!EntityId.TrySplit(entity, out var cls, out var entityGeid)) return null;
        if (entityGeid != geid) return null;

        return new ContainerIdentified(line.Timestamp, geid, cls);
    }

    private static InventoryEvent? ParseDragCapacity(LogLine line)
    {
        var m = DragCapacity().Match(line.Rest);
        if (!m.Success) return null;

        return new ContainerCapacitySeen(
            line.Timestamp,
            InventoryRef.Parse(m.Groups["srcinv"].Value),
            long.Parse(m.Groups["srccap"].Value),
            InventoryRef.Parse(m.Groups["tgtinv"].Value),
            long.Parse(m.Groups["tgtcap"].Value));
    }

    private static InventoryEvent? ParseLocationChange(LogLine line)
    {
        var m = LocationChange().Match(line.Rest);
        return m.Success
            ? new LocationChanged(line.Timestamp, m.Groups["landing"].Value, m.Groups["location"].Value)
            : null;
    }

    private static InventoryEvent? ParseVehicleFlown(LogLine line)
    {
        var m = VehicleFlownLine().Match(line.Rest);
        if (!m.Success) return null;

        var geid = m.Groups["geid"].Value;
        return EntityId.TrySplit(m.Groups["entity"].Value, out var cls, out var entityGeid) && entityGeid == geid
            ? new VehicleFlown(line.Timestamp, geid, cls)
            : null;
    }

    private static InventoryEvent? ParseVehicleRetrieved(LogLine line)
    {
        var m = VehicleRetrievedLine().Match(line.Rest);
        return m.Success ? new VehicleRetrieved(line.Timestamp, m.Groups["geid"].Value) : null;
    }

    private static InventoryEvent? ParseDisconnect(LogLine line)
    {
        var m = Disconnect().Match(line.Rest);
        return m.Success
            ? new ChannelDisconnected(line.Timestamp, int.Parse(m.Groups["cause"].Value), m.Groups["reason"].Value)
            : null;
    }

    private static InventoryEvent? ParseLocationName(LogLine line)
    {
        var m = LocationName().Match(line.Rest);
        if (!m.Success) return null;

        // "Location[]" matches with an empty name. Binding an id to "" would lock out the
        // real name later, because the ingestor treats a second, different name as a
        // conflict rather than a correction. Same guard as ParseRouteStart.
        var name = m.Groups["name"].Value.Trim();
        return name.Length == 0 ? null : new LocationNamed(line.Timestamp, name);
    }
}
