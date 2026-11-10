using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

static class Prog
{
    public static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    public static TextBox Code(string text, double size = 14) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = size, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0) };
    public static Button Left(Button b) { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 14, 0, 0); return b; }
    public static Grid Facts(params (string Key, FrameworkElement Value)[] rows)
    {
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var k = Muted(rows[i].Key); k.Margin = rows[i].Value.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(k, i); Grid.SetRow(rows[i].Value, i); Grid.SetColumn(rows[i].Value, 1); g.Children.Add(k); g.Children.Add(rows[i].Value);
        }
        return g;
    }
    public static TextBlock Text(string text) { var t = T(text, size: 14); t.TextWrapping = TextWrapping.Wrap; return t; }
}

/// <summary>tdwin/postazioni.html: Postazioni: the PCs, browsers and phones that open the program from the network.</summary>
public sealed class WorkstationsPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("postazioni", new { }));
    async Task Revoke(string id) { var r = await Api.Current.Call("postazioni.azione", new { azione = "revoca", id }); Main.Flash(r.S("testo")); await Load(); }

    void Draw(JsonNode d)
    {
        var page = Col(F.Heading(null, "Postazioni", "I PC, i browser e i telefoni che aprono il programma dalla rete. Il PC server non conta e non va registrato"));
        if (d.Bo("standalone")) page.Children.Add(F.Notice("Questa installazione è Un PC solo: il programma si usa solo da questo PC, non ci sono postazioni. Per cambiare apri di nuovo l'installazione (Impostazioni di Windows, App, TD Booking Suite, Modifica) e scegli Server con postazioni: i dati restano.", "warning"));
        if (d.S("codice") != "")
        {
            var code = d.S("codice"); var shown = code[..3] + " " + code[3..];
            page.Children.Add(F.Card(T("Codice per la nuova postazione", "H2"), Prog.Text($"Sulla postazione apri il programma o il browser su {d.S("ufficio")}" + (d.S("fuori") != "" ? $", o da fuori su {d.S("fuori")}" : "") + $", e inserisci questo codice entro {d.I("minuti")} minuti").M(0, 6, 0, 0),
                Prog.Code(shown, 34).M(0, 12, 0, 0), Prog.Left(F.Small(B("Copia il codice", () => { Clipboard.SetText(shown); Main.Flash("Copiato."); })))));
        }
        var items = d.A("registrate").ToList();
        var list = Col(F.Section("Registrate", null, Prog.Muted($"{items.Count} di {d.I("limite")} consentite dalla licenza")));
        if (items.Count > 0)
        {
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) }; foreach (var w in new[] { 2.0, 1.2, 1.2, 1.2, 0.8 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            void Cell(FrameworkElement e, int row, int col) { var b = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 12, 8), Child = e }; Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b); }
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var heads = new[] { "Nome", "Registrata", "Ultimo accesso", "Indirizzo", "" };
            for (int i = 0; i < heads.Length; i++) Cell(T(heads[i], size: 13, bold: true, color: Res("Muted")), 0, i);
            var r = 1;
            foreach (var p in items)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var id = p.S("id");
                Cell(Row(T(p.S("nome"), size: 14, bold: true), p.Bo("attiva") ? null : MainWindow.Pill("Oltre il limite", "danger").M(8, 0, 0, 0)), r, 0); Cell(T(p.S("abbinata"), size: 14), r, 1);
                Cell(p.S("accesso") != "" ? T(p.S("accesso"), size: 14) : Prog.Muted("mai"), r, 2); Cell(T(p.S("ip"), size: 14), r, 3); Cell(F.Small(BA("Revoca", () => Revoke(id), "Ghost")), r, 4); r++;
            }
            list.Children.Add(g);
        }
        else list.Children.Add(Prog.Muted("Nessuna postazione registrata: per ora il programma si usa solo dal PC server").M(0, 10, 0, 0));
        var waiting = d.A("attesa").ToList();
        if (waiting.Count > 0)
        {
            list.Children.Add(T("In attesa del codice", size: 17, bold: true).M(0, 16, 0, 4));
            foreach (var p in waiting) { var id = p.S("id"); list.Children.Add(Row(Prog.Text($"{p.S("nome")}, codice valido fino alle {p.S("fino")}"), F.Small(BA("Annulla", () => Revoke(id), "Ghost")).M(10, 0, 0, 0)).M(0, 4, 0, 4)); }
        }
        page.Children.Add(F.Card(list));
        var name = Box(); name.MaxLength = 80; name.Tag = "Reception, PC di Marco, Tablet sala"; AgendaPage.Hint(name);
        page.Children.Add(F.Card(T("Aggiungi una postazione", "H2"), F.Field("Nome, per riconoscerla", name), Prog.Left(BA("Genera il codice", async () => Draw(await Api.Current.Call("postazioni.azione", new { azione = "nuova", nome = name.Text })), "Primary"))));
        var facts = new List<(string, FrameworkElement)> { ("Indirizzo in ufficio", Prog.Code(d.S("ufficio"))) }; if (d.S("fuori") != "") facts.Add(("Indirizzo da fuori", Prog.Code(d.S("fuori"))));
        facts.Add(("Impronta del certificato", Prog.Code(d.S("impronta") != "" ? d.S("impronta") : "non ancora creato")));
        var note = Prog.Muted("Il programma per le postazioni mostra la stessa impronta al primo collegamento: se è uguale, il collegamento è quello giusto. Per i browser e i telefoni puoi installare ");
        note.Inlines.Add(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("il certificato")) { Command = new Relay(() => _ = Guard(async () => Downloads.Save("TD-Booking-Suite-CA.crt", await Api.Current.Bytes("postazioni.certificato", new { })))) });
        note.Inlines.Add(" tra le autorità attendibili");
        page.Children.Add(F.Card(T("Indirizzo e certificato", "H2"), Prog.Facts(facts.ToArray()), note.M(0, 10, 0, 0)));
        Content = F.Page(1180, page);
    }
}

