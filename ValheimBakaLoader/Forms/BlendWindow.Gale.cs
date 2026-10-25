using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// Following a Gale synced profile: the eight RPCs the Mods hall's box, the condition bar row
    /// and the approval dialog talk to. The two the dialog's configs part adds are in
    /// BlendWindow.GaleConfigs.cs.
    /// <para>
    /// The page never sees the service. It asks gale.check, this side reads the profile's meta
    /// through <see cref="IGaleSyncClient"/> on a worker, reads the realm's installed mods the
    /// way the Mods hall does, and hands back the comparison and whether the row is due. Only
    /// gale.check reaches the network for the meta, and only for a realm with an id set; the page
    /// calls it at the three moments the spec names (after the first frame, when the Mods hall
    /// opens, and on Check now) and at no other. gale.configs fetches the profile's zip, and only
    /// when the host opens the dialog's configs part or presses its Refresh.
    /// </para>
    /// <para>
    /// The installs themselves are not here. The dialog runs the Mods hall's own add, one mod at
    /// a time, through mods.addFromUrl with the version pinned in the address, so the download,
    /// the checks on it and every failure sentence are the ones that road already has. Nothing
    /// in this file removes a mod, and nothing in it can.
    /// </para>
    /// <para>
    /// A realm can follow one of its own test copies in place of a Gale profile (1.3.0,
    /// FollowTestCopy). The same RPCs then read the copy's folders on this PC through
    /// <see cref="TestCopyGaleFeed"/> (picked in BlendWindow.TestCopyFeed.cs), every check, read
    /// and approval is held to the follow's key rather than to the Gale id, and the copy's
    /// approval and unticks are kept apart from the Gale profile's. Its mods go in through
    /// testcopy.pullMods, in that file, not through the add.
    /// </para>
    /// </summary>
    public partial class BlendWindow
    {
        /// <summary>The one reader of Gale's sync service. Resolved in InitializeBridge.</summary>
        private IGaleSyncClient GaleClient;

        /// <summary>
        /// What the last check of each realm came back with. Held in memory only: the stored
        /// part of a follow is the three fields on the profile, and a restart simply checks again.
        /// </summary>
        private readonly ConcurrentDictionary<string, GaleCheckMemo> GaleMemos =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A check already out for a realm, so a second ask joins it rather than sending a second request.</summary>
        private readonly ConcurrentDictionary<string, Task<GaleCheckMemo>> GaleChecksInFlight =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object GaleLock = new();

        private long GaleSeq;

        /// <summary>
        /// One check, as it came back. The comparison is made again from this every time the
        /// page asks, against the unticks and the approval stamp on disk at that moment, so an
        /// untick or a dismissal shows at once without a second trip.
        /// </summary>
        internal sealed class GaleCheckMemo
        {
            /// <summary>Which check this is. The page hands it back with a dismissal so a stamp is never taken from a newer check than the one the host was shown.</summary>
            public long Seq { get; init; }

            /// <summary>The follow's key the check was made for (the Gale id, or "testcopy:" and the copy's name). A memo for any other key is stale.</summary>
            public string ProfileId { get; init; }

            public DateTime CheckedUtc { get; init; }

            public GaleCheckResult Result { get; init; }

            /// <summary>The realm's mods as the scan read them, as name and version. Null when the check failed.</summary>
            public IReadOnlyList<(string Name, string Version)> Installed { get; init; }

            /// <summary>
            /// The same scan with what the "on this server only" list needs beside each name: the
            /// mod's own FullName, which the switch looks it up by, and whether it is switched off.
            /// Null when the check failed.
            /// </summary>
            public IReadOnlyList<GaleInstalledMod> Mods { get; init; }
        }

        /// <summary>
        /// One mod the scan read, as the Gale check holds it. <see cref="Name"/> is what the diff
        /// compares by (the manifest's FullName, or the bare ModName for a folder with no author);
        /// <see cref="FullName"/> is the scan's own, the one mods.disable and mods.enable look a mod
        /// up by. They differ for an author-less folder ("-Name" against "Name"), and a switch sent
        /// with the diff's name would find nothing. <see cref="Hash"/> is a hash of the mod's files,
        /// read only when the follow is a test copy, whose lines carry one too; null otherwise.
        /// </summary>
        internal readonly record struct GaleInstalledMod(string Name, string Version, string FullName, bool Off, string Hash = null);

        private void RegisterGaleRpcs()
        {
            // What the box draws: the id, the last approval and the last check, with no trip.
            RegisterRpc("gale.get", p => Task.FromResult<object>(GaleState(GaleProfileOf(p))));

            // Takes a pasted id. A bad one is refused with its one sentence before anything is
            // fetched or stored; a blank one is the same as Forget. A different id is a
            // different profile, so the last approval and the unticks of the old one go.
            RegisterRpc("gale.set", p =>
            {
                var profile = GaleProfileOf(p);
                var check = GaleProfileId.Check(p?.Value<string>("id"));
                if (!check.Ok && !check.Blank)
                    return Task.FromResult<object>(new { ok = false, sentenceId = check.SentenceId, state = GaleState(profile) });

                var prefs = LoadGalePrefs(profile);
                if (prefs == null)
                    return Task.FromResult<object>(new { ok = false, sentenceId = GaleNoRealmSentence, state = GaleState(profile) });

                if (check.Blank)
                {
                    ForgetGale(prefs);
                }
                else if (!string.Equals(prefs.GaleProfileId, check.Id, StringComparison.Ordinal) || prefs.FollowsTestCopy())
                {
                    if (!string.Equals(prefs.GaleProfileId, check.Id, StringComparison.Ordinal))
                    {
                        prefs.GaleProfileId = check.Id;
                        prefs.GaleLastUpdatedAt = null;
                        prefs.GaleUnticked = new List<string>();
                        prefs.GaleNotAvailable = new List<string>();
                        prefs.GaleConfigUnticked = new List<string>();
                    }
                    // A code pasted is the host choosing a Gale profile, so a test copy the
                    // realm took its mods from is let go of.
                    prefs.ForgetTestCopyFollow();
                    ServerPrefsProvider.SavePreferences(prefs);
                    GaleMemos.TryRemove(profile, out _);
                    GaleConfigMemos.TryRemove(profile, out _);
                }

                return Task.FromResult<object>(new { ok = true, sentenceId = (string)null, state = GaleState(profile) });
            });

            // Stops following: the id, the last approval and every untick go, and nothing is
            // asked of the service again for this realm. The mods it installed stay. A test copy
            // the realm follows is let go of too (testcopy.follow with no copy lets go of the
            // copy alone).
            RegisterRpc("gale.forget", p =>
            {
                var profile = GaleProfileOf(p);
                var prefs = LoadGalePrefs(profile);
                if (prefs != null) ForgetGale(prefs);
                GaleMemos.TryRemove(profile, out _);
                GaleConfigMemos.TryRemove(profile, out _);
                return Task.FromResult<object>(GaleState(profile));
            });

            // The check. The trip and the scan run on a worker, so the window never waits on
            // the service; a second ask while one is out joins it. A failure of any kind comes
            // back as ok false with one sentence for the status line and never raises the row.
            RegisterRpc("gale.check", async p => await GaleCheckAsync(GaleProfileOf(p)));

            // The host waved the row away, or an install finished: the profile's updatedAt as
            // the check they were shown read it is stored, and the row stays down until the
            // profile moves again. The stamp is this side's copy of the service's text, never
            // one sent back by the page, because the page's message is parsed with dates turned
            // into dates and the text would not survive the trip.
            RegisterRpc("gale.dismiss", p =>
            {
                var profile = GaleProfileOf(p);
                var seq = p?.Value<long?>("seq");
                var prefs = LoadGalePrefs(profile);
                GaleMemos.TryGetValue(profile, out var memo);

                var stale = prefs == null || memo == null || seq == null || memo.Seq != seq
                    || memo.Result?.Ok != true
                    || !string.Equals(memo.ProfileId, prefs.GaleFollowKey(), StringComparison.Ordinal);
                if (!stale)
                {
                    prefs.SetGaleFeedLastApproved(memo.Result.Meta.UpdatedAt);
                    ServerPrefsProvider.SavePreferences(prefs);

                    // After an install the folder is not what the check read, so the counts the
                    // box shows are made against the disk as it is now. Read off the disk only:
                    // Gale is not asked again until the next of the three moments.
                    RereadGaleInstalled(profile, memo);
                }

                return Task.FromResult<object>(new { ok = !stale, state = GaleState(profile) });
            });

            // An install walk that stopped short or had a mod fail: the stamp is not taken, so
            // the row stays for what is left, but the mods that did go in are on the disk now
            // and must not be offered again. The last check is compared against the folder as
            // it is, with the same check and the same Seq, and nothing is asked of Gale.
            RegisterRpc("gale.rescan", p =>
            {
                var profile = GaleProfileOf(p);
                var prefs = LoadGalePrefs(profile);
                GaleMemos.TryGetValue(profile, out var memo);
                var fresh = prefs != null && memo != null && memo.Result?.Ok == true
                    && string.Equals(memo.ProfileId, prefs.GaleFollowKey(), StringComparison.Ordinal)
                    && RereadGaleInstalled(profile, memo);
                return Task.FromResult<object>(new { ok = fresh, state = GaleState(profile) });
            });

            // The ticks in the approval dialog. A name unticked is remembered until the host
            // ticks it again; a ticked one is taken off the list. Only "Namespace-Name" is kept,
            // or, while the realm follows a test copy, a mod folder's own name; and the copy's
            // unticks are its own list, so the Gale profile's are left as they were.
            RegisterRpc("gale.untick", p =>
            {
                var profile = GaleProfileOf(p);
                var prefs = LoadGalePrefs(profile);
                if (prefs == null) return Task.FromResult<object>(new { ok = false, state = GaleState(profile) });

                var local = prefs.FollowsTestCopy();
                var off = Names(p?["unticked"], local);
                var on = Names(p?["ticked"], local);
                var kept = ServerPreferences.NormalizeUnticked(prefs.GaleFeedUnticked())
                    .Where(n => !on.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .Concat(off)
                    .ToList();
                prefs.SetGaleFeedUnticked(kept);
                ServerPrefsProvider.SavePreferences(prefs);

                return Task.FromResult<object>(new { ok = true, state = GaleState(profile) });
            });

            // BepInEx first, when the server has none and the profile carries the pack. The
            // approval dialog calls this before its first mod. The write is the Mods hall's own
            // (the one bepinex.install makes), and the Install press is the host's answer: a
            // question nobody has answered yet is answered yes, the way the condition bar's Yes
            // answers it, while a No the host gave earlier stands and this one write still goes
            // ahead because they approved it here. Held to the realm on screen, to a stopped
            // server, and to a last check that really lists the pack.
            RegisterRpc("gale.bepinex", async p => await GaleBepInExAsync(GaleProfileOf(p)));

            // The profile's config files: the list with its previews, and the apply.
            RegisterGaleConfigRpcs();
        }

        /// <summary>The pack the dialog puts in first: the one BakaLoader installs everywhere else.</summary>
        internal static readonly string GaleBepInExPack =
            $"{BepInExService.DefaultPackageOwner}-{BepInExService.DefaultPackageName}";

        internal async Task<object> GaleBepInExAsync(string profile)
        {
            object Refused(string reason, string error, Dictionary<string, object> errorParams = null) =>
                new { ok = false, wrote = false, Reason = reason, Error = error, ErrorParams = errorParams, state = GaleState(profile) };

            if (!string.Equals(profile, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                return Refused("realmChanged", "The server on screen changed, so nothing was installed.");

            var pluginsDir = GetPluginsDirectoryFor(profile);
            var baseExe = GetCanonicalBaseServerExe(profile);
            if (string.IsNullOrWhiteSpace(pluginsDir) || string.IsNullOrWhiteSpace(baseExe))
                return Refused("noServerPath", "BepInEx plugins folder not found. Set a valid server .exe path first.");

            var holding = RunningSessionName(new[] { pluginsDir });
            if (holding != null)
                return Refused("serverRunning", $"Stop the server '{holding}' first. Nothing was installed.",
                    new Dictionary<string, object> { ["name"] = holding });

            if (BepInEx.Status(baseExe, pluginsDir, KnownInstalls()).Installed)
                return new { ok = true, wrote = false, Reason = (string)null, Error = (string)null, ErrorParams = (object)null, state = GaleState(profile) };

            if (!GaleCarriesBepInEx(profile))
                return Refused("bepinexNotInProfile", "The Gale profile does not carry BepInEx. Put BepInEx in first.");

            UserPrefsProvider.Mutate(prefs =>
            {
                if (!BepInExConsent.Unanswered(prefs.BepInExMaintenanceAsked)) return false;
                prefs.BepInExMaintained = true;
                prefs.BepInExMaintenanceAsked = true;
                return true;
            });

            var written = await WriteBepInExAsync(null, update: false);
            return new { ok = true, wrote = true, Reason = (string)null, Error = (string)null, ErrorParams = (object)null, bepinex = written, state = GaleState(profile) };
        }

        /// <summary>True when the last check of this realm's follow lists the BepInEx pack.</summary>
        private bool GaleCarriesBepInEx(string profile)
        {
            var prefs = LoadGalePrefs(profile);
            if (!GaleMemos.TryGetValue(profile ?? "", out var memo) || memo?.Result?.Ok != true) return false;
            if (!string.Equals(memo.ProfileId, prefs?.GaleFollowKey(), StringComparison.Ordinal)) return false;
            return GaleListsBepInEx(memo.Result.Meta);
        }

        private static bool GaleListsBepInEx(GaleProfileMeta meta) =>
            meta?.Mods?.Any(m => string.Equals(m?.Name?.Trim(), GaleBepInExPack, StringComparison.OrdinalIgnoreCase)) == true;

        /// <summary>
        /// Marks a pinned build of a mod as gone from Thunderstore on a realm's follow, or takes
        /// the mark off once that build went in. Keyed by version, so a profile that moves to a
        /// new version offers the mod again. Quiet on a realm that would not read.
        /// </summary>
        private void MarkGaleNotAvailable(string profile, string mod, string version, bool gone)
        {
            var prefs = LoadGalePrefs(profile);
            if (prefs == null || string.IsNullOrWhiteSpace(prefs.GaleProfileId)) return;
            var key = GaleNotAvailableKey(mod, version);
            var marks = ServerPreferences.NormalizeUnticked(prefs.GaleNotAvailable);
            var had = marks.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (gone == had) return;
            prefs.GaleNotAvailable = gone
                ? marks.Append(key).ToList()
                : marks.Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList();
            try { ServerPrefsProvider.SavePreferences(prefs); }
            catch (Exception e) { AppLogger?.Debug("Could not note {0} on the Gale follow of {1}: {2}", key, profile, e.Message); }
        }

        internal static string GaleNotAvailableKey(string mod, string version) => $"{mod?.Trim()}@{version?.Trim()}";

        /// <summary>The sentence a box gets when the realm it names is not a saved profile.</summary>
        internal const string GaleNoRealmSentence = "mods.gale.no_realm";

        private string GaleProfileOf(JObject p)
        {
            var named = p?.Value<string>("profile");
            return string.IsNullOrWhiteSpace(named) ? ActiveProfileName : named.Trim();
        }

        private ServerPreferences LoadGalePrefs(string profile)
        {
            try { return ServerPrefsProvider.LoadPreferences(profile); }
            catch (Exception e)
            {
                AppLogger?.Debug("Could not read the profile {0} for its Gale follow: {1}", profile, e.Message);
                return null;
            }
        }

        private void ForgetGale(ServerPreferences prefs)
        {
            prefs.GaleProfileId = null;
            prefs.GaleLastUpdatedAt = null;
            prefs.GaleUnticked = new List<string>();
            prefs.GaleNotAvailable = new List<string>();
            prefs.GaleConfigUnticked = new List<string>();
            prefs.ForgetTestCopyFollow();
            ServerPrefsProvider.SavePreferences(prefs);
            GaleMemos.TryRemove(prefs.ProfileName ?? "", out _);
            GaleConfigMemos.TryRemove(prefs.ProfileName ?? "", out _);
        }

        private static List<string> Names(JToken token, bool localFeed = false)
        {
            if (token is not JArray list) return new List<string>();
            return list.OfType<JValue>()
                .Where(v => v.Type == JTokenType.String)
                .Select(v => ((string)v)?.Trim())
                .Where(n => localFeed ? GaleProfileDiff.IsLocalName(n) : GaleProfileDiff.IsFullName(n))
                .ToList();
        }

        /// <summary>
        /// Runs one check for a realm and answers with its state. Never throws: the client
        /// answers every failure as an outcome, and the scan is wrapped.
        /// </summary>
        internal async Task<object> GaleCheckAsync(string profile)
        {
            var prefs = LoadGalePrefs(profile);
            var id = prefs?.GaleFollowKey();
            if (string.IsNullOrWhiteSpace(id)) return GaleState(profile);

            Task<GaleCheckMemo> run;
            lock (GaleLock)
            {
                if (!GaleChecksInFlight.TryGetValue(profile, out run))
                {
                    // Gale's service, or the test copy the realm follows read off this PC.
                    var client = GaleFeedFor(profile, prefs);
                    var pluginsDir = GetPluginsDirectoryFor(profile);
                    // Task.Run, not a bare call: the client's first await is behind a DNS
                    // lookup and a TLS handshake, and the scan is a walk of the disk. Neither
                    // belongs on the thread that draws the window.
                    run = Task.Run(() => RunGaleCheckAsync(client, id, pluginsDir));
                    GaleChecksInFlight[profile] = run;
                }
            }

            GaleCheckMemo memo;
            try
            {
                memo = await run;
            }
            finally
            {
                lock (GaleLock)
                {
                    if (GaleChecksInFlight.TryGetValue(profile, out var current) && current == run)
                        GaleChecksInFlight.TryRemove(profile, out _);
                }
            }

            // The host may have forgotten the profile, or pasted another, while this was out.
            // A check for an id the realm no longer follows is dropped rather than shown.
            var now = LoadGalePrefs(profile);
            if (memo != null && now != null && string.Equals(now.GaleFollowKey(), memo.ProfileId, StringComparison.Ordinal))
            {
                GaleMemos[profile] = new GaleCheckMemo
                {
                    Seq = Interlocked.Increment(ref GaleSeq),
                    ProfileId = memo.ProfileId,
                    CheckedUtc = memo.CheckedUtc,
                    Result = memo.Result,
                    Installed = memo.Installed,
                    Mods = memo.Mods,
                };
            }

            return GaleState(profile);
        }

        private async Task<GaleCheckMemo> RunGaleCheckAsync(IGaleSyncClient client, string id, string pluginsDir)
        {
            GaleCheckResult result;
            try
            {
                result = client == null
                    ? new GaleCheckResult { Outcome = GaleCheckOutcome.NetworkError, SentenceId = GaleSyncClient.NetworkErrorSentence }
                    : await client.GetMetaAsync(id).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client promises not to throw. A stand in that does is still one quiet line.
                result = new GaleCheckResult { Outcome = GaleCheckOutcome.NetworkError, SentenceId = GaleSyncClient.NetworkErrorSentence };
            }

            // A profile for another game is a status line, never a comparison and never a row.
            result = GaleProfileDiff.RefuseOtherGame(result);

            IReadOnlyList<GaleInstalledMod> installed = null;
            if (result?.Ok == true)
            {
                // Against a test copy the realm's own folders are hashed too, as the copy's are.
                installed = ReadInstalledForGale(pluginsDir, result.Meta?.LocalFeed == true);
                if (installed == null)
                {
                    // A folder that would not read is not an empty server: comparing against
                    // nothing would offer every mod in the profile as an add.
                    result = new GaleCheckResult { Outcome = GaleCheckOutcome.Malformed, SentenceId = GaleUnreadableSentence };
                }
            }

            return new GaleCheckMemo
            {
                ProfileId = id, CheckedUtc = DateTime.UtcNow, Result = result,
                Installed = Pairs(installed), Mods = installed,
            };
        }

        /// <summary>
        /// Puts back the memo for a realm with its installed list read off the disk again,
        /// keeping the check itself, its Seq and its time. Only the stored memo it was handed is
        /// replaced, so a newer check that landed meanwhile is left alone. False when the folder
        /// would not read, and the memo is then left as it was.
        /// </summary>
        private bool RereadGaleInstalled(string profile, GaleCheckMemo memo)
        {
            var installed = ReadInstalledForGale(GetPluginsDirectoryFor(profile), memo.Result?.Meta?.LocalFeed == true);
            if (installed == null) return false;
            return GaleMemos.TryUpdate(profile, new GaleCheckMemo
            {
                Seq = memo.Seq,
                ProfileId = memo.ProfileId,
                CheckedUtc = memo.CheckedUtc,
                Result = memo.Result,
                Installed = Pairs(installed),
                Mods = installed,
            }, memo);
        }

        private static IReadOnlyList<(string Name, string Version)> Pairs(IReadOnlyList<GaleInstalledMod> mods) =>
            mods?.Select(m => (m.Name, m.Version)).ToList();

        /// <summary>What the comparison reads: each mod's name, version and the hash of its files, when it was read.</summary>
        private static IEnumerable<(string Name, string Version, string Hash)> Triples(GaleCheckMemo memo) =>
            memo.Mods != null
                ? memo.Mods.Select(m => (m.Name, m.Version, m.Hash))
                : (memo.Installed ?? Array.Empty<(string Name, string Version)>()).Select(m => (m.Name, m.Version, (string)null));

        /// <summary>The status line when the realm's own mod folder could not be read.</summary>
        internal const string GaleUnreadableSentence = "mods.gale.check.unreadable";

        /// <summary>
        /// The realm's mods, read the way the Mods hall reads them: ModScanner's manifest name
        /// and version per folder. A realm with no plugins folder yet has no mods, which is an
        /// answer; a folder that is there and will not read is null, which is not. Against a test
        /// copy (<paramref name="localFeed"/>) each mod's version is read the way the copy's is
        /// and its files are hashed, so the two sides are read alike.
        /// </summary>
        private IReadOnlyList<GaleInstalledMod> ReadInstalledForGale(string pluginsDir, bool localFeed = false)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                return Array.Empty<GaleInstalledMod>();
            try
            {
                return ModScanner.ScanPlugins(pluginsDir)
                    .Where(m => m != null)
                    .Select(m => new GaleInstalledMod(
                        string.IsNullOrWhiteSpace(m.Author) ? m.ModName : m.FullName,
                        localFeed ? TestCopyGaleFeed.VersionOf(m) : m.InstalledVersion,
                        m.FullName, m.Disabled,
                        localFeed ? TestCopyGaleFeed.FolderHash(m) : null))
                    .ToList();
            }
            catch (Exception e)
            {
                AppLogger?.Debug("Could not read the mods in {0} for the Gale check: {1}", pluginsDir, e.Message);
                return null;
            }
        }

        /// <summary>
        /// Everything the box, the row and the dialog draw from, for one realm. Made fresh on
        /// every ask from the profile on disk and the last check held, so the counts follow an
        /// untick and the due flag follows a dismissal straight away.
        /// </summary>
        internal object GaleState(string profile)
        {
            var prefs = LoadGalePrefs(profile);
            var id = prefs?.GaleProfileId;
            var key = prefs?.GaleFollowKey();
            var testCopy = prefs?.FollowsTestCopy() == true ? prefs.FollowTestCopy.Trim() : null;
            GaleMemos.TryGetValue(profile ?? "", out var memo);
            if (memo != null && !string.Equals(memo.ProfileId, key, StringComparison.Ordinal)) memo = null;

            object check = null;
            if (memo != null)
            {
                var ok = memo.Result?.Ok == true;
                GaleProfileDiffResult diff = ok
                    ? GaleProfileDiff.Compare(memo.Result.Meta, Triples(memo), prefs?.GaleFeedUnticked())
                    : null;
                // A pinned build Thunderstore no longer has is not work: it is listed with its
                // mark, and neither counts nor raises the row until the profile pins another.
                var gone = new HashSet<string>(prefs?.GaleNotAvailable ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                bool Gone(GaleDiffRow r) => gone.Contains(GaleNotAvailableKey(r.Name, r.ProfileVersion));
                var addCount = diff?.Rows.Count(r => r.Kind == GaleDiffKind.Add && !Gone(r)) ?? 0;
                // A mod rebuilt on a test copy under the same version is an update to the host,
                // and is counted with them; changedCount says how many of them it is.
                var updateCount = diff?.Rows.Count(r => (r.Kind == GaleDiffKind.Update || r.Kind == GaleDiffKind.Changed) && !Gone(r)) ?? 0;
                var changedCount = diff?.Rows.Count(r => r.Kind == GaleDiffKind.Changed && !Gone(r)) ?? 0;
                // The framework rows are the BepInEx row's, so the list and its count are the
                // server's own mods the profile does not name, and nothing else.
                var onlyHereCount = diff?.Rows.Count(r => r.Kind == GaleDiffKind.NotInProfile) ?? 0;
                var here = new Dictionary<string, GaleInstalledMod>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in memo.Mods ?? Array.Empty<GaleInstalledMod>())
                    if (!string.IsNullOrWhiteSpace(m.Name)) here.TryAdd(m.Name.Trim(), m);
                check = new
                {
                    seq = memo.Seq,
                    at = memo.CheckedUtc.ToString("o"),
                    ok,
                    statusId = ok ? null : memo.Result?.SentenceId ?? GaleSyncClient.NetworkErrorSentence,
                    statusGame = ok ? null : memo.Result?.Game,
                    updatedAt = ok ? memo.Result.Meta.UpdatedAt : null,
                    due = ok && addCount + updateCount > 0 && GaleProfileDiff.IsDue(diff, prefs?.GaleFeedLastApproved()),
                    addCount,
                    updateCount,
                    changedCount,
                    skipped = diff?.Skipped ?? 0,
                    onlyHereCount,
                    rows = diff == null
                        ? new List<object>()
                        : diff.Rows.Select(r => GaleRowDto(r, Gone(r),
                            r.Kind == GaleDiffKind.NotInProfile && here.TryGetValue(r.Name ?? "", out var m) ? m : null)).ToList(),
                    // What the configs part last found, so the Review link knows whether the
                    // dialog has anything there. Null until the configs part has been opened.
                    configChanges = GaleConfigChanges(profile, prefs),
                    // BepInEx on this realm, for the dialog: missing is only said when the
                    // install could be looked at, and inProfile is whether the profile lists the
                    // pack, which is what lets Install put it in first.
                    bepinex = ok
                        ? new { missing = GaleBepInExMissing(profile), inProfile = GaleListsBepInEx(memo.Result.Meta) }
                        : null,
                };
            }

            string heldBy = null;
            try
            {
                var plugins = GetPluginsDirectoryFor(profile);
                if (!string.IsNullOrWhiteSpace(plugins)) heldBy = RunningSessionName(new[] { plugins });
            }
            catch { /* no answer is not a running server; the add refuses on its own if it is */ }

            return new
            {
                profile,
                following = !string.IsNullOrWhiteSpace(key),
                id,
                // What the follow reads: "gale" for a Gale profile, "testcopy" for one of this
                // realm's test copies (named in testCopy), or null for nothing.
                feed = testCopy != null ? "testcopy" : string.IsNullOrWhiteSpace(id) ? null : "gale",
                testCopy,
                lastApproved = prefs?.GaleFeedLastApproved(),
                unticked = prefs?.GaleFeedUnticked() ?? new List<string>(),
                // The realm that has this install's plugins folder open, when one does. The
                // dialog says to stop it first and does not offer Install while it is up.
                heldBy,
                check,
            };
        }

        /// <summary>True only when this realm's install was looked at and has no BepInEx. Unknown is not missing.</summary>
        private bool GaleBepInExMissing(string profile)
        {
            try
            {
                var baseExe = GetCanonicalBaseServerExe(profile);
                if (string.IsNullOrWhiteSpace(baseExe) || BepInEx == null) return false;
                return !BepInEx.Status(baseExe, GetPluginsDirectoryFor(profile), KnownInstalls()).Installed;
            }
            catch (Exception e)
            {
                AppLogger?.Debug("Could not read BepInEx on {0} for the Gale dialog: {1}", profile, e.Message);
                return false;
            }
        }

        private static object GaleRowDto(GaleDiffRow r, bool notAvailable, GaleInstalledMod? onServer) => new
        {
            name = r.Name,
            notAvailable,
            // For a server-only row: the scan's own FullName, which the switch looks the mod up
            // by, and whether it is switched off now. Null and false on every other row.
            fullName = onServer?.FullName,
            off = onServer?.Off ?? false,
            kind = GaleKindWord(r.Kind),
            untickedFrom = r.UntickedFrom == null ? null : GaleKindWord(r.UntickedFrom.Value),
            profileVersion = r.ProfileVersion,
            serverVersion = r.ServerVersion,
            downgrade = r.IsDowngrade,
        };

        private static string GaleKindWord(GaleDiffKind kind) => kind switch
        {
            GaleDiffKind.Add => "add",
            GaleDiffKind.Update => "update",
            GaleDiffKind.Same => "same",
            GaleDiffKind.Unticked => "unticked",
            GaleDiffKind.Disabled => "disabled",
            GaleDiffKind.NotInProfile => "notInProfile",
            GaleDiffKind.Framework => "framework",
            GaleDiffKind.Changed => "changed",
            _ => "same",
        };
    }
}
