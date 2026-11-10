using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace TD.Core;

/// <summary>
/// The passwords as Django keeps them in core_user.password: "argon2$argon2id$v=19$m=102400,t=2,p=8$salt$hash" (the one
/// it writes) and "pbkdf2_sha256$iterations$salt$hash" (accepted, older). New passwords are written as argon2 with
/// Django 5.2's parameters, so the old program still opens them.
/// </summary>
public static class Passwords
{
    const int TimeCost = 2, MemoryCost = 102400, Parallelism = 8, HashLen = 32;
    const string SaltChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static bool Usable(string encoded) => !string.IsNullOrEmpty(encoded) && !encoded.StartsWith('!');

    public static bool Verify(string password, string encoded)
    {
        if (!Usable(encoded) || password == null) return false;
        try
        {
            if (encoded.StartsWith("argon2$")) return VerifyArgon2(password, encoded[7..]);
            if (encoded.StartsWith("pbkdf2_sha256$"))
            {
                var p = encoded.Split('$');
                var hash = Pbkdf2(password, p[2], int.Parse(p[1]), Convert.FromBase64String(p[3]).Length);
                return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(p[3]));
            }
        }
        catch (FormatException) { }
        catch (IndexOutOfRangeException) { }
        return false;
    }

    /// <summary>True when the stored hash should be written again with today's parameters (after a good login).</summary>
    public static bool MustUpdate(string encoded) => !encoded.StartsWith($"argon2$argon2id$v=19$m={MemoryCost},t={TimeCost},p={Parallelism}$");

    public static string Make(string password)
    {
        var salt = new StringBuilder();
        foreach (var b in RandomNumberGenerator.GetBytes(22)) salt.Append(SaltChars[b % SaltChars.Length]);
        var saltBytes = Encoding.UTF8.GetBytes(salt.ToString());
        var hash = Argon2(password, saltBytes, TimeCost, MemoryCost, Parallelism, HashLen);
        return $"argon2$argon2id$v=19$m={MemoryCost},t={TimeCost},p={Parallelism}${B64NoPad(saltBytes)}${B64NoPad(hash)}";
    }

    /// <summary>A password nobody can type (Django's "!" + 40 random characters): the account enters another way.</summary>
    public static string Unusable()
    {
        var s = new StringBuilder("!");
        foreach (var b in RandomNumberGenerator.GetBytes(40)) s.Append(SaltChars[b % SaltChars.Length]);
        return s.ToString();
    }

    static bool VerifyArgon2(string password, string rest)
    {
        // rest: argon2id$v=19$m=102400,t=2,p=8$salt$hash
        var parts = rest.Split('$');
        if (parts.Length < 5 || parts[0] != "argon2id" && parts[0] != "argon2i") return false;
        int m = 0, t = 0, p = 0;
        foreach (var kv in parts[2].Split(','))
        {
            var x = kv.Split('=');
            if (x[0] == "m") m = int.Parse(x[1]); else if (x[0] == "t") t = int.Parse(x[1]); else if (x[0] == "p") p = int.Parse(x[1]);
        }
        var salt = UnB64NoPad(parts[3]);
        var want = UnB64NoPad(parts[4]);
        byte[] got;
        if (parts[0] == "argon2id") got = Argon2(password, salt, t, m, p, want.Length);
        else
        {
            using var a = new Argon2i(Encoding.UTF8.GetBytes(password)) { Salt = salt, Iterations = t, MemorySize = m, DegreeOfParallelism = p };
            got = a.GetBytes(want.Length);
        }
        return CryptographicOperations.FixedTimeEquals(got, want);
    }

    static byte[] Argon2(string password, byte[] salt, int t, int m, int p, int len)
    {
        using var a = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, Iterations = t, MemorySize = m, DegreeOfParallelism = p };
        return a.GetBytes(len);
    }

    static byte[] Pbkdf2(string password, string salt, int iterations, int len) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), iterations, HashAlgorithmName.SHA256, len);

    static string B64NoPad(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');
    static byte[] UnB64NoPad(string s) { s = s.Replace('-', '+').Replace('_', '/'); while (s.Length % 4 != 0) s += "="; return Convert.FromBase64String(s); }
}
