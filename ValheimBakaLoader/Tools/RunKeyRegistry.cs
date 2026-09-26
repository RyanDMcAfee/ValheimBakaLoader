using Microsoft.Win32;
using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>Which of the two Run keys a call is about.</summary>
    public enum RunKeyHive
    {
        /// <summary>HKEY_CURRENT_USER: this account only, and writable without elevation.</summary>
        CurrentUser,

        /// <summary>HKEY_LOCAL_MACHINE: every account on the PC, and writable only when elevated.</summary>
        LocalMachine,
    }

    /// <summary>What a delete did, which a bare true or false cannot say.</summary>
    public enum RunKeyDeleteOutcome
    {
        /// <summary>The entry was there and it is gone.</summary>
        Removed,

        /// <summary>There was nothing of that name to remove.</summary>
        NotThere,

        /// <summary>Windows refused the write. On the machine hive that means "not elevated".</summary>
        Refused,
    }

    /// <summary>
    /// The Windows Run key, behind a seam so the startup preference can be driven in a test
    /// without a registry.
    /// <para>
    /// The shape matters more than it looks. A read opens the key READ ONLY, which a normal
    /// account is allowed to do in both hives, so nothing has to be caught and nothing has to
    /// be logged for it. A write only ever names the current user's hive: the preference is
    /// per account and needs no elevation, and an entry under the machine hive is one this
    /// application will not write.
    /// </para>
    /// </summary>
    public interface IRunKeyRegistry
    {
        /// <summary>
        /// What that hive's Run key holds under this name, or null when it holds nothing and
        /// when the key cannot be read at all. Never throws.
        /// </summary>
        string Read(RunKeyHive hive, string entryName);

        /// <summary>
        /// Writes the entry under the CURRENT USER's hive. True when it landed.
        /// <para>
        /// When it did not, <paramref name="problem"/> carries what Windows said, in the shape
        /// "TypeOfTheProblem: the sentence Windows wrote". A write that is refused is the one
        /// case where the switch would read on while nothing started, so the caller has a line
        /// to log that names the reason rather than a bare false.
        /// </para>
        /// </summary>
        bool Write(string entryName, string value, out string problem);

        /// <summary>Removes the entry from that hive, and says which of the three things happened.</summary>
        RunKeyDeleteOutcome Delete(RunKeyHive hive, string entryName);
    }

    /// <inheritdoc cref="IRunKeyRegistry"/>
    public sealed class WindowsRunKeyRegistry : IRunKeyRegistry
    {
        /// <summary>The one key this application ever opens.</summary>
        public const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public string Read(RunKeyHive hive, string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName)) return null;

            try
            {
                // writable: false is the whole point. Opening this for writing is what used to
                // make every read of the machine hive throw on an ordinary account.
                using var key = Root(hive).OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(entryName)?.ToString();
            }
            catch
            {
                // A key that cannot be read holds nothing as far as this application is
                // concerned. There is no host-facing difference between the two.
                return null;
            }
        }

        public bool Write(string entryName, string value, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(entryName))
            {
                problem = "the entry has no name";
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null)
                {
                    problem = "the Run key for this account could not be opened for writing";
                    return false;
                }

                key.SetValue(entryName, value ?? "");
                return true;
            }
            catch (Exception e)
            {
                // The kind and the sentence, both, because the two answer different questions:
                // UnauthorizedAccessException is a policy saying no, IOException is the key
                // itself, and a host reading the log should not have to guess which.
                problem = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        public RunKeyDeleteOutcome Delete(RunKeyHive hive, string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName)) return RunKeyDeleteOutcome.NotThere;

            try
            {
                using var key = Root(hive).OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return RunKeyDeleteOutcome.Refused;
                if (key.GetValue(entryName) == null) return RunKeyDeleteOutcome.NotThere;
                key.DeleteValue(entryName, throwOnMissingValue: false);
                return RunKeyDeleteOutcome.Removed;
            }
            catch (UnauthorizedAccessException)
            {
                return RunKeyDeleteOutcome.Refused;
            }
            catch (System.Security.SecurityException)
            {
                return RunKeyDeleteOutcome.Refused;
            }
            catch
            {
                return RunKeyDeleteOutcome.Refused;
            }
        }

        private static RegistryKey Root(RunKeyHive hive) =>
            hive == RunKeyHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
    }
}
