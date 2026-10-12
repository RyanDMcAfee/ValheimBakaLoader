using System;
using System.Collections.Concurrent;
using System.IO;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The three folders BakaLoader keeps things in outside its install, named in ONE place.
    /// <para>
    /// WHY. Before this class each of these was written out wherever it was needed: the preferences
    /// file, the logs, the player cache, the language packs and the analytics journal each
    /// carried its own copy of the LocalLow path, two places built Valheim's save folder by
    /// hand, and the self-update work folder was built inline. A second copy of BakaLoader run
    /// for testing therefore read and wrote the owner's real data, and there was no way to
    /// point it anywhere else.
    /// </para>
    /// <para>
    /// Each folder can be moved with an environment variable, for an isolated test run:
    /// <see cref="DataVariable"/>, <see cref="ValheimSaveVariable"/> and
    /// <see cref="UpdateVariable"/>. A value is honoured only when it is an absolute path to a
    /// folder that exists or can be made, and that can be written to. Anything else (unset,
    /// blank, relative, unwritable) means exactly the path BakaLoader has always used, string
    /// for string, so a host who never sets one sees nothing change.
    /// </para>
    /// </summary>
    public static class AppDataRoot
    {
        /// <summary>Moves the BakaLoader data folder (preferences, logs, caches, languages).</summary>
        public const string DataVariable = "BAKALOADER_DATA_DIR";

        /// <summary>Moves the default Valheim save folder a fresh install points at.</summary>
        public const string ValheimSaveVariable = "BAKALOADER_VALHEIM_SAVE_DIR";

        /// <summary>Moves the self-update work folder.</summary>
        public const string UpdateVariable = "BAKALOADER_UPDATE_DIR";

        /// <summary>
        /// The shipped data folder, with its variable left in on purpose: the paths built from
        /// it are stored and compared in that form, and PathCheck.Resolve expands it on read.
        /// </summary>
        internal const string DefaultBakaLoaderRoot = @"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader";

        /// <summary>
        /// The shipped Valheim save folder in the form preferences store it, variable and all.
        /// </summary>
        internal const string DefaultValheimSaveRoot = @"%USERPROFILE%\AppData\LocalLow\IronGate\Valheim";

        /// <summary>The self-update work folder's name under LocalApplicationData.</summary>
        internal const string DefaultUpdateFolderName = "BakaLoaderUpdate";

        /// <summary>
        /// The BakaLoader data folder. Unset, this is the shipped string with
        /// <c>%USERPROFILE%</c> still in it; set, it is the override as a full path.
        /// </summary>
        public static string BakaLoaderRoot => Override(DataVariable) ?? DefaultBakaLoaderRoot;

        /// <summary>
        /// Valheim's default save folder in the form preferences store it. Unset, the shipped
        /// string with <c>%USERPROFILE%</c> still in it; set, the override as a full path.
        /// </summary>
        public static string ValheimSaveRoot => Override(ValheimSaveVariable) ?? DefaultValheimSaveRoot;

        /// <summary>
        /// Valheim's default save folder with no variable left in it, for the readers that need
        /// a concrete place. Unset, it is built from the profile folder exactly as before.
        /// </summary>
        public static string ValheimSaveRootFull => Override(ValheimSaveVariable) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "IronGate", "Valheim");

        /// <summary>
        /// Where a self-update downloads and where the updater leaves its result. The updater
        /// is this same program started again, so it inherits the variable and agrees.
        /// </summary>
        public static string UpdateWorkRoot => Override(UpdateVariable) ?? Path.Combine(
            LocalRoot, DefaultUpdateFolderName);

        /// <summary>
        /// The folder that stands in for %LOCALAPPDATA%: the WebView2 profile, the Atlas map
        /// cache, SteamCMD, the install isolation fallback and the update report all live under
        /// it. Unset, it is the real LocalApplicationData folder exactly as before; with
        /// <see cref="DataVariable"/> set, it is a <c>local</c> folder inside that root, so an
        /// isolated run never touches the owner's LocalAppData either.
        /// </summary>
        public static string LocalRoot
        {
            get
            {
                var data = Override(DataVariable);
                return data != null
                    ? Path.Combine(data, "local")
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
        }

        /// <summary>
        /// True when this run keeps its data somewhere other than the shipped folder, which is
        /// what an isolated test instance looks like. Start with Windows is never written then.
        /// </summary>
        public static bool DataOverrideActive => Override(DataVariable) != null;

        // One answer per value seen, so a log line that asks for its folder does not probe the
        // disk every time. A variable cannot change under a running process in practice; a
        // test that changes one gets a fresh answer because the value is the key.
        private static readonly ConcurrentDictionary<string, string> Accepted = new(StringComparer.Ordinal);

        private static string Override(string variable)
        {
            string raw;
            try { raw = Environment.GetEnvironmentVariable(variable); }
            catch { return null; }
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var answer = Accepted.GetOrAdd(raw, v => Accept(v) ?? string.Empty);
            return answer.Length == 0 ? null : answer;
        }

        /// <summary>
        /// The full path a raw value names when it is usable as a data folder, or null. Usable
        /// means absolute, made if missing, and writable: a probe file is written and removed.
        /// A relative path is refused rather than anchored at the working directory, which is
        /// the mistake issue 17 was about.
        /// </summary>
        internal static string Accept(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Trim().Trim('"');
            try
            {
                if (!Path.IsPathFullyQualified(value)) return null;
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
                Directory.CreateDirectory(full);
                var probe = Path.Combine(full, ".bakaloader-write-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return full;
            }
            catch
            {
                return null;
            }
        }
    }
}
