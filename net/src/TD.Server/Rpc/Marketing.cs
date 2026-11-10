using System.Text;
using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.marketing, campaign_edit, campaign_preview and export: the campaigns to the subscribers and the
/// CSV files (subscribers, clients, bookings).</summary>
public static class Marketing
{
    [Rpc("marketing", Perm = "campaign.manage")]
    public static object Page(Call c)
    {
        var x = c.C; var t = c.Tenant;
        if (!(t.MarketingEnabled || t.ExportEnabled)) throw new UnauthorizedAccessException("Il marketing lo attiva l'amministratore.");
        var names = x.S.Query<Campaign>("SELECT * FROM core_campaign ORDER BY created DESC LIMIT 100").ToList();
        return new
        {
            attivo = t.MarketingEnabled, esporta = t.ExportEnabled, richiesto = t.MarketingRequested, iscritti = new Campaigns(x).Contacts().Count,
            campagne = names.Select(k => new { id = k.Id, nome = k.Name, oggetto = k.Subject, stato = k.Status }),
            esiti = x.S.Query<Delivery>("SELECT * FROM core_delivery WHERE channel<>'webhook' ORDER BY created DESC LIMIT 30").Select(d => new
            {
                nome = d.CampaignId is Guid id ? names.FirstOrDefault(k => k.Id == id)?.Name ?? x.S.Scalar<string>("SELECT name FROM core_campaign WHERE id=@i", new { i = id }) : "Comunicazione di servizio",
                quando = x.Cal.ToLocal(d.Created).ToString("dd/MM HH:mm"), stato = d.Status,
            }),
        };
    }

    [Rpc("marketing.richiedi", Perm = "campaign.manage")]
    public static object Ask(Call c)
    {
        var x = c.C; if (c.Tenant.MarketingEnabled) return new { };
        x.S.Execute("UPDATE core_tenant SET marketing_requested=true WHERE id=@t", new { t = c.Tenant.Id }); x.Audit("marketing.requested");
        return new { testo = "Richiesta registrata per l'amministratore." };
    }

    static Campaign Draft(Call c, Guid? id)
    {
        if (!c.Tenant.MarketingEnabled) throw new UnauthorizedAccessException("Richiedi l'abilitazione marketing.");
        var k = id.HasValue ? c.C.S.Get<Campaign>(id) ?? throw new KeyNotFoundException() : null;
        if (k != null && k.Status != "draft") throw new UnauthorizedAccessException("Una campagna accodata non è modificabile.");
        return k;
    }

    [Rpc("campagna.modulo", Perm = "campaign.manage")]
    public static object Form(Call c)
    {
        var x = c.C; var k = Draft(c, c.Id("id")); var f = Json.Obj(k?.Filters);
        return new
        {
            nome = c.Tenant.Name, titolo = "Crea una comunicazione", descrizione = "Messaggio semplice, senza HTML. Invio solo a contatti con consenso confermato.",
            campi = new List<Fld>
            {
                new() { Nome = "name", Etichetta = "Nome", Valore = k?.Name ?? "", Max = 100 }, new() { Nome = "subject", Etichetta = "Oggetto", Valore = k?.Subject ?? "", Max = 180 },
                new() { Nome = "body", Etichetta = "Messaggio", Tipo = "area", Valore = k?.Body ?? "", Largo = true },
                new() { Nome = "scheduled", Etichetta = "Invio programmato", Tipo = "datetime", Valore = k?.Scheduled is DateTime s ? x.Cal.ToLocal(s).ToString("yyyy-MM-dd'T'HH:mm") : "" },
                new() { Nome = "tag", Etichetta = "Solo i clienti con l'etichetta", Tipo = "select", Valore = f.Str("tag"), Scelte = new object[] { new { id = "", nome = "Tutti" } }.Concat(new Clients(x).KnownTags().Select(t => (object)new { id = t, nome = t })) },
                new() { Nome = "vip", Etichetta = "Solo i clienti VIP", Tipo = "check", Valore = Json.Truthy(f["vip"]) },
                new() { Nome = "after", Etichetta = "Ultima visita dal", Tipo = "date", Valore = f.Str("after") },
                new() { Nome = "before", Etichetta = "Nessuna visita dal", Tipo = "date", Valore = f.Str("before"), Aiuto = "Per esempio per chi non torna da sei mesi" },
                new() { Nome = "visits", Etichetta = "Almeno quante visite", Tipo = "number", Valore = f["visits"] != null ? f.Str("visits") : "" },
            },
        };
    }

