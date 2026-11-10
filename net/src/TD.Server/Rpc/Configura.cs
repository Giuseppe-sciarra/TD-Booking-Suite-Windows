using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.setup and its actions: Configura l'attività: the steps with their state, type of business and
/// locations, Logo e aspetto, the usual questions, Al banco, the links and the QR of the booking page.</summary>
public static class Configura
{
    public static readonly Dictionary<string, string> CATALOG_HELP = new()
    {
        ["locations"] = "I luoghi dove lavori. Ogni servizio e ogni risorsa appartiene a una sede: con più sedi, crea qui ognuna e poi i servizi di ciascuna",
        ["schedules"] = "I giorni e gli orari in cui si può prenotare. Valgono per tutta l'attività, oppure solo per una sede, un servizio o una risorsa",
        ["exceptions"] = "Ferie, festivi e aperture straordinarie, anche dal giorno al giorno: hanno la precedenza sugli orari di apertura",
        ["services"] = "Quello che il cliente sceglie quando prenota in una sede: al ristorante il Tavolo, dal parrucchiere Taglio, Piega e Colore, in hotel la Camera. La sede è il luogo, il servizio è cosa si prenota lì, con posti, durata e preavviso",
        ["resources"] = "Chi o cosa viene prenotato: operatori, cabine, camere, tavoli, campi. Ogni risorsa appartiene a una sede",
        ["options"] = "Versioni dello stesso servizio con durata diversa, per esempio taglio corto o lungo",
        ["turns"] = "I turni che si ripetono ogni settimana, per esempio pranzo e cena, con i coperti di ciascuno",
        ["occurrences"] = "Le date con posti limitati: un evento, un corso, un tour. Una data si può ripetere ogni settimana o ogni N settimane",
        ["products"] = "Quello che vendi oltre ai servizi, per esempio shampoo e spazzole: lo trovi in Cassa e nei preventivi. Il reparto dello scontrino si sceglie in Registratore di cassa",
    };
    public static readonly HashSet<string> OWNER_CATALOG = new() { "schedules", "exceptions", "resources", "services", "options", "turns", "occurrences", "products" };
    public static bool CatalogAllowed(Call c, string kind) => c.User.IsStaff || OWNER_CATALOG.Contains(kind);
    static readonly (string Key, string Label)[] CHANNELS = { ("google", "Google, profilo dell'attività"), ("instagram", "Instagram"), ("facebook", "Facebook"), ("whatsapp", "WhatsApp"), ("newsletter", "Newsletter") };
    static readonly string[] SWATCHES = { "#7a55ad", "#be185d", "#b91c1c", "#c2410c", "#a16207", "#15803d", "#0f766e", "#1d4ed8", "#334155" };

