using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/cassa.py, the bill: its rows in cents, sent to the register or left out of the receipt («Non nello
/// scontrino», Preventivo pagamenti cassa), the Parcheggio (stand-by), the annullo.
/// The form: {righe: [{desc, qty, price, svc, off, staff, park}], discount, pay, pay_cash…, cf, lottery, gift_code,
/// gift_use, copy, contact, parked: [ids], park_note, key}.</summary>
public sealed partial class Till
{
    static readonly string[] ROW = { "desc", "qty", "price", "svc", "off", "staff" };

    /// <summary>cents(): "12,50" → 1250; ValueError as BookingException.</summary>
    public static int Cents(string value)
    {
        var v = (value ?? "").Replace(",", ".").Trim(); if (v == "") v = "0";
        if (!decimal.TryParse(v, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x) || x < 0 || x > 99999.99m) throw new BookingException("Importo non valido.");
        return (int)Math.Round(x * 100, 0, MidpointRounding.AwayFromZero);
    }
    /// <summary>row_off(): the discount of a row: «100%» or «5,00» (euro); in cents, with the percent for the label.</summary>
    public static (int Cents, string Pct) RowOff(string value, int full, string desc)
    {
        var v = Regex.Replace(value ?? "", @"\s", "");
        if (v == "") return (0, "");
        if (v.EndsWith('%'))
        {
            if (!decimal.TryParse(v[..^1].Replace(",", "."), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var p) || !(p > 0 && p <= 100) || p != Math.Round(p, 2))
                throw new BookingException($"Sconto di «{desc}»: una percentuale da 0,01 a 100.");
            return ((int)Math.Round(full * p / 100, 0, MidpointRounding.AwayFromZero), Prices.Normal(p).Replace(".", ","));
        }
        var o = Cents(v);
        if (o > full) throw new BookingException($"Lo sconto di «{desc}» supera il prezzo della riga.");
        return (o, "");
    }
    static List<JsonObject> Rows(JsonObject post) => (post["righe"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
    public static bool HasRows(JsonObject post) => Rows(post).Any(r => r.Str("desc").Trim() != "" || r.Str("price").Trim() != "");

    /// <summary>bill(): the lines, discount, payments, codice fiscale and lotteria of the form, in cents; the department from
    /// the service. credit: the cents of a gift card that may pay it: what it pays is taken off the total.</summary>
    public static (JsonObject Data, int Total) Bill(JsonObject post, JsonObject conf, int credit = 0)
    {
        var deps = conf["departments"] as JsonObject ?? new JsonObject(); var def = Int(conf["default_dept"]) is var dd && dd != 0 ? dd : 1; var lines = new JsonArray();
        foreach (var r in Rows(post))
        {
            var desc = Text(r.Str("desc")); var price = r.Str("price"); var svc = r.Str("svc");
            if (desc == "" && price.Trim() == "") continue;  // a row left empty
            if (!int.TryParse(r.Str("qty").Trim(), out var qty)) qty = 0;
            if (desc == "" || qty is < 1 or > 99) throw new BookingException("Ogni riga vuole una descrizione e una quantità da 1 a 99.");
            var cents = Cents(price);
            if (cents == 0) throw new BookingException($"Manca il prezzo di «{desc}».");
            // a product (p:id) or a gift card sold (gift:expiry) on their own department
            var dept = svc.StartsWith("p:") ? Int(conf["product_dept"]) : svc.StartsWith("gift:") ? Int(conf["gift_dept"]) : Int(deps[svc]?["dept"]);
            var line = new JsonObject { ["desc"] = desc, ["qty"] = qty, ["cents"] = cents, ["dept"] = dept != 0 ? dept : def };
            if (svc != "") line["svc"] = svc.Length > 44 ? svc[..44] : svc;  // which service, for the tiles lit in the Cassa
            var who = Text(r.Str("staff")); if (who != "") line["staff"] = who;  // who did it, for the client's record only: never on the receipt
            var (o, pct) = RowOff(r.Str("off"), qty * cents, desc);
            if (o != 0) { line["off"] = o; line["pct"] = pct; }
            if (svc.StartsWith("gift:"))
            {
                if (qty != 1 || o != 0) throw new BookingException("Una gift card per riga, senza sconto di riga.");
                if (cents < 100) throw new BookingException("Una gift card vale almeno 1,00 €.");
                if (svc.Length > 5 && !DateOnly.TryParseExact(svc[5..], "yyyy-MM-dd", out _)) throw new BookingException("Scadenza della gift card non valida.");
            }
            lines.Add(line);
        }
        if (lines.Count == 0) throw new BookingException("Aggiungi almeno una riga al conto.");
        if (lines.Count > 60) throw new BookingException("Al massimo 60 righe per conto.");
        var gross = lines.Sum(l => Int(l["qty"]) * Int(l["cents"]) - Int(l["off"]));
        if (gross <= 0) throw new BookingException("Con gli sconti il conto è a zero: togli uno sconto o una riga.");
        var discount = post.Str("discount");
        var off = discount.Trim().EndsWith('%') ? RowOff(discount, gross, "Sconto sul totale").Cents : Cents(discount);  // in euro or with the %
        if (off >= gross) throw new BookingException("Lo sconto non può superare il conto.");
        var total = gross - off; var use = Math.Min(credit, total); total -= use;
        if (use != 0 && lines.Any(l => l.Str("svc").StartsWith("gift:"))) throw new BookingException("Una gift card non si paga con un'altra gift card.");
        var split = PAYMENTS.Select(p => (p.Id, Cents: Cents(post.Str("pay_" + p.Id)))).Where(x => x.Cents != 0).ToList();
        if (split.Count > 0 && split.Sum(x => x.Cents) != total) throw new BookingException($"I pagamenti fanno {Euro(split.Sum(x => x.Cents))}, il conto {Euro(total)}.");
        if (split.Count == 0) split.Add((PAYMENTS.Any(p => p.Id == post.Str("pay")) && total != 0 ? post.Str("pay") : "cash", total));
        var cf = Regex.Replace(post.Str("cf"), @"\s", "").ToUpperInvariant(); var lottery = Regex.Replace(post.Str("lottery"), @"\s", "").ToUpperInvariant();
        if (cf != "" && !CF.IsMatch(cf)) throw new BookingException("Codice fiscale non valido: 16 caratteri, o le 11 cifre della partita IVA.");
        if (lottery != "" && !LOTTERY.IsMatch(lottery)) throw new BookingException("Codice lotteria non valido: 8 caratteri tra lettere e numeri.");
        var d = new JsonObject { ["lines"] = lines, ["discount"] = off, ["payments"] = new JsonArray(split.Select(x => (JsonNode)new JsonArray(x.Id, x.Cents)).ToArray()), ["cf"] = cf, ["lottery"] = lottery };
        if (use != 0) d["gift"] = new JsonObject { ["cents"] = use };
        return (d, total);
    }

    /// <summary>prefill(): the booking's service or variant at the price written on it, with the others of its visit.</summary>
    public List<JsonObject> Prefill(Booking booking)
    {
        var rows = new List<Booking> { booking };
        if (booking.Visit != null) rows.AddRange(c.S.Query<Booking>("SELECT * FROM core_booking WHERE visit=@v AND id<>@id AND status<>'cancelled' ORDER BY start", new { v = booking.Visit, id = booking.Id }));
        return rows.Select(b => new JsonObject
        {
            ["desc"] = Text(string.Join(" ", new[] { b.Snap.Str("service") is var s && s != "" ? s : c.Row<Service>(b.ServiceId).Name, b.Snap.Str("option") }.Where(x => x != ""))), ["qty"] = 1,
            ["price"] = b.Snap.Str("total").Replace(".", ","), ["svc"] = b.ServiceId.ToString(), ["staff"] = b.ResourceId.HasValue ? c.Row<Resource>(b.ResourceId)?.Name ?? "" : "",
        }).ToList();
    }

    /// <summary>split(): the rows marked «Non nello scontrino» (park) apart from the others, each as a form of its own.</summary>
    static (JsonObject Keep, JsonObject Park) Split(JsonObject post)
    {
        var keep = (JsonObject)post.DeepClone(); var park = (JsonObject)post.DeepClone();
        bool Parked(JsonObject r) => Json.Truthy(r["park"]) && r.Str("park") != "0";
        keep["righe"] = new JsonArray(Rows(post).Where(r => !Parked(r)).Select(r => r.DeepClone()).ToArray());
        park["righe"] = new JsonArray(Rows(post).Where(Parked).Select(r => r.DeepClone()).ToArray());
        foreach (var (k, _) in PAYMENTS) park.Remove("pay_" + k);
        park.Remove("discount"); park.Remove("pay");
        return (keep, park);
    }
    public JsonObject ParkedData(Parked x) => c.Decrypt(x.Pii, "parked:" + x.Id);
    /// <summary>settled(): a bill «Non nello scontrino» taken into a receipt: its treatment in the client's record loses the mark.</summary>
    void Settled(Parked x)
    {
        if (!Guid.TryParse(ParkedData(x).Str("treatment"), out var id)) return;
        var tr = c.S.Get<Treatment>(id); if (tr == null) return;
        var info = c.Decrypt(tr.Pii, "treatment:" + tr.Id); info["kind"] = "parked_paid"; tr.Pii = c.Encrypt(info, "treatment:" + tr.Id); c.S.Update(tr, "pii");
    }
    /// <summary>A bill «Non nello scontrino» thrown away: the treatment stays in the client's record, without the mark.</summary>
    public void DropParked(Parked x) { Settled(x); c.S.Delete<Parked>(x.Id); }
    static string Note(string note, int n) { var s = string.Join(" ", (note ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); return s.Length > n ? s[..n] : s; }

    /// <summary>park(): lines out of the receipt, on the client's card: Preventivo pagamenti cassa. The booking's work is
    /// done: completed. Without a client and without a booking nothing is kept (null).</summary>
    public Parked Park(Location loc, Contact contact, Booking booking, JsonObject post, string key, string note = "")
    {
        var (d, total) = Bill(post, Conf(loc));
        if (Lines(d).Any(l => l.Str("svc").StartsWith("gift:"))) throw new BookingException("La gift card si vende con lo scontrino: non può restare fuori.");
        if (contact == null && booking == null) { c.Audit("sale.parked", null, new { total = Prices.Str(total / 100m), unrecorded = true }); return null; }
        var x = new Parked { TenantId = c.Tenant.Id, LocationId = loc.Id, ContactId = contact?.Id, BookingId = booking?.Id, Total = total / 100m, Key = key, Actor = c.Actor?.ToString() ?? "", Created = c.Now };
        var data = new JsonObject { ["lines"] = d["lines"].DeepClone(), ["discount"] = d["discount"].DeepClone(), ["note"] = Note(note, 120) };
        x.Pii = c.Encrypt(data, "parked:" + x.Id); c.S.Insert(x);
        c.Audit("sale.parked", x.Id, new { total = Prices.Str(x.Total) });
        if (contact != null)
        {
            // the treatment is done: in the record and the reports today, with a mark until it is in a receipt
            var rows = Lines(d).Select(l => (Int(l["qty"]) > 1 ? $"{Int(l["qty"])} x " : "") + l.Str("desc") + (l.Str("staff") != "" ? $" ({l.Str("staff")})" : "") + $": {Euro(Int(l["qty"]) * Int(l["cents"]) - Int(l["off"]))}").ToList();
            if (!string.IsNullOrEmpty(note)) rows.Add(Note(note, 120));
            string Cut(string v, int n) => v.Length > n ? v[..n] : v;
            var tr = new Clients(c).SaveTreatment(contact, new JsonObject
            {
                ["date"] = c.Today.ToString("yyyy-MM-dd"), ["service"] = Cut(string.Join(" + ", Lines(d).Select(l => l.Str("desc")).Distinct()), 120), ["staff"] = Cut(string.Join(", ", Lines(d).Select(l => l.Str("staff")).Where(s => s != "").Distinct()), 100),
                ["details"] = Cut(string.Join("\n", rows), 4000), ["amount"] = total, ["by"] = By(Shares(d, total)), ["kind"] = "parked",
            }, null, booking?.Id);
            data["treatment"] = tr.Id.ToString(); x.Pii = c.Encrypt(data, "parked:" + x.Id); c.S.Update(x, "pii");
        }
        Complete(booking);
        return x;
    }

    /// <summary>sell(): Paga in cassa: the receipt of the rows not «Non nello scontrino», the others in Preventivo pagamenti
    /// cassa; park: the whole bill there. The suspended ones taken back into this bill are gone from there. Returns the
    /// line to show ("" when the bill had already been sent).</summary>
    public string Sell(Location loc, Booking booking, JsonObject post, string action, bool contactsOk)
    {
        var key = post.Str("key");
        if (!Regex.IsMatch(key, "^[A-Za-z0-9_-]{20,64}$")) throw new BookingException("Ricarica la pagina e riprova.");
        if (c.S.Scalar<long>("SELECT (SELECT count(*) FROM core_sale WHERE key=@k)+(SELECT count(*) FROM core_parked WHERE key=@k)", new { k = key }) > 0) return "";
        var contact = booking?.ContactId is Guid bc ? c.S.Get<Contact>(bc) : null;
        if (post.Str("contact") != "" && contactsOk) contact = (Guid.TryParse(post.Str("contact"), out var cid) ? c.S.Get<Contact>(cid) : null) ?? throw new KeyNotFoundException();
        var ids = (post["parked"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => Guid.TryParse(x, out _)).Select(Guid.Parse).ToArray() ?? Array.Empty<Guid>();
        var taken = c.S.Query<Parked>("SELECT * FROM core_parked WHERE id = ANY(@ids) AND location_id=@l FOR UPDATE", new { ids, l = loc.Id }).ToList();
        if (contact == null && taken.FirstOrDefault(x => x.ContactId.HasValue) is Parked tp) contact = c.S.Get<Contact>(tp.ContactId.Value);
        var (keep, later) = Split(post);
        if (action == "park") { keep = null; later = (JsonObject)post.DeepClone(); later.Remove("pay"); }
        var note = post.Str("park_note");
        void DropTaken() { if (taken.Count > 0) c.S.Execute("DELETE FROM core_parked WHERE id = ANY(@ids)", new { ids = taken.Select(x => x.Id).ToArray() }); }
        if (keep == null || !HasRows(keep))
        {
            if (!HasRows(later)) throw new BookingException("Aggiungi almeno una riga al conto.");
            if (Gift.Clean(post.Str("gift_code")) != "") throw new BookingException("La gift card si usa sullo scontrino: togli il codice per lasciare tutto il conto fuori dallo scontrino.");
            foreach (var (k, _) in PAYMENTS) later.Remove("pay_" + k);
            var x = Park(loc, contact, booking, later, key, note);
            DropTaken();
            if (x == null) return $"Non nello scontrino: {Euro(Bill(later, Conf(loc)).Total)}. Senza cliente non resta segnato da nessuna parte.";
            return $"Non nello scontrino: {Prices.Euro((decimal?)x.Total)}, segnato tra i trattamenti del cliente." + (contact != null ? "" : " Resta sulla prenotazione.");
        }
        var gift = new Gift(c); Giftcard card = null; var credit = 0;
        var code = Gift.Clean(post.Str("gift_code"));
        if (code != "")
        {
            card = gift.Usable(code); credit = Gift.Cents(card);
            var want = post.Str("gift_use").Trim(); if (want != "") credit = Math.Min(credit, Cents(want));
        }
        var (d, total) = Bill(keep, Conf(loc), credit);
        if (card != null && d["gift"] is JsonObject g) { g["id"] = card.Id.ToString(); g["code"] = card.Code; }
        var sold = Lines(d).Where(l => l.Str("svc").StartsWith("gift:")).ToList(); var codes = sold.Select(_ => gift.NewCode()).ToList();
        for (int i = 0; i < sold.Count; i++) sold[i]["desc"] = $"GIFT CARD {codes[i]}";
        var due = Payments(d).Where(p => p.Kind == "due").Sum(p => p.Cents);
        if (due != 0 && contact == null) throw new BookingException("Con «Da pagare» scegli il cliente: così resta segnato chi deve pagare.");
        if (Json.Truthy(post["copy"]))
        {
            var email = contact != null ? new Clients(c).Data(contact).Str("email") : "";
            if (email == "") throw new BookingException("Per la copia per email scegli un cliente con l'email nella scheda.");
            d["copy"] = true; d["email"] = email;
        }
        var parkedX = HasRows(later) ? Park(loc, contact, booking, later, key + "p", note) : null;
        foreach (var x in taken) Settled(x);
        var sale = Enqueue(loc, "sale", d, key, booking?.Id, contact?.Id, total / 100m, due / 100m);
        gift.Reserve(sale);
        for (int i = 0; i < sold.Count; i++)
        {
            var exp = sold[i].Str("svc")[5..];
            c.S.Insert(new Giftcard { TenantId = c.Tenant.Id, Code = codes[i], ContactId = contact?.Id, SaleId = sale.Id, Amount = Int(sold[i]["cents"]) / 100m, Balance = Int(sold[i]["cents"]) / 100m, Status = "pending", Created = c.Now,
                Expires = exp != "" ? DateOnly.ParseExact(exp, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue) : null });
        }
        DropTaken();
        if (Json.Truthy(d["copy"]))
            c.S.Insert(new Consentevent { TenantId = c.Tenant.Id, ContactId = contact.Id, Kind = "confirmed", Purpose = "receipt_copy", Channel = "desk", TextHash = Privacy.TextHash(COPY_TEXT), Ref = sale.Id.ToString(), Actor = c.Actor?.ToString() ?? "", At = c.Now });
        c.Audit("sale.sent", sale.Id, new { total = Prices.Str(sale.Total), booking = booking?.Id.ToString() ?? "" });
        return "Conto inviato alla cassa." + (parkedX != null ? $" Non nello scontrino: {Prices.Euro((decimal?)parkedX.Total)}, tra i trattamenti del cliente." : "");
    }

    // ---------- Parcheggio, the bills in stand-by
    /// <summary>draft_of(): the bill as the Cassa had it (rows, fields, booking, operator pressed), cleaned; null when there is nothing.</summary>
    public static JsonObject DraftOf(JsonNode raw)
    {
        if (raw is not JsonObject src || src["lines"] is not JsonArray lines || raw.ToJsonString().Length > 64 * 1024) return null;
        string Cut(string v, int n) => v.Length > n ? v[..n] : v;
        var d = new JsonObject();
        d["lines"] = new JsonArray(lines.OfType<JsonObject>().Where(l => l.Str("desc").Trim() != "" || l.Str("price").Trim() != "").Take(60)
            .Select(l => { var o = new JsonObject(); foreach (var (k, v) in l) o[k] = Cut(v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? (b ? "1" : "") : v?.ToString() ?? "", 200); return (JsonNode)o; }).ToArray());
        var fields = new JsonObject();
        if (src["fields"] is JsonObject f) foreach (var (k, v) in f) fields[Cut(k, 40)] = v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : Cut(v?.ToString() ?? "", 200);
        d["fields"] = fields; d["booking"] = Cut(src.Str("booking"), 36);
        d["parked"] = new JsonArray((src["parked"] as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).Take(20).Select(x => (JsonNode)Cut(x, 36)).ToArray() ?? Array.Empty<JsonNode>());
        d["active"] = Cut(src.Str("active"), 100);
        return ((JsonArray)d["lines"]).Count > 0 ? d : null;
    }
    /// <summary>draft_total(): what the bill comes to, as far as it can be said: a row without its price counts nothing.</summary>
    public static decimal DraftTotal(JsonObject d, JsonObject conf)
    {
        var lines = Lines(d).ToList();
        var q = new JsonObject { ["righe"] = new JsonArray(lines.Where(l => l.Str("park") != "1").Select(l => l.DeepClone()).ToArray()), ["discount"] = d["fields"].Str("till-discount") is var a && a != "" ? a : d["fields"].Str("discount") };
        try { return Bill(q, conf).Total / 100m; }
        catch (BookingException)
        {
            var tot = 0m;
            foreach (var l in lines) try { tot += Cents(l.Str("price")) * Math.Max(1, int.TryParse(l.Str("qty"), out var n) ? n : 1) / 100m; } catch (BookingException) { }
            return tot;
        }
    }
    /// <summary>standby(): a bill to the Parcheggio: not a receipt, not done; the client and the booking are kept as links.</summary>
    public Standby ToStandby(Location loc, JsonObject d, string note, string contactId = null, string bookingId = null)
    {
        var contact = Guid.TryParse(contactId, out var cid) ? c.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = cid }) : null;
        var booking = Guid.TryParse(bookingId, out var bid) ? c.S.Get<Booking>(bid) : null;
        var x = new Standby { TenantId = c.Tenant.Id, LocationId = loc.Id, ContactId = contact?.Id, BookingId = booking?.Id, Total = DraftTotal(d, Conf(loc)), Actor = c.Actor?.ToString() ?? "", Created = c.Now };
        x.Pii = c.Encrypt(new JsonObject { ["draft"] = d.DeepClone(), ["note"] = Note(note, 80) }, "standby:" + x.Id); c.S.Insert(x);
        c.Audit("sale.standby", x.Id, new { total = Prices.Str(x.Total) });
        return x;
    }
    public JsonObject StandbyData(Standby x) => c.Decrypt(x.Pii, "standby:" + x.Id);

    static readonly Regex DOC = new(@"^\s*(?:(\d{1,4})\s*-)?\s*(\d{1,4})\s*$");  // «2026-0012» or just «12» (RCH, no closure number)
    public static (string Z, string Number)? ParseDoc(string text) { var m = DOC.Match(text ?? ""); return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null; }
    public bool CanVoid(JsonObject conf) => conf.Str("device") == "epson" || RchCmd(conf, "rch_void") != "";
    public bool CanClose(JsonObject conf) => conf.Str("device") == "epson" || RchCmd(conf, "rch_close") != "";

    /// <summary>void(): the annullo of a documento commerciale: of one in the list, or of any other written by hand (day and
    /// «Documento n.»). On RCH with no annullo command the desk does it on the register and it is only marked here.</summary>
    public string Void(Location loc, Sale sale, string docText, string dayText)
    {
        var conf = Conf(loc); var device = conf.Str("device");
        var m = ParseDoc(!string.IsNullOrEmpty(docText) ? docText : sale != null ? Doc(sale) : "");
        DateOnly? day = !string.IsNullOrEmpty(dayText) ? DateOnly.TryParseExact(dayText, "yyyy-MM-dd", out var dv) ? dv : null : sale?.Printed is DateTime p ? c.Cal.DayOf(p) : null;
        if (device == "rch" && RchCmd(conf, "rch_void") == "")
        {
            if (sale == null) throw new BookingException("Su questa cassa l'annullo si fa dalla cassa.");
            sale.Status = "void"; c.S.Update(sale, "status"); c.Audit("sale.void", sale.Id);
            var gift = new Gift(c); gift.GiveBack(sale); gift.Sold(sale, "void");
            return "Segnato come annullato.";
        }
        // on RCH the number is the one read at the foot of the receipt, typed every time: the program's count is only a guide
        if (device == "rch" && string.IsNullOrWhiteSpace(docText)) throw new BookingException("Scrivi il numero stampato in fondo allo scontrino, per esempio 2039-0013: la cassa annulla quello.");
        if (m == null || day == null || day > c.Today) throw new BookingException("Scrivi la data e il «Documento n.» dello scontrino, come sono stampati in fondo: per esempio 2026-0012.");
        if (device == "epson" && !(sale != null && sale.Serial != "" || conf.Str("serial") != "")) throw new BookingException("Manca la matricola della cassa: scrivila in Registratore di cassa.");
        if (device == "epson" && !(m.Value.Z != "" || sale != null && sale.Z != "")) throw new BookingException("Su Epson serve anche il numero della chiusura, come stampato in fondo: per esempio 2026-0012.");
        if (sale != null && c.S.Scalar<long>("SELECT count(*) FROM core_sale WHERE voids_id=@id AND status<>'failed'", new { id = sale.Id }) > 0) throw new BookingException("L'annullo è già stato inviato.");
        string Num(string v) => !string.IsNullOrEmpty(v) && v.All(char.IsDigit) ? int.Parse(v).ToString() : v ?? "";
        if (sale != null && !string.IsNullOrEmpty(docText) && (Num(m.Value.Z), Num(m.Value.Number)) != (Num(sale.Z), Num(sale.Number))) Learn(loc.Id, sale, m.Value.Z, m.Value.Number);  // the number corrected by hand teaches the count
        var v = Enqueue(loc, "void", new JsonObject { ["z"] = m.Value.Z, ["number"] = m.Value.Number, ["day"] = day.Value.ToString("yyyy-MM-dd") }, total: sale?.Total ?? 0, voids: sale?.Id);
        c.Audit("sale.void", sale?.Id ?? v.Id);
        return "Annullo inviato alla cassa.";
    }
}
