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

            var root = Root.LoadPreferences();
            var list = ListOf(root);

            list.RemoveAll(e => e.EntryName == preferences.EntryName);
            list.Add(preferences);
            preferences.LastSaved = DateTime.UtcNow;

            Root.SavePreferences(root);
            Logger.Information("Saved {noun} preferences: {name}", Noun, preferences.EntryName);
        }

        public void RemovePreferences(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            var root = Root.LoadPreferences();
            if (ListOf(root).RemoveAll(e => e.EntryName == name) == 0) return;

            Root.SavePreferences(root);
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
