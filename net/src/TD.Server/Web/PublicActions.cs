using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Web;

/// <summary>The links of the client's emails (core/public.py, stays.checkin_view, feeds.py): Gestisci (see, move,
/// cancel, calendar file, check-in), the buttons of the reminder, the consent and the unsubscribe, the calendars.</summary>
public static partial class Public
{
    static readonly Dictionary<string, string> TITLES = new() { ["cancel"] = "La tua prenotazione", ["confirm"] = "Ci sarai?", ["decline"] = "Non puoi venire?", ["consent"] = "Conferma iscrizione", ["unsubscribe"] = "Disiscriviti" };

    static string HumanMinutes(string lang, int m) =>
        m % 1440 == 0 ? (m >= 2880 ? Tr.T(lang, "{n} giorni").Replace("{n}", (m / 1440).ToString()) : Tr.T(lang, "1 giorno"))
        : m % 60 == 0 ? (m >= 120 ? Tr.T(lang, "{n} ore").Replace("{n}", (m / 60).ToString()) : Tr.T(lang, "1 ora")) : Tr.T(lang, "{n} minuti").Replace("{n}", m.ToString());

    static Actiontoken Token(Ctx c, string token, string kind = null, bool unused = false) =>
        c.S.One<Actiontoken>("SELECT * FROM core_actiontoken WHERE digest=@d AND expires>@n" + (kind != null ? " AND kind=@k" : "") + (unused ? " AND NOT used" : ""), new { d = Crypto.TokenHash(token), n = c.Now, k = kind });

