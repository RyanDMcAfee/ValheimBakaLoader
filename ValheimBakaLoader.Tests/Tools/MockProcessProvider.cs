using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using ValheimBakaLoader.Tools.Processes;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// In-memory IProcessProvider for tests: registers processes but never
    /// starts or kills anything real.
    /// </summary>
    public class MockProcessProvider : IProcessProvider
    {
        private readonly ConcurrentDictionary<string, Process> Registry = new();

        public void AddProcess(string key, Process process)
        {
            if (!Registry.TryAdd(key, process))
            {
                throw new InvalidOperationException($"A process is already registered under '{key}'.");
            }
        }

        public Process GetProcess(string key)
            => Registry.TryGetValue(key, out var process) ? process : null;

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
            return process;
        }

        public void StartIO(Process process)
        {
            // Never start real processes in tests.
        }

        public void SafelyKillProcess(string key)
        {
            // Nothing to kill in tests.
        }
    }
}
