using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/profile.py: the business profile, type of activity and locations, applied in one go: the catalog is
/// rebuilt for the type (services, the operators or rooms it needs, opening hours, the first dates of the events).
/// Bookings are never touched: anything they point to is switched off instead of deleted.</summary>
public sealed class Profile(Ctx c)
{
    public const int MAX_LOCATIONS = 50;
    static readonly string[] DEFAULT_NAMES = { "Sede principale", "Seconda sede", "Terza sede", "Quarta sede", "Quinta sede" };
    /// <summary>Name proposed for the location in position i (0-based): Sede 6, Sede 7... after the fifth.</summary>
    public static string DefaultName(int i) => i < DEFAULT_NAMES.Length ? DEFAULT_NAMES[i] : $"Sede {i + 1}";

    sealed record Spec((string Name, (string, object)[] Changes)[] Services, int Units = 0, (int[] Days, string Start, string End)[] Hours = null, bool Day = false, (int[] Weekdays, string Start, string End, int Weeks)? Events = null);
    static readonly int[] WEEK = { 0, 1, 2, 3, 4, 5, 6 }, TUE_SUN = { 1, 2, 3, 4, 5, 6 }, MON_FRI = { 0, 1, 2, 3, 4 }, MON_SAT = { 0, 1, 2, 3, 4, 5 }, TUE_SAT = { 1, 2, 3, 4, 5 };
    static (string, (string, object)[]) S(string name, params (string, object)[] changes) => (name, changes);
    static (string, object) Dur(int m) => ("duration", m);
    // services (name, changes to the preset), units for appointments and stays, opening hours, event dates
    static readonly Dictionary<string, Spec> PROFILES = new()
    {
        ["ristorante"] = new(new[] { S("Tavolo", ("capacity", 40)) }, Hours: new[] { (TUE_SUN, "12:00", "15:00"), (TUE_SUN, "19:00", "23:30") }),
        ["parrucchiere"] = new(new[] { S("Taglio", Dur(30)), S("Piega", Dur(45)), S("Colore", Dur(90)) }, 2, new[] { (TUE_SAT, "09:00", "19:00") }),
        ["medico"] = new(new[] { S("Prima visita", Dur(60)), S("Visita di controllo", Dur(30)) }, 2, new[] { (MON_FRI, "09:00", "13:00"), (MON_FRI, "15:00", "19:00") }),
        ["consulenza"] = new(new[] { S("Consulenza", Dur(60)), S("Prima chiamata conoscitiva", Dur(30)) }, 2, new[] { (MON_FRI, "09:00", "18:00") }),
        ["centro_estetico"] = new(new[] { S("Manicure", Dur(45)), S("Pulizia del viso", Dur(60)), S("Ceretta", Dur(30)) }, 2, new[] { (TUE_SAT, "09:30", "19:30") }),
        ["spa"] = new(new[] { S("Massaggio 50 minuti", Dur(50)), S("Trattamento viso", Dur(60)) }, 2, new[] { (WEEK, "10:00", "20:00") }),
        ["tattoo"] = new(new[] { S("Consulenza per il tatuaggio", Dur(30)), S("Seduta", Dur(180)) }, 2, new[] { (TUE_SAT, "11:00", "19:00") }),
        ["autoscuola"] = new(new[] { S("Lezione di guida", Dur(60)) }, 2, new[] { (MON_SAT, "08:00", "19:00") }),
        ["campo"] = new(new[] { S("Campo per un'ora", Dur(60)), S("Campo per un'ora e mezza", Dur(90)) }, 2, new[] { (WEEK, "08:00", "23:00") }),
        ["corso"] = new(new[] { S("Lezione") }, Hours: new[] { (MON_FRI, "09:00", "10:00"), (MON_FRI, "18:00", "21:00"), (new[] { 5 }, "10:00", "12:00") }),
        ["officina"] = new(new[] { S("Tagliando", Dur(60)), S("Cambio gomme", Dur(30)) }, 2, new[] { (MON_FRI, "08:00", "12:30"), (MON_FRI, "14:00", "18:00") }),
        ["hotel"] = new(new[] { S("Camera") }, 3, Day: true),
        ["casa_vacanze"] = new(new[] { S("Appartamento") }, 2, Day: true),
        ["noleggio"] = new(new[] { S("Noleggio bici") }, 4, Day: true),
        ["evento"] = new(new[] { S("Serata") }, Events: (new[] { 5 }, "21:00", "23:30", 8)),
        ["tour"] = new(new[] { S("Visita guidata") }, Events: (new[] { 5, 6 }, "10:00", "12:00", 6)),
    };
    static TimeSpan Hhmm(string v) => TimeSpan.Parse(v);

