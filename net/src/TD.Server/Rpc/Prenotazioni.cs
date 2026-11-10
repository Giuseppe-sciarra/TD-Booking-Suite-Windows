using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>Who I am, my businesses, the agenda, the booking form and the booking's actions (portal.py, public.py, agenda.py).</summary>
public static class Prenotazioni
{
    static Viewer V(Call c) => new(c.Member, c.Perms);
    static List<string> Allowed(Call c) => c.Member != null && Permissions.Ids(c.Member.Resources) is { Count: > 0 } r ? r : null;

    [Rpc("sessione.io", Tenant = false)]
    public static object Me(Call c)
    {
        using var g = c.S.Db.Global();
        var rows = c.User.IsStaff
            ? g.Query<(string, string, string)>("SELECT t.slug, t.name, coalesce(m.role,'') FROM core_tenant t LEFT JOIN core_membership m ON m.tenant_id=t.id AND m.user_id=@u ORDER BY t.created", new { u = c.User.Id })
            : g.Query<(string, string, string)>("SELECT t.slug, t.name, m.role FROM core_membership m JOIN core_tenant t ON t.id=m.tenant_id WHERE m.user_id=@u ORDER BY t.name", new { u = c.User.Id });
        var lic = c.S.Licensing.Apply();
        return new
        {
            id = c.User.Id, nome = c.S.Identity.LoginName(c.User), staff = c.User.IsStaff, mfa = !string.IsNullOrEmpty(c.User.Totp),
            attivita = rows.Select(x => new { slug = x.Item1, nome = x.Item2, ruolo = x.Item3, ruoloNome = x.Item3 == "" ? "Amministratore del programma" : Permissions.RoleLabel(x.Item3) }),
            licenza = new { lic.Kind, lic.Giorni, lic.Problema, lic.Attivita, lic.Postazioni },
            standalone = c.S.Standalone, online = c.S.Online, serverPc = c.ServerPc, versione = Server.Version,
        };
    }

    [Rpc("attivita.io")]
    public static object Business(Call c)
    {
        var t = c.Tenant; var (one, many, _) = Presets.UnitsOf(t.BusinessType);
        return new
        {
            slug = t.Slug, nome = t.Name, tipo = t.BusinessType, colore = Texts.ContrastColor(t.BrandColor), coloreScuro = Texts.ContrastColor(t.BrandColor, true), fuso = t.Timezone,
            soggiorni = t.Stays, marketing = t.MarketingEnabled, esporta = t.ExportEnabled, api = t.ApiEnabled, cassaTitolare = t.TillSelf, staff = c.User.IsStaff,
            ruoloNome = c.Member != null ? Permissions.RoleLabel(c.Member.Role) : "Amministratore del programma: solo gestione tecnica", logo = t.Logo is { Length: > 0 } ? Convert.ToBase64String(t.Logo) : "", logoLargo = t.Logo is { Length: > 0 } && t.LogoRatio > 1.4,
            ruolo = c.Member?.Role ?? "", permessi = c.Perms.OrderBy(x => x), oggi = c.C.Today, uno = one, molti = many,
            cassa = t.TillOn, servizio = Presets.ServiceWord(t.BusinessType), appuntamenti = new Agenda(c.C, V(c)).Fits(),
            standby = c.C.S.Scalar<long>("SELECT count(*) FROM core_standby"),
            paginaPubblica = c.S.Config.Online ? c.PublicOrigin.TrimEnd('/') + "/book/" + t.Slug + "/" : "",
        };
    }

    // ---------- agenda
    [Rpc("agenda.giorno", Perm = "booking.read")]
    public static object Day(Call c) => new Agenda(c.C, V(c)).Day(c.Day("data"), c.Id("sede"), c.Id("operatore"), c.Bool("annullate"));
    [Rpc("agenda.settimana", Perm = "booking.read")]
    public static object Week(Call c) => new Agenda(c.C, V(c)).Week(c.Day("data"), c.Id("sede"), c.Id("operatore"), c.Bool("annullate"));
    [Rpc("agenda.mese", Perm = "booking.read")]
    public static object Month(Call c) => new Agenda(c.C, V(c)).Month(c.Day("data"), c.Id("sede"), c.Id("operatore"));

