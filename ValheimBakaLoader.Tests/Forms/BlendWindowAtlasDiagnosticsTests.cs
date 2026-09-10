using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The Skies panel used to get the same answer for "this world has never been saved" and
    /// "the save is right there and this reader cannot make sense of it", so the host was told
    /// to wait for a first save that had already happened. The reader knows exactly why it
    /// turned a file down, it just said so into a static sink wired only into the app log.
    /// </summary>
    [Collection("DiagnosticSink")]
    public class BlendWindowAtlasDiagnosticsTests : IDisposable
    {
        private readonly string Root;

        public BlendWindowAtlasDiagnosticsTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-atlas-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void A_save_the_reader_turns_down_hands_back_its_own_reason()
        {
            // A ".db" that is not a world save at all, which is what a truncated or foreign
            // file looks like from here.
            var db = Path.Combine(Root, "Midgard.db");
            File.WriteAllBytes(db, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            var read = BlendWindow.ReadWorldSaveWithDiagnostics(db, out var diagnostics);

            Assert.Null(read);
            Assert.NotEmpty(diagnostics);
            Assert.All(diagnostics, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        }

        [Fact]
        public void The_sink_is_handed_back_to_whoever_had_it()
        {
            var seen = 0;
            var previous = ValheimBakaLoader.Tools.Atlas.WorldDbReader.DiagnosticSink;
            ValheimBakaLoader.Tools.Atlas.WorldDbReader.DiagnosticSink = _ => seen++;
            try
            {
                var db = Path.Combine(Root, "Asgard.db");
                File.WriteAllBytes(db, new byte[] { 9, 9, 9, 9 });

                BlendWindow.ReadWorldSaveWithDiagnostics(db, out _);

                // The app log still hears every line, and the sink it had is back afterwards.
                Assert.True(seen > 0, "the app log sink was cut out of the read");
                Assert.NotNull(ValheimBakaLoader.Tools.Atlas.WorldDbReader.DiagnosticSink);

                var before = seen;
                ValheimBakaLoader.Tools.Atlas.WorldDbReader.DiagnosticSink("still mine");
                Assert.Equal(before + 1, seen);
            }
            finally
            {
                ValheimBakaLoader.Tools.Atlas.WorldDbReader.DiagnosticSink = previous;
            }
        }

        [Fact]
        public void A_world_with_nothing_saved_yet_and_one_that_will_not_open_are_told_apart()
        {
            var neverSaved = JObject.FromObject(
                BlendWindow.WorldInfoWithNoSave("Midgard", saveExists: false, diagnostics: null));

            Assert.False(neverSaved.Value<bool>("hasDb"));
            Assert.False(neverSaved.Value<bool>("saveExists"));
            Assert.Null(neverSaved.Value<string>("reason"));

            var unreadable = JObject.FromObject(BlendWindow.WorldInfoWithNoSave(
                "Midgard",
                saveExists: true,
                diagnostics: new[] { "  ", "Midgard: not a world save this reader understands" }));

            Assert.False(unreadable.Value<bool>("hasDb"));
            Assert.True(unreadable.Value<bool>("saveExists"));
            Assert.Equal("Midgard: not a world save this reader understands", unreadable.Value<string>("reason"));
        }
    }
}
