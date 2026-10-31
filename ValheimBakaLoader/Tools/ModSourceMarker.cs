using System;
using System.IO;
using Newtonsoft.Json;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The note BakaLoader leaves inside a mod folder it installed, so later scans can
    /// tell where the files came from and when. Written by BakaLoader and nobody else.
    /// </summary>
    public class ModSourceMarker
    {
        [JsonProperty("schema")]
        public int Schema { get; set; }

        /// <summary>Who wrote the note, e.g. "BakaLoader 1.1.0".</summary>
        [JsonProperty("writer")]
        public string Writer { get; set; }

        /// <summary>Where the files came from: "hexium" or "thunderstore".</summary>
        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("owner")]
        public string Owner { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// The version that was installed. A note whose version does not match the
        /// folder's own manifest is not believed: the folder has been replaced since.
        /// </summary>
        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("downloadUrl")]
        public string DownloadUrl { get; set; }

        [JsonProperty("fileSize")]
        public long? FileSize { get; set; }

        [JsonProperty("installedUtc")]
        public DateTime InstalledUtc { get; set; }
    }

    /// <summary>
    /// Reads, writes and clears the source note inside a mod folder.
    /// <para>
    /// The note is only believed when BakaLoader itself wrote it, the shape is one
    /// this build knows, and the version it names is the version the folder's own
    /// manifest names. Anything else is treated as if there were no note at all, so
    /// a file somebody dropped in by hand cannot talk BakaLoader into leaving a mod
    /// out of its updates.
    /// </para>
    /// <para>
    /// A note found inside a downloaded archive is never believed and never kept:
    /// it is deleted before the folder is put in place, so a package cannot ship one
    /// and claim a history it does not have.
    /// </para>
    /// </summary>
    public static class ModSourceMarkerFile
    {
        public const string FileName = ".bakaloader-source.json";

        /// <summary>The shape this build writes, and the only one it believes.</summary>
        public const int CurrentSchema = 1;

        /// <summary>The source that makes a row sit out the Thunderstore updates.</summary>
        public const string HexiumSource = "hexium";

        /// <summary>
        /// Written on a Thunderstore install since 1.2.15, so the Mods page can say when a mod
        /// went in. It changes nothing else: only <see cref="HexiumSource"/> and
        /// <see cref="LocalSource"/> are read as a reason to leave a mod out of an update.
        /// </summary>
        public const string ThunderstoreSource = "thunderstore";

        /// <summary>
        /// Written from 1.3.2 on a mod installed from a package zip on this PC, which is nearly
        /// always a test build. It makes the row sit out the Thunderstore updates exactly as a
        /// Hexium copy does, because SemVer ranks 1.4.0-beta.1 below 1.4.0 and the published
        /// release would otherwise replace the beta on the next unattended restart.
        /// </summary>
        public const string LocalSource = "local";

        /// <summary>Every writer name starts with this, and a note that does not is refused.</summary>
        public const string WriterPrefix = "BakaLoader";

        /// <summary>
        /// The note inside a mod folder, or null when there is none or it cannot be
        /// read. Says nothing about whether the note is believed.
        /// </summary>
        public static ModSourceMarker Read(string modFolder)
        {
            if (string.IsNullOrWhiteSpace(modFolder)) return null;

            try
            {
                var path = Path.Combine(modFolder, FileName);
                if (!File.Exists(path)) return null;

                return JsonConvert.DeserializeObject<ModSourceMarker>(File.ReadAllText(path));
            }
            catch
            {
                // An unreadable note is no note.
                return null;
            }
        }

        /// <summary>
        /// True when a note may be believed: BakaLoader wrote it, this build knows
        /// the shape, it names a source, and the version it names is the version the
        /// folder's manifest names.
        /// </summary>
        public static bool IsTrusted(ModSourceMarker marker, string manifestVersion)
        {
            if (marker == null) return false;
            if (marker.Schema != CurrentSchema) return false;
            if (string.IsNullOrWhiteSpace(marker.Writer)) return false;
            if (!marker.Writer.TrimStart().StartsWith(WriterPrefix, StringComparison.Ordinal)) return false;
            if (string.IsNullOrWhiteSpace(marker.Source)) return false;
            if (string.IsNullOrWhiteSpace(marker.Version) || string.IsNullOrWhiteSpace(manifestVersion)) return false;

            return string.Equals(marker.Version.Trim(), manifestVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The source a folder may be said to have come from ("hexium", "thunderstore" or "local"), or null when
        /// there is no note, the note is not believed, or it names a source this build
        /// does not know.
        /// </summary>
        public static string ReadTrustedSource(string modFolder, string manifestVersion) =>
            SourceOf(ReadTrusted(modFolder, manifestVersion));

        /// <summary>
        /// The note itself when it may be believed (<see cref="IsTrusted"/>) and names a source
        /// this build knows, or null. The scan reads the install time off it.
        /// </summary>
        public static ModSourceMarker ReadTrusted(string modFolder, string manifestVersion)
        {
            var marker = Read(modFolder);
            if (!IsTrusted(marker, manifestVersion)) return null;
            return SourceOf(marker) == null ? null : marker;
        }

        /// <summary>The source a believed note names, spelled the way this build spells it, or null.</summary>
        internal static string SourceOf(ModSourceMarker marker)
        {
            var source = marker?.Source?.Trim();
            if (string.Equals(source, HexiumSource, StringComparison.OrdinalIgnoreCase)) return HexiumSource;
            if (string.Equals(source, ThunderstoreSource, StringComparison.OrdinalIgnoreCase)) return ThunderstoreSource;
            if (string.Equals(source, LocalSource, StringComparison.OrdinalIgnoreCase)) return LocalSource;
            return null;
        }

        /// <summary>Writes the note into a mod folder, replacing any note already there.</summary>
        public static void Write(string modFolder, ModSourceMarker marker)
        {
            if (string.IsNullOrWhiteSpace(modFolder) || marker == null) return;

            Directory.CreateDirectory(modFolder);
            var path = Path.Combine(modFolder, FileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(marker, Formatting.Indented));
        }

        /// <summary>
        /// Deletes every source note under a folder tree, and answers how many it
        /// found. Run over a freshly-unpacked archive before its files are put in
        /// place, so a downloaded package can never supply its own history.
        /// </summary>
        public static int StripFrom(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;

            var removed = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // A note that will not delete is one the next read refuses anyway,
                        // because its version will not match a manifest we just replaced.
                    }
                }
            }
            catch
            {
                // An unwalkable tree is the extractor's problem, not this one's.
            }

            return removed;
        }
    }
}
