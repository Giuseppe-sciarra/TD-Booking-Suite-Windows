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
}