    [Rpc("campagna.salva", Perm = "campaign.manage")]
    public static object Save(Call c)
    {
        var x = c.C; var k = Draft(c, c.Id("id")) ?? new Campaign { TenantId = c.Tenant.Id, Created = x.Now }; var p = new Posted(c, c.Obj("valori")); var isNew = c.Id("id") == null;
        k.Name = p.Text("name", true, 100); k.Subject = p.Text("subject", true, 180); if (k.Subject.Contains('\n') || k.Subject.Contains('\r')) p.Error("subject", "Oggetto non valido.");
        k.Body = p.Text("body", true); if (k.Body.Length > 20000) p.Error("body", "Messaggio troppo lungo.");
        k.Scheduled = p.Moment("scheduled"); var tag = p.Raw("tag"); if (tag != "" && !new Clients(x).KnownTags().Contains(tag)) p.Error("tag", $"Scegli un'opzione valida. {tag} non compare tra quelle disponibili.");
        var after = p.Date("after"); var before = p.Date("before"); var visits = p.Int("visits", false, 1, 1000);
        if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
        var f = new JsonObject(); if (tag != "") f["tag"] = tag; if (p.Bool("vip")) f["vip"] = true; if (after.HasValue) f["after"] = after.Value.ToString("yyyy-MM-dd"); if (before.HasValue) f["before"] = before.Value.ToString("yyyy-MM-dd"); if (visits is > 0) f["visits"] = visits;
        k.Filters = Json.Dump(f);
        if (isNew) x.S.Insert(k); else x.S.Update(k);
        x.Audit("campaign.saved", k.Id);
        return new { id = k.Id };
    }

    [Rpc("campagna", Perm = "campaign.manage")]
    public static object Preview(Call c)
    {
        var x = c.C; var k = x.S.Get<Campaign>(c.NeedId("id")) ?? throw new KeyNotFoundException();
        return new { id = k.Id, nome = k.Name, oggetto = k.Subject, testo = k.Body, stato = k.Status, programmata = k.Scheduled is DateTime s ? x.Cal.ToLocal(s).ToString("dd/MM HH:mm") : "", idonei = new Campaigns(x).Contacts(Json.Obj(k.Filters)).Count };
    }

    [Rpc("campagna.azione", Perm = "campaign.manage")]
    public static object Action(Call c)
    {
        var x = c.C; var k = x.S.Get<Campaign>(c.NeedId("id")) ?? throw new KeyNotFoundException(); var action = c.Str("azione");
        switch (action)
        {
            case "queue": new Campaigns(x).Enqueue(k); return new { testo = "Campagna accodata. Lo stato degli invii è visibile nel marketing." };
            case "test":
                {
                    if (!c.Tenant.MarketingEnabled) throw new BookingException("Marketing disabilitato.");
                    var email = c.S.Identity.Email(c.User); if (email == "") throw new BookingException("Il tuo account non ha un'email: la prova non ha dove arrivare.");
                    new Bookings(x).Queue(new JsonObject { ["to"] = email, ["subject"] = "[PROVA] " + k.Subject, ["body"] = k.Body }); x.Audit("campaign.test", k.Id);
                    return new { testo = "Prova accodata al tuo indirizzo di accesso." };
                }
            case "pause" or "resume":
                if (!c.Tenant.MarketingEnabled) throw new BookingException("Marketing disabilitato.");
                k.Status = action == "pause" ? "paused" : "queued"; x.S.Update(k, "status"); x.Audit("campaign." + action, k.Id);
                return new { };
        }
        throw new BookingException("Operazione non valida.");
    }

