using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The one outstanding "yes, fetch that from Hexium" the page has been handed.
    /// <para>
    /// Installing from the second site is something the host does, never something the
    /// app does, so the install call will not move a byte without a token this gate
    /// issued. A token is good for the exact package and version the host was shown, it
    /// is spent the moment it is offered up, and it goes stale on its own so a dialog
    /// left open overnight is not still good in the morning.
    /// </para>
    /// <para>
    /// Only one acceptance stands at a time: asking a second question drops the first,
    /// because the only question that counts is the one on the screen.
    /// </para>
    /// </summary>
    public class HexiumConsentGate
    {
        /// <summary>How long an acceptance stays good once the host has been shown the question.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Guards the whole of an issue, a take and a clear.
        /// <para>
        /// "Spent once" was written as a read followed by a write with nothing holding the
        /// two together, and that is only once-and-once-only while a single caller is asking.
        /// The bridge answers each page call on its own task, so two install calls carrying
        /// the same token could both read it before either cleared it, and both would pass:
        /// one answer, two downloads. The contract enforces itself here rather than relying
        /// on nobody ever calling twice at the same moment.
        /// </para>
        /// </summary>
        private readonly object _lock = new();

        private string _token;
        private string _forKey;
        private DateTime _issuedUtc;

        /// <summary>Test seam: the clock staleness is measured against.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>True while an acceptance is outstanding, whether or not it is still fresh.</summary>
        public bool HasOutstanding
        {
            get { lock (_lock) return !string.IsNullOrEmpty(_token); }
        }

        /// <summary>
        /// Records that the host has been shown one exact package and version, and hands
        /// back the token that says so.
        /// </summary>
        public string Issue(string owner, string name, string version)
        {
            lock (_lock)
            {
                _token = Guid.NewGuid().ToString("N");
                _forKey = Key(owner, name, version);
                _issuedUtc = UtcNow();
                return _token;
            }
        }

        /// <summary>
        /// Spends the outstanding acceptance if it is this one, is still fresh, and names
        /// this exact package and version. The acceptance is always cleared, taken or
        /// not, so a token is good once and once only.
        /// <para>
        /// Reading the acceptance and clearing it happen inside one lock, so two calls
        /// arriving at the same moment with the same token cannot both be told yes: the
        /// first one through takes it, and the second finds nothing there.
        /// </para>
        /// </summary>
        public bool Take(string token, string owner, string name, string version)
        {
            string expected, expectedFor;
            DateTime issued;

            lock (_lock)
            {
                expected = _token;
                expectedFor = _forKey;
                issued = _issuedUtc;

                _token = null;
                _forKey = null;
                _issuedUtc = DateTime.MinValue;
            }

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(expected)) return false;
            if (!string.Equals(token, expected, StringComparison.Ordinal)) return false;
            if (UtcNow() - issued > Lifetime) return false;

            return string.Equals(expectedFor, Key(owner, name, version), StringComparison.Ordinal);
        }

        /// <summary>Drops any outstanding acceptance without spending it.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _token = null;
                _forKey = null;
                _issuedUtc = DateTime.MinValue;
            }
        }

        private static string Key(string owner, string name, string version) =>
            $"{(owner ?? "").Trim()}|{(name ?? "").Trim()}|{(version ?? "").Trim()}";
    }
}
