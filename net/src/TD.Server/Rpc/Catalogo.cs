using System.Globalization;
using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.catalog, catalog_edit, calendar_remove, remove_dates: the lists of Configura l'attività (sedi, orari,
/// chiusure, servizi, risorse, varianti, turni, date, prodotti), their forms (core/forms.py) and Rimuovi / Elimina.</summary>
public static partial class Catalogo
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    public static readonly (string Kind, string Title)[] KINDS =
    {
        ("locations", "Sedi"), ("schedules", "Orari di apertura"), ("exceptions", "Chiusure e giorni speciali"), ("services", "Servizi"), ("resources", "Risorse"), ("options", "Varianti dei servizi"),
        ("turns", "Turni del ristorante"), ("occurrences", "Eventi con data"), ("products", "Prodotti"),
    };
    static readonly Dictionary<string, string> EDIT_TITLES = new() { ["locations"] = "sede", ["schedules"] = "fascia oraria", ["exceptions"] = "chiusura o giorno speciale", ["services"] = "servizio", ["resources"] = "risorsa", ["options"] = "variante", ["turns"] = "turno", ["occurrences"] = "data dell'evento", ["products"] = "prodotto" };
    static readonly Dictionary<string, string> EDIT_HELP = new()
    {
        ["locations"] = "Il nome con cui riconosci la sede, l'indirizzo e il telefono che compaiono nelle email di conferma",
        ["schedules"] = "Un giorno della settimana con un orario di apertura. Per pranzo e cena crea due fasce. Le prenotazioni già fatte non vengono mai annullate",
        ["exceptions"] = "Uno o più giorni di chiusura, o con un orario diverso dal solito: valgono più degli orari di apertura. Le prenotazioni già fatte non vengono mai annullate",
        ["services"] = "Quello che il cliente sceglie quando prenota. Compaiono solo le impostazioni che servono a questo tipo di prenotazione",
        ["resources"] = "Una persona o una cosa che si prenota: un operatore, una cabina, una camera, un campo",
        ["options"] = "La stessa prestazione con una durata diversa, per esempio Taglio corto e Taglio lungo",
        ["turns"] = "Un turno che si ripete ogni settimana, per esempio la cena del venerdì dalle 19:30 alle 23:00, con i suoi coperti",
        ["occurrences"] = "Una data dell'evento o del corso, con i posti disponibili. Con Ripeti crei in un colpo tutte le date della stagione",
        ["products"] = "Un prodotto che vendi in Cassa e che metti nei preventivi",
    };
    static readonly Dictionary<string, string> REMOVABLE = new() { ["locations"] = "Sede", ["services"] = "Servizio", ["resources"] = "Risorsa", ["options"] = "Variante", ["turns"] = "Turno", ["occurrences"] = "Data", ["products"] = "Prodotto" };
    public static readonly string[] WEEKDAYS = { "Lunedì", "Martedì", "Mercoledì", "Giovedì", "Venerdì", "Sabato", "Domenica" };
    static readonly (string, string)[] PURPOSES = { ("service", "Apertura"), ("receive", "Invio online"), ("arrival", "Arrivo / ritiro"), ("departure", "Partenza / consegna") };

    static void Allowed(Call c, string kind)
    {
        if (!KINDS.Any(k => k.Kind == kind) || !Configura.CatalogAllowed(c, kind)) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
    }
    static string Hm(TimeSpan? t) => t.HasValue ? t.Value.ToString(@"hh\:mm") : "";
    static string D(DateTime d) => d.ToString("dd/MM/yyyy");
    static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";
    static string ScheduleLabel(Schedule o) => $"{WEEKDAYS[o.Weekday]}, {Hm(o.Start)}–{Hm(o.End)}" + (o.EveryWeeks > 1 && o.ValidFrom.HasValue ? $", ogni {o.EveryWeeks} settimane dal {D(o.ValidFrom.Value)}" : "");
    static string ExceptionLabel(Exceptionday o) => (o.Until.HasValue && o.Until > o.Date ? $"Dal {D(o.Date)} al {D(o.Until.Value)}" : D(o.Date)) + ", " + (o.Closed ? "chiuso" : Hm(o.Start) + "–" + Hm(o.End)) + (o.Label != "" ? ", " + o.Label : "");
    /// <summary>service_summary: what a service is, in words, under its name in the list.</summary>
    static string Summary(Service s)
    {
        var last = s.Kind == "capacity" && s.LastArrival > 0 ? $", ultimo arrivo {s.LastArrival} minuti prima della chiusura" : "";
        return s.Kind switch
        {
            "capacity" when !s.Turnover => $"{s.Capacity} coperti per ogni pranzo e cena, il cliente sceglie l'ora di arrivo" + last,
            "capacity" => $"A turni: {s.Capacity} posti, ogni posto si libera dopo {s.Duration} minuti" + last,
            "shift" => $"Turni: {s.Capacity} posti totali, i coperti di ogni turno sono in Turni del ristorante",
            "resource" => $"Appuntamento di {s.Duration} minuti" + (s.ResourceLabel != "" ? " con " + s.ResourceLabel.ToLower(It) : "") + (s.Price.HasValue ? " · " + Prices.Euro(s.Price) : ""),
            "day" => $"A notti: {(s.MinDays > 0 ? $"da {s.MinDays} a" : "anche in giornata, fino a")} {s.MaxDays}, arrivo alle {Hm(s.Checkin)}",
            "event" => "Evento con date: i posti sono in Eventi con data",
            _ => "",
        };
    }
    static IEnumerable<object> Tabs(Call c)
    {
        var hidden = Configura.Hidden(c.C);
        return KINDS.Where(k => !hidden.Contains(k.Kind) && Configura.CatalogAllowed(c, k.Kind)).Select(k => (object)new { chiave = k.Kind, titolo = k.Kind == "resources" ? Presets.UnitsOf(c.Tenant.BusinessType).Many : k.Title });
    }

    [Rpc("catalogo", Perm = "catalog.write")]
    public static object List(Call c)
    {
        var kind = c.Str("tipo"); Allowed(c, kind); var x = c.C; var t = c.Tenant; const int SIZE = 50; var table = Profile.TableOf(kind);
        var title = KINDS.First(k => k.Kind == kind).Title; var help = Configura.CATALOG_HELP[kind];
        if (kind == "resources") (_, title, help) = Presets.UnitsOf(t.BusinessType);
        // active items first, by name or by date; the ones kept for old bookings at the end
        var (from, order) = kind switch
        {
            "schedules" => ("core_schedule r", "r.weekday, r.start"), "exceptions" => ("core_exceptionday r", "r.date DESC"), "occurrences" => ("core_occurrence r", "r.active DESC, r.start"),
            "services" or "resources" => ($"{table} r JOIN core_location l ON l.id=r.location_id", "r.active DESC, l.name, r.name"), _ => (table + " r", "r.active DESC, r.name"),
        };
        var count = x.S.Scalar<long>($"SELECT count(*) FROM {table}"); var pages = Math.Max(1, (int)((count + SIZE - 1) / SIZE)); var page = Math.Clamp(c.Int("pagina", 1), 1, pages);
        var ids = x.S.Query<Guid>($"SELECT r.id FROM {from} ORDER BY {order}, r.id LIMIT {SIZE} OFFSET {(page - 1) * SIZE}").ToList();
        string Only(Guid? l, Guid? s, Guid? r) => l.HasValue ? x.Row<Location>(l).Name : s.HasValue ? x.Row<Service>(s).Name : r.HasValue ? x.Row<Resource>(r).Name : "";
        object Line(Guid id)
        {
            string label; var small = new List<string>(); bool? active = null; var series = false; string photo = null;
            switch (kind)
            {
                case "locations": { var o = x.Row<Location>(id); label = o.Name; if (o.Address != "") small.Add(o.Address); active = o.Active; break; }
                case "schedules": { var o = x.S.Get<Schedule>(id); label = ScheduleLabel(o); if (Only(o.LocationId, o.ServiceId, o.ResourceId) is var w && w != "") small.Add("Solo per " + w); break; }
                case "exceptions": { var o = x.S.Get<Exceptionday>(id); label = ExceptionLabel(o); if (Only(o.LocationId, o.ServiceId, o.ResourceId) is var w && w != "") small.Add("Solo per " + w); break; }
                case "services": { var o = x.Row<Service>(id); label = o.Name; small.Add(Summary(o)); small.Add("Sede: " + x.Row<Location>(o.LocationId).Name); active = o.Active; break; }
                case "resources":
                    {
                        var o = x.Row<Resource>(id); label = o.Name; small.Add("Sede: " + x.Row<Location>(o.LocationId).Name); active = o.Active;
                        photo = o.PhotoAt.HasValue && x.S.One<byte[]>("SELECT data FROM core_resourcephoto WHERE resource_id=@r", new { r = id }) is byte[] data ? Convert.ToBase64String(data) : ""; break;
                    }
                case "options": { var o = x.Row<Option>(id); label = o.Name; small.Add($"{x.Row<Service>(o.ServiceId).Name}, {o.Duration} minuti"); active = o.Active; break; }
                case "turns": { var o = x.S.Get<Shiftrule>(id); label = o.Name; active = o.Active; break; }
                // the date in the business's time (the web page writes it in UTC)
                case "occurrences": { var o = x.Row<Occurrence>(id); label = $"{x.Row<Service>(o.ServiceId).Name}, {x.Cal.ToLocal(o.Start):dd/MM/yyyy HH:mm}"; series = o.Series.HasValue; if (series) small.Add("Fa parte di una serie"); active = o.Active; break; }
                default: { var o = x.S.Get<Product>(id); label = o.Name; small.Add(o.Price is > 0 ? Prices.Euro(o.Price) : "Prezzo libero, lo scrivi in Cassa"); active = o.Active; break; }
            }
            return new
            {
                id, etichetta = label, righe = small, foto = photo, serie = series,
                spento = active == false ? kind == "products" ? "Non in vendita" : "Disattivato, resta per le prenotazioni passate" : "",
                togli = kind is "schedules" or "exceptions" ? "fascia" : REMOVABLE.ContainsKey(kind) && active != false ? "elimina" : "",
            };
        }
        return new
        {
            tipo = kind, titolo = title, aiuto = help, pagina = page, pagine = pages, schede = Tabs(c), righe = ids.Select(Line).ToList(),
            profilo = kind == "services" && c.User.IsStaff, tipoNome = Presets.All.TryGetValue(t.BusinessType ?? "", out var bp) ? bp.Label : "",
            modelli = kind == "services" && c.User.IsStaff ? Presets.All.Select(p => new { id = p.Key, nome = p.Value.Label, aiuto = p.Value.Hint }) : null,
        };
    }

    // ---------- Rimuovi, Elimina
    static object Who(Call c, Booking b)
    {
        var d = Permissions.Shown(c.Perms, new Bookings(c.C).Data(b)); var masked = Json.Truthy(d["masked"]);
        return new { id = b.Id, nome = $"{d.Str("first_name")} {d.Str("last_name")}".Trim(), inizio = new Bookings(c.C).Iso(b.Start), servizio = b.Snap.Str("service"), telefono = masked ? "" : d.Str("phone"), whatsapp = masked ? "" : ClientMails.WhatsApp(c.C, d.Str("phone"), b) };
    }

    [Rpc("catalogo.elimina", Perm = "catalog.write")]
    public static object Remove(Call c)
    {
        var kind = c.Str("tipo"); Allowed(c, kind); var x = c.C; var id = c.NeedId("id"); var table = Profile.TableOf(kind); var pf = new Profile(x);
        x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = c.Tenant.Id });
        if (!x.S.Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {table} WHERE id=@id)", new { id })) throw new KeyNotFoundException();
        if (kind == "occurrences")
        {
            // an event date, or it and the next ones of its series: the bookings on them are listed before, to call back
            var obj = x.S.Get<Occurrence>(id); var series = obj.Series.HasValue && c.Bool("serie");
            var dates = series ? x.S.Query<Guid>("SELECT id FROM core_occurrence WHERE series=@s AND start>@a ORDER BY start", new { s = obj.Series, a = obj.Start }).Prepend(id).ToList() : new List<Guid> { id };
            var held = x.S.Query<Booking>("SELECT * FROM core_booking WHERE occurrence_id = ANY(@d) AND status = ANY(@o) AND \"end\">@n ORDER BY start", new { d = dates.ToArray(), o = Bookings.OPEN, n = x.Now }).ToList();
            if (held.Count > 0 && !c.Bool("conferma"))
                return new
                {
                    controlla = true, titolo = "Prima di togliere " + (dates.Count > 1 ? "queste date" : "questa data"), pulsante = dates.Count > 1 ? $"Togli {dates.Count} date" : "Togli la data",
                    testo = "Queste prenotazioni sono sulle date che stai togliendo. Restano valide e le date con prenotazioni restano nello storico, spente: chiama i clienti per spostarle o annullarle.",
                    righe = held.Select(b => Who(c, b)).ToList(),
                };
            var keptDates = dates.Sum(d => pf.Remove("occurrences", d));
            x.Audit("catalog.removed", id, new { kind, dates = dates.Count, kept = keptDates });
            return new { testo = (dates.Count > 1 ? $"Tolte {dates.Count} date." : "Data tolta.") + (keptDates > 0 ? $" {keptDates} con prenotazioni restano nello storico, spente: le prenotazioni restano valide." : "") };
        }
        if (REMOVABLE.ContainsKey(kind))
        {
            // every catalog item can be removed; what bookings point to is switched off and kept in the history
            if (kind == "locations" && !x.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_location WHERE active AND id<>@id)", new { id })) throw new BookingException("Serve almeno una sede: aggiungine un'altra prima di eliminare questa.");
            var col = kind switch { "services" => "service_id", "resources" => "resource_id", "locations" => "location_id", _ => null };
            var future = col == null ? 0 : x.S.Scalar<long>($"SELECT count(*) FROM core_booking WHERE status = ANY(@o) AND \"end\">@n AND {col}=@id", new { o = Bookings.OPEN, n = x.Now, id });
            var name = kind == "occurrences" ? "" : x.S.Scalar<string>($"SELECT name FROM {table} WHERE id=@id", new { id }) ?? "";
            var kept = pf.Remove(kind, id); x.Audit("catalog.removed", id, new { kind, kept });
            var what = name != "" ? name : REMOVABLE[kind];
            if (kept == 0) return new { testo = $"Eliminato: {what}." };
            return new { testo = $"Disattivato perché ha prenotazioni collegate: {what}. Resta nello storico ma non si prenota più" + (future > 1 ? $". Le {future} prenotazioni già fatte restano valide" : future == 1 ? ". La prenotazione già fatta resta valida" : "") + "." };
        }
        x.S.Execute($"DELETE FROM {table} WHERE id=@id", new { id });
        var conflicts = new Catalog(x).Conflicts();
        if (conflicts.Count > 0 && !c.Bool("confirm_conflicts")) throw new BookingException("Rimozione non eseguita: esistono prenotazioni in conflitto. Modifica la fascia e conferma la gestione dei conflitti.");
        new Waitlist(x).Freed();  // a closure removed: the waiting list looks again
        x.Audit("calendar.deleted", id);
        return new { testo = "Fascia rimossa." };
    }

    // ---------- the forms
    static IEnumerable<object> Rows(Ctx x, string sql, string empty = null) =>
        x.S.Query<(Guid, string)>(sql).Select(r => (object)new { id = r.Item1.ToString(), nome = r.Item2 }).Prepend(new { id = "", nome = empty ?? "Scegli" });
    static IEnumerable<object> Opts(IEnumerable<(string, string)> items) => items.Select(i => (object)new { id = i.Item1, nome = i.Item2 });

    /// <summary>only_one: with a single choice (one location, one service) the field is filled in and not shown.</summary>
    static Fld One(Ctx x, string name, string label, string sql, object value, string help = "")
    {
        var rows = x.S.Query<(Guid, string)>(sql).ToList();
        if (rows.Count == 1) return new Fld { Nome = name, Etichetta = label, Tipo = "hidden", Valore = rows[0].Item1.ToString() };
        return new Fld { Nome = name, Etichetta = label, Tipo = "select", Aiuto = help, Valore = value?.ToString() ?? "", Scelte = rows.Select(r => (object)new { id = r.Item1.ToString(), nome = r.Item2 }).Prepend(new { id = "", nome = "Scegli" }) };
    }
    /// <summary>CalendarForm: an optional narrowing, shown only when there is something to choose from.</summary>
    static IEnumerable<Fld> Narrow(Ctx x, Guid? loc, Guid? svc, Guid? res, string purpose, bool withPurpose)
    {
        if (withPurpose && x.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_service WHERE kind='day')"))
            yield return new Fld { Nome = "purpose", Etichetta = "A cosa si applica", Tipo = "select", Valore = purpose, Scelte = Opts(PURPOSES), Aiuto = "Apertura vale per le prenotazioni normali; arrivo e partenza per soggiorni e noleggi" };
        foreach (var (name, table, empty, what, value) in new[] { ("location", "core_location", "Tutte le sedi", "la sede", loc), ("service", "core_service", "Tutti i servizi", "il servizio", svc), ("resource", "core_resource", "Tutte le risorse", "la risorsa", res) })
            if (x.S.Scalar<long>($"SELECT count(*) FROM {table}") >= 2)
                yield return new Fld { Nome = name, Etichetta = "Solo per " + what, Tipo = "select", Valore = value?.ToString() ?? "", Scelte = Rows(x, $"SELECT id, name FROM {table} ORDER BY name", empty), Aiuto = $"Lascia {empty.ToLower(It)} se vale per tutta l'attività" };
    }

    static List<Fld> Fields(Call c, string kind, Guid? id, JsonObject extra)
    {
        var x = c.C; var t = c.Tenant; var f = new List<Fld>();
        Fld Text(string n, string l, string v, int max, string help = "", bool wide = false) => new() { Nome = n, Etichetta = l, Valore = v ?? "", Max = max, Aiuto = help, Largo = wide };
        Fld Num(string n, string l, object v, string help = "") => new() { Nome = n, Etichetta = l, Tipo = "number", Valore = v?.ToString() ?? "", Aiuto = help };
        Fld Check(string n, string l, bool v, string help = "") => new() { Nome = n, Etichetta = l, Tipo = "check", Valore = v, Aiuto = help };
        Fld Price(string n, decimal? v, string help) => new() { Nome = n, Etichetta = "Prezzo (€)", Tipo = "decimal", Valore = v.HasValue ? v.Value.ToString("0.00", It) : "", Aiuto = help };
        switch (kind)
        {
            case "locations":
                {
                    var o = id.HasValue ? x.S.Get<Location>(id) : new Location();
                    f.Add(Text("name", "Nome", o.Name, 100, "Nome della sede")); f.Add(Text("address", "Indirizzo", o.Address, 255, "Compare nelle email di conferma")); f.Add(Text("phone", "Telefono", o.Phone, 60, "Compare nelle email di conferma"));
                    f.Add(Num("capacity", "Posti disponibili", o.Capacity, "Per i ristoranti: il tetto che tutti i servizi della sede dividono, per esempio pranzo alla carta e menu di gruppo nella stessa sala. 0 vuol dire nessun limite"));
                    f.Add(Check("active", "Attivo", o.Active)); f.Add(Text("cin", "CIN", o.Cin, 40, "Strutture ricettive e case vacanze: compare nella pagina di prenotazione e nelle email")); f.Add(Text("cir", "CIR o codice regionale", o.Cir, 40));
                    f.Add(Text("ross_code", "Codice struttura Ross1000", o.RossCode, 20, "Per il file ISTAT dei movimenti, te lo dà l'ufficio regionale del turismo"));
                    f.Add(new Fld { Nome = "tax_rate", Etichetta = "Imposta di soggiorno, € a persona a notte", Tipo = "decimal", Valore = o.TaxRate?.ToString("0.00", It) ?? "", Aiuto = "Vuoto se il comune non la chiede. Roma, B&B: 6" });
                    f.Add(Num("tax_nights", "Notti tassate al massimo", o.TaxNights, "0 = tutte. Roma: 10")); f.Add(Num("tax_age", "Esenti sotto i … anni", o.TaxAge, "0 = nessuna esenzione per età. Roma: 10"));
                    f.Add(new Fld { Nome = "assign", Etichetta = "«Il primo disponibile» va a", Tipo = "select", Valore = o.Assign, Scelte = Opts(ASSIGN), Aiuto = "Chi riceve la prenotazione quando il cliente non sceglie con chi. Chi è segnato «Fuori da Il primo disponibile» non la riceve mai" });
                    f.Add(Num("min_gap", "Online, niente buchi più corti di (minuti)", o.MinGap, "0 = tutti gli orari liberi. Con 30, online non compaiono gli orari che lascerebbero meno di 30 minuti tra due appuntamenti dello stesso operatore; al banco si vedono tutti"));
                    if (t.BusinessType == "ristorante")
                    {
                        f.Add(new Fld { Nome = "tables", Etichetta = "Tavoli", Tipo = "select", Valore = o.Tables, Scelte = Opts(TABLES), Aiuto = "Ristoranti: sale e tavoli si preparano in Sale e tavoli. Il cliente sceglie al massimo la sala, mai il tavolo" });
                        f.Add(Check("tables_online", "Online accetta solo se c'è un tavolo adatto libero", o.TablesOnline, "Acceso: online un orario compare solo se c'è un tavolo libero con i posti giusti (o due da unire)"));
                    }
                    f.Add(new Fld { Nome = "visit_split", Etichetta = "Più servizi nella stessa visita", Tipo = "select", Valore = o.VisitSplit, Scelte = Opts(SPLIT), Aiuto = "Quando il cliente prenota per esempio colore, taglio e piega insieme: sempre con lo stesso operatore, con chi è libero, o con un altro solo se uno da solo non riesce" });
                    f.Add(Num("visit_wait", "Attesa massima tra due servizi (minuti)", o.VisitWait, "Quanto il cliente può aspettare tra la fine di un servizio e l'inizio del successivo. 0 = subito dopo"));
                    break;
                }
            case "schedules":
                {
                    var o = id.HasValue ? x.S.Get<Schedule>(id) : new Schedule { Weekday = -1 };
                    var res = !id.HasValue && Guid.TryParse(extra.Str("resource"), out var rid) ? x.Row<Resource>(rid) : null;  // «Segna ferie» from an operator
                    f.AddRange(Narrow(x, o.LocationId ?? res?.LocationId, o.ServiceId, o.ResourceId ?? res?.Id, o.Purpose, true));
                    f.Add(new Fld { Nome = "weekday", Etichetta = "Giorno", Tipo = "select", Valore = o.Weekday < 0 ? "" : o.Weekday.ToString(), Scelte = Opts(WEEKDAYS.Select((w, i) => (i.ToString(), w)).Prepend(("", "---------"))) });
                    f.Add(new Fld { Nome = "start", Etichetta = "Inizio", Tipo = "time", Valore = id.HasValue ? Hm(o.Start) : "", Aiuto = "Per esempio 12:00" }); f.Add(new Fld { Nome = "end", Etichetta = "Fine", Tipo = "time", Valore = id.HasValue ? Hm(o.End) : "", Aiuto = "Per esempio 15:00. Per pranzo e cena crea due fasce" });
                    f.Add(new Fld { Nome = "valid_from", Etichetta = "Valido dal", Tipo = "date", Valore = Iso(o.ValidFrom), Aiuto = "Vuoto: da subito" }); f.Add(new Fld { Nome = "valid_until", Etichetta = "Valido fino al", Tipo = "date", Valore = Iso(o.ValidUntil), Aiuto = "Vuoto: sempre" });
                    f.Add(Num("every_weeks", "Ogni quante settimane", o.EveryWeeks, "1 ogni settimana. 2 per le settimane alterne: conta dalla settimana di Valido dal (settimana A); per la settimana B crea le fasce con Valido dal una settimana dopo"));
                    break;
                }
            case "exceptions":
                {
                    var o = id.HasValue ? x.S.Get<Exceptionday>(id) : new Exceptionday();
                    var res = !id.HasValue && Guid.TryParse(extra.Str("resource"), out var rid) ? x.Row<Resource>(rid) : null;
                    f.AddRange(Narrow(x, o.LocationId ?? res?.LocationId, o.ServiceId, o.ResourceId ?? res?.Id, o.Purpose, true));
                    f.Add(new Fld { Nome = "date", Etichetta = "Dal giorno", Tipo = "date", Valore = id.HasValue ? Iso(o.Date) : "" }); f.Add(new Fld { Nome = "until", Etichetta = "Al giorno", Tipo = "date", Valore = Iso(o.Until), Aiuto = "Vuoto: solo quel giorno. Due settimane di ferie sono una riga sola" });
                    f.Add(Check("closed", "Chiuso tutto il giorno", o.Closed, "Togli la spunta per un orario speciale, uguale per ogni giorno, e scrivilo in Inizio e Fine"));
                    f.Add(new Fld { Nome = "start", Etichetta = "Inizio", Tipo = "time", Valore = Hm(o.Start) }); f.Add(new Fld { Nome = "end", Etichetta = "Fine", Tipo = "time", Valore = Hm(o.End) });
                    f.Add(Text("label", "Motivo", o.Label, 100, "Per esempio Ferie o Natale: lo vedi solo tu"));
                    break;
                }
            case "options":
                {
                    var o = id.HasValue ? x.S.Get<Option>(id) : new Option();
                    var svc = One(x, "service", "Servizio", "SELECT id, name FROM core_service WHERE active ORDER BY name", id.HasValue ? o.ServiceId : null);
                    if (svc.Tipo == "select") svc.Scelte = Rows(x, "SELECT id, name FROM core_service ORDER BY name");
                    f.Add(svc); f.Add(Text("name", "Nome", o.Name, 100, "Per esempio Taglio corto o Taglio lungo")); f.Add(Num("duration", "Durata (minuti)", o.Duration, "La durata di questa variante, al posto di quella del servizio"));
                    f.Add(Price("price", o.Price, "Facoltativo: vuoto vuol dire il prezzo del servizio")); f.Add(Check("active", "Attivo", o.Active));
                    break;
                }
            case "turns":
                {
                    var o = id.HasValue ? x.S.Get<Shiftrule>(id) : new Shiftrule { Weekday = -1 };
                    f.Add(Text("name", "Nome", o.Name, 100, "Per esempio Pranzo o Cena")); f.Add(One(x, "service", "Servizio", "SELECT id, name FROM core_service WHERE kind='shift' ORDER BY name", id.HasValue ? o.ServiceId : null));
                    f.Add(new Fld { Nome = "weekday", Etichetta = "Giorno", Tipo = "select", Valore = o.Weekday < 0 ? "" : o.Weekday.ToString(), Scelte = Opts(WEEKDAYS.Select((w, i) => (i.ToString(), w)).Prepend(("", "---------"))) });
                    f.Add(new Fld { Nome = "start", Etichetta = "Inizio", Tipo = "time", Valore = id.HasValue ? Hm(o.Start) : "" }); f.Add(new Fld { Nome = "end", Etichetta = "Fine", Tipo = "time", Valore = id.HasValue ? Hm(o.End) : "" });
                    f.Add(Num("capacity", "Posti disponibili", o.Capacity, "Quanti coperti si possono prenotare in questo turno, al massimo i posti totali del servizio"));
                    f.Add(new Fld { Nome = "valid_from", Etichetta = "Valido dal", Tipo = "date", Valore = Iso(o.ValidFrom), Aiuto = "Vuoto: da subito" }); f.Add(new Fld { Nome = "valid_until", Etichetta = "Valido fino al", Tipo = "date", Valore = Iso(o.ValidUntil), Aiuto = "Vuoto: sempre" });
                    f.Add(Check("active", "Attivo", o.Active));
                    break;
                }
            case "occurrences":
                {
                    var o = id.HasValue ? x.S.Get<Occurrence>(id) : new Occurrence(); string Local(DateTime d) => x.Cal.ToLocal(d).ToString("yyyy-MM-dd'T'HH:mm");
                    f.Add(One(x, "service", "Servizio", "SELECT id, name FROM core_service WHERE kind='event' ORDER BY name", id.HasValue ? o.ServiceId : null));
                    f.Add(new Fld { Nome = "start", Etichetta = "Inizio", Tipo = "datetime", Valore = id.HasValue ? Local(o.Start) : "" }); f.Add(new Fld { Nome = "end", Etichetta = "Fine", Tipo = "datetime", Valore = id.HasValue ? Local(o.End) : "" });
                    f.Add(Num("capacity", "Posti disponibili", o.Capacity)); f.Add(Check("active", "Attivo", o.Active));
                    if (!id.HasValue)
                    {
                        f.Add(new Fld { Nome = "repeat", Etichetta = "Ripeti", Tipo = "select", Valore = "0", Scelte = Opts(new[] { ("0", "Non si ripete"), ("1", "Ogni settimana"), ("2", "Ogni 2 settimane"), ("3", "Ogni 3 settimane"), ("4", "Ogni 4 settimane") }) });
                        f.Add(new Fld { Nome = "repeat_until", Etichetta = "Fino al", Tipo = "date", Valore = "", Aiuto = "L'ultimo giorno della serie, al massimo due anni dopo la prima data" });
                        f.Add(new Fld { Nome = "repeat_days", Etichetta = "Nei giorni", Tipo = "multi", Valore = Array.Empty<string>(), Scelte = Opts(WEEKDAYS.Select((w, i) => (i.ToString(), w))), Aiuto = "Vuoto: lo stesso giorno della settimana della prima data" });
                    }
                    else if (o.Series.HasValue) f.Add(new Fld { Nome = "scope", Etichetta = "Cosa cambia", Tipo = "radio", Valore = "one", Scelte = Opts(new[] { ("one", "Solo questa data"), ("series", "Questa e le successive della serie") }) });
                    break;
                }
            case "products":
                {
                    var o = id.HasValue ? x.S.Get<Product>(id) : new Product();
                    f.Add(Text("name", "Nome", o.Name, 80, "Come compare in Cassa, nei preventivi e sullo scontrino (sullo scontrino al massimo 32 caratteri)")); f.Add(Price("price", o.Price, "Facoltativo: vuoto, lo scrivi ogni volta in Cassa")); f.Add(Check("active", "Attivo", o.Active));
                    break;
                }
        }
        return f;
    }
    static readonly (string, string)[] ASSIGN = { ("alpha", "Il primo libero, in ordine di nome"), ("last", "L'operatore dell'ultima visita del cliente"), ("even", "Chi ha meno appuntamenti quel giorno"), ("priority", "L'ordine scelto per ogni operatore") };
    static readonly (string, string)[] SPLIT = { ("same", "Stesso operatore"), ("split", "Divisibile tra operatori"), ("need", "Divisibile solo se serve") };
    static readonly (string, string)[] TABLES = { ("off", "Nessuna gestione dei tavoli"), ("manual", "Li assegnate voi"), ("suggest", "Il programma propone il tavolo più adatto") };

    [Rpc("catalogo.modulo", Perm = "catalog.write")]
    public static object Form(Call c)
    {
        var kind = c.Str("tipo"); Allowed(c, kind); var x = c.C; var id = c.Id("id");
        if (id.HasValue && !x.S.Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {Profile.TableOf(kind)} WHERE id=@id)", new { id })) throw new KeyNotFoundException();
        var title = KINDS.First(k => k.Kind == kind).Title; var edit = EDIT_TITLES[kind]; var help = EDIT_HELP[kind];
        if (kind == "resources") { var (one, many, h) = Presets.UnitsOf(c.Tenant.BusinessType); title = many; edit = one.ToLower(It); help = h; }
        var special = kind switch { "services" => ServiceForm(c, id), "resources" => ResourceForm(c, id), _ => null };
        return new { tipo = kind, id, titolo = (id.HasValue ? "Modifica: " : "Nuovo: ") + edit, elenco = title, descrizione = help, nome = c.Tenant.Name, campi = special?.Fields ?? Fields(c, kind, id, c.Args), extra = special?.Extra };
    }

    [Rpc("catalogo.salva", Perm = "catalog.write")]
    public static object Save(Call c)
    {
        var kind = c.Str("tipo"); Allowed(c, kind); var x = c.C; var t = c.Tenant; var id = c.Id("id"); var p = new Posted(c, c.Obj("valori")); var table = Profile.TableOf(kind);
        if (id.HasValue && !x.S.Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {table} WHERE id=@id)", new { id })) throw new KeyNotFoundException();
        x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = t.Id });
        Guid saved;
        try
        {
            saved = kind switch { "services" => SaveService(c, id, p), "resources" => SaveResource(c, id, p), _ => SaveSimple(c, kind, id, p) };
            var conflicts = new Catalog(x).Conflicts();
            if (conflicts.Count > 0 && !c.Bool("confirm_conflicts")) throw new BookingException(Catalog.Unsettled(conflicts));
            new Waitlist(x).Freed();  // more hours, more places: the waiting list looks again
            x.Audit("catalog." + kind, saved, new { conflicts = conflicts.Count });
            return new { testo = "Salvato." + (conflicts.Count > 0 ? $" {conflicts.Count} prenotazioni da ricontattare." : "") };
        }
        catch (BookingException ex) when (ex is not FormErrors) { throw new FormErrors(new() { [""] = ex.Message }, ex.Message.Contains("Conferma esplicitamente")); }
    }

    static Guid SaveSimple(Call c, string kind, Guid? id, Posted p)
    {
        var x = c.C; var t = c.Tenant; var cat = new Catalog(x);
        void Name(string v) { if (v.Length > 120) p.Whole("Nome troppo lungo."); }
        (Guid? L, Guid? S, Guid? R, string Purpose, bool Closed, TimeSpan? A, TimeSpan? B) Level(bool closed, string purposeNow)
        {
            var l = p.Has("location") ? p.Ref("location", "core_location") : null; var s = p.Has("service") ? p.Ref("service", "core_service") : null; var r = p.Has("resource") ? p.Ref("resource", "core_resource") : null;
            var purpose = p.Has("purpose") ? p.Choice("purpose", PURPOSES.Select(q => q.Item1), true, "service") : purposeNow;
            var a = p.Time("start", !closed && kind == "schedules"); var b = p.Time("end", !closed && kind == "schedules");
            if (new object[] { l, s, r }.Count(v => v != null) > 1) p.Whole("Scegli un solo livello: sede, servizio oppure risorsa. Nessuno = tutta l'attività.");
            if (!closed && (!a.HasValue || !b.HasValue || a == b)) p.Whole("Indica una fascia con inizio e fine differenti.");
            return (l, s, r, purpose, closed, a, b);
        }
        DateTime? Day(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
        switch (kind)
        {
            case "locations":
                {
                    var o = id.HasValue ? x.S.Get<Location>(id) : new Location { TenantId = t.Id };
                    o.Name = p.Text("name", true, 100); Name(o.Name); o.Address = p.Text("address", false, 255); o.Phone = p.Text("phone", false, 60);
                    var cap = p.Int("capacity", true) ?? 0; if (cap > 10000) p.Error("capacity", "Valore tra 0 e 10000."); o.Capacity = cap; o.Active = p.Bool("active");
                    o.Cin = p.Text("cin", false, 40); o.Cir = p.Text("cir", false, 40); o.RossCode = p.Text("ross_code", false, 20); o.TaxRate = p.Dec("tax_rate", false, 5, 2);
                    o.TaxNights = (short)(p.Int("tax_nights", true, 0, 32767) ?? 0); o.TaxAge = (short)(p.Int("tax_age", true, 0, 32767) ?? 0); o.Assign = p.Choice("assign", ASSIGN.Select(a => a.Item1), true, o.Assign);
                    var gap = p.Int("min_gap") ?? 0; if (gap > 240) p.Error("min_gap", "Valore tra 0 e 240."); o.MinGap = gap;
                    if (t.BusinessType == "ristorante") { o.Tables = p.Choice("tables", TABLES.Select(a => a.Item1), false, "off"); o.TablesOnline = p.Bool("tables_online"); }
                    o.VisitSplit = p.Choice("visit_split", SPLIT.Select(a => a.Item1), false, "need"); var wait = p.Int("visit_wait", false, 0, 32767) ?? 0; if (wait > 240) p.Error("visit_wait", "Valore tra 0 e 240."); o.VisitWait = (short)wait;
                    p.Check(); if (id.HasValue) x.S.Update(o, "name", "address", "phone", "capacity", "active", "cin", "cir", "ross_code", "tax_rate", "tax_nights", "tax_age", "assign", "min_gap", "tables", "tables_online", "visit_split", "visit_wait"); else x.S.Insert(o);
                    return o.Id;
                }
            case "schedules":
                {
                    var o = id.HasValue ? x.S.Get<Schedule>(id) : new Schedule { TenantId = t.Id };
                    var k = Level(false, o.Purpose); var wd = p.Choice("weekday", Enumerable.Range(0, 7).Select(i => i.ToString()), true);
                    var from = p.Date("valid_from"); var until = p.Date("valid_until"); var every = p.Int("every_weeks", false, 0, 32767) ?? 0; if (every == 0) every = 1;
                    if (from.HasValue && until.HasValue && from > until) p.Whole("Intervallo date non valido.");
                    if (every is < 1 or > 4) p.Whole("Ogni quante settimane: da 1 a 4."); else if (every > 1 && !from.HasValue) p.Whole("Per le settimane alterne scrivi in Valido dal il giorno da cui contare.");
                    p.Check();
                    (o.LocationId, o.ServiceId, o.ResourceId, o.Purpose, o.Weekday, o.Start, o.End, o.ValidFrom, o.ValidUntil, o.EveryWeeks) = (k.L, k.S, k.R, k.Purpose, short.Parse(wd), k.A.Value, k.B.Value, Day(from), Day(until), (short)every);
                    if (id.HasValue) x.S.Update(o, "location_id", "service_id", "resource_id", "purpose", "weekday", "start", "end", "valid_from", "valid_until", "every_weeks"); else x.S.Insert(o);
                    return o.Id;
                }
            case "exceptions":
                {
                    var o = id.HasValue ? x.S.Get<Exceptionday>(id) : new Exceptionday { TenantId = t.Id };
                    var k = Level(p.Bool("closed"), o.Purpose); var date = p.Date("date", true); var until = p.Date("until"); var label = p.Text("label", false, 100);
                    if (until.HasValue && date.HasValue)
                    {
                        if (until < date) p.Whole("Il giorno di fine viene prima di quello di inizio."); else if (until.Value.DayNumber - date.Value.DayNumber > 366) p.Whole("Al massimo un anno su una riga.");
                        else if (until == date) until = null;  // a range of one day is a single date
                    }
                    p.Check();
                    (o.LocationId, o.ServiceId, o.ResourceId, o.Purpose, o.Date, o.Until, o.Closed, o.Start, o.End, o.Label) = (k.L, k.S, k.R, k.Purpose, Day(date).Value, Day(until), k.Closed, k.A, k.B, label);
                    if (id.HasValue) x.S.Update(o, "location_id", "service_id", "resource_id", "purpose", "date", "until", "closed", "start", "end", "label"); else x.S.Insert(o);
                    return o.Id;
                }
            case "options":
                {
                    var o = id.HasValue ? x.S.Get<Option>(id) : new Option { TenantId = t.Id };
                    var svc = p.Ref("service", "core_service", true); o.Name = p.Text("name", true, 100); Name(o.Name);
                    var dur = p.Int("duration", true); if (dur.HasValue && dur is < 5 or > 1440) p.Error("duration", "Durata tra 5 e 1440 minuti.");
                    var price = p.Dec("price"); if (price is < 0 or > 100000) p.Error("price", "Prezzo tra 0 e 100.000 €.");
                    p.Check(); (o.ServiceId, o.Duration, o.Price, o.Active) = (svc.Value, dur.Value, price, p.Bool("active"));
                    if (id.HasValue) x.S.Update(o, "service_id", "name", "duration", "price", "active"); else x.S.Insert(o);
                    return o.Id;
                }
            case "turns":
                {
                    var o = id.HasValue ? x.S.Get<Shiftrule>(id) : new Shiftrule { TenantId = t.Id };
                    o.Name = p.Text("name", true, 100); Name(o.Name); var svc = p.Ref("service", "core_service", true, "kind='shift'"); var wd = p.Choice("weekday", Enumerable.Range(0, 7).Select(i => i.ToString()), true);
                    var a = p.Time("start", true); var b = p.Time("end", true); var cap = p.Int("capacity", true); var from = p.Date("valid_from"); var until = p.Date("valid_until");
                    if (a.HasValue && a == b) p.Whole("Inizio e fine devono essere differenti.");
                    else if ((cap ?? 0) < 1 || svc.HasValue && cap > x.Row<Service>(svc).Capacity) p.Whole("I coperti del turno devono essere tra 1 e la capienza del servizio.");
                    else if (from.HasValue && until.HasValue && from > until) p.Whole("Periodo non valido.");
                    p.Check(); (o.ServiceId, o.Weekday, o.Start, o.End, o.Capacity, o.ValidFrom, o.ValidUntil, o.Active) = (svc.Value, short.Parse(wd), a.Value, b.Value, cap.Value, Day(from), Day(until), p.Bool("active"));
                    if (id.HasValue) x.S.Update(o, "name", "service_id", "weekday", "start", "end", "capacity", "valid_from", "valid_until", "active"); else x.S.Insert(o);
                    cat.Materialize(o); x.Refresh();
                    // a turn outside the opening hours would never be offered, without a word
                    var dates = x.S.Query<Occurrence>("SELECT * FROM core_occurrence WHERE rule_id=@r AND active ORDER BY start LIMIT 8", new { r = o.Id }).ToList(); var service = x.Row<Service>(o.ServiceId);
                    if (dates.Count > 0 && !dates.Any(d => x.Cal.Fits(d.Start, d.End, service))) throw new BookingException("Il turno è fuori dagli orari di apertura di quel giorno: cambia l'ora o allarga gli orari in Orari di apertura.");
                    return o.Id;
                }
            case "occurrences":
                {
                    var o = id.HasValue ? x.S.Get<Occurrence>(id) : new Occurrence { TenantId = t.Id }; var was = o.Start; var isNew = !id.HasValue;
                    var svc = p.Ref("service", "core_service", true, "kind='event'"); var a = p.Moment("start", true); var b = p.Moment("end", true); var cap = p.Int("capacity", true);
                    var repeat = isNew ? int.Parse(p.Choice("repeat", new[] { "0", "1", "2", "3", "4" }, false, "0")) : 0; var until = isNew ? p.Date("repeat_until") : null;
                    var days = isNew ? p.Many("repeat_days").Where(d => int.TryParse(d, out var n) && n is >= 0 and <= 6).Select(int.Parse).Distinct().OrderBy(n => n).ToList() : new();
                    var scope = !isNew && o.Series.HasValue ? p.Choice("scope", new[] { "one", "series" }, false, "one") : "one";
                    if (a.HasValue && b.HasValue && (b <= a || (b.Value - a.Value).Days > 7)) p.Whole("Durata evento non valida.");
                    else if ((cap ?? 0) < 1) p.Whole("Capienza positiva richiesta.");
                    else if (repeat > 0 && a.HasValue)
                    {
                        var first = x.Cal.DayOf(a.Value);
                        if (!until.HasValue || until <= first) p.Whole("Scrivi in Fino al l'ultimo giorno della serie, dopo la prima data."); else if (until.Value.DayNumber - first.DayNumber > 730) p.Whole("Una serie arriva al massimo a due anni dalla prima data.");
                    }
                    p.Check();
                    // the dates a change reaches: this one, and with «the next ones» the rest of its series
                    var later = scope == "series" ? x.S.Query<Occurrence>("SELECT * FROM core_occurrence WHERE series=@s AND start>@w AND id<>@i ORDER BY start", new { s = o.Series, w = was, i = o.Id }).ToList() : new();
                    if (!isNew)
                    {
                        var held = x.S.Query<Guid>("SELECT id FROM core_booking WHERE occurrence_id = ANY(@d) AND status = ANY(@o) AND \"end\">@n", new { d = later.Select(l => l.Id).Prepend(o.Id).ToArray(), o = Bookings.OPEN, n = x.Now }).Select(g => g.ToString()[..8]).ToList();
                        if (held.Count > 0) throw new BookingException("Gestisci prima le prenotazioni di queste date, spostale o annullale: " + Catalog.Listed(held) + ".");
                    }
                    (o.ServiceId, o.Start, o.End, o.Capacity, o.Active) = (svc.Value, a.Value, b.Value, cap.Value, p.Bool("active"));
                    if (isNew) x.S.Insert(o); else x.S.Update(o, "service_id", "start", "end", "capacity", "active");
                    var la = x.Cal.ToLocal(o.Start); var lb = x.Cal.ToLocal(o.End); var span = DateOnly.FromDateTime(lb).DayNumber - DateOnly.FromDateTime(la).DayNumber; var firstDay = DateOnly.FromDateTime(la);
                    void Place(Occurrence d, DateOnly day, bool insert)
                    {
                        var s = x.Cal.Local(day, la.TimeOfDay); var e = x.Cal.Local(day.AddDays(span), lb.TimeOfDay, 1);
                        if (!s.HasValue || !e.HasValue) return;  // an hour that does not exist that day (summer time) is skipped
                        (d.Start, d.End, d.Capacity, d.Active) = (s.Value, e.Value, o.Capacity, o.Active);
                        if (insert) x.S.Insert(d); else x.S.Update(d, "start", "end", "capacity", "active");
                    }
                    if (repeat > 0)
                    {
                        o.Series = Guid.NewGuid(); x.S.Update(o, "series");
                        if (days.Count == 0) days.Add(TD.Core.Domain.Calendar.Weekday(firstDay));
                        for (var monday = firstDay.AddDays(-TD.Core.Domain.Calendar.Weekday(firstDay)); monday <= until.Value; monday = monday.AddDays(7 * repeat))
                            foreach (var wd in days) { var day = monday.AddDays(wd); if (firstDay < day && day <= until.Value) Place(new Occurrence { TenantId = t.Id, ServiceId = o.ServiceId, Series = o.Series }, day, true); }
                    }
                    else if (later.Count > 0)
                    {
                        // the next dates of the series move as this one: the same days later or earlier, the new hours and places
                        var shift = firstDay.DayNumber - x.Cal.DayOf(was).DayNumber;
                        foreach (var d in later) Place(d, x.Cal.DayOf(d.Start).AddDays(shift), false);
                    }
                    return o.Id;
                }
            default:
                {
                    var o = id.HasValue ? x.S.Get<Product>(id) : new Product { TenantId = t.Id, Created = x.Now };
                    var name = string.Join(" ", p.Text("name", true, 80).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); Name(name);
                    if (name != "" && x.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_product WHERE upper(name)=upper(@n) AND active AND id<>@i)", new { n = name, i = o.Id })) p.Error("name", "C'è già un prodotto con questo nome.");
                    var price = p.Dec("price"); if (price.HasValue && !(price > 0 && price <= 99999)) p.Error("price", "Prezzo tra 0,01 e 99.999 €.");
                    p.Check(); (o.Name, o.Price, o.Active) = (name, price, p.Bool("active"));
                    if (id.HasValue) x.S.Update(o, "name", "price", "active"); else x.S.Insert(o);
                    return o.Id;
                }
        }
    }
}