    /// <summary>own_lang: the client's links open in the language he booked in, unless he switches (?lang=).</summary>
    static void OwnLang(Ctx c, HttpContext h, Booking b)
    {
        if (b != null && !h.Request.Query.ContainsKey("lang") && b.Snap.Str("lang") is "it" or "en") { h.Items["lang"] = b.Snap.Str("lang"); c.Language = b.Snap.Str("lang"); }
    }
    /// <summary>too_late: the minutes before the start when the client stops cancelling and moving by himself, once that
    /// moment has come (0 otherwise): the policy in force when the booking was made, not today's setting.</summary>
    static int TooLate(Ctx c, Booking b)
    {
        var cutoff = b.Snap["policy"]?["cancel_cutoff_minutes"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : c.Row<Service>(b.ServiceId).CancelCutoffMinutes;
        return cutoff > 0 && c.Now > b.Start.AddMinutes(-cutoff) ? cutoff : 0;
    }
    /// <summary>manage: the page of the client's link: the booking, the calendar links, Sposta and Annulla while he may,
    /// otherwise why not and the business's phone.</summary>
    static (Dictionary<string, object> Page, string Late, bool CanMove) Manage(Ctx c, Booking b)
    {
        var lang = c.Language; var cutoff = TooLate(c, b); var loc = c.Row<Location>(b.LocationId); var service = c.Row<Service>(b.ServiceId);
        var late = b.Status == "cancelled" ? Tr.T(lang, "La prenotazione è annullata") : !Bookings.MOVABLE.Contains(b.Status) || b.Start <= c.Now ? Tr.T(lang, "La prenotazione è già iniziata o conclusa")
            : cutoff > 0 ? Tr.T(lang, "Il termine per annullare o spostare online è scaduto ({tempo} prima dell'inizio)").Replace("{tempo}", HumanMinutes(lang, cutoff)) : "";
        if (late != "") late += !string.IsNullOrEmpty(loc.Phone) ? Tr.T(lang, ": per qualsiasi cosa chiama {telefono}.").Replace("{telefono}", loc.Phone) : Tr.T(lang, ": per qualsiasi cosa contatta l'attività.");
        var canMove = late == "" && service.Active && c.Row<Location>(service.LocationId).Active;
        return (new()
        {
            ["booking"] = BookingContext(c, b), ["what"] = ClientMails.Values(c, b, new JsonObject(), lang).ToDictionary(x => x.Key, x => (object)x.Value), ["where"] = string.Join(", ", new[] { loc.Name, loc.Address }.Where(x => !string.IsNullOrEmpty(x))),
            ["late"] = late, ["calendar"] = Bookings.ACCEPTED.Contains(b.Status) ? CalendarLinks(c, b) : null, ["can_move"] = canMove,
            ["checkin"] = service.Kind == "day" && Stays.STAYING.Contains(b.Status) && b.End > c.Now,
        }, late, canMove);
    }
    static Dictionary<string, object> BookingContext(Ctx c, Booking b) => new() { ["pk"] = b.Id.ToString(), ["status"] = b.Status, ["snapshot"] = b.Snap, ["party"] = b.Party, ["start"] = c.Cal.ToLocal(b.Start), ["end"] = c.Cal.ToLocal(b.End) };

    /// <summary>apply_action: what the button of the page does; the title and text of the page that follows, when they
    /// are not the usual ones.</summary>
    static (string Title, string Text)? Apply(Ctx c, HttpContext h, Actiontoken obj, bool oneClick)
    {
        var lang = c.Language; c.Lock();
        obj = c.S.One<Actiontoken>("SELECT * FROM core_actiontoken WHERE id=@id FOR UPDATE", new { id = obj.Id });
        if (obj.Expires <= c.Now) throw new BookingException(Tr.T(lang, "Collegamento scaduto."));
        if (obj.Used && obj.Kind != "unsubscribe") return null;
        (string, string)? result = null; var bk = new Bookings(c);
        if (obj.Kind == "cancel")
        {
            var b = c.S.Get<Booking>(obj.BookingId.Value);
            if (b.Start <= c.Now) throw new BookingException(Tr.T(lang, "La prenotazione è già iniziata. Contatta l'attività."));
            if (TooLate(c, b) is var cutoff and > 0) throw new BookingException(Tr.T(lang, "Il termine per annullare online è scaduto ({tempo} prima dell'inizio). Contatta direttamente l'attività.").Replace("{tempo}", HumanMinutes(lang, cutoff)));
            bk.ChangeStatus(b, "cancelled", reason: "client");
        }
        else if (obj.Kind is "confirm" or "decline")
        {
            // the buttons of the reminder
            var b = c.S.One<Booking>("SELECT * FROM core_booking WHERE id=@id FOR UPDATE", new { id = obj.BookingId });
            if (!Bookings.MOVABLE.Contains(b.Status) || b.Start <= c.Now) throw new BookingException(Tr.T(lang, "Questa prenotazione non è più attiva o è già iniziata. Contatta l'attività."));
            var now = Times.PyIso(c.Now); var snap = b.Snap; var phone = c.Row<Location>(b.LocationId).Phone;
            if (obj.Kind == "confirm")
            {
                snap.Remove("declined"); snap["client_confirmed"] = now; b.Snapshot = Json.Dump(snap); c.S.Update(b, "snapshot");
                c.Audit("booking.client_confirmed", b.Id);
                result = (Tr.T(lang, "Grazie, ti aspettiamo"), Tr.T(lang, "Abbiamo detto all'attività che ci sarai."));
            }
            else if (TooLate(c, b) > 0)
            {
                // inside the cutoff nothing is cancelled by itself: the business sees it and decides
                snap.Remove("client_confirmed"); snap["declined"] = now; b.Snapshot = Json.Dump(snap); c.S.Update(b, "snapshot");
                c.Out.NotifyStaff(c, b, "declined"); c.Audit("booking.declined", b.Id);
                result = (Tr.T(lang, "Abbiamo avvisato l'attività"), Tr.T(lang, "Il termine per annullare online è scaduto, quindi la prenotazione la annulla l'attività.")
                    + (!string.IsNullOrEmpty(phone) ? Tr.T(lang, " Per essere sicuro chiama {telefono}.").Replace("{telefono}", phone) : Tr.T(lang, " Per essere sicuro chiamala.")));
            }
            else
            {
                bk.ChangeStatus(b, "cancelled", reason: "client");
                result = (Tr.T(lang, "Prenotazione annullata"), Tr.T(lang, "Grazie di averci avvisato: il posto torna libero per altri."));
            }
        }
        else if (obj.Kind is "consent" or "unsubscribe")
        {
            var ct = c.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id FOR UPDATE", new { id = obj.ContactId }); var privacy = new Privacy(c);
            if (obj.Kind == "consent")
            {
                if (ct.Suppressed || ct.Consent == "yes") throw new BookingException(Tr.T(lang, "Questa conferma non è più valida."));
                var proof = (obj.BookingId.HasValue ? c.S.Get<Booking>(obj.BookingId.Value)?.Snap["consent_request"] as JsonObject : null)?.DeepClone() as JsonObject
                    ?? new JsonObject { ["text"] = c.Tenant.ConsentText, ["source"] = c.S.Scalar<long>("SELECT count(*) FROM core_consentevent WHERE contact_id=@c AND kind='imported'", new { c = ct.Id }) > 0 ? "import" : "unknown" };
                ct.Consent = "yes"; ct.ConsentAt = c.Now; var text = proof.Str("text");
                // proof is written only when the mailbox owner confirms
                proof["confirmed"] = Times.PyIso(c.Now); proof["booking"] = obj.BookingId?.ToString() ?? "";
                ct.ConsentProof = c.Encrypt(proof, "consent:" + ct.Id);
                privacy.Record(ct.Id, "confirmed", "email_link", text, obj.BookingId?.ToString() ?? "");
            }
            else
            {
                var already = ct.Consent == "revoked"; ct.Consent = "revoked"; ct.ConsentAt = c.Now;
                c.S.Execute("UPDATE core_actiontoken SET used=true WHERE contact_id=@c AND kind='consent'", new { c = ct.Id });
                if (!already) privacy.Record(ct.Id, "revoked", oneClick ? "one_click" : "email_link");
            }
            c.S.Update(ct, "consent", "consent_at", "consent_proof");
            c.Audit("contact." + obj.Kind, ct.Id);
        }
        obj.Used = true; c.S.Update(obj, "used");
        return result;
    }

    /// <summary>manage_token: the booking of a «Gestisci» link that the client may still move, or why not.</summary>
    static Booking ManageToken(Ctx c, string token)
    {
        var obj = Token(c, token, "cancel", unused: true); var b = obj?.BookingId is Guid id ? c.S.Get<Booking>(id) : null;
        if (b == null) throw new BookingException(Tr.T(c.Language, "Collegamento scaduto. Contatta l'attività."));
        var (_, late, canMove) = Manage(c, b);
        if (!canMove) throw new BookingException(late != "" ? late : Tr.T(c.Language, "Questa prenotazione non si sposta online. Contatta l'attività."));
        return b;
    }
    /// <summary>visiting: a booking of a visit moves with the visit: its first service, with whom, and the services after it.</summary>
    static (string Service, string Option, string Resource, JsonArray Extra)? Visiting(Ctx c, Booking b)
    {
        var rows = new Bookings(c).VisitRows(b).Where(x => Bookings.MOVABLE.Contains(x.Status)).ToList(); if (rows.Count < 2) return null;
        return (rows[0].ServiceId.ToString(), rows[0].OptionId?.ToString() ?? "", Json.Truthy(rows[0].Snap["any_resource"]) ? "" : rows[0].ResourceId?.ToString() ?? "",
            new JsonArray(rows.Skip(1).Select(x => (JsonNode)new JsonObject { ["id"] = x.ServiceId.ToString(), ["name"] = x.Snap.Str("service") is var n && n != "" ? n : c.Row<Service>(x.ServiceId).Name }).ToArray()));
    }
    /// <summary>same: what the client keeps when he moves: service, variant, people; he chooses the day, the time and, for
    /// a stay, the dates, and another operator of the same service, or whoever is free; otherwise the one he had.</summary>
    static JsonObject Same(Ctx c, Booking b, Func<string, string> p)
    {
        var v = Visiting(c, b); var keep = Json.Truthy(b.Snap["any_resource"]) ? "" : b.ResourceId?.ToString() ?? "";
        var data = new JsonObject
        {
            ["service"] = b.ServiceId.ToString(), ["option"] = b.OptionId?.ToString() ?? "", ["resource"] = p("resource") != null && v == null && c.Row<Service>(b.ServiceId).ClientPicks ? p("resource") : keep, ["party"] = b.Party,
            ["date"] = p("date") ?? "", ["start"] = p("start") ?? "", ["end_date"] = p("end_date") ?? "", ["occurrence"] = p("occurrence") ?? "",
        };
        if (v is var (service, option, resource, extra)) { data["service"] = service; data["option"] = option; data["resource"] = resource; data["extra"] = string.Join(",", extra.Select(x => x["id"].ToString())); data["visit"] = true; }
        return data;
    }

    /// <summary>feeds.feed: the bookings of an operator, the last 30 days and the next year, the same UID as the client's file.</summary>
    static string OperatorFeed(Ctx c, Resource r)
    {
        var host = Texts.Host(c.PublicOrigin); var bk = new Bookings(c);
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Tastiere Digitali//TD Booking Suite//IT", "X-WR-CALNAME:" + Texts.IcsEscape($"{r.Name} · {c.Tenant.Name}") };
        foreach (var b in c.S.Query<Booking>("SELECT * FROM core_booking WHERE resource_id=@r AND status=ANY(@st) AND \"end\">=@a AND start<=@b ORDER BY start", new { r = r.Id, st = Bookings.ACCEPTED, a = c.Now.AddDays(-30), b = c.Now.AddDays(365) }))
        {
            var loc = c.Row<Location>(b.LocationId); var what = b.Snap.Str("service") is var sv && sv != "" ? sv : c.Row<Service>(b.ServiceId).Name;
            if (r.FeedNames) { var d = bk.Data(b); what += " · " + string.Join(" ", new[] { d.Str("first_name"), d.Str("last_name") }.Where(x => x != "")); }
            lines.AddRange(new[] { "BEGIN:VEVENT", $"UID:{b.Id}@{host}", "DTSTAMP:" + Texts.Utc(c.Now), "DTSTART:" + Texts.Utc(b.Start), "DTEND:" + Texts.Utc(b.End), "SUMMARY:" + Texts.IcsEscape(what.Trim(' ', '·')),
                "LOCATION:" + Texts.IcsEscape(string.Join(", ", new[] { loc.Name, loc.Address }.Where(x => !string.IsNullOrEmpty(x)))), "END:VEVENT" });
        }
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(Texts.Fold)) + "\r\n";
    }

