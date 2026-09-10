using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Processes;

namespace ValheimBakaLoader.Tools
{
    /// <summary>What one line of steamcmd output turned out to mean.</summary>
    public enum SteamCmdSignal
    {
        /// <summary>Chatter: worth logging, worth nothing else.</summary>
        None = 0,

        /// <summary>An "Update state" line carrying a percentage and a byte count.</summary>
        Progress = 1,

        /// <summary>The app finished installing, or was already current.</summary>
        Success = 2,

        /// <summary>steamcmd gave up.</summary>
        Error = 3,
    }

    /// <summary>One parsed line of steamcmd output.</summary>
    public sealed class SteamCmdLine
    {
        public SteamCmdSignal Signal { get; init; }

        /// <summary>The state word out of an "Update state" line ("downloading", "verifying").</summary>
        public string State { get; init; }

        /// <summary>Percent complete for a progress line, otherwise minus one.</summary>
        public double Percent { get; init; } = -1;

        public long BytesDone { get; init; }

        public long BytesTotal { get; init; }

        /// <summary>True when the state word says steamcmd is checking files rather than fetching them.</summary>
        public bool IsVerifying =>
            State != null
            && (State.IndexOf("verif", StringComparison.OrdinalIgnoreCase) >= 0
                || State.IndexOf("validat", StringComparison.OrdinalIgnoreCase) >= 0
                || State.IndexOf("commit", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// Reads steamcmd's console output. The only two lines that decide anything are the
    /// "Update state" progress line and the final "Success!" or "Error!" line, so this stays
    /// a pair of tolerant patterns rather than a parser for a format Valve never documented.
    /// </summary>
    public static class SteamCmdOutput
    {
        // "Update state (0x61) downloading, progress: 12.34 (110000000 / 888934688)"
        private static readonly Regex ProgressPattern = new(
            @"Update state \(0x[0-9a-f]+\)\s*([^,]*),\s*progress:\s*([0-9]+(?:[.,][0-9]+)?)\s*\(\s*([0-9]+)\s*/\s*([0-9]+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly SteamCmdLine Nothing = new() { Signal = SteamCmdSignal.None };

        /// <summary>Turns one line into what it means. Never throws, never returns null.</summary>
        public static SteamCmdLine Read(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return Nothing;

            var match = ProgressPattern.Match(line);
            if (match.Success)
            {
                return new SteamCmdLine
                {
                    Signal = SteamCmdSignal.Progress,
                    State = match.Groups[1].Value.Trim(),
                    Percent = ReadPercent(match.Groups[2].Value),
                    BytesDone = ReadLong(match.Groups[3].Value),
                    BytesTotal = ReadLong(match.Groups[4].Value),
                };
            }

            // "Success! App '896660' fully installed." and, when nothing had to be fetched,
            // "Success! App '896660' already up to date."
            if (line.IndexOf("Success!", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new SteamCmdLine { Signal = SteamCmdSignal.Success };
            }

            if (line.IndexOf("Error!", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FAILED", StringComparison.Ordinal) >= 0)
            {
                return new SteamCmdLine { Signal = SteamCmdSignal.Error };
            }

            return Nothing;
        }

        /// <summary>
        /// True for the "Update state" prefix on its own. A fresh steamcmd self-updates and
        /// exits before it ever runs the script it was given, so seeing neither this nor a
        /// success line is how that run is told apart from a real one.
        /// </summary>
        public static bool MentionsUpdateState(string line)
            => line != null && line.IndexOf("Update state", StringComparison.OrdinalIgnoreCase) >= 0;

        private static double ReadPercent(string raw)
            => double.TryParse(
                raw.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ? value : -1;

        private static long ReadLong(string raw)
            => long.TryParse(raw, out var value) ? value : 0;
    }

    /// <summary>
    /// Owns BakaLoader's private copy of steamcmd: where it lives, fetching it the first time
    /// it is needed, and running it with its output streamed back a line at a time.
    /// </summary>
    public interface ISteamCmdRunner
    {
        /// <summary>Full path of the steamcmd.exe BakaLoader would use, whether or not it is there yet.</summary>
        string SteamCmdPath { get; }

        /// <summary>
        /// Returns a usable steamcmd.exe, downloading it into BakaLoader's own folder when it
        /// is not there yet. Null when it could not be fetched or could not be trusted.
        /// </summary>
        Task<string> EnsureInstalledAsync(CancellationToken ct);

        /// <summary>
        /// Runs an anonymous app_update for the dedicated server into <paramref name="installDir"/>,
        /// calling <paramref name="onLine"/> for every line of output. Returns the exit code, or
        /// minus one when the process could not be run at all.
        ///
        /// Pass a token nothing can cancel. A cancelled wait here answers minus one while
        /// steamcmd carries on writing the install, which reads as a failed update and lets go
        /// of the one-at-a-time lock over a folder that is still being written to.
        /// </summary>
        Task<int> RunAsync(string steamCmdPath, string installDir, Action<string> onLine, CancellationToken ct);
    }

    /// <inheritdoc cref="ISteamCmdRunner"/>
    public class SteamCmdRunner : ISteamCmdRunner
    {
        /// <summary>Valve's own download. There is no published checksum for it, so the exe is checked instead.</summary>
        public const string DownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

        private const string ExeName = "steamcmd.exe";

        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IProcessProvider Processes;
        private readonly IApplicationLogger Logger;

        public SteamCmdRunner(
            IHttpClientProvider httpClientProvider,
            IProcessProvider processes,
            IApplicationLogger logger)
        {
            HttpClientProvider = httpClientProvider;
            Processes = processes;
            Logger = logger;
        }

        /// <summary>
        /// BakaLoader's own folder under LocalAppData. Nothing here is ever written into a
        /// server install, which is steamcmd's job alone.
        /// </summary>
        public string SteamCmdPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ValheimBakaLoader", "steamcmd", ExeName);

        public async Task<string> EnsureInstalledAsync(CancellationToken ct)
        {
            var target = SteamCmdPath;
            if (File.Exists(target)) return target;

            var folder = Path.GetDirectoryName(target);
            var zipPath = Path.Combine(Path.GetTempPath(), "vbl-steamcmd-" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                Directory.CreateDirectory(folder);

                using (var client = HttpClientProvider.CreateClient())
                {
                    client.Timeout = DownloadTimeout;
                    var bytes = await client.GetByteArrayAsync(DownloadUrl, ct).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(zipPath, bytes, ct).ConfigureAwait(false);
                }

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    // Valve ships exactly one file in this zip. Anything else is not the
                    // download we asked for, and unpacking it would be someone else's idea.
                    var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                    if (entries.Count != 1
                        || !string.Equals(entries[0].Name, ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Error("Server update: the steamcmd download did not contain only {0}; refusing it.", ExeName);
                        return null;
                    }

                    entries[0].ExtractToFile(target, overwrite: true);
                }

                if (!IsSignedByValve(target))
                {
                    Logger.Error("Server update: the downloaded steamcmd is not signed by Valve; refusing it.");
                    TryDelete(target);
                    return null;
                }

                Logger.Information("Server update: steamcmd installed at {0}.", target);
                return target;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Server update: steamcmd could not be downloaded.");
                TryDelete(target);
                return null;
            }
            finally
            {
                TryDelete(zipPath);
            }
        }

        public async Task<int> RunAsync(string steamCmdPath, string installDir, Action<string> onLine, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(steamCmdPath) || string.IsNullOrWhiteSpace(installDir)) return -1;

            var key = "steamcmd-" + Guid.NewGuid().ToString("N")[..8];
            var args = "+force_install_dir \"" + installDir.TrimEnd('\\') + "\""
                + " +login anonymous +app_update " + ServerBuildTracker.SteamAppId + " validate +quit";

            try
            {
                var process = Processes.AddBackgroundProcess(key, steamCmdPath, args);

                // steamcmd's bootstrapper unpacks the Steam client next to its WORKING
                // directory, not next to its executable. Without this it would scatter tens of
                // megabytes of client files through whatever folder BakaLoader happens to be
                // installed in, and fail outright when that folder is not writable.
                var home = Path.GetDirectoryName(steamCmdPath);
                if (!string.IsNullOrWhiteSpace(home)) process.StartInfo.WorkingDirectory = home;

                process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };

                StartOutsideTheKillOnCloseJob(process);

                // The download itself is never interrupted: a half-written install is worse
                // than waiting. The token is only here so a shutting-down app stops awaiting.
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Server update: steamcmd could not be run.");
                return -1;
            }
        }

        /// <summary>
        /// Starts steamcmd WITHOUT handing it to the app's kill-on-close job object, which is
        /// what every other child process is started through. That job kills its members the
        /// instant BakaLoader's process ends, and killing steamcmd part way through replacing
        /// valheim_server.exe leaves the install a mix of two builds. Closing the app during an
        /// update is refused for exactly this reason, but a crash or a Task Manager kill is
        /// not something BakaLoader gets to refuse, so the update is left able to finish on
        /// its own. steamcmd quits by itself when it is done; it is not a long lived child.
        /// </summary>
        private static void StartOutsideTheKillOnCloseJob(System.Diagnostics.Process process)
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        /// <summary>
        /// True when the file carries an Authenticode signature that Windows trusts AND that
        /// signature names Valve. Valve publishes no checksum for steamcmd, so this is the only
        /// thing that says the bytes came from them.
        ///
        /// Both halves are needed. Reading the signer certificate out of a file proves nothing
        /// on its own: a certificate table is public data that can be copied onto any binary,
        /// and reading it does not re-hash the file, so a tampered exe carrying Valve's
        /// certificate answers "Valve" just as loudly as the real one. Windows has to be asked
        /// whether the signature actually covers these bytes and chains to a root it trusts.
        /// </summary>
        private static bool IsSignedByValve(string path)
            => IsTrustedValveBinary(path, Authenticode.IsTrusted, ReadSignerSubject);

        /// <summary>
        /// The rule the check above applies, with its two answers handed in so it can be proved
        /// without a signed binary on disk: the signature has to verify first, and only then
        /// does the name on it count for anything.
        /// </summary>
        public static bool IsTrustedValveBinary(
            string path, Func<string, bool> verifySignature, Func<string, string> readSignerSubject)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (verifySignature == null || !verifySignature(path)) return false;

            var subject = readSignerSubject?.Invoke(path);
            return !string.IsNullOrWhiteSpace(subject)
                && subject.IndexOf("Valve", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The subject of the certificate the file was signed with, or null.</summary>
        private static string ReadSignerSubject(string path)
        {
            try { return X509Certificate.CreateFromSignedFile(path).Subject; }
            catch { return null; }
        }

        private void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Logger.Debug("Server update: could not delete {0}: {1}", path, e.Message); }
        }
    }

    /// <summary>
    /// Asks Windows itself whether a file's Authenticode signature is intact and trusted. This
    /// is the check that re-hashes the file and walks the certificate chain, which is the part
    /// no amount of reading the certificate table can stand in for.
    /// </summary>
    internal static class Authenticode
    {
        /// <summary>True only when Windows verifies the signature over these exact bytes.</summary>
        public static bool IsTrusted(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            var action = WinTrustActionGenericVerifyV2;
            var filePath = Marshal.StringToCoTaskMemUni(path);
            var fileInfoPtr = IntPtr.Zero;

            try
            {
                var fileInfo = new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePath,
                    FileHandle = IntPtr.Zero,
                    KnownSubject = IntPtr.Zero,
                };

                fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    PolicyCallbackData = IntPtr.Zero,
                    SipClientData = IntPtr.Zero,
                    UiChoice = WtdUiNone,

                    // Revocation is a network call, and a host setting a server up may have no
                    // network at that moment. The chain itself is still checked.
                    RevocationChecks = WtdRevokeNone,
                    UnionChoice = WtdChoiceFile,
                    FileInfoPtr = fileInfoPtr,
                    StateAction = WtdStateActionVerify,
                    StateData = IntPtr.Zero,
                    UrlReference = IntPtr.Zero,
                    ProvFlags = WtdSaferFlag,
                    UiContext = 0,
                };

                var result = WinVerifyTrust(NoWindow, ref action, ref data);

                // Every verify has to be closed again or the state it allocated is leaked.
                data.StateAction = WtdStateActionClose;
                WinVerifyTrust(NoWindow, ref action, ref data);

                return result == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fileInfoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPtr);
                Marshal.FreeCoTaskMem(filePath);
            }
        }

        private static readonly IntPtr NoWindow = new(-1);

        private static Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdSaferFlag = 0x00000100;

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfoPtr;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProvFlags;
            public uint UiContext;
        }
    }
}
