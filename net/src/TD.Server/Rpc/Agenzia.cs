using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>core/agency.py: Configurazione del programma: a dashboard with how every business uses the program, the list
/// of the businesses and one card each with logo, company data, contacts and internal notes.
/// Usage numbers are counts only: names, contacts and messages of a business's customers never leave its pages.
/// The card is the administrator's own record of the business, sealed with the business's key.</summary>
public static class Agenzia
{
    const int WEEKS = 12, MAX_CONTACTS = 20; const string LABEL = "client-profile";
    static readonly (string Key, string Label, int Limit)[] FIELDS =
    {
        ("company", "Ragione sociale", 160), ("vat", "Partita IVA", 20), ("tax_code", "Codice fiscale", 20), ("sdi", "Codice destinatario SDI", 7), ("pec", "PEC", 160), ("email", "Email dell'attività", 160),
        ("phone", "Telefono", 40), ("address", "Indirizzo", 200), ("city", "Città", 80), ("sector", "Settore", 80), ("since", "Cliente dal", 10),
    };
    static readonly (string Key, string Label, int Limit)[] CONTACT_FIELDS = { ("name", "Nome e cognome", 100), ("role", "Ruolo", 80), ("email", "Email", 160), ("phone", "Telefono", 40), ("notes", "Note", 300) };
    static readonly CultureInfo IT = CultureInfo.GetCultureInfo("it-IT");

    static void Staff(Call c) { if (!c.User.IsStaff) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione."); }
    static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
    static string Local(DateTime? utc, string format) => utc.HasValue ? Local(utc.Value).ToString(format) : "";

    // the administrator's record of the business
    static JsonObject LoadProfile(Scope s, Crypto crypto, Tenant t)
    {
        var row = s.One<Clientprofile>("SELECT * FROM core_clientprofile WHERE tenant_id=@t", new { t = t.Id });
        var data = row != null && row.Data != "" ? crypto.Decrypt(t.Id, t.WrappedKey, row.Data, LABEL) as JsonObject ?? new JsonObject() : new JsonObject();
        if (data["contacts"] is not JsonArray) data["contacts"] = new JsonArray();
        if (data["notes"] == null) data["notes"] = "";
        foreach (var (key, _, _) in FIELDS) if (data[key] == null) data[key] = "";
        return data;
    }
    static void StoreProfile(Scope s, Crypto crypto, Tenant t, JsonObject data)
    {
        var sealed_ = crypto.Encrypt(t.Id, t.WrappedKey, data, LABEL);
        if (s.Execute("UPDATE core_clientprofile SET data=@d, updated=now() WHERE tenant_id=@t", new { d = sealed_, t = t.Id }) == 0)
            s.Execute("INSERT INTO core_clientprofile (id, tenant_id, data, updated) VALUES (@id, @t, @d, now())", new { id = Guid.NewGuid(), t = t.Id, d = sealed_ });
    }
    static JsonObject Primary(JsonObject profile)
    {
        var contacts = (JsonArray)profile["contacts"];
        return contacts.OfType<JsonObject>().FirstOrDefault(x => Json.Truthy(x["primary"])) ?? contacts.OfType<JsonObject>().FirstOrDefault();
    }
    static string CleanText(Call c, string key, int limit, string label)
    {
        var value = c.Str(key).Trim();
        if (value.Length > limit) throw new BookingException($"{label}: massimo {limit} caratteri.");
        return value;
    }

    // usage: counts only
    sealed class Usage
    {
        public long Bookings30, BookingsPrev, Upcoming, BookingsTotal, Contacts, Sent30, Queued, Issues, Locations, Services, Users;
        public DateTime? LastBooking, LastLogin; public int[] Weeks = new int[WEEKS]; public DateOnly FirstWeek; public int? Trend;
    }
    static DateOnly WeekStart(DateOnly day) => day.AddDays(-TD.Core.Domain.Calendar.Weekday(day));
    static Usage UsageOf(Scope s, Tenant t, DateTime now)
    {
        var since = now.AddDays(-30); var before = now.AddDays(-60); var first = WeekStart(DateOnly.FromDateTime(Local(now))).AddDays(-7 * (WEEKS - 1));
        var u = new Usage
        {
            Bookings30 = s.Scalar<long>("SELECT count(*) FROM core_booking WHERE created>=@since", new { since }), BookingsPrev = s.Scalar<long>("SELECT count(*) FROM core_booking WHERE created>=@before AND created<@since", new { before, since }),
            Upcoming = s.Scalar<long>("SELECT count(*) FROM core_booking WHERE status = ANY(@m) AND start>=@now", new { m = Bookings.MOVABLE, now }), BookingsTotal = s.Scalar<long>("SELECT count(*) FROM core_booking"),
            LastBooking = s.Scalar<DateTime?>("SELECT max(created) FROM core_booking"), Contacts = s.Scalar<long>("SELECT count(*) FROM core_contact"),
            Sent30 = s.Scalar<long>("SELECT count(*) FROM core_delivery WHERE status='accepted' AND sent_at>=@since", new { since }), Queued = s.Scalar<long>("SELECT count(*) FROM core_delivery WHERE status='pending'"),
            Issues = s.Scalar<long>("SELECT count(*) FROM core_delivery WHERE status IN ('failed','unknown')"), Locations = s.Scalar<long>("SELECT count(*) FROM core_location WHERE active"),
            Services = s.Scalar<long>("SELECT count(*) FROM core_service WHERE active"), FirstWeek = first,
        };
        foreach (var created in s.Query<DateTime>("SELECT created FROM core_booking WHERE created>=@from", new { from = first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) }))
        {
            var i = (DateOnly.FromDateTime(created).DayNumber - first.DayNumber) / 7;
            if (i >= 0 && i < WEEKS) u.Weeks[i]++;
        }
        u.Users = s.Scalar<long>("SELECT count(*) FROM core_membership WHERE tenant_id=@t", new { t = t.Id });
        u.LastLogin = s.Scalar<DateTime?>("SELECT max(u.last_login) FROM core_membership m JOIN core_user u ON u.id=m.user_id WHERE m.tenant_id=@t", new { t = t.Id });
        u.Trend = u.BookingsPrev > 0 ? (int)Math.Round((u.Bookings30 - u.BookingsPrev) * 100.0 / u.BookingsPrev, MidpointRounding.ToEven) : null;
        return u;
    }

