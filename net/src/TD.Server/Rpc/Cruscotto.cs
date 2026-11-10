using Dapper;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>The Dashboard (portal.dashboard): every number through the visible bookings, so each role counts what it may see.</summary>
public static class Cruscotto
{
    const int WEEKS = 12;

    [Rpc("cruscotto")]
    public static object Dashboard(Call c)
    {
        var x = c.C; var now = x.Now; var today = x.Today; var bk = new Bookings(x);
        var ag = new Agenda(x, new Viewer(c.Member, c.Perms));
        var start = x.Cal.Local(today, TimeSpan.Zero)!.Value; var end = x.Cal.Local(today.AddDays(1), TimeSpan.Zero)!.Value; var week = x.Cal.Local(today.AddDays(7), TimeSpan.Zero)!.Value;
        var (vis, vargs) = Permissions.VisibleBookings(c.Member, c.Perms, "b");
        DynamicParameters P(object args) { var p = new DynamicParameters(args); if (vargs != null) p.AddDynamicParams(vargs); return p; }
        long Count(string where, object args) => x.S.Scalar<long>($"SELECT count(*) FROM core_booking b WHERE ({vis}) AND {where}", P(args));
        var all = Agenda.SHOWN.Append("cancelled").ToArray();
        var since = now.AddDays(-30); var before = now.AddDays(-60);
        var received = Count("b.created>=@s", new { s = since }); var previous = Count("b.created>=@a AND b.created<@s", new { a = before, s = since });
        var cancelled = Count("b.created>=@s AND b.status='cancelled'", new { s = since });
        var contacts = c.Can("contacts.read");
        var metrics = new
        {
            oggi = Count("b.start<@z AND b.\"end\">@a AND b.status = ANY(@st)", new { a = start, z = end, st = Bookings.ACCEPTED }),
            settimana = Count("b.start>=@a AND b.start<@z AND b.status = ANY(@st)", new { a = start, z = week, st = Bookings.ACCEPTED }),
            inArrivo = Count("b.start>=@a AND b.status = ANY(@st)", new { a = end, st = Bookings.ACCEPTED }),
            ricevute = received, andamento = previous > 0 ? (int?)Math.Round((received - previous) * 100.0 / previous, MidpointRounding.ToEven) : null,
            disdette = cancelled, percDisdette = received > 0 ? (int)Math.Round(100.0 * cancelled / received, MidpointRounding.ToEven) : 0,
            clienti = contacts ? x.S.Scalar<long>("SELECT count(*) FROM core_contact") : (long?)null,
            iscritti = contacts ? x.S.Scalar<long>("SELECT count(*) FROM core_contact WHERE consent='yes' AND NOT suppressed") : (long?)null,
            nuovi = contacts ? x.S.Scalar<long>("SELECT count(*) FROM core_contact WHERE created>=@s", new { s = since }) : (long?)null,
        };
        object stats = null;
        if (c.Can("booking.read"))
        {
            var past = new { a = start.AddDays(-30), z = start };
            var counts = new[]
            {
                ("Svolte", "ok", Count("b.start>=@a AND b.start<@z AND b.status='completed'", past)), ("Da chiudere", "open", Count("b.start>=@a AND b.start<@z AND b.status = ANY(@o)", new { past.a, past.z, o = Bookings.OPEN })),
                ("Disdette", "danger", Count("b.start>=@a AND b.start<@z AND b.status='cancelled'", past)), ("Assenze", "warn", Count("b.start>=@a AND b.start<@z AND b.status='no_show'", past)),
            };
            var total = counts.Sum(o => o.Item3);
            var services = x.S.Query<(string, long)>($"SELECT b.snapshot->>'service', count(*) FROM core_booking b WHERE ({vis}) AND b.created>=@s AND b.status<>'cancelled' GROUP BY 1 ORDER BY 2 DESC LIMIT 5", P(new { s = since })).ToList();
            var top = Math.Max(1, services.Select(s => s.Item2).DefaultIfEmpty(0).Max());
            var monday = today.AddDays(-Calendar.Weekday(today)); var first = monday.AddDays(-7 * (WEEKS - 1));
            var weekly = new int[WEEKS];
            foreach (var created in x.S.Query<DateTime>($"SELECT b.created FROM core_booking b WHERE ({vis}) AND b.created>=@a", P(new { a = x.Cal.Local(first, TimeSpan.Zero)!.Value })))
            { var i = (x.Cal.DayOf(created).DayNumber - first.DayNumber) / 7; if (i is >= 0 and < WEEKS) weekly[i]++; }
            var days = new int[7];
            foreach (var s in x.S.Query<DateTime>($"SELECT b.start FROM core_booking b WHERE ({vis}) AND b.start>=@a AND b.start<@z AND b.status = ANY(@st)", P(new { a = start, z = week, st = Bookings.ACCEPTED })))
            { var i = x.Cal.DayOf(s).DayNumber - today.DayNumber; if (i is >= 0 and < 7) days[i]++; }
            stats = new
            {
                esiti = counts.Select(o => new { nome = o.Item1, tono = o.Item2, valore = o.Item3, perc = total > 0 ? (int)Math.Round(100.0 * o.Item3 / total, MidpointRounding.ToEven) : 0 }), esitiTotale = total,
                settimane = weekly, primaSettimana = first, giorni = days,
                servizi = services.Select(s => new { nome = string.IsNullOrEmpty(s.Item1) ? "Servizio" : s.Item1, n = s.Item2, quota = (int)Math.Round(100.0 * s.Item2 / top, MidpointRounding.ToEven) }),
            };
        }
        var none = new Dictionary<Guid, int>();
        var maxParty = new Dictionary<Guid, int>();
        object Row(Booking r, AgendaItem i, bool call = false)
        {
            var d = Permissions.Shown(c.Perms, bk.Data(r));
            return new
            {
                id = r.Id, inizio = bk.Iso(r.Start), nome = d.Str("first_name"), cognome = d.Str("last_name"), servizio = r.Snap.Str("service") is var sv && sv != "" ? sv : x.Row<Service>(r.ServiceId).Name,
                persone = r.Party, maxPersone = x.Row<Service>(r.ServiceId).MaxParty, operatore = r.Snap.Str("resource"), allergie = i.Alert, stato = r.Status, clienteOk = i.ClientOk,
                vip = i.Vip, bloccato = i.Blocked, assenze = i.NoShow, tardive = i.Late, etichette = i.Tags,
                telefono = call && !Json.Truthy(d["masked"]) ? d.Str("phone") : "", whatsapp = call ? ClientMails.WhatsApp(x, d.Str("phone"), r) : "", rifiutata = call && Json.Truthy(r.Snap["declined"]),
            };
        }
        List<object> Rows(List<Booking> rows, bool call = false)
        {
            var items = rows.Select(r => ag.Item(r, none)).ToList(); ag.Mark(items);
            return rows.Select((r, n) => Row(r, items[n], call)).ToList();
        }
        var todayRows = ag.Visible("b.start<@z AND b.\"end\">@a", new { a = start, z = end }, all).Take(50).ToList();
        // shortcut: at most 300 bookings in the list of the 7 days, the agenda has all of them
        var weekRows = ag.Visible("b.start>=@a AND b.start<@z", new { a = start, z = week }, Agenda.SHOWN).Take(300).ToList();
        var weekOut = Rows(weekRows);
        var weekDays = weekRows.Select((r, n) => (Day: x.Cal.DayOf(r.Start), Row: weekOut[n])).GroupBy(p => p.Day).Select(g => new
        { giorno = g.Key, nome = (g.Key.DayNumber - today.DayNumber) switch { 0 => "Oggi", 1 => "Domani", _ => "" }, righe = g.Select(p => p.Row) });
        var asks = ag.Visible("b.\"end\">@a", new { a = now.AddDays(-30) }, new[] { "pending" });
        var closing = ag.Visible("b.\"end\"<=@n AND b.\"end\">@a", new { n = now, a = now.AddDays(-30) }, new[] { "confirmed", "arrived", "in_progress" }); closing.Reverse();
        var calls = ag.Visible("b.start>@n AND b.start<=@z AND jsonb_exists(b.snapshot,'asked') AND NOT jsonb_exists(b.snapshot,'client_confirmed')", new { n = now, z = now.AddHours(48) }, new[] { "confirmed" });
        object undo = null;
        if (c.Id("annullabile") is Guid uid && ag.Visible("b.id=@id", new { id = uid }, all).FirstOrDefault() is Booking ub && bk.CanUndo(ub))
        { var d = Permissions.Shown(c.Perms, bk.Data(ub)); undo = new { id = ub.Id, nome = d.Str("first_name"), cognome = d.Str("last_name"), inizio = bk.Iso(ub.Start), stato = ub.Status }; }
        var turnDay = c.Day("turni", today);
        var waits = c.Can("booking.read");
        object born = null;
        if (contacts && c.Can("pii.read"))
        {
            var h = Clients.SearchKeys(x.Key); var when = new Dictionary<string, DateOnly>();
            for (var i = 0; i < 7; i++)
            {
                var d = today.AddDays(i); when[h("b:" + d.ToString("MM-dd"))] = d;
                if (d.Month == 2 && d.Day == 28 && d.AddDays(1).Month == 3) when[h("b:02-29")] = d;
            }
            var cl = new Clients(x);
            born = x.S.Query<Contact>("SELECT * FROM core_contact WHERE birthday_index = ANY(@k) AND email_index NOT LIKE 'erased:%'", new { k = when.Keys.ToArray() }).Select(ct =>
            {
                var d = cl.Data(ct); var day = when[ct.BirthdayIndex];
                return new { id = ct.Id, vip = ct.Vip, nome = d.Str("first_name"), cognome = d.Str("last_name"), giorno = day, anni = day.Year - int.Parse(d.Str("birthday")[..4]) };
            }).OrderBy(b => b.giorno).Take(20).ToList();
        }
        return new
        {
            oggi = today, numeri = metrics, statistiche = stats, righe = Rows(todayRows), settimana = weekDays,
            daChiamare = Rows(calls.Take(20).ToList(), true), daChiamareN = calls.Count, richieste = Rows(asks.Take(20).ToList()), richiesteN = asks.Count, daChiudere = Rows(closing.Take(20).ToList()), daChiudereN = closing.Count,
            annulla = undo, giornoTurni = turnDay, turni = c.Can("catalog.write") ? Turns(x, turnDay)?.Select(t => new { servizio = t.Service.Id, nome = t.Name, servizioNome = t.Service.Name, sede = x.Row<Location>(t.Service.LocationId).Name, inizio = bk.Iso(t.A), fine = bk.Iso(t.B), chiuso = t.Closed, persone = t.People }) : null,
            piuSedi = x.S.Scalar<long>("SELECT count(*) FROM core_location WHERE active") > 1,
            attesa = new
            {
                n = waits ? x.S.Scalar<long>("SELECT count(*) FROM core_waitentry WHERE status='waiting' AND last>=@t", new { t = today.ToDateTime(TimeOnly.MinValue) }) : 0,
                liberi = waits ? x.S.Scalar<long>("SELECT count(*) FROM core_waitentry WHERE status='waiting' AND last>=@t AND jsonb_exists(snapshot,'free')", new { t = today.ToDateTime(TimeOnly.MinValue) }) : 0,
            },
            compleanni = born,
            senzaAppuntamento = x.S.Scalar<long>("SELECT count(*) FROM core_service WHERE active AND kind IN ('resource','capacity')") > 0,
            inAttesa = x.S.Scalar<long>("SELECT count(*) FROM core_delivery WHERE status='pending' AND channel<>'webhook'"),
            daVerificare = x.S.Scalar<long>("SELECT count(*) FROM core_delivery WHERE status IN ('failed','unknown') AND channel<>'webhook'"),
            sito = Site(c),
        };
    }

