using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>The pages of the Portale edition only (trial.py, pages.py, site.plugin_view): Richieste di prova, Prezzi e
/// versioni with Perché sceglierci, Plugin WordPress.</summary>
public static class Portale
{
    static readonly System.Globalization.CultureInfo INV = System.Globalization.CultureInfo.InvariantCulture;
    static void Cloud(Call c) { if (c.S.Edition != "cloud") throw new KeyNotFoundException(); }
    static Sitesettings Site(Scope g) => g.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1") ?? throw new KeyNotFoundException();
    static string TypeLabel(string key) => Presets.All.TryGetValue(key ?? "", out var p) ? p.Label : "Altro, appuntamenti generici";
    static string Local(DateTime utc, string format) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")).ToString(format, INV);

    // ---------- Richieste di prova
    [Rpc("richieste", Tenant = false, Staff = true, Fresh = true)]
    public static object Requests(Call c)
    {
        Cloud(c); using var g = c.S.Db.Global(); var site = Site(g);
        var show = c.Str("stato") is "done" or "archived" ? c.Str("stato") : "new";
        var rows = g.Query<Trialrequest>("SELECT * FROM core_trialrequest WHERE status=@s ORDER BY created DESC LIMIT 200", new { s = show }).ToList();
        var clients = g.Query<Tenant>("SELECT * FROM core_tenant WHERE id=ANY(@ids)", new { ids = rows.Where(r => r.ClientId.HasValue).Select(r => r.ClientId.Value).ToArray() }).ToDictionary(t => t.Id);
        long Count(string k) => g.Scalar<long>("SELECT count(*) FROM core_trialrequest WHERE status=@k", new { k });
        return new
        {
            stato = show, attivo = site.TrialEnabled, destinatario = site.TrialEmail != "" ? site.TrialEmail : site.SupportEmail, conteggi = new { nuove = Count("new"), attivate = Count("done"), archiviate = Count("archived") },
            righe = rows.Select(r =>
            {
                var d = c.S.Crypto.Reveal(r.Pii, "trial:" + r.Id) as JsonObject ?? new JsonObject(); var t = r.ClientId.HasValue && clients.TryGetValue(r.ClientId.Value, out var x) ? x : null;
                return new
                {
                    id = r.Id, nome = d.Str("first_name"), cognome = d.Str("last_name"), quando = Local(r.Created, "dd/MM/yyyy HH:mm"), avvisata = r.Notified.HasValue, tipo = TypeLabel(r.Preset), attivita = d.Str("business"),
                    email = d.Str("email"), telefono = d.Str("phone"), messaggio = d.Str("message"), stato = r.Status, cliente = t == null ? null : new { slug = t.Slug, nome = t.Name },
                };
            }),
        };
    }

    [Rpc("richieste.azione", Tenant = false, Staff = true, Fresh = true)]
    public static object RequestAction(Call c)
    {
        Cloud(c); using var g = c.S.Db.Global(); var id = c.NeedId("id");
        if (g.Scalar<long>("SELECT count(*) FROM core_trialrequest WHERE id=@id", new { id }) == 0) throw new KeyNotFoundException();
        string text;
        switch (c.Str("azione"))
        {
            case "archive": g.Execute("UPDATE core_trialrequest SET status='archived' WHERE id=@id", new { id }); text = "Richiesta archiviata."; break;
            case "restore": g.Execute("UPDATE core_trialrequest SET status='new' WHERE id=@id", new { id }); text = "Richiesta riportata tra quelle da gestire."; break;
            case "delete": g.Execute("DELETE FROM core_trialrequest WHERE id=@id", new { id }); text = "Richiesta eliminata, con i dati di chi l'ha inviata."; break;
            default: throw new KeyNotFoundException();
        }
        g.Commit(); return new { testo = text };
    }

