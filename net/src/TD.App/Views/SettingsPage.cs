using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/form.html with TenantForm and _wordpress.html: Impostazioni e sito: brand, privacy and what the
/// business may do, then the WordPress site (the booking form in the site, the PC mode).</summary>
public sealed class SettingsPage : Page
{
    string code = "", secret = "";
    public override async Task Load() => Draw(await Api.Current.Call("impostazioni", new { }));
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static TextBox Code(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    static string When(string iso) => DateTimeOffset.TryParse(iso, out var d) ? d.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "";

    async Task Site(JsonObject args, string confirm = null)
    {
        if (confirm != null && !Ask(confirm)) return;
        var r = await Api.Current.Call("impostazioni.sito", args);
        if (r.S("codice") != "") code = r.S("codice");
        if (r.S("chiave") != "") secret = r.S("chiave");
        if (r.S("testo") != "") Main.Flash(r.S("testo"), r.Bo("errore"));
        await Load();
    }
    async Task Plugin(string version) => Downloads.Save($"td-booking-suite-{version}-wordpress.zip", await Api.Current.Bytes("impostazioni.plugin", new { }));

    /// <summary>.wp-code: the code or the key shown once, with Copia.</summary>
    Border Once(string caption, string value)
    {
        var copy = F.Small(B("Copia", () => { Clipboard.SetText(value); Main.Flash("Copiato."); })); DockPanel.SetDock(copy, Dock.Right);
        var d = new DockPanel(); d.Children.Add(copy); d.Children.Add(Col(Muted(caption, 13), Code(value)));
        return new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 10, 0, 10), Child = d };
    }
    static TextBlock Step(int n, string text) { var t = Muted($"{n}. {text}"); t.Foreground = Res("Ink"); t.Margin = new Thickness(8, 3, 0, 3); return t; }
    static Border Pill(string text) => new() { Background = Res("OkSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = T(text, size: 12.5, bold: true, color: Res("Ok")) };

    void Draw(JsonNode d)
    {
        var form = new FormView(d.A("campi"));
        var save = new Button { Content = "Salva", Style = Style("Primary"), Margin = new Thickness(0, 18, 14, 0) };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await Guard(async () => { var r = await Api.Current.Call("impostazioni.salva", new JsonObject { ["valori"] = form.Values() }); Main.Flash(r.S("testo")); await Main.Refresh("impostazioni"); }); }
            catch (FormError ex) { form.Errors(ex.Errors); }
            finally { save.IsEnabled = true; }
        };
        var back = F.Link("Torna alla dashboard", () => Main.Show("dashboard")); back.VerticalAlignment = VerticalAlignment.Center; back.Margin = new Thickness(0, 18, 0, 0);
        var page = Col(F.Heading(d.S("nome"), "Impostazioni e sito", "Brand, piano, privacy e autorizzazioni di invio, e il sito WordPress dell'attività. Riservato all'amministratore."), F.Card(form.Panel, Row(save, back)));

        var wps = d["wps"]; var sites = d.A("siti").Select(o => o.ToString()).ToList(); var version = d.S("versione");
        var head = Row(T("Sito WordPress", "H2")); if (sites.Count > 0) head.Children.Add(Pill("Modulo collegato")); if (wps.Bo("on")) head.Children.Add(Pill("Modalità PC attiva"));
        var card = Col(head);

        if (d.Bo("pubblica"))
        {
            card.Children.Add(T("Modulo di prenotazione nel sito", size: 17, bold: true).M(0, 16, 0, 4));
            if (code != "") card.Children.Add(Once("Codice di collegamento, valido 24 ore e una sola volta", code));
            card.Children.Add(Muted("Per mostrare il modulo di prenotazione dentro il sito WordPress dell'attività."));
            var first = Step(1, "Installa il plugin TD Booking Suite nel sito ("); first.Inlines.Add(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("scaricalo qui")) { Command = new Relay(() => _ = Guard(() => Plugin(version))) }); first.Inlines.Add(")");
            card.Children.Add(first); card.Children.Add(Step(2, "In WordPress apri Impostazioni, TD Booking Suite, Collegati al portale")); card.Children.Add(Step(3, "Premi qui sotto Genera il codice di collegamento e copialo: vale 24 ore e una volta sola"));
            card.Children.Add(Step(4, $"Nel plugin inserisci l'indirizzo del portale {d.S("portale")} e il codice, poi Collegati: il plugin autorizza il sito, legge sedi e servizi e crea la pagina Prenota"));
            var gen = BA(code != "" || d.Bo("inAttesa") ? "Genera un nuovo codice" : "Genera il codice di collegamento", () => Site(new JsonObject { ["azione"] = "wp_code" }), code != "" ? null : "Primary"); gen.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(gen.M(0, 12, 0, 0));
            if (sites.Count > 0)
            {
                card.Children.Add(T("Siti che mostrano il modulo", size: 17, bold: true).M(0, 16, 0, 4));
                foreach (var o in sites)
                {
                    var drop = F.Small(BA("Scollega", () => Site(new JsonObject { ["azione"] = "wp_remove", ["site"] = o }, $"Scollegare {o}? Il modulo di prenotazione non comparirà più in quel sito."), "Ghost")); DockPanel.SetDock(drop, Dock.Right);
                    var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) }; row.Children.Add(drop); row.Children.Add(T(o, size: 14, bold: true)); card.Children.Add(row);
                }
            }
        }

        // Modalità PC
        card.Children.Add(T("Modalità PC", size: 17, bold: true).M(0, 22, 0, 4));
        card.Children.Add(Muted("Il sito prende le prenotazioni anche con questo PC spento. Quando il PC è acceso, ogni 2 minuti il programma manda al sito servizi, operatori e orari liberi e prende le prenotazioni arrivate: nessuna porta da aprire."));
        foreach (var (n, s) in new[] { "Scrivi qui l'indirizzo del sito e salva, poi Genera la chiave e copiala", "Scarica il plugin e installalo in WordPress: Plugin › Aggiungi nuovo › Carica plugin", "In WordPress, Impostazioni › TD Booking Suite › Modalità PC: incolla la chiave, spunta Usa la modalità PC e salva", "Metti [td_booking_suite] in una pagina del sito", "Qui accendi Sincronizzazione attiva, Salva e Sincronizza ora: la riga in alto dice com'è andata" }.Select((s, i) => (i + 1, s)))
            card.Children.Add(Step(n, s));
        var note = Muted("Per l'agenda riservata sul sito: password nel plugin e [tdbs_agenda] in una pagina tolta dalla cache del server. ", 13); note.Inlines.Add(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("I passi nella guida")) { Command = new Relay(() => Main.Show("guida")) });
        card.Children.Add(note.M(0, 8, 0, 0));
        if (secret != "") card.Children.Add(Once("Chiave da incollare nel plugin, in Impostazioni › TD Booking Suite › Modalità PC: si vede solo adesso", secret));
        var url = Box(wps.S("url")); url.MaxLength = 200; url.Tag = "https://www.sito.it"; AgendaPage.Hint(url); var days = Box(wps.S("days"));
        var on = F.Switch(wps.Bo("on"), "Sincronizzazione attiva", "Sincronizzazione attiva"); var archive = F.Switch(wps.Bo("archive"), "Sito come archivio centrale", "Sito come archivio centrale"); var mails = F.Switch(wps.Bo("mails"), "Il sito gestisce prenotazioni ed email", "Il sito gestisce prenotazioni ed email");
        card.Children.Add(F.Grid2(F.Field("Indirizzo del sito", url), F.Field("Giorni prenotabili dal sito", days, null, "Da 1 a 90: quanti giorni avanti il sito offre gli orari")));
        var switches = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; switches.Children.Add(on); switches.Children.Add(archive.M(28, 0, 0, 0)); card.Children.Add(switches);
        card.Children.Add(Muted("Il sito tiene una copia cifrata di prenotazioni, clienti, schede di lavoro, consensi e riepilogo degli scontrini, aggiornata a ogni sincronizzazione: se il PC si rompe, si riprende tutto da lì. Va acceso anche nel plugin.", 13).M(0, 6, 0, 10));
        card.Children.Add(mails);
        card.Children.Add(Muted("Il sito conferma subito le prenotazioni online e manda tutte le email ai clienti (conferma, promemoria, modifica, annullamento) e gli avvisi a voi, anche con il PC spento per giorni: i testi si impostano nel plugin, in Impostazioni › TD Booking Suite. Il programma non manda più le email delle prenotazioni; riceve le prenotazioni e gli annullamenti fatti dai clienti dal sito. Serve il sito come archivio centrale.", 13).M(0, 6, 0, 0));
        var saveSite = BA("Salva", () => Site(new JsonObject { ["azione"] = "wps_save", ["url"] = url.Text, ["days"] = days.Text, ["on"] = on.IsChecked == true, ["archive"] = archive.IsChecked == true, ["mails"] = mails.IsChecked == true }), "Primary"); saveSite.HorizontalAlignment = HorizontalAlignment.Left;
        card.Children.Add(saveSite.M(0, 14, 0, 0));
        var acts = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        acts.Children.Add(F.Small(BA(wps.Bo("secret") ? "Genera una nuova chiave" : "Genera la chiave", () => Site(new JsonObject { ["azione"] = "wps_secret" }, wps.Bo("secret") ? "Con una chiave nuova il sito smette di sincronizzarsi finché non la incolli nel plugin. Continuare?" : null))).M(0, 0, 10, 0));
        if (wps.Bo("on")) acts.Children.Add(F.Small(BA("Sincronizza ora", () => Site(new JsonObject { ["azione"] = "wps_now" }))).M(0, 0, 10, 0));
        acts.Children.Add(F.Small(BA("Scarica il plugin", () => Plugin(version), "Ghost")));
        card.Children.Add(acts);

        // dl.facts
        string last = wps.S("at") == "" ? "ancora nessuna" : When(wps.S("at")) + ": " + (wps.S("error") != "" ? wps.S("error") : $"riuscita, {wps.I("new")} prenotazion{(wps.I("new") == 1 ? "e nuova" : "i nuove")}");
        var facts = new List<(string, string, bool)> { ("Ultima sincronizzazione", last, wps.S("at") != "" && wps.S("error") != "") };
        if (wps.Bo("archive"))
            facts.Add(("Archivio sul sito", (wps.S("archive_at") != "" ? $"{wps.S("records")} record, {wps.S("size").Replace('.', ',')} MB, ultimo invio {When(wps.S("archive_at"))}" : "ancora nessun invio") + $"; in coda {wps.S("queue")}" + (wps.S("archive_error") != "" ? ": " + wps.S("archive_error") : ""), wps.S("archive_error") != ""));
        foreach (var (k, v, bad) in facts)
        {
            var value = Muted(v); value.Foreground = Res(bad ? "Danger" : "Ink"); if (bad) value.FontWeight = FontWeights.SemiBold;
            card.Children.Add(Col(Muted(k, 13), value).M(0, 12, 0, 0));
        }
        if (wps.Bo("archive"))
        {
            card.Children.Add(Row(F.Small(BA("Carica tutto sul sito", () => Site(new JsonObject { ["azione"] = "wps_upload" }))).M(0, 0, 10, 0), F.Small(BA("Sincronizza con il sito", () => Site(new JsonObject { ["azione"] = "wps_merge" })))).M(0, 14, 0, 0));
            card.Children.Add(Muted("Confronta ogni prenotazione, cliente e scheda con il sito e tiene la versione modificata per ultima, da qualunque parte arrivi; l'ora del PC e del sito non contano, si confronta il momento esatto della modifica.", 13).M(0, 6, 0, 0));
        }
        if (wps.Bo("secret"))
        {
            var confirm = new CheckBox { Margin = new Thickness(0, 10, 0, 0), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Ci sono già clienti o prenotazioni: aggiungi quelli del sito" } };
            card.Children.Add(T("Ripristina dal sito", size: 17, bold: true).M(0, 18, 0, 4));
            card.Children.Add(Muted("Per un PC nuovo o reinstallato: riporta qui clienti, prenotazioni, schede di lavoro e consensi dell'archivio del sito, senza doppioni. Servizi, varianti e operatori si abbinano per nome: quelli che mancano vengono elencati.", 13));
            card.Children.Add(confirm);
            var restore = F.Small(BA("Ripristina dal sito", () => Site(new JsonObject { ["azione"] = "wps_restore", ["confirm"] = confirm.IsChecked == true }, "Ripristinare clienti e prenotazioni dall'archivio del sito?"))); restore.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(restore.M(0, 12, 0, 0));
        }
        page.Children.Add(F.Card(card));
        Content = F.Page(1180, page);
    }

    sealed class Relay(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object p) => true;
        public void Execute(object p) => run();
    }
}
