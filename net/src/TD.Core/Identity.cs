using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Core;

/// <summary>A signed-in window: who, from which device, since when (td_net_session, ours).</summary>
[Table("td_net_session")]
public sealed class NetSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Digest { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Seen { get; set; } = DateTime.UtcNow;
    public bool Remember { get; set; }
    public bool Mfa { get; set; }
    public int AuthVersion { get; set; }
    public string Device { get; set; } = "";
    public string Address { get; set; } = "";
}

/// <summary>core/identity.py: accounts, passwords, Authenticator codes, recovery codes, the login limits.</summary>
public sealed class Identity(Db db, Crypto crypto)
{
    static readonly string Dummy = Passwords.Make(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    public JsonObject Pii(User u) => crypto.Reveal(u.Pii, "user:" + u.Id) as JsonObject ?? new JsonObject();
    public string LoginName(User u) { var d = Pii(u); var n = d.Str("username"); return n != "" ? n : d.Str("email"); }
    public string Email(User u) => Pii(u).Str("email");

    /// <summary>The account an email or a username signs in to.</summary>
    public User ByLogin(Scope s, string value)
    {
        var key = crypto.LoginIndex(value ?? "");
        return s.One<User>("SELECT * FROM core_user WHERE login_index=@k OR name_index=@k LIMIT 1", new { k = key });
    }

    public User Authenticate(Scope s, string login, string password)
    {
        var u = ByLogin(s, login);
        if (u == null) { Passwords.Verify(password ?? "", Dummy); return null; }
        if (!u.IsActive || !Passwords.Verify(password ?? "", u.Password)) return null;
        if (Passwords.MustUpdate(u.Password)) { u.Password = Passwords.Make(password); s.Update(u, "password"); }
        return u;
    }

    public bool MfaReady(Scope s, User u) => !string.IsNullOrEmpty(u.Totp) || s.Scalar<long>("SELECT count(*) FROM core_passkey WHERE user_id=@u", new { u = u.Id }) > 0;

    /// <summary>RateBucket as throttle(): too many tries of one label for one identity in the window → refused.</summary>
    public void Throttle(string label, string identity, int limit = 30, int seconds = 900)
    {
        using var s = db.Global();
        var key = Crypto.TokenHash(label + ":" + identity);
        var now = DateTime.UtcNow;
        s.Execute("INSERT INTO core_ratebucket (key, count, ends) VALUES (@k, 0, @e) ON CONFLICT (key) DO NOTHING", new { k = key, e = now.AddSeconds(seconds) });
        var (count, ends) = s.One<(int, DateTime)>("SELECT count, ends FROM core_ratebucket WHERE key=@k FOR UPDATE", new { k = key });
        if (ends <= now) { count = 0; ends = now.AddSeconds(seconds); }
        if (count >= limit) throw new UnauthorizedAccessException("Troppi tentativi. Riprova più tardi.");
        s.Execute("UPDATE core_ratebucket SET count=@c, ends=@e WHERE key=@k", new { c = count + 1, e = ends, k = key });
        s.Commit();
    }

    // ---------- Authenticator (pyotp: SHA1, 6 digits, 30 seconds, base32 secret)
    public static string Totp(string base32, long step)
    {
        var key = Base32(base32);
        var msg = BitConverter.GetBytes(step); if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var h = HMACSHA1.HashData(key, msg);
        var o = h[^1] & 0xf;
        var bin = (h[o] & 0x7f) << 24 | h[o + 1] << 16 | h[o + 2] << 8 | h[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }
    public static byte[] Base32(string s)
    {
        s = s.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(); int buffer = 0, bits = 0;
        foreach (var ch in s)
        {
            var v = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(ch);
            if (v < 0) continue;
            buffer = buffer << 5 | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
    public static string NewBase32Secret()
    {
        var b = RandomNumberGenerator.GetBytes(20); var sb = new StringBuilder(); int buffer = 0, bits = 0;
        foreach (var x in b) { buffer = buffer << 8 | x; bits += 8; while (bits >= 5) { sb.Append("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"[(buffer >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) sb.Append("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>verify_totp: the code of now, of 30 s before or after, never one already used.</summary>
    public bool VerifyTotp(Scope s, User u, string code)
    {
        if (string.IsNullOrEmpty(u.Totp) || code.Any(ch => ch > 127)) return false;
        var secret = crypto.Reveal(u.Totp, "totp:" + u.Id)?.ToString() ?? "";
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        foreach (var cand in new[] { step, step - 1, step + 1 })
            if (cand > u.LastTotp && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Totp(secret, cand)), Encoding.ASCII.GetBytes(code)))
            {
                u.LastTotp = cand; s.Update(u, "last_totp"); return true;
            }
        return false;
    }

    /// <summary>A recovery code, used once.</summary>
    public bool UseRecovery(Scope s, User u, string code)
    {
        var list = Json.Strings(u.Recovery); var h = Crypto.TokenHash(code);
        if (!list.Remove(h)) return false;
        u.Recovery = Json.Dump(new JsonArray(list.Select(x => (JsonNode)x).ToArray())); s.Update(u, "recovery");
        return true;
    }

    public (string Secret, string Uri) NewTotp(User u, string issuer)
    {
        var secret = NewBase32Secret();
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(LoginName(u));
        return (secret, $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}");
    }
    public void SaveTotp(Scope s, User u, string secret)
    {
        u.Totp = crypto.Protect(JsonValue.Create(secret), "totp:" + u.Id); u.LastTotp = -1; s.Update(u, "totp", "last_totp");
    }
    public List<string> NewRecovery(Scope s, User u)
    {
        var codes = Enumerable.Range(0, 10).Select(_ => Bookings.TokenUrlSafe(16)).ToList();
        u.Recovery = Json.Dump(new JsonArray(codes.Select(c => (JsonNode)Crypto.TokenHash(c)).ToArray())); s.Update(u, "recovery");
        return codes;
    }

    // ---------- sessions of the windows
    public (NetSession, string Token) Open(Scope s, User u, bool remember, bool mfa, string device, string address)
    {
        var token = Bookings.TokenUrlSafe(32);
        var ns = s.Insert(new NetSession { Digest = Crypto.TokenHash(token), UserId = u.Id, Remember = remember, Mfa = mfa, AuthVersion = u.AuthVersion, Device = device ?? "", Address = address ?? "" });
        u.LastLogin = DateTime.UtcNow; s.Update(u, "last_login");
        return (ns, token);
    }

    /// <summary>The session of a token and its user, still valid: not ended, the password not changed since, not idle too long.</summary>
    public (NetSession, User) Resume(Scope s, string token, TimeSpan idle)
    {
        if (string.IsNullOrEmpty(token)) return (null, null);
        var ns = s.One<NetSession>("SELECT * FROM td_net_session WHERE digest=@d", new { d = Crypto.TokenHash(token) });
        if (ns == null) return (null, null);
        var u = s.Get<User>(ns.UserId);
        if (u == null || !u.IsActive || u.AuthVersion != ns.AuthVersion || !ns.Remember && DateTime.UtcNow - ns.Seen > idle)
        {
            s.Execute("DELETE FROM td_net_session WHERE id=@i", new { i = ns.Id }); return (null, null);
        }
        if (DateTime.UtcNow - ns.Seen > TimeSpan.FromMinutes(1)) s.Execute("UPDATE td_net_session SET seen=now() WHERE id=@i", new { i = ns.Id });
        return (ns, u);
    }
    public void Close(Scope s, string token) => s.Execute("DELETE FROM td_net_session WHERE digest=@d", new { d = Crypto.TokenHash(token ?? "") });
}
