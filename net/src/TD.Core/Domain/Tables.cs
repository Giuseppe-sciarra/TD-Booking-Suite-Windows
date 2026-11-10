using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/tables.py: the rooms and tables of a restaurant (the booking side; the floor plan is in the app).</summary>
public sealed class Tables(Ctx c, Bookings b)
{
    public static readonly string[] KINDS = { "capacity", "shift" };

    public Area PickArea(Service service, JsonNode value)
    {
        if (!Json.Truthy(value)) return null;
        var id = Bookings.AsId(value);
        var area = id.HasValue ? c.S.One<Area>("SELECT * FROM core_area WHERE id=@id AND location_id=@l", new { id, l = service.LocationId }) : null;
        return area ?? throw new BookingException("Sala non valida: ricarica la pagina.");
    }

    public HashSet<string> Taken(Location location, DateTime start, DateTime end, Guid? exclude = null) =>
        c.S.Query<string>("SELECT tables::text FROM core_booking WHERE location_id=@l AND status = ANY(@h) AND busy_start<@e AND busy_end>@s AND id IS DISTINCT FROM @x",
            new { l = location.Id, h = Bookings.HOLDING, s = start, e = end, x = exclude }).SelectMany(Json.Strings).ToHashSet();
    HashSet<string> Taken(Location l, DateTime s, DateTime e, IEnumerable<Guid> ex) => Taken(l, s, e, ex?.FirstOrDefault() is Guid g && g != Guid.Empty ? g : null);

    public List<Table> Fit(Location location, int party, DateTime start, DateTime end, Area area = null, IEnumerable<Guid> exclude = null)
    {
        var rows = c.S.Query<Table>("SELECT t.* FROM core_table t JOIN core_area a ON a.id=t.area_id WHERE a.location_id=@l" + (area != null ? " AND t.area_id=@a" : ""),
            new { l = location.Id, a = area?.Id }).ToList();
        var busy = Taken(location, start, end, exclude);
        var free = rows.Where(t => !busy.Contains(t.Id.ToString())).ToDictionary(t => t.Id.ToString());
        var one = free.Values.Where(t => t.MinCovers <= party && party <= t.MaxCovers).OrderBy(t => t.MaxCovers).ThenBy(t => t.Priority).ThenBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
        if (one != null) return new() { one };
        var pairs = new HashSet<(string, string)>();
        foreach (var (k, t) in free)
            foreach (var j in Json.Strings(t.Joins))
                if (free.ContainsKey(j) && j != k) pairs.Add(string.CompareOrdinal(k, j) < 0 ? (k, j) : (j, k));
        var best = pairs.Select(p => new List<Table> { free[p.Item1], free[p.Item2] }).Where(p => party <= p.Sum(t => t.MaxCovers))
            .OrderBy(p => p.Sum(t => t.MaxCovers)).ThenBy(p => p.Sum(t => t.Priority)).FirstOrDefault();
        return best ?? new();
    }

    public string AreaOver(Area area, DateTime start, DateTime end, int party, IEnumerable<Guid> exclude = null)
    {
        if (area == null || area.Capacity == 0) return "";
        var rows = b.Rows("area_id=@a AND busy_start<@e AND busy_end>@s AND NOT id = ANY(@x)", new { a = area.Id, s = start, e = end, x = exclude?.ToArray() ?? Array.Empty<Guid>() });
        return Bookings.Over(Bookings.Spans(rows, start, end), party, area.Capacity) ? $"oltre i coperti di {area.Name} ({area.Capacity})" : "";
    }

    public bool NeedsTable(Service service, bool desk)
    {
        var place = b.Location(service);
        return !desk && KINDS.Contains(service.Kind) && place.Tables != "off" && place.TablesOnline;
    }

    /// <summary>The room and the tables of a booking: the staff's choice, otherwise the best fit where suggested.</summary>
    public (Area, List<string>) Seat(Service service, int party, DateTime bs, DateTime be, Area area = null, JsonNode chosen = null, Guid? exclude = null, bool online = false)
    {
        var place = b.Location(service);
        if (place.Tables == "off" || !KINDS.Contains(service.Kind)) return (area, new());
        List<Table> tables;
        if (Json.Truthy(chosen))
        {
            if (chosen is not JsonArray arr || arr.Count > 8) throw new BookingException("Tavolo non valido: ricarica la pagina.");
            var ids = arr.Select(x => Bookings.AsId(x)).ToList();
            if (ids.Any(x => x == null)) throw new BookingException("Tavolo non valido: ricarica la pagina.");
            tables = c.S.Query<Table>("SELECT t.* FROM core_table t JOIN core_area a ON a.id=t.area_id WHERE a.location_id=@l AND t.id = ANY(@ids)",
                new { l = place.Id, ids = ids.Select(x => x.Value).ToArray() }).ToList();
            if (tables.Count == 0 || tables.Count != ids.Distinct().Count()) throw new BookingException("Tavolo non valido: ricarica la pagina.");
            if (tables.Select(t => t.Id.ToString()).Intersect(Taken(place, bs, be, exclude)).Any()) throw new BookingException("Il tavolo è già occupato a quell'ora.");
        }
        else tables = place.Tables == "suggest" || online && place.TablesOnline ? Fit(place, party, bs, be, area, exclude.HasValue ? new[] { exclude.Value } : null) : new();
        if (online && place.TablesOnline && tables.Count == 0) throw new BookingException("Nessun tavolo libero per quest'ora: scegline un'altra.");
        if (online && place.Tables == "manual") tables = new();
        var a = tables.Count > 0 ? c.Row<Area>(tables[0].AreaId) : area;
        return (a, tables.Select(t => t.Id.ToString()).ToList());
    }

    /// <summary>The tables of a booking as the staff call them: T1 + T2.</summary>
    public string Names(Booking booking)
    {
        var ids = booking.TableIds;
        if (ids.Count == 0) return "";
        var found = c.S.Query<Table>("SELECT * FROM core_table WHERE id::text = ANY(@ids)", new { ids = ids.ToArray() }).ToDictionary(t => t.Id.ToString(), t => t.Name);
        return string.Join(" + ", ids.Where(found.ContainsKey).Select(k => found[k]));
    }
}
