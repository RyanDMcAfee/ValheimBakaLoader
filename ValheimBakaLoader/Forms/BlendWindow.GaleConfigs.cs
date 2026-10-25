using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The configs part of the Gale dialog: gale.configs lists the config files the followed
    /// profile carries against this realm's own, with a preview of each change, and
    /// gale.configs.apply writes the ones the host ticked.
    /// <para>
    /// The zip is fetched only when the host opens that part of the dialog (and again when the
    /// profile moved since, or on its Refresh), never by gale.check and never at start up, so
    /// the three moments the meta is asked at stay the only three. What was fetched is held in
    /// memory per realm, and the apply writes those bytes, the ones the host was shown, and
    /// never a second download.
    /// </para>
    /// <para>
    /// An apply is held to a stopped server on the realm the page names, the same hold the mod
    /// install has, and to paths that stay inside the realm's BepInEx/config with no link on the
    /// way (ConfigFiles). A file it replaces is copied first into
    /// BepInEx/.bakaloader-config-backups/yyyyMMdd-HHmmss. It writes only files the profile
    /// carries, and nothing in it deletes anything.
    /// </para>
    /// <para>
    /// While the realm follows one of its test copies, the "zip" is the copy's own
    /// BepInEx/config read off this PC (<see cref="TestCopyGaleFeed.ReadConfigs"/>), read again on
    /// every open since a folder can change without a stamp to say so, and the unticks are the
    /// copy's own list. The apply and its backups are the same.
    /// </para>
    /// </summary>
    public partial class BlendWindow
    {
        /// <summary>The profile's config files as last fetched for each realm. Memory only, like the check's memo.</summary>
        private readonly ConcurrentDictionary<string, GaleConfigMemo> GaleConfigMemos =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A fetch already out for a realm, so a second ask joins it.</summary>
        private readonly ConcurrentDictionary<string, Task<GaleConfigMemo>> GaleConfigsInFlight =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object GaleConfigLock = new();

        /// <summary>The folder a replaced config is copied into first, under the realm's BepInEx folder.</summary>
        internal const string GaleConfigBackupFolder = ".bakaloader-config-backups";

        internal const string GaleConfigsNotFollowingSentence = "mods.gale.configs.not_following";

        internal sealed class GaleConfigMemo
        {
            /// <summary>The follow's key the files were read for (the Gale id, or "testcopy:" and the copy's name).</summary>
            public string ProfileId { get; init; }

            /// <summary>The profile's updatedAt the meta had when this was fetched, so a moved profile is fetched again on the next open.</summary>
            public string UpdatedAt { get; init; }

            public DateTime FetchedUtc { get; init; }

            /// <summary>The catalog id of why the fetch or the read did not come back, or null when it did.</summary>
            public string SentenceId { get; init; }

            public bool Ok => SentenceId == null;

            public IReadOnlyList<GaleProfileConfigFile> Files { get; init; } = Array.Empty<GaleProfileConfigFile>();

            /// <summary>Files under config the zip read left out one by one.</summary>
            public int ZipSkipped { get; init; }

            /// <summary>Added plus changed, as the last comparison found them, for the Review link.</summary>
            public int LastChanges { get; set; }
        }

        private void RegisterGaleConfigRpcs()
        {
            // The list, with a preview per file. Fetches the zip when nothing is held for this
            // realm's profile, when the profile moved since, or when the page asks for a refresh.
            RegisterRpc("gale.configs", async p =>
                await GaleConfigsAsync(GaleProfileOf(p), p?.Value<bool?>("refresh") == true));

            // Writes the ticked files, and remembers the unticked ones.
            RegisterRpc("gale.configs.apply", p =>
                Task.FromResult(GaleConfigsApply(GaleProfileOf(p), p?["paths"])));
        }

        internal async Task<object> GaleConfigsAsync(string profile, bool refresh)
        {
            var prefs = LoadGalePrefs(profile);
            var id = prefs?.GaleFollowKey();
            if (string.IsNullOrWhiteSpace(id)) return GaleConfigsState(profile);

            GaleMemos.TryGetValue(profile ?? "", out var check);
            var updatedAt = check?.Result?.Ok == true && string.Equals(check.ProfileId, id, StringComparison.Ordinal)
                ? check.Result.Meta.UpdatedAt
                : null;

            GaleConfigMemos.TryGetValue(profile ?? "", out var held);
            // A test copy's folder is on this PC and has no stamp that moves, so it is read again
            // on every open; reading it costs no trip.
            var stale = refresh || prefs.FollowsTestCopy() || held == null || !held.Ok
                || !string.Equals(held.ProfileId, id, StringComparison.Ordinal)
                || (updatedAt != null && !string.Equals(held.UpdatedAt, updatedAt, StringComparison.Ordinal));
            if (!stale) return GaleConfigsState(profile);

            Task<GaleConfigMemo> run;
            lock (GaleConfigLock)
            {
                if (!GaleConfigsInFlight.TryGetValue(profile, out run))
                {
                    var client = GaleFeedFor(profile, prefs);
                    // Off the window's thread: a DNS lookup, a TLS handshake, a redirect and a
                    // zip of up to fifty megabytes.
                    run = Task.Run(() => FetchGaleConfigsAsync(client, id, updatedAt));
                    GaleConfigsInFlight[profile] = run;
                }
            }

            GaleConfigMemo memo;
            try
            {
                memo = await run;
            }
            finally
            {
                lock (GaleConfigLock)
                {
                    if (GaleConfigsInFlight.TryGetValue(profile, out var current) && current == run)
                        GaleConfigsInFlight.TryRemove(profile, out _);
                }
            }

            // Forgotten, or another code pasted, while it was out: not this realm's profile now.
            var now = LoadGalePrefs(profile);
            if (memo != null && now != null && string.Equals(now.GaleFollowKey(), memo.ProfileId, StringComparison.Ordinal))
                GaleConfigMemos[profile] = memo;

            return GaleConfigsState(profile);
        }

        private static async Task<GaleConfigMemo> FetchGaleConfigsAsync(IGaleSyncClient client, string id, string updatedAt)
        {
            GaleConfigMemo Failed(string sentence) =>
                new() { ProfileId = id, UpdatedAt = updatedAt, FetchedUtc = DateTime.UtcNow, SentenceId = sentence };

            // A test copy's configs are read off this PC's disk into the shape the zip reads to,
            // so nothing is fetched and no temp file is written.
            if (client is TestCopyGaleFeed local)
            {
                var folder = local.ReadConfigs();
                if (!folder.Ok) return Failed(folder.Refusal);
                return new GaleConfigMemo
                {
                    ProfileId = id, UpdatedAt = updatedAt, FetchedUtc = DateTime.UtcNow,
                    Files = folder.Files, ZipSkipped = folder.Skipped,
                };
            }

            GaleZipResult zip;
            try
            {
                zip = client == null
                    ? new GaleZipResult { Outcome = GaleCheckOutcome.NetworkError, SentenceId = GaleSyncClient.ZipNetworkErrorSentence }
                    : await client.GetProfileZipAsync(id).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client promises not to throw. A stand in that does is still one line.
                zip = new GaleZipResult { Outcome = GaleCheckOutcome.NetworkError, SentenceId = GaleSyncClient.ZipNetworkErrorSentence };
            }

            using (zip)
            {
                if (!zip.Ok) return Failed(zip.SentenceId ?? GaleSyncClient.ZipNetworkErrorSentence);
                var read = GaleProfileConfigs.ReadZip(zip.ZipPath);
                if (!read.Ok) return Failed(read.Refusal);
                return new GaleConfigMemo
                {
                    ProfileId = id, UpdatedAt = updatedAt, FetchedUtc = DateTime.UtcNow,
                    Files = read.Files, ZipSkipped = read.Skipped,
                };
            }
        }

        /// <summary>What the configs part draws, made fresh against the disk on every ask.</summary>
        internal object GaleConfigsState(string profile)
        {
            var prefs = LoadGalePrefs(profile);
            var id = prefs?.GaleFollowKey();
            GaleConfigMemos.TryGetValue(profile ?? "", out var memo);
            if (memo != null && !string.Equals(memo.ProfileId, id, StringComparison.Ordinal)) memo = null;

            object configs = null;
            if (memo != null && !memo.Ok)
            {
                configs = new { ok = false, statusId = memo.SentenceId, at = memo.FetchedUtc.ToString("o") };
            }
            else if (memo != null)
            {
                var comparison = GaleProfileConfigs.Compare(memo.Files, SafeConfigDirectoryFor(profile));
                memo.LastChanges = comparison.Added + comparison.Changed;
                var unticked = new HashSet<string>(prefs?.GaleFeedConfigUnticked() ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                configs = new
                {
                    ok = true,
                    statusId = (string)null,
                    at = memo.FetchedUtc.ToString("o"),
                    added = comparison.Added,
                    changed = comparison.Changed,
                    same = comparison.Same,
                    skipped = memo.ZipSkipped + comparison.Skipped,
                    files = comparison.Rows.Select(r => new
                    {
                        path = r.Rel,
                        realmPath = r.RealmPath,
                        state = GaleConfigStateWord(r.State),
                        ticked = r.State != GaleConfigState.Same && !unticked.Contains(r.Rel),
                        preview = r.Preview.Select(l => new { k = l.Kind.ToString(), t = l.Text }).ToList(),
                        more = r.More,
                    }).ToList(),
                };
            }

            return new
            {
                profile,
                following = !string.IsNullOrWhiteSpace(id),
                statusId = string.IsNullOrWhiteSpace(id) ? GaleConfigsNotFollowingSentence : null,
                // The test copy the files are read from, or null for a Gale profile.
                testCopy = prefs?.FollowsTestCopy() == true ? prefs.FollowTestCopy.Trim() : null,
                heldBy = GaleHeldBy(profile),
                configs,
            };
        }

        /// <summary>Added plus changed from the last time the configs part looked, or null when it has not looked for this profile.</summary>
        private int? GaleConfigChanges(string profile, ServerPreferences prefs)
        {
            if (!GaleConfigMemos.TryGetValue(profile ?? "", out var memo) || memo == null || !memo.Ok) return null;
            if (!string.Equals(memo.ProfileId, prefs?.GaleFollowKey(), StringComparison.Ordinal)) return null;
            return memo.LastChanges;
        }

        private static string GaleConfigStateWord(GaleConfigState state) => state switch
        {
            GaleConfigState.Added => "added",
            GaleConfigState.Changed => "changed",
            _ => "same",
        };

        /// <summary>The server that holds this realm's plugins folder open, when one does. The same hold the mod install has.</summary>
        private string GaleHeldBy(string profile)
        {
            try
            {
                var plugins = GetPluginsDirectoryFor(profile);
                return string.IsNullOrWhiteSpace(plugins) ? null : RunningSessionName(new[] { plugins });
            }
            catch
            {
                return null;
            }
        }

        private string SafeConfigDirectoryFor(string profile)
        {
            try { return GetConfigDirectoryFor(profile); }
            catch (Exception) { return null; }
        }

        internal object GaleConfigsApply(string profile, JToken paths)
        {
            object Refused(string reason, string error, Dictionary<string, object> errorParams = null) => new
            {
                ok = false, written = 0, Reason = reason, Error = error, ErrorParams = errorParams,
                state = GaleConfigsState(profile),
            };

            if (!string.Equals(profile, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                return Refused("realmChanged", "The server on screen changed, so nothing was written.");
            if (_modUpdateInProgress)
                return Refused("busy", "A mod update is already in progress. Try again in a moment.");

            var configDir = SafeConfigDirectoryFor(profile);
            var pluginsDir = GetPluginsDirectoryFor(profile);
            if (string.IsNullOrWhiteSpace(configDir) || string.IsNullOrWhiteSpace(pluginsDir))
                return Refused("noServerPath", "BepInEx config folder not found. Set a valid server .exe path first.");

            var holding = RunningSessionName(new[] { pluginsDir });
            if (holding != null)
                return Refused("serverRunning", $"Stop the server '{holding}' first. Nothing was written.",
                    new Dictionary<string, object> { ["name"] = holding });

            var prefs = LoadGalePrefs(profile);
            GaleConfigMemos.TryGetValue(profile ?? "", out var memo);
            if (prefs == null || memo == null || !memo.Ok
                || !string.Equals(memo.ProfileId, prefs.GaleFollowKey(), StringComparison.Ordinal))
                return Refused("stale", "The profile's config files are not loaded any more. Press Refresh and look again.");

            // Where the files come from, for the log: the profile, or the test copy named.
            var from = prefs.FollowsTestCopy() ? "the test copy " + prefs.FollowTestCopy.Trim() : "the profile";

            var byRel = new Dictionary<string, GaleProfileConfigFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in memo.Files) byRel.TryAdd(f.Rel, f);

            var asked = new List<string>();
            foreach (var value in (paths as JArray)?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
            {
                var rel = value.Type == JTokenType.String ? ((string)value)?.Trim() : null;
                if (string.IsNullOrEmpty(rel)) continue;
                if (!byRel.TryGetValue(rel, out var carried))
                    return Refused("notInProfile", $"{rel} is not in the profile, so nothing was written.",
                        new Dictionary<string, object> { ["file"] = rel });
                if (!asked.Contains(carried.Rel, StringComparer.OrdinalIgnoreCase)) asked.Add(carried.Rel);
            }

            // Every path is checked before anything is written, so a bad one costs nothing.
            foreach (var rel in asked)
            {
                try { ConfigFiles.ResolveForCreate(configDir, rel, out _); }
                catch (Exception)
                {
                    return Refused("unsafePath", $"{rel} would land outside this server's config folder, so nothing was written.",
                        new Dictionary<string, object> { ["file"] = rel });
                }
            }

            // The ticks are the host's answer whatever happens next. A file offered and left
            // unticked is remembered; one ticked now comes off the list.
            var offered = GaleProfileConfigs.Compare(memo.Files, configDir).Rows
                .Where(r => r.State != GaleConfigState.Same)
                .Select(r => r.Rel);
            prefs.SetGaleFeedConfigUnticked(
                ServerPreferences.NormalizeUnticked(prefs.GaleFeedConfigUnticked()).Concat(offered)
                    .Where(r => !asked.Contains(r, StringComparer.OrdinalIgnoreCase)));
            ServerPrefsProvider.SavePreferences(prefs);

            asked.Sort(ConfigFiles.ComparePaths);
            var bepinexDir = Path.GetDirectoryName(Path.GetFullPath(configDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string stamp = null, backupRoot = null;
            int added = 0, replaced = 0, same = 0;

            _modUpdateInProgress = true;
            try
            {
                foreach (var rel in asked)
                {
                    var bytes = byRel[rel].Bytes;
                    try
                    {
                        var full = ConfigFiles.ResolveForCreate(configDir, rel, out var exists);
                        if (exists && GaleProfileConfigs.SameText(File.ReadAllBytes(full), bytes))
                        {
                            same++;
                            continue;
                        }

                        if (exists)
                        {
                            if (backupRoot == null) (stamp, backupRoot) = NewGaleBackupFolder(bepinexDir);
                            var backup = Path.Combine(new[] { backupRoot }.Concat(rel.Split('/')).ToArray());
                            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                            File.Copy(full, backup, overwrite: false);
                        }
                        else
                        {
                            full = ConfigFiles.CreateFoldersFor(configDir, rel);
                        }

                        File.WriteAllBytes(full, bytes);
                        if (exists) replaced++; else added++;
                        AppLogger?.Information("{Line:l}", exists
                            ? $"[Gale] Wrote {GaleProfileConfigs.RealmPrefix}{rel} from {from}. The old one is in BepInEx/{GaleConfigBackupFolder}/{stamp}/{rel}."
                            : $"[Gale] Wrote {GaleProfileConfigs.RealmPrefix}{rel} from {from}, a new file.");
                    }
                    catch (Exception e)
                    {
                        AppLogger?.Information("{Line:l}",
                            $"[Gale] Could not write {GaleProfileConfigs.RealmPrefix}{rel}: {e.Message} Stopped there, with {added + replaced} written before it.");
                        return new
                        {
                            ok = false, written = added + replaced, Reason = "writeFailed",
                            Error = $"Could not write {rel}: {e.Message}",
                            ErrorParams = new Dictionary<string, object> { ["file"] = rel, ["done"] = added + replaced },
                            state = GaleConfigsState(profile),
                        };
                    }
                }
            }
            finally
            {
                _modUpdateInProgress = false;
            }

            if (asked.Count > 0)
                AppLogger?.Information("{Line:l}",
                    $"[Gale] Configs from {from} on {profile}: {added + replaced} written ({added} new, {replaced} replaced), {same} already the same."
                    + (backupRoot != null ? $" The replaced ones are kept in BepInEx/{GaleConfigBackupFolder}/{stamp}." : ""));

            return new
            {
                ok = true, written = added + replaced, added, replaced, same,
                backup = backupRoot != null ? $"BepInEx/{GaleConfigBackupFolder}/{stamp}" : null,
                Reason = (string)null, Error = (string)null, ErrorParams = (object)null,
                state = GaleConfigsState(profile),
            };
        }

        /// <summary>A fresh folder for this apply's backups, named by the time, with a number after it when that second is taken.</summary>
        private static (string Stamp, string Folder) NewGaleBackupFolder(string bepinexDir)
        {
            var parent = Path.Combine(bepinexDir, GaleConfigBackupFolder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var name = stamp;
            for (var n = 2; Directory.Exists(Path.Combine(parent, name)); n++) name = stamp + "-" + n;
            var folder = Path.Combine(parent, name);
            Directory.CreateDirectory(folder);
            return (name, folder);
        }
    }
}
