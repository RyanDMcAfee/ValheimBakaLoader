using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace ValheimBakaLoader.Tests
{
    /// <summary>
    /// Where a test run is allowed to write, decided before a single test runs.
    /// <para>
    /// The shipped defaults for the logs folder, the preferences file, the player cache and the
    /// analytics file are all built out of %USERPROFILE%, and LoggerCore expands that variable
    /// itself when it builds the file sink. So a logger built from anything other than the fake
    /// preferences, which is any logger a test news up directly, lands in the owner's real
    /// LocalLow folder and appends to the file the running app is writing. It did: that file
    /// carries test lines from 04:53 on 2026-09-20.
    /// </para>
    /// <para>
    /// The fake preferences already point their own LogsFolderPath at a temp folder, and that
    /// stays. This is the floor under it: the variable every one of those defaults is built out
    /// of names a temp folder for the whole test process, so there is no path left through which
    /// a test can reach the real one. A module initializer, because it runs before any test
    /// class is constructed and before any static field in this assembly is touched.
    /// </para>
    /// </summary>
    public static class TestLogRedirect
    {
        /// <summary>The temp home this run pretends to have. One per process.</summary>
        public static readonly string HomeFolder = Path.Combine(
            Path.GetTempPath(), "vbl-test-home-" + Environment.ProcessId);

        [ModuleInitializer]
        public static void PointEverythingAtTemp()
        {
            try
            {
                Directory.CreateDirectory(HomeFolder);
                Environment.SetEnvironmentVariable("USERPROFILE", HomeFolder);
            }
            catch
            {
                // A temp folder that will not be made is not a reason to refuse to run the
                // suite. The fake preferences still redirect the loggers that go through them.
            }
        }
    }
}
