using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>Cassa (cassa.till_view): the bill of a booking or of who buys without one, sent to the location's register;
/// the Parcheggio, the «Da pagare», the documents of today with their copy and annullo, the daily closure.</summary>
public static class Cassa
{
    static readonly Dictionary<string, string> KINDS = new() { ["resource"] = "Appuntamento / risorsa", ["capacity"] = "Capienza", ["event"] = "Evento / tour", ["shift"] = "Ristorante / pizzeria a turni", ["day"] = "Soggiorno / noleggio" };
    static readonly Dictionary<string, string> STATUS = new() { ["queued"] = "In coda", ["sending"] = "In corso", ["done"] = "Emesso", ["failed"] = "Non emesso", ["unknown"] = "Da verificare", ["void"] = "Annullato" };
    static readonly Dictionary<string, string> SALE_KIND = new() { ["sale"] = "Scontrino", ["void"] = "Annullo", ["probe"] = "Prova collegamento", ["test"] = "Stampa di prova", ["close"] = "Chiusura giornaliera" };

    static (Till Till, List<Location> Where, Location Loc) Place(Call c)
    {
        var t = new Till(c.C); var where = t.Places();
        return (t, where, where.FirstOrDefault(l => l.Id == c.Id("sede")) ?? where.FirstOrDefault());
    }
    static Booking VisibleBooking(Call c, Guid id)
    {
        var (vis, vargs) = Permissions.VisibleBookings(c.Member, c.Perms, "b"); var p = new DynamicParameters(vargs); p.Add("id", id);
        return c.C.S.One<Booking>($"SELECT b.* FROM core_booking b WHERE b.id=@id AND ({vis})", p) ?? throw new KeyNotFoundException();
    }
    static string Name(Call c, Guid? contact, bool namesOk, string fallback = "")
    {
        if (!contact.HasValue) return "";
        if (!namesOk) return fallback;
        var d = new Clients(c.C).Data(c.C.S.Get<Contact>(contact.Value)); return string.Join(" ", new[] { d.Str("first_name"), d.Str("last_name") }.Where(x => x != ""));
    }
    static string Dec(decimal? v) => v.HasValue ? Prices.Str(v.Value).Replace(".", ",") : "";
    static object StandbyRows(Call c, Till t, Location loc, bool namesOk) =>
        c.C.S.Query<Standby>("SELECT * FROM core_standby WHERE location_id=@l ORDER BY created", new { l = loc.Id }).ToList().Select(x =>
        {
            var d = t.StandbyData(x);
            return new { id = x.Id, totale = Prices.Str(x.Total), nota = d.Str("note"), chi = Name(c, x.ContactId, namesOk), da = new Bookings(c.C).Iso(x.Created), righe = (d["draft"]?["lines"] as JsonArray)?.Select(l => new { desc = l.Str("desc"), staff = l.Str("staff") }) };
        }).ToList();

