using System.Globalization;
using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>core/tables.py, the pages: Sale e tavoli: the floor plan of a room at a day and time, tables dragged into
/// place, bookings and walk-ins seated with a click; the forms of a room and of a table.</summary>
public static class Tavoli
{
    static readonly string[] OPEN = { "pending", "confirmed", "arrived", "in_progress" };

    [Rpc("tavoli", Perm = "booking.read")]
    public static object Floor(Call c)
    {
        var x = c.C; var now = x.Cal.ToLocal(x.Now);
        var areas = x.S.Query<Area>("SELECT a.* FROM core_area a JOIN core_location l ON l.id=a.location_id ORDER BY l.name, a.name").ToList();
        var mine = c.Member != null ? Permissions.Ids(c.Member.Locations) : new();
        if (mine.Count > 0) areas = areas.Where(a => mine.Contains(a.LocationId.ToString())).ToList();
        var area = areas.FirstOrDefault(a => a.Id.ToString() == c.Str("sala")) ?? areas.FirstOrDefault();
        var day = c.Day("data", DateOnly.FromDateTime(now));
        var clock = TimeOnly.TryParseExact(c.Str("ora"), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(now.Hour, now.Minute);
        var tables = new List<object>(); var loose = new List<object>(); var walkin = new List<object>(); Location location = null;
        if (area != null)
        {
            location = x.Row<Location>(area.LocationId); var at = x.Cal.Local(day, clock.ToTimeSpan()) ?? x.Now; var bk = new Bookings(x);
            var (vis, args) = Permissions.VisibleBookings(c.Member, c.Perms, "b"); var p = new Dapper.DynamicParameters(args); p.Add("l", area.LocationId); p.Add("at", at); p.Add("st", OPEN);
            var byTable = new Dictionary<string, (object Who, Booking B)>();
            foreach (var r in x.S.Query<Booking>($"SELECT b.* FROM core_booking b WHERE b.location_id=@l AND b.status = ANY(@st) AND b.busy_start<=@at AND b.busy_end>@at AND ({vis}) ORDER BY b.start", p))
            {
                var d = Permissions.Shown(c.Perms, bk.Data(r)); var name = (d.Str("first_name") + " " + d.Str("last_name")).Trim();
                var who = new { prenotazione = r.Id, nome = name != "" ? name : "Cliente", da = x.Cal.ToLocal(r.Start).ToString("HH:mm"), persone = r.Party };
                foreach (var k in r.TableIds) byTable.TryAdd(k, (who, r));
                if (r.TableIds.Count == 0 && (r.AreaId == null || r.AreaId == area.Id)) loose.Add(who);
            }
            foreach (var tb in x.S.Query<Table>("SELECT * FROM core_table WHERE area_id=@a ORDER BY name", new { a = area.Id }))
            {
                var has = byTable.TryGetValue(tb.Id.ToString(), out var w);
                tables.Add(new { id = tb.Id, nome = tb.Name, min = tb.MinCovers, max = tb.MaxCovers, x = Math.Round(tb.X, 1), y = Math.Round(tb.Y, 1), stato = !has ? "free" : w.B.Status is "arrived" or "in_progress" ? "seated" : "booked", chi = has ? w.Who : null });
            }
            walkin = x.S.Query<Service>("SELECT * FROM core_service WHERE active AND kind='capacity' AND location_id=@l ORDER BY name", new { l = area.LocationId }).Select(s => (object)new { id = s.Id, nome = s.Name }).ToList();
        }
        return new
        {
            nome = c.Tenant.Name, sale = areas.Select(a => new { id = a.Id, nome = a.Name, sede = areas.Count > 1 && area != null && a.LocationId != area.LocationId ? x.Row<Location>(a.LocationId).Name : "" }),
            sala = area?.Id, salaNome = area?.Name, coperti = area?.Capacity ?? 0, sede = location?.Name, spenti = location?.Tables == "off", giorno = day.ToString("yyyy-MM-dd"), ora = clock.ToString("HH:mm"),
            tavoli = tables, senza = loose, modifica = c.Can("catalog.write"), siede = c.Can("booking.write"), servizi = walkin,
        };
    }

