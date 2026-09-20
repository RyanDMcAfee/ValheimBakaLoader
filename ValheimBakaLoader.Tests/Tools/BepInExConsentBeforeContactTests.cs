using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The answer is read BEFORE anything is asked of Thunderstore.
    /// <para>
    /// In 1.2.0 the restart window awaited the pack version and read the consent on the next
    /// line, so a host who said NO, and a host who had never been asked at all, still had their
    /// machine reach thunderstore.io on every single restart. Nothing downstream of the answer
    /// could ever have used the reply, so the request was not buying anything either.
    /// </para>
    /// <para>
    /// The proof is a recording transport with a real Thunderstore client on top of it and a
    /// real BepInEx service on top of that: an empty list of addresses is a stronger statement
    /// than any flag the code could set. The last test here asks with a yes, so the recorder is
    /// known to be live rather than merely quiet.
    /// </para>
    /// </summary>
    public class BepInExConsentBeforeContactTests : BaseTest
    {
        // ------------------------------------------------------------------ the rule, driven

        /// <summary>
        /// Unanswered is its own state and it is not a yes: the preference defaults to ON, so a
        /// host upgrading from 1.1.x reads as "maintained" before anybody has asked them
        /// anything, and a profile that auto-starts never reaches the question at all.
        /// </summary>
        [Theory]
        [InlineData(false, true, "never asked, and the switch is at its default of on")]
        [InlineData(false, false, "never asked, switch off")]
        [InlineData(true, false, "asked, and the answer was no")]
        public async Task Without_a_yes_the_restart_window_asks_thunderstore_nothing(
            bool asked, bool maintained, string why)
        {
            var (service, handler) = Build();

            var plan = await BepInExUnattended.PlanAsync(
                BepInExConsent.Effective(maintained, asked),
                () => throw new InvalidOperationException("the install must not even be read: " + why),
                package => service.LatestVersionAsync(package),
                () => throw new InvalidOperationException("nothing else may be asked either"));

            Assert.Equal(BepInExUnattendedAction.Skip, plan.Action);
            Assert.Null(plan.Latest);
            Assert.Null(plan.Status);
            Assert.Equal(0, handler.Count);
            Assert.Empty(handler.Hosts);
        }

        /// <summary>
        /// And with a yes it really does ask, which is what makes the empty lists above mean
        /// something: the same transport, the same client, the same call.
        /// </summary>
        [Fact]
        public async Task With_a_yes_the_site_is_asked_and_the_recorder_catches_it()
        {
            var (service, handler) = Build();

            var plan = await BepInExUnattended.PlanAsync(
                BepInExConsent.Effective(maintained: true, asked: true),
                () => Installed("5.4.2333"),
                package => service.LatestVersionAsync(package),
                () => false);

            Assert.True(handler.Count > 0, "the site was never asked, so the zero above proves nothing");
            Assert.NotNull(plan.Status);
        }

        // ------------------------------------------------------------------ the window's own order

        /// <summary>
        /// The window goes through the rule that carries the order, and the consent it hands in
        /// is read before it. A gate on the source, because the window itself cannot be built
        /// here, and because what this guards against is the two lines swapping back.
        /// </summary>
        [Fact]
        public void The_restart_window_reads_the_answer_before_it_asks_the_site()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var at = bridge.IndexOf("private async Task ApplyBepInExUpdateAsync(string profile)", StringComparison.Ordinal);
            Assert.True(at > 0, "the unattended BepInEx step is gone");

            var body = bridge.Substring(at, Math.Min(2600, bridge.Length - at));

            var consent = body.IndexOf("Tools.BepInExConsent.Effective(", StringComparison.Ordinal);
            var site = body.IndexOf("LatestVersionAsync(", StringComparison.Ordinal);

            Assert.True(consent > 0, "the window no longer reads the answer at all");
            Assert.True(site > 0, "the window no longer asks the site at all");
            Assert.True(consent < site,
                "the answer has to be read before the site is asked, or every restart contacts "
                + "Thunderstore whatever the host said");

            // and the ask is inside the rule that will not run it without a yes
            Assert.Contains("Tools.BepInExUnattended.PlanAsync(", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// One step DOES run ahead of the answer, and it has to: finishing a write that stopped
        /// part way is putting a copy that is already on disk back, and the moment this server
        /// is down is the only moment it can happen. It reaches nothing, which is what makes it
        /// safe to leave in front, and this holds it to that.
        /// </summary>
        [Fact]
        public void The_one_step_that_runs_before_the_answer_reaches_nothing()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];

            var window = bridge.IndexOf("private async Task ApplyBepInExUpdateAsync(string profile)", StringComparison.Ordinal);
            var heal = bridge.IndexOf("HealInterruptedBepInExWrite(baseExe, installs);", window, StringComparison.Ordinal);
            var consent = bridge.IndexOf("Tools.BepInExConsent.Effective(", window, StringComparison.Ordinal);
            Assert.True(heal > 0 && consent > heal, "the repair no longer runs ahead of the answer");

            // and the method it calls asks the site nothing: it renames what is already there
            var service = AppSourceTree.Files()["BepInExService.cs"];
            var at = service.IndexOf("public BepInExInstallResult HealInterruptedWrite(", StringComparison.Ordinal);
            Assert.True(at > 0, "the repair is gone");
            var method = service.Substring(at, Math.Min(3000, service.Length - at));
            var ends = method.IndexOf("\n        }\n", StringComparison.Ordinal);
            if (ends > 0) method = method.Substring(0, ends);

            Assert.DoesNotContain("await", method, StringComparison.Ordinal);
            Assert.DoesNotContain("Thunderstore", method, StringComparison.Ordinal);
            Assert.DoesNotContain("Http", method, StringComparison.Ordinal);
        }

        /// <summary>
        /// The repair of files that have GONE still needs the answer, exactly as it did before.
        /// This is the one path that writes without a new version behind it, and a host who said
        /// no must not have it run for them either.
        /// </summary>
        [Fact]
        public void A_repair_under_a_trusted_note_still_needs_the_answer()
        {
            Assert.Equal(
                BepInExUnattendedAction.Skip,
                BepInExUnattended.Decide(
                    consentEffective: false, installed: false, installedVersion: "5.4.2350",
                    latestVersion: "5.4.2350", otherServersRunning: false, filesMissing: true));

            Assert.Equal(
                BepInExUnattendedAction.Apply,
                BepInExUnattended.Decide(
                    consentEffective: true, installed: false, installedVersion: "5.4.2350",
                    latestVersion: "5.4.2350", otherServersRunning: false, filesMissing: true));
        }

        // ------------------------------------------------------------------ plumbing

        private static BepInExStatus Installed(string packVersion) => new()
        {
            BaseFolder = Path.Combine(Path.GetTempPath(), "not-read-here"),
            Installed = true,
            MaintainedByBakaLoader = true,
            PackVersion = packVersion,
            Package = BepInExService.DefaultPackage,
        };

        /// <summary>
        /// A real BepInEx service over a real Thunderstore client over a recording transport, so
        /// the question "was anything asked" is answered by the wire and not by a mock.
        /// </summary>
        private (BepInExService Service, RecordingHttpHandler Handler) Build()
        {
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Listing(), Encoding.UTF8, "application/json"),
                });

            var context = new RestClientContext(GetService<Serilog.ILogger>(), provider);
            var thunderstore = new ThunderstoreClient(context);
            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());

            return (new BepInExService(thunderstore, provider, isolation, Mock.Of<IApplicationLogger>()),
                provider.Handler);
        }

        /// <summary>The package page's own answer, which is the first address the service asks.</summary>
        private static string Listing() =>
            "{\"namespace\":\"" + BepInExService.DefaultPackageOwner + "\","
            + "\"name\":\"" + BepInExService.DefaultPackageName + "\","
            + "\"latest\":{\"version_number\":\"5.4.2350\",\"is_active\":true}}";
    }
}