    // ---------- deleting as Django does: what something else needs (PROTECT) stays; what only exists for it goes along
    static readonly Dictionary<string, (string[] Protect, string[] Cascade, string[] Null)> DEPENDS = new()
    {
        ["core_location"] = (new[] { "core_resource.location_id", "core_service.location_id", "core_schedule.location_id", "core_exceptionday.location_id", "core_booking.location_id", "core_sale.location_id", "core_parked.location_id", "core_standby.location_id" }, new[] { "core_area.location_id" }, Array.Empty<string>()),
        ["core_area"] = (Array.Empty<string>(), new[] { "core_table.area_id" }, new[] { "core_booking.area_id" }),
        ["core_resource"] = (new[] { "core_serviceresource.resource_id", "core_schedule.resource_id", "core_exceptionday.resource_id", "core_booking.resource_id" }, new[] { "core_resourcephoto.resource_id", "core_timeblock.resource_id" }, new[] { "core_waitentry.resource_id" }),
        ["core_service"] = (new[] { "core_option.service_id", "core_schedule.service_id", "core_exceptionday.service_id", "core_occurrence.service_id", "core_shiftrule.service_id", "core_booking.service_id" }, new[] { "core_serviceresource.service_id", "core_timeblock.service_id", "core_waitentry.service_id" }, Array.Empty<string>()),
        ["core_option"] = (new[] { "core_booking.option_id" }, Array.Empty<string>(), Array.Empty<string>()),
        ["core_shiftrule"] = (new[] { "core_occurrence.rule_id" }, Array.Empty<string>(), Array.Empty<string>()),
        ["core_occurrence"] = (new[] { "core_booking.occurrence_id" }, Array.Empty<string>(), Array.Empty<string>()),
    };
    static (string Table, string Column) Ref(string r) { var p = r.Split('.'); return (p[0], p[1]); }
    /// <summary>False when something that must stay points to the row (Django's ProtectedError): nothing is deleted.</summary>
    public bool Delete(string table, Guid id)
    {
        var d = DEPENDS.GetValueOrDefault(table);
        foreach (var r in d.Protect ?? Array.Empty<string>()) { var (t, col) = Ref(r); if (c.S.Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {t} WHERE {col}=@id)", new { id })) return false; }
        foreach (var r in d.Cascade ?? Array.Empty<string>())
        {
            var (t, col) = Ref(r);
            if (DEPENDS.ContainsKey(t)) foreach (var child in c.S.Query<Guid>($"SELECT id FROM {t} WHERE {col}=@id", new { id }).ToList()) Delete(t, child);
            else c.S.Execute($"DELETE FROM {t} WHERE {col}=@id", new { id });
        }
        foreach (var r in d.Null ?? Array.Empty<string>()) { var (t, col) = Ref(r); c.S.Execute($"UPDATE {t} SET {col}=NULL WHERE {col}=@id", new { id }); }
        c.S.Execute($"DELETE FROM {table} WHERE id=@id", new { id });
        return true;
    }
    static readonly HashSet<string> HAS_ACTIVE = new() { "core_location", "core_resource", "core_service", "core_option", "core_shiftrule", "core_occurrence", "core_product" };
    /// <summary>retire(): delete what no booking points to; switch off the rest. Returns how many were kept switched off.</summary>
    public int Retire(string table, string where = "true", object args = null)
    {
        var kept = 0;
        foreach (var id in c.S.Query<Guid>($"SELECT id FROM {table} WHERE {where}", args).ToList())
            if (!Delete(table, id)) { if (HAS_ACTIVE.Contains(table)) c.S.Execute($"UPDATE {table} SET active=false WHERE id=@id AND active", new { id }); kept++; }
        return kept;
    }
    static readonly Dictionary<string, string> TABLES = new()
    {
        ["locations"] = "core_location", ["schedules"] = "core_schedule", ["exceptions"] = "core_exceptionday", ["services"] = "core_service", ["resources"] = "core_resource", ["options"] = "core_option",
        ["turns"] = "core_shiftrule", ["occurrences"] = "core_occurrence", ["products"] = "core_product",
    };
    public static string TableOf(string kind) => TABLES[kind];
    /// <summary>remove(): Elimina from the catalog lists. What bookings point to is switched off instead, with the things
    /// that only exist for it: a location takes its services and resources along. Returns how many were kept off.</summary>
    public int Remove(string kind, Guid id)
    {
        var kept = 0; var a = new { id };
        if (kind == "services")
        {
            c.S.Execute("DELETE FROM core_schedule WHERE service_id=@id", a); c.S.Execute("DELETE FROM core_exceptionday WHERE service_id=@id", a);
            foreach (var rule in c.S.Query<Guid>("SELECT id FROM core_shiftrule WHERE service_id=@id", a).ToList()) kept += Retire("core_occurrence", "rule_id=@r", new { r = rule });
            kept += Retire("core_occurrence", "service_id=@id", a) + Retire("core_shiftrule", "service_id=@id", a) + Retire("core_option", "service_id=@id", a);
            c.S.Execute("DELETE FROM core_serviceresource WHERE service_id=@id", a);
        }
        else if (kind == "resources")
        {
            c.S.Execute("DELETE FROM core_schedule WHERE resource_id=@id", a); c.S.Execute("DELETE FROM core_exceptionday WHERE resource_id=@id", a); c.S.Execute("DELETE FROM core_serviceresource WHERE resource_id=@id", a);
        }
        else if (kind == "locations")
        {
            foreach (var s in c.S.Query<Guid>("SELECT id FROM core_service WHERE location_id=@id", a).ToList()) kept += Remove("services", s);
            foreach (var r in c.S.Query<Guid>("SELECT id FROM core_resource WHERE location_id=@id", a).ToList()) kept += Remove("resources", r);
            c.S.Execute("DELETE FROM core_schedule WHERE location_id=@id", a); c.S.Execute("DELETE FROM core_exceptionday WHERE location_id=@id", a);
        }
        else if (kind == "turns") kept += Retire("core_occurrence", "rule_id=@id", a);
        return kept + Retire(TABLES[kind], "id=@id", a);
    }

    // ---------- the profile
    public sealed record Described(string Type, string Label, List<Location> Locations, int Services);
    public Described Describe() => new(c.Tenant.BusinessType ?? "", Presets.All.TryGetValue(c.Tenant.BusinessType ?? "", out var p) ? p.Label : "Personalizzato",
        c.S.Query<Location>("SELECT * FROM core_location WHERE active ORDER BY name").ToList(), (int)c.S.Scalar<long>("SELECT count(*) FROM core_service WHERE active"));

    public static List<string> CleanNames(IList<string> names, int count)
    {
        count = Math.Clamp(count, 1, MAX_LOCATIONS); var outp = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var n = (i < names.Count ? names[i] ?? "" : "").Trim(); if (n.Length > 100) n = n[..100];
            outp.Add(n == "" ? DefaultName(i) : n);
        }
        if (outp.Select(Crypto.CaseFold).Distinct().Count() < outp.Count) throw new BookingException("Dai un nome diverso a ogni sede.");
        return outp;
    }

