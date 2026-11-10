using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.holidays_view and allergens_view: the Italian public holidays of a year as closures in one click;
/// the intolerances and allergies the booking form asks about.</summary>
public static class CatalogoAltro
{
    static void Owner(Call c) { if (!Configura.CatalogAllowed(c, "exceptions")) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione."); }

    static (int Year, List<(DateOnly Day, string Name, bool There)> Rows) Holidays(Call c, string patronDay, string patronName)
    {
        var x = c.C; var today = x.Today; var year = Math.Clamp(c.Int("anno", today.Year), today.Year, today.Year + 2);
        var whole = x.S.Query<Exceptionday>("SELECT * FROM core_exceptionday WHERE purpose='service' AND location_id IS NULL AND service_id IS NULL AND resource_id IS NULL").ToList();
        return (year, Catalog.Holidays(year, patronDay, patronName).Select(h => (h.Day, h.Name, whole.Any(e => Calendar.Covering(e, h.Day)))).ToList());
    }
    static object View(Call c, int year, List<(DateOnly Day, string Name, bool There)> rows, HashSet<string> chosen)
    {
        var t = c.Tenant; var today = c.C.Today;
        var patron = rows.Where(r => !string.IsNullOrEmpty(t.PatronDay) && r.Day.ToString("MM-dd") == t.PatronDay).Select(r => (DateOnly?)r.Day).FirstOrDefault();
        return new
        {
            anno = year, anni = Enumerable.Range(today.Year, 3), patrono = patron?.ToString("yyyy-MM-dd") ?? "", nomePatrono = t.PatronName,
            righe = rows.Select(r => new { giorno = r.Day, nome = r.Name, segnata = r.There, spuntata = chosen.Contains(r.Day.ToString("yyyy-MM-dd")) }),
        };
    }

    [Rpc("festivita", Perm = "catalog.write")]
    public static object HolidaysPage(Call c)
    {
        Owner(c); var t = c.Tenant; var (year, rows) = Holidays(c, t.PatronDay, t.PatronName);
        // once a year is marked, the missing ones start unticked
        return View(c, year, rows, rows.Any(r => r.There) ? new() : rows.Select(r => r.Day.ToString("yyyy-MM-dd")).ToHashSet());
    }

    [Rpc("festivita.segna", Perm = "catalog.write")]
    public static object HolidaysSave(Call c)
    {
        Owner(c); var x = c.C; var t = c.Tenant;
        DateOnly? patron = null;
        if (c.Str("patron_day") != "") { if (DateOnly.TryParseExact(c.Str("patron_day"), "yyyy-MM-dd", out var pd)) patron = pd; else throw new FormErrors(new() { [""] = "Data del santo patrono non valida." }); }
        t.PatronDay = patron?.ToString("MM-dd") ?? ""; var name = string.Join(" ", c.Str("patron_name").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); t.PatronName = patron.HasValue ? name.Length > 60 ? name[..60] : name : "";
        var (year, rows) = Holidays(c, t.PatronDay, t.PatronName); var chosen = (c.Args["day"] as JsonArray)?.Select(v => v?.ToString()).ToHashSet() ?? new();
        x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = t.Id });
        x.S.Execute("UPDATE core_tenant SET patron_day=@d, patron_name=@n WHERE id=@t", new { d = t.PatronDay, n = t.PatronName, t = t.Id });
        var made = 0;
        foreach (var r in rows.Where(r => chosen.Contains(r.Day.ToString("yyyy-MM-dd")) && !r.There)) { x.S.Insert(new Exceptionday { TenantId = t.Id, Date = r.Day.ToDateTime(TimeOnly.MinValue), Closed = true, Label = r.Name }); made++; }
        var conflicts = new Catalog(x).Conflicts();
        if (conflicts.Count > 0 && !c.Bool("confirm_conflicts")) throw new FormErrors(new() { [""] = Catalog.Unsettled(conflicts) }, true);
        x.Audit("catalog.holidays", year, new { created = made, conflicts = conflicts.Count });
        return new { testo = (made > 0 ? $"Segnate {made} festività del {year} come chiusure." : $"Le festività scelte del {year} erano già segnate.") + (conflicts.Count > 0 ? $" {conflicts.Count} prenotazioni da ricontattare." : "") };
    }

    // ---------- Intolleranze e allergie
    [Rpc("allergie", Perm = "catalog.write")]
    public static object Allergens(Call c)
    {
        var t = c.Tenant; if (!Presets.ServesFood(t.BusinessType)) throw new KeyNotFoundException();
        return new { voci = Json.Strings(t.Allergens), accese = t.AllergensOn };
    }

    [Rpc("allergie.azione", Perm = "catalog.write")]
    public static object AllergensAction(Call c)
    {
        var x = c.C; var t = c.Tenant; if (!Presets.ServesFood(t.BusinessType)) throw new KeyNotFoundException();
        var action = c.Str("azione"); var items = Json.Strings(t.Allergens); string text;
        if (action == "toggle")
        {
            t.AllergensOn = c.Bool("on"); if (t.AllergensOn && items.Count == 0) items = Presets.Allergens.ToList();
            text = t.AllergensOn ? "Il modulo di prenotazione ora chiede intolleranze e allergie." : "Il modulo non le chiede più. Quelle già segnalate restano nelle prenotazioni.";
        }
        else if (action == "add")
        {
            var name = string.Join(" ", c.Str("name").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length is < 1 or > 60 || name.Any(ch => ch < 32)) throw new BookingException("Scrivi un nome, al massimo 60 caratteri.");
            if (items.Any(i => TD.Core.Crypto.CaseFold(i) == TD.Core.Crypto.CaseFold(name))) throw new BookingException(name + " è già nell'elenco.");
            if (items.Count >= 40) throw new BookingException("Al massimo 40 voci.");
            items.Add(name); text = "Aggiunta: " + name + ".";
        }
        else if (action == "remove") { var name = c.Str("name"); items = items.Where(i => i != name).ToList(); text = "Tolta: " + name + ". Resta nelle prenotazioni dove era già segnalata."; }
        else if (action == "reset") { items = Presets.Allergens.ToList(); text = "Elenco ripristinato: i 14 allergeni di legge più il lattosio."; }
        else throw new BookingException("Operazione non valida.");
        t.Allergens = Json.Dump(new JsonArray(items.Select(i => (JsonNode)i).ToArray()));
        x.S.Execute("UPDATE core_tenant SET allergens=@a::jsonb, allergens_on=@o WHERE id=@t", new { a = t.Allergens, o = t.AllergensOn, t = t.Id });
        x.Audit("allergens." + action, t.Id, new { items = items.Count, on = t.AllergensOn });
        return new { testo = text, voci = items, accese = t.AllergensOn };
    }

    // ---------- Importa servizi
    /// <summary>portal.services_import: the price list of the old program (Excel or CSV): a service a row with its price
    /// and minutes, made in one location, done by its operators when asked. A name already there is skipped.</summary>
    [Rpc("servizi.importa", Perm = "catalog.write")]
    public static object ImportServices(Call c)
    {
        if (!Configura.CatalogAllowed(c, "services")) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
        var x = c.C; var t = c.Tenant; var places = x.S.Query<Location>("SELECT * FROM core_location WHERE active ORDER BY name").ToList();
        if (c.Args["file"] == null) return new { sedi = places.Select(l => new { id = l.Id, nome = l.Name }) };
        byte[] raw; try { raw = Convert.FromBase64String(c.Str("file")); } catch (FormatException) { raw = Array.Empty<byte>(); }
        if (raw.Length == 0) throw new BookingException("Scegli il file da importare.");
        if (raw.Length > Importer.MAX_BYTES) throw new BookingException("File troppo grande: al massimo 20 MB.");
        var loc = places.FirstOrDefault(l => l.Id == c.Id("location")) ?? throw new BookingException("Scegli la sede.");
        var (head, rows) = Importer.Read(raw); var col = new Dictionary<string, int>(); for (int i = 0; i < head.Count; i++) col[Importer.Plain(head[i])] = i;
        int? Pick(params string[] names) => names.Where(col.ContainsKey).Select(n => (int?)col[n]).FirstOrDefault();
        var n = Pick("servizio", "nome", "descrizione", "trattamento"); var pr = Pick("prezzo", "prezzovendita", "importo"); var du = Pick("durata", "duratainminuti", "minuti", "tempomedio", "tempo");
        var gr = Pick("gruppo", "categoria", "reparto", "tipo");  // the resale rows of the old program become products
        if (n == null) throw new BookingException("Nel file manca la colonna Servizio (il nome).");
        var have = x.S.Query<string>("SELECT name FROM core_service WHERE location_id=@l", new { l = loc.Id }).Select(TD.Core.Crypto.CaseFold).ToHashSet();
        var goods = x.S.Query<string>("SELECT name FROM core_product").Select(TD.Core.Crypto.CaseFold).ToHashSet();
        var staff = c.Bool("staff") ? x.S.Query<Guid>("SELECT id FROM core_resource WHERE location_id=@l AND active", new { l = loc.Id }).ToList() : new();
        int created = 0, same = 0, products = 0; var errors = new List<string>(); var line = 1;
        foreach (var row in rows)
        {
            line++; string Cell(int? k) => k.HasValue && k < row.Count ? row[k.Value].Trim() : "";
            var name = Importer.Name(Cell(n)); if (name.Length > 120) name = name[..120];
            if (name == "") continue;
            var resale = System.Text.RegularExpressions.Regex.IsMatch(Cell(gr), "rivendit|prodott|vendita|retail", System.Text.RegularExpressions.RegexOptions.IgnoreCase); var folded = TD.Core.Crypto.CaseFold(name);
            if (folded.Contains("gift card") || folded.Contains("giftcard")) continue;  // the program has its own gift cards
            if ((resale ? goods : have).Contains(folded)) { same++; continue; }
            decimal? price = null; var minutes = 0; var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (Cell(pr) != "") { if (decimal.TryParse(Cell(pr).Replace("€", "").Replace(",", ".").Trim(), System.Globalization.NumberStyles.Float, inv, out var d)) price = Math.Round(d, 2, MidpointRounding.ToEven); else { errors.Add($"riga {line}: prezzo o durata non validi"); continue; } }
            if (Cell(du) != "") { if (double.TryParse(Cell(du).Replace(",", "."), System.Globalization.NumberStyles.Float, inv, out var m) && !double.IsNaN(m) && Math.Abs(m) < 1e9) minutes = (int)m; else { errors.Add($"riga {line}: prezzo o durata non validi"); continue; } }
            if (price is < 0 or > 99999) { errors.Add($"riga {line}: prezzo non valido"); continue; }
            if (resale) { x.S.Insert(new Product { TenantId = t.Id, Name = name.Length > 80 ? name[..80] : name, Price = price is > 0 ? price : null, Created = x.Now }); goods.Add(folded); products++; continue; }
            var svc = new Service { TenantId = t.Id, LocationId = loc.Id, Name = name, Kind = "resource", Duration = minutes is >= 5 and <= 720 ? minutes : 30, Interval = 15, Price = price, ResourceLabel = "Operatore" }; x.S.Insert(svc);
            foreach (var r in staff) x.S.Insert(new Serviceresource { TenantId = t.Id, ServiceId = svc.Id, ResourceId = r });
            have.Add(folded); created++;
        }
        x.Audit("services.imported", loc.Id, new { created, same });
        return new { sedi = places.Select(l => new { id = l.Id, nome = l.Name }), fatto = new { creati = created, prodotti = products, uguali = same, errori = errors.Take(20), quantiErrori = errors.Count } };
    }
}
