using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Web;

/// <summary>Sign in for the windows: password, then the Authenticator code where the account has one; quick sign in
/// without password for the owners the agency allows, only from the server PC and the registered workstations.</summary>
public static class Auth
{
    sealed record Pre(Guid User, DateTime Until, int Version, bool Remember, string Device);
    static readonly ConcurrentDictionary<string, Pre> pending = new();
    public static readonly TimeSpan Idle = TimeSpan.FromHours(12);

    static string Address(HttpContext h) => h.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    static bool Trusted(HttpContext h) => h.Items["server_pc"] is true || h.Items.ContainsKey("postazione");
    static bool MfaEnabled(Server s) => !s.Standalone;

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/login", (Server s, HttpContext h, JsonObject body) =>
        {
            var login = (body.Str("login") is var l && l.Length > 254) ? l[..254] : body.Str("login");
            try
            {
                s.Identity.Throttle("login-ip", Address(h), 50);
                using var g = s.Db.Global();
                var found = s.Identity.ByLogin(g, login);
                s.Identity.Throttle("login-user", found?.Id.ToString() ?? s.Crypto.LoginIndex(login), 10);
                var pw = body.Str("password"); if (pw.Length > 1024) pw = pw[..1024];
                var u = s.Identity.Authenticate(g, login, pw);
                g.Commit();
                if (u == null) return Results.Json(new { error = "Credenziali non valide." }, statusCode: 401);
                return Finish(s, h, u, Json.Truthy(body["remember"]), body.Str("device"));
            }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
        });

        app.MapPost("/api/login/quick", (Server s, HttpContext h, JsonObject body) =>
        {
            try { s.Identity.Throttle("login-ip", Address(h), 50); } catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
            if (!Trusted(h)) return Results.Json(new { error = "Senza password si entra solo dal PC del programma e dalle postazioni registrate." }, statusCode: 403);
            using var g = s.Db.Global();
            var id = Bookings.AsId(body["user"]);
            var u = id.HasValue ? g.One<User>(@"SELECT u.* FROM core_user u WHERE u.id=@id AND u.quick_login AND u.is_active AND NOT u.is_staff
                AND EXISTS (SELECT 1 FROM core_membership m WHERE m.user_id=u.id AND m.role='owner')", new { id }) : null;
            if (u == null) return Results.Json(new { error = "Account non disponibile." }, statusCode: 404);
            foreach (var t in g.Query<Guid>("SELECT tenant_id FROM core_membership WHERE user_id=@u AND role='owner'", new { u = u.Id }).ToList())
                using (var c = new Ctx(s.Db, s.Crypto, t) { Actor = u.Id }) { c.Audit("login.quick", u.Id); c.Commit(); }
            return Finish(s, h, u, true, body.Str("device"));
        });

        // the buttons of the login window: who may come in without password, only on a trusted device
        app.MapGet("/api/login/quick", (Server s, HttpContext h) =>
        {
            if (!Trusted(h)) return Results.Json(Array.Empty<object>());
            using var g = s.Db.Global();
            var rows = g.Query<User>(@"SELECT DISTINCT u.* FROM core_user u JOIN core_membership m ON m.user_id=u.id WHERE u.quick_login AND u.is_active AND NOT u.is_staff AND m.role='owner'").ToList();
            return Results.Json(rows.Select(u => new
            {
                id = u.Id, name = s.Identity.LoginName(u),
                where = string.Join(", ", g.Query<string>("SELECT t.name FROM core_membership m JOIN core_tenant t ON t.id=m.tenant_id WHERE m.user_id=@u AND m.role='owner' ORDER BY t.name", new { u = u.Id })),
            }).OrderBy(x => x.name));
        });

        app.MapPost("/api/verify", (Server s, HttpContext h, JsonObject body) =>
        {
            var key = body.Str("pre");
            if (!pending.TryGetValue(key, out var pre) || pre.Until < DateTime.UtcNow) return Results.Json(new { error = "Accesso scaduto: inserisci di nuovo la password." }, statusCode: 401);
            try { s.Identity.Throttle("mfa", Address(h), 10); s.Identity.Throttle("mfa-user", pre.User.ToString(), 10); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
            using var g = s.Db.Global();
            var u = g.One<User>("SELECT * FROM core_user WHERE id=@id AND is_active AND auth_version=@v FOR UPDATE", new { id = pre.User, v = pre.Version });
            var code = body.Str("code").Trim();
            var ok = u != null && (s.Identity.VerifyTotp(g, u, code) || s.Identity.UseRecovery(g, u, code));
            if (!ok) { g.Commit(); return Results.Json(new { error = "Codice non valido, scaduto o già utilizzato." }, statusCode: 401); }
            pending.TryRemove(key, out _);
            var (_, token) = s.Identity.Open(g, u, pre.Remember, true, pre.Device, Address(h));
            g.Commit();
            return Results.Json(new { token });
        });

        app.MapPost("/api/logout", (Server s, HttpContext h) =>
        {
            using var g = s.Db.Global();
            s.Identity.Close(g, Bearer(h)); g.Commit();
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/postazione/pair", (Server s, HttpContext h, JsonObject body) =>
        {
            try
            {
                s.Identity.Throttle("postazione", Address(h), 10);
                var (token, ws) = s.Workstations.Pair(body.Str("codice"));
                return Results.Json(new { token, nome = ws.Nome, impronta = TD.Core.Win.Certs.Fingerprint(s.Config.DataFolder) });
            }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
            catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        });
    }

    static IResult Finish(Server s, HttpContext h, User u, bool remember, string device)
    {
        using var g = s.Db.Global();
        if (MfaEnabled(s) && s.Identity.MfaReady(g, u))
        {
            var key = Bookings.TokenUrlSafe(24);
            pending[key] = new Pre(u.Id, DateTime.UtcNow.AddMinutes(5), u.AuthVersion, remember, device);
            foreach (var old in pending.Where(x => x.Value.Until < DateTime.UtcNow).Select(x => x.Key).ToList()) pending.TryRemove(old, out _);
            return Results.Json(new { mfa = true, pre = key });
        }
        var (_, token) = s.Identity.Open(g, u, remember, false, device, Address(h));
        g.Commit();
        return Results.Json(new { token });
    }

    public static string Bearer(HttpContext h)
    {
        var a = h.Request.Headers.Authorization.ToString();
        return a.StartsWith("Bearer ") ? a[7..].Trim() : "";
    }
}
