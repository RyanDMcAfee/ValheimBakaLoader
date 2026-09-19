using DeviceId;
using Semver;
using System;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Answers questions about the running build: its version, its runtime,
    /// and a stable anonymous identifier for the machine it runs on.
    /// </summary>
    public static class AssemblyHelper
    {
        private static readonly Lazy<string> Version = new(ReadVersion);
        private static readonly Lazy<string> DeviceHash = new(ComputeDeviceHash);

        /// <summary>The semantic version of the running build (build metadata stripped).</summary>
        public static string GetApplicationVersion() => Version.Value;

        /// <summary>
        /// Compares a version string against the running build.
        /// 1 = newer than this build, 0 = same, -1 = older, -2 = unparseable.
        /// </summary>
        public static int CompareVersion(string version) => CompareVersion(version, GetApplicationVersion());

        /// <summary>
        /// Compares two version strings the same way, without either of them having to be the
        /// running build. 1 = <paramref name="version"/> is newer, 0 = the same, -1 = older,
        /// -2 = one of them could not be read.
        /// <para>
        /// A language pack names the oldest app it will install into, and the version it is
        /// held against is injectable so the suite can prove the refusal without shipping a
        /// build for every case. Both sides are parsed rather than compared as text, because
        /// "1.10.0" sorts before "1.9.0" as text and after it as a version.
        /// </para>
        /// </summary>
        public static int CompareVersion(string version, string against)
        {
            try
            {
                var mine = SemVersion.Parse(against, SemVersionStyles.Any);
                var theirs = SemVersion.Parse(version, SemVersionStyles.Any);
                return SemVersion.CompareSortOrder(theirs, mine);
            }
            catch
            {
                return -2;
            }
        }

        public static Version GetDotnetRuntimeVersion() => Environment.Version;

        /// <summary>
        /// A stable, anonymous device identifier: an MD5 hex digest of the
        /// machine's MAC + hostname. Not reversible to either input; used only
        /// to de-duplicate telemetry and crash reports per machine.
        /// </summary>
        public static string GetClientCorrelationId() => DeviceHash.Value;

        /// <summary>A crash report pre-filled with everything environmental.</summary>
        public static CrashReport BuildCrashReport() => new()
        {
            CrashReportId = Guid.NewGuid().ToString(),
            ClientCorrelationId = GetClientCorrelationId(),
            Timestamp = DateTime.UtcNow,
            AppVersion = GetApplicationVersion(),
            OsVersion = Environment.OSVersion.VersionString,
            DotnetVersion = Environment.Version.ToString(),
            CurrentCulture = CultureInfo.CurrentCulture?.ToString(),
            CurrentUICulture = CultureInfo.CurrentUICulture?.ToString(),
        };

        private static string ReadVersion()
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";

            // The csproj appends "+build<date>" metadata; everything after the
            // '+' is not part of the comparable version.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        private static string ComputeDeviceHash()
        {
            var fingerprint = new DeviceIdBuilder()
                .AddMacAddress()
                .AddMachineName()
                .ToString()
                .ToLowerInvariant();

            using var md5 = MD5.Create();
            var digest = md5.ComputeHash(Encoding.UTF8.GetBytes(fingerprint));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }
}