    static IResult Throttled(Server s, HttpContext h, string label, int limit, int seconds)
    {
        try { s.Identity.Throttle(label, Address(s, h), limit, seconds); return null; }
        catch (UnauthorizedAccessException ex) { return PublicHost.Plain(403, ex.Message); }
    }
    const string CSRF = "Verifica di sicurezza non riuscita. Ricarica la pagina e riprova.";

    static partial void MapActions(WebApplication app)
    {
        app.MapMethods("/action/{slug}/{token}/", new[] { "GET", "POST" }, async (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            if (Throttled(s, h, "action", 60, 60) is { } stop) return stop;
            var form = h.Request.Method == "POST" ? await h.Request.ReadFormAsync() : null;
            if (form != null && !PublicHost.CsrfOk(h, form)) return PublicHost.Plain(403, CSRF);
            using var c = PublicHost.Ctx(s, h, t);
            var obj = Token(c, token);
            if (obj == null) return PublicHost.Message(s, h, t, "Collegamento scaduto", "Contatta l'attività per assistenza.", 400);
            var b = obj.BookingId is Guid id ? c.S.Get<Booking>(id) : null; OwnLang(c, h, b); var lang = c.Language;
            if (form != null)
            {
                try
                {
                    var (title, text) = Apply(c, h, obj, form["List-Unsubscribe"] == "One-Click") ?? (Tr.T(lang, "Operazione completata"), Tr.T(lang, "La tua richiesta è stata registrata."));
                    c.Commit();
                    return PublicHost.Page(s, h, "message.html", t, new() { ["title"] = title, ["text"] = text, ["public"] = true });
                }
                catch (BookingException ex) { return PublicHost.Page(s, h, "message.html", t, new() { ["title"] = Tr.T(lang, "Operazione non disponibile"), ["text"] = ex.Message, ["public"] = true }, 400); }
            }
            var ctx = new Dictionary<string, object> { ["title"] = Tr.T(lang, TITLES.GetValueOrDefault(obj.Kind, "Conferma")), ["done"] = obj.Used, ["kind"] = obj.Kind, ["public"] = true };
            if (b != null && obj.Kind is "cancel" or "confirm" or "decline") { foreach (var (k, v) in Manage(c, b).Page) ctx[k] = v; ctx["moved"] = h.Request.Query.ContainsKey("spostata"); }
            return PublicHost.Page(s, h, "action.html", t, ctx);
        });

        app.MapPost("/action/{slug}/{token}/one-click/", async (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            var form = await h.Request.ReadFormAsync(); using var c = PublicHost.Ctx(s, h, t);
            var obj = Token(c, token, "unsubscribe");
            if (obj == null || form["List-Unsubscribe"] != "One-Click") return Results.Json(new JsonObject { ["ok"] = false }, statusCode: 400);
            Apply(c, h, obj, true); c.Commit();
            return Results.Json(new JsonObject { ["ok"] = true });
        });

        // Sposta from the client's link: the public page's rules, the checks of Move, until the cutoff
        app.MapGet("/action/{slug}/{token}/sposta/", (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            if (Throttled(s, h, "action", 60, 60) is { } stop) return stop;
            using var c = PublicHost.Ctx(s, h, t); Booking b;
            try { b = ManageToken(c, token); }
            catch (BookingException ex) { return PublicHost.Page(s, h, "message.html", t, new() { ["title"] = Tr.T(c.Language, "Spostamento non disponibile"), ["text"] = ex.Message, ["public"] = true }, 400); }
            OwnLang(c, h, b); var lang = c.Language;
            var widget = Widget(c, h); var v = Visiting(c, b); var first = v?.Service ?? b.ServiceId.ToString();
            // the operators of the same service, when the client could choose them when booking
            var services = new JsonArray();
            foreach (var x in widget["services"].AsArray().Where(x => x["id"].ToString() == first))
            { var item = (JsonObject)x.DeepClone(); item["options"] = new JsonArray(); if (v != null) item["resources"] = new JsonArray(); services.Add(item); }
            widget["services"] = services;
            var path = h.Request.Path.Value; var mine = Json.Truthy(b.Snap["any_resource"]) ? "" : b.ResourceId?.ToString() ?? "";
            var editing = new JsonObject { ["service"] = first, ["option"] = "", ["resource"] = mine, ["party"] = b.Party }; if (v != null) editing["extra"] = v.Value.Extra;
            var pick = services.Count > 0 && services[0]["resources"].AsArray().Count > 0;
            widget["submit"] = path; widget["slots"] = path + "orari/"; widget["editing"] = editing; widget["locked"] = true; widget["pick_resource"] = pick;
            return PublicHost.Page(s, h, "booking.html", t, new()
            {
                ["widget"] = widget, ["moving"] = true, ["public"] = true, ["booking"] = BookingContext(c, b), ["manage"] = $"/action/{slug}/{token}/",
                ["intro"] = Tr.T(lang, pick ? "Stesso servizio: scegli il nuovo giorno, l'orario e, se vuoi, un altro operatore" : "Stesso servizio: scegli il nuovo giorno e l'orario"),
            });
        });
        app.MapPost("/action/{slug}/{token}/sposta/", async (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            var sent = h.Request.Headers["X-CSRFToken"].ToString(); var cookie = h.Request.Cookies["csrftoken"] ?? "";
            if (cookie.Length != 43 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cookie), Encoding.ASCII.GetBytes(sent.PadRight(43)[..43])) || sent.Length != 43) return Error(403, CSRF);
            JsonObject data;
            try { s.Identity.Throttle("action", Address(s, h), 60, 60); s.Identity.Throttle("booking:" + t.Id, Address(s, h), 20, 3600); data = await Body(h); }
            catch (UnauthorizedAccessException ex) { return Error(403, ex.Message); }
            catch (BookingException ex) { return Error(400, ex.Message); }
            return Answer(s, h, t, c =>
            {
                var b = ManageToken(c, token); OwnLang(c, h, b);
                new Bookings(c).Move(b.Id, Same(c, b, k => data.ContainsKey(k) ? data[k]?.ToString() ?? "" : null), desk: false);
                return Results.Json(new JsonObject { ["redirect"] = $"/action/{slug}/{token}/?spostata=1" });
            });
        });
        app.MapGet("/action/{slug}/{token}/sposta/orari/", (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            try { s.Identity.Throttle("availability:" + t.Id, Address(s, h), 120, 60); } catch (UnauthorizedAccessException ex) { return Error(403, ex.Message); }
            string P(string k) => h.Request.Query.TryGetValue(k, out var v) ? v.ToString() : null;
            return Answer(s, h, t, c =>
            {
                var b = ManageToken(c, token); var same = Same(c, b, P);
                return Results.Json(new JsonObject { ["slots"] = FreeSlots(c, k => same[k]?.ToString(), new[] { b.Id }) });
            }, "Richiesta non valida.");
        });

        // the booking as a calendar file, from the client's link
        app.MapGet("/action/{slug}/{token}/calendario.ics", (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            if (Throttled(s, h, "action", 60, 60) is { } stop) return stop;
            using var c = PublicHost.Ctx(s, h, t); var obj = Token(c, token, "cancel"); var b = obj?.BookingId is Guid id ? c.S.Get<Booking>(id) : null;
            if (b == null || !Bookings.ACCEPTED.Contains(b.Status)) return PublicHost.Plain(404, "Prenotazione non disponibile.");
            h.Response.Headers.ContentDisposition = "attachment; filename=\"prenotazione.ics\"";
            return Results.Text(Texts.Ics(c, b), "text/calendar; charset=utf-8", Encoding.UTF8);
        });
        // the private calendar of an operator, from its signed link; renewed or switched off, 404
        app.MapGet("/action/{slug}/feed/{token}/calendario.ics", (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            if (Throttled(s, h, "feed", 120, 3600) is { } stop) return stop;
            using var c = PublicHost.Ctx(s, h, t); Resource r = null;
            try
            {
                var raw = token.Split(':')[0].Replace('-', '+').Replace('_', '/'); raw += new string('=', (4 - raw.Length % 4) % 4);
                var key = JsonNode.Parse(Convert.FromBase64String(raw)) as JsonArray;
                if (key is { Count: 2 } && Guid.TryParse(key[0].ToString(), out var id) && int.TryParse(key[1].ToString(), out var version))
                {
                    var found = c.S.Get<Resource>(id);
                    // the link is the signature of this operator and this version: anything else is not ours
                    if (found != null && found.Feed != 0 && found.Feed == version && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(DjangoSigning.SignObject("td-feed", $"[\"{found.Id}\",{found.Feed}]", s.Config.SecretKey)))) r = found;
                }
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { }
            return r == null ? PublicHost.Plain(404, "Calendario non disponibile.") : Results.Text(OperatorFeed(c, r), "text/calendar; charset=utf-8", Encoding.UTF8);
        });
        // the calendar of a unit for Airbnb, Booking.com and the others: busy nights only
        app.MapGet("/book/{slug}/calendario/{unit:guid}/{file}", (Server s, HttpContext h, string slug, Guid unit, string file) =>
        {
            var (t, no) = PublicHost.Business(s, slug, true); if (t == null) return no;
            try { s.Identity.Throttle("availability:" + t.Id, Address(s, h), 120, 60); } catch (UnauthorizedAccessException ex) { return PublicHost.Plain(403, ex.Message); }
            using var c = PublicHost.Ctx(s, h, t); var stays = new Stays(c); var u = c.S.One<Resource>("SELECT * FROM core_resource WHERE id=@unit AND active", new { unit });
            if (u == null || !file.EndsWith(".ics") || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(file[..^4]), Encoding.UTF8.GetBytes(stays.FeedToken(u)))) return PublicHost.Plain(404, "Calendario non disponibile.");
            var name = new string(u.Name.Normalize(NormalizationForm.FormKD).Where(ch => ch < 128).ToArray()).ToLowerInvariant().Replace(" ", "-");
            h.Response.Headers.ContentDisposition = $"inline; filename=\"{(name != "" ? name : "alloggio")}.ics\"";
            return Results.Text(stays.Feed(u), "text/calendar; charset=utf-8", Encoding.UTF8);
        });

        // check-in online from the client's «Gestisci» link, until the departure: the same form as the desk's
        app.MapMethods("/action/{slug}/{token}/check-in/", new[] { "GET", "POST" }, async (Server s, HttpContext h, string slug, string token) =>
        {
            var (t, no) = PublicHost.Business(s, slug, false); if (t == null) return no;
            if (Throttled(s, h, "action", 60, 60) is { } stop) return stop;
            var form = h.Request.Method == "POST" ? await h.Request.ReadFormAsync() : null;
            if (form != null && !PublicHost.CsrfOk(h, form)) return PublicHost.Plain(403, CSRF);
            using var c = PublicHost.Ctx(s, h, t); var obj = Token(c, token, "cancel"); var b = obj?.BookingId is Guid id ? c.S.Get<Booking>(id) : null;
            if (b == null || c.Row<Service>(b.ServiceId).Kind != "day" || !Stays.STAYING.Contains(b.Status) || b.End <= c.Now)
                return PublicHost.Page(s, h, "message.html", t, new() { ["title"] = "Check-in non disponibile", ["text"] = "Il collegamento è scaduto o il soggiorno è concluso. Contatta la struttura.", ["public"] = true }, 400);
            var error = ""; string Post(string k) => form[k].ToString();
            if (form != null)
            {
                if (Throttled(s, h, "booking:" + t.Id, 20, 3600) is { } many) return many;
                try
                {
                    var guests = Stays.ReadGuests(Post, b.Party); if (guests.Count == 0) throw new BookingException("Scrivi almeno un ospite.");
                    new Stays(c).SaveGuests(b, guests, Stays.ReadTrip(Post)); c.Commit();
                    return PublicHost.Page(s, h, "message.html", t, new() { ["title"] = "Check-in online fatto", ["text"] = "Grazie: i dati degli ospiti sono arrivati. All'arrivo mostrate i documenti, come chiede la legge.", ["public"] = true });
                }
                catch (BookingException ex) { error = ex.Message; }
            }
            var data = new Bookings(c).Data(b); var saved = (data["guests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            var rows = Enumerable.Range(0, b.Party).Select(i =>
            {
                JsonObject g;
                if (error != "") { g = new JsonObject(); foreach (var k in Stays.GUEST.Append("exemption")) g[k] = Post($"g{i}-{k}"); }
                else g = i < saved.Count ? saved[i] : new JsonObject { ["kind"] = b.Party == 1 ? "16" : i == 0 ? "17" : "19" };
                return (object)new Dictionary<string, object> { ["i"] = (long)i, ["g"] = g };
            }).ToList();
            object trip = data; if (error != "") { var (tourism, transport) = Stays.ReadTrip(Post); trip = new Dictionary<string, object> { ["tourism"] = tourism, ["transport"] = transport }; }
            static List<object> Pairs(IEnumerable<(string, string)> x) => x.Select(p => (object)new object[] { p.Item1, p.Item2 }).ToList();
            return PublicHost.Page(s, h, "guests.html", t, new()
            {
                ["booking"] = BookingContext(c, b), ["rows"] = rows, ["kinds"] = Pairs(Stays.KINDS), ["documents"] = Pairs(Stays.Documents()), ["error"] = error, ["public"] = true, ["post_to"] = h.Request.Path.Value,
                ["heads"] = Stays.HEADS.Cast<object>().ToList(), ["exemptions"] = Pairs(Stays.EXEMPTIONS), ["trip"] = trip,
                ["tourism"] = Pairs(Stays.TOURISM.Select(x => (x.ToUpperInvariant(), x))), ["transport"] = Pairs(Stays.TRANSPORT.Select(x => (x.ToUpperInvariant(), x))),
            });
        });
    }
}
