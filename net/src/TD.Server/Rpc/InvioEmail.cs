using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>core/mailsetup.py: the email page of the program and of each business, with three ways to send: SMTP (any
/// provider), Google / Gmail with OAuth, Microsoft 365 / Exchange with Graph. The administrator only.</summary>
public static class InvioEmail
{
    const string WHO = "amministratore del programma";
    static readonly (string Key, string Name, string Host, string Port, string Hint)[] PROVIDERS =
    {
        ("gmail", "Gmail o Google Workspace", "smtp.gmail.com", "587", "Utente: l'indirizzo Gmail completo. Password: una password per le app, da Account Google, Sicurezza, Verifica in due passaggi, Password per le app. Mittente: lo stesso indirizzo o un alias aggiunto in Gmail, Invia messaggio come"),
        ("microsoft", "Microsoft 365 o Outlook", "smtp.office365.com", "587", "Utente: l'indirizzo completo. L'amministratore deve abilitare l'SMTP autenticato per la casella. Mittente: la stessa casella o una con il permesso Invia come"),
        ("aruba", "Aruba", "smtps.aruba.it", "465", "Utente e password della casella Aruba. Mittente: un indirizzo dello stesso dominio"),
        ("register", "Register.it", "authsmtp.securemail.pro", "465", "Utente: la casella SMTP autenticata, per esempio smtp@tuodominio.it. Mittente: qualunque indirizzo del dominio, per esempio info@"),
        ("ovh", "OVHcloud", "ssl0.ovh.net", "465", "Utente e password della casella OVH. Mittente: un indirizzo dello stesso dominio"),
        ("brevo", "Brevo", "smtp-relay.brevo.com", "587", "Utente e chiave SMTP dalla sezione SMTP e API di Brevo. Mittente: un indirizzo verificato in Brevo"),
        ("other", "Altro", "", "", "Chiedi a chi gestisce le email del dominio server, porta, utente e password SMTP"),
    };
    static readonly (string Key, string Label, string Text)[] TABS =
    {
        ("smtp", "SMTP", "Qualsiasi casella: del dominio, Aruba, Register.it, Gmail o Microsoft con password"), ("google", "Google / Gmail", "Collegamento con l'account Google, senza password SMTP"),
        ("microsoft", "Microsoft 365 / Exchange", "Invio da una casella di Microsoft 365 con Microsoft Graph"),
    };
    static readonly Dictionary<string, string> TAB_OF = new() { ["smtp"] = "smtp", ["gmail"] = "google", ["graph"] = "microsoft" };

    /// <summary>Target: where a configuration lives: the program (SiteSettings) or one business.</summary>
    sealed class Target(Call c, bool site)
    {
        public bool Site => site;
        public string Key => site ? "site" : c.Tenant.Slug;
        public string Name => site ? SiteRow().Name : c.Tenant.Name;
        Sitesettings SiteRow() { using var g = c.S.Db.Global(); return g.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1") ?? new Sitesettings(); }
        public bool Own => site ? !string.IsNullOrEmpty(SiteRow().Smtp) : !string.IsNullOrEmpty(c.Tenant.Smtp);
        public JsonObject Load() => site ? (SiteRow().Smtp is { Length: > 0 } s ? (JsonObject)c.S.Crypto.Reveal(s, "site-smtp") : new JsonObject()) : string.IsNullOrEmpty(c.Tenant.Smtp) ? new JsonObject() : c.C.Decrypt(c.Tenant.Smtp, "smtp");
        public void Store(JsonObject data)
        {
            if (site) { using var g = c.S.Db.Global(); g.Execute("UPDATE core_sitesettings SET smtp=@s, updated=@n", new { s = data != null ? c.S.Crypto.Protect(data, "site-smtp") : "", n = DateTime.UtcNow }); g.Commit(); return; }
            var x = c.C; c.Tenant.Smtp = data != null ? x.Encrypt(data, "smtp") : "";
            x.S.Execute("UPDATE core_tenant SET smtp=@s WHERE id=@t", new { s = c.Tenant.Smtp, t = c.Tenant.Id });
            x.Audit(data != null ? "smtp.updated" : "smtp.removed", c.Tenant.Id, new { method = data != null ? Mail.Method(data) : "smtp" });
        }
        public JsonObject Effective() { if (!site) return Mail.Config(c.C); using var g = c.S.Db.Global(); return Mail.Agency(g, c.S.Crypto); }
    }
    static string RedirectUri(Call c) => c.PublicOrigin.TrimEnd('/') + "/oauth/google/";

    [Rpc("smtp", Staff = true, Fresh = true)] public static object Page(Call c) => Page(c, new Target(c, false));
    [Rpc("smtp.sito", Staff = true, Fresh = true, Tenant = false)] public static object SitePage(Call c) => Page(c, new Target(c, true));
    [Rpc("smtp.azione", Staff = true, Fresh = true)] public static object Action(Call c) => Action(c, new Target(c, false));
    [Rpc("smtp.sito.azione", Staff = true, Fresh = true, Tenant = false)] public static object SiteAction(Call c) => Action(c, new Target(c, true));

    static object Page(Call c, Target target)
    {
        var current = target.Load(); var method = current.Count > 0 ? Mail.Method(current) : ""; var config = target.Effective(); var own = target.Own;
        string Cur(string k, string same) => method == same ? current.Str(k) : ""; var name = current.Str("sender_name") != "" ? current.Str("sender_name") : target.Name;
        var smtp = new List<Fld>
        {
            new() { Nome = "host", Etichetta = "Server SMTP", Valore = method is "" or "smtp" ? current.Str("host") : "", Max = 253 },
            new() { Nome = "port", Etichetta = "Porta", Tipo = "select", Valore = method is "" or "smtp" && current.Str("port") == "465" ? "465" : "587", Scelte = new object[] { new { id = "587", nome = "587 — STARTTLS" }, new { id = "465", nome = "465 — TLS" } } },
            new() { Nome = "username", Etichetta = "Utente SMTP", Valore = method is "" or "smtp" ? current.Str("username") : "", Max = 254, Aiuto = "La casella con cui il server ti fa entrare, per esempio smtp@tuodominio.it" },
            new() { Nome = "password", Etichetta = "Password SMTP (vuoto = mantieni)", Tipo = "password", Valore = "", Max = 1024 },
            new() { Nome = "sender_name", Etichetta = "Nome del mittente, per esempio Booking Suite TD", Valore = method is "" or "smtp" && current.Count > 0 ? current.Str("sender_name") : name, Max = 80, Largo = true },
            new() { Nome = "sender", Etichetta = "Email del mittente, quella che vedono i clienti", Valore = method is "" or "smtp" ? current.Str("sender") : "", Aiuto = "Per esempio info@tuodominio.it. L'email parte dalla casella dell'utente SMTP e mostra questo indirizzo: il server deve accettarlo, di solito basta che sia dello stesso dominio" },
            new() { Nome = "reply_to", Etichetta = "Rispondi a", Valore = method is "" or "smtp" ? current.Str("reply_to") : "", Aiuto = "Facoltativo: dove arrivano le risposte dei clienti, se diverso dal mittente" },
            new() { Nome = "verified", Etichetta = "Ho verificato il dominio mittente e la configurazione SPF/DKIM/DMARC", Tipo = "check", Valore = false },
        };
        var google = new List<Fld>
        {
            new() { Nome = "client_id", Etichetta = "Client ID", Valore = Cur("client_id", "gmail"), Max = 200, Aiuto = "Da Google Cloud, API e servizi, Credenziali: un ID client OAuth di tipo Applicazione web" },
            new() { Nome = "client_secret", Etichetta = "Client secret (vuoto = mantieni)", Tipo = "password", Valore = "", Max = 200 },
            new() { Nome = "sender_name", Etichetta = "Nome del mittente", Valore = name, Max = 80, Largo = true },
            new() { Nome = "sender", Etichetta = "Email del mittente", Valore = Cur("sender", "gmail"), Aiuto = "Vuoto: l'account Google collegato. Un altro indirizzo deve essere un alias di Gmail, Invia messaggio come" },
            new() { Nome = "reply_to", Etichetta = "Rispondi a", Valore = Cur("reply_to", "gmail"), Aiuto = "Facoltativo: dove arrivano le risposte dei clienti" },
        };
        var graph = new List<Fld>
        {
            new() { Nome = "tenant_id", Etichetta = "ID della directory (tenant)", Valore = Cur("tenant_id", "graph"), Max = 100, Largo = true, Aiuto = "In Entra ID, Panoramica: ID directory (tenant), oppure il dominio, per esempio tuodominio.onmicrosoft.com" },
            new() { Nome = "client_id", Etichetta = "ID dell'applicazione (client)", Valore = Cur("client_id", "graph"), Max = 100 },
            new() { Nome = "client_secret", Etichetta = "Valore del segreto client (vuoto = mantieni)", Tipo = "password", Valore = "", Max = 300 },
            new() { Nome = "mailbox", Etichetta = "Casella da cui partono le email", Valore = Cur("mailbox", "graph"), Aiuto = "Per esempio prenotazioni@tuodominio.it: una casella di Microsoft 365 o di Exchange Online" },
            new() { Nome = "sender_name", Etichetta = "Nome del mittente", Valore = name, Max = 80, Largo = true },
            new() { Nome = "sender", Etichetta = "Email del mittente", Valore = Cur("sender", "graph"), Aiuto = "Vuoto: la casella qui sopra. Un altro indirizzo richiede il permesso Invia come su quella casella" },
            new() { Nome = "reply_to", Etichetta = "Rispondi a", Valore = Cur("reply_to", "graph") },
        };
        return new
        {
            sito = target.Site, nome = target.Site ? "" : c.Tenant.Name,
            titolo = target.Site ? "Invio email dell'" + WHO : "Invio email",
            descrizione = target.Site ? "Serve per inviti, conferme, notifiche e richieste di prova, per tutte le attività che non hanno un invio proprio. Le campagne marketing usano sempre e solo l'invio dell'attività"
                : "La casella da cui partono le conferme delle prenotazioni, le notifiche e gli inviti di " + c.Tenant.Name + ": Gmail, Microsoft 365 o l'email del suo dominio. Se il marketing è attivo, serve anche per le campagne. Senza, si usa l'invio dell'" + WHO + ". Le credenziali restano cifrate e gli utenti dell'attività non le vedono",
            smtp, google, graph, schede = TABS.Select(t => new { chiave = t.Key, nome = t.Label, testo = t.Text }), scheda = TAB_OF.GetValueOrDefault(method, "smtp"),
            fornitori = PROVIDERS.Select(p => new { nome = p.Name, host = p.Host, porta = p.Port, aiuto = p.Hint }),
            metodo = method, etichetta = Mail.METHODS.GetValueOrDefault(method, ""), account = current.Str("account") != "" ? current.Str("account") : current.Str("sender"), casella = current.Str("mailbox"), ritorno = RedirectUri(c),
            configurato = config != null, proprio = own, daEnv = target.Site && !own && config != null, daAgenzia = !target.Site && !own && config != null,
            mittente = config != null ? Mail.From(config) : "", come = config != null ? Mail.Describe(config) : "", daSito = !target.Site && WpSync.BySite(c.C), email = c.S.Identity.Email(c.User),
        };
    }

    static string CleanName(Posted p)
    {
        var v = p.Text("sender_name", false, 80);
        if (v.Any(ch => "\r\n<>\"@".Contains(ch))) p.Error("sender_name", "Solo il nome, senza a capo, virgolette o indirizzi.");
        return v;
    }
    static string Email(Posted p, string name, bool required = false)
    {
        var v = p.Text(name, required, 320);
        if (v != "" && !Clients.ValidEmail(v)) p.Error(name, "Inserisci un indirizzo email valido.");
        return v;
    }

    static readonly ConcurrentDictionary<string, (string Target, DateTime Until, JsonObject Data)> pending = new();

    static object Action(Call c, Target target)
    {
        var current = target.Load(); var method = current.Count > 0 ? Mail.Method(current) : ""; var p = new Posted(c, c.Obj("valori"));
        string Keep(string k, string same) => p.Raw(k) != "" ? p.Raw(k) : method == same ? current.Str(k) : "";
        switch (c.Str("azione"))
        {
            case "test":
                {
                    var to = c.Str("test_to").Trim();
                    var problem = Clients.ValidEmail(to) ? Mail.SendTest(target.Effective(), to, target.Name, c.PublicOrigin) : "Indirizzo del destinatario non valido.";
                    return problem != "" ? new { testo = "Email di prova non partita. " + problem, errore = true } : new { testo = "Email di prova inviata a " + to + ". Se non la vedi, controlla anche lo spam.", errore = false };
                }
            case "remove":
                if (method == "gmail" && current.Str("refresh_token") != "") Mail.GoogleRevoke(current.Str("refresh_token"));
                target.Store(null);
                return new { testo = "Invio email rimosso" + (target.Site ? "." : ": si usa l'SMTP dell'agenzia.") };
            case "save_smtp":
                {
                    var data = new JsonObject
                    {
                        ["host"] = p.Text("host", true, 253), ["port"] = p.Raw("port") is "587" or "465" ? int.Parse(p.Raw("port")) : 0, ["username"] = p.Text("username", false, 254), ["password"] = p.Raw("password"),
                        ["sender_name"] = CleanName(p), ["sender"] = Email(p, "sender", true), ["reply_to"] = Email(p, "reply_to"), ["verified"] = true,
                    };
                    if (Till.Int(data["port"]) == 0) p.Error("port", "Scegli un'opzione valida.");
                    if (!p.Bool("verified")) p.Error("verified", Posted.REQUIRED);
                    if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
                    data["mode"] = Till.Int(data["port"]) == 465 ? "ssl" : "starttls"; data["method"] = "smtp";
                    try { Network.PublicAddresses(data.Str("host")); }
                    catch (BookingException ex) when (ex.Message == "Indirizzo non raggiungibile.") { throw new FormErrors(new() { ["host"] = "Server SMTP non trovato: controlla il nome, per esempio smtp.tuodominio.it." }); }
                    catch (BookingException ex) { throw new FormErrors(new() { [""] = ex.Message }); }
                    if (data.Str("password") == "") data["password"] = method == "smtp" ? current.Str("password") : "";
                    target.Store(data);
                    return new { testo = target.Site ? "SMTP dell'" + WHO + " salvato: le email di servizio di tutte le attività senza un SMTP proprio partono da qui." : "SMTP salvato. Usa l'email di prova per controllarlo." };
                }
            case "save_graph":
                {
                    var tenantId = p.Text("tenant_id", true, 100); if (tenantId != "" && tenantId.Any(ch => "/?#@ ".Contains(ch))) p.Error("tenant_id", "ID non valido.");
                    var data = new JsonObject
                    {
                        ["tenant_id"] = tenantId, ["client_id"] = p.Text("client_id", true, 100), ["client_secret"] = Keep("client_secret", "graph"), ["mailbox"] = Email(p, "mailbox", true),
                        ["sender_name"] = CleanName(p), ["sender"] = Email(p, "sender"), ["reply_to"] = Email(p, "reply_to"), ["method"] = "graph",
                    };
                    if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
                    if (data.Str("sender") == "") data["sender"] = data.Str("mailbox");
                    if (data.Str("client_secret") == "") throw new FormErrors(new() { ["client_secret"] = "Inserisci il valore del segreto client." });
                    try { Mail.GraphToken(data); } catch (MailApiError ex) { throw new FormErrors(new() { [""] = ex.Message }); }
                    target.Store(data);
                    return new { testo = "Microsoft 365 collegato: le email partono da " + data.Str("mailbox") + ". Manda un'email di prova per controllare il permesso di invio." };
                }
            case "google_connect":
                {
                    var data = new JsonObject { ["client_id"] = p.Text("client_id", true, 200), ["client_secret"] = Keep("client_secret", "gmail"), ["sender_name"] = CleanName(p), ["sender"] = Email(p, "sender"), ["reply_to"] = Email(p, "reply_to") };
                    if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
                    if (data.Str("client_secret") == "") throw new FormErrors(new() { ["client_secret"] = "Inserisci il client secret." });
                    foreach (var old in pending.Where(x => x.Value.Until < DateTime.UtcNow).Select(x => x.Key).ToList()) pending.TryRemove(old, out _);
                    var state = Bookings.TokenUrlSafe(24); pending[state] = (target.Key, DateTime.UtcNow.AddMinutes(15), data);
                    return new { apri = Mail.GoogleAuthUrl(data.Str("client_id"), RedirectUri(c), state) };  // the window opens Google in the browser
                }
        }
        throw new BookingException("Operazione non valida.");
    }

    /// <summary>google_callback: Google comes back here after the consent page, in the browser; the state given to the
    /// administrator a moment before says for whom (the program or a business).</summary>
    public static void Map(WebApplication app) => app.MapGet("/oauth/google/", (Server s, HttpContext h) =>
    {
        IResult Say(string text, bool ok = false) => Results.Content($"<!doctype html><meta charset=utf-8><title>TD Booking Suite</title><body style=\"font:16px system-ui;max-width:520px;margin:15vh auto;padding:0 20px;text-align:center\"><h1 style=\"font-size:22px\">{(ok ? "Fatto" : "Non riuscito")}</h1><p>{System.Net.WebUtility.HtmlEncode(text)}</p><p>Puoi chiudere questa pagina e tornare al programma.</p>", "text/html; charset=utf-8");
        var q = h.Request.Query;
        if (!pending.TryRemove(q["state"].ToString(), out var p) || p.Until < DateTime.UtcNow) return Say("Collegamento con Google scaduto o non valido: riprova.");
        if (q["error"].ToString() is { Length: > 0 } error) return Say("Google non ha concesso l'accesso" + (error == "access_denied" ? ": hai annullato il consenso." : ": " + (error.Length > 80 ? error[..80] : error) + "."));
        (string RefreshToken, string Account) got; var redirect = s.PublicOrigin.TrimEnd('/') + "/oauth/google/";
        try { got = Mail.GoogleExchange(p.Data.Str("client_id"), p.Data.Str("client_secret"), q["code"].ToString(), redirect); } catch (MailApiError ex) { return Say(ex.Message); }
        var config = new JsonObject
        {
            ["method"] = "gmail", ["client_id"] = p.Data.Str("client_id"), ["client_secret"] = p.Data.Str("client_secret"), ["refresh_token"] = got.RefreshToken, ["account"] = got.Account,
            ["sender"] = p.Data.Str("sender") != "" ? p.Data.Str("sender") : got.Account, ["sender_name"] = p.Data.Str("sender_name"), ["reply_to"] = p.Data.Str("reply_to"),
        };
        if (config.Str("sender") == "") return Say("Google non ha indicato l'indirizzo dell'account: scrivilo in Email del mittente e ricollega.");
        if (p.Target == "site") { using var g = s.Db.Global(); g.Execute("UPDATE core_sitesettings SET smtp=@s, updated=@n", new { s = s.Crypto.Protect(config, "site-smtp"), n = DateTime.UtcNow }); g.Commit(); }
        else
        {
            Guid? id; using (var g = s.Db.Global()) id = g.Scalar<Guid?>("SELECT id FROM core_tenant WHERE slug=@s", new { s = p.Target });
            if (id == null) return Results.NotFound();
            using var c = new Ctx(s.Db, s.Crypto, id.Value);
            c.S.Execute("UPDATE core_tenant SET smtp=@s WHERE id=@t", new { s = c.Encrypt(config, "smtp"), t = id }); c.Audit("smtp.updated", id, new { method = "gmail" }); c.Commit();
        }
        return Say("Google collegato: le email partono da " + (got.Account != "" ? got.Account : config.Str("sender")) + ". Manda un'email di prova.", true);
    });
}
