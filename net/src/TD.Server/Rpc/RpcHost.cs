using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;
using TD.Server.Web;

namespace TD.Server.Rpc;

/// <summary>A method the windows may call: POST /api/rpc/{Name} with the arguments as JSON.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    /// <summary>The permission it needs (permissions.py), none for any member.</summary>
    public string Perm { get; init; }
    /// <summary>Inside a business (X-TD-Attivita); false for the program's own pages (licence, backup, account).</summary>
    public bool Tenant { get; init; } = true;
    /// <summary>Only the program's administrator (is_staff).</summary>
    public bool Staff { get; init; }
    /// <summary>Asks for the password again when the last check is older than 30 minutes (identity.fresh).</summary>
    public bool Fresh { get; init; }
}

/// <summary>Who calls, from where, inside which business, with which rights; the unit of work opened on demand.</summary>
public sealed class Call : IDisposable
{
    public Server S { get; init; }
    public HttpContext Http { get; init; }
    public User User { get; init; }
    public NetSession Session { get; init; }
    public Tenant Tenant { get; init; }
    public Membership Member { get; init; }
    public HashSet<string> Perms { get; init; } = new();
    public JsonObject Args { get; init; } = new();
    public bool ServerPc => Http.Items["server_pc"] is true;
    Ctx ctx;
    public Ctx C => ctx ??= new Ctx(S.Db, S.Crypto, Tenant.Id) { Actor = User.Id, PublicOrigin = PublicOrigin, Out = Outbox.Instance, Edition = S.Edition };
    public bool HasCtx => ctx != null;
    public string PublicOrigin => S.PublicOrigin;

