using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Service.Auth;
using ValheimBakaLoader.Service.Web;

namespace ValheimBakaLoader.Service.Tests;

/// <summary>
/// One service in memory per test: its own data folder, its own fake bridge, the real
/// WebUI folder out of the tree, and helpers for the cookie dance the tests all need.
/// </summary>
public sealed class ServiceHost : IAsyncDisposable
{
    public const string Password = "correct horse battery";

    public ServiceHost(bool withPassword = true, Action<ServiceOptions>? tune = null)
    {
        DataDir = Path.Combine(Path.GetTempPath(), "bakaloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDir);

        Options = new ServiceOptions
        {
            DataDir = DataDir,
            WebUiDir = RepoWebUi(),
            Version = "9.9.9",
            Lang = "en",
        };
        tune?.Invoke(Options);

        if (withPassword) new PasswordStore(Options.ServiceFilePath).SetPassword(Password);

        Bridge = new FakeBridge();
        App = ServiceApp.Build(Options, Bridge, b => b.WebHost.UseTestServer());
    }

    public string DataDir { get; }
    public ServiceOptions Options { get; }
    public FakeBridge Bridge { get; }
    public WebApplication App { get; }
    public TestServer Server => App.GetTestServer();

    public async Task<ServiceHost> StartAsync()
    {
        await App.StartAsync();
        return this;
    }

    /// <summary>A client on http://localhost or https://localhost that follows nothing and keeps no cookies.</summary>
    public HttpClient Client(bool https = false)
    {
        var client = new HttpClient(Server.CreateHandler())
        {
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"),
        };
        return client;
    }

    /// <summary>Signs in and answers the Set-Cookie line, which carries the flags under test.</summary>
    public async Task<string> LoginSetCookieAsync(bool https = false, string? password = null)
    {
        using var client = Client(https);
        using var response = await client.PostAsync("/login", Form(("password", password ?? Password)));
        Assert.Equal(System.Net.HttpStatusCode.SeeOther, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies), "no Set-Cookie on a good login");
        return cookies!.First(c => c.StartsWith(ServiceApp.CookieName + "=", StringComparison.Ordinal));
    }

    /// <summary>The Cookie header to send back, cut out of a Set-Cookie line.</summary>
    public static string CookieHeader(string setCookie) => setCookie.Split(';')[0];

    public async Task<string> SessionCookieAsync() => CookieHeader(await LoginSetCookieAsync());

    public async Task<WebSocket> OpenSocketAsync(string cookie, string origin = "http://localhost")
    {
        var client = Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = origin;
        };
        return await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
    }

    public static FormUrlEncodedContent Form(params (string Name, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));

    public static async Task SendAsync(WebSocket socket, string json)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public static async Task<JObject> ReceiveAsync(WebSocket socket, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var text = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer, cts.Token);
            if (frame.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("the socket closed while a message was expected");
            text.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage) break;
        }
        return JObject.Parse(Encoding.UTF8.GetString(text.ToArray()));
    }

    /// <summary>The tree's own WebUI, found by walking up from the test binary.</summary>
    public static string RepoWebUi()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "ValheimBakaLoader", "WebUI");
            if (File.Exists(Path.Combine(candidate, "i18n", "en.json"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("ValheimBakaLoader/WebUI was not found above the test binary");
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync(); } catch { }
        try { await App.DisposeAsync(); } catch { }
        try { Directory.Delete(DataDir, recursive: true); } catch { }
    }
}
