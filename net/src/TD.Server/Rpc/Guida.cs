using System.Reflection;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.guide: the Guida, the same text as the web program's (templates/guide.html), rendered once by
/// tools/gen_guide.py for the administrator or not, with the second factor on or off; here the role and the business.</summary>
public static class Guida
{
    [Rpc("guida", Tenant = false)]
    public static IResult Page(Call c)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"guide/guida-{(c.User.IsStaff ? 1 : 0)}{(c.S.Standalone ? 0 : 1)}.json");
        using var reader = new StreamReader(stream); var json = reader.ReadToEnd();
        if (!c.User.IsStaff)
        {
            using var g = c.S.Db.Global(); var slug = c.Http.Request.Headers["X-TD-Attivita"].ToString();
            var t = g.One<Tenant>("SELECT * FROM core_tenant WHERE slug=@s", new { s = slug }) ?? g.One<Tenant>("SELECT * FROM core_tenant ORDER BY created LIMIT 1");
            var m = t == null ? null : g.One<Membership>("SELECT * FROM core_membership WHERE tenant_id=@t AND user_id=@u", new { t = t.Id, u = c.User.Id });
            string Esc(string v) => System.Text.Json.JsonSerializer.Serialize(v, TD.Core.Crypto.Json)[1..^1];
            json = m == null ? json.Replace("Sei §r§ di §t§", "Come funziona TD Booking Suite") : json.Replace("§r§", Esc(Permissions.RoleLabel(m.Role).ToLowerInvariant())).Replace("§t§", Esc(t.Name));
        }
        return Results.Content(json, "application/json; charset=utf-8");
    }
}
