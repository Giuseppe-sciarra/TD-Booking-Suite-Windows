using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>platform.support_view: Accesso dell'amministratore: the owner grants time-boxed content access to the
/// administrator of the program; every page he opens stays recorded.</summary>
public static class Assistenza
{
    static object View(Call c, string text = "")
    {
        var x = c.C; var bk = new Bookings(x); var id = c.S.Identity; var now = x.Now;
        return new
        {
            testo = text,
            concessioni = x.S.Query<Supportgrant>("SELECT * FROM core_supportgrant ORDER BY created DESC LIMIT 50").Select(g => new
            {
                id = g.Id, nome = id.LoginName(x.S.One<User>("SELECT * FROM core_user WHERE id=@u", new { u = g.UserId })), motivo = g.Reason, revocato = g.Revoked, attivo = !g.Revoked && g.Expires > now, scade = bk.Iso(g.Expires),
            }).ToList(),
            letture = x.S.Query<Audit>("SELECT * FROM core_audit WHERE action='support.access' ORDER BY created DESC LIMIT 100").Select(r => { var d = Json.Obj(r.Detail); return new { pagina = d.Str("path"), metodo = d.Str("method"), quando = bk.Iso(r.Created) }; }).ToList(),
        };
    }

    [Rpc("assistenza", Perm = "users.manage")]
    public static object Page(Call c) => View(c);

    [Rpc("assistenza.azione", Perm = "users.manage", Fresh = true)]
    public static object Action(Call c)
    {
        var x = c.C; var action = c.Str("azione");
        if (action == "grant")
        {
            var login = c.Str("email").Trim(); if (login.Length > 254) login = login[..254];
            var staff = c.S.Identity.ByLogin(x.S, login); if (staff is not { IsStaff: true }) throw new BookingException("Nessun amministratore con questo username o email.");
            var reason = c.Str("reason").Trim(); if (reason.Length is < 10 or > 300) throw new BookingException("Descrivi il motivo dell'assistenza (10-300 caratteri).");
            var hours = c.Int("hours", 4); if (hours is < 1 or > 72) throw new BookingException("Durata tra 1 e 72 ore.");
            var g = new Supportgrant { TenantId = c.Tenant.Id, UserId = staff.Id, Reason = reason, Expires = x.Now.AddHours(hours), CreatedBy = c.User.Id.ToString(), Created = x.Now };
            x.S.Insert(g); x.Audit("support.granted", g.Id, new { hours });
            return View(c, $"Accesso concesso per {hours} ore.");
        }
        if (action == "revoke")
        {
            var g = c.Id("grant") is Guid gid ? x.S.One<Supportgrant>("SELECT * FROM core_supportgrant WHERE id=@i", new { i = gid }) : null; if (g == null) throw new BookingException("Accesso non trovato.");
            g.Revoked = true; x.S.Update(g, "revoked"); x.Audit("support.revoked", g.Id);
            return View(c, "Accesso revocato.");
        }
        throw new BookingException("Operazione non valida.");
    }
}
