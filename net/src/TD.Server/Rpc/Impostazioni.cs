using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>platform.tenant_settings with portal.wordpress_panels and the WordPress actions of portal.setup_action:
/// Impostazioni e sito: brand, privacy and what the business may do, and the WordPress site. The administrator only.</summary>
public static class Impostazioni
{
    static void Staff(Call c) { if (!c.User.IsStaff) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione."); }
    static WpSite Site(Call c) => new(c.S.Db, c.S.Crypto, c.Tenant.Id, c.PublicOrigin, Outbox.Instance);

    [Rpc("impostazioni", Fresh = true)]
    public static object Page(Call c)
    {
        Staff(c); var x = c.C; var t = c.Tenant; var wps = WpSync.Panel(x);
        Fld Check(string name, string label, bool value) => new() { Nome = name, Etichetta = label, Tipo = "check", Valore = value, Largo = true };
        var fields = new List<Fld>
        {
            new() { Nome = "name", Etichetta = "Nome attività", Valore = t.Name, Max = 120 }, new() { Nome = "slug", Etichetta = "Indirizzo breve", Valore = t.Slug, Max = 60, Disattivo = true },
            new() { Nome = "timezone", Etichetta = "Fuso orario", Valore = t.Timezone, Max = 64 },
            Check("maintenance", "Manutenzione tecnica: sospendi temporaneamente prenotazioni e portale", t.Maintenance),
            Check("marketing_enabled", "Marketing: l'attività invia campagne email ai clienti iscritti", t.MarketingEnabled),
            Check("export_enabled", "Marketing: l'attività scarica il CSV dei clienti iscritti (Brevo, Mailchimp, fogli)", t.ExportEnabled),
            Check("client_mails_enabled", "Email ai clienti: l'attività sceglie quali inviare e ne scrive oggetto e testo (in Configurazione promemoria). Promemoria, arrivo e recensione, e quando partono, li impostate voi in Configurazione promemoria", t.ClientMailsEnabled),
            Check("alloggiati_self", "Alloggiati Web: l'attività inserisce da sola utente, password e chiave del servizio web (in Alloggi)", t.AlloggiatiSelf),
            Check("till_self", "Registratore di cassa: l'attività lo collega da sola (tipo, indirizzo, reparti e pagamenti)", t.TillSelf),
            new() { Nome = "smtp_daily_limit", Etichetta = "Limite email giornaliero", Tipo = "number", Valore = t.SmtpDailyLimit }, new() { Nome = "smtp_hourly_limit", Etichetta = "Limite email orario", Tipo = "number", Valore = t.SmtpHourlyLimit },
            new() { Nome = "brand_color", Etichetta = "Colore del marchio", Valore = t.BrandColor, Max = 7, Segnaposto = "#7a55ad" }, new() { Nome = "tagline", Etichetta = "Sottotitolo", Valore = t.Tagline, Max = 180 },
            new() { Nome = "website", Etichetta = "Sito web", Valore = t.Website, Max = 200 },
            new() { Nome = "privacy_text", Etichetta = "Informativa prenotazioni", Tipo = "area", Valore = t.PrivacyText }, new() { Nome = "privacy_text_en", Etichetta = "Informativa in inglese (facoltativa)", Tipo = "area", Valore = t.PrivacyTextEn },
            new() { Nome = "consent_text", Etichetta = "Testo consenso marketing", Tipo = "area", Valore = t.ConsentText }, new() { Nome = "consent_text_en", Etichetta = "Consenso in inglese (facoltativo)", Tipo = "area", Valore = t.ConsentTextEn },
            new() { Nome = "retention_days", Etichetta = "Conservazione prenotazioni (giorni)", Tipo = "number", Valore = t.RetentionDays },
            Check("receive_schedule", "Limita anche l'orario di invio delle richieste online", t.ReceiveSchedule), Check("api_enabled", "Permetti all'attività di gestire API e webhook", t.ApiEnabled),
            new() { Nome = "logo_file", Etichetta = "Logo PNG trasparente, JPEG o WebP, massimo 1 MB, anche orizzontale", Tipo = "logo", Valore = "" },
            new() { Nome = "embed_origins_text", Etichetta = "Siti autorizzati a mostrare il widget (una origine HTTPS per riga)", Tipo = "area", Valore = string.Join("\n", Origins(t)) },
        };
        return new
        {
            nome = t.Name, campi = fields, pubblica = c.S.Config.Online, siti = Origins(t), portale = c.PublicOrigin.TrimEnd('/'), versione = PluginVersion(),
            inAttesa = t.ConnectHash != "" && t.ConnectUntil > x.Now, wps,
        };
    }
    static List<string> Origins(Tenant t) => (JsonNode.Parse(string.IsNullOrEmpty(t.EmbedOrigins) ? "[]" : t.EmbedOrigins) as JsonArray ?? new JsonArray()).Select(o => o.ToString()).ToList();

    [Rpc("impostazioni.salva", Fresh = true)]
    public static object Save(Call c)
    {
        Staff(c); var x = c.C; var t = c.Tenant; var p = new Posted(c, c.Obj("valori"));
        var name = p.Text("name", true, 120); var zone = p.Text("timezone", true, 64);
        if (zone != "" && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _)) p.Error("timezone", "Fuso orario non valido.");
        int? daily = p.Int("smtp_daily_limit", true), hourly = p.Int("smtp_hourly_limit", true);
        foreach (var (k, v) in new[] { ("smtp_daily_limit", daily), ("smtp_hourly_limit", hourly) }) if (v is < 1 or > 10000) p.Error(k, "Limite tra 1 e 10.000.");
        var color = p.Text("brand_color", true, 7); if (color != "" && !Regex.IsMatch(color, @"^#[0-9a-fA-F]{6}\z")) p.Error("brand_color", "Colore non valido.");
        var tagline = p.Text("tagline", false, 180);
        var site = p.Text("website", false, 200); if (site != "" && !site.Contains("://")) site = "http://" + site;
        if (site != "" && !(Uri.TryCreate(site, UriKind.Absolute, out var su) && su.Scheme is "http" or "https" && su.Host.Contains('.'))) p.Error("website", "Inserisci un URL valido.");
        var privacy = p.Text("privacy_text", true); var privacyEn = p.Text("privacy_text_en"); var consent = p.Text("consent_text", true); var consentEn = p.Text("consent_text_en");
        var keep = p.Int("retention_days", true); if (keep is < 30 or > 3650) p.Error("retention_days", "Valore tra 30 e 3650 giorni.");
        var (logo, ratio) = Configura.Logo(p.Raw("logo_file"), e => p.Error("logo_file", e));
        var origins = p.Raw("embed_origins_text").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (origins.Length > 10) p.Error("embed_origins_text", "Massimo 10 siti.");
        else if (origins.Any(v => WpSync.ValidOrigin(v) == "" || v.TrimEnd().EndsWith('/'))) p.Error("embed_origins_text", "Usa solo origini HTTPS, senza percorsi o caratteri jolly.");
        if (p.Errors.Count > 0) throw new FormErrors(p.Errors);
        x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = t.Id });
        x.S.Execute(@"UPDATE core_tenant SET name=@name, timezone=@zone, maintenance=@m, marketing_enabled=@mk, export_enabled=@ex, client_mails_enabled=@cm, alloggiati_self=@al, till_self=@ti, smtp_daily_limit=@daily, smtp_hourly_limit=@hourly,
            brand_color=@color, tagline=@tagline, website=@site, privacy_text=@privacy, privacy_text_en=@privacyEn, consent_text=@consent, consent_text_en=@consentEn, retention_days=@keep, receive_schedule=@rs, api_enabled=@api, embed_origins=@eo::jsonb WHERE id=@t",
            new
            {
                name, zone, m = p.Bool("maintenance"), mk = p.Bool("marketing_enabled"), ex = p.Bool("export_enabled"), cm = p.Bool("client_mails_enabled"), al = p.Bool("alloggiati_self"), ti = p.Bool("till_self"), daily, hourly, color, tagline, site,
                privacy, privacyEn, consent, consentEn, keep, rs = p.Bool("receive_schedule"), api = p.Bool("api_enabled"), eo = new JsonArray(origins.Select(v => (JsonNode)WpSync.ValidOrigin(v)).ToArray()).ToJsonString(), t = t.Id,
            });
        if (logo != null) x.S.Execute("UPDATE core_tenant SET logo=@l, logo_ratio=@r WHERE id=@t", new { l = logo, r = ratio, t = t.Id });
        x.Audit("tenant.settings", t.Id);
        return new { testo = "Impostazioni aggiornate." };
    }

    /// <summary>merged(): what Sincronizza con il sito did, in words.</summary>
    static object Merged(JsonObject r)
    {
        var text = $"Sincronizzato con il sito. Presi dal sito: {r.Str("added")} nuovi qui, {r.Str("updated_here")} aggiornati perché sul sito erano più recenti. Mandati al sito: {r.Str("sent")}, più recenti qui o non ancora sul sito. Già uguali: {r.Str("same")}.";
        var missing = (r["missing"] as JsonArray).Select(m => m.ToString()).ToList(); var lost = Till.Int(r["lost"]);
        if (Till.Int(r["tombstones"]) > 0) text += $" Cancellati sul sito ma tenuti qui: {r.Str("tombstones")}; il programma non cancella niente da solo.";
        if (missing.Count > 0) text += " Nel catalogo mancano: " + string.Join(", ", missing.Take(20)) + "." + (lost > 0 ? $" {lost} non portati qui: crea i servizi con lo stesso nome e sincronizza di nuovo." : "");
        else if (lost > 0) text += $" {lost} non portati qui perché il loro cliente non c'è.";
        if (r.Str("error") != "") text += $" L'invio al sito non è riuscito ({r.Str("error")}): quello che deve andare è in coda e parte con le prossime sincronizzazioni.";
        return new { testo = text, avviso = missing.Count > 0 || lost > 0 || r.Str("error") != "" };
    }
    static object Error(string text) => new { testo = text, errore = true };

    [Rpc("impostazioni.sito", Fresh = true)]
    public static object Wordpress(Call c)
    {
        Staff(c); var t = c.Tenant; var site = Site(c); var actor = c.User.Id;
        switch (c.Str("azione"))
        {
            case "wp_code":
                {
                    // wpconnect.new_code: a one-time code, valid one day; no 0/O or 1/I
                    const string ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
                    var raw = new string(Enumerable.Range(0, 16).Select(_ => ALPHABET[RandomNumberGenerator.GetInt32(ALPHABET.Length)]).ToArray());
                    var code = string.Join("-", Enumerable.Range(0, 4).Select(i => raw.Substring(i * 4, 4)));
                    var x = c.C; x.S.Execute("UPDATE core_tenant SET connect_hash=@h, connect_until=@u WHERE id=@t", new { h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("tdbs-connect:" + raw))).ToLowerInvariant(), u = x.Now.AddDays(1), t = t.Id });
                    x.Audit("wordpress.code", t.Id);
                    return new { codice = code };
                }
            case "wp_remove":
                {
                    var which = c.Str("site"); var origins = Origins(t); if (!origins.Contains(which)) return new { };
                    var x = c.C; x.S.Execute("UPDATE core_tenant SET embed_origins=@o::jsonb WHERE id=@t", new { o = new JsonArray(origins.Where(o => o != which).Select(o => (JsonNode)o).ToArray()).ToJsonString(), t = t.Id });
                    x.Audit("wordpress.removed", t.Id, new { site = which });
                    return new { testo = which + " non può più mostrare il modulo di prenotazione." };
                }
            case "wps_secret": return new { chiave = site.NewSecret(actor) };
            case "wps_upload":  // the site as the archive, everything again
                {
                    if (!t.WpArchive) return Error("Accendi prima Sito come archivio centrale.");
                    var n = site.UploadAll(); site.Sync(full: true); var err = site.ArchiveErrorNow();
                    return err != "" ? Error($"{n} record in coda per il sito, invio non riuscito: {err}. Riprova o attendi la prossima sincronizzazione.") : new { testo = $"{n} record in coda per il sito: partono adesso e con le prossime sincronizzazioni." };
                }
            case "wps_restore":
                try
                {
                    var r = site.Restore(actor, c.Bool("confirm")); var missing = (r["missing"] as JsonArray).Select(m => m.ToString()).ToList(); var lost = Till.Int(r["lost"]);
                    var text = $"Ripristinati dal sito {r.Str("contacts")} clienti, {r.Str("bookings")} prenotazioni, {r.Str("treatments")} schede di lavoro e {r.Str("consents")} eventi dei consensi" + (Till.Int(r["skipped"]) > 0 ? $"; {r.Str("skipped")} già presenti" : "") + ".";
                    if (missing.Count > 0) text += " Nel catalogo mancano: " + string.Join(", ", missing.Take(20)) + "." + (lost > 0 ? $" {lost} prenotazioni non ripristinate: crea i servizi con lo stesso nome e ripristina di nuovo." : "");
                    return new { testo = text, avviso = missing.Count > 0 };
                }
                catch (SiteException ex) { return Error("Ripristino non riuscito: " + ex.Message.TrimEnd('.') + "."); }
            case "wps_merge":  // the newest of each record, on both sides
                if (!t.WpArchive) return Error("Accendi prima Sito come archivio centrale.");
                try { return Merged(site.Merge(actor)); }
                catch (SiteException ex) { return Error("Sincronizzazione con il sito non riuscita: " + ex.Message.TrimEnd('.') + ". Non è cambiato niente: riprova quando il sito risponde."); }
            case "wps_now":
                {
                    var n = site.Sync(full: true); var err = site.SyncError();
                    if (!Json.Truthy(WpSync.Conf(c.C)["on"])) return Error("Accendi prima la sincronizzazione.");
                    if (err != "" && n == 0) return Error("Sincronizzazione non riuscita: " + err + ".");
                    return new { testo = $"Sincronizzato con il sito: {n} prenotazion{(n == 1 ? "e nuova" : "i nuove")}." };
                }
            case "wps_save":
                try { var r = site.Save(c.Args, actor); return r != null ? Merged(r) : new { testo = "Sito WordPress salvato." }; }  // the archive just turned on, with the site answering
                catch (SiteException ex) { return Error(ex.Message); }
        }
        throw new BookingException("Operazione non valida.");
    }

    // ---------- the plugin: the zip built from the folder that comes with the program (site.plugin_zip)
    static IEnumerable<(string Path, byte[] Data)> PluginFiles()
    {
        var a = Assembly.GetExecutingAssembly();
        foreach (var name in a.GetManifestResourceNames().Where(n => n.StartsWith("wp/")).OrderBy(n => n.Replace('\\', '/'), StringComparer.Ordinal))
        {
            using var s = a.GetManifestResourceStream(name); using var ms = new MemoryStream(); s.CopyTo(ms);
            yield return (name[3..].Replace('\\', '/'), ms.ToArray());
        }
    }
    static string PluginVersion() =>
        PluginFiles().FirstOrDefault(f => f.Path == "td-booking-suite.php").Data is byte[] php && Regex.Match(Encoding.UTF8.GetString(php), @"^ \* Version: (\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : Server.Version;

    [Rpc("impostazioni.plugin")]
    public static IResult Plugin(Call c)
    {
        Staff(c); var files = PluginFiles().ToList();
        if (!files.Any(f => f.Path == "td-booking-suite.php")) throw new KeyNotFoundException("Il plugin WordPress non è incluso in questa installazione.");
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            z.CreateEntry("td-booking-suite/");
            foreach (var (path, data) in files)
            {
                var e = z.CreateEntry("td-booking-suite/" + path, CompressionLevel.Optimal); e.ExternalAttributes = 0b110_100_100 << 16;  // 0644, for the sites on Linux
                using var w = e.Open(); w.Write(data);
            }
        }
        return Results.File(ms.ToArray(), "application/zip", $"td-booking-suite-{PluginVersion()}-wordpress.zip");
    }
}
