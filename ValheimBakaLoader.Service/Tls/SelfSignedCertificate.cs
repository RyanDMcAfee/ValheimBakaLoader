using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ValheimBakaLoader.Service.Tls;

/// <summary>
/// A certificate made once into the data folder for a service bound beyond loopback with
/// nothing better. The browser will warn, which is why the fingerprint is logged at start:
/// a host compares it with what the warning shows and then trusts it on purpose.
/// </summary>
public static class SelfSignedCertificate
{
    public const string CertFileName = "cert.pem";
    public const string KeyFileName = "key.pem";

    /// <summary>Loads the pair under <paramref name="folder"/>, or makes it when it is not there.</summary>
    public static X509Certificate2 EnsureInFolder(string folder, IEnumerable<string> hostNames)
    {
        var certPath = Path.Combine(folder, CertFileName);
        var keyPath = Path.Combine(folder, KeyFileName);

        if (File.Exists(certPath) && File.Exists(keyPath))
            return Load(certPath, keyPath);

        Directory.CreateDirectory(folder);
        using var fresh = Create(hostNames);
        using var rsa = fresh.GetRSAPrivateKey()!;

        File.WriteAllText(certPath, fresh.ExportCertificatePem() + "\n", new UTF8Encoding(false));
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem() + "\n", new UTF8Encoding(false));
        Auth.PasswordStore.RestrictToOwner(keyPath);

        return Load(certPath, keyPath);
    }

    /// <summary>
    /// A PEM pair as a certificate Kestrel can serve with. The PEM reader hands back an
    /// ephemeral key that SslStream cannot always use directly, so it goes through PKCS12
    /// once, which is the shape every platform's TLS stack accepts.
    /// </summary>
    public static X509Certificate2 Load(string certPath, string keyPath)
    {
        using var fromPem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), null);
    }

    public static X509Certificate2 Create(IEnumerable<string> hostNames, TimeSpan? lifetime = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=BakaLoader", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        var any = false;
        foreach (var name in hostNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
            any = true;
        }
        if (!any) san.AddDnsName("localhost");

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        var from = DateTimeOffset.UtcNow.AddMinutes(-5);
        var until = from + (lifetime ?? TimeSpan.FromDays(3650));
        return request.CreateSelfSigned(from, until);
    }

    /// <summary>SHA256 over the DER bytes, as colon separated hex, the way a browser shows it.</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        var hex = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var parts = new List<string>(hex.Length / 2);
        for (var i = 0; i + 1 < hex.Length; i += 2) parts.Add(hex.Substring(i, 2));
        return string.Join(":", parts);
    }
}
