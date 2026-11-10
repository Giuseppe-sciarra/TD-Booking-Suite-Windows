using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>tdwin/licenza.html: Licenza: what this installation may do, the online check, the updates, the installation
/// code, the licence pasted and the free trial.</summary>
public sealed class LicencePage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("licenza.pagina", new { }));
    async Task Act(object args)
    {
        var r = await Api.Current.Call("licenza.azione", args);
        Main.Flash(r.S("testo"), r.Bo("errore")); if (r.S("avviso") != "") Say(r.S("avviso"));
        await Main.Refresh("licenza");
    }
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static string When(string iso, string join = " alle ") => DateTime.TryParse(iso, out var d) ? $"{d:dd/MM/yyyy}{join}{d:HH:mm}" : "";
    static FrameworkElement Facts(params (string, string)[] rows)
    {
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var k = Muted(rows[i].Item1); var v = T(rows[i].Item2, size: 14); v.TextWrapping = TextWrapping.Wrap; k.Margin = v.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1); g.Children.Add(k); g.Children.Add(v);
        }
        return g;
    }
    static Button Left(Button b) { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 14, 0, 0); return b; }

    void Draw(JsonNode d)
    {
        var kind = d.S("stato"); var staff = d.Bo("staff");
        var page = Col(F.Heading(null, "Licenza", "Cosa può fare questa installazione e il codice da dare a chi ti vende la licenza"));
        var head = kind == "licenza" ? Row(T("Licenza attiva", "H2"), MainWindow.Pill(d.S("id"), "ok").M(10, 0, 0, 0))
            : kind == "prova" ? Row(T("Versione di prova", "H2"), MainWindow.Pill($"{d.I("giorni")} giorn{(d.I("giorni") == 1 ? "o" : "i")}", "warn").M(10, 0, 0, 0)) : Row(T("Il programma è fermo", "H2"), MainWindow.Pill("Senza licenza", "danger").M(10, 0, 0, 0));
        var first = Col(head);
        if (kind == "licenza") { var p = new TextBlock { FontSize = 14, Margin = new Thickness(0, 6, 0, 0) }; p.Inlines.Add("Intestata a "); p.Inlines.Add(new System.Windows.Documents.Run(d.S("intestatario")) { FontWeight = FontWeights.SemiBold }); first.Children.Add(p); }
        else first.Children.Add(Muted(kind == "prova" ? $"Fino al {When(d.S("fine"))}: poi il programma chiede la licenza" : d.S("problema") + " Prenotazioni e clienti restano salvati, e i backup continuano").M(0, 6, 0, 0));
        if (d.S("problema") != "" && kind != "bloccato") first.Children.Add(F.Notice(d.S("problema"), "warning"));
        var facts = new List<(string, string)>(); if (kind == "licenza") facts.Add(("Tipo", d.S("tipo")));
        facts.Add(("Attività", d.S("attivita") + (d.I("attivita") > 1 ? ", versione portale" : ", versione singola")));
        facts.Add(("Postazioni in rete", d.Bo("standalone") ? "nessuna: installazione Un PC solo" : d.S("postazioni") + ", oltre al PC server"));
        if (kind == "licenza") { facts.Add(("Scadenza", d.S("scadenza") != "" ? d.S("scadenza") : "nessuna")); facts.Add(("Installazione", d.Bo("installazione") ? "solo questa" : "qualunque")); }
        first.Children.Add(Facts(facts.ToArray())); page.Children.Add(F.Card(first));

        if (d.Bo("online"))
            page.Children.Add(F.Card(T("Controllo online", "H2"), Muted("Una volta al giorno, quando c'è Internet, il programma guarda se chi te l'ha venduta ha cambiato la licenza: una licenza nuova per questo PC arriva da sola, una revocata smette di valere. Senza Internet la licenza in uso resta com'è"),
                T(d.S("controllo") != "" ? "Ultimo controllo il " + When(d.S("controllo")) : "Ancora nessun controllo riuscito", size: 14).M(0, 10, 0, 0), staff ? Left(F.Small(BA("Controlla ora", () => Act(new { azione = "online" })))) : null));

        // Aggiornamenti
        var mode = new ComboBox { ItemsSource = new[] { new ComboBoxItem { Content = "Avvisa quando c'è una versione nuova", Tag = "avvisa" }, new ComboBoxItem { Content = "Spento", Tag = "spento" } }, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        mode.SelectedIndex = d.S("modo") == "spento" ? 1 : 0;
        var updates = Col(T("Aggiornamenti", "H2"), Muted("Dopo l'avvio e una volta al giorno il programma guarda se c'è una versione nuova e te la propone: si installa solo quando premi Aggiorna ora, con la conferma di Windows. Prima di cambiare il database ne fa una copia"),
            Facts(("Versione installata", d.S("versione")), ("Ultimo controllo", (d.S("ultimo") != "" ? When(d.S("ultimo")) : "ancora nessuno") + (d.S("esito") != "" ? ": " + d.S("esito") : ""))));
        if (d["aggiorna"] is JsonObject up)
            updates.Children.Add(Row(BA(up.S("azione"), () => Updater.Run(up.S("url")), "Primary"), B(up.S("altroTesto"), () => Updater.Open(up.S("altro"))).M(10, 0, 0, 0)).M(0, 14, 0, 0));
        if (staff) { updates.Children.Add(F.Field("Versioni nuove", mode)); updates.Children.Add(Left(F.Small(BA("Salva e controlla ora", () => Act(new { azione = "aggiornamenti", modo = (string)((ComboBoxItem)mode.SelectedItem).Tag }))))); }
        page.Children.Add(F.Card(updates));

        var code = new TextBox { Text = d.S("codice"), IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 15, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0, 10, 0, 0) };
        page.Children.Add(F.Card(T("Codice installazione", "H2"), Muted("Mandalo a chi ti vende la licenza: così la licenza vale solo su questo PC"), code, Left(F.Small(B("Copia il codice", () => { Clipboard.SetText(d.S("codice")); Main.Flash("Copiato."); })))));
        if (staff)
        {
            var text = Box("", true); text.MinHeight = 96; text.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            page.Children.Add(F.Card(T(kind == "licenza" ? "Cambia o rinnova la licenza" : "Inserisci la licenza", "H2"), F.Field("Incolla la licenza, inizia con TDBS1.", text), Left(BA("Attiva la licenza", () => Act(new { azione = "licenza", licenza = text.Text }), "Primary"))));
            if (d.Bo("prova"))
                page.Children.Add(F.Card(T("Prova gratuita", "H2"), Muted("Sette giorni con tutte le funzioni, una attività e due postazioni in rete. Si attiva una volta sola: alla fine il programma chiede la licenza"), Left(BA("Attiva la prova di 7 giorni", () => Act(new { azione = "prova" })))));
        }
        else page.Children.Add(F.Card(T("La licenza la inserisce l'amministratore del programma.", size: 14)));
        Content = F.Page(1180, page);
    }
}

