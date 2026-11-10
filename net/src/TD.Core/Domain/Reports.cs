using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>reports.py: the reports of a period (occupancy, absences and late cancellations, new and returning clients,
/// who leaves with the next appointment, origin, estimated value) and, from the clients' records, visits and money by
/// operator, month, year, service and client; each table as head and rows (numbers as numbers).</summary>
public sealed class Reports(Ctx c, Viewer v)
{
    public sealed record Table(string[] Head, List<object[]> Rows);
    public static readonly (string Key, string Title)[] TITLES =
    {
        ("lavori_operatore", "Lavori e incassi per operatore"), ("lavori_mese", "Visite e incassi per mese"), ("lavori_anno", "Anno per anno, con gli operatori"),
        ("lavori_servizio", "I servizi più fatti"), ("da_incassare", "Da incassare"), ("sospesi", "Trattamenti non nello scontrino"), ("gift_card", "Gift card vendute"), ("lavori_cliente", "Clienti: visite e speso"), ("elenco", "Elenco dei trattamenti"),
        ("occupazione", "Occupazione per operatore e risorsa"), ("assenze", "Assenze e disdette all'ultimo momento"), ("clienti", "Clienti nuovi e di ritorno"),
        ("origine", "Da dove arrivano le prenotazioni"), ("valore", "Valore stimato"),
    };
    static readonly Dictionary<string, string> ORIGIN = new() { ["public"] = "Pagina pubblica", ["wordpress"] = "Sito WordPress", ["desk"] = "Banco", ["walkin"] = "Senza appuntamento", ["import"] = "Importata", ["agenda"] = "Agenda del sito" };
    static readonly Dictionary<string, string> GIFT = new() { ["pending"] = "Da emettere", ["active"] = "Attiva", ["void"] = "Annullata" };

    static int Minutes(IEnumerable<Span> spans) => (int)Math.Floor(spans.Sum(s => (s.B - s.A).TotalSeconds) / 60);
    static int Pct(long n, long d) => d != 0 ? (int)Math.Round(100.0 * n / d, MidpointRounding.ToEven) : 0;
    static decimal Euro(long cents) => cents * 0.01m;
    static long Floor(long a, long b) => (long)Math.Floor((double)a / b);
    static long Mod(long a, long b) => a - b * Floor(a, b);
    static long Int(JsonNode n) => n is JsonValue x ? x.TryGetValue<long>(out var l) ? l : x.TryGetValue<double>(out var d) ? (long)d : 0 : 0;

    public Dictionary<string, Table> Tables(DateTime a, DateTime b, DateOnly first, DateOnly last, bool occupancy = true)
    {
        var outp = new Dictionary<string, Table>(); var cal = c.Cal;
        var (vis, vargs) = Permissions.VisibleBookings(v.Member, v.Perms, "b");
        var p = new DynamicParameters(new { a, z = b }); if (vargs != null) p.AddDynamicParams(vargs);
        var rows = c.S.Query<Booking>($"SELECT b.* FROM core_booking b WHERE b.start>=@a AND b.start<@z AND ({vis})", p).ToList();
        var held = rows.Where(x => x.Status != "cancelled").ToList();
        string Name(Booking x) => x.Snap.Str("service") is var s && s != "" ? s : c.Row<Service>(x.ServiceId).Name;
        // occupancy: booked minutes over the open minutes of each operator or room, time blocks excluded
        var booked = new Dictionary<Guid, int>();
        foreach (var x in held) if (x.ResourceId.HasValue) booked[x.ResourceId.Value] = booked.GetValueOrDefault(x.ResourceId.Value) + Minutes(new[] { new Span(x.Start, x.End < b ? x.End : b) });
        var occ = new List<object[]>();
        if (occupancy)
        {
            var where = "r.active"; var rp = new DynamicParameters(); var m = v.Member;  // the same reach as visible bookings
            if (m != null && v.Resources.Count > 0) { where += " AND r.id = ANY(@r)"; rp.Add("r", v.Resources.Select(Guid.Parse).ToArray()); }
            else if (m != null && (v.Locations.Count > 0 || m.Role == "operator")) { where += " AND r.location_id = ANY(@l)"; rp.Add("l", v.Locations.Select(Guid.Parse).ToArray()); }
            foreach (var r in c.S.Query<Resource>($"SELECT r.* FROM core_resource r JOIN core_location l ON l.id=r.location_id WHERE {where} ORDER BY l.name, r.name", rp))
            {
                // the location's hours as well, not only the business's
                var levels = new List<Calendar.Level> { new(null, null, null), new(r.LocationId, null, null), new(null, null, r.Id) };
                var spans = new List<Span>();
                for (var d = first; d <= last; d = d.AddDays(1))
                {
                    var d0 = cal.Local(d, TimeSpan.Zero).Value; var d1 = cal.Local(d.AddDays(1), TimeSpan.Zero).Value;
                    spans.AddRange(cal.Windows(d, levels).Where(w => w.A < d1 && w.B > d0).Select(w => new Span(w.A > d0 ? w.A : d0, w.B < d1 ? w.B : d1)));
                }
                var free = Minutes(Calendar.Minus(spans, cal.BlockSpans(first, last, r.Id).Select(x => new Span(x.A, x.B))));
                var n = booked.GetValueOrDefault(r.Id);
                occ.Add(new object[] { r.Name, c.Row<Location>(r.LocationId).Name, free, n, Pct(n, free) });
            }
        }
        outp["occupazione"] = new(new[] { "Risorsa", "Sede", "Minuti disponibili", "Minuti prenotati", "Occupazione %" }, occ);
        // new client: no booking kept before this one
        var ids = rows.Where(x => x.ContactId.HasValue).Select(x => x.ContactId.Value).Distinct().ToArray();
        var since = c.S.Query<(Guid, DateTime)>("SELECT contact_id, min(start) FROM core_booking WHERE contact_id = ANY(@i) AND status<>'cancelled' GROUP BY contact_id", new { i = ids }).ToDictionary(x => x.Item1, x => x.Item2);
        bool IsNew(Booking x) => !since.TryGetValue(x.ContactId.Value, out var f) || x.Start <= f;
        var groups = new Dictionary<string, int[]>();
        foreach (var x in rows)
        {
            var keys = new List<string> { Name(x) }; if (x.ContactId.HasValue) keys.Add(IsNew(x) ? "Clienti nuovi" : "Clienti abituali"); keys.Add("Totale");
            foreach (var k in keys)
            {
                if (!groups.TryGetValue(k, out var g)) groups[k] = g = new int[3];
                g[0]++; if (x.Status == "no_show") g[1]++; if (Agenda.Late(x.Status, x.Start, x.Snap)) g[2]++;
            }
        }
        var fixedKeys = new[] { "Clienti nuovi", "Clienti abituali", "Totale" };
        var order = groups.Keys.Where(k => !fixedKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).Concat(fixedKeys.Where(groups.ContainsKey));
        outp["assenze"] = new(new[] { "Servizio o clienti", "Prenotazioni", "Assenze", "Assenze %", "Disdette all'ultimo momento", "Disdette all'ultimo momento %" },
            order.Select(k => new object[] { k, groups[k][0], groups[k][1], Pct(groups[k][1], groups[k][0]), groups[k][2], Pct(groups[k][2], groups[k][0]) }).ToList());
        // clients of the period, new or returning, and the visits done that left with the next appointment already booked
        var clients = new Dictionary<Guid, bool>();
        foreach (var x in held.OrderByDescending(x => x.Start)) if (x.ContactId.HasValue) clients[x.ContactId.Value] = IsNew(x);  // the first of the period decides
        var visits = held.Where(x => x.Status == "completed" && x.ContactId.HasValue).ToList();
        var after = c.S.Query<(Guid, DateTime, DateTime)>("SELECT contact_id, start, created FROM core_booking WHERE contact_id = ANY(@i) AND start>=@a AND status<>'cancelled'", new { i = visits.Select(x => x.ContactId.Value).Distinct().ToArray(), a })
            .ToLookup(x => x.Item1, x => (S: x.Item2, C: x.Item3));
        bool Rebooked(Booking x)
        {
            var close = cal.Local(cal.DayOf(x.Start).AddDays(1), TimeSpan.Zero).Value;
            return after[x.ContactId.Value].Any(n => n.S >= x.End && n.C < close);
        }
        var back = visits.Count(Rebooked); var fresh = clients.Values.Count(x => x);
        outp["clienti"] = new(new[] { "Voce", "Valore" }, new()
        {
            new object[] { "Clienti nuovi", fresh }, new object[] { "Clienti di ritorno", clients.Count - fresh }, new object[] { "Visite fatte", visits.Count },
            new object[] { "Uscite con il prossimo appuntamento già preso", back }, new object[] { "Uscite con il prossimo appuntamento %", Pct(back, visits.Count) },
        });
        // where they came from, and how many arrived while the business was closed
        var origin = new Dictionary<(string, string), int[]>();
        foreach (var x in rows)
        {
            var k = (ORIGIN.GetValueOrDefault(x.Origin ?? "") ?? "Non registrata", x.Source ?? "");
            if (!origin.TryGetValue(k, out var g)) origin[k] = g = new int[2];
            g[0]++; if (!cal.Windows(cal.DayOf(x.Created), c.Row<Service>(x.ServiceId)).Any(w => w.A <= x.Created && x.Created < w.B)) g[1]++;
        }
        outp["origine"] = new(new[] { "Origine", "Fonte", "Prenotazioni", "Arrivate a locale chiuso" }, origin.OrderByDescending(x => x.Value[0]).Select(x => new object[] { x.Key.Item1, x.Key.Item2, x.Value[0], x.Value[1] }).ToList());
        // the value at the list price of the booking, not money taken
        var value = new Dictionary<string, (int N, decimal V)>();
        foreach (var x in held)
        {
            if (x.Status == "no_show") continue;
            var k = Name(x); var cur = value.GetValueOrDefault(k);
            var raw = Json.Truthy(x.Snap["total"]) ? x.Snap.Str("total") : x.Snap.Str("price");
            value[k] = (cur.N + 1, cur.V + (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var dv) ? dv : 0));
        }
        var vr = value.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new object[] { x.Key, x.Value.N, x.Value.V }).ToList();
        if (value.Count > 0) vr.Add(new object[] { "Totale", value.Values.Sum(x => x.N), value.Values.Sum(x => x.V) });
        outp["valore"] = new(new[] { "Servizio", "Prenotazioni", "Valore stimato da listino (€)" }, vr);
        return outp;
    }

    /// <summary>From the clients' records (receipts and the history of the old program): visits and money by operator,
    /// month, year, service and client, the figures on top and the bars of the dashboard. Only for who may export; the
    /// clients with their names only with pii.read.</summary>
    public (Dictionary<string, Table> Tables, JsonObject Dash) RecordTables(DateOnly first, DateOnly last)
    {
        var outp = new Dictionary<string, Table>();
        if (!v.Can("export.operational")) return (outp, null);
        var names = v.Can("pii.read"); var cal = c.Cal;
        var byStaff = new Dictionary<string, long[]>(); var byMonth = new SortedDictionary<string, long[]>(StringComparer.Ordinal); var byClient = new Dictionary<Guid, (long N, long Cost, DateOnly? Last)>();
        var byYear = new SortedDictionary<int, (long[] T, Dictionary<string, long> Who)>(); var byService = new Dictionary<string, int>(); var listed = new List<(Treatment T, JsonObject D, long Cost)>();
        static long[] At<K>(IDictionary<K, long[]> d, K k) { if (!d.TryGetValue(k, out var x)) d[k] = x = new long[2]; return x; }
        // shortcut: a client record has no location; a location administrator sees the business's records, as on the client card
        foreach (var t in c.S.Query<Treatment>("SELECT * FROM core_treatment ORDER BY date, created"))
        {
            var d = c.Decrypt(t.Pii, "treatment:" + t.Id); var cost = Int(d["amount"]); var date = DateOnly.FromDateTime(t.Date);
            var split = new List<(string Who, long C)>(); var by = d["by"] as JsonObject;
            if (by != null) foreach (var (k, x) in by) split.Add((k, Int(x)));
            var staff = d.Str("staff").Split(',').Select(x => x.Trim()).Where(x => x != "").ToList();
            var n = d.Str("kind") == "payment" ? 0 : 1;  // a «Da pagare» paid later is money in on its day, not one more visit
            if (split.Count == 0 && staff.Count > 0)
            {
                var m = new Dictionary<string, long>(); for (int i = 0; i < staff.Count; i++) m[staff[i]] = Floor(cost, staff.Count) + (i == 0 ? Mod(cost, staff.Count) : 0);
                split = m.Select(x => (x.Key, x.Value)).ToList();
            }
            if (split.Count == 0) split.Add(("", cost));
            else if (cost != 0 && split.Sum(x => x.C) != cost && by != null && by.Count > 0)
            {
                // products and lines without an operator
                var rest = cost - split.Sum(x => x.C); var i = split.FindIndex(x => x.Who == "");
                if (i >= 0) split[i] = ("", split[i].C + rest); else split.Add(("", rest));
            }
            if (!byYear.TryGetValue(date.Year, out var y)) byYear[date.Year] = y = (new long[2], new());
            y.T[0] += n; y.T[1] += cost;
            foreach (var (who, x) in split) { var k = who == "" ? "Senza operatore" : who; y.Who[k] = y.Who.GetValueOrDefault(k) + x; }
            if (date < first || date > last) continue;
            foreach (var (who, x) in split) { var s = At(byStaff, who == "" ? "Senza operatore" : who); s[0] += n; s[1] += x; }
            var mo = At(byMonth, date.ToString("yyyy-MM")); mo[0] += n; mo[1] += cost;
            var cl = byClient.GetValueOrDefault(t.ContactId); cl = (cl.N + n, cl.Cost + cost, cl.Last);
            if (n > 0)
            {
                cl.Last = cl.Last.HasValue && cl.Last > date ? cl.Last : date;
                foreach (var one in d.Str("service").Split(" + ")) if (one.Trim() != "") byService[one.Trim()] = byService.GetValueOrDefault(one.Trim()) + 1;
            }
            byClient[t.ContactId] = cl; listed.Add((t, d, cost));
        }
        var total = new[] { byMonth.Values.Sum(x => x[0]), byMonth.Values.Sum(x => x[1]) };
        var staffRows = byStaff.OrderByDescending(x => x.Value[1]).ToList();
        outp["lavori_operatore"] = new(new[] { "Operatore", "Lavori", "Incassato (€)" }, staffRows.Select(x => new object[] { x.Key, x.Value[0], Euro(x.Value[1]) }).ToList());
        var months = byMonth.Select(x => new object[] { x.Key[5..] + "/" + x.Key[..4], x.Value[0], Euro(x.Value[1]) }).ToList();
        if (byMonth.Count > 0) months.Add(new object[] { "Totale", total[0], Euro(total[1]) });
        outp["lavori_mese"] = new(new[] { "Mese", "Visite", "Incassato (€)" }, months);
        // Counter + Counter keeps only what is above zero
        var sum = new Dictionary<string, long>(); foreach (var y in byYear.Values) foreach (var (k, x) in y.Who) sum[k] = sum.GetValueOrDefault(k) + x;
        var team = sum.Where(x => x.Value > 0).OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
        outp["lavori_anno"] = new(new[] { "Anno", "Visite", "Incassato (€)" }.Concat(team).ToArray(),
            byYear.Select(x => new object[] { x.Key, x.Value.T[0], Euro(x.Value.T[1]) }.Concat(team.Select(n => (object)Euro(x.Value.Who.GetValueOrDefault(n)))).ToArray()).ToList());
        outp["lavori_servizio"] = new(new[] { "Servizio", "Volte" }, byService.OrderByDescending(x => x.Value).Select(x => new object[] { x.Key, x.Value }).ToList());
        var people = new Dictionary<Guid, string>();
        string Person(Guid id)
        {
            if (people.TryGetValue(id, out var s)) return s;
            var ct = c.Row<Contact>(id); var d = ct == null ? new JsonObject() : c.Decrypt(ct.Pii, "contact:" + id);
            return people[id] = string.Join(" ", new[] { d.Str("first_name"), d.Str("last_name") }.Where(x => x != "")) is var n && n != "" ? n : "Cliente";
        }
        if (names)
        {
            outp["lavori_cliente"] = new(new[] { "Cliente", "Visite", "Speso (€)", "Ultima visita" },
                byClient.Select(x => new object[] { Person(x.Key), x.Value.N, Euro(x.Value.Cost), x.Value.Last?.ToString("dd/MM/yyyy") ?? "" }).OrderByDescending(r => (decimal)r[2]).ToList());
            outp["elenco"] = new(new[] { "Data", "Cliente", "Trattamento", "Operatore", "Importo (€)" },
                Enumerable.Reverse(listed).Select(x => new object[] { x.T.Date.ToString("dd/MM/yyyy"), Person(x.T.ContactId), x.D.Str("service"), x.D.Str("staff"), Euro(x.Cost) }).ToList());
        }
        // what the clients still owe, today, whatever the period: the «Da pagare» of the Cassa not paid yet
        var open = c.S.Query<Sale>("SELECT * FROM core_sale WHERE kind='sale' AND status='done' AND due>0 AND settled IS NULL ORDER BY created").ToList();
        string Who(Guid? id) => id.HasValue ? names ? Person(id.Value) : "Cliente" : "";
        outp["da_incassare"] = new(new[] { "Cliente", "Scontrino del", "Numero", "Da pagare (€)", "Da giorni" }, open.Select(s =>
        {
            var day = cal.DayOf(s.Printed ?? s.Created);
            return new object[] { s.ContactId.HasValue && names ? Person(s.ContactId.Value) : "Cliente", day.ToString("dd/MM/yyyy"), Till.Doc(s), s.Due, c.Today.DayNumber - day.DayNumber };
        }).ToList());
        // Preventivo pagamenti cassa, to this day: done and not paid, with no receipt yet
        var parked = c.S.Query<Parked>("SELECT * FROM core_parked ORDER BY created").ToList(); var pr = new List<object[]>();
        foreach (var x in parked)
            foreach (var l in c.Decrypt(x.Pii, "parked:" + x.Id)["lines"] as JsonArray ?? new JsonArray())
                pr.Add(new object[] { Who(x.ContactId), cal.ToLocal(x.Created).ToString("dd/MM/yyyy"), (Int(l["qty"]) > 1 ? $"{Int(l["qty"])} x " : "") + l.Str("desc"), l.Str("staff"), Euro(Int(l["qty"]) * Int(l["cents"]) - Int(l["off"])) });
        outp["sospesi"] = new(new[] { "Cliente", "Del", "Cosa", "Operatore", "Importo (€)" }, pr);
        // gift cards sold in the period, and the credit left on each
        var ga = cal.Local(first, TimeSpan.Zero).Value; var gz = cal.Local(last.AddDays(1), TimeSpan.Zero).Value;
        outp["gift_card"] = new(new[] { "Codice", "Intestata a", "Venduta il", "Valore (€)", "Credito (€)", "Scade", "Stato" },
            c.S.Query<Giftcard>("SELECT * FROM core_giftcard WHERE created>=@a AND created<@z AND status<>'pending' ORDER BY created", new { a = ga, z = gz })
                .Select(g => new object[] { g.Code, Who(g.ContactId), cal.ToLocal(g.Created).ToString("dd/MM/yyyy"), g.Amount, g.Balance, g.Expires?.ToString("dd/MM/yyyy") ?? "", GIFT.GetValueOrDefault(g.Status, g.Status) }).ToList());
        // the dashboard: the figures of the period and its bars
        if (total[0] == 0 && open.Count == 0 && parked.Count == 0) return (outp, null);
        var dash = new JsonObject
        {
            ["incassato"] = Euro(total[1]), ["visite"] = total[0], ["media"] = total[0] != 0 ? Euro(Floor(total[1], total[0])) : 0m, ["clienti"] = byClient.Count,
            ["mesi"] = new JsonArray(byMonth.TakeLast(24).Select(x => (JsonNode)new JsonObject { ["nome"] = x.Key[5..] + "/" + x.Key[2..4], ["incassato"] = Euro(x.Value[1]) }).ToArray()),
            ["operatori"] = new JsonArray(staffRows.Select(x => (JsonNode)new JsonObject { ["nome"] = x.Key, ["incassato"] = Euro(x.Value[1]) }).ToArray()),
            ["daIncassare"] = open.Sum(s => s.Due), ["daIncassareN"] = open.Count, ["sospesi"] = parked.Sum(x => x.Total), ["sospesiN"] = parked.Count,
        };
        return (outp, dash);
    }

    /// <summary>portal.csv_cell, profile sheet: a text a spreadsheet would read as a formula gets an apostrophe.</summary>
    public static string SheetCell(string s) =>
        s.TrimStart().Length > 0 && "=+-@".Contains(s.TrimStart()[0]) || s.Length > 0 && "\t\r\n".Contains(s[0]) ? "'" + s : s;

    /// <summary>A one-sheet Excel file without libraries: inline strings, numbers as numbers.</summary>
    public static byte[] Xlsx(string[] head, IEnumerable<object[]> rows, string title)
    {
        static string Cell(object x) => x switch
        {
            null => "<c t=\"inlineStr\"><is><t xml:space=\"preserve\"></t></is></c>",
            int or long or decimal or double => $"<c t=\"n\"><v>{Convert.ToString(x, CultureInfo.InvariantCulture)}</v></c>",
            _ => $"<c t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(SheetCell(x.ToString())).Replace("&quot;", "\"").Replace("&apos;", "'")}</t></is></c>",
        };
        var data = string.Concat(new[] { head.Cast<object>().ToArray() }.Concat(rows).Select(r => "<row>" + string.Concat(r.Select(Cell)) + "</row>"));
        using var buf = new MemoryStream();
        using (var z = new ZipArchive(buf, ZipArchiveMode.Create, true))
        {
            void Put(string name, string text) { using var w = new StreamWriter(z.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)); w.Write(text); }
            Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Put("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{SecurityElement.Escape(title.Length > 31 ? title[..31] : title)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/worksheets/sheet1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetData>{data}</sheetData></worksheet>");
        }
        return buf.ToArray();
    }
}