    // ---------- export: the CSV files
    /// <summary>csv_cell: in a sheet, what begins like a formula is written as text.</summary>
    static string Cell(string s, string profile)
    {
        s ??= "";
        if (profile == "sheet" && (s.TrimStart().Length > 0 && "=+-@".Contains(s.TrimStart()[0]) || s.Length > 0 && "\t\r\n".Contains(s[0]))) s = "'" + s;
        return s.Any(ch => ch is ',' or '"' or '\r' or '\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
    /// <summary>json.dumps(value, ensure_ascii=False), as Python spaces it.</summary>
    static string PyJson(JsonNode n) => n switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(k => System.Text.Json.JsonSerializer.Serialize(k.Key, TD.Core.Crypto.Json) + ": " + PyJson(k.Value))) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyJson)) + "]",
        _ => n.ToJsonString(TD.Core.Crypto.Json),
    };

    [Rpc("esporta")]
    public static IResult Export(Call c)
    {
        var kind = c.Str("kind", "contacts"); var profile = c.Str("profile", "sheet"); var x = c.C;
        c.Require(kind == "bookings" ? "export.operational" : "export.marketing");
        if (profile is not ("sheet" or "generic" or "brevo" or "mailchimp")) throw new BookingException("Formato non valido.");
        // vendor files are only for eligible recipients: never a full address book with a "consent" column
        if (kind == "marketing" && !c.Tenant.ExportEnabled) throw new UnauthorizedAccessException("L'esportazione degli iscritti la attiva l'amministratore.");
        if (profile is "brevo" or "mailchimp" && kind != "marketing") throw new BookingException("I formati Brevo e Mailchimp esportano solo i destinatari con consenso confermato (kind=marketing).");
        var rows = new List<string[]>(); string[] header;
        if (kind == "bookings")
        {
            var today = x.Today; DateOnly first, last;
            if (!DateOnly.TryParseExact(c.Str("from", today.ToString("yyyy-MM-dd")), "yyyy-MM-dd", out first) || !DateOnly.TryParseExact(c.Str("to", first.AddDays(29).ToString("yyyy-MM-dd")), "yyyy-MM-dd", out last) || last.DayNumber - first.DayNumber is < 0 or > 366)
                throw new BookingException("Intervallo non valido.");
            var (vis, args) = Permissions.VisibleBookings(c.Member, c.Perms, "b"); var p = new Dapper.DynamicParameters(args); p.Add("a", x.Cal.Local(first, TimeSpan.Zero)); p.Add("z", x.Cal.Local(last.AddDays(1), TimeSpan.Zero));
            var found = x.S.Query<Booking>($"SELECT b.* FROM core_booking b WHERE b.start<@z AND b.\"end\">@a AND ({vis}) ORDER BY b.start", p).ToList();
            if (found.Count > 50000) throw new BookingException("Restringi l'intervallo.");
            var notes = c.Can("notes.read"); var bk = new Bookings(x);
            header = new[] { "ID", "Nome", "Cognome", "Email", "Telefono", "Inizio", "Fine", "Stato", "Servizio", "Sede", "Risorsa", "Note cliente", "Note interne", "Campi aggiuntivi", "Intolleranze", "Origine", "Fonte" };
            foreach (var b in found)
            {
                var d = bk.Data(b);
                rows.Add(new[]
                {
                    b.Id.ToString(), d.Str("first_name"), d.Str("last_name"), d.Str("email"), d.Str("phone"), bk.Iso(b.Start), bk.Iso(b.End), b.Status, b.Snap.Str("service"), b.Snap.Str("location"), b.Snap.Str("resource"),
                    notes ? d.Str("notes") : "", notes ? d.Str("internal_notes") : "", PyJson(d["answers"] ?? new JsonObject()), notes ? string.Join(", ", (d["allergies"] as JsonArray ?? new JsonArray()).Select(a => a?.ToString())) : "",
                    Reports.ORIGIN.GetValueOrDefault(b.Origin ?? "", b.Origin ?? ""), b.Source,
                });
            }
        }
        else if (kind is "contacts" or "marketing")
        {
            var found = kind == "marketing" ? new Campaigns(x).Contacts() : x.S.Query<Contact>("SELECT * FROM core_contact WHERE email_index NOT LIKE 'erased:%' ORDER BY created").ToList();
            if (found.Count > 50000) throw new BookingException("Richiedi un export di portabilità da console per oltre 50.000 contatti.");
            header = profile == "brevo" ? new[] { "EMAIL", "FIRSTNAME", "LASTNAME", "SMS", "CONSENT", "CONSENT_DATE" } : profile == "mailchimp" ? new[] { "Email Address", "First Name", "Last Name", "Phone Number", "Consent", "Consent Date" }
                : new[] { "email", "first_name", "last_name", "phone", "consent", "consent_date" };
            var cl = new Clients(x);
            foreach (var k in found) { var d = cl.Data(k); rows.Add(new[] { d.Str("email"), d.Str("first_name"), d.Str("last_name"), d.Str("phone"), k.Consent, k.ConsentAt is DateTime at ? Times.PyIso(at) : "" }); }
        }
        else throw new BookingException("Export non valido.");
        var sb = new StringBuilder("﻿").Append(string.Join(",", header.Select(h => Cell(h, "")))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(",", r.Select(v => Cell(v, profile)))).Append("\r\n");
        x.Audit("export." + kind, null, new { detail_count = rows.Count, profile });
        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"td-booking-suite-{kind}.csv");
    }
}
