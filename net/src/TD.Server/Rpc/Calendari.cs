using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.google_view and outlook_view: Collega, Scollega and the calendars' choice from an operator's card,
/// and Google's or Microsoft's answer coming back to the one address of the installation.</summary>
public static class Calendari
{
    sealed record Asked(Guid Tenant, Guid Resource, Guid User, string Service, string Verifier, string Uri, DateTime Until);
    /// <summary>The consent pages open in the browser: who asked is kept here for a quarter of an hour, by its state.</summary>
    static readonly ConcurrentDictionary<string, Asked> pending = new();
    static readonly string[] LOOPBACK = { "127.0.0.1", "localhost", "::1", "[::1]" };

    static string When(Call c, DateTime? at) => at.HasValue ? c.C.Cal.ToLocal(at.Value).ToString("dd/MM HH:mm") : "";
    /// <summary>What the card shows about Google: nothing for a new operator or while the administrator has not set the
    /// OAuth client (unless a connection is still there).</summary>
    internal static object Google(Call c, Resource r, bool isNew)
    {
        var g = new GoogleCalendar(c.C); var ready = g.Config().Count > 0;
        if (isNew || !(ready || r.Google != "" || r.GoogleError != "")) return null;
        var st = g.State(r);
        return new
        {
            pronto = ready, attivo = r.Google != "", errore = r.GoogleError, quando = When(c, r.GoogleAt), account = st.Str("account"), leggi = st.Str("read") != "" ? st.Str("read") : "primary", scrivi = st.Str("write") != "" ? st.Str("write") : "primary",
            calendari = (st["calendars"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().Select(x => new { id = x[0].ToString(), nome = x[1].ToString(), scrivibile = x[2]?.GetValueKind() == System.Text.Json.JsonValueKind.True }),
        };
    }
    internal static object Outlook(Call c, Resource r, bool isNew)
    {
        var m = new OutlookCalendar(c.C); var ready = m.Config().Count > 0;
        if (isNew || !(ready || r.Microsoft != "" || r.MicrosoftError != "")) return null;
        return new { pronto = ready, attivo = r.Microsoft != "", errore = r.MicrosoftError, quando = When(c, r.MicrosoftAt), account = m.State(r).Str("account") };
    }

    /// <summary>redirect_uri: back to this program at its public address (a «Web application» client), or, asked on this
    /// very PC, at the loopback address a «Desktop app» client accepts (Windows without a public address).</summary>
    static string RedirectUri(Call c, string path) => LOOPBACK.Contains(c.Http.Request.Host.Host) ? $"{c.Http.Request.Scheme}://{c.Http.Request.Host.Value}{path}" : c.PublicOrigin.TrimEnd('/') + path;

    [Rpc("risorsa.calendario", Perm = "catalog.write")]
    public static object Act(Call c)
    {
        if (!Configura.CatalogAllowed(c, "resources")) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
        var x = c.C; var r = x.S.Get<Resource>(c.NeedId("id")) ?? throw new KeyNotFoundException(); var google = c.Str("servizio") != "outlook"; var name = google ? "google" : "outlook";
        switch (c.Str("azione"))
        {
            case "disconnect":
                if (google) new GoogleCalendar(x).Disconnect(r); else new OutlookCalendar(x).Disconnect(r);
                x.Audit(name + ".disconnected", r.Id); return new { testo = google ? "Google Calendar scollegato." : "Outlook scollegato." };
            case "calendars" when google:
                new GoogleCalendar(x).Choose(r, c.Str("read"), c.Str("write")); x.Audit("google.calendars", r.Id);
                return new { testo = "Calendari scelti: entro 5 minuti si allinea." };
            case "connect":
                {
                    if ((google ? new GoogleCalendar(x).Config() : new OutlookCalendar(x).Config()).Count == 0) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
                    foreach (var old in pending.Where(p => p.Value.Until < DateTime.UtcNow).Select(p => p.Key).ToList()) pending.TryRemove(old, out _);
                    var (verifier, challenge) = OperatorCalendar.Pkce(); var state = Bookings.TokenUrlSafe(24); var uri = RedirectUri(c, google ? GoogleCalendar.PATH : OutlookCalendar.PATH);
                    pending[state] = new(c.Tenant.Id, r.Id, c.User.Id, name, verifier, uri, DateTime.UtcNow.AddMinutes(15));
                    return new { apri = google ? new GoogleCalendar(x).Start(uri, state, challenge) : new OutlookCalendar(x).Start(uri, state, challenge) };  // the window opens it in the browser
                }
        }
        throw new BookingException("Operazione non valida.");
    }

    public static void Map(WebApplication app)
    {
        foreach (var (path, name, label) in new[] { (GoogleCalendar.PATH, "google", "Google Calendar"), (OutlookCalendar.PATH, "outlook", "Outlook") })
            app.MapGet(path, (Server s, HttpContext h) =>
            {
                IResult Say(string text, bool ok = false) => Results.Content($"<!doctype html><meta charset=utf-8><title>TD Booking Suite</title><body style=\"font:16px system-ui;max-width:520px;margin:15vh auto;padding:0 20px;text-align:center\"><h1 style=\"font-size:22px\">{(ok ? "Fatto" : "Non riuscito")}</h1><p>{System.Net.WebUtility.HtmlEncode(text)}</p><p>Puoi chiudere questa pagina e tornare al programma.</p>", "text/html; charset=utf-8");
                var q = h.Request.Query;
                if (!pending.TryRemove(q["state"].ToString(), out var p) || p.Until < DateTime.UtcNow || p.Service != name) return Say($"Collegamento a {label} non riuscito o scaduto: riprova dalla scheda.");
                using var c = new Ctx(s.Db, s.Crypto, p.Tenant) { Actor = p.User, PublicOrigin = s.PublicOrigin, Edition = s.Edition };
                var r = c.S.Get<Resource>(p.Resource); if (r == null) return Say($"Collegamento a {label} non riuscito o scaduto: riprova dalla scheda.");
                string error;
                if (name == "google") { new GoogleCalendar(c).Finish(r, q["code"].ToString(), p.Uri, p.Verifier, q["error"].ToString()); error = r.GoogleError; }
                else { new OutlookCalendar(c).Finish(r, q["code"].ToString(), p.Uri, p.Verifier, q["error"].ToString()); error = r.MicrosoftError; }
                c.Audit(name + (error == "" ? ".connected" : ".failed"), r.Id); c.Commit();
                return error == "" ? Say($"{label} collegato a {r.Name}: entro 5 minuti si allinea.", true) : Say(error);
            });
    }
}