    /// <summary>What the administrator should look at, most urgent first.</summary>
    static List<(string Level, string Text)> Attention(Tenant t, Usage u, DateTime now, string edition)
    {
        var today = DateOnly.FromDateTime(Local(now)); var items = new List<(string, string)>();  // the local day: in UTC the night after midnight is still yesterday
        if (edition != "private")
        {
            if (t.Status == "suspended") items.Add(("danger", "Servizio sospeso"));
            else if (t.Status == "trial" && t.TrialUntil.HasValue)
            {
                var until = DateOnly.FromDateTime(t.TrialUntil.Value); var days = until.DayNumber - today.DayNumber;
                if (days < 0) items.Add(("danger", "Prova scaduta il " + until.ToString("dd/MM/yyyy")));
                else if (days <= 14) items.Add(("warn", "Prova in scadenza " + (days == 0 ? "oggi" : days == 1 ? "domani" : $"tra {days} giorni")));
            }
        }
        if (t.Maintenance) items.Add(("warn", "In manutenzione tecnica"));
        if (u.Issues > 0) items.Add(("danger", $"{u.Issues} invi{(u.Issues == 1 ? "o" : "i")} email da verificare"));
        if (t.MarketingRequested && !t.MarketingEnabled) items.Add(("info", "Ha chiesto di attivare il marketing"));
        // the retention the worker runs every day: failed, or stopped with the worker
        if (!string.IsNullOrEmpty(t.RetentionError)) items.Add(("danger", "Pulizia dei dati non riuscita (" + t.RetentionError + "): vedi il log del worker"));
        else if (now - (t.RetentionAt ?? t.Created) > TimeSpan.FromDays(2))
            items.Add(("warn", "Pulizia dei dati ferma" + (t.RetentionAt.HasValue ? " dal " + Local(t.RetentionAt, "dd/MM/yyyy") : ": mai eseguita") + ", controlla il worker"));
        if (u.LastLogin == null) items.Add(("info", "Nessuno ha ancora fatto accesso"));
        else if (u.Bookings30 == 0 && (now - t.Created).Days >= 30) items.Add(("info", "Nessuna prenotazione negli ultimi 30 giorni"));
        return items;
    }

    sealed record Row(Tenant T, Usage U, List<(string Level, string Text)> Attention, bool Granted, JsonObject Profile);
    static List<Row> Annotate(Call c, DateTime now, bool profiles)
    {
        List<Tenant> tenants; using (var g = c.S.Db.Global()) tenants = g.Query<Tenant>("SELECT * FROM core_tenant ORDER BY name").ToList();
        var rows = new List<Row>(); var edition = c.S.Edition;
        foreach (var t in tenants)
        {
            using var s = c.S.Db.Tenant(t.Id); var u = UsageOf(s, t, now);
            var granted = s.Scalar<long>("SELECT count(*) FROM core_supportgrant WHERE user_id=@u AND NOT revoked AND expires>@n", new { u = c.User.Id, n = now }) > 0;
            rows.Add(new Row(t, u, Attention(t, u, now, edition), granted, profiles ? LoadProfile(s, c.S.Crypto, t) : null));
        }
        return rows;
    }
    static object Mark(Tenant t) => new { slug = t.Slug, nome = t.Name, colore = t.BrandColor, logo = t.Logo is { Length: > 0 } ? Convert.ToBase64String(t.Logo) : "", stato = t.Status };
    static object Numbers(Usage u, long top) => new
    {
        b30 = u.Bookings30, quota = (int)Math.Round(100.0 * u.Bookings30 / top, MidpointRounding.ToEven), settimane = u.Weeks, arrivo = u.Upcoming, utenti = u.Users,
        accesso = Local(u.LastLogin, "dd/MM/yyyy HH:mm"), accessoGiorno = Local(u.LastLogin, "dd/MM/yyyy"),
    };
    static bool CanCreate(Call c) { using var g = c.S.Db.Global(); return g.Scalar<long>("SELECT count(*) FROM core_tenant") < c.S.TenantLimit; }

    [Rpc("programma", Tenant = false, Staff = true, Fresh = true)]
    public static object Dashboard(Call c)
    {
        var now = DateTime.UtcNow; var rows = Annotate(c, now, false); var edition = c.S.Edition;
        var weeks = Enumerable.Range(0, WEEKS).Select(i => rows.Sum(r => r.U.Weeks[i])).ToArray();
        var first = WeekStart(DateOnly.FromDateTime(Local(now))).AddDays(-7 * (WEEKS - 1));
        long b30 = rows.Sum(r => r.U.Bookings30), prev = rows.Sum(r => r.U.BookingsPrev); var top = Math.Max(1, rows.Select(r => r.U.Bookings30).DefaultIfEmpty(0).Max());
        var rank = new Dictionary<string, int> { ["danger"] = 0, ["warn"] = 1, ["info"] = 2 };
        // one entry per business, the most urgent first
        var alerts = rows.Where(r => r.Attention.Count > 0).Select(r => (Level: r.Attention.Select(a => a.Level).MinBy(l => rank[l]), r)).OrderBy(x => rank[x.Level]).ThenBy(x => x.r.T.Name, StringComparer.Ordinal).ToList();
        var last = rows.Where(r => r.T.RetentionAt.HasValue).Select(r => r.T.RetentionAt).DefaultIfEmpty(null).Max();
        List<object> trials = new();
        if (edition == "cloud")
        {
            using var g = c.S.Db.Global();
            foreach (var r in g.Query<Trialrequest>("SELECT * FROM core_trialrequest WHERE status='new' ORDER BY created DESC LIMIT 4"))
            {
                var d = c.S.Crypto.Reveal(r.Pii, "trial:" + r.Id) as JsonObject ?? new JsonObject();
                trials.Add(new { id = r.Id, attivita = d.Str("business") != "" ? d.Str("business") : d.Str("first_name"), nome = (d.Str("first_name") + " " + d.Str("last_name")).Trim(), tipo = Presets.All.TryGetValue(r.Preset, out var p) ? p.Label : "Appuntamenti generici", quando = Local(r.Created, "dd/MM HH:mm") });
            }
        }
        return new
        {
            oggi = Local(now).ToString("dddd dd MMMM yyyy", IT), puoCreare = CanCreate(c), edizione = edition, quanti = rows.Count, richieste = trials,
            totali = new
            {
                clienti = rows.Count, attivi = rows.Count(r => r.T.Status == "active"), prova = rows.Count(r => r.T.Status == "trial"), sospesi = rows.Count(r => r.T.Status == "suspended"), b30,
                tendenza = prev > 0 ? (int?)Math.Round((b30 - prev) * 100.0 / prev, MidpointRounding.ToEven) : null, arrivo = rows.Sum(r => r.U.Upcoming), inviate = rows.Sum(r => r.U.Sent30),
                pulizia = last.HasValue ? $"ultima il {Local(last, "dd/MM/yyyy")} alle {Local(last, "HH:mm")}, la fa il programma ogni giorno" : "non ancora eseguita, la fa il programma ogni giorno",
            },
            settimane = weeks, primo = first.ToString("yyyy-MM-dd"),
            avvisi = alerts.Take(6).Select(a => new { livello = a.Level, slug = a.r.T.Slug, nome = a.r.T.Name, testi = a.r.Attention.Select(x => x.Text) }), altri = Math.Max(0, alerts.Count - 6),
            classifica = rows.OrderByDescending(r => r.U.Bookings30).ThenBy(r => r.T.Name, StringComparer.Ordinal).Take(8).Select(r => new { segno = Mark(r.T), numeri = Numbers(r.U, top) }),
        };
    }

