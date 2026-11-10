using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TD.Core.Data;

namespace TD.Core.Win;

/// <summary>tdwin/certs.py and the certificate half of tdwin/rete.py: a certification authority made for this
/// installation signs the server certificate (EC P-256), limited by name constraints to this PC's names and private
/// addresses; renewed by itself a month before it expires. The files are the same PEM files as the Python's.</summary>
public static class Certs
{
    static readonly object Lock = new();
    static readonly (IPAddress Net, int Bits)[] PRIVATE =
    {
        (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("172.16.0.0"), 12), (IPAddress.Parse("192.168.0.0"), 16), (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("169.254.0.0"), 16), (IPAddress.Parse("100.64.0.0"), 10), (IPAddress.Parse("::1"), 128), (IPAddress.Parse("fc00::"), 7), (IPAddress.Parse("fe80::"), 10),
    };

    public static string Folder(string data) => Path.Combine(data, "certs");
    public static string CaPath(string data) => Path.Combine(Folder(data), "ca.crt");

    public static bool InNet(IPAddress a, IPAddress net, int bits)
    {
        if (a.AddressFamily != net.AddressFamily) return false;
        var x = a.GetAddressBytes(); var y = net.GetAddressBytes();
        for (int i = 0; i < bits; i++) if ((x[i / 8] >> (7 - i % 8) & 1) != (y[i / 8] >> (7 - i % 8) & 1)) return false;
        return true;
    }
    public static bool IsPrivate(IPAddress a) => PRIVATE.Any(p => InNet(a, p.Net, p.Bits));

    public static HashSet<string> OwnAddresses()
    {
        var found = new HashSet<string> { "127.0.0.1", "::1" };
        try { foreach (var a in Dns.GetHostAddresses(Dns.GetHostName())) found.Add(a.ToString().Split('%')[0]); } catch (SocketException) { }
        return found;
    }

    /// <summary>localhost, the configured names, this PC's name and addresses, lower case and once each.</summary>
    public static List<string> LocalNames(IEnumerable<string> extra)
    {
        string fqdn;
        try { fqdn = Dns.GetHostEntry(Dns.GetHostName()).HostName; } catch { fqdn = Dns.GetHostName(); }
        var all = new List<string> { "localhost", "127.0.0.1", "::1" }; all.AddRange(extra); all.Add(Dns.GetHostName()); all.Add(fqdn);
        all.AddRange(OwnAddresses().OrderBy(x => x, StringComparer.Ordinal));
        var seen = new List<string>();
        foreach (var n0 in all)
        {
            var n = (n0 ?? "").Trim().ToLowerInvariant().Split('%')[0];
            if (n != "" && !seen.Contains(n)) seen.Add(n);
        }
        return seen;
    }

    static string Host(string url) { try { return new Uri(url).Host; } catch { return ""; } }
    public static List<string> Configured(AppConfig c) => c.Addresses.Append(Host(c.Origin)).Where(n => !string.IsNullOrEmpty(n)).Select(n => n.ToLowerInvariant()).ToList();

    public static List<string> Names(AppConfig c)
    {
        var keep = Configured(c).ToHashSet();
        var outp = new List<string>();
        foreach (var n in LocalNames(Configured(c)))
        {
            if (!IPAddress.TryParse(n, out var a)) { outp.Add(n); continue; }
            if (keep.Contains(n) || IsPrivate(a)) outp.Add(n);
        }
        return outp;
    }

