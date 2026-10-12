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
    /// Following a Gale synced profile: the seven RPCs the Mods hall's box, the condition bar row
    /// and the approval dialog talk to.
    /// <para>
    /// The page never sees the service. It asks gale.check, this side reads the profile's meta
    /// through <see cref="IGaleSyncClient"/> on a worker, reads the realm's installed mods the
    /// way the Mods hall does, and hands back the comparison and whether the row is due. Only
    /// gale.check ever reaches the network, and only for a realm with an id set; the page calls
    /// it at the three moments the spec names (after the first frame, when the Mods hall opens,
    /// and on Check now) and at no other.
    /// </para>
    /// <para>
    /// The installs themselves are not here. The dialog runs the Mods hall's own add, one mod at
    /// a time, through mods.addFromUrl with the version pinned in the address, so the download,
    /// the checks on it and every failure sentence are the ones that road already has. Nothing
    /// in this file removes a mod, and nothing in it can.
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

            /// <summary>The id the check was made for. A memo for any other id is stale.</summary>
            public string ProfileId { get; init; }

            public DateTime CheckedUtc { get; init; }

            public GaleCheckResult Result { get; init; }

            /// <summary>The realm's mods as the scan read them, as name and version. Null when the check failed.</summary>
            public IReadOnlyList<(string Name, string Version)> Installed { get; init; }
        }

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
                else if (!string.Equals(prefs.GaleProfileId, check.Id, StringComparison.Ordinal))
                {
                    prefs.GaleProfileId = check.Id;
                    prefs.GaleLastUpdatedAt = null;
                    prefs.GaleUnticked = new List<string>();
                    ServerPrefsProvider.SavePreferences(prefs);
                    GaleMemos.TryRemove(profile, out _);
                }

                return Task.FromResult<object>(new { ok = true, sentenceId = (string)null, state = GaleState(profile) });
            });

            // Stops following: the id, the last approval and every untick go, and nothing is
            // asked of the service again for this realm. The mods it installed stay.
            RegisterRpc("gale.forget", p =>
            {
                var profile = GaleProfileOf(p);
                var prefs = LoadGalePrefs(profile);
                if (prefs != null) ForgetGale(prefs);
                GaleMemos.TryRemove(profile, out _);
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
                    || !string.Equals(memo.ProfileId, prefs.GaleProfileId, StringComparison.Ordinal);
                if (!stale)
                {
                    prefs.GaleLastUpdatedAt = memo.Result.Meta.UpdatedAt;
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
                    && string.Equals(memo.ProfileId, prefs.GaleProfileId, StringComparison.Ordinal)
                    && RereadGaleInstalled(profile, memo);
                return Task.FromResult<object>(new { ok = fresh, state = GaleState(profile) });
            });

            // The ticks in the approval dialog. A name unticked is remembered until the host
            // ticks it again; a ticked one is taken off the list. Only "Namespace-Name" is kept.
            RegisterRpc("gale.untick", p =>
            {
                var profile = GaleProfileOf(p);
                var prefs = LoadGalePrefs(profile);
                if (prefs == null) return Task.FromResult<object>(new { ok = false, state = GaleState(profile) });

                var off = Names(p?["unticked"]);
                var on = Names(p?["ticked"]);
                var kept = ServerPreferences.NormalizeUnticked(prefs.GaleUnticked)
                    .Where(n => !on.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .Concat(off)
                    .ToList();
                prefs.GaleUnticked = ServerPreferences.NormalizeUnticked(kept);
                ServerPrefsProvider.SavePreferences(prefs);

                return Task.FromResult<object>(new { ok = true, state = GaleState(profile) });
            });
        }

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
            ServerPrefsProvider.SavePreferences(prefs);
            GaleMemos.TryRemove(prefs.ProfileName ?? "", out _);
        }

        private static List<string> Names(JToken token)
        {
            if (token is not JArray list) return new List<string>();
            return list.OfType<JValue>()
                .Where(v => v.Type == JTokenType.String)
                .Select(v => ((string)v)?.Trim())
                .Where(GaleProfileDiff.IsFullName)
                .ToList();
        }

        /// <summary>
        /// Runs one check for a realm and answers with its state. Never throws: the client
        /// answers every failure as an outcome, and the scan is wrapped.
        /// </summary>
        internal async Task<object> GaleCheckAsync(string profile)
        {
            var prefs = LoadGalePrefs(profile);
            var id = prefs?.GaleProfileId;
            if (string.IsNullOrWhiteSpace(id)) return GaleState(profile);

            Task<GaleCheckMemo> run;
            lock (GaleLock)
            {
                if (!GaleChecksInFlight.TryGetValue(profile, out run))
                {
                    var client = GaleClient;
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
            if (memo != null && now != null && string.Equals(now.GaleProfileId, memo.ProfileId, StringComparison.Ordinal))
            {
                GaleMemos[profile] = new GaleCheckMemo
                {
                    Seq = Interlocked.Increment(ref GaleSeq),
                    ProfileId = memo.ProfileId,
                    CheckedUtc = memo.CheckedUtc,
                    Result = memo.Result,
                    Installed = memo.Installed,
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

            IReadOnlyList<(string, string)> installed = null;
            if (result?.Ok == true)
            {
                installed = ReadInstalledForGale(pluginsDir);
                if (installed == null)
                {
                    // A folder that would not read is not an empty server: comparing against
                    // nothing would offer every mod in the profile as an add.
                    result = new GaleCheckResult { Outcome = GaleCheckOutcome.Malformed, SentenceId = GaleUnreadableSentence };
                }
            }

            return new GaleCheckMemo { ProfileId = id, CheckedUtc = DateTime.UtcNow, Result = result, Installed = installed };
        }

        /// <summary>
        /// Puts back the memo for a realm with its installed list read off the disk again,
        /// keeping the check itself, its Seq and its time. Only the stored memo it was handed is
        /// replaced, so a newer check that landed meanwhile is left alone. False when the folder
        /// would not read, and the memo is then left as it was.
        /// </summary>
        private bool RereadGaleInstalled(string profile, GaleCheckMemo memo)
        {
            var installed = ReadInstalledForGale(GetPluginsDirectoryFor(profile));
            if (installed == null) return false;
            return GaleMemos.TryUpdate(profile, new GaleCheckMemo
            {
                Seq = memo.Seq,
                ProfileId = memo.ProfileId,
                CheckedUtc = memo.CheckedUtc,
                Result = memo.Result,
                Installed = installed,
            }, memo);
        }

        /// <summary>The status line when the realm's own mod folder could not be read.</summary>
        internal const string GaleUnreadableSentence = "mods.gale.check.unreadable";

        /// <summary>
        /// The realm's mods, read the way the Mods hall reads them: ModScanner's manifest name
        /// and version per folder. A realm with no plugins folder yet has no mods, which is an
        /// answer; a folder that is there and will not read is null, which is not.
        /// </summary>
        private IReadOnlyList<(string, string)> ReadInstalledForGale(string pluginsDir)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                return Array.Empty<(string, string)>();
            try
            {
                return ModScanner.ScanPlugins(pluginsDir)
                    .Where(m => m != null)
                    .Select(m => (string.IsNullOrWhiteSpace(m.Author) ? m.ModName : m.FullName, m.InstalledVersion))
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
            GaleMemos.TryGetValue(profile ?? "", out var memo);
            if (memo != null && !string.Equals(memo.ProfileId, id, StringComparison.Ordinal)) memo = null;

            object check = null;
            if (memo != null)
            {
                var ok = memo.Result?.Ok == true;
                GaleProfileDiffResult diff = ok
                    ? GaleProfileDiff.Compare(memo.Result.Meta, memo.Installed, prefs?.GaleUnticked)
                    : null;
                check = new
                {
                    seq = memo.Seq,
                    at = memo.CheckedUtc.ToString("o"),
                    ok,
                    statusId = ok ? null : memo.Result?.SentenceId ?? GaleSyncClient.NetworkErrorSentence,
                    updatedAt = ok ? memo.Result.Meta.UpdatedAt : null,
                    due = ok && GaleProfileDiff.IsDue(diff, prefs?.GaleLastUpdatedAt),
                    addCount = diff?.AddCount ?? 0,
                    updateCount = diff?.UpdateCount ?? 0,
                    skipped = diff?.Skipped ?? 0,
                    rows = diff == null ? new List<object>() : diff.Rows.Select(GaleRowDto).ToList(),
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
                following = !string.IsNullOrWhiteSpace(id),
                id,
                lastApproved = prefs?.GaleLastUpdatedAt,
                unticked = prefs?.GaleUnticked ?? new List<string>(),
                // The realm that has this install's plugins folder open, when one does. The
                // dialog says to stop it first and does not offer Install while it is up.
                heldBy,
                check,
            };
        }

        private static object GaleRowDto(GaleDiffRow r) => new
        {
            name = r.Name,
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
            _ => "same",
        };
    }
}