    [Rpc("programma.clienti", Tenant = false, Staff = true, Fresh = true)]
    public static object Clients(Call c)
    {
        var rows = Annotate(c, DateTime.UtcNow, true); var top = Math.Max(1, rows.Select(r => r.U.Bookings30).DefaultIfEmpty(0).Max());
        return new
        {
            puoCreare = CanCreate(c), edizione = c.S.Edition,
            conteggi = new { tutti = rows.Count, attivi = rows.Count(r => r.T.Status == "active"), prova = rows.Count(r => r.T.Status == "trial"), sospesi = rows.Count(r => r.T.Status == "suspended") },
            clienti = rows.Select(r =>
            {
                var contact = Primary(r.Profile); var p = r.Profile;
                var words = new[] { r.T.Name, r.T.Slug, p.Str("company"), p.Str("city"), p.Str("sector") }.Concat(((JsonArray)p["contacts"]).OfType<JsonObject>().Select(x => x.Str("name")));
                return new
                {
                    segno = Mark(r.T), numeri = Numbers(r.U, top), cerca = Crypto.CaseFold(string.Join(" ", words.Where(w => !string.IsNullOrEmpty(w)))), settore = p.Str("sector"), citta = p.Str("city"),
                    referente = contact == null ? null : new { nome = contact.Str("name"), recapito = contact.Str("phone") != "" ? contact.Str("phone") : contact.Str("email") },
                    avvisi = r.Attention.Select(a => new { livello = a.Level, testo = a.Text }),
                };
            }),
        };
    }

    /// <summary>trial.prefill: the first values of Nuovo cliente for a trial request.</summary>
    static (Trialrequest, JsonObject) Prefill(Call c, Scope g)
    {
        if (c.Id("richiesta") is not Guid id || g.One<Trialrequest>("SELECT * FROM core_trialrequest WHERE id=@id AND status='new'", new { id }) is not { } r) return (null, new JsonObject());
        var d = c.S.Crypto.Reveal(r.Pii, "trial:" + r.Id) as JsonObject ?? new JsonObject(); var site = g.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1") ?? new Sitesettings();
        var person = (d.Str("first_name") + " " + d.Str("last_name")).Trim(); var name = d.Str("business") != "" ? d.Str("business") : person;
        var slug = Regex.Replace(Regex.Replace(new string(name.Normalize(System.Text.NormalizationForm.FormKD).Where(ch => ch < 128).ToArray()).ToLowerInvariant(), @"[^\w\s-]", ""), @"[-\s]+", "-").Trim('-', '_');
        return (r, new JsonObject
        {
            ["name"] = name, ["slug"] = slug.Length > 60 ? slug[..60] : slug, ["status"] = "trial", ["trial_until"] = DateOnly.FromDateTime(DateTime.Now).AddDays(site.TrialDays).ToString("yyyy-MM-dd"),
            ["email"] = d.Str("email"), ["preset"] = r.Preset, ["contact_name"] = person, ["contact_phone"] = d.Str("phone"), ["giorni"] = site.TrialDays,
        });
    }

    /// <summary>site.tenant_defaults: what a new business starts with, from Impostazioni generali.</summary>
    static Tenant Blank(Scope g, JsonObject values)
    {
        var s = g.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1") ?? new Sitesettings();
        return new Tenant
        {
            Name = values.Str("name"), Slug = values.Str("slug"), Timezone = s.DefaultTimezone, BrandColor = s.DefaultBrandColor, PrivacyText = s.DefaultPrivacyText, ConsentText = s.DefaultConsentText, RetentionDays = s.DefaultRetentionDays,
            SmtpDailyLimit = s.DefaultDailyLimit, SmtpHourlyLimit = s.DefaultHourlyLimit, Status = values.Str("status") != "" ? values.Str("status") : "active",
            TrialUntil = DateOnly.TryParse(values.Str("trial_until"), CultureInfo.InvariantCulture, out var until) ? until.ToDateTime(TimeOnly.MinValue) : null,
        };
    }

    [Rpc("programma.nuovo", Tenant = false, Staff = true, Fresh = true)]
    public static object NewClient(Call c)
    {
        using var g = c.S.Db.Global(); var (trial, values) = Prefill(c, g); var cloud = c.S.Edition == "cloud";
        var fields = Impostazioni.TenantFields(Blank(g, values), cloud, true);
        return new
        {
            puoCreare = CanCreate(c), edizione = c.S.Edition, suggerimento = "Per più attività serve una licenza Portale con più attività: la inserisci da Licenza.",
            campi = fields.Where(f => f.Nome is "name" or "slug"), piano = fields.Where(f => f.Nome is not ("name" or "slug")),
            tipi = Presets.Choices.Skip(1).Select(x => new { id = x.Key, nome = x.Label }), richiesta = trial?.Id, valori = values,
        };
    }

