using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/waitlist.py, the worker's turn: places freed, offered to the waiting list; kept places that expire.</summary>
public sealed partial class Waitlist
{
    static string Key(Slot x) => x.Start + "|" + x.Resource + "|" + x.Occurrence;
    static bool Inside(Waitentry e, TimeSpan t) => (e.TimeFrom == null || t >= e.TimeFrom) && (e.TimeTo == null || t < e.TimeTo);

    /// <summary>free(): the free times of the first day of the entry with something it was not told yet, by the rules of
    /// the online page, inside its time window; for a stay, the stay itself.</summary>
    public List<Slot> Free(Waitentry e, ICollection<string> seen)
    {
        var s = c.Row<Service>(e.ServiceId); var bk = new Bookings(c);
        if (!(s.Active && c.Row<Location>(s.LocationId).Active)) return new();
        var resource = e.ResourceId?.ToString() ?? ""; DateOnly first = DateOnly.FromDateTime(e.First), last = DateOnly.FromDateTime(e.Last);
        try
        {
            if (s.Kind == "day")
            {
                var w = bk.Bounds(s, new JsonObject { ["start"] = bk.Iso(c.Cal.Local(first, s.Checkin)!.Value), ["end_date"] = last.ToString("yyyy-MM-dd"), ["party"] = e.Party, ["resource"] = resource });
                var x = new Slot { Start = bk.Iso(w.Start), End = bk.Iso(w.End), Resource = w.Resource?.Id.ToString() ?? "", Occurrence = "", StartUtc = w.Start, EndUtc = w.End };
                return seen.Contains(Key(x)) ? new() : new() { x };
            }
            for (var d = first > c.Today ? first : c.Today; d <= last; d = d.AddDays(1))
            {
                var slots = bk.Available(s.Id.ToString(), d, null, resource != "" ? (JsonNode)resource : null, e.Party).Where(x => !seen.Contains(Key(x)) && Inside(e, c.Cal.ToLocal(x.StartUtc).TimeOfDay)).ToList();
                if (slots.Count > 0) return slots;
            }
        }
        catch (BookingException) { }
        return new();
    }

    /// <summary>link(): the booking page on the day of the offer, signed for this entry.</summary>
    public string Link(Waitentry e, Slot slot)
    {
        var token = DjangoSigning.Dumps("waitlist", $"{{\"e\":\"{e.Id}\",\"d\":\"{slot.Start[..10]}\"}}", c.Now); var lang = Json.Obj(e.Snapshot).Str("lang");
        return $"{c.PublicOrigin}/book/{c.Tenant.Slug}/?attesa={token}" + (lang != "" ? "&lang=" + lang : "");
    }