    /// <summary>wpsync.banner: the site fed by this PC: in progress, done with how many new bookings, or the error.</summary>
    static object Site(Call c)
    {
        var t = c.Tenant; if (Json.Obj(t.WpSync).Count == 0) return null;
        var s = Json.Obj(t.WpState); DateTime? When(string k) => DateTime.TryParse(s.Str(k), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;
        if (When("running") is DateTime r && c.C.Now - r < TimeSpan.FromMinutes(10)) return new { inCorso = true };
        if (When("at") is not DateTime at) return null;
        var ids = s["ids"] as System.Text.Json.Nodes.JsonArray;
        return new { inCorso = false, alle = Times.Iso(at, c.C.Zone), nuove = int.TryParse(s.Str("new"), out var n) ? n : 0, errore = s.Str("error"), una = ids?.Count == 1 ? ids[0]?.ToString() : "" };
    }

    public sealed record Turn(Service Service, DateTime A, DateTime B, string Name, bool Closed, long People);

    /// <summary>agenda.turns: the turns of a restaurant on a day, open or closed for that day, with the people booked.</summary>
    public static List<Turn> Turns(Ctx x, DateOnly day)
    {
        var services = x.S.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE (s.kind='shift' OR (s.kind='capacity' AND NOT s.turnover)) AND s.active AND l.active ORDER BY l.name, s.name").ToList();
        if (services.Count == 0) return null;
        var first = x.Cal.Local(day, TimeSpan.Zero)!.Value; var outp = new List<Turn>();
        foreach (var s in services)
        {
            var spans = s.Kind == "shift"
                ? x.S.Query<(DateTime, DateTime, string)>("SELECT o.start, o.\"end\", coalesce(r.name,'') FROM core_occurrence o LEFT JOIN core_shiftrule r ON r.id=o.rule_id WHERE o.service_id=@s AND o.active AND o.start>=@a AND o.start<@z ORDER BY o.start", new { s = s.Id, a = first, z = first.AddDays(1) }).ToList()
                : x.Cal.Windows(day, s).Where(w => x.Cal.DayOf(w.A) == day).Select(w => (w.A, w.B, "")).ToList();
            foreach (var (a, b, name) in spans)
                outp.Add(new Turn(s, a, b, name != "" ? name : x.Cal.ToLocal(a).Hour < 16 ? "Pranzo" : "Cena", x.Cal.Blocked(a, b, service: s.Id) != null,
                    x.S.Scalar<long>("SELECT coalesce(sum(party),0) FROM core_booking WHERE service_id=@s AND status = ANY(@o) AND busy_start<@b AND busy_end>@a", new { s = s.Id, o = Bookings.OPEN, a, b })));
        }
        return outp;
    }

