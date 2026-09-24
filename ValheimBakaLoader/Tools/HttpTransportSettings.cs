using Microsoft.Extensions.DependencyInjection;
using System;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The two connection switches, read off the host's preferences for the one place that
    /// makes HttpClients. Every remote client in the app asks HttpClientProvider for its
    /// client, so wiring this here is what puts the switches in front of all of them at once.
    /// <para>
    /// The preferences provider is resolved lazily. It logs, the logger reads preferences,
    /// and asking for it in the constructor would close that ring while the container is
    /// still being built.
    /// </para>
    /// </summary>
    public class HttpTransportSettings : IHttpTransportSettings
    {
        private readonly IServiceProvider Services;
        private IUserPreferencesProvider Prefs;

        public HttpTransportSettings(IServiceProvider services)
        {
            Services = services;
        }

        public HttpTransportOptions Current
        {
            get
            {
                Prefs ??= Services.GetService<IUserPreferencesProvider>();
                var saved = Prefs?.LoadPreferences();
                if (saved == null) return HttpTransportOptions.Default;

                return new HttpTransportOptions
                {
                    BypassProxy = saved.BypassSystemProxy,
                    IPv4Only = saved.ForceIPv4,
                };
            }
        }
    }
}
