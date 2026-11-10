using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>A span of time in UTC, half open [A, B).</summary>
public readonly record struct Span(DateTime A, DateTime B);

/// <summary>
/// core/calendar.py: opening rules in civil time, inventory in UTC. The business's schedules, exceptions and time blocks
/// are few rows, so they are read once per unit of work and filtered here (the Python filtered them in SQL each time).
/// </summary>
public sealed class Calendar
{
    public TimeZoneInfo Zone { get; }
    readonly List<Schedule> schedules;
    readonly List<Exceptionday> exceptions;
    readonly List<Timeblock> blocks;
    readonly Dictionary<(DateOnly, string, Guid?, Guid?, Guid?), List<Span>> memo = new();

    public Calendar(Scope s, string timezone)
    {
        Zone = Zones.Find(timezone);
        schedules = s.All<Schedule>().ToList();
        exceptions = s.All<Exceptionday>().ToList();
        blocks = s.All<Timeblock>().ToList();
    }

    /// <summary>For tests: the rows given directly.</summary>
    public Calendar(string timezone, IEnumerable<Schedule> schedules, IEnumerable<Exceptionday> exceptions, IEnumerable<Timeblock> blocks)
    {
        Zone = Zones.Find(timezone);
        this.schedules = schedules.ToList(); this.exceptions = exceptions.ToList(); this.blocks = blocks.ToList();
    }

