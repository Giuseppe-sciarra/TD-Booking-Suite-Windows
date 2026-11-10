using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/waitlist.py: the waiting list.</summary>
public sealed partial class Waitlist(Ctx c)
{
    public const int MAX_DAYS = 31, LINK_DAYS = 35;
    public static readonly int[] WAIT_MINUTES = { 15, 60, 120 };
    public const string NOTICE = "Avvisami per email se si libera un posto per questa richiesta.";
    /// <summary>The entry whose own kept place does not count against it, on the page of its link.</summary>
    public static readonly AsyncLocal<Guid?> Own = new();

    /// <summary>A place given up between start and end: the waiting entries on those days are looked at again.</summary>
    public void Freed(DateTime? start = null, DateTime? end = null)
    {
        int n;
        if (start.HasValue)
            n = c.S.Execute("UPDATE core_waitentry SET due=true WHERE status='waiting' AND first<=@l AND last>=@f",
                new { l = c.Cal.DayOf(end!.Value.AddTicks(-10)).ToDateTime(TimeOnly.MinValue), f = c.Cal.DayOf(start.Value).ToDateTime(TimeOnly.MinValue) });
        else n = c.S.Execute("UPDATE core_waitentry SET due=true WHERE status='waiting'");
        if (n > 0) c.S.Execute("UPDATE core_tenant SET wait_check=@n WHERE id=@t", new { n = c.Now, t = c.Tenant.Id });
    }

    /// <summary>The places kept for the first in line over start-end, as the booking sweep counts them.</summary>
    public List<(DateTime, int)> Holds(Service service, DateTime start, DateTime end, Resource resource = null, Occurrence occurrence = null)
    {
        var outp = new List<(DateTime, int)>(); var own = Own.Value;
        foreach (var e in c.S.Query<Waitentry>("SELECT * FROM core_waitentry WHERE held_until>@n AND id IS DISTINCT FROM @own", new { n = c.Now, own }))
        {
            var h = Json.Obj(e.Snapshot)["hold"] as JsonObject;
            if (h == null) continue;
            var a = Times.ParseAware(h.Str("busy_start"), out _)!.Value; var b = Times.ParseAware(h.Str("busy_end"), out _)!.Value;
            if (!(a < end && start < b)) continue;
            bool hit; int n;
            if (resource != null) { hit = h.Str("resource") == resource.Id.ToString(); n = 1; }
            else if (occurrence != null) { hit = h.Str("occurrence") == occurrence.Id.ToString(); n = (int)h["party"]; }
            else { hit = h.Str("service") == service.Id.ToString() && h.Str("resource") == ""; n = (int)h["party"]; }
            if (hit) { outp.Add((a > start ? a : start, n)); outp.Add((b < end ? b : end, -n)); }
        }
        return outp;
    }

    /// <summary>The client of the entry books from its link: the place kept for him is his.</summary>
    public void Claim(Guid wait)
    {
        var e = c.S.One<Waitentry>("SELECT * FROM core_waitentry WHERE id=@w AND status='waiting' FOR UPDATE", new { w = wait });
        if (e?.HeldUntil == null) return;
        var snap = Json.Obj(e.Snapshot); snap.Remove("hold");
        e.HeldUntil = null; e.Snapshot = Json.Dump(snap);
        c.S.Update(e, "held_until", "snapshot");
    }

    /// <summary>A booking made: the entries of the same client for that service and day are done, and the one of the link.</summary>
    public void Booked(Booking booking, Guid? wait = null)
    {
        var day = c.Cal.DayOf(booking.Start).ToDateTime(TimeOnly.MinValue);
        c.S.Execute(@"UPDATE core_waitentry SET status='done', held_until=NULL, due=false WHERE status='waiting' AND
            ((@c::uuid IS NOT NULL AND contact_id=@c AND service_id=@s AND first<=@d AND last>=@d) OR id=@w)",
            new { c = booking.ContactId, s = booking.ServiceId, d = day, w = wait ?? Guid.Empty });
    }

