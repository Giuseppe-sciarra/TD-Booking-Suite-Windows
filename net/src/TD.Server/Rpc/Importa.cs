using TD.Core;
using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.contacts_import: clients and treatment history from the old program (CSV or Excel): file, columns,
/// preview, import. Needs what adding clients and treatments by hand needs; the marketing consents also the privacy
/// permission. The window keeps the token of the sealed copy between the steps (the web page kept it in the session).</summary>
public static class Importa
{
    static string CapFirst(string s) => s == "" ? s : char.ToUpper(s[0]) + s[1..];
    static object Lines(ClientImport.Reasons r) => r.Items.Select(x => new
    {
        motivo = CapFirst(x.Reason),
        righe = x.Lines.Count == 1 ? $"riga {x.Lines[0]}" : $"{x.Lines.Count} righe, {string.Join(", ", x.Lines.Take(12))}" + (x.Lines.Count > 12 ? " e altre" : ""),
    }).ToList();

    [Rpc("clienti.importa", Perm = "contacts.read")]
    public static object Clients(Call c)
    {
        foreach (var perm in new[] { "pii.read", "notes.read", "booking.write" }) c.Require(perm);
        var x = c.C; var t = c.Tenant; var imp = new ClientImport(x); var action = c.Str("azione"); var token = c.Str("token"); var error = "";
        var spec = Presets.RecordOf(t.BusinessType, x.S.Scalar<long>("SELECT count(*) FROM core_service WHERE kind='resource'") > 0); var consentOk = c.Can("privacy.manage");
        object Page(object more = null) => new { spec = spec == null ? null : new { titolo = spec.Title, uno = spec.One }, consensi = consentOk, marketing = t.MarketingEnabled, conservazione = t.RetentionDays, errore = error, file = more };
        if (action == "upload")
        {
            byte[] raw; try { raw = Convert.FromBase64String(c.Str("file")); } catch (FormatException) { raw = Array.Empty<byte>(); }
            try
            {
                if (raw.Length == 0) throw new BookingException("Scegli il file da importare.");
                if (raw.Length > Importer.MAX_BYTES) throw new BookingException("File troppo grande: al massimo 20 MB.");
                var (h, r) = Importer.Read(raw); ClientImport.Drop(token);
                token = imp.Stash(c.Str("nome"), h, r); action = "";
            }
            catch (BookingException ex) { error = ex.Message; return Page(); }
        }
        var stash = token != "" ? imp.Unstash(token) : null;
        if (action == "reset" || stash == null) { ClientImport.Drop(token); return Page(); }
        var (name, head, rows) = stash.Value;
        var choices = ClientImport.Fields(spec != null, consentOk); var allowed = choices.Select(f => f.Key).ToHashSet();
        List<string> mapping;
        if (action is "preview" or "import") { var posted = (c.Args["colonne"] as JsonArray)?.Select(v => v?.ToString() ?? "").ToList() ?? new(); mapping = Enumerable.Range(0, head.Count).Select(i => i < posted.Count && allowed.Contains(posted[i]) ? posted[i] : "").ToList(); }
        else mapping = ClientImport.Guess(head, spec != null, consentOk);
        var ask = c.Bool("ask") && consentOk && t.MarketingEnabled && mapping.Contains("consent");
        var check = token + "|" + string.Join(",", mapping) + (ask ? ":ask" : "");  // bound to this very file, not only to its columns
        // the import runs only on the columns of the preview just seen
        if (action == "import" && c.Str("checked") != check) { action = "preview"; error = "Hai cambiato qualche colonna: controlla di nuovo l'anteprima."; }
        ClientImport.Planned plan = null;
        try
        {
            if (action == "import")
            {
                // NO KEY UPDATE: bookings wait as with every writer, while a card saved meanwhile can still write its
                // audit row; the cards themselves are locked by the plan
                x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR NO KEY UPDATE", new { t = t.Id });
                var done = imp.Apply(imp.Plan(head, rows, mapping, spec != null, consentOk, true), ask, token);
                x.Audit("contacts.imported", token, new { rows = done.GetValueOrDefault("rows"), created = done.GetValueOrDefault("created"), updated = done.GetValueOrDefault("updated"), treatments = done.GetValueOrDefault("treatments"), consents = done.GetValueOrDefault("consents"), requested = done.GetValueOrDefault("requested") });
                x.AfterCommit.Add(() => ClientImport.Drop(token));
                return new { fatto = ClientImport.Summary(done) };
            }
            if (action == "preview") plan = imp.Plan(head, rows, mapping, spec != null, consentOk);
        }
        catch (BookingException ex) { error = ex.Message; }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") { error = "Nel frattempo qualcuno ha aggiunto uno di questi clienti: premi di nuovo Importa, l'anteprima lo terrà in conto."; }
        return Page(new
        {
            token, nome = name, righe = rows.Count, campi = choices.Select(f => new { id = f.Key, nome = f.Label }), ask, verificato = check,
            colonne = head.Select((h, i) => new { titolo = h, esempi = string.Join(" · ", ClientImport.Samples(rows, i)), campo = mapping[i] }),
            piano = plan == null ? null : new
            {
                esempi = plan.Samples.Select(s => new { riga = s.Line, nome = s.Name, stato = s.Status, telefono = s.Phone, email = s.Email, voce = s.Entry }), problemi = Lines(plan.Problems), scarti = Lines(plan.Issues),
                dal = plan.Since.ToString("dd/MM/yyyy"), numeri = plan.Report,
            },
        });
    }
}