/// <summary>tdwin/rete.html: Rete e certificato (Online e sito on Un PC solo): the public address, the proxies, the certificate.</summary>
public sealed class NetworkPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("rete", new { }));
    async Task Act(object args) { var r = await Api.Current.Call("rete.azione", args); Main.Flash(r.S("testo")); await Load(); }
    static string Pick(string filter) { var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter }; return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null; }

    void Draw(JsonNode d)
    {
        bool standalone = d.Bo("standalone"), online = d.Bo("online"); var port = d.S("porta");
        var page = Col(F.Heading(null, standalone ? "Online e sito" : "Rete e certificato", standalone ? "La pagina prenotazioni per i clienti e il modulo nel sito WordPress: spenti, il programma risponde solo a questo PC"
            : "Come si arriva al programma da fuori dall'ufficio: l'indirizzo pubblico, il proxy davanti al server e il certificato che il server mostra"));
        if (standalone)
        {
            var status = Prog.Muted(""); status.Margin = new Thickness(12, 0, 0, 0); status.VerticalAlignment = VerticalAlignment.Center;
            var toggle = B(online ? "Spegni" : "Accendi", () =>
            {
                // the switch asks Windows for permission and reconfigures this PC (setup-server.ps1 -Online on/off)
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{d.S("script")}\" -Online {(online ? "off" : "on")}") { UseShellExecute = true, Verb = "runas" });
                    status.Text = "Conferma nella finestra di Windows che chiede l'autorizzazione. Poi il programma riparte: riaprilo.";
                }
                catch (System.ComponentModel.Win32Exception ex) { status.Text = "Non riuscito: " + ex.Message; }
            }, online ? null : "Primary");
            page.Children.Add(F.Card(F.Section("Prenotazioni online e sito", null, MainWindow.Pill(online ? "Accese" : "Spente", online ? "ok" : "danger")),
                Prog.Text(online ? $"Il PC risponde anche dalla rete sulla porta {port}: la pagina prenotazioni e il modulo nel sito funzionano. Da fuori si raggiungono solo quelle: agenda, clienti e configurazione restano su questo PC. Per Internet il router inoltra la porta {port} a questo PC, e qui sotto scrivi l'indirizzo pubblico."
                    : $"Il programma risponde solo a questo PC: niente pagina prenotazioni per i clienti e niente modulo nel sito. Accese, il PC risponde anche dalla rete sulla porta {port}, come un server, ma da fuori si raggiungono solo la pagina prenotazioni e il collegamento del sito.").M(0, 8, 0, 12),
                Row(toggle, status)));
        }
        if (!standalone || online)
        {
            foreach (var w in d.A("avvisi")) page.Children.Add(F.Notice(w.ToString(), "warning"));
            var origin = Box(d.S("indirizzo")); origin.MaxLength = 200; origin.Tag = "https://booking.tuosito.it"; AgendaPage.Hint(origin);
            var proxy = Box(d.S("proxy"), true); proxy.MinHeight = 74; proxy.Tag = "192.168.1.10"; AgendaPage.Hint(proxy);
            var addresses = F.Card(T("Indirizzi", "H2"), Prog.Facts(("In ufficio", Prog.Code(d.S("ufficio"))), ("Da fuori", d.S("fuori") != "" ? Prog.Code(d.S("fuori")) : Prog.Muted("non impostato"))),
                F.Field("Indirizzo pubblico", origin, null, "Quello dei link nelle email (conferme, consensi, inviti) e da usare fuori dall'ufficio. Il nome deve portare a questo server, o al proxy davanti. Vuoto: si usa l'indirizzo dell'ufficio"),
                F.Field("Proxy fidati", proxy, null, "Gli indirizzi IP dei proxy davanti al server, uno per riga, anche come rete (10.0.0.0/24). Da loro il programma legge l'indirizzo vero di chi si collega, per i limiti di accesso e l'elenco delle postazioni. Vuoto se non c'è un proxy"),
                Prog.Left(BA("Salva", () => Act(new { azione = "indirizzi", indirizzo = origin.Text, proxy = proxy.Text }), "Primary")));
            var steps = Col(T("Con un proxy davanti", "H2"));
            foreach (var (n, s) in new[] { "Nel proxy (NPMplus, Nginx Proxy Manager, Caddy, IIS) crea l'host con il nome pubblico e il suo certificato, per esempio Let's Encrypt", $"Destinazione https:// IP di questo PC, porta {port}: schema https, senza controllare il certificato del server", "Lascia l'intestazione Host com'è e aggiungi X-Forwarded-For: NPMplus lo fa già", "Qui accanto scrivi l'indirizzo pubblico e l'IP del proxy" }.Select((s, i) => (i + 1, s)))
                steps.Children.Add(Prog.Text($"{n}. {s}").M(0, 6, 0, 0));
            steps.Children.Add(Prog.Muted($"Senza proxy: apri la porta {port} verso questo PC sul router e carica qui sotto un certificato per il nome pubblico").M(0, 10, 0, 0));
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            var aside = F.Card(steps); aside.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(aside, 2); grid.Children.Add(addresses); grid.Children.Add(aside); page.Children.Add(grid);

            var own = d.Bo("proprio"); string certFile = null, keyFile = null; var certName = Prog.Muted("Nessun file scelto", 13); var keyName = Prog.Muted("Nessun file scelto", 13); var password = new PasswordBox();
            FrameworkElement File(string label, string filter, TextBlock name, Action<string> set) => F.Field(label, Row(F.Small(B("Scegli il file", () => { if (Pick(filter) is string f) { set(f); name.Text = System.IO.Path.GetFileName(f); } })), name.M(10, 0, 0, 0)));
            certName.VerticalAlignment = keyName.VerticalAlignment = VerticalAlignment.Center;
            var cert = Col(Row(T("Certificato", "H2"), MainWindow.Pill(own ? "Caricato" : "Interno", own ? "ok" : "").M(10, 0, 0, 0)),
                Prog.Facts(("Per", Prog.Text(d.S("soggetto"))), ("Nomi", Prog.Text(d.S("nomi") != "" ? d.S("nomi") : "—")), ("Emesso da", Prog.Text(d.S("emittente"))), ("Scade", Prog.Text(d.S("scadenza"))), ("Impronta", Prog.Code(d.S("impronta")))),
                Prog.Muted(own ? "Il programma per le postazioni accetta da solo un certificato riconosciuto da Windows; con uno autofirmato mostra questa impronta al primo collegamento."
                    : "Il certificato interno lo crea e lo rinnova il programma: vale in ufficio, dove le postazioni lo riconoscono dall'impronta. Da fuori i browser non lo conoscono.").M(0, 10, 0, 0),
                F.Grid2(File("Certificato: .pfx, .p12, oppure .crt o .pem", "Certificati|*.pfx;*.p12;*.pem;*.crt;*.cer", certName, f => certFile = f), File("Chiave privata (.key), se non è nel file", "Chiavi|*.key;*.pem", keyName, f => keyFile = f), F.Field("Password del file, se c'è", password)),
                Prog.Muted("Con la catena completa: il certificato del server e quelli dell'autorità che lo ha firmato. Il programma controlla che chiave e certificato vadano insieme e che non sia scaduto; un mese prima della scadenza lo ricorda in cima alle pagine").M(0, 10, 0, 0),
                Prog.Left(BA("Carica e usa", () =>
                {
                    string B64(string f) { if (f == null) return ""; var data = System.IO.File.ReadAllBytes(f); if (data.Length > 200_000) throw new ApiError("File troppo grande per un certificato."); return Convert.ToBase64String(data); }
                    return Act(new { azione = "certificato", certificato = B64(certFile), chiave = B64(keyFile), password = password.Password });
                }, "Primary")));
            if (d.Bo("caricato")) cert.Children.Add(Prog.Left(BA("Torna al certificato interno", async () => { if (Ask("Tornare al certificato interno? Da fuori i browser daranno un avviso.")) await Act(new { azione = "interno" }); }, "Ghost")));
            page.Children.Add(F.Card(cert));
        }
        Content = F.Page(1180, page);
    }
}

