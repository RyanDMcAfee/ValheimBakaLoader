using System;
using System.IO;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The note BakaLoader leaves inside a mod folder it installed from the second site.
    /// It is the only thing that makes a row sit out the ordinary update path, so it is
    /// only believed when BakaLoader wrote it, this build knows the shape, and it names
    /// the same version the folder's own manifest names. A note anybody could drop in by
    /// hand must not be able to talk BakaLoader into leaving a mod alone.
    /// <para>
    /// Everything here works under a temporary folder. No real BepInEx tree is touched.
    /// </para>
    /// </summary>
    public class ModSourceMarkerTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-marker-" + Guid.NewGuid().ToString("N"));

        public ModSourceMarkerTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        private string Folder(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteRaw(string folder, object marker) =>
            File.WriteAllText(Path.Combine(folder, ModSourceMarkerFile.FileName),
                JsonConvert.SerializeObject(marker));

        private static ModSourceMarker Good(string version = "1.2.3") => new()
        {
            Schema = ModSourceMarkerFile.CurrentSchema,
            Writer = "BakaLoader 1.1.0",
            Source = ModSourceMarkerFile.HexiumSource,
            Owner = "DrakeMods",
            Name = "LockSmith",
            Version = version,
            DownloadUrl = "https://cdn.hexium.gg/upload/1255/1.2.3.zip",
            FileSize = 889308,
            InstalledUtc = DateTime.UtcNow,
        };

        [Fact]
        public void A_note_BakaLoader_wrote_for_this_version_is_believed()
        {
            var folder = Folder("DrakeMods-LockSmith");
            ModSourceMarkerFile.Write(folder, Good());

            Assert.Equal("hexium", ModSourceMarkerFile.ReadTrustedSource(folder, "1.2.3"));
        }

        [Fact]
        public void A_note_naming_a_different_version_than_the_manifest_is_not_believed()
        {
            // The folder has been replaced since the note was written, so these files are
            // no longer the ones it describes.
            var folder = Folder("DrakeMods-LockSmith");
            ModSourceMarkerFile.Write(folder, Good("1.2.3"));

            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(folder, "1.3.0"));
        }

        [Fact]
        public void A_note_somebody_else_wrote_is_not_believed()
        {
            var folder = Folder("Someone-Mod");
            WriteRaw(folder, new
            {
                schema = 1,
                writer = "ModManager 4",
                source = "hexium",
                owner = "Someone",
                name = "Mod",
                version = "1.2.3",
            });

            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(folder, "1.2.3"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(99)]
        public void A_note_in_a_shape_this_build_does_not_know_is_not_believed(int schema)
        {
            var folder = Folder("Someone-Mod" + schema);
            WriteRaw(folder, new
            {
                schema,
                writer = "BakaLoader 1.1.0",
                source = "hexium",
                version = "1.2.3",
            });

            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(folder, "1.2.3"));
        }

        [Fact]
        public void A_note_naming_a_source_this_build_does_not_know_is_not_believed()
        {
            var folder = Folder("Someone-Mod");
            WriteRaw(folder, new
            {
                schema = 1,
                writer = "BakaLoader 1.1.0",
                source = "somewhere-else",
                version = "1.2.3",
            });

            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(folder, "1.2.3"));
        }

        [Fact]
        public void A_folder_with_no_note_and_a_folder_with_an_unreadable_one_read_the_same()
        {
            var none = Folder("No-Note");
            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(none, "1.2.3"));

            var broken = Folder("Broken-Note");
            File.WriteAllText(Path.Combine(broken, ModSourceMarkerFile.FileName), "{ this is not json");
            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(broken, "1.2.3"));

            var empty = Folder("Empty-Note");
            File.WriteAllText(Path.Combine(empty, ModSourceMarkerFile.FileName), "");
            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(empty, "1.2.3"));
        }

        [Fact]
        public void A_folder_with_no_manifest_version_is_never_believed()
        {
            var folder = Folder("DrakeMods-LockSmith");
            ModSourceMarkerFile.Write(folder, Good("unknown"));

            // "unknown" is what a folder with no manifest reads as, and a note cannot
            // claim a version there is no manifest to check it against.
            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(folder, null));
            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(folder, ""));
        }

        [Fact]
        public void A_note_that_came_inside_a_downloaded_package_is_deleted_before_the_folder_is_placed()
        {
            var extracted = Folder("extracted");
            var nested = Path.Combine(extracted, "plugins", "Deep");
            Directory.CreateDirectory(nested);

            WriteRaw(extracted, Good());
            WriteRaw(nested, Good());
            File.WriteAllText(Path.Combine(extracted, "manifest.json"), "{}");

            var removed = ModSourceMarkerFile.StripFrom(extracted);

            Assert.Equal(2, removed);
            Assert.False(File.Exists(Path.Combine(extracted, ModSourceMarkerFile.FileName)));
            Assert.False(File.Exists(Path.Combine(nested, ModSourceMarkerFile.FileName)));
            // Everything else in the package is left exactly as it came.
            Assert.True(File.Exists(Path.Combine(extracted, "manifest.json")));
        }

        [Fact]
        public void Stripping_a_folder_with_no_notes_is_a_no_op()
        {
            var extracted = Folder("clean");
            File.WriteAllText(Path.Combine(extracted, "manifest.json"), "{}");

            Assert.Equal(0, ModSourceMarkerFile.StripFrom(extracted));
            Assert.Equal(0, ModSourceMarkerFile.StripFrom(Path.Combine(Root, "does-not-exist")));
            Assert.Equal(0, ModSourceMarkerFile.StripFrom(null));
        }

        [Fact]
        public void The_note_the_app_writes_carries_everything_a_later_read_needs()
        {
            var folder = Folder("DrakeMods-LockSmith");
            ModSourceMarkerFile.Write(folder, Good());

            var read = ModSourceMarkerFile.Read(folder);

            Assert.Equal(1, read.Schema);
            Assert.StartsWith("BakaLoader", read.Writer);
            Assert.Equal("hexium", read.Source);
            Assert.Equal("DrakeMods", read.Owner);
            Assert.Equal("LockSmith", read.Name);
            Assert.Equal("1.2.3", read.Version);
            Assert.Equal("https://cdn.hexium.gg/upload/1255/1.2.3.zip", read.DownloadUrl);
            Assert.Equal(889308, read.FileSize);
            Assert.True(read.InstalledUtc > DateTime.UtcNow.AddMinutes(-5));
        }
    }
}
