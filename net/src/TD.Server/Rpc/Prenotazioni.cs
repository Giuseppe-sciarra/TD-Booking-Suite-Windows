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
                etichetta = s.ResourceLabel, campi = JsonNode.Parse(s.Fields), prezzo = s.Price.HasValue ? Prices.Euro(s.Price) : "",
                varianti = options[s.Id].OrderBy(o => o.Name).Select(o => new { id = o.Id, nome = o.Name, durata = o.Duration, prezzo = o.Price.HasValue ? Prices.Euro(o.Price) : "" }),
                sale = s.Kind is "capacity" or "shift" ? areas[s.LocationId].Select(a => new { id = a.Id, nome = a.Name }) : null,
                operatori = resources.Where(r => r.LocationId == s.LocationId && links[s.Id].Any(l => l.ResourceId == r.Id) && (mine == null || mine.Contains(r.Id.ToString())))
                    .Select(r => new
                    {
                        id = r.Id, nome = r.Name, posti = r.Capacity,
                        assente = r.AwayUntil.HasValue && DateOnly.FromDateTime(r.AwayUntil.Value) >= today ? new { dal = r.AwayFrom, al = r.AwayUntil, sostituto = r.AwayById } : null,
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
            serie = b.Series, tavoli = new Tables(c.C, bk).Names(b), annulla = bk.CanUndo(b), passaggi = moves,
            spostabile = Bookings.MOVABLE.Contains(b.Status), passato = b.Start <= c.C.Now,
            whatsapp = c.Can("pii.read") ? ClientMails.WhatsApp(c.C, bk.Data(b).Str("phone"), b) : "",
            storia = history.Select(h => new { azione = h.Item1, chi = Who(h.Item2), quando = bk.Iso(h.Item3), dettagli = JsonNode.Parse(h.Item4) }),
            motivi = Bookings.REASONS.Select(x => new { id = x.Key, nome = x.Label }),
        };
    }

    [Rpc("prenotazione.stato", Perm = "booking.write")]
    public static object Status(Call c)
    {
        var b = Visible(c, c.NeedId("id")); var bk = new Bookings(c.C);
        var nb = bk.ChangeStatus(b, c.Str("stato"), c.Str("motivo"), c.Str("nota"), c.Bool("annulla"));
        return new { ok = true, stato = nb.Status };
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
        var shown = c.Can("pii.read");
        return new Clients(c.C).Search(c.Str("q"), Math.Clamp(c.Int("quanti", 10), 1, 50)).Select(f =>
        {
            var d = Permissions.Shown(c.Perms, f.Data);
            return new { id = f.Contact.Id, nome = d.Str("first_name"), cognome = d.Str("last_name"), email = d.Str("email"), telefono = d.Str("phone"), vip = f.Contact.Vip, bloccato = f.Contact.OnlineBlocked };
        });
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
