using System;
using System.IO;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The rules that decide whether an install BakaLoader did not make may be written to at
    /// all: whether the host has answered the question, what their doorstop config points at,
    /// whether the core is the framework this pack carries, and which servers are up that no
    /// profile list knows about. Every one of them is pure, so every one of them gets a table.
    /// </summary>
    public class BepInExTakeoverRulesTests
    {
        // ------------------------------------------------------------------ consent

        /// <summary>
        /// Three states, not two. The preference defaults to ON, so a host upgrading from
        /// 1.1.x reads as "maintained" from the first second of the first launch and a profile
        /// that auto-starts never reaches the question at all. Reading the switch alone says
        /// yes on behalf of somebody who has not spoken.
        /// </summary>
        [Theory]
        // asked and said yes: the one state anything unattended happens in
        [InlineData(true, true, true, false, false)]
        // asked and said no
        [InlineData(false, true, false, false, true)]
        // never asked, switch on its default: NOT a yes
        [InlineData(true, false, false, true, false)]
        // never asked, switch turned off by something else: still not an answer
        [InlineData(false, false, false, true, false)]
        public void Consent_is_the_switch_and_the_answer_together(
            bool maintained, bool asked, bool effective, bool unanswered, bool declined)
        {
            Assert.Equal(effective, BepInExConsent.Effective(maintained, asked));
            Assert.Equal(unanswered, BepInExConsent.Unanswered(asked));
            Assert.Equal(declined, BepInExConsent.Declined(maintained, asked));
        }

        // ------------------------------------------------------------------ the doorstop config

        private const string Modern =
            "# Doorstop\n[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n";

        private const string Legacy =
            "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n";

        private const string Manager =
            "[General]\nenabled = true\n"
            + "target_assembly = C:\\Users\\Someone\\AppData\\Roaming\\com.kesomannen.gale\\valheim"
            + "\\profiles\\Default\\BepInEx\\core\\BepInEx.Preloader.dll\n";

        [Fact]
        public void Both_doorstop_generations_are_read()
        {
            Assert.Equal(@"BepInEx\core\BepInEx.Preloader.dll", BepInExDoorstop.TargetInText(Modern));
            Assert.Equal(@"BepInEx\core\BepInEx.Preloader.dll", BepInExDoorstop.TargetInText(Legacy));

            Assert.Equal(BepInExDoorstop.ModernSection, BepInExDoorstop.SectionInText(Modern));
            Assert.Equal(BepInExDoorstop.LegacySection, BepInExDoorstop.SectionInText(Legacy));

            Assert.Null(BepInExDoorstop.TargetInText("[General]\nenabled = true\n"));
            Assert.Null(BepInExDoorstop.TargetInText(null));
            Assert.Null(BepInExDoorstop.SectionInText("no sections here"));
        }

        /// <summary>
        /// The one that matters: a host coming from r2modman, Gale or Thunderstore Mod Manager
        /// has this pointed at that tool's own profile folder, and writing the pack's copy over
        /// it swaps their whole mod set for an empty one with nothing on screen saying so.
        /// </summary>
        [Fact]
        public void A_target_pointing_at_another_tools_profile_is_driven_elsewhere()
        {
            var install = Path.Combine(@"D:\", "common", "Valheim dedicated server");

            Assert.True(BepInExDoorstop.DrivenElsewhere(install, BepInExDoorstop.TargetInText(Manager)));

            // The ordinary install, spelled relative, absolute, and with the slashes the other
            // way round: all three are this install's own BepInEx and none of them is foreign.
            Assert.False(BepInExDoorstop.DrivenElsewhere(install, @"BepInEx\core\BepInEx.Preloader.dll"));
            Assert.False(BepInExDoorstop.DrivenElsewhere(install, "BepInEx/core/BepInEx.Preloader.dll"));
            Assert.False(BepInExDoorstop.DrivenElsewhere(install,
                Path.Combine(install, @"BepInEx\core\BepInEx.Preloader.dll")));
            Assert.False(BepInExDoorstop.DrivenElsewhere(install, @".\BepInEx\core\BepInEx.Preloader.dll"));

            // Silence is not evidence: no file, no setting, no install root.
            Assert.False(BepInExDoorstop.DrivenElsewhere(install, null));
            Assert.False(BepInExDoorstop.DrivenElsewhere(install, "   "));
            Assert.False(BepInExDoorstop.DrivenElsewhere(null, @"BepInEx\core\BepInEx.Preloader.dll"));
        }

        /// <summary>
        /// The host's file is kept unless it cannot drive the loader going in. Two things say
        /// so and nothing else does.
        /// </summary>
        [Theory]
        // same generation, same header: kept
        [InlineData("4.4.0", "4.3.0", "General", "General", false)]
        // the pack ships a different Doorstop major
        [InlineData("4.4.0", "3.7.0", "General", "General", true)]
        [InlineData("3.7.0", "4.4.0", "UnityDoorstop", "UnityDoorstop", true)]
        // the header differs, which is the same break by another name
        [InlineData("4.4.0", "4.4.0", "General", "UnityDoorstop", true)]
        // nothing to compare on one side or the other is never a difference
        [InlineData(null, "4.4.0", "General", "General", false)]
        [InlineData("4.4.0", null, "General", null, false)]
        [InlineData("not a version", "4.4.0", "General", "General", false)]
        public void The_hosts_doorstop_config_only_gives_way_to_a_new_generation(
            string packVersion, string diskVersion, string packSection, string diskSection, bool changed)
            => Assert.Equal(changed,
                BepInExDoorstop.GenerationChanged(packVersion, diskVersion, packSection, diskSection));

        [Theory]
        [InlineData("4.4.0", "4")]
        [InlineData("v3.0.0", "3")]
        [InlineData("  4  ", "4")]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("beta", null)]
        public void The_doorstop_major_is_the_leading_number(string text, string major)
            => Assert.Equal(major, BepInExDoorstop.MajorOf(text));

        // ------------------------------------------------------------------ a foreign core

        [Theory]
        [InlineData("5.4.23.5", false)]
        [InlineData("5.0.0.0", false)]
        [InlineData("6.0.0.0", true)]
        [InlineData("4.9.9.9", true)]
        // nothing readable is not evidence of anything
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("unknown", false)]
        public void A_core_that_is_not_a_five_is_foreign(string fileVersion, bool foreign)
            => Assert.Equal(foreign, BepInExService.IsForeignCoreVersion(fileVersion));

        // ------------------------------------------------------------------ going backwards

        [Theory]
        [InlineData("5.4.23.5", "5.4.23.3", true)]
        [InlineData("5.4.23.3", "5.4.23.5", false)]
        [InlineData("5.4.23.5", "5.4.23.5", false)]
        // nothing to compare means nothing to refuse
        [InlineData(null, "5.4.23.5", false)]
        [InlineData("5.4.23.5", null, false)]
        public void Going_backwards_is_recognised_by_the_two_file_versions(
            string installed, string pack, bool downgrade)
            => Assert.Equal(downgrade, BepInExService.IsDowngrade(installed, pack));

        // ------------------------------------------------------------------ a server nobody started here

        /// <summary>
        /// A host who launched the server from the Steam library, from a shortcut, or from a
        /// task on boot has a live process holding the very files a write replaces, and nothing
        /// in BakaLoader's own profile list says so.
        /// </summary>
        [Fact]
        public void A_running_process_on_this_install_blocks_the_write_and_is_named()
        {
            var basePath = Path.Combine(@"D:\", "common", "Valheim dedicated server", "valheim_server.exe");
            var isolated = Path.Combine(@"D:\", "common", ".bakaloader-instances", "Quiet", "valheim_server.exe");
            var elsewhere = Path.Combine(@"E:\", "another", "valheim_server.exe");

            Assert.Equal(
                new[] { "Valheim dedicated server", "Quiet" },
                BepInExService.ForeignServersBlockingWrite(basePath, new[] { basePath, isolated, elsewhere }));

            Assert.Empty(BepInExService.ForeignServersBlockingWrite(basePath, new[] { elsewhere }));
            Assert.Empty(BepInExService.ForeignServersBlockingWrite(basePath, Array.Empty<string>()));
            Assert.Empty(BepInExService.ForeignServersBlockingWrite(basePath, null));
        }

        // ------------------------------------------------------------------ the unattended window

        /// <summary>
        /// The rows the window grew. Every one of them is an install BakaLoader can see it did
        /// not make, and on every one of them the answer is to leave it alone and say so rather
        /// than to write while nobody is watching.
        /// </summary>
        [Theory]
        // the host has answered yes and nothing is odd: the ordinary adoption
        [InlineData(true, false, false, false, false, false, BepInExUnattendedAction.Apply)]
        // NOT ASKED. The switch reads on by default, and this is the whole point of the
        // consent rule: an answer nobody gave is not a yes.
        [InlineData(false, false, false, false, false, false, BepInExUnattendedAction.Skip)]
        // another mod manager's doorstop target
        [InlineData(true, true, false, false, false, false, BepInExUnattendedAction.Skip)]
        // a core that is not a 5.x
        [InlineData(true, false, true, false, false, false, BepInExUnattendedAction.Skip)]
        // ownership was given up because somebody else wrote here
        [InlineData(true, false, false, true, false, false, BepInExUnattendedAction.Skip)]
        // a core BakaLoader does not recognise
        [InlineData(true, false, false, false, true, false, BepInExUnattendedAction.Skip)]
        public void The_window_leaves_an_install_it_did_not_make_alone(
            bool consent, bool drivenElsewhere, bool foreignCore, bool drifted, bool unrecognised,
            bool filesMissing, BepInExUnattendedAction expected)
            => Assert.Equal(expected, BepInExUnattended.Decide(
                consent, installed: true, installedVersion: "5.4.2333", latestVersion: "5.4.2350",
                otherServersRunning: false,
                drivenElsewhere: drivenElsewhere, foreignCore: foreignCore, drifted: drifted,
                unrecognised: unrecognised, filesMissing: filesMissing));

        /// <summary>
        /// A file the note lists that is gone is an install to repair, and the repair is the
        /// write. It happens even when the version has not moved, because the version is not
        /// what is wrong: an antivirus took winhttp.dll and the server has been running vanilla
        /// ever since.
        /// </summary>
        [Fact]
        public void Files_that_went_missing_are_put_back_even_when_the_version_has_not_moved()
        {
            Assert.Equal(BepInExUnattendedAction.Apply, BepInExUnattended.Decide(
                consentEffective: true, installed: true, installedVersion: "5.4.2350",
                latestVersion: "5.4.2350", otherServersRunning: false, filesMissing: true));

            // and it still waits for the other servers on this install
            Assert.Equal(BepInExUnattendedAction.Defer, BepInExUnattended.Decide(
                consentEffective: true, installed: true, installedVersion: "5.4.2350",
                latestVersion: "5.4.2350", otherServersRunning: true, filesMissing: true));

            // without the repair there is nothing to do, which is the row this is next to
            Assert.Equal(BepInExUnattendedAction.Skip, BepInExUnattended.Decide(
                consentEffective: true, installed: true, installedVersion: "5.4.2350",
                latestVersion: "5.4.2350", otherServersRunning: false));
        }

        /// <summary>
        /// The file that goes is usually winhttp.dll, and losing THAT one makes the whole
        /// install answer "not installed": it is half of what installed means. So the repair
        /// has to reach past the not-installed row as well as past the version one, or the one
        /// case the antivirus rule exists for is the one case it never fires on.
        /// </summary>
        [Fact]
        public void A_missing_loader_file_is_repaired_although_the_install_reads_as_absent()
        {
            Assert.Equal(BepInExUnattendedAction.Apply, BepInExUnattended.Decide(
                consentEffective: true, installed: false, installedVersion: "5.4.2350",
                latestVersion: "5.4.2350", otherServersRunning: false, filesMissing: true));

            // and it still waits for the other servers on this install
            Assert.Equal(BepInExUnattendedAction.Defer, BepInExUnattended.Decide(
                consentEffective: true, installed: false, installedVersion: "5.4.2350",
                latestVersion: "5.4.2350", otherServersRunning: true, filesMissing: true));

            // Nothing installed and nothing missing from a note is still the launch path's
            // business: there is no install to repair, only one to put in.
            Assert.Equal(BepInExUnattendedAction.Skip, BepInExUnattended.Decide(
                consentEffective: true, installed: false, installedVersion: null,
                latestVersion: "5.4.2350", otherServersRunning: false));

            // and none of the four "this is not yours to write to" answers is walked past by it
            Assert.Equal(BepInExUnattendedAction.Skip, BepInExUnattended.Decide(
                consentEffective: true, installed: false, installedVersion: "5.4.2350",
                latestVersion: "5.4.2350", otherServersRunning: false,
                drivenElsewhere: true, filesMissing: true));
            Assert.Equal(BepInExUnattendedAction.Skip, BepInExUnattended.Decide(
                consentEffective: false, installed: false, installedVersion: "5.4.2350",
                latestVersion: "5.4.2350", otherServersRunning: false, filesMissing: true));
        }

        // ------------------------------------------------------------------ what a note describes

        [Theory]
        [InlineData("BepInEx/core/BepInEx.dll", true)]
        [InlineData("BepInEx/core/net472/Mono.Cecil.dll", true)]
        [InlineData("winhttp.dll", true)]
        [InlineData(".doorstop_version", true)]
        // symbols and docs ride along beside an assembly and never load anything
        [InlineData("BepInEx/core/BepInEx.pdb", false)]
        [InlineData("BepInEx/core/BepInEx.xml", false)]
        // the host's own files, which they are free to edit without losing their install
        [InlineData("doorstop_config.ini", false)]
        [InlineData("BepInEx/config/BepInEx.cfg", false)]
        [InlineData("changelog.txt", false)]
        [InlineData("BepInEx/plugins/SomeMod/SomeMod.dll", false)]
        [InlineData(null, false)]
        public void Only_the_files_that_decide_what_loads_are_compared(string path, bool compared)
            => Assert.Equal(compared, BepInExIntegrity.IsLoaderIdentity(path));

        // ------------------------------------------------------------------ which pack goes in

        private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The four things that stop a restart window putting a published pack in, and the
        /// order they are asked in. A host pressing the button has read the version and chosen
        /// it, so every one of these answers null for a manual write.
        /// </summary>
        [Theory]
        // an ordinary pack, listed a week ago, with a size: in it goes
        [InlineData("5.4.2350", false, -168, 1024L, null)]
        // a pre-release is published to be tried, by somebody who chose to try it
        [InlineData("5.4.2400-rc.1", false, -168, 1024L, BepInExSkipReason.PreRelease)]
        [InlineData("5.4.2400-beta", false, -168, 1024L, BepInExSkipReason.PreRelease)]
        // a package the listing marks deprecated
        [InlineData("5.4.2350", true, -168, 1024L, BepInExSkipReason.Deprecated)]
        // listed an hour ago: somebody else finds a bad upload first
        [InlineData("5.4.2350", false, -1, 1024L, BepInExSkipReason.Soak)]
        // exactly on the line, and just past it
        [InlineData("5.4.2350", false, -71, 1024L, BepInExSkipReason.Soak)]
        [InlineData("5.4.2350", false, -72, 1024L, null)]
        // nothing to check the download against
        [InlineData("5.4.2350", false, -168, null, BepInExSkipReason.Unverified)]
        [InlineData("5.4.2350", false, -168, 0L, BepInExSkipReason.Unverified)]
        // and the order: a pre-release that is also deprecated, new and sizeless is still
        // named as the first thing that was wrong with it
        [InlineData("5.4.2400-rc.1", true, -1, null, BepInExSkipReason.PreRelease)]
        [InlineData("5.4.2350", true, -1, null, BepInExSkipReason.Deprecated)]
        [InlineData("5.4.2350", false, -1, null, BepInExSkipReason.Soak)]
        public void A_window_only_writes_a_pack_that_earned_its_way_in(
            string version, bool deprecated, int listedHoursAgo, long? size, string refusal)
        {
            var listed = Now.AddHours(listedHoursAgo);

            Assert.Equal(refusal, BepInExPackPolicy.RefusalFor(
                unattended: true, version, deprecated, listed, size, Now));

            // A press is a decision the host is allowed to make and the window is not.
            Assert.Null(BepInExPackPolicy.RefusalFor(
                unattended: false, version, deprecated, listed, size, Now));
        }

        /// <summary>
        /// PLANNER CORRECTION 4b. A version the site has taken down (<c>is_active</c> false) is
        /// treated exactly like a deprecated package and asked about FIRST, because it is the
        /// only one of these that says the pack should not be on the machine at all rather than
        /// not yet or not unwatched. The live listing really does carry the field.
        /// </summary>
        [Theory]
        // pulled on its own
        [InlineData("5.4.2350", false, true, -168, 1024L, BepInExSkipReason.Pulled)]
        // pulled beats every other answer, including the ones asked before it
        [InlineData("5.4.2400-rc.1", false, true, -168, 1024L, BepInExSkipReason.Pulled)]
        [InlineData("5.4.2350", true, true, -1, null, BepInExSkipReason.Pulled)]
        // and a listing that said nothing about it is not a pulled one
        [InlineData("5.4.2350", false, false, -168, 1024L, null)]
        public void A_version_the_site_took_down_is_never_written_unattended(
            string version, bool deprecated, bool pulled, int listedHoursAgo, long? size, string refusal)
        {
            var listed = Now.AddHours(listedHoursAgo);

            Assert.Equal(refusal, BepInExPackPolicy.RefusalFor(
                unattended: true, version, deprecated, listed, size, Now, pulled));

            Assert.Null(BepInExPackPolicy.RefusalFor(
                unattended: false, version, deprecated, listed, size, Now, pulled));
        }

        /// <summary>
        /// A listing that says nothing about <c>is_active</c> counts as one that is still
        /// served, the same way one that says nothing about deprecation counts as one that is
        /// not. Only an answer that actually said no stops a write.
        /// </summary>
        [Theory]
        [InlineData(null, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void A_listing_that_said_nothing_about_is_active_is_still_served(bool? raw, bool active)
            => Assert.Equal(active, new ThunderstorePackageVersion { IsActiveRaw = raw }.IsActive);

        /// <summary>
        /// A listing with no date is not held back. The soak is a rule about a pack that IS
        /// known to be new, and refusing every pack whose listing happened not to carry a date
        /// would stop the window working at all the day Thunderstore renames a field.
        /// </summary>
        [Fact]
        public void A_pack_with_no_publish_date_is_not_held_back_by_the_soak()
        {
            Assert.False(BepInExPackPolicy.StillSoaking(null, Now));
            Assert.Null(BepInExPackPolicy.EligibleAt(null));
            Assert.Null(BepInExPackPolicy.RefusalFor(
                unattended: true, "5.4.2350", deprecated: false, null, 1024L, Now));
        }

        /// <summary>
        /// When the wait ends, which is what the row shows. A date that came out of JSON
        /// without a zone on it is read as UTC rather than as this machine's own time: a host
        /// on the far side of the world is half a soak away from Greenwich, and comparing the
        /// two would hold their window back a day or let it write a day early.
        /// </summary>
        [Fact]
        public void The_row_can_say_when_the_wait_ends()
        {
            var listed = new DateTime(2026, 9, 20, 6, 0, 0, DateTimeKind.Utc);
            Assert.Equal(listed.AddHours(72), BepInExPackPolicy.EligibleAt(listed));

            var unzoned = new DateTime(2026, 9, 20, 6, 0, 0, DateTimeKind.Unspecified);
            Assert.Equal(listed.AddHours(72), BepInExPackPolicy.EligibleAt(unzoned));

            var local = listed.ToLocalTime();
            Assert.Equal(listed.AddHours(72), BepInExPackPolicy.EligibleAt(local));
        }
    }
}
