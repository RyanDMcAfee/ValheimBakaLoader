using System.Security.Cryptography;
using System.Text;

namespace ValheimBakaLoader.Service.Auth;

/// <summary>
/// The way in on a box where no password has been set: a token printed once to the
/// journal, which only somebody who can read the journal has. The login page takes it
/// once, together with the password to set, and then it is gone.
/// </summary>
public sealed class SetupToken
{
    private readonly object _gate = new();
    private string? _token;

    /// <summary>Makes a fresh token, replacing any earlier one.</summary>
    public string Issue()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        lock (_gate) _token = token;
        return token;
    }

    public bool IsIssued
    {
        get { lock (_gate) return _token != null; }
    }

    /// <summary>True once, for the right token; afterwards nothing matches.</summary>
    public bool TryRedeem(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        lock (_gate)
        {
            if (_token == null) return false;
            var a = Encoding.UTF8.GetBytes(_token);
            var b = Encoding.UTF8.GetBytes(candidate.Trim().ToLowerInvariant());
            if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) return false;
            _token = null;
            return true;
        }
    }
}
