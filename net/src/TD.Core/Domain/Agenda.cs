using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>Who looks at the agenda and what he may see: the membership's scope and rights (request.member, can()).</summary>
public sealed record Viewer(Membership Member, HashSet<string> Perms)
{
    public bool Can(string p) => Perms.Contains(p);
    public List<string> Resources => Member == null ? new() : Permissions.Ids(Member.Resources);
    public List<string> Locations => Member == null ? new() : Permissions.Ids(Member.Locations);
}

/// <summary>An appointment as the agenda shows it (agenda.item), with the client's marks (clients.mark).</summary>
public sealed class AgendaItem
{
    public Guid Id { get; set; }
    public Guid? Resource { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? ContactId { get; set; }
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public string Who { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Status { get; set; } = "";
    public int Color { get; set; }
    public int Party { get; set; }
    public List<string> Alert { get; set; } = new();
    public bool Movable { get; set; }
    public int Minutes { get; set; }
    public int Snap { get; set; }
    public string Phase { get; set; } = "";
    public string PhaseLabel { get; set; } = "";
    public bool Visit { get; set; }
    public bool Forced { get; set; }
    public string Origin { get; set; } = "";
    public bool Vip { get; set; }
    public bool Blocked { get; set; }
    public List<string> Tags { get; set; } = new();
    public int NoShow { get; set; }
    public int Late { get; set; }
    public string Notes { get; set; } = "";
    public string Price { get; set; } = "";
}

public sealed class AgendaBlock { public Guid Id { get; set; } public Guid? Resource { get; set; } public DateTime Start { get; set; } public DateTime End { get; set; } public string Label { get; set; } = ""; public string Reason { get; set; } = ""; public bool Weekly { get; set; } public string Note { get; set; } = ""; }
public sealed class AgendaOperator { public Guid Id { get; set; } public string Name { get; set; } = ""; public int Color { get; set; } public List<Span> Open { get; set; } = new(); public bool Works { get; set; } public string PhotoUrl { get; set; } = ""; }
public sealed class AgendaLocation { public Guid Id { get; set; } public string Name { get; set; } = ""; }

public sealed class AgendaDay
{
    public DateOnly Day { get; set; }
    public DateOnly Today { get; set; }
    public List<AgendaLocation> Places { get; set; } = new();
    public Guid? Location { get; set; }
    public List<AgendaOperator> Operators { get; set; } = new();
    public List<string> Off { get; set; } = new();
    public List<AgendaItem> Items { get; set; } = new();
    public List<AgendaBlock> Blocks { get; set; } = new();
    public int FirstMinute { get; set; }
    public int LastMinute { get; set; }
    public bool ClosedDay { get; set; }
    public bool Writer { get; set; }
    public bool Appointments { get; set; }
    public string One { get; set; } = "";
    public string Many { get; set; } = "";
    public int Total { get; set; }
}

/// <summary>core/agenda.py, the data of the day, the week and the month; the windows draw them.</summary>
public sealed class Agenda(Ctx c, Viewer v)
{
    public const int STEP = 15, COLORS = 8;
    public static readonly string[] SHOWN = { "pending", "confirmed", "arrived", "in_progress", "completed", "no_show" };
    public static readonly string[] MONTHS = { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" };

    public bool Fits() => c.S.Scalar<long>("SELECT count(*) FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND s.kind='resource' AND l.active") > 0;

    public (List<AgendaLocation>, Location) Place(bool appointments, Guid? wanted)
    {
        var rows = c.S.Query<Location>("SELECT * FROM core_location WHERE active AND id IN (SELECT location_id FROM core_service WHERE active" + (appointments ? " AND kind='resource'" : "") + ") ORDER BY name").ToList();
        if (v.Locations.Count > 0) rows = rows.Where(l => v.Locations.Contains(l.Id.ToString())).ToList();
        var chosen = rows.FirstOrDefault(l => l.Id == wanted) ?? rows.FirstOrDefault();
        return (rows.Select(l => new AgendaLocation { Id = l.Id, Name = l.Name }).ToList(), chosen);
    }

    /// <summary>Who works in this location for appointments, as the viewer may see them.</summary>
    public List<Resource> Operators(Location location)
    {
        var rows = c.S.Query<Resource>(@"SELECT * FROM core_resource WHERE active AND NOT secondary AND location_id=@l AND id IN
            (SELECT sr.resource_id FROM core_serviceresource sr JOIN core_service s ON s.id=sr.service_id WHERE s.active AND s.kind='resource' AND s.location_id=@l) ORDER BY name", new { l = location.Id }).ToList();
        if (v.Resources.Count > 0) rows = rows.Where(r => v.Resources.Contains(r.Id.ToString())).ToList();
        return rows;
    }
    public static Dictionary<Guid, int> Colors(List<Resource> ops) => ops.Select((o, i) => (o.Id, i % COLORS + 1)).ToDictionary(x => x.Id, x => x.Item2);

    Dictionary<Guid, List<Service>> ServicesOf(List<Resource> ops)
    {
        var outp = ops.ToDictionary(o => o.Id, _ => new List<Service>());
        foreach (var (rid, sid) in c.S.Query<(Guid, Guid)>("SELECT sr.resource_id, sr.service_id FROM core_serviceresource sr JOIN core_service s ON s.id=sr.service_id WHERE sr.resource_id = ANY(@r) AND s.active AND s.kind='resource'", new { r = ops.Select(o => o.Id).ToArray() }))
            outp[rid].Add(c.Row<Service>(sid));
        return outp;
    }

    /// <summary>When an operator can be booked that day: the union of his services' windows, minus his blocks.</summary>
    public List<Span> OpenSpans(DateOnly day, Resource op, List<Service> services)
    {
        var spans = new List<Span>();
        foreach (var s in services) spans.AddRange(c.Cal.Windows(day, s, op));
        return Calendar.Minus(Calendar.Merge(spans), c.Cal.BlockSpans(day.AddDays(-1), day, op.Id).Select(x => new Span(x.A, x.B)));
    }

    public List<Booking> Visible(string where, object args, string[] statuses)
    {
        var (vis, vargs) = Permissions.VisibleBookings(v.Member, v.Perms, "b");
        var p = new Dapper.DynamicParameters(args); if (vargs != null) p.AddDynamicParams(vargs); p.Add("st", statuses);
        return c.S.Query<Booking>($"SELECT b.* FROM core_booking b WHERE b.status = ANY(@st) AND ({vis}) AND {where} ORDER BY b.start", p).ToList();
    }

    /// <summary>The marks of the clients (VIP, not bookable online, tags, absences, late cancellations) for who may see the cards.</summary>
    void Mark(List<AgendaItem> items)
    {
        var ids = items.Where(i => i.ContactId.HasValue).Select(i => i.ContactId.Value).Distinct().ToArray();
        if (ids.Length == 0 || !v.Can("contacts.read")) return;
        var counts = new Dictionary<Guid, int[]>();
        foreach (var (cid, status, start, snap) in c.S.Query<(Guid, string, DateTime, string)>("SELECT contact_id, status, start, snapshot::text FROM core_booking WHERE contact_id = ANY(@c) AND status IN ('no_show','cancelled')", new { c = ids }))
        {
            if (!counts.TryGetValue(cid, out var n)) counts[cid] = n = new int[2];
            if (status == "no_show") n[0]++;
            else if (Late(status, start, Json.Obj(snap))) n[1]++;
        }
        var cards = c.S.Query<Contact>("SELECT * FROM core_contact WHERE id = ANY(@c)", new { c = ids }).ToDictionary(x => x.Id);
        var cl = new Clients(c);
        foreach (var i in items)
        {
            if (i.ContactId is not Guid id || !cards.TryGetValue(id, out var ct)) continue;
            var n = counts.GetValueOrDefault(id) ?? new int[2];
            i.Vip = ct.Vip; i.Blocked = ct.OnlineBlocked; i.NoShow = n[0]; i.Late = n[1];
            if (!string.IsNullOrEmpty(ct.TagIndex)) i.Tags = Json.Strings(cl.Data(ct)["tags"]?.ToJsonString() ?? "[]");
        }
    }
    public const int LATE_HOURS = 24;
    public static bool Late(string status, DateTime start, JsonObject snap) =>
        status == "cancelled" && snap.Str("cancel_reason") is "client" or "unconfirmed" && snap["undo"] is JsonObject u && Times.ParseAware(u.Str("at"), out _) > start.AddHours(-LATE_HOURS);

    public AgendaItem Item(Booking r, Dictionary<Guid, int> color)
    {
        var bk = new Bookings(c);
        var data = Permissions.Shown(v.Perms, bk.Data(r));
        var service = c.Row<Service>(r.ServiceId);
        var ra = c.Cal.ToLocal(r.Start); var rb = c.Cal.ToLocal(r.End);
        var name = $"{data.Str("first_name")} {data.Str("last_name")}".Trim();
        return new AgendaItem
        {
            Id = r.Id, Resource = r.ResourceId, ServiceId = r.ServiceId, ContactId = r.ContactId, Name = name == "" ? "Cliente" : name,
            Service = (r.Snap.Str("service") is var sv && sv != "" ? sv : service.Name) + (r.Visit != null ? " · visita" : ""),
            From = $"{ra:HH:mm}", To = $"{rb:HH:mm}", Start = r.Start, End = r.End, Status = r.Status, Color = r.ResourceId.HasValue ? color.GetValueOrDefault(r.ResourceId.Value) : 0,
            Who = r.Snap.Str("resource"), Party = r.Party, Alert = (data["allergies"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new(),
            Movable = Bookings.MOVABLE.Contains(r.Status) && service.Kind == "resource" && ra.Date == rb.Date && r.End > c.Now,
            Minutes = (int)(rb - ra).TotalMinutes, Snap = service.Interval is >= 5 and <= 60 ? service.Interval : STEP, Visit = r.Visit != null,
            Forced = r.Snap.ContainsKey("forced"), Origin = r.Origin, Notes = data.Str("notes"), Price = r.Snap.Str("total"),
        };
    }

    /// <summary>The days a booking shows on between first and last: a stay on every day from arrival to departure.</summary>
    public List<(DateOnly, AgendaItem)> DaysOf(Booking r, Dictionary<Guid, int> color, DateOnly first, DateOnly last)
    {
        var x = Item(r, color); var a = c.Cal.DayOf(r.Start); var b = c.Cal.DayOf(r.End);
        if (c.Row<Service>(r.ServiceId).Kind != "day" || a == b) return first <= a && a <= last ? new() { (a, x) } : new();
        var outp = new List<(DateOnly, AgendaItem)>();
        for (var d = a > first ? a : first; d <= (b < last ? b : last); d = d.AddDays(1))
        {
            var phase = d == a ? "arrivo" : d == b ? "partenza" : "in casa";
            outp.Add((d, new AgendaItem
            {
                Id = x.Id, Resource = x.Resource, ServiceId = x.ServiceId, ContactId = x.ContactId, Name = x.Name, Service = x.Service, Who = x.Who, Start = x.Start, End = x.End,
                From = x.From, To = x.To, Status = x.Status, Color = x.Color, Party = x.Party, Alert = x.Alert, Movable = x.Movable, Minutes = x.Minutes, Snap = x.Snap, Visit = x.Visit,
                Forced = x.Forced, Origin = x.Origin, Notes = x.Notes, Price = x.Price, Phase = phase,
                PhaseLabel = phase == "arrivo" ? "Arrivo " + x.From : phase == "partenza" ? "Partenza " + x.To : "In casa",
            }));
        }
        return outp;
    }

    public AgendaDay Day(DateOnly day, Guid? location = null, Guid? op = null, bool cancelled = false)
    {
        var today = c.Today; var (one, many, _) = Presets.UnitsOf(c.Tenant.BusinessType);
        var outp = new AgendaDay { Day = day, Today = today, One = one, Many = many, Appointments = Fits() };
        var (places, loc) = Place(true, location);
        outp.Places = places; outp.Location = loc?.Id;
        var everyone = loc != null ? Operators(loc) : new();
        var color = Colors(everyone);
        var chosen = everyone.FirstOrDefault(o => o.Id == op);
        var ops = chosen != null ? new List<Resource> { chosen } : everyone;
        var start = c.Cal.Local(day, TimeSpan.Zero)!.Value; var end = c.Cal.Local(day.AddDays(1), TimeSpan.Zero)!.Value;
        var statuses = cancelled ? SHOWN.Append("cancelled").ToArray() : SHOWN;
        var rows = loc == null ? new List<Booking>() : Visible("b.location_id=@l AND b.start<@e AND b.\"end\">@s" + (chosen != null ? " AND b.resource_id=@r" : ""), new { l = loc.Id, s = start, e = end, r = chosen?.Id }, statuses);
        var mine = ServicesOf(ops);
        var spans = ops.ToDictionary(o => o.Id, o => OpenSpans(day, o, mine[o.Id]));
        bool Works(Resource o) => spans[o.Id].Any(s => s.B > start && s.A < end);
        var blocks = ops.ToDictionary(o => o.Id, o => c.Cal.BlockSpans(day, day, o.Id));
        var off = ops.Where(o => !Works(o) && !rows.Any(r => r.ResourceId == o.Id) && blocks[o.Id].Count == 0).ToList();
        if (off.Count < ops.Count) ops = ops.Except(off).ToList(); else off.Clear();
        outp.Operators = ops.Select(o => new AgendaOperator { Id = o.Id, Name = o.Name, Color = color[o.Id], Open = spans[o.Id], Works = Works(o), PhotoUrl = o.PhotoUrl ?? "" }).ToList();
        outp.Off = off.Select(o => o.Name).ToList();
        outp.Items = rows.Select(r => Item(r, color)).ToList();
        Mark(outp.Items);
        var cl = new Clients(c);
        outp.Blocks = ops.SelectMany(o => blocks[o.Id].Select(b => new AgendaBlock
        {
            Id = b.Block.Id, Resource = o.Id, Start = b.A, End = b.B, Reason = b.Block.Reason, Label = Bookings.ReasonLabel(b.Block.Reason), Weekly = b.Block.Weekly,
            Note = string.IsNullOrEmpty(b.Block.Pii) ? "" : c.DecryptText(b.Block.Pii, "block:" + b.Block.Id),
        })).ToList();
        // the frame of the day: the working time of anyone and any booking or block outside it, in whole hours
        int Min(DateTime t) => (int)(c.Cal.ToLocal(t) - day.ToDateTime(TimeOnly.MinValue)).TotalMinutes;
        var edges = spans.Values.SelectMany(s => s).Select(s => (Min(s.A), Min(s.B))).Concat(rows.Select(r => (Min(r.Start), Min(r.End))))
            .Concat(outp.Blocks.Select(b => (Min(b.Start), Min(b.End)))).Where(x => x.Item2 > 0 && x.Item1 < 1440).Select(x => (Math.Max(0, x.Item1), Math.Min(1440, x.Item2))).ToList();
        outp.FirstMinute = edges.Count > 0 ? edges.Min(x => x.Item1) / 60 * 60 : 540;
        var last = edges.Count > 0 ? edges.Max(x => x.Item2) : 1140;
        outp.LastMinute = Math.Min(1440, (last + 59) / 60 * 60);
        outp.ClosedDay = ops.Count > 0 && !ops.Any(Works);
        outp.Writer = v.Can("booking.write") && day >= today;
        outp.Total = rows.Count(r => r.Status != "cancelled");
        return outp;
    }

    public sealed class WeekData
    {
        public DateOnly Monday { get; set; } public DateOnly Today { get; set; } public string Title { get; set; } = "";
        public List<AgendaLocation> Places { get; set; } = new(); public Guid? Location { get; set; }
        public List<AgendaOperator> Operators { get; set; } = new();
        public List<List<AgendaItem>> Days { get; set; } = new();
        public List<List<Span>> Open { get; set; } = new();
        public bool Grid { get; set; } public bool Writer { get; set; } public int FirstMinute { get; set; } public int LastMinute { get; set; } public int Total { get; set; }
    }

    public WeekData Week(DateOnly day, Guid? location = null, Guid? op = null, bool cancelled = false)
    {
        var today = c.Today; var monday = day.AddDays(-Calendar.Weekday(day)); var sunday = monday.AddDays(6);
        var appointments = Fits();
        var (places, loc) = Place(appointments, location);
        var ops = loc != null && appointments ? Operators(loc) : new();
        var color = Colors(ops); var chosen = ops.FirstOrDefault(o => o.Id == op);
        var start = c.Cal.Local(monday, TimeSpan.Zero)!.Value; var end = c.Cal.Local(monday.AddDays(7), TimeSpan.Zero)!.Value;
        var statuses = cancelled ? SHOWN.Append("cancelled").ToArray() : SHOWN;
        var rows = Visible("b.start<@e AND b.\"end\">@s" + (loc != null ? " AND b.location_id=@l" : "") + (chosen != null ? " AND b.resource_id=@r" : ""), new { s = start, e = end, l = loc?.Id, r = chosen?.Id }, statuses);
        var w = new WeekData { Monday = monday, Today = today, Places = places, Location = loc?.Id, Writer = v.Can("booking.write"), Grid = appointments && ops.Count > 0 };
        w.Title = monday.Month == sunday.Month ? $"{monday.Day}–{sunday.Day} {MONTHS[sunday.Month - 1]} {sunday.Year}" : $"{monday.Day} {MONTHS[monday.Month - 1]} – {sunday.Day} {MONTHS[sunday.Month - 1]} {sunday.Year}";
        for (int i = 0; i < 7; i++) w.Days.Add(new());
        var items = new List<AgendaItem>();
        foreach (var r in rows) foreach (var (d, x) in DaysOf(r, color, monday, sunday)) { w.Days[d.DayNumber - monday.DayNumber].Add(x); items.Add(x); }
        Mark(items);
        w.Operators = ops.Select(o => new AgendaOperator { Id = o.Id, Name = o.Name, Color = color[o.Id], PhotoUrl = o.PhotoUrl ?? "" }).ToList();
        w.Total = rows.Count(r => r.Status != "cancelled");
        var shown = chosen != null ? new List<Resource> { chosen } : ops; var mine = ServicesOf(shown);
        var mins = new List<(int, int)>();
        for (int i = 0; i < 7; i++)
        {
            var d = monday.AddDays(i);
            var all = Calendar.Merge(shown.SelectMany(o => OpenSpans(d, o, mine[o.Id])));
            w.Open.Add(all);
            int Min(DateTime t) => (int)(c.Cal.ToLocal(t) - d.ToDateTime(TimeOnly.MinValue)).TotalMinutes;
            mins.AddRange(all.Select(s => (Min(s.A), Min(s.B))));
            mins.AddRange(w.Days[i].Select(x => (Min(x.Start), Min(x.End))));
        }
        mins = mins.Where(x => x.Item2 > 0 && x.Item1 < 1440).Select(x => (Math.Max(0, x.Item1), Math.Min(1440, x.Item2))).ToList();
        w.FirstMinute = mins.Count > 0 ? mins.Min(x => x.Item1) / 60 * 60 : 540;
        w.LastMinute = mins.Count > 0 ? Math.Min(1440, (mins.Max(x => x.Item2) + 59) / 60 * 60) : 1140;
        return w;
    }

    public sealed class MonthCell { public DateOnly Day { get; set; } public bool Inside { get; set; } public bool IsToday { get; set; } public bool Past { get; set; } public List<AgendaItem> Items { get; set; } = new(); public int Count { get; set; } public int People { get; set; } }
    public sealed class MonthData { public string Title { get; set; } = ""; public DateOnly First { get; set; } public List<List<MonthCell>> Weeks { get; set; } = new(); public int Total { get; set; } public List<AgendaLocation> Places { get; set; } = new(); public Guid? Location { get; set; } public List<AgendaOperator> Operators { get; set; } = new(); }

    public MonthData Month(DateOnly day, Guid? location = null, Guid? op = null)
    {
        var today = c.Today; var first = new DateOnly(day.Year, day.Month, 1); var last = first.AddMonths(1).AddDays(-1);
        var ga = first.AddDays(-Calendar.Weekday(first)); var gb = last.AddDays(6 - Calendar.Weekday(last));
        var appointments = Fits(); var (places, loc) = Place(appointments, location);
        var ops = loc != null && appointments ? Operators(loc) : new(); var color = Colors(ops); var chosen = ops.FirstOrDefault(o => o.Id == op);
        var start = c.Cal.Local(ga, TimeSpan.Zero)!.Value; var end = c.Cal.Local(gb.AddDays(1), TimeSpan.Zero)!.Value;
        var rows = loc == null ? new() : Visible("b.start<@e AND b.\"end\">@s AND b.location_id=@l" + (chosen != null ? " AND b.resource_id=@r" : ""), new { s = start, e = end, l = loc.Id, r = chosen?.Id }, SHOWN);
        var byDay = new Dictionary<DateOnly, List<AgendaItem>>();
        foreach (var r in rows) foreach (var (d, x) in DaysOf(r, color, ga, gb)) { if (!byDay.TryGetValue(d, out var l)) byDay[d] = l = new(); l.Add(x); }
        var m = new MonthData { Title = char.ToUpper(MONTHS[first.Month - 1][0]) + MONTHS[first.Month - 1][1..] + " " + first.Year, First = first, Places = places, Location = loc?.Id,
            Operators = ops.Select(o => new AgendaOperator { Id = o.Id, Name = o.Name, Color = color[o.Id] }).ToList() };
        var order = new Dictionary<string, int> { ["partenza"] = 0, ["in casa"] = 1, ["arrivo"] = 2, [""] = 3 };
        for (var d = ga; d <= gb;)
        {
            var week = new List<MonthCell>();
            for (int i = 0; i < 7; i++, d = d.AddDays(1))
            {
                var items = byDay.GetValueOrDefault(d)?.OrderBy(x => order[x.Phase]).ThenBy(x => x.Start).ToList() ?? new();
                week.Add(new MonthCell { Day = d, Inside = d.Month == first.Month, IsToday = d == today, Past = d < today, Items = items, Count = items.Count, People = items.Sum(x => x.Party) });
            }
            m.Weeks.Add(week);
        }
        m.Total = m.Weeks.SelectMany(w => w).Where(x => x.Inside).SelectMany(x => x.Items).Select(x => x.Id).Distinct().Count();
        return m;
    }
}
