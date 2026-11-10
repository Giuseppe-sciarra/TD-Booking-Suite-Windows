using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/stays.py: hotel, B&amp;B and holiday homes. The calendar of each unit for the portals (.ics) and theirs
/// read back as closures; the guests, sealed in the booking, typed at the desk or by the client from his link; the
/// Alloggiati Web file or its web service with the receipts, the ISTAT movements for Ross1000, the tourist tax by the
/// town's rules. The Ross1000 file is downloaded and loaded by the business.</summary>
public sealed partial class Stays
{
    public static readonly string[] STAYING = new[] { "pending" }.Concat(Bookings.ACCEPTED).ToArray();  // what holds a unit for the portals; the files and the tax count the accepted ones only
    public static readonly (string Id, string Label)[] KINDS = { ("16", "Ospite singolo"), ("17", "Capofamiglia"), ("18", "Capogruppo"), ("19", "Familiare"), ("20", "Membro del gruppo") };
    public static readonly string[] HEADS = { "16", "17", "18" };  // who shows a document; family and group members travel on their head's
    // Ross1000, asked once a stay at check-in: the values of its tracciato (XML 2.4)
    public static readonly string[] TOURISM = { "Culturale", "Balneare", "Congressuale/Affari", "Fieristico", "Sportivo/Fitness", "Scolastico", "Religioso", "Sociale", "Parchi Tematici", "Termale/Trattamenti salute", "Enogastronomico", "Cicloturismo", "Escursionistico/Naturalistico", "Altro motivo", "Non specificato" };
    public static readonly string[] TRANSPORT = { "Auto", "Aereo", "Aereo+Pullman", "Aereo+Navetta/Taxi/Auto", "Aereo+Treno", "Treno", "Pullman", "Caravan/Autocaravan", "Barca/Nave/Traghetto", "Moto", "Bicicletta", "A piedi", "Altro mezzo", "Non specificato" };
    // the tourist tax: why a guest does not pay (or pays less), chosen for each guest
    public static readonly (string Id, string Label)[] EXEMPTIONS =
    {
        ("", "Paga"), ("resident", "Residente nel comune"), ("worker", "Lavoro (notti massime all'anno)"), ("student", "Studio (notti massime all'anno)"), ("escort", "Accompagnatore turistico del gruppo"), ("driver", "Autista del pullman"),
        ("care", "Cure, assistenza a un malato"), ("disability", "Disabilità grave o suo accompagnatore"), ("staff", "Personale della struttura"), ("duty", "Forze dell'ordine o soccorso in servizio"), ("emergency", "Emergenza, ospite delle autorità"),
        ("reduced", "Riduzione (gruppi scolastici o sportivi)"), ("other", "Altro motivo di esenzione"),
    };
    public static readonly (string Id, string Label)[] TAX_CATEGORIES =
    {
        ("albergo1", "Albergo 1 stella"), ("albergo2", "Albergo 2 stelle"), ("albergo3", "Albergo 3 stelle"), ("albergo4", "Albergo 4 stelle"), ("albergo5", "Albergo 5 stelle e lusso"), ("rta", "Residenza turistico-alberghiera"), ("agriturismo", "Agriturismo"),
        ("affittacamere1", "Affittacamere o guest house cat. 1"), ("affittacamere2", "Affittacamere o guest house cat. 2"), ("affittacamere3", "Affittacamere o guest house cat. 3"), ("ostello", "Ostello"), ("cav1", "Casa vacanze cat. 1"), ("cav2", "Casa vacanze cat. 2"),
        ("bb", "Bed and breakfast"), ("casaferie", "Casa per ferie"), ("country", "Country house"), ("rifugio", "Rifugio"), ("uso_turistico", "Alloggio per uso turistico"), ("locazione", "Locazione breve"), ("campeggio", "Campeggio o villaggio"),
    };
    public static readonly string[] WEEKDAY_NAMES = WEEKDAYS;
    static readonly CultureInfo INV = CultureInfo.InvariantCulture;

    DateOnly Local(DateTime moment) => c.Cal.DayOf(moment);
    JsonObject Data(Booking b) => new Bookings(c).Data(b);

    // ---- calendars of the portals
    public string FeedToken(Resource unit) => c.Index("ical:" + unit.Id, "ical-feed")[..32];
    public string FeedLink(Resource unit) => $"{c.PublicOrigin.TrimEnd('/')}/book/{c.Tenant.Slug}/calendario/{unit.Id}/{FeedToken(unit)}.ics";

