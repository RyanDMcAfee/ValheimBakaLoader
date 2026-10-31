using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Which pastes into Add from link are a zip on this PC (1.3.2, issue 32), read off the one table
    /// the page's selftest reads too (scripts/ui/local_path_cases.json), so the host's rule and the
    /// page's copy of it can never drift apart. Adversarial scenarios 2 and 3: Explorer's Copy as
    /// path with its quotes, spaces and line end; an unbalanced quote; single quotes left alone; UNC,
    /// file:/// with %20, file://server/share, forward slashes, the long path prefix, %USERPROFILE%
    /// not expanded, a bare name and a relative path; and every link shape still a link.
    /// </summary>
    public class LocalPackagePathTests
    {
        public static IEnumerable<object[]> Cases()
        {
            var table = JObject.Parse(AppSourceTree.Read("scripts", "ui", "local_path_cases.json"));
            var i = 0;
            foreach (var row in (JArray)table["cases"]!)
                yield return new object[] { i++, (string)row["in"], (bool)row["local"], (string)row["path"], (string)row["problem"] };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void The_table_reads_the_same_on_the_host(int row, string pasted, bool local, string path, string problem)
        {
            var recognised = LocalPackagePath.TryRecognize(pasted, out var gotPath, out var gotProblem);

            Assert.True(local == recognised, $"row {row}: {pasted} was {(recognised ? "" : "not ")}taken for a path");
            if (!local) return;
            Assert.Equal(path, gotPath);
            Assert.Equal(problem, gotProblem);
        }

        [Fact]
        public void The_table_carries_every_shape_the_spec_names()
        {
            var rows = Cases().Select(c => (string)c[1]).ToList();
            Assert.Contains(rows, r => r.StartsWith("\"") && r.TrimEnd().EndsWith("\""));
            Assert.Contains(rows, r => r.EndsWith("\r\n"));
            Assert.Contains(rows, r => r.StartsWith("'"));
            Assert.Contains(rows, r => r.StartsWith(@"\\nas"));
            Assert.Contains(rows, r => r.Contains("%20"));
            Assert.Contains(rows, r => r.StartsWith("file://nas"));
            Assert.Contains(rows, r => r.StartsWith(@"\\?\"));
            Assert.Contains(rows, r => r.StartsWith("%USERPROFILE%"));
            Assert.Contains(rows, r => r.StartsWith("ror2mm://"));
            Assert.Contains(rows, r => r.StartsWith("https://") && r.EndsWith(".zip"));
        }

        [Fact]
        public void Nothing_is_read_off_the_disk_and_nothing_is_expanded()
        {
            // A path that cannot exist is still recognised: the recogniser asks nothing of the disk,
            // so it can run on the window's thread with a dead share on the other end.
            Assert.True(LocalPackagePath.TryRecognize(@"\\no-such-host-anywhere\share\Acme-Aurora-1.4.0.zip", out var p, out var problem));
            Assert.Null(problem);
            Assert.Equal(@"\\no-such-host-anywhere\share\Acme-Aurora-1.4.0.zip", p);

            Assert.True(LocalPackagePath.TryRecognize(@"%USERPROFILE%\x.zip", out p, out problem));
            Assert.Equal(@"%USERPROFILE%\x.zip", p);
            Assert.Equal(LocalPackagePath.NotAbsolute, problem);
        }
    }
}
