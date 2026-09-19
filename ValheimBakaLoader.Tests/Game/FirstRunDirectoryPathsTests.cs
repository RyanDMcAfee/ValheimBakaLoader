using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Why the two Directories boxes looked blank after the first time setup, proved rather
    /// than assumed, and what the Settings hall is handed now.
    /// <para>
    /// The report was that the boxes under Directories are empty on a fresh install even
    /// though setup was completed and Open opens the right folder. Both halves of that are
    /// true at once, and this is the reason: the wizard writes its two answers to the APP
    /// WIDE settings, and the hall renders the PROFILE's own two fields, which are null
    /// until somebody overrides a path for one server. Open never read the profile, it
    /// resolved the same fallback the launcher resolves, so it opened the right folder
    /// while the box above it said nothing.
    /// </para>
    /// <para>
    /// Nothing about where the values are stored changes. What changes is that the reply the
    /// hall renders from now carries the path in force and where it came from, so the line
    /// above each box can say it.
    /// </para>
    /// </summary>
    public class FirstRunDirectoryPathsTests
    {
        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        // ------------------------------------------------------- A. the state after setup

        /// <summary>
        /// A fresh install, before anything is typed anywhere: the app wide settings carry
        /// the two built in defaults and a profile carries neither.
        /// </summary>
        [Fact]
        public void A_fresh_install_keeps_both_paths_at_the_app_level_and_none_on_a_profile()
        {
            var app = UserPreferences.GetDefault();
            Assert.Equal(Resources.DefaultServerPath, app.ServerExePath);
            Assert.Equal(Resources.DefaultValheimSaveFolder, app.SaveDataFolderPath);

            var profile = new ServerPreferences();
            Assert.Null(profile.ServerExePath);
            Assert.Null(profile.SaveDataFolderPath);
        }

        /// <summary>
        /// The wizard's own write, run against a document that really round trips through the
        /// on-disk shape. Afterwards the app wide settings hold both answers and the profile
        /// still holds neither: this IS the blank box, reproduced.
        /// </summary>
        [Fact]
        public void The_wizard_answers_land_on_the_app_wide_settings_and_never_on_a_profile()
        {
            var document = new RoundTrippingUserPrefs();
            IUserPreferencesProvider app = document;
            var profiles = new ServerPreferencesProvider(document, Quiet());
            profiles.SavePreferences(new ServerPreferences { ProfileName = "Default" });

            // Exactly the body of the setup.complete handler.
            const string exe = @"D:\Games\Valheim dedicated server\valheim_server.exe";
            const string save = @"D:\Games\ValheimSaves";
            app.Mutate(prefs =>
            {
                if (!string.IsNullOrWhiteSpace(exe)) prefs.ServerExePath = exe.Trim();
                if (!string.IsNullOrWhiteSpace(save)) prefs.SaveDataFolderPath = save.Trim();
                prefs.SetupCompleted = true;
            });

            var after = app.LoadPreferences();
            Assert.True(after.SetupCompleted);
            Assert.Equal(exe, after.ServerExePath);
            Assert.Equal(save, after.SaveDataFolderPath);

            // And the hall's own source of values, untouched. This is the blank box.
            var profile = profiles.LoadPreferences("Default");
            Assert.Null(profile.ServerExePath);
            Assert.Null(profile.SaveDataFolderPath);
        }

        /// <summary>
        /// The handler this models, named in the source, so the reproduction above cannot
        /// quietly stop describing the real path. setup.complete writes the whole app wide
        /// document and mentions no profile at all.
        /// </summary>
        [Fact]
        public void The_setup_handler_writes_the_app_wide_document_and_names_no_profile()
        {
            var bridge = Bridge();
            var start = bridge.IndexOf("RegisterRpc(\"setup.complete\"", StringComparison.Ordinal);
            Assert.True(start > 0, "setup.complete is gone from the bridge");
            var body = bridge.Substring(start, bridge.IndexOf("RegisterRpc(\"setup.reset\"", start, StringComparison.Ordinal) - start);

            Assert.Contains("UserPrefsProvider.Mutate(prefs =>", body);
            Assert.Contains("prefs.ServerExePath = exe.Trim();", body);
            Assert.Contains("prefs.SaveDataFolderPath = save.Trim();", body);
            Assert.DoesNotContain("ServerPrefsProvider", body);
        }

        /// <summary>
        /// And the other half of the report: Open was never reading the box. It resolves the
        /// profile override first and falls back to the app wide value, which after the
        /// wizard is the answer the host typed. Right folder, blank box, same install.
        /// </summary>
        [Fact]
        public void Open_resolves_the_same_fallback_the_launcher_does_rather_than_the_box()
        {
            var bridge = Bridge();

            Assert.Contains("\"saveData\" => ResolveSaveDataFolder(null),", bridge);
            Assert.Contains("\"serverDir\" => Path.GetDirectoryName(GetServerExePath() ?? \"\"),", bridge);

            // Both of those resolve profile first, app wide second.
            Assert.Contains("if (!string.IsNullOrWhiteSpace(profilePrefs?.SaveDataFolderPath)) return profilePrefs.SaveDataFolderPath;", bridge);
            Assert.Contains("if (!string.IsNullOrWhiteSpace(profilePrefs?.ServerExePath)) return profilePrefs.ServerExePath;", bridge);
        }

        // ------------------------------------------------------------------ B. the answer

        /// <summary>
        /// The reply the hall renders from, on the install above: both boxes still empty,
        /// and both lines above them able to say what is in force and that it is the
        /// default rather than something set for this one server.
        /// </summary>
        [Fact]
        public void A_profile_with_no_paths_of_its_own_reports_the_default_and_says_so()
        {
            var app = new UserPreferences
            {
                ServerExePath = @"D:\Games\Valheim dedicated server\valheim_server.exe",
                SaveDataFolderPath = @"D:\Games\ValheimSaves",
            };

            var dto = BlendWindow.BuildProfilePrefsDto(new ServerPreferences { ProfileName = "Default" }, app);

            Assert.Equal(@"D:\Games\Valheim dedicated server\valheim_server.exe",
                dto.Value<string>("EffectiveServerExePath"));
            Assert.Equal("default", dto.Value<string>("ServerExePathSource"));
            Assert.Equal(@"D:\Games\ValheimSaves", dto.Value<string>("EffectiveSaveDataFolderPath"));
            Assert.Equal("default", dto.Value<string>("SaveDataFolderPathSource"));

            // The boxes are the override and stay the override: still null, still blank.
            Assert.Null(dto.Value<string>("ServerExePath"));
            Assert.Null(dto.Value<string>("SaveDataFolderPath"));
        }

        /// <summary>A profile that names its own paths wins, and says that it did.</summary>
        [Fact]
        public void A_profile_that_names_its_own_paths_wins_and_is_named_as_the_source()
        {
            var app = new UserPreferences
            {
                ServerExePath = @"C:\Steam\Valheim dedicated server\valheim_server.exe",
                SaveDataFolderPath = @"C:\Saves",
            };
            var profile = new ServerPreferences
            {
                ProfileName = "Trialgrounds",
                ServerExePath = @"E:\Second\Valheim dedicated server\valheim_server.exe",
                SaveDataFolderPath = @"E:\Second\saves",
            };

            var dto = BlendWindow.BuildProfilePrefsDto(profile, app);

            Assert.Equal(@"E:\Second\Valheim dedicated server\valheim_server.exe",
                dto.Value<string>("EffectiveServerExePath"));
            Assert.Equal("profile", dto.Value<string>("ServerExePathSource"));
            Assert.Equal(@"E:\Second\saves", dto.Value<string>("EffectiveSaveDataFolderPath"));
            Assert.Equal("profile", dto.Value<string>("SaveDataFolderPathSource"));
        }

        /// <summary>
        /// The stored default carries %USERPROFILE%, and a host reading a line that says
        /// where their worlds are wants the folder, not the variable.
        /// </summary>
        [Fact]
        public void The_effective_paths_arrive_with_their_environment_variables_filled_in()
        {
            var dto = BlendWindow.BuildProfilePrefsDto(
                new ServerPreferences { ProfileName = "Default" }, UserPreferences.GetDefault());

            var save = dto.Value<string>("EffectiveSaveDataFolderPath");
            var exe = dto.Value<string>("EffectiveServerExePath");

            Assert.DoesNotContain("%", save);
            Assert.DoesNotContain("%", exe);
            Assert.EndsWith(@"\IronGate\Valheim", save);
            Assert.EndsWith(PathCheck.ServerExeName, exe);

            // The stored values, which the boxes show, are untouched by the expansion.
            Assert.Contains("%", Resources.DefaultValheimSaveFolder);
        }

        /// <summary>
        /// Additive in the way that matters: every key the page already read is still there,
        /// spelled the same way, so nothing that knows only the old reply changes behaviour.
        /// </summary>
        [Fact]
        public void The_reply_keeps_every_key_it_carried_before()
        {
            var profile = new ServerPreferences
            {
                ProfileName = "Default",
                Name = "Baka Gaijin",
                WorldName = "Midgard",
                Port = 2456,
                RconEnabled = true,
                RconPort = 25575,
                AdditionalArgs = "-crossplay",
            };

            var before = JObject.FromObject(profile);
            var after = BlendWindow.BuildProfilePrefsDto(profile, UserPreferences.GetDefault());

            foreach (var key in before.Properties().Select(x => x.Name))
                Assert.True(JToken.DeepEquals(before[key], after[key]), "the reply changed " + key);

            // Four keys added and nothing else.
            var added = after.Properties().Select(x => x.Name)
                .Except(before.Properties().Select(x => x.Name)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(
                new[]
                {
                    "EffectiveSaveDataFolderPath", "EffectiveServerExePath",
                    "SaveDataFolderPathSource", "ServerExePathSource",
                },
                added);
        }

        /// <summary>
        /// Both halls that hand the page a profile hand it the same shape. profiles.save
        /// answering the bare object was how the lines went stale the moment they were saved.
        /// </summary>
        [Fact]
        public void Both_profile_replies_carry_the_paths_in_force()
        {
            var bridge = Bridge();

            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
                bridge, @"BuildProfilePrefsDto\(prefs, UserPrefsProvider\.LoadPreferences\(\)\)").Count);
            // And neither of them hands back the bare object any more.
            Assert.DoesNotContain("return Task.FromResult<object>(prefs);", bridge);
        }

        /// <summary>
        /// Stands in for userprefs.json: a load hands back its own copy of the document and a
        /// save replaces it wholesale, through the same on-disk shape the real file uses.
        /// </summary>
        private sealed class RoundTrippingUserPrefs : IUserPreferencesProvider
        {
            private UserPreferencesFile Stored = new();

            public event EventHandler<UserPreferences> PreferencesSaved;

            public UserPreferences LoadPreferences() => UserPreferences.FromFile(Stored);

            public void SavePreferences(UserPreferences preferences)
            {
                Stored = preferences.ToFile();
                PreferencesSaved?.Invoke(this, preferences);
            }
        }
    }
}
