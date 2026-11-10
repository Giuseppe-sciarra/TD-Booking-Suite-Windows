using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Domain;
using TD.Core.Win;

namespace TD.Server.Web;

/// <summary>tdwin/rete.py: how the server is reached from outside: the public address (the one of the links in the
/// emails), the trusted proxies (from them the visitor's real address), the certificate the server shows.</summary>
public sealed class Rete(Server s)
{
    public sealed record State(string Origin, List<(IPAddress Net, int Bits)> Proxies, bool Own, JsonObject Info, string Indirizzo, string Proxy);
    State applied; DateTime at;
    string Folder => Certs.Folder(s.Config.DataFolder);
    public string OwnPath => Path.Combine(Folder, "proprio.pem");

    /// <summary>apply(): the address and the proxies as the administrator set them; read again once a minute.</summary>
    public State Apply(bool force = false)
    {
        if (!force && applied != null && DateTime.UtcNow - at < TimeSpan.FromSeconds(60)) return applied;
        using var g = s.Db.Global(); g.Execute("INSERT INTO tdwin_rete (id, indirizzo, proxy, certificato, info, aggiornata) VALUES (1, '', '', 'interno', '{}'::jsonb, now()) ON CONFLICT (id) DO NOTHING");
        var r = g.One<(string Indirizzo, string Proxy, string Certificato, string Info)>("SELECT indirizzo, proxy, certificato, info::text FROM tdwin_rete WHERE id=1"); g.Commit();
        List<(IPAddress, int)> nets; try { nets = Proxies(r.Proxy); } catch (BookingException) { nets = new(); }
        applied = new State(r.Indirizzo ?? "", nets, r.Certificato == "proprio", Json.Obj(r.Info), r.Indirizzo ?? "", r.Proxy ?? ""); at = DateTime.UtcNow;
        return applied;
    }
    /// <summary>settings.PUBLIC_ORIGIN: the public address when set, otherwise the office one.</summary>
    public string PublicOrigin => Apply().Origin is { Length: > 0 } o ? o : s.Config.Origin;

