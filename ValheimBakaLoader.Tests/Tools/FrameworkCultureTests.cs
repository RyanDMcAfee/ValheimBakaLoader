using System;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The interface is in one language, and .NET's own sentences have to be in it too.
    /// <para>
    /// Any IO, permission, network or JSON failure that escapes an RPC handler becomes page
    /// text through <c>ex.Message</c>, and .NET writes those in
    /// <c>CultureInfo.CurrentUICulture</c>. The csproj sets no InvariantGlobalization and
    /// nothing pinned a culture, so a host running Japanese Windows was already shown
    /// Japanese framework sentences inside an English interface. That was inherited, not
    /// created, and it is fixed here.
    /// </para>
    /// <para>
    /// A gate on the source, because what it guards against is somebody later moving the
    /// call, or reaching for CurrentCulture while they are there.
    /// </para>
    /// </summary>
    public class FrameworkCultureTests
    {
        private static string Program() => AppSourceTree.Files()["Program.cs"];

        [Fact]
        public void The_ui_culture_is_pinned_for_this_thread_and_every_thread_after_it()
        {
            var source = Program();

            Assert.Contains("CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;", source);
            Assert.Contains("CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;", source);
        }

        /// <summary>
        /// Before Application.Run, and before the first window: the splash is the first
        /// thing that can fail, and its message box is host-facing too.
        /// </summary>
        [Fact]
        public void It_happens_before_any_window_opens()
        {
            var source = Program();

            var pin = source.IndexOf("PinFrameworkMessagesToEnglish();", StringComparison.Ordinal);
            var highDpi = source.IndexOf("Application.SetHighDpiMode", StringComparison.Ordinal);
            var run = source.IndexOf("Application.Run(", StringComparison.Ordinal);

            Assert.True(pin > 0, "nothing pins the framework message language");
            Assert.True(pin < highDpi, "the language is pinned after the app has begun setting itself up");
            Assert.True(pin < run, "the language is pinned after a window could already have opened");
        }

        /// <summary>
        /// Only the UI culture moves. How a host's machine writes numbers, dates, money and
        /// sort order is theirs, and nothing about the language of an error message should
        /// change it. This is the line that would be easy to add by accident.
        /// </summary>
        [Fact]
        public void The_hosts_own_number_and_date_formatting_is_left_alone()
        {
            var source = Program();

            Assert.DoesNotContain("CultureInfo.DefaultThreadCurrentCulture =", source);
            Assert.DoesNotContain("CurrentThread.CurrentCulture =", source);
            Assert.DoesNotContain("CultureInfo.CurrentCulture =", source);
        }
    }
}
