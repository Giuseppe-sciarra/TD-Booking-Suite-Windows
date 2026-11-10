using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>stays.stays_view, stay_files and guests_view: Alloggi: the day (arrivals, departures, in house, breakfasts), the
/// rooms' cleaning, the portals' calendars, the rules for a period, the files for the authorities, and the guests of a stay.</summary>
public static class Alloggi
{
    static readonly CultureInfo INV = CultureInfo.InvariantCulture, IT = CultureInfo.GetCultureInfo("it-IT");
    static readonly Dictionary<string, string> HOUSEKEEPING = new() { ["clean"] = "Pulita", ["dirty"] = "Da pulire", ["out"] = "Fuori servizio" };

    static List<Resource> UnitsOf(Call c)
    {
        var rows = c.C.S.Query<Resource>(@"SELECT r.* FROM core_resource r JOIN core_location l ON l.id=r.location_id WHERE r.active AND r.id IN
            (SELECT sr.resource_id FROM core_serviceresource sr JOIN core_service s ON s.id=sr.service_id WHERE s.kind='day') ORDER BY l.name, r.name").ToList();
        var locs = c.Member != null ? Permissions.Ids(c.Member.Locations) : new List<string>();
        return locs.Count > 0 ? rows.Where(r => locs.Contains(r.LocationId.ToString())).ToList() : rows;
    }
    static T Pick<T>(IEnumerable<T> rows, Guid? id, Func<T, Guid> key) where T : class => rows.FirstOrDefault(x => key(x) == id) ?? throw new KeyNotFoundException();
    static DateOnly DayArg(Call c, string key, DateOnly fallback) => DateOnly.TryParseExact(c.Str(key), "yyyy-MM-dd", INV, DateTimeStyles.None, out var d) ? d : fallback;
    static bool WsEditor(Call c) => c.User.IsStaff || c.Tenant.AlloggiatiSelf && c.Member?.Role == "owner";
    /// <summary>The stays still to come on a unit out of service: they are to be moved.</summary>
    static List<Booking> Conflicts(Call c, Resource unit, bool visibleOnly)
    {
        var (vis, args) = Permissions.VisibleBookings(c.Member, c.Perms, "b"); var p = new Dapper.DynamicParameters(args); p.Add("r", unit.Id); p.Add("st", Stays.STAYING); p.Add("n", c.C.Now);
        return c.C.S.Query<Booking>($"SELECT b.* FROM core_booking b WHERE b.resource_id=@r AND b.status = ANY(@st) AND b.\"end\">@n {(visibleOnly ? $"AND ({vis})" : "")} ORDER BY b.start", p).ToList();
    }
    static string Short(DateOnly d) => $"{d.Day}/{d.Month:00}";

    [Rpc("alloggi", Perm = "booking.read")]
    public static object Page(Call c)
    {
        var x = c.C; var t = c.Tenant; var stays = new Stays(x); var bk = new Bookings(x); var today = x.Today; var day = DayArg(c, "giorno", today); var units = UnitsOf(c);
        var (vis, vargs) = Permissions.VisibleBookings(c.Member, c.Perms, "b");
        var rows = stays.Between(day.AddDays(-1), day, null, vis, vargs).Select(b =>
        {
            var who = Permissions.Shown(c.Perms, bk.Data(b)); var guests = (who["guests"] as JsonArray)?.Count ?? 0; var service = x.Row<Service>(b.ServiceId);
            return new
            {
                B = b, First = x.Cal.DayOf(b.Start), Last = x.Cal.DayOf(b.End), Who = who,
                Item = new
                {
                    id = b.Id, unita = b.Snap.Str("resource") != "" ? b.Snap.Str("resource") : service.Name, nome = $"{who.Str("first_name")} {who.Str("last_name")}".Trim(), persone = b.Party, dal = Short(x.Cal.DayOf(b.Start)), al = Short(x.Cal.DayOf(b.End)),
                    mascherato = Json.Truthy(who["masked"]), dati = guests, inviato = b.Snap.Str("alloggiati") != "", errore = b.Snap.Str("alloggiati_error"),
                    allergie = string.Join(", ", (who["allergies"] as JsonArray ?? new JsonArray()).Select(a => a?.ToString() ?? "")),
                },
            };
        }).ToList();
        var year = int.TryParse(c.Str("anno"), out var y) && y is >= 2000 and <= 2100 ? y : today.Year;
        var locations = x.S.Query<Location>("SELECT * FROM core_location WHERE active ORDER BY name").ToList(); var categories = Stays.TAX_CATEGORIES.ToDictionary(k => k.Id, k => k.Label);
        var files = c.Can("pii.read") && c.Can("export.operational"); var conf = stays.WsConf(); var breakfast = rows.Where(r => r.First < day && day <= r.Last).ToList();  // who slept here the night before
        string Days(JsonNode list) => string.Join(", ", (list as JsonArray ?? new JsonArray()).Select(d => Stays.WEEKDAY_NAMES[(int)d]));
        var taxed = locations.Any(l => l.TaxRate.HasValue || Json.Arr(l.TaxRules).Count > 0);
        return new
        {
            giorno = day.ToString("yyyy-MM-dd"), oggi = today.ToString("yyyy-MM-dd"), ieri = today.AddDays(-1).ToString("yyyy-MM-dd"), mese = today.ToString("yyyy-MM"), anno = year, titoloColazioni = day.ToString("d MMMM", IT).ToLowerInvariant(),
            liste = new[]
            {
                new { titolo = "Arrivi", vuoto = "Nessun arrivo.", righe = rows.Where(r => r.First == day).Select(r => r.Item) }, new { titolo = "Partenze", vuoto = "Nessuna partenza.", righe = rows.Where(r => r.Last == day).Select(r => r.Item) },
                new { titolo = "In casa", vuoto = "Nessuno in casa.", righe = rows.Where(r => r.First < day && day < r.Last).Select(r => r.Item) },
            },
            colazioni = breakfast.Select(r => r.Item), colazioniPersone = breakfast.Sum(r => r.B.Party), noteNascoste = breakfast.Count > 0 && Json.Truthy(breakfast[0].Who["notes_hidden"]),
            unita = units.Select(u => new
            {
                id = u.Id, nome = u.Name, stato = u.Housekeeping, statoNome = HOUSEKEEPING.GetValueOrDefault(u.Housekeeping ?? "", u.Housekeeping), link = stays.FeedLink(u), indirizzi = c.Can("catalog.write") ? string.Join("\n", stays.Feeds(u)) : "",
                letto = u.IcalAt.HasValue ? x.Cal.ToLocal(u.IcalAt.Value).ToString("dd/MM HH:mm") : "", errore = u.IcalError ?? "",
                daSpostare = u.Housekeeping == "out" ? Conflicts(c, u, true).Take(20).Select(b => new { id = b.Id, dal = Short(x.Cal.DayOf(b.Start)), al = Short(x.Cal.DayOf(b.End)) }) : null,
            }),
            servizi = x.S.Query<Service>("SELECT * FROM core_service WHERE kind='day' AND active ORDER BY name").Select(s => new
            {
                id = s.Id, nome = s.Name,
                regole = Json.Arr(s.StayRules).OfType<JsonObject>().Select(r => $"dal {r.Str("from")} al {r.Str("until")}" + (((int?)r["min"] ?? 0) > 0 ? $" · almeno {(int)r["min"]} notti" : "")
                    + ((r["no_arrival"] as JsonArray)?.Count > 0 ? " · niente arrivi: " + Days(r["no_arrival"]) : "") + ((r["no_departure"] as JsonArray)?.Count > 0 ? " · niente partenze: " + Days(r["no_departure"]) : "")),
            }),
            giorni = Stays.WEEKDAY_NAMES, categorie = Stays.TAX_CATEGORIES.Select(k => new { id = k.Id, nome = k.Label }),
            sedi = locations.Select(l => new
            {
                id = l.Id, nome = l.Name, categoria = l.TaxCategory ?? "", notti = (int)l.TaxYearNights, accompagnatori = (int)l.TaxEscort, riduzione = (int)l.TaxReduction,
                regole = Json.Arr(l.TaxRules).OfType<JsonObject>().Select(r => (Json.Truthy(r["yearly"]) ? $"ogni anno dal {r.Str("from")[8..]}/{r.Str("from")[5..7]} al {r.Str("until")[8..]}/{r.Str("until")[5..7]}" : $"dal {r.Str("from")} al {r.Str("until")}")
                    + $" · {categories.GetValueOrDefault(r.Str("category"), "tutte le categorie")} · € {r.Str("rate")} a notte" + (((int?)r["nights"] ?? 0) > 0 ? $" · al massimo {(int)r["nights"]} notti consecutive" : "")),
            }),
            imposta = !taxed ? null : stays.Tax(year).Select(row => new
            {
                sede = row.Location.Name, ignoti = row.Unknown, motivi = string.Join("; ", row.Codes.Select(k => $"{k.Key} {k.Value}")),
                trimestri = row.Quarters.Select(q => new
                {
                    q = q.Q, tariffe = Stays.RatesText(q.Rates), notti = string.Join("; ", q.Rates.Values), ridotta = Stays.RatesText(q.Reduced), nottiRidotte = q.Reduced.Values.Sum(), esenti = q.Exempt, imposta = q.Amount.ToString("0.00", IT),
                }),
                totale = new { ordinarie = row.Quarters.Sum(q => q.Rates.Values.Sum()), ridotte = row.Quarters.Sum(q => q.Reduced.Values.Sum()), esenti = row.Quarters.Sum(q => q.Exempt), imposta = row.Quarters.Sum(q => q.Amount).ToString("0.00", IT) },
            }),
            file = files, ws = conf.Count == 0 ? null : new { utente = conf.Str("user"), auto = Json.Truthy(conf["auto"]), errore = conf.Str("error") }, wsEditor = WsEditor(c),
            ricevute = files && conf.Count > 0 ? x.S.Query<Receipt>("SELECT id, day, rows, CASE WHEN pdf IS NULL THEN NULL ELSE ''::bytea END AS pdf, tenant_id FROM core_receipt ORDER BY day DESC LIMIT 60").Select(r => new { giorno = r.Day.ToString("yyyy-MM-dd"), data = r.Day.ToString("dd/MM/yyyy"), righe = r.Rows, pdf = r.Pdf != null }) : null,
        };
    }

    [Rpc("alloggi.azione", Perm = "booking.read")]
    public static object Action(Call c)
    {
        var x = c.C; var t = c.Tenant; var stays = new Stays(x); var units = UnitsOf(c); var action = c.Str("azione");
        try
        {
            switch (action)
            {
                case "room":
                    {
                        c.Require("booking.write"); var u = Pick(units, c.Id("unit"), r => r.Id);
                        if (HOUSEKEEPING.ContainsKey(c.Str("status"))) { u.Housekeeping = c.Str("status"); x.S.Update(u, "housekeeping"); }
                        var n = u.Housekeeping == "out" ? Conflicts(c, u, false).Count : 0;
                        return new { avviso = n > 0 ? $"{u.Name} è fuori servizio: non si prenota più. Ha {n} prenotazioni da spostare, le trovi sotto Camere e pulizie." : "" };
                    }
                case "feeds" or "refresh":
                    {
                        c.Require("catalog.write"); var u = Pick(units, c.Id("unit"), r => r.Id);
                        if (action == "feeds") { stays.SetFeeds(u, c.Str("urls")); x.Audit("stays.feeds", u.Id); }
                        return new { testo = stays.Refresh(u) ? $"Calendari di {u.Name} letti adesso." : $"{u.Name}: un calendario non risponde, restano le notti lette l'ultima volta." };
                    }
                case "rule_add" or "rule_del":
                    {
                        c.Require("catalog.write"); var s = Pick(x.S.Query<Service>("SELECT * FROM core_service WHERE kind='day'"), c.Id("service"), r => r.Id); var rules = Json.Arr(s.StayRules);
                        if (action == "rule_del") { if (int.TryParse(c.Str("rule"), out var i) && i >= 0 && i < rules.Count) rules.RemoveAt(i); }
                        else
                        {
                            if (!DateOnly.TryParseExact(c.Str("from"), "yyyy-MM-dd", INV, DateTimeStyles.None, out var a) || !DateOnly.TryParseExact(c.Str("until"), "yyyy-MM-dd", INV, DateTimeStyles.None, out var z)) throw new BookingException("Scegli le date del periodo.");
                            if (z < a) throw new BookingException("Il periodo finisce prima di iniziare.");
                            var n = c.Str("min") == "" ? "0" : c.Str("min"); if (!n.All(char.IsAsciiDigit) || n.Length > 3 || int.Parse(n) > 60) throw new BookingException("Soggiorno minimo non valido.");
                            JsonArray Days(string key) => new((c.Args[key] as JsonArray ?? new JsonArray()).Select(v => v?.ToString() ?? "").Where(v => v.Length == 1 && "0123456".Contains(v)).Select(int.Parse).Distinct().OrderBy(v => v).Select(v => (JsonNode)v).ToArray());
                            rules.Add(new JsonObject { ["from"] = a.ToString("yyyy-MM-dd"), ["until"] = z.ToString("yyyy-MM-dd"), ["min"] = int.Parse(n), ["no_arrival"] = Days("no_arrival"), ["no_departure"] = Days("no_departure") });
                        }
                        s.StayRules = Json.Dump(rules); x.S.Update(s, "stay_rules"); x.Audit("stays.rules", s.Id); return new { };
                    }
                case "tax_location":
                    {
                        c.Require("catalog.write"); var loc = Pick(x.S.Query<Location>("SELECT * FROM core_location"), c.Id("location"), r => r.Id); var cat = c.Str("tax_category");
                        if (cat != "" && !Stays.TAX_CATEGORIES.Any(k => k.Id == cat)) throw new BookingException("Categoria non valida.");
                        int Whole(string key) => c.Str(key) == "" ? 0 : int.TryParse(c.Str(key), NumberStyles.AllowLeadingSign, INV, out var v) ? v : throw new BookingException("Scrivi numeri interi.");
                        int n = Whole("tax_year_nights"), e = Whole("tax_escort"), r = Whole("tax_reduction");
                        if (!(n is >= 0 and <= 366 && e is >= 0 and <= 100 && r is >= 0 and <= 100)) throw new BookingException("Valori fuori misura.");
                        loc.TaxCategory = cat; loc.TaxYearNights = (short)n; loc.TaxEscort = (short)e; loc.TaxReduction = (short)r;
                        x.S.Update(loc, "tax_category", "tax_year_nights", "tax_escort", "tax_reduction"); x.Audit("stays.tax", loc.Id); return new { };
                    }
                case "tax_add" or "tax_del":
                    {
                        c.Require("catalog.write"); var loc = Pick(x.S.Query<Location>("SELECT * FROM core_location"), c.Id("location"), r => r.Id); var rules = Json.Arr(loc.TaxRules);
                        if (action == "tax_del") { if (int.TryParse(c.Str("rule"), out var i) && i >= 0 && i < rules.Count) rules.RemoveAt(i); }
                        else
                        {
                            if (!DateOnly.TryParseExact(c.Str("from"), "yyyy-MM-dd", INV, DateTimeStyles.None, out var a) || !DateOnly.TryParseExact(c.Str("until"), "yyyy-MM-dd", INV, DateTimeStyles.None, out var z)
                                || !decimal.TryParse(c.Str("rate").Replace(',', '.'), NumberStyles.Number, INV, out var rate)) throw new BookingException("Scegli le date e scrivi la tariffa, per esempio 6,00.");
                            var yearly = c.Bool("yearly");
                            if (z < a && !yearly) throw new BookingException("Il periodo finisce prima di iniziare.");
                            if (rate < 0 || rate > 100) throw new BookingException("Tariffa non valida.");
                            var n = c.Str("nights") == "" ? "0" : c.Str("nights"); if (!n.All(char.IsAsciiDigit) || n.Length > 4 || int.Parse(n) > 366) throw new BookingException("Notti massime non valide.");
                            if (c.Str("category") != "" && !Stays.TAX_CATEGORIES.Any(k => k.Id == c.Str("category"))) throw new BookingException("Categoria non valida.");
                            rules.Add(new JsonObject { ["category"] = c.Str("category"), ["from"] = a.ToString("yyyy-MM-dd"), ["until"] = z.ToString("yyyy-MM-dd"), ["yearly"] = yearly, ["rate"] = rate.ToString("0.00", INV), ["nights"] = int.Parse(n) });
                        }
                        loc.TaxRules = Json.Dump(rules); x.S.Update(loc, "tax_rules"); x.Audit("stays.tax", loc.Id); return new { };
                    }
                case "ws_send":
                    {
                        c.Require("pii.read"); c.Require("export.operational");
                        var (n, errors) = stays.WsSend(DateOnly.ParseExact(c.Str("day"), "yyyy-MM-dd", INV));
                        return new
                        {
                            testo = n > 0 ? $"Inviati ad Alloggiati Web: {n} soggiorni. I dati dei documenti sono stati cancellati, la ricevuta arriva da domani." : errors.Count == 0 ? "Niente da inviare: gli arrivi del giorno sono già inviati o senza i dati degli ospiti." : "",
                            errori = errors.Select(e => "Rifiutato da Alloggiati Web, " + e),
                        };
                    }
                case "sent":
                    c.Require("pii.read"); c.Require("export.operational");
                    return new { testo = $"Fatto: documenti cancellati per {stays.Sent(DateOnly.ParseExact(c.Str("day"), "yyyy-MM-dd", INV))} prenotazioni." };
            }
        }
        catch (Stays.WSError ex) { throw new BookingException(ex.Message); }
        catch (FormatException ex) { throw new BookingException(ex.Message); }
        throw new BookingException("Operazione non valida.");
    }

    /// <summary>The agency sets the Alloggiati Web credentials, the owner too when the agency allows it.</summary>
    [Rpc("alloggi.credenziali", Perm = "booking.read", Fresh = true)]
    public static object Credentials(Call c)
    {
        if (!WsEditor(c)) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
        var stays = new Stays(c.C); stays.WsSave(Stays.WsClean(k => c.Str(k), c.Bool("ws_off"), c.Bool("ws_auto"), stays.WsConf()));
        return new { testo = "Alloggiati Web salvato." };
    }

    /// <summary>The files for the authorities, downloaded: Alloggiati Web (arrivals of today or yesterday), Ross1000 (a month), a receipt.</summary>
    [Rpc("alloggi.file", Perm = "pii.read")]
    public static object File(Call c)
    {
        c.Require("export.operational"); var x = c.C; var stays = new Stays(x); var what = c.Str("cosa"); byte[] body; string name; int missing = 0; var today = x.Today;
        const string BAD = "Scegli un giorno (oggi o ieri) o un mese valido.";
        if (what == "alloggiati")
        {
            if (!DateOnly.TryParseExact(c.Str("giorno"), "yyyy-MM-dd", INV, DateTimeStyles.None, out var day) || day != today && day != today.AddDays(-1)) throw new BookingException(BAD);
            var (text, n) = stays.Alloggiati(day); body = Encoding.ASCII.GetBytes(text); missing = n; name = $"alloggiati-{day:yyyyMMdd}.txt";
        }
        else if (what == "ricevuta")
        {
            if (!DateOnly.TryParseExact(c.Str("giorno"), "yyyy-MM-dd", INV, DateTimeStyles.None, out var day)) throw new BookingException(BAD);
            var r = x.S.One<Receipt>("SELECT * FROM core_receipt WHERE day=@d AND pdf IS NOT NULL", new { d = day.ToDateTime(TimeOnly.MinValue) }) ?? throw new KeyNotFoundException();
            return new { nome = $"ricevuta-alloggiati-{day:yyyyMMdd}.pdf", dati = Convert.ToBase64String(r.Pdf), avviso = "" };
        }
        else if (what == "ross1000")
        {
            var parts = c.Str("mese").Split('-'); if (parts.Length != 2 || !int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || y is < 2000 or > 2100 || m is < 1 or > 12) throw new BookingException(BAD);
            var loc = c.Id("sede") is Guid id ? x.S.Get<Location>(id) : null; if (loc == null) throw new KeyNotFoundException();
            var (text, n) = stays.Ross1000(loc, y, m); body = Encoding.UTF8.GetBytes(text); missing = n; name = $"ross1000-{y}{m:00}.xml";
        }
        else throw new KeyNotFoundException();
        x.Audit("stays.file", what, new { missing });
        return new { nome = name, dati = Convert.ToBase64String(body), avviso = missing > 0 ? $"Nel file mancano {missing} prenotazioni senza i dati degli ospiti: completale e scaricalo di nuovo." : "" };
    }

    static Booking Stay(Call c)
    {
        var b = Prenotazioni.Visible(c, c.NeedId("id")); if (c.C.Row<Service>(b.ServiceId).Kind != "day") throw new KeyNotFoundException();
        return b;
    }

    /// <summary>guests_view: the guests of a stay at the desk: the forms as saved, one for each person.</summary>
    [Rpc("alloggi.ospiti", Perm = "booking.write")]
    public static object Guests(Call c)
    {
        c.Require("pii.read"); var x = c.C; var b = Stay(c); var data = new Bookings(x).Data(b); var saved = (data["guests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList(); var service = x.Row<Service>(b.ServiceId);
        return new
        {
            id = b.Id, servizio = b.Snap.Str("service") != "" ? b.Snap.Str("service") : service.Name, risorsa = b.Snap.Str("resource"), cliente = $"{data.Str("first_name")} {data.Str("last_name")}".Trim(), persone = b.Party,
            dal = x.Cal.DayOf(b.Start).ToString("dd/MM/yyyy"), al = x.Cal.DayOf(b.End).ToString("dd/MM/yyyy"),
            righe = Enumerable.Range(0, b.Party).Select(i => i < saved.Count ? (JsonNode)saved[i].DeepClone() : new JsonObject { ["kind"] = b.Party == 1 ? "16" : i == 0 ? "17" : "19" }),
            tipi = Stays.KINDS.Select(k => new { id = k.Id, nome = k.Label }), documenti = Stays.Documents().Select(k => new { id = k.Id, nome = k.Label }), capi = Stays.HEADS,
            esenzioni = Stays.EXEMPTIONS.Select(k => new { id = k.Id, nome = k.Label }), turismo = Stays.TOURISM.Select(v => new { id = v.ToUpperInvariant(), nome = v }), trasporto = Stays.TRANSPORT.Select(v => new { id = v.ToUpperInvariant(), nome = v }),
            turismoScelto = data.Str("tourism"), trasportoScelto = data.Str("transport"),
        };
    }

    [Rpc("alloggi.ospiti.salva", Perm = "booking.write")]
    public static object GuestsSave(Call c)
    {
        c.Require("pii.read"); var x = c.C; var b = Stay(c); var posted = c.Obj("valori") ?? new JsonObject(); string Post(string k) => posted.Str(k);
        var guests = Stays.ReadGuests(Post, b.Party); if (guests.Count == 0) throw new BookingException("Scrivi almeno un ospite.");
        new Stays(x).SaveGuests(b, guests, Stays.ReadTrip(Post));
        return new { testo = "Dati degli ospiti salvati." };
    }
}
