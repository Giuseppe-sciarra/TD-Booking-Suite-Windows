using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>cassa.settings_view: Registratore di cassa: the register of each location, its departments and payments, and
/// how it is reached. This program is on the shop's computer: it calls the register itself (direct), or the bridge does.</summary>
public static class Registratore
{
    static void Allowed(Call c)
    {
        if (!(c.User.IsStaff || c.Tenant.TillSelf && c.Member?.Role == "owner")) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");
    }
    static object Cards(Call c, Guid? foundFor = null, object found = null, Guid? tokenFor = null, string token = "")
    {
        var x = c.C; var bk = new Bookings(x);
        return new
        {
            dispositivi = Till.DEVICES.Select(d => new { id = d.Id, nome = d.Label }), porte = TillLink.Ports(), velocita = Till.BAUDS, diretto = true,
            ponte = c.PublicOrigin.TrimEnd('/') + "/book/" + c.Tenant.Slug + "/api/cassa/",
            sedi = x.S.Query<Location>("SELECT * FROM core_location WHERE active ORDER BY name").Select(l =>
            {
                var conf = Till.Conf(l); var deps = conf["departments"] as JsonObject ?? new JsonObject();
                var last = x.S.One<Sale>("SELECT * FROM core_sale WHERE location_id=@l AND kind IN ('probe','test') ORDER BY created DESC LIMIT 1", new { l = l.Id });
                return new
                {
                    id = l.Id, nome = l.Name, conf, visto = l.TillSeen.HasValue ? bk.Iso(l.TillSeen.Value) : "", haCodice = l.TillToken != "",
                    pagamenti = Till.PAYMENTS.Select(p => new { id = p.Id, nome = p.Label, valore = conf["payments"]?[p.Id]?.ToString() ?? "" }),
                    servizi = x.S.Query<Service>("SELECT * FROM core_service WHERE location_id=@l AND active ORDER BY name", new { l = l.Id }).Select(s => new { id = s.Id, nome = s.Name, reparto = deps[s.Id.ToString()]?["dept"]?.ToString() ?? "", iva = deps[s.Id.ToString()].Str("vat") }),
                    repartiImpostati = deps.Count(d => Json.Truthy(d.Value?["dept"]) || Json.Truthy(d.Value?["vat"])),
                    ultima = last == null ? null : new { tipo = Cassa.SALE_KIND.GetValueOrDefault(last.Kind, last.Kind), quando = bk.Iso(last.Created), fatto = last.Status == "done", stato = Cassa.STATUS.GetValueOrDefault(last.Status, last.Status).ToLowerInvariant(), errore = last.Error },
                    rch = Till.RCH_CMDS.Keys.ToDictionary(k => k, k => Till.RchCmd(conf, k)),
                    trovate = foundFor == l.Id ? found : null, codice = tokenFor == l.Id ? token : "",
                };
            }),
        };
    }

    [Rpc("registratore")]
    public static object Page(Call c) { Allowed(c); return Cards(c); }

    [Rpc("registratore.ponte")]
    public static IResult Bridge(Call c)
    {
        Allowed(c);
        using var s = typeof(Till).Assembly.GetManifestResourceStream("TD.Core.ponte_cassa.py"); using var m = new MemoryStream(); s.CopyTo(m);
        return Results.File(m.ToArray(), "text/x-python", "ponte_cassa.py");
    }

