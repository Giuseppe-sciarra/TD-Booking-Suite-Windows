using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

public class BookingException(string message) : Exception(message);
/// <summary>Forza at the desk: what the booking goes beyond, said before it is saved (force_ok confirms it).</summary>
public sealed class ForceWarning(string message) : BookingException(message);

/// <summary>A free time as available() gives it: ISO times in the business's zone, as the pages and the plugin read them.</summary>
public sealed class Slot
{
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public string Resource { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string Occurrence { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public List<Piece> Plan { get; set; }
}

/// <summary>One service of a visit placed at an exact time: (service, option, r, start, end, bs, be, gap, second).</summary>
public sealed record Piece(Service Service, Option Option, Resource R, DateTime Start, DateTime End, DateTime Bs, DateTime Be, (DateTime? A, DateTime? B) Gap, List<Resource> Second);

/// <summary>core/booking.py, the half that finds free times: lengths, places, operators, cabins, limits.</summary>
public sealed partial class Bookings(Ctx c)
{
    public static readonly string[] HOLDING = { "pending", "confirmed", "arrived", "in_progress", "completed", "no_show" };
    public static readonly string[] OPEN = { "pending", "confirmed", "arrived", "in_progress" };
    public static readonly string[] ACCEPTED = { "confirmed", "arrived", "in_progress", "completed" };
    public static readonly string[] MOVABLE = { "pending", "confirmed" };
    public static readonly Dictionary<string, string[]> MOVES = new()
    {
        ["pending"] = new[] { "confirmed", "cancelled" },
        ["confirmed"] = new[] { "arrived", "in_progress", "completed", "no_show", "cancelled" },
        ["arrived"] = new[] { "in_progress", "completed", "cancelled" },
        ["in_progress"] = new[] { "completed", "cancelled" },
        ["completed"] = new[] { "confirmed" }, ["no_show"] = new[] { "confirmed" }, ["cancelled"] = new[] { "confirmed" },
    };
    public const int UNDO_MINUTES = 5;
    public static readonly (string Key, string Label)[] REASONS =
        { ("client", "Disdetta del cliente"), ("business", "Imprevisto dell'attività"), ("weather", "Maltempo"), ("duplicate", "Prenotazione doppia"), ("other", "Altro") };
    public static readonly Dictionary<string, string> REASON_LABELS =
        REASONS.ToDictionary(x => x.Key, x => x.Label).Concat(new Dictionary<string, string> { ["rejected"] = "Richiesta rifiutata", ["unconfirmed"] = "Non confermata dal cliente" }).ToDictionary(x => x.Key, x => x.Value);
    public static readonly string[] REARM = { "reminded", "reminded2", "arrival", "asked", "client_confirmed", "declined" };
    public const int VISIT_MAX = 5;

    static TimeSpan M(double minutes) => TimeSpan.FromMinutes(minutes);
    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    public string Iso(DateTime utc) => Times.Iso(utc, c.Zone);

    // ---------- input checks
    public static int Integer(JsonNode value, string label, int minimum = 1, int maximum = 10000)
    {
        if (value is JsonValue v && v.TryGetValue<bool>(out _)) throw new BookingException(label + " non valido.");
        var s = value?.ToString() ?? "";
        if (!Regex.IsMatch(s, @"^\d+$") ) throw new BookingException(label + " non valido.");
        if (!long.TryParse(s, out var val) || val < minimum || val > maximum) throw new BookingException(label + " fuori intervallo.");
        return (int)val;
    }
    public static Guid? AsId(JsonNode v) => Guid.TryParse(v?.ToString(), out var g) ? g : null;
    const string INVALID = "Selezione non valida o non disponibile.";

    public Service ActiveService(JsonNode id)
    {
        var g = AsId(id) ?? throw new BookingException(INVALID);
        var s = c.Row<Service>(g);
        if (s == null || !s.Active || !Location(s).Active) throw new BookingException(INVALID);
        return s;
    }
    public Location Location(Service s) => c.Row<Location>(s.LocationId);

    /// <summary>details(): the variant and the operator chosen, checked, and the minutes.</summary>
    public (Option, Resource, int) Details(Service service, JsonNode optionId = null, JsonNode resourceId = null)
    {
        Option option = null; Resource resource = null;
        if (Json.Truthy(optionId))
        {
            option = c.Row<Option>(AsId(optionId) ?? throw new BookingException(INVALID));
            if (option == null || option.ServiceId != service.Id || !option.Active) throw new BookingException(INVALID);
        }
        if (Json.Truthy(resourceId))
        {
            resource = c.Row<Resource>(AsId(resourceId) ?? throw new BookingException(INVALID));
            if (resource == null || !resource.Active || resource.Secondary || resource.LocationId != service.LocationId) throw new BookingException(INVALID);
            if (c.S.Scalar<long>("SELECT count(*) FROM core_serviceresource WHERE service_id=@s AND resource_id=@r", new { s = service.Id, r = resource.Id }) == 0)
                throw new BookingException("La risorsa non appartiene al servizio.");
            if (service.Kind is not ("resource" or "day")) throw new BookingException("Risorsa non prevista.");
        }
        return (option, resource, Length(service, option, resource));
    }

    readonly Dictionary<(Guid, Guid), Serviceresource> links = new();
    public Serviceresource Link(Service s, Resource r)
    {
        if (links.TryGetValue((s.Id, r.Id), out var x)) return x;
        return links[(s.Id, r.Id)] = c.S.One<Serviceresource>("SELECT * FROM core_serviceresource WHERE service_id=@s AND resource_id=@r LIMIT 1", new { s = s.Id, r = r.Id });
    }

    /// <summary>Minutes of an appointment: the variant's, the operator's own, a table by its party, the service's.</summary>
    public int Length(Service service, Option option = null, Resource resource = null, int party = 1)
    {
        if (option != null) return option.Duration;
        if (resource != null && service.Kind == "resource")
        {
            var own = Link(service, resource)?.Duration;
            if (own is > 0) return own.Value;
        }
        if (service.Kind == "capacity" && service.Turnover)
        {
            foreach (var pair in Json.Arr(service.PartyDurations).Select(x => (N: (int)x[0], Min: (int)x[1])).OrderBy(x => x.N).ThenBy(x => x.Min))
                if (party <= pair.N) return pair.Min;
        }
        return service.Duration;
    }

    /// <summary>The sweep of the places: true when party does not fit.</summary>
    public static bool Over(IEnumerable<(DateTime At, int Delta)> events, int party, int cap)
    {
        var occupied = 0;
        foreach (var e in events.OrderBy(x => x.At).ThenBy(x => x.Delta))
        {
            occupied += e.Delta;
            if (occupied + party > cap) return true;
        }
        return party > cap;
    }
    public static List<(DateTime, int)> Spans(IEnumerable<Booking> rows, DateTime start, DateTime end, int? each = null) =>
        rows.SelectMany(r => { var n = each ?? r.Party; return new[] { (Max(r.BusyStart, start), n), (Min(r.BusyEnd, end), -n) }; }).ToList();

    public List<Booking> Rows(string where, object args) =>
        c.S.Query<Booking>("SELECT * FROM core_booking WHERE status = ANY(@holding) AND " + where, Merge(args, new { holding = HOLDING })).ToList();
    static Dapper.DynamicParameters Merge(object a, object b) { var p = new Dapper.DynamicParameters(a); p.AddDynamicParams(b); return p; }
    static Guid[] Ex(IEnumerable<Guid> exclude) => exclude?.ToArray() ?? Array.Empty<Guid>();

    /// <summary>The covers of the whole place, taken by every restaurant service there; the words of the limit passed.</summary>
    public string Shared(Service service, DateTime start, DateTime end, int party, IEnumerable<Guid> exclude = null)
    {
        var place = Location(service);
        if (service.Kind is not ("capacity" or "shift") || place.Capacity == 0) return "";
        var rows = Rows("busy_start<@e AND busy_end>@s AND location_id=@l AND service_id IN (SELECT id FROM core_service WHERE kind IN ('capacity','shift')) AND NOT id = ANY(@x)",
            new { s = start, e = end, l = place.Id, x = Ex(exclude) });
        return Over(Spans(rows, start, end), party, place.Capacity) ? $"oltre i coperti del locale ({place.Capacity})" : "";
    }

    /// <summary>The rhythm of the arrivals, for the kitchen.</summary>
    public string Paced(Service service, DateTime start, int party, IEnumerable<Guid> exclude = null)
    {
        if (service.Kind != "capacity" || service.PaceCovers == 0 && service.PaceBookings == 0) return "";
        var local = c.Cal.ToLocal(start);
        var a = start - M((local.Hour * 60 + local.Minute) % service.Interval) - TimeSpan.FromSeconds(local.Second) - TimeSpan.FromTicks(local.Ticks % TimeSpan.TicksPerSecond);
        var parties = c.S.Query<int>("SELECT party FROM core_booking WHERE service_id=@s AND status = ANY(@h) AND start>=@a AND start<@b AND NOT id = ANY(@x)",
            new { s = service.Id, h = HOLDING, a, b = a + M(service.Interval), x = Ex(exclude) }).ToList();
        if (service.PaceCovers > 0 && parties.Sum() + party > service.PaceCovers || service.PaceBookings > 0 && parties.Count + 1 > service.PaceBookings)
            return $"oltre il ritmo degli arrivi delle {c.Cal.ToLocal(a):HH:mm}";
        return "";
    }

    /// <summary>What the slots of one day with one operator need, read once (2.22.3).</summary>
    public sealed class DayCache { public List<(DateTime A, DateTime B, Timeblock Block)> Spans; public List<Booking> Rows; }
    readonly Dictionary<(Guid, DateOnly), List<Booking>> dayRows = new();
    public DayCache Cache(DateOnly day, Resource resource, Service service)
    {
        if (!dayRows.TryGetValue((resource.Id, day), out var rows))
        {
            var a = c.Cal.Local(day.AddDays(-1), TimeSpan.Zero)!.Value; var z = c.Cal.Local(day.AddDays(2), TimeSpan.Zero)!.Value;
            rows = dayRows[(resource.Id, day)] = Rows("busy_start<@z AND busy_end>@a AND (resource_id=@r OR secondary_id=@r OR upper(more::text) LIKE upper(@like))",
                new { a, z, r = resource.Id, like = "%" + resource.Id + "%" });
        }
        return new DayCache { Spans = c.Cal.BlockSpans(day.AddDays(-1), day.AddDays(1), resource.Id, service.Id), Rows = rows };
    }
    /// <summary>Forget the bookings read for the days (after a write in the same unit of work).</summary>
    public void Fresh() => dayRows.Clear();

    public bool CapacityAvailable(Service service, DateTime start, DateTime end, int party, Resource resource = null, Occurrence occurrence = null,
        IEnumerable<Guid> exclude = null, bool blocks = true, bool online = false, bool rooms = true, (DateTime A, DateTime B)? gap = null, DayCache cache = null)
    {
        if (gap.HasValue && resource != null)
            return CapacityAvailable(service, start, gap.Value.A, party, resource, occurrence, exclude, blocks, online, rooms, null, cache)
                && CapacityAvailable(service, gap.Value.B, end, party, resource, occurrence, exclude, blocks, online, rooms, null, cache);
        if (blocks && (cache != null ? cache.Spans.Any(x => x.A < end && start < x.B) : c.Cal.Blocked(start, end, resource?.Id, service.Id) != null)) return false;
        var excl = Ex(exclude);
        List<Booking> rows; int cap;
        if (cache != null && resource != null) { rows = cache.Rows.Where(r => r.BusyStart < end && r.BusyEnd > start && !excl.Contains(r.Id)).ToList(); cap = 1; party = 1; }
        else if (resource != null)
        {
            rows = Rows("busy_start<@e AND busy_end>@s AND NOT id = ANY(@x) AND (resource_id=@r OR secondary_id=@r OR upper(more::text) LIKE upper(@like))",
                new { s = start, e = end, x = excl, r = resource.Id, like = "%" + resource.Id + "%" });
            cap = 1; party = 1;
        }
        else if (occurrence != null)
        {
            rows = Rows("busy_start<@e AND busy_end>@s AND NOT id = ANY(@x) AND occurrence_id=@o", new { s = start, e = end, x = excl, o = occurrence.Id });
            cap = occurrence.Capacity;
        }
        else
        {
            rows = Rows("busy_start<@e AND busy_end>@s AND NOT id = ANY(@x) AND service_id=@v", new { s = start, e = end, x = excl, v = service.Id });
            cap = service.Capacity;
            if (online && service.OnlineCapacity > 0) cap = Math.Min(cap, service.OnlineCapacity);
        }
        var events = new List<(DateTime, int)>();
        foreach (var row in rows)
        {
            var n = resource != null ? 1 : row.Party;
            var lo = Max(row.BusyStart, start); var hi = Min(row.BusyEnd, end);
            // inside another client's posa the operator is free (the cabin is not)
            if (resource != null && row.GapStart.HasValue && row.ResourceId == resource.Id && row.GapStart <= lo && hi <= row.GapEnd) continue;
            events.Add((lo, n)); events.Add((hi, -n));
        }
        if (c.Tenant.WaitMode == "queue") events.AddRange(new Waitlist(c).Holds(service, start, end, resource, occurrence));
        if (Over(events, party, cap)) return false;
        if (occurrence != null && service.Kind == "shift") return CapacityAvailable(service, start, end, party, exclude: exclude, blocks: false, online: online, rooms: rooms);
        return resource != null || !rooms || Shared(service, start, end, party, exclude) == "";
    }

    readonly Dictionary<(Guid, bool), List<Resource>> linked = new();
    /// <summary>The active operators (or cabins and machines, secondary) of a service, in name order.</summary>
    public List<Resource> Linked(Service service, bool secondary = false)
    {
        if (linked.TryGetValue((service.Id, secondary), out var l)) return l;
        return linked[(service.Id, secondary)] = c.S.Query<Resource>(
            "SELECT * FROM core_resource WHERE active AND secondary=@sec AND location_id=@loc AND id IN (SELECT resource_id FROM core_serviceresource WHERE service_id=@s) ORDER BY name",
            new { sec = secondary, loc = service.LocationId, s = service.Id }).ToList();
    }

    /// <summary>The posa of an appointment: the client stays, the operator is free for another.</summary>
    public (DateTime A, DateTime B)? Posa(Service service, DateTime start, DateTime end, Resource resource)
    {
        if (resource == null || service.Kind != "resource") return null;
        var own = Link(service, resource);
        var (at, minutes) = own != null && own.PauseMinutes.HasValue ? (own.PauseStart ?? 0, own.PauseMinutes.Value) : (service.PauseStart, service.PauseMinutes);
        if (minutes == 0) return null;
        var a = start + M(at); var b = a + M(minutes);
        return start < a && b < end ? (a, b) : null;
    }

    public static List<List<Resource>> Groups(IEnumerable<Resource> pool)
    {
        var order = new List<string>(); var d = new Dictionary<string, List<Resource>>();
        foreach (var r in pool)
        {
            var k = Crypto.CaseFold((r.Group ?? "").Trim());
            if (!d.TryGetValue(k, out var l)) { d[k] = l = new(); order.Add(k); }
            l.Add(r);
        }
        return order.Select(k => d[k]).ToList();
    }

    /// <summary>The first free cabin or machine of each group: null when none needed, false (Ok=false) when a group has none free.</summary>
    public (bool Ok, List<Resource> Got) Support(Service service, DateTime bs, DateTime be, IEnumerable<Guid> exclude = null, List<Resource> pool = null)
    {
        pool ??= Linked(service, true);
        if (pool.Count == 0) return (true, null);
        var got = Groups(pool).Select(g => g.FirstOrDefault(r => CapacityAvailable(service, bs, be, 1, r, exclude: exclude))).ToList();
        return got.Contains(null) ? (false, null) : (true, got);
    }

    public static void Equip(Booking b, List<Resource> second)
    {
        b.SecondaryId = second is { Count: > 0 } ? second[0].Id : null;
        b.MoreIds = second is { Count: > 1 } ? second.Skip(1).Select(r => r.Id.ToString()).ToList() : new();
        b.Snap["secondary"] = second is { Count: > 0 } ? string.Join(", ", second.Select(r => r.Name)) : "";
    }

    /// <summary>Who gets "Il primo disponibile", as its location says.</summary>
    public List<Resource> Ranked(Service service, IEnumerable<Resource> resources, DateOnly day, Guid? prefer = null)
    {
        var way = Location(service).Assign; var list = resources.Where(r => !r.AutoExcluded).ToList();
        if (way == "even")
        {
            var a = c.Cal.Local(day, TimeSpan.Zero)!.Value;
            var load = c.S.Query<(Guid, long)>("SELECT resource_id, count(*) FROM core_booking WHERE resource_id = ANY(@r) AND status = ANY(@h) AND start>=@a AND start<@b GROUP BY resource_id",
                new { r = list.Select(x => x.Id).ToArray(), h = HOLDING, a, b = a.AddDays(1) }).ToDictionary(x => x.Item1, x => x.Item2);
            return list.OrderBy(r => load.GetValueOrDefault(r.Id)).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        }
        if (way == "priority") return list.OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        if (way == "last" && prefer.HasValue) return list.OrderBy(r => r.Id != prefer).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        return list;
    }

    /// <summary>The operator of the client's last visit, for the locations that give it back to him.</summary>
    public Guid? LastOperator(Service service, JsonObject pii, JsonObject data, bool staff)
    {
        if (Location(service).Assign != "last" || Json.Truthy(data["resource"])) return null;
        const string sql = "SELECT resource_id FROM core_booking WHERE resource_id IS NOT NULL AND status = ANY(@a) AND ";
        if (staff && Json.Truthy(data["contact"]))
            return AsId(data["contact"]) is Guid cid ? c.S.One<Guid?>(sql + "contact_id=@c ORDER BY start DESC LIMIT 1", new { a = ACCEPTED, c = cid }) : null;
        if (pii.Str("email") != "")
            return c.S.One<Guid?>(sql + "contact_id IN (SELECT id FROM core_contact WHERE email_index=@i) ORDER BY start DESC LIMIT 1", new { a = ACCEPTED, i = c.Index(pii.Str("email"), "email") });
        return null;
    }

    /// <summary>The posa and the cabin or machine of an appointment; none free is said as Forza says the rest.</summary>
    public ((DateTime?, DateTime?) Gap, List<Resource> Second) Kit(Service service, DateTime start, DateTime end, DateTime bs, DateTime be, Resource resource, List<string> issues, IEnumerable<Guid> exclude = null)
    {
        var pool = service.Kind == "resource" ? Linked(service, true) : new List<Resource>();
        var (ok, second) = Support(service, bs, be, exclude, pool);
        if (!ok)
        {
            second = new();
            foreach (var g in Groups(pool))
            {
                var r = g.FirstOrDefault(x => CapacityAvailable(service, bs, be, 1, x, exclude: exclude));
                if (r != null) second.Add(r);
                else issues.Add("nessuna risorsa libera tra " + string.Join(", ", g.Select(x => x.Name)));
            }
        }
        var p = Posa(service, start, end, resource);
        return (p.HasValue ? (p.Value.A, p.Value.B) : (null, null), second);
    }

    /// <summary>A time that would leave less than minutes free next to another appointment, a block or the edges of the hours.</summary>
    public static bool Holes(List<(DateTime S, DateTime E)> rows, DateTime bs, DateTime be, int minutes)
    {
        var n = M(minutes);
        return rows.Any(x => TimeSpan.Zero < bs - x.E && bs - x.E < n || TimeSpan.Zero < x.S - be && x.S - be < n);
    }

    public bool HasUnits(Service service) => service.Kind == "day" && c.S.Scalar<long>("SELECT count(*) FROM core_serviceresource WHERE service_id=@s", new { s = service.Id }) > 0;

    public List<Resource> FreeUnits(Service service, DateTime bs, DateTime be, int party, Resource resource = null, IEnumerable<Guid> exclude = null) =>
        Linked(service).Where(u => resource == null || u.Id == resource.Id)
            .Where(u => u.Housekeeping != "out" && (u.Capacity == 0 || u.Capacity >= party) && CapacityAvailable(service, bs, be, 1, u, exclude: exclude) && !c.Cal.BlockedStay(bs, be, service, u))
            .ToList();

    static readonly (string P, string Words)[] PERIODS = { ("day", "al giorno"), ("week", "a settimana"), ("month", "al mese") };
    readonly Dictionary<(Guid, DateOnly), Dictionary<string, int[]>> limitCache = new();
    /// <summary>The limits of a service or an operator: what one more booking of minutes that day goes beyond, in words.</summary>
    public string Limited(Guid owner, bool isResource, string name, string limitsJson, DateOnly day, int minutes, IEnumerable<Guid> exclude = null, bool cached = false)
    {
        var lim = Json.Obj(limitsJson);
        if (!lim.Any(x => Json.Truthy(x.Value))) return "";
        if (!cached || !limitCache.TryGetValue((owner, day), out var used))
        {
            var week = day.AddDays(-Calendar.Weekday(day)); var month = new DateOnly(day.Year, day.Month, 1); var next = month.AddMonths(1);
            var from = c.Cal.Local(week < month ? week : month, TimeSpan.Zero)!.Value;
            var to = c.Cal.Local(week.AddDays(7) > next ? week.AddDays(7) : next, TimeSpan.Zero)!.Value;
            var rows = c.S.Query<(DateTime, DateTime)>($"SELECT start, \"end\" FROM core_booking WHERE {(isResource ? "resource_id" : "service_id")}=@o AND status = ANY(@h) AND start>=@a AND start<@b AND NOT id = ANY(@x)",
                new { o = owner, h = HOLDING, a = from, b = to, x = Ex(exclude) });
            used = PERIODS.ToDictionary(p => p.P, _ => new int[2]);
            foreach (var (a, b) in rows)
            {
                var d = c.Cal.DayOf(a);
                foreach (var (p, inside) in new[] { ("day", d == day), ("week", week <= d && d < week.AddDays(7)), ("month", month <= d && d < next) })
                    if (inside) { used[p][0]++; used[p][1] += (int)Math.Floor((b - a).TotalMinutes); }
            }
            if (cached) limitCache[(owner, day)] = used;
        }
        foreach (var (p, words) in PERIODS)
        {
            var n = (int?)lim[p + "_n"] ?? 0; var m = (int?)lim[p + "_m"] ?? 0;
            if (n > 0 && used[p][0] + 1 > n) return $"oltre le {n} prenotazioni {words} di {name}";
            if (m > 0 && used[p][1] + minutes > m) return $"oltre i {m} minuti prenotati {words} di {name}";
        }
        return "";
    }
    public string Limited(Service s, DateOnly day, int minutes, IEnumerable<Guid> exclude = null, bool cached = false) => Limited(s.Id, false, s.Name, s.Limits, day, minutes, exclude, cached);
    public string Limited(Resource r, DateOnly day, int minutes, IEnumerable<Guid> exclude = null, bool cached = false) => Limited(r.Id, true, r.Name, r.Limits, day, minutes, exclude, cached);

    /// <summary>An operator fuori ufficio that day.</summary>
    public static bool Away(Resource r, DateOnly day) =>
        r != null && r.AwayFrom.HasValue && r.AwayUntil.HasValue && DateOnly.FromDateTime(r.AwayFrom.Value) <= day && day <= DateOnly.FromDateTime(r.AwayUntil.Value);

    /// <summary>The candidate times of a day: (start, end, the date of an event).</summary>
    public IEnumerable<(DateTime Start, DateTime End, Occurrence Event)> Candidates(Service service, DateOnly day, Option option = null, Resource resource = null, bool desk = false, bool force = false, int party = 1)
    {
        var now = c.Now;
        var duration = M(Length(service, option, resource, party));
        var before = M(service.BufferBefore); var after = M(service.BufferAfter);
        var lead = desk ? now : now + M(service.LeadMinutes);
        var horizon = now.AddDays(desk ? 730 : service.HorizonDays);
        if (service.Kind is "event" or "shift")
        {
            var rows = c.S.Query<Occurrence>("SELECT * FROM core_occurrence WHERE service_id=@s AND active AND start<=@h" + (force ? "" : " AND start>=@l") + " ORDER BY start",
                new { s = service.Id, h = horizon, l = lead });
            foreach (var ev in rows)
                if (c.Cal.DayOf(ev.Start) == day && (force || c.Cal.Fits(ev.Start - before, ev.End + after, service)))
                    yield return (ev.Start, ev.End, ev);
            yield break;
        }
        if (service.Kind == "day") yield break;
        var whole = service.Kind == "capacity" && !service.Turnover;
        var last = M(service.Kind == "capacity" && !desk ? service.LastArrival : 0);
        var step = M(desk && service.Kind == "resource" ? Math.Min(service.Interval, 15) : service.Interval);
        if (step <= TimeSpan.Zero) yield break;
        foreach (var w in c.Cal.Windows(day, service, resource))
        {
            for (var tick = w.A; tick < w.B; tick += step)
            {
                var end = whole ? w.B - after : tick + duration;
                if (c.Cal.DayOf(tick) == day && lead <= tick && tick <= horizon && tick - before >= w.A && end + after <= w.B && tick <= w.B - last)
                    yield return (tick, end, null);
            }
        }
    }

    /// <summary>Free slots of a day (available()).</summary>
    public List<Slot> Available(JsonNode serviceId, DateOnly day, JsonNode optionId = null, JsonNode resourceId = null, JsonNode party = null, IEnumerable<Guid> exclude = null,
        ICollection<string> allowed = null, bool desk = false, bool force = false, Guid? prefer = null, JsonNode area = null)
    {
        var service = ActiveService(serviceId);
        var (option, resource, _) = Details(service, optionId, resourceId);
        var tables = new Tables(c, this);
        var pickedArea = tables.PickArea(service, area); var table = tables.NeedsTable(service, desk);
        var people = Integer(party ?? 1, "Quantità", 1, desk ? 1000 : service.MaxParty);
        var pace = !desk || service.PaceDesk;
        var today = c.Today;
        if (day < today && !force || day > today.AddDays(desk ? 730 : Math.Min(service.HorizonDays, 730))) return new();
        var resources = new List<Resource> { resource };
        if (service.Kind == "resource" && resource == null)
        {
            resources = Linked(service).ToList();
            if (allowed != null) resources = resources.Where(r => allowed.Contains(r.Id.ToString())).ToList();
            resources = Ranked(service, resources, day, prefer);
            var subs = resources.Where(r => Away(r, day)).Select(r => r.AwayById).ToHashSet();
            resources = resources.OrderBy(r => !subs.Contains(r.Id)).ToList();  // stable, as Python's sorted
        }
        if (!force) resources = resources.Where(r => !Away(r, day)).ToList();
        var rank = new Dictionary<string, int>();
        for (int i = 0; i < resources.Count; i++) rank.TryAdd(resources[i]?.Id.ToString() ?? "", i);
        var pool = service.Kind == "resource" ? Linked(service, true) : new List<Resource>();
        var gap = service.Kind == "resource" && !desk ? Location(service).MinGap : 0;
        DateTime ga = default, gb = default;
        if (gap > 0) { var a0 = c.Cal.Local(day, TimeSpan.Zero)!.Value; ga = a0.AddDays(-1); gb = a0.AddDays(2); }
        var ex = Ex(exclude);
        var result = new List<Slot>();
        foreach (var res in resources)
        {
            var near = new List<(DateTime, DateTime)>();
            if (gap > 0)
            {
                near = c.S.Query<(DateTime, DateTime)>("SELECT busy_start, busy_end FROM core_booking WHERE resource_id=@r AND status = ANY(@h) AND busy_start<@b AND busy_end>@a AND NOT id = ANY(@x)",
                    new { r = res?.Id, h = HOLDING, a = ga, b = gb, x = ex }).ToList();
                near.AddRange(c.Cal.Windows(day, service, res).SelectMany(w => new[] { (w.A, w.A), (w.B, w.B) }));
                near.AddRange(c.Cal.BlockSpans(day, day, res?.Id).Select(x => (x.A, x.B)));
            }
            var cache = res != null && service.Kind == "resource" ? Cache(day, res, service) : null;
            foreach (var (start, end, ev) in Candidates(service, day, option, res, desk, force, people))
            {
                var bs = start - M(service.BufferBefore); var be = end + M(service.BufferAfter);
                if (force || CapacityAvailable(service, bs, be, people, res, ev, ex, online: !desk, gap: Posa(service, start, end, res), cache: cache)
                    && !(pace && Paced(service, start, people, ex) != "") && Support(service, bs, be, ex, pool).Ok && !(gap > 0 && Holes(near, bs, be, gap))
                    && tables.AreaOver(pickedArea, bs, be, people, ex) == "" && !(table && tables.Fit(Location(service), people, bs, be, pickedArea, ex).Count == 0))
                {
                    var minutes = (int)Math.Floor((end - start).TotalMinutes);
                    if (!desk && !force && (Limited(service, day, minutes, ex, true) != "" || res != null && Limited(res, day, minutes, ex, true) != "")) continue;
                    result.Add(new Slot { Start = Iso(start), End = Iso(end), StartUtc = start, EndUtc = end, Resource = res?.Id.ToString() ?? "", ResourceName = res?.Name ?? "", Occurrence = ev?.Id.ToString() ?? "" });
                }
            }
        }
        result = result.OrderBy(x => x.Start, StringComparer.Ordinal).ThenBy(x => rank[x.Resource]).ToList();
        if (service.Kind == "resource" && resource == null)
        {
            var seen = new HashSet<string>();
            result = result.Where(x => seen.Add(x.Start)).ToList();
        }
        return result.Take(500).ToList();
    }
}

public static class Times
{
    /// <summary>Python's datetime.isoformat() of an aware time: 2026-03-29T09:00:00+02:00 (microseconds only when there are).</summary>
    public static string Iso(DateTime utc, TimeZoneInfo zone)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var off = zone.GetUtcOffset(utc);
        var frac = local.Ticks % TimeSpan.TicksPerSecond;
        var s = local.ToString(frac == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return s + (off < TimeSpan.Zero ? "-" : "+") + off.Duration().ToString(@"hh\:mm");
    }
    /// <summary>datetime.fromisoformat for aware times: the UTC instant, null without an offset (naive) or invalid.</summary>
    public static DateTime? ParseAware(string s, out bool naive)
    {
        naive = false;
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.EndsWith("Z")) s = s[..^1] + "+00:00";
        if (DateTimeOffset.TryParseExact(s, new[] { "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd HH:mmzzz" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.UtcDateTime;
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        { naive = true; return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified); }
        return null;
    }
}
