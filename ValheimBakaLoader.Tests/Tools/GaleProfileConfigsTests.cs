using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The config half of a Gale profile, over zips built by hand in memory and a realm folder in
    /// the temp directory. What is proved: a zip's config entries are found under either prefix
    /// and nothing else is taken; a hostile entry (rooted, a step up, a link) turns the whole zip
    /// down, as do too many entries, too much unpacked and a file that is not a zip; a file too
    /// large or of a kind the Configs hall does not show is left out and counted; a file the
    /// realm lacks is added, one it has in other words is changed, and one that differs only in
    /// line endings or a trailing newline is the same; the preview is a real unified diff and is
    /// cut at 400 lines with the rest counted; and the create path refuses what Resolve refuses.
    /// </summary>
    public class GaleProfileConfigsTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "vbl-galecfg-" + Guid.NewGuid().ToString("N"));
        private readonly string ConfigDir;

        public GaleProfileConfigsTests()
        {
            ConfigDir = Path.Combine(Root, "BepInEx", "config");
            Directory.CreateDirectory(ConfigDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
        }

        internal static byte[] Zip(params (string Name, string Text)[] entries) =>
            ZipWith(entries.Select(e => (e.Name, e.Text == null ? null : Encoding.UTF8.GetBytes(e.Text), 0)).ToArray());

        /// <summary>A zip with each entry's bytes and external attributes as given. A null body is a folder entry.</summary>
        internal static byte[] ZipWith(params (string Name, byte[] Bytes, int Attrs)[] entries)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, bytes, attrs) in entries)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                    if (attrs != 0) entry.ExternalAttributes = attrs;
                    if (bytes == null) continue;
                    using var s = entry.Open();
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            return buffer.ToArray();
        }

        private static GaleZipRead Read(byte[] zip)
        {
            using var stream = new MemoryStream(zip);
            return GaleProfileConfigs.ReadZip(stream);
        }

        private void Realm(string rel, string text)
        {
            var full = Path.Combine(ConfigDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Encoding.UTF8.GetBytes(text));
        }

        // ------------------------------------------------------------------ reading the zip

        [Fact]
        public void Config_entries_are_read_under_both_prefixes_and_nothing_else_is_taken()
        {
            var read = Read(Zip(
                ("export.r2x", "profileName: p\nmods: []"),
                ("config/", null),
                ("config/a.cfg", "A"),
                ("config/Sub/b.yaml", "B"),
                ("BepInEx/config/c.json", "C"),
                ("BepInEx/plugins/Mod/mod.dll", "MZ"),
                ("patchers/x.cfg", "not a config folder"),
                ("readme.md", "outside")));

            Assert.True(read.Ok);
            Assert.True(read.HasManifest);
            Assert.Equal(new[] { "a.cfg", "c.json", "Sub/b.yaml" }, read.Files.Select(f => f.Rel).ToArray());
            Assert.Equal("B", Encoding.UTF8.GetString(read.Files.Single(f => f.Rel == "Sub/b.yaml").Bytes));
            Assert.Equal(0, read.Skipped);
        }

        [Fact]
        public void The_prefix_is_matched_without_case_and_a_backslash_name_is_read_as_a_slash()
        {
            var read = Read(Zip(("Config\\Sub\\a.cfg", "A"), ("bepinex/CONFIG/b.cfg", "B")));
            Assert.True(read.Ok);
            Assert.Equal(new[] { "b.cfg", "Sub/a.cfg" }, read.Files.Select(f => f.Rel).ToArray());
        }

        [Theory]
        [InlineData("config/../evil.cfg")]
        [InlineData("../config/evil.cfg")]
        [InlineData("/config/evil.cfg")]
        [InlineData("C:/config/evil.cfg")]
        [InlineData("config/a.cfg:stream")]
        [InlineData("somewhere/../../evil.cfg")]
        public void A_hostile_name_anywhere_turns_the_whole_zip_down(string name)
        {
            var read = Read(Zip(("config/good.cfg", "fine"), (name, "bad")));
            Assert.False(read.Ok);
            Assert.Equal(GaleProfileConfigs.UnsafeSentence, read.Refusal);
            Assert.Empty(read.Files);
        }

        [Fact]
        public void An_entry_that_is_a_link_turns_the_whole_zip_down()
        {
            // S_IFLNK in the unix half of the attributes, the way zip writes a symlink.
            var unixLink = unchecked((int)(0xA1FFu << 16));
            var read = Read(ZipWith(("config/good.cfg", Encoding.UTF8.GetBytes("fine"), 0),
                ("config/link.cfg", Encoding.UTF8.GetBytes("/etc/passwd"), unixLink)));
            Assert.Equal(GaleProfileConfigs.UnsafeSentence, read.Refusal);

            var reparse = Read(ZipWith(("config/link.cfg", Encoding.UTF8.GetBytes("x"), (int)FileAttributes.ReparsePoint)));
            Assert.Equal(GaleProfileConfigs.UnsafeSentence, reparse.Refusal);

            // An ordinary unix file mode is not a link.
            var plain = Read(ZipWith(("config/a.cfg", Encoding.UTF8.GetBytes("x"), unchecked((int)(0x81A4u << 16)))));
            Assert.True(plain.Ok);
        }

        [Fact]
        public void More_entries_than_the_cap_turns_the_zip_down()
        {
            var entries = Enumerable.Range(0, GaleProfileConfigs.MaxEntries + 1)
                .Select(i => ("config/f" + i + ".cfg", "x")).ToArray();
            Assert.Equal(GaleProfileConfigs.TooManySentence, Read(Zip(entries)).Refusal);

            var atCap = Enumerable.Range(0, GaleProfileConfigs.MaxEntries).Select(i => ("config/f" + i + ".cfg", "x")).ToArray();
            Assert.True(Read(Zip(atCap)).Ok);
        }

        [Fact]
        public void A_file_over_two_megabytes_is_left_out_and_counted_and_one_at_the_cap_is_read()
        {
            var big = new string('x', GaleProfileConfigs.MaxEntryBytes + 1);
            var cap = new string('y', GaleProfileConfigs.MaxEntryBytes);
            var read = Read(Zip(("config/big.cfg", big), ("config/cap.cfg", cap), ("config/small.cfg", "s")));
            Assert.True(read.Ok);
            Assert.Equal(new[] { "cap.cfg", "small.cfg" }, read.Files.Select(f => f.Rel).ToArray());
            Assert.Equal(1, read.Skipped);
        }

        [Fact]
        public void Too_much_unpacked_altogether_turns_the_zip_down()
        {
            // Thirty files just under the per-file cap come to more than the total.
            var one = new string('z', GaleProfileConfigs.MaxEntryBytes - 10);
            var entries = Enumerable.Range(0, 30).Select(i => ("config/f" + i + ".cfg", one)).ToArray();
            Assert.Equal(GaleProfileConfigs.TooLargeSentence, Read(Zip(entries)).Refusal);
        }

        [Fact]
        public void A_kind_the_Configs_hall_does_not_show_and_a_second_copy_are_left_out_and_counted()
        {
            var read = Read(Zip(("config/notes.txt", "t"), ("config/data.dll", "MZ"), ("config/a.cfg", "1"),
                ("BepInEx/config/A.cfg", "2"), ("config/bad /x.cfg", "trailing space")));
            Assert.True(read.Ok);
            Assert.Equal("a.cfg", Assert.Single(read.Files).Rel);
            Assert.Equal("1", Encoding.UTF8.GetString(read.Files[0].Bytes));
            Assert.Equal(4, read.Skipped);
        }

        [Fact]
        public void Bytes_that_are_not_a_zip_are_refused_with_their_sentence()
        {
            Assert.Equal(GaleProfileConfigs.NotZipSentence, Read(Encoding.UTF8.GetBytes("<html>not a zip</html>")).Refusal);
            Assert.Equal(GaleProfileConfigs.NotZipSentence, Read(Array.Empty<byte>()).Refusal);
            Assert.Equal(GaleProfileConfigs.NotZipSentence, GaleProfileConfigs.ReadZip(Path.Combine(Root, "missing.zip")).Refusal);
        }

        // ------------------------------------------------------------------ the states

        [Fact]
        public void Added_changed_and_same_against_the_realm()
        {
            Realm("same.cfg", "[General]\r\nA = 1\r\n");
            Realm("changed.cfg", "[General]\nA = 1\n");
            var files = Read(Zip(("config/same.cfg", "[General]\nA = 1"), ("config/changed.cfg", "[General]\nA = 2\n"),
                ("config/New/added.cfg", "B = 1\n"))).Files;

            var compared = GaleProfileConfigs.Compare(files, ConfigDir);

            var states = compared.Rows.ToDictionary(r => r.Rel, r => r.State);
            Assert.Equal(GaleConfigState.Same, states["same.cfg"]);
            Assert.Equal(GaleConfigState.Changed, states["changed.cfg"]);
            Assert.Equal(GaleConfigState.Added, states["New/added.cfg"]);
            Assert.Equal((1, 1, 1), (compared.Added, compared.Changed, compared.Same));
            Assert.Equal("BepInEx/config/New/added.cfg", compared.Rows.Single(r => r.Rel == "New/added.cfg").RealmPath);
            Assert.Empty(compared.Rows.Single(r => r.State == GaleConfigState.Same).Preview);
            Assert.Equal(new[] { '+' }, compared.Rows.Single(r => r.State == GaleConfigState.Added).Preview.Select(l => l.Kind).ToArray());
        }

        [Theory]
        [InlineData("a\r\nb\r\n", "a\nb")]
        [InlineData("a\nb\n\n", "a\nb")]
        [InlineData("a\rb", "a\nb\n")]
        [InlineData("\uFEFFa\nb", "a\nb")]
        public void Line_endings_a_trailing_newline_and_a_byte_order_mark_are_the_same_file(string realm, string profile)
        {
            var bom = realm.StartsWith("\uFEFF", StringComparison.Ordinal);
            var bytes = bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(realm.Substring(1))).ToArray() : Encoding.UTF8.GetBytes(realm);
            Assert.Equal(GaleConfigState.Same, GaleProfileConfigs.Row("x.cfg", bytes, Encoding.UTF8.GetBytes(profile)).State);
            Assert.True(GaleProfileConfigs.SameText(bytes, Encoding.UTF8.GetBytes(profile)));
        }

        [Fact]
        public void A_changed_word_on_one_line_is_changed()
        {
            Assert.Equal(GaleConfigState.Changed,
                GaleProfileConfigs.Row("x.cfg", Encoding.UTF8.GetBytes("a = 1\r\n"), Encoding.UTF8.GetBytes("a = 2\r\n")).State);
        }

        [Fact]
        public void A_file_behind_a_junction_on_this_realm_is_left_out_and_counted()
        {
            var outside = Path.Combine(Root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "x.cfg"), "theirs");
            InstallIsolationService.CreateJunctionOrThrow(Path.Combine(ConfigDir, "link"), outside);

            var compared = GaleProfileConfigs.Compare(Read(Zip(("config/link/x.cfg", "ours"), ("config/a.cfg", "a"))).Files, ConfigDir);

            Assert.Equal("a.cfg", Assert.Single(compared.Rows).Rel);
            Assert.Equal(1, compared.Skipped);
        }

        // ------------------------------------------------------------------ the preview

        [Fact]
        public void The_preview_is_a_unified_diff_with_context_and_a_hunk_head()
        {
            var a = Enumerable.Range(1, 20).Select(i => "line " + i).ToList();
            var b = a.ToList();
            b[9] = "line ten";
            b.Insert(15, "inserted");

            var lines = GaleProfileConfigs.Unified(a, b);
            var text = string.Join("\n", lines.Select(l => l.Kind + l.Text));

            Assert.Equal('@', lines[0].Kind);
            Assert.Equal("@@ -7,12 +7,13 @@", lines[0].Text);
            Assert.Contains("-line 10", text);
            Assert.Contains("+line ten", text);
            Assert.Contains("+inserted", text);
            Assert.Contains(" line 7", text);
            Assert.DoesNotContain("line 6", text);
            Assert.DoesNotContain("line 20", text);
            Assert.Equal(1, lines.Count(l => l.Kind == '-'));
            Assert.Equal(2, lines.Count(l => l.Kind == '+'));
        }

        [Fact]
        public void Two_changes_far_apart_are_two_hunks()
        {
            var a = Enumerable.Range(1, 40).Select(i => "l" + i).ToList();
            var b = a.ToList();
            b[2] = "x";
            b[35] = "y";
            var heads = GaleProfileConfigs.Unified(a, b).Where(l => l.Kind == '@').Select(l => l.Text).ToList();
            Assert.Equal(new[] { "@@ -1,6 +1,6 @@", "@@ -33,7 +33,7 @@" }, heads);
        }

        [Fact]
        public void A_preview_past_four_hundred_lines_is_cut_and_the_rest_counted()
        {
            var profile = string.Join("\n", Enumerable.Range(0, 1000).Select(i => "k" + i + " = 1"));
            var row = GaleProfileConfigs.Row("big.cfg", null, Encoding.UTF8.GetBytes(profile));
            Assert.Equal(GaleProfileConfigs.MaxPreviewLines, row.Preview.Count);
            Assert.Equal(600, row.More);

            var small = GaleProfileConfigs.Row("small.cfg", null, Encoding.UTF8.GetBytes("a\nb"));
            Assert.Equal(2, small.Preview.Count);
            Assert.Equal(0, small.More);
        }

        [Fact]
        public void A_middle_too_large_to_match_still_says_what_goes_and_what_comes()
        {
            var a = Enumerable.Range(0, 3000).Select(i => "a" + i).ToList();
            var b = Enumerable.Range(0, 3000).Select(i => "b" + i).ToList();
            var lines = GaleProfileConfigs.Unified(a, b);
            Assert.Equal(3000, lines.Count(l => l.Kind == '-'));
            Assert.Equal(3000, lines.Count(l => l.Kind == '+'));
        }

        // ------------------------------------------------------------------ the create path

        [Theory]
        [InlineData("../evil.cfg")]
        [InlineData("a/../../evil.cfg")]
        [InlineData("C:\\evil.cfg")]
        [InlineData("\\\\server\\share\\evil.cfg")]
        [InlineData("evil.txt")]
        [InlineData("a/./b.cfg")]
        [InlineData("")]
        public void The_create_path_refuses_what_Resolve_refuses(string rel)
        {
            Assert.ThrowsAny<ArgumentException>(() => ConfigFiles.ResolveForCreate(ConfigDir, rel, out _));
        }

        [Fact]
        public void The_create_path_takes_a_new_file_in_new_folders_and_says_whether_it_is_there()
        {
            var full = ConfigFiles.ResolveForCreate(ConfigDir, "New/Deeper/x.cfg", out var exists);
            Assert.False(exists);
            Assert.Equal(Path.Combine(ConfigDir, "New", "Deeper", "x.cfg"), full);
            Assert.False(Directory.Exists(Path.Combine(ConfigDir, "New")), "resolving made a folder");

            var made = ConfigFiles.CreateFoldersFor(ConfigDir, "New/Deeper/x.cfg");
            Assert.Equal(full, made);
            Assert.True(Directory.Exists(Path.Combine(ConfigDir, "New", "Deeper")));
            Assert.False(File.Exists(made), "making the folders made the file");

            Realm("here.cfg", "x");
            ConfigFiles.ResolveForCreate(ConfigDir, "here.cfg", out var there);
            Assert.True(there);
        }

        [Fact]
        public void The_create_path_will_not_go_through_a_junction_or_take_a_folder_for_a_file()
        {
            var outside = Path.Combine(Root, "outside");
            Directory.CreateDirectory(outside);
            InstallIsolationService.CreateJunctionOrThrow(Path.Combine(ConfigDir, "link"), outside);

            Assert.ThrowsAny<ArgumentException>(() => ConfigFiles.ResolveForCreate(ConfigDir, "link/new.cfg", out _));
            Assert.ThrowsAny<ArgumentException>(() => ConfigFiles.CreateFoldersFor(ConfigDir, "link/deeper/new.cfg"));
            Assert.False(Directory.Exists(Path.Combine(outside, "deeper")), "a folder was made through the junction");

            Directory.CreateDirectory(Path.Combine(ConfigDir, "folder.cfg"));
            Assert.ThrowsAny<ArgumentException>(() => ConfigFiles.ResolveForCreate(ConfigDir, "folder.cfg", out _));
        }

        [Fact]
        public void Resolve_still_refuses_a_file_that_is_not_there()
        {
            Assert.Throws<FileNotFoundException>(() => ConfigFiles.Resolve(ConfigDir, "missing.cfg"));
        }
    }
}
