using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/clients.py, the card of a client: its data, notes, fiscal data, tags and preferences, the signed
/// consents, the discount, the numbers of the visits and the record of what was done (treatments).</summary>
public sealed partial class Clients
{
    /// <summary>update(): a new email or phone moves the contact's key, unless another client already has it.</summary>
    public JsonObject Update(Contact contact, JsonObject fields = null, string notes = null)
    {
        var data = Data(contact);
        if (fields != null)
        {
            var d = Clean(fields); var key = KeyFor(d);
            bool Taken(string k) => c.S.Scalar<long>("SELECT count(*) FROM core_contact WHERE email_index=@k AND id<>@id", new { k, id = contact.Id }) > 0;
            // a phone shared with another person keeps both cards, told apart by the name
            if (d.Str("email") == "" && key != contact.EmailIndex && Taken(key)) key = KeyFor(d, true);
            if (key != contact.EmailIndex && Taken(key)) throw new BookingException("Un altro cliente ha già questa email o questo telefono.");
            foreach (var (k, v) in d) data[k] = v?.DeepClone();
            contact.EmailIndex = key;
        }
        if (notes != null) data["staff_notes"] = notes.Length > 4000 ? notes[..4000] : notes;
        SealCard(contact, data); SaveCard(contact);
        return data;
    }

    // ---------- fiscal data
    public static readonly (string Id, string Label)[] FISCAL_KINDS = { ("privato", "Privato"), ("azienda", "Azienda o ditta"), ("professionista", "Professionista") };
    public static readonly (string Key, string Label)[] FISCAL =
    {
        ("fiscal_kind", "Tipo"), ("company", "Ragione sociale"), ("tax_code", "Codice fiscale"), ("vat", "Partita IVA"), ("address", "Indirizzo"),
        ("zip", "CAP"), ("city", "Comune"), ("province", "Provincia"), ("country", "Nazione"), ("sdi", "Codice destinatario SDI"), ("pec", "PEC"),
    };
    static readonly Regex CF = new("^[A-Z]{6}[0-9LMNPQRSTUV]{2}[ABCDEHLMPRST][0-9LMNPQRSTUV]{2}[A-Z][0-9LMNPQRSTUV]{3}[A-Z]$");
    static readonly int[] ODD = { 1, 0, 5, 7, 9, 13, 15, 17, 19, 21, 2, 4, 18, 20, 11, 3, 6, 8, 12, 14, 16, 10, 22, 25, 24, 23 };
    public static bool CfOk(string v)
    {
        if (!CF.IsMatch(v)) return false;
        int N(char ch) => char.IsDigit(ch) ? ch - '0' : ch - 'A';
        return (char)('A' + v[..15].Select((ch, i) => i % 2 == 0 ? ODD[N(ch)] : N(ch)).Sum() % 26) == v[15];
    }
    /// <summary>The 11 digits of a partita IVA, the last one the check digit.</summary>
    public static bool VatOk(string v)
    {
        if (!Regex.IsMatch(v, @"^[0-9]{11}$")) return false;
        var s = v[..10].Select((ch, i) => { var n = ch - '0'; return i % 2 == 0 ? n : 2 * n - (n > 4 ? 9 : 0); }).Sum();
        return (10 - s % 10) % 10 == v[10] - '0';
    }
    public static Dictionary<string, string> CleanFiscal(JsonObject post)
    {
        var f = FISCAL.ToDictionary(x => x.Key, x => post.Str(x.Key));
        if (f.Values.Any(v => v.Any(ch => ch < 32))) throw new BookingException("Dati fiscali non validi.");
        foreach (var k in f.Keys.ToList()) f[k] = string.Join(" ", f[k].Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var k in new[] { "tax_code", "vat", "zip", "province", "country", "sdi" }) f[k] = f[k].Replace(" ", "").ToUpperInvariant();
        f["pec"] = Crypto.CaseFold(f["pec"]); if (f["country"] == "") f["country"] = "IT"; if (f["fiscal_kind"] == "") f["fiscal_kind"] = "privato";
        var it = f["country"] == "IT"; var company = f["fiscal_kind"] != "privato"; var labels = FISCAL.ToDictionary(x => x.Key, x => x.Label);
        if (!FISCAL_KINDS.Any(k => k.Id == f["fiscal_kind"])) throw new BookingException("Tipo di cliente non valido.");
        if (!company) f["company"] = "";
        foreach (var (k, limit) in new[] { ("company", 160), ("address", 200), ("city", 80), ("vat", 30), ("zip", 12), ("pec", 254) })
            if (f[k].Length > limit) throw new BookingException($"{labels[k]}: al massimo {limit} caratteri.");
        if (!Regex.IsMatch(f["country"], "^[A-Z]{2}$")) throw new BookingException("Nazione: la sigla di due lettere, per esempio IT.");
        if (f["tax_code"] != "" && !(CfOk(f["tax_code"]) || company && VatOk(f["tax_code"])))
            throw new BookingException("Codice fiscale non valido: controlla i 16 caratteri" + (company ? ", o le 11 cifre di un'azienda." : "."));
        if (f["vat"] != "" && it && !VatOk(f["vat"])) throw new BookingException("Partita IVA non valida: 11 cifre, controlla di averle scritte tutte giuste.");
        if (f["zip"] != "" && it && !Regex.IsMatch(f["zip"], "^[0-9]{5}$")) throw new BookingException("CAP non valido: 5 cifre.");
        if (f["province"] != "" && !Regex.IsMatch(f["province"], "^[A-Z]{2}$")) throw new BookingException("Provincia: la sigla di due lettere, per esempio RM.");
        if (f["sdi"] != "" && !Regex.IsMatch(f["sdi"], "^[A-Z0-9]{7}$")) throw new BookingException("Codice destinatario non valido: 7 caratteri tra lettere e numeri, o 0000000.");
        if (f["pec"] != "" && !ValidEmail(f["pec"])) throw new BookingException("PEC non valida.");
        if (f["fiscal_kind"] == "privato" && !f.Any(x => x.Key is not ("fiscal_kind" or "country") && x.Value != "")) return new();
        return f;
    }
    public void SaveFiscal(Contact contact, Dictionary<string, string> f)
    {
        var data = Data(contact);
        foreach (var (k, _) in FISCAL) { if (f.GetValueOrDefault(k, "") != "") data[k] = f[k]; else data.Remove(k); }
        SealCard(contact, data); SaveCard(contact);
    }
    /// <summary>The code for the receipt: the codice fiscale, otherwise the 11 digits of the partita IVA.</summary>
    public static string ReceiptCode(JsonObject d) => d.Str("tax_code") is var t && t != "" ? t : Regex.IsMatch(d.Str("vat"), "^[0-9]{11}$") ? d.Str("vat") : "";