    [Rpc("programma.crea", Tenant = false, Staff = true, Fresh = true)]
    public static object Create(Call c)
    {
        var cloud = c.S.Edition == "cloud"; var p = new Posted(c, c.Obj("valori") ?? new JsonObject()); var acc = new Accounts(c.S.Db, c.S.Crypto);
        var email = Crypto.CaseFold(c.Str("email").Trim()); if (email != "") Accounts.ValidateEmail(email);
        var contact = new JsonObject { ["name"] = CleanText(c, "contact_name", 100, "Referente"), ["role"] = "Titolare", ["email"] = email, ["phone"] = CleanText(c, "contact_phone", 40, "Telefono"), ["notes"] = "", ["primary"] = true };
        var slug = p.Text("slug", true, 60); if (slug != "" && !Regex.IsMatch(slug, @"^[-a-zA-Z0-9_]+\z")) p.Error("slug", "Inserisci uno 'slug' valido contenente lettere, cifre, sottolineati o trattini.");
        var store = Impostazioni.ReadTenant(p, cloud);
        var id = Guid.NewGuid(); using var s = c.S.Db.Tenant(id);
        if (slug != "" && s.Scalar<long>("SELECT count(*) FROM core_tenant WHERE slug=@slug", new { slug }) > 0) p.Error("slug", "Tenant con questo Indirizzo breve esiste già.");
        if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
        Guard(c, s);
        var (trial, _) = Prefill(c, s);
        var t = Blank(s, new JsonObject()); t.Id = id; t.Slug = slug; t.Name = p.Text("name"); t.WrappedKey = c.S.Crypto.NewTenantKey(id);
        s.Insert(t); store(s, id);
        t = s.Get<Tenant>(id); var x = new Ctx(s, c.S.Crypto, t) { Actor = c.User.Id, PublicOrigin = c.PublicOrigin, Out = Outbox.Instance, Edition = c.S.Edition };
        Seed(x, c.Str("preset"));
        var (user, created) = acc.AccountFor(s, c.Str("username"), email, id);
        // an account not activated yet stays its owner's: the administrator would get the link or set the password
        if (!created && !Passwords.Usable(user.Password)) throw new BookingException("Questo account non è ancora attivo: deve attivarlo dal suo invito, poi lo aggiungi.");
        // the owner runs the business: services and opening hours included
        s.Execute("INSERT INTO core_membership (tenant_id, user_id, role, can_schedule, locations, resources) VALUES (@t, @u, 'owner', true, '[]'::jsonb, '[]'::jsonb)", new { t = id, u = user.Id });
        var password = c.Str("password"); var link = acc.GrantAccess(s, user, password, c.PublicOrigin);
        x.Audit("tenant.created", id);
        var mail = c.S.Identity.Email(user);
        if (link != "" && mail != "") new Bookings(x).Queue(new JsonObject { ["to"] = mail, ["subject"] = "Il tuo accesso a " + t.Name, ["body"] = "Imposta la password per entrare nel portale:\n" + link });
        if (contact.Str("name") != "")
        {
            var profile = new JsonObject { ["notes"] = "", ["contacts"] = new JsonArray(contact) }; foreach (var (k, _, _) in FIELDS) profile[k] = "";
            profile["since"] = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"); StoreProfile(s, c.S.Crypto, t, profile);
        }
        // the request becomes this business: type and contact already in place
        if (trial != null) s.Execute("UPDATE core_trialrequest SET status='done', client_id=@t WHERE id=@id", new { t = id, id = trial.Id });
        var ready = Accounts.MailReady(s, t); x.Commit(); c.S.EditionChanged();
        using var after = c.S.Db.Global();
        return new
        {
            creato = Mark(after.Get<Tenant>(id)), link, mailPronta = ready, puoCreare = after.Scalar<long>("SELECT count(*) FROM core_tenant") < c.S.TenantLimit,
            testo = "Cliente creato. " + (password != "" ? "L'amministratore dell'attività può già accedere con la password che hai scelto." : link != "" ? "L'invito è valido per 24 ore." : "L'amministratore dell'attività aveva già un accesso: trova la nuova attività nel suo portale."),
        };
    }

    /// <summary>platform.private_guard: one creation at a time, within the licence's number of businesses.</summary>
    static void Guard(Call c, Scope s)
    {
        s.Execute("SELECT pg_advisory_xact_lock(80561239)");
        if (s.Scalar<long>("SELECT count(*) FROM core_tenant") >= c.S.TenantLimit)
            throw new BookingException(c.S.Edition == "private" ? "L'edizione Private gestisce una sola attività con più utenti." : $"Puoi gestire al massimo {c.S.TenantLimit} attività. Per più attività serve una licenza Portale con più attività: la inserisci da Licenza.");
    }
    /// <summary>platform.seed_tenant: the type's real catalog, the same Configura l'attività applies; a generic appointment without a type.</summary>
    static void Seed(Ctx x, string preset)
    {
        var s = x.S; var id = x.Tenant.Id;
        if (Presets.All.ContainsKey(preset ?? "")) { new Profile(x).Apply(preset, new List<string> { "Sede principale" }); return; }
        var location = new Location { TenantId = id, Name = "Sede principale" }; s.Insert(location);
        var resource = new Resource { TenantId = id, LocationId = location.Id, Name = "Operatore 1" }; s.Insert(resource);
        var service = new Service { TenantId = id, LocationId = location.Id, Name = "Appuntamento", Kind = "resource" }; s.Insert(service);
        s.Insert(new Serviceresource { TenantId = id, ServiceId = service.Id, ResourceId = resource.Id });
        for (short d = 0; d < 5; d++) s.Insert(new Schedule { TenantId = id, Weekday = d, Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(18) });
    }

    // ---------- tdwin/views.py: Configurazione guidata: licence, the business and its owner, backups, workstations, then work
    [Rpc("inizia", Tenant = false, Staff = true)]
    public static object Guide(Call c)
    {
        var st = c.S.Licensing.Apply(true); using var g = c.S.Db.Global();
        var businesses = g.Query<Tenant>("SELECT * FROM core_tenant ORDER BY created").ToList().Select(t =>
        {
            var owner = g.One<User>("SELECT u.* FROM core_membership m JOIN core_user u ON u.id=m.user_id WHERE m.tenant_id=@t AND m.role='owner' ORDER BY m.id LIMIT 1", new { t = t.Id });
            return new { slug = t.Slug, nome = t.Name, titolare = owner != null ? c.S.Identity.LoginName(owner) : "", io = owner != null && owner.Id == c.User.Id };
        }).ToList();
        var backup = new Work.Backup(c.S).Row(); var l = st.Licence;
        return new
        {
            standalone = c.S.Standalone, io = c.S.Identity.LoginName(c.User), ufficio = c.S.Config.Origin, attivita = businesses, altre = st.Attivita > businesses.Count,
            tipi = Presets.Choices.Skip(1).Select(x => new { id = x.Key, nome = x.Label }),
            licenza = new
            {
                stato = st.Kind, id = l?.Id ?? "", intestatario = l?.Intestatario ?? "", tipo = l != null && TD.Core.Win.Licensing.TIPI.TryGetValue(l.Tipo ?? "", out var kind) ? kind : "", scadenza = l?.Scadenza?.ToString("dd/MM/yyyy") ?? "",
                fine = st.Fine.HasValue ? Local(st.Fine.Value).ToString("dd/MM/yyyy HH:mm") : "", provaDisponibile = c.S.Licensing.TrialStart() == null && st.Kind != "licenza",
            },
            passi = new { licenza = st.Kind != "bloccato", attivita = businesses.Count > 0, backup = backup.Attivo && backup.Chiave != "", postazioni = g.Scalar<long>("SELECT count(*) FROM tdwin_postazione WHERE token IS NOT NULL") > 0 },
            oraBackup = backup.Ora.ToString(@"hh\:mm"),
        };
    }