    /// <summary>The busy nights of a unit, all day and without names: its stays and the closures over it. The nights read
    /// from a portal stay out, or two portals would hand each other's back.</summary>
    public string Feed(Resource unit)
    {
        var now = c.Now; var today = c.Today; var events = new List<(Guid Id, DateOnly A, DateOnly Z)>();
        foreach (var b in c.S.Query<Booking>("SELECT * FROM core_booking WHERE resource_id=@r AND status = ANY(@st) AND \"end\">=@n", new { r = unit.Id, st = STAYING, n = now }))
        { var a = Local(b.Start); var z = Local(b.End); events.Add((b.Id, a, z > a.AddDays(1) ? z : a.AddDays(1))); }
        var last = today.AddDays(730);
        foreach (var x in c.S.Query<Exceptionday>(@"SELECT * FROM core_exceptionday WHERE purpose='service' AND closed AND feed='' AND (resource_id=@r
            OR (resource_id IS NULL AND service_id IN (SELECT service_id FROM core_serviceresource WHERE resource_id=@r))
            OR (location_id=@l AND service_id IS NULL AND resource_id IS NULL) OR (location_id IS NULL AND service_id IS NULL AND resource_id IS NULL))", new { r = unit.Id, l = unit.LocationId }))
            if (Calendar.Covering(x, today, last)) { var d = DateOnly.FromDateTime(x.Date); events.Add((x.Id, d, (x.Until.HasValue ? DateOnly.FromDateTime(x.Until.Value) : d).AddDays(1))); }
        var host = c.PublicOrigin.Split("://", 2).Last().Split('/')[0].Split(':')[0];
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Tastiere Digitali//TD Booking Suite//IT", "METHOD:PUBLISH" };
        foreach (var e in events.OrderBy(e => e.A))
            lines.AddRange(new[] { "BEGIN:VEVENT", $"UID:{e.Id}@{host}", "DTSTAMP:" + Texts.Utc(now), $"DTSTART;VALUE=DATE:{e.A:yyyyMMdd}", $"DTEND;VALUE=DATE:{e.Z:yyyyMMdd}", "SUMMARY:Non disponibile", "END:VEVENT" });
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(Texts.Fold)) + "\r\n";
    }