    /// <summary>An appointment dragged or stretched in the agenda (agenda.move_api): new start, operator, minutes.</summary>
    [Rpc("agenda.sposta", Perm = "booking.write")]
    public static object Drag(Call c)
    {
        var bk = new Bookings(c.C);
        var b = Visible(c, c.NeedId("id"));
        if (c.Bool("force") && !c.Can("booking.force")) throw new UnauthorizedAccessException("Il tuo ruolo non consente di forzare: chiedi al titolare.");
        var res = c.Id("operatore") ?? b.ResourceId;
        var err = Permissions.CheckNewBooking(c.Member, b.LocationId, res);
        if (err != null) throw new UnauthorizedAccessException(err);
        var data = new JsonObject
        {
            ["service"] = b.ServiceId.ToString(), ["option"] = b.OptionId?.ToString() ?? "", ["resource"] = res?.ToString() ?? "", ["party"] = b.Party,
            ["start"] = c.Str("start"), ["minutes"] = c.Int("minuti") > 0 ? c.Int("minuti") : null, ["force"] = c.Bool("force"), ["force_ok"] = c.Bool("force_ok"),
        };
        var moved = bk.Move(b.Id, data);
        return new { ok = true, id = moved.Id };
    }

    // ---------- the booking form of the desk
    [Rpc("prenotazione.catalogo", Perm = "booking.write")]
    public static object Catalog(Call c)
    {
        var mine = Allowed(c);
        var locs = c.Member != null ? Permissions.Ids(c.Member.Locations) : new();
        var services = c.C.S.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND l.active ORDER BY s.name").ToList();
        if (locs.Count > 0) services = services.Where(s => locs.Contains(s.LocationId.ToString())).ToList();
        var options = c.C.S.All<Option>("active").ToLookup(o => o.ServiceId);
        var links = c.C.S.All<Serviceresource>().ToLookup(l => l.ServiceId);
        var resources = c.C.S.All<Resource>("active AND NOT secondary", order: "name").ToList();
        var areas = c.C.S.All<Area>(order: "name").ToLookup(a => a.LocationId);
        var today = c.C.Today;
        return new
        {
            servizio = Presets.ServiceWord(c.Tenant.BusinessType), nota = Presets.NoteHint(c.Tenant.BusinessType), oggi = today,
            allergeni = c.Tenant.AskedAllergens, forza = c.Can("booking.force"), cercaClienti = c.Can("contacts.read") && c.Can("pii.read"),
            servizi = services.Select(s => new
            {
                id = s.Id, nome = s.Name, tipo = s.Kind, durata = s.Duration, sede = c.C.Row<Location>(s.LocationId).Name, sedeId = s.LocationId, maxPersone = s.MaxParty,
                etichetta = s.ResourceLabel, descrizione = s.Description, turnover = s.Turnover, campi = JsonNode.Parse(s.Fields), prezzo = s.Price.HasValue ? Prices.Euro(s.Price) : "",
                varianti = options[s.Id].OrderBy(o => o.Name).Select(o => new { id = o.Id, nome = o.Name, durata = o.Duration, prezzo = o.Price.HasValue ? Prices.Euro(o.Price) : "" }),
                sale = s.Kind is "capacity" or "shift" ? areas[s.LocationId].Select(a => new { id = a.Id, nome = a.Name }) : null,
                operatori = resources.Where(r => r.LocationId == s.LocationId && links[s.Id].Any(l => l.ResourceId == r.Id) && (mine == null || mine.Contains(r.Id.ToString())))
                    .Select(r => new
                    {
                        id = r.Id, nome = r.Name, posti = r.Capacity, descrizione = r.Description,
                        assente = r.AwayUntil.HasValue && DateOnly.FromDateTime(r.AwayUntil.Value) >= today ? new { dal = r.AwayFrom, al = r.AwayUntil, sostituto = r.AwayById, sostitutoNome = r.AwayById.HasValue ? c.C.Row<Resource>(r.AwayById.Value)?.Name : "" } : null,
                    }),
            }),
        };
    }