    static List<Fld> Fields(Call c, string what, Guid? id, Posted p = null)
    {
        var x = c.C;
        if (what == "sala")
        {
            var a = id.HasValue ? x.S.Get<Area>(id) : null;
            return new()
            {
                new() { Nome = "location", Etichetta = "Sede", Tipo = "select", Valore = a?.LocationId.ToString() ?? "", Scelte = x.S.Query<Location>("SELECT * FROM core_location WHERE active ORDER BY name").Select(l => (object)new { id = l.Id.ToString(), nome = l.Name }) },
                new() { Nome = "name", Etichetta = "Nome della sala", Valore = a?.Name ?? "", Max = 60, Aiuto = "Per esempio Sala, Dehors, Veranda: il cliente può chiederla quando prenota" },
                new() { Nome = "capacity", Etichetta = "Coperti della sala", Tipo = "number", Valore = a?.Capacity ?? 0, Aiuto = "Le persone sedute insieme al massimo in questa sala. 0 vuol dire nessun limite oltre ai posti del servizio" },
            };
        }
        var t = id.HasValue ? x.S.Get<Table>(id) : null; var areas = x.S.Query<Area>("SELECT a.* FROM core_area a JOIN core_location l ON l.id=a.location_id ORDER BY l.name, a.name").ToList();
        return new()
        {
            new() { Nome = "area", Etichetta = "Sala", Tipo = "select", Valore = t?.AreaId.ToString() ?? c.Str("sala"), Scelte = new object[] { new { id = "", nome = "---------" } }.Concat(areas.Select(a => (object)new { id = a.Id.ToString(), nome = a.Name })) },
            new() { Nome = "name", Etichetta = "Nome del tavolo", Valore = t?.Name ?? "", Max = 30, Aiuto = "Per esempio 1, 12 o Finestra" },
            new() { Nome = "min_covers", Etichetta = "Persone minime", Tipo = "number", Valore = t?.MinCovers ?? 1 }, new() { Nome = "max_covers", Etichetta = "Persone massime", Tipo = "number", Valore = t?.MaxCovers ?? 4 },
            new() { Nome = "priority", Etichetta = "Priorità", Tipo = "number", Valore = t?.Priority ?? 0, Aiuto = "Tra due tavoli adatti si propone prima quello col numero più basso" },
            new()
            {
                Nome = "joins", Etichetta = "Si unisce a", Tipo = "multi", Valore = t != null ? Json.Strings(t.Joins) : new List<string>(), Aiuto = "I tavoli che si avvicinano a questo per un gruppo più grande",
                Scelte = x.S.Query<(Guid Id, string Name, string Area)>("SELECT t.id, t.name, a.name FROM core_table t JOIN core_area a ON a.id=t.area_id ORDER BY a.name, t.name").Where(o => o.Id != id).Select(o => (object)new { id = o.Id.ToString(), nome = $"{o.Name} ({o.Area})" }),
            },
        };
    }

    [Rpc("tavoli.modulo", Perm = "catalog.write")]
    public static object Form(Call c)
    {
        var what = c.Str("cosa"); if (what is not ("sala" or "tavolo")) throw new KeyNotFoundException(); var id = c.Id("id"); var x = c.C;
        var name = !id.HasValue ? null : what == "sala" ? x.S.Get<Area>(id)?.Name : x.S.Get<Table>(id)?.Name; if (id.HasValue && name == null) throw new KeyNotFoundException();
        return new
        {
            nome = c.Tenant.Name, campi = Fields(c, what, id), titolo = !id.HasValue ? (what == "sala" ? "Nuova sala" : "Nuovo tavolo") : $"{(what == "sala" ? "Sala" : "Tavolo")} {name}",
            descrizione = what == "sala" ? "Il cliente sceglie al massimo la sala, mai il tavolo" : "I coperti che il tavolo accoglie e con quali altri si unisce. Sulla piantina lo spostate trascinandolo",
            elimina = !id.HasValue ? "" : what == "sala" ? "Togli la sala e i suoi tavoli" : "Togli il tavolo",
        };
    }

