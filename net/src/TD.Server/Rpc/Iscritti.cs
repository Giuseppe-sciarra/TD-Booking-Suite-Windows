using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.attendees: Iscritti of a class or an event date: who is booked, how many, the participants' names, the
/// tick of who came (Presente: done, Assente: absent, back with Togli); the substitute instructor takes every booking of
/// the date in the agenda, the original one noted on it and given back when the substitute is taken off; the enrolled
/// told by email when the desk ticks it.</summary>
public static class Iscritti
{
    static List<Booking> Rows(Call c, Booking reference)
    {
        var (vis, args) = Permissions.VisibleBookings(c.Member, c.Perms, "b"); var p = new Dapper.DynamicParameters(args);
        p.Add("s", reference.ServiceId); p.Add("st", reference.Start); p.Add("o", reference.OccurrenceId);
        return c.C.S.Query<Booking>($"SELECT b.* FROM core_booking b WHERE b.service_id=@s AND b.start=@st AND b.occurrence_id IS NOT DISTINCT FROM @o AND b.status<>'cancelled' AND ({vis}) ORDER BY b.created", p).ToList();
    }

    [Rpc("prenotazione.iscritti", Perm = "booking.read")]
    public static object Page(Call c)
    {
        var x = c.C; var reference = Prenotazioni.Visible(c, c.NeedId("id")); var rows = Rows(c, reference); var bk = new Bookings(x); var service = x.Row<Service>(reference.ServiceId);
        var when = x.Cal.ToLocal(reference.Start).ToString("dddd dd MMMM yyyy, HH:mm", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
        var substitute = rows.Select(b => b.Snap.Str("substitute")).FirstOrDefault(v => v != "") ?? "";
        return new
        {
            id = reference.Id, titolo = reference.Snap.Str("service") != "" ? reference.Snap.Str("service") : service.Name, quando = when, persone = rows.Sum(b => b.Party), iniziata = reference.Start <= x.Now, evento = service.Kind == "event", sostituto = substitute,
            risorse = x.S.Query<Resource>("SELECT * FROM core_resource WHERE active AND NOT secondary AND location_id=@l ORDER BY name", new { l = reference.LocationId }).Select(r => new { id = r.Id, nome = r.Name }),
            righe = rows.Select(b =>
            {
                var d = Permissions.Shown(c.Perms, bk.Data(b));
                return new
                {
                    id = b.Id, nome = $"{d.Str("first_name")} {d.Str("last_name")}".Trim(), persone = b.Party, stato = b.Status,
                    partecipanti = Json.Truthy(d["masked"]) ? "" : string.Join(", ", (d["participants"] as JsonArray ?? new JsonArray()).Select(v => v?.ToString() ?? "")),
                };
            }),
        };
    }

    [Rpc("prenotazione.iscritti.azione", Perm = "booking.write")]
    public static object Action(Call c)
    {
        c.Require("booking.read");
        var x = c.C; var reference = Prenotazioni.Visible(c, c.NeedId("id")); var rows = Rows(c, reference); var action = c.Str("azione");
        if (action == "substitute")
        {
            var r = c.Id("resource") is Guid rid ? x.S.One<Resource>("SELECT * FROM core_resource WHERE active AND NOT secondary AND location_id=@l AND id=@id", new { l = reference.LocationId, id = rid }) : null; var notify = r != null && c.Bool("notify");
            foreach (var b in rows)
            {
                var snap = b.Snap;
                if (!snap.ContainsKey("original")) { snap["original"] = snap.Str("resource"); snap["original_id"] = b.ResourceId?.ToString() ?? ""; }
                if (r != null) { b.ResourceId = r.Id; snap["substitute"] = r.Name; snap["resource"] = r.Name; }
                else
                {
                    b.ResourceId = Guid.TryParse(snap.Str("original_id"), out var old) ? old : null; snap["resource"] = snap.Str("original");
                    foreach (var k in new[] { "substitute", "original", "original_id" }) snap.Remove(k);  // no substitute yet: nothing to take off, not a crash
                }
                b.Snapshot = Json.Dump(snap); x.S.Update(b, "resource_id", "snapshot");
                if (notify) ClientMails.Substitute(x, b);
            }
            x.Audit("booking.substitute", reference.Id, new { resource = r?.Id.ToString() ?? "", notified = notify });
        }
        else
        {
            var b = rows.FirstOrDefault(v => v.Id == c.Id("booking")) ?? throw new KeyNotFoundException();
            new Bookings(x).ChangeStatus(b, action == "present" ? "completed" : action == "absent" ? "no_show" : "confirmed");
        }
        return new { };
    }
}