    /// <summary>The locations of the form: the existing ones kept and renamed in order, new ones created, the extra ones go
    /// (switched off when bookings use them).</summary>
    (List<Location> All, List<Location> New, int Archived) PlaceLocations(List<string> names)
    {
        var current = c.S.Query<Location>("SELECT * FROM core_location ORDER BY active DESC, name").ToList(); var all = new List<Location>(); var fresh = new List<Location>();
        for (int i = 0; i < names.Count; i++)
        {
            Location loc;
            if (i < current.Count) { loc = current[i]; loc.Name = names[i]; loc.Active = true; c.S.Update(loc, "name", "active"); }
            else { loc = new Location { TenantId = c.Tenant.Id, Name = names[i] }; c.S.Insert(loc); fresh.Add(loc); }
            all.Add(loc);
        }
        var archived = 0;
        foreach (var id in c.S.Query<Guid>("SELECT id FROM core_location WHERE NOT (id = ANY(@k))", new { k = all.Select(l => l.Id).ToArray() }).ToList()) archived += Remove("locations", id);
        return (all, fresh, archived);
    }

    static void Set(Service s, string key, object v)
    {
        switch (key)
        {
            case "duration": s.Duration = (int)v; break; case "interval": s.Interval = (int)v; break; case "capacity": s.Capacity = (int)v; break; case "max_party": s.MaxParty = (int)v; break;
            case "request_above": s.RequestAbove = (int)v; break; case "turnover": s.Turnover = (bool)v; break; case "last_arrival": s.LastArrival = (int)v; break; case "client_picks": s.ClientPicks = (bool)v; break;
            case "min_days": s.MinDays = (int)v; break; case "max_days": s.MaxDays = (int)v; break; case "checkin": s.Checkin = (TimeSpan)v; break; case "checkout": s.Checkout = (TimeSpan)v; break;
            default: throw new ArgumentException(key);
        }
    }
    /// <summary>The type's starting catalog in these locations: services, the operators or rooms they need, event dates.</summary>
    (int Services, int Resources, int Dates) BuildCatalog(string preset, List<Location> locations)
    {
        var p = Presets.All[preset]; var spec = PROFILES[preset]; var label = p.ResourceLabel != "" ? p.ResourceLabel : "Risorsa"; int services = 0, resources = 0, dates = 0;
        foreach (var loc in locations)
        {
            var units = new List<Resource>();
            for (int n = 1; n <= spec.Units; n++)
            {
                var r = new Resource { TenantId = c.Tenant.Id, LocationId = loc.Id, Name = $"{label} {n}", Capacity = spec.Day && p.Defaults.TryGetValue("max_party", out var mp) ? (int)mp : 0 };
                c.S.Insert(r); units.Add(r);
            }
            resources += units.Count;
            foreach (var (name, changes) in spec.Services)
            {
                // appointments are booked online at least an hour ahead, now that the desk has its own rules
                var s = new Service { TenantId = c.Tenant.Id, LocationId = loc.Id, Name = name, Preset = preset, Kind = p.Kind, ResourceLabel = p.ResourceLabel, Fields = Json.Dump(Presets.Questions(preset)), LeadMinutes = p.Kind == "resource" ? 60 : 0 };
                foreach (var (k, v) in p.Defaults) Set(s, k, v);
                foreach (var (k, v) in changes) Set(s, k, v);
                c.S.Insert(s); services++;
                foreach (var u in units) c.S.Insert(new Serviceresource { TenantId = c.Tenant.Id, ServiceId = s.Id, ResourceId = u.Id });
                if (spec.Events is var (weekdays, start, end, weeks))
                    for (int d = 1; d <= weeks * 7; d++)
                    {
                        var day = c.Today.AddDays(d);
                        if (!weekdays.Contains(Calendar.Weekday(day))) continue;
                        c.S.Insert(new Occurrence { TenantId = c.Tenant.Id, ServiceId = s.Id, Start = c.Cal.Local(day, Hhmm(start)).Value, End = c.Cal.Local(day, Hhmm(end)).Value, Capacity = s.Capacity }); dates++;
                    }
            }
        }
        return (services, resources, dates);
    }