    /// <summary>origin(): https://name[:port] from what the administrator typed, "" to clear.</summary>
    public static string Origin(string text)
    {
        text = (text ?? "").Trim().TrimEnd('/'); if (text == "") return "";
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) throw new BookingException("Indirizzo non valido: scrivilo come https://booking.esempio.it");
        if (u.Scheme != "https") throw new BookingException("L'indirizzo pubblico deve essere https://: da fuori il collegamento va sempre cifrato.");
        if (u.AbsolutePath != "/" || u.Query != "" || u.UserInfo != "" || u.Host == "") throw new BookingException("Solo il nome e la porta, senza percorso: per esempio https://booking.esempio.it");
        var host = u.Host.Trim('[', ']').ToLowerInvariant(); string name;
        if (IPAddress.TryParse(host, out _)) name = host.Contains(':') ? $"[{host}]" : host;
        else if (host.Length <= 253 && Regex.IsMatch(host, @"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z")) name = host;
        else throw new BookingException("Nome del server non valido.");
        return "https://" + name + (u.Port != 443 ? $":{u.Port}" : "");
    }
    /// <summary>proxies(): trusted proxies, one address or network per line. Strict: 192.168.1.10/24, an address copied
    /// with its netmask, would trust the whole office network.</summary>
    public static List<(IPAddress Net, int Bits)> Proxies(string text)
    {
        var outp = new List<(IPAddress, int)>();
        foreach (var line in Regex.Split(text ?? "", @"[\s,;]+").Where(l => l != ""))
        {
            var parts = line.Split('/'); var ok = parts.Length <= 2 && IPAddress.TryParse(parts[0], out var ip);
            var bits = 0;
            if (ok) { ip = IPAddress.Parse(parts[0]); var max = ip.GetAddressBytes().Length * 8; bits = max; ok = parts.Length == 1 || int.TryParse(parts[1], out bits) && bits >= 0 && bits <= max; if (ok) ok = HostBitsZero(ip, bits); if (ok) outp.Add((ip, bits)); }
            if (!ok) throw new BookingException($"«{(line.Length > 60 ? line[..60] : line)}»: scrivi l'IP del proxy (192.168.1.10) o una rete intera (192.168.1.0/24).");
        }
        return outp;
    }
    static bool HostBitsZero(IPAddress ip, int bits)
    {
        var b = ip.GetAddressBytes();
        for (int i = bits; i < b.Length * 8; i++) if ((b[i / 8] & (0x80 >> (i % 8))) != 0) return false;
        return true;
    }
    static string Show((IPAddress Net, int Bits) n) => n.Bits == n.Net.GetAddressBytes().Length * 8 ? n.Net.ToString() : $"{n.Net}/{n.Bits}";

    /// <summary>client(): the visitor's address: from a trusted proxy the one it appends to X-Forwarded-For (anything to
    /// its left is written by the visitor and can be false). IIS can add the port too.</summary>
    public string Client(HttpContext h)
    {
        var remote = h.Connection.RemoteIpAddress; if (remote == null) return "unknown";
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (!Apply().Proxies.Any(n => n.Net.AddressFamily == remote.AddressFamily && Certs.InNet(remote, n.Net, n.Bits))) return remote.ToString();
        var last = h.Request.Headers["X-Forwarded-For"].ToString().Split(',')[^1].Trim();
        if (last.StartsWith('[')) last = last[1..].Split(']')[0]; else if (last.Count(ch => ch == ':') == 1) last = last.Split(':')[0];
        return IPAddress.TryParse(last, out var ip) ? ip.ToString() : remote.ToString();
    }

    // ---------- certificate
    static JsonObject Describe(X509Certificate2 c)
    {
        var names = new JsonArray();
        foreach (var ext in c.Extensions.OfType<X509SubjectAlternativeNameExtension>()) { foreach (var n in ext.EnumerateDnsNames()) names.Add((JsonNode)n); foreach (var ip in ext.EnumerateIPAddresses()) names.Add((JsonNode)ip.ToString()); }
        string Part(X500DistinguishedName dn, string oid) => dn.EnumerateRelativeDistinguishedNames().FirstOrDefault(r => r.GetSingleElementType().Value == oid)?.GetSingleElementValue();
        return new JsonObject
        {
            ["soggetto"] = Part(c.SubjectName, "2.5.4.3") ?? c.Subject, ["emittente"] = Part(c.IssuerName, "2.5.4.10") ?? Part(c.IssuerName, "2.5.4.3") ?? c.Issuer, ["nomi"] = names,
            ["dal"] = Times.PyIso(c.NotBefore.ToUniversalTime()), ["scadenza"] = Times.PyIso(c.NotAfter.ToUniversalTime()), ["autofirmato"] = c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData),
        };
    }

    /// <summary>read(): the certificate chain and the private key from a .pfx/.p12, or from PEM text with the certificates
    /// and the key (one file or two put together): the server's certificate first, then the authorities, then the key.</summary>
    static (string Pem, JsonObject Info) Read(byte[] data, string password)
    {
        X509Certificate2 leaf; var chain = new X509Certificate2Collection();
        var text = Encoding.Latin1.GetString(data);
        if (text.Contains("-----BEGIN"))
        {
            try { chain.ImportFromPem(text); } catch (CryptographicException) { }
            if (chain.Count == 0) throw new BookingException("Nel file non c'è un certificato in formato PEM.");
            var block = Regex.Match(text, @"-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----.+?-----END (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----", RegexOptions.Singleline);
            if (!block.Success) throw new BookingException("Manca la chiave privata: carica anche il file della chiave (.key), oppure il .pfx.");
            var encrypted = block.Value.Contains("ENCRYPTED");
            if (encrypted && string.IsNullOrEmpty(password)) throw new BookingException("La chiave privata è protetta: scrivi la sua password.");
            leaf = null; var readable = false;
            foreach (var c in chain)
            {
                var pem = c.ExportCertificatePem();
                try { leaf = encrypted ? X509Certificate2.CreateFromEncryptedPem(pem, block.Value, password) : X509Certificate2.CreateFromPem(pem, block.Value); readable = true; break; }
                catch (ArgumentException) { readable = true; }  // the key is fine, it is not this certificate's
                catch (CryptographicException) { }
            }
            if (leaf == null) throw new BookingException(readable ? "La chiave privata non è quella di questo certificato." : "Chiave privata non leggibile o password sbagliata.");
        }
        else
        {
            try { chain.Import(data, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet); } catch (CryptographicException) { throw new BookingException("File .pfx non leggibile o password sbagliata."); }
            leaf = chain.FirstOrDefault(c => c.HasPrivateKey) ?? throw new BookingException("Nel file .pfx mancano il certificato o la chiave privata.");
        }
        var now = DateTime.UtcNow;
        if (leaf.NotAfter.ToUniversalTime() <= now) throw new BookingException($"Il certificato è scaduto il {leaf.NotAfter.ToUniversalTime():dd/MM/yyyy}.");
        if (leaf.NotBefore.ToUniversalTime() > now) throw new BookingException($"Il certificato vale solo dal {leaf.NotBefore.ToUniversalTime():dd/MM/yyyy}.");
        string key = leaf.GetRSAPrivateKey() is RSA rsa ? rsa.ExportPkcs8PrivateKeyPem() : leaf.GetECDsaPrivateKey() is ECDsa ec ? ec.ExportPkcs8PrivateKeyPem() : throw new BookingException("Chiave privata non leggibile o password sbagliata.");
        var all = new StringBuilder(leaf.ExportCertificatePem()).Append('\n');
        foreach (var c in chain.Where(c => !c.RawData.AsSpan().SequenceEqual(leaf.RawData))) all.Append(c.ExportCertificatePem()).Append('\n');
        return (all.Append(key).Append('\n').ToString(), Describe(leaf));
    }

    /// <summary>install(): check the files, prove that the server can load them, then make them the certificate it shows.</summary>
    public JsonObject Install(byte[] data, string password)
    {
        var (pem, info) = Read(data, password ?? "");
        try { using var test = X509Certificate2.CreateFromPem(pem, pem); if (!test.HasPrivateKey) throw new CryptographicException(); }
        catch (CryptographicException) { throw new BookingException("Il server HTTPS non accetta questo certificato: prova a esportarlo di nuovo come .pfx."); }
        Directory.CreateDirectory(Folder); var work = OwnPath + "." + Guid.NewGuid().ToString("N")[..8];
        try { File.WriteAllText(work, pem); File.Move(work, OwnPath, true); } finally { if (File.Exists(work)) File.Delete(work); }
        using (var g = s.Db.Global()) { g.Execute("UPDATE tdwin_rete SET certificato='proprio', info=@i::jsonb, aggiornata=now() WHERE id=1", new { i = info.ToJsonString() }); g.Commit(); }
        Apply(true); return info;
    }
    public void UseInternal() { using (var g = s.Db.Global()) { g.Execute("UPDATE tdwin_rete SET certificato='interno', aggiornata=now() WHERE id=1"); g.Commit(); } Apply(true); }
    public void SaveAddresses(string origin, string proxies)
    {
        var o = Origin(origin); var p = string.Join("\n", Proxies(proxies).Select(Show)); Apply();
        using (var g = s.Db.Global()) { g.Execute("UPDATE tdwin_rete SET indirizzo=@o, proxy=@p, aggiornata=now() WHERE id=1", new { o, p }); g.Commit(); }
        Apply(true);
    }

    /// <summary>What the server shows now, described; and whether it is the uploaded one.</summary>
    public (JsonObject Served, bool Own) Served()
    {
        var (crt, _) = Certs.Paths(s.Config.DataFolder, s.Db, s.Config); var pem = File.ReadAllText(crt); var c = new X509Certificate2Collection(); c.ImportFromPem(pem);
        return (Describe(c[0]), Path.GetFullPath(crt) == Path.GetFullPath(OwnPath));
    }
    /// <summary>fingerprint(): what the window shows at the first connection: the internal authority's, or the uploaded certificate's own.</summary>
    public string Fingerprint()
    {
        var (crt, _) = Certs.Paths(s.Config.DataFolder, s.Db, s.Config);
        if (Path.GetFullPath(crt) != Path.GetFullPath(OwnPath)) return Certs.Fingerprint(s.Config.DataFolder);
        var c = new X509Certificate2Collection(); c.ImportFromPem(File.ReadAllText(crt)); var raw = Convert.ToHexString(SHA256.HashData(c[0].RawData));
        return string.Join(" ", Enumerable.Range(0, raw.Length / 4).Select(i => raw.Substring(i * 4, 4)));
    }
    static bool Covers(IEnumerable<string> names, string host)
    {
        host = host.ToLowerInvariant();
        return names.Select(n => n.ToLowerInvariant()).Any(n => n == host || n.StartsWith("*.") && host.Count(ch => ch == '.') == n.Count(ch => ch == '.') && host.EndsWith(n[1..]));
    }
    /// <summary>warnings(): what does not add up between the address, the certificate and the proxies.</summary>
    public List<string> Warnings(State st)
    {
        var outp = new List<string>(); var host = st.Origin != "" ? new Uri(st.Origin).Host : "";
        if (st.Own)
        {
            var until = Times.ParseAware(st.Info.Str("scadenza"), out _) ?? DateTime.UtcNow; var days = (int)Math.Floor((until - DateTime.UtcNow).TotalDays);
            if (days < 0) outp.Add($"Il certificato caricato è scaduto il {until:dd/MM/yyyy}: il server è tornato al certificato interno. Carica quello rinnovato.");
            else if (days <= 30) outp.Add($"Il certificato caricato scade il {until:dd/MM/yyyy}: carica quello rinnovato prima di allora.");
            if (host != "" && !Covers((st.Info["nomi"] as JsonArray ?? new JsonArray()).Select(n => n.ToString()), host)) outp.Add($"Il certificato caricato non vale per {host}: da fuori browser e telefoni daranno un avviso.");
            if (Json.Truthy(st.Info["autofirmato"])) outp.Add("Il certificato caricato è autofirmato: i browser lo segnalano finché non viene installato tra quelli attendibili; il programma per le postazioni lo accetta dopo il controllo dell'impronta.");
        }
        else if (st.Origin != "" && st.Proxies.Count == 0)
            outp.Add("Da fuori il certificato interno non è riconosciuto dai browser: o metti davanti un proxy con il suo certificato e lo scrivi tra i proxy fidati, o carichi qui un certificato per " + host + ". Il programma per le postazioni funziona lo stesso, dopo il controllo dell'impronta.");
        return outp;
    }
}
