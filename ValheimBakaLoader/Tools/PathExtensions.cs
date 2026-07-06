using System;
using System.IO;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Path validation helpers. Inputs may contain environment variables;
    /// failures throw with a message suitable for showing the user.
    /// </summary>
    public static class PathExtensions
    {
        /// <summary>
        /// Resolves a path to an existing file, optionally requiring an
        /// extension to be present on the path.
        /// </summary>
        public static FileInfo GetFileInfo(string path, string extension = null)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);

            if (string.IsNullOrWhiteSpace(expanded))
                throw new ArgumentException("Cannot open file, path is not defined.");

            if (extension != null && !Path.HasExtension(expanded))
                throw new ArgumentException($"Cannot open file, must point to a valid {extension} file:{Environment.NewLine}{expanded}");

            if (!File.Exists(expanded))
                throw new FileNotFoundException($"File not found at path:{Environment.NewLine}{expanded}");

            return new FileInfo(expanded);
        }

        /// <summary>Resolves a directory path, optionally requiring it to exist.</summary>
        public static DirectoryInfo GetDirectoryInfo(string path, bool checkExists = false)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);

            if (string.IsNullOrWhiteSpace(expanded))
                throw new ArgumentException("Cannot open directory, path is not defined.");

            if (checkExists && !Directory.Exists(expanded))
                throw new DirectoryNotFoundException($"Directory not found at path:{Environment.NewLine}{expanded}");

            return new DirectoryInfo(expanded);
        }

        /// <summary>Makes an arbitrary string safe to use as a file name.</summary>
        public static string GetValidFileName(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return "file";

            // Stay comfortably under the 255-char filesystem limit, leaving
            // room for suffixes like rolling-log dates.
            if (filename.Length > 150) filename = filename[..150];

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                filename = filename.Replace(invalid, '-');
            }

            return filename;
        }
    }
}