    public sealed record Result(int Services, int Resources, int Dates, int Locations, int Archived, string Label, string Type, bool Kept);

    /// <summary>A new type: the catalog is rebuilt for it. Bookings are never touched, what they use is switched off.</summary>
    public Result Apply(string preset, List<string> names)
    {
        if (!Presets.All.TryGetValue(preset ?? "", out var p)) throw new BookingException("Scegli un tipo di attività.");
        var spec = PROFILES[preset]; var t = c.Tenant;
        // 1. the old catalog: hours and anything tied to services and resources
        c.S.Execute("DELETE FROM core_schedule"); c.S.Execute("DELETE FROM core_exceptionday WHERE service_id IS NOT NULL OR resource_id IS NOT NULL");
        var archived = Retire("core_occurrence") + Retire("core_shiftrule") + Retire("core_option");
        c.S.Execute("DELETE FROM core_serviceresource");
        archived += Retire("core_service") + Retire("core_resource");
        // 2. locations, 3. the new catalog, the same in every location
        var (locations, _, gone) = PlaceLocations(names); archived += gone;
        var made = BuildCatalog(preset, locations);
        // 4. opening hours for the whole business; stays and events are open every day
        foreach (var (days, start, end) in spec.Hours ?? new[] { (WEEK, "00:00", (string)null) })
            foreach (var d in days)
                c.S.Insert(new Schedule { TenantId = t.Id, Weekday = (short)d, Start = Hhmm(start), End = end != null ? Hhmm(end) : new TimeSpan(0, 23, 59, 59, 999).Add(TimeSpan.FromTicks(9990)) });
        // presets.first_settings: restaurants ask about allergies from the first day; a type without food loses them
        t.BusinessType = preset;
        if (preset == "ristorante") { if (Json.Arr(t.Allergens).Count == 0) t.Allergens = Json.Dump(new JsonArray(Presets.Allergens.Select(a => (JsonNode)a).ToArray())); t.AllergensOn = true; }
        else if (!Presets.ServesFood(preset)) { t.Allergens = "[]"; t.AllergensOn = false; }
        c.S.Execute("UPDATE core_tenant SET business_type=@b, allergens=@a::jsonb, allergens_on=@o WHERE id=@t", new { b = t.BusinessType, a = t.Allergens, o = t.AllergensOn, t = t.Id });
        c.Audit("tenant.profile", t.Id, new { type = preset, locations = locations.Count, archived });
        return new(made.Services, made.Resources, made.Dates, locations.Count, archived, p.Label, preset, false);
    }

    /// <summary>The same type again: services, operators and hours stay as the business made them. Only the locations follow
    /// the form; a new location starts with the type's catalog, so it can be booked at once.</summary>
    public Result Keep(List<string> names)
    {
        var preset = c.Tenant.BusinessType ?? "";
        var (locations, fresh, archived) = PlaceLocations(names);
        var made = PROFILES.ContainsKey(preset) ? BuildCatalog(preset, fresh) : (0, 0, 0);
        c.Audit("tenant.locations", c.Tenant.Id, new { type = preset, locations = locations.Count, @new = fresh.Count, archived });
        return new(made.Item1, made.Item2, made.Item3, locations.Count, archived, Presets.All.TryGetValue(preset, out var p) ? p.Label : "Personalizzato", preset, true);
    }
}
