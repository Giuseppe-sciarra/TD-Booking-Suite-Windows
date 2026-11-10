using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>What a change of the catalog leaves behind (portal.calendar_conflicts), the turns of a restaurant made into
/// dates (shifts.materialize) and the Italian public holidays (calendar.holidays).</summary>
public sealed class Catalog(Ctx c)
{
    /// <summary>Bookings that take part in a peak above the capacity of their group.</summary>
    HashSet<Guid> Overbooked(List<Booking> rows)
    {
        var groups = new Dictionary<(string, Guid), (int Cap, List<(DateTime At, int Starts, int N, Guid Id)> Events)>();
        foreach (var b in rows)
        {
            var s = c.Row<Service>(b.ServiceId); var keys = new List<((string, Guid), int, int)>();
            if (b.ResourceId.HasValue) keys.Add((("resource", b.ResourceId.Value), 1, 1));
            else if (b.OccurrenceId.HasValue)
            {
                keys.Add((("occurrence", b.OccurrenceId.Value), c.Row<Occurrence>(b.OccurrenceId).Capacity, b.Party));
                if (s.Kind == "shift") keys.Add((("service", b.ServiceId), s.Capacity, b.Party));
            }
            else keys.Add((("service", b.ServiceId), s.Capacity, b.Party));
            foreach (var (key, cap, n) in keys)
            {
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (cap, new());
                g.Events.Add((b.BusyStart, 1, n, b.Id)); g.Events.Add((b.BusyEnd, 0, n, b.Id));
            }
        }
        var result = new HashSet<Guid>();
        foreach (var g in groups.Values)
        {
            var active = new Dictionary<Guid, int>(); var occupied = 0;
            // ends sort before starts at the same instant: touching intervals do not overlap
            foreach (var e in g.Events.OrderBy(e => e.At).ThenBy(e => e.Starts))
            {
                if (e.Starts == 1) { active[e.Id] = e.N; occupied += e.N; if (occupied > g.Cap) result.UnionWith(active.Keys); }
                else if (active.Remove(e.Id, out var n)) occupied -= n;
            }
        }
        return result;
    }

    /// <summary>Complete impact check after a catalog change: the first 8 characters of every booking left out of place.</summary>
    public List<string> Conflicts()
    {
        c.Refresh(); var cal = c.Cal; var tick = TimeSpan.FromTicks(10);
        var rows = c.S.Query<Booking>("SELECT * FROM core_booking WHERE status = ANY(@o) AND \"end\">@n ORDER BY start, id", new { o = Bookings.OPEN, n = c.Now }).ToList();
        var links = c.S.Query<(Guid, Guid)>("SELECT service_id, resource_id FROM core_serviceresource").ToHashSet(); var conflicts = new List<Guid>();
        foreach (var b in rows)
        {
            var s = c.Row<Service>(b.ServiceId); var loc = c.Row<Location>(b.LocationId); var r = c.Row<Resource>(b.ResourceId); var o = c.Row<Option>(b.OptionId);
            var valid = s.Active && s.LocationId == b.LocationId && loc.Active && (r == null || r.LocationId == b.LocationId && r.Active) && (o == null || o.Active) && (r == null || links.Contains((s.Id, r.Id)));
            if (s.Kind == "day") valid = valid && cal.Fits(b.Start, b.Start + tick, s, purpose: "arrival") && cal.Fits(b.End, b.End + tick, s, purpose: "departure") && !cal.BlockedStay(b.Start, b.End, s, r);
            else valid = valid && cal.Fits(b.BusyStart, b.BusyEnd, s, r);
            // a pause or a turn closed over a booking: to call back as the hours that change
            valid = valid && cal.Blocked(b.BusyStart, b.BusyEnd, b.ResourceId, b.ServiceId) == null;
            if (!valid) conflicts.Add(b.Id);
        }
        var over = Overbooked(rows);
        conflicts.AddRange(rows.Where(b => over.Contains(b.Id) && !conflicts.Contains(b.Id)).Select(b => b.Id));
        return conflicts.Select(id => id.ToString()[..8]).ToList();
    }
    public static string Listed(List<string> refs) => string.Join(", ", refs.Take(10)) + (refs.Count > 10 ? " e altre" : "");
    public static string Unsettled(List<string> conflicts) => $"La modifica lascia {conflicts.Count} prenotazioni da verificare: {Listed(conflicts)}. Conferma esplicitamente dopo averle controllate.";

