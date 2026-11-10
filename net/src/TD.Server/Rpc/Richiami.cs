using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.recalls_view and portal.wa_reminders: Richiami (the clients to call back) and Promemoria WhatsApp
/// (the appointments to remind and yesterday's visits to thank, each with the chat ready; nothing is sent by the program).</summary>
public static class Richiami
{
    [Rpc("richiami", Perm = "contacts.read")]
    public static object Page(Call c)
    {
        var x = c.C; var cl = new Clients(x); var bk = new Bookings(x); var now = x.Now;
        return new
        {
            righe = cl.Recalls(now).Select(r =>
            {
                var d = Permissions.Shown(c.Perms, cl.Data(r.Contact)); var masked = Json.Truthy(d["masked"]);
                return new
                {
                    prenotazione = r.Booking.Id, cliente = r.Contact.Id, nome = d.Str("first_name"), cognome = d.Str("last_name"), telefono = d.Str("phone"), email = d.Str("email"), mascherato = masked,
                    whatsapp = masked ? "" : ClientMails.WhatsApp(x, d.Str("phone")), servizio = r.Service.Name, servizioId = r.Service.Id, ultima = bk.Iso(r.Last), dal = bk.Iso(r.DueAt), scaduto = r.DueAt <= now,
                    emailInviata = r.Booking.Snap.Str("recall_sent"),
                };
            }).ToList(),
        };
    }

    [Rpc("richiamo.fatto", Perm = "contacts.read")]
    public static object Done(Call c)
    {
        c.Require("booking.write"); var x = c.C;
        var b = x.S.One<Booking>("SELECT b.* FROM core_booking b JOIN core_service s ON s.id=b.service_id WHERE b.id=@id AND s.recall_days>0 AND b.contact_id IS NOT NULL", new { id = c.NeedId("booking") }) ?? throw new KeyNotFoundException();
        b.Snap["recall_done"] = Times.PyIso(x.Now); x.S.Update(b, "snapshot");
        x.Audit("contact.recalled", b.ContactId, new { booking = b.Id.ToString() });
        return new { testo = "Segnato come fatto." };
    }

    // ---------- Promemoria WhatsApp
    static readonly string[] CAME = { "arrived", "in_progress", "completed" };
    static bool Writer(Call c) => c.Can("booking.write") && c.Can("pii.read");
    static string Review(Call c) => ClientMails.Current(c.Tenant)["review"].Str("url");

    /// <summary>One a visit (its first service), with the name and the chat.</summary>
    static IEnumerable<(Booking B, JsonObject D)> Visits(Call c, DateTime a, DateTime b, string[] statuses)
    {
        var bk = new Bookings(c.C); var seen = new HashSet<Guid>();
        foreach (var r in new Agenda(c.C, Prenotazioni.V(c)).Visible("b.start>=@a AND b.start<@b", new { a, b }, statuses))
        {
            if (r.Visit.HasValue && !seen.Add(r.Visit.Value)) continue;
            yield return (r, bk.Data(r));
        }
    }
    static string Who(JsonObject d) => string.Join(" ", new[] { d.Str("first_name"), d.Str("last_name") }.Where(v => v != ""));
    static string Thanks(Call c, Booking b, JsonObject d, string url)
    {
        var link = c.Can("pii.read") ? ClientMails.WhatsApp(c.C, d.Str("phone")) : ""; if (link == "") return "";
        var v = ClientMails.Values(c.C, b, d); v["link"] = url;
        return link + "?text=" + Uri.EscapeDataString(Texts.Fill(string.IsNullOrEmpty(c.Tenant.WhatsappThanks) ? ClientMails.THANKS : c.Tenant.WhatsappThanks, v));
    }
    static object Row(Call c, Booking r, JsonObject d, string wa, string flag, bool due = false) => new
    {
        id = r.Id, chi = Who(d), inizio = new Bookings(c.C).Iso(r.Start), servizio = r.Snap.Str("service") is var s && s != "" ? s : c.C.Row<Service>(r.ServiceId).Name, operatore = r.Snap.Str("resource"),
        whatsapp = wa, mandato = r.Snap.Str(flag), daAvvisare = due,
    };

