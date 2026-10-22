using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace ValheimBakaLoader.Service.Tls;

/// <summary>Where the certificate came from, for the log line at start.</summary>
public enum CertificateSource
{
    None,
    Given,
    SelfSigned,
}

/// <summary>
/// The decision about the bind, taken before Kestrel is touched so it can be tested on
/// its own and so a refusal is one clear sentence rather than a stack trace out of the
/// server. The rule: loopback may go bare, anything else goes under TLS or not at all.
/// </summary>
public sealed class ListenPlan
{
    public IPEndPoint EndPoint { get; }
    public bool UseTls { get; }
    public CertificateSource CertificateSource { get; }
    public string? CertificateFolder { get; }

    private ListenPlan(IPEndPoint endPoint, bool useTls, CertificateSource source, string? folder)
    {
        EndPoint = endPoint;
        UseTls = useTls;
        CertificateSource = source;
        CertificateFolder = folder;
    }

    public static ListenPlan Resolve(ServiceOptions options)
    {
        var endPoint = ParseBind(options.Bind);
        var loopback = IPAddress.IsLoopback(endPoint.Address);
        var given = !string.IsNullOrWhiteSpace(options.CertPath) || !string.IsNullOrWhiteSpace(options.KeyPath);

        if (given)
        {
            if (string.IsNullOrWhiteSpace(options.CertPath) || string.IsNullOrWhiteSpace(options.KeyPath))
                throw new InvalidOperationException("A certificate needs both --cert and --key.");
            if (!File.Exists(options.CertPath)) throw new FileNotFoundException("The certificate file is missing.", options.CertPath);
            if (!File.Exists(options.KeyPath)) throw new FileNotFoundException("The key file is missing.", options.KeyPath);
            return new ListenPlan(endPoint, true, CertificateSource.Given, null);
        }

        // Loopback is the SSH tunnel and reverse proxy case: nothing leaves the box, so no
        // certificate is made that nobody would ever be shown.
        if (loopback)
            return new ListenPlan(endPoint, false, CertificateSource.None, null);

        if (options.Tls == TlsMode.Off)
        {
            throw new InvalidOperationException(
                $"Refusing to listen on {endPoint} without TLS: a login over plain HTTP on a network " +
                "is a password in the clear. Bind to 127.0.0.1 behind a tunnel or a reverse proxy, " +
                "give --cert and --key, or drop --no-tls so a self-signed certificate is made.");
        }

        return new ListenPlan(endPoint, true, CertificateSource.SelfSigned, Path.Combine(options.DataDir, "tls"));
    }

    /// <summary>The certificate this plan serves with, or null for a bare loopback bind.</summary>
    public X509Certificate2? LoadCertificate(ServiceOptions options)
    {
        return CertificateSource switch
        {
            CertificateSource.Given => SelfSignedCertificate.Load(options.CertPath!, options.KeyPath!),
            CertificateSource.SelfSigned => SelfSignedCertificate.EnsureInFolder(
                CertificateFolder!, HostNamesFor(EndPoint)),
            _ => null,
        };
    }

    private static IEnumerable<string> HostNamesFor(IPEndPoint endPoint)
    {
        yield return "localhost";
        yield return Dns.GetHostName();
        if (!endPoint.Address.Equals(IPAddress.Any) && !endPoint.Address.Equals(IPAddress.IPv6Any))
            yield return endPoint.Address.ToString();
    }

    /// <summary>"host:port", "[v6]:port", a bare port, or "localhost:port".</summary>
    public static IPEndPoint ParseBind(string bind)
    {
        var text = (bind ?? "").Trim();
        if (text.Length == 0) text = ServiceOptions.DefaultBind;

        if (int.TryParse(text, out var portOnly))
            return new IPEndPoint(IPAddress.Loopback, CheckPort(portOnly));

        if (text.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
            return new IPEndPoint(IPAddress.Loopback, CheckPort(int.Parse(text["localhost:".Length..])));

        if (IPEndPoint.TryParse(text, out var endPoint) && endPoint.Port != 0)
            return endPoint;

        throw new ArgumentException($"Cannot read '{bind}' as host:port.", nameof(bind));
    }

    private static int CheckPort(int port) =>
        port is > 0 and < 65536 ? port : throw new ArgumentException($"Port {port} is out of range.");
}
