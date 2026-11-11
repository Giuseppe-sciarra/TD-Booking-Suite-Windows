using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/cassa.py: the fiscal register (registratore telematico) of a location. The desk closes the bill, the
/// register prints and transmits the documento commerciale, TD keeps what it answered and, only when the client asks,
/// emails him a copy. TD never emits a fiscal document by itself.
/// Two protocols: RCH (Print!F: XML commands POSTed to /service.cgi on the shop's network, or the same commands through
/// USB, a virtual COM port) and Epson (Fiscal ePOS-Print POSTed to /cgi-bin/fpmate.cgi). A request is marked "sending"
/// and committed before it leaves: an answer lost on the way leaves it "unknown", and the operator checks the register
/// before sending it again, so a bill is never printed twice.</summary>
public sealed partial class Till(Ctx c)
{
    public static readonly (string Id, string Label)[] DEVICES = { ("", "Nessuno"), ("rch", "RCH Print!F"), ("epson", "Epson FP-81II / FP-90III RT") };
    public static readonly (string Id, string Label)[] PAYMENTS = { ("cash", "Contanti"), ("card", "Carta o bancomat"), ("other", "Altro"), ("due", "Da pagare") };
    public static readonly (string Id, string Label)[] SETTLE = PAYMENTS[..3];
    public static string PayLabel(string k) => PAYMENTS.FirstOrDefault(p => p.Id == k).Label ?? k;
    // shortcut: RCH 1 contanti, 4 carta, 2 non riscosso; Epson paymentType 0 contanti, 2 carta, 5 non riscosso: changed in Registratore di cassa
    public static readonly Dictionary<string, Dictionary<string, int>> DEFAULT_PAY = new()
    { ["rch"] = new() { ["cash"] = 1, ["card"] = 4, ["other"] = 2, ["due"] = 2 }, ["epson"] = new() { ["cash"] = 0, ["card"] = 2, ["other"] = 5, ["due"] = 5 } };
    public const string COPY_TEXT = "Il cliente vuole la copia dello scontrino per email.";
    public static readonly TimeSpan STALE = TimeSpan.FromSeconds(120);
    /// <summary>The register answers a closure when it has printed it and sent the day: slower than a receipt, under STALE.</summary>
    public const int CLOSE_WAIT = 90;
    static readonly Regex CF = new("^([A-Z0-9]{16}|[0-9]{11})$"), LOTTERY = new("^[A-Z0-9]{8}$");
    public static readonly Regex PORT = new("^(COM[0-9]{1,3}|/dev/tty[A-Za-z0-9]{1,20})$");
    public static readonly int[] BAUDS = { 9600, 19200, 38400, 57600, 115200 };
    public static readonly Dictionary<string, (string Default, string Need)> RCH_CMDS = new()
    { ["rch_off"] = ("=V-/${importo}", "{importo}"), ["rch_void"] = ("=k/&{data}/[{z}/]{numero}", "{numero}"), ["rch_close"] = ("=C3 =C10 =C1", "") };

    public static JsonObject Conf(Location l) => Json.Obj(l.Till);
    public static int Int(JsonNode n, int fallback = 0) => n is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? (int)l : int.TryParse(v.ToString(), out var p) ? p : fallback : fallback;
    public static int PayCode(JsonObject conf, string k) => conf["payments"]?[k] != null ? Int(conf["payments"][k]) : DEFAULT_PAY[conf.Str("device")][k];
    public static string RchCmd(JsonObject conf, string key) => conf.ContainsKey(key) ? conf.Str(key) : RCH_CMDS[key].Default;