    [Rpc("cassa", Perm = "sale.write")]
    public static object Page(Call c)
    {
        var x = c.C; var (t, where, loc) = Place(c); var bk = new Bookings(x); var cl = new Clients(x);
        if (loc == null) return new { nessuna = true };
        var conf = Till.Conf(loc); var namesOk = c.Can("pii.read"); var today = x.Today; var start = x.Cal.Local(today, TimeSpan.Zero)!.Value;
        var booking = c.Id("prenotazione") is Guid bid ? VisibleBooking(c, bid) : null;
        t.Stale(loc);
        // services, variants and products as tiles; "name" is what the register prints (32 characters, no accents)
        var items = new List<(string Group, string Label, string Name, string Price, string Svc)>();
        foreach (var s in x.S.Query<Service>("SELECT * FROM core_service WHERE location_id=@l AND active ORDER BY kind, name", new { l = loc.Id }).ToList())
        {
            var opts = x.S.Query<Option>("SELECT * FROM core_option WHERE service_id=@s AND active ORDER BY name", new { s = s.Id }).ToList(); var group = KINDS.GetValueOrDefault(s.Kind, "");
            if (opts.Count > 0) items.AddRange(opts.Select(o => (group, $"{s.Name} {o.Name}", Till.Text($"{s.Name} {o.Name}"), Dec(o.Price ?? s.Price), s.Id.ToString())));
            else items.Add((group, s.Name, Till.Text(s.Name), Dec(s.Price), s.Id.ToString()));
        }
        items.AddRange(x.S.Query<Product>("SELECT * FROM core_product WHERE active ORDER BY name").Select(p => ("Prodotti", p.Name, Till.Text(p.Name), Dec(p.Price), "p:" + p.Id)));
        var rows = x.S.Query<Sale>("SELECT * FROM core_sale WHERE location_id=@l AND kind IN ('sale','void','close') AND created>=@d ORDER BY created DESC", new { l = loc.Id, d = start }).ToList();
        var due = x.S.Query<Sale>("SELECT * FROM core_sale WHERE location_id=@l AND kind='sale' AND status='done' AND due>0 AND settled IS NULL ORDER BY created", new { l = loc.Id }).ToList();
        var lines = booking != null ? t.Prefill(booking) : new List<JsonObject>();
        // suspended bills and a quote taken into this bill; their client when the booking has none
        var ids = (c["sospeso"] as JsonArray)?.Select(v => Bookings.AsId(v)).Where(v => v.HasValue).Select(v => v.Value).ToArray() ?? Array.Empty<Guid>();
        var taken = x.S.Query<Parked>("SELECT * FROM core_parked WHERE id = ANY(@ids) AND location_id=@l ORDER BY created", new { ids, l = loc.Id }).ToList(); var off = 0;
        foreach (var p in taken)
        {
            var d = t.ParkedData(p); off += Till.Int(d["discount"]);
            foreach (var l in (d["lines"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                lines.Add(new JsonObject { ["desc"] = l.Str("desc"), ["qty"] = Till.Int(l["qty"]), ["price"] = Till.Money(Till.Int(l["cents"])), ["svc"] = l.Str("svc"), ["staff"] = l.Str("staff"), ["off"] = Till.Int(l["off"]) != 0 ? l.Str("pct") != "" ? l.Str("pct") + "%" : Till.Money(Till.Int(l["off"])) : "" });
        }
        var contact = booking?.ContactId is Guid bc ? x.S.Get<Contact>(bc) : taken.FirstOrDefault(p => p.ContactId.HasValue) is Parked tp ? x.S.Get<Contact>(tp.ContactId.Value) : null;
        var quote = c.Id("preventivo") is Guid qid ? x.S.Get<Quote>(qid) : null;
        if (quote != null)
        {
            foreach (var l in (x.Decrypt(quote.Pii, "quote:" + quote.Id)["lines"] as JsonArray)?.OfType<JsonObject>().Where(l => Till.Int(l["cents"]) != 0) ?? Enumerable.Empty<JsonObject>())
                lines.Add(new JsonObject { ["desc"] = Till.Text(l.Str("desc")), ["qty"] = Till.Int(l["qty"]), ["price"] = Till.Money(Till.Int(l["cents"])), ["svc"] = l.Str("ref").StartsWith("s:") ? l.Str("ref")[2..] : l.Str("ref").StartsWith("p:") ? l.Str("ref") : "", ["staff"] = "" });
            contact ??= x.Row<Contact>(quote.ContactId);
        }
        // from the Gift card page: the card as a row of the bill, its client
        if (c.Str("gift") != "")
        {
            if (decimal.TryParse(c.Str("gift").Replace(",", "."), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 9999)
                lines.Add(new JsonObject { ["desc"] = "GIFT CARD", ["qty"] = 1, ["price"] = Till.Money((int)(v * 100)), ["svc"] = "gift:" + (DateOnly.TryParseExact(c.Str("scade"), "yyyy-MM-dd", out var exp) ? exp.ToString("yyyy-MM-dd") : ""), ["staff"] = "" });
            if (contact == null && c.Id("cliente") is Guid gc) contact = x.S.Get<Contact>(gc);
        }
        else if (contact == null && c.Id("cliente") is Guid pc && c.Can("contacts.read")) contact = x.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = pc });
        var who = booking != null && namesOk ? bk.Data(booking) : contact != null && namesOk ? cl.Data(contact) : null;
        var ops = x.S.Query<Resource>("SELECT * FROM core_resource WHERE location_id=@l AND active ORDER BY name", new { l = loc.Id }).ToList();
        var ag = new Agenda(x, new Viewer(c.Member, c.Perms)); var tone = Agenda.Colors(ag.Operators(loc));
        var parked = x.S.Query<Parked>("SELECT * FROM core_parked WHERE location_id=@l ORDER BY created DESC", new { l = loc.Id }).ToList(); var takenIds = taken.Select(p => p.Id).ToHashSet();
        object ParkedRow(Parked p)
        {
            var d = t.ParkedData(p);
            return new { id = p.Id, totale = Prices.Str(p.Total), nota = d.Str("note"), chi = p.ContactId.HasValue ? Name(c, p.ContactId, namesOk, "Cliente") : "", cliente = p.ContactId, prenotazione = p.BookingId, creato = bk.Iso(p.Created),
                righe = (d["lines"] as JsonArray)?.Select(l => new { desc = l.Str("desc"), qty = Till.Int(l["qty"]), staff = l.Str("staff") }) };
        }
        var sb = StandbyRows(c, t, loc, namesOk);
        return new
        {
            sedi = where.Select(l => new { id = l.Id, nome = l.Name }), sede = loc.Id, sedeNome = loc.Name,
            mancaChiusura = t.MissingClose(loc), puoChiudere = t.CanClose(conf), puoAnnullare = t.CanVoid(conf), contata = conf.Str("device") == "rch",
            prenotazione = booking == null ? null : new { id = booking.Id, inizio = bk.Iso(booking.Start), cliente = booking.ContactId, haScontrino = x.S.Scalar<long>("SELECT count(*) FROM core_sale WHERE booking_id=@b", new { b = booking.Id }) > 0 },
            chi = who == null ? null : new { nome = who.Str("first_name"), cognome = who.Str("last_name") },
            scegliCliente = c.Can("contacts.read") && namesOk, cliente = contact != null && namesOk ? contact.Id : (Guid?)null, cf = contact != null && namesOk ? Clients.ReceiptCode(cl.Data(contact)) : "",
            preventivo = quote == null ? null : $"{quote.Year}-{quote.Number:0000}",
            gruppi = items.GroupBy(i => i.Group).Select(g => new { nome = g.Key, voci = g.Select(i => new { etichetta = i.Label, nome = i.Name, prezzo = i.Price, svc = i.Svc }) }),
            operatori = ops.Select(o => new { nome = o.Name, colore = tone.GetValueOrDefault(o.Id) }), operatore = booking?.ResourceId is Guid rid ? x.Row<Resource>(rid)?.Name ?? "" : "",
            righe = lines, presi = taken.Select(p => p.Id), sconto = off != 0 ? Till.Money(off) : "", chiave = Bookings.TokenUrlSafe(24),
            pagamenti = Till.PAYMENTS.Select(p => new { id = p.Id, nome = p.Label }), saldi = Till.SETTLE.Select(p => new { id = p.Id, nome = p.Label }),
            giftImporti = Gift.PRESETS, giftScade = Gift.Proposed(today),
            gift = contact == null ? Enumerable.Empty<object>() : new Gift(x).Of(contact.Id).Where(g => g.Status == "active" && g.Balance > 0 && !(g.Expires.HasValue && DateOnly.FromDateTime(g.Expires.Value) < today)).Select(g => (object)new { codice = g.Code, credito = Prices.Str(g.Balance) }),
            standby = sb, parcheggio = ((System.Collections.ICollection)sb).Count + due.Count,
            daPagare = due.Select(s => new { id = s.Id, importo = Prices.Str(s.Due), totale = Prices.Str(s.Total), chi = s.ContactId.HasValue ? Name(c, s.ContactId, namesOk, "Cliente") : "Cliente", cliente = s.ContactId, stampato = s.Printed.HasValue ? bk.Iso(s.Printed.Value) : null, numero = s.Number, documento = Till.Doc(s) }),
            sospesi = parked.Select(ParkedRow), inAttesa = parked.Where(p => contact != null && p.ContactId == contact.Id && !takenIds.Contains(p.Id)).Select(p => p.Id),
            scontrini = rows.Select((s, i) => new
            {
                id = s.Id, tipo = s.Kind, tipoNome = SALE_KIND.GetValueOrDefault(s.Kind, s.Kind), stato = s.Status, statoNome = STATUS.GetValueOrDefault(s.Status, s.Status), totale = Prices.Str(s.Total), creato = bk.Iso(s.Created), errore = s.Error,
                numero = s.Number, documento = Till.Doc(s), matricola = s.Serial, email = s.Mailed.HasValue, chi = Name(c, s.ContactId, namesOk), cliente = s.ContactId, prenotazione = s.BookingId,
                daPagare = Prices.Str(s.Due), pagato = s.Settled.HasValue ? bk.Iso(s.Settled.Value) : null, giorno = s.Printed.HasValue ? x.Cal.DayOf(s.Printed.Value) : (DateOnly?)null,
                annullato = s.VoidsId.HasValue && x.S.Get<Sale>(s.VoidsId.Value) is Sale old ? Till.Doc(old) : "", copia = Json.Truthy(t.Data(s)["copy"]),
                dettaglio = i < 5 && s.Kind == "sale" ? t.CopyLines(s, true).Rows : null,
            }),
            occupata = rows.Any(s => s.Status is "queued" or "sending"), oggi = today, inizioMese = new DateOnly(today.Year, today.Month, 1),
        };
    }

    /// <summary>Every action of the page; returns the line to show and, for a bill taken back from the Parcheggio, its draft.</summary>
    [Rpc("cassa.azione", Perm = "sale.write")]
    public static object Action(Call c)
    {
        var x = c.C; var (t, _, loc) = Place(c); var a = c.Str("azione");
        if (loc == null) throw new KeyNotFoundException();
        var conf = Till.Conf(loc); var booking = c.Id("booking") is Guid bid ? VisibleBooking(c, bid) : null;
        Standby Parked() => c.Id("standby_id") is Guid sid ? x.S.One<Standby>("SELECT * FROM core_standby WHERE id=@id AND location_id=@l FOR UPDATE", new { id = sid, l = loc.Id }) : null;
        switch (a)
        {
            case "sell" or "park":
                x.Lock();
                var done = t.Sell(loc, booking, c.Args, a, c.Can("contacts.read"));
                return new { testo = done == "" ? "Questo conto è già stato inviato." : done };
            case "standby":
                var d = Till.DraftOf(c["draft"]) ?? throw new BookingException("Nel conto non c'è niente da parcheggiare.");
                var sb = t.ToStandby(loc, d, c.Str("standby_note"), c.Str("contact") is var ct && ct != "" ? ct : d["fields"].Str("contact"), d.Str("booking") is var db && db != "" ? db : booking?.Id.ToString());
                return new { testo = $"Conto parcheggiato{(sb.Total != 0 ? ": " + Prices.Euro((decimal?)sb.Total) : "")}. Lo riprendi dal Parcheggio." };
            case "resume":
                var st = Parked() ?? throw new BookingException("Questo conto non è più nel Parcheggio: forse l'ha già ripreso un'altra postazione.");
                var swap = Till.DraftOf(c["swap"]);  // the bill open here goes to the Parcheggio, it is not lost
                if (swap != null) t.ToStandby(loc, swap, "", swap["fields"].Str("contact"), swap.Str("booking"));
                var draft = t.StandbyData(st)["draft"]?.DeepClone() ?? new JsonObject(); x.Audit("sale.standby_resumed", st.Id); x.S.Delete<Standby>(st.Id);
                return new { testo = "Conto ripreso dal Parcheggio." + (swap != null ? " Quello che c'era in cassa è andato nel Parcheggio." : ""), conto = draft };
            case "drop_standby":
                var ds = Parked() ?? throw new KeyNotFoundException();
                x.Audit("sale.standby_dropped", ds.Id, new { total = Prices.Str(ds.Total) }); x.S.Delete<Standby>(ds.Id);
                return new { testo = "Conto tolto dal Parcheggio." };
            case "close":
                if (conf.Str("device") == "rch" && Till.RchCmd(conf, "rch_close") == "") throw new BookingException("Su questa cassa la chiusura si fa dalla cassa.");
                t.Enqueue(loc, "close"); x.Audit("sale.close", loc.Id);
                return new { testo = "Chiusura giornaliera inviata alla cassa." };
            case "drop_parked":
                var p = c.Id("parked_id") is Guid pid ? x.S.One<Parked>("SELECT * FROM core_parked WHERE id=@id AND location_id=@l", new { id = pid, l = loc.Id }) : null; if (p == null) throw new KeyNotFoundException();
                x.Audit("sale.parked_dropped", p.Id, new { total = Prices.Str(p.Total) }); t.DropParked(p);
                return new { testo = $"Tolto: {Prices.Euro((decimal?)p.Total)}. Il trattamento resta nella scheda del cliente." };
            case "closed_by_hand":
                // the closure done on the register: only noted, so the reminder goes away
                var cs = new Sale { TenantId = x.Tenant.Id, LocationId = loc.Id, Kind = "close", Status = "done", Printed = x.Now, Error = "Fatta dalla cassa", Key = Bookings.TokenUrlSafe(24), Actor = c.User.Id.ToString(), Created = x.Now };
                cs.Pii = x.Encrypt(new JsonObject(), "sale:" + cs.Id); x.S.Insert(cs); t.Closed(loc.Id); x.Audit("sale.close_by_hand", loc.Id);
                return new { testo = "Segnata la chiusura fatta dalla cassa." };
        }
        if (a == "void" && c.Id("sale") == null) return new { testo = t.Void(loc, null, c.Str("doc"), c.Str("day")) };
        var sale = c.Id("sale") is Guid id ? x.S.One<Sale>("SELECT * FROM core_sale WHERE id=@id AND location_id=@l", new { id, l = loc.Id }) : null; if (sale == null) throw new KeyNotFoundException();
        var text = "";
        if (a == "retry" && sale.Status is "failed" or "unknown") t.Retry(sale, loc);  // unknown: the operator has looked, nothing came out of the register
        else if (a == "printed" && sale.Status == "unknown")
        {
            var m = Till.ParseDoc(c.Str("number"));  // «2039-0013» as printed, or just the number
            t.Finish(sale.Id, "done", new() { ["number"] = m?.Number ?? "", ["z"] = m?.Z ?? "" });
            if (m != null) t.Learn(loc.Id, x.S.Get<Sale>(sale.Id), m.Value.Z, m.Value.Number);
            x.Audit("sale.printed_by_hand", sale.Id);
        }
        else if (a == "void" && sale.Kind == "sale" && sale.Status == "done") text = t.Void(loc, sale, c.Str("doc"), c.Str("day"));
        else if (a == "settle") { t.Settle(sale.Id, c.Str("how")); text = $"Segnato pagato: {Prices.Euro((decimal?)sale.Due)}."; }
        else if (a == "mail" && sale.Status is "done" or "void") text = t.SendCopy(sale) != null ? "Copia inviata per email." : "Il cliente non ha chiesto la copia per email.";
        return new { testo = text };
    }

    /// <summary>cassa_copy.html: the copy of a document, to read to the client or print.</summary>
    [Rpc("cassa.copia", Perm = "sale.write")]
    public static object Copy(Call c)
    {
        var (t, _, loc) = Place(c); var sale = c.C.S.One<Sale>("SELECT * FROM core_sale WHERE id=@id AND location_id=@l", new { id = c.NeedId("id"), l = loc?.Id }) ?? throw new KeyNotFoundException();
        var (head, rows) = t.CopyLines(sale);
        return new { testa = head, righe = rows, stato = sale.Status, tipo = sale.Kind };
    }

    /// <summary>gift.lookup: the Cassa asks for a code: its credit, or why it cannot pay.</summary>
    [Rpc("giftcard.cerca", Perm = "sale.write")]
    public static object GiftLookup(Call c)
    {
        var g = new Gift(c.C);
        try { var card = g.Usable(c.Str("code")); return new { codice = card.Code, credito = Prices.Str(card.Balance).Replace(".", ","), chi = g.Holder(card, c.Can("pii.read")), scade = card.Expires?.ToString("dd/MM/yyyy") ?? "" }; }
        catch (BookingException ex) { return new { errore = ex.Message }; }
    }

    /// <summary>cassa.standby_page: every bill in the Parcheggio of every register.</summary>
    [Rpc("standby", Perm = "sale.write")]
    public static object StandbyPage(Call c)
    {
        var t = new Till(c.C); var where = t.Places(); var namesOk = c.Can("pii.read");
        return new { gruppi = where.Select(l => new { sede = l.Id, nome = l.Name, righe = StandbyRows(c, t, l, namesOk) }).ToList(), piuSedi = where.Count > 1, oggi = c.C.Today };
    }
}