    [Rpc("tavoli.salva", Perm = "catalog.write")]
    public static object Save(Call c)
    {
        var what = c.Str("cosa"); if (what is not ("sala" or "tavolo")) throw new KeyNotFoundException(); var id = c.Id("id"); var x = c.C; var p = new Posted(c, c.Obj("valori"));
        if (what == "sala")
        {
            var a = id.HasValue ? x.S.Get<Area>(id) ?? throw new KeyNotFoundException() : new Area { TenantId = c.Tenant.Id };
            var loc = Guid.TryParse(p.Raw("location"), out var lg) ? x.S.One<Location>("SELECT * FROM core_location WHERE id=@i AND active", new { i = lg }) : null;
            if (loc == null) p.Error("location", p.Raw("location") == "" ? Posted.REQUIRED : "Scegli un'opzione valida. La scelta effettuata non compare tra quelle disponibili.");
            a.Name = p.Text("name", true, 60); var cap = p.Int("capacity", true); if (cap > 10000) p.Error("capacity", "Valore tra 0 e 10000.");
            if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
            a.LocationId = loc.Id; a.Capacity = cap ?? 0;
            if (id.HasValue) x.S.Update(a); else x.S.Insert(a);
            x.Audit("tables.sala_saved", a.Id);
            return new { testo = "Salvato.", sala = a.Id };
        }
        var t = id.HasValue ? x.S.Get<Table>(id) ?? throw new KeyNotFoundException() : new Table { TenantId = c.Tenant.Id };
        var area = Guid.TryParse(p.Raw("area"), out var ag) ? x.S.Get<Area>(ag) : null;
        if (area == null) p.Error("area", p.Raw("area") == "" ? Posted.REQUIRED : "Scegli un'opzione valida. La scelta effettuata non compare tra quelle disponibili.");
        t.Name = p.Text("name", true, 30); int? lo = p.Int("min_covers", true, 0, 32767), hi = p.Int("max_covers", true, 0, 32767), prio = p.Int("priority", true, 0, 32767);
        if (!(1 <= (lo ?? 0) && (lo ?? 0) <= (hi ?? 0) && (hi ?? 0) <= 100)) p.Whole("Persone minime e massime tra 1 e 100, le minime non oltre le massime.");
        if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
        var wanted = (c.Obj("valori")["joins"] as JsonArray)?.Select(j => j?.ToString() ?? "").ToArray() ?? Array.Empty<string>();
        // only the tables of the same location join
        var joins = x.S.Query<Guid>("SELECT t.id FROM core_table t JOIN core_area a ON a.id=t.area_id WHERE t.id::text = ANY(@w) AND a.location_id=@l", new { w = wanted, l = area.LocationId }).Select(g => g.ToString()).ToList();
        t.AreaId = area.Id; t.MinCovers = (short)lo; t.MaxCovers = (short)hi; t.Priority = (short)prio; t.Joins = Json.Dump(new JsonArray(joins.Select(j => (JsonNode)j).ToArray()));
        if (id.HasValue) x.S.Update(t); else x.S.Insert(t);
        var me = t.Id.ToString();
        // joining goes both ways
        foreach (var o in x.S.Query<Table>("SELECT * FROM core_table WHERE id<>@i", new { i = t.Id }))
        {
            var theirs = Json.Strings(o.Joins); var want = joins.Contains(o.Id.ToString());
            if (want != theirs.Contains(me)) { theirs = theirs.Where(j => j != me).Concat(want ? new[] { me } : Array.Empty<string>()).ToList(); o.Joins = Json.Dump(new JsonArray(theirs.Select(j => (JsonNode)j).ToArray())); x.S.Update(o, "joins"); }
        }
        x.Audit("tables.tavolo_saved", t.Id);
        return new { testo = "Salvato.", sala = t.AreaId };
    }

