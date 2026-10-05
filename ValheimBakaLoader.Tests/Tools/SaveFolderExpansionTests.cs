using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Issue 17: a save folder written as <c>%USERPROFILE%\...</c> is a FOLDER NAME, not a place.
    /// <para>
    /// The shipped default app wide save folder is the literal string
    /// <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim</c>. Through 1.2.8 some readers filled
    /// the variable in and some did not, and the ones that did not handed the raw text to
    /// <c>Path.Combine</c> and <c>Directory.CreateDirectory</c>, which anchored a RELATIVE path
    /// at the working directory. A tester duplicating a realm got a real folder called
    /// <c>%USERPROFILE%</c> inside his BakaLoader install with the copied world in it, while
    /// <c>worlds.list</c> looked in LocalLow and threw, an admin add wrote its temporary file
    /// into the install folder and failed, and Open said "Folder not found: %USERPROFILE%\...".
    /// </para>
    /// <para>
    /// The fix is one boundary, <see cref="PathCheck.Resolve"/>, applied the moment a path is
    /// read from preferences. These tests drive the real readers over a real temporary folder
    /// through a FAKE environment variable, so the variable is one this box only has while the
    /// test runs and the folder it points at is one the test made. Nothing here touches a
    /// Valheim install, the registry, or the user's own save folders.
    /// </para>
    /// </summary>
    public class SaveFolderExpansionTests : BaseTest, IDisposable
    {
        /// <summary>
        /// The fake variable. Named for this file so no other test can be reading it, and set
        /// and cleared around every case rather than left on the process.
        /// </summary>
        private const string VariableName = "VBL_SAVE_EXPANSION_TEST_HOME";

        /// <summary>The variable as it is written in a stored path.</summary>
        private const string Variable = "%" + VariableName + "%";

        private readonly string Home =
            Path.Combine(Path.GetTempPath(), "vbl-save-expand-" + Guid.NewGuid().ToString("N"));

        public SaveFolderExpansionTests()
        {
            Directory.CreateDirectory(Home);
            Environment.SetEnvironmentVariable(VariableName, Home);
            ClearTheStrayFolderThisTestWouldLeave();
        }

        public void Dispose()
        {
            ClearTheStrayFolderThisTestWouldLeave();
            Environment.SetEnvironmentVariable(VariableName, null);
            try { Directory.Delete(Home, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// The folder a build WITHOUT this fix would make: one literally called
        /// <c>%VBL_SAVE_EXPANSION_TEST_HOME%</c> beside the test binary. Cleared at both ends,
        /// because the cases below assert it is NOT there and a leftover from an earlier run on
        /// a build that did not have the fix would otherwise fail a tree that does. Nothing
        /// else on this box can own a folder of that name.
        /// </summary>
        private static void ClearTheStrayFolderThisTestWouldLeave()
        {
            try
            {
                var stray = Path.Combine(Environment.CurrentDirectory, Variable);
                if (Directory.Exists(stray)) Directory.Delete(stray, recursive: true);
            }
            catch { /* best effort: the assertion says so if it is still there */ }
        }

        /// <summary>The stored shape the tester had: a variable, then the rest of the path.</summary>
        private static string Stored(params string[] parts)
            => Path.Combine(new[] { Variable }.Concat(parts).ToArray());

        /// <summary>Where the OLD build would have put it: the raw text, against this process's
        /// working directory. Asked about, never created.</summary>
        private static string WhereTheOldBuildWouldHavePutIt(string stored)
            => Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, stored));

        // ------------------------------------------------------- 1. the boundary itself

        /// <summary>
        /// Resolve fills the variable in AND makes the path full. Expand only did the first of
        /// those two, which is why a relative remnant could still reach a disk call.
        /// </summary>
        [Fact]
        public void Resolve_fills_the_variable_in_and_anchors_what_is_left()
        {
            var resolved = PathCheck.Resolve(Stored("saves"));

            Assert.Equal(Path.Combine(Home, "saves"), resolved);
            Assert.DoesNotContain("%", resolved);
            Assert.True(Path.IsPathRooted(resolved), resolved + " is still a relative path");

            // The half that was missing: Expand alone leaves a path that is not rooted, and
            // every disk call downstream of it then anchors it wherever it happens to run.
            Assert.False(Path.IsPathRooted(PathCheck.Expand(@"saves\mine")));
            Assert.True(Path.IsPathRooted(PathCheck.Resolve(@"saves\mine")));
        }

        /// <summary>Nothing typed stays nothing, both ways, so a fallback chain still falls back.</summary>
        [Fact]
        public void Nothing_typed_resolves_to_nothing_rather_than_to_the_working_directory()
        {
            foreach (var empty in new[] { null, "", "   " })
            {
                Assert.Equal("", PathCheck.Resolve(empty));
                Assert.Null(PathCheck.ResolveOrNull(empty));
            }
        }

        // -------------------------------------- 2. an isolated realm's folder, for real

        /// <summary>
        /// The line issue 17 was about. A realm given its own save folder, with the app wide
        /// folder stored as a variable: the folder is created under the REAL folder the variable
        /// names, the path handed back is expanded (so that is what gets stored), and nothing is
        /// created in the folder BakaLoader is running from.
        /// </summary>
        [Fact]
        public void An_isolated_realm_is_created_under_the_real_folder_and_stores_the_expanded_path()
        {
            var stored = Stored("AppData", "LocalLow", "IronGate", "Valheim");
            var strayRoot = WhereTheOldBuildWouldHavePutIt(stored);

            var made = IsolatedSaveFolder.Create(stored, "Sulniel_Dev");

            Assert.DoesNotContain("%", made);
            Assert.True(Path.IsPathRooted(made));
            Assert.Equal(
                Path.Combine(Home, "AppData", "LocalLow", "IronGate", "Valheim", "servers", "Sulniel_Dev"),
                made);
            Assert.True(Directory.Exists(made), made + " was not created");

            // The game's own layout, so a realm that has never started still reads as a save
            // folder rather than as an empty directory.
            Assert.True(Directory.Exists(Path.Combine(made, "worlds_local")));

            // And the whole point: NOT a folder called %VBL_SAVE_EXPANSION_TEST_HOME% beside
            // the program. This is the directory the tester found on his disk.
            Assert.False(Directory.Exists(strayRoot),
                "a folder named for the variable was created at " + strayRoot);
        }

        /// <summary>
        /// The numbering only skips a folder that already holds something, and the base is
        /// resolved before any of it. Driven as a table: the one disk question is handed in.
        /// </summary>
        [Fact]
        public void A_second_realm_of_the_same_name_is_numbered_from_the_resolved_base()
        {
            var stored = Stored("saves");
            var first = Path.Combine(Home, "saves", "servers", "Proving");

            Assert.Equal(first, IsolatedSaveFolder.Plan(stored, "Proving", _ => false));
            Assert.Equal(Path.Combine(Home, "saves", "servers", "Proving-2"),
                IsolatedSaveFolder.Plan(stored, "Proving", path =>
                    string.Equals(path, first, StringComparison.OrdinalIgnoreCase)));

            // A predicate that answers yes to everything is a disk that answers yes to
            // everything, and this used to be a bare while loop over it. It is a REFUSAL now
            // rather than a fall-through; see the case that drives that below.
            Assert.Throws<HostFacingException>(
                () => IsolatedSaveFolder.Plan(stored, "Proving", _ => true));
        }

        /// <summary>A blank base falls back to Valheim's own folder, with no variable in it.</summary>
        [Fact]
        public void A_blank_base_falls_back_to_the_real_valheim_folder()
        {
            var planned = IsolatedSaveFolder.Plan("   ", "Default", _ => false);

            Assert.DoesNotContain("%", planned);
            Assert.StartsWith(IsolatedSaveFolder.ValheimDefault(), planned, StringComparison.OrdinalIgnoreCase);
        }

        // --------------------------- 3. the readers a raw stored path used to reach raw

        /// <summary>
        /// A profile FILE holding the raw string loads with the raw string kept, because a host's
        /// %USERPROFILE% is theirs and keeps their preferences portable, and every reader of it
        /// sees the folder. Both halves asserted together: this is the contract the fix rests on.
        /// </summary>
        [Fact]
        public void A_profile_file_holding_the_raw_string_keeps_it_and_is_read_expanded()
        {
            var stored = Stored("AppData", "LocalLow", "IronGate", "Valheim");
            var real = Path.Combine(Home, "AppData", "LocalLow", "IronGate", "Valheim");

            var profile = ServerPreferences.FromFile(new ServerPreferencesFile
            {
                ProfileName = "Sulniel_Dev",
                WorldName = "Sulneil",
                SaveDataFolderPath = stored,
            });

            // Kept as the host wrote it.
            Assert.Equal(stored, profile.SaveDataFolderPath);

            // And read as a place. This is the line the Directories card shows as "in force".
            var dto = BlendWindow.BuildProfilePrefsDto(profile, UserPreferences.GetDefault());
            Assert.Equal(real, dto.Value<string>("EffectiveSaveDataFolderPath"));
            Assert.Equal("profile", dto.Value<string>("SaveDataFolderPathSource"));
            Assert.Equal(stored, dto.Value<string>("SaveDataFolderPath"));
        }

        /// <summary>
        /// Everything a launch hands the game comes through one place, and it is resolved there:
        /// the save folder, the executable, and so the <c>-savedir</c> on the command line and
        /// the access lists written beside the worlds.
        /// </summary>
        [Fact]
        public void A_launch_resolves_both_stored_paths_before_anything_downstream_sees_them()
        {
            var saves = Path.Combine(Home, "saves");
            Directory.CreateDirectory(saves);

            var options = ValheimServerOptions.FromPreferences(
                new ServerPreferences { ProfileName = "Sulniel_Dev", WorldName = "Sulneil" },
                new UserPreferences
                {
                    SaveDataFolderPath = Stored("saves"),
                    ServerExePath = Stored("server", PathCheck.ServerExeName),
                },
                null);

            Assert.Equal(saves, options.SaveDataFolderPath);
            Assert.Equal(Path.Combine(Home, "server", PathCheck.ServerExeName), options.ServerExePath);
            Assert.DoesNotContain("%", options.SaveDataFolderPath);
            Assert.DoesNotContain("%", options.ServerExePath);

            // The folder validation that worlds.list and the launch both go through answers the
            // same place rather than throwing about a path nobody can find.
            Assert.Equal(saves, options.GetValidatedSaveDataFolder().FullName);
        }

        /// <summary>
        /// WorldStore, handed a stored path through the boundary, finds the world. Handed the raw
        /// string it finds nothing, which is exactly what the tester's duplicate did: the world
        /// was on disk and the list was empty.
        /// </summary>
        [Fact]
        public void WorldStore_finds_the_world_through_the_boundary_and_not_through_the_raw_string()
        {
            var stored = Stored("saves");
            var worlds = Path.Combine(Home, "saves", "worlds_local");
            Directory.CreateDirectory(worlds);
            File.WriteAllText(Path.Combine(worlds, "Sulneil.fwl"), "");

            Assert.Equal(new[] { "Sulneil" },
                WorldStore.Enumerate(PathCheck.Resolve(stored)).Select(w => w.Name).ToArray());

            // The pre-fix call, for the falsification: the same world, the same disk, no answer.
            Assert.Empty(WorldStore.Enumerate(stored));
        }

        /// <summary>
        /// The access lists. Through the boundary the line lands beside the worlds; handed the
        /// raw string the write throws, which is the DirectoryNotFoundException the tester read
        /// off his screen when he added an admin.
        /// </summary>
        [Fact]
        public void An_admin_line_lands_beside_the_worlds_through_the_boundary_and_throws_without_it()
        {
            var stored = Stored("saves");
            var saves = Path.Combine(Home, "saves");
            Directory.CreateDirectory(saves);

            var service = GetService<PlayerListService>();
            const string steamId = "76561198012345678";

            Assert.True(service.AddToList(PathCheck.Resolve(stored), PlayerListType.Admin, steamId));

            var listed = File.ReadAllLines(Path.Combine(saves, "adminlist.txt"));
            Assert.Contains(steamId, listed);
            Assert.Contains("V_" + steamId, listed);

            // And the pre-fix call. The folder it resolves against does not exist, so the write
            // raises rather than silently landing somewhere else; nothing is created there.
            var stray = WhereTheOldBuildWouldHavePutIt(stored);
            Assert.ThrowsAny<Exception>(
                () => service.AddToList(stored, PlayerListType.Admin, "76561198087654321"));
            Assert.False(Directory.Exists(stray), "a folder named for the variable appeared at " + stray);
        }

        /// <summary>
        /// Two profiles naming ONE folder, one of them with a variable in it, are sharing a save
        /// folder. SameFolder compared raw full paths, so it answered no: the Barrow drew the
        /// worlds as unclaimed, the world-clash check in front of a start let both servers load
        /// one world, and the delete guard would have taken the other realm's worlds with it.
        /// </summary>
        [Fact]
        public void One_folder_written_two_ways_is_one_folder_to_every_rule_that_compares_them()
        {
            var stored = Stored("saves");
            var real = Path.Combine(Home, "saves");

            var profiles = new[]
            {
                new ServerPreferences { ProfileName = "Default", WorldName = "Sulneil", SaveDataFolderPath = stored },
                new ServerPreferences { ProfileName = "Sulniel_Dev", WorldName = "Sulneil", SaveDataFolderPath = real },
            };

            Assert.Equal("Default",
                BlendWindow.ProfileSelectingWorld(profiles, "Sulneil", real, null)?.ProfileName);
            Assert.Equal("Sulniel_Dev",
                BlendWindow.ProfileSharingSaveFolder(profiles, "Default", real)?.ProfileName);
        }

        // ----------------------- 4. the repair offer, on the tester's exact folder shape

        /// <summary>
        /// The rule, as a table. Pure, so every case a host can land in is a row here rather
        /// than a folder somebody had to arrange on a build machine.
        /// </summary>
        [Theory]
        // the tester's shape: a variable in the stored path, worlds in the install folder,
        // nothing where the app reads now
        [InlineData(true, false, false, true, true, StraySaveFolder.ShapeStray)]
        // worlds in both: said out loud, nothing done
        [InlineData(true, true, true, true, true, StraySaveFolder.ShapeBoth)]
        // the destination folder is THERE and holds no worlds, which is what a save folder the
        // game has never written to looks like. No move, because a move into a folder that
        // exists is a merge, and the loudest row of the four, because a start here comes up on
        // a brand new world with the real one on the same disk.
        [InlineData(true, true, false, true, true, StraySaveFolder.ShapeDestinationEmpty)]
        // the host already moved it by hand, which is what the tester did: silence
        [InlineData(true, true, true, false, false, StraySaveFolder.ShapeNone)]
        // THE ROW A REVIEW ASKED FOR. The skeleton a move leaves: the folder named for the
        // variable is still there, and there is not a world in it. Silence, not "both".
        [InlineData(true, true, true, true, false, StraySaveFolder.ShapeNone)]
        // an empty leftover folder is not a save folder, so there is nothing to offer
        [InlineData(true, false, false, true, false, StraySaveFolder.ShapeNone)]
        // no variable in the stored path: the old build could not have anchored it anywhere else
        [InlineData(false, false, false, true, true, StraySaveFolder.ShapeNone)]
        [InlineData(false, true, true, true, true, StraySaveFolder.ShapeNone)]
        public void The_repair_offer_is_a_table(
            bool carries, bool resolvedThere, bool resolvedWorlds,
            bool strayThere, bool strayWorlds, string expected)
        {
            Assert.Equal(expected, StraySaveFolder.Judge(new StrayFolderFacts
            {
                StoredCarriesVariable = carries,
                ResolvedExists = resolvedThere,
                ResolvedHoldsWorlds = resolvedWorlds,
                StrayExists = strayThere,
                StrayHoldsWorlds = strayWorlds,
            }));
        }

        /// <summary>
        /// What counts as a save folder, which is the question the row is really asking. A
        /// directory is not one, however deep: that is the leftover a move or a by-hand repair
        /// makes, and reading it as a save folder is what raised a row nobody could act on.
        /// </summary>
        [Fact]
        public void A_save_folder_is_one_that_holds_worlds_rather_than_one_that_merely_exists()
        {
            string Make(params string[] parts)
            {
                var dir = Path.Combine(new[] { Home, "shapes" }.Concat(parts).ToArray());
                Directory.CreateDirectory(dir);
                return dir;
            }

            Assert.False(StraySaveFolder.HoldsWorlds(Path.Combine(Home, "not-there")));
            Assert.False(StraySaveFolder.HoldsWorlds(Make("bare")));
            Assert.False(StraySaveFolder.HoldsWorlds(Make("chain", "AppData", "LocalLow")));
            // an isolated realm nobody has started yet: worlds_local is there and empty
            Make("never-started", "worlds_local");
            Assert.False(StraySaveFolder.HoldsWorlds(Path.Combine(Home, "shapes", "never-started")));

            // a world the game wrote, one level down, in either spelling of the folder
            foreach (var sub in new[] { "worlds_local", "worlds" })
            {
                var save = Make("in-" + sub, sub);
                File.WriteAllText(Path.Combine(save, "Sulneil.fwl"), "");
                Assert.True(StraySaveFolder.HoldsWorlds(Path.Combine(Home, "shapes", "in-" + sub)));
            }

            // a 1.0 world, which is a DIRECTORY rather than a pair of files
            var chunked = Make("chunked", "worlds_local", "Sulneil");
            Assert.True(StraySaveFolder.HoldsWorlds(Path.Combine(Home, "shapes", "chunked")));
            Assert.True(Directory.Exists(chunked));

            // the worlds folder itself, typed into the box by a host
            var flat = Make("flat");
            File.WriteAllText(Path.Combine(flat, "Sulneil.db"), "");
            Assert.True(StraySaveFolder.HoldsWorlds(flat));

            // and the app wide shape: servers/<realm>, each of them a save folder of its own
            var realm = Make("app-wide", "servers", "Sulniel_Dev", "worlds_local");
            File.WriteAllText(Path.Combine(realm, "Sulneil.fwl"), "");
            Assert.True(StraySaveFolder.HoldsWorlds(Path.Combine(Home, "shapes", "app-wide")));

            // an empty servers folder is the skeleton again, and it is not a save folder
            Make("moved-away", "servers");
            Assert.False(StraySaveFolder.HoldsWorlds(Path.Combine(Home, "shapes", "moved-away")));
        }

        /// <summary>
        /// The prune never leaves the install folder it was given, whatever it is handed, and
        /// stops at the first directory that still holds something. Driven straight, because
        /// "how far up does this delete" is the one question worth being able to answer without
        /// a move in front of it.
        /// </summary>
        [Fact]
        public void The_prune_stops_at_the_install_folder_and_at_the_first_folder_that_is_not_empty()
        {
            var install = Path.Combine(Home, "install");
            var keep = Path.Combine(install, "%VAR%", "AppData", "keepme");
            var leaf = Path.Combine(install, "%VAR%", "AppData", "LocalLow", "IronGate", "Valheim", "servers", "gone");

            Directory.CreateDirectory(Path.GetDirectoryName(leaf));
            Directory.CreateDirectory(keep);
            File.WriteAllText(Path.Combine(keep, "mine.txt"), "");

            StraySaveFolder.PruneEmptyFoldersAbove(leaf, install);

            // everything empty above the leaf is gone
            Assert.False(Directory.Exists(Path.Combine(install, "%VAR%", "AppData", "LocalLow")));
            // and the walk stopped the moment a folder still held something
            Assert.True(Directory.Exists(Path.Combine(install, "%VAR%", "AppData")));
            Assert.True(File.Exists(Path.Combine(keep, "mine.txt")));
            Assert.True(Directory.Exists(install));

            // the boundary itself is never takeable, even when it is empty and even when the
            // leaf handed in is directly under it
            var bare = Path.Combine(Home, "bare-install");
            Directory.CreateDirectory(bare);
            StraySaveFolder.PruneEmptyFoldersAbove(Path.Combine(bare, "gone"), bare);
            Assert.True(Directory.Exists(bare), "the prune took the install folder it was given");

            // and nothing is touched when the leaf is not under the boundary at all
            var elsewhere = Path.Combine(Home, "elsewhere", "empty");
            Directory.CreateDirectory(elsewhere);
            StraySaveFolder.PruneEmptyFoldersAbove(Path.Combine(elsewhere, "gone"), install);
            Assert.True(Directory.Exists(elsewhere));
        }

        /// <summary>
        /// The fallback every realm without a folder of its own shares, as a rule of its own:
        /// this is the layer the sibling-realm row lives in, and until now nothing drove it.
        /// </summary>
        [Fact]
        public void A_realm_with_no_folder_of_its_own_asks_about_the_app_wide_string()
        {
            Assert.Equal(@"D:\Mine", StraySaveFolder.StoredFor(@"D:\Mine", Stored("saves")));
            Assert.Equal(Stored("saves"), StraySaveFolder.StoredFor(null, Stored("saves")));
            Assert.Equal(Stored("saves"), StraySaveFolder.StoredFor("   ", Stored("saves")));
            Assert.Equal("", StraySaveFolder.StoredFor(null, null));
        }

        /// <summary>
        /// The folders the question is asked of: blanks dropped, each made full, the same folder
        /// never asked twice. A shortcut and a service launch disagree about the working
        /// directory, which is why there are two of them rather than one guessed at.
        /// </summary>
        [Fact]
        public void The_install_folders_asked_about_are_full_paths_with_no_repeats()
        {
            var here = Path.GetFullPath(Environment.CurrentDirectory);

            Assert.Equal(new[] { here },
                StraySaveFolder.FoldersToAsk(new[] { here, here, ".", "", "   ", null }).ToArray());

            Assert.Equal(new[] { here, Path.GetFullPath(Home) },
                StraySaveFolder.FoldersToAsk(new[] { ".", Home }).ToArray());

            Assert.Empty(StraySaveFolder.FoldersToAsk(null));
            Assert.Empty(StraySaveFolder.FoldersToAsk(new string[] { null, "  " }));
        }

        /// <summary>
        /// And the whole answer, asked of more than one install folder: a build started from a
        /// shortcut and one started from a service have different working directories, so both
        /// are asked and the one that has something to say brings its own folder back with it.
        /// </summary>
        [Fact]
        public void The_answer_names_the_install_folder_that_had_something_to_say()
        {
            var install = Path.Combine(Home, "install");
            var elsewhere = Path.Combine(Home, "started-from-here");
            Directory.CreateDirectory(elsewhere);

            var stored = Stored("AppData", "LocalLow", "IronGate", "Valheim", "servers", "Sulniel_Dev");
            var stray = StraySaveFolder.StrayPathFor(stored, install);
            Directory.CreateDirectory(Path.Combine(stray, "worlds_local"));
            File.WriteAllText(Path.Combine(stray, "worlds_local", "Sulneil.fwl"), "");

            var answer = StraySaveFolder.For(stored, new[] { elsewhere, install });

            Assert.Equal(StraySaveFolder.ShapeStray, answer.Shape);
            Assert.Equal(install, answer.Install);
            Assert.Equal(stray, answer.Stray);
            Assert.Equal(PathCheck.Resolve(stored), answer.Resolved);
            Assert.Equal(stored, answer.Stored);

            // nothing on disk anywhere, and the answer still names the two paths it was asked
            // about rather than null: the page prints Resolved whatever the shape is.
            var quiet = StraySaveFolder.For(Stored("nowhere"), new[] { elsewhere, install });
            Assert.Equal(StraySaveFolder.ShapeNone, quiet.Shape);
            Assert.Null(quiet.Stray);
            Assert.Null(quiet.Install);
            Assert.Equal(PathCheck.Resolve(Stored("nowhere")), quiet.Resolved);
        }

        /// <summary>
        /// Whether a stored path carries a variable is decided by whether filling them in
        /// CHANGES it, so a percent sign in a folder name and a variable this machine has never
        /// heard of are both correctly not variables.
        /// </summary>
        [Fact]
        public void A_percent_sign_is_not_a_variable_unless_this_machine_knows_the_name()
        {
            Assert.True(StraySaveFolder.CarriesVariable(Stored("saves")));
            Assert.False(StraySaveFolder.CarriesVariable(@"D:\100% full\saves"));
            Assert.False(StraySaveFolder.CarriesVariable(@"%NOTHING_ON_THIS_BOX_IS_CALLED_THIS%\saves"));
            Assert.False(StraySaveFolder.CarriesVariable(null));
            Assert.False(StraySaveFolder.CarriesVariable("   "));
        }

        /// <summary>An absolute stored path never had a second place, so there is none to name.</summary>
        [Fact]
        public void An_absolute_stored_path_has_no_stray_twin()
        {
            Assert.Null(StraySaveFolder.StrayPathFor(@"D:\Saves", @"H:\BakaLoader"));
            Assert.Null(StraySaveFolder.StrayPathFor("   ", @"H:\BakaLoader"));
            Assert.Null(StraySaveFolder.StrayPathFor(Stored("saves"), "   "));

            Assert.Equal(Path.Combine(@"H:\BakaLoader", Variable, "saves"),
                StraySaveFolder.StrayPathFor(Stored("saves"), @"H:\BakaLoader"));
        }

        /// <summary>
        /// And the same rule off a real disk, on the tester's shape and then on the shape after
        /// he moved the folder by hand. The "install folder" here is a temporary directory, so
        /// nothing is written anywhere near a real BakaLoader install.
        /// </summary>
        [Fact]
        public void The_repair_offer_fires_on_the_real_folder_shape_and_goes_quiet_once_it_is_fixed()
        {
            var install = Path.Combine(Home, "install");
            var stored = Stored("AppData", "LocalLow", "IronGate", "Valheim", "servers", "Sulniel_Dev");

            // What the old build made: the raw text as a folder tree inside the install.
            var stray = StraySaveFolder.StrayPathFor(stored, install);
            Directory.CreateDirectory(Path.Combine(stray, "worlds_local"));
            File.WriteAllText(Path.Combine(stray, "worlds_local", "Sulneil.fwl"), "");

            Assert.Equal(StraySaveFolder.ShapeStray,
                StraySaveFolder.Judge(StraySaveFolder.Look(stored, install)));

            // The move, which only ever happens on a press.
            var resolved = PathCheck.Resolve(stored);
            StraySaveFolder.Move(stray, resolved, install);

            Assert.True(File.Exists(Path.Combine(resolved, "worlds_local", "Sulneil.fwl")));
            Assert.False(Directory.Exists(stray));
            Assert.Equal(StraySaveFolder.ShapeNone,
                StraySaveFolder.Judge(StraySaveFolder.Look(stored, install)));
        }

        /// <summary>
        /// THE ONE A REVIEW CAUGHT, and the shape every host who hit issue 17 has: TWO realms
        /// reading one stored string.
        /// <para>
        /// A duplicated realm stores its own folder, <c>&lt;app wide&gt;\servers\&lt;name&gt;</c>.
        /// The realm it was duplicated FROM stores nothing of its own and falls back to the app
        /// wide string, which is the parent of that. So moving the duplicate's folder empties a
        /// directory chain inside the install that the sibling realm still asks about: the leaf
        /// is gone and <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\servers</c> is still
        /// sitting there with nothing in it.
        /// </para>
        /// <para>
        /// Both halves of that are asserted here, because either one alone leaves the row
        /// standing for good: the move takes the empty chain it made with it, and a directory
        /// that holds no worlds is not a save folder to offer to move. Before the fix this fails
        /// on the sibling realm with "both", which is the row the reporter of issue 17 would have
        /// read on his first launch of 1.2.9 over a folder holding no worlds at all, and which
        /// sits at rank 2 of the condition bar where it hides every other row that realm has.
        /// </para>
        /// </summary>
        [Fact]
        public void A_sibling_realm_reading_the_same_stored_folder_goes_quiet_when_the_move_is_done()
        {
            var install = Path.Combine(Home, "install");
            var appWide = Stored("AppData", "LocalLow", "IronGate", "Valheim");
            var realm = Stored("AppData", "LocalLow", "IronGate", "Valheim", "servers", "Sulniel_Dev");

            // The host's own worlds, in the folder the app reads now. This is why the sibling
            // realm answers "both" rather than "stray": the destination is really there.
            var resolvedAppWide = PathCheck.Resolve(appWide);
            Directory.CreateDirectory(Path.Combine(resolvedAppWide, "worlds_local"));
            File.WriteAllText(Path.Combine(resolvedAppWide, "worlds_local", "Home.fwl"), "");

            // And what the old build made: the duplicate's world, in the literal %VAR% tree
            // inside the install folder.
            var stray = StraySaveFolder.StrayPathFor(realm, install);
            Directory.CreateDirectory(Path.Combine(stray, "worlds_local"));
            File.WriteAllText(Path.Combine(stray, "worlds_local", "Sulneil.fwl"), "");

            Assert.Equal(StraySaveFolder.ShapeStray,
                StraySaveFolder.Judge(StraySaveFolder.Look(realm, install)));

            StraySaveFolder.Move(stray, PathCheck.Resolve(realm), install);

            // The realm that was moved: quiet, and its world is where the app reads.
            Assert.Equal(StraySaveFolder.ShapeNone,
                StraySaveFolder.Judge(StraySaveFolder.Look(realm, install)));
            Assert.True(File.Exists(Path.Combine(PathCheck.Resolve(realm), "worlds_local", "Sulneil.fwl")));

            // And the realm that never had a folder of its own: also quiet. Said twice, because
            // the two halves of the fix each close it on their own and both are wanted.
            Assert.Equal(StraySaveFolder.ShapeNone,
                StraySaveFolder.Judge(StraySaveFolder.Look(appWide, install)));
            Assert.False(Directory.Exists(StraySaveFolder.StrayPathFor(appWide, install)),
                "the empty %VAR% skeleton is still inside the install folder");

            // The install folder itself is NOT taken: the prune stops at it however empty the
            // chain above the leaf turns out to be.
            Assert.True(Directory.Exists(install), "the prune walked up out of the install folder");
        }

        /// <summary>
        /// A directory chain with no worlds in it is not a save folder, whoever left it there.
        /// <para>
        /// Asked without a move at all, because a host can reach this shape by hand: the
        /// by-hand repair in Troubleshooting says to move the realm folder across and delete the
        /// <c>%USERPROFILE%</c> folder, and somebody who does the first and not the second has a
        /// skeleton on disk. "Anything at all inside it" counted that as contents and raised a
        /// row offering to move a folder holding no worlds.
        /// </para>
        /// </summary>
        [Fact]
        public void A_directory_chain_with_no_worlds_in_it_is_not_a_save_folder()
        {
            var install = Path.Combine(Home, "install");
            var appWide = Stored("AppData", "LocalLow", "IronGate", "Valheim");

            var resolvedAppWide = PathCheck.Resolve(appWide);
            Directory.CreateDirectory(Path.Combine(resolvedAppWide, "worlds_local"));
            File.WriteAllText(Path.Combine(resolvedAppWide, "worlds_local", "Home.fwl"), "");

            var stray = StraySaveFolder.StrayPathFor(appWide, install);
            Directory.CreateDirectory(Path.Combine(stray, "servers"));

            Assert.Equal(StraySaveFolder.ShapeNone,
                StraySaveFolder.Judge(StraySaveFolder.Look(appWide, install)));

            // An empty worlds folder is the same answer: the game makes that the moment a realm
            // is given its own folder, and a realm that has never been started holds no worlds.
            Directory.CreateDirectory(Path.Combine(stray, "worlds_local"));
            Assert.Equal(StraySaveFolder.ShapeNone,
                StraySaveFolder.Judge(StraySaveFolder.Look(appWide, install)));

            // And one world file in it is the whole difference.
            File.WriteAllText(Path.Combine(stray, "worlds_local", "Sulneil.fwl"), "");
            Assert.Equal(StraySaveFolder.ShapeBoth,
                StraySaveFolder.Judge(StraySaveFolder.Look(appWide, install)));
        }

        /// <summary>
        /// The move refuses rather than merging, and refuses before it touches anything: two
        /// save folders are two sets of worlds, and choosing between them is not this code's
        /// decision to make.
        /// </summary>
        [Fact]
        public void The_move_refuses_when_there_is_already_a_folder_at_the_destination()
        {
            var install = Path.Combine(Home, "install");
            var stored = Stored("saves");

            // Worlds on BOTH sides, because that is what "both" now means: a folder that is
            // merely there is the shape below this one.
            var stray = StraySaveFolder.StrayPathFor(stored, install);
            Directory.CreateDirectory(Path.Combine(stray, "worlds_local"));
            File.WriteAllText(Path.Combine(stray, "worlds_local", "Sulneil.fwl"), "");

            var resolved = PathCheck.Resolve(stored);
            Directory.CreateDirectory(Path.Combine(resolved, "worlds_local"));
            File.WriteAllText(Path.Combine(resolved, "worlds_local", "Home.fwl"), "");

            Assert.Equal(StraySaveFolder.ShapeBoth,
                StraySaveFolder.Judge(StraySaveFolder.Look(stored, install)));
            Assert.Throws<InvalidOperationException>(
                () => StraySaveFolder.Move(stray, resolved, install));

            // Nothing moved, nothing merged, and the folders both hold exactly what they did.
            Assert.True(File.Exists(Path.Combine(stray, "worlds_local", "Sulneil.fwl")));
            Assert.Equal(new[] { "Home.fwl" },
                Directory.GetFiles(Path.Combine(resolved, "worlds_local"))
                    .Select(Path.GetFileName).ToArray());

            // And a stray folder that is not there is a refusal rather than a silent nothing.
            Assert.Throws<DirectoryNotFoundException>(
                () => StraySaveFolder.Move(Path.Combine(Home, "gone"), Path.Combine(Home, "elsewhere"), install));
        }

        /// <summary>
        /// The destination folder is THERE and holds no worlds, which is the one shape with a
        /// real risk in it and no button to offer: a start would come up on a brand new world
        /// while the real one sits in the folder the old build made. So it is said as an error
        /// and the sentence names which of the two folders the worlds are in.
        /// </summary>
        [Fact]
        public void A_destination_that_exists_and_holds_no_worlds_is_its_own_shape()
        {
            var install = Path.Combine(Home, "install");
            var stored = Stored("saves");

            var stray = StraySaveFolder.StrayPathFor(stored, install);
            Directory.CreateDirectory(Path.Combine(stray, "worlds_local"));
            File.WriteAllText(Path.Combine(stray, "worlds_local", "Sulneil.fwl"), "");

            // What a save folder the game has never written a world into looks like, which is
            // also what BakaLoader makes for a realm given its own folder.
            var resolved = PathCheck.Resolve(stored);
            Directory.CreateDirectory(Path.Combine(resolved, "worlds_local"));

            Assert.Equal(StraySaveFolder.ShapeDestinationEmpty,
                StraySaveFolder.Judge(StraySaveFolder.Look(stored, install)));

            // No button for it, and the move refuses on its own account as well: a move into a
            // folder that exists would be a merge whatever the row offered.
            Assert.Throws<InvalidOperationException>(
                () => StraySaveFolder.Move(stray, resolved, install));
            Assert.True(File.Exists(Path.Combine(stray, "worlds_local", "Sulneil.fwl")));

            // One world into the destination by hand and it is the ordinary "both".
            File.WriteAllText(Path.Combine(resolved, "worlds_local", "Home.fwl"), "");
            Assert.Equal(StraySaveFolder.ShapeBoth,
                StraySaveFolder.Judge(StraySaveFolder.Look(stored, install)));
        }

        /// <summary>
        /// Across drives is the ORDINARY case for this move, not the odd one: the stray folder
        /// is inside the BakaLoader install and the destination is under the user profile, which
        /// on the machine this was reported from were H: and C:. Directory.Move refuses two
        /// different roots outright, so a one-call version of the move would have failed for the
        /// very host it was written for. The arm that copies, counts and then deletes is driven
        /// here directly, because a test cannot conjure a second volume and the mechanics do not
        /// depend on there being one.
        /// </summary>
        [Fact]
        public void The_move_copies_and_then_deletes_when_the_two_folders_are_on_different_drives()
        {
            var stray = Path.Combine(Home, "install", "%VAR%", "servers", "Sulniel_Dev");
            var worlds = Path.Combine(stray, "worlds_local");
            Directory.CreateDirectory(worlds);
            File.WriteAllText(Path.Combine(worlds, "Sulneil.fwl"), "fwl");
            File.WriteAllText(Path.Combine(worlds, "Sulneil.db"), "db");
            File.WriteAllText(Path.Combine(stray, "adminlist.txt"), "76561198012345678");

            var destination = Path.Combine(Home, "localLow", "servers", "Sulniel_Dev");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            StraySaveFolder.CopyAcrossVolumes(stray, destination);

            Assert.Equal("fwl", File.ReadAllText(Path.Combine(destination, "worlds_local", "Sulneil.fwl")));
            Assert.Equal("db", File.ReadAllText(Path.Combine(destination, "worlds_local", "Sulneil.db")));
            Assert.Equal("76561198012345678", File.ReadAllText(Path.Combine(destination, "adminlist.txt")));

            // Three files in, three files out, and the source is gone rather than left as a
            // second copy for the next reader to find.
            Assert.Equal(3, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
            Assert.False(Directory.Exists(stray));
        }

        /// <summary>
        /// And the same arm with a destination that is already there: refused by
        /// <see cref="StraySaveFolder.Move"/> before it ever reaches a copy, so a folder holding
        /// worlds cannot be written into by this.
        /// </summary>
        [Fact]
        public void The_move_never_reaches_a_copy_when_the_destination_is_already_there()
        {
            var stray = Path.Combine(Home, "install", "%VAR%", "saves");
            Directory.CreateDirectory(stray);
            File.WriteAllText(Path.Combine(stray, "from-the-old-folder.txt"), "old");

            var destination = Path.Combine(Home, "localLow", "saves");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "already-here.txt"), "new");

            Assert.Throws<InvalidOperationException>(
                () => StraySaveFolder.Move(stray, destination, Path.Combine(Home, "install")));

            Assert.True(File.Exists(Path.Combine(stray, "from-the-old-folder.txt")));
            Assert.Equal(new[] { "already-here.txt" },
                Directory.GetFiles(destination).Select(Path.GetFileName).ToArray());
        }

        // ------------------------- 5. a save folder changed while the server is running

        /// <summary>
        /// A save folder saved while the world is up IS a difference the host has to be told
        /// about, and the rule that raises "restart pending" says so: the running server was
        /// handed its folder on the command line and keeps writing there until it restarts.
        /// </summary>
        [Fact]
        public void A_save_folder_changed_while_the_server_runs_is_a_pending_restart()
        {
            var first = Path.Combine(Home, "saves");
            var second = Path.Combine(Home, "other-saves");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);

            var exe = Path.Combine(Home, PathCheck.ServerExeName);
            File.WriteAllText(exe, "");

            ValheimServerOptions For(string folder) => ValheimServerOptions.FromPreferences(
                new ServerPreferences { ProfileName = "Sulniel_Dev", WorldName = "Sulneil", Port = 2456 },
                new UserPreferences { SaveDataFolderPath = folder, ServerExePath = exe },
                null);

            var running = For(first);
            Assert.True(ValheimServerOptions.RelaunchWouldDiffer(running, For(second)),
                "a save folder saved while the world is up raises nothing, so the two disagree in silence");

            // And writing the SAME folder out by hand, variable filled in, is not a change: a
            // host who typed their folder out must not be asked to restart for nothing.
            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(running, For(Stored("saves"))));
        }

        /// <summary>
        /// The player lists follow the SAVED value from the next call on, because the handler
        /// resolves the folder off preferences on every call rather than off the launch-time
        /// copy the running session holds. Held over the source, because the alternative the
        /// rule exists to forbid is a line that reads the session's options instead.
        /// </summary>
        [Fact]
        public void The_player_list_handlers_read_the_saved_folder_rather_than_the_launch_time_copy()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            Assert.Contains(
                "bool? listed = PlayerListService.IsListed(ResolveSaveDataFolder(null), list, id);",
                bridge);
            Assert.Contains("var folder = ResolveSaveDataFolder(null);", bridge);

            // The boundary is in the one method both of those go through, so neither handler
            // has to remember it.
            Assert.Contains("private string ResolveSaveDataFolder(string explicitPath)", bridge);
            Assert.Contains("=> PathCheck.Resolve(StoredSaveDataFolder(explicitPath));", bridge);

            // And the save says so out loud: the reply carries the one fact no later read of
            // the profile has, and the page has two sentences for it.
            Assert.Contains("reply[\"SaveFolderMovedWhileRunning\"] = SaveFolderMovedWhileTheServerIsUp(prefs);",
                bridge);

            var app = AppSourceTree.Web("app.js");
            Assert.Contains("r.SaveFolderMovedWhileRunning", app);
            Assert.Contains("T(\"world.saved.savedir.toast\")", app);
            Assert.Contains("T(\"world.saved.savedir.running\")", app);
        }

        /// <summary>
        /// The two RPCs the row is made of, held over the source, because they live on a window
        /// a test cannot construct and the facts they carry are the ones a later edit would drop
        /// without noticing: the install folder the prune is bounded by, the running-server
        /// refusal in front of a folder move, and the five fields the page reads off the reply.
        /// </summary>
        [Fact]
        public void The_move_rpc_hands_the_prune_its_boundary_and_refuses_while_a_server_is_up()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            // The boundary travels with the answer rather than being worked out again at the
            // call: a prune that inferred how far up it may delete would be inferring it from
            // the very path it is deleting.
            Assert.Contains("StraySaveFolder.Move(found.Stray, found.Resolved, found.Install);", bridge);
            Assert.Contains("RefuseWhileASessionIsWritingTo(found.Stray, found.Resolved);", bridge);

            // In that order: the refusal is before the move, not after it.
            var refuses = bridge.IndexOf("RefuseWhileASessionIsWritingTo(found.Stray", StringComparison.Ordinal);
            var moves = bridge.IndexOf("StraySaveFolder.Move(found.Stray", StringComparison.Ordinal);
            Assert.True(refuses > 0 && moves > refuses,
                "the running-server check sits after the move, so it cannot stop one");

            // The fallback the sibling-realm case lives in, and the whole answer, both out in
            // the open where the tests above drive them.
            Assert.Contains("StraySaveFolder.StoredFor(", bridge);
            Assert.Contains("=> StraySaveFolder.For(", bridge);
            Assert.Contains("StoredSaveFolderToAskAbout(profileName), InstallFoldersAStrayPathCouldBeUnder());",
                bridge);

            // And the reply the page reads, field by field.
            foreach (var field in new[] { "shape = found.Shape", "stored = found.Stored",
                                          "resolved = found.Resolved", "stray = found.Stray" })
                Assert.Contains(field, bridge);

            // The log names every realm at startup, not only the one the window opened on: an
            // auto-start realm is launched before the page has asked anything.
            Assert.Contains("if (SplashIndex == 0) NameStraySaveFoldersInTheLog();", bridge);
            Assert.Contains("TieAFailedStartToTheStraySaveFolder(StartProfile);", bridge);
        }

        // --------------------- 5b. the second read of the row: rank, memory, and two folders

        /// <summary>
        /// A row the host has waved away stays away, and a row about something ELSE is said.
        /// <para>
        /// THE BUG. Two of the shapes are raised from facts BakaLoader cannot change: worlds
        /// in both folders, and a destination folder that is there holding nothing, both STAND
        /// until somebody moves folders about in Explorer. The page asks on the first frame, on
        /// every realm switch and after every Save Config, so a dismissal that cleared the bar
        /// and nothing else meant the host read the same sentence on the next boot, the next
        /// switch and the next save, for ever, about a decision they had already made. It is
        /// the bug the unattended BepInEx notice had, and this is the same answer: a fact is
        /// NAMED, the name is written down, and anything that changes on disk changes the name.
        /// </para>
        /// </summary>
        [Fact]
        public void A_waved_away_fact_is_not_said_again_and_a_changed_one_is()
        {
            var install = Path.Combine(Home, "install");
            var stored = Stored("AppData", "LocalLow", "IronGate", "Valheim", "servers", "Sulniel_Dev");
            var stray = StraySaveFolder.StrayPathFor(stored, install);
            Directory.CreateDirectory(Path.Combine(stray, "worlds_local"));
            File.WriteAllText(Path.Combine(stray, "worlds_local", "Sulneil.fwl"), "");

            var first = StraySaveFolder.For(stored, new[] { install });
            Assert.Equal(StraySaveFolder.ShapeStray, first.Shape);

            var key = StraySaveFolder.KeyFor(first);
            Assert.NotNull(key);
            Assert.Contains(StraySaveFolder.ShapeStray, key);
            Assert.Contains(stray, key);
            Assert.Contains(PathCheck.Resolve(stored), key);

            // An install that has waved nothing away holds no keys, and neither null nor an
            // empty list is allowed to read as "seen".
            Assert.False(StraySaveFolder.Seen(key, null));
            Assert.False(StraySaveFolder.Seen(key, new List<string>()));

            var kept = StraySaveFolder.Remember(null, key);
            Assert.Equal(new[] { key }, kept);
            Assert.True(StraySaveFolder.Seen(key, kept));

            // Asked again over an UNCHANGED disk: the same fact, so the row is not raised. This
            // is the boot, the realm switch and the save that used to stand it back up.
            var again = StraySaveFolder.For(stored, new[] { install });
            Assert.Equal(key, StraySaveFolder.KeyFor(again));
            Assert.True(StraySaveFolder.Seen(StraySaveFolder.KeyFor(again), kept));

            // And the fact CHANGES: the host makes the folder the app reads, by hand, without
            // the worlds in it. Different shape, different name, news again.
            Directory.CreateDirectory(PathCheck.Resolve(stored));
            var changed = StraySaveFolder.For(stored, new[] { install });
            Assert.Equal(StraySaveFolder.ShapeDestinationEmpty, changed.Shape);
            var second = StraySaveFolder.KeyFor(changed);
            Assert.NotEqual(key, second);
            Assert.False(StraySaveFolder.Seen(second, kept));

            // Waving THAT away keeps the first one. A list rather than one field, because the
            // fact is per realm and a host with several realms can be in two shapes at once:
            // one field meant each dismissal undid the one before it.
            var both = StraySaveFolder.Remember(kept, second);
            Assert.True(StraySaveFolder.Seen(key, both));
            Assert.True(StraySaveFolder.Seen(second, both));

            // The same fact twice is one entry, so a host closing a row twice cannot grow the
            // list, and the newest is at the end.
            var twice = StraySaveFolder.Remember(both, second);
            Assert.Equal(both.Count, twice.Count);
            Assert.Equal(second, twice[twice.Count - 1]);

            // And it is bounded, so nothing here can grow for ever: the oldest entry falling
            // off the front is one row said once more, which is the cheap end of the trade.
            var crowd = new List<string>();
            for (var i = 0; i < StraySaveFolder.KeysKept + 5; i++)
                crowd = StraySaveFolder.Remember(crowd, "fact-" + i);
            Assert.Equal(StraySaveFolder.KeysKept, crowd.Count);
            Assert.False(StraySaveFolder.Seen("fact-0", crowd));
            Assert.True(StraySaveFolder.Seen("fact-" + (StraySaveFolder.KeysKept + 4), crowd));

            // An answer with nothing to say names no fact at all, and a nameless fact is never
            // seen: an empty key matching an empty entry would silence every row at once.
            var quiet = StraySaveFolder.For(Stored("nowhere"), new[] { install });
            Assert.Null(StraySaveFolder.KeyFor(quiet));
            Assert.False(StraySaveFolder.Seen(StraySaveFolder.KeyFor(quiet), both));
            Assert.False(StraySaveFolder.Seen("", new List<string> { "" }));
        }

        /// <summary>
        /// The waved-away facts survive a trip through userprefs.json, because a dismissal that
        /// only lasted until the app closed is the bug this was written for.
        /// </summary>
        [Fact]
        public void The_waved_away_facts_survive_a_trip_through_the_preferences_file()
        {
            var prefs = new UserPreferences
            {
                StraySaveFolderSeenKeys = new List<string> { "stray|a|b", "destination_empty|c|d" },
            };

            Assert.Equal(prefs.StraySaveFolderSeenKeys,
                UserPreferences.FromFile(prefs.ToFile()).StraySaveFolderSeenKeys);

            // A document that has never held one reads as none rather than as null, so the
            // Seen question never has to allow for a missing list.
            Assert.NotNull(UserPreferences.FromFile(new UserPreferencesFile()).StraySaveFolderSeenKeys);
            Assert.Empty(UserPreferences.FromFile(new UserPreferencesFile()).StraySaveFolderSeenKeys);

            // And a hand-edited document holding blanks cannot put an entry in that matches an
            // answer naming no fact.
            Assert.Empty(UserPreferences.FromFile(new UserPreferencesFile
            {
                StraySaveFolderSeenKeys = new List<string> { "", "   ", null },
            }).StraySaveFolderSeenKeys);
        }

        /// <summary>
        /// Worlds in MORE than one old folder is its own shape, names every folder, and offers
        /// no move.
        /// <para>
        /// THE BUG. The answer stopped at the first install folder that had something to say.
        /// A relative path is anchored at the WORKING DIRECTORY, and one install can have had
        /// more than one of those: a shortcut carrying a "Start in" of its own and a
        /// service-style launch leave their messes in different folders. The host was told
        /// about one tree of worlds, pressed the button, and the second tree stayed on the disk
        /// with nothing ever said about it.
        /// </para>
        /// </summary>
        [Fact]
        public void More_than_one_old_folder_is_its_own_shape_and_names_every_one()
        {
            var shortcut = Path.Combine(Home, "started-from-a-shortcut");
            var service = Path.Combine(Home, "started-as-a-service");
            var stored = Stored("AppData", "LocalLow", "IronGate", "Valheim", "servers", "Sulniel_Dev");

            foreach (var root in new[] { shortcut, service })
            {
                var tree = StraySaveFolder.StrayPathFor(stored, root);
                Directory.CreateDirectory(Path.Combine(tree, "worlds_local"));
                File.WriteAllText(Path.Combine(tree, "worlds_local", "Sulneil.fwl"), "");
            }

            var answer = StraySaveFolder.For(stored, new[] { shortcut, service });

            Assert.Equal(StraySaveFolder.ShapeMany, answer.Shape);
            Assert.Equal(2, answer.StrayFolders.Count);
            Assert.Contains(StraySaveFolder.StrayPathFor(stored, shortcut), answer.StrayFolders);
            Assert.Contains(StraySaveFolder.StrayPathFor(stored, service), answer.StrayFolders);

            // The first is still the one named on its own, for the sentences that name one, and
            // the install folder beside it is the boundary a prune would be bounded by. The
            // move RPC refuses any shape but ShapeStray, so this one never reaches a prune.
            Assert.Equal(StraySaveFolder.StrayPathFor(stored, shortcut), answer.Stray);
            Assert.Equal(shortcut, answer.Install);

            // The key names BOTH, so tidying one of them up by hand is news.
            var key = StraySaveFolder.KeyFor(answer);
            Assert.Contains(StraySaveFolder.StrayPathFor(stored, service), key);

            Directory.Delete(StraySaveFolder.StrayPathFor(stored, service), recursive: true);
            var oneLeft = StraySaveFolder.For(stored, new[] { shortcut, service });
            Assert.Equal(StraySaveFolder.ShapeStray, oneLeft.Shape);
            Assert.Single(oneLeft.StrayFolders);
            Assert.NotEqual(key, StraySaveFolder.KeyFor(oneLeft));
            Assert.False(StraySaveFolder.Seen(StraySaveFolder.KeyFor(oneLeft),
                StraySaveFolder.Remember(null, key)));
        }

        /// <summary>
        /// A new realm's own save folder REFUSES rather than landing on a name the disk has
        /// just said is taken.
        /// <para>
        /// THE BUG. The numbering had a ceiling, which it needed, because the question is asked
        /// of a disk and a predicate that says yes to everything would spin for ever. But
        /// falling out of the loop AT the ceiling handed back the last numbered name it tried,
        /// a path the predicate had just said is occupied, and Create then called
        /// CreateDirectory on it and shaped a new realm's save folder over whatever was in it.
        /// </para>
        /// </summary>
        [Fact]
        public void A_new_realms_save_folder_refuses_rather_than_landing_on_an_occupied_name()
        {
            var stored = Stored("saves");
            var parent = Path.Combine(Home, "saves", IsolatedSaveFolder.ServersFolderName);

            var refused = Assert.Throws<HostFacingException>(
                () => IsolatedSaveFolder.Plan(stored, "Proving", _ => true));

            Assert.Equal("paths.isolated.noFreeName", refused.MessageId);
            Assert.Equal("Proving", refused.Params["name"]);
            Assert.Equal(parent, refused.Params["folder"]);
            Assert.Equal(
                IsolatedSaveFolder.NameCeiling.ToString(System.Globalization.CultureInfo.InvariantCulture),
                refused.Params["ceiling"]);

            // The sentence names the folder and the name, because both are things the host can
            // act on, and it never names the path it would have taken.
            Assert.Contains(parent, refused.Message);
            Assert.Contains("Proving", refused.Message);
            Assert.DoesNotContain("Proving-999", refused.Message);

            // And the ordinary case is untouched: the numbering still skips what is taken.
            Assert.Equal(Path.Combine(parent, "Proving"),
                IsolatedSaveFolder.Plan(stored, "Proving", _ => false));
        }

        /// <summary>
        /// The disk half of the row's check runs on a worker with a ceiling, so a drive that is
        /// not answering cannot hold the window.
        /// <para>
        /// THE BUG. The handler was <c>Task.FromResult</c> over the whole answer, which means
        /// the Look and the HoldsWorlds walk under it ran on the window's own thread, with its
        /// painting stopped, on the first frame, on every realm switch and after every Save
        /// Config. One of the two folders it walks is wherever BakaLoader was unzipped, which
        /// on the install this was reported from was an external drive.
        /// </para>
        /// </summary>
        [Fact]
        public void The_stray_folder_check_does_its_disk_work_on_a_worker_with_a_budget()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            Assert.DoesNotContain(
                "RegisterRpc(\"paths.strayCheck\", p => Task.FromResult<object>(", bridge);
            Assert.Contains("RegisterRpc(\"paths.strayCheck\", async p =>", bridge);
            Assert.Contains("var looking = Task.Run(() => StraySaveFolder.For(stored, roots));", bridge);
            Assert.Contains(
                "var finished = await Task.WhenAny(looking, Task.Delay(StrayCheckBudget));", bridge);
            Assert.Contains("internal static TimeSpan StrayCheckBudget", bridge);

            // The preference reads stay on the window's thread, so what the worker is handed is
            // values rather than a window field.
            Assert.Contains("var stored = StoredSaveFolderToAskAbout(profile);", bridge);
            Assert.Contains("var roots = InstallFoldersAStrayPathCouldBeUnder().ToArray();", bridge);

            // Running out is an ANSWER, with nothing to say, because this check is quiet by
            // design: a toast about a question the host never asked is the one thing it must
            // not produce.
            Assert.Contains("Shape = StraySaveFolder.ShapeNone,", bridge);

            // And a worker nobody waits on any more has its throw read rather than left to sit
            // there as an unobserved task exception.
            Assert.Contains("TaskContinuationOptions.OnlyOnFaulted", bridge);

            // The startup sweep is the same disk work once PER REALM, on the road to the first
            // frame, so it is fire and forget on a worker as well. Nothing reads its answer: the
            // only thing it produces is log lines, so there is nothing to wait for.
            var sweep = bridge.IndexOf(
                "private void NameStraySaveFoldersInTheLog()", StringComparison.Ordinal);
            Assert.True(sweep > 0, "the startup sweep is gone");
            var body = bridge.Substring(sweep, Math.Min(1800, bridge.Length - sweep));
            Assert.Contains("_ = Task.Run(", body);
            Assert.Contains("foreach (var who in realms)", body);
            Assert.DoesNotContain("foreach (var prefs in ServerPrefsProvider.LoadPreferences())", body);
        }

        /// <summary>
        /// The reply names the FACT, and closing the row writes it down under the one gate
        /// every other writer of userprefs.json takes. Held over the source because both sides
        /// live on a window a test cannot construct.
        /// </summary>
        [Fact]
        public void The_rows_reply_names_the_fact_and_closing_it_writes_that_down()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            Assert.Contains("var key = StraySaveFolder.KeyFor(found);", bridge);
            Assert.Contains("strays = found.StrayFolders,", bridge);
            Assert.Contains("seen = StraySaveFolder.Seen(", bridge);
            Assert.Contains("UserPrefsProvider.LoadPreferences()?.StraySaveFolderSeenKeys)", bridge);

            Assert.Contains("RegisterRpc(\"paths.strayNoticeSeen\", p =>", bridge);
            // The key comes from the PAGE, because it is the key of the sentence the host
            // really read: asking the disk again here could write down a fact that appeared in
            // the meantime and silence a row nobody has ever seen.
            Assert.Contains("var closed = p.Value<string>(\"key\");", bridge);
            Assert.Contains("UserPrefsProvider.Mutate(prefs =>", bridge);
            Assert.Contains(
                "var kept = StraySaveFolder.Remember(prefs.StraySaveFolderSeenKeys, closed);", bridge);
            Assert.Contains("prefs.StraySaveFolderSeenKeys = kept;", bridge);

            var app = AppSourceTree.Web("app.js");
            Assert.Contains("Native.call(\"paths.strayNoticeSeen\",{key:found.key})", app);
            Assert.Contains("if(s.seen&&!(opts&&opts.asked))", app);
        }

        /// <summary>
        /// The two sentences about a save folder that moved under a RUNNING server are said on
        /// the save that moved it, and not on every save after that.
        /// <para>
        /// THE BUG. The disagreement is two paths that do not match, and it STANDS until the
        /// server restarts. So every later save of anything at all on that card, a port, a
        /// password, a backup count, came back with the same two sentences about the save
        /// folder, and a host saving three times in a row read them three times.
        /// </para>
        /// </summary>
        [Fact]
        public void The_running_servers_save_folder_disagreement_is_said_once_per_change()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            Assert.Contains(
                "private readonly Tools.Logging.OncePerChange SaveFolderDisagreementTold = new();", bridge);
            Assert.Contains("return SaveFolderDisagreementTold.Changed(", bridge);

            // Two forgets: a server that is not up, and two paths that agree. Both of them mean
            // the next real disagreement is news, so a restart followed by another move says it
            // again rather than staying quiet for ever.
            Assert.Equal(2, bridge.Split("SaveFolderDisagreementTold.Forget(").Length - 1);

            // And the rule itself, over the real helper with the real state shape.
            var told = new ValheimBakaLoader.Tools.Logging.OncePerChange();
            string Pair(string launched, string saved)
                => (PathCheck.Resolve(launched) + "|" + PathCheck.Resolve(saved)).ToUpperInvariant();

            // the save that moved it says it, and the saves after it do not
            Assert.True(told.Changed("Sulniel_Dev", Pair(Stored("saves"), @"D:\Other")));
            Assert.False(told.Changed("Sulniel_Dev", Pair(Stored("saves"), @"D:\Other")));

            // the same two folders written the other way round are the same fact, which is the
            // half that would otherwise say it again every time the host typed the variable out
            Assert.False(told.Changed("Sulniel_Dev", Pair(Path.Combine(Home, "saves"), @"d:\other")));

            // a THIRD folder is a new fact, and is said
            Assert.True(told.Changed("Sulniel_Dev", Pair(Stored("saves"), @"D:\Third")));

            // and another realm's saves are another realm's: one realm going quiet must not
            // take the next realm's first word with it
            Assert.True(told.Changed("Another", Pair(Stored("saves"), @"D:\Third")));

            // the server stopped, so the next disagreement is news again
            told.Forget("Sulniel_Dev");
            Assert.True(told.Changed("Sulniel_Dev", Pair(Stored("saves"), @"D:\Third")));
        }

        // -------------------------------- 6. the rule that keeps the boundary a boundary

        /// <summary>
        /// THE GATE THIS WHOLE RELEASE IS ABOUT, generalised.
        /// <para>
        /// Issue 17 is not really a bug in one line. It is what happens when a thing that must
        /// be done to every stored path is done at each SITE that remembers to do it: the
        /// published 1.2.8 called <c>Environment.ExpandEnvironmentVariables</c> fourteen times
        /// across eight files of the app, so "did this one remember" was a question with
        /// fourteen answers, and the places that had not remembered created a folder named for
        /// the variable and then stored its name. No test could fail on that, because every one
        /// of the fourteen was correct where it stood.
        /// </para>
        /// <para>
        /// So the rule is structural rather than per-site: the app project expands environment
        /// variables in ONE place, <see cref="PathCheck.Expand"/>, and every reader goes through
        /// it. A future site that reaches for the framework call directly fails here, which is
        /// the only moment anyone would think about it. The sibling
        /// <c>ValheimBakaLoader.Tools</c> project is named as the one exception and says why:
        /// it cannot see PathCheck, and what it expands is a path this app owns rather than one
        /// a host typed.
        /// </para>
        /// <para>
        /// Against the published 1.2.8 this fails and names all eight files.
        /// </para>
        /// </summary>
        [Fact]
        public void The_app_expands_environment_variables_in_exactly_one_place()
        {
            const string Call = "Environment.ExpandEnvironmentVariables";
            var root = Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader");

            var sites = new System.Collections.Generic.List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;

                var text = File.ReadAllText(file);
                var at = 0;
                while ((at = text.IndexOf(Call, at, StringComparison.Ordinal)) >= 0)
                {
                    sites.Add(Path.GetFileName(file));
                    at += Call.Length;
                }
            }

            Assert.Equal(new[] { "PathCheck.cs" }, sites.ToArray());

            // And the one site is inside the boundary rather than somewhere else in that file.
            var boundary = AppSourceTree.Read("ValheimBakaLoader", "Tools", "PathCheck.cs");
            var expand = boundary.IndexOf("public static string Expand(string raw)", StringComparison.Ordinal);
            Assert.True(expand > 0, "PathCheck.Expand is gone, so there is no boundary to be the only one");
            var body = boundary.Substring(expand, boundary.IndexOf("\n        }", expand, StringComparison.Ordinal) - expand);
            Assert.Contains(Call, body);

            // Resolve is the half that was missing, and it is built on Expand rather than beside
            // it: two boundaries would be the same defect with one more place to forget.
            var resolve = boundary.IndexOf("public static string Resolve(string raw)", StringComparison.Ordinal);
            Assert.True(resolve > 0, "PathCheck.Resolve is gone");
            var resolveBody = boundary.Substring(resolve, boundary.IndexOf("\n        }", resolve, StringComparison.Ordinal) - resolve);
            Assert.Contains("Expand(raw)", resolveBody);
            Assert.Contains("Path.GetFullPath(expanded)", resolveBody);
        }

        /// <summary>
        /// Both new sentences and the whole repair row are in the catalog, so none of them can
        /// render as its own id on a host reading any language.
        /// </summary>
        [Fact]
        public void Every_new_sentence_is_in_the_english_catalog()
        {
            var catalog = Newtonsoft.Json.Linq.JObject.Parse(
                File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(),
                    "ValheimBakaLoader", "WebUI", "i18n", "en.json")));
            var keys = (Newtonsoft.Json.Linq.JObject)catalog["keys"];

            foreach (var id in new[]
                     {
                         "cond.stray_save.title", "cond.stray_save.body", "cond.stray_save.both",
                         "cond.stray_save.destination_empty",
                         "cond.stray_save.move", "cond.stray_save.moved.toast",
                         "paths.stray.reason.nothing_to_move", "paths.stray.reason.move_failed",
                         "paths.stray.reason.server_running",
                         "world.saved.savedir.toast", "world.saved.savedir.running",
                     })
            {
                Assert.True(keys[id] != null, "the catalog has no " + id);
            }
        }
    }
}
