using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface IAppUpdateService
    {
        /// <summary>
        /// Checks GitHub for a newer BakaLoader release and, if one is found, downloads it
        /// and stages a headless watchdog process that will wait for this app to exit,
        /// replace the installed files with the new version, relaunch BakaLoader, and then
        /// clean up after itself. Returns <c>true</c> when an update was staged (the caller
        /// should then close the app to let the watchdog take over), or <c>false</c> when the
        /// app is already current or the update could not be staged.
        /// </summary>
        Task<bool> CheckAndStageUpdateAsync();
    }

    /// <summary>
    /// Self-update installer for BakaLoader. The download-and-swap has to happen while the
    /// app is NOT running (its own files are locked and the child Valheim server is tied to
    /// this process via a Job Object), so the actual file replacement is delegated to a
    /// detached, windowless PowerShell "watchdog" that outlives the app:
    ///
    ///   1. BakaLoader downloads the new release zip and verifies it contains the exe.
    ///   2. BakaLoader writes + launches the hidden watchdog, then closes.
    ///   3. The watchdog waits for BakaLoader's PID to exit, extracts the zip, robocopies
    ///      the new files over the install directory, relaunches BakaLoader, and deletes
    ///      its own temp working folder.
    ///
    /// On relaunch, the normal startup path auto-starts any profile whose AutoStart pref is
    /// on, so the Valheim server comes back up without any special relaunch arguments.
    /// </summary>
    public class AppUpdateService : IAppUpdateService
    {
        private const string ExeName = "ValheimBakaLoader.exe";
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

        private readonly IGitHubClient GitHub;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;

        public AppUpdateService(
            IGitHubClient gitHub,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger logger)
        {
            GitHub = gitHub;
            HttpClientProvider = httpClientProvider;
            Logger = logger;
        }

        public async Task<bool> CheckAndStageUpdateAsync()
        {
            try
            {
                var release = await GitHub.GetLatestReleaseAsync();
                if (release == null)
                {
                    Logger.Information("Self-update: no GitHub release available.");
                    return false;
                }

                // CompareVersion returns 1 when the release is newer than the running app.
                if (AssemblyHelper.CompareVersion(release.TagName) != 1)
                {
                    Logger.Information("Self-update: already current (installed vs release {0}).", release.TagName);
                    return false;
                }

                var asset = release.Assets?
                    .FirstOrDefault(a => a?.BrowserDownloadUrl != null
                        && a.BrowserDownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                {
                    Logger.Information("Self-update: release {0} has no .zip asset to install.", release.TagName);
                    return false;
                }

                var workDir = Path.Combine(Path.GetTempPath(), "BakaLoaderUpdate");
                Directory.CreateDirectory(workDir);
                var zipPath = Path.Combine(workDir, "update.zip");
                TryDelete(zipPath);

                Logger.Information("Self-update: downloading {0} -> {1}", asset.BrowserDownloadUrl, zipPath);
                await DownloadFileAsync(asset.BrowserDownloadUrl, zipPath);

                if (!ZipContainsExe(zipPath))
                {
                    Logger.Error("Self-update: downloaded zip does not contain {0}; aborting.", ExeName);
                    TryDelete(zipPath);
                    return false;
                }

                var installDir = Path.GetDirectoryName(GetCurrentExePath());
                if (string.IsNullOrWhiteSpace(installDir))
                {
                    Logger.Error("Self-update: could not resolve the install directory; aborting.");
                    return false;
                }

                LaunchWatchdog(workDir, zipPath, installDir);
                Logger.Information("Self-update: watchdog launched for release {0}; app will now close to update.", release.TagName);
                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Self-update: failed to check or stage an update.");
                return false;
            }
        }

        private async Task DownloadFileAsync(string url, string destinationPath)
        {
            using var client = HttpClientProvider.CreateClient();
            client.Timeout = DownloadTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

            using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination);
        }

        private static bool ZipContainsExe(string zipPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                return archive.Entries.Any(e =>
                    string.Equals(Path.GetFileName(e.FullName), ExeName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static string GetCurrentExePath()
        {
            using var proc = Process.GetCurrentProcess();
            return proc.MainModule?.FileName;
        }

        /// <summary>
        /// Writes the watchdog PowerShell script to the work directory and launches it as a
        /// detached, hidden process. The script waits for this app's PID to exit before it
        /// touches any files, so the app is safe to close immediately after this returns.
        /// </summary>
        private void LaunchWatchdog(string workDir, string zipPath, string installDir)
        {
            var appPid = Process.GetCurrentProcess().Id;
            var exePath = Path.Combine(installDir, ExeName);
            var extractDir = Path.Combine(workDir, "extracted");
            var scriptPath = Path.Combine(workDir, "update-watchdog.ps1");

            var script = BuildWatchdogScript(appPid, zipPath, extractDir, installDir, exePath, workDir);
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDir,
            };

            Process.Start(psi);
        }

        private static string BuildWatchdogScript(
            int appPid, string zipPath, string extractDir, string installDir, string exePath, string workDir)
        {
            // A self-deleting, windowless updater. Every path is passed as a literal so the
            // script has no external dependencies beyond stock PowerShell + robocopy.
            return $@"
$ErrorActionPreference = 'Stop'
$appPid     = {appPid}
$zipPath    = '{Escape(zipPath)}'
$extractDir = '{Escape(extractDir)}'
$installDir = '{Escape(installDir)}'
$exePath    = '{Escape(exePath)}'
$workDir    = '{Escape(workDir)}'

# 1. Wait for BakaLoader to fully exit so its files unlock.
try {{ Wait-Process -Id $appPid -Timeout 120 -ErrorAction SilentlyContinue }} catch {{}}
Start-Sleep -Seconds 1

# 2. Fresh extraction of the downloaded release.
if (Test-Path $extractDir) {{ Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue }}
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

# 3. Find the folder that actually contains the exe (zips may nest it one level deep).
$exeFile = Get-ChildItem -Path $extractDir -Filter '{ExeName}' -Recurse -File | Select-Object -First 1
if (-not $exeFile) {{ exit 1 }}
$srcDir = $exeFile.DirectoryName

# 4. Copy the new files over the install directory. /R + /W keep retries short if a
#    handle lingers; the exe itself is excluded on this pass then copied last.
robocopy $srcDir $installDir /E /R:3 /W:2 /XF '{ExeName}' | Out-Null
Copy-Item -Path $exeFile.FullName -Destination $exePath -Force

# 5. Relaunch the updated app.
Start-Process -FilePath $exePath -WorkingDirectory $installDir

# 6. Clean up our own temp working folder.
Start-Sleep -Seconds 1
try {{ Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue }} catch {{}}
";
        }

        private static string Escape(string path) => path?.Replace("'", "''");

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Logger.Debug("Self-update: could not delete {0}: {1}", path, e.Message);
            }
        }
    }
}
