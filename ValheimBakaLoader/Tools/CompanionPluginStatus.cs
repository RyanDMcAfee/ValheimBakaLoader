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
    /// One book of companion plugin failures: the records, the lock around them, and every
    /// operation on them.
    /// <para>
    /// This used to be a pile of static fields, and the running app is still happy with exactly
    /// one book. What was not happy was anything that needed a book of its own. Installers write
    /// from whichever thread a server start happens to be on, several starts can be in the air
    /// at once, and a reader sharing the book with an unrelated writer reads whatever that
    /// writer was doing. Making the book an instance costs the app nothing and lets a caller
    /// that must not be handed somebody else's news keep its own.
    /// </para>
    /// </summary>
    public sealed class CompanionPluginRecords
    {
        private readonly object Lock = new();

        // Keyed on profile plus plugin, so two realms that fail the same plugin keep separate
        // records and clearing one realm never touches the other. The comparer is
        // case-insensitive across the whole key, which is how profile names compare elsewhere.
        private readonly Dictionary<string, CompanionPluginFailure> Recorded =
            new(StringComparer.OrdinalIgnoreCase);

        private static string Normalize(string profile)
            => string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();

        private static string KeyOf(string profile, string plugin)
            => (Normalize(profile) ?? string.Empty) + "\u0000" + plugin;

        /// <summary>
        /// Records that a plugin could not be installed, replacing any earlier failure for it
        /// under the profile given.
        /// </summary>
        public void ReportFailure(string profile, string plugin, string message)
        {
            if (string.IsNullOrWhiteSpace(plugin)) return;

            var wanted = Normalize(profile);
            lock (Lock)
            {
                Recorded[KeyOf(wanted, plugin)] = new CompanionPluginFailure(
                    plugin,
                    string.IsNullOrWhiteSpace(message) ? "No further detail was reported." : message.Trim(),
                    DateTime.UtcNow,
                    wanted);
            }
        }

        /// <summary>
        /// Clears any recorded failure for a plugin that has now installed cleanly: the record
        /// for the profile given, and the unscoped one a caller with no profile in hand left
        /// behind. Another profile's record is never touched.
        /// </summary>
        public void ReportSuccess(string profile, string plugin)
        {
            if (string.IsNullOrWhiteSpace(plugin)) return;

            var wanted = Normalize(profile);
            lock (Lock)
            {
                Recorded.Remove(KeyOf(wanted, plugin));
                if (wanted != null) Recorded.Remove(KeyOf(null, plugin));
            }
        }

        /// <summary>Every plugin still in a failed state, oldest failure first.</summary>
        public IReadOnlyList<CompanionPluginFailure> Failures
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
        public IReadOnlyList<CompanionPluginFailure> FailuresFor(string profile)
        {
            var wanted = Normalize(profile);
            if (wanted == null) return Failures;

            return Failures
                .Where(f => f.Profile == null
                    || string.Equals(f.Profile, wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>True when at least one companion plugin is in a failed state.</summary>
        public bool HasFailures
        {
            get { lock (Lock) return Recorded.Count > 0; }
        }

        /// <summary>
        /// Forgets one profile's failures and leaves every other profile's alone. Passing no
        /// profile forgets the unscoped records.
        /// </summary>
        public void ClearProfile(string profile)
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
        public void Clear()
        {
            lock (Lock) Recorded.Clear();
        }
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
    ///
    /// The book behind these calls is an instance now (CompanionPluginRecords). The app keeps
    /// the one shared book and behaves exactly as it always did. A caller that must not read
    /// another caller's writes opens a book of its own with BeginOwnRecords, and everything it
    /// starts from inside that scope, background work included, writes into that book instead
    /// of the shared one.
    /// </summary>
    public static class CompanionPluginStatus
    {
        // The one book the app runs on.
        private static readonly CompanionPluginRecords SharedRecords = new();

        // A book belonging to this call stack, when one has been opened. AsyncLocal rather than
        // a plain static field: the scope must not reach a caller it was not opened around, and
        // work handed to a background task from inside it carries the same book along.
        private static readonly AsyncLocal<CompanionPluginRecords> OwnRecords = new();

        // The profile whose install pass is running on this call stack. AsyncLocal rather than
        // a plain static field: a scope opened around one server's installers must not leak
        // into another server's start happening at the same time.
        private static readonly AsyncLocal<string> AmbientProfile = new();

        /// <summary>The book this call stack reads and writes: its own if one is open, else the shared one.</summary>
        public static CompanionPluginRecords Records => OwnRecords.Value ?? SharedRecords;

        /// <summary>True while this call stack keeps a book of its own rather than the shared one.</summary>
        public static bool UsingOwnRecords => OwnRecords.Value != null;

        /// <summary>
        /// Opens a fresh book for this call stack and for everything started from inside it.
        /// Dispose puts back whichever book was in force before.
        /// <para>
        /// Nothing in the app opens one. It exists because the record is process-wide while the
        /// writers are not: a server start files its news from a background task, and two starts
        /// naming the same realm write over each other's records even though they have nothing
        /// to do with each other. A caller whose records have to be its own opens a book here
        /// and is then reading only what it wrote itself.
        /// </para>
        /// </summary>
        public static IDisposable BeginOwnRecords()
        {
            var previous = OwnRecords.Value;
            OwnRecords.Value = new CompanionPluginRecords();
            return new RecordsScope(previous);
        }

        private sealed class RecordsScope : IDisposable
        {
            private readonly CompanionPluginRecords Previous;
            private bool Restored;

            public RecordsScope(CompanionPluginRecords previous) => Previous = previous;

            public void Dispose()
            {
                if (Restored) return;
                Restored = true;
                OwnRecords.Value = Previous;
            }
        }

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
            AmbientProfile.Value = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
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
            => Records.ReportFailure(AmbientProfile.Value, plugin, message);

        /// <summary>
        /// Clears any recorded failure for a plugin that has now installed cleanly: the record
        /// for the profile in scope, and the unscoped one a caller with no profile in hand left
        /// behind. Another profile's record is never touched.
        /// </summary>
        public static void ReportSuccess(string plugin)
            => Records.ReportSuccess(AmbientProfile.Value, plugin);

        /// <summary>Every plugin still in a failed state, oldest failure first.</summary>
        public static IReadOnlyList<CompanionPluginFailure> Failures => Records.Failures;

        /// <summary>
        /// The failures one server profile should be shown: its own, plus any recorded with no
        /// profile in hand. Asking with no profile returns everything, which is what the
        /// single-server path has always been given.
        /// </summary>
        public static IReadOnlyList<CompanionPluginFailure> FailuresFor(string profile)
            => Records.FailuresFor(profile);

        /// <summary>True when at least one companion plugin is in a failed state.</summary>
        public static bool HasFailures => Records.HasFailures;

        /// <summary>
        /// Forgets one profile's failures and leaves every other profile's alone. Called at the
        /// start of that profile's install pass, so a plugin that has since been fixed stops
        /// being reported while the other realm's news survives. Passing no profile forgets the
        /// unscoped records.
        /// </summary>
        public static void ClearProfile(string profile) => Records.ClearProfile(profile);

        /// <summary>Forgets every recorded failure, whatever profile it belongs to.</summary>
        public static void Clear() => Records.Clear();
    }
}
