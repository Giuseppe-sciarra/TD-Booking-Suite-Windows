using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/platform.py and models.UserManager: the accounts of the people: found or created from a form, the
/// invitation link, a password typed by the administrator, with Django's rules for a new password.</summary>
public sealed class Accounts(Db db, Crypto crypto)
{
    static readonly Regex USERNAME = new(@"^[a-z0-9][a-z0-9._-]{2,39}\z");
    static readonly Regex EMAIL = new(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+\z");
    static readonly Lazy<HashSet<string>> Common = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TD.Core.common-passwords-12.txt"); using var r = new StreamReader(s);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    });

    public static string CleanUsername(string value)
    {
        value = Crypto.CaseFold((value ?? "").Trim());
        if (value != "" && !USERNAME.IsMatch(value)) throw new BookingException("Username non valido: da 3 a 40 caratteri tra lettere, numeri, punto, trattino e trattino basso, senza spazi né accenti.");
        return value;
    }
    public static void ValidateEmail(string email) { if (!EMAIL.IsMatch(email ?? "") || email.Length > 254) throw new BookingException("Inserisci un indirizzo email valido."); }
    /// <summary>AUTH_PASSWORD_VALIDATORS: 12 characters at least, not a common one, not only digits (the common ones of
    /// Django's list that are 12 long or more: the shorter are refused by the length).</summary>
    public static void ValidatePassword(string password)
    {
        var errors = new List<string>(); password ??= "";
        if (password.Length < 12) errors.Add("Questa password è troppo corta. Deve contenere almeno 12 caratteri.");
        if (Common.Value.Contains(password.ToLowerInvariant().Trim())) errors.Add("Questa password è troppo comune.");
        if (password.Length > 0 && password.All(char.IsDigit)) errors.Add("Questa password è interamente numerica.");
        if (errors.Count > 0) throw new BookingException(string.Join(" ", errors));
    }

    public User Create(Scope s, string email, string username = "", bool staff = false)
    {
        email = Crypto.CaseFold((email ?? "").Trim()); username = CleanUsername(username);
        if (email == "" && username == "") throw new BookingException("Serve un'email o uno username.");
        // usernames and emails share the index space without clashing (no "@" in a username); without an email the username is the login
        var u = new User { LoginIndex = crypto.LoginIndex(email != "" ? email : username), NameIndex = username != "" ? crypto.LoginIndex(username) : null, Password = Passwords.Unusable(), IsStaff = staff, IsSuperuser = staff };
        var pii = new JsonObject { ["email"] = email }; if (username != "") pii["username"] = username;
        u.Pii = crypto.Protect(pii, "user:" + u.Id); s.Insert(u); return u;
    }

    /// <summary>Someone who also works in another business (or for the agency): the access of that person is not this business's to hand out.</summary>
    public bool Elsewhere(User u, Guid tenant)
    {
        using var g = db.Global();
        return u.IsStaff || g.Scalar<long>("SELECT count(*) FROM core_membership WHERE user_id=@u AND tenant_id<>@t", new { u = u.Id, t = tenant }) > 0;
    }

    /// <summary>The account a form names: its email or its username with the email optional. An existing one is returned as
    /// it is, a new one is created without password. Found by the username alone, an account that works elsewhere is refused.</summary>
    public (User User, bool Created) AccountFor(Scope s, string usernameRaw, string emailRaw, Guid tenant)
    {
        var username = CleanUsername(usernameRaw); var email = Crypto.CaseFold((emailRaw ?? "").Trim());
        if (email == "" && username == "") throw new BookingException("Scrivi lo username o l'email.");
        if (email != "") ValidateEmail(email);
        var byMail = email != "" ? s.One<User>("SELECT * FROM core_user WHERE login_index=@k", new { k = crypto.LoginIndex(email) }) : null;
        var found = byMail ?? (username != "" ? s.One<User>("SELECT * FROM core_user WHERE name_index=@k", new { k = crypto.LoginIndex(username) }) : null);
        if (found != null)
        {
            var d = crypto.Reveal(found.Pii, "user:" + found.Id) as JsonObject ?? new JsonObject();
            if (username != "" && d.Str("username") != username || email != "" && d.Str("email") != email)
                throw new BookingException("Username ed email non sono dello stesso account: per una persona che ha già un accesso basta uno dei due.");
            if (byMail == null && Elsewhere(found, tenant)) throw new BookingException("Username già usato da un'altra persona: scegline un altro, oppure scrivi l'email di questa persona.");
            return (found, false);
        }
        if (username == "") throw new BookingException("Per una persona nuova scegli lo username: l'email da sola basta per chi ha già un accesso.");
        return (Create(s, email, username), true);
    }

    /// <summary>A personal link valid 24 hours; the ones before stop working.</summary>
    public string Invitation(Scope s, User u, string origin)
    {
        s.Execute("UPDATE core_invitation SET used=true WHERE user_id=@u AND NOT used", new { u = u.Id });
        var token = Bookings.TokenUrlSafe(32);
        s.Execute("INSERT INTO core_invitation (token, user_id, expires, used) VALUES (@t, @u, @e, false)", new { t = Crypto.TokenHash(token), u = u.Id, e = DateTime.UtcNow.AddHours(24) });
        return origin.TrimEnd('/') + "/invite/" + token + "/";
    }

    /// <summary>recupera_accesso: a forgotten password, from the server PC as a Windows administrator: sessions,
    /// Authenticator and passkeys revoked, and the link, valid 24 hours, to choose the new password.</summary>
    public (bool Staff, string Link) Recover(string login, string origin)
    {
        using var g = db.Global();
        var u = new Identity(db, crypto).ByLogin(g, login); if (u is not { IsActive: true }) throw new BookingException("Nessun account con questo username o email.");
        u = g.One<User>("SELECT * FROM core_user WHERE id=@id FOR UPDATE", new { id = u.Id });
        u.AuthVersion++; u.Totp = ""; u.Recovery = "[]"; u.Password = "!" + Bookings.TokenUrlSafe(30); g.Update(u, "auth_version", "totp", "recovery", "password");
        g.Execute("DELETE FROM core_passkey WHERE user_id=@u", new { u = u.Id });
        var link = Invitation(g, u, origin); var tenants = g.Query<Guid>("SELECT tenant_id FROM core_membership WHERE user_id=@u", new { u = u.Id }).ToList(); g.Commit();
        // in the Registro attività of each business: whoever picks the new password is not the one who had it
        foreach (var t in tenants) { using var c = new Ctx(db, crypto, t); c.Audit("member.password_reset", u.Id, new { via = "console" }); c.Commit(); }
        return (u.IsStaff, link);
    }

    /// <summary>Two ways in: an invitation link, or a password the administrator types now.</summary>
    public string GrantAccess(Scope s, User u, string password, string origin)
    {
        if (string.IsNullOrEmpty(password)) return Passwords.Usable(u.Password) ? "" : Invitation(s, u, origin);
        if (Passwords.Usable(u.Password)) throw new BookingException("Questo utente ha già un accesso: la sua password non viene cambiata. Usa Ripristina password.");
        ValidatePassword(password);
        u.Password = Passwords.Make(password); u.AuthVersion++; s.Update(u, "password", "auth_version");
        return "";
    }

    /// <summary>site.mail_ready: the business's SMTP or the program's.</summary>
    public static bool MailReady(Scope s, Tenant t) => !string.IsNullOrEmpty(t.Smtp) || s.Scalar<long>("SELECT count(*) FROM core_sitesettings WHERE smtp<>''") > 0;
}
