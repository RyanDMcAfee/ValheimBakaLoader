using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Windows.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Data;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Processes;

namespace ValheimBakaLoader.Tests
{
    /// <summary>
    /// Base fixture for every test class: builds the real production
    /// container via <see cref="Program.ConfigureServices"/>, then swaps the
    /// boundary services (disk, processes, network, user prefs) for
    /// in-memory fakes so tests never touch the outside world.
    /// </summary>
    public class BaseTest
    {
        protected IServiceCollection ServiceCollection { get; }

        protected IServiceProvider ServiceProvider { get; }

        protected MockDataFileProvider MockDataFileProvider { get; } = new();

        protected MockHttpClientProvider MockHttpClientProvider { get; } = new();

        protected MockProcessProvider MockProcessProvider { get; } = new();

        protected MockUserPreferencesProvider MockUserPreferencesProvider { get; } = new();

        public BaseTest()
        {
            ServiceCollection = new ServiceCollection();
            Program.ConfigureServices(ServiceCollection, Array.Empty<string>());

            Swap<IFileProvider>(MockDataFileProvider);
            Swap<IProcessProvider>(MockProcessProvider);
            Swap<IHttpClientProvider>(MockHttpClientProvider);
            Swap<IUserPreferencesProvider>(MockUserPreferencesProvider);

            ServiceProvider = ServiceCollection.BuildServiceProvider();
        }

        private void Swap<TService>(TService fake) where TService : class
            => ServiceCollection.Replace(ServiceDescriptor.Singleton(fake));

        protected TService GetService<TService>()
            => ServiceProvider.GetRequiredService<TService>();

        protected TForm GetForm<TForm>() where TForm : Form
            => GetService<IFormProvider>().GetForm<TForm>();
    }
}
