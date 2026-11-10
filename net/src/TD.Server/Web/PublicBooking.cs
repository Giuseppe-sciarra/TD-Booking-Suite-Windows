using System.Text.Json;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Web;

/// <summary>The booking page of a business and what it asks the server (core/public.py): /book/slug/, its free times,
/// the booking sent, «Avvisami se si libera». Nobody is signed in here: the page's signed ticket is the only pass.</summary>
public static partial class Public
{
    static readonly Dictionary<string, string> INTRO = new() { ["none"] = "Scegli giorno e orario", ["location"] = "Scegli la sede, poi giorno e orario", ["service"] = "Scegli il servizio, poi giorno e orario" };

    static string SourceTag(string value) { value = (value ?? "").Trim().ToLowerInvariant(); return System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9_-]{1,30}$") ? value : ""; }
    static string PhotoLink(Resource r, string slug) => r.PhotoAt.HasValue ? $"/brand/{slug}/foto/{r.Id}.jpg?v={new DateTimeOffset(DateTime.SpecifyKind(r.PhotoAt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()}" : r.PhotoUrl;

    /// <summary>widget_data, for the public page: what the form shows and where it sends.</summary>
    internal static JsonObject Widget(Ctx c, HttpContext h, Waitentry wait = null)
    {
        var t = c.Tenant; var q = h.Request.Query; var en = c.Language == "en";
        var rows = c.S.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND l.active ORDER BY s.name").ToList();
        // [td_booking_suite location="..."] shows one location only, service="..." that service only
        if (Guid.TryParse(q["location"], out var place) && rows.Any(s => s.LocationId == place)) rows = rows.Where(s => s.LocationId == place).ToList();
        if (Guid.TryParse(q["service"], out var only) && rows.Any(s => s.Id == only)) rows = rows.Where(s => s.Id == only).ToList();
        // a private service only from its own link, and then alone on the page
        var secret = q["riservato"].ToString(); if (secret.Length > 40) secret = secret[..40];
        var own = secret != "" ? rows.Where(s => s.Secret != "" && s.Secret == secret).ToList() : new();
        rows = own.Count > 0 ? own : rows.Where(s => s.Secret == "").ToList();
        var options = c.S.All<Option>("active", order: "id").ToLookup(o => o.ServiceId); var links = c.S.All<Serviceresource>().ToLookup(l => l.ServiceId);
        var resources = c.S.All<Resource>("active AND NOT secondary", order: "name").ToList(); var areas = c.S.All<Area>(order: "name").ToLookup(a => a.LocationId);
        var services = new JsonArray();
        foreach (var s in rows)
        {
            var item = new JsonObject
            {
                ["id"] = s.Id.ToString(), ["name"] = en && s.NameEn != "" ? s.NameEn : s.Name, ["kind"] = s.Kind, ["turnover"] = s.Turnover, ["resource_label"] = s.ResourceLabel,
                ["description"] = en && s.DescriptionEn != "" ? s.DescriptionEn : s.Description, ["location"] = c.Row<Location>(s.LocationId).Name, ["max_party"] = s.MaxParty, ["request_above"] = s.RequestAbove,
                ["fields"] = JsonNode.Parse(string.IsNullOrEmpty(s.Fields) ? "[]" : s.Fields), ["price"] = "", // prices in the staff's own form only
                ["options"] = new JsonArray(options[s.Id].Select(o => (JsonNode)new JsonObject { ["id"] = o.Id.ToString(), ["name"] = o.Name, ["duration"] = o.Duration, ["price"] = "" }).ToArray()),
                ["areas"] = new JsonArray(s.Kind is "capacity" or "shift" ? areas[s.LocationId].Select(a => (JsonNode)new JsonObject { ["id"] = a.Id.ToString(), ["name"] = a.Name }).ToArray() : Array.Empty<JsonNode>()),
            };
            // the cabins and machines are never chosen; with client_picks off the client does not choose with whom
            var list = new JsonArray();
            if (s.ClientPicks) foreach (var r in resources.Where(r => r.LocationId == s.LocationId && links[s.Id].Any(l => l.ResourceId == r.Id)))
            {
                var res = new JsonObject { ["id"] = r.Id.ToString(), ["name"] = r.Name, ["capacity"] = r.Capacity, ["description"] = r.Description, ["photo"] = PhotoLink(r, t.Slug) };
                if (r.AwayUntil.HasValue && DateOnly.FromDateTime(r.AwayUntil.Value) >= DateOnly.FromDateTime(c.Now))
                    res["away"] = new JsonArray($"{r.AwayFrom:yyyy-MM-dd}", $"{r.AwayUntil:yyyy-MM-dd}", r.AwayById?.ToString() ?? "", r.AwayById.HasValue ? c.Row<Resource>(r.AwayById.Value)?.Name ?? "" : "");
                list.Add(res);
            }
            item["resources"] = list; services.Add(item);
        }
        var prefix = $"/book/{t.Slug}/api";
        var ticket = new JsonObject { ["tenant"] = t.Id.ToString(), ["origin"] = q.ContainsKey("theme") ? "wordpress" : "public", ["src"] = SourceTag(q["src"]) };
        if (wait != null) ticket["wait"] = wait.Id.ToString();
        if (own.Count > 0) ticket["private"] = new JsonArray(own.Select(s => (JsonNode)s.Id.ToString()).ToArray());
        var widget = new JsonObject
        {
            ["service_label"] = Tr.T(c.Language, Presets.ServiceWord(t.BusinessType)), ["note_hint"] = Presets.NoteHint(t.BusinessType), ["services"] = services, ["today"] = c.Today.ToString("yyyy-MM-dd"), ["timezone"] = t.Timezone,
            ["slots"] = prefix + "/slots/", ["submit"] = prefix + "/submit/", ["ticket"] = DjangoSigning.Dumps("public-booking", ticket.ToJsonString()), ["internal"] = false,
            ["allergens"] = new JsonArray(t.AskedAllergens.Select(a => (JsonNode)a).ToArray()), ["can_force"] = false, ["wait"] = prefix + "/attesa/",
        };
        // the page's language also for its answers and for the booking it makes
        if (c.Language != "it") foreach (var k in new[] { "slots", "submit", "wait" }) widget[k] = widget[k] + "?lang=" + c.Language;
        return widget;
    }
    static string FirstStep(JsonObject widget)
    {
        var services = widget["services"].AsArray(); if (services.Count <= 1) return "none";
        return services.Select(s => s["name"].ToString()).Distinct().Count() == 1 && services.Select(s => s["location"].ToString()).Distinct().Count() > 1 ? "location" : "service";
    }