    /// <summary>create_business: the business and its owner, with the password typed now: what Clienti, Nuovo cliente does, in one step.
    /// "io": the administrator works in the business too, one account for everything (a program used by its owner).</summary>
    [Rpc("inizia.attivita", Tenant = false, Staff = true, Fresh = true)]
    public static object GuideBusiness(Call c)
    {
        var me = c.Str("chi") == "io"; var password = c.Str("password"); var acc = new Accounts(c.S.Db, c.S.Crypto);
        if (!me && password != c.Str("password2")) throw new BookingException("Le due password del titolare non coincidono.");
        var name = string.Join(" ", c.Str("nome").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        if (name == "") throw new BookingException("Scrivi il nome dell'attività.");
        if (name.Length > 100) name = name[..100];
        var id = Guid.NewGuid(); using var s = c.S.Db.Tenant(id);
        var slug0 = Regex.Replace(Regex.Replace(new string(name.Normalize(System.Text.NormalizationForm.FormKD).Where(ch => ch < 128).ToArray()).ToLowerInvariant(), @"[^\w\s-]", ""), @"[-\s]+", "-").Trim('-', '_');
        if (slug0.Length > 40) slug0 = slug0[..40]; slug0 = slug0.Trim('-'); if (slug0 == "") slug0 = "attivita";
        var slug = slug0; for (var n = 2; s.Scalar<long>("SELECT count(*) FROM core_tenant WHERE slug=@slug", new { slug }) > 0; n++) slug = $"{slug0}-{n}";
        Guard(c, s);
        var t = Blank(s, new JsonObject()); t.Id = id; t.Slug = slug; t.Name = name; t.WrappedKey = c.S.Crypto.NewTenantKey(id); s.Insert(t);
        var x = new Ctx(s, c.S.Crypto, t) { Actor = c.User.Id, PublicOrigin = c.PublicOrigin, Out = Outbox.Instance, Edition = c.S.Edition };
        Seed(x, c.Str("tipo"));
        var user = c.User;
        if (!me)
        {
            bool created; (user, created) = acc.AccountFor(s, c.Str("username"), c.Str("email"), id);
            if (user.IsStaff) throw new BookingException("È l'account dell'amministratore del programma: per lavorare tu con questo account scegli «Io, con questo account»; il titolare ne ha uno suo.");
            // someone else's account not activated yet stays his: a password typed here would take it over
            if (!created && !Passwords.Usable(user.Password)) throw new BookingException("Questo account non è ancora attivo: deve attivarlo dal suo invito.");
        }
        s.Execute("INSERT INTO core_membership (tenant_id, user_id, role, can_schedule, locations, resources) VALUES (@t, @u, 'owner', true, '[]'::jsonb, '[]'::jsonb)", new { t = id, u = user.Id });
        // someone who already has an account keeps their password: one typed here would be ignored, better say it
        if (!me && Passwords.Usable(user.Password) && password != "") throw new BookingException("Questo account ha già una password, che resta quella: lascia vuoti i campi della password.");
        if (!me && !Passwords.Usable(user.Password))
        {
            if (password != password.Trim() || password == "") throw new BookingException("Scegli la password del titolare.");
            acc.GrantAccess(s, user, password, c.PublicOrigin);
        }
        x.Audit("tenant.created", id); x.Commit(); c.S.EditionChanged();
        return new { slug, testo = me ? $"{name} è pronta: lavori tu, con questo account." : $"{name} è pronta, con il suo titolare." };
    }

    static Tenant Find(Scope g, string slug) => g.One<Tenant>("SELECT * FROM core_tenant WHERE slug=@slug", new { slug }) ?? throw new KeyNotFoundException();

    /// <summary>mail_status: how the confirmations of this business leave.</summary>
    static object MailStatus(Call c, Scope s, Tenant t)
    {
        static string Address(JsonObject m) => m.Str("sender_name") != "" ? $"{m.Str("sender_name")} <{m.Str("sender")}>" : m.Str("sender");
        if (!string.IsNullOrEmpty(t.Smtp))
        {
            try { var m = (JsonObject)c.S.Crypto.Decrypt(t.Id, t.WrappedKey, t.Smtp, "smtp"); return new { proprio = true, agenzia = false, mittente = Address(m), come = Mail.Describe(m) }; }
            catch (Exception) { return new { proprio = true, agenzia = false, mittente = "", come = "" }; }
        }
        var a = Mail.Agency(s, c.S.Crypto);
        return new { proprio = false, agenzia = a != null, mittente = a != null ? Address(a) : "", come = "" };
    }

