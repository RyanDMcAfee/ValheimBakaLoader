using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ValheimBakaLoader.Tools.Logging
{
    /// <summary>
    /// One finished application line and the realm it was written for, or null when it
    /// belongs to no realm.
    /// <para>
    /// The realm is the only part that can move after the line is written: a realm that is
    /// renamed takes its lines to the new name, or the Saga would hide them for good, since
    /// no realm on screen would ever carry the old one again.
    /// </para>
    /// </summary>
    public sealed class AppLogLine
    {
        private volatile string Realm;

        public AppLogLine(string line, string profile)
        {
            Line = line;
            Realm = profile;
        }

        public string Line { get; }

        public string Profile
        {
            get => Realm;
            internal set => Realm = value;
        }
    }

    /// <summary>
    /// A logger whose lines carry the realm they were written for. Kept beside
    /// <see cref="IBufferedLogger"/> rather than in it, so the plain string tail and the
    /// plain string event every other reader uses stay exactly what they were.
    /// </summary>
    public interface IRealmTaggedLog
    {
        /// <summary>Every finished line with its realm, raised right after LogReceived.</summary>
        event Action<AppLogLine> LineReceived;

        /// <summary>The in-memory tail, oldest first, each line with its realm.</summary>
        IEnumerable<AppLogLine> TaggedBuffer { get; }

        /// <summary>Moves every held line of one realm to its new name.</summary>
        void RenameRealm(string from, string to);
    }

    /// <summary>
    /// Which realm the application line being written belongs to.
    /// <para>
    /// There is one application log for the whole window, and with two realms running their
    /// lines arrived in it interleaved and untagged, so the Saga of one realm read the other
    /// realm's starts, stops and sweeps as if they were its own. The realm cannot be worked out
    /// from the sentence afterwards (most of them never name it), so it is said where the work
    /// is done: a scope around a per-realm path, which reaches every logger that path calls
    /// into, or a tagged logger where a scope is awkward.
    /// </para>
    /// <para>
    /// The scope is an AsyncLocal, so it follows the work through every await and into the
    /// tasks it starts, and never leaks into work that was started somewhere else. A line
    /// written with no scope belongs to no realm and shows on every realm.
    /// </para>
    /// </summary>
    public static class AppLog
    {
        private static readonly AsyncLocal<string> Ambient = new();

        /// <summary>The realm the current work is for, or null for none.</summary>
        public static string CurrentRealm => Ambient.Value;

        /// <summary>
        /// Lines written until this is disposed belong to <paramref name="profile"/>. A blank
        /// name changes nothing: a path that lost its realm name is not a claim that the work
        /// belongs to no realm, so the scope around it, when there is one, still holds.
        /// Scopes nest; disposing one puts back what was there before it.
        /// </summary>
        public static IDisposable ForRealm(string profile)
        {
            return string.IsNullOrWhiteSpace(profile) ? NoChange.Instance : new Scope(profile.Trim());
        }

        /// <summary>Lines written until this is disposed belong to no realm, whatever the scope outside says.</summary>
        public static IDisposable ForNoRealm() => new Scope(null);

        /// <summary>
        /// A logger whose every line belongs to <paramref name="profile"/>, for a call site where
        /// a scope would have to wrap more than the one line.
        /// </summary>
        public static IApplicationLogger ForRealm(this IApplicationLogger logger, string profile)
        {
            return logger == null ? null : new RealmLogger(logger, () => profile);
        }

        /// <summary>
        /// The same, with the name asked on every line. A server's realm name is set after the
        /// server is built, so its logger cannot be handed a fixed one.
        /// </summary>
        public static IApplicationLogger ForRealm(this IApplicationLogger logger, Func<string> profile)
        {
            return logger == null ? null : new RealmLogger(logger, profile ?? (() => null));
        }

        private sealed class Scope : IDisposable
        {
            private readonly string Previous;
            private bool Done;

            public Scope(string realm)
            {
                Previous = Ambient.Value;
                Ambient.Value = realm;
            }

            public void Dispose()
            {
                if (Done) return;
                Done = true;
                Ambient.Value = Previous;
            }
        }

        private sealed class NoChange : IDisposable
        {
            public static readonly NoChange Instance = new();

            public void Dispose() { }
        }

        /// <summary>
        /// Writes through to the real logger inside a realm scope. Everything else is the real
        /// logger's own: its level dial, its template binding, its tail and its live event, so a
        /// line written through this is the same line, with a realm beside it.
        /// </summary>
        private sealed class RealmLogger : IApplicationLogger, IRealmTaggedLog
        {
            private readonly IApplicationLogger Inner;
            private readonly Func<string> Realm;

            public RealmLogger(IApplicationLogger inner, Func<string> realm)
            {
                Inner = inner;
                Realm = realm;
            }

            public event Action<string> LogReceived
            {
                add => Inner.LogReceived += value;
                remove => Inner.LogReceived -= value;
            }

            public event Action<AppLogLine> LineReceived
            {
                add { if (Inner is IRealmTaggedLog tagged) tagged.LineReceived += value; }
                remove { if (Inner is IRealmTaggedLog tagged) tagged.LineReceived -= value; }
            }

            public IEnumerable<string> LogBuffer => Inner.LogBuffer;

            public IEnumerable<AppLogLine> TaggedBuffer
                => Inner is IRealmTaggedLog tagged ? tagged.TaggedBuffer : Array.Empty<AppLogLine>();

            public void RenameRealm(string from, string to)
            {
                if (Inner is IRealmTaggedLog tagged) tagged.RenameRealm(from, to);
            }

            public void Write(LogEvent logEvent)
            {
                string realm;
                try { realm = Realm(); }
                catch { realm = null; }

                using (ForRealm(realm)) Inner.Write(logEvent);
            }

            public bool IsEnabled(LogEventLevel level) => Inner.IsEnabled(level);

            public bool BindMessageTemplate(string messageTemplate, object[] propertyValues,
                out MessageTemplate parsedTemplate, out IEnumerable<LogEventProperty> boundProperties)
                => Inner.BindMessageTemplate(messageTemplate, propertyValues, out parsedTemplate, out boundProperties);

            public bool BindProperty(string propertyName, object value, bool destructureObjects, out LogEventProperty property)
                => Inner.BindProperty(propertyName, value, destructureObjects, out property);
        }
    }
}
