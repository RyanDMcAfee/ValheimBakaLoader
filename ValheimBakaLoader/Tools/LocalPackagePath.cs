using System;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Says whether what was pasted into Add from link is a file on this PC rather than a link,
    /// and if it is, which file. Pure: it reads nothing off the disk, so it can run on the window's
    /// own thread without a share that is not answering holding it up.
    /// <para>
    /// A test build of a mod is a zip on the host's own disk, and the box only ever took links,
    /// so a pasted <c>C:\...\Aurora-1.4.0.zip</c> died in the link parser with a sentence about a
    /// missing https://. This is asked FIRST, before either link parser, so a path never reaches
    /// one, and anything with <c>://</c> in it that is not <c>file://</c> is left exactly where it
    /// was, which is how a download link ending in .zip still goes down the link road.
    /// </para>
    /// <para>
    /// The page holds the same rule as looksLikeLocalPath() in app.js, and the two are held equal by
    /// one table both tests read (scripts/ui/local_path_cases.json), the way the Herald's mod code
    /// pattern is held on both sides.
    /// </para>
    /// </summary>
    public static class LocalPackagePath
    {
        /// <summary>A bare file name, a relative path or a %VARIABLE% path: a file, but not one this can find.</summary>
        public const string NotAbsolute = "notAbsolute";

        /// <summary>A quote on one end and not the other, which is a paste that lost a character.</summary>
        public const string UnbalancedQuote = "unbalancedQuote";

        // A drive letter, a colon, then a slash either way round. C:x.zip (drive relative) is not one.
        private static readonly Regex DrivePath = new(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);

        // \\server\share\..., //server/share/..., and the \\?\ long path form, which has the same shape.
        private static readonly Regex UncPath = new(@"^[\\/]{2}[^\\/]+[\\/][^\\/]+", RegexOptions.CultureInvariant);

        private const char Quote = '"';

        /// <summary>
        /// True when <paramref name="pasted"/> is meant as a file on this PC. Then
        /// <paramref name="path"/> is the path to read (a file:// address turned into its local
        /// form), and <paramref name="problem"/> is null, or <see cref="NotAbsolute"/> or
        /// <see cref="UnbalancedQuote"/> for a path that cannot be read as given. False means it is
        /// not a path at all and belongs to the link road, untouched.
        /// <para>
        /// One pair of double quotes comes off, the way Explorer's Copy as path writes them, with
        /// the spaces and the line end a paste carries on either side. Single quotes are not
        /// quotes here. Environment variables are not expanded: %USERPROFILE% is read as the
        /// host typed it, and a path that only means something after expansion is not a full path.
        /// </para>
        /// </summary>
        public static bool TryRecognize(string pasted, out string path, out string problem)
        {
            path = null;
            problem = null;
            if (pasted == null) return false;

            var text = pasted.Trim();
            var unbalanced = false;
            if (text.Length >= 2 && text[0] == Quote && text[text.Length - 1] == Quote)
            {
                text = text.Substring(1, text.Length - 2).Trim();
            }
            else if (text.Length > 0 && (text[0] == Quote || text[text.Length - 1] == Quote))
            {
                unbalanced = true;
                text = text.Trim(Quote).Trim();
            }

            if (text.Length == 0) return false;

            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                path = text;
                if (unbalanced) { problem = UnbalancedQuote; return true; }
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile && !string.IsNullOrWhiteSpace(uri.LocalPath)
                    && (DrivePath.IsMatch(uri.LocalPath) || UncPath.IsMatch(uri.LocalPath)))
                {
                    path = uri.LocalPath;
                    return true;
                }
                problem = NotAbsolute;
                return true;
            }

            // Every other address is a link, whatever its path looks like.
            if (text.IndexOf("://", StringComparison.Ordinal) >= 0) return false;

            if (DrivePath.IsMatch(text) || UncPath.IsMatch(text))
            {
                path = text;
                if (unbalanced) problem = UnbalancedQuote;
                return true;
            }

            // Not a full path, but plainly meant as a file: a name ending in .zip, a backslash,
            // or a path that leans on the current folder or a variable. Said as such, rather than
            // handed to a link parser that would call it a link with no https:// on it.
            if (text.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || text.IndexOf('\\') >= 0
                || text.StartsWith("%", StringComparison.Ordinal)
                || text.StartsWith(".", StringComparison.Ordinal))
            {
                path = text;
                problem = unbalanced ? UnbalancedQuote : NotAbsolute;
                return true;
            }

            return false;
        }
    }
}
