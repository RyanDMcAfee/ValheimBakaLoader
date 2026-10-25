using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The local feed a test copy gives its source realm (1.3.0, H1 item 3), against two temp
    /// realm trees: the mod list with versions and folder hashes, the comparison it feeds
    /// (adds, updates, same version with different files, on this server only), and the config
    /// files read in the profile zip's shape. No network, no real install.
    /// </summary>
    public class TestCopyGaleFeedTests : IDisposable
    {
        private readonly string Root;
        private readonly string SourceBep;
        private readonly string CopyBep;
        private readonly ModScanner Scanner = new(new Mock<IApplicationLogger>().Object);

        public TestCopyGaleFeedTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-tcfeed-" + Guid.NewGuid().ToString("N"));
            SourceBep = Path.Combine(Root, "source", "BepInEx");
            CopyBep = Path.Combine(Root, "copy", "BepInEx");
            foreach (var bep in new[] { SourceBep, CopyBep })
            {
                Directory.CreateDirectory(Path.Combine(bep, "plugins"));
                Directory.CreateDirectory(Path.Combine(bep, "patchers"));
                Directory.CreateDirectory(Path.Combine(bep, "config"));
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
        }

        private static void Mod(string bep, string folder, string version, string dllText, string dllName = null, string under = "plugins")
        {
            var dir = Path.Combine(bep, under, folder);
            Directory.CreateDirectory(dir);
            if (version != null)
            {
                var name = folder.Contains('-') ? folder.Substring(folder.IndexOf('-') + 1) : folder;
                File.WriteAllText(Path.Combine(dir, "manifest.json"),
                    "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\",\"website_url\":\"\",\"description\":\"\",\"dependencies\":[]}");
            }
            File.WriteAllText(Path.Combine(dir, dllName ?? (folder + ".dll")), dllText);
        }

        private TestCopyGaleFeed Feed() => new("Sunset_bakaT", Path.Combine(CopyBep, "plugins"), Path.Combine(CopyBep, "config"), Scanner);

        /// <summary>The source's own mods as the Gale check reads them against a test copy.</summary>
        private IEnumerable<(string Name, string Version, string Hash)> Installed() =>
            Scanner.ScanPlugins(Path.Combine(SourceBep, "plugins"))
                .Select(m => (TestCopyGaleFeed.NameOf(m), TestCopyGaleFeed.VersionOf(m), TestCopyGaleFeed.FolderHash(m)))
                .ToList();

        private void TheUsualPair()
        {
            Mod(CopyBep, "Someone-NewMod", "1.0.0", "new");
            Mod(SourceBep, "Someone-Bumped", "1.0.0", "old build");
            Mod(CopyBep, "Someone-Bumped", "1.1.0", "new build");
            Mod(SourceBep, "Someone-Rebuilt", "2.0.0", "first build");
            Mod(CopyBep, "Someone-Rebuilt", "2.0.0", "second build, same version");
            Mod(SourceBep, "Someone-Same", "3.0.0", "identical");
            Mod(CopyBep, "Someone-Same", "3.0.0", "identical");
            Mod(SourceBep, "Someone-SourceOnly", "1.2.3", "only on the source");
            // A mod made by hand: no author in the folder name and no manifest.
            Mod(SourceBep, "LooseMod", null, "loose v1");
            Mod(CopyBep, "LooseMod", null, "loose v2");
            Mod(SourceBep, "LooseSame", null, "the same loose files");
            Mod(CopyBep, "LooseSame", null, "the same loose files");
        }

        [Fact]
        public async Task The_feed_lists_the_copys_mods_with_versions_and_hashes_as_a_local_profile()
        {
            TheUsualPair();
            var result = await Feed().GetMetaAsync("ignored");

            Assert.True(result.Ok);
            Assert.True(result.Meta.LocalFeed);
            Assert.Equal("valheim", result.Meta.Community);
            Assert.StartsWith(TestCopyGaleFeed.StampPrefix, result.Meta.UpdatedAt);

            var byName = result.Meta.Mods.ToDictionary(m => m.Name);
            Assert.Equal(new[] { "LooseMod", "LooseSame", "Someone-Bumped", "Someone-NewMod", "Someone-Rebuilt", "Someone-Same" },
                byName.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("1.1.0", byName["Someone-Bumped"].Version);
            Assert.Equal("unknown", byName["LooseMod"].Version);
            Assert.All(byName.Values, m => Assert.True(m.Enabled));
            Assert.All(byName.Values, m => Assert.Matches("^[0-9a-f]{64}$", m.ContentHash));
        }

        [Fact]
        public async Task Against_the_source_it_gives_adds_updates_changed_same_and_only_here()
        {
            TheUsualPair();
            var meta = (await Feed().GetMetaAsync(null)).Meta;
            var diff = GaleProfileDiff.Compare(meta, Installed(), Array.Empty<string>());
            var kinds = diff.Rows.ToDictionary(r => r.Name, r => r.Kind);

            Assert.Equal(GaleDiffKind.Add, kinds["Someone-NewMod"]);
            Assert.Equal(GaleDiffKind.Update, kinds["Someone-Bumped"]);
            Assert.Equal(GaleDiffKind.Changed, kinds["Someone-Rebuilt"]);
            Assert.Equal(GaleDiffKind.Same, kinds["Someone-Same"]);
            Assert.Equal(GaleDiffKind.NotInProfile, kinds["Someone-SourceOnly"]);
            // The hand made mod is compared by its files, not skipped and not "only here".
            Assert.Equal(GaleDiffKind.Changed, kinds["LooseMod"]);
            Assert.Equal(GaleDiffKind.Same, kinds["LooseSame"]);
            Assert.Equal(0, diff.Skipped);
            Assert.Equal(2, diff.ChangedCount);
            Assert.True(diff.HasWork);

            var rebuilt = diff.Rows.Single(r => r.Name == "Someone-Rebuilt");
            Assert.Equal("2.0.0", rebuilt.ProfileVersion);
            Assert.Equal("2.0.0", rebuilt.ServerVersion);
        }

        [Fact]
        public async Task A_mod_switched_off_on_the_copy_differs_from_the_same_mod_switched_on()
        {
            Mod(SourceBep, "Someone-Switch", "1.0.0", "the dll");
            Mod(CopyBep, "Someone-Switch", "1.0.0", "the dll", dllName: "Someone-Switch.dll" + ModSwitch.OffSuffix);

            var meta = (await Feed().GetMetaAsync(null)).Meta;
            var row = Assert.Single(GaleProfileDiff.Compare(meta, Installed(), null).Rows);
            Assert.Equal(GaleDiffKind.Changed, row.Kind);
        }

        [Fact]
        public async Task The_note_BakaLoader_leaves_in_a_folder_is_not_part_of_the_hash_but_gives_a_version()
        {
            Mod(SourceBep, "NoManifest", null, "same files");
            Mod(CopyBep, "NoManifest", null, "same files");
            File.WriteAllText(Path.Combine(SourceBep, "plugins", "NoManifest", ModSourceMarkerFile.FileName),
                "{\"Schema\":1,\"Writer\":\"BakaLoader 1.2.0\",\"Source\":\"thunderstore\",\"Version\":\"4.5.6\",\"InstalledUtc\":\"2026-01-01T00:00:00Z\"}");
            File.WriteAllText(Path.Combine(CopyBep, "plugins", "NoManifest", ModSourceMarkerFile.FileName),
                "{\"Schema\":1,\"Writer\":\"BakaLoader 1.2.0\",\"Source\":\"thunderstore\",\"Version\":\"4.5.6\",\"InstalledUtc\":\"2026-10-24T09:00:00Z\"}");

            var meta = (await Feed().GetMetaAsync(null)).Meta;
            Assert.Equal("4.5.6", Assert.Single(meta.Mods).Version);
            Assert.Equal(GaleDiffKind.Same, Assert.Single(GaleProfileDiff.Compare(meta, Installed(), null).Rows).Kind);
        }

        [Fact]
        public async Task A_patcher_part_is_in_the_hash_and_the_fingerprint_moves_only_when_the_copy_does()
        {
            Mod(SourceBep, "Someone-Both", "1.0.0", "plugin");
            Mod(CopyBep, "Someone-Both", "1.0.0", "plugin");
            Mod(SourceBep, "Someone-Both", null, "patcher one", dllName: "Patch.dll", under: "patchers");
            Mod(CopyBep, "Someone-Both", null, "patcher two", dllName: "Patch.dll", under: "patchers");

            var first = (await Feed().GetMetaAsync(null)).Meta;
            Assert.Equal(GaleDiffKind.Changed, Assert.Single(GaleProfileDiff.Compare(first, Installed(), null).Rows).Kind);

            var again = (await Feed().GetMetaAsync(null)).Meta;
            Assert.Equal(first.UpdatedAt, again.UpdatedAt);

            File.WriteAllText(Path.Combine(CopyBep, "patchers", "Someone-Both", "Patch.dll"), "patcher one");
            File.SetLastWriteTimeUtc(Path.Combine(CopyBep, "patchers", "Someone-Both", "Patch.dll"), DateTime.UtcNow.AddMinutes(1));
            var moved = (await Feed().GetMetaAsync(null)).Meta;
            Assert.NotEqual(first.UpdatedAt, moved.UpdatedAt);
            Assert.Equal(GaleDiffKind.Same, Assert.Single(GaleProfileDiff.Compare(moved, Installed(), null).Rows).Kind);
        }

        [Fact]
        public void The_configs_are_read_in_the_zip_shape_without_the_per_realm_files()
        {
            File.WriteAllText(Path.Combine(CopyBep, "config", "a.mod.cfg"), "[A]\nkey = 2\n");
            File.WriteAllText(Path.Combine(SourceBep, "config", "a.mod.cfg"), "[A]\nkey = 1\n");
            File.WriteAllText(Path.Combine(CopyBep, "config", "new.mod.cfg"), "[New]\n");
            File.WriteAllText(Path.Combine(CopyBep, "config", "same.cfg"), "[S]\n");
            File.WriteAllText(Path.Combine(SourceBep, "config", "same.cfg"), "[S]\n");
            Directory.CreateDirectory(Path.Combine(CopyBep, "config", "sub"));
            File.WriteAllText(Path.Combine(CopyBep, "config", "sub", "deep.yml"), "a: 1\n");
            File.WriteAllText(Path.Combine(CopyBep, "config", "notes.txt"), "not a config kind");
            foreach (var perRealm in TestCopyGaleFeed.PerRealmConfigs)
                File.WriteAllText(Path.Combine(CopyBep, "config", perRealm), "[Server]\nPort = 25576\n");

            var read = Feed().ReadConfigs();
            Assert.True(read.Ok);
            Assert.Equal(new[] { "a.mod.cfg", "new.mod.cfg", "same.cfg", "sub/deep.yml" }, read.Files.Select(f => f.Rel).ToArray());
            Assert.DoesNotContain(read.Files, f => TestCopyGaleFeed.PerRealmConfigs.Contains(f.Rel, StringComparer.OrdinalIgnoreCase));

            var comparison = GaleProfileConfigs.Compare(read.Files, Path.Combine(SourceBep, "config"));
            var states = comparison.Rows.ToDictionary(r => r.Rel, r => r.State);
            Assert.Equal(GaleConfigState.Changed, states["a.mod.cfg"]);
            Assert.Equal(GaleConfigState.Added, states["new.mod.cfg"]);
            Assert.Equal(GaleConfigState.Same, states["same.cfg"]);
            Assert.Equal(GaleConfigState.Added, states["sub/deep.yml"]);
        }

        [Fact]
        public async Task The_zip_the_feed_writes_reads_back_to_the_same_files_and_goes_away_on_dispose()
        {
            File.WriteAllText(Path.Combine(CopyBep, "config", "a.mod.cfg"), "[A]\nkey = 2\n");
            File.WriteAllText(Path.Combine(CopyBep, "config", "b.json"), "{}");

            string path;
            using (var zip = await Feed().GetProfileZipAsync(null))
            {
                Assert.True(zip.Ok);
                path = zip.ZipPath;
                var read = GaleProfileConfigs.ReadZip(path);
                Assert.True(read.Ok);
                Assert.Equal(Feed().ReadConfigs().Files.Select(f => f.Rel + "=" + Encoding.UTF8.GetString(f.Bytes)),
                    read.Files.Select(f => f.Rel + "=" + Encoding.UTF8.GetString(f.Bytes)));
            }
            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task A_refused_feed_answers_its_sentence_and_reads_nothing()
        {
            var refused = TestCopyGaleFeed.Refused("Gone_bakaT", TestCopyGaleFeed.GoneSentence);
            var meta = await refused.GetMetaAsync(null);
            Assert.False(meta.Ok);
            Assert.Equal(TestCopyGaleFeed.GoneSentence, meta.SentenceId);
            Assert.Equal(TestCopyGaleFeed.GoneSentence, refused.ReadConfigs().Refusal);
            using var zip = await refused.GetProfileZipAsync(null);
            Assert.False(zip.Ok);
        }

        [Fact]
        public async Task A_copy_with_no_plugins_folder_has_no_mods_and_a_missing_config_folder_has_no_files()
        {
            Mod(SourceBep, "Someone-Here", "1.0.0", "x");
            var feed = new TestCopyGaleFeed("Empty_bakaT", Path.Combine(Root, "nowhere", "plugins"), Path.Combine(Root, "nowhere", "config"), Scanner);
            var meta = (await feed.GetMetaAsync(null)).Meta;
            Assert.Empty(meta.Mods);
            Assert.Equal(GaleDiffKind.NotInProfile, Assert.Single(GaleProfileDiff.Compare(meta, Installed(), null).Rows).Kind);
            var read = feed.ReadConfigs();
            Assert.True(read.Ok);
            Assert.Empty(read.Files);
        }

        // ------------------------------------------------------------------ the diff kind on its own

        private static GaleProfileMeta Profile(bool local, params (string Name, string Version, string Hash)[] mods) => new()
        {
            UpdatedAt = "stamp",
            LocalFeed = local,
            Mods = mods.Select(m => new GaleProfileMod { Name = m.Name, Enabled = true, Version = m.Version, ContentHash = m.Hash }).ToList(),
        };

        [Fact]
        public void Changed_needs_a_hash_on_both_sides_and_a_Gale_profile_never_gives_it()
        {
            // Gale's meta has no hashes: the same version is Same whatever the server's files are.
            var gale = Profile(false, ("Someone-A", "1.0.0", null));
            Assert.Equal(GaleDiffKind.Same, GaleProfileDiff.Compare(gale, new[] { ("Someone-A", "1.0.0", "abc") }, null).Rows.Single().Kind);
            Assert.Equal(GaleDiffKind.Same, GaleProfileDiff.Compare(gale, new[] { ("Someone-A", "1.0.0") }, null).Rows.Single().Kind);

            var local = Profile(true, ("Someone-A", "1.0.0", "abc"));
            Assert.Equal(GaleDiffKind.Same, GaleProfileDiff.Compare(local, new[] { ("Someone-A", "1.0.0", "abc") }, null).Rows.Single().Kind);
            Assert.Equal(GaleDiffKind.Changed, GaleProfileDiff.Compare(local, new[] { ("Someone-A", "1.0.0", "abd") }, null).Rows.Single().Kind);
            // A server mod whose files could not be read is compared by version only.
            Assert.Equal(GaleDiffKind.Same, GaleProfileDiff.Compare(local, new[] { ("Someone-A", "1.0.0", (string)null) }, null).Rows.Single().Kind);
            // A different version is an update whatever the hashes say.
            Assert.Equal(GaleDiffKind.Update, GaleProfileDiff.Compare(local, new[] { ("Someone-A", "0.9.0", "abc") }, null).Rows.Single().Kind);
        }

        [Fact]
        public void A_changed_mod_is_work_can_be_unticked_and_raises_the_row_only_when_the_copy_moved()
        {
            var local = Profile(true, ("Someone-A", "1.0.0", "new"));
            var installed = new[] { ("Someone-A", "1.0.0", "old") };

            var diff = GaleProfileDiff.Compare(local, installed, null);
            Assert.Equal(1, diff.ChangedCount);
            Assert.True(diff.HasWork);
            Assert.True(GaleProfileDiff.IsDue(diff, null));
            Assert.False(GaleProfileDiff.IsDue(diff, "stamp"));

            var unticked = GaleProfileDiff.Compare(local, installed, new[] { "Someone-A" }).Rows.Single();
            Assert.Equal(GaleDiffKind.Unticked, unticked.Kind);
            Assert.Equal(GaleDiffKind.Changed, unticked.UntickedFrom);
            Assert.False(GaleProfileDiff.Compare(local, installed, new[] { "Someone-A" }).HasWork);
        }

        [Fact]
        public void Only_a_local_feed_may_name_a_mod_by_a_bare_folder_name_or_carry_no_readable_version()
        {
            var bare = new GaleProfileMod { Name = "LooseMod", Enabled = true, Version = "unknown", ContentHash = "h" };
            var gale = new GaleProfileMeta { Mods = new[] { bare } };
            var local = new GaleProfileMeta { Mods = new[] { bare }, LocalFeed = true };

            var fromGale = GaleProfileDiff.Compare(gale, new[] { ("LooseMod", "unknown", "h") }, null);
            Assert.Equal(1, fromGale.Skipped);
            Assert.Equal(GaleDiffKind.NotInProfile, fromGale.Rows.Single().Kind);

            var fromCopy = GaleProfileDiff.Compare(local, new[] { ("LooseMod", "unknown", "h") }, null);
            Assert.Equal(0, fromCopy.Skipped);
            Assert.Equal(GaleDiffKind.Same, fromCopy.Rows.Single().Kind);

            // An unreadable version against a readable one is an update: the copy cannot show it has it.
            Assert.Equal(GaleDiffKind.Update, GaleProfileDiff.Compare(local, new[] { ("LooseMod", "1.0.0", "h") }, null).Rows.Single().Kind);

            Assert.True(GaleProfileDiff.IsLocalName("LooseMod"));
            Assert.True(GaleProfileDiff.IsLocalName("My Mod 2"));
            Assert.False(GaleProfileDiff.IsLocalName(".."));
            Assert.False(GaleProfileDiff.IsLocalName("a/b"));
            Assert.False(GaleProfileDiff.IsLocalName(" padded"));
            Assert.False(GaleProfileDiff.IsLocalName(""));
        }
    }
}