    [Rpc("whatsapp.promemoria", Perm = "booking.read")]
    public static object Reminders(Call c)
    {
        c.Require("pii.read"); var x = c.C; var t = c.Tenant; var today = x.Today; var w = ClientMails.WaWindow(x, x.Now);
        DateOnly? Arg(string k) => DateOnly.TryParseExact(c.Str(k), "yyyy-MM-dd", out var d) ? d : null;
        DateTime Mid(DateOnly d) => x.Cal.Local(d, TimeSpan.Zero).Value;
        var day = Arg("giorno"); var first = Arg("da");
        var span = day.HasValue ? new List<DateOnly> { day.Value } : Enumerable.Range(0, 7).Select((first ?? today).AddDays).ToList();
        var text = string.IsNullOrEmpty(t.WhatsappReminder) ? ClientMails.REMINDER : t.WhatsappReminder;
        var days = span.Select(d =>
        {
            var rows = Visits(c, Mid(d), Mid(d.AddDays(1)), new[] { "confirmed" }).Select(v => (v.B, v.D, Due: w.HasValue && w.Value.A <= v.B.Start && v.B.Start < w.Value.B)).ToList();
            return new
            {
                data = d, daAvvisare = rows.Any(r => r.Due), daFare = rows.Count(r => r.B.Snap.Str("wa_reminded") == ""),
                righe = rows.Select(r => Row(c, r.B, r.D, ClientMails.WhatsApp(x, r.D.Str("phone"), r.B, text), "wa_reminded", r.Due)).ToList(),
            };
        }).ToList();
        var url = Review(c); var thanksDay = Arg("grazie") ?? today.AddDays(-1);
        var thanks = url == "" ? new() : Visits(c, Mid(thanksDay), Mid(thanksDay.AddDays(1)), CAME).Select(v => Row(c, v.B, v.D, Thanks(c, v.B, v.D, url), "wa_thanked")).ToList();
        return new
        {
            giorni = days, singolo = day.HasValue, oggi = today, prima = span[0].AddDays(day.HasValue ? -1 : -7), dopo = span[0].AddDays(day.HasValue ? 1 : 7),
            quando = ClientMails.WA_WHEN.FirstOrDefault(k => k.Id != "" && k.Id == t.WaWhen).Label ?? "", etichetta = t.WaWhen == "d1" ? "da avvisare oggi" : "da avvisare adesso",
            scrive = c.Can("booking.write"), grazie = thanks, giornoGrazie = thanksDay, recensione = url,
        };
    }

    [Rpc("whatsapp.segna", Perm = "booking.read")]
    public static object Mark(Call c)
    {
        c.Require("pii.read"); c.Require("booking.write"); var x = c.C;
        var b = Prenotazioni.Visible(c, c.NeedId("booking")); var on = c.Str("sent") != "0"; var flag = c.Str("kind") == "thanks" ? "wa_thanked" : "wa_reminded";
        // the whole visit: one message says all its services
        foreach (var r in b.Visit.HasValue ? x.S.Query<Booking>("SELECT * FROM core_booking WHERE visit=@v", new { v = b.Visit }).ToList() : new List<Booking> { b })
        {
            if (on) r.Snap[flag] = Times.PyIso(x.Now); else r.Snap.Remove(flag);
            x.S.Update(r, "snapshot");
        }
        x.Audit(on ? flag == "wa_thanked" ? "whatsapp.thanked" : "whatsapp.reminded" : "whatsapp.unmarked", b.Id);
        return new { ok = true, alle = on ? x.Cal.ToLocal(x.Now).ToString("HH:mm") : "" };
    }

    /// <summary>The banners on top of every page (context.py: wa_due, thanks_todo): how many clients still to remind now and
    /// how many of yesterday still to thank; 0 when the business does not use them or this person cannot do it.</summary>
    [Rpc("avvisi")]
    public static object Nudges(Call c)
    {
        if (!Writer(c)) return new { whatsapp = 0, grazie = 0 };
        var x = c.C; var w = ClientMails.WaWindow(x, x.Now); var url = Review(c);
        var a = x.Cal.Local(x.Today.AddDays(-1), TimeSpan.Zero).Value; var z = x.Cal.Local(x.Today, TimeSpan.Zero).Value;
        return new
        {
            whatsapp = w.HasValue ? Visits(c, w.Value.A, w.Value.B, new[] { "confirmed" }).Count(v => !v.B.Snap.ContainsKey("wa_reminded")) : 0,
            grazie = url == "" ? 0 : Visits(c, a, z, CAME).Count(v => !v.B.Snap.ContainsKey("wa_thanked") && Clients.NormalizePhone(v.D.Str("phone")) != ""),
        };
    }
}