    [Rpc("tavoli.elimina", Perm = "catalog.write")]
    public static object Remove(Call c)
    {
        var what = c.Str("cosa"); if (what is not ("sala" or "tavolo")) throw new KeyNotFoundException(); var id = c.NeedId("id"); var x = c.C;
        var ids = (what == "tavolo" ? x.S.Query<Guid>("SELECT id FROM core_table WHERE id=@i", new { i = id }) : x.S.Query<Guid>("SELECT id FROM core_table WHERE area_id=@i", new { i = id })).Select(g => g.ToString()).ToList();
        if (what == "tavolo" ? ids.Count == 0 : x.S.Get<Area>(id) == null) throw new KeyNotFoundException();
        foreach (var o in x.S.Query<Table>("SELECT * FROM core_table WHERE NOT (id::text = ANY(@i))", new { i = ids.ToArray() }))
        {
            var theirs = Json.Strings(o.Joins); if (!theirs.Any(ids.Contains)) continue;
            o.Joins = Json.Dump(new JsonArray(theirs.Where(j => !ids.Contains(j)).Select(j => (JsonNode)j).ToArray())); x.S.Update(o, "joins");
        }
        // the bookings stay, without that room (SET_NULL); the tables go with their room (CASCADE)
        if (what == "sala") { x.S.Execute("UPDATE core_booking SET area_id=NULL WHERE area_id=@i", new { i = id }); x.S.Execute("DELETE FROM core_table WHERE area_id=@i", new { i = id }); x.S.Execute("DELETE FROM core_area WHERE id=@i", new { i = id }); }
        else x.S.Execute("DELETE FROM core_table WHERE id=@i", new { i = id });
        x.Audit($"tables.{what}_removed", id);
        return new { testo = "Tolto. Le prenotazioni restano, senza quel tavolo." };
    }

    /// <summary>place: a table dragged on the floor plan: where it stands, in percent of the plan.</summary>
    [Rpc("tavoli.sposta", Perm = "catalog.write")]
    public static object Place(Call c)
    {
        var x = c.C; var id = c.NeedId("table");
        if (!double.TryParse(c.Str("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var px) || !double.TryParse(c.Str("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var py) || double.IsNaN(px) || double.IsNaN(py)) throw new BookingException("Posizione non valida.");
        if (x.S.Execute("UPDATE core_table SET x=@x, y=@y WHERE id=@i", new { x = Math.Clamp(px, 0, 95), y = Math.Clamp(py, 0, 95), i = id }) == 0) throw new KeyNotFoundException();
        return new { ok = true };
    }

    /// <summary>assign: a booking given a table on the floor plan (or none); the table must be free for its whole time.</summary>
    [Rpc("tavoli.assegna", Perm = "booking.write")]
    public static object Assign(Call c)
    {
        var x = c.C; x.Lock(); var b = Prenotazioni.Visible(c, c.NeedId("booking"));
        if (!OPEN.Contains(b.Status)) throw new BookingException("Selezione non valida o non disponibile.");  // seated ones too
        var chosen = (c["tables"] as JsonArray)?.Select(t => t?.ToString() ?? "").Where(t => t != "").ToList() ?? new();
        if (chosen.Count > 0)
        {
            var (area, ids) = new Tables(x, new Bookings(x)).Seat(x.Row<Service>(b.ServiceId), b.Party, b.BusyStart, b.BusyEnd, chosen: new JsonArray(chosen.Select(t => (JsonNode)t).ToArray()), exclude: b.Id);
            if (ids == null || ids.Count == 0) throw new BookingException("La sede non gestisce i tavoli: si accende in Sedi.");
            b.AreaId = area?.Id; b.TableIds = ids;
        }
        else b.TableIds = new List<string>();
        x.S.Update(b, "area_id", "tables"); x.Audit("booking.tables", b.Id, new { tables = b.TableIds });
        return new { ok = true };
    }

    /// <summary>seat_walkin: a client walking in, seated now on a table of the floor plan.</summary>
    [Rpc("tavoli.entra", Perm = "booking.write")]
    public static object Walkin(Call c)
    {
        var x = c.C; var bk = new Bookings(x); var s = bk.ActiveService(c["service"]);
        if (Permissions.CheckNewBooking(c.Member, s.LocationId, null) is string no) throw new UnauthorizedAccessException(no);
        bk.Create(new JsonObject { ["service"] = c.Str("service"), ["party"] = c.Str("party") != "" ? c.Str("party") : "1", ["walkin"] = true, ["tables"] = new JsonArray(c.Str("table")), ["request_key"] = Bookings.TokenUrlSafe(24) });
        return new { testo = "Cliente seduto al tavolo." };
    }
}