    /// <summary>client_mails.offer: Si è liberato un posto, to a client of the list who left an email and did not say no
    /// to the emails about his bookings: the free times of the day, the link of the booking page and who takes the place.</summary>
    Delivery Offer(Waitentry e, List<Slot> slots, string link, DateTime? until)
    {
        var conf = (JsonObject)ClientMails.Current(c.Tenant)["waitlist"]; var d = Data(e); var contact = c.S.Get<Contact>(e.ContactId);
        if (WpSync.BySite(c) || !Json.Truthy(conf["on"]) || d.Str("email") == "" || contact == null || !contact.Notices) return null;
        var lang = Json.Obj(e.Snapshot).Str("lang") is var l && l != "" ? l : "it"; var s = c.Row<Service>(e.ServiceId);
        var first = c.Cal.ToLocal(slots[0].StartUtc);
        var when = s.Kind == "day" ? Texts.When(first, c.Cal.ToLocal(slots[0].EndUtc), true, lang)
            : Tr.T(lang, "{giorno} {data} alle {ora}").Replace("{giorno}", Tr.T(lang, Texts.WEEKDAYS[Calendar.Weekday(DateOnly.FromDateTime(first))])).Replace("{data}", $"{first:dd/MM/yyyy}")
                .Replace("{ora}", string.Join(", ", slots.Take(6).Select(x => c.Cal.ToLocal(x.StartUtc).ToString("HH:mm")))) + (slots.Count > 6 ? Tr.T(lang, " e altri orari") : "");
        var v = new Dictionary<string, string>
        {
            ["nome"] = d.Str("first_name"), ["servizio"] = s.Name, ["quando"] = when, ["data"] = $"{first:dd/MM/yyyy}", ["ora"] = $"{first:HH:mm}", ["sede"] = c.Row<Location>(s.LocationId).Name,
            ["operatore"] = e.ResourceId is Guid r ? c.Row<Resource>(r).Name : "", ["attivita"] = c.Tenant.Name, ["istruzioni"] = "", ["link"] = link,
        };
        var kept = until.HasValue ? c.Cal.ToLocal(until.Value) : default;
        var body = Texts.Fill(Tr.T(lang, conf.Str("body")), v) + "\n" + (until.HasValue
            ? Tr.T(lang, "Il posto è tenuto per te fino alle {ora} di {giorno}, poi passa a chi viene dopo in lista.").Replace("{ora}", $"{kept:HH:mm}").Replace("{giorno}", Tr.T(lang, Texts.WEEKDAYS[Calendar.Weekday(DateOnly.FromDateTime(kept))]))
            : Tr.T(lang, "Lo abbiamo scritto a tutti quelli in lista: lo prende chi prenota per primo. Se non c'è più, sulla pagina vedi cosa resta libero."));
        return new Bookings(c).Queue(new JsonObject
        {
            ["to"] = d.Str("email"), ["subject"] = string.Join(" ", Texts.Fill(Tr.T(lang, conf.Str("subject")), v).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)), ["body"] = body,
            ["html"] = Texts.EmailHtml(c.Tenant, body, new() { (Tr.T(lang, "Prenota"), link) }, lang),
        }, e.ContactId);
    }

    /// <summary>step(): a kept place not taken in time goes back, and so to the next in line; then each entry that is due
    /// is told of the free times it was not told yet (the first in line keeps the place, with «Uno alla volta»).</summary>
    public int Step()
    {
        var now = c.Now; var queue = c.Tenant.WaitMode == "queue"; var sent = 0; var bk = new Bookings(c);
        var expired = c.S.Query<Waitentry>("SELECT * FROM core_waitentry WHERE held_until<=@n", new { n = now }).ToList();
        foreach (var e in expired) { var snap = Json.Obj(e.Snapshot); snap.Remove("hold"); e.HeldUntil = null; e.Snapshot = Json.Dump(snap); c.S.Update(e, "held_until", "snapshot"); }
        if (expired.Count > 0) c.S.Execute("UPDATE core_waitentry SET due=true WHERE status='waiting'");
        foreach (var e in c.S.Query<Waitentry>("SELECT * FROM core_waitentry WHERE status='waiting' AND due AND last>=@d::date ORDER BY created", new { d = c.Today.ToString("yyyy-MM-dd") }).ToList())
        {
            e.Due = false; var snap = Json.Obj(e.Snapshot);
            if (!(queue && e.HeldUntil != null))
            {
                var seen = Json.Strings(snap["seen"]?.ToJsonString() ?? "[]"); var slots = Free(e, seen.ToHashSet());
                if (queue && slots.Count > 0)
                {
                    slots = slots.Take(1).ToList(); var x = slots[0]; var s = c.Row<Service>(e.ServiceId);
                    e.HeldUntil = now.AddMinutes(c.Tenant.WaitMinutes);
                    snap["hold"] = new JsonObject { ["service"] = s.Id.ToString(), ["resource"] = x.Resource, ["occurrence"] = x.Occurrence, ["party"] = e.Party, ["busy_start"] = bk.Iso(x.StartUtc.AddMinutes(-s.BufferBefore)), ["busy_end"] = bk.Iso(x.EndUtc.AddMinutes(s.BufferAfter)) };
                }
                if (slots.Count > 0)
                {
                    // once per free time: the newest 300 are remembered, the entry lives a month at most
                    var all = seen.Concat(slots.Select(Key)).ToList(); snap["seen"] = new JsonArray(all.Skip(Math.Max(0, all.Count - 300)).Select(k => (JsonNode)k).ToArray());
                    snap["free"] = new JsonArray(slots.Take(6).Select(x => (JsonNode)x.Start).ToArray()); snap["told"] = Times.PyIso(now);
                    e.Snapshot = Json.Dump(snap);
                    if (Offer(e, slots, Link(e, slots[0]), e.HeldUntil) != null) sent++;
                }
            }
            e.Snapshot = Json.Dump(snap); c.S.Update(e, "due", "held_until", "snapshot");
        }
        c.S.Execute("UPDATE core_tenant SET wait_check=(SELECT min(held_until) FROM core_waitentry WHERE held_until IS NOT NULL) WHERE id=@t", new { t = c.Tenant.Id });
        return sent;
    }
}