    [Rpc("prenotazione.orari", Perm = "booking.write")]
    public static object Slots(Call c)
    {
        var bk = new Bookings(c.C); var day = c.Day("data");
        Guid[] exclude = null;
        if (c.Id("prenotazione") is Guid pid)
        {
            var b = Visible(c, pid);
            if (!Bookings.MOVABLE.Contains(b.Status)) throw new BookingException("Questa prenotazione non si può più spostare.");
            exclude = b.Visit != null && Json.Truthy(c["extra"]) ? c.C.S.Query<Guid>("SELECT id FROM core_booking WHERE visit=@v", new { v = b.Visit }).ToArray() : new[] { b.Id };
        }
        var force = c.Bool("forza") && c.Can("booking.force");
        var service = bk.ActiveService(c["service"]);
        if (service.Kind == "day")
        {
            var start = c.C.Cal.Local(day, service.Checkin) ?? throw new BookingException("Data non disponibile.");
            var data = new JsonObject { ["start"] = bk.Iso(start), ["end_date"] = c.Str("end_date"), ["party"] = c["party"]?.DeepClone() ?? 1 };
            if (bk.HasUnits(service))
            {
                var unit = bk.Details(service, null, c["resource"]).Item2;
                var party = Bookings.Integer(c["party"] ?? 1, "Quantità", 1, 1000);
                data["resource"] = unit?.Id.ToString() ?? "";
                var w = bk.Bounds(service, data, exclude, desk: true);
                return new { slots = bk.FreeUnits(service, w.Bs, w.Be, party, unit, exclude).Select(u => new { start = bk.Iso(w.Start), end = bk.Iso(w.End), resource = u.Id.ToString(), resourceName = u.Name, description = u.Description, capacity = u.Capacity, occurrence = "" }) };
            }
            var w2 = bk.Bounds(service, data, exclude, desk: true);
            return new { slots = new[] { new { start = bk.Iso(w2.Start), end = bk.Iso(w2.End), resource = "", resourceName = "", occurrence = "" } } };
        }
        if (Json.Truthy(c["extra"]))
        {
            var (option, resource, _) = bk.Details(service, c["option"], c["resource"]);
            var items = bk.VisitServices(service, c["extra"]).Select(x => (x, (Option)null)).ToList(); items[0] = (service, option);
            return new { slots = bk.VisitSlots(items, day, resource, exclude, true, Allowed(c)).Select(Out) };
        }
        return new { slots = bk.Available(c["service"], day, c["option"], c["resource"], c["party"], exclude, Allowed(c), desk: true, force: force, area: c["area"]).Select(Out) };
        static object Out(Slot x) => new { start = x.Start, end = x.End, resource = x.Resource, resourceName = x.ResourceName, occurrence = x.Occurrence };
    }

    [Rpc("prenotazione.nuova", Perm = "booking.write")]
    public static object Create(Call c)
    {
        if (c.Bool("force") && !c.Can("booking.force")) throw new UnauthorizedAccessException("Il tuo ruolo non consente di forzare: chiedi al titolare.");
        var bk = new Bookings(c.C);
        try
        {
            var s = bk.ActiveService(c["service"]);
            var err = Permissions.CheckNewBooking(c.Member, s.LocationId, Bookings.AsId(c["resource"]));
            if (err != null) throw new UnauthorizedAccessException(err);
        }
        catch (BookingException) { }  // Create reports the validation error
        var data = (JsonObject)c.Args.DeepClone();
        if (!data.ContainsKey("request_key")) data["request_key"] = Bookings.TokenUrlSafe(24);
        var (b, _) = bk.Create(data, origin: c.Str("origine") == "agenda" ? "agenda" : "desk", allowed: Allowed(c));
        return new { id = b.Id, status = b.Status };
    }

