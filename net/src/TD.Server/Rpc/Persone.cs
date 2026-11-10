using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>platform.members_view: Persone e permessi: who has access to a business and with which role. Only the
/// administrator of the program, after confirming it is him.</summary>
public static class Persone
{
    static void Staff(Call c) { if (!c.User.IsStaff) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione."); }
    static Accounts Acc(Call c) => new(c.S.Db, c.S.Crypto);

    static object View(Call c, string link = "", string text = "")
    {
        var x = c.C; var t = c.Tenant; var id = c.S.Identity;
        var names = x.S.Query<(Guid, string)>("SELECT id, name FROM core_location UNION ALL SELECT id, name FROM core_resource").ToDictionary(r => r.Item1.ToString(), r => r.Item2);
        return new
        {
            invito = link, testo = text, emailPronta = Accounts.MailReady(x.S, t), senzaPassword = true, mfa = !c.S.Standalone,
            persone = x.S.Query<Membership>("SELECT * FROM core_membership WHERE tenant_id=@t ORDER BY id", new { t = t.Id }).Select(m =>
            {
                var u = x.S.One<User>("SELECT * FROM core_user WHERE id=@u", new { u = m.UserId }); var d = id.Pii(u);
                return new
                {
                    id = m.Id, nome = id.LoginName(u), email = d.Str("username") != "" ? d.Str("email") : "", ruolo = m.Role, ruoloNome = Permissions.RoleLabel(m.Role), disponibilita = m.CanSchedule,
                    solo = Permissions.Ids(m.Locations).Concat(Permissions.Ids(m.Resources)).Select(k => names.GetValueOrDefault(k, "?")), rapido = u.QuickLogin, staff = u.IsStaff,
                };
            }).ToList(),
            ruoli = Permissions.Roles.Keys.Select(k => new { id = k, nome = Permissions.RoleLabel(k) }),
            sedi = x.S.Query<(Guid, string)>("SELECT id, name FROM core_location ORDER BY name").Select(l => new { id = l.Item1, nome = l.Item2 }),
            risorse = x.S.Query<(Guid, string)>("SELECT id, name FROM core_resource ORDER BY name").Select(r => new { id = r.Item1, nome = r.Item2 }),
        };
    }

    [Rpc("persone", Fresh = true)]
    public static object Page(Call c) { Staff(c); return View(c); }

    [Rpc("persone.azione", Fresh = true)]
    public static object Action(Call c)
    {
        Staff(c); var x = c.C; var t = c.Tenant; var acc = Acc(c); var id = c.S.Identity; var bk = new Bookings(x); var action = c.Str("azione"); var link = ""; var text = "";
        if (action == "invite")
        {
            var role = c.Str("role"); if (!Permissions.Roles.ContainsKey(role)) throw new BookingException("Ruolo non valido.");
            List<string> Chosen(string key, string table) { var ids = (c.Args[key] as JsonArray)?.Select(v => Bookings.AsId(v)).Where(v => v.HasValue).Select(v => v.Value).ToArray() ?? Array.Empty<Guid>(); return x.S.Query<Guid>($"SELECT id FROM {table} WHERE id = ANY(@i)", new { i = ids }).Select(v => v.ToString()).ToList(); }
            var locations = Chosen("locations", "core_location"); var resources = Chosen("resources", "core_resource");
            if (role is "manager" or "operator" && locations.Count == 0 && resources.Count == 0)
            {
                // a single-location business: the location is implicit
                var only = x.S.Query<Guid>("SELECT id FROM core_location LIMIT 2").ToList();
                if (only.Count == 1) locations = new() { only[0].ToString() };
                else if (role == "operator") throw new BookingException("Assegna all'operatore sede almeno una sede o una risorsa.");
            }
            var password = c.Str("password");
            var (u, created) = acc.AccountFor(x.S, c.Str("username"), c.Str("email"), t.Id);
            // an account that already exists is only added: activated or given a password from here only by the business it
            // belongs to alone, never by the agency (the link or the password would be its own)
            if (!created && (password != "" || !Passwords.Usable(u.Password)) && acc.Elsewhere(u, t.Id))
                throw new BookingException("Questa persona ha già un accesso in un'altra attività, non ancora attivato: deve attivarlo dal suo invito, poi puoi aggiungerla.");
            if (u.Id == c.User.Id && c.Member == null) throw new BookingException("L'amministratore del programma entra nei contenuti con l'accesso di assistenza concesso dall'attività, non aggiungendo se stessa.");
            var member = x.S.One<Membership>("SELECT * FROM core_membership WHERE tenant_id=@t AND user_id=@u", new { t = t.Id, u = u.Id });
            if (member == null)
            {
                var mid = x.S.Scalar<long>("INSERT INTO core_membership (tenant_id, user_id, role, can_schedule, locations, resources) VALUES (@t, @u, 'operator', false, '[]'::jsonb, '[]'::jsonb) RETURNING id", new { t = t.Id, u = u.Id });
                member = x.S.One<Membership>("SELECT * FROM core_membership WHERE id=@i", new { i = mid });
            }
            if (member.UserId == c.User.Id && role != "owner") throw new BookingException("Non puoi togliere a te stesso il ruolo di amministratore.");
            if (member.Role == "owner" && role != "owner" && x.S.Scalar<long>("SELECT count(*) FROM core_membership WHERE tenant_id=@t AND role='owner' AND id<>@i", new { t = t.Id, i = member.Id }) == 0)
                throw new BookingException("L'attività deve avere almeno un amministratore.");
            var locs = role == "owner" ? new List<string>() : locations; var res = role == "owner" ? new List<string>() : resources;
            x.S.Execute("UPDATE core_membership SET role=@r, can_schedule=@s, locations=@l::jsonb, resources=@x::jsonb WHERE id=@i",
                new { r = role, s = role == "owner" || c.Bool("can_schedule"), l = Json.Dump(new JsonArray(locs.Select(v => (JsonNode)v).ToArray())), x = Json.Dump(new JsonArray(res.Select(v => (JsonNode)v).ToArray())), i = member.Id });
            link = acc.GrantAccess(x.S, u, password, c.PublicOrigin);
            var mail = id.Email(u);
            if (link != "" && mail != "") bk.Queue(new JsonObject { ["to"] = mail, ["subject"] = "Invito · " + t.Name, ["body"] = "Attiva il tuo accesso:\n" + link });
            if (password != "") text = id.LoginName(u) + " può già accedere con la password che hai scelto.";
            x.Audit("member.invited", u.Id, new { role, locations = locs.Count, resources = res.Count });
        }
        else
        {
            var member = x.S.One<Membership>("SELECT * FROM core_membership WHERE tenant_id=@t AND id=@i", new { t = t.Id, i = (long)c.Int("member") }) ?? throw new KeyNotFoundException();
            var u = x.S.One<User>("SELECT * FROM core_user WHERE id=@u", new { u = member.UserId });
            if (action is "quick_on" or "quick_off")
            {
                // one click from the program's PC and the registered workstations, for an owner only
                if (member.Role != "owner" || u.IsStaff) throw new BookingException("Senza password entra solo un titolare.");
                u.QuickLogin = action == "quick_on"; x.S.Update(u, "quick_login"); x.Audit("member.quick_login", u.Id, new { on = u.QuickLogin });
                text = u.QuickLogin ? id.LoginName(u) + " entra senza password dal PC del programma e dalle postazioni registrate." : id.LoginName(u) + " entra di nuovo con la password.";
            }
            else if (action == "remove")
            {
                var owners = x.S.Query<long>("SELECT id FROM core_membership WHERE tenant_id=@t AND role='owner' FOR UPDATE", new { t = t.Id }).Count();
                if (member.Role == "owner" && owners == 1) throw new BookingException("L'attività deve avere almeno un amministratore.");
                x.S.Execute("DELETE FROM core_membership WHERE id=@i", new { i = member.Id }); x.Audit("member.removed", u.Id);
            }
            else if (action == "reset")
            {
                if (u.IsStaff) throw new BookingException("Usa la procedura di recupero amministratore da console.");
                var agency = c.Member == null;
                u.AuthVersion++; u.Password = Passwords.Unusable(); x.S.Update(u, "auth_version", "password");
                link = acc.Invitation(x.S, u, c.PublicOrigin); var mail = id.Email(u);
                if (mail != "") bk.Queue(new JsonObject { ["to"] = mail, ["subject"] = "Ripristino accesso · " + t.Name, ["body"] = "Imposta una nuova password:\n" + link + "\nIl metodo MFA rimane attivo." });
                x.Audit("member.password_reset", u.Id);
                // the agency enters the contents only with a support grant: the link goes to the person, not on this screen;
                // without an email the agency hands it over, since only the agency manages the people
                if (agency && mail != "") { link = ""; text = "Link per la nuova password inviato a " + mail + "."; }
            }
            else throw new BookingException("Operazione non valida.");
        }
        return View(c, link, text);
    }
}
