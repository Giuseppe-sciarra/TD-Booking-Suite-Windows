using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Web;

/// <summary>What other programs ask a business (core/api.py, cassa.bridge, wpconnect.py): the read-only API with its
/// key, the bridge of the cash register, and the WordPress plugin that connects and reads what to show.</summary>
public static partial class Public
{
    static string Bearer(HttpContext h) { var v = h.Request.Headers.Authorization.ToString(); return (v.StartsWith("Bearer ") ? v[7..] : v).Trim(); }

    /// <summary>endpoint + authorized: read-only, the key in Authorization: Bearer, at most 120 calls a minute per business.</summary>
    static IResult Api(Server s, HttpContext h, string slug, Func<Ctx, JsonObject> view)
    {
        var (t, no) = PublicHost.Business(s, slug, true); if (t == null) return no;
        if (h.Request.Method != "GET") return Error(405, "Solo lettura.");
        var key = Bearer(h);
        if (string.IsNullOrEmpty(t.ApiKey) || key == "" || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Crypto.TokenHash(key)), Encoding.ASCII.GetBytes(t.ApiKey))) return Error(403, "Chiave API non valida.");
        try { s.Identity.Throttle("api:" + t.Id, t.Id.ToString(), 120, 60); } catch (UnauthorizedAccessException ex) { return Error(429, ex.Message); }
        try { using var c = PublicHost.Ctx(s, h, t); return Results.Json(view(c)); }
        catch (Exception ex) when (ex is BookingException or FormatException or ArgumentException) { return Error(400, ex is BookingException && ex.Message != "" ? ex.Message : "Parametri non validi."); }
    }
    static DateOnly DayParam(HttpContext h, string name, DateOnly fallback) => h.Request.Query[name].ToString() is { Length: > 0 } v ? DateOnly.ParseExact(v, "yyyy-MM-dd") : fallback;

    /// <summary>wpconnect.config: public data the booking page shows anyway, for the plugin's list of shortcodes.</summary>
    static JsonObject WpConfig(Server s, Tenant t)
    {
        using var scope = s.Db.Tenant(t.Id); var portal = s.PublicOrigin.TrimEnd('/');
        var locations = scope.All<Location>("active", order: "name").ToList(); var names = locations.ToDictionary(l => l.Id, l => l.Name);
        var services = scope.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND l.active AND s.secret='' ORDER BY l.name, s.name").ToList();
        return new JsonObject
        {
            ["ok"] = true, ["tenant"] = t.Slug, ["name"] = t.Name, ["portal"] = portal, ["business_type"] = t.BusinessType, ["business_label"] = Presets.All.TryGetValue(t.BusinessType ?? "", out var p) ? p.Label : "",
            ["brand_color"] = t.BrandColor, ["booking_url"] = portal + "/book/" + t.Slug + "/",
            ["locations"] = new JsonArray(locations.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id.ToString(), ["name"] = l.Name, ["address"] = l.Address }).ToArray()),
            ["services"] = new JsonArray(services.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id.ToString(), ["name"] = x.Name, ["location"] = x.LocationId.ToString(), ["location_name"] = names[x.LocationId] }).ToArray()),
        };
    }

    static partial void MapWordPress(WebApplication app)
    {
        // ---------- the API of a business: bookings, services, free times
        app.Map("/book/{slug}/api/v1/bookings/", (Server s, HttpContext h, string slug) => Api(s, h, slug, c =>
        {
            var first = DayParam(h, "from", c.Today); var last = DayParam(h, "to", first.AddDays(30));
            if (last.DayNumber - first.DayNumber is < 0 or > 366) throw new BookingException("Intervallo non valido, massimo 367 giorni.");
            var a = c.Cal.Local(first, TimeSpan.Zero) ?? throw new FormatException(); var b = c.Cal.Local(last.AddDays(1), TimeSpan.Zero) ?? throw new FormatException(); var bk = new Bookings(c); var o = new JsonArray();
            foreach (var x in c.S.Query<Booking>("SELECT * FROM core_booking WHERE start<@b AND \"end\">@a ORDER BY start LIMIT 5000", new { a, b }))
            {
                var d = bk.Data(x);
                o.Add(new JsonObject
                {
                    ["id"] = x.Id.ToString(), ["status"] = x.Status, ["start"] = bk.Iso(x.Start), ["end"] = bk.Iso(x.End), ["service"] = x.ServiceId.ToString(), ["service_name"] = x.Snap.Str("service"), ["resource"] = x.ResourceId?.ToString() ?? "",
                    ["resource_name"] = x.Snap.Str("resource"), ["location"] = x.LocationId.ToString(), ["party"] = x.Party, ["origin"] = x.Origin, ["source"] = x.Source, ["price"] = x.Snap["total"]?.DeepClone() ?? "", ["created"] = bk.Iso(x.Created),
                    ["client"] = new JsonObject { ["first_name"] = d.Str("first_name"), ["last_name"] = d.Str("last_name"), ["email"] = d.Str("email"), ["phone"] = d.Str("phone") },
                });
            }
            return new JsonObject { ["bookings"] = o };
        }));
        app.Map("/book/{slug}/api/v1/services/", (Server s, HttpContext h, string slug) => Api(s, h, slug, c =>
        {
            var links = c.S.All<Serviceresource>().ToLookup(l => l.ServiceId); var o = new JsonArray();
            foreach (var x in c.S.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND l.active ORDER BY s.name"))
                o.Add(new JsonObject
                {
                    ["id"] = x.Id.ToString(), ["name"] = x.Name, ["kind"] = x.Kind, ["duration"] = x.Duration, ["price"] = x.Price.HasValue ? x.Price.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "",
                    ["location"] = x.LocationId.ToString(), ["location_name"] = c.Row<Location>(x.LocationId).Name,
                    ["resources"] = new JsonArray(links[x.Id].Select(l => c.Row<Resource>(l.ResourceId)).Where(r => r is { Active: true }).Select(r => (JsonNode)new JsonObject { ["id"] = r.Id.ToString(), ["name"] = r.Name }).ToArray()),
                });
            return new JsonObject { ["services"] = o };
        }));
        app.Map("/book/{slug}/api/v1/availability/", (Server s, HttpContext h, string slug) => Api(s, h, slug, c =>
        {
            JsonNode N(string k) => h.Request.Query[k].ToString() is { Length: > 0 } v ? JsonValue.Create(v) : null;
            var day = DateOnly.ParseExact(h.Request.Query["day"].ToString(), "yyyy-MM-dd");
            return new JsonObject { ["slots"] = new JsonArray(new Bookings(c).Available(N("service"), day, N("option"), N("resource"), N("party") ?? 1)
                .Select(x => (JsonNode)new JsonObject { ["start"] = x.Start, ["end"] = x.End, ["resource"] = x.Resource, ["resource_name"] = x.ResourceName, ["occurrence"] = x.Occurrence }).ToArray()) };
        }));

        // ---------- the bridge of a location's cash register: GET the next request, POST its answer
        app.Map("/book/{slug}/api/cassa/", async (Server s, HttpContext h, string slug) =>
        {
            var (t, no) = PublicHost.Business(s, slug, true); if (t == null) return no;
            using var c = PublicHost.Ctx(s, h, t); var key = Bearer(h);
            var loc = key.StartsWith("tdcassa_") ? c.S.One<Location>("SELECT * FROM core_location WHERE till_token=@k", new { k = Crypto.TokenHash(key) }) : null;
            var device = loc != null ? Till.Conf(loc).Str("device") : "";
            if (device == "") return Error(403, "Codice del ponte non valido.");
            try { s.Identity.Throttle("till:" + loc.Id, loc.Id.ToString(), 120, 60); } catch (UnauthorizedAccessException ex) { return Error(429, ex.Message); }
            c.S.Execute("UPDATE core_location SET till_seen=@n WHERE id=@id", new { n = c.Now, id = loc.Id }); var till = new Till(c);
            if (h.Request.Method == "GET")
            {
                var sale = till.Claim(loc); JsonNode job = null;
                if (sale != null)
                {
                    var j = till.JobOf(sale, loc); var o = new JsonObject { ["id"] = j.Id.ToString(), ["attempt"] = j.Attempt };
                    if (j.Commands != null && j.Url == null) { o["serial"] = new JsonObject { ["port"] = j.Port, ["baud"] = j.Baud, ["framing"] = j.Framing }; o["commands"] = new JsonArray(j.Commands.Select(x => (JsonNode)x).ToArray()); }
                    else { o["url"] = j.Url; o["body"] = j.Body; }
                    job = o;
                }
                c.Commit(); return Results.Json(new JsonObject { ["job"] = job });
            }
            if (h.Request.Method != "POST") return Error(405, "Metodo non consentito.");
            try
            {
                var r = await JsonNode.ParseAsync(h.Request.Body) as JsonObject ?? throw new FormatException();
                var sale = Guid.TryParse(r.Str("id"), out var id) ? c.S.One<Sale>("SELECT * FROM core_sale WHERE id=@id AND location_id=@l", new { id, l = loc.Id }) : null; if (sale == null) throw new FormatException();
                int Int(string k) => r[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : int.TryParse(r.Str(k), out var m) ? m : 0;
                var (state, info) = Till.Outcome(device, new Till.Sent(r.Str("error") is { Length: > 0 } e ? e : null, r.Str("detail"), r.Str("serial") is { Length: > 0 } ser ? ser : null, Int("at"), Int("status"), r.Str("body")));
                sale = till.Finish(sale.Id, state, info, r.Str("attempt")); c.Commit();
                return Results.Json(new JsonObject { ["status"] = sale.Status });
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { return Error(400, "Risposta non valida."); }
        });

        // ---------- the WordPress plugin
        app.MapGet("/book/{slug}/api/config/", (Server s, string slug) => { var (t, no) = PublicHost.Business(s, slug, true); return t == null ? no : Results.Json(WpConfig(s, t)); });
        app.MapPost("/api/wordpress/connect/", async (Server s, HttpContext h) =>
        {
            static IResult Fail(string text, int status = 400) => Results.Json(new JsonObject { ["ok"] = false, ["error"] = text }, statusCode: status);
            try { s.Identity.Throttle("wp-connect", Address(s, h), 20, 3600); } catch (UnauthorizedAccessException) { return Fail("Troppi tentativi: riprova tra un'ora.", 429); }
            JsonObject data; try { data = await JsonNode.ParseAsync(h.Request.Body) as JsonObject; } catch (JsonException) { data = null; }
            if (data == null) return Fail("Richiesta non valida.");
            var code = new string(data.Str("code").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (code.Length != 16) return Fail("Il codice di collegamento non è valido.");
            var site = WpSync.ValidOrigin(data.Str("site"));
            if (site == "") return Fail("L'indirizzo del sito deve essere https://dominio, senza percorsi.");
            // one use only, also when two requests arrive together: the row stays locked until the code is spent
            Tenant t;
            using (var g = s.Db.Global())
            {
                t = g.One<Tenant>("SELECT * FROM core_tenant WHERE connect_hash=@h AND connect_until>@n FOR UPDATE", new { h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("tdbs-connect:" + code))).ToLowerInvariant(), n = DateTime.UtcNow });
                if (t == null) return Fail("Codice non valido o scaduto: generane uno nuovo dal portale.", 403);
                var origins = (JsonNode.Parse(string.IsNullOrEmpty(t.EmbedOrigins) ? "[]" : t.EmbedOrigins) as JsonArray ?? new JsonArray()).Select(o => o.ToString()).ToList();
                if (!origins.Contains(site)) { if (origins.Count >= 10) return Fail("L'attività ha già 10 siti collegati: togline uno dalle impostazioni.", 409); origins.Add(site); }
                g.Execute("UPDATE core_tenant SET embed_origins=@o::jsonb, connect_hash='', connect_until=NULL WHERE id=@id", new { o = JsonSerializer.Serialize(origins), id = t.Id });
                g.Commit();
            }
            using (var c = PublicHost.Ctx(s, h, t)) { c.Audit("wordpress.connected", t.Id, new { site }); c.Commit(); }
            return Results.Json(WpConfig(s, t));
        });
    }
}
