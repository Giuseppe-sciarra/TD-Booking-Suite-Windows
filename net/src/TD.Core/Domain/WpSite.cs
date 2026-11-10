using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>What the site said no to, or did not say at all (wpsync's ValueError).</summary>
public sealed class SiteException(string message) : Exception(message);

/// <summary>core/wpsync.py, the calls: only the PC calls the site, signed HMAC-SHA256 over "time.body" with the shared
/// secret. /sync sends what the booking form needs and takes the bookings made meanwhile; /archive/push and /archive/pull
/// keep the site as the central archive (the newest of each record wins, by version); restore and merge bring it back.
/// Each step is its own unit of work, as the Python's scope() blocks: the state is seen while a sync runs.</summary>
public sealed class WpSite(Db db, Crypto crypto, Guid tenant, string origin = "", IOutbox outbox = null)
{
    public const int EVERY = 120, WINDOW = 300, MAX_DAYS = 90, CHUNK = 400_000;
    static readonly TimeSpan FULL = TimeSpan.FromMinutes(15), SOON = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RUNNING = TimeSpan.FromMinutes(10);
    /// <summary>face(): the operator's photo as a 128 px JPEG data address; the server plugs in who can resize.</summary>
    public static Func<byte[], string> Face = _ => "";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly Dictionary<string, string> STATUS = new() { ["pending"] = "In attesa di conferma", ["confirmed"] = "Confermata", ["arrived"] = "Arrivato", ["in_progress"] = "In corso", ["completed"] = "Completata", ["cancelled"] = "Annullata", ["no_show"] = "Assente" };
    static readonly string[] SKIP = { "tenant_id", "pii", "consent_proof", "request_fingerprint", "email_index", "phone_index", "name_index", "tag_index", "birthday_index" };
    static readonly string[] ORDER = { "contact", "booking", "treatment", "consent", "sale", "wait" };

    T Unit<T>(Func<Ctx, T> f, Guid? actor = null)
    {
        using var c = new Ctx(db, crypto, tenant) { Actor = actor, PublicOrigin = origin, Out = outbox ?? NoOutbox.Instance };
        var r = f(c); c.Commit(); return r;
    }
    void Unit(Action<Ctx> f, Guid? actor = null) => Unit(c => { f(c); return 0; }, actor);
    JsonObject Conf() => Unit(WpSync.Conf);
    JsonObject State() => Unit(c => Json.Obj(c.Tenant.WpState));
    void State(JsonObject kw) => Unit(c => c.S.Execute("UPDATE core_tenant SET wp_state = COALESCE(wp_state, '{}'::jsonb) || @k::jsonb WHERE id=@t", new { k = Json.Dump(kw), t = tenant }));
    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
    static JsonArray List(IEnumerable<string> items) => new(items.Select(x => (JsonNode)x).ToArray());

    // ---------- the signature and the seal, as the plugin's tdbs_pc_seal / tdbs_pc_open
    public static string Sign(string secret, string ts, byte[] body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray())).ToLowerInvariant();
    public static bool Verify(string secret, JsonNode ts, byte[] body, string sig, long? now = null)
    {
        if (!long.TryParse(ts?.ToString(), out var t)) return false;
        return Math.Abs((now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - t) <= WINDOW && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, ts.ToString(), body)), Encoding.UTF8.GetBytes(sig ?? ""));
    }
    static byte[] Aes(string secret) => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, null, Encoding.UTF8.GetBytes("tdbs-wp-data"));
    /// <summary>AES-256-GCM, key HKDF-SHA256 of the shared secret, base64(iv.tag.ciphertext).</summary>
    public static string Seal(string secret, JsonNode data)
    {
        var iv = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(data.ToJsonString(Crypto.Json)); var ct = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(Aes(secret), 16); aes.Encrypt(iv, plain, ct, tag);
        return Convert.ToBase64String(iv.Concat(tag).Concat(ct).ToArray());
    }
    /// <summary>null for what does not open with this secret.</summary>
    public static JsonNode Unseal(string secret, string value)
    {
        try
        {
            var raw = Convert.FromBase64String(value ?? ""); if (raw.Length < 28) return null;
            var plain = new byte[raw.Length - 28];
            using var aes = new AesGcm(Aes(secret), 16); aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            return JsonNode.Parse(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or System.Text.Json.JsonException) { return null; }
    }

    // ---------- the card in Impostazioni e sito
    public string NewSecret(Guid? actor) => Unit(c =>
    {
        var conf = WpSync.Conf(c); conf["secret"] = Bookings.TokenUrlSafe(32); WpSync.Store(c, conf); c.Audit("wpsync.secret", tenant);
        return conf.Str("secret");
    }, actor);

    /// <summary>save(): the site's address, how many days ahead, on or off, archive, the site in charge. Returns what
    /// merge did when the archive was just turned on with the site answering.</summary>
    public JsonObject Save(JsonObject post, Guid? actor)
    {
        var url = WpSync.ValidOrigin(post.Str("url").Trim().TrimEnd('/'));
        if (url == "") throw new SiteException("Indirizzo del sito non valido: scrivilo come https://www.sito.it, senza percorsi.");
        var days = Json.Truthy(post["days"]) ? int.TryParse(post.Str("days"), out var dv) ? dv : 0 : 30;
        if (days is < 1 or > MAX_DAYS) throw new SiteException($"I giorni vanno da 1 a {MAX_DAYS}.");
        bool on = Json.Truthy(post["on"]), archive = Json.Truthy(post["archive"]), mails = Json.Truthy(post["mails"]);
        var (was, started) = Unit(c =>
        {
            var conf = WpSync.Conf(c);
            if (on && !Json.Truthy(conf["secret"])) throw new SiteException("Prima genera la chiave e incollala nel plugin.");
            if (archive && !on) throw new SiteException("Il sito fa da archivio solo con la sincronizzazione attiva.");
            if (mails && !archive) throw new SiteException("Il sito gestisce prenotazioni ed email solo come archivio centrale: accendi anche quello.");
            var first = mails && !Json.Truthy(conf["mails"]);
            // since: what changed before the site took over was already mailed by the PC (the site keeps quiet on it)
            conf["url"] = url; conf["days"] = days; conf["on"] = on; conf["mails"] = mails; if (first) conf["mails_since"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            WpSync.Store(c, conf); var old = c.Tenant.WpArchive;
            c.S.Execute("UPDATE core_tenant SET wp_archive=@a, wp_state = COALESCE(wp_state, '{}'::jsonb) || '{\"sent\":\"\"}'::jsonb WHERE id=@t", new { a = archive, t = tenant });  // the next call sends the free times again
            return (old, first);
        });
        JsonObject got = null;
        if (archive && !was)
        {
            // the first time the PC and the site line up, the newest of each record wins; with the site unreachable
            // everything is queued as before, and the site still keeps what was changed there later
            try { got = Merge(actor); } catch (SiteException) { UploadAll(); }
        }
        Unit(c =>
        {
            if (was && !archive) c.S.Execute("DELETE FROM core_siteoutbox");
            // the bookings to come, to the site again: it reminds them
            if (started) WpSync.Touch(c.S, tenant, "booking", c.S.Query<Guid>("SELECT id FROM core_booking WHERE start>@n", new { n = c.Now }).Select(x => x.ToString()), stamp: false);
            c.Audit("wpsync.saved", tenant, new { site = url, on, archive, mails });
        }, actor);
        return got;
    }

    // ---------- what the site shows with the PC off
    JsonObject Snapshot(Ctx c, int days)
    {
        var bk = new Bookings(c); var today = c.Today; var services = new JsonArray();
        foreach (var s in c.S.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND l.active AND s.secret='' AND s.kind='resource' ORDER BY s.name").ToList())
        {
            // shortcut: the service's own questions are not on the site, a service that needs one stays off it
            if ((JsonNode.Parse(string.IsNullOrEmpty(s.Fields) ? "[]" : s.Fields) as JsonArray ?? new JsonArray()).Any(f => Json.Truthy(f?["required"]))) continue;
            var location = c.Row<Location>(s.LocationId);
            var options = c.S.Query<Option>("SELECT * FROM core_option WHERE service_id=@s AND active ORDER BY name", new { s = s.Id }).ToList(); var people = bk.Linked(s);
            var slots = new JsonObject();
            // shortcut: the bookings of a day are read again for each variant (the Python keeps them in a memo), fine for a shop's catalogue
            foreach (var o in new Option[] { null }.Concat(options))  // also without variant (key ""): a service added to a visit goes at its own duration
                foreach (var r in people)
                    for (int n = 0; n < days; n++)
                    {
                        var d = today.AddDays(n);
                        var t = bk.Available(s.Id.ToString(), d, o != null ? (JsonNode)o.Id.ToString() : null, r.Id.ToString()).Select(x => $"{c.Cal.ToLocal(x.StartUtc):HH:mm}-{c.Cal.ToLocal(x.EndUtc):HH:mm}").ToList();
                        if (t.Count == 0) continue;
                        var byOption = slots[o?.Id.ToString() ?? ""] as JsonObject; if (byOption == null) slots[o?.Id.ToString() ?? ""] = byOption = new JsonObject();
                        var byPerson = byOption[r.Id.ToString()] as JsonObject; if (byPerson == null) byOption[r.Id.ToString()] = byPerson = new JsonObject();
                        byPerson[d.ToString("yyyy-MM-dd")] = List(t);
                    }
            services.Add(new JsonObject
            {
                ["id"] = s.Id.ToString(), ["name"] = s.Name, ["description"] = s.Description, ["location"] = location.Name, ["picks"] = s.ClientPicks,
                // more services in one visit on the site: of the same location, with the same operator or not, waiting at most this
                ["location_id"] = s.LocationId.ToString(), ["split"] = location.VisitSplit, ["wait"] = location.VisitWait,
                // for the emails the site writes itself: a request waits for the business's yes; the client cancels until cutoff minutes before
                ["approval"] = s.RequestAll, ["cutoff"] = s.CancelCutoffMinutes, ["phone"] = location.Phone,
                ["options"] = new JsonArray(options.Select(o => (JsonNode)new JsonObject { ["id"] = o.Id.ToString(), ["name"] = o.Name, ["duration"] = o.Duration }).ToArray()),
                ["resources"] = new JsonArray(people.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id.ToString(), ["name"] = r.Name }).ToArray()), ["slots"] = slots,
            });
        }
        var phone = c.S.Scalar<string>("SELECT phone FROM core_location WHERE active AND phone<>'' ORDER BY name LIMIT 1") ?? "";
        var reminder = Json.Truthy(ClientMails.Current(c.Tenant)["reminder"]?["on"]);
        // for the emails the site in charge decides itself: the instructions before the arrival and the recall of every service
        var extra = new JsonObject();
        foreach (var s in c.S.Query<Service>("SELECT * FROM core_service WHERE recall_days>0 OR instructions<>''")) extra[s.Id.ToString()] = new JsonObject { ["instructions"] = s.Instructions, ["recall"] = s.RecallDays };
        return new JsonObject
        {
            ["v"] = 1, ["name"] = c.Tenant.Name, ["timezone"] = c.Tenant.Timezone, ["generated"] = Times.PyIso(c.Now), ["days"] = days, ["phone"] = phone, ["services"] = services, ["extra"] = extra, ["master"] = Master(c), ["color"] = c.Tenant.BrandColor,
            ["texts"] = new JsonObject
            {
                ["privacy"] = c.Tenant.PrivacyText, ["consent"] = c.Tenant.ConsentText,
                ["notices"] = reminder ? "Mandami per email la conferma, il promemoria e gli avvisi su questa prenotazione" : "Mandami per email la conferma e gli avvisi su questa prenotazione",
            },
        };
    }
    /// <summary>For the site: in charge or not, and since when (the archive's records changed before are not mailed by it).</summary>
    static JsonObject Master(Ctx c) => WpSync.BySite(c) ? new JsonObject { ["on"] = true, ["since"] = long.TryParse(WpSync.Conf(c).Str("mails_since"), out var v) ? v : 0 } : new JsonObject { ["on"] = false };

    /// <summary>row(): a booking as the private agenda of the site shows it.</summary>
    static JsonObject Row(Ctx c, Booking b, JsonObject d)
    {
        var option = b.OptionId is Guid o ? c.Row<Option>(o) : null; var resource = b.ResourceId is Guid r ? c.Row<Resource>(r) : null;
        return new JsonObject
        {
            ["start"] = c.Cal.ToLocal(b.Start).ToString("yyyy-MM-dd'T'HH:mm"), ["end"] = c.Cal.ToLocal(b.End).ToString("yyyy-MM-dd'T'HH:mm"),
            ["service"] = c.Row<Service>(b.ServiceId).Name + (option != null ? " · " + option.Name : ""), ["resource"] = b.ResourceId?.ToString() ?? "", ["operator"] = resource?.Name ?? "",
            ["status"] = STATUS[b.Status], ["pending"] = b.Status == "pending", ["client"] = (d.Str("first_name") + " " + d.Str("last_name")).Trim(), ["phone"] = d.Str("phone"), ["notes"] = d.Str("notes"),
        };
    }
    /// <summary>agenda(): the private agenda of the site, read-only with the PC off: the bookings of the next days from today
    /// (the cancelled ones out), with client, phone and notes, sealed; and the operators with their colour and photo.</summary>
    string SiteAgenda(Ctx c, JsonObject conf)
    {
        var bk = new Bookings(c); var start = c.Cal.Local(c.Today, TimeSpan.Zero)!.Value; var days = conf["days"] != null ? Till.Int(conf["days"]) : 30;
        var rows = c.S.Query<Booking>("SELECT * FROM core_booking WHERE start>=@a AND start<@b AND status<>'cancelled' ORDER BY start", new { a = start, b = start.AddDays(days) }).Select(b => (JsonNode)Row(c, b, bk.Data(b))).ToArray();
        var ops = c.S.Query<Resource>("SELECT * FROM core_resource WHERE active ORDER BY name").ToList(); var color = Agenda.Colors(ops);
        string Photo(Resource r) => r.PhotoAt == null ? "" : c.S.Scalar<byte[]>("SELECT data FROM core_resourcephoto WHERE resource_id=@r LIMIT 1", new { r = r.Id }) is byte[] data ? Face(data) : "";
        return Seal(conf.Str("secret"), new JsonObject
        {
            ["generated"] = Times.PyIso(c.Now), ["timezone"] = c.Tenant.Timezone,
            ["operators"] = new JsonArray(ops.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id.ToString(), ["name"] = r.Name, ["color"] = color[r.Id], ["photo"] = Photo(r) }).ToArray()), ["bookings"] = new JsonArray(rows),
        });
    }

    /// <summary>take(): the site's bookings, each one once (request_key wp-id); refused now, a request at the same time; one
    /// that cannot be kept at all is acked too and its client told why (by the site when it is in charge). Source "agenda":
    /// written by the staff in the site's agenda, a booking of the desk.</summary>
    (List<string> New, List<string> Ack, List<string> Errors, JsonArray Refused) Take(JsonNode rows)
    {
        var made = new List<string>(); var ack = new List<string>(); var errors = new List<string>(); var refused = new JsonArray(); var site = Unit(WpSync.BySite);
        foreach (var r in (rows as JsonArray ?? new JsonArray()).Take(200).OfType<JsonObject>())
        {
            var sid = r.Str("id"); if (!Regex.IsMatch(sid, @"^[a-f0-9]{32}\z")) continue;
            var key = "wp-" + sid; ack.Add(sid); var staff = r.Str("source") == "agenda";
            if (Unit(c => c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_booking WHERE request_key=@k)", new { k = key }))) continue;
            if (!DateOnly.TryParseExact(r.Str("date"), "yyyy-MM-dd", out var day) || !TimeOnly.TryParseExact(r.Str("time"), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock)
                || Unit(c => c.Cal.Local(day, clock.ToTimeSpan())) is not DateTime start) { errors.Add("una prenotazione del sito con data non valida"); continue; }
            string P(string k) => Json.Truthy(r[k]) ? r.Str(k) : "";
            // the other services of the visit, placed again here one after the other (the PC has the truth)
            var extra = (r["extra"] as JsonArray)?.OfType<JsonObject>().Select(x => x.Str("service")).ToList() ?? new List<string>();
            JsonObject Data(Ctx c)
            {
                var d = new JsonObject { ["service"] = r.Str("service"), ["option"] = P("option"), ["resource"] = Json.Truthy(r["any"]) ? "" : r.Str("resource"), ["start"] = Times.Iso(start, c.Zone), ["party"] = 1 };
                if (extra.Count > 0) d["extra"] = List(extra);
                foreach (var k in new[] { "first_name", "last_name", "email", "phone", "notes" }) d[k] = P(k);
                d["marketing"] = r["marketing"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) && mb && !staff; d["notices"] = !(r["notices"] is JsonValue nv && nv.TryGetValue<bool>(out var nb) && !nb); d["request_key"] = key;
                if (staff && Json.Truthy(r["free"])) d["force"] = true;  // Altro orario: any time, kept if nothing is in the way
                return d;
            }
            Booking Create(bool late) => Unit(c =>
            {
                c.Desk = staff; var d = Data(c);
                if (late)
                {
                    // a visit whose time went meanwhile is kept as a request of its first service, the others in the note
                    d["resource"] = r.Str("resource");
                    if (extra.Count > 0)
                    {
                        var names = c.S.Query<string>("SELECT name FROM core_service WHERE id::text = ANY(@i)", new { i = extra.Where(x => Regex.IsMatch(x, @"^[0-9a-f-]{36}\z")).ToArray() });
                        d["extra"] = new JsonArray(); d["notes"] = (d.Str("notes") + "\nDal sito con: " + string.Join(", ", names)).Trim();
                    }
                }
                return new Bookings(c).Create(d, origin: staff ? "agenda" : "wordpress", late: late).Item1;
            });
            Booking b;
            try { b = Create(false); }
            catch (BookingException)
            {
                try { b = Create(true); }
                catch (BookingException ex)
                {
                    errors.Add($"{r.Str("date")} {r.Str("time")}: {ex.Message}");
                    if (site) refused.Add(new JsonObject { ["id"] = sid, ["reason"] = Cut(ex.Message, 300) });  // the site tells the client
                    else if (P("email").Contains('@') && !staff) Unit(c => new Bookings(c).Queue(new JsonObject
                    {
                        ["to"] = P("email"), ["subject"] = "Richiesta non registrata · " + c.Tenant.Name,
                        ["body"] = $"La tua richiesta di prenotazione per il {day:dd/MM/yyyy} alle {r.Str("time")} non è stata registrata: {ex.Message}\nContatta {c.Tenant.Name} per prenotare.",
                    }));
                    continue;
                }
            }
            made.Add(b.Id.ToString());
            // the site confirmed it at once; the time was taken meanwhile here: a request now, the site tells the client
            if (site && b.Status == "pending" && !staff) refused.Add(new JsonObject { ["id"] = sid, ["kept"] = true, ["reason"] = "l'orario nel frattempo è stato occupato: l'attività ti ricontatta per confermarlo o spostarlo" });
        }
        return (made, ack, errors, refused);
    }

    /// <summary>apply(): what the clients did on the site's links, done here (the site sent the emails): cancelled, confirmed
    /// (Confermo), declined inside the cutoff (Non vengo, the business decides). Returns the ids to ack.</summary>
    List<string> Apply(JsonNode changes)
    {
        var done = new List<string>();
        foreach (var ch in (changes as JsonArray ?? new JsonArray()).Take(500).OfType<JsonObject>())
        {
            var cid = ch.Str("id"); if (!Regex.IsMatch(cid, @"^[0-9]{1,18}\z")) continue;
            done.Add(cid); var reference = ch.Str("ref"); var what = ch.Str("action");
            try
            {
                Unit(c =>
                {
                    var b = Regex.IsMatch(reference, @"^[0-9a-f]{8}-[0-9a-f-]{27}\z") ? c.S.One<Booking>("SELECT * FROM core_booking WHERE id::text=@r", new { r = reference })
                        : Regex.IsMatch(reference, @"^wp-[a-f0-9]{32}\z") ? c.S.One<Booking>("SELECT * FROM core_booking WHERE request_key=@r LIMIT 1", new { r = reference }) : null;
                    if (b == null || b.Status is not ("pending" or "confirmed")) return;
                    var at = Cut(Json.Truthy(ch["at"]) ? ch.Str("at") : Times.PyIso(c.Now), 40);
                    if (what == "confirm") { b.Snap.Remove("declined"); b.Snap["client_confirmed"] = at; c.S.Update(b, "snapshot"); }
                    else if (what == "declined") { b.Snap.Remove("client_confirmed"); b.Snap["declined"] = at; c.S.Update(b, "snapshot"); }
                    else if (what == "cancel") new Bookings(c).ChangeStatus(b, "cancelled", reason: "client");
                    else return;
                    c.Audit("wpsync.change", b.Id, new { what });
                });
            }
            catch (BookingException) { }
        }
        return done;
    }

    /// <summary>sync(): one call to the site; how it went stays on the tenant for the card and the banner. Returns the new
    /// bookings. The free times go again when something was written in the register, every FULL, or full.</summary>
    public int Sync(bool full = false)
    {
        var conf = Conf();
        if (!(Json.Truthy(conf["on"]) && Json.Truthy(conf["url"]) && Json.Truthy(conf["secret"]))) return 0;
        var old = State(); var now = DateTime.UtcNow;
        State(new JsonObject { ["running"] = Times.PyIso(now) });
        try
        {
            // after a call that did not get through, a light call first: the free times are slow to count and a site
            // that is down must not keep the worker and the database busy every two minutes
            if (Json.Truthy(old["down"])) Call(conf, "/sync", new JsonObject { ["ack"] = new JsonArray(), ["changes_ack"] = new JsonArray(), ["refused"] = new JsonArray() });
            var (body, due, mark) = Unit(c =>
            {
                var last = c.S.One<Audit>("SELECT * FROM core_audit WHERE action NOT LIKE 'wpsync.%' ORDER BY created DESC LIMIT 1");
                var m = last == null ? "" : $"{last.Created:O}|{last.Id}";
                TimeSpan? age = old.Str("sent") != "" ? c.Now - Times.ParseAware(old.Str("sent"), out _)!.Value : null;
                var d = full || Json.Truthy(old["down"]) || age == null || age > FULL || m != old.Str("mark") && age > SOON;
                var j = new JsonObject { ["ack"] = old["ack"]?.DeepClone() ?? new JsonArray(), ["changes_ack"] = old["changes_ack"]?.DeepClone() ?? new JsonArray(), ["refused"] = old["refused"]?.DeepClone() ?? new JsonArray() };
                if (d) { j["snapshot"] = Snapshot(c, conf["days"] != null ? Till.Int(conf["days"]) : 30); j["agenda"] = SiteAgenda(c, conf); }
                return (j, d, m);
            });
            var data = Call(conf, "/sync", body);
            var (made, ack, errors, refused) = Take(data["bookings"]);
            var done = Apply(data["changes"]);  // what the clients did on the site's links
            var at = Times.PyIso(DateTime.UtcNow);
            var state = new JsonObject
            {
                ["running"] = "", ["at"] = at, ["new"] = made.Count, ["ids"] = List(made.Take(20)), ["ack"] = List(ack), ["changes_ack"] = List(done), ["refused"] = refused,
                ["error"] = Cut(string.Join("; ", errors), 500), ["down"] = false,
            };
            if (due) { state["sent"] = at; state["mark"] = mark; }
            State(state);
            if (made.Count > 0) Unit(c => c.Audit("wpsync.bookings", tenant, new { n = made.Count }));
            if (Unit(c => c.Tenant.WpArchive))
            {
                try { Push(conf); }
                catch (SiteException ex) { ArchiveError(ex.Message); }
            }
            return made.Count;
        }
        catch (SiteException ex)
        {
            // ack, sent and mark stay: again next time
            State(new JsonObject { ["running"] = "", ["at"] = Times.PyIso(DateTime.UtcNow), ["new"] = 0, ["ids"] = new JsonArray(), ["error"] = Cut(ex.Message, 500), ["down"] = true });
            return 0;
        }
    }
    void ArchiveError(string text)
    {
        var a = State()["archive"]?.DeepClone() as JsonObject ?? new JsonObject(); a["error"] = Cut(text, 500);
        State(new JsonObject { ["archive"] = a });
    }

    /// <summary>call(): one signed POST JSON to /wp-json/tdbs/v1&lt;route&gt; (the path, not ?rest_route=, so a firewall can allow
    /// it by prefix); the answer only with the site's signature.</summary>
    static JsonObject Call(JsonObject conf, string route, JsonObject payload)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString()); var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(); var secret = conf.Str("secret");
        using var req = new HttpRequestMessage(HttpMethod.Post, conf.Str("url") + "/wp-json/tdbs/v1" + route) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("application/json"); req.Headers.Accept.ParseAdd("application/json"); req.Headers.UserAgent.ParseAdd("TD-Booking-Suite");
        req.Headers.Add("X-TDBS-Time", ts); req.Headers.Add("X-TDBS-Signature", Sign(secret, ts, body));
        byte[] raw; int code;
        try
        {
            using var res = http.Send(req, HttpCompletionOption.ResponseHeadersRead); code = (int)res.StatusCode;
            using var stream = res.Content.ReadAsStream(); using var ms = new MemoryStream(); var buf = new byte[81920]; int n;
            var most = res.IsSuccessStatusCode ? 10_000_000 : 4000;
            while (ms.Length < most && (n = stream.Read(buf, 0, (int)Math.Min(buf.Length, most - ms.Length))) > 0) ms.Write(buf, 0, n);
            raw = ms.ToArray();
            if (!res.IsSuccessStatusCode)
            {
                string said = ""; try { said = (JsonNode.Parse(raw) as JsonObject)?.Str("error") ?? ""; } catch (System.Text.Json.JsonException) { }
                throw new SiteException(code is 401 or 403 ? "il sito non riconosce la chiave: incollala di nuovo nel plugin"
                    : said.TrimEnd('.').ToLowerInvariant() is var s && s != "" ? s
                    : code == 404 ? "nel sito manca il plugin TD Booking Suite in modalità PC, o la chiave non è ancora nel plugin" : $"il sito ha risposto con l'errore {code}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            throw new SiteException($"sito non raggiungibile ({(ex is TaskCanceledException ? "tempo scaduto" : ex.InnerException?.Message ?? ex.Message)})");
        }
        byte[] inner; JsonObject env;
        try { env = JsonNode.Parse(raw) as JsonObject; inner = Encoding.UTF8.GetBytes(env["body"].ToString()); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NullReferenceException) { throw new SiteException("il sito ha risposto in modo non valido"); }
        if (!Verify(secret, env["time"], inner, env["sig"]?.ToString())) throw new SiteException("risposta del sito senza la firma giusta: controlla la chiave nel plugin");
        return JsonNode.Parse(inner) as JsonObject ?? new JsonObject();
    }

    // ---------- the site as the central archive
    /// <summary>ver: the microseconds since 1970 UTC of a moment, exact (wall-clock times are never compared).</summary>
    static string Ver(string expr) => $"(extract(epoch from date_trunc('second', {expr}))::bigint * 1000000 + (extract(microseconds from {expr})::bigint % 1000000))";
    static string Stamp(string kind) => kind == "consent" ? "at" : "created";
    static string Moment(long v) => DateTime.UnixEpoch.AddTicks(v * 10).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
    /// <summary>kept(): what the site must have of each kind (pack gives no empty record for these).</summary>
    static string Kept(string kind) => kind switch
    {
        "contact" => "NOT starts_with(t.email_index, @e)",
        "consent" => "NOT EXISTS (SELECT 1 FROM core_contact k WHERE k.id=t.contact_id AND starts_with(k.email_index, @e) AND k.merged_into_id IS NULL)",
        "sale" => "t.kind IN ('sale','void')",
        _ => "true",
    };

    /// <summary>pack(): what the site keeps of each record, its fields as they are; null for what it must no longer have: a
    /// record gone, an erased or merged card, the consent history of an erased card, a test of the register.</summary>
    static Dictionary<string, (JsonObject Rec, long? Ver)> Pack(Ctx c, string kind, string[] refs)
    {
        var table = WpSync.KINDS[kind]; var outp = refs.ToDictionary(r => r, _ => ((JsonObject)null, (long?)null));
        c.S.Execute("SELECT set_config('TimeZone', @z, true)", new { z = c.Tenant.Timezone });  // the dates as the business reads them, with their offset
        var numeric = Schema.Columns[table].Where(x => x.Type == "numeric").Select(x => x.Column).ToList();
        var bk = new Bookings(c);
        var bookings = kind == "booking" ? c.S.Query<Booking>("SELECT * FROM core_booking WHERE id::text = ANY(@r)", new { r = refs }).ToDictionary(b => b.Id.ToString()) : null;
        foreach (var (id, json, v) in c.S.Query<(string, string, long)>($"SELECT t.id::text, to_jsonb(t)::text, {Ver($"coalesce(t.updated, t.{Stamp(kind)})")} FROM {table} t WHERE t.id::text = ANY(@r)", new { r = refs }))
        {
            var row = (JsonObject)JsonNode.Parse(json); JsonObject o = new();
            foreach (var (k, val) in row) if (!SKIP.Contains(k)) o[k] = numeric.Contains(k) && val != null ? val.ToJsonString() : val?.DeepClone();  // a price as "25.00", as str(Decimal)
            switch (kind)
            {
                case "contact":
                    if (row.Str("email_index").StartsWith(Clients.ERASED)) o = null;
                    else { o["data"] = c.Decrypt(row.Str("pii"), "contact:" + id); if (row.Str("consent_proof") != "") o["proof"] = c.Decrypt(row.Str("consent_proof"), "consent:" + id); }
                    break;
                case "consent":
                    {
                        var card = c.S.One<Contact>("SELECT * FROM core_contact WHERE id=@i", new { i = Guid.Parse(row.Str("contact_id")) });
                        if (card.EmailIndex.StartsWith(Clients.ERASED) && card.MergedIntoId == null) o = null;
                        else o["contact_id"] = (card.MergedIntoId ?? card.Id).ToString();  // a merged card's history goes with the card it went into
                        break;
                    }
                case "sale":
                    if (row.Str("kind") is not ("sale" or "void")) o = null;
                    else o["location"] = c.Row<Location>(Guid.Parse(row.Str("location_id"))).Name;  // the summary: lines, payments and codice fiscale stay on the PC
                    break;
                case "treatment": o["data"] = c.Decrypt(row.Str("pii"), "treatment:" + id); break;
                case "wait": o["data"] = c.Decrypt(row.Str("pii"), "wait:" + id); break;
                default:
                    {
                        var b = bookings[id]; var d = bk.Data(b); o["data"] = d;
                        o["names"] = new JsonObject
                        {
                            ["service"] = c.Row<Service>(b.ServiceId).Name, ["location"] = c.Row<Location>(b.LocationId).Name, ["resource"] = b.ResourceId is Guid r ? c.Row<Resource>(r).Name : "",
                            ["option"] = b.OptionId is Guid op ? c.Row<Option>(op).Name : "", ["secondary"] = b.SecondaryId is Guid se ? c.Row<Resource>(se).Name : "",
                        };
                        if (b.Status != "cancelled") o["view"] = Row(c, b, (JsonObject)d.DeepClone());  // what [tdbs_agenda] shows; never the client record
                        break;
                    }
            }
            outp[id] = (o, v);
        }
        return outp;
    }

    /// <summary>push(): the outbox to /archive/push in order of number, a chunk per call; what the site acks leaves the
    /// outbox (a record changed meanwhile has a new number and goes again). Returns how many acked.</summary>
    int Push(JsonObject conf, int calls = 20)
    {
        var sent = 0; var secret = conf.Str("secret");
        for (int i = 0; i < calls; i++)
        {
            var ops = Unit(c =>
            {
                var rows = c.S.Query<Siteoutbox>("SELECT * FROM core_siteoutbox ORDER BY seq LIMIT 500").ToList(); var list = new JsonArray(); var size = 0;
                if (rows.Count == 0) return list;
                var objs = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => Pack(c, g.Key, g.Select(r => r.Ref).ToArray()));
                foreach (var r in rows)
                {
                    var (rec, ver) = objs[r.Kind][r.Ref];
                    // v, the version the site compares (a record gone: the moment it was queued, its number comes from the clock)
                    var op = new JsonObject { ["kind"] = r.Kind, ["ref"] = r.Ref, ["seq"] = r.Seq, ["v"] = ver ?? r.Seq, ["day"] = rec != null && r.Kind == "booking" ? Cut(rec.Str("start"), 10) : "", ["data"] = rec != null ? Seal(secret, rec) : "" };
                    size += op.Str("data").Length + 200;
                    if (list.Count > 0 && size > CHUNK) break;
                    list.Add(op);
                }
                return list;
            });
            if (ops.Count == 0) break;
            var data = Call(conf, "/archive/push", new JsonObject { ["ops"] = ops });
            var acked = (data["acked"] as JsonArray ?? new JsonArray()).OfType<JsonValue>().Where(x => x.TryGetValue<long>(out _)).Select(x => x.GetValue<long>()).ToArray();
            Unit(c => c.S.Execute("DELETE FROM core_siteoutbox WHERE seq = ANY(@a)", new { a = acked }));
            sent += acked.Length;
            State(new JsonObject { ["archive"] = new JsonObject { ["at"] = Times.PyIso(DateTime.UtcNow), ["records"] = Till.Int(data["records"]), ["size"] = long.TryParse(data.Str("size"), out var sz) ? sz : 0, ["error"] = "" } });
            if (acked.Length < ops.Count) break;  // the site took only part: the rest at the next sync
        }
        return sent;
    }

    /// <summary>Carica tutto sul sito: every record in the outbox again, with the versions they have (on the site a record
    /// changed there later stays); the next syncs send them. Returns how many.</summary>
    public long UploadAll() => Unit(c =>
    {
        c.S.Execute("DELETE FROM core_siteoutbox");
        foreach (var kind in ORDER) WpSync.Touch(c.S, tenant, kind, c.S.Query<string>($"SELECT t.id::text FROM {WpSync.KINDS[kind]} t WHERE {Kept(kind)}", new { e = Clients.ERASED }), stamp: false);
        return c.S.Scalar<long>("SELECT count(*) FROM core_siteoutbox");
    });
    /// <summary>What Carica tutto says, with the push tried at once (sync full).</summary>
    public string ArchiveErrorNow() => (State()["archive"] as JsonObject)?.Str("error") ?? "";
    public string SyncError() => State().Str("error");

    /// <summary>pages(): the rows of a kind in the site's archive, page after page; with gone also the empty ones of what is gone.</summary>
    static IEnumerable<JsonObject> Pages(JsonObject conf, string kind, bool gone = false)
    {
        var after = "";
        while (true)
        {
            var body = new JsonObject { ["kind"] = kind, ["after"] = after }; if (gone) body["gone"] = true;
            var data = Call(conf, "/archive/pull", body);
            foreach (var r in (data["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) yield return r;
            var next = Json.Truthy(data["next"]) ? data.Str("next") : "";
            if (next == "" || next == after) yield break;
            after = next;
        }
    }
    static List<JsonObject> Pull(JsonObject conf, string kind) => Pages(conf, kind).Select(r => Unseal(conf.Str("secret"), r.Str("data")) as JsonObject).Where(x => x != null).ToList();
    /// <summary>stamped(): the version of a record from its own data, for the rows a plugin before 2.20.0 saved without one.</summary>
    static long Stamped(JsonObject rec)
    {
        var s = Json.Truthy(rec["updated"]) ? rec.Str("updated") : Json.Truthy(rec["created"]) ? rec.Str("created") : rec.Str("at");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? (d.UtcTicks - DateTime.UnixEpoch.Ticks) / 10 : 0;
    }
    /// <summary>versions(): ref → (version, record) of the whole archive of a kind, record null for what is gone there.</summary>
    static Dictionary<string, (long V, JsonObject Rec)> Versions(JsonObject conf, string kind)
    {
        var outp = new Dictionary<string, (long, JsonObject)>();
        foreach (var r in Pages(conf, kind, gone: true))
        {
            var v = r["v"] is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : 0; var has = Json.Truthy(r["data"]);
            var rec = has ? Unseal(conf.Str("secret"), r.Str("data")) as JsonObject : null;
            if (has && rec == null) continue;  // does not open with this key: not ours to judge
            outp[Json.Truthy(r["ref"]) ? r.Str("ref") : ""] = (v != 0 ? v : rec != null ? Stamped(rec) : 0, rec);
        }
        return outp;
    }

    /// <summary>The archive's records written here (restore and merge): cards sealed with this PC's business key, the
    /// catalogue of this PC matched by name (sede, servizio, variante, operatore); Cards maps the site's card ids to the cards here.</summary>
    sealed class Writer
    {
        readonly Ctx c; readonly Clients cl; readonly bool here; readonly HashSet<string> missing; readonly Action lost;
        public readonly Dictionary<string, string> Cards = new();
        readonly Dictionary<(string, string), Service> byPlace = new(); readonly Dictionary<string, Service> byName = new();
        readonly Dictionary<string, Resource> people = new(); readonly Dictionary<(Guid, string), Option> options = new();
        public Writer(Ctx c, HashSet<string> missing, Action lost, bool here = false)
        {
            this.c = c; cl = new Clients(c); this.here = here; this.missing = missing; this.lost = lost;
            foreach (var s in c.S.Query<Service>("SELECT * FROM core_service ORDER BY name, id")) { byPlace[(c.Row<Location>(s.LocationId).Name, s.Name)] = s; byName.TryAdd(s.Name, s); }
            foreach (var r in c.S.Query<Resource>("SELECT * FROM core_resource ORDER BY name, id")) people[r.Name] = r;
            foreach (var o in c.S.Query<Option>("SELECT * FROM core_option ORDER BY name, id")) options[(o.ServiceId, o.Name)] = o;
        }
        static JsonObject D(JsonObject rec) => rec["data"] as JsonObject ?? new JsonObject();

        /// <summary>fill(): the record's own columns (never the links to other rows) over the row's defaults or the row here.</summary>
        static JsonObject Fields(string table, JsonObject rec, params string[] skip)
        {
            var o = new JsonObject();
            foreach (var (col, _) in Schema.Columns[table]) if (rec.ContainsKey(col) && !col.EndsWith("_id") && !SKIP.Contains(col) && !skip.Contains(col)) o[col] = rec[col]?.DeepClone();
            return o;
        }
        static JsonObject Defaults<T>() where T : new()
        {
            var row = new T(); (row as IJsonColumns)?.Flush(); var o = new JsonObject(); var props = typeof(T).GetProperties().ToDictionary(p => Scope.Snake(p.Name));
            foreach (var (col, type) in Schema.Columns[Scope.Table<T>()])
            {
                if (!props.TryGetValue(col, out var p)) continue;
                o[col] = p.GetValue(row) switch
                {
                    null => null, string s when type == "jsonb" => JsonNode.Parse(s), string s => s, bool b => b, int i => i, short sh => sh, long l => l, decimal m => m.ToString(CultureInfo.InvariantCulture),
                    DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture), var x => x.ToString(),
                };
            }
            return o;
        }
        void Insert<T>(JsonObject fields, JsonObject set) where T : new()
        {
            var j = Defaults<T>(); foreach (var (k, v) in fields) j[k] = v?.DeepClone(); foreach (var (k, v) in set) j[k] = v?.DeepClone(); j["tenant_id"] = c.Tenant.Id.ToString();
            var table = Scope.Table<T>();
            c.S.Execute($"INSERT INTO {table} SELECT (jsonb_populate_record(null::{table}, @j::jsonb)).*", new { j = Json.Dump(j) });
        }
        void Update(string table, string id, JsonObject fields, JsonObject set)
        {
            var j = new JsonObject(); foreach (var (k, v) in fields) if (k != "id") j[k] = v?.DeepClone(); foreach (var (k, v) in set) j[k] = v?.DeepClone();
            var cols = string.Join(",", j.Select(x => "\"" + x.Key + "\""));
            c.S.Execute($"UPDATE {table} t SET ({cols}) = (SELECT {cols} FROM jsonb_populate_record(null::{table}, @j::jsonb)) WHERE t.id::text=@id", new { j = Json.Dump(j), id });
        }

        /// <summary>The key of the site's card here, and the card already here with it (the same person), if any.</summary>
        public (string Key, Contact Hit) CardKey(JsonObject rec)
        {
            var d = D(rec); var key = d.Str("email") != "" || d.Str("phone") != "" ? cl.KeyFor(d) : "restored:" + rec.Str("id");
            Contact By(string k) => c.S.One<Contact>("SELECT * FROM core_contact WHERE email_index=@k LIMIT 1", new { k });
            var hit = By(key);
            if (hit != null && d.Str("email") == "" && !cl.SamePerson(hit, d)) { key = cl.KeyFor(d, shared: true); hit = By(key); }
            return (key, hit);
        }
        /// <summary>A card made again (id null) or written over with the site's.</summary>
        public void Contact(JsonObject rec, string id, string updated)
        {
            var pk = rec.Str("id"); var d = D(rec); var (key, hit) = CardKey(rec);
            var set = new JsonObject { ["consent_proof"] = Json.Truthy(rec["proof"]) ? c.Encrypt(rec["proof"], "consent:" + pk) : "", ["updated"] = updated };
            if (id == null) { set["email_index"] = key; set["pii"] = ""; Insert<Contact>(Fields("core_contact", rec), set); }
            else
            {
                if (hit == null || hit.Id.ToString() == id) set["email_index"] = key;  // shortcut: an email that is now another card's here keeps the old key
                Update("core_contact", id, Fields("core_contact", rec), set);
            }
            var ct = c.S.One<Contact>("SELECT * FROM core_contact WHERE id::text=@i", new { i = id ?? pk });
            cl.SealCard(ct, d); cl.SaveCard(ct);
            c.S.Execute("UPDATE core_contact SET updated=@u::timestamptz WHERE id=@i", new { u = updated, i = ct.Id });  // the site's version, so it does not go back
        }
        /// <summary>A booking made again or written over; false when its service is not here (counted as lost).</summary>
        public bool Booking(JsonObject rec, string id, string updated)
        {
            var pk = rec.Str("id"); var n = rec["names"] as JsonObject ?? new JsonObject();
            var s = byPlace.GetValueOrDefault((n.Str("location"), n.Str("service"))) ?? byName.GetValueOrDefault(n.Str("service"));
            if (s == null) { missing.Add("servizio " + n.Str("service")); lost(); return false; }
            var option = options.GetValueOrDefault((s.Id, n.Str("option"))); var resource = people.GetValueOrDefault(n.Str("resource")); var secondary = people.GetValueOrDefault(n.Str("secondary"));
            foreach (var (name, got, label) in new[] { ("resource", (object)resource, "operatore"), ("option", option, "variante"), ("secondary", secondary, "risorsa") })
                if (n.Str(name) != "" && got == null) missing.Add($"{label} {n.Str(name)}");
            var had = id == null ? null : c.S.Scalar<string>("SELECT contact_id::text FROM core_booking WHERE id::text=@i", new { i = id });
            // shortcut: event dates, tables and other cabins are not matched, the booking comes back without them
            var set = new JsonObject
            {
                ["service_id"] = s.Id.ToString(), ["location_id"] = s.LocationId.ToString(), ["option_id"] = option?.Id.ToString(), ["resource_id"] = resource?.Id.ToString(), ["secondary_id"] = secondary?.Id.ToString(),
                ["contact_id"] = Card(Json.Truthy(rec["contact_id"]) ? rec.Str("contact_id") : "", had), ["pii"] = c.Encrypt(D(rec), "booking:" + pk), ["request_fingerprint"] = "", ["updated"] = updated,
            };
            var fields = Fields("core_booking", rec, "more", "tables");
            if (id == null) Insert<Booking>(fields, set); else Update("core_booking", id, fields, set);
            return true;
        }
        /// <summary>A client record or a consent event made again (or a client record written over); false without its card here.</summary>
        public bool Child(string kind, JsonObject rec, string id, string updated)
        {
            var pk = rec.Str("id"); var table = WpSync.KINDS[kind];
            var had = id == null ? null : c.S.Scalar<string>($"SELECT contact_id::text FROM {table} WHERE id::text=@i", new { i = id });
            var card = Card(Json.Truthy(rec["contact_id"]) ? rec.Str("contact_id") : "", had); if (card == null) return false;
            var set = new JsonObject { ["contact_id"] = card, ["updated"] = updated };
            if (kind == "treatment")
            {
                var booking = Json.Truthy(rec["booking_id"]) ? rec.Str("booking_id") : "";
                set["booking_id"] = booking != "" && c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_booking WHERE id::text=@b)", new { b = booking }) ? booking : null;
                set["pii"] = c.Encrypt(D(rec), "treatment:" + pk);
            }
            var fields = Fields(table, rec);
            if (id != null) Update(table, id, fields, set); else if (kind == "treatment") Insert<Treatment>(fields, set); else Insert<Consentevent>(fields, set);
            return true;
        }
        /// <summary>The card here of the site's card id; for merge (here) also a card here with that id, else the one it had.</summary>
        string Card(string siteId, string had)
        {
            if (Cards.TryGetValue(siteId, out var got) || !here) return got;
            return siteId != "" && c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_contact WHERE id::text=@i)", new { i = siteId }) ? siteId : had;
        }
    }

    JsonObject Ready(bool archive = false)
    {
        var conf = Conf();
        if (!(Json.Truthy(conf["url"]) && Json.Truthy(conf["secret"]))) throw new SiteException("Prima scrivi l'indirizzo del sito e incolla la chiave nel plugin.");
        if (archive && !Unit(c => c.Tenant.WpArchive)) throw new SiteException("Accendi prima Sito come archivio centrale.");
        return conf;
    }
    static bool Exists(Ctx c, string table, string id) => c.S.Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {table} WHERE id::text=@i)", new { i = id });

    /// <summary>Ripristina dal sito: the cards, bookings, client records and consent history of the archive made again here
    /// with this PC's business key, the catalogue matched by name. What is already here is skipped, so a second time adds
    /// nothing; into a business with clients or bookings only with confirm. Returns the counts and the names missing.</summary>
    public JsonObject Restore(Guid? actor, bool confirm = false)
    {
        var conf = Ready();
        if (!confirm && Unit(c => c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_contact) OR EXISTS(SELECT 1 FROM core_booking)")))
            throw new SiteException("Ci sono già clienti o prenotazioni: spunta la conferma per aggiungere quelli del sito.");
        var got = new[] { "contact", "booking", "treatment", "consent" }.ToDictionary(k => k, k => Pull(conf, k));
        int contacts = 0, bookings = 0, treatments = 0, consents = 0, skipped = 0, lost = 0; var missing = new HashSet<string>();
        WpSync.Quiet.Value = true;  // already on the site: not sent back
        try
        {
            Unit(c =>
            {
                var w = new Writer(c, missing, () => lost++);
                foreach (var rec in got["contact"])
                {
                    var pk = rec.Str("id");
                    if (Exists(c, "core_contact", pk)) { w.Cards[pk] = pk; skipped++; continue; }
                    var (_, hit) = w.CardKey(rec);
                    if (hit != null) { w.Cards[pk] = hit.Id.ToString(); skipped++; continue; }  // the same person already here: the bookings go on that card
                    w.Contact(rec, null, Json.Truthy(rec["updated"]) ? rec.Str("updated") : null); w.Cards[pk] = pk; contacts++;
                }
                foreach (var rec in got["booking"])
                {
                    if (c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_booking WHERE id::text=@i OR request_key=@k)", new { i = rec.Str("id"), k = rec.Str("request_key") })) { skipped++; continue; }
                    if (w.Booking(rec, null, Json.Truthy(rec["updated"]) ? rec.Str("updated") : null)) bookings++;
                }
                foreach (var kind in new[] { "treatment", "consent" })
                    foreach (var rec in got[kind])
                    {
                        if (Exists(c, WpSync.KINDS[kind], rec.Str("id")) || !w.Child(kind, rec, null, Json.Truthy(rec["updated"]) ? rec.Str("updated") : null)) { skipped++; continue; }
                        if (kind == "treatment") treatments++; else consents++;
                    }
                c.Audit("archive.restored", tenant, new { contacts, bookings, treatments, consents, skipped, lost });
            }, actor);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { throw new SiteException("questi dati sono già in un'altra attività di questo programma: il ripristino è per un PC nuovo"); }  // the ids are kept
        finally { WpSync.Quiet.Value = false; }
        return new JsonObject { ["contacts"] = contacts, ["bookings"] = bookings, ["treatments"] = treatments, ["consents"] = consents, ["skipped"] = skipped, ["lost"] = lost, ["missing"] = List(missing.OrderBy(x => x, StringComparer.Ordinal)) };
    }

    /// <summary>Sincronizza con il sito: every record of the archive against the one here, by id, and the newest wins on both
    /// sides, by version, never by the clocks' hours. Only on the site: made here. Older here: written over with the
    /// site's, keeping its version. Newer here, or not on the site: queued and pushed. Gone on the site: kept here, only
    /// counted. The receipts' summary and the consent history only go up. Error when the push did not get through.</summary>
    public JsonObject Merge(Guid? actor)
    {
        var conf = Ready(archive: true);
        var site = ORDER.ToDictionary(k => k, k => Versions(conf, k));
        int added = 0, updatedHere = 0, same = 0, tombstones = 0, lost = 0; var missing = new HashSet<string>(); var go = ORDER.ToDictionary(k => k, _ => new List<string>());
        WpSync.Quiet.Value = true;  // what comes from the site is not sent back
        try
        {
            Unit(c =>
            {
                var w = new Writer(c, missing, () => lost++, here: true);
                foreach (var kind in ORDER)
                {
                    var table = WpSync.KINDS[kind];
                    var here = c.S.Query<(string, long)>($"SELECT t.id::text, {Ver($"coalesce(t.updated, t.{Stamp(kind)})")} FROM {table} t").ToDictionary(x => x.Item1, x => x.Item2);
                    var ok = c.S.Query<string>($"SELECT t.id::text FROM {table} t WHERE {Kept(kind)}", new { e = Clients.ERASED }).ToHashSet();  // without the erased cards and their history
                    foreach (var (reference, (sv, rec)) in site[kind])
                    {
                        long? lv = here.TryGetValue(reference, out var l) ? l : null;
                        if (kind == "contact" && lv != null && rec != null) w.Cards[reference] = reference;
                        if (rec == null)  // gone on the site
                        {
                            if (ok.Contains(reference) && lv > sv) go[kind].Add(reference);
                            else if (ok.Contains(reference)) tombstones++;
                            else same++;
                        }
                        else if (lv == null)  // only on the site
                        {
                            var stamp = sv != 0 ? Moment(sv) : null; bool made;
                            if (kind == "contact")
                            {
                                var (_, hit) = w.CardKey(rec);
                                if (hit != null) { w.Cards[reference] = hit.Id.ToString(); same++; continue; }
                                w.Contact(rec, null, stamp); made = true;
                            }
                            else if (kind == "booking")
                            {
                                if (c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_booking WHERE request_key=@k)", new { k = rec.Str("request_key") })) { same++; continue; }
                                made = w.Booking(rec, null, stamp);
                            }
                            else if (kind is "sale" or "wait") continue;  // shortcut: the receipts are the register's, on the PC (the site has only their summary); the waiting list lives on the PC too
                            else { made = w.Child(kind, rec, null, stamp); if (!made) lost++; }
                            if (!made) continue;
                            added++; if (kind == "contact") w.Cards[reference] = reference;
                        }
                        else if (lv > sv || !ok.Contains(reference))  // newer here, or erased here: what is here goes (never the site's data on an erased card)
                        {
                            if (lv > sv) go[kind].Add(reference); else same++;
                        }
                        else if (lv < sv && kind is "contact" or "booking" or "treatment")  // newer on the site: written here
                        {
                            if (kind == "contact") w.Contact(rec, reference, Moment(sv));
                            else if (!(kind == "booking" ? w.Booking(rec, reference, Moment(sv)) : w.Child(kind, rec, reference, Moment(sv)))) continue;
                            updatedHere++;
                        }
                        else same++;
                    }
                    go[kind].AddRange(ok.Where(r => !site[kind].ContainsKey(r)).OrderBy(r => r, StringComparer.Ordinal));  // not on the site yet
                }
                c.Audit("archive.merged", tenant, new { added, updated_here = updatedHere, same, tombstones, lost, sent = go.Values.Sum(x => x.Count) });
            }, actor);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { throw new SiteException("questi dati sono già in un'altra attività di questo programma"); }
        finally { WpSync.Quiet.Value = false; }
        Unit(c => { foreach (var (kind, refs) in go) WpSync.Touch(c.S, tenant, kind, refs, stamp: false); });
        var error = "";
        try { Push(conf); } catch (SiteException ex) { error = Cut(ex.Message, 500); ArchiveError(error); }
        return new JsonObject
        {
            ["added"] = added, ["updated_here"] = updatedHere, ["sent"] = go.Values.Sum(x => x.Count), ["same"] = same, ["tombstones"] = tombstones, ["lost"] = lost,
            ["missing"] = List(missing.OrderBy(x => x, StringComparer.Ordinal)), ["error"] = error,
        };
    }

    /// <summary>run(): the worker, every EVERY seconds: each business with the site linked this way.</summary>
    public static int Run(Db db, Crypto crypto, string origin, IOutbox outbox)
    {
        List<Guid> ids; using (var g = db.Global()) ids = g.Query<Guid>("SELECT id FROM core_tenant WHERE wp_sync<>''").ToList();
        var done = 0;
        foreach (var id in ids)
        {
            bool open; using (var c = new Ctx(db, crypto, id)) open = c.Operational();
            if (open) done += new WpSite(db, crypto, id, origin, outbox).Sync();
        }
        return done;
    }
}
