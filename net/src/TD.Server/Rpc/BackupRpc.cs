using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>tdwin/backup_views.py: the Backup page of the program's administrator. The work itself is the worker's.</summary>
public static class BackupRpc
{
    static readonly (string Id, string Nome)[] TIPI = { ("cartella", "Cartella o disco esterno"), ("rete", "Cartella di rete, NAS"), ("ftp", "FTP"), ("ftps", "FTPS, FTP con TLS"), ("sftp", "SFTP") };

    /// <summary>Django's filesizeformat in Italian.</summary>
    public static string Size(long bytes)
    {
        string N(double v) => v.ToString("0.0", CultureInfo.GetCultureInfo("it-IT"));
        return bytes < 1024 ? $"{bytes}\u00a0byte" : bytes < 1 << 20 ? N(bytes / 1024.0) + "\u00a0KB" : bytes < 1 << 30 ? N(bytes / 1048576.0) + "\u00a0MB" : N(bytes / 1073741824.0) + "\u00a0GB";
    }
    static string Local(DateTime? t, string f) => t.HasValue ? DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToLocalTime().ToString(f) : "";

    static List<Fld> Fields(WinDestinazione d) => new()
    {
        new() { Nome = "nome", Etichetta = "Nome", Valore = d?.Nome ?? "", Max = 80 },
        new() { Nome = "tipo", Etichetta = "Tipo", Tipo = "select", Valore = d?.Tipo ?? "cartella", Scelte = TIPI.Select(t => (object)new { id = t.Id, nome = t.Nome }) },
        new() { Nome = "percorso", Etichetta = "Cartella", Valore = d?.Percorso ?? "", Max = 400, Largo = true, Aiuto = @"Disco esterno: E:\Backup. Cartella di rete: \\nas\backup\booking. FTP e SFTP: la cartella sul server, per esempio /backup/booking" },
        new() { Nome = "host", Etichetta = "Server", Valore = d?.Host ?? "", Max = 255, Aiuto = "Solo per FTP, FTPS e SFTP: per esempio ftp.miodominio.it o 192.168.1.50" },
        new() { Nome = "porta", Etichetta = "Porta", Tipo = "number", Valore = d?.Porta?.ToString() ?? "", Aiuto = "Vuota: 21 per FTP e FTPS, 22 per SFTP, 445 per la rete" },
        new() { Nome = "utente", Etichetta = "Utente", Valore = d?.Utente ?? "", Max = 255, Aiuto = "Per la cartella di rete e per FTP o SFTP. Vuoto per un disco esterno" },
        new() { Nome = "password_nuova", Etichetta = "Password", Tipo = "password", Valore = "", Aiuto = d != null && d.Password != "" ? "Vuota per tenere quella salvata" : "" },
        new() { Nome = "conserva", Etichetta = "Copie da tenere", Tipo = "number", Valore = (d?.Conserva ?? 14).ToString(), Aiuto = "0 per tenerle tutte" },
        new() { Nome = "attiva", Etichetta = "Attiva", Tipo = "check", Valore = d?.Attiva ?? true },
    };

    [Rpc("backup", Tenant = false, Staff = true)]
    public static object Page(Call c)
    {
        var b = new Work.Backup(c.S); var row = b.Row(); using var g = c.S.Db.Global();
        var edit = c.Id("modifica") is Guid id ? g.Get<WinDestinazione>(id) : null;
        var folder = b.Folder;
        return new
        {
            impostata = row.Chiave != "", impostata_il = Local(row.PasswordImpostata, "dd/MM/yyyy"), attivo = row.Attivo, ora = row.Ora.ToString(@"hh\:mm"), conserva = row.ConservaLocali, cartella = folder,
            destinazioni = g.Query<WinDestinazione>("SELECT * FROM tdwin_destinazione ORDER BY creata").Select(d => new
            {
                id = d.Id, nome = d.Nome, attiva = d.Attiva, tipo = TIPI.FirstOrDefault(t => t.Id == d.Tipo).Nome ?? d.Tipo,
                dettaglio = (d.Host != "" ? d.Host + (d.Porta.HasValue ? ":" + d.Porta : "") + " " : "") + d.Percorso + ", tiene " + (d.Conserva > 0 ? d.Conserva + " copie" : "tutte le copie"),
                esito = d.UltimoEsito, controllo = Local(d.UltimoControllo, "dd/MM HH:mm"), impronta = d.Impronta,
            }).ToList(),
            modifica = edit == null ? null : new { id = edit.Id, nome = edit.Nome }, campi = Fields(edit),
            file = Directory.Exists(folder) ? Directory.GetFiles(folder, "tdbs-*.tdbk").OrderByDescending(p => p, StringComparer.Ordinal).Select(p => new { nome = Path.GetFileName(p), data = File.GetLastWriteTime(p).ToString("dd/MM/yyyy HH:mm"), dimensione = Size(new FileInfo(p).Length) }).ToList() : null,
            storico = g.Query<WinEsecuzione>("SELECT * FROM tdwin_esecuzione ORDER BY inizio DESC LIMIT 20").Select(r => new { quando = Local(r.Inizio, "dd/MM/yyyy HH:mm"), manuale = r.Manuale, esito = r.Esito, dettagli = r.Dettagli, dimensione = r.Dimensione > 0 ? Size(r.Dimensione) : "" }).ToList(),
        };
    }