    // ---------- tags, birthday, test, category, VIP, not bookable online
    public sealed record Profile(List<string> Tags, string Birthday, string TestDate, string Category, bool Vip, bool OnlineBlocked);
    public static Profile CleanProfile(JsonObject post, DateOnly today, JsonArray categories)
    {
        var tags = new Dictionary<string, string>();
        foreach (var raw in post.Str("tags").Split(','))
        {
            var t = string.Join(" ", raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); if (t.Length > 40) t = t[..40];
            if (t != "") tags.TryAdd(string.Join(" ", Words(t)), t);  // "Sposa" and "sposa" are one tag
        }
        if (tags.Count > 20 || tags.Values.Any(t => t.Any(ch => ch < 32))) throw new BookingException("Etichette: al massimo 20, separate da virgole.");
        string Day(string key, int from, string error)
        {
            if (post.Str(key) == "") return "";
            if (!DateOnly.TryParseExact(post.Str(key), "yyyy-MM-dd", out var d) || d < new DateOnly(from, 1, 1) || d > today) throw new BookingException(error);
            return d.ToString("yyyy-MM-dd");
        }
        var category = post.Str("category");
        if (category != "" && !categories.Any(x => x.Str("name") == category)) throw new BookingException("Categoria non valida.");
        return new Profile(tags.Values.ToList(), Day("birthday", 1900, "Data di nascita non valida."), Day("test_date", 2000, "Data del test non valida."), category, Json.Truthy(post["vip"]), Json.Truthy(post["online_blocked"]));
    }
    public void SaveProfile(Contact contact, Profile p)
    {
        var data = Data(contact);
        if (p.Tags.Count > 0) data["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode)t).ToArray()); else data.Remove("tags");
        foreach (var (k, v) in new[] { ("birthday", p.Birthday), ("test_date", p.TestDate), ("category", p.Category) }) { if (v != "") data[k] = v; else data.Remove(k); }
        contact.Vip = p.Vip; contact.OnlineBlocked = p.OnlineBlocked;
        SealCard(contact, data); SaveCard(contact, "vip", "online_blocked");
    }

    // ---------- a consent signed at the desk
    static readonly Regex SIGNATURE = new("^data:image/png;base64,([A-Za-z0-9+/]{100,300000}={0,2})$");
    public static readonly string[] CONSENT_TITLES = { "Informativa privacy", "Consenso informato al trattamento", "Regole di cancellazione" };
    public void SignConsent(Contact contact, JsonObject post)
    {
        var title = string.Join(" ", post.Str("title").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); if (title.Length > 120) title = title[..120];
        var text = post.Str("text").Replace("\r\n", "\n").Trim();
        if (title == "" || text == "" || text.Length > 8000) throw new BookingException("Scrivi il titolo e il testo da firmare, al massimo 8000 caratteri.");
        var m = SIGNATURE.Match(post.Str("signature")); var ok = false;
        try { ok = m.Success && Convert.FromBase64String(m.Groups[1].Value) is { Length: > 4 } b && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G'; } catch (FormatException) { }
        if (!ok) throw new BookingException("Manca la firma: il cliente firma nel riquadro.");
        var data = Data(contact); var signed = data["signed"] as JsonArray; if (signed == null) data["signed"] = signed = new JsonArray();
        if (signed.Count >= 50) throw new BookingException("Troppi consensi firmati in questa scheda.");
        signed.Add(new JsonObject { ["title"] = title, ["text"] = text, ["sign"] = m.Value, ["at"] = Times.PyIso(c.Now), ["by"] = c.Actor?.ToString() ?? "" });
        SealCard(contact, data); SaveCard(contact);
    }

    /// <summary>known_tags(): every tag in use, for the filter of the list. shortcut: opens only the tagged cards.</summary>
    public List<string> KnownTags() =>
        c.S.Query<Contact>("SELECT * FROM core_contact WHERE tag_index<>'' AND email_index NOT LIKE 'erased:%'").SelectMany(x => Json.Strings(Data(x)["tags"]?.ToJsonString() ?? "[]"))
            .Distinct().OrderBy(Crypto.CaseFold, StringComparer.Ordinal).ToList();
    public string TagLike(string tag) => "%" + TagKey(SearchKeys(c.Key), tag) + "%";

    /// <summary>set_discount(): a percentage (up to 100) or euro off; his confirmed future bookings take it at once.</summary>
    public int SetDiscount(Contact contact, string kind, string value)
    {
        if (kind is not ("" or "percent" or "amount")) throw new BookingException("Tipo di sconto non valido.");
        decimal amount = 0;
        if (kind != "" && !decimal.TryParse((string.IsNullOrWhiteSpace(value) ? "0" : value).Trim().Replace("€", "").Replace(" ", "").Replace(",", "."), NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            throw new BookingException("Scrivi lo sconto con un numero, per esempio 10 o 5,50.");
        if (kind != "" && !(amount >= 0.01m && amount <= (kind == "percent" ? 100m : 100000m)))
            throw new BookingException(kind == "percent" ? "Lo sconto in percentuale va da 0,01 a 100." : "Lo sconto in euro va da 0,01 a 100.000.");
        contact.DiscountKind = kind; contact.Discount = Math.Round(amount, 2, MidpointRounding.ToEven);
        c.S.Update(contact, "discount_kind", "discount");
        var changed = 0;
        foreach (var b in c.S.Query<Booking>("SELECT * FROM core_booking WHERE contact_id=@c AND status = ANY(@m) AND start>@n", new { c = contact.Id, m = Bookings.MOVABLE, n = c.Now }).ToList())
        {
            var before = Json.Dump(b.Snap);
            Prices.Stamp(c.S, b.Snap, c.Row<Service>(b.ServiceId), c.Row<Option>(b.OptionId), contact, c.Row<Resource>(b.ResourceId));
            if (Json.Dump(b.Snap) != before) { c.S.Update(b, "snapshot"); changed++; }
        }
        return changed;
    }

    // ---------- the numbers of the card
    public sealed record Stats(int Visits, DateTime? Last, Booking Next, int Cancelled, int NoShow, (string Name, int N)? Service, (string Name, int N)? Staff, int Total, decimal? Spent);
    /// <summary>history(): all the bookings of a client the user may see, newest first, and the numbers of the card.</summary>
    public (List<Booking> Rows, Stats Stats) History(Contact contact, Membership member, HashSet<string> perms)
    {
        var (vis, vargs) = Permissions.VisibleBookings(member, perms, "b");
        var p = new DynamicParameters(vargs); p.Add("cid", contact.Id);
        var rows = c.S.Query<Booking>($"SELECT b.* FROM core_booking b WHERE b.contact_id=@cid AND ({vis}) ORDER BY b.start DESC", p).ToList();
        var now = c.Now;
        var done = rows.Where(b => b.Start <= now && Bookings.ACCEPTED.Contains(b.Status)).ToList();
        var next = rows.Where(b => b.Start > now && Bookings.MOVABLE.Contains(b.Status)).OrderBy(b => b.Start).FirstOrDefault();
        (string, int)? Top(IEnumerable<string> names) => names.Where(n => n != "").GroupBy(n => n).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).Cast<(string, int)?>().FirstOrDefault();
        return (rows, new Stats(done.Count, done.FirstOrDefault()?.Start, next, rows.Count(b => b.Status == "cancelled"), rows.Count(b => b.Status == "no_show"),
            Top(done.Select(b => b.Snap.Str("service") is var s && s != "" ? s : c.Row<Service>(b.ServiceId).Name)), Top(done.Select(b => b.Snap.Str("resource"))), rows.Count, Prices.Spent(done.Select(b => b.Snap))));
    }
    public sealed record Counts(long Visits, DateTime? Last, long Next);
    /// <summary>visit_counts(): visits and last visit for a page of the client list.</summary>
    public Dictionary<Guid, Counts> VisitCounts(IEnumerable<Guid> ids) =>
        c.S.Query<(Guid, long, DateTime?, long)>(@"SELECT contact_id, count(*) FILTER (WHERE start<=@n AND status = ANY(@a)), max(start) FILTER (WHERE start<=@n AND status = ANY(@a)),
            count(*) FILTER (WHERE start>@n AND status = ANY(@m)) FROM core_booking WHERE contact_id = ANY(@ids) GROUP BY contact_id", new { n = c.Now, a = Bookings.ACCEPTED, m = Bookings.MOVABLE, ids = ids.ToArray() })
            .ToDictionary(r => r.Item1, r => new Counts(r.Item2, r.Item3, r.Item4));

    // ---------- the record: what was done, visit by visit
    public static JsonObject CleanTreatment(JsonObject data, DateOnly today)
    {
        if (!DateOnly.TryParseExact(data.Str("date"), "yyyy-MM-dd", out var day)) throw new BookingException("Data non valida.");
        if (day > today) throw new BookingException("La data non può essere nel futuro: qui si scrive quello che è stato fatto.");
        if (day.Year < 1900) throw new BookingException("Data non valida.");
        var o = new JsonObject { ["date"] = day.ToString("yyyy-MM-dd") };
        foreach (var (k, limit) in new[] { ("service", 120), ("staff", 100) })
        {
            var v = data.Str(k).Trim();
            if (v.Length > limit || v.Any(ch => ch < 32)) throw new BookingException("Dati non validi o troppo lunghi.");
            o[k] = v;
        }
        if (o.Str("service") == "") throw new BookingException("Scrivi cosa è stato fatto.");
        var details = data.Str("details").Replace("\r\n", "\n").Trim();
        if (details.Length > 4000 || details.Any(ch => ch < 32 && ch != '\n' && ch != '\t')) throw new BookingException("Dettagli non validi o troppo lunghi.");
        o["details"] = details;
        return o;
    }
    public JsonObject Info(Treatment t) => c.Decrypt(t.Pii, "treatment:" + t.Id);
    /// <summary>save_treatment(): an edit by hand keeps what the visit cost (amount, by, kind).</summary>
    public Treatment SaveTreatment(Contact contact, JsonObject d, Treatment treatment = null, Guid? booking = null)
    {
        var t = treatment ?? new Treatment { TenantId = c.Tenant.Id, ContactId = contact.Id, BookingId = booking, Author = c.Actor?.ToString() ?? "", Created = c.Now };
        var info = new JsonObject();
        if (treatment != null) foreach (var (k, v) in Info(treatment)) if (k is "amount" or "by" or "kind") info[k] = v?.DeepClone();
        foreach (var k in new[] { "service", "staff", "details", "amount", "by", "kind" }) if (d.ContainsKey(k)) info[k] = d[k]?.DeepClone();
        t.Date = DateOnly.ParseExact(d.Str("date"), "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue); t.Pii = c.Encrypt(info, "treatment:" + t.Id);
        if (treatment == null) c.S.Insert(t); else c.S.Update(t);
        return t;
    }
    public List<(Treatment Row, JsonObject Info)> Treatments(Contact contact) =>
        c.S.Query<Treatment>("SELECT * FROM core_treatment WHERE contact_id=@c ORDER BY date DESC, created DESC", new { c = contact.Id }).Select(t => (t, Info(t))).ToList();
    /// <summary>from_booking(): what a booking already says about the treatment: day, service with its variant, who.</summary>
    public JsonObject FromBooking(Booking b) => new()
    {
        ["date"] = c.Cal.DayOf(b.Start).ToString("yyyy-MM-dd"), ["service"] = (b.Snap.Str("service") is var s && s != "" ? s : c.Row<Service>(b.ServiceId).Name) + (b.Snap.Str("option") != "" ? " · " + b.Snap.Str("option") : ""),
        ["staff"] = b.Snap.Str("resource"), ["details"] = "", ["booking"] = b.Id.ToString(),
    };
}

/// <summary>core/privacy.py: the history of the consent, the access request (everything held about a client) and the erasure.</summary>
public sealed partial class Privacy
{
    static readonly JsonObject ANON_CONTACT = new() { ["first_name"] = "Anonimo", ["last_name"] = "", ["email"] = "", ["phone"] = "" };
    static readonly JsonObject ANON_BOOKING = new() { ["first_name"] = "Anonimo", ["last_name"] = "", ["email"] = "", ["phone"] = "", ["notes"] = "", ["internal_notes"] = "", ["answers"] = new JsonObject() };

    /// <summary>history(): with the history of the cards merged into this one.</summary>
    public List<Consentevent> History(Contact contact) =>
        c.S.Query<Consentevent>("SELECT e.* FROM core_consentevent e JOIN core_contact k ON k.id=e.contact_id WHERE e.contact_id=@c OR k.merged_into_id=@c ORDER BY e.at, e.id", new { c = contact.Id }).ToList();

    /// <summary>export_contact(): access request: everything held about one contact, this business only.</summary>
    public JsonObject Export(Contact contact)
    {
        var cl = new Clients(c); var bk = new Bookings(c); string Iso(DateTime? d) => d.HasValue ? Times.PyIso(d.Value) : null; string Day(DateTime d) => d.ToString("yyyy-MM-dd");
        JsonObject With(JsonObject first, JsonObject rest, JsonObject last = null) { foreach (var (k, v) in rest) first[k] = v?.DeepClone(); if (last != null) foreach (var (k, v) in last) first[k] = v?.DeepClone(); return first; }
        var card = With(new JsonObject { ["id"] = contact.Id.ToString() }, cl.Data(contact), new JsonObject { ["consent"] = contact.Consent, ["consent_at"] = Iso(contact.ConsentAt), ["suppressed"] = contact.Suppressed });
        var o = new JsonObject { ["tenant"] = c.Tenant.Name, ["generated"] = Times.PyIso(c.Now), ["contact"] = card };
        o["bookings"] = new JsonArray(c.S.Query<Booking>("SELECT * FROM core_booking WHERE contact_id=@c ORDER BY start", new { c = contact.Id }).Select(b =>
        {
            var d = bk.Data(b); d.Remove("cancel_token");
            return (JsonNode)new JsonObject { ["id"] = b.Id.ToString(), ["start"] = Iso(b.Start), ["end"] = Iso(b.End), ["status"] = b.Status, ["service"] = b.Snap["service"]?.DeepClone(), ["location"] = b.Snap["location"]?.DeepClone(), ["resource"] = b.Snap["resource"]?.DeepClone(), ["data"] = d };
        }).ToArray());
        o["waiting_list"] = new JsonArray(c.S.Query<Waitentry>("SELECT * FROM core_waitentry WHERE contact_id=@c ORDER BY created", new { c = contact.Id }).Select(w => (JsonNode)new JsonObject
        { ["service"] = c.Row<Service>(w.ServiceId)?.Name, ["first"] = Day(w.First), ["last"] = Day(w.Last), ["status"] = w.Status, ["data"] = c.Decrypt(w.Pii, "wait:" + w.Id), ["consent"] = Json.Obj(w.Snapshot)["consent"]?.DeepClone() }).ToArray());
        o["treatments"] = new JsonArray(c.S.Query<Treatment>("SELECT * FROM core_treatment WHERE contact_id=@c ORDER BY date, created", new { c = contact.Id }).Select(t =>
            (JsonNode)With(new JsonObject { ["date"] = Day(t.Date) }, cl.Info(t), new JsonObject { ["booking"] = t.BookingId?.ToString() ?? "" })).ToArray());
        o["consent_history"] = new JsonArray(History(contact).Select(e => (JsonNode)new JsonObject { ["at"] = Iso(e.At), ["event"] = e.Kind, ["channel"] = e.Channel, ["text_sha256"] = e.TextHash }).ToArray());
        o["quotes"] = new JsonArray(c.S.Query<Quote>("SELECT * FROM core_quote WHERE contact_id=@c ORDER BY created", new { c = contact.Id }).Select(q =>
            (JsonNode)With(new JsonObject { ["number"] = $"{q.Year}-{q.Number:0000}", ["created"] = Iso(q.Created), ["total"] = Prices.Str(q.Total) }, c.Decrypt(q.Pii, "quote:" + q.Id))).ToArray());
        o["to_pay_at_the_desk"] = new JsonArray(c.S.Query<Parked>("SELECT * FROM core_parked WHERE contact_id=@c ORDER BY created", new { c = contact.Id }).Select(x =>
            (JsonNode)With(new JsonObject { ["created"] = Iso(x.Created), ["total"] = Prices.Str(x.Total) }, c.Decrypt(x.Pii, "parked:" + x.Id))).ToArray());
        o["photos"] = new JsonArray(c.S.Query<Clientphoto>("SELECT id, date, image, booking_id, contact_id, tenant_id, created FROM core_clientphoto WHERE contact_id=@c ORDER BY date, created", new { c = contact.Id }).Select(p => (JsonNode)new JsonObject
        { ["date"] = Day(p.Date), ["booking"] = p.BookingId?.ToString() ?? "", ["jpeg_base64"] = Convert.ToBase64String(c.Crypto.DecryptBytes(c.Tenant.Id, c.Tenant.WrappedKey, p.Image, "photo:" + p.Id)) }).ToArray());
        o["gift_cards"] = new JsonArray(c.S.Query<Giftcard>("SELECT * FROM core_giftcard WHERE contact_id=@c", new { c = contact.Id }).Select(g => (JsonNode)new JsonObject
        { ["code"] = g.Code, ["amount"] = Prices.Str(g.Amount), ["balance"] = Prices.Str(g.Balance), ["expires"] = g.Expires.HasValue ? Day(g.Expires.Value) : null, ["status"] = g.Status }).ToArray());
        return o;
    }

    void AnonymizeBooking(Booking b)
    {
        b.Pii = c.Encrypt(ANON_BOOKING.DeepClone(), "booking:" + b.Id); b.RequestFingerprint = ""; b.ContactId = null;
        b.Snap.Remove("consent_request"); b.Snap["anonymized"] = Times.PyIso(c.Now);
        c.S.Update(b, "pii", "request_fingerprint", "contact_id", "snapshot");
        c.S.Execute("DELETE FROM core_actiontoken WHERE booking_id=@b", new { b = b.Id });
        c.S.Execute("DELETE FROM core_clientphoto WHERE booking_id=@b", new { b = b.Id });
    }

    void AnonymizeContact(Contact ct, bool forget)
    {
        var cl = new Clients(c); var anon = (JsonObject)ANON_CONTACT.DeepClone();
        if (forget)
        {
            // the keyed indexes of the email and phone stay, so an import from the old program cannot bring the person back
            var d = cl.Data(ct); var keys = new JsonArray();
            if (d.Str("email") != "") keys.Add(cl.EmailIndex(d.Str("email")));
            if (d.Str("phone") != "") keys.Add(cl.PhoneIndex(d.Str("phone")));
            if (keys.Count > 0) anon["erased_keys"] = keys;
        }
        ct.Pii = c.Encrypt(anon, "contact:" + ct.Id);
        ct.EmailIndex = Clients.ERASED + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        ct.Consent = "revoked"; ct.ConsentProof = ""; ct.Suppressed = true; ct.PhoneIndex = ct.NameIndex = ct.TagIndex = ct.BirthdayIndex = ""; ct.Vip = ct.OnlineBlocked = false;
        c.S.Update(ct);
        var id = new { c = ct.Id };
        foreach (var table in new[] { "core_actiontoken", "core_treatment", "core_clientphoto", "core_standby", "core_waitentry" }) c.S.Execute($"DELETE FROM {table} WHERE contact_id=@c", id);
        // his receipts keep lines and totals, not his codice fiscale (cassa.forget)
        foreach (var s in c.S.Query<Sale>("SELECT * FROM core_sale WHERE contact_id=@c", id).ToList())
        {
            var d = c.Decrypt(s.Pii, "sale:" + s.Id); foreach (var k in new[] { "cf", "lottery", "email" }) d.Remove(k);
            s.Pii = c.Encrypt(d, "sale:" + s.Id); s.ContactId = null; s.Actor = ""; c.S.Update(s, "pii", "contact_id", "actor");
        }
        c.S.Execute("DELETE FROM core_quote WHERE contact_id=@c", id); c.S.Execute("DELETE FROM core_parked WHERE contact_id=@c", id);
        c.S.Execute("UPDATE core_giftcard SET contact_id=NULL WHERE contact_id=@c", id);
        foreach (var d in c.S.Query<Delivery>("SELECT * FROM core_delivery WHERE contact_id=@c AND payload<>''", id).ToList())
        {
            d.Payload = c.Encrypt(new JsonObject { ["redacted"] = true }, "delivery:" + d.Id);
            if (d.Status is "pending" or "sending") d.Status = "skipped";
            c.S.Update(d, "payload", "status");
        }
    }

    /// <summary>erase_contact(): erasure request. Future open bookings must be handled first.</summary>
    public void Erase(Contact contact)
    {
        c.Lock();
        contact = c.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id FOR UPDATE", new { id = contact.Id });
        if (c.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE contact_id=@c AND status = ANY(@o) AND \"end\">@n", new { c = contact.Id, o = Bookings.OPEN, n = c.Now }) > 0)
            throw new BookingException("Il cliente ha prenotazioni future: annullale o completale prima della cancellazione.");
        foreach (var b in c.S.Query<Booking>("SELECT * FROM core_booking WHERE contact_id=@c", new { c = contact.Id }).ToList()) AnonymizeBooking(b);
        AnonymizeContact(contact, true);
        Record(contact.Id, "erased", "operator");
        c.Audit("privacy.erased", contact.Id);
    }
}