    /// <summary>free_slots: the free times of a day, or the units free for a stay, as the form asks for them.</summary>
    internal static JsonArray FreeSlots(Ctx c, Func<string, string> p, IEnumerable<Guid> exclude = null)
    {
        JsonNode N(string k) => p(k) is var v && !string.IsNullOrEmpty(v) ? JsonValue.Create(v) : null;
        if (!DateOnly.TryParseExact(p("date") ?? "", "yyyy-MM-dd", out var day)) throw new BookingException("Richiesta non valida.");
        var bk = new Bookings(c); var service = bk.ActiveService(N("service")); var slug = c.Tenant.Slug;
        JsonObject Row(Slot x) => new() { ["start"] = x.Start, ["end"] = x.End, ["resource"] = x.Resource, ["resource_name"] = x.ResourceName, ["occurrence"] = x.Occurrence };
        if (service.Kind == "day")
        {
            var start = c.Cal.Local(day, service.Checkin) ?? throw new BookingException("Data non disponibile.");
            var data = new JsonObject { ["start"] = bk.Iso(start), ["end_date"] = p("end_date"), ["party"] = p("party") ?? "1" };
            if (bk.HasUnits(service))
            {
                var unit = bk.Details(service, null, N("resource")).Item2; var party = Bookings.Integer(N("party") ?? 1, "Quantità", 1, service.MaxParty);
                data["resource"] = unit?.Id.ToString() ?? "";
                var w = bk.Bounds(service, data, exclude);
                return new JsonArray(bk.FreeUnits(service, w.Bs, w.Be, party, unit, exclude).Select(u => (JsonNode)new JsonObject
                {
                    ["start"] = bk.Iso(w.Start), ["end"] = bk.Iso(w.End), ["resource"] = u.Id.ToString(), ["resource_name"] = u.Name, ["description"] = u.Description, ["photo"] = PhotoLink(u, slug), ["capacity"] = u.Capacity, ["occurrence"] = "",
                }).ToArray());
            }
            var w2 = bk.Bounds(service, data, exclude);
            return new JsonArray(new JsonObject { ["start"] = bk.Iso(w2.Start), ["end"] = bk.Iso(w2.End), ["resource"] = "", ["resource_name"] = "", ["occurrence"] = "" });
        }
        if (!string.IsNullOrEmpty(p("extra")))
        {
            // several services in one visit: the times the whole visit fits; moving it, its services are left out
            var ex = exclude?.ToList();
            if (ex is { Count: 1 } && c.S.Scalar<Guid?>("SELECT visit FROM core_booking WHERE id=@id", new { id = ex[0] }) is Guid v) ex = c.S.Query<Guid>("SELECT id FROM core_booking WHERE visit=@v", new { v }).ToList();
            var (option, resource, _) = bk.Details(service, N("option"), N("resource"));
            var items = bk.VisitServices(service, N("extra")).Select(x => (x, (Option)null)).ToList(); items[0] = (service, option);
            return new JsonArray(bk.VisitSlots(items, day, resource, ex).Select(x => (JsonNode)Row(x)).ToArray());
        }
        return new JsonArray(bk.Available(N("service"), day, N("option"), N("resource"), N("party") ?? 1, exclude, area: N("area")).Select(x => (JsonNode)Row(x)).ToArray());
    }
    /// <summary>closed: no time on the page that day, booked or free: the hours, closures and dates of the event say none.</summary>
    static bool Closed(Ctx c, Func<string, string> p)
    {
        JsonNode N(string k) => p(k) is var v && !string.IsNullOrEmpty(v) ? JsonValue.Create(v) : null;
        var bk = new Bookings(c); var service = bk.ActiveService(N("service")); if (service.Kind == "day") return false;
        var (option, resource, _) = bk.Details(service, N("option"), N("resource")); var day = DateOnly.ParseExact(p("date"), "yyyy-MM-dd");
        var pool = resource != null || service.Kind != "resource" ? new List<Resource> { resource } : bk.Linked(service);
        return !pool.Any(r => bk.Candidates(service, day, option, r).Any());
    }