    static Booking Visible(Call c, Guid id)
    {
        var (vis, args) = Permissions.VisibleBookings(c.Member, c.Perms, "b");
        var p = new Dapper.DynamicParameters(args); p.Add("id", id);
        return c.C.S.One<Booking>($"SELECT b.* FROM core_booking b WHERE b.id=@id AND ({vis})", p) ?? throw new KeyNotFoundException();
    }

    [Rpc("prenotazione.dettaglio", Perm = "booking.read")]
    public static object Detail(Call c)
    {
        var b = Visible(c, c.NeedId("id")); var bk = new Bookings(c.C);
        var data = Permissions.Shown(c.Perms, bk.Data(b));
        var service = c.C.Row<Service>(b.ServiceId); var contact = c.C.Row<Contact>(b.ContactId);
        var rows = bk.VisitRows(b);
        var history = c.C.S.Query<(string, string, DateTime, string)>("SELECT action, actor, created, detail::text FROM core_audit WHERE target=@t ORDER BY created", new { t = b.Id.ToString() }).ToList();
        string Who(string id) { if (!Guid.TryParse(id, out var g)) return id == "" ? "Cliente o sistema" : id; using var s = c.S.Db.Global(); var u = s.Get<User>(g); return u == null ? "—" : c.S.Identity.LoginName(u); }
        var moves = Bookings.MOVES.GetValueOrDefault(b.Status) ?? Array.Empty<string>();
        return new
        {
            id = b.Id, stato = b.Status, inizio = bk.Iso(b.Start), fine = bk.Iso(b.End), persone = b.Party, origine = b.Origin, fonte = b.Source,
            servizio = b.Snap.Str("service") is var sv && sv != "" ? sv : service.Name, tipo = service.Kind, serviceId = b.ServiceId, variante = b.Snap.Str("option"), optionId = b.OptionId,
            sede = b.Snap.Str("location"), operatore = b.Snap.Str("resource"), resourceId = b.ResourceId, cabina = b.Snap.Str("secondary"),
            prezzo = b.Snap.Str("price"), totale = b.Snap.Str("total"), sconto = b.Snap.Str("discount"), forzata = b.Snap["forced"]?.DeepClone(),
            motivo = b.Snap.Str("cancel_reason") is var r && r != "" ? Bookings.REASON_LABELS.GetValueOrDefault(r, r) : "",
            dati = data, campi = b.Snap["field_labels"]?.DeepClone(), contatto = contact == null ? null : new { id = contact.Id, vip = contact.Vip, bloccato = contact.OnlineBlocked },
            visita = rows.Count > 1 ? rows.Select(x => new { id = x.Id, servizio = x.Snap.Str("service"), operatore = x.Snap.Str("resource"), inizio = bk.Iso(x.Start), fine = bk.Iso(x.End), stato = x.Status }) : null,
            modifica = Editing(c, bk, b, rows), codice = b.Id.ToString()[..8], sedeId = b.LocationId,
            notaCliente = contact != null && c.Can("notes.read") ? new Clients(c.C).Data(contact).Str("staff_notes") : "",
            rifiutata = Json.Truthy(b.Snap["declined"]), clienteOk = Json.Truthy(b.Snap["client_confirmed"]), turnover = service.Turnover, maxPersone = service.MaxParty,
            posa = b.GapStart.HasValue ? $"{c.C.Cal.ToLocal(b.GapStart.Value):HH:mm}–{c.C.Cal.ToLocal(b.GapEnd.Value):HH:mm}" : "", sala = c.C.Row<Area>(b.AreaId)?.Name ?? "",
            sostituto = b.Snap.Str("substitute"), originale = b.Snap.Str("original"), videochiamata = service.RemoteUrl ?? "", unita = Presets.UnitsOf(c.Tenant.BusinessType).One,
            oggiOk = c.C.Cal.DayOf(b.Start) <= c.C.Today, inCorso = service.Kind is "resource" or "capacity", testDaFare = TestDue(c, b, service, contact),
            ripetibile = service.Kind is "resource" or "capacity" && b.ContactId.HasValue && Bookings.OPEN.Contains(b.Status), iscritti = service.Kind is "capacity" or "event",
            prossimeSerie = b.Series.HasValue ? c.C.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE series=@s AND start>@st AND status = ANY(@o)", new { s = b.Series, st = b.Start, o = Bookings.OPEN }) : 0,
            operatoriSerie = b.Series.HasValue && service.Kind == "resource" ? bk.Linked(service).Select(r => (object)new { id = r.Id, nome = r.Name }).ToArray() : Array.Empty<object>(),
            marchi = contact == null || !c.Can("contacts.read") ? null : Marks(c, contact),
            serie = b.Series, tavoli = new Tables(c.C, bk).Names(b), annulla = bk.CanUndo(b), passaggi = moves,
            spostabile = Bookings.MOVABLE.Contains(b.Status), passato = b.Start <= c.C.Now,
            whatsapp = c.Can("pii.read") ? ClientMails.WhatsApp(c.C, bk.Data(b).Str("phone"), b) : "",
            storia = history.Select(h => new { azione = h.Item1, chi = Who(h.Item2), quando = bk.Iso(h.Item3), dettagli = JsonNode.Parse(h.Item4) }),
            motivi = Bookings.REASONS.Select(x => new { id = x.Key, nome = x.Label }),
            scheda = Record(c, b, contact, data), foto = DayPhotos(c, b, contact),
        };
    }

