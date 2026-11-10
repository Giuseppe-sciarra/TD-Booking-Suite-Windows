using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Domain;
using TD.Core.Win;

namespace TD.Server.Rpc;

/// <summary>tdwin/aggiornamenti.py: new versions of the program, proposed and never installed behind the user's back.
/// The worker looks at the newest public release on GitHub after start and once a day; Aggiorna ora in the window
/// downloads the MSI, checks it with the SHA-256 published next to it and starts it with Windows' confirmation.</summary>
public static class Updates
{
    public const string REPO = "Giuseppe-sciarra/TD-Booking-Suite-per-Windows";
    public static (int, int, int)? Parse(string tag) => Regex.Match((tag ?? "").Trim(), @"^v?([0-9]+)\.([0-9]+)\.([0-9]+)\z") is { Success: true } m ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    public static string MsiUrl(string ver) => $"https://github.com/{REPO}/releases/download/v{ver}/TD-Booking-Suite-{ver}.msi";
    public static string NotesUrl(string ver) => $"https://github.com/{REPO}/releases/tag/v{ver}";
    public sealed record Row(string Modo, DateTime? UltimoControllo, string Esito, string Disponibile);
    public static Row Get(Server s)
    {
        using var g = s.Db.Global(); g.Execute("INSERT INTO tdwin_aggiornamenti (id, modo, esito, disponibile) VALUES (1, 'avvisa', '', '') ON CONFLICT (id) DO NOTHING");
        var r = g.One<Row>("SELECT modo, ultimo_controllo AS UltimoControllo, esito, disponibile FROM tdwin_aggiornamenti WHERE id=1"); g.Commit(); return r;
    }
    static void Set(Server s, string sql, object args) { using var g = s.Db.Global(); g.Execute("UPDATE tdwin_aggiornamenti SET " + sql + " WHERE id=1", args); g.Commit(); }
    public static void Mode(Server s, string mode) { Get(s); Set(s, "modo=@m", new { m = mode }); }

    /// <summary>newer(): the version of a final release newer than this one, with its MSI and .sha256 attached; "" otherwise.</summary>
    static string Newer(JsonObject release)
    {
        var fresh = Parse(release.Str("tag_name")); var now = Parse(Server.Version);
        if (Json.Truthy(release["draft"]) || Json.Truthy(release["prerelease"]) || fresh == null || now == null || fresh.Value.CompareTo(now.Value) <= 0) return "";
        var ver = $"{fresh.Value.Item1}.{fresh.Value.Item2}.{fresh.Value.Item3}"; var names = (release["assets"] as JsonArray ?? new JsonArray()).Select(a => a?.Str("name")).ToHashSet();
        return names.Contains($"TD-Booking-Suite-{ver}.msi") && names.Contains($"TD-Booking-Suite-{ver}.msi.sha256") ? ver : "";
    }
    /// <summary>check(): the look at GitHub, stored for the Licenza page; returns it in words. Throws offline or for an answer that is not a release.</summary>
    public static async Task<string> Check(Server s)
    {
        Get(s); JsonObject release;
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; h.DefaultRequestHeaders.UserAgent.ParseAdd("TD-Booking-Suite");
            release = JsonNode.Parse(await h.GetStringAsync($"https://api.github.com/repos/{REPO}/releases/latest")) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            Set(s, "esito=@e", new { e = ex is System.Text.Json.JsonException ? "La risposta di GitHub non è leggibile: riprovo più tardi." : "GitHub non raggiungibile da questo PC: riprovo più tardi." });
            throw;
        }
        var available = Newer(release); var said = available != "" ? $"È disponibile la versione {available}." : $"La {Server.Version} è l'ultima versione.";
        Set(s, "disponibile=@d, esito=@e, ultimo_controllo=now()", new { d = available, e = said });
        return said;
    }
    /// <summary>notice(): Aggiorna ora, for the program's administrator and the titolare: when GitHub has a newer version,
    /// on a postazione also when its window is older than the server (the window says its version).</summary>
    public static object Notice(Call c)
    {
        var row = Get(c.S); bool owner; using (var g = c.S.Db.Global()) owner = g.Scalar<long>("SELECT count(*) FROM core_membership WHERE user_id=@u AND role='owner'", new { u = c.User.Id }) > 0;
        if (row.Modo == "spento" || !(c.User.IsStaff || owner)) return null;
        var window = Regex.Match(c.Http.Request.Headers.UserAgent.ToString(), @"TDBookingSuite/([0-9]+\.[0-9]+\.[0-9]+)");
        var mine = Parse(window.Success ? window.Groups[1].Value : Server.Version) ?? Parse(Server.Version).Value; var here = Parse(Server.Version).Value;
        var target = Parse(row.Disponibile) is { } d && d.CompareTo(here) > 0 ? row.Disponibile : Server.Version;
        if (mine.CompareTo(Parse(target).Value) >= 0) return null;
        return new
        {
            titolo = $"È disponibile la versione {target}", testo = "Aggiorna ora la scarica, la controlla e la installa con la conferma di Windows: il programma si chiude per qualche minuto. Prenotazioni, clienti e impostazioni restano",
            url = MsiUrl(target), azione = "Aggiorna ora", altro = NotesUrl(target), altroTesto = "Cosa cambia",
        };
    }
}

