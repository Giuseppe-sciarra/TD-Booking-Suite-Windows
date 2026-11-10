using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>Blocca orario (agenda.block_view): a pause, training or meeting of an operator, once or every week.</summary>
public static class Blocchi
{
    static readonly string[] REASONS = { "break", "training", "meeting", "other" };

    static List<Resource> Blockable(Call c)
    {
        var rows = c.C.S.Query<Resource>("SELECT r.* FROM core_resource r JOIN core_location l ON l.id=r.location_id WHERE r.active AND l.active ORDER BY r.secondary, r.name").ToList();
        var m = c.Member; if (m == null) return rows;
        var res = Permissions.Ids(m.Resources); var locs = Permissions.Ids(m.Locations);
        if (res.Count > 0) return rows.Where(r => res.Contains(r.Id.ToString())).ToList();
        if (locs.Count > 0 || m.Role == "operator") return rows.Where(r => locs.Contains(r.LocationId.ToString())).ToList();
        return rows;
    }

    [Rpc("blocco.dati", Perm = "booking.write")]
    public static object Form(Call c)
    {
        var allowed = Blockable(c);
        Timeblock b = c.Id("id") is Guid id ? c.C.S.Get<Timeblock>(id) : null;
        if (b != null && !allowed.Any(r => r.Id == b.ResourceId)) throw new KeyNotFoundException();
        return new
        {
            operatori = allowed.Select(r => new { id = r.Id, nome = r.Name }), etichetta = Presets.UnitsOf(c.Tenant.BusinessType).One,
            motivi = REASONS.Select(r => new { id = r, nome = Bookings.ReasonLabel(r) }),
            blocco = b == null ? null : new
            {
                id = b.Id, operatore = b.ResourceId, giorno = DateOnly.FromDateTime(b.Day), inizio = b.Start.ToString(@"hh\:mm"), fine = b.End.ToString(@"hh\:mm"),
                motivo = b.Reason, settimanale = b.Weekly, fino = b.Until.HasValue ? DateOnly.FromDateTime(b.Until.Value) : (DateOnly?)null,
                nota = string.IsNullOrEmpty(b.Pii) ? "" : c.C.DecryptText(b.Pii, "block:" + b.Id),
            },
        };
    }

    [Rpc("blocco.salva", Perm = "booking.write")]
    public static object Save(Call c)
    {
        var allowed = Blockable(c);
        var x = c.C; x.Lock();
        var b = c.Id("id") is Guid id ? x.S.Get<Timeblock>(id) ?? throw new KeyNotFoundException() : new Timeblock { TenantId = x.Tenant.Id, Created = x.Now };
        var res = allowed.FirstOrDefault(r => r.Id == c.Id("operatore")) ?? throw new BookingException("Scegli chi blocchi.");
        if (b.ResourceId.HasValue && !allowed.Any(r => r.Id == b.ResourceId)) throw new KeyNotFoundException();
        if (!TimeSpan.TryParse(c.Str("inizio"), out var start) || !TimeSpan.TryParse(c.Str("fine"), out var end)) throw new BookingException("Scrivi inizio e fine, per esempio 13:00 e 14:00.");
        if (start == end) throw new BookingException("Inizio e fine devono essere differenti.");
        var reason = c.Str("motivo", "break"); if (!REASONS.Contains(reason)) throw new BookingException("Motivo non valido.");
        var day = c.Day("giorno"); var weekly = c.Bool("settimanale");
        DateOnly? until = weekly && DateOnly.TryParseExact(c.Str("fino"), "yyyy-MM-dd", out var u) ? u : null;
        if (until.HasValue && until < day) throw new BookingException("Fino al viene prima del giorno del blocco.");
        var note = string.Join(" ", c.Str("nota").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); if (note.Length > 200) note = note[..200];
        var isNew = !c.Id("id").HasValue;
        b.ResourceId = res.Id; b.Day = day.ToDateTime(TimeOnly.MinValue); b.Start = start; b.End = end; b.Reason = reason; b.Weekly = weekly; b.Until = until?.ToDateTime(TimeOnly.MinValue);
        b.Pii = note != "" ? x.Encrypt(note, "block:" + b.Id) : "";
        if (isNew) x.S.Insert(b); else x.S.Update(b);
        x.CalendarChanged();
        var start0 = x.Cal.Local(day, TimeSpan.Zero)!.Value;
        var hit = x.S.Query<Booking>("SELECT * FROM core_booking WHERE resource_id=@r AND status = ANY(@o) AND \"end\">@n AND start>=@s", new { r = res.Id, o = Bookings.OPEN, n = x.Now, s = start0 })
            .Where(bk => x.Cal.Blocked(bk.BusyStart, bk.BusyEnd, res.Id) != null).Select(bk => bk.Id.ToString()[..8]).ToList();
        if (hit.Count > 0 && !c.Bool("conferma") && !c.Bool("force_ok"))
            throw new ForceWarning($"In quell'orario ci sono già {hit.Count} prenotazioni ({string.Join(", ", hit.Take(5))}). Restano valide: chiamale per spostarle. Bloccare lo stesso?");
        x.Audit("agenda.block", b.Id, new { kind = reason, weekly, conflicts = hit.Count });
        if (!isNew) new Waitlist(x).Freed();
        return new { ok = true, id = b.Id, daRicontattare = hit.Count };
    }

    [Rpc("blocco.togli", Perm = "booking.write")]
    public static object Remove(Call c)
    {
        var allowed = Blockable(c);
        var b = c.C.S.Get<Timeblock>(c.NeedId("id")) ?? throw new KeyNotFoundException();
        if (!allowed.Any(r => r.Id == b.ResourceId)) throw new KeyNotFoundException();
        c.C.Lock();
        c.C.S.Delete<Timeblock>(b.Id); c.C.Audit("agenda.unblock", b.Id);
        new Waitlist(c.C).Freed();
        return new { ok = true };
    }
}