    /// <summary>trial.notify_pending: the email to the agency for each new request, from the worker. After a failure it
    /// waits 2, 4, 8, 16 minutes, so an outage does not use up the attempts.</summary>
    public static int Notify(Server s)
    {
        if (s.Edition != "cloud") return 0;
        using var g = s.Db.Global(); var site = Site(g); var config = Mail.Agency(g, s.Crypto); var to = site.TrialEmail != "" ? site.TrialEmail : site.SupportEmail; var sent = 0; var now = DateTime.UtcNow;
        if (config == null || to == "") return 0;
        foreach (var r in g.Query<Trialrequest>("SELECT * FROM core_trialrequest WHERE notified IS NULL AND attempts<5 AND created>=@a ORDER BY created LIMIT 20", new { a = now.AddDays(-7) }).ToList())
        {
            if (r.Attempts > 0 && now < r.Created.AddMinutes(Math.Pow(2, r.Attempts))) continue;
            var d = s.Crypto.Reveal(r.Pii, "trial:" + r.Id) as JsonObject ?? new JsonObject(); string Or(string k, string none) => d.Str(k) != "" ? d.Str(k) : none;
            var body = "Nuova richiesta di prova dal portale " + site.Name + "\n\n" + $"Nome: {d.Str("first_name")} {d.Str("last_name")}\nEmail: {d.Str("email")}\nTelefono: {Or("phone", "non indicato")}\n"
                + $"Attività: {Or("business", "non indicata")}\nTipo di attività: {TypeLabel(r.Preset)}\n" + (d.Str("message") != "" ? $"Messaggio: {d.Str("message")}\n" : "")
                + "\nPer attivarla: apri TD Booking Suite, Richieste di prova\n";
            try
            {
                Mail.Send(config, new JsonObject { ["to"] = to, ["subject"] = "Richiesta di prova: " + Or("business", d.Str("first_name") + " " + d.Str("last_name")), ["body"] = body },
                    $"<trial-{r.Id}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}@{s.PublicOrigin.Split("//")[^1]}>");
                g.Execute("UPDATE core_trialrequest SET notified=@n, attempts=attempts+1 WHERE id=@id", new { n = DateTime.UtcNow, id = r.Id }); sent++;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or MailResponse or MailApiError or FormatException or InvalidOperationException or System.Security.Authentication.AuthenticationException)
            { g.Execute("UPDATE core_trialrequest SET attempts=attempts+1 WHERE id=@id", new { id = r.Id }); }
        }
        g.Commit(); return sent;
    }

    // ---------- Prezzi e versioni, Perché sceglierci
    static readonly (string Id, string Label)[] EDITIONS = { ("cloud", "Cloud, in abbonamento"), ("private", "Private, sul server del cliente") };
    static List<Fld> PricingFields(Sitesettings s) => new()
    {
        new() { Nome = "pricing_enabled", Etichetta = "Mostra la pagina pubblica Prezzi e versioni", Tipo = "check", Valore = s.PricingEnabled, Largo = true },
        new() { Nome = "pricing_title", Etichetta = "Titolo", Valore = s.PricingTitle, Max = 120 },
        new() { Nome = "pricing_note", Etichetta = "Nota sotto i prezzi", Valore = s.PricingNote, Max = 200 },
        new() { Nome = "pricing_intro", Etichetta = "Testo sotto il titolo", Tipo = "area", Valore = s.PricingIntro, Max = 300, Largo = true },
        new() { Nome = "pricing_highlights", Etichetta = "Punti di forza, uno per riga: Titolo | testo", Tipo = "area", Valore = s.PricingHighlights, Largo = true },
        new() { Nome = "pricing_faq", Etichetta = "Domande frequenti, una per riga: Domanda | risposta", Tipo = "area", Valore = s.PricingFaq, Largo = true },
    };
    static List<Fld> WhyFields(Sitesettings s) => new()
    {
        new() { Nome = "why_enabled", Etichetta = "Mostra la pagina pubblica Perché sceglierci", Tipo = "check", Valore = s.WhyEnabled, Largo = true },
        new() { Nome = "why_title", Etichetta = "Titolo", Valore = s.WhyTitle, Max = 120 },
        new() { Nome = "why_intro", Etichetta = "Testo sotto il titolo", Tipo = "area", Valore = s.WhyIntro, Max = 300, Largo = true },
        new() { Nome = "why_points", Etichetta = "Motivi, uno per riga: Titolo | testo", Tipo = "area", Valore = s.WhyPoints, Largo = true },
        new() { Nome = "why_columns", Etichetta = "Colonne del confronto, separate da |: la prima è la tua", Valore = s.WhyColumns, Max = 200, Largo = true },
        new() { Nome = "why_rows", Etichetta = "Righe del confronto, una per riga: Voce | tua colonna | seconda colonna | terza colonna", Tipo = "area", Valore = s.WhyRows, Largo = true },
        new() { Nome = "why_note", Etichetta = "Nota sotto il confronto", Valore = s.WhyNote, Max = 300, Largo = true },
    };

    [Rpc("prezzi", Tenant = false, Staff = true, Fresh = true)]
    public static object Pricing(Call c)
    {
        Cloud(c); using var g = c.S.Db.Global(); var s = Site(g); var origin = c.PublicOrigin.TrimEnd('/');
        return new
        {
            attiva = s.PricingEnabled, percheAttiva = s.WhyEnabled, pagina = origin + "/prezzi/", perche = origin + "/perche-sceglierci/", campi = PricingFields(s), campiPerche = WhyFields(s),
            piani = g.Query<Pricingplan>("SELECT * FROM core_pricingplan ORDER BY edition, \"order\", name").Select(p => new
            {
                id = p.Id, nome = p.Name, etichetta = p.Badge, descrizione = p.Description.Length > 70 ? p.Description[..69] + "…" : p.Description, versione = EDITIONS.First(e => e.Id == p.Edition).Label,
                prezzo = (p.Price + " " + p.Period).Trim(), visibile = p.Active,
            }),
        };
    }

    [Rpc("prezzi.salva", Tenant = false, Staff = true, Fresh = true)]
    public static object PricingSave(Call c)
    {
        Cloud(c); using var g = c.S.Db.Global(); var s = Site(g); var p = new Posted(c, c.Obj("valori") ?? new JsonObject()); var why = c.Str("pagina") == "why"; string label;
        if (why)
        {
            s.WhyEnabled = p.Bool("why_enabled"); s.WhyTitle = p.Text("why_title", true, 120); s.WhyIntro = p.Text("why_intro", true, 300); s.WhyPoints = p.Text("why_points");
            s.WhyColumns = p.Text("why_columns", true, 200); s.WhyRows = p.Text("why_rows"); s.WhyNote = p.Text("why_note", false, 300); label = "Perché sceglierci";
        }
        else
        {
            s.PricingEnabled = p.Bool("pricing_enabled"); s.PricingTitle = p.Text("pricing_title", true, 120); s.PricingIntro = p.Text("pricing_intro", true, 300); s.PricingHighlights = p.Text("pricing_highlights");
            s.PricingFaq = p.Text("pricing_faq"); s.PricingNote = p.Text("pricing_note", false, 200); label = "Prezzi e versioni";
        }
        if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
        s.Updated = DateTime.UtcNow;
        g.Update(s, "why_enabled", "why_title", "why_intro", "why_points", "why_columns", "why_rows", "why_note", "pricing_enabled", "pricing_title", "pricing_intro", "pricing_highlights", "pricing_faq", "pricing_note", "updated"); g.Commit();
        return new { testo = "Pagina " + label + " salvata" + ((why ? s.WhyEnabled : s.PricingEnabled) ? "" : ": è spenta, nessuno la vede") + "." };
    }

    [Rpc("piano", Tenant = false, Staff = true, Fresh = true)]
    public static object Plan(Call c)
    {
        Cloud(c); using var g = c.S.Db.Global();
        var p = c.Int("id") is int id and > 0 ? g.One<Pricingplan>("SELECT * FROM core_pricingplan WHERE id=@id", new { id }) ?? throw new KeyNotFoundException() : null; var x = p ?? new Pricingplan();
        return new
        {
            id = p?.Id ?? 0, nome = p?.Name ?? "",
            campi = new List<Fld>
            {
                new() { Nome = "edition", Etichetta = "Versione", Tipo = "select", Valore = x.Edition, Scelte = EDITIONS.Select(e => (object)new { id = e.Id, nome = e.Label }) },
                new() { Nome = "name", Etichetta = "Nome del piano", Valore = x.Name, Max = 60 },
                new() { Nome = "badge", Etichetta = "Etichetta in evidenza, per esempio Il più scelto", Valore = x.Badge, Max = 40 },
                new() { Nome = "price", Etichetta = "Prezzo, per esempio 29 € oppure Su misura", Valore = x.Price, Max = 30 },
                new() { Nome = "period", Etichetta = "Periodo, per esempio al mese", Valore = x.Period, Max = 40 },
                new() { Nome = "free_months", Etichetta = "Mesi gratuiti", Tipo = "number", Valore = (int)x.FreeMonths, Aiuto = "Per esempio 2: sulla scheda compare I primi 2 mesi sono gratis. Lascia 0 se non ce ne sono" },
                new() { Nome = "price_note", Etichetta = "Nota sotto il prezzo", Valore = x.PriceNote, Max = 120 },
                new() { Nome = "description", Etichetta = "Descrizione breve", Tipo = "area", Valore = x.Description, Max = 240, Largo = true },
                new() { Nome = "features", Etichetta = "Cosa comprende, una voce per riga", Tipo = "area", Valore = x.Features, Largo = true },
                new() { Nome = "cta_label", Etichetta = "Testo del pulsante", Valore = x.CtaLabel, Max = 40 },
                new() { Nome = "cta_url", Etichetta = "Collegamento del pulsante (vuoto: richiesta di prova o email di assistenza)", Valore = x.CtaUrl, Max = 300, Largo = true },
                new() { Nome = "highlighted", Etichetta = "Metti in evidenza questo piano", Tipo = "check", Valore = x.Highlighted, Largo = true },
                new() { Nome = "active", Etichetta = "Visibile nella pagina", Tipo = "check", Valore = x.Active, Largo = true },
                new() { Nome = "order", Etichetta = "Ordine (i numeri più bassi vengono prima)", Tipo = "number", Valore = (int)x.Order },
            },
        };
    }

    [Rpc("piano.salva", Tenant = false, Staff = true, Fresh = true)]
    public static object PlanSave(Call c)
    {
        Cloud(c); using var g = c.S.Db.Global(); var p = new Posted(c, c.Obj("valori") ?? new JsonObject());
        var x = c.Int("id") is int id and > 0 ? g.One<Pricingplan>("SELECT * FROM core_pricingplan WHERE id=@id", new { id }) ?? throw new KeyNotFoundException() : new Pricingplan();
        if (c.Str("azione") == "delete") { if (x.Id == 0) throw new KeyNotFoundException(); g.Execute("DELETE FROM core_pricingplan WHERE id=@id", new { id = x.Id }); g.Commit(); return new { testo = "Piano eliminato." }; }
        x.Edition = p.Text("edition", true); if (x.Edition != "" && !EDITIONS.Any(e => e.Id == x.Edition)) p.Error("edition", "Scegli un'opzione valida.");
        x.Name = p.Text("name", true, 60); x.Badge = p.Text("badge", false, 40); x.Price = p.Text("price", true, 30); x.Period = p.Text("period", false, 40); x.PriceNote = p.Text("price_note", false, 120);
        x.Description = p.Text("description", false, 240); x.Features = p.Text("features"); x.CtaLabel = p.Text("cta_label", false, 40);
        // pages.safe_link: an address the button may open, nothing that could carry a script
        var link = p.Text("cta_url", false, 300);
        if (link != "")
        {
            if (!(link.StartsWith("https://") || link.StartsWith("http://") || link.StartsWith("mailto:") || link.StartsWith("tel:") || link.StartsWith('/') && !link.StartsWith("//"))) p.Error("cta_url", "Usa un indirizzo che inizia con https://, mailto:, tel: oppure /");
            else if (link.Any(ch => "\"'<> \\".Contains(ch))) p.Error("cta_url", "Il collegamento contiene caratteri non validi.");
        }
        x.CtaUrl = link; x.Highlighted = p.Bool("highlighted"); x.Active = p.Bool("active"); x.Order = (short)(p.Int("order", true, 0, 32767) ?? 10);
        var free = p.Int("free_months", false, 0, 32767) ?? 0; if (free > 24) p.Error("free_months", "Al massimo 24 mesi."); x.FreeMonths = (short)free;
        if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
        if (x.Id == 0) g.Execute("INSERT INTO core_pricingplan (edition, name, badge, price, period, price_note, description, features, cta_label, cta_url, highlighted, active, \"order\", free_months) VALUES (@Edition, @Name, @Badge, @Price, @Period, @PriceNote, @Description, @Features, @CtaLabel, @CtaUrl, @Highlighted, @Active, @Order, @FreeMonths)", x);
        else g.Update(x, "edition", "name", "badge", "price", "period", "price_note", "description", "features", "cta_label", "cta_url", "highlighted", "active", "order", "free_months");
        g.Commit(); return new { testo = "Piano salvato." };
    }

    // ---------- Plugin WordPress
    [Rpc("plugin", Tenant = false, Staff = true)]
    public static object Plugin(Call c)
    {
        Cloud(c); using var g = c.S.Db.Global();
        return new
        {
            versione = Impostazioni.PluginVersion(), portale = c.PublicOrigin.TrimEnd('/'),
            attivita = g.Query<Tenant>("SELECT * FROM core_tenant ORDER BY name").Select(t => new { slug = t.Slug, nome = t.Name, siti = (JsonNode.Parse(string.IsNullOrEmpty(t.EmbedOrigins) ? "[]" : t.EmbedOrigins) as JsonArray ?? new JsonArray()).Select(o => o.ToString()) }),
        };
    }
}
