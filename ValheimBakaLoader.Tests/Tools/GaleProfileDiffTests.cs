using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The comparison between a Gale profile and a server's mods, over hand written manifests.
    /// Every manifest here goes through <see cref="GaleSyncClient.Parse"/> first, so the diff
    /// is tested on what the reader actually hands it rather than on objects built to suit.
    /// </summary>
    public class GaleProfileDiffTests
    {
        private const string Stamp = "2026-10-06T12:34:56.789Z";

        private static string Line(string name, bool enabled, int major, int minor, int patch) =>
            "{\"name\":\"" + name + "\",\"enabled\":" + (enabled ? "true" : "false")
            + ",\"version\":{\"major\":" + major + ",\"minor\":" + minor + ",\"patch\":" + patch + "}}";

        private static GaleProfileMeta Meta(params string[] lines) => MetaAt(Stamp, lines);

        private static GaleProfileMeta MetaAt(string updatedAt, params string[] lines)
        {
            var json = "{\"id\":\"ABCDEF\",\"manifest\":{\"profileName\":\"Test\",\"mods\":["
                + string.Join(",", lines) + "]},\"updatedAt\":\"" + updatedAt + "\"}";
            var meta = GaleSyncClient.Parse(json);
            Assert.NotNull(meta);
            return meta;
        }

        private static List<InstalledMod> Server(params (string Folder, string Version)[] mods) =>
            mods.Select(m =>
            {
                var dash = m.Folder.IndexOf('-');
                return dash > 0
                    ? new InstalledMod { Author = m.Folder.Substring(0, dash), ModName = m.Folder.Substring(dash + 1), InstalledVersion = m.Version }
                    : new InstalledMod { Author = "", ModName = m.Folder, InstalledVersion = m.Version };
            }).ToList();

        private static GaleDiffRow Row(GaleProfileDiffResult diff, string name) =>
            Assert.Single(diff.Rows, r => r.Name == name);

        [Fact]
        public void A_ticked_mod_the_server_does_not_have_is_an_add()
        {
            var diff = GaleProfileDiff.Compare(Meta(Line("ValheimModding-Jotunn", true, 2, 20, 1)), Server(), null);

            var row = Row(diff, "ValheimModding-Jotunn");
            Assert.Equal(GaleDiffKind.Add, row.Kind);
            Assert.Equal("2.20.1", row.ProfileVersion);
            Assert.Null(row.ServerVersion);
            Assert.False(row.Installed);
            Assert.Equal(1, diff.AddCount);
            Assert.True(diff.HasWork);
        }

        [Fact]
        public void The_BepInEx_pack_is_the_framework_never_an_add_and_never_work()
        {
            // Every Valheim profile carries the pack, and the mod scan never lists it, so a
            // plain comparison would make it an add on every check and raise the row for ever.
            var diff = GaleProfileDiff.Compare(Meta(Line("denikson-BepInExPack_Valheim", true, 5, 4, 2202)), Server(), null);

            var row = Row(diff, "denikson-BepInExPack_Valheim");
            Assert.Equal(GaleDiffKind.Framework, row.Kind);
            Assert.Equal("5.4.2202", row.ProfileVersion);
            Assert.Null(row.ServerVersion);
            Assert.Equal(0, diff.AddCount);
            Assert.Equal(0, diff.UpdateCount);
            Assert.False(diff.HasWork);
            Assert.False(GaleProfileDiff.IsDue(diff, null));
            Assert.False(GaleProfileDiff.IsDue(diff, "an older stamp"));
        }

        [Fact]
        public void HookGenPatcher_the_scan_cannot_see_is_the_framework_but_one_in_plugins_compares_as_a_mod()
        {
            var meta = Meta(Line("ValheimModding-HookGenPatcher", true, 0, 0, 4));

            // Kept among the patchers, which the scan skips: not an add.
            var unseen = GaleProfileDiff.Compare(meta, Server(), null);
            Assert.Equal(GaleDiffKind.Framework, Row(unseen, "ValheimModding-HookGenPatcher").Kind);
            Assert.False(unseen.HasWork);

            // Installed into plugins as an ordinary mod: it follows the profile like one.
            var inPlugins = GaleProfileDiff.Compare(meta, Server(("ValheimModding-HookGenPatcher", "0.0.3")), null);
            Assert.Equal(GaleDiffKind.Update, Row(inPlugins, "ValheimModding-HookGenPatcher").Kind);
            Assert.True(inPlugins.HasWork);
        }

        [Fact]
        public void The_pack_beside_a_real_add_leaves_only_the_add_as_work()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("denikson-BepInExPack_Valheim", true, 5, 4, 2202), Line("A-Add", true, 1, 0, 0)), Server(), null);

            Assert.Equal(1, diff.AddCount);
            Assert.Equal(GaleDiffKind.Add, Row(diff, "A-Add").Kind);
            Assert.Equal(GaleDiffKind.Framework, Row(diff, "denikson-BepInExPack_Valheim").Kind);
        }

        [Fact]
        public void A_different_version_on_the_server_is_an_update_either_way()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("ValheimModding-Jotunn", true, 2, 20, 1), Line("Azumatt-AzuCraftyBoxes", true, 1, 2, 0)),
                Server(("ValheimModding-Jotunn", "2.19.0"), ("Azumatt-AzuCraftyBoxes", "1.3.0")),
                null);

            var up = Row(diff, "ValheimModding-Jotunn");
            Assert.Equal(GaleDiffKind.Update, up.Kind);
            Assert.Equal("2.19.0", up.ServerVersion);
            Assert.False(up.IsDowngrade);

            // The server follows the profile, so an older pin is still an update, marked as one going down.
            var down = Row(diff, "Azumatt-AzuCraftyBoxes");
            Assert.Equal(GaleDiffKind.Update, down.Kind);
            Assert.True(down.IsDowngrade);

            Assert.Equal(2, diff.UpdateCount);
        }

        [Fact]
        public void The_same_version_is_same_and_is_nothing_to_do()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("ValheimModding-Jotunn", true, 2, 20, 1)),
                Server(("ValheimModding-Jotunn", "2.20.1")),
                null);

            Assert.Equal(GaleDiffKind.Same, Row(diff, "ValheimModding-Jotunn").Kind);
            Assert.False(diff.HasWork);
        }

        [Theory]
        [InlineData("2.20")]          // missing parts read as zero
        [InlineData("v2.20.0")]       // a leading v is tolerated
        [InlineData(" 2.20.0 ")]
        public void Versions_compare_as_versions_not_as_text(string serverVersion)
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("ValheimModding-Jotunn", true, 2, 20, 0)),
                Server(("ValheimModding-Jotunn", serverVersion)),
                null);

            Assert.Equal(GaleDiffKind.Same, Row(diff, "ValheimModding-Jotunn").Kind);
        }

        [Fact]
        public void Ten_is_higher_than_nine()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("A-Mod", true, 1, 10, 0)),
                Server(("A-Mod", "1.9.0")),
                null);

            var row = Row(diff, "A-Mod");
            Assert.Equal(GaleDiffKind.Update, row.Kind);
            Assert.False(row.IsDowngrade);
        }

        [Theory]
        [InlineData("unknown")]       // what ModScanner reports for a folder with no manifest.json
        [InlineData("")]
        [InlineData(null)]
        [InlineData("not a version")]
        public void A_server_version_nobody_can_read_is_an_update_never_same(string serverVersion)
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("A-Mod", true, 1, 0, 0)),
                new[] { ("A-Mod", serverVersion) },
                null);

            var row = Row(diff, "A-Mod");
            Assert.Equal(GaleDiffKind.Update, row.Kind);
            Assert.False(row.IsDowngrade);
            Assert.NotNull(row.ServerVersion);
        }

        [Fact]
        public void Names_match_without_regard_to_case()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("ValheimModding-Jotunn", true, 2, 20, 1)),
                Server(("valheimmodding-jotunn", "2.20.1")),
                null);

            Assert.Equal(GaleDiffKind.Same, Row(diff, "ValheimModding-Jotunn").Kind);
            Assert.DoesNotContain(diff.Rows, r => r.Kind == GaleDiffKind.NotInProfile);
        }

        [Fact]
        public void A_name_the_host_unticked_is_unticked_whether_add_or_update()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("A-New", true, 1, 0, 0), Line("B-Old", true, 2, 0, 0), Line("C-Fine", true, 1, 0, 0)),
                Server(("B-Old", "1.0.0"), ("C-Fine", "1.0.0")),
                new[] { " a-new ", "B-Old", "C-Fine", "" });

            var add = Row(diff, "A-New");
            Assert.Equal(GaleDiffKind.Unticked, add.Kind);
            Assert.Equal(GaleDiffKind.Add, add.UntickedFrom);

            var update = Row(diff, "B-Old");
            Assert.Equal(GaleDiffKind.Unticked, update.Kind);
            Assert.Equal(GaleDiffKind.Update, update.UntickedFrom);
            Assert.Equal("2.0.0", update.ProfileVersion);
            Assert.Equal("1.0.0", update.ServerVersion);

            // Unticked only means something when there is something to do.
            var same = Row(diff, "C-Fine");
            Assert.Equal(GaleDiffKind.Same, same.Kind);
            Assert.Null(same.UntickedFrom);

            Assert.False(diff.HasWork);
            Assert.Equal(0, diff.AddCount);
            Assert.Equal(0, diff.UpdateCount);
        }

        [Fact]
        public void A_mod_the_profile_has_switched_off_is_disabled_and_skipped_installed_or_not()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("A-Off", false, 1, 0, 0), Line("B-OffButHere", false, 3, 0, 0)),
                Server(("B-OffButHere", "2.0.0")),
                null);

            var notHere = Row(diff, "A-Off");
            Assert.Equal(GaleDiffKind.Disabled, notHere.Kind);
            Assert.Null(notHere.ServerVersion);

            // Switched off in the profile is not a reason to remove it, and not "not in profile" either.
            var here = Row(diff, "B-OffButHere");
            Assert.Equal(GaleDiffKind.Disabled, here.Kind);
            Assert.Equal("2.0.0", here.ServerVersion);
            Assert.Equal("3.0.0", here.ProfileVersion);

            Assert.False(diff.HasWork);
        }

        [Fact]
        public void A_mod_only_the_server_has_is_listed_and_never_removed()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(Line("A-Mod", true, 1, 0, 0)),
                Server(("A-Mod", "1.0.0"), ("Local-Tweaks", "0.1.0"), ("LooseDll", "unknown")),
                null);

            var only = Row(diff, "Local-Tweaks");
            Assert.Equal(GaleDiffKind.NotInProfile, only.Kind);
            Assert.Equal("0.1.0", only.ServerVersion);
            Assert.Null(only.ProfileVersion);

            // A folder with no author segment is named by its folder, not "-LooseDll".
            Assert.Equal(GaleDiffKind.NotInProfile, Row(diff, "LooseDll").Kind);
            Assert.False(diff.HasWork);
        }

        [Fact]
        public void There_is_no_row_kind_that_removes_anything()
        {
            Assert.Equal(
                new[] { "Add", "Update", "Same", "Unticked", "Disabled", "NotInProfile", "Framework" },
                System.Enum.GetNames(typeof(GaleDiffKind)));
        }

        [Fact]
        public void An_empty_manifest_lists_the_server_and_has_nothing_to_do()
        {
            var diff = GaleProfileDiff.Compare(Meta(), Server(("A-Mod", "1.0.0")), null);

            Assert.Equal(GaleDiffKind.NotInProfile, Assert.Single(diff.Rows).Kind);
            Assert.Equal(0, diff.Skipped);
            Assert.False(diff.HasWork);
            Assert.False(GaleProfileDiff.IsDue(diff, null));
        }

        [Fact]
        public void An_empty_manifest_against_an_empty_server_is_empty()
        {
            var diff = GaleProfileDiff.Compare(Meta(), Server(), null);
            Assert.Empty(diff.Rows);
            Assert.False(diff.HasWork);
        }

        [Fact]
        public void A_null_manifest_and_a_null_server_do_not_throw()
        {
            var diff = GaleProfileDiff.Compare(null, (IEnumerable<InstalledMod>)null, null);
            Assert.Empty(diff.Rows);
            Assert.Null(diff.UpdatedAt);
            Assert.False(GaleProfileDiff.IsDue(diff, "x"));
            Assert.False(GaleProfileDiff.IsDue(null, "x"));
        }

        [Fact]
        public void Malformed_lines_are_skipped_and_counted_and_the_good_ones_still_count()
        {
            var meta = Meta(
                Line("Good-Mod", true, 1, 0, 0),
                "\"just a string\"",
                "42",
                "null",
                "{\"enabled\":true,\"version\":{\"major\":1,\"minor\":0,\"patch\":0}}",                       // no name
                "{\"name\":7,\"enabled\":true,\"version\":{\"major\":1,\"minor\":0,\"patch\":0}}",              // name not a string
                "{\"name\":\"NoDash\",\"enabled\":true,\"version\":{\"major\":1,\"minor\":0,\"patch\":0}}",     // not Namespace-Name
                "{\"name\":\"Too-Many-Dashes\",\"enabled\":true,\"version\":{\"major\":1,\"minor\":0,\"patch\":0}}",
                "{\"name\":\"No-Version\",\"enabled\":true}",
                "{\"name\":\"String-Version\",\"enabled\":true,\"version\":\"1.0.0\"}",
                "{\"name\":\"Half-Version\",\"enabled\":true,\"version\":{\"major\":1,\"minor\":0}}",
                "{\"name\":\"Negative-Version\",\"enabled\":true,\"version\":{\"major\":-1,\"minor\":0,\"patch\":0}}",
                "{\"name\":\"Float-Version\",\"enabled\":true,\"version\":{\"major\":1.5,\"minor\":0,\"patch\":0}}",
                "{\"name\":\"Huge-Version\",\"enabled\":true,\"version\":{\"major\":99999999999999999999999,\"minor\":0,\"patch\":0}}",
                "{\"name\":\"No-Tick\",\"version\":{\"major\":1,\"minor\":0,\"patch\":0}}",
                "{\"name\":\"String-Tick\",\"enabled\":\"yes\",\"version\":{\"major\":1,\"minor\":0,\"patch\":0}}",
                Line("Good-Mod", true, 9, 9, 9));                                                                // a second line for a name already read

            var diff = GaleProfileDiff.Compare(meta, Server(("No-Version", "1.0.0")), null);

            var good = Row(diff, "Good-Mod");
            Assert.Equal(GaleDiffKind.Add, good.Kind);
            Assert.Equal("1.0.0", good.ProfileVersion);   // the first line stands

            Assert.Equal(16, diff.Skipped);
            Assert.Single(diff.Rows);
            // A real name with an unreadable line is still in the profile, so the server copy is
            // not listed as the server's own: it is simply not offered.
            Assert.DoesNotContain(diff.Rows, r => r.Name == "No-Version");
        }

        [Fact]
        public void Kinds_are_counted_by_kind()
        {
            var diff = GaleProfileDiff.Compare(
                Meta(
                    Line("A-Add", true, 1, 0, 0),
                    Line("B-Add", true, 1, 0, 0),
                    Line("C-Update", true, 2, 0, 0),
                    Line("D-Same", true, 1, 0, 0),
                    Line("E-Off", false, 1, 0, 0),
                    Line("F-No", true, 1, 0, 0)),
                Server(("C-Update", "1.0.0"), ("D-Same", "1.0.0"), ("G-Mine", "1.0.0")),
                new[] { "F-No" });

            Assert.Equal(2, diff.AddCount);
            Assert.Equal(1, diff.UpdateCount);
            Assert.Single(diff.Of(GaleDiffKind.Same));
            Assert.Single(diff.Of(GaleDiffKind.Disabled));
            Assert.Single(diff.Of(GaleDiffKind.Unticked));
            Assert.Single(diff.Of(GaleDiffKind.NotInProfile));
            Assert.Equal(Stamp, diff.UpdatedAt);
        }

        // ------------------------------------------------------------------ when the row is due

        [Fact]
        public void Due_when_there_is_work_and_the_profile_moved()
        {
            var diff = GaleProfileDiff.Compare(Meta(Line("A-Add", true, 1, 0, 0)), Server(), null);

            Assert.True(GaleProfileDiff.IsDue(diff, null));                       // never approved or dismissed
            Assert.True(GaleProfileDiff.IsDue(diff, "2026-10-01T00:00:00Z"));      // approved an older one
        }

        [Fact]
        public void Not_due_when_the_profile_has_not_moved_since_the_last_approval_or_dismissal()
        {
            var diff = GaleProfileDiff.Compare(Meta(Line("A-Add", true, 1, 0, 0)), Server(), null);
            Assert.False(GaleProfileDiff.IsDue(diff, Stamp));
        }

        [Fact]
        public void Not_due_when_the_profile_moved_but_there_is_nothing_to_do()
        {
            var same = GaleProfileDiff.Compare(Meta(Line("A-Mod", true, 1, 0, 0)), Server(("A-Mod", "1.0.0")), null);
            Assert.False(GaleProfileDiff.IsDue(same, "an older stamp"));

            var onlyUnticked = GaleProfileDiff.Compare(Meta(Line("A-Mod", true, 1, 0, 0)), Server(), new[] { "A-Mod" });
            Assert.False(GaleProfileDiff.IsDue(onlyUnticked, "an older stamp"));

            var onlyDisabled = GaleProfileDiff.Compare(Meta(Line("A-Mod", false, 1, 0, 0)), Server(), null);
            Assert.False(GaleProfileDiff.IsDue(onlyDisabled, "an older stamp"));
        }

        [Fact]
        public void The_stamp_compares_as_the_services_own_text()
        {
            // The same instant written another way is a different stamp: nothing here parses
            // dates, so nothing can disagree about time zones or precision.
            var diff = GaleProfileDiff.Compare(MetaAt("2026-10-06T12:34:56.789+00:00", Line("A-Add", true, 1, 0, 0)), Server(), null);
            Assert.Equal("2026-10-06T12:34:56.789+00:00", diff.UpdatedAt);
            Assert.False(GaleProfileDiff.IsDue(diff, "2026-10-06T12:34:56.789+00:00"));
            Assert.True(GaleProfileDiff.IsDue(diff, "2026-10-06T12:34:56.789Z"));
        }
    }
}
