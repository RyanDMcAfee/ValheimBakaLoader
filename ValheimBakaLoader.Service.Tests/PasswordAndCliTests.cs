using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Service.Auth;
using ValheimBakaLoader.Service.Cli;

namespace ValheimBakaLoader.Service.Tests;

public class PasswordAndCliTests
{
    [Fact]
    public async Task Passwd_round_trips_through_service_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bakaloader-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await ServiceCli.RunAsync(
                new[] { "passwd", "--data-dir", dir },
                name => name == ServiceCli.PasswordVariable ? "swordfish and chips" : null,
                new StringReader(""),
                stdout,
                stderr,
                _ => throw new InvalidOperationException("passwd must not start the server"));

            Assert.Equal(0, code);
            Assert.Contains("Password set in", stdout.ToString());

            var file = JObject.Parse(File.ReadAllText(Path.Combine(dir, "service.json")));
            var entry = (JObject)file["password"]!;
            Assert.Equal(PasswordStore.Algorithm, entry.Value<string>("algorithm"));
            Assert.Equal(PasswordStore.Iterations, entry.Value<int>("iterations"));
            Assert.Equal(PasswordStore.SaltBytes, Convert.FromBase64String(entry.Value<string>("salt")!).Length);
            Assert.Equal(PasswordStore.HashBytes, Convert.FromBase64String(entry.Value<string>("hash")!).Length);

            var store = new PasswordStore(Path.Combine(dir, "service.json"));
            Assert.True(store.HasPassword);
            Assert.True(store.Verify("swordfish and chips"));
            Assert.False(store.Verify("swordfish and chip"));
            Assert.False(store.Verify(null));

            // A second passwd replaces the first, with a fresh salt.
            var firstSalt = entry.Value<string>("salt");
            store.SetPassword("another password");
            Assert.False(store.Verify("swordfish and chips"));
            Assert.True(store.Verify("another password"));
            var second = JObject.Parse(File.ReadAllText(store.Path));
            Assert.NotEqual(firstSalt, second["password"]!.Value<string>("salt"));

            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Passwd_from_the_prompt_needs_the_two_lines_to_match()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bakaloader-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var stderr = new StringWriter();
            var code = await ServiceCli.RunAsync(
                new[] { "passwd", "--data-dir=" + dir },
                _ => null,
                new StringReader("one password\nanother password\n"),
                new StringWriter(),
                stderr,
                _ => throw new InvalidOperationException());
            Assert.Equal(1, code);
            Assert.Contains("did not match", stderr.ToString());
            Assert.False(File.Exists(Path.Combine(dir, "service.json")));

            code = await ServiceCli.RunAsync(
                new[] { "passwd", "--data-dir=" + dir },
                _ => null,
                new StringReader("same password\nsame password\n"),
                new StringWriter(),
                stderr,
                _ => throw new InvalidOperationException());
            Assert.Equal(0, code);
            Assert.True(new PasswordStore(Path.Combine(dir, "service.json")).Verify("same password"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task The_command_line_reads_its_flags_and_the_environment()
    {
        ServiceOptions? seen = null;
        var code = await ServiceCli.RunAsync(
            new[] { "--bind", "0.0.0.0:9000", "--data-dir", "/tmp/x", "--no-tls", "--lang=ru" },
            name => name == ServiceOptions.DataDirVariable ? "/var/lib/elsewhere" : null,
            new StringReader(""), new StringWriter(), new StringWriter(),
            o => { seen = o; return Task.FromResult(0); });

        Assert.Equal(0, code);
        Assert.NotNull(seen);
        Assert.Equal("0.0.0.0:9000", seen!.Bind);
        Assert.Equal("/tmp/x", seen.DataDir);
        Assert.Equal(TlsMode.Off, seen.Tls);
        Assert.Equal("ru", seen.Lang);

        // The environment fills what the flags leave alone.
        await ServiceCli.RunAsync(
            Array.Empty<string>(),
            name => name == ServiceOptions.DataDirVariable ? "/var/lib/elsewhere"
                  : name == ServiceOptions.BindVariable ? "127.0.0.1:1234" : null,
            new StringReader(""), new StringWriter(), new StringWriter(),
            o => { seen = o; return Task.FromResult(0); });
        Assert.Equal("/var/lib/elsewhere", seen!.DataDir);
        Assert.Equal("127.0.0.1:1234", seen.Bind);

        var stdout = new StringWriter();
        code = await ServiceCli.RunAsync(new[] { "--version" }, _ => null, new StringReader(""), stdout, new StringWriter(),
            _ => throw new InvalidOperationException());
        Assert.Equal(0, code);
        Assert.Matches(@"^\d+\.\d+\.\d+", stdout.ToString().Trim());

        var stderr = new StringWriter();
        code = await ServiceCli.RunAsync(new[] { "--what" }, _ => null, new StringReader(""), new StringWriter(), stderr,
            _ => throw new InvalidOperationException());
        Assert.Equal(2, code);
        Assert.Contains("Unknown argument", stderr.ToString());
    }
}
