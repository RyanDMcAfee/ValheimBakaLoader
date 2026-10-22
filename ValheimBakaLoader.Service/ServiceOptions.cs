using System.Reflection;

namespace ValheimBakaLoader.Service;

/// <summary>Whether a bind beyond loopback is wrapped in TLS, which it must be.</summary>
public enum TlsMode
{
    /// <summary>A given cert, or a self-signed one made once into the data folder.</summary>
    Auto,

    /// <summary>
    /// Plain HTTP. Allowed on loopback only, which is the SSH tunnel and reverse proxy
    /// case; asked for beyond loopback the service refuses to start.
    /// </summary>
    Off,
}

/// <summary>
/// Everything the service is told from outside: the command line, the environment and
/// the defaults. Nothing here is read from the page.
/// </summary>
public sealed class ServiceOptions
{
    public const string DefaultBind = "127.0.0.1:8471";
    public const string DataDirVariable = "BAKALOADER_DATA_DIR";
    public const string BindVariable = "BAKALOADER_BIND";

    /// <summary>host:port to listen on. Loopback by default: an SSH tunnel needs no certificate.</summary>
    public string Bind { get; set; } = DefaultBind;

    /// <summary>Where service.json, the TLS material, the cookie keys, the atlas and the packs live.</summary>
    public string DataDir { get; set; } = DefaultDataDir(Environment.GetEnvironmentVariable);

    /// <summary>The page's files, shipped beside the binary.</summary>
    public string WebUiDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "WebUI");

    /// <summary>Mapped to /atlas/. Null means a folder under the data dir.</summary>
    public string? AtlasDir { get; set; }

    /// <summary>Mapped to /lang/. Null means a folder under the data dir.</summary>
    public string? LangDir { get; set; }

    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }
    public TlsMode Tls { get; set; } = TlsMode.Auto;

    /// <summary>What boot.js hands the page as BAKA_VERSION.</summary>
    public string Version { get; set; } = AssemblyVersion();

    /// <summary>What boot.js hands the page as BAKA_LANG, and the language the login page speaks.</summary>
    public string Lang { get; set; } = "en";

    public TimeSpan SessionIdle { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(180);
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public int LoginFailuresAllowed { get; set; } = 5;
    public TimeSpan LoginLockout { get; set; } = TimeSpan.FromSeconds(60);

    public string ServiceFilePath => Path.Combine(DataDir, "service.json");
    public string EffectiveAtlasDir => AtlasDir ?? Path.Combine(DataDir, "atlas");
    public string EffectiveLangDir => LangDir ?? Path.Combine(DataDir, "lang");

    public static string DefaultDataDir(Func<string, string?> getEnv)
    {
        var fromEnv = getEnv(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        // ~/.local/share on Linux, which is where a per-user service keeps its state; the
        // system unit overrides it with /var/lib/bakaloader through the variable above.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(local, "bakaloader");
    }

    private static string AssemblyVersion()
    {
        var informational = typeof(ServiceOptions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";

        // The SDK appends "+<commit>" to the informational version; the page wants the
        // plain number it can compare with a catalog's appVersion.
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