    // ---------- the protocols
    /// <summary>lan(): 192.168.1.50 or 192.168.1.50:8080: an address of the shop's own network, never a name or a public address.</summary>
    public static string Lan(string address)
    {
        var parts = (address ?? "").Trim().Split(':', 2); var port = parts.Length > 1 ? parts[1] : "";
        if (!Regex.IsMatch(parts[0], @"^\d{1,3}(\.\d{1,3}){3}$") || !IPAddress.TryParse(parts[0], out var ip) || port != "" && !(int.TryParse(port, out var p) && p is > 0 and < 65536))
            throw new BookingException("Scrivi l'indirizzo IP della cassa nella rete del negozio, per esempio 192.168.1.50.");
        if (!Private(ip)) throw new BookingException("L'indirizzo deve essere della rete del negozio (192.168…, 10…, 172.16…).");
        return port != "" ? $"{ip}:{int.Parse(port)}" : ip.ToString();
    }
    /// <summary>ipaddress.is_private for IPv4, without 0.0.0.0.</summary>
    public static bool Private(IPAddress ip)
    {
        var b = ip.GetAddressBytes(); if (b.Length != 4 || b.All(x => x == 0)) return false;
        return b[0] is 0 or 10 or 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
            || b[0] == 192 && b[1] == 0 && (b[2] == 2 || b[2] == 0 && (b[3] <= 7 || b[3] is 170 or 171))
            || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) || b[0] == 203 && b[1] == 0 && b[2] == 113 || b[0] >= 240;
    }
    public static string Url(JsonObject conf) =>
        conf.Str("device") == "rch" ? $"http://{conf.Str("address")}/service.cgi" : $"http://{conf.Str("address")}/cgi-bin/fpmate.cgi?devid=local_printer&timeout=10000";
    /// <summary>text(): printable on the register: no accents, no control characters, no parentheses (they close RCH's text).</summary>
    public static string Text(string value, int n = 32)
    {
        var sb = new StringBuilder();
        foreach (var ch in (value ?? "").Normalize(NormalizationForm.FormKD)) if (ch < 128) sb.Append(ch);
        var s = string.Join(" ", Regex.Replace(sb.ToString(), @"[()\x00-\x1f\x7f]", "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > n ? s[..n] : s;
    }
    public static string Money(int cents) => $"{cents / 100},{cents % 100:00}";
    public static string Euro(int cents) => Prices.Euro(cents / 100m);
    static int GiftCents(JsonObject d) => Int(d["gift"]?["cents"]);
    static IEnumerable<JsonObject> Lines(JsonObject d) => (d["lines"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
    public static List<(string Kind, int Cents)> Payments(JsonObject d) => (d["payments"] as JsonArray)?.OfType<JsonArray>().Select(p => (p[0].ToString(), Int(p[1]))).ToList() ?? new();
    static string OffLabel(JsonObject l) => l.Str("pct") != "" ? $"SCONTO {l.Str("pct")}%" : "SCONTO";

    /// <summary>units(): RCH: one sale line a piece, with the discount of its row split over its pieces (line=false: taken off
    /// the price); the discount on the whole bill spread over the pieces in proportion, the cents left on the dearest.</summary>
    static List<(string Desc, int Dept, int Cents, int Off)> Units(JsonObject d, bool line)
    {
        var rows = new List<(string Desc, int Dept, int Cents, int Off)>();
        foreach (var l in Lines(d))
        {
            int o = Int(l["off"]), q = Int(l["qty"]); var cut = Enumerable.Repeat(o / q, q).ToArray(); cut[^1] += o - cut.Sum();
            rows.AddRange(cut.Select(x => (l.Str("desc"), Int(l["dept"]), Int(l["cents"]), x)));
        }
        if (!line) rows = rows.Select(r => (r.Desc, r.Dept, r.Cents - r.Off, 0)).ToList();
        foreach (var (kind, off) in new[] { ("discount", Int(d["discount"])), ("gift", GiftCents(d)) })
        {
            if (off == 0) continue;
            var net = rows.Select(r => r.Cents - r.Off).ToList(); var full = net.Sum(); var cut = net.Select(n => (int)((long)n * off / full)).ToArray();
            var top = net.IndexOf(net.Max()); cut[top] += off - cut.Sum();
            for (int i = 0; i < rows.Count; i++)
                rows[i] = kind == "gift" && line ? rows[i] with { Off = rows[i].Off + cut[i] } : rows[i] with { Cents = rows[i].Cents - cut[i] };  // the gift card as the row's discount
        }
        return rows;
    }

    /// <summary>rch(): the commands, one a line: in the XML of the web service, or a frame each through USB.</summary>
    public static List<string> Rch(Sale sale, JsonObject d, JsonObject conf)
    {
        var cmds = new List<string> { "=K" };  // clears whatever a failed request left open
        if (sale.Kind is "sale" or "void" or "test") cmds.Add("=C1");  // the key back on REG
        if (sale.Kind == "test") cmds.AddRange(new[] { "=o", "=\"/(PROVA DI COLLEGAMENTO)", "=\"/(TD BOOKING SUITE)", "=\"/(DOCUMENTO NON FISCALE)", "=o" });
        if (sale.Kind == "sale")
        {
            if (d.Str("cf") != "") cmds.Add($"=\"/?C/({d.Str("cf")})");
            if (d.Str("lottery") != "") cmds.Add($"=\"/?L/$1/({d.Str("lottery")})");
            var off = RchCmd(conf, "rch_off");
            foreach (var (desc, dept, cents, x) in Units(d, off != ""))
            {
                cmds.Add($"=R{dept}/${cents}/({Text(desc)})");
                if (x != 0) cmds.Add(off.Replace("{importo}", x.ToString()));
            }
            // a bill all paid with a gift card closes at 0: =T with no amount is the exact total
            cmds.Add("=S"); cmds.AddRange(Payments(d).Select(p => p.Cents != 0 ? $"=T{PayCode(conf, p.Kind)}/${p.Cents}" : $"=T{PayCode(conf, p.Kind)}"));
        }
        else if (sale.Kind == "void")
        {
            var day = DateOnly.ParseExact(d.Str("day"), "yyyy-MM-dd"); var z = d.Str("z");
            foreach (var cmd in RchCmd(conf, "rch_void").Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                cmds.Add((z != "" ? cmd : Regex.Replace(cmd, @"\[[^\]]*\{z\}[^\]]*\]|\{z\}/?", "")).Replace("{data}", day.ToString("ddMMyy")).Replace("{z}", z != "" ? int.Parse(z).ToString("0000") : "").Replace("{numero}", int.Parse(d.Str("number")).ToString("0000")));
        }
        else if (sale.Kind == "close") cmds.AddRange(RchCmd(conf, "rch_close").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        return cmds;
    }

    /// <summary>xml.sax.saxutils.quoteattr</summary>
    static string Q(string s)
    {
        s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "&#10;").Replace("\r", "&#13;").Replace("\t", "&#9;");
        if (!s.Contains('"')) return "\"" + s + "\"";
        return s.Contains('\'') ? "\"" + s.Replace("\"", "&quot;") + "\"" : "'" + s + "'";
    }
    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public string Epson(Sale sale, JsonObject d, JsonObject conf)
    {
        string body;
        if (sale.Kind == "probe") body = "<printerCommand><queryPrinterStatus operator=\"1\" statusType=\"1\" /></printerCommand>";
        else if (sale.Kind == "test") body = "<printerNonFiscal><beginNonFiscal operator=\"1\" /><printNormal operator=\"1\" font=\"1\" data=\"PROVA DI COLLEGAMENTO TD BOOKING SUITE\" /><printNormal operator=\"1\" font=\"1\" data=\"DOCUMENTO NON FISCALE\" /><endNonFiscal operator=\"1\" /></printerNonFiscal>";
        else if (sale.Kind == "close") body = "<printerFiscalReport><printZReport operator=\"1\" /></printerFiscalReport>";
        else if (sale.Kind == "void")
        {
            // the register finds the document by Z, number, day and its serial, then prints the annullo by itself
            var o = sale.VoidsId.HasValue ? c.S.Get<Sale>(sale.VoidsId.Value) : null;
            var day = d.Str("day") != "" ? DateOnly.ParseExact(d.Str("day"), "yyyy-MM-dd") : c.Cal.DayOf(o.Printed.Value);
            var z = d.Str("z") != "" ? d.Str("z") : o.Z; var n = d.Str("number") != "" ? d.Str("number") : o.Number; var serial = o != null && !string.IsNullOrEmpty(o.Serial) ? o.Serial : conf.Str("serial");
            body = $"<printerFiscalReceipt><printRecMessage operator=\"1\" messageType=\"4\" message={Q($"VOID {int.Parse(z):0000} {int.Parse(n):0000} {day:ddMMyyyy} {serial}")} /></printerFiscalReceipt>";
        }
        else
        {
            var rows = new StringBuilder();
            foreach (var l in Lines(d))
            {
                rows.Append($"<printRecItem operator=\"1\" description={Q(Text(l.Str("desc"), 38))} quantity=\"{Int(l["qty"])}\" unitPrice=\"{Money(Int(l["cents"]))}\" department=\"{Int(l["dept"])}\" justification=\"1\" />");
                if (Int(l["off"]) != 0) rows.Append($"<printRecItemAdjustment operator=\"1\" adjustmentType=\"0\" description=\"{OffLabel(l)}\" amount=\"{Money(Int(l["off"]))}\" department=\"{Int(l["dept"])}\" justification=\"1\" />");
            }
            if (Int(d["discount"]) != 0) rows.Append($"<printRecSubtotalAdjustment operator=\"1\" adjustmentType=\"1\" description=\"SCONTO\" amount=\"{Money(Int(d["discount"]))}\" justification=\"1\" />");
            if (GiftCents(d) != 0) rows.Append($"<printRecSubtotalAdjustment operator=\"1\" adjustmentType=\"1\" description={Q(Text("GIFT CARD " + d["gift"].Str("code"), 38))} amount=\"{Money(GiftCents(d))}\" justification=\"1\" />");
            if (d.Str("cf") != "") rows.Append($"<printRecMessage operator=\"1\" messageType=\"7\" index=\"1\" message={Q(d.Str("cf"))} />");
            if (d.Str("lottery") != "") rows.Append($"<printRecLotteryID operator=\"1\" code={Q(d.Str("lottery"))} />");
            foreach (var (k, cents) in Payments(d)) rows.Append($"<printRecTotal operator=\"1\" description={Q(PayLabel(k).ToUpperInvariant())} payment=\"{Money(cents)}\" paymentType=\"{PayCode(conf, k)}\" index=\"1\" justification=\"1\" />");
            body = $"<printerFiscalReceipt><beginFiscalReceipt operator=\"1\" />{rows}<endFiscalReceipt operator=\"1\" /></printerFiscalReceipt>";
        }
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" + body + "</s:Body></s:Envelope>";
    }

    public sealed record Job(Guid Id, string Attempt, string Url = null, string Body = null, string Port = null, int Baud = 0, string Framing = null, List<string> Commands = null, int Wait = 0);
    public JsonObject Data(Sale s) => c.Decrypt(s.Pii, "sale:" + s.Id);
    public static string Attempt(Sale s) => Times.PyIso(s.Claimed.Value);
    /// <summary>The «Documento n.» as printed at the foot of the receipt: closure-number, four digits each (2039-0013).</summary>
    public static string Doc(Sale s) => int.TryParse(s.Z, out var z) && int.TryParse(s.Number, out var n) && s.Z.All(char.IsDigit) && s.Number.All(char.IsDigit) ? $"{z:0000}-{n:0000}" : s.Z != "" && s.Number != "" ? $"{s.Z}-{s.Number}" : s.Number;

    /// <summary>job(): what the register gets: a POST on the network, or the commands through the COM port.</summary>
    public Job JobOf(Sale sale, Location location)
    {
        var conf = Conf(location); var wait = sale.Kind == "close" ? CLOSE_WAIT : 0;
        if (conf.Str("device") == "epson") return new Job(sale.Id, Attempt(sale), Url(conf), Epson(sale, Data(sale), conf), Wait: wait);
        var cmds = Rch(sale, Data(sale), conf);
        if (conf.Str("connection") == "usb") return new Job(sale.Id, Attempt(sale), Port: conf.Str("port"), Baud: Int(conf["baud"], 9600), Framing: conf.Str("framing"), Commands: cmds, Wait: wait);
        return new Job(sale.Id, Attempt(sale), Url(conf), "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Service>" + string.Concat(cmds.Select(x => $"<cmd>{Esc(x)}</cmd>")) + "</Service>", Wait: wait);
    }

    /// <summary>read(): what the register answered: done (with number and z when it says them), busy, failed, or unknown when
    /// the answer cannot be read and the document may have been printed.</summary>
    public static (string State, Dictionary<string, string> Info) Read(string device, int status, string body)
    {
        (string, Dictionary<string, string>) Unknown(string text) => ("unknown", new() { ["error"] = text });
        XElement root;
        try { root = XDocument.Parse(body ?? "").Root; } catch (System.Xml.XmlException) { return Unknown($"Risposta della cassa non leggibile (HTTP {status}): guarda se lo scontrino è uscito."); }
        if (device == "rch")
        {
            var r = root.Element("Request");
            if (r == null) return Unknown("Risposta della cassa non leggibile: guarda se lo scontrino è uscito.");
            string F(string k) => (r.Element(k)?.Value is { Length: > 0 } v ? v : "0").Trim();
            if (F("busy") != "0") return ("busy", new());
            if (F("errorCode") == "0") return ("done", new());
            var why = new[] { ("paperEnd", "carta finita"), ("coverOpen", "coperchio aperto"), ("printerError", "stampante in errore") }.Where(x => F(x.Item1) != "0").Select(x => x.Item2).ToList();
            return ("failed", new() { ["error"] = $"La cassa risponde errore {F("errorCode")}" + (why.Count > 0 ? ": " + string.Join(", ", why) : "") + "." });
        }
        var res = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "response");
        if (res == null) return Unknown("Risposta della cassa non leggibile: guarda se lo scontrino è uscito.");
        var info = new Dictionary<string, string>(); foreach (var e in res.DescendantsAndSelf()) info[e.Name.LocalName] = e.HasElements ? "" : e.Value.Trim();
        if (res.Attribute("success")?.Value == "true") return ("done", new() { ["number"] = info.GetValueOrDefault("fiscalReceiptNumber", ""), ["z"] = info.GetValueOrDefault("zRepNumber", "") });
        return ("failed", new() { ["error"] = "La cassa risponde: " + (res.Attribute("code")?.Value is { Length: > 0 } code ? code : "errore") + (res.Attribute("status")?.Value is { Length: > 0 } st ? $" (stato {st})" : "") + "." });
    }

    /// <summary>deliver's result (Error unreachable|unknown|refused, Serial ok|nak|sent, or Status and Body).</summary>
    public sealed record Sent(string Error = null, string Detail = null, string Serial = null, int At = 0, int Status = 0, string Body = null);

    /// <summary>outcome(): deliver's result, from this server or from the bridge, as the state of the request.</summary>
    public static (string State, Dictionary<string, string> Info) Outcome(string device, Sent r)
    {
        (string, Dictionary<string, string>) E(string state, string text) => (state, new() { ["error"] = text });
        if (r.Error == "unknown") return E("unknown", "La cassa non ha risposto in tempo: guarda se lo scontrino è uscito prima di riprovare.");
        if (r.Error == "refused") { var t = string.IsNullOrEmpty(r.Detail) ? "Il ponte non ha inviato la richiesta." : r.Detail; return E("failed", t.Length > 200 ? t[..200] : t); }
        if (!string.IsNullOrEmpty(r.Error)) return E("failed", "La cassa non risponde: è accesa e collegata? Indirizzo o porta giusti?");
        if (r.Serial == "ok") return ("done", new());
        if (r.Serial == "nak") return E("failed", $"La cassa rifiuta il comando {r.At + 1}: controlla reparti e pagamenti.");
        if (!string.IsNullOrEmpty(r.Serial)) return E("unknown", "Inviato senza conferma della cassa: guarda se lo scontrino è uscito.");
        return Read(device, r.Status, r.Body ?? "");
    }

    // ---------- the queue
    public void Stale(Location location)
    {
        var late = c.S.Query<Guid>("SELECT id FROM core_sale WHERE location_id=@l AND status='sending' AND claimed<@t", new { l = location.Id, t = c.Now - STALE }).ToList();
        if (late.Count == 0) return;
        WpSync.Touch(c, "sale", late);  // the site's archive gets it too
        c.S.Execute("UPDATE core_sale SET status='unknown', error='Nessuna risposta dalla cassa: guarda se lo scontrino è uscito prima di riprovare.' WHERE id = ANY(@ids)", new { ids = late.ToArray() });
    }
    /// <summary>claim(): the oldest request waiting, now sending; none while another one is with the register (one at a time).</summary>
    public Sale Claim(Location location)
    {
        c.S.Execute("SELECT 1 FROM core_location WHERE id=@id FOR UPDATE", new { id = location.Id });
        Stale(location);
        if (c.S.Scalar<long>("SELECT count(*) FROM core_sale WHERE location_id=@l AND status='sending'", new { l = location.Id }) > 0) return null;
        var sale = c.S.One<Sale>("SELECT * FROM core_sale WHERE location_id=@l AND status='queued' ORDER BY created LIMIT 1", new { l = location.Id });
        if (sale != null) { sale.Status = "sending"; sale.Claimed = Micro(c.Now); c.S.Update(sale, "status", "claimed"); }
        return sale;
    }
    /// <summary>The database keeps microseconds: the same instant before and after a save.</summary>
    static DateTime Micro(DateTime t) => new(t.Ticks - t.Ticks % 10, DateTimeKind.Utc);

    /// <summary>finish(): the register's answer: emitted (with its number when it gives one), not emitted, busy (back in the
    /// queue) or to check. A late answer of an attempt already given up is dropped. Emitted: the booking completed, the
    /// copy mailed.</summary>
    public Sale Finish(Guid saleId, string state, Dictionary<string, string> info, string attemptId = null)
    {
        var sale = c.S.One<Sale>("SELECT * FROM core_sale WHERE id=@id FOR UPDATE", new { id = saleId });
        if (sale.Status is not ("sending" or "unknown") || !string.IsNullOrEmpty(attemptId) && sale.Claimed.HasValue && attemptId != Attempt(sale)) return sale;
        if (state == "busy") { sale.Status = "queued"; sale.Claimed = null; c.S.Update(sale, "status", "claimed"); return sale; }
        var location = c.S.Get<Location>(sale.LocationId); var conf = Conf(location);
        var was = sale.Status; sale.Status = state; var error = info.GetValueOrDefault("error", ""); sale.Error = error.Length > 200 ? error[..200] : error;
        if (state == "done")
        {
            string Cut(string v, int n) => (v ?? "").Length > n ? v[..n] : v ?? "";
            sale.Number = Cut(info.GetValueOrDefault("number"), 20); sale.Z = Cut(info.GetValueOrDefault("z"), 10); sale.Serial = conf.Str("serial"); sale.Printed = Micro(c.Now);
            if (sale.Kind == "sale" && sale.Number == "" && conf.Str("device") == "rch")
            {
                // RCH never says the number: the program counts. A «Da verificare» found printed keeps the place it had when it was sent
                if (was == "unknown" && sale.Claimed.HasValue) sale.Printed = sale.Claimed;
                (sale.Z, sale.Number) = Counted(sale);
            }
            if (sale.Kind == "close")
            {
                if (was == "unknown" && sale.Claimed.HasValue) sale.Printed = sale.Claimed;  // found printed later: it closed the day it was sent
                Closed(location.Id);
            }
        }
        c.S.Update(sale, "status", "error", "number", "z", "serial", "printed");
        var actor = c.Actor; c.Actor = null; c.Audit("sale." + state, sale.Id, new { kind = sale.Kind, number = sale.Number }); c.Actor = actor;
        var gift = new Gift(c);
        if (sale.Kind == "sale")
        {
            if (state == "failed") gift.GiveBack(sale);  // not emitted: the credit back on the card, taken again by Riprova
            if (state == "done") gift.Sold(sale, "done");
        }
        if (state == "done" && sale.Kind == "void" && sale.VoidsId.HasValue)
        {
            c.S.Execute("UPDATE core_sale SET status='void' WHERE id=@id", new { id = sale.VoidsId }); WpSync.Touch(c, "sale", new[] { sale.VoidsId.Value });
            var old = c.S.Get<Sale>(sale.VoidsId.Value);
            if (old != null)
            {
                gift.GiveBack(old); gift.Sold(old, "void");
                if (old.ContactId.HasValue) Record(old, $"Annullo dello scontrino n. {(old.Z != "" ? old.Z + "-" : "")}{old.Number}".Trim(), new[] { $"Annullato il {c.Cal.ToLocal(c.Now):dd/MM/yyyy HH:mm}" }, -1);
            }
        }
        if (state == "done" && sale.Kind == "sale" && sale.ContactId.HasValue)
        {
            var (head, rows) = CopyLines(sale, true);
            Record(sale, string.Join(" + ", Lines(Data(sale)).Select(l => l.Str("desc")).Distinct()), rows.Concat(head));
        }
        if (state == "done" && sale.Kind == "sale")
        {
            if (sale.BookingId.HasValue) Complete(c.S.Get<Booking>(sale.BookingId.Value));
            SendCopy(sale);
        }
        return sale;
    }
    /// <summary>The booking's work is done: it and the others of its visit completed, unless not started yet.</summary>
    void Complete(Booking b)
    {
        if (b == null) return;
        var bk = new Bookings(c);
        var rows = b.Visit != null ? c.S.Query<Booking>("SELECT * FROM core_booking WHERE visit=@v", new { v = b.Visit }).OrderBy(x => x.Id == b.Id ? 0 : 1).ToList() : new List<Booking> { b };
        foreach (var x in rows)
            if (x.Status is "confirmed" or "arrived" or "in_progress")
                try { bk.ChangeStatus(x, "completed"); } catch (BookingException) { }  // not started yet: the desk completes it later
    }

    /// <summary>counted(): the «chiusura-documento» pair of a document emitted on a register that does not say it (RCH): the
    /// closure it will belong to (the last closure's number plus one) and its number: the register numbers its documents
    /// one after the other, so the program counts all the receipts and annulli it emitted here, plus the documents the
    /// register made on its own keys (doc_offset).</summary>
    public (string Z, string Number) Counted(Sale sale)
    {
        var conf = Conf(c.S.Get<Location>(sale.LocationId));  // fresh: the one read before the register was called may be old
        var n = c.S.Scalar<long>(@"SELECT count(*) FROM core_sale WHERE location_id=@l AND kind IN ('sale','void') AND id<>@id
            AND ((status IN ('done','void') AND printed<@p) OR (status='unknown' AND claimed<@p))", new { l = sale.LocationId, id = sale.Id, p = sale.Printed });
        return (conf["last_z"] != null ? (Int(conf["last_z"]) + 1).ToString() : "", (n + 1 + Int(conf["doc_offset"])).ToString());
    }
    /// <summary>learn(): the desk typed the «Documento n.» as printed on a receipt: the program learns the closure number and
    /// how many documents the register made on its own keys, and renumbers the documents of that day and after.</summary>
    public void Learn(Guid locationId, Sale sale, string z, string number)
    {
        var location = c.S.Get<Location>(locationId); var conf = Conf(location);
        bool Digits(string v) => !string.IsNullOrEmpty(v) && v.All(char.IsDigit);
        if (conf.Str("device") != "rch" || !Digits(number) || !sale.Printed.HasValue) return;
        var delta = int.Parse(number) - int.Parse(Counted(sale).Number);
        if (Digits(z)) conf["last_z"] = int.Parse(z) - 1;
        conf["doc_offset"] = Int(conf["doc_offset"]) + delta; location.Till = Json.Dump(conf); c.S.Update(location, "till");
        var day = c.Cal.Local(c.Cal.DayOf(sale.Printed.Value), TimeSpan.Zero)!.Value;
        foreach (var s in c.S.Query<Sale>("SELECT * FROM core_sale WHERE location_id=@l AND kind IN ('sale','void') AND status IN ('done','void','unknown') AND printed>=@d AND id<>@id", new { l = locationId, d = day, id = sale.Id }).Append(sale).ToList())
        {
            if (s.Id == sale.Id) s.Number = int.Parse(number).ToString();
            else if (Digits(s.Number)) s.Number = (int.Parse(s.Number) + delta).ToString();
            if (Digits(z)) s.Z = int.Parse(z).ToString();
            c.S.Update(s, "number", "z");
        }
    }
    /// <summary>closed(): a daily closure done on an RCH: the next documents belong to the closure after.</summary>
    public void Closed(Guid locationId)
    {
        var location = c.S.Get<Location>(locationId); var conf = Conf(location);
        if (conf.Str("device") == "rch" && conf["last_z"] != null) { conf["last_z"] = Int(conf["last_z"]) + 1; location.Till = Json.Dump(conf); c.S.Update(location, "till"); }
    }
    /// <summary>shares(): {operator: cents} of a bill: each line net of its discount, the discount on the total spread over the lines.</summary>
    public static List<(string Who, int Cents)> Shares(JsonObject d, int total)
    {
        var nets = Lines(d).Select(l => (Who: l.Str("staff"), Cents: Int(l["qty"]) * Int(l["cents"]) - Int(l["off"]))).ToList(); long gross = nets.Sum(n => n.Cents); if (gross == 0) gross = 1;
        var outp = new List<(string Who, int Cents)>();
        foreach (var (who, cents) in nets)
        {
            var i = outp.FindIndex(x => x.Who == who); var part = (int)Math.Floor((decimal)cents * total / gross);
            if (i < 0) outp.Add((who, part)); else outp[i] = (who, outp[i].Cents + part);
        }
        if (outp.Count > 0) { var top = outp.FindIndex(x => x.Cents == outp.Max(o => o.Cents)); outp[top] = (outp[top].Who, outp[top].Cents + total - outp.Sum(o => o.Cents)); }  // the cents of the rounding
        return outp;
    }
    static JsonObject By(IEnumerable<(string Who, int Cents)> shares, int sign = 1) { var o = new JsonObject(); foreach (var (w, n) in shares) o[w] = sign * n; return o; }

    /// <summary>record(): an emitted receipt (or its annullo) with a client goes in the client's record: the day, what, by
    /// whom, lines and prices, and what it cost, in all and for each operator, for the reports.</summary>
    void Record(Sale sale, string what, IEnumerable<string> rows, int sign = 1)
    {
        var d = Data(sale); var lines = sale.Kind == "sale" ? Lines(d).ToList() : new();
        var staff = string.Join(", ", lines.Select(l => l.Str("staff")).Where(x => x != "").Distinct());
        if (staff == "" && sale.BookingId.HasValue && c.S.Get<Booking>(sale.BookingId.Value)?.ResourceId is Guid rid) staff = c.Row<Resource>(rid)?.Name ?? "";
        var day = c.Cal.DayOf(sale.Printed ?? c.Now); var total = Paid(sale);
        string Cut(string v, int n) => v.Length > n ? v[..n] : v;
        var actor = c.Actor; c.Actor = null;
        new Clients(c).SaveTreatment(c.S.Get<Contact>(sale.ContactId.Value), new JsonObject
        { ["date"] = day.ToString("yyyy-MM-dd"), ["service"] = Cut(what, 120) is { Length: > 0 } w ? w : "Scontrino", ["staff"] = Cut(staff, 100), ["details"] = Cut(string.Join("\n", rows), 4000), ["amount"] = sign * total, ["by"] = By(Shares(d, total), sign) }, null, sale.BookingId);
        c.Actor = actor;
    }
    /// <summary>paid(): the cents the client has paid: a «Da pagare» still open is not money in.</summary>
    public static int Paid(Sale sale) => (int)(sale.Total * 100) - (sale.Settled.HasValue ? 0 : (int)(sale.Due * 100));

    /// <summary>settle(): the client pays what he owed: the day and how, and in his record the payment, for the reports.</summary>
    public Sale Settle(Guid saleId, string how)
    {
        if (!SETTLE.Any(p => p.Id == how)) throw new BookingException("Scegli come ha pagato.");
        var sale = c.S.One<Sale>("SELECT * FROM core_sale WHERE id=@id FOR UPDATE", new { id = saleId });
        if (!(sale.Kind == "sale" && sale.Status == "done" && sale.Due > 0 && !sale.Settled.HasValue)) throw new BookingException("Questo scontrino non ha niente da pagare.");
        sale.Settled = c.Now; sale.SettledWith = how; c.S.Update(sale, "settled", "settled_with");
        c.Audit("sale.settled", sale.Id, new { due = Prices.Str(sale.Due), how });
        if (sale.ContactId.HasValue)
        {
            var d = Data(sale); var cents = (int)(sale.Due * 100);
            var staff = string.Join(", ", Lines(d).Select(l => l.Str("staff")).Where(x => x != "").Distinct()); if (staff.Length > 100) staff = staff[..100];
            var doc = sale.Number != "" ? $"n. {Doc(sale)} " : ""; var what = $"Pagamento dello scontrino {doc}del {c.Cal.ToLocal(sale.Printed.Value):dd/MM/yyyy}"; if (what.Length > 120) what = what[..120];
            new Clients(c).SaveTreatment(c.S.Get<Contact>(sale.ContactId.Value), new JsonObject
            { ["date"] = c.Cal.DayOf(sale.Settled.Value).ToString("yyyy-MM-dd"), ["service"] = what, ["staff"] = staff, ["details"] = $"Pagato {Prices.Euro((decimal?)sale.Due)} con {PayLabel(how).ToLowerInvariant()}", ["amount"] = cents, ["by"] = By(Shares(d, cents)), ["kind"] = "payment" }, null, sale.BookingId);
        }
        return sale;
    }

    /// <summary>Deliver: how a request reaches the register (TillLink.Deliver on the server; a recorder in the tests).</summary>
    public static Func<Job, Sent> Deliver = TillLink.Deliver;
    static readonly Dictionary<Guid, object> LINES = new();  // one sender a location at a time: the register takes one request at a time

    /// <summary>send_now(): after the request is committed, a unit of work each step, the register called in between.</summary>
    public static void SendNow(Db db, Crypto crypto, Guid tenantId, Guid locationId)
    {
        while (true)
        {
            Job job; string device; Guid id; string attempt;
            using (var x = new Ctx(db, crypto, tenantId))
            {
                var t = new Till(x); var location = x.S.Get<Location>(locationId); var sale = t.Claim(location);
                if (sale == null) { x.Commit(); return; }
                job = t.JobOf(sale, location); device = Conf(location).Str("device"); id = sale.Id; attempt = Attempt(sale); x.Commit();
            }
            (string State, Dictionary<string, string> Info) r = ("failed", new() { ["error"] = "La cassa è occupata: riprova tra poco." });
            for (int i = 0; i < 5; i++)
            {
                var got = Outcome(device, Deliver(job));
                if (got.State != "busy") { r = got; break; }
                Thread.Sleep(1000);
            }
            using (var x = new Ctx(db, crypto, tenantId)) { new Till(x).Finish(id, r.State, r.Info, attempt); x.Commit(); }
        }
    }
    /// <summary>kick(): the register is called after the page has answered, in a thread of its own.</summary>
    public static void Kick(Db db, Crypto crypto, Guid tenantId, Guid locationId)
    {
        object gate; lock (LINES) { if (!LINES.TryGetValue(locationId, out gate)) LINES[locationId] = gate = new object(); }
        Task.Run(() => { try { lock (gate) SendNow(db, crypto, tenantId, locationId); } catch (Exception ex) { Console.Error.WriteLine(ex); } });
    }
    void KickAfterCommit(Location location)
    {
        if (Conf(location).Str("link") != "direct" || c.Db == null) return;
        var (db, crypto, t, l) = (c.Db, c.Crypto, c.Tenant.Id, location.Id);
        c.AfterCommit.Add(() => Kick(db, crypto, t, l));
    }
    public Sale Enqueue(Location location, string kind = "sale", JsonObject data = null, string key = null, Guid? booking = null, Guid? contact = null, decimal total = 0, decimal due = 0, Guid? voids = null)
    {
        var sale = new Sale { TenantId = c.Tenant.Id, LocationId = location.Id, Kind = kind, Key = string.IsNullOrEmpty(key) ? Bookings.TokenUrlSafe(24) : key, BookingId = booking, ContactId = contact, Total = total, Due = due, VoidsId = voids, Actor = c.Actor?.ToString() ?? "", Created = c.Now };
        sale.Pii = c.Encrypt(data ?? new JsonObject(), "sale:" + sale.Id); c.S.Insert(sale);
        KickAfterCommit(location);
        return sale;
    }
    public void Retry(Sale sale, Location location)
    {
        if (sale.Kind == "sale") new Gift(c).Reserve(sale);
        sale.Status = "queued"; sale.Error = ""; sale.Claimed = null; c.S.Update(sale, "status", "error", "claimed");
        KickAfterCommit(location); c.Audit("sale.retry", sale.Id);
    }

    // ---------- the copy for the client
    /// <summary>copy_lines(): the lines of the copy for the client; who=true, for the client's record, with who did each line.</summary>
    public (List<string> Head, List<string> Rows) CopyLines(Sale sale, bool who = false)
    {
        var d = Data(sale); var rows = new List<string>();
        foreach (var l in Lines(d))
        {
            rows.Add($"{Int(l["qty"])} x {l.Str("desc")}  {Euro(Int(l["qty"]) * Int(l["cents"]))}" + (who && l.Str("staff") != "" ? $" · {l.Str("staff")}" : ""));
            if (Int(l["off"]) != 0) { var label = OffLabel(l); rows.Add($"{char.ToUpper(label[0]) + label[1..].ToLowerInvariant()}  -{Euro(Int(l["off"]))}"); }
        }
        if (Int(d["discount"]) != 0) rows.Add($"Sconto  -{Euro(Int(d["discount"]))}");
        if (GiftCents(d) != 0) rows.Add($"Gift card {d["gift"].Str("code")}  -{Euro(GiftCents(d))}");
        rows.Add($"Totale  {Prices.Euro((decimal?)sale.Total)}");
        rows.AddRange(Payments(d).Select(p => $"{PayLabel(p.Kind)}  {Euro(p.Cents)}"));
        var head = new[] { sale.Number != "" ? $"Documento commerciale n. {Doc(sale)}" : "Documento commerciale", sale.Printed.HasValue ? $"del {c.Cal.ToLocal(sale.Printed.Value):dd/MM/yyyy HH:mm}" : "", sale.Serial != "" ? $"Registratore telematico {sale.Serial}" : "" };
        return (head.Where(x => x != "").ToList(), rows);
    }
    /// <summary>send_copy(): only when the client asked for it at the desk, to the email of his card.</summary>
    public Delivery SendCopy(Sale sale)
    {
        var d = Data(sale);
        if (!Json.Truthy(d["copy"]) || d.Str("email") == "" || !sale.ContactId.HasValue) return null;
        var (head, rows) = CopyLines(sale);
        var body = string.Join("\n", new[] { "Ecco la copia del tuo scontrino.", "" }.Concat(head).Append("").Concat(rows).Concat(new[] { "", "Questa è una copia di cortesia: il documento commerciale valido è quello emesso dal registratore telematico." }));
        c.S.Execute("UPDATE core_sale SET mailed=@n WHERE id=@id", new { n = c.Now, id = sale.Id });
        return new Bookings(c).Queue(new JsonObject { ["to"] = d.Str("email"), ["subject"] = $"Copia del documento commerciale · {c.Tenant.Name}", ["body"] = body, ["html"] = Texts.EmailHtml(c.Tenant, body, new()) }, sale.ContactId);
    }

    /// <summary>unclosed(): when the last receipt was emitted here with no daily closure after it, before today.</summary>
    DateTime? Unclosed(Location loc)
    {
        var today = c.Cal.Local(c.Today, TimeSpan.Zero)!.Value;
        var last = c.S.One<DateTime?>("SELECT printed FROM core_sale WHERE location_id=@l AND kind='close' AND status='done' AND printed IS NOT NULL ORDER BY printed DESC LIMIT 1", new { l = loc.Id });
        return c.S.One<DateTime?>("SELECT printed FROM core_sale WHERE location_id=@l AND kind='sale' AND status IN ('done','void') AND printed<@t" + (last.HasValue ? " AND printed>@p" : "") + " ORDER BY printed DESC LIMIT 1", new { l = loc.Id, t = today, p = last });
    }
    /// <summary>missing_close(): the last day with receipts emitted here and no daily closure after them, before today.</summary>
    public DateOnly? MissingClose(Location loc) => Unclosed(loc) is DateTime s ? c.Cal.DayOf(s) : null;
    /// <summary>unsure_close(): the closure sent after those receipts and still «Da verificare» (the register did not answer
    /// in time): the reminder asks whether it came out, instead of asking for another one every day.</summary>
    public Sale UnsureClose(Location loc) => Unclosed(loc) is DateTime s
        ? c.S.One<Sale>("SELECT * FROM core_sale WHERE location_id=@l AND kind='close' AND status='unknown' AND created>@s ORDER BY created DESC LIMIT 1", new { l = loc.Id, s }) : null;
    /// <summary>places(): the locations with a register.</summary>
    public List<Location> Places() => c.S.Query<Location>("SELECT * FROM core_location WHERE active ORDER BY name").Where(l => Conf(l).Str("device") != "").ToList();
}

/// <summary>scripts/ponte_cassa.py deliver: a request of the program to the register, network or USB.</summary>
public static class TillLink
{
    const int TIMEOUT = 20;
    // the register is on the shop's network: never through a proxy
    static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };  // each send has its own wait

    public static Till.Sent Deliver(Till.Job job) => job.Port != null ? SerialSend(job) : HttpSend(job.Url, job.Body, job.Wait);

    // ---------- where the register is
    const string EPSON_STATUS = "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><printerCommand><queryPrinterStatus operator=\"1\" statusType=\"1\" /></printerCommand></s:Body></s:Envelope>";
    /// <summary>The hosts of the /24 of each private IPv4 address of this PC. shortcut: a /24 each, the shop networks seen
    /// so far; ask for the address by hand on a bigger one</summary>
    public static List<string> Hosts()
    {
        var mine = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && Till.Private(a) && !IPAddress.IsLoopback(a));
        return mine.Select(a => { var b = a.GetAddressBytes(); return $"{b[0]}.{b[1]}.{b[2]}."; }).Distinct().OrderBy(x => x, StringComparer.Ordinal).SelectMany(n => Enumerable.Range(1, 254).Select(i => n + i)).ToList();
    }
    /// <summary>What answers at host:port: "rch" (its web service answers an empty request with its status, nothing printed
    /// or cleared), "epson" (the status of fpmate) or null.</summary>
    public static async Task<string> Identify(string host, int port = 80, double wait = 0.4)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(wait));
            await tcp.ConnectAsync(host, port, stop.Token);
        }
        catch (Exception) { return null; }
        async Task<string> Post(string path, string body)
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var r = await Http.PostAsync($"http://{host}:{port}{path}", new StringContent(body, Encoding.UTF8, "text/xml"), stop.Token);
                return await r.Content.ReadAsStringAsync(stop.Token);
            }
            catch (Exception) { return ""; }
        }
        var answer = await Post("/service.cgi", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Service></Service>");
        if (answer.Contains("<Service") && answer.Contains("<errorCode")) return "rch";
        return (await Post("/cgi-bin/fpmate.cgi?devid=local_printer&timeout=3000", EPSON_STATUS)).Contains("success=\"") ? "epson" : null;
    }
    /// <summary>The registers answering on the shop's network, and the COM ports of this PC.</summary>
    public static (List<(string Device, string Address)> Found, List<string> Ports) Discover(List<string> hosts = null, int port = 80)
    {
        hosts ??= Hosts();
        using var gate = new SemaphoreSlim(64);
        var kinds = Task.WhenAll(hosts.Select(async h => { await gate.WaitAsync(); try { return await Identify(h, port); } finally { gate.Release(); } })).GetAwaiter().GetResult();
        var found = hosts.Zip(kinds).Where(x => x.Second != null).Select(x => (x.Second, port == 80 ? x.First : $"{x.First}:{port}")).ToList();
        return (found, Ports());
    }
    public static List<string> Ports() { try { return System.IO.Ports.SerialPort.GetPortNames().OrderBy(x => x, StringComparer.Ordinal).ToList(); } catch (Exception) { return new(); } }

    static bool Lan(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "http" && IPAddress.TryParse(u.Host, out var ip) && Till.Private(ip);

    /// <summary>http_send(): unreachable when nothing reached the register; unknown when it got the request and did not
    /// answer: it may have printed.</summary>
    public static Till.Sent HttpSend(string url, string body, int wait = 0)
    {
        if (!Lan(url)) return new Till.Sent(Error: "refused", Detail: "Indirizzo della cassa fuori dalla rete del negozio.");
        try
        {
            using var content = new StringContent(body, Encoding.UTF8); content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(wait > 0 ? wait : TIMEOUT));
            using var res = Http.Send(new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, stop.Token);
            using var reader = new StreamReader(res.Content.ReadAsStream(), Encoding.UTF8);
            var buf = new char[1 << 20]; var n = reader.ReadBlock(buf, 0, buf.Length);
            return new Till.Sent(Status: (int)res.StatusCode, Body: new string(buf, 0, n));
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException) { return new Till.Sent(Error: "unreachable"); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { return new Till.Sent(Error: "unknown"); }
    }

    /// <summary>frame(): STX, address "01", length on 3 digits, "N", the command, checksum (sum of the bytes from the address
    /// on, mod 256, 2 hex digits), ETX; or the command and a carriage return.</summary>
    public static byte[] Frame(string command, string framing)
    {
        var data = Encoding.ASCII.GetBytes(command);
        if (framing == "cr") return data.Append((byte)'\r').ToArray();
        var body = Encoding.ASCII.GetBytes("01" + data.Length.ToString("000") + "N").Concat(data).ToArray();
        return new byte[] { 2 }.Concat(body).Concat(Encoding.ASCII.GetBytes((body.Sum(b => b) % 256).ToString("X2"))).Append((byte)3).ToArray();
    }

    /// <summary>serial_send(): RCH via USB (a virtual COM port): one frame a command, ACK before the next, NAK sent again up
    /// to 3 times.</summary>
    public static Till.Sent SerialSend(Till.Job job)
    {
        var framing = string.IsNullOrEmpty(job.Framing) ? "stx" : job.Framing;
        System.IO.Ports.SerialPort port;
        try
        {
            port = new System.IO.Ports.SerialPort(job.Port, job.Baud == 0 ? 9600 : job.Baud) { ReadTimeout = job.Wait > 0 ? job.Wait * 1000 : 3000, WriteTimeout = 3000, Handshake = framing == "cr" ? System.IO.Ports.Handshake.XOnXOff : System.IO.Ports.Handshake.None };
            port.Open();
        }
        catch (Exception) { return new Till.Sent(Error: "unreachable"); }
        using (port)
        {
            for (int i = 0; i < job.Commands.Count; i++)
            {
                var acked = false;
                for (int k = 0; k < 3 && !acked; k++)
                {
                    var frame = Frame(job.Commands[i], framing);
                    try { port.Write(frame, 0, frame.Length); } catch (Exception) { return new Till.Sent(Error: i == 0 ? "unreachable" : "unknown"); }
                    if (framing == "cr") { acked = true; break; }
                    int answer; try { answer = port.ReadByte(); } catch (Exception) { answer = -1; }
                    if (answer == 6) acked = true;
                    else if (answer != 0x15) return new Till.Sent(Error: i == 0 ? "unreachable" : "unknown");  // silence: the last ones may have printed
                }
                if (!acked) return new Till.Sent(Serial: "nak", At: i);
            }
        }
        return new Till.Sent(Serial: framing == "cr" ? "sent" : "ok");
    }
}

