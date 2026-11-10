using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Web;

/// <summary>identity.passkey_begin and passkey_finish for the program's window. A window cannot ask Windows for a
/// passkey as a web page does, so it opens a page of this server in the browser with a ticket: the page makes or uses
/// the passkey (the same WebAuthn, the same stored form as the Python program), and the window asks how it went.</summary>
public static partial class Auth
{
    sealed class Ask
    {
        public string Kind; public Guid? User; public int Version; public byte[] Challenge; public DateTime Until; public bool Remember; public string Device = "", Name = "Passkey";
        public Guid? Session; public bool Begun, Used; public string State = "attesa", Token = "";
    }
    static readonly ConcurrentDictionary<string, Ask> asks = new();
    const string REFUSED = "Passkey non valida o richiesta scaduta.";
    static string PageOrigin(HttpContext h) => $"{h.Request.Scheme}://{h.Request.Host.Value}";
    static string Unpadded(string b64) => b64.TrimEnd('=');

    /// <summary>A new ticket, valid five minutes: the address of the page to open in the browser, and its name.</summary>
    static (string Ticket, string Url) Ticket(HttpContext h, Ask ask)
    {
        foreach (var old in asks.Where(x => x.Value.Until < DateTime.UtcNow.AddMinutes(-10)).Select(x => x.Key).ToList()) asks.TryRemove(old, out _);
        var ticket = Bookings.TokenUrlSafe(32); ask.Until = DateTime.UtcNow.AddMinutes(5); ask.Challenge = RandomNumberGenerator.GetBytes(64); asks[ticket] = ask;
        return (ticket, $"{PageOrigin(h)}/passkey/{ticket}/");
    }
    /// <summary>Aggiungi una passkey, from Accesso e sicurezza (Rpc/Account.cs): who is signed in, just confirmed.</summary>
    public static string RegisterTicket(Server s, HttpContext h, User u, Guid session, string name)
    {
        if (!MfaEnabled(s)) throw new UnauthorizedAccessException("Il secondo fattore non è attivo in questa installazione.");
        name = (name ?? "").Trim(); if (name.Length > 80) name = name[..80];
        return Ticket(h, new Ask { Kind = "register", User = u.Id, Version = u.AuthVersion, Session = session, Name = name != "" ? name : "Passkey" }).Url;
    }