    [Rpc("backup.azione", Tenant = false, Staff = true, Fresh = true)]
    public static object Action(Call c)
    {
        var b = new Work.Backup(c.S); var row = b.Row(); using var g = c.S.Db.Global();
        var target = c.Id("id") is Guid id ? g.Get<WinDestinazione>(id) : null; var action = c.Str("azione");
        object Done(string text) { g.Commit(); return new { testo = text }; }
        switch (action)
        {
            case "impostazioni":
                {
                    if (!TimeSpan.TryParseExact(c.Str("ora"), @"hh\:mm", CultureInfo.InvariantCulture, out var at) || !int.TryParse(c.Str("conserva_locali") == "" ? "7" : c.Str("conserva_locali"), out var keep))
                        throw new BookingException("Ora nel formato 01:30 e copie da tenere tra 1 e 365.");
                    var on = c.Bool("attivo"); if (on && row.Chiave == "") throw new BookingException("Prima imposta la password dei backup.");
                    g.Execute("UPDATE tdwin_impostazionibackup SET attivo=@on, ora=@at, conserva_locali=@keep WHERE id=1", new { on, at, keep = Math.Clamp(keep, 1, 365) });
                    return Done(on ? $"Backup automatico attivo ogni giorno alle {at:hh\\:mm}." : "Backup automatico spento.");
                }
            case "password":
                if (c.Str("password") != c.Str("password2")) throw new BookingException("Le due password non coincidono.");
                try { b.SetPassword(c.Str("password")); } catch (ArgumentException ex) { throw new BookingException(ex.Message); }
                return Done("Password dei backup salvata. Scrivila e conservala lontano dal PC: senza, i backup non si aprono.");
            case "esegui":
                if (row.Chiave == "") throw new BookingException("Prima imposta la password dei backup.");
                g.Execute("UPDATE tdwin_impostazionibackup SET richiesto=now() WHERE id=1");
                return Done("Backup avviato: tra qualche minuto lo trovi nello storico qui sotto.");
            case "destinazione":
                {
                    var p = new Posted(c, c.Obj("valori") ?? new JsonObject()); var d = target ?? new WinDestinazione();
                    var before = target == null ? null : (d.Tipo, d.Host, d.Porta) as (string, string, int?)?;
                    d.Nome = p.Text("nome", true, 80); d.Tipo = p.Text("tipo", true); d.Percorso = p.Text("percorso", true, 400); d.Host = p.Text("host", false, 255); d.Porta = p.Int("porta"); d.Utente = p.Text("utente", false, 255);
                    d.Conserva = p.Int("conserva", true) ?? 0; d.Attiva = p.Bool("attiva");
                    if (d.Tipo != "" && !TIPI.Any(t => t.Id == d.Tipo)) p.Error("tipo", "Scegli un'opzione valida.");
                    if (d.Tipo == "cartella" && !Regex.IsMatch(d.Percorso, @"^([A-Za-z]:[\\/]|\\\\)")) p.Error("percorso", @"Scrivi il percorso completo, per esempio E:\Backup o \\nas\backup.");
                    if (d.Tipo == "rete" && !d.Percorso.Replace('/', '\\').StartsWith(@"\\")) p.Error("percorso", @"Una cartella di rete inizia con \\, per esempio \\nas\backup\booking.");
                    if (d.Tipo is "ftp" or "ftps" or "sftp" && d.Host == "") p.Error("host", "Serve l'indirizzo del server.");
                    if (d.Tipo == "sftp" && d.Utente == "") p.Error("utente", "Per SFTP serve l'utente.");
                    if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
                    if (p.Raw("password_nuova") != "") d.Password = b.SealSecret(d, p.Raw("password_nuova"));
                    if (before != null && before != (d.Tipo, d.Host, d.Porta)) d.Impronta = "";  // another server, another key
                    d.ProvaRichiesta = DateTime.UtcNow;
                    if (target == null) g.Insert(d); else g.Update(d, "nome", "tipo", "attiva", "percorso", "host", "porta", "utente", "password", "impronta", "conserva", "prova_richiesta");
                    return Done("Destinazione salvata: ora la provo, l'esito compare qui sotto tra qualche secondo.");
                }
            case "prova" when target != null:
                g.Execute("UPDATE tdwin_destinazione SET prova_richiesta=now(), ultimo_esito='Prova in corso' WHERE id=@id", new { id = target.Id });
                return Done("Prova avviata: l'esito compare qui tra qualche secondo.");
            case "impronta" when target != null:
                g.Execute("UPDATE tdwin_destinazione SET impronta='' WHERE id=@id", new { id = target.Id });
                return Done("Impronta azzerata: il prossimo collegamento registra quella nuova.");
            case "elimina" when target != null:
                g.Execute("DELETE FROM tdwin_destinazione WHERE id=@id", new { id = target.Id });
                return Done("Destinazione eliminata. I backup già copiati lì restano dove sono.");
        }
        throw new BookingException("Operazione non valida.");
    }

    [Rpc("backup.scarica", Tenant = false, Staff = true, Fresh = true)]
    public static IResult Download(Call c)
    {
        var name = c.Str("nome"); if (!Regex.IsMatch(name, @"^tdbs-[a-z2-7]{16}-\d{8}-\d{6}\.tdbk$")) throw new KeyNotFoundException();
        var path = Path.Combine(new Work.Backup(c.S).Folder, name); if (!File.Exists(path)) throw new KeyNotFoundException();
        return Results.File(path, "application/octet-stream", name);
    }
}
