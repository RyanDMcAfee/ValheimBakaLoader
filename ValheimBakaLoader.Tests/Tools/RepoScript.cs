using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Runs one of the repository's own gate scripts and hands back what it said.
    /// <para>
    /// Two of the gates here are not C#: the catalog check is python and the lookup's
    /// self test is node, because both have to read the files the way the shipped
    /// program reads them (JSON the way the page parses it, plural categories out of
    /// the same Intl the page selects with). A C# reimplementation of either would be
    /// a second opinion rather than a gate.
    /// </para>
    /// <para>
    /// Nothing here ever skips. A missing interpreter is reported as a failure with
    /// the list of names that were tried, because a gate that quietly stands down is
    /// the false green the whole catalog effort exists to avoid.
    /// </para>
    /// </summary>
    public static class RepoScript
    {
        private static readonly object Lock = new();
        private static string CachedPython;
        private static string CachedNode;

        /// <summary>The result of one run: what it printed, and whether it was happy.</summary>
        public sealed class Result
        {
            public int ExitCode { get; init; }
            public string Output { get; init; } = "";
            public bool Ok => ExitCode == 0;

            /// <summary>Everything it said, ready to put in an assertion message.</summary>
            public override string ToString() => "exit " + ExitCode + Environment.NewLine + Output;
        }

        /// <summary>Runs a program from the repository root and captures both streams.</summary>
        public static Result Run(string exe, params string[] arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = AppSourceTree.RepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process == null) return new Result { ExitCode = -1, Output = "could not start " + exe };

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(true); } catch (Exception) { /* it was already gone */ }
                return new Result { ExitCode = -2, Output = "timed out" + Environment.NewLine + stdout + stderr };
            }

            return new Result { ExitCode = process.ExitCode, Output = (stdout + stderr).Replace("\r\n", "\n") };
        }

        private static readonly string[] PythonNames =
        {
            "python", "py", @"C:\Python314\python.exe", "python3",
        };

        private static readonly string[] NodeNames = { "node", "node.exe" };

        /// <summary>
        /// The first interpreter on this machine that actually runs. The Windows Store
        /// python3 stub answers to the name and then opens a shop, so a candidate only
        /// counts once it has printed something back.
        /// </summary>
        public static string Python() => Resolve(ref CachedPython, PythonNames, new[] { "-c", "print(1)" }, "1");

        /// <summary>The node on this machine, proved the same way.</summary>
        public static string Node() => Resolve(ref CachedNode, NodeNames, new[] { "-e", "process.stdout.write('1')" }, "1");

        private static string Resolve(ref string cache, IEnumerable<string> names, string[] probe, string expected)
        {
            lock (Lock)
            {
                if (cache != null) return cache;
                var tried = new List<string>();
                foreach (var name in names)
                {
                    tried.Add(name);
                    try
                    {
                        var said = Run(name, probe);
                        if (said.Ok && said.Output.Trim() == expected) { cache = name; return cache; }
                    }
                    catch (Exception) { /* not on this machine under that name */ }
                }
                throw new InvalidOperationException(
                    "none of these could be run: " + string.Join(", ", tried) +
                    ". The catalog gates need it; they are not allowed to stand down.");
            }
        }

        /// <summary>A path inside the repository, from its parts.</summary>
        public static string At(params string[] parts) =>
            Path.Combine(AppSourceTree.RepoRoot(), Path.Combine(parts));
    }
}
