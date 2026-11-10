using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>forms.ResourceForm: an operator, a cabin, a room: its photo, what it does (with its own duration, price and
/// posa for each service), its own working days, its calendars, its limits.</summary>
public static partial class Catalogo
{
    sealed record Special(List<Fld> Fields, object Extra);
    static readonly (string P, string Words)[] LIMIT_WORDS = { ("day", "al giorno"), ("week", "a settimana"), ("month", "al mese") };
    /// <summary>add_limits: bookings and booked minutes at most per day, week, month; empty = no limit.</summary>
    static IEnumerable<Fld> Limits(string limitsJson, string who)
    {
        var l = Json.Obj(limitsJson);
        foreach (var (p, words) in LIMIT_WORDS)
            foreach (var (k, what) in new[] { ("n", "Prenotazioni"), ("m", "Minuti prenotati") })
                yield return new Fld { Nome = $"lim_{p}_{k}", Etichetta = $"{what} al massimo {words}", Tipo = "number", Valore = Json.Truthy(l[$"{p}_{k}"]) ? l.Str($"{p}_{k}") : "", Aiuto = (p, k) == ("day", "n") ? $"Online, per {who}. Vuoto o 0: nessun limite. Al banco si supera con Forza" : "" };
    }
    static string ReadLimits(Posted p)
    {
        var o = new JsonObject();
        foreach (var (per, _) in LIMIT_WORDS) foreach (var k in new[] { "n", "m" }) if (p.Int($"lim_{per}_{k}", false, 0, 100000) is int v && v > 0) o[$"{per}_{k}"] = v;
        return Json.Dump(o);
    }
    static List<Service> Bookable(Ctx x) => x.S.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND s.kind IN ('resource','day') AND l.active ORDER BY l.name, s.name").ToList();
    static string FeedLink(Call c, Resource r) => $"{c.PublicOrigin.TrimEnd('/')}/action/{c.Tenant.Slug}/feed/{DjangoSigning.SignObject("td-feed", $"[\"{r.Id}\",{r.Feed}]", c.S.Config.SecretKey)}/calendario.ics";

    static Special ResourceForm(Call c, Guid? id)
    {
        var x = c.C; var t = c.Tenant; var isNew = !id.HasValue; var r = isNew ? new Resource() : x.S.Get<Resource>(id); var f = new List<Fld>();
        var bookable = Bookable(x); var many = x.S.Scalar<long>("SELECT count(*) FROM core_location WHERE active") > 1; var appointments = bookable.Any(s => s.Kind == "resource");
        var word = Presets.ServiceWord(t.BusinessType).ToLower(It);
        f.Add(new Fld { Nome = "name", Etichetta = "Nome", Valore = r.Name, Max = 100, Aiuto = "Per esempio Marco, Cabina 1, Camera Blu, Campo A" });
        f.Add(One(x, "location", "Sede", "SELECT id, name FROM core_location WHERE active ORDER BY name", isNew ? null : r.LocationId));
        if (f[^1].Tipo == "select") f[^1].Scelte = Rows(x, "SELECT id, name FROM core_location ORDER BY name");
        if (x.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_service WHERE kind='day')")) f.Add(new Fld { Nome = "capacity", Etichetta = "Persone massime (0 = nessun limite)", Tipo = "number", Valore = r.Capacity.ToString(), Aiuto = "Serve per camere e mezzi: quante persone ci stanno" });
        f.Add(new Fld { Nome = "description", Etichetta = "Descrizione", Tipo = "area", Valore = r.Description, Aiuto = "Facoltativa: compare sotto il nome nella pagina prenotazioni", Largo = true });
        var photo = !isNew && r.PhotoAt.HasValue ? x.S.One<byte[]>("SELECT data FROM core_resourcephoto WHERE resource_id=@r", new { r = r.Id }) : null;
        f.Add(new Fld { Nome = "photo_file", Etichetta = "Foto", Tipo = "photo", Valore = photo != null ? Convert.ToBase64String(photo) : "", Aiuto = appointments ? "PNG, JPEG o WebP fino a 5 MB: compare in agenda e quando il cliente sceglie con chi prenotare" : "PNG, JPEG o WebP fino a 5 MB: compare nell'elenco e nella prenotazione" });
        if (photo != null) f.Add(new Fld { Nome = "photo_remove", Etichetta = "Togli la foto", Tipo = "check", Valore = false });
        f.Add(new Fld { Nome = "photo_url", Etichetta = "Oppure l'indirizzo HTTPS di una foto", Valore = r.PhotoUrl, Max = 500 });
        f.Add(new Fld { Nome = "active", Etichetta = "Attivo", Tipo = "check", Valore = r.Active });
        f.Add(new Fld { Nome = "secondary", Etichetta = "Si usa insieme all'operatore: cabina, lettino, macchinario, auto", Tipo = "check", Valore = r.Secondary, Aiuto = "Il cliente non la sceglie e non ha una colonna in agenda: i servizi che la spuntano si prenotano solo se è libera lei (o un'altra spuntata) insieme all'operatore" });
        f.Add(new Fld { Nome = "group", Etichetta = "Gruppo", Valore = r.Group, Max = 40, Aiuto = "Solo per cabine e macchinari, per esempio Cabina o Laser: un servizio che spunta voci di gruppi diversi ne vuole una libera per ogni gruppo. Vuoto: tutte nello stesso gruppo" });
        f.Add(new Fld { Nome = "auto_excluded", Etichetta = "Fuori da «Il primo disponibile»", Tipo = "check", Valore = r.AutoExcluded, Aiuto = "Si prenota solo se il cliente o il banco la scelgono" });
        f.Add(new Fld { Nome = "priority", Etichetta = "Ordine in «Il primo disponibile» (1 = per primo)", Tipo = "number", Valore = r.Priority.ToString(), Aiuto = "Conta solo se la sede dà «Il primo disponibile» secondo l'ordine scelto" });
        f.Add(new Fld { Nome = "feed_names", Etichetta = "Nel calendario anche il nome del cliente", Tipo = "check", Valore = r.FeedNames });
        f.Add(new Fld { Nome = "away_from", Etichetta = "Fuori ufficio dal", Tipo = "date", Valore = Iso(r.AwayFrom), Aiuto = "Vuoto: in servizio. Nei giorni fuori ufficio non si prenota con lui" });
        f.Add(new Fld { Nome = "away_until", Etichetta = "Fuori ufficio fino al", Tipo = "date", Valore = Iso(r.AwayUntil) });
        f.Add(new Fld { Nome = "away_by", Etichetta = "Lo sostituisce", Tipo = "select", Valore = r.AwayById?.ToString() ?? "", Aiuto = "Facoltativo: in quei giorni prende «Il primo disponibile» e online lo proponiamo al suo posto",
            Scelte = x.S.Query<(Guid, string)>("SELECT id, name FROM core_resource WHERE active AND NOT secondary AND id<>@i ORDER BY name", new { i = r.Id }).Select(o => (object)new { id = o.Item1.ToString(), nome = o.Item2 }).Prepend(new { id = "", nome = "Nessuno" }) });
        f.Add(new Fld { Nome = "feed_on", Etichetta = "Calendario da aggiungere al telefono", Tipo = "check", Valore = r.Feed != 0,
            Aiuto = r.Feed != 0 ? "Link privato da aggiungere al calendario del telefono: " + FeedLink(c, r) : "Un link privato con le sue prenotazioni, servizio e orario, da aggiungere a Google Calendar, Outlook o iPhone come calendario a cui iscriversi" });
        if (r.Feed != 0) f.Add(new Fld { Nome = "feed_new", Etichetta = "Cambia il link del calendario", Tipo = "check", Valore = false, Aiuto = "Il link di prima smette di funzionare: serve se è finito in mani sbagliate" });
        var busyHelp = "L'indirizzo segreto .ics del suo calendario Google, Outlook o iCloud: ogni 15 minuti i suoi impegni diventano orari occupati, senza titoli. Vuoto: nessuno";
        if (r.BusyError != "") busyHelp = "Ultima lettura non riuscita: " + r.BusyError; else if (r.BusyAt.HasValue) busyHelp += $". Letto il {x.Cal.ToLocal(r.BusyAt.Value):dd/MM} alle {x.Cal.ToLocal(r.BusyAt.Value):HH:mm}";
        f.Add(new Fld { Nome = "busy_url", Etichetta = "Calendario esterno da segnare come occupato", Valore = !isNew && r.Busy != "" ? x.DecryptText(r.Busy, "busy:" + r.Id) : "", Max = 2000, Aiuto = busyHelp });
        f.AddRange(Limits(r.Limits, "questa persona"));
        // what it does: a row a service, with the operator's own duration, price and posa
        var links = isNew ? new() : x.S.Query<Serviceresource>("SELECT * FROM core_serviceresource WHERE resource_id=@r", new { r = r.Id }).ToDictionary(l => l.ServiceId);
        var hours = isNew ? new() : x.S.Query<Schedule>("SELECT * FROM core_schedule WHERE resource_id=@r AND purpose='service' ORDER BY weekday, start", new { r = r.Id }).ToList();
        var split = Enumerable.Range(0, 7).Any(d => hours.Count(h => h.Weekday == d) > 2) || hours.Any(h => h.EveryWeeks > 1);
        var extra = new
        {
            risorsa = true, nuovo = isNew, appuntamenti = appointments, etichettaServizi = appointments ? "Cosa fa" : "Si prenota per",
            aiutoServizi = "Si prenota solo per quelli spuntati; all'inizio sono spuntati tutti." + (appointments ? $" Durata e prezzo vuoti vogliono dire quelli del {word}; con una variante valgono quelli della variante." : ""),
            servizi = bookable.Select(s =>
            {
                var l = links.GetValueOrDefault(s.Id);
                return new
                {
                    id = s.Id, nome = many ? $"{s.Name} ({x.Row<Location>(s.LocationId).Name})" : s.Name, spuntato = isNew || l != null, appuntamento = s.Kind == "resource",
                    durata = l?.Duration?.ToString() ?? "", prezzo = l?.Price?.ToString("0.00", It) ?? "", prima = l?.PauseStart?.ToString() ?? "", posa = l?.PauseMinutes?.ToString() ?? "",
                    durataDi = s.Duration.ToString(), prezzoDi = s.Price.HasValue ? Prices.Euro(s.Price).Replace(" €", "") : "—", primaDi = s.PauseStart.ToString(), posaDi = s.PauseMinutes.ToString(),
                };
            }),
            // working days only where someone works; hours already set stay visible, never deleted unseen
            orari = appointments || hours.Count > 0, spezzati = split, propri = hours.Count > 0,
            giorni = Enumerable.Range(0, 7).Select(d =>
            {
                var mine = hours.Where(h => h.Weekday == d).ToList();
                return new { nome = WEEKDAYS[d], acceso = hours.Count > 0 ? mine.Count > 0 : d < 5, inizio = mine.Count > 0 ? Hm(mine[0].Start) : "09:00", fine = mine.Count > 0 ? Hm(mine[0].End) : "18:00", inizio2 = mine.Count > 1 ? Hm(mine[1].Start) : "", fine2 = mine.Count > 1 ? Hm(mine[1].End) : "" };
            }),
        };
        return new(f, extra);
    }

    static Guid SaveResource(Call c, Guid? id, Posted p)
    {
        var x = c.C; var t = c.Tenant; var isNew = !id.HasValue; var o = isNew ? new Resource { TenantId = t.Id } : x.S.Get<Resource>(id); var bookable = Bookable(x); var appointments = bookable.Any(s => s.Kind == "resource");
        var hoursNow = isNew ? new() : x.S.Query<Schedule>("SELECT * FROM core_schedule WHERE resource_id=@r AND purpose='service'", new { r = o.Id }).ToList();
        var split = Enumerable.Range(0, 7).Any(d => hoursNow.Count(h => h.Weekday == d) > 2) || hoursNow.Any(h => h.EveryWeeks > 1); var hours = appointments || hoursNow.Count > 0;
        var name = p.Text("name", true, 100); if (name.Length > 120) p.Whole("Nome troppo lungo.");
        var loc = p.Ref("location", "core_location", true); var withCap = x.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_service WHERE kind='day')"); var cap = withCap ? p.Int("capacity", true) : null;
        var description = p.Text("description"); var photoUrl = p.Text("photo_url", false, 500);
        if (photoUrl != "" && !Uri.TryCreate(photoUrl, UriKind.Absolute, out _)) p.Error("photo_url", "Inserisci un URL valido."); else if (photoUrl != "" && !photoUrl.StartsWith("https://")) p.Error("photo_url", "Usa un indirizzo HTTPS.");
        var group = p.Text("group", false, 40); var priority = p.Int("priority", false, 0, 32767) ?? 0; var from = p.Date("away_from"); var until = p.Date("away_until");
        var awayBy = p.Ref("away_by", "core_resource", false, "active AND NOT secondary"); if (awayBy == o.Id) awayBy = null;
        var busy = p.Text("busy_url", false, 2000); if (busy != "" && !Regex.IsMatch(busy, @"^(https|webcal)://[^\s/]+", RegexOptions.IgnoreCase)) p.Error("busy_url", "Usa l'indirizzo che inizia con https:// o webcal://");
        byte[] photo = null;
        if (p.Raw("photo_file") != "")
        {
            // the window already turned it upright and made it a JPEG of 800 px at most (branding.clean_photo)
            try { photo = Convert.FromBase64String(p.Raw("photo_file")); } catch (FormatException) { photo = Array.Empty<byte>(); }
            if (photo.Length > 5 * 1024 * 1024) p.Error("photo_file", "Foto massimo 5 MB."); else if (photo.Length < 4 || photo[0] != 0xFF || photo[1] != 0xD8) p.Error("photo_file", "Usa una foto PNG, JPEG o WebP valida, massimo 40 megapixel.");
        }
        var chosen = p.Refs("services", "core_service").Where(s => bookable.Any(b => b.Id == s)).Select(s => bookable.First(b => b.Id == s)).ToList();
        var own = new Dictionary<Guid, (int? Dur, decimal? Price, int? At, int? Pause)>();
        foreach (var s in bookable.Where(s => s.Kind == "resource"))
            own[s.Id] = (p.Int($"dur_{s.Id}", false, 5, 1440), p.Dec($"price_{s.Id}") is decimal pr ? pr is < 0 or > 100000 ? Bad($"price_{s.Id}") : pr : null, p.Int($"pa_{s.Id}", false, 0, 1440), p.Int($"pm_{s.Id}", false, 0, 1440));
        decimal? Bad(string field) { p.Error(field, "Assicurati che questo valore sia compreso tra 0 e 100000."); return null; }
        var limits = ReadLimits(p);
        // clean()
        if (loc.HasValue && chosen.Any(s => s.LocationId != loc)) p.Whole("Spunta solo servizi della sua sede.");
        foreach (var s in bookable.Where(s => s.Kind == "resource"))
        {
            var k = own[s.Id]; if (k.Pause == null) continue;
            var at = k.At ?? s.PauseStart; var whole = k.Dur is > 0 ? k.Dur.Value : s.Duration;
            if (k.Pause > 0 && !(0 < at && at + k.Pause < whole)) p.Error($"pm_{s.Id}", $"{s.Name}: la posa sta dentro la durata, dopo un po' di lavoro e prima della finitura.");
        }
        if (from.HasValue != until.HasValue || from.HasValue && until < from) p.Error("away_until", "Scrivi dal e fino al, con la fine non prima dell'inizio.");
        if (awayBy.HasValue && loc.HasValue && x.Row<Resource>(awayBy).LocationId != loc) p.Error("away_by", "Scegli qualcuno della stessa sede.");
        var ownHours = p.Bool("own_hours"); var days = new List<(int D, TimeSpan A, TimeSpan B, TimeSpan? A2, TimeSpan? B2)>();
        if (ownHours && hours && !split)
        {
            var ticked = Enumerable.Range(0, 7).Where(n => p.Bool($"day_{n}")).ToList();
            if (ticked.Count == 0) p.Whole("Spunta almeno un giorno di lavoro, o spegni Ha giorni e orari suoi.");
            foreach (var n in ticked)
            {
                var a = p.Time($"start_{n}"); var b = p.Time($"end_{n}"); var a2 = p.Time($"start2_{n}"); var b2 = p.Time($"end2_{n}");
                if (!a.HasValue || !b.HasValue || b <= a) { p.Whole($"{WEEKDAYS[n]}: l'orario di fine viene dopo quello di inizio."); break; }
                if ((a2.HasValue || b2.HasValue) && (!a2.HasValue || !b2.HasValue || b2 <= a2)) { p.Whole($"{WEEKDAYS[n]}: la seconda fascia vuole inizio e fine, con la fine dopo l'inizio."); break; }
                if (a2.HasValue && a2 < b && a < b2) { p.Whole($"{WEEKDAYS[n]}: le due fasce si sovrappongono."); break; }
                days.Add((n, a.Value, b.Value, a2, b2));
            }
        }
        p.Check();
        // save()
        var oldBusy = !isNew && o.Busy != "" ? x.DecryptText(o.Busy, "busy:" + o.Id) : "";
        // a new random version: the links given before stop working, also those of a feed switched off and on again
        if (p.Bool("feed_on") && (o.Feed == 0 || p.Bool("feed_new"))) o.Feed = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        if (!p.Bool("feed_on")) o.Feed = 0;
        (o.Name, o.LocationId, o.Description, o.PhotoUrl, o.Active, o.FeedNames, o.Secondary, o.Group, o.AutoExcluded, o.Priority, o.AwayFrom, o.AwayUntil, o.AwayById, o.Limits) =
            (name, loc.Value, description, photoUrl, p.Bool("active"), p.Bool("feed_names"), p.Bool("secondary"), group, p.Bool("auto_excluded"), priority, from?.ToDateTime(TimeOnly.MinValue), until?.ToDateTime(TimeOnly.MinValue), awayBy, limits);
        if (withCap) o.Capacity = cap.Value;
        if (isNew) x.S.Insert(o); else x.S.Update(o, "name", "location_id", "capacity", "description", "photo_url", "active", "feed_names", "secondary", "group", "auto_excluded", "priority", "away_from", "away_until", "away_by_id", "limits", "feed");
        if (busy != oldBusy)
        {
            o.Busy = busy != "" ? x.Encrypt(JsonValue.Create(busy), "busy:" + o.Id) : ""; o.BusyAt = null; o.BusyError = ""; x.S.Update(o, "busy", "busy_at", "busy_error");
            x.S.Execute("DELETE FROM core_timeblock WHERE resource_id=@r AND reason='busy'", new { r = o.Id });
        }
        // the links are kept, with their own duration and price: only the unticked go, the ticked are added
        x.S.Execute("DELETE FROM core_serviceresource WHERE resource_id=@r AND NOT (service_id = ANY(@s))", new { r = o.Id, s = chosen.Select(s => s.Id).ToArray() });
        foreach (var s in chosen)
        {
            var link = x.S.One<Serviceresource>("SELECT * FROM core_serviceresource WHERE service_id=@s AND resource_id=@r", new { s = s.Id, r = o.Id });
            if (link == null) { link = new Serviceresource { TenantId = t.Id, ServiceId = s.Id, ResourceId = o.Id }; x.S.Insert(link); }
            if (s.Kind != "resource") continue;
            var k = own[s.Id]; (link.Duration, link.Price, link.PauseMinutes, link.PauseStart) = (k.Dur, k.Price, k.Pause, k.Pause.HasValue ? k.At : null);
            x.S.Update(link, "duration", "price", "pause_start", "pause_minutes");
        }
        if (photo != null)
        {
            x.S.Execute("DELETE FROM core_resourcephoto WHERE resource_id=@r", new { r = o.Id });
            x.S.Insert(new Resourcephoto { TenantId = t.Id, ResourceId = o.Id, Data = photo }); o.PhotoAt = x.Now; x.S.Update(o, "photo_at");
        }
        else if (p.Bool("photo_remove")) { x.S.Execute("DELETE FROM core_resourcephoto WHERE resource_id=@r", new { r = o.Id }); o.PhotoAt = null; x.S.Update(o, "photo_at"); }
        if (hours && !split)
        {
            x.S.Execute("DELETE FROM core_schedule WHERE resource_id=@r AND purpose='service'", new { r = o.Id });
            if (ownHours)
                foreach (var d in days)
                {
                    x.S.Insert(new Schedule { TenantId = t.Id, ResourceId = o.Id, Weekday = (short)d.D, Start = d.A, End = d.B });
                    if (d.A2.HasValue) x.S.Insert(new Schedule { TenantId = t.Id, ResourceId = o.Id, Weekday = (short)d.D, Start = d.A2.Value, End = d.B2.Value });
                }
        }
        return o.Id;
    }
}
