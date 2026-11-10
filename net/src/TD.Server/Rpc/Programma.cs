using System.Globalization;
using System.IO.Compression;
using System.Text;
using TD.Core.Data;
using TD.Core.Domain;
using TD.Core.Win;

namespace TD.Server.Rpc;

/// <summary>tdwin/stato.py: the pages that took more than a few seconds, written by the program itself.</summary>
public static class Slow
{
    static readonly object gate = new();
    static string Folder(Server s) => Path.Combine(s.Config.DataFolder, "log");
    /// <summary>One line, newest at the end: when, who saw it (programma or finestra), the page without its query, how long.</summary>
    public static void Note(Server s, string where, string what, double seconds)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Folder(s)); var path = Path.Combine(Folder(s), "lente.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, Path.Combine(Folder(s), "lente.1.log"), true);  // shortcut: one old file kept, enough for weeks
                File.AppendAllText(path, $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}\t{where}\t{(what.Length > 200 ? what[..200] : what)}\t{seconds.ToString("0.0", CultureInfo.InvariantCulture)}\n", Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    static List<string[]> Lines(Server s, int limit)
    {
        var rows = new List<string>();
        foreach (var name in new[] { "lente.1.log", "lente.log" }) { try { rows.AddRange(File.ReadAllLines(Path.Combine(Folder(s), name), Encoding.UTF8)); } catch (IOException) { } }
        return rows.Skip(Math.Max(0, rows.Count - limit)).Select(r => r.Split('\t')).Where(p => p.Length == 4).Reverse().ToList();
    }

    [Rpc("stato", Tenant = false, Staff = true)]
    public static object Page(Call c)
    {
        var since = DateTime.Now.AddHours(-24);
        var day = Lines(c.S, 500).Count(p => DateTime.TryParseExact(p[0], "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at >= since);
        return new { versione = Server.Version, giorno = day, righe = Lines(c.S, 200).Select(p => new { quando = p[0], dove = p[1] == "finestra" ? "Finestra" : "Programma", pagina = p[2], secondi = p[3] }) };
    }
    [Rpc("stato.log", Tenant = false, Staff = true)]
    public static IResult Download(Call c)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var p in Directory.Exists(Folder(c.S)) ? Directory.GetFiles(Folder(c.S), "*.log").OrderBy(x => x, StringComparer.Ordinal) : Enumerable.Empty<string>())
            {
                try { var data = File.ReadAllBytes(p); using var w = z.CreateEntry(Path.GetFileName(p), CompressionLevel.Optimal).Open(); w.Write(data.AsSpan(Math.Max(0, data.Length - 5_000_000))); } catch (IOException) { }
            }
        return Results.File(ms.ToArray(), "application/zip", $"log-td-booking-suite-{DateTime.Now:yyyy-MM-dd}.zip");
    }
    /// <summary>The window's own report: only from who is signed in, only a name and a number, nothing else is kept.</summary>
    [Rpc("stato.lento", Tenant = false)]
    public static object Report(Call c)
    {
        var page = c.Str("p"); if (page.Length > 200) page = page[..200];
        if (double.TryParse(c.Str("ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && page != "" && ms > 0 && ms < 3_600_000) Note(c.S, "finestra", page, ms / 1000);
        return new { };
    }
}

/// <summary>tdwin/postazioni.admin_view and rete_views.page: Postazioni and Rete e certificato, for the program's administrator.</summary>
public static class Programma
{
    [Rpc("postazioni", Tenant = false, Staff = true)]
    public static object Workstations(Call c) => WorkstationsData(c, null);
    static object WorkstationsData(Call c, string code)
    {
        using var g = c.S.Db.Global(); var allowed = c.S.Workstations.AllowedIds(g); var st = c.S.Rete.Apply();
        string Local(DateTime? t, string f = "dd/MM/yyyy HH:mm") => t.HasValue ? t.Value.ToLocalTime().ToString(f) : "";
        return new
        {
            standalone = c.S.Standalone, codice = code, minuti = TD.Core.Win.Workstations.CODE_MINUTES, limite = c.S.Workstations.Limit(), ufficio = c.S.Config.Origin, fuori = st.Origin, impronta = c.S.Rete.Fingerprint(),
            registrate = g.Query<WinPostazione>("SELECT * FROM tdwin_postazione WHERE token IS NOT NULL ORDER BY abbinata").Select(p => new { id = p.Id, nome = p.Nome, attiva = allowed.Contains(p.Id), abbinata = Local(p.Abbinata), accesso = Local(p.UltimoAccesso), ip = p.UltimoIp ?? "" }),
            attesa = g.Query<WinPostazione>("SELECT * FROM tdwin_postazione WHERE token IS NULL AND scadenza_codice>now() ORDER BY creata").Select(p => new { id = p.Id, nome = p.Nome, fino = Local(p.ScadenzaCodice, "HH:mm") }),
        };
    }
    [Rpc("postazioni.azione", Tenant = false, Staff = true, Fresh = true)]
    public static object WorkstationAction(Call c)
    {
        if (c.Str("azione") == "nuova")
        {
            try { var (_, code) = c.S.Workstations.NewCode(c.Str("nome")); return WorkstationsData(c, code); } catch (ArgumentException ex) { throw new BookingException(ex.Message); }
        }
        if (c.Id("id") is not Guid id) throw new BookingException("Postazione non trovata.");
        c.S.Workstations.Revoke(id);
        return new { testo = "Postazione revocata: per usarla di nuovo serve un nuovo codice." };
    }
    /// <summary>The local certification authority, for phones and browsers that should trust the program's address.</summary>
    [Rpc("postazioni.certificato", Tenant = false, Staff = true)]
    public static IResult Authority(Call c)
    {
        var path = Certs.CaPath(c.S.Config.DataFolder); if (!File.Exists(path)) throw new KeyNotFoundException();
        return Results.File(File.ReadAllBytes(path), "application/x-x509-ca-cert", "TD-Booking-Suite-CA.crt");
    }

    [Rpc("rete", Tenant = false, Staff = true)]
    public static object Network(Call c)
    {
        var r = c.S.Rete; var st = r.Apply(); var (served, own) = r.Served();
        return new
        {
            standalone = c.S.Standalone, online = c.S.Online, porta = c.S.Config.Port, ufficio = c.S.Config.Origin, fuori = st.Origin, indirizzo = st.Indirizzo, proxy = st.Proxy, avvisi = r.Warnings(st),
            proprio = own, caricato = st.Own, soggetto = served.Str("soggetto"), nomi = string.Join(", ", (served["nomi"] as System.Text.Json.Nodes.JsonArray).Select(n => n.ToString())), emittente = served.Str("emittente"),
            scadenza = Times.ParseAware(served.Str("scadenza"), out _)?.ToString("dd/MM/yyyy"), impronta = r.Fingerprint(),
            // where the installer puts the setup script
            script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "script", "setup-server.ps1")),
        };
    }
    [Rpc("rete.azione", Tenant = false, Staff = true, Fresh = true)]
    public static object NetworkAction(Call c)
    {
        var r = c.S.Rete;
        try
        {
            switch (c.Str("azione"))
            {
                case "indirizzi": r.SaveAddresses(c.Str("indirizzo"), c.Str("proxy")); return new { testo = "Indirizzi salvati: valgono da subito." };
                case "certificato":
                    {
                        byte[] File64(string key) { try { return Convert.FromBase64String(c.Str(key)); } catch (FormatException) { return Array.Empty<byte>(); } }
                        var cert = File64("certificato"); var key = File64("chiave");
                        if (cert.Length == 0) throw new BookingException("Scegli il file del certificato: .pfx, oppure .crt o .pem con la sua chiave.");
                        if (cert.Length > 200_000 || key.Length > 200_000) throw new BookingException("File troppo grande per un certificato.");
                        var info = r.Install(key.Length > 0 ? cert.Concat(new byte[] { (byte)'\n' }).Concat(key).ToArray() : cert, c.Str("password"));
                        return new { testo = $"Certificato per {info.Str("soggetto")} caricato: il server lo usa dalla prossima connessione." };
                    }
                case "interno": r.UseInternal(); return new { testo = "Il server usa di nuovo il certificato interno." };
            }
        }
        catch (IOException) { throw new BookingException("Non riesco a scrivere il certificato nella cartella del programma: riprova tra qualche secondo."); }
        throw new BookingException("Operazione non valida.");
    }
}
