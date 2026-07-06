using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Data;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Single-slot in-memory IFileProvider: whatever was saved last is
    /// returned for any path, and nothing touches disk.
    /// </summary>
    public class MockDataFileProvider : IFileProvider
    {
        private object Stored;

        public Task<TFile> LoadAsync<TFile>(string filePath) where TFile : class
            => Task.FromResult(Stored as TFile);

        public Task SaveAsync<TFile>(string filePath, TFile data) where TFile : class
        {
            Stored = data;
            return Task.CompletedTask;
        }
    }
}
