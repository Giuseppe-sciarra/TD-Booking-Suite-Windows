using System.Diagnostics;
using System.Formats.Tar;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentFTP;
using Npgsql;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using TD.Core;
using TD.Core.Data;
using TD.Core.Win;

namespace TD.Server.Work;

/// <summary>tdwin/backup.py: the database, the keys that open it, the licence and the installation id, in one file
/// encrypted with the backup password (same format as the Python program: its files open here and the other way round).
/// The worker writes the file in the data folder every night (or right away with "Esegui ora") and copies it to each
/// active destination: a folder or external disk, a network folder (NAS), FTP, FTPS or SFTP. Each keeps the last N copies.</summary>
public sealed class Backup(Server s)
{
    static readonly byte[] MAGIC = Encoding.ASCII.GetBytes("TDBS-WINBACKUP-1\n");
    const int CHUNK = 1 << 20;
    public const string VERIFIED = "verificato da Windows";

    /// <summary>The installation's code in the file names: installations sharing a NAS or FTP folder keep their own copies.</summary>
    public string Prefix => "tdbs-" + Licensing.Normalize(s.Licensing.InstallCode()).ToLowerInvariant();
    public bool Own(string name) => name.StartsWith(Prefix + "-") && name.EndsWith(".tdbk");
    public static string LocalFolder(string data) => Path.Combine(data, "backup");
    public string Folder => LocalFolder(s.Config.DataFolder);
    public static string WorkRoot(string data) { var root = Path.Combine(data, "tmp"); Directory.CreateDirectory(root); return root; }
    static string WorkDir(string data) { var d = Path.Combine(WorkRoot(data), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    /// <summary>Unencrypted leftovers of a backup or restore cut short: the worker removes them when it starts. The folder itself stays.</summary>
    public void CleanWork()
    {
        var root = Path.Combine(s.Config.DataFolder, "tmp"); if (!Directory.Exists(root)) return;
        foreach (var d in Directory.GetDirectories(root)) { try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        foreach (var f in Directory.GetFiles(root)) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // encryption: header, nonce, AES-256-GCM stream, tag; the key comes from the backup password and the header's salt
    public static byte[] Derive(string password, byte[] salt) => SCrypt.Generate(Encoding.UTF8.GetBytes(password), salt, 1 << 15, 8, 1, 32);

    public WinImpostazionibackup Row()
    {
        using var g = s.Db.Global();
        g.Execute("INSERT INTO tdwin_impostazionibackup (id, attivo, ora, conserva_locali, chiave, sale) VALUES (1, false, '01:30', 7, '', '') ON CONFLICT (id) DO NOTHING");
        var row = g.One<WinImpostazionibackup>("SELECT * FROM tdwin_impostazionibackup WHERE id=1"); g.Commit(); return row;
    }
    void Exec(string sql, object args = null) { using var g = s.Db.Global(); g.Execute(sql, args); g.Commit(); }

    public void SetPassword(string password)
    {
        if (password.Length < 12) throw new ArgumentException("La password dei backup deve avere almeno 12 caratteri.");
        var salt = RandomNumberGenerator.GetBytes(16); Row();
        Exec("UPDATE tdwin_impostazionibackup SET sale=@sale, chiave=@chiave, password_impostata=now() WHERE id=1",
            new { sale = Crypto.B64(salt), chiave = s.Crypto.Protect(JsonValue.Create(Crypto.B64(Derive(password, salt))), "backup-key") });
    }

    static void Pump(GcmBlockCipher gcm, Stream src, Stream dst, long count)
    {
        var buf = new byte[CHUNK]; var enc = new byte[CHUNK + 64];
        while (count > 0)
        {
            var n = src.Read(buf, 0, (int)Math.Min(CHUNK, count)); if (n <= 0) throw new EndOfStreamException();
            count -= n; dst.Write(enc, 0, gcm.ProcessBytes(buf, 0, n, enc, 0));
        }
        dst.Write(enc, 0, gcm.DoFinal(enc, 0));
    }

    public static void Encrypt(string source, string dest, byte[] key, byte[] salt)
    {
        var header = MAGIC.Concat(salt).ToArray(); var nonce = RandomNumberGenerator.GetBytes(12);
        var gcm = new GcmBlockCipher(new AesEngine()); gcm.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, header));
        using var src = File.OpenRead(source); using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
        dst.Write(header); dst.Write(nonce); Pump(gcm, src, dst, src.Length);
    }

    public static byte[] ReadSalt(string path)
    {
        var head = new byte[MAGIC.Length + 16]; int got;
        using (var f = File.OpenRead(path)) got = f.ReadAtLeast(head, head.Length, false);
        if (got < head.Length || !head.AsSpan(0, MAGIC.Length).SequenceEqual(MAGIC)) throw new ArgumentException("Non è un backup di TD Booking Suite per Windows.");
        return head[MAGIC.Length..];
    }

    /// <summary>Writes dest only when the password and every byte of the file are right.</summary>
    public static void Decrypt(string source, string dest, byte[] key)
    {
        var size = new FileInfo(source).Length; var start = MAGIC.Length + 16;
        if (size < start + 12 + 16) throw new ArgumentException("Backup troncato o non valido.");
        var temporary = dest + ".part";
        using (var src = File.OpenRead(source))
        {
            var header = new byte[start]; var nonce = new byte[12]; src.ReadExactly(header); src.ReadExactly(nonce);
            var gcm = new GcmBlockCipher(new AesEngine()); gcm.Init(false, new AeadParameters(new KeyParameter(key), 128, nonce, header));
            try { using var dst = File.Create(temporary); Pump(gcm, src, dst, size - start - 12); }  // the tag is the file's last 16 bytes
            catch (InvalidCipherTextException) { File.Delete(temporary); throw new ArgumentException("Password dei backup errata, oppure il file è danneggiato."); }
        }
        File.Move(temporary, dest, true);
    }

    // the database, with the worker's superuser: the web service cannot read db-admin.json
    static string Tool(AppConfig c, string name) => c.PgBin != "" ? Path.Combine(c.PgBin, name) : name;
    public static void Pg(AppConfig c, Admin admin, string tool, params string[] args)
    {
        var db = new NpgsqlConnectionStringBuilder(AppConfig.ToNpgsql(admin.Url));
        var info = new ProcessStartInfo(Tool(c, tool)) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PGPASSWORD"] = db.Password ?? ""; info.Environment["PGHOST"] = string.IsNullOrEmpty(db.Host) ? "127.0.0.1" : db.Host; info.Environment["PGPORT"] = db.Port.ToString(); info.Environment["PGUSER"] = db.Username;
        using var p = Process.Start(info); var err = p.StandardError.ReadToEndAsync(); _ = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(3600_000)) { p.Kill(true); throw new InvalidOperationException(tool + ": non ha finito in un'ora"); }
        if (p.ExitCode != 0) { var last = err.Result.Trim().Split('\n').LastOrDefault(l => l.Trim() != "")?.Trim() ?? "errore"; throw new InvalidOperationException($"{tool}: {(last.Length > 300 ? last[..300] : last)}"); }
    }
    static string DbName(AppConfig c) => new NpgsqlConnectionStringBuilder(AppConfig.ToNpgsql(c.DatabaseUrl)).Database;

    void Bundle(string work, string dest)
    {
        var info = new JsonObject { ["versione"] = Server.Version, ["creato"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz"), ["pc"] = Environment.MachineName, ["install_id"] = s.Config.InstallId };
        var secrets = new JsonObject { ["DJANGO_SECRET_KEY"] = s.Config.SecretKey, ["TDBS_KEYS"] = s.Config.KeysJson, ["TDBS_ACTIVE_KEY"] = s.Config.ActiveKey };
        using var file = File.Create(dest); using var tar = new TarWriter(file, TarEntryFormat.Pax, false);
        tar.WriteEntry(Path.Combine(work, "database.dump"), "database.dump");
        foreach (var (name, data) in new[] { ("info.json", info), ("segreti.json", secrets) })
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))), ModificationTime = DateTimeOffset.UtcNow });
        if (File.Exists(s.Licensing.LicencePath)) tar.WriteEntry(s.Licensing.LicencePath, "licenza.txt");
    }

    public static string Describe(Exception ex)
    {
        var text = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
        SocketException socket = null; var timeout = false;
        for (var e = ex; e != null && socket == null; e = e is AggregateException many ? many.InnerExceptions.FirstOrDefault() : e.InnerException) { socket = e as SocketException; timeout |= e is TimeoutException; }
        // FluentFTP tries every address and then says only this
        if (socket == null && !timeout && text == "Failed to connect to host.") text = "collegamento non riuscito: controlla indirizzo e porta";
        if (timeout || socket?.SocketErrorCode == SocketError.TimedOut) text = "il server non risponde";
        else if (socket?.SocketErrorCode == SocketError.ConnectionRefused) text = "collegamento rifiutato: controlla indirizzo e porta";
        else if (socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain) text = "indirizzo del server sconosciuto";
        else if (ex is FluentFTP.Exceptions.FtpCommandException or FluentFTP.Exceptions.FtpAuthenticationException) text = "il server FTP rifiuta: " + text;
        return text.Length > 300 ? text[..300] : text;
    }

    // destinations
    string Secret(WinDestinazione d) => d.Password != "" ? (string)s.Crypto.Reveal(d.Password, "backup-dest:" + d.Id) : "";
    public string SealSecret(WinDestinazione d, string password) => password != "" ? s.Crypto.Protect(JsonValue.Create(password), "backup-dest:" + d.Id) : "";

    /// <summary>The first connection records the server's key or certificate; later ones must show the same.</summary>
    void Pin(WinDestinazione d, string seen)
    {
        if (d.Impronta == "") { d.Impronta = seen; Exec("UPDATE tdwin_destinazione SET impronta=@seen WHERE id=@id", new { seen, id = d.Id }); }
        else if (d.Impronta != seen) throw new ArgumentException("il server ha un'altra chiave o un altro certificato rispetto al primo collegamento. Se lo hai cambiato tu, azzera l'impronta della destinazione");
    }

    interface ITarget : IDisposable { void Put(string local, string name); List<string> List(); void Remove(string name); }

    sealed class FolderTarget : ITarget
    {
        readonly string root; readonly Backup b;
        public FolderTarget(Backup b, string path) { this.b = b; root = path; Directory.CreateDirectory(root); }
        public void Put(string local, string name) { File.Copy(local, Path.Combine(root, name + ".part"), true); File.Move(Path.Combine(root, name + ".part"), Path.Combine(root, name), true); }
        public List<string> List() => Directory.GetFiles(root, "tdbs-*.tdbk").Select(Path.GetFileName).Where(b.Own).ToList();
        public void Remove(string name) => File.Delete(Path.Combine(root, name));
        public void Dispose() { }
    }

    public static string Unc(string path) => @"\\" + path.Trim().Replace('/', '\\').TrimStart('\\').TrimEnd('\\');

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NetResource { public int Scope, Type, DisplayType, Usage; public string LocalName, RemoteName, Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetAddConnection2(ref NetResource resource, string password, string user, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetCancelConnection2(string name, int flags, bool force);

    /// <summary>A network folder \\nas\cartella with its own user, without mapping a drive in Windows.</summary>
    ITarget Network(WinDestinazione d)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Le cartelle di rete si usano solo da Windows.");
        var root = Unc(d.Percorso); var share = string.Join("\\", root.Split('\\').Take(4));  // \\server\share
        if (d.Utente != "")
        {
            // shortcut: Windows' own SMB client, always on port 445; a NAS on another port needs a library
            var r = new NetResource { Type = 1, RemoteName = share }; var code = WNetAddConnection2(ref r, Secret(d), d.Utente, 0);
            if (code == 1219) { WNetCancelConnection2(share, 0, true); code = WNetAddConnection2(ref r, Secret(d), d.Utente, 0); }  // connected before with another user
            if (code != 0) throw new IOException(new System.ComponentModel.Win32Exception(code).Message);
        }
        return new FolderTarget(this, root);
    }

    sealed class FtpTarget : ITarget
    {
        readonly FtpClient ftp; readonly Backup b;
        public FtpTarget(Backup b, WinDestinazione d)
        {
            this.b = b; var tls = d.Tipo == "ftps"; string seen = null; var refused = false;
            ftp = new FtpClient(d.Host, d.Utente != "" ? d.Utente : "anonymous", b.Secret(d), d.Porta ?? 21);
            ftp.Config.ConnectTimeout = ftp.Config.ReadTimeout = ftp.Config.DataConnectionConnectTimeout = ftp.Config.DataConnectionReadTimeout = 60000;
            ftp.Config.EncryptionMode = tls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None; ftp.Config.DataConnectionEncryption = tls;
            // a certificate Windows would trust (Let's Encrypt, renewed every few months) needs no pin; a self-signed one,
            // as on many NAS, is pinned at the first connection. Once recognised by Windows, never pinned later: a
            // certificate that stops being recognised may be someone in between, who must not receive the password
            ftp.ValidateCertificate += (_, e) =>
            {
                if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None) { e.Accept = true; seen ??= VERIFIED; return; }
                if (d.Impronta == VERIFIED) { refused = true; e.Accept = false; return; }
                seen = "sha256 " + Convert.ToHexString(SHA256.HashData(e.Certificate.GetRawCertData())).ToLowerInvariant();
                e.Accept = d.Impronta == "" || d.Impronta == seen;
            };
            try { ftp.Connect(); }
            catch (Exception) when (refused) { ftp.Dispose(); throw new ArgumentException("il certificato del server non è più riconosciuto da Windows, mentre prima lo era: qualcuno potrebbe essersi messo in mezzo. Se lo hai cambiato tu, azzera l'impronta della destinazione"); }
            catch (Exception) when (seen != null && seen != VERIFIED && d.Impronta != "" && d.Impronta != seen) { ftp.Dispose(); b.Pin(d, seen); throw; }
            catch { ftp.Dispose(); throw; }
            if (tls && seen != null && (seen != VERIFIED || d.Impronta == "")) b.Pin(d, seen);
            var path = d.Percorso != "" ? d.Percorso : "/";
            foreach (var part in (path.StartsWith('/') ? new[] { "/" } : Array.Empty<string>()).Concat(path.Split('/', StringSplitOptions.RemoveEmptyEntries)))
            {
                try { ftp.SetWorkingDirectory(part); }
                catch (FluentFTP.Exceptions.FtpCommandException) { ftp.CreateDirectory(part); ftp.SetWorkingDirectory(part); }
            }
        }
        public void Put(string local, string name)
        {
            if (ftp.UploadFile(local, name + ".part", FtpRemoteExists.Overwrite, false) != FtpStatus.Success) throw new IOException("il file non è arrivato sul server FTP");
            ftp.MoveFile(name + ".part", name, FtpRemoteExists.Overwrite);
        }
        public List<string> List()
        {
            try { return ftp.GetNameListing().Select(n => n.TrimEnd('/').Split('/').Last()).Where(b.Own).ToList(); }
            catch (FluentFTP.Exceptions.FtpCommandException) { return new(); }  // some servers answer 550 for an empty folder
        }
        public void Remove(string name) => ftp.DeleteFile(name);
        public void Dispose() { try { ftp.Disconnect(); } catch (Exception) { } ftp.Dispose(); }
    }

    sealed class SftpTarget : ITarget
    {
        readonly Renci.SshNet.SftpClient sftp; readonly string root; readonly Backup b;
        public SftpTarget(Backup b, WinDestinazione d)
        {
            // shortcut: password login only, add key files when a customer's server needs them
            this.b = b; string seen = null;
            var info = new Renci.SshNet.ConnectionInfo(d.Host, d.Porta ?? 22, d.Utente, new Renci.SshNet.PasswordAuthenticationMethod(d.Utente, b.Secret(d))) { Timeout = TimeSpan.FromSeconds(30) };
            sftp = new Renci.SshNet.SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(30) };
            sftp.HostKeyReceived += (_, e) =>
            {
                // the key's own type from its blob (ssh-rsa, ssh-ed25519), as the Python program recorded it
                var len = (e.HostKey[0] << 24) | (e.HostKey[1] << 16) | (e.HostKey[2] << 8) | e.HostKey[3];
                seen = Encoding.ASCII.GetString(e.HostKey, 4, len) + " " + Crypto.B64(SHA256.HashData(e.HostKey));
                e.CanTrust = d.Impronta == "" || d.Impronta == seen;
            };
            try { sftp.Connect(); }
            catch { sftp.Dispose(); if (seen != null) b.Pin(d, seen); throw; }  // the key is recorded before the password is tried; another key is the error to show
            b.Pin(d, seen);
            root = d.Percorso != "" ? d.Percorso : "."; var current = "";
            foreach (var part in (root.StartsWith('/') ? new[] { "/" } : Array.Empty<string>()).Concat(root.Split('/', StringSplitOptions.RemoveEmptyEntries)))
            {
                current = current == "" ? part : current.TrimEnd('/') + "/" + part;
                if (!sftp.Exists(current)) sftp.CreateDirectory(current);
            }
        }
        string At(string name) => root.TrimEnd('/') + "/" + name;
        public void Put(string local, string name)
        {
            using (var f = File.OpenRead(local)) sftp.UploadFile(f, At(name + ".part"), true);
            try { sftp.RenameFile(At(name + ".part"), At(name), true); }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or NotSupportedException) { if (sftp.Exists(At(name))) sftp.DeleteFile(At(name)); sftp.RenameFile(At(name + ".part"), At(name)); }
        }
        public List<string> List() => sftp.ListDirectory(root).Select(f => f.Name).Where(b.Own).ToList();
        public void Remove(string name) => sftp.DeleteFile(At(name));
        public void Dispose() => sftp.Dispose();
    }

    ITarget Open(WinDestinazione d) => d.Tipo switch
    {
        "cartella" => new FolderTarget(this, d.Percorso), "rete" => Network(d), "ftp" or "ftps" => new FtpTarget(this, d), "sftp" => new SftpTarget(this, d),
        _ => throw new ArgumentException("tipo di destinazione sconosciuto"),
    };

    void Send(WinDestinazione d, string local)
    {
        using var target = Open(d); target.Put(local, Path.GetFileName(local));
        if (d.Conserva > 0) { var names = target.List().OrderBy(n => n, StringComparer.Ordinal).ToList(); foreach (var old in names.Take(Math.Max(0, names.Count - d.Conserva))) target.Remove(old); }
    }

    /// <summary>Write, list and delete a small file: the same steps of a real backup.</summary>
    public void Check(WinDestinazione d)
    {
        var work = WorkDir(s.Config.DataFolder);
        try
        {
            var probe = Path.Combine(work, "prova-collegamento.txt"); File.WriteAllText(probe, "TD Booking Suite: prova di scrittura del backup\n");
            using var target = Open(d); target.Put(probe, "prova-collegamento.txt"); target.Remove("prova-collegamento.txt");
        }
        finally { Directory.Delete(work, true); }
    }

    void Prune(string folder, int keep)
    {
        if (keep <= 0) return;
        var own = Directory.GetFiles(folder, "tdbs-*.tdbk").Where(p => Own(Path.GetFileName(p))).OrderBy(p => p, StringComparer.Ordinal).ToList();
        foreach (var old in own.Take(Math.Max(0, own.Count - keep))) File.Delete(old);
    }

    public string Run(bool manual, ILogger log)
    {
        var row = Row(); long job;
        using (var g = s.Db.Global()) { job = g.Scalar<long>("INSERT INTO tdwin_esecuzione (inizio, esito, file, dimensione, dettagli, manuale) VALUES (now(), 'in corso', '', 0, '', @manual) RETURNING id", new { manual }); g.Commit(); }
        var lines = new List<string>(); var outcome = "ok"; var file = ""; long size = 0;
        try
        {
            if (row.Chiave == "") throw new ArgumentException("Manca la password dei backup: impostala nella pagina Backup.");
            if (s.Admin == null) throw new InvalidOperationException("Il servizio non riesce a leggere db-admin.json nella cartella dei dati.");
            var salt = Crypto.UnB64(row.Sale); var key = Crypto.UnB64((string)s.Crypto.Reveal(row.Chiave, "backup-key"));
            Directory.CreateDirectory(Folder); string name;
            while (File.Exists(Path.Combine(Folder, name = $"{Prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.tdbk"))) Thread.Sleep(1000);  // two runs in the same second
            var part = Path.Combine(Folder, name + ".part"); var work = WorkDir(s.Config.DataFolder);
            try
            {
                Pg(s.Config, s.Admin, "pg_dump", "-Fc", "--no-password", "-d", DbName(s.Config), "-f", Path.Combine(work, "database.dump"));
                Bundle(work, Path.Combine(work, "bundle.tar"));
                File.Delete(part); Encrypt(Path.Combine(work, "bundle.tar"), part, key, salt);
                // only a complete file gets the backup name: a cut one never counts as a copy
                File.Move(part, Path.Combine(Folder, name));
            }
            finally { File.Delete(part); try { Directory.Delete(work, true); } catch (IOException) { } }
            file = name; size = new FileInfo(Path.Combine(Folder, name)).Length;
            Prune(Folder, row.ConservaLocali);
            lines.Add($"Copia sul PC server: {Path.Combine(Folder, name)}");
            List<WinDestinazione> targets; using (var g = s.Db.Global()) targets = g.Query<WinDestinazione>("SELECT * FROM tdwin_destinazione WHERE attiva ORDER BY creata").ToList();
            foreach (var d in targets)
            {
                try { Send(d, Path.Combine(Folder, name)); lines.Add($"{d.Nome}: copiato"); }
                catch (Exception ex) { outcome = "parziale"; lines.Add($"{d.Nome}: non riuscito, {Describe(ex)}"); }  // one unreachable destination must not stop the others
            }
        }
        catch (Exception ex) { log.LogError(ex, "backup non riuscito"); outcome = "errore"; lines.Add(Describe(ex)); }
        Exec("UPDATE tdwin_esecuzione SET esito=@outcome, fine=now(), dettagli=@details, file=@file, dimensione=@size WHERE id=@job", new { outcome, details = string.Join("\n", lines), file, size, job });
        return outcome;
    }

    /// <summary>One pass of the worker: destination tests, a backup asked from the page, the nightly one.</summary>
    public void Tick(ILogger log)
    {
        List<WinDestinazione> asked; using (var g = s.Db.Global()) asked = g.Query<WinDestinazione>("SELECT * FROM tdwin_destinazione WHERE prova_richiesta IS NOT NULL").ToList();
        foreach (var d in asked)
        {
            string result; try { Check(d); result = "Collegamento riuscito"; } catch (Exception ex) { result = "Non riuscito: " + Describe(ex); }
            Exec("UPDATE tdwin_destinazione SET prova_richiesta=NULL, ultimo_esito=@result, ultimo_controllo=now() WHERE id=@id", new { result, id = d.Id });
        }
        var row = Row(); var local = DateTime.Now;
        // a PC that was off at the set time makes the backup as soon as it is on again, the same day
        var due = row.Attivo && local.TimeOfDay >= row.Ora && row.UltimoGiorno?.Date != local.Date;
        if (row.Richiesto == null && !due) return;
        Exec(due ? "UPDATE tdwin_impostazionibackup SET richiesto=NULL, ultimo_giorno=@day WHERE id=1" : "UPDATE tdwin_impostazionibackup SET richiesto=NULL WHERE id=1", new { day = local.Date });
        Run(row.Richiesto != null, log);
    }

    /// <summary>tdwin ripristina_backup: with the services stopped, as administrator (script ripristina-backup.ps1).
    /// The backup goes into a side database first; only when pg_restore succeeds the current database is renamed
    /// (kept, never dropped) and the restored one takes its name. Then config.json gets the keys of the backup.</summary>
    public static int Restore(AppConfig config, Admin admin, string source, string passwordFile, TextWriter say)
    {
        if (!File.Exists(source)) { say.WriteLine($"File non trovato: {source}"); return 1; }
        if (admin == null) { say.WriteLine("Serve db-admin.json: apri il ripristino come amministratore."); return 1; }
        string password;
        if (passwordFile != null) password = File.ReadAllText(passwordFile, Encoding.UTF8).Trim();
        else
        {
            say.Write("Password dei backup: "); var typed = new StringBuilder();
            for (var k = Console.ReadKey(true); k.Key != ConsoleKey.Enter; k = Console.ReadKey(true)) { if (k.Key == ConsoleKey.Backspace) { if (typed.Length > 0) typed.Length--; } else if (k.KeyChar != 0) typed.Append(k.KeyChar); }
            say.WriteLine(); password = typed.ToString();
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss"); var work = WorkDir(config.DataFolder);
        try
        {
            try { Decrypt(source, Path.Combine(work, "bundle.tar"), Derive(password, ReadSalt(source))); } catch (ArgumentException ex) { say.WriteLine(ex.Message); return 1; }
            TarFile.ExtractToDirectory(Path.Combine(work, "bundle.tar"), work, true);
            var info = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "info.json")))!; var secrets = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "segreti.json")))!;
            var made = info["creato"]!.ToString(); say.WriteLine($"Backup del {made[..Math.Min(16, made.Length)].Replace('T', ' ')} dal PC {info["pc"]}, versione {info["versione"]}");
            var name = DbName(config); var side = name + "_ripristino"; var old = $"{name}_prima_{stamp}"; bool exists;
            static string Q(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
            var cs = new NpgsqlConnectionStringBuilder(AppConfig.ToNpgsql(admin.Url)) { Database = "postgres", Pooling = false };
            using (var conn = new NpgsqlConnection(cs.ConnectionString))
            {
                conn.Open();
                void Sql(string text, string arg = null) { using var cmd = new NpgsqlCommand(text, conn); if (arg != null) cmd.Parameters.AddWithValue("n", arg); cmd.ExecuteNonQuery(); }
                Sql($"DROP DATABASE IF EXISTS {Q(side)}"); Sql($"CREATE DATABASE {Q(side)}");
                try { Pg(config, admin, "pg_restore", "--no-owner", "--no-privileges", "--exit-on-error", "--no-password", "-d", side, Path.Combine(work, "database.dump")); }
                catch (InvalidOperationException ex) { Sql($"DROP DATABASE IF EXISTS {Q(side)}"); say.WriteLine($"Ripristino non riuscito, i dati attuali non sono stati toccati. {ex.Message}"); return 1; }
                Sql("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=@n AND pid<>pg_backend_pid()", name);
                using (var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname=@n", conn)) { cmd.Parameters.AddWithValue("n", name); exists = cmd.ExecuteScalar() != null; }
                if (exists) Sql($"ALTER DATABASE {Q(name)} RENAME TO {Q(old)}");
                Sql($"ALTER DATABASE {Q(side)} RENAME TO {Q(name)}");
            }
            var path = Path.Combine(config.DataFolder, "config.json"); File.Copy(path, Path.Combine(config.DataFolder, $"config.prima_{stamp}.json"), true);
            foreach (var k in new[] { "DJANGO_SECRET_KEY", "TDBS_KEYS", "TDBS_ACTIVE_KEY" }) config.Raw["env"]![k] = secrets[k]!.ToString();
            config.Raw["install_id"] = info["install_id"]!.ToString(); config.Save();
            // the licence that works here after the restore: a renewal made after the backup wins over the backup's
            var licensing = new Licensing(config.DataFolder, info["install_id"]!.ToString(), null); var code = licensing.InstallCode();
            bool Usable(string text) { try { return text != "" && licensing.Problem(Licensing.Parse(text), DateOnly.FromDateTime(DateTime.Now), code) == ""; } catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException) { return false; } }
            var current = File.Exists(licensing.LicencePath) ? File.ReadAllText(licensing.LicencePath, Encoding.UTF8) : "";
            var restored = File.Exists(Path.Combine(work, "licenza.txt")) ? File.ReadAllText(Path.Combine(work, "licenza.txt"), Encoding.UTF8) : "";
            var chosen = Usable(current) ? current : Usable(restored) ? restored : current != "" ? current : restored;
            if (chosen != "" && chosen != current) { Directory.CreateDirectory(Path.GetDirectoryName(licensing.LicencePath)!); File.WriteAllText(licensing.LicencePath, chosen, new UTF8Encoding(false)); }
            say.WriteLine($"Ripristino completato. Il database di prima resta come {old}" + (exists ? "" : ", non c'era") + ": quando sei sicuro puoi eliminarlo.");
            return 0;
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }
}
