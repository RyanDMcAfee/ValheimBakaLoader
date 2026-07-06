using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Broad classification used to decide which spawn modifier (creature star-level
    /// vs equipment quality) applies and how an entry is labelled in the picker.
    /// </summary>
    public enum ItemCategory
    {
        Item,
        Equipment,
        Resource,
        Creature,
    }

    /// <summary>
    /// A single spawnable thing the right-click "Spawn X at" picker can show. Populated
    /// either from the live, mod-aware <c>items.json</c> written by the BakaLoaderItemIndexer
    /// companion plugin, or from the bundled vanilla fallback list.
    /// </summary>
    public class ItemCatalogEntry
    {
        /// <summary>The in-game prefab name passed to the <c>spawn</c> command.</summary>
        [JsonProperty("prefab")]
        public string PrefabName { get; set; }

        /// <summary>The localized, human-readable name (mod items resolve their real name here).</summary>
        [JsonProperty("name")]
        public string DisplayName { get; set; }

        /// <summary>The localized description shown in the picker.</summary>
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("category")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ItemCategory Category { get; set; }

        /// <summary>True for equipment whose 3rd spawn argument is its quality (1-4).</summary>
        [JsonProperty("hasQuality")]
        public bool HasQuality { get; set; }

        /// <summary>True for creatures whose 3rd spawn argument is its star-level (0-2).</summary>
        [JsonProperty("hasLevel")]
        public bool HasLevel { get; set; }

        /// <summary>The best label to display, never null.</summary>
        [JsonIgnore]
        public string Label => string.IsNullOrWhiteSpace(DisplayName) ? PrefabName : DisplayName;
    }

    /// <summary>
    /// Loads and searches the spawnable-item catalog. Prefers the live <c>items.json</c>
    /// produced by the companion indexer plugin inside the server's BepInEx folder (so it
    /// reflects the actual installed mods); falls back to the bundled vanilla list shipped
    /// in <c>Resources/vanilla-items.json</c> until that first indexed start has run.
    /// </summary>
    public class ItemCatalog
    {
        private readonly IApplicationLogger Logger;
        private readonly object Lock = new();

        private List<ItemCatalogEntry> _entries = new();
        private string _loadedFrom;

        public ItemCatalog(IApplicationLogger logger)
        {
            Logger = logger;
            LoadBundledFallback();
        }

        /// <summary>The path the current catalog was loaded from (for diagnostics).</summary>
        public string LoadedFrom => _loadedFrom;

        /// <summary>True when the live, mod-aware catalog is in use (not the vanilla fallback).</summary>
        public bool IsLiveCatalog =>
            !string.IsNullOrEmpty(_loadedFrom) &&
            _loadedFrom.EndsWith("items.json", StringComparison.OrdinalIgnoreCase) &&
            !_loadedFrom.Equals(BundledPath, StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<ItemCatalogEntry> Entries
        {
            get { lock (Lock) return _entries.ToList(); }
        }

        private static string BundledPath =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "vanilla-items.json");

        /// <summary>
        /// Loads the live catalog from <c>&lt;bepInExDir&gt;/items.json</c> if it exists and is
        /// newer than what's already loaded; otherwise keeps the bundled vanilla fallback.
        /// Safe to call repeatedly (e.g. each time the picker opens).
        /// </summary>
        public void EnsureLoaded(string bepInExDir)
        {
            try
            {
                var candidate = string.IsNullOrWhiteSpace(bepInExDir)
                    ? null
                    : Path.Combine(bepInExDir, "items.json");

                if (candidate != null && File.Exists(candidate))
                {
                    if (string.Equals(_loadedFrom, candidate, StringComparison.OrdinalIgnoreCase)) return;
                    LoadFrom(candidate);
                }
                else if (_entries.Count == 0)
                {
                    LoadBundledFallback();
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Could not refresh item catalog: {message}", e.Message);
            }
        }

        private void LoadBundledFallback()
        {
            if (File.Exists(BundledPath))
            {
                LoadFrom(BundledPath);
            }
            else
            {
                Logger.Warning("Bundled vanilla item catalog not found at {path}", BundledPath);
            }
        }

        private void LoadFrom(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var entries = JsonConvert.DeserializeObject<List<ItemCatalogEntry>>(json) ?? new();
                entries = entries
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.PrefabName))
                    .ToList();

                lock (Lock)
                {
                    _entries = entries;
                    _loadedFrom = path;
                }

                Logger.Information("Loaded {count} catalog entries from {path}", entries.Count, path);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to load item catalog from {path}", path);
            }
        }

        /// <summary>
        /// Case-insensitive search over DisplayName then PrefabName, ranked exact &gt; starts-with
        /// &gt; contains. An empty term returns all entries alphabetically by label.
        /// </summary>
        public IEnumerable<ItemCatalogEntry> Search(string term)
        {
            var all = Entries;

            if (string.IsNullOrWhiteSpace(term))
            {
                return all.OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase);
            }

            term = term.Trim();

            int Rank(ItemCatalogEntry e)
            {
                var name = e.Label ?? string.Empty;
                var prefab = e.PrefabName ?? string.Empty;

                if (name.Equals(term, StringComparison.OrdinalIgnoreCase) ||
                    prefab.Equals(term, StringComparison.OrdinalIgnoreCase)) return 0;
                if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                    prefab.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 1;
                if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return 2;
                if (prefab.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
                return 99;
            }

            return all
                .Select(e => (entry: e, rank: Rank(e)))
                .Where(x => x.rank < 99)
                .OrderBy(x => x.rank)
                .ThenBy(x => x.entry.Label, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.entry);
        }
    }
}