/// <summary>tdwin/stato.html: Stato del programma: the pages that took more than a few seconds.</summary>
public sealed class StatePage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("stato", new { }));
    void Draw(JsonNode d)
    {
        var day = d.I("giorno"); var rows = d.A("righe").Select(r => new[] { r.S("quando"), r.S("dove"), r.S("pagina"), r.S("secondi") }).ToList();
        var card = Col(Row(T("Ultime 24 ore", "H2"), (day > 0 ? MainWindow.Pill($"{day} lent{(day == 1 ? "a" : "e")}", "danger") : MainWindow.Pill("Tutto regolare", "ok")).M(10, 0, 0, 0)),
            Prog.Muted("«Programma»: la pagina ci ha messo più di 3 secondi a rispondere. «Finestra»: la finestra l'ha aspettata più di 5 secondi, anche se il programma aveva già risposto (per esempio la prima volta di un'azione, l'antivirus, la finestra stessa). Per l'assistenza premi Scarica i log e manda il file").M(0, 6, 0, 10),
            rows.Count > 0 ? F.Table(new[] { "Quando", "Dove", "Pagina", "Secondi" }, rows) : Prog.Text("Nessuna pagina lenta registrata."));
        Content = F.Page(1180, Col(F.Heading(null, "Stato del programma", $"Le pagine che ci hanno messo più di qualche secondo, scritte dal programma da solo. Versione {d.S("versione")}",
            BA("Scarica i log per l'assistenza", async () => Downloads.Save($"log-td-booking-suite-{DateTime.Now:yyyy-MM-dd}.zip", await Api.Current.Bytes("stato.log", new { })))), F.Card(card)));
    }
}

sealed class Relay(Action run) : System.Windows.Input.ICommand
{
    public event EventHandler CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object p) => true;
    public void Execute(object p) => run();
}
