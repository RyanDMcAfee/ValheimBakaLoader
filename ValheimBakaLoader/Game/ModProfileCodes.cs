using System;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// The mod profile code a realm can put on its Discord status post: the share code a
    /// mod manager hands out, which a player pastes into their own manager to get the same
    /// mods as the server.
    /// <para>
    /// It belongs to one realm rather than to the app, because realms carry different mod
    /// sets, so it lives on <see cref="ServerPreferences"/>. The check here is deliberately
    /// light: nothing is ever sent to Thunderstore or to Gale to ask whether a code is real.
    /// It only keeps out what could not be a code at all, which also keeps a backtick, a
    /// mention or a link out of the code span the post puts it in.
    /// </para>
    /// <para>
    /// The page holds the same two patterns (HERALD_MODCODE_RE in app.js), so a code the
    /// window accepts is one this side accepts. A test reads both and holds them equal.
    /// </para>
    /// <para>
    /// A Gale code is the same id the Mods hall follows a Gale profile by, so it is checked by
    /// <see cref="Tools.GaleProfileId"/> rather than by a pattern of its own. It used to have one,
    /// letters, digits and dashes, which refused every sync id with an underscore in it: a
    /// quarter of them or so, since the long id is URL safe base64.
    /// </para>
    /// </summary>
    public static class ModProfileCodes
    {
        public const string None = "none";
        public const string Thunderstore = "thunderstore";
        public const string Gale = "gale";

        /// <summary>
        /// A Thunderstore share code, as r2modman and the Thunderstore client show it:
        /// letters, digits and dashes.
        /// </summary>
        public const string ThunderstorePattern = "^[A-Za-z0-9][A-Za-z0-9-]{3,63}$";

        /// <summary>
        /// A Gale code as the page checks it: the six character code, or the twenty two character
        /// sync id. Not matched on this side; <see cref="IsAcceptable"/> asks
        /// <see cref="Tools.GaleProfileId.Check"/>.
        /// </summary>
        public const string GalePattern = Tools.GaleProfileId.PagePattern;

        private static readonly Regex ThunderstoreRe = new(ThunderstorePattern, RegexOptions.CultureInvariant);

        /// <summary>One of the three spellings, whatever was stored. Anything else reads as none.</summary>
        public static string NormalizeKind(string kind)
        {
            var k = kind?.Trim();
            if (string.Equals(k, Thunderstore, StringComparison.OrdinalIgnoreCase)) return Thunderstore;
            if (string.Equals(k, Gale, StringComparison.OrdinalIgnoreCase)) return Gale;
            return None;
        }

        /// <summary>
        /// Whether a code is one this kind of manager could have handed out. A blank code is
        /// always fine, because blank means no line in the post.
        /// </summary>
        public static bool IsAcceptable(string kind, string code)
        {
            var c = code?.Trim();
            if (string.IsNullOrEmpty(c)) return true;
            return NormalizeKind(kind) switch
            {
                Thunderstore => ThunderstoreRe.IsMatch(c),
                Gale => Tools.GaleProfileId.Check(c).Ok,
                _ => false,
            };
        }

        /// <summary>
        /// The code the post should carry, or null when it carries none: no kind chosen, a
        /// blank code, or a stored value that does not pass the check (an older file, or a
        /// hand edit), which is left out of the post rather than put into a channel.
        /// </summary>
        public static string Postable(string kind, string code)
        {
            var c = code?.Trim();
            if (string.IsNullOrEmpty(c)) return null;
            if (NormalizeKind(kind) == None) return null;
            return IsAcceptable(kind, c) ? c : null;
        }
    }
}
