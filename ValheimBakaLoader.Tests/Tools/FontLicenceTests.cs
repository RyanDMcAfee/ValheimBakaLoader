using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The interface ships its fonts rather than fetching them from Google, so the
    /// release carries somebody else's font files. The SIL Open Font License asks for
    /// its own text to travel with them, so a font added later without its licence is
    /// a licence breach nobody would notice by looking at the window. These gates read
    /// the folder itself: a new woff2 with no OFL file beside it fails here.
    /// </summary>
    public class FontLicenceTests
    {
        private static string FontsFolder =>
            Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "fonts");

        /// <summary>Cinzel-latin-ext.woff2 belongs to the Cinzel family, so its licence is OFL-Cinzel.txt.</summary>
        private static string FamilyOf(string fileName) => fileName.Split('-')[0];

        [Fact]
        public void Every_bundled_font_ships_its_open_font_licence()
        {
            var families = Directory.GetFiles(FontsFolder, "*.woff2")
                                    .Select(Path.GetFileName)
                                    .Select(FamilyOf)
                                    .Distinct(StringComparer.Ordinal)
                                    .ToList();

            Assert.NotEmpty(families);

            foreach (var family in families)
            {
                var licence = Path.Combine(FontsFolder, $"OFL-{family}.txt");
                Assert.True(File.Exists(licence), $"{family} ships with no OFL-{family}.txt beside it.");
            }
        }

        [Fact]
        public void Each_licence_names_its_font_and_carries_the_whole_OFL_text()
        {
            var expectedCopyright = new (string Family, string Line)[]
            {
                ("Cinzel", "Copyright (c) 2012, Natanael Gama (www.ndiscovered.com|info@ndiscovered.com), with Reserved Font Name Cinzel."),
                ("Inter", "Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)"),
                ("JetBrainsMono", "Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono)"),
            };

            foreach (var (family, line) in expectedCopyright)
            {
                var text = AppSourceTree.Lf(File.ReadAllText(Path.Combine(FontsFolder, $"OFL-{family}.txt")));

                Assert.StartsWith(line, text, StringComparison.Ordinal);
                Assert.Contains("SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007", text, StringComparison.Ordinal);
                Assert.Contains("PREAMBLE", text, StringComparison.Ordinal);
                Assert.Contains("DEFINITIONS", text, StringComparison.Ordinal);
                Assert.Contains("TERMINATION", text, StringComparison.Ordinal);
                Assert.Contains("THE FONT SOFTWARE IS PROVIDED \"AS IS\"", text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void No_licence_file_sits_in_the_fonts_folder_without_a_font_to_cover()
        {
            var families = Directory.GetFiles(FontsFolder, "*.woff2")
                                    .Select(Path.GetFileName)
                                    .Select(FamilyOf)
                                    .ToHashSet(StringComparer.Ordinal);

            foreach (var licence in Directory.GetFiles(FontsFolder, "OFL-*.txt").Select(Path.GetFileName))
            {
                var family = Path.GetFileNameWithoutExtension(licence).Substring("OFL-".Length);
                Assert.True(families.Contains(family), $"{licence} covers a font that is no longer bundled.");
            }
        }

        [Fact]
        public void The_repository_licence_is_the_version_that_covers_the_bundled_assets()
        {
            var text = AppSourceTree.Lf(File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "LICENSE")));

            Assert.Contains("BakaLoader Source-Available License\nVersion 1.1,", text, StringComparison.Ordinal);
            Assert.Contains("SIL Open Font License, Version 1.1", text, StringComparison.Ordinal);
            Assert.Contains("Serilog.Sinks.File", text, StringComparison.Ordinal);
            Assert.Contains("THIRD-PARTY SERVICES AND CONTENT", text, StringComparison.Ordinal);

            var notice = AppSourceTree.Lf(File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "NOTICE")));
            Assert.Contains("License, Version 1.1.", notice, StringComparison.Ordinal);
        }
    }
}