    /// <summary>The nights of each event of a calendar file, as (first night, departure day).</summary>
    public static List<(DateOnly A, DateOnly Z)> Parse(string text)
    {
        var o = new List<(DateOnly, DateOnly)>(); DateOnly? a = null, z = null;
        foreach (var line in Regex.Replace(text, "\r?\n[ \t]", "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var at = line.IndexOf(':'); var name = (at < 0 ? line : line[..at]).Split(';')[0].Trim().ToUpperInvariant(); var value = at < 0 ? "" : line[(at + 1)..].Trim();
            DateOnly? Date() => value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", INV, DateTimeStyles.None, out var d) ? d : null;
            if (name == "BEGIN" && value == "VEVENT") a = z = null;
            else if (name == "DTSTART") { a = Date(); if (a == null) z = null; }
            else if (name == "DTEND") { z = Date(); if (z == null) a = null; }  // a broken date: that event is skipped
            else if (name == "END" && value == "VEVENT" && a.HasValue) o.Add((a.Value, z.HasValue && z > a ? z.Value : a.Value.AddDays(1)));
        }
        return o;
    }

    /// <summary>A portal's calendar: https, never an address of the server's own network.</summary>
    public static Func<string, string> Fetch = url =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host == "") throw new ArgumentException("indirizzo non valido");
        foreach (var addr in Dns.GetHostAddresses(u.Host)) if (!Network.Global(addr)) throw new ArgumentException("indirizzo non raggiungibile");
        // shortcut: a redirect is followed without the check above; portals answer directly, recheck if one ever redirects
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("TDBookingSuite");
        var raw = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 3_000_000));
    };
    static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host != "" ? u.Host : "calendario";
    public List<string> Feeds(Resource unit) => string.IsNullOrEmpty(unit.IcalIn) ? new() : (c.DecryptNode(unit.IcalIn, "ical:" + unit.Id) as JsonArray ?? new JsonArray()).Select(x => x.ToString()).ToList();
    string FeedKey(string url) => c.Index(url, "ical-in")[..16];

    /// <summary>The addresses of the portals' calendars of a unit, sealed (they hold the portal's secret).</summary>
    public void SetFeeds(Resource unit, string text)
    {
        var urls = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        if (urls.FirstOrDefault(u => !Regex.IsMatch(u, @"^https://\S{4,600}\z")) is string bad) throw new BookingException($"Indirizzo non valido: {(bad.Length > 60 ? bad[..60] : bad)}. Copialo intero dal portale, inizia con https://");
        unit.IcalIn = urls.Count > 0 ? c.Encrypt(new JsonArray(urls.Select(u => (JsonNode)u).ToArray()), "ical:" + unit.Id) : ""; c.S.Update(unit, "ical_in");
        c.S.Execute("DELETE FROM core_exceptionday WHERE resource_id=@r AND feed<>'' AND NOT (feed = ANY(@k))", new { r = unit.Id, k = urls.Select(FeedKey).ToArray() });
    }

    /// <summary>Reads the unit's portal calendars again: their nights become closures of the unit, replaced at every reading.
    /// A calendar that does not answer keeps its last nights: better a night closed than a double booking.</summary>
    public bool Refresh(Resource unit)
    {
        var today = c.Today; var read = new Dictionary<string, (string Name, List<(DateOnly A, DateOnly Z)> Nights)>(); var errors = new List<string>();
        foreach (var url in Feeds(unit))
            try { read[FeedKey(url)] = (HostOf(url), Parse(Fetch(url))); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or ArgumentException or TaskCanceledException) { errors.Add(HostOf(url)); }
        foreach (var (key, (name, nights)) in read)
        {
            c.S.Execute("DELETE FROM core_exceptionday WHERE resource_id=@r AND feed=@k", new { r = unit.Id, k = key });
            foreach (var (a, z) in nights.Where(n => n.Z > today))
            {
                var label = "Occupato su " + name;
                c.S.Insert(new Exceptionday { TenantId = c.Tenant.Id, ResourceId = unit.Id, Purpose = "service", Closed = true, Date = a.ToDateTime(TimeOnly.MinValue), Until = z.DayNumber - a.DayNumber > 1 ? z.AddDays(-1).ToDateTime(TimeOnly.MinValue) : null, Label = label.Length > 100 ? label[..100] : label, Feed = key });
            }
        }
        unit.IcalAt = c.Now; var said = errors.Count > 0 ? "Non risponde: " + string.Join(", ", errors) : ""; unit.IcalError = said.Length > 200 ? said[..200] : said;
        c.S.Update(unit, "ical_at", "ical_error"); c.CalendarChanged();
        return errors.Count == 0;
    }

    /// <summary>The worker, every hour: the units with portal calendars, read again; Alloggiati Web, the arrivals of yesterday
    /// and today and the receipts; what goes wrong is shown in Alloggi. Returns how many calendars answered.</summary>
    public static int Run(Db db, Crypto crypto, string origin, IOutbox outbox, string edition = "private")
    {
        var done = 0; List<Guid> tenants; using (var g = db.Global()) tenants = g.Query<Guid>("SELECT id FROM core_tenant").ToList();
        foreach (var id in tenants)
        {
            using var c = new Ctx(db, crypto, id) { PublicOrigin = origin, Out = outbox, Edition = edition };
            if (!c.Operational()) continue;
            var stays = new Stays(c);
            foreach (var unit in c.S.Query<Resource>("SELECT * FROM core_resource WHERE ical_in<>''").ToList()) if (stays.Refresh(unit)) done++;
            var conf = stays.WsConf();
            if (conf.Count > 0)
            {
                string error;
                try
                {
                    c.S.Execute("SAVEPOINT alloggiati");
                    if (Json.Truthy(conf["auto"])) foreach (var day in new[] { c.Today.AddDays(-1), c.Today }) stays.WsSend(day, true);
                    stays.Receipts(conf); error = ""; c.S.Execute("RELEASE SAVEPOINT alloggiati");
                }
                catch (WSError ex) { error = ex.Message; }
                if (error != conf.Str("error")) { conf["error"] = error; stays.WsSave(conf, false); }
            }
            c.Commit();
        }
        return done;
    }

    // ---- guests and the Alloggiati tables: the tables of the Polizia di Stato, 31/10/2026: towns change every January
    static List<Dictionary<string, string>> Table(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TD.Core.alloggiati.{name}.csv")!; using var r = new StreamReader(s, Encoding.ASCII);
        var head = r.ReadLine()!.Split(','); var rows = new List<Dictionary<string, string>>();
        for (var line = r.ReadLine(); line != null; line = r.ReadLine()) { if (line == "") continue; var cells = line.Split(','); rows.Add(head.Select((h, i) => (h, i < cells.Length ? cells[i] : "")).ToDictionary(x => x.h, x => x.Item2)); }
        return rows;
    }
    /// <summary>ASCII in capitals, accents dropped, as the Alloggiati file and its tables are written.</summary>
    public static string Plain(string text) => new string((text ?? "").Normalize(NormalizationForm.FormKD).Where(ch => ch < 128).ToArray()).ToUpperInvariant();
    static string Key(string text) => Regex.Replace(Plain(text), "[^A-Z]", "");
    /// <summary>Name or code -> (code, province; ES for a state). States first, then the towns in force, then the ceased ones (still good as a birthplace).</summary>
    static readonly Lazy<Dictionary<string, (string Code, string Province)>> Places = new(() =>
    {
        var o = new Dictionary<string, (string, string)>();
        foreach (var name in new[] { "stati", "comuni" })
            foreach (var r in Table(name).OrderBy(r => r["DataFineVal"] != "")) { o.TryAdd(Key(r["Descrizione"]), (r["Codice"], r["Provincia"])); o.TryAdd(r["Codice"], (r["Codice"], r["Provincia"])); }
        o["ITALIANA"] = o["ITALIANO"] = o["ITALIA"];
        return o;
    });
    public static (string Code, string Province)? Place(string text)
    {
        var t = (text ?? "").Trim();
        return Places.Value.TryGetValue(t != "" && t.All(char.IsAsciiDigit) ? t : Key(t), out var p) ? p : null;
    }
    static readonly Lazy<List<(string Id, string Label)>> Docs = new(() => Table("documenti").Select(r => (r["Codice"], r["Descrizione"].Length > 0 ? char.ToUpper(r["Descrizione"][0]) + r["Descrizione"][1..].ToLowerInvariant() : "")).ToList());
    public static List<(string Id, string Label)> Documents() => Docs.Value;

    public static readonly string[] GUEST = { "kind", "last_name", "first_name", "sex", "birth_date", "birth_place", "citizenship", "residence", "doc_type", "doc_number", "doc_place" };
    /// <summary>The guests typed in, checked: towns and states must be in the tables, the document is asked of the heads.</summary>
    public static JsonArray ReadGuests(Func<string, string> post, int party)
    {
        var o = new JsonArray();
        for (int i = 0; i < party; i++)
        {
            var g = new JsonObject(); foreach (var k in GUEST) { var v = post($"g{i}-{k}").Trim(); g[k] = v.Length > 60 ? v[..60] : v; }
            if (g.Str("last_name") == "" && g.Str("first_name") == "") continue;
            var who = $"Ospite {i + 1}: ";
            if (!KINDS.Any(k => k.Id == g.Str("kind"))) g["kind"] = "16";
            if (g.Str("last_name") == "" || g.Str("first_name") == "") throw new BookingException(who + "scrivi nome e cognome.");
            if (g.Str("sex") is not ("M" or "F")) throw new BookingException(who + "indica il sesso.");
            if (!DateOnly.TryParseExact(g.Str("birth_date"), "yyyy-MM-dd", INV, DateTimeStyles.None, out var born) || born < new DateOnly(1900, 1, 1) || born > DateOnly.FromDateTime(DateTime.UtcNow)) throw new BookingException(who + "data di nascita non valida.");
            foreach (var (f, label) in new[] { ("birth_place", "luogo di nascita"), ("citizenship", "cittadinanza"), ("residence", "residenza") })
                if (Place(g.Str(f)) == null) throw new BookingException(who + $"{label} non trovato. Scrivi il comune italiano o lo Stato estero per intero, per esempio Roma o Germania.");
            if (HEADS.Contains(g.Str("kind")))
            {
                if (!Documents().Any(d => d.Id == g.Str("doc_type")) || !Regex.IsMatch(g.Str("doc_number"), @"^[A-Za-z0-9./-]{3,20}\z")) throw new BookingException(who + "scegli il documento e scrivi il numero.");
                if (Place(g.Str("doc_place")) == null) throw new BookingException(who + "luogo di rilascio del documento non trovato (comune o Stato).");
            }
            else foreach (var k in new[] { "doc_type", "doc_number", "doc_place" }) g[k] = "";
            var ex = post($"g{i}-exemption"); g["exemption"] = EXEMPTIONS.Any(e => e.Id == ex) ? ex : "";
            o.Add(g);
        }
        if (o.Count > 0 && o[0].Str("kind") is "19" or "20") throw new BookingException("Il primo ospite è il capofamiglia, il capogruppo o un ospite singolo.");
        return o;
    }
    /// <summary>Why and how the guests came (Ross1000), once for the stay.</summary>
    public static (string Tourism, string Transport) ReadTrip(Func<string, string> post)
    {
        static string Pick(string v, string[] values) => values.Select(x => x.ToUpperInvariant()).Contains(v) ? v : "NON SPECIFICATO";
        return (Pick(post("tourism"), TOURISM), Pick(post("transport"), TRANSPORT));
    }
    public void SaveGuests(Booking b, JsonArray guests, (string Tourism, string Transport)? trip = null)
    {
        var data = Data(b); data["guests"] = guests; if (trip.HasValue) { data["tourism"] = trip.Value.Tourism; data["transport"] = trip.Value.Transport; }
        b.Pii = c.Encrypt(data, "booking:" + b.Id); var snap = b.Snap; snap.Remove("alloggiati_error"); b.Snapshot = Json.Dump(snap);  // changed: the worker may try again
        c.S.Update(b, "pii", "snapshot"); c.Audit("booking.guests", b.Id, new { guests = guests.Count });
    }

    // ---- the files
    static string Pad(string text, int n) { var p = Plain(text); return (p.Length > n ? p[..n] : p).PadRight(n); }
    /// <summary>One guest, 168 characters (Alloggiati Web, Tabella 1).</summary>
    public static string AlloggiatiLine(JsonObject g, DateOnly arrival, int nights)
    {
        var born = Place(g.Str("birth_place"))!.Value; var italy = born.Province != "ES"; var head = HEADS.Contains(g.Str("kind"));
        var line = g.Str("kind") + arrival.ToString("dd/MM/yyyy", INV) + Math.Min(30, Math.Max(1, nights)).ToString("00") + Pad(g.Str("last_name"), 50) + Pad(g.Str("first_name"), 30) + (g.Str("sex") == "M" ? "1" : "2")
            + DateOnly.ParseExact(g.Str("birth_date"), "yyyy-MM-dd", INV).ToString("dd/MM/yyyy", INV) + (italy ? born.Code : "").PadRight(9) + (italy ? born.Province : "").PadRight(2)
            + (italy ? "100000100" : born.Code) + Place(g.Str("citizenship"))!.Value.Code
            + (head ? Pad(g.Str("doc_type"), 5) + Pad(g.Str("doc_number"), 20) + Place(g.Str("doc_place"))!.Value.Code : new string(' ', 34));
        if (line.Length != 168) throw new InvalidOperationException("schedina di " + line.Length + " caratteri");
        return line;
    }
    static List<JsonObject> Guests(JsonObject data) => (data["guests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();

    /// <summary>The accepted stays that touch the days from first to last.</summary>
    public List<Booking> Between(DateOnly first, DateOnly last, Guid? location = null, string visible = null, object args = null)
    {
        var p = new Dapper.DynamicParameters(args); p.Add("st", Bookings.ACCEPTED); p.Add("a", c.Cal.Local(first.AddDays(-1), TimeSpan.Zero)); p.Add("z", c.Cal.Local(last.AddDays(2), TimeSpan.Zero)); p.Add("loc", location);
        return c.S.Query<Booking>($@"SELECT b.* FROM core_booking b JOIN core_service s ON s.id=b.service_id WHERE s.kind='day' AND b.status = ANY(@st) AND b.start<@z AND b.""end"">@a
            {(location.HasValue ? "AND b.location_id=@loc" : "")} {(visible != null ? $"AND ({visible})" : "")} ORDER BY b.start", p).ToList();
    }

    /// <summary>The Alloggiati Web file of the arrivals of a day, and the stays left out because their guests are missing.</summary>
    public (string Body, int Missing) Alloggiati(DateOnly day)
    {
        var lines = new List<string>(); var missing = 0;
        foreach (var b in Between(day, day))
        {
            if (Local(b.Start) != day) continue;
            var guests = Guests(Data(b)); if (guests.Count == 0) { missing++; continue; }
            lines.AddRange(guests.Select(g => AlloggiatiLine(g, day, Local(b.End).DayNumber - day.DayNumber)));
        }
        return (string.Join("\r\n", lines.Take(1000)), missing);  // the portal takes 1000 rows a file: no small structure gets there in a day
    }
    void Forget(Booking b)
    {
        var data = Data(b); foreach (var g in Guests(data)) foreach (var k in new[] { "doc_type", "doc_number", "doc_place" }) g[k] = "";
        b.Pii = c.Encrypt(data, "booking:" + b.Id); var snap = b.Snap; snap["alloggiati"] = c.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00", INV); snap.Remove("alloggiati_error"); b.Snapshot = Json.Dump(snap);
        c.S.Update(b, "pii", "snapshot");
    }
    /// <summary>The Alloggiati file of a day is sent: the documents of its guests are deleted (DM 7/1/2013 art. 4-bis); what ISTAT and the tax need stays.</summary>
    public int Sent(DateOnly day)
    {
        var n = 0;
        foreach (var b in Between(day, day)) { if (Local(b.Start) != day || Guests(Data(b)).Count == 0) continue; Forget(b); n++; }
        c.Audit("stays.alloggiati_sent", day.ToString("yyyy-MM-dd"), new { bookings = n });
        return n;
    }

    // ---- Alloggiati Web, its web service (MANUALEWS rev. 01, 24/01/2022)
    public const string WS = "https://alloggiatiweb.poliziadistato.it/service/service.asmx";
    public sealed class WSError(string message) : Exception(message);
    public JsonObject WsConf() => string.IsNullOrEmpty(c.Tenant.Alloggiati) ? new JsonObject() : c.Decrypt(c.Tenant.Alloggiati, "alloggiati");

    /// <summary>One SOAP 1.1 call, going out only. Replaced in the tests.</summary>
    public static Func<string, string, string> WsPost = (method, body) =>
    {
        var envelope = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><{method} xmlns=\"AlloggiatiService\">{body}</{method}></soap:Body></soap:Envelope>";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var req = new HttpRequestMessage(HttpMethod.Post, WS) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") }; req.Headers.TryAddWithoutValidation("SOAPAction", $"\"AlloggiatiService/{method}\"");
            using var r = http.Send(req); using var s = r.Content.ReadAsStream(); using var reader = new StreamReader(s, Encoding.UTF8);  // a SOAP fault comes with a 500
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { throw new WSError("Alloggiati Web non risponde: riprova più tardi o scarica il file e caricalo sul portale."); }
    };
    /// <summary>The answer of a call: its elements by name, without namespaces.</summary>
    static Func<string, List<XElement>> Ws(string method, params (string Key, object Value)[] args)
    {
        static string X(string v) => SecurityElement.Escape(v) ?? "";
        var body = string.Concat(args.Select(a => $"<{a.Key}>" + (a.Value is IEnumerable<string> list ? string.Concat(list.Select(x => $"<string>{X(x)}</string>")) : X(a.Value?.ToString() ?? "")) + $"</{a.Key}>"));
        XElement root; try { root = XDocument.Parse(WsPost(method, body)).Root; } catch (System.Xml.XmlException) { throw new WSError("Alloggiati Web ha risposto in modo non valido: riprova più tardi."); }
        List<XElement> Find(string name) => root.DescendantsAndSelf().Where(e => e.Name.LocalName == name).ToList();
        if (Find("faultstring").FirstOrDefault() is XElement fault) { var t = fault.Value == "" ? "errore" : fault.Value; throw new WSError("Alloggiati Web: " + (t.Length > 200 ? t[..200] : t)); }
        return Find;
    }
    /// <summary>An EsitoOperazioneServizio: done, and why not.</summary>
    static (bool Ok, string Why) Outcome(XElement e)
    {
        string Get(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value ?? "";
        var why = (Get("ErroreDes") + " " + Get("ErroreDettaglio")).Trim(); return (Get("esito") == "true", why.Length > 200 ? why[..200] : why);
    }
    static string WsToken(JsonObject conf)
    {
        var find = Ws("GenerateToken", ("Utente", conf.Str("user")), ("Password", conf.Str("password")), ("WsKey", conf.Str("wskey")));
        var (ok, why) = find("result").Count > 0 ? Outcome(find("result")[0]) : (false, ""); var token = find("token").Select(e => e.Value).FirstOrDefault(v => v != "") ?? "";
        if (!ok || token == "") throw new WSError($"Alloggiati Web non accetta l'accesso ({(why != "" ? why : "nessun motivo")}): controlla utente, password e chiave web service. Dopo ogni cambio di password serve una chiave nuova.");
        return token;
    }
    /// <summary>Test or Send: the outcome of each row, in order.</summary>
    static List<(bool Ok, string Why)> WsRows(JsonObject conf, string token, string method, List<string> rows)
    {
        var find = Ws(method, ("Utente", conf.Str("user")), ("token", token), ("ElencoSchedine", rows)); var o = find("EsitoOperazioneServizio").Select(Outcome).ToList();
        if (o.Count != rows.Count) { var why = find(method + "Result").Count > 0 ? Outcome(find(method + "Result")[0]).Why : ""; throw new WSError("Alloggiati Web ha rifiutato l'invio" + (why != "" ? $": {why}" : "") + "."); }
        return o;
    }

    /// <summary>The arrivals of a day not sent yet, to Alloggiati Web: first Test, then Send of the stays whose rows are all
    /// good, so a family never goes without its head (Send takes the good rows only). Those taken lose their documents at
    /// once (DM 7/1/2013 art. 4-bis) and count in the receipt of today; the others keep the reason, shown at the desk. The
    /// worker (auto) sends a stay once its arrival time has come and leaves the refused ones until they are corrected.
    /// Returns (stays sent, reasons of the refused).</summary>
    public (int Sent, List<string> Errors) WsSend(DateOnly day, bool auto = false)
    {
        var conf = WsConf(); var now = c.Now; var today = c.Today;
        if (conf.Count == 0) throw new WSError("Alloggiati Web non è configurato.");
        if (day != today && day != today.AddDays(-1)) throw new WSError("Alloggiati Web accetta solo gli arrivi di oggi o di ieri.");
        var todo = Between(day, day).Where(b => Local(b.Start) == day && Guests(Data(b)).Count > 0 && b.Snap.Str("alloggiati") == "" && !(auto && (b.Snap.Str("alloggiati_error") != "" || b.Start > now))).ToList();
        if (todo.Count == 0) return (0, new());
        var lines = todo.Select(b => Guests(Data(b)).Select(g => AlloggiatiLine(g, day, Local(b.End).DayNumber - day.DayNumber)).ToList()).ToList();
        var token = WsToken(conf); var errors = new List<string>();
        List<(Booking B, List<string> Rows)> Split(List<(bool Ok, string Why)> results, List<(Booking B, List<string> Rows)> group)
        {
            var i = 0; var good = new List<(Booking, List<string>)>();
            foreach (var (b, rows) in group)
            {
                var res = results.Skip(i).Take(rows.Count).ToList(); i += rows.Count;
                if (res.All(r => r.Ok)) { good.Add((b, rows)); continue; }
                var why = string.Join("; ", res.Select((r, k) => (r, k)).Where(x => !x.r.Ok).Select(x => $"ospite {x.k + 1}: {(x.r.Why != "" ? x.r.Why : "dato non valido")}")); if (why.Length > 300) why = why[..300];
                var snap = b.Snap; snap["alloggiati_error"] = why; b.Snapshot = Json.Dump(snap); c.S.Update(b, "snapshot");
                errors.Add($"{(b.Snap.Str("resource") != "" ? b.Snap.Str("resource") : c.Row<Service>(b.ServiceId).Name)}: {why}");
            }
            return good;
        }
        var all = todo.Zip(lines, (b, l) => (b, l)).ToList();
        var good = Split(WsRows(conf, token, "Test", lines.SelectMany(l => l).ToList()), all);
        var done = good.Count > 0 ? Split(WsRows(conf, token, "Send", good.SelectMany(g => g.Rows).ToList()), good) : new();
        foreach (var (b, _) in done) Forget(b);
        if (done.Count > 0)
        {
            var n = done.Sum(d => d.Rows.Count);
            if (c.S.Execute("UPDATE core_receipt SET rows=rows+@n WHERE day=@d", new { n, d = today.ToDateTime(TimeOnly.MinValue) }) == 0)
                c.S.Insert(new Receipt { TenantId = c.Tenant.Id, Day = today.ToDateTime(TimeOnly.MinValue), Rows = n });
        }
        c.Audit("stays.alloggiati_ws", day.ToString("yyyy-MM-dd"), new { sent = done.Count, refused = errors.Count, auto });
        return (done.Count, errors);
    }

    /// <summary>The receipts of the past days of sending not downloaded yet: the Polizia gives them for 30 days, never for today. Those older than five years go.</summary>
    public int Receipts(JsonObject conf)
    {
        var today = c.Today; var n = 0;
        c.S.Execute("DELETE FROM core_receipt WHERE day<@d", new { d = today.AddDays(-5 * 366).ToDateTime(TimeOnly.MinValue) });
        var rows = c.S.Query<Receipt>("SELECT * FROM core_receipt WHERE pdf IS NULL AND day<@t AND day>=@f", new { t = today.ToDateTime(TimeOnly.MinValue), f = today.AddDays(-29).ToDateTime(TimeOnly.MinValue) }).ToList();
        if (rows.Count == 0) return 0;
        var token = WsToken(conf);
        foreach (var r in rows)
        {
            var pdf = Ws("Ricevuta", ("Utente", conf.Str("user")), ("token", token), ("Data", $"{r.Day:yyyy-MM-dd}T00:00:00"))("PDF").Select(e => e.Value).FirstOrDefault(v => v != "");
            if (pdf == null) continue;
            try { r.Pdf = Convert.FromBase64String(pdf); } catch (FormatException) { continue; }
            c.S.Update(r, "pdf"); n++;
        }
        return n;
    }
    /// <summary>The credentials as typed; an empty password or key keeps the one saved.</summary>
    public static JsonObject WsClean(Func<string, string> post, bool off, bool auto, JsonObject saved)
    {
        if (off) return new JsonObject();
        var conf = new JsonObject { ["user"] = post("ws_user").Trim(), ["auto"] = auto };
        foreach (var k in new[] { "password", "wskey" }) { var v = post("ws_" + k).Trim(); conf[k] = v != "" ? v : saved.Str(k); }
        if (!(Regex.IsMatch(conf.Str("user"), @"^[\w.@-]{3,60}\z") && conf.Str("password").Length is > 0 and <= 100 && Regex.IsMatch(conf.Str("wskey"), @"^\S{4,200}\z")))
            throw new BookingException("Per Alloggiati Web servono utente, password e chiave web service (la trovi nel portale, menu utente, Chiave Web Service).");
        return conf;
    }
    public void WsSave(JsonObject conf, bool audit = true)
    {
        c.Tenant.Alloggiati = conf.Count > 0 ? c.Encrypt(conf, "alloggiati") : ""; c.S.Execute("UPDATE core_tenant SET alloggiati=@a WHERE id=@t", new { a = c.Tenant.Alloggiati, t = c.Tenant.Id });
        if (audit) c.Audit("stays.alloggiati_conf", null, new { on = conf.Count > 0, auto = Json.Truthy(conf["auto"]) });
    }

    // ---- Ross1000
    static readonly Dictionary<string, string> CHANNELS = new() { ["public"] = "DIRETTA WEB", ["wordpress"] = "DIRETTA WEB", ["desk"] = "DIRETTA TRADIZIONALE", ["walkin"] = "DIRETTA TRADIZIONALE" };
    /// <summary>The ISTAT movements of a month for Ross1000 (XML 2.4): one day after the other, also the days without guests
    /// (movimento zero) and the days the location is closed (apertura NO). Names stay out: they are optional there.</summary>
    public (string Body, int Missing) Ross1000(Location location, int year, int month)
    {
        static string X(string v) => SecurityElement.Escape(v) ?? "";
        var first = new DateOnly(year, month, 1); var last = first.AddMonths(1).AddDays(-1);
        var units = c.S.Query<Resource>("SELECT * FROM core_resource WHERE active AND location_id=@l AND id IN (SELECT sr.resource_id FROM core_serviceresource sr JOIN core_service s ON s.id=sr.service_id WHERE s.kind='day')", new { l = location.Id }).ToList();
        var rows = Between(first, last, location.Id).Select(b => (B: b, Data: Data(b), A: Local(b.Start), Z: Local(b.End))).ToList();
        var missing = rows.Count(r => first <= r.A && r.A <= last && Guests(r.Data).Count == 0);
        var closed = new HashSet<DateOnly>();
        foreach (var x in c.S.Query<Exceptionday>("SELECT * FROM core_exceptionday WHERE (location_id=@l OR location_id IS NULL) AND service_id IS NULL AND resource_id IS NULL AND purpose='service' AND closed", new { l = location.Id }))
            if (Calendar.Covering(x, first, last)) { var a = DateOnly.FromDateTime(x.Date); var z = x.Until.HasValue ? DateOnly.FromDateTime(x.Until.Value) : a; for (var d = a; d <= z; d = d.AddDays(1)) closed.Add(d); }
        var o = new List<string> { "<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "<movimenti>", $"<codice>{X(location.RossCode)}</codice><prodotto>TD Booking Suite</prodotto>" };
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            var rooms = rows.Where(r => r.A <= d && d < r.Z).Select(r => r.B.ResourceId ?? r.B.Id).Distinct().Count();
            o.Add(closed.Contains(d)
                ? $"<movimento><data>{d:yyyyMMdd}</data><struttura><apertura>NO</apertura><camereoccupate>0</camereoccupate><cameredisponibili>0</cameredisponibili><lettidisponibili>0</lettidisponibili></struttura>"
                : $"<movimento><data>{d:yyyyMMdd}</data><struttura><apertura>SI</apertura><camereoccupate>{(units.Count > 0 ? Math.Min(rooms, units.Count) : rooms)}</camereoccupate><cameredisponibili>{units.Count}</cameredisponibili><lettidisponibili>{units.Sum(u => u.Capacity)}</lettidisponibili></struttura>");
            var arrivals = new StringBuilder(); var departures = new StringBuilder();
            foreach (var r in rows)
            {
                var guests = Guests(r.Data); var ids = guests.Select((_, i) => $"{r.B.Id:N}"[..12] + $"-{i + 1}").ToList();
                if (r.A == d)
                    for (int i = 0; i < guests.Count; i++)
                    {
                        var g = guests[i]; var born = Place(g.Str("birth_place"))!.Value; var home = Place(g.Str("residence"))!.Value;
                        arrivals.Append($"<arrivo><idswh>{ids[i]}</idswh><tipoalloggiato>{g.Str("kind")}</tipoalloggiato><idcapo>{(g.Str("kind") is "19" or "20" ? ids[0] : "")}</idcapo>"
                            + $"<sesso>{g.Str("sex")}</sesso><cittadinanza>{Place(g.Str("citizenship"))!.Value.Code}</cittadinanza><statoresidenza>{(home.Province != "ES" ? "100000100" : home.Code)}</statoresidenza>"
                            + $"<luogoresidenza>{(home.Province != "ES" ? home.Code : "")}</luogoresidenza><datanascita>{g.Str("birth_date").Replace("-", "")}</datanascita>"
                            + $"<statonascita>{(born.Province != "ES" ? "100000100" : born.Code)}</statonascita><comunenascita>{(born.Province != "ES" ? born.Code : "")}</comunenascita>"
                            + $"<tipoturismo>{X(r.Data.Str("tourism") != "" ? r.Data.Str("tourism") : "NON SPECIFICATO")}</tipoturismo><mezzotrasporto>{X(r.Data.Str("transport") != "" ? r.Data.Str("transport") : "NON SPECIFICATO")}</mezzotrasporto><canaleprenotazione>{CHANNELS.GetValueOrDefault(r.B.Origin ?? "", "NON SPECIFICATO")}</canaleprenotazione></arrivo>");
                    }
                if (r.Z == d) for (int i = 0; i < guests.Count; i++) departures.Append($"<partenza><idswh>{ids[i]}</idswh><tipoalloggiato>{guests[i].Str("kind")}</tipoalloggiato><arrivo>{r.A:yyyyMMdd}</arrivo></partenza>");
            }
            o.Add((arrivals.Length > 0 ? "<arrivi>" + arrivals + "</arrivi>" : "") + (departures.Length > 0 ? "<partenze>" + departures + "</partenze>" : "") + "</movimento>");
        }
        o.Add("</movimenti>");
        return (string.Join("\n", o) + "\n", missing);
    }

    // ---- the tourist tax
    static int Age(DateOnly born, DateOnly on) => on.Year - born.Year - ((on.Month, on.Day).CompareTo((born.Month, born.Day)) < 0 ? 1 : 0);
    /// <summary>The rate and the most nights taxed in a stay, for a night: the first rule of the structure's category (or of
    /// every category) whose period holds it; a season (yearly) comes back every year, also across the new year.</summary>
    static (decimal? Rate, int Cap) TaxRule(Location loc, DateOnly night)
    {
        foreach (var r in Json.Arr(loc.TaxRules).OfType<JsonObject>())
        {
            if (r.Str("category") != "" && r.Str("category") != loc.TaxCategory) continue;
            var yearly = Json.Truthy(r["yearly"]); var iso = night.ToString("yyyy-MM-dd");
            var (a, z, d) = yearly ? (r.Str("from")[5..], r.Str("until")[5..], iso[5..]) : (r.Str("from"), r.Str("until"), iso);
            bool Le(string x, string y) => string.CompareOrdinal(x, y) <= 0;
            if (Le(a, z) ? Le(a, d) && Le(d, z) : Le(a, d) || Le(d, z)) return (decimal.Parse(r.Str("rate"), INV), (int?)r["nights"] ?? 0);
        }
        return (loc.TaxRate, loc.TaxNights);
    }
    static string Money(IEnumerable<decimal> rates) => string.Join("; ", rates.Select(r => r.ToString("0.00", INV).Replace('.', ',')));
    public sealed class TaxQuarter { public int Q; public Dictionary<decimal, int> Rates = new(), Reduced = new(); public int Exempt; public decimal Amount; }
    public sealed class TaxRow { public Location Location; public int Unknown; public Dictionary<string, int> Codes = new(); public List<TaxQuarter> Quarters = Enumerable.Range(1, 4).Select(q => new TaxQuarter { Q = q }).ToList(); }

    /// <summary>The tourist tax of a year by location and quarter, laid out as the annual declaration (DM 29/4/2022): nights
    /// at each ordinary rate, at each reduced rate, exempt. Each guest each night: exempt by age that night or by the reason
    /// chosen for him, reduced, or at the rate of that night; the nights of a stay after the cap are not taxed, those of
    /// workers and students also after their nights of the year. A stay without guests' data counts everyone at the
    /// ordinary rate. It is a sum, not a payment.</summary>
    public List<TaxRow> Tax(int year)
    {
        var o = new Dictionary<Guid, TaxRow>(); var order = new List<TaxRow>(); var yearly = new Dictionary<(Guid, string, string, string), int>(); var labels = EXEMPTIONS.ToDictionary(e => e.Id, e => e.Label);
        var locs = c.S.Query<Location>("SELECT * FROM core_location").Where(l => l.TaxRate.HasValue || Json.Arr(l.TaxRules).Count > 0).ToDictionary(l => l.Id);
        foreach (var b in Between(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31)).Where(b => locs.ContainsKey(b.LocationId)))
        {
            var loc = locs[b.LocationId]; var a = Local(b.Start); var n = Local(b.End).DayNumber - a.DayNumber; var guests = Guests(Data(b));
            if (!o.TryGetValue(loc.Id, out var row)) { o[loc.Id] = row = new TaxRow { Location = loc }; order.Add(row); }
            if (guests.Count == 0) row.Unknown++;
            var escorts = loc.TaxEscort > 0 ? guests.Count(g => g.Str("exemption") != "escort") / loc.TaxEscort : guests.Count;
            foreach (var g in guests.Count > 0 ? guests : Enumerable.Range(0, b.Party).Select(_ => new JsonObject()).ToList())
            {
                var code = g.Str("exemption"); DateOnly? born = g.Str("birth_date") != "" ? DateOnly.ParseExact(g.Str("birth_date"), "yyyy-MM-dd", INV) : null;
                if (code == "escort") { if (escorts > 0) escorts--; else code = ""; }  // more escorts than the town exempts: they pay
                var who = (loc.Id, Plain(g.Str("last_name")), Plain(g.Str("first_name")), g.Str("birth_date"));
                for (int i = 0; i < n; i++)
                {
                    var night = a.AddDays(i); var (rate, cap) = TaxRule(loc, night);
                    if (rate == null || cap > 0 && i >= cap) continue;
                    if (night.Year != year) continue;
                    var q = row.Quarters[(night.Month - 1) / 3];
                    var why = born.HasValue && Age(born.Value, night) < loc.TaxAge ? "Età" : code is not ("" or "reduced" or "worker" or "student") ? labels[code] : "";
                    if (code is "worker" or "student" && loc.TaxYearNights > 0)
                    {
                        if (yearly.GetValueOrDefault(who) >= loc.TaxYearNights) why = labels[code];
                        else if (why == "") yearly[who] = yearly.GetValueOrDefault(who) + 1;
                    }
                    if (why != "") { q.Exempt++; row.Codes[why] = row.Codes.GetValueOrDefault(why) + 1; }
                    else if (code == "reduced" && loc.TaxReduction > 0) { var r = Math.Round(rate.Value * (100 - loc.TaxReduction) / 100, 2, MidpointRounding.ToEven); q.Reduced[r] = q.Reduced.GetValueOrDefault(r) + 1; q.Amount += r; }
                    else { q.Rates[rate.Value] = q.Rates.GetValueOrDefault(rate.Value) + 1; q.Amount += rate.Value; }
                }
            }
        }
        return order;
    }
    public static string RatesText(Dictionary<decimal, int> rates) => Money(rates.Keys);
}