    /// <summary>The client record on the booking's page: what was written for this booking, or the form to write it once
    /// the appointment has started, and the three times before with their photos.</summary>
    static object Record(Call c, Booking b, Contact contact, JsonObject data)
    {
        if (contact == null || !c.Can("notes.read") || Json.Truthy(data["notes_hidden"])) return null;
        var x = c.C; var spec = Presets.RecordOf(c.Tenant.BusinessType, x.S.Scalar<long>("SELECT count(*) FROM core_service WHERE kind='resource'") > 0);
        if (spec == null) return null;
        var cl = new Clients(x); var entries = cl.Treatments(contact); var prefill = cl.FromBooking(b); var day = x.Cal.DayOf(b.Start).ToDateTime(TimeOnly.MinValue);
        var photos = Foto.On(c) ? new Photos(x).ByDay(contact.Id) : new();
        object Row((Treatment Row, JsonObject Info) t, bool withPhotos) => new
        { id = t.Row.Id, data = DateOnly.FromDateTime(t.Row.Date), servizio = t.Info.Str("service"), operatore = t.Info.Str("staff"), dettagli = t.Info.Str("details"), foto = withPhotos ? photos.GetValueOrDefault(DateOnly.FromDateTime(t.Row.Date)) : null };
        return new
        {
            titolo = spec.Title, uno = spec.One, cosa = spec.What, suggerimento = spec.Hint, precompilata = prefill, aperta = b.Start <= x.Now && Bookings.ACCEPTED.Contains(b.Status),
            cose = x.S.Query<string>("SELECT name FROM core_service WHERE active ORDER BY name").Concat(spec.Suggest).Distinct(), persone = x.S.Query<string>("SELECT name FROM core_resource WHERE active ORDER BY name"),
            collegate = entries.Where(t => t.Row.BookingId == b.Id).Select(t => Row(t, false)), precedenti = entries.Where(t => t.Row.BookingId != b.Id && t.Row.Date < day).Take(3).Select(t => Row(t, true)),
        };
    }

