using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>What Google or Microsoft answered when it is not a yes (gcal.GoogleError): status 0 is our own side
/// (unreachable, no answer), the reason is theirs (invalid_grant as it is, for the tokens).</summary>
public sealed class CalendarError(int status, string reason = "") : Exception(reason != "" ? reason : $"http_{status}")
{
    public int Status { get; } = status; public string Reason { get; } = reason;
}

/// <summary>feeds.py, the way in: an outside calendar (Google, Outlook, iCloud: its secret .ics address) read every 15
/// minutes into the operator's Occupato blocks, without titles or people.</summary>
public static class OutsideCalendar
{
    public static readonly TimeSpan EVERY = TimeSpan.FromMinutes(15), AHEAD = TimeSpan.FromDays(180);
    static readonly string[] WEEKDAYS = { "MO", "TU", "WE", "TH", "FR", "SA", "SU" };
    static readonly CultureInfo INV = CultureInfo.InvariantCulture;

    /// <summary>A moment as the calendar wrote it: the time on the wall and the zone it is told in.</summary>
    public readonly record struct At(DateTime Wall, TimeZoneInfo Zone)
    {
        public DateTime Utc => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(Zone.IsInvalidTime(Wall) ? Wall.AddHours(1) : Wall, DateTimeKind.Unspecified), Zone);
    }

    /// <summary>moment: a DTSTART / DTEND: a day (all day, at midnight of the business), UTC, with its TZID, or floating.</summary>
    public static At Moment(string value, string pars, TimeZoneInfo tz)
    {
        var up = pars.ToUpperInvariant();
        if (up.Contains("VALUE=DATE") && !up.Contains("DATE-TIME") || value.Length == 8) return new(DateTime.ParseExact(value[..8], "yyyyMMdd", INV), tz);
        if (value.EndsWith('Z')) return new(DateTime.ParseExact(value, "yyyyMMdd'T'HHmmss'Z'", INV), TimeZoneInfo.Utc);
        var zone = tz;
        if (Regex.Match(pars, "TZID=\"?([^;:\"]+)") is { Success: true } m)
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(m.Groups[1].Value); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }  // a name we do not know: the business's time
        return new(DateTime.ParseExact(value[..15], "yyyyMMdd'T'HHmmss", INV), zone);
    }
    public static TimeSpan Duration(string value)
    {
        var m = Regex.Match(value, @"^P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?\z"); if (!m.Success) throw new FormatException("durata");
        int N(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return new TimeSpan(N(1) * 7 + N(2), N(3), N(4), N(5));
    }
    /// <summary>repeat: the starts of a repeated event until last.
    /// shortcut: DAILY and WEEKLY (with BYDAY) only; MONTHLY and YEARLY count once, add them if someone needs them.</summary>
    public static List<At> Repeat(At start, string rule, DateTime last)
    {
        var r = rule.ToUpperInvariant().Split(';').Where(x => x.Contains('=')).Select(x => x.Split('=', 2)).GroupBy(x => x[0]).ToDictionary(x => x.Key, x => x.Last()[1]);
        if (r.GetValueOrDefault("FREQ") is not ("DAILY" or "WEEKLY")) return new() { start };
        var step = Math.Max(int.TryParse(r.GetValueOrDefault("INTERVAL"), out var i0) ? i0 : 1, 1); var count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c0) ? c0 : 0;
        if (r.GetValueOrDefault("UNTIL") is { Length: > 0 } until) { var end = Moment(until, "", start.Zone).Utc.AddDays(until.Length == 8 ? 1 : 0); if (end < last) last = end; }
        var days = (r.GetValueOrDefault("BYDAY") ?? "").Split(',').Where(x => x.Length >= 2 && WEEKDAYS.Contains(x[^2..])).Select(x => Array.IndexOf(WEEKDAYS, x[^2..])).Distinct().OrderBy(x => x).ToList();
        var weekday = ((int)start.Wall.DayOfWeek + 6) % 7; if (days.Count == 0) days.Add(weekday);
        var monday = start.Wall.Date.AddDays(-weekday); var o = new List<At>();
        for (int i = 0; i < 3000; i++)
            foreach (var d in r["FREQ"] == "DAILY" ? new[] { start.Wall.Date.AddDays(i * step) } : days.Select(x => monday.AddDays(7 * i * step + x)))
            {
                var when = new At(d + start.Wall.TimeOfDay, start.Zone);
                if (when.Utc < start.Utc) continue;
                if (when.Utc > last || count > 0 && o.Count >= count) return o;
                o.Add(when);
            }
        return o;
    }

    /// <summary>events: (start, end) of the busy events from now on; the free (TRANSP:TRANSPARENT) and cancelled ones are
    /// left out, and so are the dates a repeated event dropped (EXDATE) or moved to another event (RECURRENCE-ID).</summary>
    public static List<(DateTime Start, DateTime End)> Events(string text, TimeZoneInfo tz, DateTime now)
    {
        var found = new List<(Dictionary<string, (string P, string V)> Keys, List<(string P, string V)> Ex)>(); Dictionary<string, (string, string)> ev = null; List<(string, string)> ex = null; var inner = 0;
        var last = now + AHEAD; var o = new List<(DateTime, DateTime)>();
        foreach (var line in Regex.Replace(text, "\r?\n[ \t]", "").Split('\n'))
        {
            var cut = line.IndexOf(':'); var name = cut < 0 ? line : line[..cut]; var value = (cut < 0 ? "" : line[(cut + 1)..]).Trim();
            var semi = name.IndexOf(';'); var key = (semi < 0 ? name : name[..semi]).Trim().ToUpperInvariant(); var pars = semi < 0 ? "" : name[(semi + 1)..];
            if (key == "BEGIN") { if (value.ToUpperInvariant() == "VEVENT") { ev = new(); ex = new(); } else if (ev != null) inner++; }  // VALARM inside: its DURATION is not the event's
            else if (key == "END" && ev != null) { if (inner > 0) inner--; else { found.Add((ev, ex)); ev = null; } }
            else if (ev != null && inner == 0) { if (key == "EXDATE") ex.AddRange(value.Split(',').Select(x => (pars, x))); else ev[key] = (pars, value); }
        }
        string V(Dictionary<string, (string P, string V)> e, string k) => e.TryGetValue(k, out var x) ? x.V : "";
        var moved = new HashSet<(string, DateTime)>();
        foreach (var (e, _) in found)
            try { if (e.TryGetValue("RECURRENCE-ID", out var rid)) moved.Add((V(e, "UID"), Moment(rid.V, rid.P, tz).Utc)); } catch (Exception x) when (x is FormatException or ArgumentException) { }
        foreach (var (e, exdates) in found)
            try
            {
                if (!e.TryGetValue("DTSTART", out var ds) || V(e, "TRANSP").ToUpperInvariant() == "TRANSPARENT" || V(e, "STATUS").ToUpperInvariant() == "CANCELLED") continue;
                var start = Moment(ds.V, ds.P, tz);
                var length = e.TryGetValue("DTEND", out var de) ? Moment(de.V, de.P, tz).Utc - start.Utc : e.ContainsKey("DURATION") ? Duration(V(e, "DURATION")) : ds.V.Length == 8 ? TimeSpan.FromDays(1) : TimeSpan.Zero;
                var repeated = e.ContainsKey("RRULE"); var skip = new HashSet<DateTime>();
                if (repeated) { foreach (var (p, v) in exdates) skip.Add(Moment(v, p, tz).Utc); foreach (var (u, m) in moved) if (u == V(e, "UID")) skip.Add(m); }
                foreach (var s in repeated ? Repeat(start, V(e, "RRULE"), last) : new List<At> { start })
                    if (s.Utc + length > now && s.Utc < last && length > TimeSpan.Zero && !skip.Contains(s.Utc)) o.Add((s.Utc, s.Utc + length));
            }
            catch (Exception x) when (x is FormatException or ArgumentException or KeyNotFoundException) { }  // one event we cannot read does not stop the others
        return o;
    }

    /// <summary>blocks: the busy spans as blocks of the operator's days (an end not after the start goes to the next day).</summary>
    public static List<(DateOnly Day, TimeSpan Start, TimeSpan End)> Blocks(IEnumerable<(DateTime Start, DateTime End)> spans, TimeZoneInfo tz)
    {
        var o = new List<(DateOnly, TimeSpan, TimeSpan)>();
        foreach (var (s0, e0) in spans)
        {
            var s = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s0, DateTimeKind.Utc), tz); var e = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e0, DateTimeKind.Utc), tz);
            for (var day = s.Date; day < e; day = day.AddDays(1))
            {
                var a = s > day ? s : day; var z = e < day.AddDays(1) ? e : day.AddDays(1);
                if (a < z) o.Add((DateOnly.FromDateTime(day), a.TimeOfDay, z.Date == day ? z.TimeOfDay : TimeSpan.Zero));
            }
        }
        return o;
    }

    /// <summary>check: only https (webcal is https), only toward public addresses, as the SMTP.
    /// shortcut: the name is resolved again by the request; a DNS that changes between the two is not caught.</summary>
    static Uri Check(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host == "") throw new ArgumentException("L'indirizzo deve iniziare con https:// o webcal://");
        IPAddress[] found; try { found = Dns.GetHostAddresses(u.Host); } catch (System.Net.Sockets.SocketException) { throw new ArgumentException("Calendario non raggiungibile."); }
        if (found.Length == 0 || found.Any(a => !Network.Global(a))) throw new ArgumentException("Indirizzo non raggiungibile.");
        return u;
    }
    /// <summary>fetch: the calendar's text, at most 5 MB; every redirect is checked like the first address.</summary>
    public static Func<string, string> Fetch = url =>
    {
        var u = Check(Regex.Replace(url.Trim(), "^webcal://", "https://", RegexOptions.IgnoreCase));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("TDBookingSuite");
        for (int hop = 0; hop < 6; hop++)
        {
            using var res = http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } next) { u = Check(new Uri(u, next).ToString()); continue; }
            if (!res.IsSuccessStatusCode) throw new ArgumentException($"Il calendario ha risposto con un errore {(int)res.StatusCode}.");
            using var stream = res.Content.ReadAsStream(); using var ms = new MemoryStream(); var buffer = new byte[81920]; int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) { ms.Write(buffer, 0, n); if (ms.Length > 5_000_000) throw new ArgumentException("Il calendario è troppo grande (oltre 5 MB)."); }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        throw new ArgumentException("Troppi rimandi: controlla l'indirizzo del calendario.");
    };

    /// <summary>The blocks read from outside (busy, google, outlook), replaced each time.</summary>
    public static void Replace(Ctx c, Resource r, string reason, IEnumerable<(DateTime Start, DateTime End)> spans)
    {
        c.S.Execute("DELETE FROM core_timeblock WHERE resource_id=@r AND reason=@k", new { r = r.Id, k = reason });
        foreach (var (day, a, z) in Blocks(spans, c.Zone).Take(3000))
            c.S.Insert(new Timeblock { TenantId = c.Tenant.Id, ResourceId = r.Id, Day = day.ToDateTime(TimeOnly.MinValue), Start = a, End = z, Reason = reason, Created = c.Now });
    }

    /// <summary>read: one outside calendar into the operator's Occupato blocks; an error is kept for the page.</summary>
    public static bool Read(Ctx c, Resource r)
    {
        List<(DateTime, DateTime)> spans;
        try { spans = Events(Fetch(c.DecryptText(r.Busy, "busy:" + r.Id)), c.Zone, c.Now); }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or IOException or TaskCanceledException or FormatException)
        {
            var text = ex is ArgumentException ? ex.Message : "Calendario non raggiungibile."; r.BusyError = text.Length > 200 ? text[..200] : text; r.BusyAt = c.Now; c.S.Update(r, "busy_error", "busy_at"); return false;
        }
        Replace(c, r, "busy", spans); r.BusyError = ""; r.BusyAt = c.Now; c.S.Update(r, "busy_error", "busy_at");
        return true;
    }

    /// <summary>The three refresh() of the worker: every outside calendar, Google and Outlook connection that is due.</summary>
    public static int Refresh(Db db, Crypto crypto, string origin, IOutbox outbox, string edition = "private")
    {
        var done = 0; List<Guid> tenants; using (var g = db.Global()) tenants = g.Query<Guid>("SELECT id FROM core_tenant").ToList();
        foreach (var id in tenants)
        {
            List<Guid> busy, google, outlook; bool hasGoogle, hasOutlook;
            using (var c = new Ctx(db, crypto, id) { Edition = edition })
            {
                if (!c.Operational()) continue;
                hasGoogle = !string.IsNullOrEmpty(c.Tenant.Google); hasOutlook = !string.IsNullOrEmpty(c.Tenant.Microsoft);
                busy = c.S.Query<Guid>("SELECT id FROM core_resource WHERE busy<>'' AND (busy_at IS NULL OR busy_at<=@t)", new { t = c.Now - EVERY }).ToList();
                google = hasGoogle ? c.S.Query<Guid>("SELECT id FROM core_resource WHERE google<>'' AND (google_at IS NULL OR google_at<=@t)", new { t = c.Now - GoogleCalendar.EVERY }).ToList() : new();
                outlook = hasOutlook ? c.S.Query<Guid>("SELECT id FROM core_resource WHERE microsoft<>'' AND (microsoft_at IS NULL OR microsoft_at<=@t)", new { t = c.Now - GoogleCalendar.EVERY }).ToList() : new();
            }
            // one operator a transaction: a calendar that fails does not undo the others
            foreach (var (list, kind) in new[] { (busy, 0), (google, 1), (outlook, 2) })
                foreach (var rid in list)
                {
                    using var c = new Ctx(db, crypto, id) { PublicOrigin = origin, Out = outbox, Edition = edition }; var r = c.S.Get<Resource>(rid); if (r == null) continue;
                    if (kind == 0 ? Read(c, r) : kind == 1 ? new GoogleCalendar(c).Sync(r) : new OutlookCalendar(c).Sync(r)) done++;
                    c.Commit();
                }
        }
        return done;
    }
}