/// <summary>core/gift.py: gift cards. One is sold at the Cassa as a line of the bill and is active once the register has
/// emitted that receipt. A bill paid with it prints a receipt with the credit taken off: what is left is paid as usual.
/// The credit is taken when the bill goes to the register and given back if the register does not emit it, or on its annullo.</summary>
public sealed class Gift(Ctx c)
{
    const string ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";  // no 0/O, 1/I: read aloud at the desk
    public static readonly int[] PRESETS = { 25, 50, 100, 150 };
    public const int MONTHS = 12;
    public static string Clean(string code) { var s = Regex.Replace((code ?? "").ToUpperInvariant(), "[^A-Z0-9]", ""); return s.Length > 12 ? s[..12] : s; }
    public string NewCode()
    {
        while (true)
        {
            var code = new string(Enumerable.Range(0, 8).Select(_ => ALPHABET[System.Security.Cryptography.RandomNumberGenerator.GetInt32(ALPHABET.Length)]).ToArray());
            if (c.S.Scalar<long>("SELECT count(*) FROM core_giftcard WHERE code=@c", new { c = code }) == 0) return code;
        }
    }
    /// <summary>proposed(): today and MONTHS months, on the last day of that month when it is shorter.</summary>
    public static DateOnly Proposed(DateOnly today) => today.AddMonths(MONTHS);
    public static int Cents(Giftcard card) => (int)(card.Balance * 100);
    /// <summary>usable(): the card of a code, if it can pay now: active, not expired, with credit.</summary>
    public Giftcard Usable(string code)
    {
        var card = c.S.One<Giftcard>("SELECT * FROM core_giftcard WHERE code=@c FOR UPDATE", new { c = Clean(code) }) ?? throw new BookingException("Gift card non trovata: controlla il codice.");
        if (card.Status == "pending") throw new BookingException("Questa gift card non è ancora attiva: lo scontrino della sua vendita non risulta emesso.");
        if (card.Status == "void") throw new BookingException("Questa gift card è annullata.");
        if (card.Expires.HasValue && DateOnly.FromDateTime(card.Expires.Value) < c.Today) throw new BookingException($"Questa gift card è scaduta il {card.Expires:dd/MM/yyyy}.");
        if (card.Balance <= 0) throw new BookingException("Questa gift card non ha più credito.");
        return card;
    }
    int Taken(Sale sale) => -(int)c.S.Scalar<long>("SELECT coalesce(sum(cents),0) FROM core_giftmove WHERE sale_id=@s", new { s = sale.Id });
    JsonNode Of(Sale sale) => c.Decrypt(sale.Pii, "sale:" + sale.Id)["gift"];
    /// <summary>reserve(): the credit of the bill taken off its card (once: a retry after a give-back takes it again).</summary>
    public void Reserve(Sale sale)
    {
        var g = Of(sale); if (g == null) return; var cents = Till.Int(g["cents"]);
        if (Taken(sale) >= cents) return;
        var card = c.S.One<Giftcard>("SELECT * FROM core_giftcard WHERE id=@id FOR UPDATE", new { id = Guid.Parse(g.Str("id")) });
        if (card.Status != "active" || card.Expires.HasValue && DateOnly.FromDateTime(card.Expires.Value) < c.Today || Cents(card) < cents)
            throw new BookingException($"La gift card {card.Code} non ha più {Till.Euro(cents)} di credito: rifai il conto.");
        c.S.Insert(new Giftmove { TenantId = c.Tenant.Id, CardId = card.Id, SaleId = sale.Id, Cents = -cents, Created = c.Now });
        card.Balance -= cents / 100m; c.S.Update(card, "balance");
    }
    /// <summary>give_back(): a receipt not emitted, or voided: its credit goes back on the card.</summary>
    public void GiveBack(Sale sale)
    {
        var n = Taken(sale); if (n <= 0) return;
        var card = Guid.TryParse(Of(sale).Str("id"), out var id) ? c.S.One<Giftcard>("SELECT * FROM core_giftcard WHERE id=@id FOR UPDATE", new { id }) : null;
        if (card == null) return;
        c.S.Insert(new Giftmove { TenantId = c.Tenant.Id, CardId = card.Id, SaleId = sale.Id, Cents = n, Created = c.Now });
        card.Balance += n / 100m; c.S.Update(card, "balance");
    }
    /// <summary>sold(): the cards sold with this receipt: active when it is emitted, void with its annullo.</summary>
    public void Sold(Sale sale, string state) =>
        c.S.Execute("UPDATE core_giftcard SET status=@to WHERE sale_id=@s AND status=@from", new { s = sale.Id, from = state == "done" ? "pending" : "active", to = state == "done" ? "active" : "void" });
    public string Holder(Giftcard card, bool namesOk)
    {
        if (!card.ContactId.HasValue) return "";
        if (!namesOk) return "Cliente";
        var d = new Clients(c).Data(c.S.Get<Contact>(card.ContactId.Value)); return string.Join(" ", new[] { d.Str("first_name"), d.Str("last_name") }.Where(x => x != ""));
    }
    /// <summary>of(): the cards of a client, not the voided ones, newest first.</summary>
    public List<Giftcard> Of(Guid contact) => c.S.Query<Giftcard>("SELECT * FROM core_giftcard WHERE contact_id=@c AND status<>'void' ORDER BY created DESC", new { c = contact }).ToList();
}