    public JsonObject Data(Waitentry e) => c.Decrypt(e.Pii, "wait:" + e.Id);

    /// <summary>clean(): service, operator, days, time window and people of an entry, checked.</summary>
    Waitentry Clean(JsonObject data, bool desk)
    {
        var bk = new Bookings(c);
        var s = Bookings.AsId(data["service"]) is Guid sid ? c.S.One<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.id=@id AND s.active AND l.active", new { id = sid }) : null;
        if (s == null) throw new BookingException("Selezione non valida o non disponibile.");
        if (s.Kind is not ("resource" or "capacity" or "shift" or "event" or "day")) throw new BookingException("Servizio non valido.");
        var (_, resource, _) = bk.Details(s, null, data.Str("resource") != "" ? data["resource"] : null);
        var party = Bookings.Integer(data["party"] ?? 1, "Persone", 1, desk ? 1000 : s.MaxParty);
        if (!DateOnly.TryParseExact(data.Str("first"), "yyyy-MM-dd", out var first)) throw new BookingException("Data non valida.");
        var last = first; if (data.Str("last") != "" && !DateOnly.TryParseExact(data.Str("last"), "yyyy-MM-dd", out last)) throw new BookingException("Data non valida.");
        if (first < c.Today) throw new BookingException("Scegli un giorno da oggi in poi.");
        if (s.Kind == "day" && last <= first) throw new BookingException("Scegli la data di partenza.");
        if (last < first) throw new BookingException("Il giorno finale viene prima del primo.");
        if (last.DayNumber - first.DayNumber > MAX_DAYS) throw new BookingException($"Al massimo {MAX_DAYS} giorni.");
        TimeSpan? Time(string k)
        {
            if (data.Str(k) == "" || s.Kind == "day") return null;
            if (!TimeOnly.TryParse(data.Str(k), System.Globalization.CultureInfo.InvariantCulture, out var t)) throw new BookingException("Orario non valido.");
            return t.ToTimeSpan();
        }
        var from = Time("time_from"); var to = Time("time_to");
        if (from.HasValue && to.HasValue && from >= to) throw new BookingException("L'orario finale viene prima di quello iniziale.");
        return new Waitentry { TenantId = c.Tenant.Id, ServiceId = s.Id, ResourceId = resource?.Id, First = first.ToDateTime(TimeOnly.MinValue), Last = last.ToDateTime(TimeOnly.MinValue), TimeFrom = from, TimeTo = to, Party = party, Status = "waiting", Created = c.Now };
    }

    /// <summary>add(), from the desk: the card chosen, or found or made from the data typed. The same client already waiting
    /// for that service on those days keeps his entry. (The online entry comes with the public pages.)</summary>
    public (Waitentry Entry, bool Created) AddAtDesk(JsonObject data, Contact contact)
    {
        c.Lock();
        var e = Clean(data, true); var cl = new Clients(c); JsonObject pii;
        if (contact != null) { var d = cl.Data(contact); pii = new JsonObject { ["first_name"] = d.Str("first_name"), ["last_name"] = d.Str("last_name"), ["email"] = d.Str("email"), ["phone"] = d.Str("phone") }; }
        else { pii = Clients.Clean(data); contact = cl.FindOrCreate(pii).Item1; }
        var same = c.S.One<Waitentry>("SELECT * FROM core_waitentry WHERE contact_id=@c AND service_id=@s AND status='waiting' AND first<=@l AND last>=@f LIMIT 1", new { c = contact.Id, s = e.ServiceId, l = e.Last, f = e.First });
        if (same != null) return (same, false);
        e.ContactId = contact.Id;
        e.Snapshot = Json.Dump(new JsonObject { ["consent"] = new JsonObject { ["at"] = Times.PyIso(c.Now), ["channel"] = "desk", ["text_sha256"] = Privacy.TextHash(c.Tenant.PrivacyText + "\n" + NOTICE) }, ["actor"] = c.Actor?.ToString() ?? "" });
        e.Pii = c.Encrypt(pii, "wait:" + e.Id); c.S.Insert(e);
        c.Audit("waitlist.added", e.Id, new { channel = "desk" });
        return (e, true);
    }