    /// <summary>agenda.turn_switch: Chiudi / Riapri a turn for one day. The bookings already there stay: they are counted first.</summary>
    [Rpc("turno.cambia", Perm = "catalog.write")]
    public static object Switch(Call c)
    {
        var x = c.C; var day = c.Day("giorno"); var sid = c.NeedId("servizio");
        var turn = Turns(x, day)?.FirstOrDefault(t => t.Service.Id == sid && new Bookings(x).Iso(t.A) == c.Str("inizio")) ?? throw new BookingException("Turno non trovato: ricarica la pagina.");
        var la = x.Cal.ToLocal(turn.A); var lb = x.Cal.ToLocal(turn.B);
        var label = $"{turn.Name} delle {la:HH:mm} di {day:dd/MM}";
        if (c.Bool("apri"))
        {
            x.Lock();
            foreach (var g in x.Cal.BlockSpans(day, day, service: sid).Where(s => s.Block.Reason == "closed" && !s.Block.Weekly && DateOnly.FromDateTime(s.Block.Day) == day).Select(s => s.Block.Id).Distinct().ToList()) x.S.Delete<Timeblock>(g);
            x.Audit("agenda.turn_open", sid, new { day = day.ToString("yyyy-MM-dd"), start = c.Str("inizio") }); x.CalendarChanged();
            new Waitlist(x).Freed(turn.A, turn.B);
            return new { ok = true, testo = $"{label}: di nuovo aperto, si prenota." };
        }
        if (turn.Closed) return new { ok = true, testo = "" };
        var held = x.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE service_id=@s AND status = ANY(@o) AND busy_start<@b AND busy_end>@a", new { s = sid, o = Bookings.OPEN, a = turn.A, b = turn.B });
        if (held > 0 && !c.Bool("force_ok"))
            throw new ForceWarning($"In questo turno ci sono {held} prenotazioni. Chiudendolo restano valide: chiama i clienti per spostarle o annullarle. Online e al banco il turno non si prenota più. Chiudere il turno?");
        x.Lock();
        // shortcut: the hours of the turn are blocked for the service, so a turn overlapping them closes too
        x.S.Insert(new Timeblock { TenantId = x.Tenant.Id, ServiceId = sid, Day = day.ToDateTime(TimeOnly.MinValue), Start = la.TimeOfDay, End = lb.TimeOfDay, Reason = "closed", Created = x.Now });
        x.Audit("agenda.turn_closed", sid, new { day = day.ToString("yyyy-MM-dd"), start = c.Str("inizio"), bookings = held }); x.CalendarChanged();
        return new { ok = true, testo = $"{label}: chiuso, online e al banco non si prenota." + (held > 0 ? $" {held} prenotazioni restano, da ricontattare." : "") };
    }
}
