using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/importer.py, after the reading: import of the client list and of the treatment history from the old
/// program. The file is read once and sealed with the business key in a temporary file; the staff say what each column
/// holds, the preview runs the same code as the import without writing. An existing card is never overwritten: it only
/// gets its empty fields, new notes and new treatments. Marketing consents in the file are recorded as "Importato" and
/// stay to be confirmed: only the confirmation from the mailbox makes them valid. Whoever asked for their data to be
/// erased is not brought back.</summary>
public sealed class ClientImport(Ctx c)
{
    public const int KEEP_SECONDS = 7200;  // the sealed copy between the steps
    public static readonly (string Key, string Label)[] FIELDS =
    {
        ("", "Non importare"), ("first_name", "Nome"), ("last_name", "Cognome"), ("full_name", "Nome e cognome insieme"), ("phone", "Telefono"), ("email", "Email"), ("code", "Codice cliente del vecchio programma"), ("notes", "Note"),
        ("extra", "Nelle note, con il titolo della colonna"), ("date", "Data del trattamento"), ("service", "Trattamento fatto"), ("staff", "Operatore"), ("details", "Dettagli del trattamento"), ("amount", "Importo del trattamento"),
        ("amount_by", "Importo per operatore"), ("consent", "Consenso marketing"),
        // the fiscal data of the card
        ("company", "Ragione sociale"), ("tax_code", "Codice fiscale"), ("vat", "Partita IVA"), ("address", "Indirizzo"), ("zip", "CAP"), ("city", "Comune"), ("province", "Provincia"), ("sdi", "Codice destinatario SDI"), ("pec", "PEC"),
    };
    static readonly HashSet<string> RECORD = new() { "date", "service", "staff", "details", "amount", "amount_by" };
    static readonly HashSet<string> SINGLE = new() { "first_name", "last_name", "full_name", "code", "date", "service", "staff", "amount", "amount_by", "consent", "company", "tax_code", "vat", "address", "zip", "city", "province", "sdi", "pec" };
    static readonly string[] FISCAL_KEYS = { "company", "tax_code", "vat", "address", "zip", "city", "province", "sdi", "pec" };
    // column titles of the common programs and of hand-made sheets, without spaces and accents
    static readonly string[] SKIP = { "sesso", "genere" };
    static readonly string[] EXTRA = { "datanascita", "datadinascita", "nascita", "compleanno", "ultimavisita", "dataultimavisita", "primavisita", "dataiscrizione", "datainserimento", "datacreazione", "professione", "tessera" };
    static readonly (string Field, string[] Names)[] GUESS =
    {
        ("tax_code", new[] { "cf", "codicefiscale", "codfisc", "codfiscale" }), ("vat", new[] { "partitaiva", "piva" }), ("company", new[] { "ragionesociale" }), ("pec", new[] { "pec", "emailpec" }),
        ("sdi", new[] { "sdi", "codicesdi", "codicedestinatario", "codiceunivoco" }), ("zip", new[] { "cap" }), ("address", new[] { "indirizzo", "via", "residenza" }), ("city", new[] { "comune", "citta", "localita", "paese" }), ("province", new[] { "provincia", "prov" }),
        ("full_name", new[] { "nomecognome", "cognomenome", "nomeecognome", "cognomeenome", "nominativo", "cliente", "nomecliente", "fullname", "name" }),
        ("first_name", new[] { "nome", "firstname", "givenname" }), ("last_name", new[] { "cognome", "lastname", "surname", "familyname" }),
        ("email", new[] { "email", "mail", "eposta", "posta", "emailaddress", "indirizzoemail" }), ("phone", new[] { "telefono", "cellulare", "cell", "tel", "mobile", "phone", "whatsapp", "recapito" }),
        ("code", new[] { "codice", "codicecliente", "codcliente", "cod", "idcliente", "id", "clientid", "customerid", "numerocliente" }), ("notes", new[] { "note", "notes", "annotazioni", "osservazioni", "nota" }),
        ("date", new[] { "data", "datatrattamento", "datavisita", "dataappuntamento", "dataservizio", "giorno", "date", "quando" }), ("service", new[] { "trattamento", "servizio", "servizi", "prestazione", "service", "treatment", "lavoro", "seduta" }),
        ("staff", new[] { "operatore", "operatrice", "collaboratore", "collaboratrice", "staff", "dipendente", "eseguitoda", "artista" }),
        // what the visit cost, in all and for each operator ("Cristina: 22,00; Diana: 18,00"), for the reports
        ("amount_by", new[] { "importoperoperatore", "incassoperoperatore", "importioperatori" }), ("amount", new[] { "importo", "importotrattamento", "speso", "incassato", "totalepagato", "pagato" }),
        ("details", new[] { "dettagli", "notetecniche", "notetrattamento", "notecolore", "notelavoro", "formula", "colore", "descrizione", "prodotti", "details", "ricetta" }),
        // only these exact titles: "Consenso privacy" is the notice the client signed, not a yes to marketing
        ("consent", new[] { "consensomarketing", "marketing", "newsletter", "privacymarketing", "promozioni", "consensonewsletter", "consensopromozioni", "consensocommerciale" }),
    };
    static readonly HashSet<string> YES = new() { "si", "sì", "s", "yes", "y", "x", "1", "true", "vero", "ok", "consenso", "acconsento", "✓", "✔" };

