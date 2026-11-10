using System.Text.Json.Nodes;
using Dapper;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.waitlist_view: Lista d'attesa: who waits for a place, from the booking page or added here, with what
/// freed for each. Names and contacts as the role may see them, the entries of its scope.</summary>
public static class Attesa
{
    static List<Waitentry> Rows(Call c)
    {
        var x = c.C; var m = c.Member; var where = "w.status='waiting' AND w.last>=@t"; var p = new DynamicParameters(); p.Add("t", x.Today.ToDateTime(TimeOnly.MinValue));
        if (m != null)
        {
            var locs = Permissions.Ids(m.Locations); var res = Permissions.Ids(m.Resources);
            if (locs.Count > 0 || m.Role == "operator" && res.Count == 0) { where += " AND s.location_id = ANY(@locs)"; p.Add("locs", locs.Select(Guid.Parse).ToArray()); }
            if (res.Count > 0) { where += " AND (w.resource_id = ANY(@res) OR w.resource_id IS NULL)"; p.Add("res", res.Select(Guid.Parse).ToArray()); }
        }
        return x.S.Query<Waitentry>($"SELECT w.* FROM core_waitentry w JOIN core_service s ON s.id=w.service_id WHERE {where} ORDER BY w.first, w.created", p).ToList();
    }

    [Rpc("attesa", Perm = "booking.read")]
    public static object Page(Call c)
    {
        var x = c.C; var wl = new Waitlist(x); var bk = new Bookings(x); var now = x.Now;
        Contact client = c.Id("cliente") is Guid cid && c.Can("pii.read") ? x.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = cid }) : null;
        var cd = client != null ? new Clients(x).Data(client) : null;
        return new
        {
            coda = c.Tenant.WaitMode == "queue", oggi = x.Today, piuSedi = x.S.Scalar<long>("SELECT count(*) FROM core_location WHERE active") > 1,
            righe = Rows(c).Select(e =>
            {
                var d = Permissions.Shown(c.Perms, wl.Data(e)); var s = x.Row<Service>(e.ServiceId); var snap = Json.Obj(e.Snapshot); var masked = Json.Truthy(d["masked"]);
                return new
                {
                    id = e.Id, nome = d.Str("first_name"), cognome = d.Str("last_name"), telefono = d.Str("phone"), email = d.Str("email"), mascherato = masked, whatsapp = masked ? "" : ClientMails.WhatsApp(x, d.Str("phone")),
                    liberi = (snap["free"] as JsonArray)?.Select(f => Times.ParseAware(f?.ToString(), out _)).Where(f => f.HasValue).Select(f => bk.Iso(f.Value)), tenuto = e.HeldUntil.HasValue && e.HeldUntil > now ? bk.Iso(e.HeldUntil.Value) : null,
                    servizio = s.Name, servizioId = s.Id, sede = x.Row<Location>(s.LocationId).Name, tipo = s.Kind, dal = DateOnly.FromDateTime(e.First), al = DateOnly.FromDateTime(e.Last), dalle = e.TimeFrom?.ToString(@"hh\:mm"), alle = e.TimeTo?.ToString(@"hh\:mm"),
                    operatore = e.ResourceId.HasValue ? x.Row<Resource>(e.ResourceId)?.Name : "", operatoreId = e.ResourceId, persone = e.Party, online = snap["consent"].Str("channel") == "public", creato = bk.Iso(e.Created), cliente = e.ContactId,
                };
            }).ToList(),
            cliente = client == null ? null : new { id = client.Id, nome = cd.Str("first_name"), cognome = cd.Str("last_name"), telefono = cd.Str("phone") },
            servizi = x.S.Query<(Guid, string, string)>("SELECT s.id, s.name, l.name FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND l.active ORDER BY s.name").Select(s => new { id = s.Item1, nome = s.Item2, sede = s.Item3 }),
            operatori = x.S.Query<(Guid, string)>(@"SELECT r.id, r.name FROM core_resource r JOIN core_location l ON l.id=r.location_id WHERE r.active AND l.active AND r.id IN
                (SELECT sr.resource_id FROM core_serviceresource sr JOIN core_service s ON s.id=sr.service_id WHERE s.active AND s.kind IN ('resource','day')) ORDER BY r.name").Select(r => new { id = r.Item1, nome = r.Item2 }),
        };
    }

    [Rpc("attesa.azione", Perm = "booking.read")]
    public static object Action(Call c)
    {
        c.Require("booking.write"); var x = c.C; var wl = new Waitlist(x); var a = c.Str("azione");
        if (a == "add")
        {
            var chosen = c.Can("pii.read") && c.Id("contact") is Guid cid ? x.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = cid }) : null;
            if (c.Member != null)
            {
                // inside the user's scope
                var s = c.Id("service") is Guid sid ? x.S.One<Service>("SELECT * FROM core_service WHERE id=@id AND active", new { id = sid }) : null; if (s == null) throw new BookingException("Selezione non valida o non disponibile.");
                if (Permissions.CheckNewBooking(c.Member, s.LocationId, c.Id("resource")) is string no) throw new UnauthorizedAccessException(no);
            }
            var (_, created) = wl.AddAtDesk(c.Args, chosen);
            return new { testo = created ? "Aggiunto alla lista d'attesa." : "Era già in lista per questi giorni." };
        }
        var e = Rows(c).FirstOrDefault(r => r.Id == c.Id("entry")) ?? throw new KeyNotFoundException();
        wl.Close(e, a == "remove");
        return new { testo = a == "remove" ? "Tolto dalla lista." : "Segnato come fatto." };
    }
}