    [Rpc("registratore.azione")]
    public static object Action(Call c)
    {
        Allowed(c); var x = c.C; var t = c.Tenant; var action = c.Str("azione");
        var loc = x.S.One<Location>("SELECT * FROM core_location WHERE id=@id", new { id = c.NeedId("sede") }) ?? throw new KeyNotFoundException();
        string text = "";
        if (action == "save")
        {
            var device = c.Str("device");
            if (!Till.DEVICES.Any(d => d.Id == device)) throw new BookingException("Scegli il registratore.");
            var conf = new JsonObject();
            if (device != "")
            {
                conf["device"] = device; conf["link"] = c.Str("link") == "direct" ? "direct" : "bridge";
                var serial = Regex.Replace(c.Str("serial").ToUpperInvariant(), "[^A-Z0-9]", ""); conf["serial"] = serial.Length > 20 ? serial[..20] : serial;
                var departments = new JsonObject(); var payments = new JsonObject(); conf["departments"] = departments; conf["payments"] = payments;
                if (device == "rch" && c.Str("connection") == "usb")
                {
                    // Epson's USB goes through its own driver: network only
                    var port = c.Str("port").Trim(); if (port.StartsWith("com", StringComparison.OrdinalIgnoreCase)) port = port.ToUpperInvariant();
                    if (!Till.PORT.IsMatch(port)) throw new BookingException("Scrivi la porta della cassa, per esempio COM3.");
                    var baud = c.Int("baud", 9600); if (!Till.BAUDS.Contains(baud)) throw new BookingException("Velocità non valida.");
                    conf["connection"] = "usb"; conf["port"] = port; conf["baud"] = baud; conf["framing"] = c.Str("framing") == "cr" ? "cr" : "stx";
                }
                else { conf["connection"] = "net"; conf["address"] = Till.Lan(c.Str("address")); }
                int? Number(string name, int low = 1)
                {
                    var v = c.Str(name).Trim(); if (v == "") return null;
                    if (!v.All(char.IsAsciiDigit) || !(int.TryParse(v, out var n) && low <= n && n <= 99)) throw new BookingException("Reparti e pagamenti sono numeri da 1 a 99 (Epson: tipo di pagamento da 0).");
                    return n;
                }
                var def = Number("default_dept") ?? 1; conf["default_dept"] = def;
                foreach (var k in new[] { "product_dept", "gift_dept" }) if (Number(k) is int n) conf[k] = n;  // empty, the one of the free lines
                if (device == "rch")
                {
                    var v = c.Str("last_z").Trim();  // the closure number the documents are counted from
                    if (v != "" && !(v.All(char.IsAsciiDigit) && int.TryParse(v, out var z) && z <= 9999)) throw new BookingException("Numero dell'ultima chiusura: da 0 a 9999, come stampato sulla chiusura.");
                    if (v != "") conf["last_z"] = int.Parse(v);
                    foreach (var (key, (preset, need)) in Till.RCH_CMDS)
                    {
                        v = string.Join(" ", (c.Args[key] == null ? preset : c.Str(key)).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                        if (v != "" && !(v.Split(' ').All(cmd => Regex.IsMatch(cmd, @"^=[\x21-\x7e]{1,38}\z") && !cmd.Contains('(')) && v.Contains(need) && v.Length <= 80))
                            throw new BookingException($"Comandi RCH: ognuno comincia con =, senza parentesi, separati da uno spazio{(need != "" ? ", e ci vuole " + need : "")}.");
                        conf[key] = v;
                    }
                }
                var depts = c.Obj("reparti"); var vats = c.Obj("iva");
                foreach (var sid in x.S.Query<Guid>("SELECT id FROM core_service WHERE location_id=@l", new { l = loc.Id }))
                {
                    var raw = depts.Str(sid.ToString()).Trim(); int? dept = null;
                    if (raw != "") { if (!raw.All(char.IsAsciiDigit) || !(int.TryParse(raw, out var n) && n is >= 1 and <= 99)) throw new BookingException("Reparti e pagamenti sono numeri da 1 a 99 (Epson: tipo di pagamento da 0)."); dept = n; }
                    var vat = vats.Str(sid.ToString()).Trim(); if (vat.Length > 8) vat = vat[..8];
                    if (dept.HasValue || vat != "") departments[sid.ToString()] = new JsonObject { ["dept"] = dept ?? def, ["vat"] = vat };
                }
                foreach (var (k, _) in Till.PAYMENTS) payments[k] = Number("pay_" + k, 0) ?? Till.DEFAULT_PAY[device][k];
                // the documents the register made on its own keys, learned from a number corrected at the desk, stay
                // (Python forgets them on Salva: the numbers would start wrong again)
                if (device == "rch" && Till.Conf(loc)["doc_offset"] is JsonNode off) conf["doc_offset"] = off.DeepClone();
            }
            loc.Till = Json.Dump(conf); x.S.Update(loc, "till");
            x.S.Execute("UPDATE core_tenant SET till_on = EXISTS(SELECT 1 FROM core_location WHERE active AND till<>'{}'::jsonb) WHERE id=@t", new { t = t.Id });
            x.Audit("till.settings", loc.Id, new { device });
            text = device != "" ? "Registratore salvato." : "Registratore tolto dalla sede.";
        }
        else if (action == "discover")
        {
            var (found, ports) = TillLink.Discover(); x.Audit("till.discover", loc.Id, new { found = found.Count });
            return Cards(c, loc.Id, new { rete = found.Select(f => new { dispositivo = f.Device, indirizzo = f.Address }), porte = ports.Select(p => new { porta = p, descrizione = "" }) });
        }
        else if (action is "probe" or "test")
        {
            var conf = Till.Conf(loc); if (conf.Str("device") == "") throw new BookingException("Prima salva il registratore.");
            new Till(x).Enqueue(loc, action);
            text = conf.Str("link") == "direct" ? "Richiesta inviata alla cassa." : "Richiesta in coda: la manda il ponte cassa appena si collega.";
        }
        else if (action == "token")
        {
            var token = "tdcassa_" + Bookings.TokenUrlSafe(32);
            loc.TillToken = TD.Core.Crypto.TokenHash(token); x.S.Update(loc, "till_token"); x.Audit("till.token", loc.Id);
            return Cards(c, null, null, loc.Id, token);
        }
        else if (action == "revoke")
        {
            loc.TillToken = ""; x.S.Update(loc, "till_token"); x.Audit("till.token_revoked", loc.Id); text = "Codice del ponte revocato.";
        }
        return new { testo = text };
    }
}