    static object DayPhotos(Call c, Booking b, Contact contact)
    {
        if (contact == null || !Foto.On(c)) return null;
        var day = c.C.Cal.DayOf(b.Start); var got = new Photos(c.C).Album(contact, day);
        return new { giorno = day, righe = got.Count > 0 ? got[0].Photos : new List<Guid>(), liberi = got.Count > 0 ? got[0].Left : Photos.PER_DAY, scrivi = c.Can("booking.write"), max = Photos.PER_DAY };
    }

    /// <summary>The record entry written from the booking's page (portal.booking_detail, action treatment).</summary>
    [Rpc("prenotazione.voce", Perm = "booking.write")]
    public static object Entry(Call c)
    {
        var b = Visible(c, c.NeedId("id")); var contact = c.C.Row<Contact>(b.ContactId) ?? throw new KeyNotFoundException();
        return new { testo = Clienti.Entry(c, contact, b.Id) };
    }

    static object Marks(Call c, Contact ct)
    {
        int ns = 0, late = 0;
        foreach (var (status, start, snap) in c.C.S.Query<(string, DateTime, string)>("SELECT status, start, snapshot::text FROM core_booking WHERE contact_id=@c AND status IN ('no_show','cancelled')", new { c = ct.Id }))
            if (status == "no_show") ns++; else if (Agenda.Late(status, start, Json.Obj(snap))) late++;
        return new { vip = ct.Vip, bloccato = ct.OnlineBlocked, assenze = ns, tardive = late, etichette = string.IsNullOrEmpty(ct.TagIndex) ? new List<string>() : Json.Strings(new Clients(c.C).Data(ct)["tags"]?.ToJsonString() ?? "[]") };
    }

    /// <summary>portal.test_due: the service wants a test done within so many days.</summary>
    static string TestDue(Call c, Booking b, Service service, Contact contact)
    {
        var days = service.TestDays;
        if (days == 0 || contact == null || !Bookings.OPEN.Contains(b.Status)) return "";
        var done = new Clients(c.C).Data(contact).Str("test_date");
        if (done == "") return "nella scheda del cliente non c'è un test";
        var d = DateOnly.ParseExact(done, "yyyy-MM-dd");
        return d.AddDays(days) < c.C.Cal.DayOf(b.Start) ? $"l'ultimo test è del {d:dd/MM/yyyy}, più di {days} giorni prima" : "";
    }

    /// <summary>public.visiting: a booking of a visit moves with the visit: its first service, with whom, the services after it.</summary>
    static object Editing(Call c, Bookings bk, Booking b, List<Booking> rows)
    {
        var mv = rows.Where(x => Bookings.MOVABLE.Contains(x.Status)).ToList();
        if (mv.Count < 2) return new { service = b.ServiceId.ToString(), option = b.OptionId?.ToString() ?? "", resource = b.ResourceId?.ToString() ?? "", party = b.Party, extra = Array.Empty<object>() };
        return new
        {
            service = mv[0].ServiceId.ToString(), option = mv[0].OptionId?.ToString() ?? "", resource = Json.Truthy(mv[0].Snap["any_resource"]) ? "" : mv[0].ResourceId?.ToString() ?? "", party = b.Party,
            extra = mv.Skip(1).Select(x => (object)new { id = x.ServiceId.ToString(), name = x.Snap.Str("service") is var n && n != "" ? n : c.C.Row<Service>(x.ServiceId).Name }).ToArray(),
        };
    }

    [Rpc("prenotazione.stato", Perm = "booking.cancel")]
    public static object Status(Call c)
    {
        var b = Visible(c, c.NeedId("id")); var bk = new Bookings(c.C);
        var to = c.Str("stato"); var reason = c.Str("motivo");
        if (to == "reject") { to = "cancelled"; reason = "rejected"; }
        var nb = bk.ChangeStatus(b, to, reason, c.Str("nota"), c.Bool("annulla"));
        // "anche le prossime date della serie": the others quietly, the client got the email of this one
        if (to == "cancelled" && !c.Bool("annulla") && b.Series.HasValue && c.Bool("serie"))
            foreach (var other in c.C.S.Query<Booking>("SELECT * FROM core_booking WHERE series=@s AND start>@st AND status = ANY(@o)", new { s = b.Series, st = b.Start, o = Bookings.OPEN }).ToList())
                bk.ChangeStatus(other, "cancelled", reason, quiet: true);
        return new { ok = true, stato = nb.Status };
    }

