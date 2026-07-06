using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools.Http;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Discovers the machine's LAN address and (via a lookup service) its
    /// public address, so the UI can show players what to connect to.
    /// </summary>
    public interface IIpAddressProvider
    {
        string ExternalIpAddress { get; }

        string InternalIpAddress { get; }

        event EventHandler<string> ExternalIpChanged;

        event EventHandler<string> InternalIpChanged;

        Task LoadExternalIpAddressAsync();

        Task LoadInternalIpAddressAsync();
    }

    public class IpAddressProvider : RestClient, IIpAddressProvider
    {
        private string _external;
        private string _internal;

        public IpAddressProvider(IRestClientContext context) : base(context)
        {
        }

        public event EventHandler<string> ExternalIpChanged;

        public event EventHandler<string> InternalIpChanged;

        public string ExternalIpAddress
        {
            get => _external;
            private set => SetAndNotify(ref _external, value, ExternalIpChanged);
        }

        public string InternalIpAddress
        {
            get => _internal;
            private set => SetAndNotify(ref _internal, value, InternalIpChanged);
        }

        public Task LoadExternalIpAddressAsync()
        {
            return Get(Resources.UrlExternalIpLookup)
                .WithCallback<IpLookupResult>((_, result) =>
                {
                    if (!string.IsNullOrWhiteSpace(result?.Ip))
                    {
                        ExternalIpAddress = result.Ip;
                    }
                })
                .SendAsync();
        }

        public Task LoadInternalIpAddressAsync()
        {
            // Candidate = a non-loopback IPv4 address on an interface that is
            // up and actually routes somewhere (has a gateway).
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Select(nic => nic.GetIPProperties())
                .Where(props => props.GatewayAddresses.Any())
                .SelectMany(props => props.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a.Address))
                .ToList();

            if (candidates.Count == 0)
            {
                Logger.Warning("No LAN address found: no routable IPv4 interfaces are up");
                return Task.CompletedTask;
            }

            // DHCP-assigned addresses are the most likely "real" LAN address;
            // sorting keeps the pick stable when there are several.
            var dhcp = candidates.Where(a => a.PrefixOrigin == PrefixOrigin.Dhcp).ToList();
            var pool = dhcp.Count > 0 ? dhcp : candidates;

            InternalIpAddress = pool
                .Select(a => a.Address.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderBy(s => s)
                .FirstOrDefault();

            return Task.CompletedTask;
        }

        private void SetAndNotify(ref string field, string value, EventHandler<string> changed)
        {
            if (field == value) return;

            field = value;
            changed?.Invoke(this, value);
        }

        private class IpLookupResult
        {
            [JsonProperty("ip")] public string Ip { get; set; }
        }
    }
}
