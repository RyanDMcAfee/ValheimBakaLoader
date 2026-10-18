using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Forms;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Issue 23: the Dashboard card said "_main.383.db2: world version 42 is outside the supported
    /// chunked range 40-41" for a world Valheim 1.0.20 had just saved. The synthetic tests prove
    /// the reader takes the header; this one proves a world the real game wrote reads end to end
    /// through the same call the card makes, BlendWindow.ReadWorldSaveWithDiagnostics on the
    /// world folder, with every chunk parsed and nothing said against it.
    /// <para>
    /// A real world is far too big to check in and belongs to its owner, so the folder comes
    /// from BAKALOADER_V42_WORLD, the way BundledPluginCompatibilityTests finds a game install.
    /// Unset, the test says it checked nothing and passes, so a checkout without the copy is not
    /// red. The folder is only ever read.
    /// </para>
    /// </summary>
    [Collection("DiagnosticSink")]
    public class AtlasRealWorldVersion42Tests
    {
        private const string WorldVariable = "BAKALOADER_V42_WORLD";

        private readonly ITestOutputHelper _out;

        public AtlasRealWorldVersion42Tests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void A_world_valheim_1_0_20_saved_reads_whole_through_the_dashboard_read()
        {
            var folder = Environment.GetEnvironmentVariable(WorldVariable);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                // A gate that checked nothing must never look like a gate that passed.
                _out.WriteLine("SKIPPED: set " + WorldVariable + " to a world folder Valheim 1.0.20 wrote "
                    + "(_main.N.db2, _main.N.chunks and the .chunk files) to run this check.");
                return;
            }

            var info = BlendWindow.ReadWorldSaveWithDiagnostics(folder, out var diagnostics);

            foreach (var line in diagnostics) _out.WriteLine("note: " + line);
            Assert.True(info != null, "the world did not read: " + string.Join(" | ", diagnostics));

            _out.WriteLine("world version " + info.WorldVersion);
            _out.WriteLine("net time " + info.NetTime + " (day " + info.DayNumber + ")");
            _out.WriteLine("zdos " + info.ZdoCount);
            _out.WriteLine("chunks " + info.ChunksTotal + ", skipped " + info.ChunksSkipped);
            _out.WriteLine("shortfalls " + info.ChunkShortfalls.Count
                + " (" + info.ChunkShortfalls.Sum(s => s.Missing) + " records)");
            _out.WriteLine("portals " + info.Portals.Count);
            _out.WriteLine("map tables " + info.MapTables.Count);
            _out.WriteLine("build clusters " + info.BuildClusters.Count);
            _out.WriteLine("locations " + info.Locations.Count + ", generated zones " + info.GeneratedZoneCount);

            Assert.Equal(42, info.WorldVersion);
            Assert.True(info.ChunksTotal > 0, "the index named no chunks");
            Assert.Equal(0, info.ChunksSkipped);
            Assert.DoesNotContain(diagnostics, d => d.Contains("outside the supported"));
            Assert.True(info.ZdoCount > 0);
            Assert.Contains(info.Locations, l => l.Prefab == "StartTemple");
        }
    }
}
