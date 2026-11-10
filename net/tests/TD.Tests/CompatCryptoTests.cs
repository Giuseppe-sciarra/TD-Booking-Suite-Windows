using System.Text.Json.Nodes;
using TD.Core;
using Xunit;

namespace TD.Tests;

/// <summary>What the Python program sealed opens here, and what we seal opens there (tools/check_python.py).</summary>
public class CompatCryptoTests
{
    static readonly JsonObject F = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "python.json")))!.AsObject();
    static Crypto C => new(F["keys"]!.ToJsonString(), (string)F["active"]);
    static Guid T => Guid.Parse((string)F["tenant"]);

    [Fact]
    public void Opens_what_python_sealed()
    {
        var data = C.Decrypt(T, (string)F["wrapped"], (string)F["sealed"], "contact:abc");
        Assert.True(JsonNode.DeepEquals(F["plain"], data));
        Assert.Equal("Màrio", (string)data["first_name"]);
    }

    [Fact]
    public void Wrong_label_or_tenant_does_not_open()
    {
        Assert.ThrowsAny<Exception>(() => C.Decrypt(T, (string)F["wrapped"], (string)F["sealed"], "contact:abd"));
        Assert.ThrowsAny<Exception>(() => C.Decrypt(Guid.NewGuid(), (string)F["wrapped"], (string)F["sealed"], "contact:abc"));
    }

    [Fact]
    public void Bytes_and_indexes_match()
    {
        var raw = Crypto.UnB64((string)F["bytes"]);
        Assert.Equal(new byte[] { 0xff, 0xd8, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, C.DecryptBytes(T, (string)F["wrapped"], raw, "photo:1"));
        Assert.Equal((string)F["login_index"], C.LoginIndex("  Mario.Rossi@Example.COM "));
        Assert.Equal((string)F["index_value"], Crypto.Index("x", "iiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii"u8.ToArray(), "phone"));
        Assert.Equal((string)F["token_hash"], Crypto.TokenHash("abc"));
    }

    [Fact]
    public void Round_trip_here()
    {
        var c = C;
        var node = JsonNode.Parse("{\"a\":\"è\",\"b\":[1,2]}");
        var s = c.Encrypt(T, (string)F["wrapped"], node, "x:1");
        Assert.True(JsonNode.DeepEquals(node, c.Decrypt(T, (string)F["wrapped"], s, "x:1")));
        var nk = c.NewTenantKey(T);
        Assert.Equal(32, c.TenantKey(T, nk).Length);
    }

    [Fact]
    public void Passwords_from_django()
    {
        Assert.True(Passwords.Verify("Password-di-prova-1", (string)F["argon2"]));
        Assert.False(Passwords.Verify("password-di-prova-1", (string)F["argon2"]));
        Assert.True(Passwords.Verify("Password-di-prova-1", (string)F["pbkdf2"]));
        Assert.True(Passwords.MustUpdate((string)F["pbkdf2"]));
        Assert.False(Passwords.MustUpdate((string)F["argon2"]));
        var mine = Passwords.Make("Nuova-password-2");
        Assert.True(Passwords.Verify("Nuova-password-2", mine));
        Assert.False(Passwords.Verify("x", Passwords.Unusable()));
    }
}

public class LicensingTests
{
    static System.Text.Json.Nodes.JsonObject F() => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "licence.json")));

    [Fact]
    public void Reads_licences_signed_by_TD_Licenze()
    {
        var f = F(); var keep = TD.Core.Win.Licensing.PublicKey;
        try
        {
            TD.Core.Win.Licensing.PublicKey = f["public"]!.ToString();
            var l = TD.Core.Win.Licensing.Parse(f["licence"]!.ToString());
            Assert.Equal("L-TEST-1", l.Id); Assert.Equal(3, l.Postazioni); Assert.Equal("server", l.Tipo); Assert.Equal(new DateOnly(2027, 12, 31), l.Scadenza);
            Assert.Equal("pc", TD.Core.Win.Licensing.Parse(f["expired"]!.ToString()).Tipo);
            var ex = Assert.Throws<ArgumentException>(() => TD.Core.Win.Licensing.Parse(f["tampered"]!.ToString()));
            Assert.Contains("firma", ex.Message);
        }
        finally { TD.Core.Win.Licensing.PublicKey = keep; }
    }

    [Fact]
    public void The_seller_key_is_built_in() => Assert.Equal(43, TD.Core.Win.Licensing.PublicKey.Length);

    [Fact]
    public void Authenticator_codes_match_pyotp()
    {
        // pyotp.TOTP("JBSWY3DPEHPK3PXP").at(59) and RFC 6238's SHA1 vector truncated to 6 digits
        Assert.Equal(TD.Core.Identity.Totp("JBSWY3DPEHPK3PXP", 59 / 30), TD.Core.Identity.Totp("JBSWY3DPEHPK3PXP", 1));
        Assert.Equal("287082", TD.Core.Identity.Totp("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", 1));
    }
}