    /// <summary>Create the recurring seating dates of a turn; never rewrite occupied turns.</summary>
    public void Materialize(Shiftrule rule)
    {
        var now = c.Now; var today = c.Today; var service = c.Row<Service>(rule.ServiceId);
        if (c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_booking b JOIN core_occurrence o ON o.id=b.occurrence_id WHERE o.rule_id=@r AND o.start>=@n AND b.status = ANY(@s))", new { r = rule.Id, n = now, s = Bookings.OPEN }))
            throw new BookingException("Questo turno ha prenotazioni future. Gestiscile prima di modificare la regola; puoi creare un nuovo turno per un periodo successivo.");
        c.S.Execute("UPDATE core_occurrence SET active=false WHERE rule_id=@r AND start>=@n", new { r = rule.Id, n = now });
        if (!rule.Active) return;
        DateOnly? D(DateTime? d) => d.HasValue ? DateOnly.FromDateTime(d.Value) : null;
        var first = D(rule.ValidFrom) is DateOnly f && f > today ? f : today; var far = today.AddDays(730);
        var last = today.AddDays(Math.Min(service.HorizonDays, 730)); if ((D(rule.ValidUntil) ?? far) < last) last = D(rule.ValidUntil) ?? far;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (Calendar.Weekday(day) != rule.Weekday) continue;
            var start = c.Cal.Local(day, rule.Start); var end = c.Cal.Local(rule.End < rule.Start ? day.AddDays(1) : day, rule.End, 1);
            if (!start.HasValue || !end.HasValue || end <= start) continue;
            var occ = c.S.One<Occurrence>("SELECT * FROM core_occurrence WHERE rule_id=@r AND start=@s LIMIT 1", new { r = rule.Id, s = start });
            if (occ != null) { occ.End = end.Value; occ.Capacity = rule.Capacity; occ.Active = true; c.S.Update(occ, "end", "capacity", "active"); }
            else c.S.Insert(new Occurrence { TenantId = c.Tenant.Id, RuleId = rule.Id, ServiceId = rule.ServiceId, Start = start.Value, End = end.Value, Capacity = rule.Capacity });
        }
    }

    // ---------- the Italian public holidays; 4 October is a national holiday again from 2026 (San Francesco d'Assisi)
    static readonly (int M, int D, string Name)[] HOLIDAYS = { (1, 1, "Capodanno"), (1, 6, "Epifania"), (4, 25, "Festa della Liberazione"), (5, 1, "Festa del Lavoro"), (6, 2, "Festa della Repubblica"), (8, 15, "Ferragosto"),
        (10, 4, "San Francesco d'Assisi"), (11, 1, "Ognissanti"), (12, 8, "Immacolata"), (12, 25, "Natale"), (12, 26, "Santo Stefano") };
    /// <summary>The public holidays of a year with Easter and Easter Monday, and the patron saint of the town ("06-29").</summary>
    public static List<(DateOnly Day, string Name)> Holidays(int year, string patronDay = "", string patronName = "")
    {
        var e = Easter(year); var outp = HOLIDAYS.Where(h => (h.M, h.D) != (10, 4) || year >= 2026).Select(h => (new DateOnly(year, h.M, h.D), h.Name)).ToList();
        outp.Add((e, "Pasqua")); outp.Add((e.AddDays(1), "Lunedì dell'Angelo"));
        if ((patronDay ?? "").Length == 5 && int.TryParse(patronDay[..2], out var m) && int.TryParse(patronDay[3..], out var d) && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(year, m))
            outp.Add((new DateOnly(year, m, d), string.IsNullOrEmpty(patronName) ? "Santo patrono" : patronName));
        return outp.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList();
    }
    /// <summary>Easter Sunday in the Gregorian calendar (the anonymous algorithm, Meeus/Jones/Butcher).</summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, cc = year % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30, i = cc / 4, k = cc % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        return new DateOnly(year, (h + l - 7 * m + 114) / 31, (h + l - 7 * m + 114) % 31 + 1);
    }
}
