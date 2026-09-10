using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools.Data;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// A preference entry that lives in a named collection inside
    /// userprefs.json (server profiles, worlds).
    /// </summary>
    public interface INamedEntry
    {
        string EntryName { get; }

        DateTime LastSaved { get; set; }
    }

    public interface IUserPreferencesProvider
    {
        event EventHandler<UserPreferences> PreferencesSaved;

        UserPreferences LoadPreferences();

        void SavePreferences(UserPreferences preferences);

        /// <summary>
        /// The only safe way to change userprefs.json: load, change, write back, all three under
        /// the one gate every other writer of that file takes.
        /// <para>
        /// <see cref="SavePreferences"/> writes the WHOLE document, servers and worlds included,
        /// so a caller that loads the document, edits its own corner and saves is not editing one
        /// field: it is replacing every field, with whatever the rest of them looked like when it
        /// loaded. The Discord status post did exactly that from a timer thread while the server's
        /// stdout thread recorded a launched build, and the profile's launch history went back to
        /// what it was seconds earlier, which is the difference between a quiet start and the
        /// launch guard asking about a build change that already happened.
        /// </para>
        /// <para>
        /// The default body is the whole implementation, so an in-memory provider in a test gets
        /// the same serialisation without writing a line.
        /// </para>
        /// </summary>
        void Mutate(Action<UserPreferences> change)
        {
            if (change == null) return;

            Mutate(prefs =>
            {
                change(prefs);
                return true;
            });
        }

        /// <summary>
        /// The same read, change and write under one gate, for a caller that only sometimes has
        /// something to write. Returning false from <paramref name="change"/> leaves the document
        /// exactly as it was and writes nothing, so a no-op cannot cost a disk write or a saved
        /// event. Answers whether anything was written.
        /// </summary>
        bool Mutate(Func<UserPreferences, bool> change)
        {
            if (change == null) return false;

            lock (PreferencesFileGate.Gate)
            {
                var prefs = LoadPreferences();
                if (prefs == null) return false;

                if (!change(prefs)) return false;

                SavePreferences(prefs);
                return true;
            }
        }
    }

    public interface IServerPreferencesProvider
    {
        event EventHandler<List<ServerPreferences>> PreferencesSaved;

        ServerPreferences LoadPreferences(string profileName);

        IEnumerable<ServerPreferences> LoadPreferences();

        void SavePreferences(ServerPreferences preferences);

        void RemovePreferences(string profileName);
    }

    public interface IWorldPreferencesProvider
    {
        event EventHandler<List<WorldPreferences>> PreferencesSaved;

        WorldPreferences LoadPreferences(string worldName);

        IEnumerable<WorldPreferences> LoadPreferences();

        void SavePreferences(WorldPreferences preferences);

        void RemovePreferences(string worldName);
    }

    /// <summary>
    /// Owns the userprefs.json file. A missing or unreadable file simply
    /// yields the built-in defaults, so first launch needs no special casing.
    /// </summary>
    public class UserPreferencesProvider : JsonFileProvider, IUserPreferencesProvider
    {
        public UserPreferencesProvider(ILogger logger) : base(logger)
        {
        }

        public event EventHandler<UserPreferences> PreferencesSaved;

        public UserPreferences LoadPreferences()
        {
            try
            {
                // LoadAsync returns null for a missing/unreadable file, and
                // FromFile(null) hands back every default.
                var file = LoadAsync<UserPreferencesFile>(Resources.UserPrefsFilePathV2)
                    .GetAwaiter().GetResult();
                return UserPreferences.FromFile(file);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not read user preferences; continuing with defaults");
                return UserPreferences.GetDefault();
            }
        }

        public void SavePreferences(UserPreferences preferences)
        {
            try
            {
                SaveAsync(Resources.UserPrefsFilePathV2, preferences.ToFile())
                    .GetAwaiter().GetResult();
                Logger.Information("User preferences written to disk");
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not write user preferences");
                return;
            }

            PreferencesSaved?.Invoke(this, preferences);
        }
    }

    /// <summary>
    /// The one gate every read-modify-write of userprefs.json goes through. Server profiles,
    /// worlds and the user's own settings all live in that single file, so saving a profile is
    /// really "load the whole document, change one entry, write the whole document back". The
    /// file provider locks each disk operation on its own, which is not enough: two threads can
    /// both load before either writes, and the later write drops the other's change.
    /// <para>
    /// It has to sit outside the generic type: a static field inside
    /// <c>PreferencesSection&lt;T&gt;</c> would give server profiles and worlds a lock each,
    /// which is exactly the pair that needs to share one.
    /// </para>
    /// </summary>
    internal static class PreferencesFileGate
    {
        public static readonly object Gate = new();
    }

    /// <summary>
    /// Shared plumbing for the named collections inside user preferences.
    /// Subclasses only say which list they own and what to call it in logs.
    /// </summary>
    public abstract class PreferencesSection<T> where T : class, INamedEntry
    {
        private readonly IUserPreferencesProvider Root;
        private readonly ILogger Logger;
        private readonly string Noun;

        protected PreferencesSection(IUserPreferencesProvider root, ILogger logger, string noun)
        {
            Root = root;
            Logger = logger;
            Noun = noun;

            Root.PreferencesSaved += (_, prefs) => PreferencesSaved?.Invoke(this, ListOf(prefs));
        }

        public event EventHandler<List<T>> PreferencesSaved;

        /// <summary>The list within the root preferences that this section owns.</summary>
        protected abstract List<T> ListOf(UserPreferences prefs);

        public IEnumerable<T> LoadPreferences() => ListOf(Root.LoadPreferences());

        public T LoadPreferences(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"A name is required to look up {Noun} preferences");

            var matches = LoadPreferences().Where(e => e.EntryName == name).ToList();
            if (matches.Count > 1)
            {
                Logger.Warning("Found {count} {noun} entries named '{name}'; using the newest",
                    matches.Count, Noun, name);
            }

            return matches.OrderByDescending(e => e.LastSaved).FirstOrDefault();
        }

        public void SavePreferences(T preferences)
        {
            if (preferences == null) return;

            // Load, change, write back - all three under one gate. The server's stdout thread
            // records a launched build here while the UI thread saves a profile edit; without
            // this the second write is built on a document read before the first one landed.
            lock (PreferencesFileGate.Gate)
            {
                var root = Root.LoadPreferences();
                var list = ListOf(root);

                list.RemoveAll(e => e.EntryName == preferences.EntryName);
                list.Add(preferences);
                preferences.LastSaved = DateTime.UtcNow;

                Root.SavePreferences(root);
            }

            Logger.Information("Saved {noun} preferences: {name}", Noun, preferences.EntryName);
        }

        public void RemovePreferences(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            // Same read-modify-write as SavePreferences, same gate.
            lock (PreferencesFileGate.Gate)
            {
                var root = Root.LoadPreferences();
                if (ListOf(root).RemoveAll(e => e.EntryName == name) == 0) return;

                Root.SavePreferences(root);
            }

            Logger.Information("Removed {noun} preferences: {name}", Noun, name);
        }
    }

    public class ServerPreferencesProvider : PreferencesSection<ServerPreferences>, IServerPreferencesProvider
    {
        public ServerPreferencesProvider(IUserPreferencesProvider userPreferencesProvider, ILogger logger)
            : base(userPreferencesProvider, logger, "server profile")
        {
        }

        protected override List<ServerPreferences> ListOf(UserPreferences prefs) => prefs.Servers;
    }

    public class WorldPreferencesProvider : PreferencesSection<WorldPreferences>, IWorldPreferencesProvider
    {
        public WorldPreferencesProvider(IUserPreferencesProvider userPreferencesProvider, ILogger logger)
            : base(userPreferencesProvider, logger, "world")
        {
        }

        protected override List<WorldPreferences> ListOf(UserPreferences prefs) => prefs.Worlds;
    }
}
