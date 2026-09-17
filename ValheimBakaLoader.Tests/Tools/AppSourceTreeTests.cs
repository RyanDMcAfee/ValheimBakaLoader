using System.Linq;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The source gates compare against literals that sometimes span two lines. Those
    /// literals are written with LF, so the text the gates read has to be LF too, whatever
    /// the checkout did to the working copy. The first CI run of 1.1.0 failed on exactly
    /// this: a Windows runner checked out CRLF and a two-line count found nothing.
    /// </summary>
    public class AppSourceTreeTests
    {
        [Fact]
        public void Line_endings_are_normalised_to_LF()
        {
            Assert.Equal("a\nb\nc", AppSourceTree.Lf("a\r\nb\nc"));
            Assert.Equal("", AppSourceTree.Lf(""));
            Assert.Null(AppSourceTree.Lf(null));
        }

        [Fact]
        public void Every_source_the_gates_read_arrives_without_a_carriage_return()
        {
            Assert.DoesNotContain('\r', AppSourceTree.Web("app.js"));
            Assert.DoesNotContain('\r', AppSourceTree.Web("index.html"));
            Assert.DoesNotContain('\r', AppSourceTree.Web("app.css"));
            Assert.All(AppSourceTree.Files().Values.Take(50), text => Assert.DoesNotContain('\r', text));
        }
    }
}
