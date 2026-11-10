using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>tdwin/backup.html: Backup: the password, the nightly backup, where the copies go, the files and the history.</summary>
public sealed class BackupPage : Page
{
    string edit = "";
    public override async Task Load() => Draw(await Api.Current.Call("backup", new { modifica = edit }));
    async Task Act(object args) { var r = await Api.Current.Call("backup.azione", args); Main.Flash(r.S("testo")); await Load(); }

    static Grid Table(string[] head, double[] widths, List<FrameworkElement[]> rows)
    {
        var g = new Grid { Margin = new Thickness(0, 10, 0, 0) }; foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Cell(FrameworkElement e, int row, int col) { var b = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 12, 8), Child = e }; Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b); }
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); for (int i = 0; i < head.Length; i++) Cell(T(head[i], size: 13, bold: true, color: Res("Muted")), 0, i);
        for (int r = 0; r < rows.Count; r++) { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); for (int i = 0; i < rows[r].Length; i++) Cell(rows[r][i], r + 1, i); }
        return g;
    }

    void Draw(JsonNode d)
    {
        var set = d.Bo("impostata");
        var page = Col(F.Heading(null, "Backup", "Una copia cifrata di prenotazioni, clienti e impostazioni, ogni giorno, sul PC server e dove vuoi tu", BA("Esegui un backup ora", () => Act(new { azione = "esegui" }), "Primary")));

        var pw = new PasswordBox(); var pw2 = new PasswordBox();
        page.Children.Add(F.Card(Row(T("Password dei backup", "H2"), (set ? MainWindow.Pill("Impostata il " + d.S("impostata_il"), "ok") : MainWindow.Pill("Da impostare", "danger")).M(10, 0, 0, 0)),
            Prog.Text("Ogni backup è cifrato con questa password e contiene anche le chiavi del database: senza la password non si apre, nemmeno da chi ti ha installato il programma. Scrivila e conservala lontano dal PC" + (set ? ". Se la cambi, i backup già fatti si aprono ancora con quella vecchia" : "")).M(0, 8, 0, 0),
            F.Grid2(F.Field((set ? "Nuova password" : "Password") + ", almeno 12 caratteri", pw), F.Field("Ripeti la password", pw2)),
            Prog.Left(BA("Salva la password", () => Act(new { azione = "password", password = pw.Password, password2 = pw2.Password }), set ? null : "Primary"))));

        var on = new CheckBox { Content = T("Fai un backup ogni giorno", size: 14), IsChecked = d.Bo("attivo"), IsEnabled = set, Margin = new Thickness(0, 26, 0, 4) };
        var at = Box(d.S("ora")); at.MaxLength = 5; var keep = Box(d.S("conserva")); keep.MaxLength = 3;
        page.Children.Add(F.Card(Row(T("Backup automatico", "H2"), (d.Bo("attivo") ? MainWindow.Pill("Attivo", "ok") : MainWindow.Pill("Spento", "danger")).M(10, 0, 0, 0)),
            F.Cols(3, 22, Col(on, Prog.Muted("Se il PC è spento a quell'ora, il backup parte appena si riaccende", 13)), F.Field("Ora", at), F.Field("Copie da tenere sul PC server", keep, null, "In " + d.S("cartella"))),
            Prog.Left(BA("Salva", () => Act(new { azione = "impostazioni", attivo = on.IsChecked == true, ora = at.Text.Trim(), conserva_locali = keep.Text.Trim() }), "Primary"))));

        var dest = Col(F.Section("Dove copiare i backup", null, Prog.Muted("Ogni backup va anche qui, oltre che sul PC server")));
        var items = d.A("destinazioni").ToList();
        if (items.Count > 0)
            dest.Children.Add(Table(new[] { "Destinazione", "Tipo", "Ultima prova", "" }, new[] { 2.2, 1.1, 1.6, 2.1 }, items.Select(x =>
            {
                var id = x.S("id"); var name = Col(Row(T(x.S("nome"), size: 14, bold: true), x.Bo("attiva") ? null : MainWindow.Pill("In pausa", "").M(8, 0, 0, 0)), Prog.Muted(x.S("dettaglio"), 13));
                FrameworkElement last = x.S("esito") != "" ? Prog.Text(x.S("esito") + (x.S("controllo") != "" ? "  " + x.S("controllo") : "")) : Prog.Muted("mai");
                var actions = new WrapPanel();
                actions.Children.Add(F.Small(BA("Prova", () => Act(new { azione = "prova", id }), "Ghost")));
                actions.Children.Add(F.Small(BA("Modifica", () => { edit = id; return Load(); }, "Ghost")));
                if (x.S("impronta") != "") { var reset = F.Small(BA("Azzera impronta", () => Act(new { azione = "impronta", id }), "Ghost")); reset.ToolTip = x.S("impronta"); actions.Children.Add(reset); }
                actions.Children.Add(F.Small(BA("Elimina", () => Act(new { azione = "elimina", id }), "Ghost")));
                return new FrameworkElement[] { name, T(x.S("tipo"), size: 14), last, actions };
            }).ToList()));
        else dest.Children.Add(Prog.Muted("Nessuna destinazione: i backup restano solo sul PC server, e se il PC si rompe si perdono con lui. Aggiungine almeno una qui sotto").M(0, 10, 0, 0));
        page.Children.Add(F.Card(dest));

        var editing = d["modifica"]; var form = new FormView(d.A("campi"));
        var save = new Button { Content = editing != null ? "Salva e prova" : "Aggiungi e prova", Style = Style("Primary"), Margin = new Thickness(0, 14, 14, 0) };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await Guard(async () => { var r = await Api.Current.Call("backup.azione", new JsonObject { ["azione"] = "destinazione", ["id"] = edit, ["valori"] = form.Values() }); Main.Flash(r.S("testo")); edit = ""; await Load(); }); }
            catch (FormError ex) { form.Errors(ex.Errors); Main.Flash("Controlla i campi della destinazione.", true); }
            finally { save.IsEnabled = true; }
        };
        var buttons = Row(save);
        if (editing != null) { var cancel = F.Link("Annulla", () => { edit = ""; _ = Guard(Load); }); cancel.VerticalAlignment = VerticalAlignment.Center; cancel.Margin = new Thickness(0, 14, 0, 0); buttons.Children.Add(cancel); }
        page.Children.Add(F.Card(T(editing != null ? "Modifica " + editing.S("nome") : "Aggiungi una destinazione", "H2"), form.Panel, buttons));

        var files = d.A("file").ToList();
        page.Children.Add(F.Card(T("Backup sul PC server", "H2"), files.Count > 0
            ? Table(new[] { "File", "Data", "Dimensione", "" }, new[] { 3.0, 1.3, 1.0, 0.7 }, files.Select(f =>
            {
                var name = f.S("nome");
                return new FrameworkElement[] { Prog.Code(name), T(f.S("data"), size: 14), T(f.S("dimensione"), size: 14), F.Link("Scarica", () => _ = Guard(async () => Downloads.Save(name, await Api.Current.Bytes("backup.scarica", new { nome = name })))) };
            }).ToList())
            : Prog.Muted("Ancora nessun backup").M(0, 10, 0, 0)));

        var runs = d.A("storico").ToList();
        page.Children.Add(F.Card(T("Storico", "H2"), runs.Count > 0
            ? Table(new[] { "Quando", "Esito", "Dettagli" }, new[] { 1.2, 0.9, 4.0 }, runs.Select(r =>
            {
                var pill = r.S("esito") switch { "ok" => MainWindow.Pill("Riuscito", "ok"), "parziale" => MainWindow.Pill("In parte", "danger"), "errore" => MainWindow.Pill("Non riuscito", "danger"), _ => MainWindow.Pill("In corso", "warn") };
                pill.HorizontalAlignment = HorizontalAlignment.Left; pill.VerticalAlignment = VerticalAlignment.Top;
                return new FrameworkElement[] { Col(T(r.S("quando"), size: 14), r.Bo("manuale") ? Prog.Muted("a mano", 13) : null), pill, Col(Prog.Text(r.S("dettagli")), r.S("dimensione") != "" ? Prog.Muted(r.S("dimensione"), 13) : null) };
            }).ToList())
            : Prog.Muted("Nessun backup eseguito").M(0, 10, 0, 0)));

        page.Children.Add(F.Card(T("Come fare", "H2"),
            F.Details("Disco esterno o chiavetta", Prog.Text(@"Collega il disco, guarda la lettera in Esplora file (per esempio E:) e aggiungi una destinazione Cartella o disco esterno con percorso E:\Backup TD Booking. Se il disco è staccato all'ora del backup, la copia sul PC server si fa lo stesso e nello storico vedi la destinazione non riuscita. Per avere sempre una copia fuori dall'ufficio usa due dischi e alternali ogni settimana")),
            F.Details("Cartella di rete o NAS", Prog.Text(@"Sul NAS crea una cartella condivisa e un utente che può scriverci. Aggiungi una destinazione Cartella di rete con percorso \\nome-del-nas\cartella, l'utente e la password del NAS. Non serve collegare l'unità di rete in Windows: il programma entra da solo")),
            F.Details("FTP, FTPS o SFTP", Prog.Text("Usa l'indirizzo, l'utente e la password del server, e una cartella solo per questi backup. Preferisci SFTP o FTPS: FTP semplice manda la password in chiaro. Al primo collegamento il programma memorizza la chiave o il certificato del server e poi controlla che sia sempre lo stesso")),
            F.Details("Ripristinare un backup", Prog.Text("Sul PC server apri il menu Start, TD Booking Suite, Ripristina backup, scegli il file .tdbk e scrivi la password dei backup. Il programma si ferma per un minuto, rimette i dati del backup e riparte. Il database di prima resta da parte, non viene cancellato. Su un PC nuovo installa prima il programma, poi ripristina: dopo, chiedi una nuova licenza con il nuovo codice installazione"))));
        Content = F.Page(1180, page);
    }
}
