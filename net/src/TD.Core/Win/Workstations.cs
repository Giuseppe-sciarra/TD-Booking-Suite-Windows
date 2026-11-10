using System.Security.Cryptography;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Core.Win;

/// <summary>tdwin/postazioni.py: every PC that opens the program from the network is registered once with a 6-digit code
/// made on the Postazioni page; the licence says how many may be registered.</summary>
public sealed class Workstations(Db db, Licensing licensing)
{
    public const int CODE_MINUTES = 15, MAX_TRIES = 50;
    public static string CodeHash(string code) => Crypto.TokenHash("postazione:" + code);
    public int Limit() => licensing.Apply().Postazioni;

    const string Paired = "token IS NOT NULL";
    const string Pending = "token IS NULL AND scadenza_codice > now() AND tentativi < 50";

    /// <summary>Over the licence's limit, the workstations registered first keep working.</summary>
    public HashSet<Guid> AllowedIds(Scope s) =>
        s.Query<Guid>($"SELECT id FROM tdwin_postazione WHERE {Paired} ORDER BY abbinata LIMIT @l", new { l = Limit() }).ToHashSet();

    public WinPostazione Current(string token, string address)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var s = db.Global();
        var found = s.One<WinPostazione>($"SELECT * FROM tdwin_postazione WHERE {Paired} AND token=@t", new { t = Crypto.TokenHash(token) });
        if (found == null || !AllowedIds(s).Contains(found.Id)) return null;
        if (found.UltimoAccesso == null || DateTime.UtcNow - found.UltimoAccesso > TimeSpan.FromMinutes(5))
        {
            s.Execute("UPDATE tdwin_postazione SET ultimo_accesso=now(), ultimo_ip=@ip::inet WHERE id=@id", new { id = found.Id, ip = string.IsNullOrEmpty(address) ? null : address });
            s.Commit();
        }
        return found;
    }

    public (WinPostazione, string Code) NewCode(string name)
    {
        name = string.Join(" ", (name ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 80) name = name[..80];
        if (name == "") throw new ArgumentException("Dai un nome alla postazione, per esempio Reception o PC di Marco.");
        using var s = db.Global();
        s.Execute("LOCK TABLE tdwin_postazione IN SHARE ROW EXCLUSIVE MODE");
        var used = s.Scalar<long>($"SELECT count(*) FROM tdwin_postazione WHERE ({Paired}) OR ({Pending})");
        if (used >= Limit()) throw new ArgumentException($"La licenza consente {Limit()} postazioni in rete e sono tutte usate: revocane una o chiedi una licenza con più postazioni.");
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        var item = s.Insert(new WinPostazione { Nome = name, Codice = CodeHash(code), ScadenzaCodice = DateTime.UtcNow.AddMinutes(CODE_MINUTES), Creata = DateTime.UtcNow });
        s.Commit();
        return (item, code);
    }

    /// <summary>The token for the workstation waiting for this code.</summary>
    public (string Token, WinPostazione) Pair(string code)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        using var s = db.Global();
        var waiting = s.Query<WinPostazione>($"SELECT * FROM tdwin_postazione WHERE {Pending} FOR UPDATE").ToList();
        var match = code.Length == 6 ? waiting.FirstOrDefault(x => x.Codice == CodeHash(code)) : null;
        string error = null, token = null;
        if (match == null)
        {
            s.Execute($"UPDATE tdwin_postazione SET tentativi=tentativi+1 WHERE {Pending}");
            error = "Codice non valido o scaduto: fanne generare uno nuovo dalla pagina Postazioni.";
        }
        else if (s.Scalar<long>($"SELECT count(*) FROM tdwin_postazione WHERE {Paired}") >= Limit())
            error = $"La licenza consente {Limit()} postazioni in rete e sono tutte usate.";
        else
        {
            token = Bookings.TokenUrlSafe(32);
            s.Execute("UPDATE tdwin_postazione SET token=@t, codice='', scadenza_codice=NULL, abbinata=now() WHERE id=@id", new { t = Crypto.TokenHash(token), id = match.Id });
        }
        s.Commit();
        if (error != null) throw new ArgumentException(error);
        return (token, match);
    }

    public void Revoke(Guid id) { using var s = db.Global(); s.Execute("DELETE FROM tdwin_postazione WHERE id=@id", new { id }); s.Commit(); }
}