    // ---------- the name constraints of the authority (DER, as cryptography writes them)
    static byte[] NameConstraints(IEnumerable<string> dns, IEnumerable<(IPAddress, int)> nets)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                foreach (var d in dns)
                    using (w.PushSequence()) w.WriteCharacterString(UniversalTagNumber.IA5String, d, new Asn1Tag(TagClass.ContextSpecific, 2));
                foreach (var (net, bits) in nets)
                    using (w.PushSequence())
                    {
                        var addr = net.GetAddressBytes(); var mask = new byte[addr.Length];
                        for (int i = 0; i < bits; i++) mask[i / 8] |= (byte)(0x80 >> (i % 8));
                        w.WriteOctetString(addr.Concat(mask).ToArray(), new Asn1Tag(TagClass.ContextSpecific, 7));
                    }
            }
        }
        return w.Encode();
    }

    /// <summary>True when the authority's name constraints let it sign for this name (no constraints: any).</summary>
    public static bool Permitted(X509Certificate2 ca, string name)
    {
        var ext = ca.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.30");
        if (ext == null) return true;
        var r = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
        if (!r.HasData) return true;
        var tag = r.PeekTag();
        if (tag != new Asn1Tag(TagClass.ContextSpecific, 0, true)) return true;
        var permitted = r.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var isIp = IPAddress.TryParse(name, out var ip);
        while (permitted.HasData)
        {
            var sub = permitted.ReadSequence();
            var t = sub.PeekTag();
            if (t.TagValue == 2 && !isIp)
            {
                var v = sub.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2));
                if (name == v || name.EndsWith("." + v)) return true;
            }
            else if (t.TagValue == 7 && isIp)
            {
                var raw = sub.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                var half = raw.Length / 2; var addr = new IPAddress(raw[..half]); var mask = raw[half..];
                var bits = mask.Sum(b => Convert.ToString(b, 2).Count(ch => ch == '1'));
                if (InNet(ip, addr, bits)) return true;
            }
            else sub.ReadEncodedValue();
        }
        return false;
    }

    static void Write(string path, string text)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, true); return; }
            catch (IOException) when (i < 19) { Thread.Sleep(100); }
        }
    }

    static X509Certificate2 Authority(string data, AppConfig cfg, List<string> wanted, out ECDsa key)
    {
        Directory.CreateDirectory(Folder(data));
        var keyPath = Path.Combine(Folder(data), "ca.key");
        if (File.Exists(keyPath) && File.Exists(CaPath(data)))
        {
            try
            {
                var ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaPath(data)));
                key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(keyPath));
                var conf = Configured(cfg).ToHashSet();
                if (wanted.Where(conf.Contains).All(n => Permitted(ca, n)) && ca.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
                    return ca;
            }
            catch (CryptographicException) { }
        }
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName($"CN={Trunc("TD Booking Suite CA " + Dns.GetHostName(), 64)}, O=TD Booking Suite");
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var dns = new List<string>(); var ips = new List<(IPAddress, int)>();
        foreach (var n in wanted)
        {
            if (!IPAddress.TryParse(n, out var a)) { dns.Add(n); continue; }
            if (!IsPrivate(a)) ips.Add((a, a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
        }
        req.CertificateExtensions.Add(new X509Extension("2.5.29.30", NameConstraints(dns, PRIVATE.Concat(ips)), true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(3650 * 2));
        Write(keyPath, key.ExportPkcs8PrivateKeyPem());
        Write(CaPath(data), cert.ExportCertificatePem());
        return cert;
    }
    static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>The server certificate (with the authority after it) and its key, made or renewed when needed.</summary>
    public static (string Cert, string Key) Ensure(string data, AppConfig cfg)
    {
        lock (Lock)
        {
            var wanted = Names(cfg);
            var ca = Authority(data, cfg, wanted, out var caKey);
            wanted = wanted.Where(n => Permitted(ca, n)).ToList();
            var certPath = Path.Combine(Folder(data), "server.crt"); var keyPath = Path.Combine(Folder(data), "server.key");
            var caSki = new X509SubjectKeyIdentifierExtension(new PublicKey(caKey), false).SubjectKeyIdentifier;
            if (File.Exists(certPath) && File.Exists(keyPath))
            {
                try
                {
                    var current = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                    var san = current.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                    var have = san == null ? new HashSet<string>() : san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(i => i.ToString())).Select(x => x.ToLowerInvariant()).ToHashSet();
                    var aki = current.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier?.ToArray();
                    var signedBy = aki != null && Convert.ToHexString(aki) == caSki;
                    if (current.NotAfter.ToUniversalTime() - DateTime.UtcNow > TimeSpan.FromDays(30) && wanted.All(have.Contains) && signedBy) return (certPath, keyPath);
                }
                catch (CryptographicException) { }
            }
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var host = Host(cfg.Origin); if (host == "") host = "localhost";
            var req = new CertificateRequest(new X500DistinguishedName("CN=" + Trunc(host, 64)), key, HashAlgorithmName.SHA256);
            var sb = new SubjectAlternativeNameBuilder();
            foreach (var n in wanted) { if (IPAddress.TryParse(n, out var a)) sb.AddIpAddress(a); else sb.AddDnsName(n); }
            req.CertificateExtensions.Add(sb.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(Convert.FromHexString(caSki)));
            var now = DateTimeOffset.UtcNow;
            var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7f;
            var cert = req.Create(ca.SubjectName, X509SignatureGenerator.CreateForECDsa(caKey), now.AddDays(-1), now.AddDays(397), serial);
            Write(keyPath, key.ExportPkcs8PrivateKeyPem());
            Write(certPath, cert.ExportCertificatePem() + "\n" + ca.ExportCertificatePem() + "\n");
            return (certPath, keyPath);
        }
    }

    /// <summary>rete.paths: the uploaded certificate while it is valid and usable, otherwise the internal one.</summary>
    public static (string, string) Paths(string data, Db db, AppConfig cfg = null)
    {
        cfg ??= AppConfig.Load(data);
        string kind;
        using (var s = db.Global()) kind = s.Scalar<string>("SELECT certificato FROM tdwin_rete WHERE id=1") ?? "interno";
        if (kind == "proprio")
        {
            var own = Path.Combine(Folder(data), "proprio.pem");
            try
            {
                var pem = File.ReadAllText(own);
                var c = X509Certificate2.CreateFromPem(pem, pem);
                if (c.NotAfter.ToUniversalTime() > DateTime.UtcNow && c.HasPrivateKey) return (own, own);
            }
            catch (Exception) { }
        }
        return Ensure(data, cfg);
    }

    public static string Fingerprint(string data)
    {
        if (!File.Exists(CaPath(data))) return "";
        var raw = Convert.ToHexString(SHA256.HashData(X509Certificate2.CreateFromPem(File.ReadAllText(CaPath(data))).RawData));
        return string.Join(" ", Enumerable.Range(0, raw.Length / 4).Select(i => raw.Substring(i * 4, 4)));
    }
}
