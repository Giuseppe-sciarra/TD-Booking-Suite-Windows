using System.Text.Json.Nodes;
using TD.Core;
using Xunit;

namespace TD.Tests;

/// <summary>Passkeys checked as py_webauthn checks them (fixtures/passkeys.json: a credential and a signature it
/// accepted, with what the Python program would store).</summary>
public class PasskeyTests
{
    static readonly JsonNode F = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "passkeys.json")))!;
    static string Rp => (string)F["rp"]; static string Origin => (string)F["origin"];

    [Fact]
    public void Registers_and_signs_in_as_python()
    {
        foreach (var k in F["keys"]!.AsArray())
        {
            var made = Passkeys.Register(k!["register"], Passkeys.UnB64((string)k["challenge1"]), Rp, Origin);
            Assert.Equal((string)k["stored_id"], Passkeys.B64(made.CredentialId));
            Assert.Equal((string)k["stored_key"], Passkeys.B64(made.PublicKey));
            Assert.Equal((long)k["count"], Passkeys.Assert(k["get"], Passkeys.UnB64((string)k["challenge2"]), Rp, Origin, Passkeys.UnB64((string)k["stored_key"]), 0));
        }
    }

    [Fact]
    public void Refuses_what_is_not_ours()
    {
        var k = F["keys"]![0]!; var key = Passkeys.UnB64((string)k["stored_key"]); var challenge = Passkeys.UnB64((string)k["challenge2"]);
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Assert(k["get"], new byte[32], Rp, Origin, key, 0));                       // another challenge
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Assert(k["get"], challenge, Rp, "https://altro.example", key, 0));         // another site's page
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Assert(k["get"], challenge, "altro.example", Origin, key, 0));             // made for another site
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Assert(k["get"], challenge, Rp, Origin, key, 7));                          // the counter did not move on
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Assert(k["get"], challenge, Rp, Origin, Passkeys.UnB64((string)F["keys"]![1]!["stored_key"]), 0));  // another key
        var forged = k["get"]!.DeepClone(); var sig = Passkeys.UnB64((string)forged["response"]!["signature"]); sig[^1] ^= 1; forged["response"]!["signature"] = Passkeys.B64(sig);
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Assert(forged, challenge, Rp, Origin, key, 0));
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Register(k["register"], new byte[32], Rp, Origin));
        Assert.Throws<Passkeys.Refused>(() => Passkeys.Register(k["get"], Passkeys.UnB64((string)k["challenge1"]), Rp, Origin));  // a sign-in is not a new passkey
    }
}
