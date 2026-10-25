using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Steam Cloud characters in the test copy dialog (1.3.0, H1 item 2). A host who plays on
    /// Steam Cloud keeps every character under Steam's own folder, in
    /// userdata\&lt;account&gt;\892970\remote\characters, and the dialog said there were none.
    /// Those folders are listed now, every account that has one, each row saying "cloud"; a copy
    /// of one is written into the save folder's characters_local and never into Steam's folder.
    /// <para>
    /// Everything here is a temp tree: no real Steam folder, no save folder, no registry.
    /// </para>
    /// </summary>
    public class CharacterCloudTests : IDisposable
    {
        private const string AccountA = "44957764";
        private const string AccountB = "1000001";

        private readonly string Root;
        private readonly string SaveRoot;
        private readonly string SteamRoot;

        public CharacterCloudTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-cloudchars-" + Guid.NewGuid().ToString("N"));
            SaveRoot = Path.Combine(Root, "LocalLow", "IronGate", "Valheim");
            SteamRoot = Path.Combine(Root, "Steam");
            Directory.CreateDirectory(SaveRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
        }

        private string CloudDir(string account) =>
            Path.Combine(SteamRoot, "userdata", account, "892970", "remote", "characters");

        private byte[] Character(string dir, string file, string name)
        {
            Directory.CreateDirectory(dir);
            var bytes = CharacterFileTests.GameLayout.Build(name).File;
            File.WriteAllBytes(Path.Combine(dir, file), bytes);
            return bytes;
        }

        [Fact]
        public void Every_account_with_a_cloud_folder_is_listed_as_cloud_beside_the_local_ones()
        {
            Character(Path.Combine(SaveRoot, "characters_local"), "ragnar.fch", "Ragnar");
            Character(CloudDir(AccountA), "astrid.fch", "Astrid");
            Character(CloudDir(AccountB), "bjorn.fch", "Bjorn");
            // An account that never saved a character here, and a folder that is not an account.
            Directory.CreateDirectory(Path.Combine(SteamRoot, "userdata", "2000002", "892970", "remote"));
            Directory.CreateDirectory(Path.Combine(SteamRoot, "userdata", "ac", "892970", "remote", "characters"));
            File.WriteAllBytes(Path.Combine(SteamRoot, "userdata", "ac", "892970", "remote", "characters", "x.fch"),
                CharacterFileTests.GameLayout.Build("NotAnAccount").File);
            // A game backup beside a cloud character is not a character.
            File.WriteAllBytes(Path.Combine(CloudDir(AccountA), "astrid_backup_auto-20261024.fch"),
                CharacterFileTests.GameLayout.Build("Astrid").File);

            var folders = CharacterCopy.CloudFolders(SteamRoot);
            Assert.Equal(new[] { AccountB, AccountA }, folders.Select(f => f.Account).ToArray());

            var list = CharacterCopy.List(SaveRoot, SteamRoot);
            Assert.Equal(new[] { "Astrid", "Bjorn", "Ragnar" }, list.Select(c => c.Name).ToArray());

            var astrid = list.Single(c => c.Name == "Astrid");
            Assert.Equal("cloud", astrid.Source);
            Assert.Equal("cloud:" + AccountA, astrid.Folder);
            Assert.Equal("astrid.fch", astrid.File);
            Assert.Equal("Astrid_bakaT", astrid.CopyName);
            Assert.Equal("astrid_bakat.fch", astrid.CopyFile);
            Assert.False(astrid.CopyExists);

            Assert.Equal("cloud:" + AccountB, list.Single(c => c.Name == "Bjorn").Folder);
            var ragnar = list.Single(c => c.Name == "Ragnar");
            Assert.Equal("local", ragnar.Source);
            Assert.Equal("characters_local", ragnar.Folder);

            // The old overload is the save folder only, as it always was.
            Assert.Equal(new[] { "Ragnar" }, CharacterCopy.List(SaveRoot).Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Ragnar" }, CharacterCopy.List(SaveRoot, null).Select(c => c.Name).ToArray());
        }

        [Fact]
        public void A_copy_of_a_cloud_character_lands_in_characters_local_and_Steams_folder_is_untouched()
        {
            var original = Character(CloudDir(AccountA), "astrid.fch", "Astrid");
            var before = Directory.GetFiles(CloudDir(AccountA)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            // A PC that has only ever saved to Steam Cloud has no characters_local yet.
            Assert.False(Directory.Exists(Path.Combine(SaveRoot, "characters_local")));

            var result = CharacterCopy.Copy(SaveRoot, SteamRoot, "cloud:" + AccountA, "astrid.fch");

            Assert.Equal("characters_local", result.Folder);
            Assert.Equal("astrid_bakat.fch", result.File);
            Assert.Equal("Astrid_bakaT", result.Name);
            Assert.Equal("Astrid", result.SourceName);
            var written = Path.Combine(SaveRoot, "characters_local", "astrid_bakat.fch");
            Assert.Equal(written, result.Path);
            Assert.True(CharacterFile.TryRead(File.ReadAllBytes(written), out var info, out _));
            Assert.Equal("Astrid_bakaT", info.Name);

            // Nothing was written into, or changed in, Steam's folder.
            Assert.Equal(before, Directory.GetFiles(CloudDir(AccountA)).OrderBy(f => f, StringComparer.Ordinal).ToArray());
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(CloudDir(AccountA), "astrid.fch")));
            Assert.False(File.Exists(Path.Combine(CloudDir(AccountA), "astrid_bakat.fch")));

            // The list now says the copy is there, asked where it was written.
            Assert.True(CharacterCopy.List(SaveRoot, SteamRoot).Single(c => c.Folder == "cloud:" + AccountA).CopyExists);
            var refusal = Assert.Throws<HostFacingException>(() => CharacterCopy.Copy(SaveRoot, SteamRoot, "cloud:" + AccountA, "astrid.fch"));
            Assert.Equal("characters.copy.exists", refusal.MessageId);
        }

        [Theory]
        [InlineData("cloud:44957764", null, "a cloud folder with no Steam folder to find it in")]
        [InlineData("cloud:", "steam", "no account")]
        [InlineData("cloud:..", "steam", "a step up for an account")]
        [InlineData("cloud:12ab", "steam", "an account that is not digits")]
        [InlineData("cloud:44957764\\..\\..", "steam", "a path in the account")]
        [InlineData("remote", "steam", "a folder that is none of the places")]
        public void A_folder_that_is_not_a_place_characters_are_kept_is_refused_by_name(string folder, string steam, string why)
        {
            Character(CloudDir(AccountA), "astrid.fch", "Astrid");
            var refusal = Assert.Throws<HostFacingException>(() =>
                CharacterCopy.Copy(SaveRoot, steam == null ? null : SteamRoot, folder, "astrid.fch"));
            Assert.True(refusal.MessageId == "characters.copy.badRef", why);
            Assert.False(Directory.Exists(Path.Combine(SaveRoot, "characters_local")), why);
        }

        [Fact]
        public void A_refused_cloud_copy_leaves_the_save_folder_as_it_found_it()
        {
            Directory.CreateDirectory(CloudDir(AccountA));
            File.WriteAllBytes(Path.Combine(CloudDir(AccountA), "broken.fch"), new byte[] { 1, 2, 3 });

            var missing = Assert.Throws<HostFacingException>(() => CharacterCopy.Copy(SaveRoot, SteamRoot, "cloud:" + AccountA, "nobody.fch"));
            Assert.Equal("characters.copy.missing", missing.MessageId);
            var unreadable = Assert.Throws<HostFacingException>(() => CharacterCopy.Copy(SaveRoot, SteamRoot, "cloud:" + AccountA, "broken.fch"));
            Assert.Equal("characters.copy.unreadable", unreadable.MessageId);

            // characters_local is made only for a copy that is going to be written.
            Assert.False(Directory.Exists(Path.Combine(SaveRoot, "characters_local")));
        }

        [Fact]
        public void No_Steam_folder_or_no_userdata_lists_no_cloud_characters_and_never_throws()
        {
            Character(Path.Combine(SaveRoot, "characters"), "old.fch", "Old");
            Assert.Empty(CharacterCopy.CloudFolders(null));
            Assert.Empty(CharacterCopy.CloudFolders(Path.Combine(Root, "no-steam-here")));
            Directory.CreateDirectory(SteamRoot);
            Assert.Empty(CharacterCopy.CloudFolders(SteamRoot));
            Assert.Equal("local", Assert.Single(CharacterCopy.List(SaveRoot, SteamRoot)).Source);
        }
    }
}
