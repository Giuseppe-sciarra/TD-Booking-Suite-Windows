using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>core/quotes.py: Preventivi. A quote for a client: services, products and free lines with their price, notes
/// and validity; printed with the business's header, opened in Cassa when the client accepts. A free line can be saved
/// among the products.</summary>
public static class Preventivi
{
    const int VALID_DAYS = 30;
    static void Rights(Call c) => c.Require("pii.read");
    static string Ref(Quote q) => $"{q.Year}-{q.Number:0000}";
    static JsonObject Data(Call c, Quote q) => c.C.Decrypt(q.Pii, "quote:" + q.Id);
    static string Who(JsonObject d) => string.Join(" ", new[] { d.Str("first_name"), d.Str("last_name") }.Where(x => x != ""));
    static string Words(string v, int n) { var s = string.Join(" ", (v ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); return s.Length > n ? s[..n] : s; }

    [Rpc("preventivi.elenco", Perm = "booking.write")]
    public static object List(Call c)
    {
        Rights(c); var x = c.C; var q = Crypto.CaseFold(Words(c.Str("q"), 200));
        var rows = x.S.Query<Quote>("SELECT * FROM core_quote ORDER BY year DESC, number DESC LIMIT 300").Select(r => (Row: r, Who: Who(Data(c, r)))).Where(r => q == "" || Crypto.CaseFold(r.Who).Contains(q) || Ref(r.Row).Contains(q));
        return new { righe = rows.Select(r => new { id = r.Row.Id, numero = Ref(r.Row), chi = r.Who, totale = Prices.Str(r.Row.Total), creato = x.Cal.DayOf(r.Row.Created), valido = r.Row.ValidUntil.HasValue ? DateOnly.FromDateTime(r.Row.ValidUntil.Value) : (DateOnly?)null, cliente = r.Row.ContactId }).ToList() };
    }

    /// <summary>catalog(): services (and their variants) and products, to add a line with a tap; the same name once.</summary>
    static IEnumerable<object> Catalog(Call c)
    {
        var x = c.C; var seen = new HashSet<(string, string)>();
        foreach (var s in x.S.Query<Service>("SELECT * FROM core_service WHERE active ORDER BY name").ToList())
        {
            var opts = x.S.Query<Option>("SELECT * FROM core_option WHERE service_id=@s AND active ORDER BY name", new { s = s.Id }).ToList();
            var rows = opts.Count > 0 ? opts.Select(o => ($"{s.Name} {o.Name}", o.Price ?? s.Price)).ToList() : new List<(string, decimal?)> { (s.Name, s.Price) };
            foreach (var (name, price) in rows) if (seen.Add(("Servizi", name))) yield return new { gruppo = "Servizi", nome = name, prezzo = price.HasValue ? Prices.Str(price.Value).Replace(".", ",") : "", rif = "s:" + s.Id };
        }
        foreach (var p in x.S.Query<Product>("SELECT * FROM core_product WHERE active ORDER BY name"))
            if (seen.Add(("Prodotti", p.Name))) yield return new { gruppo = "Prodotti", nome = p.Name, prezzo = p.Price.HasValue ? Prices.Str(p.Price.Value).Replace(".", ",") : "", rif = "p:" + p.Id };
    }

    [Rpc("preventivo.dati", Perm = "booking.write")]
    public static object Form(Call c)
    {
        Rights(c); var x = c.C; var quote = c.Id("id") is Guid id ? x.S.Get<Quote>(id) ?? throw new KeyNotFoundException() : null;
        var d = quote != null ? Data(c, quote) : new JsonObject();
        var contact = quote?.ContactId is Guid qc ? x.S.Get<Contact>(qc) : c.Id("cliente") is Guid cid ? x.S.Get<Contact>(cid) : null;
        if (contact != null && quote == null) { var cd = new Clients(x).Data(contact); foreach (var k in new[] { "first_name", "last_name", "phone", "email" }) d[k] = cd.Str(k); }
        return new
        {
            id = quote?.Id, numero = quote != null ? Ref(quote) : "", nome = d.Str("first_name"), cognome = d.Str("last_name"), telefono = d.Str("phone"), email = d.Str("email"), note = d.Str("notes"), cliente = contact?.Id,
            righe = (d["lines"] as JsonArray)?.Select(l => new { desc = l.Str("desc"), qty = Till.Int(l["qty"]), price = Prices.Str(Till.Int(l["cents"]) / 100m).Replace(".", ","), rif = l.Str("ref") }),
            listino = Catalog(c).ToList(), valido = quote != null ? quote.ValidUntil.HasValue ? DateOnly.FromDateTime(quote.ValidUntil.Value) : (DateOnly?)null : x.Today.AddDays(VALID_DAYS),
        };
    }

    [Rpc("preventivo.salva", Perm = "booking.write")]
    public static object Save(Call c)
    {
        Rights(c); var x = c.C; var quote = c.Id("id") is Guid id ? x.S.Get<Quote>(id) ?? throw new KeyNotFoundException() : null; var today = x.Today;
        var d = new JsonObject { ["first_name"] = Words(c.Str("first_name"), 80), ["last_name"] = Words(c.Str("last_name"), 80), ["phone"] = Words(c.Str("phone"), 40), ["email"] = Cut(c.Str("email").Trim(), 160), ["notes"] = Cut(c.Str("notes").Trim(), 2000) };
        // lines(): description, quantity, price; a free row marked to save becomes a product
        var rows = new List<(string Desc, int Qty, int Cents, string Ref, bool Save)>();
        foreach (var r in (c["righe"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var desc = Words(r.Str("desc"), 120);
            if (desc == "" && r.Str("price").Trim() == "") continue;
            if (!int.TryParse(r.Str("qty").Trim(), out var qty)) qty = 0;
            if (desc == "" || qty is < 1 or > 99) throw new BookingException("Ogni riga vuole una descrizione e una quantità da 1 a 99.");
            var rf = Regex.IsMatch(r.Str("rif"), "^[sp]:[0-9a-f-]{36}$") ? r.Str("rif") : "";
            rows.Add((desc, qty, Till.Cents(r.Str("price")), rf, rf == "" && Json.Truthy(r["save"])));
        }
        if (rows.Count == 0) throw new BookingException("Aggiungi almeno una riga al preventivo.");
        if (rows.Count > 60) throw new BookingException("Al massimo 60 righe per preventivo.");
        if (d.Str("first_name") == "" && d.Str("last_name") == "") throw new BookingException("Scrivi il nome del cliente.");
        if (d.Str("email") != "" && !Regex.IsMatch(d.Str("email"), @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new BookingException("Email non valida.");
        DateOnly? valid = null;
        if (c.Str("valid_until") != "") { if (!DateOnly.TryParseExact(c.Str("valid_until"), "yyyy-MM-dd", out var v)) throw new BookingException("Data di validità non valida."); valid = v; }
        var contact = c.Id("contact") is Guid cid ? x.S.Get<Contact>(cid) ?? throw new KeyNotFoundException() : null;
        if (contact == null && c.Bool("new_client"))
            contact = new Clients(x).FindOrCreate(new JsonObject { ["first_name"] = d.Str("first_name"), ["last_name"] = d.Str("last_name"), ["phone"] = d.Str("phone"), ["email"] = d.Str("email") }, true).Item1;
        var made = 0; var lines = new JsonArray();
        foreach (var r in rows)
        {
            var rf = r.Ref;
            if (r.Save)
            {
                // the products come in a little at a time, from the quotes
                var name = Cut(r.Desc, 80);
                var prod = x.S.One<Product>("SELECT * FROM core_product WHERE lower(name)=lower(@n) AND active LIMIT 1", new { n = name });
                if (prod == null) { prod = x.S.Insert(new Product { TenantId = x.Tenant.Id, Name = name, Price = r.Cents != 0 ? r.Cents / 100m : null, Active = true, Created = x.Now }); made++; }
                rf = "p:" + prod.Id;
            }
            lines.Add(new JsonObject { ["desc"] = r.Desc, ["qty"] = r.Qty, ["cents"] = r.Cents, ["ref"] = rf });
        }
        d["lines"] = lines; var total = rows.Sum(r => r.Qty * r.Cents) / 100m; var isNew = quote == null;
        if (isNew)
        {
            x.Lock();  // one number at a time
            quote = new Quote { TenantId = x.Tenant.Id, Year = (short)today.Year, Actor = c.User.Id.ToString(), Created = x.Now, Updated = x.Now };
            quote.Number = (x.S.Scalar<int?>("SELECT max(number) FROM core_quote WHERE year=@y", new { y = today.Year }) ?? 0) + 1;
        }
        quote.ContactId = contact?.Id; quote.Total = total; quote.ValidUntil = valid?.ToDateTime(TimeOnly.MinValue); quote.Updated = x.Now; quote.Pii = x.Encrypt(d, "quote:" + quote.Id);
        if (isNew) x.S.Insert(quote); else x.S.Update(quote);
        x.Audit("quote.saved", quote.Id, new { @ref = Ref(quote), total = Prices.Str(total) });
        return new { id = quote.Id, testo = $"Preventivo {Ref(quote)} salvato." + (made > 0 ? $" {made} nuov{(made == 1 ? "o prodotto" : "i prodotti")} in Prodotti." : "") };
    }
    static string Cut(string v, int n) => v.Length > n ? v[..n] : v;

    [Rpc("preventivo.elimina", Perm = "booking.write")]
    public static object Delete(Call c)
    {
        Rights(c); var q = c.C.S.Get<Quote>(c.NeedId("id")) ?? throw new KeyNotFoundException();
        c.C.Audit("quote.deleted", q.Id, new { @ref = Ref(q) }); c.C.S.Delete<Quote>(q.Id);
        return new { testo = $"Preventivo {Ref(q)} eliminato." };
    }

    /// <summary>header(): the business on top of the quote: the data the agency keeps for it, the first location where they are missing.</summary>
    [Rpc("preventivo.foglio", Perm = "booking.write")]
    public static object Sheet(Call c)
    {
        Rights(c); var x = c.C; var q = x.S.Get<Quote>(c.NeedId("id")) ?? throw new KeyNotFoundException(); var d = Data(c, q); var t = c.Tenant;
        var prof = x.S.One<Clientprofile>("SELECT * FROM core_clientprofile WHERE tenant_id=@t LIMIT 1", new { t = t.Id }); var p = prof != null && prof.Data != "" ? x.Decrypt(prof.Data, "client-profile") : new JsonObject();
        var loc = x.S.One<Location>("SELECT * FROM core_location WHERE active ORDER BY name LIMIT 1");
        var address = string.Join(", ", new[] { p.Str("address"), p.Str("city") }.Where(v => v != "")); if (address == "") address = loc?.Address ?? "";
        var phone = p.Str("phone") != "" ? p.Str("phone") : loc?.Phone ?? "";
        var missing = new[] { (address, "indirizzo"), (phone, "telefono"), (p.Str("vat"), "partita IVA") }.Where(m => m.Item1 == "").Select(m => m.Item2);
        return new
        {
            id = q.Id, numero = Ref(q), totale = Prices.Str(q.Total), creato = x.Cal.DayOf(q.Created), valido = q.ValidUntil.HasValue ? DateOnly.FromDateTime(q.ValidUntil.Value) : (DateOnly?)null, chi = Who(d), telefono = d.Str("phone"), email = d.Str("email"), note = d.Str("notes"),
            intestazione = new { nome = p.Str("company") != "" ? p.Str("company") : t.Name, marchio = t.Name, indirizzo = address, telefono = phone, email = p.Str("email"), piva = p.Str("vat"), cf = p.Str("tax_code") },
            righe = (d["lines"] as JsonArray)?.Select(l => new { desc = l.Str("desc"), qty = Till.Int(l["qty"]), prezzo = Prices.Str(Till.Int(l["cents"]) / 100m), importo = Prices.Str(Till.Int(l["qty"]) * Till.Int(l["cents"]) / 100m) }),
            mancano = missing.ToList(),
        };
    }
}