    static void MapPasskeys(WebApplication app)
    {
        // the window asks for a ticket: to enter with the passkey alone (as the web sign-in), as the second factor
        // after the password, or to confirm who is signed in
        app.MapPost("/api/passkey/avvia", (Server s, HttpContext h, JsonObject body) =>
        {
            if (!MfaEnabled(s)) return Results.Json(new { error = "Il secondo fattore non è attivo in questa installazione." }, statusCode: 403);
            try { s.Identity.Throttle("passkey", Address(h), 30); } catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
            using var g = s.Db.Global(); var ask = new Ask { Kind = "login", Remember = Json.Truthy(body["remember"]), Device = body.Str("device") };
            switch (body.Str("tipo"))
            {
                case "login":
                    {
                        var login = body.Str("login").Trim(); if (login == "") return Results.Json(new { error = "Inserisci prima il tuo username o la tua email." }, statusCode: 400);
                        // an unknown name gets a ticket too, and fails at the end like a wrong passkey: nobody learns who has an account
                        var u = s.Identity.ByLogin(g, login.Length > 254 ? login[..254] : login); if (u is { IsActive: true }) { ask.User = u.Id; ask.Version = u.AuthVersion; }
                        break;
                    }
                case "mfa":
                    {
                        if (!pending.TryGetValue(body.Str("pre"), out var pre) || pre.Until < DateTime.UtcNow) return Results.Json(new { error = "Accesso scaduto: inserisci di nuovo la password." }, statusCode: 401);
                        ask.User = pre.User; ask.Version = pre.Version; ask.Remember = pre.Remember; ask.Device = pre.Device; break;
                    }
                case "confirm":
                    {
                        var (ns, u) = s.Identity.Resume(g, Bearer(h), Idle);
                        if (u == null) return Results.Json(new { error = "Sessione scaduta: entra di nuovo.", login = true }, statusCode: 401);
                        ask.Kind = "confirm"; ask.User = u.Id; ask.Version = u.AuthVersion; ask.Session = ns.Id; break;
                    }
                default: return Results.Json(new { error = "Operazione non valida." }, statusCode: 400);
            }
            var (ticket, url) = Ticket(h, ask);
            return Results.Json(new { ticket, apri = url });
        });

        // how it went, asked by the window every second or so: a sign-in gets its session here, once
        app.MapPost("/api/passkey/esito", (Server s, HttpContext h, JsonObject body) =>
        {
            var key = body.Str("ticket");
            if (!asks.TryGetValue(key, out var ask) || ask.Until < DateTime.UtcNow && ask.State == "attesa") { asks.TryRemove(key, out _); return Results.Json(new { stato = "errore", error = "Richiesta scaduta: riprova." }); }
            if (ask.State == "attesa") return Results.Json(new { stato = "attesa" });
            asks.TryRemove(key, out _);
            if (ask.State != "fatto") return Results.Json(new { stato = "errore", error = REFUSED });
            if (ask.Kind != "login") return Results.Json(new { stato = "fatto" });
            using var g = s.Db.Global();
            var u = g.One<User>("SELECT * FROM core_user WHERE id=@id AND is_active AND auth_version=@v", new { id = ask.User, v = ask.Version });
            if (u == null) return Results.Json(new { stato = "errore", error = REFUSED });
            var (_, token) = s.Identity.Open(g, u, ask.Remember, true, ask.Device, Address(h)); g.Commit();
            return Results.Json(new { stato = "fatto", token });
        });

        app.MapGet("/passkey/{ticket}/", (Server s, HttpContext h, string ticket) =>
        {
            if (!asks.TryGetValue(ticket, out var ask) || ask.Until < DateTime.UtcNow || ask.Used)
                return PublicHost.Page(s, h, "message.html", null, new() { ["title"] = "Collegamento scaduto", ["text"] = "Torna al programma e riprova: la pagina vale cinque minuti e una volta sola." }, 400);
            var who = ""; if (ask.User is Guid id) { using var g = s.Db.Global(); if (g.Get<User>(id) is { } u) who = s.Identity.LoginName(u); }
            return PublicHost.Page(s, h, "passkey.html", null, new() { ["ticket"] = ticket, ["kind"] = ask.Kind == "register" ? "register" : "login", ["who"] = who, ["name"] = ask.Name, ["public"] = true });
        });

        app.MapPost("/passkey/{ticket}/begin", (Server s, HttpContext h, string ticket) =>
        {
            try { s.Identity.Throttle("passkey", Address(h), 30); } catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
            if (!asks.TryGetValue(ticket, out var ask) || ask.Until < DateTime.UtcNow || ask.Used) return Results.Json(new { error = REFUSED }, statusCode: 400);
            using var g = s.Db.Global(); var rp = h.Request.Host.Host; ask.Begun = true;
            var keys = ask.User is Guid id ? g.Query<Passkey>("SELECT * FROM core_passkey WHERE user_id=@u", new { u = id }).ToList() : new();
            var known = new JsonArray(keys.Select(k => (JsonNode)new JsonObject { ["id"] = Unpadded(k.CredentialId), ["type"] = "public-key" }).ToArray());
            JsonObject options;
            if (ask.Kind == "register")
            {
                var u = g.Get<User>(ask.User.Value); if (u == null) return Results.Json(new { error = REFUSED }, statusCode: 400);
                if (keys.Count >= 20) return Results.Json(new { error = "Limite passkey raggiunto." }, statusCode: 400);
                var name = s.Identity.LoginName(u);
                options = new JsonObject
                {
                    ["rp"] = new JsonObject { ["name"] = "TD Booking Suite", ["id"] = rp }, ["user"] = new JsonObject { ["id"] = Unpadded(Passkeys.B64(u.Id.ToByteArray(true))), ["name"] = name, ["displayName"] = name },
                    ["challenge"] = Unpadded(Passkeys.B64(ask.Challenge)), ["pubKeyCredParams"] = new JsonArray(new JsonObject { ["type"] = "public-key", ["alg"] = -7 }, new JsonObject { ["type"] = "public-key", ["alg"] = -257 }),
                    ["timeout"] = 60000, ["excludeCredentials"] = known, ["authenticatorSelection"] = new JsonObject { ["residentKey"] = "required", ["requireResidentKey"] = true, ["userVerification"] = "required" }, ["attestation"] = "none",
                };
            }
            else options = new JsonObject { ["challenge"] = Unpadded(Passkeys.B64(ask.Challenge)), ["timeout"] = 60000, ["rpId"] = rp, ["allowCredentials"] = known, ["userVerification"] = "required" };
            return Results.Json(new JsonObject { ["options"] = options });
        });

        app.MapPost("/passkey/{ticket}/finish", (Server s, HttpContext h, string ticket, JsonObject body) =>
        {
            try { s.Identity.Throttle("passkey-finish", Address(h), 20); } catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
            if (!asks.TryGetValue(ticket, out var ask) || ask.Until < DateTime.UtcNow) return Results.Json(new { error = REFUSED }, statusCode: 400);
            lock (ask) { if (ask.Used || !ask.Begun) return Results.Json(new { error = REFUSED }, statusCode: 400); ask.Used = true; }  // one try a ticket, also when it fails
            List<string> codes = null;
            try
            {
                using var g = s.Db.Global();
                var u = ask.User is Guid id ? g.One<User>("SELECT * FROM core_user WHERE id=@id AND is_active AND auth_version=@v FOR UPDATE", new { id, v = ask.Version }) : null;
                if (u == null) throw new Passkeys.Refused("user");
                var credential = body["credential"]; var rp = h.Request.Host.Host; var origin = PageOrigin(h);
                if (ask.Kind == "register")
                {
                    var made = Passkeys.Register(credential, ask.Challenge, rp, origin);
                    if (g.Scalar<long>("SELECT count(*) FROM core_passkey WHERE user_id=@u", new { u = u.Id }) >= 20 || g.Scalar<long>("SELECT count(*) FROM core_passkey WHERE credential_id=@c", new { c = Passkeys.B64(made.CredentialId) }) > 0) throw new Passkeys.Refused("limit");
                    var name = body.Str("name").Trim(); if (name == "") name = ask.Name; if (name.Length > 80) name = name[..80];
                    g.Insert(new Passkey { UserId = u.Id, CredentialId = Passkeys.B64(made.CredentialId), PublicKey = Passkeys.B64(made.PublicKey), SignCount = made.SignCount, Name = name, Created = DateTime.UtcNow });
                    if (string.IsNullOrEmpty(u.Recovery) || u.Recovery == "[]") codes = s.Identity.NewRecovery(g, u);
                    g.Execute("UPDATE td_net_session SET mfa=true WHERE id=@i", new { i = ask.Session });  // this session has its second factor now
                }
                else
                {
                    var key = g.One<Passkey>("SELECT * FROM core_passkey WHERE credential_id=@c AND user_id=@u FOR UPDATE", new { c = Passkeys.B64(Passkeys.UnB64(credential?["id"]?.ToString())), u = u.Id }) ?? throw new Passkeys.Refused("unknown");
                    var handle = credential?["response"]?["userHandle"]?.ToString();
                    if (!string.IsNullOrEmpty(handle) && !Passkeys.UnB64(handle).AsSpan().SequenceEqual(u.Id.ToByteArray(true))) throw new Passkeys.Refused("userHandle");
                    key.SignCount = Passkeys.Assert(credential, ask.Challenge, rp, origin, Passkeys.UnB64(key.PublicKey), key.SignCount); g.Update(key, "sign_count");
                    if (ask.Kind == "confirm")
                    {
                        var ns = g.One<NetSession>("SELECT * FROM td_net_session WHERE id=@i", new { i = ask.Session }) ?? throw new Passkeys.Refused("session");
                        s.Identity.Confirmed(g, ns);
                    }
                }
                g.Commit(); ask.State = "fatto";
                return Results.Json(new JsonObject { ["ok"] = true, ["codes"] = new JsonArray((codes ?? new()).Select(c => (JsonNode)c).ToArray()) });
            }
            catch (Exception ex) when (ex is Passkeys.Refused or Npgsql.PostgresException or InvalidOperationException) { ask.State = "errore"; return Results.Json(new { error = REFUSED }, statusCode: 400); }
        });
    }
}
