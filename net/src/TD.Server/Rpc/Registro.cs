using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.audit_view: Registro attività: the operations recorded, 100 a page, the most recent first.</summary>
public static class Registro
{
    [Rpc("registro", Perm = "audit.read")]
    public static object Page(Call c)
    {
        var x = c.C; var bk = new Bookings(x); const int SIZE = 100;
        var count = x.S.Scalar<long>("SELECT count(*) FROM core_audit"); var pages = Math.Max(1, (int)((count + SIZE - 1) / SIZE)); var page = Math.Clamp(c.Int("pagina", 1), 1, pages);
        return new
        {
            pagina = page, pagine = pages,
            righe = x.S.Query<Audit>("SELECT * FROM core_audit ORDER BY created DESC LIMIT @n OFFSET @o", new { n = SIZE, o = (page - 1) * SIZE }).Select(r =>
            {
                var d = Json.Obj(r.Detail); var reason = Bookings.REASON_LABELS.GetValueOrDefault(d.Str("reason"), "");
                return new
                {
                    quando = bk.Iso(r.Created),
                    operazione = r.Action + (Json.Truthy(d["reason"]) ? " · " + reason.ToLowerInvariant() : "") + (Json.Truthy(d["forced"]) ? " · forzata: " + string.Join(", ", (d["forced"] as JsonArray)?.Select(f => f?.ToString()) ?? new[] { d.Str("forced") }) : ""),
                    utente = d.Str("via") == "console" ? "Console del server" : r.Actor != "" ? r.Actor : "Cliente online", riferimento = r.Target,
                };
            }),
        };
    }
}