    [Rpc("programma.cliente", Tenant = false, Staff = true, Fresh = true)]
    public static object Client(Call c)
    {
        Tenant t; using (var g = c.S.Db.Global()) t = Find(g, c.Str("slug"));
        var now = DateTime.UtcNow; using var s = c.S.Db.Tenant(t.Id); var profile = LoadProfile(s, c.S.Crypto, t); var u = UsageOf(s, t, now); var edition = c.S.Edition;
        var since = DateOnly.TryParseExact(profile.Str("since"), "yyyy-MM-dd", out var d) ? d : DateOnly.FromDateTime(t.Created);
        var kinds = new Dictionary<string, string> { ["email"] = "email", ["pec"] = "email", ["phone"] = "tel", ["since"] = "date" };
        var contacts = ((JsonArray)profile["contacts"]).OfType<JsonObject>().ToList();
        var members = s.Query<Membership>("SELECT * FROM core_membership WHERE tenant_id=@t ORDER BY role", new { t = t.Id }).ToList();
        return new
        {
            edizione = edition, segno = Mark(t), sito = t.Website, ragione = profile.Str("company"), citta = profile.Str("city"), dal = since.ToString("dd/MM/yyyy"),
            concesso = s.Scalar<long>("SELECT count(*) FROM core_supportgrant WHERE user_id=@u AND NOT revoked AND expires>@n", new { u = c.User.Id, n = now }) > 0,
            paginaPubblica = c.S.Config.Online ? c.PublicOrigin.TrimEnd('/') + "/book/" + t.Slug + "/" : "",
            avvisi = Attention(t, u, now, edition).Select(a => new { livello = a.Level, testo = a.Text }).ToList(),
            uso = new
            {
                b30 = u.Bookings30, tendenza = u.Trend, arrivo = u.Upcoming, contatti = u.Contacts, inviate = u.Sent30, sedi = u.Locations, servizi = u.Services, totale = u.BookingsTotal,
                ultima = Local(u.LastBooking, "dd/MM/yyyy"), accesso = Local(u.LastLogin, "dd/MM/yyyy HH:mm"), coda = u.Queued, problemi = u.Issues, settimane = u.Weeks, primo = u.FirstWeek.ToString("yyyy-MM-dd"),
            },
            tipo = Presets.All.TryGetValue(t.BusinessType ?? "", out var preset) ? preset.Label : "", marketing = t.MarketingEnabled ? "attivo" : t.MarketingRequested ? "richiesto" : "spento",
            righe = FIELDS.Select(f => new { chiave = f.Key, etichetta = f.Label, limite = f.Limit, valore = profile.Str(f.Key), tipo = kinds.GetValueOrDefault(f.Key, "text") }), note = profile.Str("notes"),
            referenti = contacts.Select(x => new { name = x.Str("name"), role = x.Str("role"), email = x.Str("email"), phone = x.Str("phone"), notes = x.Str("notes"), primary = Json.Truthy(x["primary"]) }), pieni = contacts.Count >= MAX_CONTACTS,
            utenti = members.Select(m => { var user = s.Get<User>(m.UserId); return new { nome = c.S.Identity.LoginName(user), ruolo = Permissions.RoleLabel(m.Role), mfa = c.S.Identity.MfaReady(s, user), accesso = Local(user.LastLogin, "dd/MM/yyyy HH:mm") }; }).ToList(),
            posta = MailStatus(c, s, t), siti = (JsonNode.Parse(string.IsNullOrEmpty(t.EmbedOrigins) ? "[]" : t.EmbedOrigins) as JsonArray ?? new JsonArray()).Select(o => o.ToString()),
        };
    }

