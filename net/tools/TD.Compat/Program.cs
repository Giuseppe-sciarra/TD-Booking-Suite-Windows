// The C# half of tools/compat/compat.py: the same dump from the same database, to diff with the Python's.
// dotnet run --project tools/TD.Compat -- "<npgsql connection string>" dump|create
using System.Text.Json;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

var keys = "{\"v1\":\"" + Crypto.B64("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"u8.ToArray()) + "\",\"index\":\"" + Crypto.B64("iiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii"u8.ToArray()) + "\"}";
var crypto = new Crypto(keys, "v1");
using var db = new Db(args[0]);
Guid tid;
using (var g = db.Global()) tid = g.Scalar<Guid>("SELECT id FROM core_tenant WHERE slug='cmp'");
var mode = args.Length > 1 ? args[1] : "dump";

if (mode == "create")
{
    // bookings made by the C# for the Python to read back: public, desk, a visit, a forced one, cancelled and moved
    using var c = new Ctx(db, crypto, tid);
    var b = new Bookings(c);
    var svc = c.S.All<Service>().ToDictionary(x => x.Name);
    var res = c.S.All<Resource>().ToDictionary(x => x.Name);
    JsonObject P(string s, int days, int h, int m = 0, params (string, JsonNode)[] kv)
    {
        var day = c.Today.AddDays(days);
        var o = new JsonObject { ["service"] = svc[s].Id.ToString(), ["start"] = b.Iso(c.Cal.Local(day, new TimeSpan(h, m, 0))!.Value), ["party"] = 1, ["first_name"] = "Giulia", ["last_name"] = "Bianchi",
            ["email"] = "giulia" + days + h + m + "@example.invalid", ["phone"] = "333 765 43" + (10 + days).ToString(), ["notes"] = "Nota è àccentata", ["request_key"] = Bookings.TokenUrlSafe(24) };
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }
    var made = new List<string>();
    void Try(string what, Func<Booking> f) { try { made.Add(what + ":" + f().Id); } catch (BookingException e) { made.Add(what + ":ERR " + e.Message); } }
    Try("web-taglio", () => b.Create(P("Taglio", 3, 11)).Item1);
    Try("web-colore", () => b.Create(P("Colore", 3, 9, 0, ("resource", res["Anna"].Id.ToString()))).Item1);
    c.Actor = c.S.Scalar<Guid?>("SELECT id FROM core_user LIMIT 1") ?? Guid.NewGuid();
    Try("desk-taglio", () => b.Create(P("Taglio", 4, 10, 15, ("resource", res["Bruno"].Id.ToString()), ("email", ""))).Item1);
    Try("visit", () => b.Create(P("Taglio", 6, 9, 0, ("extra", svc["Colore"].Id.ToString()))).Item1);
    Try("forced", () => b.Create(P("Taglio", 1, 10, 0, ("resource", res["Anna"].Id.ToString()), ("force", true), ("force_ok", true))).Item1);
    Try("pranzo", () => b.Create(P("Pranzo", 3, 12, 30, ("party", 5))).Item1);
    Try("cancel", () => { var x = b.Create(P("Taglio", 4, 15)).Item1; return b.ChangeStatus(x, "cancelled", "business", "chiusura  improvvisa"); });
    Try("move", () => { var x = b.Create(P("Taglio", 7, 11, 0, ("resource", res["Anna"].Id.ToString()))).Item1; return b.Move(x.Id, P("Taglio", 7, 14, 0, ("resource", res["Anna"].Id.ToString()))); });
    c.Commit();
    Console.WriteLine(string.Join("\n", made));
    return;
}

using (var c = new Ctx(db, crypto, tid))
{
    var b = new Bookings(c);
    var svc = c.S.All<Service>().ToDictionary(x => x.Name);
    var res = c.S.All<Resource>().ToDictionary(x => x.Name);
    var links = c.S.All<Serviceresource>().ToList();
    var cases = new List<(string Name, string R, bool Desk, string Opt, int Party)>();
    foreach (var name in new[] { "Taglio", "Colore", "Trattamento viso", "Pranzo", "Corso" })
    {
        var s = svc[name];
        foreach (var desk in new[] { false, true })
        {
            cases.Add((name, "", desk, null, 1));
            if (s.Kind == "resource")
            {
                foreach (var r in res.Values) if (links.Any(l => l.ServiceId == s.Id && l.ResourceId == r.Id) && !r.Secondary) cases.Add((name, r.Name, desk, null, 1));
                if (name == "Taglio") cases.Add((name, "", desk, c.S.Scalar<Guid>("SELECT id FROM core_option WHERE name='Lungo'").ToString(), 1));
            }
            if (name == "Pranzo") { cases.Add((name, "", desk, null, 2)); cases.Add((name, "", desk, null, 5)); cases.Add((name, "", desk, null, 8)); }
        }
    }
    var slots = new JsonObject();
    for (int d = 0; d < 12; d++)
    {
        var day = c.Today.AddDays(d);
        foreach (var (name, rname, desk, opt, party) in cases)
        {
            var key = $"{day:yyyy-MM-dd}|{name}|{rname}|{(desk ? "desk" : "web")}|{opt ?? ""}|{party}";
            try
            {
                var r = b.Available(svc[name].Id.ToString(), day, opt, rname != "" ? res[rname].Id.ToString() : null, party, desk: desk);
                slots[key] = new JsonArray(r.Select(x => (JsonNode)new JsonArray(x.Start, x.End, x.Resource, x.Occurrence)).ToArray());
            }
            catch (BookingException e) { slots[key] = "ERR:" + e.Message; }
        }
        var v = b.VisitSlots(new() { (svc["Taglio"], null), (svc["Colore"], null) }, day);
        slots[$"{day:yyyy-MM-dd}|VISIT"] = new JsonArray(v.Select(x => (JsonNode)new JsonArray(x.Start, x.End, x.Resource, x.ResourceName)).ToArray());
    }
    var books = new JsonObject();
    foreach (var x in c.S.Query<Booking>("SELECT * FROM core_booking ORDER BY created"))
    {
        var d = b.Data(x); d.Remove("cancel_token");
        var snap = x.Snap; snap.Remove("undo");
        var ct = x.ContactId.HasValue ? new Clients(c).Data(c.Row<Contact>(x.ContactId.Value)) : null;
        books[x.Id.ToString()] = new JsonObject
        {
            ["status"] = x.Status, ["start"] = b.Iso(x.Start).Replace("+01:00", "+00:00"), ["resource"] = x.ResourceId?.ToString() ?? "", ["secondary"] = x.SecondaryId?.ToString() ?? "",
            ["gap"] = new JsonArray(x.GapStart.HasValue ? "" + x.GapStart : "", x.GapEnd.HasValue ? "" + x.GapEnd : ""), ["tables"] = JsonNode.Parse(x.Tables), ["area"] = x.AreaId?.ToString() ?? "",
            ["snapshot"] = snap, ["pii"] = d, ["contact"] = ct, ["visit"] = x.Visit?.ToString() ?? "",
        };
    }
    Console.WriteLine(new JsonObject { ["slots"] = slots, ["bookings"] = books }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
