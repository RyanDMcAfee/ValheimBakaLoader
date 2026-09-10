using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The decisions the RPC bridge makes before it touches anything: which launch history to
    /// trust, when a held start is worth a Discord post, whether a folder is somebody else's,
    /// what counts as a valid world or backup reference, and whether a pre-update snapshot
    /// actually protected anything. Each of these used to live inline in an RPC body where
    /// nothing could reach it.
    /// </summary>
    public class BlendWindowBridgeRulesTests
    {
        // ---------------------------------------------------------------- Thunderstore page

        /// <summary>
        /// The page hands over a package identity, never an address, and the app builds the
        /// address. Anything that could carry the shell somewhere other than the mod's own
        /// Thunderstore page has to come back as no address at all.
        /// </summary>
        [Theory]
        [InlineData("Azumatt", "AzuAntiItemLag", true)]
        [InlineData("JereKuusela", "World_Edit_Commands", true)]
        [InlineData("ComfyMods", "Gizmo.Legacy", true)]
        [InlineData("Azumatt/..", "AzuAntiItemLag", false)]      // a slash walks the path
        [InlineData("Azumatt", "Azu/../../evil", false)]
        [InlineData("https://evil.example", "x", false)]          // a scheme
        [InlineData("Azumatt", "Azu?redirect=evil", false)]       // a query
        [InlineData("Azumatt", "Azu#frag", false)]                // a fragment
        [InlineData("Azumatt Mods", "AzuAntiItemLag", false)]     // a space
        [InlineData("..", "..", false)]                           // dots only
        [InlineData("", "AzuAntiItemLag", false)]
        [InlineData("Azumatt", null, false)]
        public void Only_a_plain_package_identity_makes_a_thunderstore_address(
            string packageNamespace, string packageName, bool expected)
        {
            var url = BlendWindow.ThunderstorePageUrl(packageNamespace, packageName);

            Assert.Equal(expected, url != null);
            if (expected)
                Assert.Equal(
                    "https://thunderstore.io/c/valheim/p/" + packageNamespace + "/" + packageName + "/",
                    url);
        }

        /// <summary>
        /// A folder the index matched carries its package identity and an address. A loose DLL
        /// folder that matched nothing carries neither, so the row has no page to offer rather
        /// than one guessed from the folder name.
        /// </summary>
        [Fact]
        public void A_matched_mod_row_carries_its_thunderstore_page_and_an_unmatched_one_carries_none()
        {
            var matched = JObject.FromObject(BlendWindow.BuildModDto(new InstalledMod
            {
                Author = "Azumatt",
                ModName = "AzuAntiItemLag",
                InstalledVersion = "1.0.4",
                ThunderstoreNamespace = "Azumatt",
                ThunderstoreName = "AzuAntiItemLag",
            }));

            Assert.Equal("Azumatt", matched.Value<string>("thunderstoreNamespace"));
            Assert.Equal("AzuAntiItemLag", matched.Value<string>("thunderstoreName"));
            Assert.Equal(
                "https://thunderstore.io/c/valheim/p/Azumatt/AzuAntiItemLag/",
                matched.Value<string>("thunderstoreUrl"));

            // The keys the page already reads are untouched.
            Assert.Equal("AzuAntiItemLag", matched.Value<string>("ModName"));

            var unmatched = JObject.FromObject(BlendWindow.BuildModDto(new InstalledMod
            {
                Author = "",
                ModName = "SomeLooseDll",
                InstalledVersion = "unknown",
            }));

            Assert.Null(unmatched.Value<string>("thunderstoreNamespace"));
            Assert.Null(unmatched.Value<string>("thunderstoreName"));
            Assert.Null(unmatched.Value<string>("thunderstoreUrl"));
        }

        // ---------------------------------------------------------------- LG-09

        [Fact]
        public void The_stored_launch_history_beats_the_payload_even_when_the_payload_has_both_fields()
        {
            // The page was loaded before the last launch recorded its build, so the payload
            // carries a stale pair. Both fields being filled in is not evidence of freshness.
            var fromBrowser = new ServerPreferences
            {
                ProfileName = "Default",
                LastLaunchedServerBuild = "21981590",
                LastLaunchedGameVersion = "0.220.5",
            };
            var onDisk = new ServerPreferences
            {
                ProfileName = "Default",
                LastLaunchedServerBuild = "25185644",
                LastLaunchedGameVersion = "1.0.7",
            };

            var merged = BlendWindow.MergeLaunchHistory(fromBrowser, onDisk);

            Assert.Equal("25185644", merged.LastLaunchedServerBuild);
            Assert.Equal("1.0.7", merged.LastLaunchedGameVersion);
        }

        [Fact]
        public void A_profile_with_no_stored_history_keeps_whatever_the_payload_carried()
        {
            var fromBrowser = new ServerPreferences
            {
                ProfileName = "Fresh",
                LastLaunchedServerBuild = "21981590",
            };

            var merged = BlendWindow.MergeLaunchHistory(fromBrowser, new ServerPreferences { ProfileName = "Fresh" });

            Assert.Equal("21981590", merged.LastLaunchedServerBuild);
        }

        // ---------------------------------------------------------------- LG-11

        [Theory]
        [InlineData(true, true, true)]    // BakaLoader started it: nobody may be watching
        [InlineData(false, true, false)]  // the host pressed Start and is looking at the banner
        [InlineData(true, false, false)]  // event posts are off
        [InlineData(false, false, false)]
        public void Only_an_unattended_hold_is_worth_a_Discord_post(bool automatic, bool posts, bool expected)
        {
            Assert.Equal(expected, BlendWindow.ShouldAnnounceHold(automatic, posts));
        }

        // ---------------------------------------------------------------- W-12

        [Fact]
        public void A_save_folder_another_profile_points_at_is_reported()
        {
            var shared = Path.Combine(Path.GetTempPath(), "vbl-shared-worlds");
            var all = new List<ServerPreferences>
            {
                new() { ProfileName = "Doomed", SaveDataFolderPath = shared },
                new() { ProfileName = "Keeper", SaveDataFolderPath = shared + Path.DirectorySeparatorChar },
            };

            var owner = BlendWindow.ProfileSharingSaveFolder(all, "Doomed", shared);

            Assert.NotNull(owner);
            Assert.Equal("Keeper", owner.ProfileName);
        }

        [Fact]
        public void A_save_folder_only_this_profile_uses_is_free_to_delete()
        {
            var mine = Path.Combine(Path.GetTempPath(), "vbl-my-worlds");
            var all = new List<ServerPreferences>
            {
                new() { ProfileName = "Doomed", SaveDataFolderPath = mine },
                new() { ProfileName = "Other", SaveDataFolderPath = Path.Combine(Path.GetTempPath(), "vbl-other") },
            };

            Assert.Null(BlendWindow.ProfileSharingSaveFolder(all, "Doomed", mine));
        }

        // ---------------------------------------------------------------- W-13

        [Fact]
        public void The_restore_safety_layer_is_stamped_in_local_time()
        {
            // Every other stamp in the system - the game's own restore layers, BakaLoader's
            // pre-update layers - is a local wall clock, and the Barrow parses all of them the
            // same way. (On a machine whose local time IS UTC the two agree and this only
            // proves the format.)
            var name = BlendWindow.RestoreLayerName("Midgard");

            Assert.StartsWith("Midgard_backup_restore-", name);

            var stamp = name["Midgard_backup_restore-".Length..];
            var parsed = DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(Math.Abs((DateTime.Now - parsed).TotalMinutes) < 2,
                $"the stamp {stamp} is not on the local clock");
        }

        [Fact]
        public void The_restore_layer_name_formats_the_time_it_is_given()
        {
            var when = new DateTime(2026, 9, 10, 14, 5, 6);
            Assert.Equal("Midgard_backup_restore-20260910-140506", BlendWindow.RestoreLayerName("Midgard", when));
        }

        // ---------------------------------------------------------------- W-15

        [Fact]
        public void A_world_source_reference_must_name_a_known_folder_and_a_real_subfolder()
        {
            var known = new List<string> { Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) };
            var folder = known[0];

            // The shape the setup wizard actually sends is accepted.
            BlendWindow.ValidateWorldSourceRef("Midgard", folder, "worlds_local", known);

            // A subfolder that could climb out of the save folder is refused, and so is one
            // that simply is not one of the two the game uses.
            Assert.Throws<ArgumentException>(
                () => BlendWindow.ValidateWorldSourceRef("Midgard", folder, @"..\..\Windows", known));
            Assert.Throws<ArgumentException>(
                () => BlendWindow.ValidateWorldSourceRef("Midgard", folder, "backups", known));

            // A world name with a separator in it is refused.
            Assert.Throws<ArgumentException>(
                () => BlendWindow.ValidateWorldSourceRef(@"..\Midgard", folder, "worlds_local", known));

            // A folder nobody has ever heard of is refused.
            Assert.Throws<ArgumentException>(
                () => BlendWindow.ValidateWorldSourceRef("Midgard", @"C:\Windows\System32", "worlds_local", known));
        }

        // ---------------------------------------------------------------- W-17 (deleting a world)

        /// <summary>
        /// The last gate in front of an irreversible delete. Whitespace either side is forgiven
        /// because a name copied off the screen usually brings some; a different capitalisation
        /// is not, because the whole point of the box is that the host reads the name.
        /// </summary>
        [Theory]
        [InlineData("Midgard", "Midgard", true)]
        [InlineData("Midgard", "  Midgard  ", true)]
        [InlineData("Midgard", "midgard", false)]
        [InlineData("Midgard", "MIDGARD", false)]
        [InlineData("Midgard", "Midgar", false)]      // one letter short of the name
        [InlineData("Midgard", "Midgard2", false)]    // the neighbour with a similar name
        [InlineData("Midgard", "", false)]
        [InlineData("Midgard", "   ", false)]
        [InlineData("Midgard", null, false)]
        [InlineData("", "", false)]
        [InlineData(null, "Midgard", false)]
        public void A_world_goes_only_when_its_own_name_was_written_out(string world, string typed, bool confirmed)
        {
            Assert.Equal(confirmed, BlendWindow.DeleteWorldNameConfirmed(world, typed));
        }

        [Fact]
        public void A_realm_with_no_save_folder_of_its_own_still_claims_the_world_it_is_pointed_at()
        {
            // The one that would have hurt: a blank SaveDataFolderPath means "the shared
            // user-level folder", not "somewhere else". Read as somewhere else, nothing claims
            // the world and the delete takes it out from under a realm that is merely stopped.
            var userSave = Path.Combine(Path.GetTempPath(), "vbl-user-worlds");
            var all = new List<ServerPreferences>
            {
                new() { ProfileName = "Default", WorldName = "Midgard", SaveDataFolderPath = null },
            };

            var claimant = BlendWindow.ProfileSelectingWorld(all, "Midgard", userSave, userSave);

            Assert.NotNull(claimant);
            Assert.Equal("Default", claimant.ProfileName);
        }

        [Fact]
        public void A_realm_keeping_its_worlds_elsewhere_does_not_claim_a_world_of_the_same_name()
        {
            var userSave = Path.Combine(Path.GetTempPath(), "vbl-user-worlds");
            var isolated = Path.Combine(Path.GetTempPath(), "vbl-user-worlds", "servers", "proving");
            var all = new List<ServerPreferences>
            {
                new() { ProfileName = "Proving", WorldName = "Midgard", SaveDataFolderPath = isolated },
            };

            // Two worlds, same name, different folders. Only the realm's own copy is claimed.
            Assert.Null(BlendWindow.ProfileSelectingWorld(all, "Midgard", userSave, userSave));
            Assert.Equal("Proving",
                BlendWindow.ProfileSelectingWorld(all, "Midgard", isolated, userSave)?.ProfileName);
        }

        [Fact]
        public void An_archived_realm_claims_its_world_too_and_a_capital_does_not_hide_it()
        {
            // Archived is "put away", not "given up": bringing one back onto a world that is no
            // longer there is exactly what this stops. World names are file names on Windows,
            // so the match ignores case the way the file system does.
            var folder = Path.Combine(Path.GetTempPath(), "vbl-user-worlds");
            var all = new List<ServerPreferences>
            {
                new() { ProfileName = "Old raid", WorldName = "MIDGARD", SaveDataFolderPath = folder, Archived = true },
            };

            Assert.Equal("Old raid",
                BlendWindow.ProfileSelectingWorld(all, "midgard", folder, folder)?.ProfileName);
        }

        [Fact]
        public void A_world_no_realm_is_pointed_at_is_free_to_delete()
        {
            var folder = Path.Combine(Path.GetTempPath(), "vbl-user-worlds");
            var all = new List<ServerPreferences>
            {
                new() { ProfileName = "Default", WorldName = "Midgard", SaveDataFolderPath = folder },
            };

            Assert.Null(BlendWindow.ProfileSelectingWorld(all, "Eikthyr", folder, folder));
            Assert.Null(BlendWindow.ProfileSelectingWorld(null, "Midgard", folder, folder));
            Assert.Null(BlendWindow.ProfileSelectingWorld(all, "", folder, folder));
        }

        // ---------------------------------------------------------------- W-16

        [Theory]
        [InlineData("Midgard.fwl", false)]                          // the live pre-1.0 pair
        [InlineData("Midgard.db", false)]
        [InlineData("Midgard", false)]                              // the live 1.0 directory
        [InlineData("Midgard_backup_20260910-140506.fwl", true)]
        [InlineData("Midgard_backup_20260910-140506", true)]        // a 1.0 backup directory
        [InlineData("Asgard_backup_20260910-140506.fwl", false)]    // somebody else's layer
        public void Only_a_backup_shaped_name_of_that_world_is_a_usable_reference(string file, bool usable)
        {
            Assert.Equal(usable, BlendWindow.IsUsableBackupReference(file, "Midgard"));
        }

        // ---------------------------------------------------------------- LG-10, caller side

        [Fact]
        public void A_snapshot_that_copied_nothing_while_worlds_exist_protected_nothing()
        {
            var copiedNone = new WorldStore.WorldSnapshotResult();
            copiedNone.Skipped.Add("Midgard: nothing saved yet");

            Assert.False(BlendWindow.SnapshotProtectedTheWorlds(copiedNone, hadWorlds: true));
        }

        [Fact]
        public void An_empty_save_folder_has_nothing_to_protect_and_passes()
        {
            Assert.True(BlendWindow.SnapshotProtectedTheWorlds(new WorldStore.WorldSnapshotResult(), hadWorlds: false));
        }

        [Fact]
        public void A_snapshot_that_copied_a_world_protected_it()
        {
            var copied = new WorldStore.WorldSnapshotResult();
            copied.Copied.Add("Midgard_backup_20260910-140506");

            Assert.True(BlendWindow.SnapshotProtectedTheWorlds(copied, hadWorlds: true));
        }

        [Fact]
        public void A_snapshot_that_failed_outright_protected_nothing()
        {
            var failed = new WorldStore.WorldSnapshotResult { Error = "the save folder could not be read" };

            Assert.False(BlendWindow.SnapshotProtectedTheWorlds(failed, hadWorlds: false));
        }
    }
}