    [Rpc("prenotazione.note", Perm = "booking.write")]
    public static object Notes(Call c)
    {
        c.Require("notes.read");
        var b = Visible(c, c.NeedId("id")); var bk = new Bookings(c.C);
        var pii = bk.Data(b); var text = c.Str("note"); pii["internal_notes"] = text.Length > 4000 ? text[..4000] : text;
        b.Pii = c.C.Encrypt(pii, "booking:" + b.Id); b.Updated = c.C.Now; c.C.S.Update(b, "pii", "updated");
        c.C.Audit("booking.notes", b.Id);
        return new { ok = true };
    }

    [Rpc("prenotazione.partecipanti", Perm = "booking.write")]
    public static object Participants(Call c)
    {
        c.Require("pii.read");
        var b = Visible(c, c.NeedId("id")); var bk = new Bookings(c.C);
        var names = c.Str("nomi").Split('\n').Select(x => string.Join(" ", x.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))).Where(x => x != "").Select(x => x.Length > 100 ? x[..100] : x).ToList();
        if (names.Count > Math.Max(b.Party, 1) || names.Any(n => n.Any(ch => ch < 32))) throw new BookingException($"Al massimo {b.Party} nomi, uno per riga.");
        var pii = bk.Data(b); pii["participants"] = new JsonArray(names.Select(x => (JsonNode)x).ToArray());
        b.Pii = c.C.Encrypt(pii, "booking:" + b.Id); b.Updated = c.C.Now; c.C.S.Update(b, "pii", "updated");
        c.C.Audit("booking.participants", b.Id, new { count = names.Count });
        return new { ok = true };
    }

    /// <summary>Sposta la serie: this date or this and the next ones, first the plan, then the move.</summary>
    [Rpc("prenotazione.spostaSerie", Perm = "booking.write")]
    public static object ShiftSeries(Call c)
    {
        var b = Visible(c, c.NeedId("id")); var bk = new Bookings(c.C);
        var parsed = Times.ParseAware(c.Str("start"), out var naive);
        var at = parsed == null ? null : naive ? c.C.Cal.Local(DateOnly.FromDateTime(parsed.Value), parsed.Value.TimeOfDay) : parsed;
        if (at == null || !Bookings.MOVABLE.Contains(b.Status)) throw new BookingException("Scegli il nuovo giorno e l'ora.");
        var service = c.C.Row<Service>(b.ServiceId);
        var r = bk.Details(service, null, Json.Truthy(c["resource"]) ? c["resource"] : null).Item2 ?? c.C.Row<Resource>(b.ResourceId);
        var err = Permissions.CheckNewBooking(c.Member, service.LocationId, r?.Id);
        if (err != null) throw new UnauthorizedAccessException(err);
        var following = c.Bool("successive");
        if (c.Bool("sposta")) return new { fatte = bk.SeriesMove(b, at.Value, r, following) };
        return new { date = bk.SeriesMovePlan(b, at.Value, r, following).Select(x => new { da = bk.Iso(x.B.Start), a = bk.Iso(x.At), perche = x.Why }) };
    }

    [Rpc("prenotazione.sposta", Perm = "booking.write")]
    public static object Move(Call c)
    {
        if (c.Bool("force") && !c.Can("booking.force")) throw new UnauthorizedAccessException("Il tuo ruolo non consente di forzare: chiedi al titolare.");
        var b = Visible(c, c.NeedId("id"));
        var data = (JsonObject)c.Args.DeepClone();
        var err = Permissions.CheckNewBooking(c.Member, c.C.Row<Service>(Bookings.AsId(data["service"]) ?? b.ServiceId)?.LocationId ?? b.LocationId, Bookings.AsId(data["resource"]));
        if (err != null) throw new UnauthorizedAccessException(err);
        var moved = new Bookings(c.C).Move(b.Id, data);
        return new { ok = true, id = moved.Id };
    }

    [Rpc("prenotazione.serie", Perm = "booking.write")]
    public static object Series(Call c)
    {
        var b = Visible(c, c.NeedId("id")); var bk = new Bookings(c.C);
        var every = c.Int("ogni"); var until = DateOnly.TryParseExact(c.Str("fino"), "yyyy-MM-dd", out var u) ? u : (DateOnly?)null;
        if (c.Bool("crea")) return new { fatte = bk.SeriesCreate(b, every, until ?? default) };
        return new { date = bk.SeriesPlan(b, every, until).Select(x => new { inizio = bk.Iso(x.Start), perche = x.Why }) };
    }

    // ---------- clients for the form
    [Rpc("clienti.cerca", Perm = "contacts.read")]
    public static object Search(Call c)
    {
        // in Cassa (cassa=true): the code for the receipt and the client's gift cards with credit
        var till = c.Bool("cassa") && c.Can("sale.write"); var cl = new Clients(c.C); var today = c.C.Today;
        object Row(Contact ct, JsonObject raw, JsonObject d) => new
        {
            id = ct.Id, nome = d.Str("first_name"), cognome = d.Str("last_name"), email = d.Str("email"), telefono = d.Str("phone"), vip = ct.Vip, bloccato = ct.OnlineBlocked,
            cf = till && c.Can("pii.read") ? Clients.ReceiptCode(raw) : "",
            gift = till ? new Gift(c.C).Of(ct.Id).Where(g => g.Status == "active" && g.Balance > 0 && !(g.Expires.HasValue && DateOnly.FromDateTime(g.Expires.Value) < today)).Select(g => (object)new { codice = g.Code, credito = Prices.Str(g.Balance).Replace(".", ",") }).ToList() : null,
        };
        if (c.Id("id") is Guid cid)
        {
            var ct = c.C.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = cid });
            if (ct == null || !c.Can("pii.read")) return Array.Empty<object>();
            var d0 = cl.Data(ct);
            return new[] { Row(ct, d0, d0) };
        }
        return cl.Search(c.Str("q"), Math.Clamp(c.Int("quanti", 10), 1, 50)).Select(f => Row(f.Contact, f.Data, Permissions.Shown(c.Perms, f.Data))).ToList();
    }
}

public static class Elenco
{
    /// <summary>The list of bookings (portal.bookings ?vista=elenco): a period, a status, a word to search.</summary>
    [Rpc("prenotazioni.elenco", Perm = "booking.read")]
    public static object List(Call c)
    {
        var x = c.C; var from = c.Day("dal", x.Today); var to = c.Day("al", from.AddDays(30));
        var status = c.Str("stato");
        var statuses = status == "" ? Agenda.SHOWN.Append("cancelled").ToArray() : new[] { status };
        var ag = new Agenda(x, new Viewer(c.Member, c.Perms));
        var rows = ag.Visible("b.start>=@a AND b.start<@z", new { a = x.Cal.Local(from, TimeSpan.Zero), z = x.Cal.Local(to.AddDays(1), TimeSpan.Zero) }, statuses);
        var items = rows.Take(1000).Select(r => ag.Item(r, new Dictionary<Guid, int>())).ToList();
        var q = Crypto.CaseFold(c.Str("q").Trim());
        if (q != "") items = items.Where(i => Crypto.CaseFold(i.Name + " " + i.Service + " " + i.Who).Contains(q)).ToList();
        return new { dal = from, al = to, righe = items, troppe = rows.Count > 1000 };
    }
}
