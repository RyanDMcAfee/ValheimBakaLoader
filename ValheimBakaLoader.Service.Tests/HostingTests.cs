using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimBakaLoader.Service.Hosting;
using ValheimBakaLoader.Service.Tls;
using ValheimBakaLoader.Service.Web;

namespace ValheimBakaLoader.Service.Tests;

public class HostingTests
{
    [Fact]
    public async Task Boot_js_carries_the_three_globals_and_is_never_cached()
    {
        await using var host = await new ServiceHost(tune: o => { o.Version = "1.2.3"; o.Lang = "ru"; }).StartAsync();
        using var client = host.Client();
        client.DefaultRequestHeaders.Add("Cookie", await host.SessionCookieAsync());

        using var response = await client.GetAsync("/boot.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/javascript", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        var js = await response.Content.ReadAsStringAsync();
        Assert.Equal("""window.BAKA_HOST="service";window.BAKA_VERSION="1.2.3";window.BAKA_LANG="ru";""", js);
    }

    [Fact]
    public void Boot_js_escapes_what_it_is_given()
    {
        var js = ServiceApp.BootJs(new ServiceOptions { Version = "1\"</script>", Lang = "en" });
        Assert.DoesNotContain("</script>", js);
        Assert.Contains("\\\"", js);
    }

    [Fact]
    public async Task The_atlas_and_lang_folders_are_served_under_their_prefixes()
    {
        await using var host = await new ServiceHost().StartAsync();
        File.WriteAllText(Path.Combine(host.Options.EffectiveAtlasDir, "world.png"), "not really a png");
        Directory.CreateDirectory(Path.Combine(host.Options.EffectiveLangDir, "ru", "1.0.0"));
        File.WriteAllText(Path.Combine(host.Options.EffectiveLangDir, "ru", "1.0.0", "strings.json"), "{}");

        using var client = host.Client();
        client.DefaultRequestHeaders.Add("Cookie", await host.SessionCookieAsync());

        using var atlas = await client.GetAsync("/atlas/world.png");
        Assert.Equal(HttpStatusCode.OK, atlas.StatusCode);
        Assert.Equal("not really a png", await atlas.Content.ReadAsStringAsync());

        using var pack = await client.GetAsync("/lang/ru/1.0.0/strings.json");
        Assert.Equal(HttpStatusCode.OK, pack.StatusCode);

        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("no-cache", root.Headers.CacheControl?.ToString());
    }

    [Fact]
    public void The_self_signed_certificate_is_made_once_and_serves_tls()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bakaloader-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var first = SelfSignedCertificate.EnsureInFolder(folder, new[] { "localhost", "127.0.0.1" });
            using var again = SelfSignedCertificate.EnsureInFolder(folder, new[] { "localhost" });

            Assert.True(first.HasPrivateKey);
            Assert.Equal(first.Thumbprint, again.Thumbprint);
            Assert.True(first.NotAfter > DateTime.UtcNow.AddDays(365));
            Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", SelfSignedCertificate.Fingerprint(first));
            Assert.True(File.Exists(Path.Combine(folder, SelfSignedCertificate.CertFileName)));
            Assert.True(File.Exists(Path.Combine(folder, SelfSignedCertificate.KeyFileName)));

            ServeOverTls(first).GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static async Task ServeOverTls(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/", () => "hello over tls");
        await app.StartAsync();

        var address = app.Urls.First();
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, served, _, _) =>
                served != null && served.Thumbprint == certificate.Thumbprint,
        };
        using var client = new HttpClient(handler);
        Assert.Equal("hello over tls", await client.GetStringAsync(address + "/"));
        await app.StopAsync();
    }

    [Fact]
    public void A_bind_beyond_loopback_is_refused_without_tls_and_gets_a_certificate_otherwise()
    {
        var loopback = ListenPlan.Resolve(new ServiceOptions { Bind = "127.0.0.1:8471", Tls = TlsMode.Off });
        Assert.False(loopback.UseTls);

        var bare = new ServiceOptions { Bind = "0.0.0.0:8471", Tls = TlsMode.Off, DataDir = "/tmp/never" };
        var ex = Assert.Throws<InvalidOperationException>(() => ListenPlan.Resolve(bare));
        Assert.Contains("without TLS", ex.Message);

        var auto = ListenPlan.Resolve(new ServiceOptions { Bind = "0.0.0.0:8471", DataDir = "/tmp/never" });
        Assert.True(auto.UseTls);
        Assert.Equal(CertificateSource.SelfSigned, auto.CertificateSource);
        Assert.Equal("/tmp/never/tls", auto.CertificateFolder);

        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 9000), ListenPlan.ParseBind("9000"));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 9001), ListenPlan.ParseBind("localhost:9001"));
        Assert.Equal(new IPEndPoint(IPAddress.IPv6Any, 8471), ListenPlan.ParseBind("[::]:8471"));
        Assert.Throws<ArgumentException>(() => ListenPlan.ParseBind("nowhere"));
    }

    [Fact]
    public async Task The_shutdown_coordinator_waits_for_the_bridge_but_not_past_the_deadline()
    {
        var quick = new FakeBridge();
        Assert.True(await ShutdownCoordinator.StopAsync(quick, TimeSpan.FromSeconds(5), NullLogger.Instance));
        Assert.True(quick.ShutDownCalled);

        var stuck = new FakeBridge { HangOnShutdown = true };
        var started = DateTime.UtcNow;
        Assert.False(await ShutdownCoordinator.StopAsync(stuck, TimeSpan.FromMilliseconds(300), NullLogger.Instance));
        Assert.True(stuck.ShutDownCalled);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
