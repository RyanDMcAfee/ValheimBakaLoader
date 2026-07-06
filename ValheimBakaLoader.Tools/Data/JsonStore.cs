using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ValheimBakaLoader.Tools.Data
{
    /// <summary>An entity that can live in a keyed JSON collection.</summary>
    public interface IKeyed
    {
        string Key { get; }
    }

    /// <summary>
    /// Reads and writes whole JSON documents on disk. The interface exists so
    /// tests can swap the file system out for an in-memory stand-in.
    /// </summary>
    public interface IFileProvider
    {
        Task<TFile> LoadAsync<TFile>(string filePath) where TFile : class;

        Task SaveAsync<TFile>(string filePath, TFile data) where TFile : class;
    }

    /// <summary>
    /// Disk-backed IFileProvider using Newtonsoft.Json. Paths may contain
    /// environment variables (e.g. %USERPROFILE%). Reads that fail are logged
    /// and come back null; writes create the target directory and throw on
    /// failure so callers can decide how loud to be about it.
    /// </summary>
    public class JsonFileProvider : IFileProvider
    {
        private readonly object FileLock = new();

        public JsonFileProvider(ILogger logger)
        {
            Logger = logger;
        }

        protected ILogger Logger { get; }

        public Task<TFile> LoadAsync<TFile>(string filePath) where TFile : class
        {
            var path = Environment.ExpandEnvironmentVariables(filePath);
            TFile result = null;

            try
            {
                string json = null;
                lock (FileLock)
                {
                    if (File.Exists(path))
                    {
                        json = File.ReadAllText(path);
                    }
                }

                if (json != null)
                {
                    result = JsonConvert.DeserializeObject<TFile>(json);
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not read {path}", path);
            }

            return Task.FromResult(result);
        }

        public Task SaveAsync<TFile>(string filePath, TFile data) where TFile : class
        {
            var path = Environment.ExpandEnvironmentVariables(filePath);
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);

            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The on-disk shape of a keyed collection: a single "data" object mapping
    /// entity keys to entities. This matches the historical players.json layout,
    /// so existing files keep loading across versions.
    /// </summary>
    public class KeyedDataFile<TEntity>
    {
        public KeyedDataFile()
        {
        }

        public KeyedDataFile(Dictionary<string, TEntity> data)
        {
            Data = data;
        }

        [JsonProperty("data")]
        public Dictionary<string, TEntity> Data { get; set; } = new();
    }

    /// <summary>The read/query surface of a keyed entity collection.</summary>
    public interface IDataRepository<TEntity> where TEntity : IKeyed
    {
        /// <summary>Raised whenever an entity is added or replaced.</summary>
        event EventHandler<TEntity> EntityUpdated;

        IEnumerable<TEntity> Data { get; }

        TEntity FindById(string key);

        void Remove(TEntity entity);
    }

    /// <summary>
    /// A keyed entity collection persisted as one JSON file. Mutations update the
    /// in-memory dictionary immediately and flush the whole collection to disk in
    /// the background, so callers never wait on I/O and a failed write can never
    /// corrupt in-memory state.
    /// </summary>
    public abstract class KeyedJsonRepository<TEntity> : IDataRepository<TEntity>
        where TEntity : IKeyed
    {
        private readonly IFileProvider Files;
        private readonly string FilePath;
        private Dictionary<string, TEntity> Entities = new();

        protected KeyedJsonRepository(IFileProvider files, ILogger logger, string filePath)
        {
            Files = files;
            Logger = logger;
            FilePath = filePath;
        }

        protected ILogger Logger { get; }

        public event EventHandler<TEntity> EntityUpdated;

        public IEnumerable<TEntity> Data => Entities.Values;

        public TEntity FindById(string key)
        {
            return key != null && Entities.TryGetValue(key, out var entity) ? entity : default;
        }

        public virtual async Task LoadAsync()
        {
            var file = await Files.LoadAsync<KeyedDataFile<TEntity>>(FilePath);
            Entities = file?.Data ?? new Dictionary<string, TEntity>();
        }

        public void Remove(TEntity entity)
        {
            if (entity?.Key == null || !Entities.Remove(entity.Key)) return;

            SaveInBackground();
        }

        protected void Upsert(TEntity entity)
        {
            Entities[entity.Key] = entity;
            EntityUpdated?.Invoke(this, entity);

            SaveInBackground();
        }

        protected void UpsertBulk(IEnumerable<TEntity> entities)
        {
            foreach (var entity in entities)
            {
                Entities[entity.Key] = entity;
                EntityUpdated?.Invoke(this, entity);
            }

            SaveInBackground();
        }

        private void SaveInBackground()
        {
            // Snapshot now so the write isn't racing later mutations.
            var snapshot = new KeyedDataFile<TEntity>(new Dictionary<string, TEntity>(Entities));

            Task.Run(async () =>
            {
                try
                {
                    await Files.SaveAsync(FilePath, snapshot);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Could not save {path}", FilePath);
                }
            });
        }
    }
}
