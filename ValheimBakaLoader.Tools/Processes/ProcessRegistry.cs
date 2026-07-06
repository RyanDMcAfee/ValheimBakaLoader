using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ValheimBakaLoader.Tools.Processes
{
    /// <summary>
    /// Tracks the child processes the app launches, keyed by name, so the
    /// server lifecycle code can find, start, and stop them without holding
    /// Process references itself. Tests swap in an in-memory fake.
    /// </summary>
    public interface IProcessProvider
    {
        /// <summary>Registers a process under a key. Throws if the key is taken.</summary>
        void AddProcess(string key, Process process);

        /// <summary>Returns the live process for a key, or null if unknown or exited.</summary>
        Process GetProcess(string key);

        /// <summary>
        /// Builds a windowless process with redirected output, registers it
        /// under the key, and returns it un-started so the caller can attach
        /// output handlers before calling <see cref="StartIO"/>.
        /// </summary>
        Process AddBackgroundProcess(string key, string command, string args);

        /// <summary>Starts a registered process and begins async output reads.</summary>
        void StartIO(Process process);

        /// <summary>
        /// Politely asks the process registered under the key to close.
        /// No-op when the key isn't registered or the process already exited.
        /// </summary>
        void SafelyKillProcess(string key);
    }

    public class ProcessProvider : IProcessProvider
    {
        private readonly ConcurrentDictionary<string, Process> Registry = new();

        public void AddProcess(string key, Process process)
        {
            if (!Registry.TryAdd(key, process))
            {
                throw new InvalidOperationException($"A process is already registered under '{key}'.");
            }

            // Registrations clean themselves up once the process ends.
            process.Exited += (_, _) => Registry.TryRemove(key, out _);
        }

        public Process GetProcess(string key)
        {
            if (!Registry.TryGetValue(key, out var process)) return null;

            try
            {
                if (process.HasExited) return null;
            }
            catch (Exception)
            {
                // HasExited throws until the process has been started;
                // a registered-but-not-started process still counts as live.
            }

            return process;
        }

        public Process AddBackgroundProcess(string key, string command, string args)
        {
            var process = new Process
            {
                EnableRaisingEvents = true,
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            AddProcess(key, process);
            return GetProcess(key);
        }

        public void StartIO(Process process)
        {
            process.Start();

            // Tie the child to this app's Job Object so it can never outlive us.
            ChildProcessTracker.AddProcess(process);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public void SafelyKillProcess(string key)
        {
            var target = GetProcess(key);
            if (target == null) return;

            // taskkill WITHOUT /F sends a close request rather than terminating,
            // which gives the Valheim server the chance to flush a final world
            // save on the way down. Do not make this forceful.
            var killer = AddBackgroundProcess($"taskkill-{target.Id}", "taskkill", $"/pid {target.Id}");
            StartIO(killer);
        }
    }
}