/// <summary>reports.py, import of the future appointments: every row booked as at the desk with Forza: what it goes
/// beyond is a conflict. The preview runs the same and takes it all back. No email to the clients (origin import).</summary>
public static class ImportaAppuntamenti
{
    static readonly (string Key, string Label)[] FIELDS =
    {
        ("", "Non importare"), ("date", "Data"), ("time", "Ora di inizio"), ("end", "Ora di fine"), ("minutes", "Durata in minuti"), ("end_date", "Data di partenza (soggiorni)"), ("service", "Servizio"), ("staff", "Operatore o risorsa"),
        ("full_name", "Nome e cognome insieme"), ("first_name", "Nome"), ("last_name", "Cognome"), ("phone", "Telefono"), ("email", "Email"), ("notes", "Note"), ("party", "Persone"),
    };
    static readonly Dictionary<string, string> GUESS = new()
    {
        ["data"] = "date", ["giorno"] = "date", ["dataappuntamento"] = "date", ["date"] = "date", ["ora"] = "time", ["orario"] = "time", ["orainizio"] = "time", ["inizio"] = "time", ["dalle"] = "time", ["time"] = "time",
        ["orafine"] = "end", ["fine"] = "end", ["alle"] = "end", ["durata"] = "minutes", ["minuti"] = "minutes", ["partenza"] = "end_date", ["datapartenza"] = "end_date", ["checkout"] = "end_date",
        ["servizio"] = "service", ["trattamento"] = "service", ["prestazione"] = "service", ["service"] = "service", ["operatore"] = "staff", ["operatrice"] = "staff", ["risorsa"] = "staff", ["staff"] = "staff",
        ["cliente"] = "full_name", ["nominativo"] = "full_name", ["nomecognome"] = "full_name", ["cognomenome"] = "full_name", ["nome"] = "first_name", ["cognome"] = "last_name",
        ["telefono"] = "phone", ["cellulare"] = "phone", ["cell"] = "phone", ["tel"] = "phone", ["email"] = "email", ["mail"] = "email", ["note"] = "notes", ["persone"] = "party", ["coperti"] = "party", ["ospiti"] = "party",
    };
    const int MAX_ROWS = 3000;  // shortcut: one request books the whole file, every row with the checks of the desk; past it, a background job

