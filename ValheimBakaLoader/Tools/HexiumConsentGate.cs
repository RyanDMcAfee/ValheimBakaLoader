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

        private string _token;
        private string _forKey;
        private DateTime _issuedUtc;

        /// <summary>Test seam: the clock staleness is measured against.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>True while an acceptance is outstanding, whether or not it is still fresh.</summary>
        public bool HasOutstanding => !string.IsNullOrEmpty(_token);

        /// <summary>
        /// Records that the host has been shown one exact package and version, and hands
        /// back the token that says so.
        /// </summary>
        public string Issue(string owner, string name, string version)
        {
            _token = Guid.NewGuid().ToString("N");
            _forKey = Key(owner, name, version);
            _issuedUtc = UtcNow();
            return _token;
        }

        /// <summary>
        /// Spends the outstanding acceptance if it is this one, is still fresh, and names
        /// this exact package and version. The acceptance is always cleared, taken or
        /// not, so a token is good once and once only.
        /// </summary>
        public bool Take(string token, string owner, string name, string version)
        {
            var expected = _token;
            var expectedFor = _forKey;
            var issued = _issuedUtc;

            _token = null;
            _forKey = null;
            _issuedUtc = DateTime.MinValue;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(expected)) return false;
            if (!string.Equals(token, expected, StringComparison.Ordinal)) return false;
            if (UtcNow() - issued > Lifetime) return false;

            return string.Equals(expectedFor, Key(owner, name, version), StringComparison.Ordinal);
        }

        /// <summary>Drops any outstanding acceptance without spending it.</summary>
        public void Clear()
        {
            _token = null;
            _forKey = null;
            _issuedUtc = DateTime.MinValue;
        }

        private static string Key(string owner, string name, string version) =>
            $"{(owner ?? "").Trim()}|{(name ?? "").Trim()}|{(version ?? "").Trim()}";
    }
}
