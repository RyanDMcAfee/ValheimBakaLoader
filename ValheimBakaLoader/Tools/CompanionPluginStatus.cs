using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One companion plugin that could not be installed or refreshed.</summary>
    public sealed class CompanionPluginFailure
    {
        public CompanionPluginFailure(string plugin, string message, DateTime whenUtc, string profile = null)
        {
            Plugin = plugin;
            Message = message;
            WhenUtc = whenUtc;
            Profile = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
        }

        /// <summary>Operator-facing name of the plugin, for example "Item indexer".</summary>
        public string Plugin { get; }

        /// <summary>Why it failed, in the words of the exception that stopped it.</summary>
        public string Message { get; }

        /// <summary>When the failure was recorded.</summary>
        public DateTime WhenUtc { get; }

        /// <summary>
        /// The server profile whose start ran the installer, or null when the caller had no
        /// profile in hand. Two realms can be up at once, so a failure belonging to one of them
        /// must never be shown against the other. A null belongs to no realm in particular and
        /// is shown to all of them.
        /// </summary>
        public string Profile { get; }

        /// <summary>One line an operator can read, used by the server log and the web UI.</summary>
        public string Describe() =>
            "The " + Plugin + " plugin could not be installed, so the features it powers stay off. " + Message;
    }

    /// <summary>
    /// The companion plugins are installed on a best-effort basis: one that will not copy
    /// must never block a server start. Before this existed the only trace of such a failure
    /// was a line in the application log, which no operator sees, so the feature simply went
    /// missing with no explanation. Installers record their failures here, the server start
    /// path writes them into the server log the web UI shows, and the bridge hands the list
    /// to the interface.
    ///
    /// Static on purpose: the installers are constructed with a logger and nothing else, so
    /// handing a service down to each of them would have meant changing every constructor.
    /// One app process can run several servers at once, so each record is filed under the
    /// profile whose install pass wrote it. See BeginProfile and FailuresFor.
    /// </summary>
    public static class CompanionPluginStatus
    {
        private static readonly object Lock = new();

        // Keyed on profile plus plugin, so two realms that fail the same plugin keep separate
        // records and clearing one realm never touches the other. The comparer is
        // case-insensitive across the whole key, which is how profile names compare elsewhere.
        private static readonly Dictionary<string, CompanionPluginFailure> Recorded =
            new(StringComparer.OrdinalIgnoreCase);

        // The profile whose install pass is running on this call stack. AsyncLocal rather than
        // a plain static field: a scope opened around one server's installers must not leak
        // into another server's start happening at the same time.
        private static readonly AsyncLocal<string> AmbientProfile = new();

        private static string Normalize(string profile)
            => string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();

        private static string KeyOf(string profile, string plugin)
            => (Normalize(profile) ?? string.Empty) + "\u0000" + plugin;

        /// <summary>The profile a report made right now would carry, or null outside any scope.</summary>
        public static string CurrentProfile => AmbientProfile.Value;

        /// <summary>
        /// Files every failure and success reported on this call stack under one server profile.
        /// Dispose puts back whatever scope was in force before, so scopes nest and a caller
        /// with no profile in hand goes on working exactly as it did.
        /// </summary>
        public static IDisposable BeginProfile(string profile)
        {
            var previous = AmbientProfile.Value;
            AmbientProfile.Value = Normalize(profile);
            return new ProfileScope(previous);
        }

        private sealed class ProfileScope : IDisposable
        {
            private readonly string Previous;
            private bool Restored;

            public ProfileScope(string previous) => Previous = previous;

            public void Dispose()
            {
                if (Restored) return;
                Restored = true;
                AmbientProfile.Value = Previous;
            }
        }

        /// <summary>
        /// Records that a plugin could not be installed, replacing any earlier failure for it
        /// under the profile currently in scope.
        /// </summary>
        public static void ReportFailure(string plugin, string message)
        {
            if (string.IsNullOrWhiteSpace(plugin)) return;

            var profile = AmbientProfile.Value;
            lock (Lock)
            {
                Recorded[KeyOf(profile, plugin)] = new CompanionPluginFailure(
                    plugin,
                    string.IsNullOrWhiteSpace(message) ? "No further detail was reported." : message.Trim(),
                    DateTime.UtcNow,
                    profile);
            }
        }

        /// <summary>
        /// Clears any recorded failure for a plugin that has now installed cleanly: the record
        /// for the profile in scope, and the unscoped one a caller with no profile in hand left
        /// behind. Another profile's record is never touched.
        /// </summary>
        public static void ReportSuccess(string plugin)
        {
            if (string.IsNullOrWhiteSpace(plugin)) return;

            var profile = AmbientProfile.Value;
            lock (Lock)
            {
                Recorded.Remove(KeyOf(profile, plugin));
                if (profile != null) Recorded.Remove(KeyOf(null, plugin));
            }
        }

        /// <summary>Every plugin still in a failed state, oldest failure first.</summary>
        public static IReadOnlyList<CompanionPluginFailure> Failures
        {
            get
            {
                lock (Lock)
                    return Recorded.Values
                        .OrderBy(f => f.WhenUtc)
                        .ThenBy(f => f.Plugin, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(f => f.Profile ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();
            }
        }

        /// <summary>
        /// The failures one server profile should be shown: its own, plus any recorded with no
        /// profile in hand. Asking with no profile returns everything, which is what the
        /// single-server path has always been given.
        /// </summary>
        public static IReadOnlyList<CompanionPluginFailure> FailuresFor(string profile)
        {
            var wanted = Normalize(profile);
            if (wanted == null) return Failures;

            return Failures
                .Where(f => f.Profile == null
                    || string.Equals(f.Profile, wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>True when at least one companion plugin is in a failed state.</summary>
        public static bool HasFailures
        {
            get { lock (Lock) return Recorded.Count > 0; }
        }

        /// <summary>
        /// Forgets one profile's failures and leaves every other profile's alone. Called at the
        /// start of that profile's install pass, so a plugin that has since been fixed stops
        /// being reported while the other realm's news survives. Passing no profile forgets the
        /// unscoped records.
        /// </summary>
        public static void ClearProfile(string profile)
        {
            var wanted = Normalize(profile);
            lock (Lock)
            {
                var stale = Recorded
                    .Where(pair => string.Equals(pair.Value.Profile, wanted, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var key in stale) Recorded.Remove(key);
            }
        }

        /// <summary>Forgets every recorded failure, whatever profile it belongs to.</summary>
        public static void Clear()
        {
            lock (Lock) Recorded.Clear();
        }
    }
}
