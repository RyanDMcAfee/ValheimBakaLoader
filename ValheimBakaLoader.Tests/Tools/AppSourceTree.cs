using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The app's own C# source, read once for the whole test run.
    /// <para>
    /// Several gates here are on the source rather than on a value, because what they
    /// guard against is a line somebody adds later. Each of those used to walk the
    /// project folder itself, which meant crossing the build output on every one of
    /// them; on a synced drive that is slow enough to starve a timing-sensitive test
    /// running alongside. The walk now skips build folders outright and the result is
    /// held, so every gate costs one dictionary lookup after the first.
    /// </para>
    /// </summary>
    public static class AppSourceTree
    {
        private static readonly object Lock = new();
        private static Dictionary<string, string> Cached;

        /// <summary>The repository root, found from this file's own compiled-in path.</summary>
        public static string RepoRoot([CallerFilePath] string here = "")
        {
            var tools = Path.GetDirectoryName(here);                 // ...\ValheimBakaLoader.Tests\Tools
            var tests = Path.GetDirectoryName(tools);                // ...\ValheimBakaLoader.Tests
            return Path.GetDirectoryName(tests);                     // the repository root
        }

        /// <summary>
        /// Source text with every line ending as LF. The repository stores LF, but a checkout
        /// on a runner with Git's Windows default (core.autocrlf true) rewrites the working
        /// copy to CRLF, and a gate that counts a literal spanning two lines then finds
        /// nothing. Every read here goes through this, so what the gates see is the same on
        /// every machine.
        /// </summary>
        public static string Lf(string text) => text?.Replace("\r\n", "\n");

        /// <summary>One file of app source, by its path from the app project folder.</summary>
        public static string Read(params string[] pathParts) =>
            Lf(File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(pathParts))));

        private static readonly Dictionary<string, string> WebCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>One of the interface's own files (app.js, index.html, app.css), read once.</summary>
        public static string Web(string fileName)
        {
            lock (Lock)
            {
                if (WebCache.TryGetValue(fileName, out var held)) return held;

                var text = Lf(File.ReadAllText(Path.Combine(RepoRoot(), "ValheimBakaLoader", "WebUI", fileName)));
                WebCache[fileName] = text;
                return text;
            }
        }

        /// <summary>
        /// Every .cs file in the app project, keyed by file name, with the build folders
        /// left out. A name that appears twice keeps whichever was read first, which is
        /// fine: nothing here asks about a file by name alone.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Files()
        {
            lock (Lock)
            {
                if (Cached != null) return Cached;

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Walk(Path.Combine(RepoRoot(), "ValheimBakaLoader"), map);
                Cached = map;
                return Cached;
            }
        }

        /// <summary>
        /// Every folder of C# that ends up on a host's disk. Four rather than one, and the
        /// three after the first are the ones a sweep forgets: ValheimBakaLoader.Tools is a
        /// second assembly beside the app, SolutionResources\ClientSecrets.cs is compiled
        /// straight into the app by the csproj, and BakaLoaderItemIndexer is one of the bundled
        /// plugins, built into Resources\ItemIndexer and installed into a host's server.
        /// </summary>
        public static readonly string[] ShippedProjects =
        {
            "ValheimBakaLoader", "ValheimBakaLoader.Tools", "SolutionResources",
            "BakaLoaderItemIndexer",
        };

        private static Dictionary<string, string> CachedShipped;

        /// <summary>
        /// Every .cs file in every folder that ships, keyed by its path from the repository
        /// root with forward slashes, build folders left out.
        /// <para>
        /// <see cref="Files"/> reads the app project alone and keys by bare file name, which is
        /// what the gates asking "does this one file say X" want. A gate whose answer is
        /// "nothing ANYWHERE does X" needs two other things: every folder in
        /// <see cref="ShippedProjects"/>, because a rule that stops at the app's own folder is a
        /// rule with a blind half; and a key that names the path, because a finding somebody has
        /// to go and look at is worth a path rather than a file name two folders might share.
        /// </para>
        /// </summary>
        public static IReadOnlyDictionary<string, string> ShippedFiles()
        {
            lock (Lock)
            {
                if (CachedShipped != null) return CachedShipped;

                var root = RepoRoot();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var project in ShippedProjects)
                {
                    var folder = Path.Combine(root, project);
                    if (!Directory.Exists(folder))
                        throw new DirectoryNotFoundException(
                            "a folder the gates sweep is not on disk: " + folder
                            + ". A sweep that quietly reads some of the folders rather than all "
                            + "of them is the false green those gates exist to avoid.");

                    WalkByPath(folder, root, map);
                }

                CachedShipped = map;
                return CachedShipped;
            }
        }

        private static void WalkByPath(
            string directory, string root, Dictionary<string, string> map)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            {
                var key = Path.GetRelativePath(root, file).Replace('\\', '/');
                map[key] = Lf(File.ReadAllText(file));
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                WalkByPath(child, root, map);
            }
        }

        private static void Walk(string directory, Dictionary<string, string> map)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            {
                var name = Path.GetFileName(file);
                if (!map.ContainsKey(name)) map[name] = Lf(File.ReadAllText(file));
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                // Build output is a copy of the source questions here are about, and on a
                // synced drive it is expensive to cross. Never walked.
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(child, map);
            }
        }
    }
}