/// <summary>What gcal.py and mscal.py share: the calls, the consent page's state and PKCE, the tokens' refresh.</summary>
public abstract class OperatorCalendar(Ctx c)
{
    protected readonly Ctx c = c;
    public static readonly TimeSpan AHEAD = TimeSpan.FromDays(180);
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    protected static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>call: one call to Google or Microsoft; its JSON answer. An error carries their reason.</summary>
    public static Func<string, string, JsonNode, string, Dictionary<string, string>, JsonObject> Call = (method, url, body, token, form) =>
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (form != null) req.Content = new FormUrlEncodedContent(form);
        else if (body != null) req.Content = new StringContent(body.ToJsonString(Plain), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new("Bearer", token);
        int status; JsonObject data;
        try
        {
            using var res = http.Send(req); using var reader = new StreamReader(res.Content.ReadAsStream()); var raw = reader.ReadToEnd(); status = (int)res.StatusCode;
            try { data = JsonNode.Parse(raw == "" ? "{}" : raw) as JsonObject ?? new JsonObject(); } catch (JsonException) { data = new JsonObject(); }
        }
        catch (HttpRequestException ex) { throw new CalendarError(0, "Servizio non raggiungibile: " + Cut(ex.InnerException?.Message ?? ex.Message, 160)); }
        catch (Exception ex) when (ex is IOException or TaskCanceledException) { throw new CalendarError(0, "Il servizio non ha risposto: " + Cut(ex.Message, 160)); }
        if (status >= 300) throw new CalendarError(status, data.Str("error") == "invalid_grant" ? "invalid_grant"
            : Cut(data["error"] is JsonObject e ? (e.Str("message") != "" ? e.Str("message") : e.Str("code")) : data.Str("error_description") != "" ? data.Str("error_description") : data.Str("error"), 200));
        return data;
    };
    protected static string Cut(string s, int n) => (s ?? "").Length > n ? s[..n] : s ?? "";
    protected static string Query(params (string K, string V)[] kv) => string.Join("&", kv.Select(x => Uri.EscapeDataString(x.K) + "=" + Uri.EscapeDataString(x.V)));
    protected static double Stamp(DateTime utc) => (DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
    protected static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    protected static string Zulu(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    protected static DateTime Parse(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime;
    protected static double Num(JsonNode n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    /// <summary>The verifier of the consent page and its challenge (PKCE, S256).</summary>
    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Bookings.TokenUrlSafe(48);
        return (verifier, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }
    protected string What(Booking b, Resource r)
    {
        var what = b.Snap.Str("service") is var sv && sv != "" ? sv : c.Row<Service>(b.ServiceId).Name;
        if (r.FeedNames) { var d = new Bookings(c).Data(b); what += " · " + string.Join(" ", new[] { d.Str("first_name"), d.Str("last_name") }.Where(x => x != "")); }
        return what.Trim(' ', '·');
    }
    protected List<Booking> Mine(Resource r) =>
        c.S.Query<Booking>("SELECT * FROM core_booking WHERE resource_id=@r AND status=ANY(@st) AND \"end\">=@a AND start<=@b", new { r = r.Id, st = Bookings.ACCEPTED, a = c.Now, b = c.Now + AHEAD }).ToList();
    /// <summary>Among the bookings no longer wanted, the ones ended as planned: they stay in his calendar, as history.</summary>
    protected HashSet<string> Ended(Resource r, IEnumerable<string> gone) =>
        c.S.Query<Guid>("SELECT id FROM core_booking WHERE id=ANY(@ids) AND resource_id=@r AND status=ANY(@st) AND \"end\"<@n",
            new { ids = gone.Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).ToArray(), r = r.Id, st = Bookings.ACCEPTED, n = c.Now }).Select(x => x.ToString()).ToHashSet();
    /// <summary>The spans kept in the state, without the past and the too far: the far ones come back with the daily full read.</summary>
    protected JsonObject Near(Dictionary<string, JsonNode> seen)
    {
        var o = new JsonObject();
        foreach (var (k, v) in seen) if (Parse(v[1].ToString()) > c.Now && Parse(v[0].ToString()) < c.Now + AHEAD) o[k] = v.DeepClone();
        return o;
    }
    protected static bool Same(JsonObject a, JsonObject b) => a.Count == b.Count && a.All(x => b[x.Key] is JsonNode y && y.ToJsonString() == x.Value.ToJsonString());
    protected static IEnumerable<(DateTime, DateTime)> Spans(JsonObject events) => events.Select(x => (Parse(x.Value[0].ToString()), Parse(x.Value[1].ToString())));
}

/// <summary>gcal.py: Google Calendar, both ways, for each operator. Every 5 minutes his events become «Occupato su
/// Google» blocks, without titles, and his bookings are written there: service and time, the client's name only when
/// the business chose it for his feed.</summary>
public sealed class GoogleCalendar(Ctx c) : OperatorCalendar(c)
{
    const string AUTH = "https://accounts.google.com/o/oauth2/v2/auth", TOKEN = "https://oauth2.googleapis.com/token", REVOKE = "https://oauth2.googleapis.com/revoke",
        CALENDARS = "https://www.googleapis.com/calendar/v3/users/me/calendarList", SCOPE = "https://www.googleapis.com/auth/calendar.events";
    public const string PATH = "/oauth/google-calendar/";
    public static readonly TimeSpan EVERY = TimeSpan.FromMinutes(4);  // the worker looks every 5 minutes
    static string EventsUrl(string cal) => "https://www.googleapis.com/calendar/v3/calendars/" + Uri.EscapeDataString(string.IsNullOrEmpty(cal) ? "primary" : cal) + "/events";

    public JsonObject Config() => string.IsNullOrEmpty(c.Tenant.Google) ? new JsonObject() : c.Decrypt(c.Tenant.Google, "google");
    public JsonObject State(Resource r) => string.IsNullOrEmpty(r.Google) ? new JsonObject() : c.Decrypt(r.Google, "google:" + r.Id);
    void Keep(Resource r, JsonObject st, DateTime? at, string error)
    {
        r.Google = st.Count > 0 ? c.Encrypt(st, "google:" + r.Id) : ""; r.GoogleAt = at; r.GoogleError = Cut(error, 200); c.S.Update(r, "google", "google_at", "google_error");
    }

    /// <summary>start: Google's consent page, with a state and a PKCE challenge.</summary>
    public string Start(string uri, string state, string challenge) => AUTH + "?" + Query(("client_id", Config().Str("client_id")), ("redirect_uri", uri), ("response_type", "code"), ("scope", SCOPE),
        ("access_type", "offline"), ("prompt", "consent"), ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"));

    /// <summary>finish: Google's answer: the operator connected, or the error on his card.</summary>
    public void Finish(Resource r, string code, string uri, string verifier, string denied)
    {
        var conf = Config(); var error = ""; JsonObject t = null;
        if (denied != "" || conf.Count == 0) error = "Google non ha dato l'accesso: " + Cut(denied != "" ? denied : "client OAuth tolto", 60) + ".";
        else
        {
            var body = new Dictionary<string, string> { ["code"] = code, ["client_id"] = conf.Str("client_id"), ["redirect_uri"] = uri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier };
            if (conf.Str("client_secret") != "") body["client_secret"] = conf.Str("client_secret");
            try { t = Call("POST", TOKEN, null, null, body); if (t.Str("refresh_token") == "") throw new CalendarError(400, "Google non ha dato l'accesso permanente: riprova."); }
            catch (CalendarError ex) { error = Message(ex); }
        }
        if (error != "") { r.GoogleError = Cut(error, 200); c.S.Update(r, "google_error"); return; }
        Keep(r, new JsonObject { ["refresh"] = t.Str("refresh_token"), ["access"] = t.Str("access_token"), ["expires"] = Stamp(c.Now) + (Num(t["expires_in"]) is > 0 and var n ? n : 3600) }, null, "");
    }
    static string Message(CalendarError ex) =>
        ex.Reason == "invalid_grant" ? "Google ha chiuso il collegamento (revocato o scaduto): collegalo di nuovo." : ex.Status == 0 ? ex.Reason
        : ex.Status == 403 ? "Google rifiuta: controlla che la Google Calendar API sia attiva nel progetto (" + Cut(ex.Reason, 80) + ")." : $"Google ha risposto con un errore {ex.Status}" + (ex.Reason != "" ? ": " + Cut(ex.Reason, 120) : ".");

    /// <summary>access: a valid access token, refreshed when it is about to end.</summary>
    string Access(JsonObject conf, JsonObject st)
    {
        if (st.Str("access") != "" && Num(st["expires"]) > Stamp(c.Now) + 60) return st.Str("access");
        var body = new Dictionary<string, string> { ["client_id"] = conf.Str("client_id"), ["refresh_token"] = st.Str("refresh"), ["grant_type"] = "refresh_token" };
        if (conf.Str("client_secret") != "") body["client_secret"] = conf.Str("client_secret");
        var t = Call("POST", TOKEN, null, null, body); if (t.Str("access_token") == "") throw new FormatException();
        st["access"] = t.Str("access_token"); st["expires"] = Stamp(c.Now) + (Num(t["expires_in"]) is > 0 and var n ? n : 3600);
        return st.Str("access");
    }

    /// <summary>busy: [start, end] of an event that keeps the operator busy; null for the free, the cancelled and the
    /// bookings we wrote.</summary>
    JsonArray Busy(JsonObject e)
    {
        if (e.Str("status") == "cancelled" || e.Str("transparency") == "transparent" || e["extendedProperties"]?["private"]?["tdb_booking_id"] != null) return null;
        try
        {
            DateTimeOffset a, b;
            if (e["start"]?["dateTime"] != null) { a = DateTimeOffset.Parse(e["start"]["dateTime"].ToString(), CultureInfo.InvariantCulture); b = DateTimeOffset.Parse(e["end"]["dateTime"].ToString(), CultureInfo.InvariantCulture); }
            else
            {
                // all day, in the business's days
                DateTimeOffset Day(JsonNode x) { var d = DateTime.ParseExact(x["date"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture); return new DateTimeOffset(d, c.Zone.GetUtcOffset(d)); }
                a = Day(e["start"]); b = Day(e["end"]);
            }
            return b > a ? new JsonArray(Iso(a), Iso(b)) : null;
        }
        catch (Exception ex) when (ex is FormatException or NullReferenceException or ArgumentException) { return null; }
    }
    static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>listing: the events of a calendar changed since its syncToken, or all from since without one; null when
    /// Google no longer knows the token (410).</summary>
    (List<JsonObject> Items, string Sync, string Name)? Listing(string token, string cal, string sync, string since)
    {
        var items = new List<JsonObject>(); var page = "";
        while (true)
        {
            var q = new List<(string, string)> { ("singleEvents", "true"), ("maxResults", "2500"), sync != "" ? ("syncToken", sync) : ("timeMin", since) }; if (page != "") q.Add(("pageToken", page));
            JsonObject r;
            try { r = Call("GET", EventsUrl(cal) + "?" + Query(q.ToArray()), null, token, null); }
            catch (CalendarError ex) when (ex.Status == 410 && sync != "") { return null; }
            items.AddRange((r["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>());
            page = r.Str("nextPageToken"); if (page == "") return (items, r.Str("nextSyncToken"), r.Str("summary"));
        }
    }

    /// <summary>read: his events into the blocks: the first time (and once a day, for the far ones coming closer) all from
    /// yesterday on, then only the changes since the last syncToken; a token Google no longer knows means all again.
    /// Our events changed in Google go to the desk as a warning (Changed).</summary>
    void Read(Resource r, string token, JsonObject st)
    {
        var before = st["events"] as JsonObject ?? new JsonObject(); var cal = st.Str("read") is var rc && rc != "" ? rc : "primary"; var wcal = st.Str("write") is var wc && wc != "" ? wc : "primary";
        var full = st.Str("sync") == "" || Num(st["full"]) < Stamp(c.Now) - 86400; var since = Zulu(c.Now.AddDays(-1));
        var got = Listing(token, cal, full ? "" : st.Str("sync"), since);
        if (got == null) { st.Remove("sync"); got = Listing(token, cal, "", since); full = true; }
        var (items, sync, account) = got.Value; st["sync"] = sync;
        var seen = full ? new Dictionary<string, JsonNode>() : before.ToDictionary(x => x.Key, x => x.Value.DeepClone());
        foreach (var e in items) { var id = e.Str("id"); if (Busy(e) is { } span) seen[id] = span; else seen.Remove(id); }
        var events = Near(seen); st["events"] = events; if (account != "") st["account"] = account; else if (st["account"] == null) st["account"] = "";
        if (full) st["full"] = Stamp(c.Now);
        if (full || !Same(events, before)) OutsideCalendar.Replace(c, r, "google", Spans(events));
        if (wcal != cal)
        {
            // the bookings go to another calendar: its changes read on their own
            var other = Listing(token, wcal, st.Str("wsync"), since) ?? Listing(token, wcal, "", since); items = other.Value.Items; st["wsync"] = other.Value.Sync;
        }
        Changed(r, items, st);
    }

    /// <summary>changed: our events moved or deleted in Google: a warning on the booking ("google" in its snapshot), never
    /// applied; it goes away when the event is as the booking again.</summary>
    void Changed(Resource r, List<JsonObject> items, JsonObject st)
    {
        var ours = (st["written"] as JsonObject ?? new JsonObject()).ToDictionary(x => EventId(x.Key, r), x => x.Key);
        var found = new Dictionary<Guid, JsonObject>(); foreach (var e in items) if (ours.TryGetValue(e.Str("id"), out var pk) && Guid.TryParse(pk, out var g)) found[g] = e;
        if (found.Count == 0) return;
        foreach (var b in c.S.Query<Booking>("SELECT * FROM core_booking WHERE id=ANY(@ids) AND status=ANY(@st)", new { ids = found.Keys.ToArray(), st = Bookings.ACCEPTED }).ToList())
        {
            var e = found[b.Id]; var note = "deleted";
            if (e.Str("status") != "cancelled")
            {
                DateTimeOffset? a = null, z = null;
                try { a = DateTimeOffset.Parse(e["start"]["dateTime"].ToString(), CultureInfo.InvariantCulture); z = DateTimeOffset.Parse(e["end"]["dateTime"].ToString(), CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is FormatException or NullReferenceException) { a = z = null; }  // turned into an all-day event
                note = a?.UtcDateTime == b.Start && z?.UtcDateTime == b.End ? "" : "moved:" + (a.HasValue ? Iso(a.Value) : "");
            }
            var snap = b.Snap; if (snap.Str("google") == note) continue;
            if (note != "") snap["google"] = note; else snap.Remove("google");
            b.Snapshot = Json.Dump(snap); c.S.Update(b, "snapshot");
        }
    }

    /// <summary>calendars: his calendars, to choose on his card which one is read and which one gets the bookings.</summary>
    void Calendars(string token, JsonObject st)
    {
        var r = Call("GET", CALENDARS + "?" + Query(("minAccessRole", "reader"), ("maxResults", "250")), null, token, null);
        st["calendars"] = new JsonArray((r["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(x => x.Str("id") != "").Take(250)
            .Select(x => (JsonNode)new JsonArray(x.Str("id"), x.Str("summaryOverride") != "" ? x.Str("summaryOverride") : x.Str("summary") != "" ? x.Str("summary") : x.Str("id"), x.Str("accessRole") is "owner" or "writer")).ToArray());
    }
    /// <summary>choose: the calendars chosen on his card, among his; a new read calendar is read whole, a new write
    /// calendar gets the bookings at the next run (and they leave the old one).</summary>
    public void Choose(Resource r, string read, string write)
    {
        var st = State(r); var known = (st["calendars"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().ToDictionary(x => x[0].ToString(), x => x[2]?.GetValueKind() == JsonValueKind.True);
        if (!known.ContainsKey(read) && read != "primary" || write != "primary" && !known.GetValueOrDefault(write)) throw new BookingException("Scegli uno dei calendari dell'elenco.");
        if (read != (st.Str("read") is var a && a != "" ? a : "primary")) st.Remove("sync");
        if (write != (st.Str("write") is var b && b != "" ? b : "primary")) st.Remove("wsync");
        st["read"] = read; st["write"] = write; Keep(r, st, r.GoogleAt, r.GoogleError);
    }

    /// <summary>The same every time: an insert retried after a timeout answers 409 instead of doubling.</summary>
    string EventId(string bookingId, Resource r) => "tdb" + Sha($"{c.Tenant.Id}:{bookingId}:{r.Id}")[..40];
    JsonObject Event(Booking b, Resource r) => new()
    {
        ["summary"] = What(b, r), ["start"] = new JsonObject { ["dateTime"] = Times.PyIso(b.Start), ["timeZone"] = c.Tenant.Timezone }, ["end"] = new JsonObject { ["dateTime"] = Times.PyIso(b.End), ["timeZone"] = c.Tenant.Timezone },
        ["transparency"] = "opaque", ["status"] = "confirmed", ["extendedProperties"] = new JsonObject { ["private"] = new JsonObject { ["tdb_booking_id"] = b.Id.ToString() } },
    };
    /// <summary>put: patch what we wrote, insert the rest; 409 on insert means it is there already (a retry, or deleted in
    /// Google: the patch with status confirmed brings it back), 404 on patch that it is gone: inserted again.</summary>
    static void Put(string token, string eid, JsonObject body, bool known, string cal)
    {
        var url = $"{EventsUrl(cal)}/{eid}?sendUpdates=none";
        if (known) try { Call("PATCH", url, body, token, null); return; } catch (CalendarError ex) when (ex.Status is 404 or 410) { }
        var insert = (JsonObject)body.DeepClone(); insert["id"] = eid;
        try { Call("POST", EventsUrl(cal) + "?sendUpdates=none", insert, token, null); } catch (CalendarError ex) when (ex.Status == 409) { Call("PATCH", url, body, token, null); }
    }
    static void Delete(string token, string cal, string eid) { try { Call("DELETE", $"{EventsUrl(cal)}/{eid}?sendUpdates=none", null, token, null); } catch (CalendarError ex) when (ex.Status is 404 or 410) { } }

    /// <summary>write: his bookings from now on as events: new ones inserted, changed ones patched, cancelled or moved away
    /// deleted. shortcut: the first time all of the next 180 days go in one run of the worker; fine up to a few hundred.</summary>
    void Write(Resource r, string token, JsonObject st)
    {
        if (st["written"] is not JsonObject written) st["written"] = written = new JsonObject();
        var cal = st.Str("write") is var w && w != "" ? w : "primary"; var wrote = st.Str("wrote") is var o && o != "" ? o : "primary";
        if (wrote != cal)
        {
            // another calendar chosen: out of the old one, all into the new one
            foreach (var pk in written.Select(x => x.Key).ToList()) { Delete(token, wrote, EventId(pk, r)); written.Remove(pk); }
            st["wrote"] = cal;
        }
        // shortcut: the signature is of our own JSON, not Python's: a state written by the Python program patches each event once
        var want = Mine(r).ToDictionary(b => b.Id.ToString(), b => { var body = Event(b, r); return (Body: body, Sig: Sha(body.ToJsonString(Plain))[..16]); });
        foreach (var (pk, (body, sig)) in want)
            if (written.Str(pk) != sig) { Put(token, EventId(pk, r), body, written.ContainsKey(pk), cal); written[pk] = sig; }
        var gone = written.Select(x => x.Key).Where(pk => !want.ContainsKey(pk)).ToList(); var done = Ended(r, gone);
        foreach (var pk in gone) { if (!done.Contains(pk)) Delete(token, cal, EventId(pk, r)); written.Remove(pk); }
    }

    /// <summary>sync: one operator, both ways; the state is kept also when Google stops half way, and the error goes on
    /// his card.</summary>
    public bool Sync(Resource r)
    {
        var conf = Config(); var st = State(r); var error = "";
        try
        {
            var token = Access(conf, st);
            if (st["calendars"] == null || st.Str("sync") == "") Calendars(token, st);  // at the first run and with each full read
            // written first: what Google gives back then is already as the bookings, so only Google's own changes warn
            Write(r, token, st); Read(r, token, st);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NullReferenceException or KeyNotFoundException or JsonException) { error = "Google ha dato una risposta che non capiamo: si riprova tra 5 minuti."; }  // never stops the worker
        catch (CalendarError ex)
        {
            error = Message(ex);
            if (ex.Status == 401) st["expires"] = 0;  // the next run asks for a new access token
            if (ex.Reason == "invalid_grant") { st = new JsonObject(); c.S.Execute("DELETE FROM core_timeblock WHERE resource_id=@r AND reason='google'", new { r = r.Id }); }
        }
        Keep(r, st, c.Now, error);
        return error == "";
    }

    /// <summary>disconnect: the access given back to Google (when it answers), the blocks read from it removed.</summary>
    public void Disconnect(Resource r)
    {
        if (State(r).Str("refresh") is { Length: > 0 } token) try { Call("POST", REVOKE, null, null, new() { ["token"] = token }); } catch (CalendarError) { }  // revoked already, or Google down: forgotten here anyway
        c.S.Execute("DELETE FROM core_timeblock WHERE resource_id=@r AND reason='google'", new { r = r.Id });
        Keep(r, new JsonObject(), null, "");
    }
}

/// <summary>mscal.py: Outlook.com / Microsoft 365 calendar, both ways, for each operator, as Google's: calendarView/delta
/// into «Occupato su Outlook» blocks, his bookings created, patched and deleted there.</summary>
public sealed class OutlookCalendar(Ctx c) : OperatorCalendar(c)
{
    const string LOGIN = "https://login.microsoftonline.com/common/oauth2/v2.0/", AUTH = LOGIN + "authorize", TOKEN = LOGIN + "token", GRAPH = "https://graph.microsoft.com/v1.0/me/", SCOPE = "offline_access User.Read Calendars.ReadWrite";
    public const string PATH = "/oauth/outlook-calendar/";

    public JsonObject Config() => string.IsNullOrEmpty(c.Tenant.Microsoft) ? new JsonObject() : c.Decrypt(c.Tenant.Microsoft, "microsoft");
    public JsonObject State(Resource r) => string.IsNullOrEmpty(r.Microsoft) ? new JsonObject() : c.Decrypt(r.Microsoft, "microsoft:" + r.Id);
    void Keep(Resource r, JsonObject st, DateTime? at, string error)
    {
        r.Microsoft = st.Count > 0 ? c.Encrypt(st, "microsoft:" + r.Id) : ""; r.MicrosoftAt = at; r.MicrosoftError = Cut(error, 200); c.S.Update(r, "microsoft", "microsoft_at", "microsoft_error");
    }
    public string Start(string uri, string state, string challenge) => AUTH + "?" + Query(("client_id", Config().Str("client_id")), ("redirect_uri", uri), ("response_type", "code"), ("scope", SCOPE),
        ("response_mode", "query"), ("prompt", "select_account"), ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"));

    public void Finish(Resource r, string code, string uri, string verifier, string denied)
    {
        var conf = Config(); var error = ""; JsonObject t = null, me = null;
        if (denied != "" || conf.Count == 0) error = "Microsoft non ha dato l'accesso: " + Cut(denied != "" ? denied : "app tolta", 60) + ".";
        else
        {
            var body = new Dictionary<string, string> { ["code"] = code, ["client_id"] = conf.Str("client_id"), ["redirect_uri"] = uri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier, ["scope"] = SCOPE };
            if (conf.Str("client_secret") != "") body["client_secret"] = conf.Str("client_secret");
            try
            {
                t = Call("POST", TOKEN, null, null, body); if (t.Str("refresh_token") == "") throw new CalendarError(400, "Microsoft non ha dato l'accesso permanente: riprova.");
                me = Call("GET", GRAPH[..^1], null, t.Str("access_token"), null);
            }
            catch (CalendarError ex) { error = Message(ex); }
        }
        if (error != "") { r.MicrosoftError = Cut(error, 200); c.S.Update(r, "microsoft_error"); return; }
        Keep(r, new JsonObject
        {
            ["refresh"] = t.Str("refresh_token"), ["access"] = t.Str("access_token"), ["expires"] = Stamp(c.Now) + (Num(t["expires_in"]) is > 0 and var n ? n : 3600), ["account"] = me.Str("mail") != "" ? me.Str("mail") : me.Str("userPrincipalName"),
        }, null, "");
    }
    static string Message(CalendarError ex) =>
        ex.Reason == "invalid_grant" ? "Microsoft ha chiuso il collegamento (revocato o scaduto): collegalo di nuovo." : ex.Status == 0 ? ex.Reason : $"Microsoft ha risposto con un errore {ex.Status}" + (ex.Reason != "" ? ": " + Cut(ex.Reason, 120) : ".");

    string Access(JsonObject conf, JsonObject st)
    {
        if (st.Str("access") != "" && Num(st["expires"]) > Stamp(c.Now) + 60) return st.Str("access");
        var body = new Dictionary<string, string> { ["client_id"] = conf.Str("client_id"), ["refresh_token"] = st.Str("refresh"), ["grant_type"] = "refresh_token", ["scope"] = SCOPE };
        if (conf.Str("client_secret") != "") body["client_secret"] = conf.Str("client_secret");
        var t = Call("POST", TOKEN, null, null, body); if (t.Str("access_token") == "") throw new FormatException();
        st["access"] = t.Str("access_token"); st["expires"] = Stamp(c.Now) + (Num(t["expires_in"]) is > 0 and var n ? n : 3600);
        if (t.Str("refresh_token") != "") st["refresh"] = t.Str("refresh_token");  // Microsoft may hand a new one: the old one ends
        return st.Str("access");
    }
    /// <summary>Graph's times are UTC without offset, with seven decimals.</summary>
    static string When(JsonNode x) { var raw = x["dateTime"].ToString(); return Times.PyIso(DateTime.SpecifyKind(DateTime.Parse(raw.Length > 26 ? raw[..26] : raw, CultureInfo.InvariantCulture), DateTimeKind.Utc)); }

    /// <summary>read: calendarView/delta from yesterday for 180 days, then only the changes (deltaLink); once a day all
    /// again, so the far ones come closer. Our own events, the free and the cancelled are left out.</summary>
    void Read(Resource r, string token, JsonObject st)
    {
        var before = st["events"] as JsonObject ?? new JsonObject(); var full = st.Str("delta") == "" || Num(st["full"]) < Stamp(c.Now) - 86400;
        var ours = (st["written"] as JsonObject ?? new JsonObject()).Select(x => x.Value[0].ToString()).ToHashSet();
        var url = !full ? st.Str("delta") : GRAPH + "calendarView/delta?" + Query(("startDateTime", Zulu(c.Now.AddDays(-1))), ("endDateTime", Zulu(c.Now + AHEAD)));
        var seen = full ? new Dictionary<string, JsonNode>() : before.ToDictionary(x => x.Key, x => x.Value.DeepClone());
        while (!string.IsNullOrEmpty(url))
        {
            var page = Call("GET", url, null, token, null);
            foreach (var e in (page["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var id = e.Str("id");
                if (e.ContainsKey("@removed") || e["isCancelled"]?.GetValueKind() == JsonValueKind.True || e.Str("showAs") is "free" or "workingElsewhere" || ours.Contains(id)) { seen.Remove(id); continue; }
                try { seen[id] = new JsonArray(When(e["start"]), When(e["end"])); } catch (Exception ex) when (ex is FormatException or NullReferenceException) { }
            }
            url = page.Str("@odata.nextLink"); if (page.Str("@odata.deltaLink") != "") st["delta"] = page.Str("@odata.deltaLink");
        }
        var events = Near(seen); st["events"] = events; if (full) st["full"] = Stamp(c.Now);
        if (full || !Same(events, before)) OutsideCalendar.Replace(c, r, "outlook", Spans(events));
    }

    JsonObject Event(Booking b, Resource r)
    {
        static JsonObject T(DateTime x) => new() { ["dateTime"] = x.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), ["timeZone"] = "UTC" };
        return new() { ["subject"] = What(b, r), ["start"] = T(b.Start), ["end"] = T(b.End), ["showAs"] = "busy", ["isReminderOn"] = false };
    }
    /// <summary>write: his bookings from now on as events: created (transactionId: a retry does not double), patched when
    /// changed, deleted when cancelled or moved away; written: {booking: [event id, signature]}.</summary>
    void Write(Resource r, string token, JsonObject st)
    {
        if (st["written"] is not JsonObject written) st["written"] = written = new JsonObject();
        var want = Mine(r).ToDictionary(b => b.Id.ToString(), b => { var body = Event(b, r); return (Body: body, Sig: Sha(body.ToJsonString(Plain))[..16]); });
        foreach (var (pk, (body, sig)) in want)
        {
            var old = written[pk] as JsonArray;
            if (old != null && old[1].ToString() == sig) continue;
            if (old != null) try { Call("PATCH", GRAPH + "events/" + old[0], body, token, null); written[pk] = new JsonArray(old[0].ToString(), sig); continue; } catch (CalendarError ex) when (ex.Status == 404) { }
            var create = (JsonObject)body.DeepClone(); create["transactionId"] = Sha($"{c.Tenant.Id}:{pk}:{r.Id}:{sig}")[..36];
            var e = Call("POST", GRAPH + "events", create, token, null); if (e.Str("id") == "") throw new FormatException();
            written[pk] = new JsonArray(e.Str("id"), sig);
        }
        var gone = written.Select(x => x.Key).Where(pk => !want.ContainsKey(pk)).ToList(); var done = Ended(r, gone);
        foreach (var pk in gone)
        {
            if (!done.Contains(pk)) try { Call("DELETE", GRAPH + "events/" + written[pk][0], null, token, null); } catch (CalendarError ex) when (ex.Status is 404 or 410) { }
            written.Remove(pk);
        }
    }

    public bool Sync(Resource r)
    {
        var conf = Config(); var st = State(r); var error = "";
        try { var token = Access(conf, st); Write(r, token, st); Read(r, token, st); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NullReferenceException or KeyNotFoundException or JsonException) { error = "Microsoft ha dato una risposta che non capiamo: si riprova tra 5 minuti."; }
        catch (CalendarError ex)
        {
            error = Message(ex);
            if (ex.Status == 401) st["expires"] = 0;
            if (ex.Status == 410) st.Remove("delta");  // the deltaLink is no longer known: all again
            if (ex.Reason == "invalid_grant") { st = new JsonObject(); c.S.Execute("DELETE FROM core_timeblock WHERE resource_id=@r AND reason='outlook'", new { r = r.Id }); }
        }
        Keep(r, st, c.Now, error);
        return error == "";
    }
    /// <summary>disconnect: the tokens forgotten (Microsoft has no revoke for them), the blocks read from it removed.</summary>
    public void Disconnect(Resource r)
    {
        c.S.Execute("DELETE FROM core_timeblock WHERE resource_id=@r AND reason='outlook'", new { r = r.Id });
        Keep(r, new JsonObject(), null, "");
    }
}
