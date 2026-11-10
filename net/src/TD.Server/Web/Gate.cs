using System.Net;
using TD.Core.Win;

namespace TD.Server.Web;

/// <summary>tdwin/middleware.py: the server PC itself, the registered workstations, the licence, on every request.</summary>
public static class Gate
{
    // reachable from any device: booking pages and customer links, the pairing of a workstation, the program's info
    static readonly string[] PUBLIC = { "/book/", "/action/", "/brand/", "/site/", "/static/", "/api/wordpress/", "/api/info", "/api/postazione/", "/oauth/" };
    static readonly string[] OFFLINE = { "/book/", "/api/wordpress/" };
    static readonly string[] UNLICENSED = { "/api/login", "/api/verify", "/api/logout", "/api/info", "/api/postazione/", "/api/rpc/licenza.", "/api/rpc/backup", "/api/rpc/sessione.", "/api/primo-avvio", "/api/conferma", "/api/rpc/inizia", "/api/rpc/account", "/static/", "/site/" };
    static HashSet<string> local = new(); static DateTime localAt;

    public static bool IsServerPc(HttpContext h)
    {
        if (DateTime.UtcNow - localAt > TimeSpan.FromMinutes(1)) { local = Certs.OwnAddresses(); localAt = DateTime.UtcNow; }
        var remote = h.Connection.RemoteIpAddress;
        if (remote == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var headers = h.Request.Headers;
        return (IPAddress.IsLoopback(remote) || local.Contains(remote.ToString())) && !headers.ContainsKey("X-Forwarded-For") && !headers.ContainsKey("Forwarded");
    }

    public static async Task Handle(HttpContext h, Func<Task> next)
    {
        var s = h.RequestServices.GetRequiredService<Server>();
        var path = h.Request.Path.Value ?? "/";
        var serverPc = IsServerPc(h);
        h.Items["server_pc"] = serverPc;
        if (!s.Online && OFFLINE.Any(path.StartsWith))
        {
            await Text(h, 404, "La pagina prenotazioni non è attiva: l'amministratore del programma la accende da Online e sito."); return;
        }
        if (!serverPc && !PUBLIC.Any(path.StartsWith))
        {
            var ws = s.Workstations.Current(h.Request.Headers["X-TD-Postazione"].ToString(), s.Rete.Client(h));
            if (ws == null) { await Text(h, 403, "Questa postazione non è registrata: aprila e inserisci il codice dalla pagina Postazioni."); return; }
            h.Items["postazione"] = ws;
        }
        var state = s.Licensing.Apply();
        h.Items["licenza"] = state;
        if (state.Kind == "bloccato" && !UNLICENSED.Any(path.StartsWith))
        {
            await Text(h, path.StartsWith("/api/rpc/") ? 402 : 503, path.StartsWith("/api/rpc/") ? state.Problema : "Servizio non attivo."); return;
        }
        var started = DateTime.UtcNow;
        try { await next(); }
        finally
        {
            var took = DateTime.UtcNow - started;
            if (took.TotalSeconds > 3) { Console.Error.WriteLine($"Pagina lenta: {h.Request.Method} {path} {took.TotalSeconds:0.0} s"); Rpc.Slow.Note(s, "programma", path, took.TotalSeconds); }
        }
    }

    public static Task Text(HttpContext h, int status, string text)
    {
        h.Response.StatusCode = status; h.Response.ContentType = "text/plain; charset=utf-8";
        return h.Response.WriteAsync(text);
    }
}