    public bool Can(string perm) => Perms.Contains(perm);
    public void Require(string perm) { if (!Can(perm)) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione."); }

    // ---------- arguments
    public JsonNode this[string key] => Args[key];
    public string Str(string key, string fallback = "") => Args[key] == null ? fallback : Args.Str(key);
    public Guid? Id(string key) => Bookings.AsId(Args[key]);
    public Guid NeedId(string key) => Id(key) ?? throw new BookingException("Selezione non valida o non disponibile.");
    public int Int(string key, int fallback = 0) => Args[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : int.TryParse(Str(key), out var j) ? j : fallback;
    public bool Bool(string key) => Json.Truthy(Args[key]);
    public DateOnly Day(string key, DateOnly? fallback = null) =>
        DateOnly.TryParseExact(Str(key), "yyyy-MM-dd", out var d) ? d : fallback ?? (HasCtx || Tenant != null ? C.Today : DateOnly.FromDateTime(DateTime.Now));
    public JsonObject Obj(string key) => Args[key] as JsonObject ?? new JsonObject();

    public void Commit() { if (ctx != null) ctx.Commit(); }
    public void Dispose() => ctx?.Dispose();
}

public static class RpcHost
{
    static readonly Dictionary<string, (MethodInfo M, RpcAttribute A)> methods = typeof(RpcHost).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Select(m => (M: m, A: m.GetCustomAttribute<RpcAttribute>())).Where(x => x.A != null)
        .ToDictionary(x => x.A.Name, x => (x.M, x.A));

    public static IEnumerable<string> Names => methods.Keys;
    static readonly JsonSerializerOptions Out = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/rpc/{name}", async (string name, Server s, HttpContext h) =>
        {
            if (!methods.TryGetValue(name, out var target)) return Results.Json(new { error = "Funzione inesistente: " + name }, statusCode: 404);
            JsonObject args;
            try { args = h.Request.ContentLength is > 0 || h.Request.Headers.TransferEncoding.Count > 0 ? (await JsonNode.ParseAsync(h.Request.Body)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (JsonException) { return Results.Json(new { error = "Richiesta non valida." }, statusCode: 400); }
            NetSession ns; User u;
            using (var g = s.Db.Global()) { (ns, u) = s.Identity.Resume(g, Auth.Bearer(h), Auth.Idle); g.Commit(); }
            if (u == null) return Results.Json(new { error = "Sessione scaduta: entra di nuovo.", login = true }, statusCode: 401);
            if (target.A.Staff && !u.IsStaff) return Results.Json(new { error = "Solo l'amministratore del programma." }, statusCode: 403);
            // identity.fresh: the sensitive operations want the user confirmed in the last 15 minutes
            if (target.A.Fresh && !Identity.IsFresh(ns)) return Results.Json(new { error = "Per questa operazione conferma che sei tu.", conferma = Auth.ConfirmMode(s, h, u) }, statusCode: 428);
            Tenant tenant = null; Membership member = null; var perms = new HashSet<string>(); Supportgrant support = null;
            if (target.A.Tenant)
            {
                var slug = h.Request.Headers["X-TD-Attivita"].ToString();
                using var g = s.Db.Global();
                tenant = g.One<Tenant>("SELECT * FROM core_tenant WHERE slug=@s", new { s = slug });
                if (tenant == null) return Results.Json(new { error = "Attività non trovata." }, statusCode: 404);
                member = g.One<Membership>("SELECT * FROM core_membership WHERE tenant_id=@t AND user_id=@u", new { t = tenant.Id, u = u.Id });
                if (member == null && !u.IsStaff) return Results.Json(new { error = "Non fai parte di questa attività." }, statusCode: 403);
                perms = Permissions.Effective(member, tenant, u.IsStaff);
                // the administrator of the program: the technical pages only; the contents with the access of assistance the
                // owner granted (Accesso dell'amministratore), confirmed in the last 15 minutes, every call recorded
                if (member == null)
                {
                    var content = target.A.Perm is string need && need != "catalog.write";
                    Supportgrant grant; using (var ts = s.Db.Tenant(tenant.Id)) grant = ts.One<Supportgrant>("SELECT * FROM core_supportgrant WHERE user_id=@u AND NOT revoked AND expires>@n ORDER BY expires DESC LIMIT 1", new { u = u.Id, n = DateTime.UtcNow });
                    if (content && grant == null) return Results.Json(new { error = "Prenotazioni e clienti di questa attività sono riservati: serve un accesso di assistenza concesso dall'amministratore dell'attività, dalla sua pagina Accesso dell'amministratore." }, statusCode: 403);
                    if (content && !Identity.IsFresh(ns)) return Results.Json(new { error = "Per questa operazione conferma che sei tu.", conferma = Auth.ConfirmMode(s, h, u) }, statusCode: 428);
                    if (grant != null) perms = Permissions.SUPPORT.ToHashSet();
                    if (content) support = grant;
                }
                if (target.A.Perm != null && !perms.Contains(target.A.Perm)) return Results.Json(new { error = "Non hai i permessi per questa operazione." }, statusCode: 403);
            }
            using var call = new Call { S = s, Http = h, User = u, Session = ns, Tenant = tenant, Member = member, Perms = perms, Args = args };
            try
            {
                if (support != null) call.C.Audit("support.access", support.Id, new { method = "RPC", path = name });
                var result = target.M.Invoke(null, new object[] { call });
                if (result is Task t) { await t; result = t.GetType().GetProperty("Result")?.GetValue(t); }
                if (result is IResult r) { call.Commit(); return r; }
                // serialized here, while the unit of work is open: the answers are often lazy queries
                var node = JsonSerializer.SerializeToNode(result ?? new { ok = true }, Out);
                call.Commit();
                return Results.Content(node.ToJsonString(Out), "application/json; charset=utf-8");
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { return Fail(ex.InnerException); }
            catch (Exception ex) { return Fail(ex); }
        });
    }

    static IResult Fail(Exception ex) => ex switch
    {
        ForceWarning w => Results.Json(new { warning = w.Message }, statusCode: 409),
        FormErrors f => Results.Json(new { error = f.Message, errori = f.Errors, conferma = f.Confirm }, statusCode: 422),
        BookingException b => Results.Json(new { error = b.Message }, statusCode: 400),
        ArgumentException a => Results.Json(new { error = a.Message }, statusCode: 400),
        UnauthorizedAccessException u => Results.Json(new { error = u.Message }, statusCode: 403),
        KeyNotFoundException => Results.Json(new { error = "Non trovato." }, statusCode: 404),
        _ => Log(ex),
    };
    static IResult Log(Exception ex)
    {
        Console.Error.WriteLine(ex);
        return Results.Json(new { error = "Errore del programma: " + ex.Message }, statusCode: 500);
    }
}