/// <summary>tdwin/views.licence_view: Licenza: what this installation may do, the online check, the updates, the
/// installation code, the licence pasted and the free trial.</summary>
public static class Licenza
{
    [Rpc("licenza.pagina", Tenant = false)]
    public static object Page(Call c)
    {
        var l = c.S.Licensing; var st = l.Apply(true); var agg = Updates.Get(c.S); var lic = st.Licence;
        string Local(DateTime? t) => t.HasValue ? t.Value.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm") : "";
        return new
        {
            stato = st.Kind, id = lic?.Id, intestatario = lic?.Intestatario, giorni = st.Giorni, fine = Local(st.Fine), problema = st.Problema, tipo = lic != null ? Licensing.TIPI.GetValueOrDefault(lic.Tipo ?? "", "") : "",
            attivita = st.Attivita, postazioni = st.Postazioni, scadenza = lic?.Scadenza?.ToString("dd/MM/yyyy") ?? "", installazione = !string.IsNullOrEmpty(lic?.Installazione), standalone = c.S.Standalone,
            online = !string.IsNullOrEmpty(l.StateUrl), controllo = Local(l.LastCheck()), staff = c.User.IsStaff, codice = l.InstallCode(), prova = l.TrialStart() == null && st.Kind != "licenza",
            versione = Server.Version, modo = agg.Modo, ultimo = Local(agg.UltimoControllo), esito = agg.Esito, aggiorna = Updates.Notice(c),
        };
    }

    [Rpc("licenza.azione", Tenant = false, Staff = true, Fresh = true)]
    public static object Action(Call c)
    {
        var l = c.S.Licensing; object Said(string text, bool error = false, string warn = null) => new { testo = text, errore = error, avviso = warn };
        switch (c.Str("azione"))
        {
            case "prova":
                {
                    var (start, created) = l.StartTrial(c.S.Identity.LoginName(c.User)); l.Apply(true);
                    return created ? Said($"Prova attivata fino al {start.AddDays(Licensing.TRIAL_DAYS).ToLocalTime():dd/MM/yyyy 'alle' HH:mm}.") : Said("La prova si attiva una volta sola: per continuare serve la licenza.", true);
                }
            case "online":
                // the state of the licences now, instead of waiting for the daily look
                try { var said = l.CheckOnline().GetAwaiter().GetResult(); return Said(said != "" ? said : "Controllato: la licenza è aggiornata."); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return Said("Internet non raggiungibile da questo PC: la licenza in uso resta valida, riprovo più tardi.", true); }
                catch (Exception ex) { return Said(ex.Message, true); }  // not readable, not signed by the seller, tampered
            case "aggiornamenti":
                {
                    // avvisa or spento, and a look at GitHub now
                    var mode = c.Str("modo"); if (mode is "avvisa" or "spento") Updates.Mode(c.S, mode);
                    if (mode == "spento") return Said("Aggiornamenti spenti: il programma non guarda più se ci sono versioni nuove.");
                    try { return Said(Updates.Check(c.S).GetAwaiter().GetResult()); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException) { return Said(Updates.Get(c.S).Esito, true); }
                }
            default:
                try
                {
                    var (data, over) = l.Install(c.Str("licenza"));
                    return Said($"Licenza {data.Id} attivata per {data.Intestatario}.", false, over > 0 ? $"Le postazioni registrate sono {over} più di quelle della licenza: le ultime registrate non entrano finché non ne revochi {over} da Postazioni." : null);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException) { return Said(ex.Message, true); }
        }
    }
}