    // ---------- what every answer in JSON shares
    internal static IResult Error(int status, string text) => Results.Json(new JsonObject { ["error"] = text }, statusCode: status);
    /// <summary>The checks of a booking sent from the page: the portal's own address, the page's signed ticket, the limits.</summary>
    static (JsonObject Ticket, IResult Refused) Ticket(Server s, HttpContext h, Tenant t)
    {
        var origin = h.Request.Headers.Origin.ToString(); var scheme = s.PublicOrigin.Split("://")[0];
        if (origin == "" || origin != s.PublicOrigin && origin != scheme + "://" + h.Request.Host.Value) return (null, Error(403, Tr.T(PublicHost.Lang(h), "Operazione non consentita.")));
        var ticket = DjangoSigning.Loads("public-booking", h.Request.Headers["X-TDBS-Ticket"], 3600);
        if (ticket == null) return (null, Error(403, Tr.T(PublicHost.Lang(h), "Pagina scaduta. Ricarica e riprova.")));
        if (ticket.Str("tenant") != t.Id.ToString()) return (null, Error(403, Tr.T(PublicHost.Lang(h), "Operazione non consentita.")));
        return (ticket, null);
    }
    internal static async Task<JsonObject> Body(HttpContext h)
    {
        try { return await JsonNode.ParseAsync(h.Request.Body) as JsonObject ?? throw new BookingException("Richiesta non valida."); }
        catch (JsonException) { throw new BookingException("Richiesta non valida."); }
    }
    /// <summary>One request in the business's data: saved when it ends well; what goes wrong is said in JSON, as the form expects.</summary>
    internal static IResult Json(Server s, HttpContext h, Tenant t, Func<Ctx, IResult> work, string fallback = "Controlla i dati.")
    {
        try { using var c = PublicHost.Ctx(s, h, t); var result = work(c); c.Commit(); return result; }
        catch (ForceWarning ex) { return Results.Json(new JsonObject { ["error"] = ex.Message, ["force"] = true }, statusCode: 409); }
        catch (UnauthorizedAccessException ex) { return Error(403, ex.Message != "" ? ex.Message : Tr.T(PublicHost.Lang(h), "Operazione non consentita.")); }
        catch (Exception ex) when (ex is BookingException or FormErrors or FormatException or InvalidOperationException or OverflowException) { return Error(400, ex is BookingException && ex.Message != "" ? ex.Message : Tr.T(PublicHost.Lang(h), fallback)); }
    }
    static string Address(Server s, HttpContext h) => s.Rete.Client(h);
    /// <summary>ics.links: Add to Google Calendar / Outlook, the event opened in the client's own calendar.</summary>
    internal static JsonObject CalendarLinks(Ctx c, Booking b)
    {
        var loc = c.Row<Location>(b.LocationId); var title = $"{(b.Snap.Str("service") is var sv && sv != "" ? sv : c.Row<Service>(b.ServiceId).Name)} · {c.Tenant.Name}";
        var place = string.Join(", ", new[] { loc.Name, loc.Address }.Where(x => !string.IsNullOrEmpty(x)));
        static string Q(params (string K, string V)[] kv) => string.Join("&", kv.Select(x => x.K + "=" + Uri.EscapeDataString(x.V).Replace("%20", "+")));
        return new JsonObject
        {
            ["google"] = "https://calendar.google.com/calendar/render?" + Q(("action", "TEMPLATE"), ("text", title), ("dates", Texts.Utc(b.Start) + "/" + Texts.Utc(b.End)), ("location", place)),
            ["outlook"] = "https://outlook.live.com/calendar/action/compose?" + Q(("path", "/calendar/action/compose"), ("rru", "addevent"), ("subject", title), ("location", place), ("startdt", $"{b.Start:yyyy-MM-ddTHH:mm:ssZ}"), ("enddt", $"{b.End:yyyy-MM-ddTHH:mm:ssZ}")),
        };
    }