    [Rpc("programma.cliente.azione", Tenant = false, Staff = true, Fresh = true)]
    public static object ClientAction(Call c)
    {
        Tenant t; using (var g = c.S.Db.Global()) t = Find(g, c.Str("slug"));
        using var s = c.S.Db.Tenant(t.Id); var x = new Ctx(s, c.S.Crypto, t) { Actor = c.User.Id }; var profile = LoadProfile(s, c.S.Crypto, t); var contacts = (JsonArray)profile["contacts"]; string text;
        int Index() => int.TryParse(c.Str("index"), out var i) ? i : throw new BookingException("Richiesta non valida.");
        switch (c.Str("azione"))
        {
            case "profile":
                {
                    var website = c.Str("website").Trim();
                    if (website != "" && !(Uri.TryCreate(website, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.'))) throw new BookingException("Inserisci un URL valido.");
                    if (website.Length > 200) throw new BookingException("Sito web troppo lungo.");
                    foreach (var (key, label, limit) in FIELDS) profile[key] = CleanText(c, key, limit, label);
                    if (profile.Str("email") != "") Accounts.ValidateEmail(profile.Str("email"));
                    if (profile.Str("pec") != "") Accounts.ValidateEmail(profile.Str("pec"));
                    if (profile.Str("since") != "" && !DateOnly.TryParseExact(profile.Str("since"), "yyyy-MM-dd", out _)) throw new BookingException("Cliente dal: data non valida.");
                    profile["notes"] = CleanText(c, "notes", 4000, "Note interne"); StoreProfile(s, c.S.Crypto, t, profile);
                    if (website != t.Website) s.Execute("UPDATE core_tenant SET website=@w WHERE id=@t", new { w = website, t = t.Id });
                    text = "Anagrafica salvata."; break;
                }
            case "contact":
                {
                    var contact = new JsonObject(); foreach (var (key, label, limit) in CONTACT_FIELDS) contact[key] = CleanText(c, key, limit, label);
                    if (contact.Str("name") == "") throw new BookingException("Indica il nome del referente.");
                    if (contact.Str("email") != "") Accounts.ValidateEmail(contact.Str("email"));
                    contact["primary"] = c.Bool("primary");
                    if (c.Str("index") == "")
                    {
                        if (contacts.Count >= MAX_CONTACTS) throw new BookingException($"Massimo {MAX_CONTACTS} referenti.");
                        if (contacts.Count == 0) contact["primary"] = true;
                        contacts.Add(contact);
                    }
                    else { var i = Index(); if (i < 0 || i >= contacts.Count) throw new BookingException("Referente non trovato."); contacts[i] = contact; }
                    if (Json.Truthy(contact["primary"])) foreach (var other in contacts.OfType<JsonObject>()) other["primary"] = ReferenceEquals(other, contact);
                    StoreProfile(s, c.S.Crypto, t, profile); text = "Referente salvato."; break;
                }
            case "contact_remove":
                {
                    var i = Index(); if (i < 0 || i >= contacts.Count) throw new BookingException("Referente non trovato.");
                    var was = Json.Truthy(contacts[i]["primary"]); contacts.RemoveAt(i);
                    if (was && contacts.Count > 0) contacts[0]["primary"] = true;
                    StoreProfile(s, c.S.Crypto, t, profile); text = "Referente rimosso."; break;
                }
            case "logo":
                {
                    if (c.Str("logo") == "") throw new BookingException("Scegli un'immagine.");
                    // the shape is kept (wide wordmarks too) and the empty transparent margins are cut, by the window
                    var (logo, ratio) = Configura.Logo(c.Str("logo"), e => throw new BookingException(e));
                    s.Execute("UPDATE core_tenant SET logo=@l, logo_ratio=@r WHERE id=@t", new { l = logo, r = ratio, t = t.Id }); x.Audit("tenant.logo", t.Id);
                    text = "Logo aggiornato: compare nel portale dell'attività e nella pagina prenotazioni."; break;
                }
            case "logo_remove":
                s.Execute("UPDATE core_tenant SET logo=NULL, logo_ratio=1 WHERE id=@t", new { t = t.Id }); x.Audit("tenant.logo", t.Id, new { removed = true });
                text = "Logo rimosso."; break;
            default: throw new BookingException("Operazione non valida.");
        }
        x.Commit(); return new { testo = text };
    }

    // ---------- core/site.py: Impostazioni generali
    static readonly (string Title, string[] Names)[] GROUPS =
    {
        ("Il portale", new[] { "name", "owner_name", "owner_url", "logo_file", "logo_dark_file", "remove_logo", "remove_logo_dark", "favicon_file", "remove_favicon", "brand_color", "default_theme" }),
        ("Pagina di accesso e assistenza", new[] { "login_title", "login_text", "support_email", "support_phone" }),
        ("Richieste di prova", new[] { "trial_enabled", "trial_email", "trial_days" }),
        ("Dati dell'azienda e privacy", new[] { "legal_name", "vat_number", "legal_address", "privacy_email", "data_location", "privacy_policy" }),
        ("Ogni nuova attività parte con", new[] { "default_timezone", "default_brand_color", "default_privacy_text", "default_consent_text", "default_retention_days", "default_daily_limit", "default_hourly_limit" }),
    };
    static readonly HashSet<string> WIDE = new() { "login_text", "login_title", "default_privacy_text", "default_consent_text", "trial_enabled", "privacy_policy" };
    static Sitesettings Site(Scope g)
    {
        if (g.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1") is { } row) return row;
        g.Insert(new Sitesettings()); return g.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1");
    }
    static string B64(byte[] data) => data is { Length: > 0 } ? Convert.ToBase64String(data) : "";

    [Rpc("generali", Tenant = false, Staff = true, Fresh = true)]
    public static object General(Call c)
    {
        using var g = c.S.Db.Global(); var s = Site(g); g.Commit(); var cloud = c.S.Edition == "cloud";
        var all = new Dictionary<string, Fld>
        {
            ["name"] = new() { Etichetta = "Nome del portale", Valore = s.Name, Max = 60 }, ["owner_name"] = new() { Etichetta = "Chi lo offre (in fondo alle pagine)", Valore = s.OwnerName, Max = 80 },
            ["owner_url"] = new() { Etichetta = "Sito di chi lo offre", Valore = s.OwnerUrl, Max = 200 },
            ["logo_file"] = new() { Etichetta = "Logo per sfondi chiari: PNG trasparente, JPEG o WebP, massimo 1 MB", Tipo = "logo", Valore = "" },
            ["logo_dark_file"] = new() { Etichetta = "Logo per sfondi scuri (menu laterale, pagina di accesso, tema scuro)", Tipo = "logo", Valore = "" },
            ["remove_logo"] = s.Logo is { Length: > 0 } ? new() { Etichetta = "Togli il logo e usa il marchio TD", Tipo = "check", Valore = false } : null,
            ["remove_logo_dark"] = s.LogoDark is { Length: > 0 } ? new() { Etichetta = "Togli il logo per sfondi scuri", Tipo = "check", Valore = false } : null,
            ["favicon_file"] = new() { Etichetta = "Icona del browser (favicon): PNG, quadrata", Tipo = "logo", Valore = "" },
            ["remove_favicon"] = s.Favicon is { Length: > 0 } ? new() { Etichetta = "Togli l'icona e usa quella TD", Tipo = "check", Valore = false } : null,
            ["brand_color"] = new() { Etichetta = "Colore principale del portale", Valore = s.BrandColor, Max = 7, Segnaposto = "#7a55ad" },
            ["default_theme"] = new() { Etichetta = "Tema di partenza: chi vuole lo cambia dalla barra in alto", Tipo = "select", Valore = s.DefaultTheme, Scelte = new object[] { new { id = "auto", nome = "Automatico, come il sistema" }, new { id = "light", nome = "Chiaro" }, new { id = "dark", nome = "Scuro" } } },
            ["login_title"] = new() { Etichetta = "Titolo della pagina di accesso", Valore = s.LoginTitle, Max = 140 }, ["login_text"] = new() { Etichetta = "Testo sotto il titolo", Tipo = "area", Valore = s.LoginText, Max = 240 },
            ["support_email"] = new() { Etichetta = "Email di assistenza", Valore = s.SupportEmail, Max = 254 }, ["support_phone"] = new() { Etichetta = "Telefono di assistenza", Valore = s.SupportPhone, Max = 40 },
            ["trial_enabled"] = new() { Etichetta = "Mostra \"Prova il servizio\" nella pagina di accesso", Tipo = "check", Valore = s.TrialEnabled },
            ["trial_email"] = new() { Etichetta = "Email che riceve le richieste di prova (vuota: quella di assistenza)", Valore = s.TrialEmail, Max = 254 }, ["trial_days"] = new() { Etichetta = "Durata della prova in giorni", Tipo = "number", Valore = (int)s.TrialDays },
            ["legal_name"] = new() { Etichetta = "Ragione sociale", Valore = s.LegalName, Max = 160 }, ["vat_number"] = new() { Etichetta = "Partita IVA", Valore = s.VatNumber, Max = 40 },
            ["legal_address"] = new() { Etichetta = "Sede legale", Valore = s.LegalAddress, Max = 200 }, ["privacy_email"] = new() { Etichetta = "Email per le richieste sulla privacy", Valore = s.PrivacyEmail, Max = 254 },
            ["data_location"] = new() { Etichetta = "Dove sono i server, per esempio Roma, Italia", Valore = s.DataLocation, Max = 120 },
            ["privacy_policy"] = new() { Etichetta = "Testo della privacy policy (vuoto: testo standard compilato con i dati qui sopra)", Tipo = "area", Valore = s.PrivacyPolicy, Segnaposto = "## Titolo di sezione\nTesto del paragrafo\n- voce di elenco" },
            ["default_timezone"] = new() { Etichetta = "Fuso orario", Valore = s.DefaultTimezone, Max = 64 }, ["default_brand_color"] = new() { Etichetta = "Colore della pagina prenotazioni", Valore = s.DefaultBrandColor, Max = 7, Segnaposto = "#7a55ad" },
            ["default_privacy_text"] = new() { Etichetta = "Informativa prenotazioni", Tipo = "area", Valore = s.DefaultPrivacyText }, ["default_consent_text"] = new() { Etichetta = "Testo consenso marketing", Tipo = "area", Valore = s.DefaultConsentText },
            ["default_retention_days"] = new() { Etichetta = "Conservazione prenotazioni (giorni)", Tipo = "number", Valore = s.DefaultRetentionDays },
            ["default_daily_limit"] = new() { Etichetta = "Limite email al giorno", Tipo = "number", Valore = s.DefaultDailyLimit }, ["default_hourly_limit"] = new() { Etichetta = "Limite email all'ora", Tipo = "number", Valore = s.DefaultHourlyLimit },
        };
        Fld Named(string name) { var f = all[name]; return f == null ? null : new Fld { Nome = name, Etichetta = f.Etichetta, Tipo = f.Tipo, Valore = f.Valore, Max = f.Max, Segnaposto = f.Segnaposto, Scelte = f.Scelte, Largo = WIDE.Contains(name) }; }
        var smtp = Mail.Agency(g, c.S.Crypto); var fromEnv = string.IsNullOrEmpty(s.Smtp) && smtp != null;
        return new
        {
            gruppi = GROUPS.Where(x => cloud || x.Title != "Richieste di prova").Select(x => new { titolo = x.Title, campi = x.Names.Select(Named).Where(f => f != null) }),
            logo = B64(s.Logo), logoScuro = B64(s.LogoDark), icona = B64(s.Favicon), privacy = c.PublicOrigin.TrimEnd('/') + "/privacy/",
            smtp = smtp == null ? null : new { mittente = smtp.Str("sender_name") != "" ? $"{smtp.Str("sender_name")} <{smtp.Str("sender")}>" : smtp.Str("sender"), daEnv = fromEnv },
        };
    }

    [Rpc("generali.salva", Tenant = false, Staff = true, Fresh = true)]
    public static object GeneralSave(Call c)
    {
        using var g = c.S.Db.Global(); var s = Site(g); var cloud = c.S.Edition == "cloud"; var p = new Posted(c, c.Obj("valori") ?? new JsonObject());
        string Color(string name) { var v = p.Text(name, true, 7); if (v != "" && !Regex.IsMatch(v, @"^#[0-9a-fA-F]{6}\z")) p.Error(name, "Colore non valido, per esempio #7a55ad."); return v; }
        string Email(string name) { var v = p.Text(name, false, 254); if (v != "") try { Accounts.ValidateEmail(v); } catch (BookingException ex) { p.Error(name, ex.Message); } return v; }
        var legal = new[] { s.LegalName, s.VatNumber, s.LegalAddress, s.PrivacyEmail, s.DataLocation, s.PrivacyPolicy };
        s.Name = p.Text("name", true, 60); s.OwnerName = p.Text("owner_name", true, 80);
        var url = p.Text("owner_url", false, 200); if (url != "" && !url.Contains("://")) url = "http://" + url;
        if (url != "" && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.'))) p.Error("owner_url", "Inserisci un URL valido.");
        s.OwnerUrl = url; s.BrandColor = Color("brand_color");
        var theme = p.Text("default_theme", true); if (theme != "" && theme is not ("auto" or "light" or "dark")) p.Error("default_theme", "Scegli un'opzione valida."); s.DefaultTheme = theme;
        s.LoginTitle = p.Text("login_title", true, 140); s.LoginText = p.Text("login_text", true, 240); s.SupportEmail = Email("support_email"); s.SupportPhone = p.Text("support_phone", false, 40);
        if (cloud)
        {
            s.TrialEnabled = p.Bool("trial_enabled"); s.TrialEmail = Email("trial_email");
            var days = p.Int("trial_days", true, 0, 32767); if (days is < 1 or > 90) p.Error("trial_days", "Da 1 a 90 giorni."); s.TrialDays = (short)(days ?? s.TrialDays);
        }
        s.LegalName = p.Text("legal_name", false, 160); s.VatNumber = p.Text("vat_number", false, 40); s.LegalAddress = p.Text("legal_address", false, 200); s.PrivacyEmail = Email("privacy_email");
        s.DataLocation = p.Text("data_location", false, 120); s.PrivacyPolicy = p.Text("privacy_policy");
        s.DefaultTimezone = p.Text("default_timezone", true, 64); if (s.DefaultTimezone != "" && !TimeZoneInfo.TryFindSystemTimeZoneById(s.DefaultTimezone, out _)) p.Error("default_timezone", "Fuso orario non valido, per esempio Europe/Rome.");
        s.DefaultBrandColor = Color("default_brand_color"); s.DefaultPrivacyText = p.Text("default_privacy_text", true); s.DefaultConsentText = p.Text("default_consent_text", true);
        s.DefaultRetentionDays = p.Int("default_retention_days", true) ?? 0; s.DefaultDailyLimit = p.Int("default_daily_limit", true) ?? 0; s.DefaultHourlyLimit = p.Int("default_hourly_limit", true) ?? 0;
        var (logo, ratio) = Configura.Logo(p.Raw("logo_file"), e => p.Error("logo_file", e)); var (dark, darkRatio) = Configura.Logo(p.Raw("logo_dark_file"), e => p.Error("logo_dark_file", e));
        var (icon, _) = Configura.Logo(p.Raw("favicon_file"), e => p.Error("favicon_file", e.Replace("Logo massimo", "Icona massimo")));
        if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
        if (p.Bool("remove_logo")) { s.Logo = null; s.LogoRatio = 1; } else if (logo != null) { s.Logo = logo; s.LogoRatio = ratio; }
        if (p.Bool("remove_logo_dark")) { s.LogoDark = null; s.LogoDarkRatio = 1; } else if (dark != null) { s.LogoDark = dark; s.LogoDarkRatio = darkRatio; }
        if (p.Bool("remove_favicon")) s.Favicon = null; else if (icon != null) s.Favicon = Work.Faces.Icon(icon);
        if (!legal.SequenceEqual(new[] { s.LegalName, s.VatNumber, s.LegalAddress, s.PrivacyEmail, s.DataLocation, s.PrivacyPolicy })) s.PrivacyUpdated = DateTime.Now.Date;
        s.Updated = DateTime.UtcNow;
        g.Update(s); g.Commit();
        return new { testo = "Impostazioni generali salvate." };
    }
}