    public static List<(string Key, string Label)> Fields(bool record, bool consent) => FIELDS.Where(f => (record || !RECORD.Contains(f.Key)) && (consent || f.Key != "consent")).ToList();

    /// <summary>What each column probably holds, from its title. Single-value fields go to the first column only.</summary>
    public static List<string> Guess(List<string> head, bool record, bool consent)
    {
        var o = new List<string>();
        foreach (var title in head)
        {
            var t = Importer.Plain(title); string field;
            if (SKIP.Contains(t)) field = "";
            else if (EXTRA.Contains(t) || EXTRA.Any(t.StartsWith)) field = "extra";
            else
            {
                field = GUESS.Where(g => g.Names.Contains(t)).Select(g => g.Field).FirstOrDefault()
                    ?? GUESS.Where(g => g.Field != "consent" && g.Names.Any(n => n.Length >= 4 && t.StartsWith(n))).Select(g => g.Field).FirstOrDefault() ?? (t != "" ? "extra" : "");
            }
            if (RECORD.Contains(field) && !record) field = "extra";
            if (field == "consent" && !consent) field = "";
            if (SINGLE.Contains(field) && o.Contains(field)) field = "extra";
            o.Add(field);
        }
        if (o.Contains("first_name") || o.Contains("last_name")) o = o.Select(f => f == "full_name" ? "extra" : f).ToList();
        return o;
    }

