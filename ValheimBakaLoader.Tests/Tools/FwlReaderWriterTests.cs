using System;
using System.IO;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    public class FwlReaderWriterTests : IDisposable
    {
        private readonly string SaveFolder =
            Path.Combine(Path.GetTempPath(), "vbl-fwl-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(SaveFolder, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void StableHashCode_MatchesLiveWorldVector()
        {
            // Verified byte-for-byte against a live worldVersion-37 .fwl:
            // seedName "yBvEFPKD9S" stores numeric seed 649688311.
            Assert.Equal(649688311, FwlWriter.GetStableHashCode("yBvEFPKD9S"));
        }

        [Fact]
        public void WriteNewWorld_RoundTripsThroughReader()
        {
            var written = FwlWriter.WriteNewWorld(SaveFolder, "TestWorld", "myseed");

            var path = Path.Combine(SaveFolder, "worlds_local", "TestWorld.fwl");
            Assert.True(File.Exists(path));

            // Leading int32 must be the payload size (file length - 4), like the game writes.
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                Assert.Equal(new FileInfo(path).Length - 4, br.ReadInt32());
            }

            var read = FwlReader.TryRead(path);
            Assert.NotNull(read);
            Assert.Equal("TestWorld", read.WorldName);
            Assert.Equal("myseed", read.SeedName);
            Assert.Equal(FwlWriter.GetStableHashCode("myseed"), read.Seed);
            Assert.Equal(written.Seed, read.Seed);
            Assert.Equal(37, read.WorldVersion);
        }

        [Fact]
        public void WriteNewWorld_BlankSeedGetsRandomSeedName()
        {
            var written = FwlWriter.WriteNewWorld(SaveFolder, "RandomWorld", "  ");
            Assert.False(string.IsNullOrWhiteSpace(written.SeedName));
            Assert.Equal(10, written.SeedName.Length);
            Assert.Equal(FwlWriter.GetStableHashCode(written.SeedName), written.Seed);
        }

        [Fact]
        public void WriteNewWorld_RefusesWhenWorldAlreadyExists()
        {
            FwlWriter.WriteNewWorld(SaveFolder, "Existing", "first");

            // The invariant: an existing world's seed must never be overwritten.
            Assert.Throws<InvalidOperationException>(
                () => FwlWriter.WriteNewWorld(SaveFolder, "Existing", "second"));

            // And the original is untouched.
            var read = FwlReader.TryReadWorld(SaveFolder, "Existing");
            Assert.Equal("first", read.SeedName);
        }

        [Fact]
        public void WriteNewWorld_RefusesWhenFwlSitsInWorldsSubfolder()
        {
            // A world living in worlds/ (not worlds_local/) is still an existing world.
            var dir = Path.Combine(SaveFolder, "worlds");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "OldWorld.fwl"), new byte[] { 1, 2, 3 });

            Assert.Throws<InvalidOperationException>(
                () => FwlWriter.WriteNewWorld(SaveFolder, "OldWorld", "seed"));
        }

        [Fact]
        public void WriteNewWorld_RejectsBadWorldNames()
        {
            Assert.Throws<ArgumentException>(() => FwlWriter.WriteNewWorld(SaveFolder, "  ", "seed"));
            Assert.Throws<ArgumentException>(() => FwlWriter.WriteNewWorld(SaveFolder, "bad|name", "seed"));
        }

        [Fact]
        public void TryRead_ReturnsNullForMissingOrGarbageFiles()
        {
            Assert.Null(FwlReader.TryRead(null));
            Assert.Null(FwlReader.TryRead(Path.Combine(SaveFolder, "nope.fwl")));

            Directory.CreateDirectory(SaveFolder);
            var garbage = Path.Combine(SaveFolder, "garbage.fwl");
            File.WriteAllBytes(garbage, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 1 });
            Assert.Null(FwlReader.TryRead(garbage));
        }
    }
}
