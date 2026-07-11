using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// One recorded server happening - a join, a leave, a death, a start/stop/crash,
    /// or a mod update. Kept deliberately tiny (short JSON keys, nulls omitted) since
    /// the journal accumulates for months.
    /// </summary>
    public class AnalyticsEvent
    {
        [JsonProperty("t")]
        public DateTime TimeUtc { get; set; }

        /// <summary>start | stop | crash | join | leave | death | modup | modin</summary>
        [JsonProperty("k")]
        public string Kind { get; set; }

        /// <summary>Server profile name the event belongs to.</summary>
        [JsonProperty("s")]
        public string Server { get; set; }

        [JsonProperty("p", NullValueHandling = NullValueHandling.Ignore)]
        public string PlayerKey { get; set; }

        [JsonProperty("n", NullValueHandling = NullValueHandling.Ignore)]
        public string PlayerName { get; set; }

        [JsonProperty("c", NullValueHandling = NullValueHandling.Ignore)]
        public string Character { get; set; }

        [JsonProperty("m", NullValueHandling = NullValueHandling.Ignore)]
        public string Mod { get; set; }

        [JsonProperty("f", NullValueHandling = NullValueHandling.Ignore)]
        public string FromVersion { get; set; }

        [JsonProperty("v", NullValueHandling = NullValueHandling.Ignore)]
        public string ToVersion { get; set; }
    }

    public interface IAnalyticsService
    {
        /// <summary>Appends one event to the journal and schedules a background save.</summary>
        void Record(AnalyticsEvent evt);

        /// <summary>
        /// All retained events for one server profile, oldest first. Returns a snapshot -
        /// safe to iterate while new events keep arriving.
        /// </summary>
        List<AnalyticsEvent> EventsFor(string serverKey);
    }

    /// <summary>
    /// The Skald's journal: a local, append-only record of server happenings that powers
    /// the analytics hall. Everything stays on this machine (one JSON file in the app's
    /// AppData folder); nothing is ever uploaded. The journal self-prunes so it can run
    /// for years without growing unbounded: events older than 180 days are dropped, and
    /// the total is capped at 20,000 entries (oldest evicted first).
    /// </summary>
    public class AnalyticsService : IAnalyticsService
    {
        private const string FilePath = @"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\analytics.json";
        private const int MaxEvents = 20000;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);
        private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

        private readonly IApplicationLogger Logger;
        private readonly object Gate = new();
        private readonly List<AnalyticsEvent> Events = new();

        private bool Loaded;
        private bool SavePending;

        public AnalyticsService(IApplicationLogger logger)
        {
            Logger = logger;
        }

        public void Record(AnalyticsEvent evt)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.Kind)) return;
            if (evt.TimeUtc == default) evt.TimeUtc = DateTime.UtcNow;

            lock (Gate)
            {
                EnsureLoaded();
                Events.Add(evt);
                Prune();
                ScheduleSave();
            }
        }

        public List<AnalyticsEvent> EventsFor(string serverKey)
        {
            lock (Gate)
            {
                EnsureLoaded();
                return Events
                    .Where(e => string.Equals(e.Server, serverKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.TimeUtc)
                    .ToList();
            }
        }

        private string ExpandedPath => Environment.ExpandEnvironmentVariables(FilePath);

        private void EnsureLoaded()
        {
            if (Loaded) return;
            Loaded = true;
            try
            {
                var path = ExpandedPath;
                if (!File.Exists(path)) return;
                var file = JsonConvert.DeserializeObject<AnalyticsFile>(File.ReadAllText(path));
                if (file?.Events != null) Events.AddRange(file.Events.Where(e => e != null));
                Prune();
            }
            catch (Exception e)
            {
                Logger.Warning("Could not load the analytics journal (starting fresh): {msg}", e.Message);
            }
        }

        private void Prune()
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            Events.RemoveAll(e => e.TimeUtc < cutoff);
            if (Events.Count > MaxEvents)
            {
                Events.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
                Events.RemoveRange(0, Events.Count - MaxEvents);
            }
        }

        private void ScheduleSave()
        {
            if (SavePending) return;
            SavePending = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(SaveDebounce);
                string json;
                lock (Gate)
                {
                    SavePending = false;
                    json = JsonConvert.SerializeObject(new AnalyticsFile { Events = Events.ToList() });
                }
                try
                {
                    var path = ExpandedPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, json);
                }
                catch (Exception e)
                {
                    Logger.Warning("Could not save the analytics journal: {msg}", e.Message);
                }
            });
        }

        private class AnalyticsFile
        {
            [JsonProperty("events")]
            public List<AnalyticsEvent> Events { get; set; }
        }
    }
}
