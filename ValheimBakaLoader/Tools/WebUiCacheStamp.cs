using System;
using System.IO;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Keeps the interface a build ships with from being served out of the last build's cache.
    /// <para>
    /// The halls are ordinary web files behind a virtual host, and the embedded browser keeps a
    /// disk cache of its own that outlives an update: the app folder gets a new app.js and a new
    /// app.css, the browser recognises the same two addresses it fetched last week, and the
    /// window comes up showing the interface you replaced. Two things stop that. Every include
    /// carries the version it was shipped with, so a new release is a new address (index.html
    /// writes the stamp; see the bootstrap at the top of it). And the first run of a build that
    /// has not run before throws away what the browser held, which covers anything a stamp
    /// cannot reach.
    /// </para>
    /// <para>
    /// The marker sits beside userprefs.json, so a reinstall over the top keeps it and a wiped
    /// profile folder simply means one extra clear.
    /// </para>
    /// </summary>
    public static class WebUiCacheStamp
    {
        /// <summary>The marker's name on disk, beside the preferences file.</summary>
        internal const string MarkerFileName = "webui-cache-version.txt";

        /// <summary>
        /// The file holding the version whose interface the browser cache was last filled for.
        /// <para>
        /// Derived from where the preferences file lives rather than written out again, so the
        /// two cannot drift apart: move the profile folder and the marker moves with it, which
        /// is the whole of what "the marker sits beside userprefs.json" is supposed to mean.
        /// </para>
        /// </summary>
        internal static readonly string MarkerFilePath = Path.Combine(
            Path.GetDirectoryName(Properties.Resources.UserPrefsFilePathV2) ?? string.Empty,
            MarkerFileName);

        /// <summary>
        /// Whether a build that is starting now should throw the browser cache away.
        /// <para>
        /// Once per version and no more. The same version starting again keeps its cache, which
        /// is the whole point of having one. A version that has never run here clears, and so
        /// does the first run after an install where nothing was ever written down, because a
        /// cache filled by an unknown build is exactly the case worth being rid of. A build that
        /// cannot say which version it is clears nothing: there would be no marker to write
        /// afterwards, so it would clear on every single launch.
        /// </para>
        /// </summary>
        /// <param name="lastVersion">The version written down after the last clear, or null.</param>
        /// <param name="currentVersion">The version running now.</param>
        public static bool ShouldClear(string lastVersion, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(currentVersion)) return false;
            if (string.IsNullOrWhiteSpace(lastVersion)) return true;

            return !string.Equals(lastVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The version the marker holds, or null when there is no readable marker.</summary>
        public static string ReadLastVersion() => ReadLastVersionFrom(ExpandedMarkerPath());

        /// <summary>Writes down the version whose cache the browser now holds.</summary>
        public static void RememberVersion(string version) => RememberVersionIn(ExpandedMarkerPath(), version);

        /// <summary>The marker's full path, with the environment variables in it resolved.</summary>
        internal static string ExpandedMarkerPath() => Environment.ExpandEnvironmentVariables(MarkerFilePath);

        /// <summary>The version a marker file holds, or null when it is missing or unreadable.</summary>
        internal static string ReadLastVersionFrom(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

                var held = File.ReadAllText(path).Trim();
                return string.IsNullOrWhiteSpace(held) ? null : held;
            }
            catch
            {
                // An unreadable marker reads as "no build has run here", which clears once more
                // than strictly needed and never less. Never fatal: this is housekeeping.
                return null;
            }
        }

        /// <summary>Writes a version into a marker file, creating the folder if it is not there yet.</summary>
        internal static void RememberVersionIn(string path, string version)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(version)) return;

                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

                File.WriteAllText(path, version.Trim());
            }
            catch
            {
                // A marker that will not write means the next launch clears again. Harmless, and
                // far better than letting a failed write stop the window from coming up.
            }
        }
    }
}