    // ---------- civil time
    /// <summary>The UTC instant of a local day and clock; null when that clock does not exist (spring forward).
    /// fold 0 takes the first of an ambiguous hour (summer time), fold 1 the second, as Python's fold.</summary>
    public static DateTime? Local(DateOnly day, TimeSpan clock, TimeZoneInfo tz, int fold = 0)
    {
        var naive = day.ToDateTime(TimeOnly.MinValue).Add(clock);
        naive = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(naive)) return null;
        TimeSpan offset;
        if (tz.IsAmbiguousTime(naive))
        {
            var o = tz.GetAmbiguousTimeOffsets(naive).OrderByDescending(x => x).ToArray();
            offset = fold == 0 ? o[0] : o[^1];
        }
        else offset = tz.GetUtcOffset(naive);
        return DateTime.SpecifyKind(naive - offset, DateTimeKind.Utc);
    }
    public DateTime? Local(DateOnly day, TimeSpan clock, int fold = 0) => Local(day, clock, Zone, fold);
    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    public DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    // ---------- span arithmetic
    public static List<Span> Merge(IEnumerable<Span> spans)
    {
        var result = new List<Span>();
        foreach (var s in spans.OrderBy(x => x.A).ThenBy(x => x.B))
        {
            if (result.Count > 0 && s.A <= result[^1].B) result[^1] = new Span(result[^1].A, s.B > result[^1].B ? s.B : result[^1].B);
            else if (s.A < s.B) result.Add(s);
        }
        return result;
    }
    public static List<Span> Intersection(List<Span> left, List<Span> right) =>
        Merge(from l in left from r in right let a = Max(l.A, r.A) let b = Min(l.B, r.B) where a < b select new Span(a, b));
    public static List<Span> Minus(List<Span> spans, IEnumerable<Span> cuts)
    {
        foreach (var c in cuts)
            spans = spans.SelectMany(s => new[] { new Span(s.A, Min(s.B, c.A)), new Span(Max(s.A, c.B), s.B) }).Where(p => p.A < p.B).ToList();
        return spans;
    }
    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    // ---------- levels: business, location, service, operator
    public readonly record struct Level(Guid? Location, Guid? Service, Guid? Resource);
    public static List<Level> Levels(Service service = null, Resource resource = null)
    {
        var r = new List<Level> { new(null, null, null) };
        if (service != null) { r.Add(new(service.LocationId, null, null)); r.Add(new(null, service.Id, null)); }
        if (resource != null) r.Add(new(null, null, resource.Id));
        return r;
    }
    static bool At(Level l, Guid? loc, Guid? svc, Guid? res) => l.Location == loc && l.Service == svc && l.Resource == res;
    static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);

    /// <summary>A single date, or a closure from date to until, covering any day from first to last.</summary>
    public static bool Covering(Exceptionday x, DateOnly first, DateOnly? last = null) =>
        D(x.Date) <= (last ?? first) && (x.Until.HasValue ? D(x.Until.Value) >= first : D(x.Date) >= first);

    public static bool InTurn(Schedule rule, DateOnly d) =>
        rule.EveryWeeks < 2 || !rule.ValidFrom.HasValue ||
        ((d.DayNumber - D(rule.ValidFrom.Value).DayNumber) + Weekday(D(rule.ValidFrom.Value))) / 7 % rule.EveryWeeks == 0;

    /// <summary>Python's weekday(): Monday 0 ... Sunday 6.</summary>
    public static int Weekday(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    List<Span> LevelWindows(DateOnly day, string purpose, Level sel, bool inherit)
    {
        var rules = schedules.Where(x => x.Purpose == purpose && At(sel, x.LocationId, x.ServiceId, x.ResourceId)).ToList();
        var exc = exceptions.Where(x => x.Purpose == purpose && At(sel, x.LocationId, x.ServiceId, x.ResourceId)).ToList();
        var spans = new List<Span>(); var shut = new List<Span>();
        foreach (var d in new[] { day.AddDays(-1), day, day.AddDays(1) })
        {
            var overrides = exc.Where(x => Covering(x, d)).ToList();
            var closed = overrides.Any(x => x.Closed);
            if (closed) shut.Add(new Span(Local(d, TimeSpan.Zero)!.Value, Local(d.AddDays(1), TimeSpan.Zero)!.Value));
            List<(TimeSpan?, TimeSpan?)> windows;
            if (overrides.Count > 0) windows = closed ? new() : overrides.Select(x => (x.Start, x.End)).ToList();
            else
            {
                var wd = Weekday(d);
                windows = rules.Where(x => x.Weekday == wd && (!x.ValidFrom.HasValue || D(x.ValidFrom.Value) <= d) && (!x.ValidUntil.HasValue || D(x.ValidUntil.Value) >= d) && InTurn(x, d))
                    .Select(x => ((TimeSpan?)x.Start, (TimeSpan?)x.End)).ToList();
                if (inherit) spans.Add(new Span(Local(d, TimeSpan.Zero)!.Value, Local(d.AddDays(1), TimeSpan.Zero)!.Value));
            }
            foreach (var (begin, end) in windows)
            {
                if (begin == null || end == null || begin == end) continue;
                var a = Local(d, begin.Value);
                var b = Local(end < begin ? d.AddDays(1) : d, end.Value, 1);
                if (a.HasValue && b.HasValue) spans.Add(new Span(a.Value, b.Value));
            }
        }
        return Minus(Merge(spans), shut);
    }

    List<Span> LevelOf(DateOnly day, string purpose, Level sel, int i)
    {
        bool Exists(string p) => schedules.Any(x => x.Purpose == p && At(sel, x.LocationId, x.ServiceId, x.ResourceId)) ||
                                 exceptions.Any(x => x.Purpose == p && At(sel, x.LocationId, x.ServiceId, x.ResourceId));
        var selected = purpose; var exists = Exists(selected);
        if (!exists && purpose is "arrival" or "departure") { selected = "service"; exists = Exists(selected); }
        if (!exists && i > 0) return null;
        var weekly = schedules.Any(x => x.Purpose == selected && At(sel, x.LocationId, x.ServiceId, x.ResourceId));
        return LevelWindows(day, selected, sel, i > 0 && !weekly);
    }

    /// <summary>The open windows of a day: every level that says something, intersected.</summary>
    public List<Span> Windows(DateOnly day, Service service = null, Resource resource = null, string purpose = "service")
    {
        List<Span> current = null; var i = 0;
        foreach (var sel in Levels(service, resource))
        {
            var key = (day, purpose, sel.Location, sel.Service, sel.Resource);
            if (!memo.TryGetValue(key, out var spans)) memo[key] = spans = LevelOf(day, purpose, sel, i);
            i++;
            if (spans == null) continue;
            current = current == null ? spans : Intersection(current, spans);
        }
        return current ?? new();
    }

    public bool Fits(DateTime start, DateTime end, Service service = null, Resource resource = null, string purpose = "service") =>
        Windows(DayOf(start), service, resource, purpose).Any(w => w.A <= start && end <= w.B);

    public bool BlockedStay(DateTime start, DateTime end, Service service, Resource resource = null)
    {
        var first = DayOf(start); var last = DayOf(end.AddTicks(-10));
        var nightsLast = last.AddDays(-1) > first ? last.AddDays(-1) : first;
        foreach (var sel in Levels(service, resource))
        {
            var rows = exceptions.Where(x => x.Purpose == "service" && x.Closed && At(sel, x.LocationId, x.ServiceId, x.ResourceId)).ToList();
            if (rows.Any(x => Covering(x, first, last) && string.IsNullOrEmpty(x.Feed)) || rows.Any(x => Covering(x, first, nightsLast) && !string.IsNullOrEmpty(x.Feed))) return true;
        }
        return false;
    }

    public static bool On(Timeblock b, DateOnly d)
    {
        var day = D(b.Day);
        return d == day || b.Weekly && d > day && (d.DayNumber - day.DayNumber) % 7 == 0 && (!b.Until.HasValue || d <= D(b.Until.Value));
    }

    /// <summary>The time blocks of an operator or a service on the days from first to last, in UTC.</summary>
    public List<(DateTime A, DateTime B, Timeblock Block)> BlockSpans(DateOnly first, DateOnly last, Guid? resource = null, Guid? service = null)
    {
        var out_ = new List<(DateTime, DateTime, Timeblock)>();
        if (resource == null && service == null) return out_;
        foreach (var b in blocks)
        {
            if (!(resource != null && b.ResourceId == resource || service != null && b.ServiceId == service)) continue;
            if (D(b.Day) > last) continue;
            if (!(!b.Weekly && D(b.Day) >= first || b.Weekly && (!b.Until.HasValue || D(b.Until.Value) >= first))) continue;
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                if (!On(b, d)) continue;
                var a = Local(d, b.Start); var z = Local(b.End <= b.Start ? d.AddDays(1) : d, b.End, 1);
                if (a.HasValue && z.HasValue) out_.Add((a.Value, z.Value, b));
            }
        }
        return out_;
    }

    public Timeblock Blocked(DateTime start, DateTime end, Guid? resource = null, Guid? service = null) =>
        BlockSpans(DayOf(start).AddDays(-1), DayOf(end), resource, service).Where(x => x.A < end && start < x.B).Select(x => x.Block).FirstOrDefault();

    // ---------- Italian public holidays
    static readonly (int M, int D, string Name)[] Fixed =
    {
        (1,1,"Capodanno"),(1,6,"Epifania"),(4,25,"Festa della Liberazione"),(5,1,"Festa del Lavoro"),(6,2,"Festa della Repubblica"),(8,15,"Ferragosto"),
        (10,4,"San Francesco d'Assisi"),(11,1,"Ognissanti"),(12,8,"Immacolata"),(12,25,"Natale"),(12,26,"Santo Stefano"),
    };
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int n = h + l - 7 * m + 114;
        return new DateOnly(year, n / 31, n % 31 + 1);
    }
    public static List<(DateOnly Day, string Name)> Holidays(int year, string patronDay = "", string patronName = "")
    {
        var e = Easter(year);
        var r = Fixed.Where(x => (x.M, x.D) != (10, 4) || year >= 2026).Select(x => (new DateOnly(year, x.M, x.D), x.Name)).ToList();
        r.Add((e, "Pasqua")); r.Add((e.AddDays(1), "Lunedì dell'Angelo"));
        if (patronDay is { Length: 5 } && int.TryParse(patronDay[..2], out var pm) && int.TryParse(patronDay[3..], out var pd)
            && pm is >= 1 and <= 12 && pd >= 1 && pd <= DateTime.DaysInMonth(year, pm))
            r.Add((new DateOnly(year, pm, pd), string.IsNullOrEmpty(patronName) ? "Santo patrono" : patronName));
        return r.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList();
    }
}

public static class Zones
{
    static readonly Dictionary<string, TimeZoneInfo> cache = new();
    /// <summary>IANA names (Europe/Rome) as the database keeps them; .NET 8 resolves them on Windows too.</summary>
    public static TimeZoneInfo Find(string name)
    {
        lock (cache)
        {
            if (cache.TryGetValue(name ?? "", out var z)) return z;
            try { z = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrEmpty(name) ? "Europe/Rome" : name); }
            catch (TimeZoneNotFoundException) { z = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"); }
            return cache[name ?? ""] = z;
        }
    }
}