    /// <summary>10:30, 10.30, 1030 or an Excel time (a fraction of the day, also after a date number).</summary>
    static TimeSpan? Clock(string value)
    {
        var v = value.Trim(); var m = System.Text.RegularExpressions.Regex.Match(v, @"\b(\d{1,2})[:.](\d{2})"); if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(v, @"^(\d{1,2})(\d{2})\z");
        if (m.Success && int.Parse(m.Groups[1].Value) < 24 && int.Parse(m.Groups[2].Value) < 60) return new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        m = System.Text.RegularExpressions.Regex.Match(v, @"^\d*\.(\d+)\z");
        if (m.Success) { var n = (int)(Math.Round(double.Parse("0." + m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 1440, MidpointRounding.ToEven) % 1440); return new TimeSpan(n / 60, n % 60, 0); }
        return null;
    }
    static string Whole(string v) => v.Trim().Split('.')[0].Split(',')[0];  // 30, 30.0 of Excel, 30,0
    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>Python's json.dumps of [row, mapping], so the same row of the same file is the same booking in both programs.</summary>
    static string PyJson(IEnumerable<string> row, IEnumerable<string> mapping)
    {
        static string Text(string s)
        {
            var b = new System.Text.StringBuilder("\"");
            foreach (var ch in s)
                b.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f", _ => ch < 0x20 || ch > 0x7e ? "\\u" + ((int)ch).ToString("x4") : ch.ToString() });
            return b.Append('"').ToString();
        }
        static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Text)) + "]";
        return "[" + List(row) + ", " + List(mapping) + "]";
    }

    static (DateTime Start, JsonObject Data) BookingData(Ctx x, List<string> row, List<string> mapping, Dictionary<string, Service> services, Dictionary<string, Resource> resources, DateOnly today)
    {
        string Get(string f) { for (int i = 0; i < mapping.Count; i++) if (mapping[i] == f && i < row.Count && row[i].Trim() != "") return row[i]; return ""; }
        var d = ClientImport.Day(Get("date"), today) ?? throw new BookingException("data mancante o non valida");
        if (!services.TryGetValue(Crypto.CaseFold(Get("service").Trim()), out var s)) throw new BookingException(Get("service") != "" ? $"servizio «{Cut(Get("service"), 40)}» non trovato" : "servizio mancante");
        Resource r = null;
        if (Get("staff") != "" && !resources.TryGetValue(Crypto.CaseFold(Get("staff").Trim()), out r)) throw new BookingException($"operatore o risorsa «{Cut(Get("staff"), 40)}» non trovato");
        var at = s.Kind == "day" ? s.Checkin : Clock(Get("time")) ?? Clock(Get("date")) ?? throw new BookingException("ora mancante o non valida");
        var start = x.Cal.Local(d, at) ?? throw new BookingException("ora che non esiste per il cambio dell'ora legale");
        string first, last;
        if (Get("full_name") != "" && Get("first_name") == "") { var parts = Get("full_name").Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries); first = parts.ElementAtOrDefault(0) ?? ""; last = (parts.ElementAtOrDefault(1) ?? "").Trim(); }
        else { first = Get("first_name"); last = Get("last_name"); }
        var party = Whole(Get("party"));
        var data = new JsonObject
        {
            ["service"] = s.Id.ToString(), ["resource"] = r?.Id.ToString() ?? "", ["start"] = new DateTimeOffset(start, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:sszzz"), ["party"] = party != "" ? party : 1, ["first_name"] = first, ["last_name"] = last,
            ["phone"] = Get("phone"), ["email"] = Get("email"), ["notes"] = Cut(Get("notes"), 2000), ["marketing"] = false, ["force"] = true,
        };
        if (s.Kind == "day") data["end_date"] = (ClientImport.Day(Get("end_date"), today) ?? throw new BookingException("data di partenza mancante")).ToString("yyyy-MM-dd");
        else if (s.Kind is "event" or "shift")
        {
            var o = x.S.One<Occurrence>("SELECT * FROM core_occurrence WHERE service_id=@s AND start=@st AND active LIMIT 1", new { s = s.Id, st = start }) ?? throw new BookingException("nessuna data dell'evento o turno a quell'ora");
            data["occurrence"] = o.Id.ToString();
        }
        else
        {
            var end = Clock(Get("end")); var n = Get("minutes");
            if (end.HasValue) { var gap = end.Value - at; if (gap < TimeSpan.Zero) gap += TimeSpan.FromDays(1); n = ((int)gap.TotalMinutes).ToString(); }
            if (n != "") data["minutes"] = Whole(n);
        }
        return (start, data);
    }

    sealed record Problem(int N, bool Conflict, string Text);
    static (Dictionary<string, int>, List<Problem>) Run(Ctx x, List<List<string>> rows, List<string> mapping, bool force)
    {
        var now = x.Now; var today = x.Today; var report = new Dictionary<string, int> { ["ok"] = 0, ["same"] = 0, ["conflicts"] = 0, ["errors"] = 0, ["past"] = 0, ["forced"] = 0 }; var problems = new List<Problem>(); var key = x.Key; var bk = new Bookings(x);
        var services = new Dictionary<string, Service>(); foreach (var s in x.S.Query<Service>("SELECT s.* FROM core_service s JOIN core_location l ON l.id=s.location_id WHERE s.active AND l.active")) services[Crypto.CaseFold(s.Name.Trim())] = s;
        var resources = new Dictionary<string, Resource>(); foreach (var r in x.S.Query<Resource>("SELECT * FROM core_resource WHERE active")) resources[Crypto.CaseFold(r.Name.Trim())] = r;
        // a row that fails leaves nothing behind: a savepoint each, as the nested transactions of the Python program
        T Try<T>(Func<T> work) { x.S.Execute("SAVEPOINT riga"); try { var v = work(); x.S.Execute("RELEASE SAVEPOINT riga"); return v; } catch { x.S.Execute("ROLLBACK TO SAVEPOINT riga"); throw; } }
        for (int at = 0; at < rows.Count; at++)
        {
            var n = at + 2; var row = rows[at];
            try
            {
                var (start, data) = BookingData(x, row, mapping, services, resources, today);
                if (start < now) { report["past"]++; continue; }
                // the same row of the same file is the same booking, also when the file is loaded again
                var rk = "import-" + Crypto.Index(PyJson(row, mapping), key, "import")[..56]; data["request_key"] = rk;
                if (x.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE request_key=@rk", new { rk }) > 0) { report["same"]++; continue; }
                try { var (_, created) = Try(() => bk.Create((JsonObject)data.DeepClone(), origin: "import")); report[created ? "ok" : "same"]++; }
                catch (ForceWarning ex)
                {
                    var text = ex.Message; if (text.StartsWith("Stai forzando: ")) text = text["Stai forzando: ".Length..]; if (text.EndsWith(" Se va bene, conferma.")) text = text[..^" Se va bene, conferma.".Length];
                    if (!force) { report["conflicts"]++; problems.Add(new(n, true, text)); continue; }
                    data["force_ok"] = true; Try(() => bk.Create((JsonObject)data.DeepClone(), origin: "import")); report["forced"]++;
                }
            }
            catch (Exception ex) when (ex is BookingException or FormatException or InvalidCastException or ArgumentException) { report["errors"]++; problems.Add(new(n, false, ex.Message)); }
        }
        return (report, problems);
    }

    [Rpc("prenotazioni.importa", Perm = "booking.write")]
    public static object Bookings_(Call c)
    {
        foreach (var perm in new[] { "contacts.read", "pii.read" }) c.Require(perm);
        var x = c.C; var imp = new ClientImport(x); var action = c.Str("azione"); var token = c.Str("token"); var error = ""; var forceOk = c.Can("booking.force");
        object Page(object more = null) => new { errore = error, forza = forceOk, file = more };
        if (action == "upload")
        {
            byte[] raw; try { raw = Convert.FromBase64String(c.Str("file")); } catch (FormatException) { raw = Array.Empty<byte>(); }
            try
            {
                if (raw.Length == 0) throw new BookingException("Scegli il file da importare.");
                if (raw.Length > Importer.MAX_BYTES) throw new BookingException("File troppo grande: al massimo 20 MB.");
                var (h, r) = Importer.Read(raw);
                if (r.Count > MAX_ROWS) throw new BookingException($"Il file ha {r.Count} righe: se ne importano al massimo {MAX_ROWS} alla volta, dividilo in più file.");
                ClientImport.Drop(token); token = imp.Stash(c.Str("nome"), h, r); action = "";
            }
            catch (BookingException ex) { error = ex.Message; return Page(); }
        }
        var stash = token != "" ? imp.Unstash(token) : null;
        if (action == "reset" || stash == null) { ClientImport.Drop(token); return Page(); }
        var (name, head, rows) = stash.Value; var allowed = FIELDS.Select(f => f.Key).ToHashSet(); List<string> mapping;
        if (action is "preview" or "import") { var posted = (c.Args["colonne"] as JsonArray)?.Select(v => v?.ToString() ?? "").ToList() ?? new(); mapping = Enumerable.Range(0, head.Count).Select(i => i < posted.Count && allowed.Contains(posted[i]) ? posted[i] : "").ToList(); }
        else { mapping = new(); foreach (var h in head) { var f = GUESS.GetValueOrDefault(Importer.Plain(h), ""); mapping.Add(mapping.Contains(f) ? "" : f); } }
        var force = forceOk && c.Bool("force"); var check = token + "|" + string.Join(",", mapping);
        if (action == "import" && c.Str("checked") != check) { action = "preview"; error = "Hai cambiato qualche colonna: controlla di nuovo l'anteprima."; }
        Dictionary<string, int> report = null; List<Problem> problems = null;
        try
        {
            if (action is "preview" or "import" && !(mapping.Contains("date") && mapping.Contains("service"))) throw new BookingException("Indica almeno la colonna della data e quella del servizio.");
            if (action == "import")
            {
                (report, problems) = Run(x, rows, mapping, force);
                x.Audit("bookings.imported", token, report); x.AfterCommit.Add(() => ClientImport.Drop(token));
                int skipped = report["conflicts"] + report["errors"];
                return new { fatto = $"Importate {report["ok"] + report["forced"]} prenotazioni" + (report["forced"] > 0 ? $", {report["forced"]} forzate" : "") + (skipped > 0 ? $"; saltate {skipped} righe" : "") + (report["past"] > 0 ? $" e {report["past"]} già passate" : "") + "." };
            }
            if (action == "preview")
            {
                // the same code on a unit of work of its own, never saved
                using var trial = new Ctx(c.S.Db, c.S.Crypto, c.Tenant.Id) { Actor = c.User.Id, PublicOrigin = c.PublicOrigin, Edition = c.S.Edition };
                (report, problems) = Run(trial, rows, mapping, false);
            }
        }
        catch (BookingException ex) { error = ex.Message; }
        return Page(new
        {
            token, nome = name, righe = rows.Count, campi = FIELDS.Select(f => new { id = f.Key, nome = f.Label }), force, verificato = check,
            colonne = head.Select((h, i) => new { titolo = h, esempi = string.Join(" · ", ClientImport.Samples(rows, i)), campo = mapping[i] }),
            esito = report, problemi = problems?.Take(200).Select(p => new { riga = p.N, conflitto = p.Conflict, testo = p.Text }),
        });
    }
}
