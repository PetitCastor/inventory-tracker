using InventoryTracker.Resolve;

namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// The ship test of 2026-10-12 at Keeger, each step checked in game by the player: a Grey's
/// Shiv's locker used in the hangar and in flight, then the ship stored and retrieved with the
/// Arclight inside. See the fixture's README.
/// </summary>
public sealed class ShipLockerTests
{
    private const string Day = "2026-10-12";
    private const string Keeger = "3058615591";
    private const string Shiv = "GLSN_Shiv";

    private const string Arclight = "862186749926";
    private const string ParaMed = "735739878137";

    [Fact]
    public void An_item_moved_into_a_ship_locker_is_in_the_ship_not_at_the_station()
    {
        // 18:45:56 request 24 moved the Arclight out of the backpack into PersonalStorage_GLSN_Shiv-000,
        // with the ship parked in the hangar at Keeger.
        using var replay = PlaytestReplay.On(Day, "18:46:00");

        var arclight = replay.Named(Arclight);
        Assert.NotNull(arclight);
        Assert.True(arclight.IsInShip);
        Assert.False(arclight.IsAtStation);
        Assert.Equal(Shiv, arclight.Root!.Key);
        Assert.Equal(Confidence.High, arclight.Confidence);
    }

    [Fact]
    public void Taking_it_back_out_of_the_locker_leaves_the_ship()
    {
        // 18:46:23 request 25 moved it back into the backpack.
        using var replay = PlaytestReplay.On(Day, "18:46:30");

        Assert.Equal("carried on your character", replay.Named(Arclight)?.Where);
    }

    [Fact]
    public void A_locker_used_in_flight_is_the_same_ship()
    {
        // 18:49:32 request 35 moved the ParaMed into the same locker in flight, just outside the
        // hangar; the player had stored it from the holster into the backpack at 18:48:50.
        using var replay = PlaytestReplay.On(Day, "18:50:00");

        Assert.Equal(Shiv, replay.RootOf(ParaMed)?.Key);
        Assert.True(replay.Named(ParaMed)?.IsInShip);
    }

    [Fact]
    public void A_ships_contents_survive_storing_and_retrieving_it()
    {
        // 19:02:55 the Arclight went into the locker; the ship was stored by 19:04:07 and called
        // back out at 19:04:21, the same ship geid with the same locker geids.
        using var replay = PlaytestReplay.On(Day, "19:04:30");

        Assert.True(replay.Named(Arclight)?.IsInShip);

        var retrieved = replay.Resolver.LastRetrieved(Shiv);
        Assert.NotNull(retrieved);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 19, 4, 21, 364, TimeSpan.Zero), retrieved.At);
        Assert.Equal(Keeger, retrieved.LocationId);
    }

    [Fact]
    public void Nothing_in_a_locker_is_shown_at_the_station_it_was_opened_at()
    {
        using var replay = PlaytestReplay.On(Day, "19:04:30");

        Assert.DoesNotContain(replay.Holdings, h => h.IsAtStation && h.Chain.Any(l => l.Key == "876737713105"));
    }

    [Fact]
    public void The_item_taken_out_after_the_retrieve_is_carried_again()
    {
        // 19:05:31 request 51 moved the Arclight from the locker back into the backpack.
        using var replay = PlaytestReplay.On(Day, "19:06:00");

        Assert.Equal("carried on your character", replay.Named(Arclight)?.Where);
        Assert.DoesNotContain(replay.Holdings, h => h.IsInShip && h.Geid == Arclight);
    }
}
