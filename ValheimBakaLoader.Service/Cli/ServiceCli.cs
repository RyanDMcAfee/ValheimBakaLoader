using ValheimBakaLoader.Service.Auth;

namespace ValheimBakaLoader.Service.Cli;

/// <summary>
/// The command line: <c>run</c> (the default), <c>passwd</c>, <c>--version</c>, and the
/// options every verb shares. Kept apart from the entry point so a test can drive it with
/// its own environment and streams.
/// </summary>
public static class ServiceCli
{
    public const string PasswordVariable = "BAKALOADER_PASSWORD";

    public const string Usage =
        "Usage: bakaloader [run|passwd|version|help] [--bind HOST:PORT] [--data-dir DIR] [--cert FILE --key FILE] [--no-tls] [--lang CODE]\n" +
        "\n" +
        "  run        serve the page (the default)\n" +
        "  passwd     set the admin password, from " + PasswordVariable + " or a prompt\n" +
        "  --version  print the version and exit\n" +
        "\n" +
        "The data folder also comes from " + ServiceOptions.DataDirVariable + ", the bind from " + ServiceOptions.BindVariable + ".\n";

    public static async Task<int> RunAsync(
        string[] args,
        Func<string, string?> getEnv,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<ServiceOptions, Task<int>> runServer)
    {
        string verb = "run";
        ServiceOptions options;
        try
        {
            options = Parse(args, getEnv, out verb);
        }
        catch (ArgumentException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            await stderr.WriteAsync(Usage);
            return 2;
        }

        switch (verb)
        {
            case "help":
                await stdout.WriteAsync(Usage);
                return 0;

            case "version":
                await stdout.WriteLineAsync(options.Version);
                return 0;

            case "passwd":
                return await PasswdAsync(options, getEnv, stdin, stdout, stderr);

            case "run":
                return await runServer(options);

            default:
                await stderr.WriteLineAsync($"Unknown command: {verb}");
                await stderr.WriteAsync(Usage);
                return 2;
        }
    }

    public static ServiceOptions Parse(string[] args, Func<string, string?> getEnv, out string verb)
    {
        verb = "run";
        var options = new ServiceOptions { DataDir = ServiceOptions.DefaultDataDir(getEnv) };

        var bindFromEnv = getEnv(ServiceOptions.BindVariable);
        if (!string.IsNullOrWhiteSpace(bindFromEnv)) options.Bind = bindFromEnv;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? inline = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                inline = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            string Value()
            {
                if (inline != null) return inline;
                if (i + 1 >= args.Length) throw new ArgumentException($"{arg} needs a value.");
                return args[++i];
            }

            switch (arg)
            {
                case "--bind": options.Bind = Value(); break;
                case "--data-dir": options.DataDir = Value(); break;
                case "--cert": options.CertPath = Value(); break;
                case "--key": options.KeyPath = Value(); break;
                case "--lang": options.Lang = Value(); break;
                case "--webui": options.WebUiDir = Value(); break;
                case "--no-tls": options.Tls = TlsMode.Off; break;
                case "--version": verb = "version"; break;
                case "--help":
                case "-h": verb = "help"; break;
                case "run":
                case "passwd":
                case "version":
                case "help": verb = arg; break;
                default: throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return options;
    }

    private static async Task<int> PasswdAsync(
        ServiceOptions options, Func<string, string?> getEnv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var password = getEnv(PasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            await stdout.WriteAsync("New password: ");
            await stdout.FlushAsync();
            password = await ReadSecretAsync(stdin);
            await stdout.WriteAsync("Again: ");
            await stdout.FlushAsync();
            var again = await ReadSecretAsync(stdin);
            if (password != again)
            {
                await stderr.WriteLineAsync("The two did not match; nothing changed.");
                return 1;
            }
        }

        if (password == null || password.Length < PasswordStore.MinimumLength)
        {
            await stderr.WriteLineAsync($"A password needs at least {PasswordStore.MinimumLength} characters; nothing changed.");
            return 1;
        }

        var store = new PasswordStore(options.ServiceFilePath);
        store.SetPassword(password);
        await stdout.WriteLineAsync($"Password set in {store.Path}");
        return 0;
    }

    /// <summary>
    /// A line without echo when a terminal is attached, a plain line otherwise, so a script
    /// can pipe one in. The Console echo switch is the only reason this touches Console.
    /// </summary>
    private static async Task<string> ReadSecretAsync(TextReader stdin)
    {
        if (!ReferenceEquals(stdin, Console.In) || Console.IsInputRedirected)
            return (await stdin.ReadLineAsync())?.TrimEnd('\r') ?? "";

        var chars = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Length > 0) chars.Length--;
                continue;
            }
            if (key.KeyChar != '\0') chars.Append(key.KeyChar);
        }
        Console.WriteLine();
        return chars.ToString();
    }
}
