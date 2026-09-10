using Moq;
using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The item indexer plugin rewrites items.json in place on every server start, so the path
    /// alone can never tell a fresh catalog from the one already in memory. These cover the
    /// reload rule the class doc has always promised.
    /// </summary>
    public class ItemCatalogTests : IDisposable
    {
        private readonly string BepInExDir;

        public ItemCatalogTests()
        {
            BepInExDir = Path.Combine(Path.GetTempPath(), "baka-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(BepInExDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(BepInExDir, recursive: true); } catch { }
            GC.SuppressFinalize(this);
        }

        private string CatalogPath => Path.Combine(BepInExDir, "items.json");

        private void WriteCatalog(DateTime writtenUtc, params string[] prefabs)
        {
            var body = string.Join(",", prefabs.Select(p =>
                "{\"prefab\":\"" + p + "\",\"name\":\"" + p + "\",\"category\":\"Item\"}"));
            File.WriteAllText(CatalogPath, "[" + body + "]");
            File.SetLastWriteTimeUtc(CatalogPath, writtenUtc);
        }

        private static ItemCatalog NewCatalog() => new(new Mock<IApplicationLogger>().Object);

        /// <summary>
        /// The regression: the guard compared paths only, so a regenerated items.json at the very
        /// same path was never picked up again for the life of the process. Adding a mod and
        /// restarting the server left the picker on the list from the first start.
        /// </summary>
        [Fact]
        public void EnsureLoaded_PicksUpARewrittenCatalogAtTheSamePath()
        {
            var catalog = NewCatalog();
            var first = DateTime.UtcNow.AddMinutes(-5);

            WriteCatalog(first, "Wood");
            catalog.EnsureLoaded(BepInExDir);
            Assert.Equal(new[] { "Wood" }, catalog.Entries.Select(e => e.PrefabName));

            WriteCatalog(first.AddMinutes(1), "Wood", "ModdedSword");
            catalog.EnsureLoaded(BepInExDir);

            Assert.Equal(new[] { "ModdedSword", "Wood" }, catalog.Entries.Select(e => e.PrefabName).OrderBy(n => n));
            Assert.Equal(CatalogPath, catalog.LoadedFrom);
            Assert.True(catalog.IsLiveCatalog);
        }

        /// <summary>
        /// The other half of the same rule: an unchanged file must not be re-read on every call,
        /// because the picker calls this each time it opens.
        /// </summary>
        [Fact]
        public void EnsureLoaded_LeavesTheCatalogAloneWhenTheWriteTimeHasNotMoved()
        {
            var catalog = NewCatalog();
            var stamp = DateTime.UtcNow.AddMinutes(-5);

            WriteCatalog(stamp, "Wood");
            catalog.EnsureLoaded(BepInExDir);

            // New content, same write time: nothing has changed as far as the catalog can tell.
            WriteCatalog(stamp, "Wood", "ModdedSword");
            File.SetLastWriteTimeUtc(CatalogPath, stamp);
            catalog.EnsureLoaded(BepInExDir);

            Assert.Equal(new[] { "Wood" }, catalog.Entries.Select(e => e.PrefabName));
        }
    }
}
