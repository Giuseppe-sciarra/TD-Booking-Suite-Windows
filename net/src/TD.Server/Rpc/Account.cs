using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>identity.security_view: Accesso e sicurezza of who is signed in: the password, Authenticator, the passkeys,
/// the recovery codes. Every change wants the user confirmed in the last 15 minutes.</summary>
public static class Account
{
    static readonly ConcurrentDictionary<Guid, (string Seed, DateTime Until)> pending = new();
    static bool Mfa(Call c) => !c.S.Standalone;

    [Rpc("account", Tenant = false)]
    public static object Page(Call c) => Data(c, null, null);

    static object Data(Call c, string notice, List<string> codes)
    {
        using var g = c.S.Db.Global(); var u = g.Get<User>(c.User.Id); var d = c.S.Identity.Pii(u);
        var seed = pending.TryGetValue(u.Id, out var p) && p.Until > DateTime.UtcNow ? p.Seed : "";
        var label = Uri.EscapeDataString("TD Booking Suite") + ":" + Uri.EscapeDataString(c.S.Identity.LoginName(u));
        return new
        {
            username = d.Str("username"), email = d.Str("email"), mfa = Mfa(c), totp = !string.IsNullOrEmpty(u.Totp), avviso = notice, codici = codes, seme = seed,
            qr = seed != "" ? Convert.ToBase64String(new QRCoder.PngByteQRCode(new QRCoder.QRCodeGenerator().CreateQrCode($"otpauth://totp/{label}?secret={seed}&issuer={Uri.EscapeDataString("TD Booking Suite")}", QRCoder.QRCodeGenerator.ECCLevel.M)).GetGraphic(6)) : "",
            chiavi = g.Query<Passkey>("SELECT * FROM core_passkey WHERE user_id=@u ORDER BY created", new { u = u.Id }).Select(k => new { id = k.Id, nome = k.Name, creata = k.Created.ToLocalTime().ToString("dd/MM/yyyy") }),
        };
    }

    [Rpc("account.azione", Tenant = false, Fresh = true)]
    public static object Action(Call c)
    {
        c.S.Identity.Throttle("security", c.User.Id.ToString(), 20); var action = c.Str("azione"); string notice = null; List<string> codes = null;
        if (action is "totp_start" or "totp_confirm" or "recovery" && !Mfa(c)) throw new BookingException("Il secondo fattore non è attivo in questa installazione.");
        using (var g = c.S.Db.Global())
        {
            var u = g.One<User>("SELECT * FROM core_user WHERE id=@i FOR UPDATE", new { i = c.User.Id });
            switch (action)
            {
                case "totp_start": pending[u.Id] = (Identity.NewBase32Secret(), DateTime.UtcNow.AddMinutes(10)); break;
                case "totp_confirm":
                    {
                        if (!pending.TryGetValue(u.Id, out var p) || p.Until < DateTime.UtcNow) throw new BookingException("Configurazione scaduta.");
                        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30; var code = c.Str("code").Trim();
                        if (code.Any(ch => ch > 127) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Identity.Totp(p.Seed, step)), Encoding.ASCII.GetBytes(code))) throw new BookingException("Codice non valido.");
                        c.S.Identity.SaveTotp(g, u, p.Seed); u.LastTotp = step; g.Update(u, "last_totp");
                        codes = c.S.Identity.NewRecovery(g, u);
                        g.Execute("UPDATE td_net_session SET mfa=true WHERE id=@i", new { i = c.Session.Id });  // this session passed the second factor
                        pending.TryRemove(u.Id, out _); notice = "Authenticator attivato."; break;
                    }
                case "recovery": codes = c.S.Identity.NewRecovery(g, u); notice = "Codici precedenti revocati."; break;
                case "password":
                    {
                        if (!Passwords.Verify(c.Str("current_password"), u.Password)) throw new BookingException("Password attuale non valida.");
                        var pw = c.Str("new_password"); Accounts.ValidatePassword(pw);
                        u.Password = Passwords.Make(pw); u.AuthVersion += 1; g.Update(u, "password", "auth_version");
                        g.Execute("UPDATE td_net_session SET auth_version=@v WHERE id=@i", new { v = u.AuthVersion, i = c.Session.Id });  // this window stays in, the others are out
                        notice = "Password aggiornata. Le altre sessioni sono state revocate."; break;
                    }
                case "delete_key":
                    if (string.IsNullOrEmpty(u.Totp) && g.Scalar<long>("SELECT count(*) FROM core_passkey WHERE user_id=@u", new { u = u.Id }) <= 1) throw new BookingException("Mantieni almeno un metodo MFA.");
                    g.Execute("DELETE FROM core_passkey WHERE id=@k AND user_id=@u", new { k = c.NeedId("key"), u = u.Id }); break;
                default: throw new BookingException("Operazione non valida.");
            }
            g.Commit();
        }
        return Data(c, notice, codes);
    }
}