    static partial void MapBooking(WebApplication app)
    {
        app.MapGet("/book/{slug}/", (Server s, HttpContext h, string slug) =>
        {
            var (t, no) = PublicHost.Business(s, slug, true); if (t == null) return no;
            using var c = PublicHost.Ctx(s, h, t); var lang = c.Language;
            // from the link of «Si è liberato un posto»: the day, the service, with whom, how many and who, already chosen
            var token = h.Request.Query["attesa"].ToString(); var wl = new Waitlist(c); var offer = token != "" ? wl.Opened(token) : null;
            var widget = Widget(c, h, offer?.Entry);
            if (offer is var (e, day))
            {
                var d = wl.Data(e); var stay = c.Row<Service>(e.ServiceId).Kind == "day";
                widget["prefill"] = new JsonObject
                {
                    ["service"] = e.ServiceId.ToString(), ["resource"] = e.ResourceId?.ToString() ?? "", ["date"] = stay ? $"{e.First:yyyy-MM-dd}" : day.ToString("yyyy-MM-dd"), ["end_date"] = stay ? $"{e.Last:yyyy-MM-dd}" : "", ["party"] = e.Party,
                    ["contact"] = new JsonObject { ["first_name"] = d.Str("first_name"), ["last_name"] = d.Str("last_name"), ["email"] = d.Str("email"), ["phone"] = d.Str("phone") },
                };
                widget["slots"] = widget["slots"] + (widget["slots"].ToString().Contains('?') ? "&" : "?") + "attesa=" + token;
            }
            var en = lang == "en"; var conf = TextMessages.Config(c);
            var channels = new List<object>(); if (TD.Core.Domain.Json.Truthy(conf["provider"])) channels.Add("sms"); if (TD.Core.Domain.Json.Truthy(conf["wa_token"])) channels.Add("whatsapp");
            return PublicHost.Page(s, h, "booking.html", t, new()
            {
                ["widget"] = widget, ["public"] = true, ["privacy_text"] = en && t.PrivacyTextEn != "" ? t.PrivacyTextEn : t.PrivacyText, ["consent_text"] = en && t.ConsentTextEn != "" ? t.ConsentTextEn : t.ConsentText,
                ["text_channels"] = channels, ["intro"] = Tr.T(lang, INTRO[FirstStep(widget)]), ["reminder"] = TD.Core.Domain.Json.Truthy(ClientMails.Current(t)["reminder"]?["on"]), ["waitlist"] = true, ["offer"] = offer != null,
                ["codes"] = c.S.All<Location>("active", order: "name").Select(ClientMails.CodesLine).Where(x => x != "").Cast<object>().ToList(),
            });
        });

        app.MapGet("/book/{slug}/api/slots/", (Server s, HttpContext h, string slug) =>
        {
            var (t, no) = PublicHost.Business(s, slug, true); if (t == null) return no;
            try { s.Identity.Throttle("availability:" + t.Id, Address(s, h), 120, 60); } catch (UnauthorizedAccessException ex) { return Error(403, ex.Message); }
            string P(string k) => h.Request.Query.TryGetValue(k, out var v) ? v.ToString() : null;
            return Json(s, h, t, c =>
            {
                // the page of an offer's link sees the place kept for its client as free
                var offer = new Waitlist(c).Opened(P("attesa")); Waitlist.Own.Value = offer?.Entry.Id;
                JsonArray slots; try { slots = FreeSlots(c, P); } finally { Waitlist.Own.Value = null; }
                var answer = new JsonObject { ["slots"] = slots };
                // no time at all that day (closed, a holiday, the hours gone): nothing to wait for, no «Avvisami»
                if (slots.Count == 0) answer["closed"] = Closed(c, P);
                return Results.Json(answer);
            }, "Richiesta non valida.");
        });

        app.MapPost("/book/{slug}/api/submit/", async (Server s, HttpContext h, string slug) =>
        {
            // Anonymous capability, independent of third-party cookies in the embedded widget: never a signed-in user's rights
            var (t, no) = PublicHost.Business(s, slug, true); if (t == null) return no;
            var (ticket, refused) = Ticket(s, h, t); if (ticket == null) return refused;
            JsonObject data;
            try
            {
                s.Identity.Throttle("booking:" + t.Id, Address(s, h), 20, 3600); data = await Body(h);
                s.Identity.Throttle("book-email:" + t.Id, Crypto.TokenHash(Crypto.CaseFold(data["email"]?.ToString() ?? "")), 5, 3600);
            }
            catch (UnauthorizedAccessException ex) { return Error(403, ex.Message); }
            catch (BookingException ex) { return Error(400, ex.Message); }
            return Json(s, h, t, c =>
            {
                // a private service only from a page opened with its link
                var asked = new[] { data["service"] }.Concat(data["extra"] as JsonArray ?? new JsonArray()).Select(x => Bookings.AsId(x)).Where(x => x.HasValue).Select(x => x.Value).ToArray();
                var allowed = (ticket["private"] as JsonArray ?? new JsonArray()).Select(x => x.ToString()).ToHashSet();
                if (asked.Length > 0 && c.S.Query<Guid>("SELECT id FROM core_service WHERE secret<>'' AND id=ANY(@ids)", new { ids = asked }).Any(id => !allowed.Contains(id.ToString())))
                    throw new BookingException(Tr.T(c.Language, "Selezione non valida o non disponibile."));
                var bk = new Bookings(c);
                var (b, created) = bk.Create(data, origin: ticket.Str("origin") is var o && o != "" ? o : "public", source: SourceTag(ticket.Str("src")), wait: Guid.TryParse(ticket.Str("wait"), out var w) ? w : null);
                var own = $"/action/{t.Slug}/{bk.Data(b).Str("cancel_token")}/"; var service = c.Row<Service>(b.ServiceId);
                // the confirmed booking into the client's calendar (a request waits for the business's yes)
                JsonNode calendar = null; if (b.Status != "pending") { var links = CalendarLinks(c, b); links["ics"] = own + "calendario.ics"; calendar = links; }
                var answer = new JsonObject { ["ok"] = true, ["created"] = created, ["pending"] = b.Status == "pending", ["reference"] = b.Id.ToString()[..8], ["cancel"] = own, ["calendar"] = calendar };
                var thanks = service.ThanksUrl != "" ? service.ThanksUrl : t.ThanksUrl; if (!string.IsNullOrEmpty(thanks)) answer["thanks"] = thanks;
                return Results.Json(answer, statusCode: created ? 201 : 200);
            });
        });

        // «Avvisami se si libera»: the same checks as a booking sent from the page
        app.MapPost("/book/{slug}/api/attesa/", async (Server s, HttpContext h, string slug) =>
        {
            var (t, no) = PublicHost.Business(s, slug, true); if (t == null) return no;
            var (ticket, refused) = Ticket(s, h, t); if (ticket == null) return refused;
            JsonObject data;
            try
            {
                s.Identity.Throttle("booking:" + t.Id, Address(s, h), 20, 3600); data = await Body(h);
                s.Identity.Throttle("book-email:" + t.Id, Crypto.TokenHash(Crypto.CaseFold(data["email"]?.ToString() ?? "")), 5, 3600);
            }
            catch (UnauthorizedAccessException ex) { return Error(403, ex.Message); }
            catch (BookingException ex) { return Error(400, ex.Message); }
            return Json(s, h, t, c => { new Waitlist(c).AddOnline(data); return Results.Json(new JsonObject { ["ok"] = true }, statusCode: 201); });
        });
    }
}
