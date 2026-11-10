using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TD.Core;

/// <summary>WebAuthn as the Python program checks it (py_webauthn in identity.py): the credential a browser made is
/// verified here, and kept in the same form (id and COSE public key in base64url), so a passkey works in both.
/// The attestation statement is not checked: the program trusts no maker's certificate, as with "none".</summary>
public static class Passkeys
{
    public sealed class Refused(string why) : Exception(why);
    public sealed record Registered(byte[] CredentialId, byte[] PublicKey, long SignCount);

    public static string B64(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_');  // crypto.b64: url-safe, with its padding
    public static byte[] UnB64(string s)
    {
        s = (s ?? "").TrimEnd('=').Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); } catch (FormatException) { throw new Refused("base64"); }
    }

    // ---------- CBOR, the little WebAuthn uses: maps, arrays, integers, byte and text strings
    static object Cbor(byte[] d, ref int i)
    {
        if (i >= d.Length) throw new Refused("cbor");
        int major = d[i] >> 5, info = d[i] & 31; i++;
        ulong n = info < 24 ? (ulong)info : 0;
        if (info is >= 24 and <= 27) { var len = 1 << (info - 24); if (i + len > d.Length) throw new Refused("cbor"); for (int k = 0; k < len; k++) n = n << 8 | d[i++]; }
        else if (info > 27) throw new Refused("cbor");
        switch (major)
        {
            case 0: return (long)n;
            case 1: return -1 - (long)n;
            case 2: case 3:
                {
                    if (n > (ulong)(d.Length - i)) throw new Refused("cbor");
                    var part = d.AsSpan(i, (int)n).ToArray(); i += (int)n; return major == 2 ? part : Encoding.UTF8.GetString(part);
                }
            case 4: { if (n > 1000) throw new Refused("cbor"); var list = new List<object>(); for (ulong k = 0; k < n; k++) list.Add(Cbor(d, ref i)); return list; }
            case 5: { if (n > 1000) throw new Refused("cbor"); var map = new Dictionary<object, object>(); for (ulong k = 0; k < n; k++) { var key = Cbor(d, ref i); map[key] = Cbor(d, ref i); } return map; }
            case 7: return info switch { 20 => false, 21 => true, _ => null };
            default: throw new Refused("cbor");
        }
    }

    /// <summary>What the browser signed over: the kind of ceremony, our challenge, the address of the page.</summary>
    static byte[] ClientData(JsonNode response, string type, byte[] challenge, string origin)
    {
        var raw = UnB64(response?["clientDataJSON"]?.ToString()); JsonObject data;
        try { data = JsonNode.Parse(raw) as JsonObject ?? throw new Refused("clientData"); } catch (System.Text.Json.JsonException) { throw new Refused("clientData"); }
        if (data["type"]?.ToString() != type) throw new Refused("type");
        if (!CryptographicOperations.FixedTimeEquals(UnB64(data["challenge"]?.ToString()), challenge)) throw new Refused("challenge");
        if (data["origin"]?.ToString() != origin) throw new Refused("origin");
        return raw;
    }
    /// <summary>The authenticator's own data: for which site, the person present and verified, the counter.</summary>
    static (long Count, int At) AuthData(byte[] a, string rpId, bool attested)
    {
        if (a.Length < 37) throw new Refused("authData");
        if (!CryptographicOperations.FixedTimeEquals(a.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(rpId)))) throw new Refused("rpId");
        var flags = a[32];
        if ((flags & 0x01) == 0 || (flags & 0x04) == 0) throw new Refused("userVerification");  // present, and verified (PIN, finger, face)
        if (attested && (flags & 0x40) == 0) throw new Refused("attestedData");
        return ((long)a[33] << 24 | (long)a[34] << 16 | (long)a[35] << 8 | a[36], 37);
    }

    /// <summary>verify_registration_response: a new passkey: its id and its public key (COSE, as it came).</summary>
    public static Registered Register(JsonNode credential, byte[] challenge, string rpId, string origin)
    {
        var response = credential?["response"]; ClientData(response, "webauthn.create", challenge, origin);
        var obj = UnB64(response?["attestationObject"]?.ToString()); int i = 0;
        if (Cbor(obj, ref i) is not Dictionary<object, object> att || att.GetValueOrDefault("authData") is not byte[] a) throw new Refused("attestation");
        var (count, at) = AuthData(a, rpId, true);
        if (a.Length < at + 18) throw new Refused("authData");
        int len = a[at + 16] << 8 | a[at + 17]; at += 18; if (len is 0 or > 1023 || a.Length < at + len) throw new Refused("credentialId");
        var id = a.AsSpan(at, len).ToArray(); at += len;
        if (!CryptographicOperations.FixedTimeEquals(id, UnB64(credential["rawId"]?.ToString() ?? credential["id"]?.ToString()))) throw new Refused("credentialId");
        int end = at; var key = Cbor(a, ref end) as Dictionary<object, object> ?? throw new Refused("publicKey");
        var cose = a.AsSpan(at, end - at).ToArray(); using var check = Key(cose);  // a key we can verify with, or refused now
        return new(id, cose, count);
    }

    /// <summary>verify_authentication_response: this passkey signed our challenge now; the new counter.</summary>
    public static long Assert(JsonNode credential, byte[] challenge, string rpId, string origin, byte[] publicKey, long signCount)
    {
        var response = credential?["response"]; var client = ClientData(response, "webauthn.get", challenge, origin);
        var a = UnB64(response?["authenticatorData"]?.ToString()); var (count, _) = AuthData(a, rpId, false);
        var signed = a.Concat(SHA256.HashData(client)).ToArray(); var signature = UnB64(response?["signature"]?.ToString());
        using var key = Key(publicKey);
        var ok = key switch
        {
            ECDsa ec => Try(() => ec.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
            RSA rsa => Try(() => rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            _ => false,
        };
        if (!ok) throw new Refused("signature");
        // a counter that goes back means a copied authenticator; many never count (always 0)
        if ((count > 0 || signCount > 0) && count <= signCount) throw new Refused("signCount");
        return count;
        static bool Try(Func<bool> f) { try { return f(); } catch (CryptographicException) { return false; } }
    }

    /// <summary>The COSE key as .NET's: ES256 (P-256) and RS256, what browsers and Windows Hello make.</summary>
    static AsymmetricAlgorithm Key(byte[] cose)
    {
        int i = 0; if (Cbor(cose, ref i) is not Dictionary<object, object> k) throw new Refused("publicKey");
        long N(long label) => k.GetValueOrDefault(label) is long v ? v : 0; byte[] B(long label) => k.GetValueOrDefault(label) as byte[] ?? throw new Refused("publicKey");
        try
        {
            if (N(1) == 2 && N(3) == -7 && N(-1) == 1) return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = B(-2), Y = B(-3) } });
            if (N(1) == 3 && N(3) == -257) return RSA.Create(new RSAParameters { Modulus = B(-1), Exponent = B(-2) });
        }
        catch (CryptographicException) { throw new Refused("publicKey"); }
        throw new Refused("algorithm");
    }
}