    /// <summary>catalog_kinds: the pages that are hidden because no service uses them.</summary>
    public static HashSet<string> Hidden(Ctx x)
    {
        var kinds = x.S.Query<string>("SELECT DISTINCT kind FROM core_service WHERE active").ToHashSet(); var hidden = new HashSet<string>();
        bool Any(string table) => x.S.Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {table} WHERE active)");
        if (!kinds.Contains("shift") && !Any("core_shiftrule")) hidden.Add("turns");
        if (!kinds.Contains("event") && !Any("core_occurrence")) hidden.Add("occurrences");
        if (!kinds.Contains("resource") && !kinds.Contains("day") && !Any("core_resource")) hidden.Add("resources");
        if (!kinds.Contains("resource") && !Any("core_option")) hidden.Add("options");
        return hidden;
    }
    /// <summary>recommended(): the usual questions of this kind of business that some active service does not ask yet.</summary>
    static (List<JsonObject> Missing, List<Service> Services) Recommended(Ctx x)
    {
        var wanted = Presets.Questions(x.Tenant.BusinessType ?? "").Select(q => (JsonObject)q.DeepClone()).ToList();
        var services = wanted.Count > 0 ? x.S.Query<Service>("SELECT * FROM core_service WHERE active ORDER BY name").ToList() : new();
        var have = services.Select(s => Json.Arr(s.Fields).Select(f => f.Str("key")).ToHashSet()).ToList();
        return (wanted.Where(q => have.Any(h => !h.Contains(q.Str("key")))).ToList(), services);
    }
    static string QuestionKind(JsonObject q) =>
        (q.Str("type") switch { "checkbox" => "Casella sì o no", "text" => "Testo libero", "number" => "Numero", _ => "Scelta tra " + string.Join(", ", (q["choices"] as JsonArray)?.Select(v => v.ToString()) ?? Array.Empty<string>()) })
        + (Json.Truthy(q["required"]) ? ", obbligatoria" : ", facoltativa");

    [Rpc("configura", Perm = "catalog.write")]
    public static object Page(Call c)
    {
        var x = c.C; var t = c.Tenant; var staff = c.User.IsStaff; long N(string sql, object a = null) => x.S.Scalar<long>(sql, a);
        var services = x.S.Query<(string Name, string Kind, string Loc)>("SELECT s.name, s.kind, l.name FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active ORDER BY l.name, s.name").ToList();
        var kinds = services.Select(s => s.Kind).ToHashSet(); var locations = x.S.Query<string>("SELECT name FROM core_location WHERE active ORDER BY name").ToList();
        var steps = new List<(string Key, string Title, bool Done, long Count, string Text, string Hint, (string, string)? Extra)>();
        var sched = N("SELECT count(*) FROM core_schedule");
        steps.Add(("locations", "Sedi", locations.Count > 0, locations.Count, locations.Count > 0 ? string.Join(", ", locations) : "Nessuna sede", CATALOG_HELP["locations"], null));
        steps.Add(("schedules", "Orari di apertura", sched > 0, sched, $"Orari impostati: {sched}. Chiusure e giorni speciali: {N("SELECT count(*) FROM core_exceptionday")}", CATALOG_HELP["schedules"], ("exceptions", "Chiusure e giorni speciali")));
        steps.Add(("services", "Servizi", services.Count > 0, services.Count, services.Count > 0 ? string.Join(", ", services.Take(6).Select(s => s.Name + (locations.Count > 1 ? " (" + s.Loc + ")" : ""))) : "Nessun servizio", CATALOG_HELP["services"],
            Hidden(x).Contains("options") ? null : ("options", "Varianti dei servizi")));
        var (one, many, unitHelp) = Presets.UnitsOf(t.BusinessType);
        var res = N("SELECT count(*) FROM core_resource WHERE active");
        if (kinds.Contains("resource") || kinds.Contains("day") || res > 0)
            steps.Add(("resources", many, res > 0, res, res > 0 ? $"{res} {(res == 1 ? one.ToLowerInvariant() : many.ToLowerInvariant())}" : $"Ancora nessuno: servono ai servizi su appuntamento o con {many.ToLowerInvariant()}", unitHelp, null));
        if (kinds.Contains("shift")) { var n = N("SELECT count(*) FROM core_shiftrule"); steps.Add(("turns", "Turni del ristorante", n > 0, n, $"{n} turni settimanali", CATALOG_HELP["turns"], null)); }
        if (kinds.Contains("event")) { var n = N("SELECT count(*) FROM core_occurrence WHERE start>=@n", new { n = x.Now }); steps.Add(("occurrences", "Eventi con data", n > 0, N("SELECT count(*) FROM core_occurrence"), $"{n} date in programma", CATALOG_HELP["occurrences"], null)); }
        if (t.TillOn || N("SELECT count(*) FROM core_product") > 0)
        {
            var n = N("SELECT count(*) FROM core_product WHERE active");  // optional, so it never counts as missing
            steps.Add(("products", "Prodotti", true, n, n > 0 ? $"{n} prodott{(n == 1 ? "o" : "i")} in vendita" : "Facoltativo: aggiungili se vendi prodotti, anche un po' alla volta dai preventivi", CATALOG_HELP["products"], null));
        }
        var members = N("SELECT count(*) FROM core_membership WHERE tenant_id=@t", new { t = t.Id }); var agencyMail = N("SELECT count(*) FROM core_sitesettings WHERE smtp<>''") > 0;
        // site and email are set by the administrator of the program: business users do not even see them
        var agency = !staff ? new List<(string, bool, string, string)>() : new()
        {
            ("Persone e permessi", members > 1, $"{members} person{(members == 1 ? "a" : "e")} con accesso", "persone"),
            ("Invio email", !string.IsNullOrEmpty(t.Smtp) || agencyMail, !string.IsNullOrEmpty(t.Smtp) ? "Invio proprio dell'attività" : agencyMail ? "Invio dell'amministratore del programma" : "Nessun invio configurato: le email restano in attesa", "smtp"),
        };
        var shownSteps = steps.Where(s => CatalogAllowed(c, s.Key)).ToList();
        var profile = new Profile(x).Describe(); var page = c.PublicOrigin.TrimEnd('/') + "/book/" + t.Slug + "/"; var shown = Math.Max(5, profile.Locations.Count);
        var allergens = Json.Strings(t.Allergens);
        return new
        {
            fatti = shownSteps.Count(s => s.Done) + agency.Count(a => a.Item2), totale = shownSteps.Count + agency.Count, molti = many, nome = t.Name,
            passi = shownSteps.Select(s => new { chiave = s.Key, titolo = s.Title, fatto = s.Done, quanti = s.Count, testo = s.Text, aiuto = s.Hint, altro = s.Extra == null ? null : new { chiave = s.Extra.Value.Item1, testo = s.Extra.Value.Item2 } }),
            profilo = new { tipo = profile.Type, nome = profile.Label, sedi = profile.Locations.Select(l => l.Name), quante = profile.Locations.Count, servizi = profile.Services },
            puoProfilo = staff, tipi = Presets.All.Select(p => new { id = p.Key, nome = p.Value.Label, aiuto = p.Value.Hint }), massimoSedi = Profile.MAX_LOCATIONS,
            nomi = profile.Locations.Select(l => l.Name).Concat(Enumerable.Range(profile.Locations.Count, shown - profile.Locations.Count).Select(Profile.DefaultName)),
            scrive = c.Can("catalog.write"), colore = t.BrandColor, frase = t.Tagline, sito = t.Website, grazie = t.ThanksUrl, haLogo = t.Logo is { Length: > 0 }, campioni = SWATCHES,
            allergie = Presets.ServesFood(t.BusinessType), allergieAccese = t.AllergensOn, allergeni = allergens,
            consigliate = staff ? Recommended(x).Missing.Select(q => new { chiave = q.Str("key"), testo = q.Str("label"), tipo = QuestionKind(q) }) : null,
            agenzia = agency.Select(a => new { titolo = a.Item1, fatto = a.Item2, testo = a.Item3, pagina = a.Item4 }),
            banco = !c.Can("users.manage") ? null : new
            {
                forza = t.ForceAll, cassa = t.TillOn, operatori = t.TillOperators, assenze = (int)t.AbsencesRequest, attesa = t.WaitMode, minuti = (int)t.WaitMinutes, minutiScelte = Waitlist.WAIT_MINUTES, whatsapp = t.WaWhen ?? "",
                whatsappScelte = ClientMails.WA_WHEN.Select(w => new { id = w.Id, nome = w.Label }), categorie = string.Concat(Json.Arr(t.ClientCategories).Select(k => $"{k.Str("name")}, {k.Str("ahead")}, {k.Str("max")}\n")),
                ruoli = new { owner = Permissions.RoleLabel("owner"), manager = Permissions.RoleLabel("manager"), reception = Permissions.RoleLabel("reception"), @operator = Permissions.RoleLabel("operator") },
            },
            pagina = c.S.Config.Online ? page : "", canali = CHANNELS.Select(k => new { nome = k.Label, link = $"{page}?src={k.Key}" }), siti = Json.Strings(t.EmbedOrigins),
            incorpora = $"<iframe src=\"{page}?src=sito\" title=\"Prenota\" width=\"100%\" height=\"900\" loading=\"lazy\" style=\"border:0\" data-tdbs></iframe>\n<script src=\"{c.PublicOrigin.TrimEnd('/')}/static/embed.js\" async></script>",
        };
    }

    [Rpc("configura.profilo", Perm = "catalog.write", Fresh = true)]
    public static object SetProfile(Call c)
    {
        if (!c.User.IsStaff) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
        var x = c.C; var t = c.Tenant; var pf = new Profile(x); var preset = c.Str("business_type");
        var count = c.Int("locations", 1); if (count is < 1 or > Profile.MAX_LOCATIONS) throw new BookingException($"Le sedi vanno da 1 a {Profile.MAX_LOCATIONS}.");
        var names = Profile.CleanNames((c.Args["names"] as JsonArray)?.Select(n => n?.ToString() ?? "").ToList() ?? new(), count);
        Profile.Result r;
        // the same type keeps what the business made; another type replaces it, only once confirmed
        if (preset != "" && preset == t.BusinessType) r = pf.Keep(names);
        else if (Presets.All.ContainsKey(t.BusinessType ?? "") && !c.Bool("confirm_type"))
            throw new BookingException($"Per passare a {(Presets.All.TryGetValue(preset, out var np) ? np.Label : "un altro tipo")} spunta la conferma: servizi, operatori e orari vengono sostituiti. Le prenotazioni restano.");
        else r = pf.Apply(preset, names);
        var sedi = r.Locations == 1 ? "sede" : "sedi"; string text;
        if (r.Kept)
        {
            text = $"Sedi aggiornate: {r.Locations} {sedi}. Servizi, operatori e orari restano come li hai impostati";
            if (r.Services > 0) text += $"; le sedi nuove partono con {r.Services} servizi del tipo";
        }
        else
        {
            text = $"Configurazione applicata: {r.Label}, {r.Locations} {sedi}, {r.Services} servizi";
            if (r.Resources > 0) text += $", {r.Resources} {Presets.UnitsOf(r.Type).Many.ToLowerInvariant()}";
            if (r.Dates > 0) text += $", {r.Dates} date in programma";
        }
        if (r.Archived > 0) text += $". {r.Archived} elementi usati da prenotazioni sono stati disattivati, non cancellati";
        return new { testo = text + "." };
    }

    [Rpc("configura.aspetto", Perm = "catalog.write")]
    public static object Brand(Call c)
    {
        var x = c.C; var t = c.Tenant; var errors = new List<string>();
        var color = c.Str("brand_color"); if (!Regex.IsMatch(color, @"^#[0-9a-fA-F]{6}\z")) errors.Add("Colore non valido.");
        var tagline = c.Str("tagline").Trim(); if (tagline.Length > 180) errors.Add("Assicurati che questo valore non contenga più di 180 caratteri.");
        var site = c.Str("website").Trim(); if (site != "" && !site.Contains("://")) site = "http://" + site;
        if (site != "" && !(Uri.TryCreate(site, UriKind.Absolute, out var su) && su.Scheme is "http" or "https" && su.Host.Contains('.') && site.Length <= 200)) errors.Add("Inserisci un URL valido.");
        var thanks = c.Str("thanks_url").Trim(); if (thanks != "" && !thanks.Contains("://")) thanks = "http://" + thanks;
        if (thanks != "" && !(Uri.TryCreate(thanks, UriKind.Absolute, out _) && thanks.Length <= 500)) errors.Add("Inserisci un URL valido."); else if (thanks != "" && !thanks.StartsWith("https://")) errors.Add("Usa un indirizzo HTTPS.");
        byte[] logo = null; double ratio = 1;
        if (c.Str("logo") != "")
        {
            // the window already cut the empty margins and made it a PNG of 800 x 300 at most (site.clean_brand)
            try { logo = Convert.FromBase64String(c.Str("logo")); } catch (FormatException) { logo = Array.Empty<byte>(); }
            if (logo.Length > 1024 * 1024) errors.Add("Logo massimo 1 MB.");
            else if (logo.Length < 24 || !logo.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) errors.Add("Usa un'immagine PNG, JPEG o WebP valida.");
            else
            {
                int w = logo[16] << 24 | logo[17] << 16 | logo[18] << 8 | logo[19], h = logo[20] << 24 | logo[21] << 16 | logo[22] << 8 | logo[23];
                if (w is < 1 or > 800 || h is < 1 or > 300) errors.Add("Usa un'immagine PNG, JPEG o WebP valida."); else ratio = Math.Round((double)w / Math.Max(h, 1), 3, MidpointRounding.ToEven);
            }
        }
        if (errors.Count > 0) throw new BookingException(string.Join(" ", errors));
        x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = t.Id });
        if (logo != null) x.S.Execute("UPDATE core_tenant SET logo=@l, logo_ratio=@r WHERE id=@t", new { l = logo, r = ratio, t = t.Id });
        else if (c.Bool("logo_remove")) x.S.Execute("UPDATE core_tenant SET logo=NULL, logo_ratio=1 WHERE id=@t", new { t = t.Id });
        x.S.Execute("UPDATE core_tenant SET brand_color=@c, tagline=@g, website=@w, thanks_url=@k WHERE id=@t", new { c = color, g = tagline, w = site, k = thanks, t = t.Id });
        x.Audit("tenant.brand", t.Id, new { logo = logo != null || t.Logo is { Length: > 0 } && !c.Bool("logo_remove") });
        return new { testo = "Logo e aspetto salvati: li vedi già nel menu a sinistra." };
    }

    [Rpc("configura.banco", Perm = "users.manage")]
    public static object Desk(Call c)
    {
        var x = c.C; var t = c.Tenant; var on = c.Bool("force_all");
        var absences = c.Int("absences_request", t.AbsencesRequest); var minutes = c.Int("wait_minutes", t.WaitMinutes); var mode = c.Str("wait_mode", t.WaitMode); var wa = c.Str("wa_when", t.WaWhen ?? "");
        if (absences is < 0 or > 5 || !Waitlist.WAIT_MINUTES.Contains(minutes) || mode is not ("all" or "queue") || !ClientMails.WA_WHEN.Any(w => w.Id == wa)) throw new BookingException("Scelta non valida.");
        var cats = c.Args["client_categories"] != null ? Json.Dump(Clients.ParseCategories(c.Str("client_categories"))) : t.ClientCategories;
        x.S.Execute("UPDATE core_tenant SET force_all=@f, till_operators=@o, absences_request=@a, wait_mode=@m, wait_minutes=@w, client_categories=@c::jsonb, wa_when=@wa WHERE id=@t",
            new { f = on, o = c.Bool("till_operators"), a = absences, m = mode, w = minutes, c = cats, wa, t = t.Id });
        x.Audit("tenant.force_all", t.Id, new { on, absences, wait_mode = mode, wait_minutes = minutes, wa_when = wa });
        return new { testo = (on ? "Ora anche reception e operatori possono forzare al banco." : "Al banco forzano solo gli amministratori.") + " Regole salvate." };
    }

    /// <summary>Adds the chosen usual questions to every active service that does not ask them yet. Nothing is removed.</summary>
    [Rpc("configura.domande", Perm = "catalog.write")]
    public static object Questions(Call c)
    {
        if (!c.User.IsStaff) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
        var x = c.C; var (missing, services) = Recommended(x); var picked = (c.Args["q"] as JsonArray)?.Select(v => v?.ToString()).ToHashSet() ?? new();
        var chosen = missing.Where(q => picked.Contains(q.Str("key"))).ToList(); var changed = 0;
        foreach (var s in services)
        {
            var fields = Json.Arr(s.Fields); var have = fields.Select(f => f.Str("key")).ToHashSet(); var add = chosen.Where(q => !have.Contains(q.Str("key"))).ToList();
            if (add.Count == 0 || fields.Count + add.Count > 12) continue;
            foreach (var q in add) fields.Add(q.DeepClone());
            s.Fields = Json.Dump(fields); x.S.Update(s, "fields"); changed++;
        }
        x.Audit("catalog.questions", c.Tenant.Id, new { questions = chosen.Count, services = changed });
        return new { testo = changed > 0 ? $"Domande aggiunte a {changed} serviz{(changed == 1 ? "io" : "i")}: compaiono subito nel modulo di prenotazione. Le modifichi in ogni servizio, alla voce Richieste aggiuntive." : "Nessuna domanda da aggiungere." };
    }

    /// <summary>The QR of the booking page, for the counter, a flyer or the menu: its bookings are tagged qr.</summary>
    [Rpc("configura.qr", Perm = "catalog.write")]
    public static IResult Qr(Call c)
    {
        using var gen = new QRCoder.QRCodeGenerator(); using var data = gen.CreateQrCode(c.PublicOrigin.TrimEnd('/') + "/book/" + c.Tenant.Slug + "/?src=qr", QRCoder.QRCodeGenerator.ECCLevel.M);
        return Results.File(new QRCoder.PngByteQRCode(data).GetGraphic(10), "image/png", $"prenota-{c.Tenant.Slug}.png");
    }
}
