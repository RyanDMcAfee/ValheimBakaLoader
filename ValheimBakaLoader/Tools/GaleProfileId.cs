using System;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What reading a pasted Gale profile id came to: the id to store and send, or the one
    /// sentence that says why it was refused. Blank is its own answer, because blank is how a
    /// host turns following off, and that is not a mistake to be told about.
    /// </summary>
    public sealed class GaleProfileIdCheck
    {
        /// <summary>The id as it will be stored and sent, trimmed. Null unless <see cref="Ok"/>.</summary>
        public string Id { get; init; }

        /// <summary>True when the id is one the Gale sync service could have handed out.</summary>
        public bool Ok { get; init; }

        /// <summary>True when nothing was typed at all, which means "follow nothing".</summary>
        public bool Blank { get; init; }

        /// <summary>
        /// The catalog id of the sentence the box shows when the id was refused, or null when
        /// it was not. The page reads it through T(); nothing on this side spells it out.
        /// </summary>
        public string SentenceId { get; init; }
    }

    /// <summary>
    /// The shape check on a Gale sync profile id, done before anything is fetched.
    /// <para>
    /// Gale's sync service takes two kinds of id and answers anything else with a 400: a UUID
    /// written as unpadded URL-safe base64 (twenty two characters from A to Z, a to z, 0 to 9,
    /// dash and underscore), or a six character code of letters and digits. Both were read off
    /// the service itself on 2026-10-12. Twenty two characters hold 132 bits and a UUID is 128,
    /// so the last character carries four bits that must be zero; the service refuses an id
    /// whose last character sets them ("...AAB" is a 400, "...AAQ" is not), and so does this.
    /// Padding ("==") and the plus and slash spelling of base64 are refused there too.
    /// </para>
    /// <para>
    /// The point of checking here is that a typo is answered at once, in one sentence, rather
    /// than by a trip to the service and a status line about a 400. Nothing is looked up: a
    /// well formed id that names no profile is the service's to answer, with a 404.
    /// </para>
    /// </summary>
    public static class GaleProfileId
    {
        /// <summary>The one sentence a refused id gets.</summary>
        public const string BadIdSentence = "mods.gale.id.bad";

        /// <summary>
        /// The same two shapes as one pattern, for the page, which has no way to call
        /// <see cref="Check"/> before it sends (HERALD_MODCODE_RE.gale in app.js). This side never
        /// matches with it; a test holds it to <see cref="IsWellFormed"/> on every shape both
        /// should take and refuse, and another holds the page's copy to it.
        /// </summary>
        public const string PagePattern = "^(?:[A-Za-z0-9]{6}|[A-Za-z0-9_-]{21}[AQgw])$";

        private static readonly Regex ShortCode = new(@"^[A-Za-z0-9]{6}\z", RegexOptions.CultureInvariant);

        private static readonly Regex Base64Uuid = new(@"^[A-Za-z0-9_-]{21}[AQgw]\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads what the host typed. Surrounding spaces are forgiven, because a paste picks
        /// them up; anything inside the id is not.
        /// </summary>
        public static GaleProfileIdCheck Check(string typed)
        {
            var id = typed?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                return new GaleProfileIdCheck { Blank = true };
            }

            if (IsWellFormed(id))
            {
                return new GaleProfileIdCheck { Ok = true, Id = id };
            }

            return new GaleProfileIdCheck { SentenceId = BadIdSentence };
        }

        /// <summary>True when an id, exactly as given, is one of the two shapes the service takes.</summary>
        public static bool IsWellFormed(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return ShortCode.IsMatch(id) || Base64Uuid.IsMatch(id);
        }
    }
}