    // ---------- the sealed copy between the steps
    /// <summary>Where the copies wait: the data folder's import\ on an installation (tdwin IMPORT_DIR).</summary>
    public static string Folder { get; set; } = Path.Combine(Path.GetTempPath(), "tdbs-import");
    static string Dir() { Directory.CreateDirectory(Folder); return Folder; }
    static bool ValidToken(string token) => Regex.IsMatch(token ?? "", @"^[A-Za-z0-9_-]{24}\z");
    /// <summary>Copies left by an import never finished go after two hours; also called by the nightly retention.</summary>
    public static void Purge()
    {
        foreach (var f in Directory.GetFiles(Dir()))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddSeconds(-KEEP_SECONDS)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    /// <summary>shortcut: a temporary file on this server; several web servers would need shared storage.</summary>
    public string Stash(string name, List<string> head, List<List<string>> rows)
    {
        Purge(); var token = Bookings.TokenUrlSafe(18);
        var data = new JsonObject { ["name"] = name.Length > 120 ? name[..120] : name, ["head"] = new JsonArray(head.Select(h => (JsonNode)h).ToArray()), ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)v).ToArray())).ToArray()) };
        File.WriteAllText(Path.Combine(Dir(), token), c.Encrypt(data, "import:" + token), Encoding.ASCII);
        return token;
    }
    public (string Name, List<string> Head, List<List<string>> Rows)? Unstash(string token)
    {
        if (!ValidToken(token)) return null;
        Purge();
        try
        {
            var d = c.DecryptNode(File.ReadAllText(Path.Combine(Dir(), token), Encoding.ASCII), "import:" + token);
            return (d.Str("name"), ((JsonArray)d["head"]).Select(h => h.ToString()).ToList(), ((JsonArray)d["rows"]).Select(r => ((JsonArray)r).Select(v => v.ToString()).ToList()).ToList());
        }
        catch (Exception) { return null; }  // gone, expired or sealed for another business: the page asks for the file again
    }
    public static void Drop(string token) { if (ValidToken(token)) try { File.Delete(Path.Combine(Dir(), token)); } catch (IOException) { } }

    public static List<string> Samples(List<List<string>> rows, int i, int n = 3)
    {
        var o = new List<string>();
        foreach (var r in rows)
        {
            var v = i < r.Count ? r[i] : ""; var cut = v.Length > 40 ? v[..40] : v;
            if (v != "" && !o.Contains(cut)) o.Add(cut);
            if (o.Count >= n) break;
        }
        return o;
    }

    // ---------- what the import does
    static string Lines(string v) => Regex.Replace(v.Replace("\r\n", "\n").Replace("\r", "\n"), @"[\x00-\x08\x0b-\x1f\x7f]", "").Trim();
    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>31/12/2024, 31-12-24, 2024-12-31, 2024/12/31 or an Excel date number. Day before month, as in Italy.</summary>
    public static DateOnly? Day(string value, DateOnly today)
    {
        var v = value.Trim(); int y, mo, d;
        if (Regex.IsMatch(v, @"^\d{5}(\.\d+)?\z")) { var n = double.Parse(v, CultureInfo.InvariantCulture); return n > 20000 && n < 80000 ? new DateOnly(1899, 12, 30).AddDays((int)n) : null; }
        var m = Regex.Match(v, @"^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})");
        if (m.Success) { y = int.Parse(m.Groups[1].Value); mo = int.Parse(m.Groups[2].Value); d = int.Parse(m.Groups[3].Value); }
        else
        {
            m = Regex.Match(v, @"^(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{4}|\d{2})\b");
            if (!m.Success) return null;
            d = int.Parse(m.Groups[1].Value); mo = int.Parse(m.Groups[2].Value); y = int.Parse(m.Groups[3].Value);
            if (y < 100) y += y <= today.Year % 100 ? 2000 : 1900;
        }
        try { return new DateOnly(y, mo, d); } catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Cents of «25,00», «1.234,50», «25.5» or «€ 25»; null when it is not an amount.</summary>
    public static long? Amount(string value)
    {
        var v = Regex.Replace(value ?? "", @"[€\s]", "");
        if (Regex.IsMatch(v, @"^-?\d{1,3}(\.\d{3})+(,\d{1,2})?\z")) v = v.Replace(".", "");
        v = v.Replace(",", ".");
        if (!Regex.IsMatch(v, @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\z") || !decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || Math.Abs(d) >= 10_000_000m) return null;
        return (long)Math.Round(d * 100, MidpointRounding.ToEven);
    }
    /// <summary>{operator: cents} of «Cristina: 22,00; Diana: 18,00»; the parts that are not an amount are left out.</summary>
    public static JsonObject Amounts(string value)
    {
        var o = new JsonObject();
        foreach (var part in Regex.Split(value ?? "", @"[;\n]"))
        {
            var at = part.LastIndexOf(':'); if (at < 0) continue;
            var name = Cut(string.Join(" ", part[..at].Split((char[])null, StringSplitOptions.RemoveEmptyEntries)), 100); if (part[..at].Trim() == "") continue;
            if (Amount(part[(at + 1)..]) is long cents) o[name] = (o[name] is JsonNode old ? (long)old : 0) + cents;
        }
        return o;
    }

    static string Fold(string s) => Crypto.CaseFold(s);
    static HashSet<string> Fulls(JsonObject d) => Fulls(d.Str("first_name"), d.Str("last_name"));
    static HashSet<string> Fulls(string first, string last) { var s = new HashSet<string> { Fold($"{first} {last}".Trim()), Fold($"{last} {first}".Trim()) }; s.Remove(""); return s; }
    /// <summary>One name holds the other ("Maria" and "Maria Rossi"): the same person, as Clients.SamePerson.</summary>
    static bool Alike(JsonObject d, string first, string last)
    {
        var a = Fold($"{d.Str("first_name")} {d.Str("last_name")}").Split((char[])null, StringSplitOptions.RemoveEmptyEntries); var b = Fold($"{first} {last}").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var (shortW, longW) = a.Length <= b.Length ? (a, b) : (b, a); return shortW.All(longW.Contains);
    }

    public sealed class Card { public Contact Row; public JsonObject D; public bool New, Wanted; }
    public sealed class Reasons
    {
        public readonly List<(string Reason, List<int> Lines)> Items = new();
        public void Add(int n, string reason) { var at = Items.FindIndex(x => x.Reason == reason); if (at < 0) Items.Add((reason, new List<int> { n })); else Items[at].Lines.Add(n); }
        public int Count => Items.Sum(x => x.Lines.Count);
    }
    public sealed record Sample(int Line, string Name, string Phone, string Email, string Status, string Entry);
    public sealed class Planned
    {
        public List<Card> Created = new(), Changed = new(), Consents = new(); public List<(Card Card, DateOnly Date, JsonObject Data)> Treatments = new(); public Dictionary<Guid, (Treatment Row, JsonObject D)> Refill = new();
        public Dictionary<string, int> Report = new(); public Reasons Problems = new(), Issues = new(); public List<Sample> Samples = new(); public DateOnly Since;
        public int R(string key) => Report.GetValueOrDefault(key);
    }

    /// <summary>Everything the import would do, computed in memory: the preview shows it, Apply writes it.
    /// lockRows for the import itself: the cards are read FOR UPDATE, so nobody edits them in between.</summary>
    public Planned Plan(List<string> head, List<List<string>> rows, List<string> mapping, bool record = true, bool consent = false, bool lockRows = false)
    {
        if (!mapping.Any(m => m is "first_name" or "last_name" or "full_name" or "phone" or "email" or "code")) throw new BookingException("Indica almeno la colonna del nome, del telefono, dell'email o del codice cliente.");
        var t = c.Tenant; var today = c.Today; var since = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(c.Now.AddDays(-t.RetentionDays), c.Zone));
        // the business key unwrapped once for the whole file
        var key = c.Key; var clients = new Clients(c);
        JsonObject Open(string pii, string label, Guid id) => Crypto.Open(pii, key, $"{t.Id}:{label}:{id}") as JsonObject ?? new JsonObject();
        string EmailKey(string e) => Crypto.Index(Fold(e), key, "email"); string PhoneKey(string p) => Crypto.Index("tel:" + p, key, "email");
        var emails = new Dictionary<string, Card>(); var phones = new Dictionary<string, List<Card>>(); var codes = new Dictionary<string, Card>(); var names = new Dictionary<string, List<Card>>();
        void Learn(Card card)
        {
            var d = card.D;
            if (d.Str("email") != "") emails.TryAdd(d.Str("email"), card);
            if (d.Str("phone") != "") { if (!phones.TryGetValue(d.Str("phone"), out var l)) phones[d.Str("phone")] = l = new(); if (!l.Contains(card)) l.Add(card); }
            if (d.Str("import_code") != "") codes.TryAdd(d.Str("import_code"), card);
            foreach (var full in Fulls(d)) { if (!names.TryGetValue(full, out var l)) names[full] = l = new(); if (!l.Contains(card)) l.Add(card); }
        }
        var cards = new List<Card>(); var forgotten = new HashSet<string>();
        foreach (var row in c.S.Query<Contact>("SELECT * FROM core_contact ORDER BY created, id" + (lockRows ? " FOR UPDATE" : "")).ToList())
        {
            var d = Open(row.Pii, "contact", row.Id);
            if (row.EmailIndex.StartsWith(Clients.ERASED)) { foreach (var k in d["erased_keys"] as JsonArray ?? new JsonArray()) forgotten.Add(k.ToString()); continue; }
            var card = new Card { Row = row, D = d }; Learn(card); cards.Add(card);
        }
        var taken = cards.Select(x => x.Row.EmailIndex).ToHashSet();
        record = record && mapping.Any(RECORD.Contains);
        var known = new Dictionary<(Guid, DateOnly, string, string, string), (Treatment Row, JsonObject D)?>(); var p = new Planned { Since = since }; var changed = new Dictionary<Guid, Card>();
        if (record)
            foreach (var tr in c.S.Query<Treatment>("SELECT * FROM core_treatment").ToList())
            {
                var d = Open(tr.Pii, "treatment", tr.Id);
                known[(tr.ContactId, DateOnly.FromDateTime(tr.Date), Fold(d.Str("service")), Fold(d.Str("staff")), d.Str("details"))] = (tr, d);
            }
        void Count(string k) => p.Report[k] = p.R(k) + 1;
        var manyNotes = mapping.Count(m => m == "notes") > 1; var manyDetails = mapping.Count(m => m == "details") > 1;
        for (int at = 0; at < rows.Count; at++)
        {
            var n = at + 2; var row = rows[at]; var got = new Dictionary<string, List<(string Head, string Value)>>();
            for (int i = 0; i < mapping.Count; i++)
                if (mapping[i] != "" && i < row.Count && row[i].Trim() != "") { if (!got.TryGetValue(mapping[i], out var l)) got[mapping[i]] = l = new(); l.Add((head[i], row[i])); }
            string One(string f) => got.TryGetValue(f, out var l) ? l[0].Value : "";
            List<(string Head, string Value)> All(string f) => got.TryGetValue(f, out var l) ? l : new();
            var first = Importer.Name(One("first_name")); var last = Importer.Name(One("last_name"));
            if (first == "" && last == "") first = Importer.Name(One("full_name"));
            if (first == "" && last != "") (first, last) = (last, "");
            var foundEmails = new List<string>(); var foundPhones = new List<string>();
            foreach (var (_, v) in All("email"))
                foreach (var raw in Regex.Split(v, @"[\s,;/]+"))
                {
                    var part = Fold(raw.Trim()); if (part == "") continue;
                    if (part.Length <= 254 && Clients.ValidEmail(part)) { if (!foundEmails.Contains(part)) foundEmails.Add(part); } else Count("bad_email");
                }
            foreach (var (_, v) in All("phone"))
                foreach (var part in Regex.Split(v, @"\s*[,;/|]\s*|\s{2,}"))
                {
                    var ph = Clients.NormalizePhone(part);
                    if (ph != "") { if (!foundPhones.Contains(ph)) foundPhones.Add(ph); } else if (part.Count(char.IsDigit) >= 6) Count("bad_phone");
                }
            var code = Importer.Line(One("code"), 60);
            var note = All("notes").Select(x => (manyNotes ? x.Head + ": " : "") + Lines(x.Value)).Concat(All("extra").Select(x => x.Head + ": " + Lines(x.Value))).ToList();
            var rowNames = Fulls(first, last);
            // find the card: any of its emails, then phone (two different emails are two people; a phone shared in the
            // family needs the name to tell who), the old code, the name alone when the row has nothing else
            var card = foundEmails.Where(emails.ContainsKey).Select(e => emails[e]).FirstOrDefault();
            var email = card != null ? card.D.Str("email") : foundEmails.FirstOrDefault() ?? ""; var phone = foundPhones.FirstOrDefault() ?? "";
            bool Differs(Card x) => email != "" && x.D.Str("email") != "" && x.D.Str("email") != email;
            if (card == null)
            {
                var ambiguous = false;
                foreach (var ph in foundPhones)
                {
                    var same = (phones.GetValueOrDefault(ph) ?? new()).Where(x => !Differs(x)).ToList();
                    // as at the desk, another name on a known phone is another person (a mother and her daughter)
                    if (rowNames.Count > 0) same = same.Where(x => Fulls(x.D).Count == 0 || Alike(x.D, first, last)).ToList();
                    if (same.Count > 1)
                    {
                        var named = same.Where(x => rowNames.Overlaps(Fulls(x.D))).ToList();
                        if (named.Count != 1) { ambiguous = true; continue; }
                        same = named;
                    }
                    if (same.Count > 0) { card = same[0]; break; }
                }
                if (card == null && ambiguous) { p.Problems.Add(n, "telefono di più clienti e nome che non basta a dire di chi è"); continue; }
            }
            if (card == null && code != "") { var x = codes.GetValueOrDefault(code); card = x != null && !Differs(x) ? x : null; }
            if (card == null && email == "" && phone == "" && !(code != "" && first != ""))
            {
                if (code != "") { p.Problems.Add(n, "cliente da creare senza telefono né email: il codice non è di un cliente già importato"); continue; }
                var same = rowNames.SelectMany(x => names.GetValueOrDefault(x) ?? new()).Distinct().ToList();
                if (same.Count > 1) { p.Problems.Add(n, "più clienti con questo nome e nessun telefono, email o codice per distinguerli"); continue; }
                card = same.FirstOrDefault();
            }
            string status;
            if (card == null)
            {
                if (email == "" && phone == "" && code == "") { p.Problems.Add(n, first != "" ? "cliente da creare senza telefono né email" : "riga senza nome, telefono, email o codice"); continue; }
                if (first == "") { p.Problems.Add(n, "cliente da creare senza nome"); continue; }
                if (forgotten.Overlaps(foundEmails.Select(EmailKey).Concat(foundPhones.Select(PhoneKey)))) { p.Problems.Add(n, "cliente che ha chiesto di cancellare i suoi dati: non si reimporta"); continue; }
                // a card of the old program without phone or email (they are in its archive too) is told apart by its code
                var k = email != "" ? EmailKey(email) : phone != "" ? PhoneKey(phone) : Crypto.Index("import:" + code, key, "email");
                if (taken.Contains(k) && email == "" && phone != "") k = clients.KeyFor(new JsonObject { ["phone"] = phone, ["first_name"] = first, ["last_name"] = last }, true);  // the phone of another person's card
                if (taken.Contains(k)) { p.Problems.Add(n, "telefono o email già usati da un altro cliente"); continue; }
                card = new Card { Row = new Contact { TenantId = t.Id, EmailIndex = k }, D = new JsonObject { ["first_name"] = first, ["last_name"] = last, ["email"] = email, ["phone"] = phone }, New = true }; taken.Add(k);
                if (code != "") card.D["import_code"] = code;
                p.Created.Add(card); cards.Add(card); Learn(card); status = "nuovo";
            }
            else
            {
                var d = card.D; var before = d.ToJsonString();
                if (last != "" && d.Str("last_name") == "" && !Fold(d.Str("first_name")).Contains(Fold(last))) d["last_name"] = last;
                if (email != "" && d.Str("email") == "")
                {
                    // a card without email is keyed by its phone: with the email it moves to the email key, as update() does
                    var k = EmailKey(email);
                    if (!taken.Contains(k)) { d["email"] = email; taken.Remove(card.Row.EmailIndex); card.Row.EmailIndex = k; taken.Add(k); }
                }
                if (phone != "" && d.Str("phone") == "") d["phone"] = phone;
                if (code != "" && d.Str("import_code") == "") d["import_code"] = code;
                Learn(card);
                if (d.ToJsonString() != before && !card.New) changed[card.Row.Id] = card;
                status = card.New ? "nuovo" : "già presente";
            }
            var filled = false;
            foreach (var f in FISCAL_KEYS)  // the fiscal data the card lacks, checked as on the card; a wrong one stays out
                if (got.ContainsKey(f) && card.D.Str(f) == "")
                    try { card.D[f] = Clients.CleanFiscal(new JsonObject { ["fiscal_kind"] = "azienda", [f] = One(f) })[f]; filled = true; } catch (BookingException) { Count("bad_fiscal"); }
            if (filled && card.D.Str("fiscal_kind") == "" && (card.D.Str("company") != "" || card.D.Str("vat") != "" || card.D.Str("tax_code") is { Length: > 0 } tc && tc.All(char.IsDigit))) card.D["fiscal_kind"] = "azienda";
            if (filled && !card.New) changed[card.Row.Id] = card;
            note.AddRange(foundEmails.Where(e => e != card.D.Str("email")).Select(e => "Altra email: " + e)); note.AddRange(foundPhones.Where(x => x != card.D.Str("phone")).Select(x => "Altro telefono: " + x));
            if (note.Count > 0)
            {
                var old = card.D.Str("staff_notes"); var add = note.Distinct().Where(x => x != "" && !old.Contains(x)).ToList();
                if (add.Count > 0) { card.D["staff_notes"] = Cut((old != "" ? old + "\n" : "") + string.Join("\n", add), 4000); if (!card.New) changed[card.Row.Id] = card; }
            }
            if (consent && YES.Contains(Fold(One("consent").Trim()).TrimEnd('\'', '.'))) card.Wanted = true;
            var entry = "";
            if (record && got.Keys.Any(RECORD.Contains))
            {
                var when = One("date") != "" ? Day(One("date"), today) : null;
                var service = Importer.Line(One("service"), 120); var staff = Importer.Line(One("staff"), 100);
                var details = Cut(string.Join("\n", All("details").Select(x => (manyDetails ? x.Head + ": " : "") + Lines(x.Value))), 4000);
                if (when == null) p.Issues.Add(n, "data non valida, per esempio 31/12/2024");
                else if (when > today) p.Issues.Add(n, "data nel futuro");
                else if (service == "") p.Issues.Add(n, "non c'è scritto cosa è stato fatto");
                else if (when < since) Count("too_old");
                else
                {
                    var k = (card.Row.Id, when.Value, Fold(service), Fold(staff), details); var money = new JsonObject();
                    if (One("amount").Trim() != "") { if (Amount(One("amount")) is long cents) money["amount"] = cents; else p.Issues.Add(n, "importo non valido, per esempio 25,00"); }
                    if (One("amount_by").Trim() != "") money["by"] = Amounts(One("amount_by"));
                    if (known.TryGetValue(k, out var old))
                    {
                        Count("same_treatment");
                        // the same treatment imported before without its amount gets it now; nothing else changes
                        if (old is var (tr, d) && money.Count > 0 && !money.Any(x => d.ContainsKey(x.Key))) { foreach (var x in money) d[x.Key] = x.Value.DeepClone(); p.Refill[tr.Id] = (tr, d); Count("amounts"); }
                    }
                    else
                    {
                        known[k] = null; var data = new JsonObject { ["service"] = service, ["staff"] = staff, ["details"] = details }; foreach (var x in money) data[x.Key] = x.Value.DeepClone();
                        p.Treatments.Add((card, when.Value, data)); entry = $"{when:dd/MM/yyyy} · {service}";
                    }
                }
            }
            if (p.Samples.Count < 8) p.Samples.Add(new Sample(n, $"{card.D.Str("first_name")} {card.D.Str("last_name")}".Trim(), card.D.Str("phone"), card.D.Str("email"), status, entry));
        }
        var imported = c.S.Query<Guid>("SELECT contact_id FROM core_consentevent WHERE kind='imported'").ToHashSet();
        foreach (var card in cards.Where(x => x.Wanted))
        {
            if (card.D.Str("email") == "") Count("consent_no_email");
            else if (card.Row.Consent == "revoked" || card.Row.Suppressed) Count("consent_revoked");
            else if (card.Row.Consent == "yes" || imported.Contains(card.Row.Id)) Count("consent_known");
            else p.Consents.Add(card);
        }
        p.Changed = changed.Values.ToList();
        p.Report["created"] = p.Created.Count; p.Report["updated"] = p.Changed.Count; p.Report["treatments"] = p.Treatments.Count; p.Report["consents"] = p.Consents.Count;
        p.Report["problems"] = p.Problems.Count; p.Report["issues"] = p.Issues.Count; p.Report["rows"] = rows.Count;
        return p;
    }

    /// <summary>Write a plan made with lockRows, inside the transaction that holds the business lock.</summary>
    public Dictionary<string, int> Apply(Planned p, bool ask = false, string batch = "")
    {
        var clients = new Clients(c); var who = c.Actor?.ToString() ?? ""; var t = c.Tenant;
        // shortcut: one statement per row, each reaching the site's archive by itself; in bulk when 20,000 rows take too long
        foreach (var card in p.Created) { clients.SealCard(card.Row, card.D); c.S.Insert(card.Row); }
        foreach (var card in p.Changed) { clients.SealCard(card.Row, card.D); clients.SaveCard(card.Row); }
        foreach (var (card, date, data) in p.Treatments)
        {
            var tr = new Treatment { TenantId = t.Id, ContactId = card.Row.Id, Date = date.ToDateTime(TimeOnly.MinValue), Author = who };
            tr.Pii = c.Encrypt(data, "treatment:" + tr.Id); c.S.Insert(tr);
        }
        foreach (var (tr, d) in p.Refill.Values) { tr.Pii = c.Encrypt(d, "treatment:" + tr.Id); c.S.Update(tr, "pii"); }
        var privacy = new Privacy(c);
        foreach (var card in p.Consents) privacy.Record(card.Row.Id, "imported", "import", null, batch);
        if (p.Consents.Count > 0)
        {
            var ids = p.Consents.Select(x => x.Row.Id).ToArray();
            c.S.Execute("UPDATE core_contact SET consent='pending' WHERE id = ANY(@ids) AND consent='none' AND NOT suppressed", new { ids });
            WpSync.Touch(c, "contact", ids);
        }
        var sent = 0;
        if (ask)
        {
            var busy = c.S.Query<Guid>("SELECT contact_id FROM core_actiontoken WHERE kind='consent' AND NOT used AND expires>@n AND contact_id IS NOT NULL", new { n = c.Now }).ToHashSet(); var bk = new Bookings(c);
            foreach (var card in p.Consents)
            {
                if (busy.Contains(card.Row.Id)) continue;
                var token = bk.ActionToken("consent", contact: card.Row.Id, days: 30);
                privacy.Record(card.Row.Id, "requested", "import", t.ConsentText, batch);
                bk.Queue(new JsonObject
                {
                    ["to"] = card.D.Str("email"), ["subject"] = "Conferma iscrizione · " + t.Name,
                    ["body"] = $"Nel nostro archivio risulta che hai scelto di ricevere novità e offerte di {t.Name}.\nPer continuare a riceverle conferma qui:\n{c.PublicOrigin.TrimEnd('/')}/action/{t.Slug}/{token}/\nSe non confermi non ti scriveremo più per questo. Se non ti interessa, ignora il messaggio.",
                }, card.Row.Id);
                sent++;
            }
        }
        var done = new Dictionary<string, int>(p.Report) { ["requested"] = sent };
        return done;
    }

    public static string Summary(Dictionary<string, int> r)
    {
        int R(string k) => r.GetValueOrDefault(k);
        var parts = new List<string> { $"{R("created")} clienti nuovi", $"{R("updated")} schede completate" };
        if (R("treatments") > 0) parts.Add($"{R("treatments")} trattamenti");
        if (R("amounts") > 0) parts.Add($"{R("amounts")} importi aggiunti a trattamenti già presenti");
        if (R("consents") > 0) parts.Add($"{R("consents")} consensi da confermare" + (R("requested") > 0 ? $", {R("requested")} email di conferma in coda" : ""));
        return "Importazione fatta: " + string.Join(", ", parts) + ".";
    }
}
