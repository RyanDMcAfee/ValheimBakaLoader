using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ValheimBakaLoader.Service.Auth;

/// <summary>
/// The one admin password, kept as a PBKDF2-SHA256 hash in service.json under the data
/// folder. The file is read on every question rather than cached, so a password set by
/// <c>bakaloader passwd</c> while the service runs is honoured at the next login without a
/// restart; it is a few hundred bytes and a login is rare.
/// </summary>
public sealed class PasswordStore
{
    public const string Algorithm = "pbkdf2-sha256";
    public const int Iterations = 600_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int MinimumLength = 8;

    private readonly string _path;
    private readonly object _gate = new();

    public PasswordStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public bool HasPassword => Read()["password"] is JObject;

    /// <summary>
    /// A short value that changes whenever the password does, carried in the cookie so a
    /// session issued under the old password stops working the moment a new one is set.
    /// </summary>
    public string? Stamp
    {
        get
        {
            var hash = (Read()["password"] as JObject)?.Value<string>("hash");
            return hash == null ? null : hash[..Math.Min(16, hash.Length)];
        }
    }

    public void SetPassword(string password)
    {
        if (password == null || password.Length < MinimumLength)
            throw new ArgumentException($"A password needs at least {MinimumLength} characters.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);

        lock (_gate)
        {
            var file = Read();
            file["password"] = new JObject
            {
                ["algorithm"] = Algorithm,
                ["iterations"] = Iterations,
                ["salt"] = Convert.ToBase64String(salt),
                ["hash"] = Convert.ToBase64String(hash),
            };
            Write(file);
        }
    }

    public bool Verify(string? password)
    {
        if (password == null) return false;
        if (Read()["password"] is not JObject entry) return false;

        try
        {
            if (entry.Value<string>("algorithm") != Algorithm) return false;
            var iterations = entry.Value<int?>("iterations") ?? 0;
            var salt = Convert.FromBase64String(entry.Value<string>("salt") ?? "");
            var expected = Convert.FromBase64String(entry.Value<string>("hash") ?? "");
            if (iterations <= 0 || salt.Length == 0 || expected.Length == 0) return false;

            var actual = Derive(password, salt, iterations, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length);

    private JObject Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new JObject();
            try
            {
                return JObject.Parse(File.ReadAllText(_path, Encoding.UTF8));
            }
            catch (JsonException)
            {
                // A damaged file means no password, which locks everyone out until passwd is
                // run again; that is safer than any guess at what it used to say.
                return new JObject();
            }
        }
    }

    private void Write(JObject file)
    {
        var folder = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // Written beside and moved over, so a crash mid-write leaves the old file whole.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, file.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
        RestrictToOwner(temp);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>The hash is only as secret as the file: owner read and write, nobody else.</summary>
    internal static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* a filesystem without modes still holds the file */ }
    }
}