/// <summary>Aggiorna ora: the MSI of a public release, checked with the SHA-256 published next to it, then msiexec with
/// Windows' confirmation; this window closes so its files can change.</summary>
public static class Updater
{
    public static void Open(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
    public static async Task Run(string url)
    {
        // only the MSI of a public release of the program, never another address
        if (!System.Text.RegularExpressions.Regex.IsMatch(url, @"^https://github\.com/Giuseppe-sciarra/TD-Booking-Suite-per-Windows/releases/download/v[0-9.]+/TD-Booking-Suite-[0-9.]+\.msi\z")) return;
        if (!Ask("Scarico la versione nuova, la controllo e la installo. Il programma si chiude per qualche minuto: poi riaprilo.\n\nContinuo?")) return;
        var msi = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetFileName(new Uri(url).AbsolutePath));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("TD-Booking-Suite");
            var expected = System.Text.RegularExpressions.Regex.Match(await http.GetStringAsync(url + ".sha256"), "[0-9a-fA-F]{64}").Value.ToLowerInvariant();
            var data = await http.GetByteArrayAsync(url);
            if (expected.Length != 64 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant() != expected) { Say("Il file scaricato non corrisponde al suo SHA-256: non lo installo. Riprova più tardi."); return; }
            await System.IO.File.WriteAllBytesAsync(msi, data);
            // shortcut: no Authenticode check of its own while the MSI is unsigned, Windows shows the publisher in the confirmation
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /passive /norestart") { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException or System.ComponentModel.Win32Exception) { Say("Aggiornamento non riuscito: " + ex.Message); }
    }
}
