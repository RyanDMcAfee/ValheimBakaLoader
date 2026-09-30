using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A refusal stands once per fact, and a closed notice stays closed.
    /// <para>
    /// WHY THIS EXISTS. The owner's install runs a scheduled restart window. Every window
    /// asked BepInEx whether there was anything to do and every one of them declined for the
    /// same reason, because the loader on that install was not put there by BakaLoader. His
    /// application log carried the line eight times over two days at Information, and the
    /// window stood the same bar up again after every launch: a sentence about a decision he
    /// had already taken, waiting to be closed, for ever. In his words: "The 'Bepinex was
    /// left as it was' disclaimer every launch is really not necessary."
    /// </para>
    /// <para>
    /// Nothing in the suite had an opinion about any of that. A refusal is recorded correctly
    /// on every single window, the DTO carries it correctly, and the page draws it correctly:
    /// each piece is right on its own and the thing they add up to is a nuisance. So the rule
    /// held here is about the SECOND time a fact arrives, which is the reading no per-call
    /// test can reach.
    /// </para>
    /// </summary>
    public class BepInExNoticeOnceTests : IDisposable
    {
        // ------------------------------------------------------------------ what names a fact

        /// <summary>
        /// Two windows over an unchanged install name the same fact, whatever the clock says.
        /// This is the whole fix in one assertion: a refusal that moved with the clock would
        /// name a new fact every few hours and the bar would come back exactly as it did.
        /// </summary>
        [Fact]
        public void A_refusal_on_the_same_rule_names_the_same_fact_hours_later()
        {
            var first = BepInExNoticeKey.Of("refused", "foreign", "5.4.2351", "5.4.19.0",
                new DateTime(2026, 9, 29, 2, 11, 0, DateTimeKind.Utc));
            var later = BepInExNoticeKey.Of("refused", "foreign", "5.4.2351", "5.4.19.0",
                new DateTime(2026, 9, 29, 12, 24, 0, DateTimeKind.Utc));

            Assert.Equal(first, later);
            Assert.True(BepInExNoticeKey.Seen(later, first));
        }

        /// <summary>A different reason is a different fact and is said once, properly.</summary>
        [Fact]
        public void Another_reason_is_another_fact()
        {
            var when = new DateTime(2026, 9, 29, 2, 11, 0, DateTimeKind.Utc);
            var foreign = BepInExNoticeKey.Of("refused", "foreign", "5.4.2351", "5.4.19.0", when);
            var soaking = BepInExNoticeKey.Of("refused", "soak", "5.4.2351", "5.4.19.0", when);

            Assert.NotEqual(foreign, soaking);
            Assert.False(BepInExNoticeKey.Seen(soaking, foreign));
        }

        /// <summary>
        /// A new pack soaking is a new fact. The same soak reason on the SAME pack is not, so
        /// a host who closed it does not read it again every night until the 72 hours pass.
        /// </summary>
        [Fact]
        public void A_new_pack_soaking_is_news_and_the_same_one_is_not()
        {
            var monday = new DateTime(2026, 9, 26, 2, 11, 0, DateTimeKind.Utc);
            var tuesday = new DateTime(2026, 9, 27, 2, 11, 0, DateTimeKind.Utc);

            var soak2351a = BepInExNoticeKey.Of("refused", "soak", "5.4.2351", "5.4.19.0", monday);
            var soak2351b = BepInExNoticeKey.Of("refused", "soak", "5.4.2351", "5.4.19.0", tuesday);
            var soak2352 = BepInExNoticeKey.Of("refused", "soak", "5.4.2352", "5.4.19.0", tuesday);

            Assert.Equal(soak2351a, soak2351b);
            Assert.NotEqual(soak2351a, soak2352);
        }

        /// <summary>
        /// A write and a heal are EVENTS: each new one is worth its own notice, so the moment
        /// is part of what names them and closing yesterday's does not close today's.
        /// </summary>
        [Theory]
        [InlineData("written")]
        [InlineData("healed")]
        public void An_event_is_named_by_its_moment(string outcome)
        {
            var first = BepInExNoticeKey.Of(outcome, null, null, null,
                new DateTime(2026, 9, 29, 2, 11, 0, DateTimeKind.Utc));
            var second = BepInExNoticeKey.Of(outcome, null, null, null,
                new DateTime(2026, 9, 30, 2, 15, 0, DateTimeKind.Utc));

            Assert.NotEqual(first, second);
            Assert.False(BepInExNoticeKey.Seen(second, first));
        }

        /// <summary>
        /// A failure is not an event. A scheduled window on an install whose loader file is
        /// held open by something else throws the same refusal every time it runs, and the
        /// first cut of this keyed it on the moment: a new fact every restart, and the bar to
        /// close after each one, which is the complaint the refusal key was written to fix.
        /// </summary>
        [Fact]
        public void The_same_failure_twice_is_the_same_fact()
        {
            var first = BepInExNoticeKey.Of("failed", "bepinex.locked", "5.4.2351", "5.4.19.0",
                new DateTime(2026, 9, 29, 2, 11, 0, DateTimeKind.Utc));
            var later = BepInExNoticeKey.Of("failed", "bepinex.locked", "5.4.2351", "5.4.19.0",
                new DateTime(2026, 9, 29, 14, 40, 0, DateTimeKind.Utc));

            Assert.Equal(first, later);
            Assert.True(BepInExNoticeKey.Seen(later, first),
                "a window that failed the same way stood the bar up again");
            Assert.DoesNotContain("2026-09-29T", first, StringComparison.Ordinal);
        }

        /// <summary>
        /// And nothing is lost by it. A different refusal id is a different fact, and so is
        /// the same id against a different pack or a different loader on disk, so a host who
        /// closed the locked-file bar still reads the one about the pack that went away.
        /// </summary>
        [Fact]
        public void A_failure_on_another_reason_is_news()
        {
            var when = new DateTime(2026, 9, 29, 2, 11, 0, DateTimeKind.Utc);
            var locked = BepInExNoticeKey.Of("failed", "bepinex.locked", "5.4.2351", "5.4.19.0", when);
            var gone = BepInExNoticeKey.Of("failed", "bepinex.repairPackGone", "5.4.2351", "5.4.19.0", when);
            var newerPack = BepInExNoticeKey.Of("failed", "bepinex.locked", "5.4.2352", "5.4.19.0", when);
            var newerHere = BepInExNoticeKey.Of("failed", "bepinex.locked", "5.4.2351", "5.4.23.5", when);

            Assert.NotEqual(locked, gone);
            Assert.NotEqual(locked, newerPack);
            Assert.NotEqual(locked, newerHere);
            Assert.False(BepInExNoticeKey.Seen(gone, locked));
            Assert.False(BepInExNoticeKey.Seen(newerPack, locked));
            Assert.False(BepInExNoticeKey.Seen(newerHere, locked));
        }

        /// <summary>Nothing recorded names no fact, and no fact is ever "already read".</summary>
        [Fact]
        public void Nothing_recorded_is_never_seen()
        {
            Assert.Null(BepInExNoticeKey.Of(null, "foreign", null, null, DateTime.UtcNow));
            Assert.False(BepInExNoticeKey.Seen(null, "refused|foreign|||"));
            Assert.False(BepInExNoticeKey.Seen("refused|foreign|||", null));
            Assert.False(BepInExNoticeKey.Seen("refused|foreign|||", ""));
        }

        // ------------------------------------------------- what the page is actually sent

        private static JObject Dto(object outcome, string seenKey)
        {
            var built = typeof(BlendWindow)
                .GetMethod("BepInExUnattendedDto",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, new[] { outcome, seenKey });
            return built == null ? null : JObject.Parse(JsonConvert.SerializeObject(built));
        }

        private static object OutcomeOf(string profile, BepInExInstallResult result, DateTime whenUtc)
            => typeof(BlendWindow)
                .GetMethod("BepInExOutcomeOf",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, new object[] { profile, result, whenUtc });

        private static BepInExInstallResult RefusedForeign() => new()
        {
            Skipped = true,
            SkipReason = BepInExSkipReason.Foreign,
            Version = "5.4.2351",
            PreviousCoreVersion = "5.4.19.0",
        };

        /// <summary>
        /// The owner's own sequence: a window declines on foreign, he closes the notice, and
        /// the next window declines on foreign again. The second answer has to arrive already
        /// marked read, or the page stands the same sentence up after every launch.
        /// </summary>
        [Fact]
        public void The_second_window_on_the_same_refusal_arrives_already_read()
        {
            var first = OutcomeOf("Second Sunset", RefusedForeign(),
                new DateTime(2026, 9, 29, 2, 11, 0, DateTimeKind.Utc));

            var before = Dto(first, null);
            Assert.False(before.Value<bool>("seen"));
            Assert.Equal("foreign", before.Value<string>("reason"));
            Assert.True(before.Value<bool>("leftAsItWas"));

            // bepinex.noticeSeen writes the key of the notice that was closed.
            var closed = before.Value<string>("key");
            Assert.False(string.IsNullOrEmpty(closed));

            var next = OutcomeOf("Second Sunset", RefusedForeign(),
                new DateTime(2026, 9, 29, 12, 24, 0, DateTimeKind.Utc));

            var after = Dto(next, closed);
            Assert.True(after.Value<bool>("seen"),
                "the next restart window stood the same fact up again, which is the whole complaint");
            Assert.Equal(closed, after.Value<string>("key"));
        }

        /// <summary>A pack that has moved on is a fact nobody has read.</summary>
        [Fact]
        public void A_new_pack_soaking_after_a_closed_soak_is_not_read()
        {
            var when = new DateTime(2026, 9, 26, 2, 11, 0, DateTimeKind.Utc);
            var soaking = new BepInExInstallResult
            {
                Skipped = true,
                SkipReason = BepInExSkipReason.Soak,
                Version = "5.4.2351",
                PreviousCoreVersion = "5.4.19.0",
                EligibleUtc = when.AddHours(72),
            };

            var closed = Dto(OutcomeOf("Second Sunset", soaking, when), null).Value<string>("key");

            var newer = new BepInExInstallResult
            {
                Skipped = true,
                SkipReason = BepInExSkipReason.Soak,
                Version = "5.4.2352",
                PreviousCoreVersion = "5.4.19.0",
                EligibleUtc = when.AddDays(2),
            };

            var after = Dto(OutcomeOf("Second Sunset", newer, when.AddDays(1)), closed);
            Assert.False(after.Value<bool>("seen"), "a pack the host has never been told about was silenced");
        }

        /// <summary>
        /// A write after a write: the second one is its own event and has to be reported, even
        /// though the host closed the first. This is the line a key without the moment in it
        /// would have crossed.
        /// </summary>
        [Fact]
        public void A_second_write_is_its_own_notice()
        {
            var wrote = new BepInExInstallResult
            {
                Installed = true,
                Replaced = true,
                Version = "5.4.2352",
                PreviousVersion = "5.4.2351",
                BackupStamp = "20260929-021100",
            };

            var firstKey = Dto(OutcomeOf("Second Sunset", wrote,
                new DateTime(2026, 9, 29, 2, 11, 0, DateTimeKind.Utc)), null).Value<string>("key");

            var again = Dto(OutcomeOf("Second Sunset", wrote,
                new DateTime(2026, 9, 30, 2, 15, 0, DateTimeKind.Utc)), firstKey);

            Assert.False(again.Value<bool>("seen"), "the next night's write was never reported");
            Assert.False(again.Value<bool>("leftAsItWas"));
        }

        /// <summary>
        /// An adoption that moved nothing records NO outcome, which is how it was before and
        /// has to stay: there is no sentence to say and a refusal an earlier window raised has
        /// stopped being true either way.
        /// </summary>
        [Fact]
        public void An_adoption_that_changed_nothing_records_no_fact()
        {
            var nothing = new BepInExInstallResult { Installed = true, NothingChanged = true };
            Assert.Null(OutcomeOf("Second Sunset", nothing, DateTime.UtcNow));
            Assert.Null(Dto(null, "refused|foreign|||"));
        }

        // ------------------------------------------------------- and it survives a restart

        /// <summary>
        /// The key is remembered on disk, or the whole thing lasts until the app is closed and
        /// the owner reads the same sentence at nine the next morning.
        /// </summary>
        [Fact]
        public void The_closed_notice_round_trips_through_the_preferences()
        {
            const string key = "refused|foreign|5.4.2351|5.4.19.0|";

            var prefs = UserPreferences.GetDefault();
            Assert.Null(prefs.BepInExNoticeSeenKey);

            prefs.BepInExNoticeSeenKey = key;

            var reloaded = UserPreferences.FromFile(prefs.ToFile());
            Assert.Equal(key, reloaded.BepInExNoticeSeenKey);

            // And through the document itself, because the on-disk names are the contract.
            var document = JsonConvert.SerializeObject(prefs.ToFile());
            Assert.Contains("\"bepInExNoticeSeenKey\":", document, StringComparison.Ordinal);
            var back = UserPreferences.FromFile(
                JsonConvert.DeserializeObject<UserPreferencesFile>(document));
            Assert.Equal(key, back.BepInExNoticeSeenKey);
        }

        /// <summary>
        /// A document written by 1.2.5, which has no such key at all, still loads: the whole
        /// point of every value on that model being nullable.
        /// </summary>
        [Fact]
        public void A_document_from_before_this_key_existed_still_loads()
        {
            var older = JsonConvert.DeserializeObject<UserPreferencesFile>(
                "{\"bepInExMaintained\":true,\"bepInExMaintenanceAsked\":true}");

            var prefs = UserPreferences.FromFile(older);
            Assert.True(prefs.BepInExMaintained);
            Assert.Null(prefs.BepInExNoticeSeenKey);
        }

        /// <summary>
        /// The call that closes the notice writes the key down. A source gate, because the RPC
        /// table lives on a Form: what it holds is that the write happens at all, which is the
        /// half that makes a closed notice stay closed past the next restart window.
        /// </summary>
        [Fact]
        public void The_notice_seen_call_writes_the_key_it_closed()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var at = bridge.IndexOf("RegisterRpc(\"bepinex.noticeSeen\"", StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer answers bepinex.noticeSeen");

            var body = bridge.Substring(at, 900);
            Assert.Contains("_bepInExLastUnattended?.Key", body, StringComparison.Ordinal);
            Assert.Contains("prefs.BepInExNoticeSeenKey = closed;", body, StringComparison.Ordinal);
            Assert.Contains("UserPrefsProvider.Mutate", body, StringComparison.Ordinal);

            // And the answer the page reads carries both halves.
            Assert.Contains("key = last.Key,", bridge, StringComparison.Ordinal);
            Assert.Contains("seen = Tools.BepInExNoticeKey.Seen(last.Key, seenKey),",
                bridge, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------- and the log follows

        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "bakaloader-notice-" + Guid.NewGuid().ToString("N"));

        private string BaseDir;
        private string BaseExe;

        public BepInExNoticeOnceTests()
        {
            BaseDir = Path.Combine(Root, "common", "Valheim dedicated server");
            Directory.CreateDirectory(BaseDir);
            BaseExe = Path.Combine(BaseDir, "valheim_server.exe");
            File.WriteAllText(BaseExe, "not really an executable");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }

        /// <summary>The application logger without its file sink, keeping the level of each line.</summary>
        private sealed class Remembering : IApplicationLogger
        {
            public List<(LogEventLevel Level, string Text)> Lines { get; } = new();

            public event Action<string> LogReceived;

            public IEnumerable<string> LogBuffer => Lines.Select(l => l.Text);

            public void Write(LogEvent logEvent)
            {
                if (logEvent == null) return;
                var text = logEvent.RenderMessage();
                Lines.Add((logEvent.Level, text));
                LogReceived?.Invoke(text);
            }

            public bool IsEnabled(LogEventLevel level) => true;

            public int Count(LogEventLevel level, string fragment)
                => Lines.Count(l => l.Level == level
                    && l.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        private static byte[] Pack(string version)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                const string root = "BepInExPack_Valheim/";
                void Put(string path, string body)
                {
                    using var stream = zip.CreateEntry(path).Open();
                    var bytes = Encoding.UTF8.GetBytes(body);
                    stream.Write(bytes, 0, bytes.Length);
                }

                Put(root + "winhttp.dll", "the doorstop proxy " + version);
                Put(root + "doorstop_config.ini", "[General]\nenabled = true\n");
                Put(root + ".doorstop_version", "4.4.0");
                Put(root + "changelog.txt", "changelog " + version);
                Put(root + "BepInEx/core/BepInEx.dll", "core " + version);
                Put(root + "BepInEx/config/BepInEx.cfg", "[Logging]\nshipped default\n");
                Put("manifest.json",
                    "{\"name\":\"BepInExPack_Valheim\",\"version_number\":\"" + version + "\"}");
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// A service whose listing can be changed between calls, so the same window can be run
        /// twice on one refusal and then once on another.
        /// </summary>
        private sealed class Listings
        {
            public string Version = "5.4.2350";
            public bool Deprecated;
            public long? FileSize;

            public ThunderstorePackage Package() => new()
            {
                Namespace = BepInExService.DefaultPackageOwner,
                Name = BepInExService.DefaultPackageName,
                IsDeprecated = Deprecated,
                Latest = new ThunderstorePackageVersion
                {
                    VersionNumber = Version,
                    FileSize = FileSize,
                },
            };
        }

        private (BepInExService Service, Remembering Log, Listings Listing) Build()
        {
            var listing = new Listings();
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Pack(listing.Version)) });

            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore.Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(() => new ThunderstoreLiveLookup { Answered = true, Package = listing.Package() });
            thunderstore.Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(() => listing.Package());

            var log = new Remembering();
            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());
            return (new BepInExService(thunderstore.Object, provider, isolation, log), log, listing);
        }

        private static IEnumerable<BepInExProfileInstall> Nobody() => Array.Empty<BepInExProfileInstall>();

        /// <summary>
        /// The same refusal, window after window, is said out loud ONCE. Every repeat goes to
        /// Debug, and a change of reason is news again.
        /// <para>
        /// This drives the real service. The owner's log had eight identical Information lines
        /// about one unchanged install over two days, and on a box that leaves a world up all
        /// evening a line like that is most of what the log has to say.
        /// </para>
        /// </summary>
        [Fact]
        public async Task The_same_pack_refusal_is_said_out_loud_once_per_change()
        {
            const string said = "was left for the host to decide on";
            var (service, log, listing) = Build();

            // An install to update, written by hand so the refusal rule is the one under test.
            var installed = await service.InstallAsync(BaseExe, null, Nobody());
            Assert.True(installed.Installed);

            // A newer pack whose listing names no size: an unattended window refuses it.
            listing.Version = "5.4.2351";
            listing.FileSize = null;

            for (var window = 0; window < 4; window++)
            {
                var result = await service.UpdateAsync(BaseExe, Nobody(),
                    options: BepInExWriteOptions.Window);
                Assert.True(result.Skipped, "window " + (window + 1) + " did not decline");
                Assert.Equal(BepInExSkipReason.Unverified, result.SkipReason);
            }

            Assert.Equal(1, log.Count(LogEventLevel.Information, said));
            Assert.Equal(3, log.Count(LogEventLevel.Debug, said));

            // A DIFFERENT reason on a different pack is news, and says so out loud.
            listing.Version = "5.4.2352";
            listing.Deprecated = true;

            var changed = await service.UpdateAsync(BaseExe, Nobody(),
                options: BepInExWriteOptions.Window);
            Assert.True(changed.Skipped);
            Assert.Equal(BepInExSkipReason.Deprecated, changed.SkipReason);

            Assert.Equal(2, log.Count(LogEventLevel.Information, said));
            Assert.Equal(3, log.Count(LogEventLevel.Debug, said));
        }

        /// <summary>
        /// The bridge's own line about a window, which cannot be driven from here because it
        /// lives on a Form: the gate is that it asks the same question before it picks a level,
        /// and that the WORDING did not move when the level did.
        /// </summary>
        [Fact]
        public void The_restart_window_line_goes_quiet_on_a_repeat_too()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];

            Assert.Contains(
                "if (BepInExWindowStateChanged(profile, \"refused:\" + (result.SkipReason ?? \"\")))",
                bridge, StringComparison.Ordinal);

            // The same sentence at both levels: a host reading the log sees the same words
            // whether it is the first window or the fortieth.
            var line = "\"BepInEx for profile {0} was left as it was at the restart window ({1}).\"";
            Assert.Equal(2, CountOf(bridge, line));

            var at = bridge.IndexOf("private bool BepInExWindowStateChanged(", StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer asks whether a window's ending has changed");
        }

        private static int CountOf(string text, string needle)
        {
            var found = 0;
            for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                found++;
            return found;
        }
    }
}
