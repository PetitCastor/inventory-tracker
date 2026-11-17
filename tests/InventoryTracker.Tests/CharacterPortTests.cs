using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Resolve;

namespace InventoryTracker.Tests;

/// <summary>
/// The character itself — its body, what the character creator puts on it, the mobiGlas, lens
/// and radar every character has — comes in every enumeration of the body, but is not an item
/// the player owns and is never listed as one.
/// </summary>
public class CharacterPortTests
{
    [Theory]
    [InlineData("Body_ItemPort")]
    [InlineData("Head_ItemPort")]
    [InlineData("Hair_ItemPort")]
    [InlineData("Beard_ItemPort")]
    [InlineData("Eyebrow_ItemPort")]
    [InlineData("Eyelashes_ItemPort")]
    [InlineData("Eyes_ItemPort")]
    [InlineData("Eyedetail_ItemPort")]
    [InlineData("Teeth_ItemPort")]
    [InlineData("Universal_Scalp_ItemPort")]
    [InlineData("Piercings_L_Ear_ItemPort")]
    [InlineData("Piercings_R_Ear_ItemPort")]
    [InlineData("mobiglas_attach")]
    [InlineData("mobiglas_screen_attach")]
    [InlineData("legacy_mobiglas_screen_attach")]
    [InlineData("Lens_ItemPort")]
    [InlineData("radar")]
    [InlineData("universal_necksock_undersuit")]
    public void A_port_of_the_character_itself_is_a_character_port(string port)
    {
        Assert.True(AttachmentSeen.IsCharacterPort(port));
    }

    [Theory]
    [InlineData("Hat_ItemPort")]
    [InlineData("Eye_Accessories_ItemPort")]
    [InlineData("Armor_Helmet")]
    [InlineData("Armor_Undersuit")]
    [InlineData("backpack")]
    [InlineData("wep_sidearm")]
    [InlineData("helmet_visor")]
    [InlineData("universal_necksock")]
    [InlineData("medPen_attach_1")]
    [InlineData("weapon_attach_hand_right")]
    public void A_port_holding_an_item_the_player_owns_is_not(string port)
    {
        Assert.False(AttachmentSeen.IsCharacterPort(port));
    }

    [Fact]
    public void An_enumeration_of_the_body_lists_only_what_the_player_owns()
    {
        // One burst as the game writes it on spawn (classes from the 2026-11-13 log): the body
        // first, then the creator's parts and the built-in gear, mixed with a helmet, a mask
        // and glasses the player bought.
        string[][] burst =
        [
            ["BODY", "body_01_noMagicPocket", "Body_ItemPort"],
            ["HEAD", "PU_Protos_Head", "Head_ItemPort"],
            ["HAIR", "hair_75", "Hair_ItemPort"],
            ["EYES", "PU_Head_Eyes_White_CharacterCustomizer", "Eyes_ItemPort"],
            ["EYEDETAIL", "Head_Eyedetail", "Eyedetail_ItemPort"],
            ["STUD", "pcg_stud_l_ear_05", "Piercings_L_Ear_ItemPort"],
            ["MOBIGLAS", "LegacyMobiGlas", "legacy_mobiglas_screen_attach"],
            ["RADAR", "FPS_DefaultRadar_Lens", "radar"],
            ["HELMET", "grin_utility_medium_helmet_01_01_01", "Armor_Helmet"],
            ["MASK", "srvl_mask_01_01_01", "Hat_ItemPort"],
            ["GLASSES", "r6p_glasses_01_01_01", "Eye_Accessories_ItemPort"],
            ["NECKSOCK", "universal_necksock_undersuit_01", "universal_necksock_undersuit"],
        ];

        var directory = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        try
        {
            db.Initialize();
            using (var connection = db.Open())
            {
                using (var session = connection.CreateCommand())
                {
                    session.CommandText = """
                        INSERT INTO session(path, first_ts, last_ts, byte_offset, complete)
                        VALUES('character.log', NULL, NULL, 0, 1);
                        """;
                    session.ExecuteNonQuery();
                }

                for (var i = 0; i < burst.Length; i++)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO attachment_sighting(session_id, line_offset, ts, geid, class_name, port, burst_id, placeholder)
                        VALUES((SELECT id FROM session WHERE path = 'character.log'), $offset,
                               '2026-11-13T14:26:59.6480000+00:00', $geid, $class, $port, 1, 0);
                        """;
                    command.Parameters.AddWithValue("$offset", i + 1);
                    command.Parameters.AddWithValue("$geid", burst[i][0]);
                    command.Parameters.AddWithValue("$class", burst[i][1]);
                    command.Parameters.AddWithValue("$port", burst[i][2]);
                    command.ExecuteNonQuery();
                }
            }

            var resolver = HoldingResolver.Load(db, DateTimeOffset.Parse("2026-11-13T15:00:00Z"));

            Assert.Equal(
                new[] { "GLASSES", "HELMET", "MASK" },
                resolver.ResolveAll().Select(h => h.Geid ?? "").Order(StringComparer.Ordinal));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
