using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TD.Core;

/// <summary>
/// Authenticated encryption, byte for byte the same as core/crypto.py of the Python program: AES-256-GCM with a 12-byte
/// nonce in front, URL-safe base64 with padding, the JSON compact and not ASCII-escaped. The master keys (TDBS_KEYS) wrap
/// one key per business (tenant.wrapped_key); every sealed field is bound to its tenant and its label (the AAD), so a
/// value moved to another row or business does not open.
/// </summary>
public sealed class Crypto
{
    readonly Dictionary<string, byte[]> keys;
    readonly string active;
    readonly ConcurrentDictionary<string, byte[]> tenantKeys = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // ensure_ascii=False
        WriteIndented = false,
    };

    public Crypto(string keysJson, string activeKey)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(keysJson) ? "{}" : keysJson) ?? new();
        keys = parsed.ToDictionary(kv => kv.Key, kv => UnB64(kv.Value));
        active = string.IsNullOrEmpty(activeKey) ? "v1" : activeKey;
        if (!keys.ContainsKey("index") || !keys.TryGetValue(active, out var k) || k.Length != 32)
            throw new InvalidOperationException("Chiavi di cifratura mancanti o non valide (TDBS_KEYS).");
    }

    public string ActiveKey => active;
    public IReadOnlyDictionary<string, byte[]> Keys => keys;

    // ---------- base64 as Python's urlsafe_b64encode / urlsafe_b64decode (padding kept, padding optional on input)
    public static string B64(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');
    public static byte[] UnB64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    public static string Dumps(JsonNode data) => data == null ? "null" : data.ToJsonString(Json);

    // ---------- the seal itself
    public static string Seal(JsonNode data, byte[] key, string aad) => SealText(Dumps(data), key, aad);
    public static string SealText(string json, byte[] key, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(json);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(aad));
        var all = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(all, 0); cipher.CopyTo(all, 12); tag.CopyTo(all, 12 + cipher.Length);
        return B64(all);
    }
    public static JsonNode Open(string value, byte[] key, string aad) => JsonNode.Parse(OpenText(value, key, aad));
    public static string OpenText(string value, byte[] key, string aad)
    {
        var raw = UnB64(value);
        if (raw.Length < 28) throw new CryptographicException("Valore cifrato troppo corto.");
        var cipher = raw.AsSpan(12, raw.Length - 28);
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(raw.AsSpan(0, 12), cipher, raw.AsSpan(raw.Length - 16), plain, Encoding.UTF8.GetBytes(aad));
        return Encoding.UTF8.GetString(plain);
    }

    // ---------- master keys: protect/reveal ("v1." + seal)
    public string Protect(JsonNode data, string label) => active + "." + Seal(data, keys[active], label);
    public JsonNode Reveal(string value, string label)
    {
        var dot = value.IndexOf('.');
        var version = value[..dot];
        if (!keys.TryGetValue(version, out var key)) throw new CryptographicException("Chiave " + version + " assente.");
        return Open(value[(dot + 1)..], key, label);
    }

    // ---------- the business's key
    public byte[] TenantKey(Guid tenant, string wrapped) =>
        tenantKeys.GetOrAdd(tenant + "|" + wrapped, _ => UnB64(Reveal(wrapped, "tenant-key:" + tenant).GetValue<string>()));
    /// <summary>A new key for a new business, wrapped as tenant.wrapped_key.</summary>
    public string NewTenantKey(Guid tenant) => Protect(JsonValue.Create(B64(RandomNumberGenerator.GetBytes(32))), "tenant-key:" + tenant);

    public string Encrypt(Guid tenant, string wrapped, JsonNode data, string label) => Seal(data, TenantKey(tenant, wrapped), tenant + ":" + label);
    public JsonNode Decrypt(Guid tenant, string wrapped, string data, string label) => Open(data, TenantKey(tenant, wrapped), tenant + ":" + label);

    public byte[] EncryptBytes(Guid tenant, string wrapped, byte[] data, string label)
    {
        var key = TenantKey(tenant, wrapped);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length]; var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(tenant + ":" + label));
        var all = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(all, 0); cipher.CopyTo(all, 12); tag.CopyTo(all, 12 + cipher.Length);
        return all;
    }
    public byte[] DecryptBytes(Guid tenant, string wrapped, byte[] raw, string label)
    {
        var key = TenantKey(tenant, wrapped);
        var cipher = raw.AsSpan(12, raw.Length - 28);
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(raw.AsSpan(0, 12), cipher, raw.AsSpan(raw.Length - 16), plain, Encoding.UTF8.GetBytes(tenant + ":" + label));
        return plain;
    }

    // ---------- blind indexes (search without decrypting)
    public static string Index(string value, byte[] key, string purpose)
    {
        var k = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt: null, info: Encoding.UTF8.GetBytes(purpose));
        return Convert.ToHexString(HMACSHA256.HashData(k, Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
    public string LoginIndex(string email) => Index(CaseFold(email.Trim()), keys["index"], "login");
    public static string TokenHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Python's str.casefold for what an email or a name holds.</summary>
    public static string CaseFold(string s) => s.ToLowerInvariant().Replace("ß", "ss").Replace("ς", "σ");
}