    /// <summary>opened(): the entry and the day of an offer's link, or null (expired, done, or not of this business).</summary>
    public (Waitentry Entry, DateOnly Day)? Opened(string token)
    {
        var data = DjangoSigning.Loads("waitlist", token, LINK_DAYS * 86400L, c.Now);
        if (data == null || !DateOnly.TryParseExact(data.Str("d"), "yyyy-MM-dd", out var day) || !Guid.TryParse(data.Str("e"), out var id)) return null;
        var e = c.S.One<Waitentry>("SELECT * FROM core_waitentry WHERE id=@id AND status='waiting'", new { id });
        return e == null ? null : (e, day);
    }

    /// <summary>add(), online: the notice box ticked, recorded as for a booking; email required, phone optional. The card
    /// is found as an online booking finds it, never rewritten; a client the business does not book online is refused.</summary>
    public (Waitentry Entry, bool Created) AddOnline(JsonObject data)
    {
        c.Lock();
        var e = Clean(data, false); var cl = new Clients(c);
        if (data["consent"]?.GetValueKind() != System.Text.Json.JsonValueKind.True) throw new BookingException("Spunta la casella per ricevere l'avviso.");
        var typed = new JsonObject(); foreach (var k in new[] { "first_name", "last_name", "email", "phone" }) typed[k] = data[k]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? data.Str(k) : "";
        var pii = Clients.Clean(typed);
        if (pii.Str("email") == "") throw new BookingException("Scrivi la tua email: l'avviso arriva lì.");
        var idx = cl.EmailIndex(pii.Str("email")); var contact = cl.ByIndex(idx) ?? cl.ByPhone(pii.Str("phone"), withoutEmail: true);
        if (contact is { OnlineBlocked: true } || pii.Str("phone") != "" && c.S.Scalar<long>("SELECT count(*) FROM core_contact WHERE phone_index=@p AND online_blocked", new { p = cl.PhoneIndex(pii.Str("phone")) }) > 0)
            throw new Bookings(c).Refused(c.Row<Service>(e.ServiceId));
        if (contact == null)
        {
            contact = new Contact { TenantId = c.Tenant.Id, EmailIndex = idx, Created = c.Now };
            cl.SealCard(contact, new JsonObject { ["first_name"] = pii.Str("first_name"), ["last_name"] = pii.Str("last_name"), ["email"] = pii.Str("email"), ["phone"] = pii.Str("phone") });
            c.S.Insert(contact);
        }
        var same = c.S.One<Waitentry>("SELECT * FROM core_waitentry WHERE contact_id=@c AND service_id=@s AND status='waiting' AND first<=@l AND last>=@f LIMIT 1", new { c = contact.Id, s = e.ServiceId, l = e.Last, f = e.First });
        if (same != null) return (same, false);
        e.ContactId = contact.Id;
        var snap = new JsonObject { ["consent"] = new JsonObject { ["at"] = Times.PyIso(c.Now), ["channel"] = "public", ["text_sha256"] = Privacy.TextHash(c.Tenant.PrivacyText + "\n" + NOTICE) } };
        if (c.Language != "it") snap["lang"] = c.Language;
        e.Snapshot = Json.Dump(snap); e.Pii = c.Encrypt(pii, "wait:" + e.Id); c.S.Insert(e);
        c.Audit("waitlist.added", e.Id, new { channel = "public" });
        return (e, true);
    }

    /// <summary>close(): done (the client booked or does not want it any more) or removed: a place kept for him goes to the next.</summary>
    public void Close(Waitentry e, bool remove)
    {
        c.Lock();
        var held = e.HeldUntil.HasValue;
        if (remove) c.S.Delete<Waitentry>(e.Id); else c.S.Execute("UPDATE core_waitentry SET status='done', held_until=NULL, due=false WHERE id=@id", new { id = e.Id });
        if (held) Freed();
        c.Audit(remove ? "waitlist.removed" : "waitlist.done", e.Id);
    }
}
