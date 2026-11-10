using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/cassa_settings.html: Registratore di cassa: the register of each location (device, how it is
/// connected, departments, payments, RCH commands), Cerca la cassa, Prova collegamento, Stampa di prova, the bridge.</summary>
public sealed class RegisterPage : Page
{
    AgendaPage z;
    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }.Also(Business.S("fuso"));
        Draw(await Api.Current.Call("registratore", new { }));
    }
    static TextBlock Muted(string text, double size = 13) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static StackPanel Fld(string label, FrameworkElement input, string small = null) => Col(F.Label(label), input, small == null ? null : Muted(small).M(0, 4, 0, 0));
    static ComboBox Pick(IEnumerable<(string Id, string Label)> items, string selected)
    {
        var c = new ComboBox { ItemsSource = items.Select(i => new ComboBoxItem { Content = i.Label, Tag = i.Id }).ToList() };
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? c.Items[0]; return c;
    }
    static string Val(ComboBox c) => (string)(c.SelectedItem as ComboBoxItem)?.Tag ?? "";
    static void Set(ComboBox c, string v) { if (c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == v) is ComboBoxItem i) c.SelectedItem = i; }

    async Task Act(object args)
    {
        var r = await Api.Current.Call("registratore.azione", args);
        if (r["sedi"] != null) { Draw(r); return; }
        Main.Flash(r.S("testo")); await Load();
    }

    void Draw(JsonNode d)
    {
        var direct = d.Bo("diretto"); var devices = d.A("dispositivi").Select(x => (x.S("id"), x.S("nome"))).ToList();
        var heading = F.Heading(null, "Registratore di cassa", null);
        ((StackPanel)heading.Children[^1]).Children.Add(WaitRows.Para(14, "Qui colleghi al programma la cassa di ogni sede, una volta sola. È la cassa a stampare e trasmettere lo scontrino: il programma non fa mai scontrini da solo. ", ((string, Action))("I passi, in ordine", () => Main.Show("guida"))));
        var page = Col(heading); var any = false;
        foreach (var c in d.A("sedi"))
        {
            any = true; var id = c.S("id"); var conf = c["conf"]; var device = conf.S("device"); var body = Col();
            // ----- the head: what is connected now
            var now = device == "" ? "Nessun registratore" : string.Join(" · ", devices.First(x => x.Item1 == device).Item2, conf.S("connection") == "usb" ? "USB " + conf.S("port") : conf.S("address"),
                conf.S("link") == "direct" ? "collegato da questo computer" : "tramite il ponte cassa" + (c.S("visto") != "" ? $", ultimo contatto {z.Local(c.S("visto")):dd/MM HH:mm}" : ", mai collegato"));
            body.Children.Add(Col(T(c.S("nome"), "H2"), Muted(now, 14)));
            // ----- the form (made first: «Usa questa» fills it)
            var dev = Pick(devices, device); var conn = Pick(new[] { ("net", "Rete (indirizzo IP)"), ("usb", "USB / seriale (solo RCH)") }, conf.S("connection") == "usb" ? "usb" : "net");
            var ip = Box(conf.S("address")); ip.Tag = "192.168.1.50"; AgendaPage.Hint(ip);
            var port = F.Suggest(d.A("porte").Select(p => p.ToString()), conf.S("port"), 30);
            var baud = Pick(d.A("velocita").Select(b => (b.ToString(), b.ToString())), conf.S("baud")); var framing = Pick(new[] { ("stx", "RCH con conferma (STX/ETX, ACK)"), ("cr", "Comandi semplici, senza conferma") }, conf.S("framing") == "cr" ? "cr" : "stx");
            var serial = Box(conf.S("serial")); serial.MaxLength = 20; var lastZ = Box(conf.S("last_z")); lastZ.MaxLength = 4; lastZ.Tag = "es. 2026"; AgendaPage.Hint(lastZ);
            var link = Pick(new[] { ("direct", "Da questo computer, nella stessa rete"), ("bridge", "Tramite il ponte cassa") }, conf.S("link") == "bridge" ? "bridge" : "direct");
            var dd = Box(conf.S("default_dept") != "" ? conf.S("default_dept") : "1"); dd.MaxLength = 2; var pd = Box(conf.S("product_dept")); pd.MaxLength = 2; pd.Tag = "come le righe libere"; AgendaPage.Hint(pd); var gd = Box(conf.S("gift_dept")); gd.MaxLength = 2; gd.Tag = "come le righe libere"; AgendaPage.Hint(gd);
            // ----- Cerca la cassa
            if (direct)
            {
                body.Children.Add(Row(F.Small(BA("Cerca la cassa", () => Act(new { sede = id, azione = "discover" }))), Muted(" Cerca le casse RCH ed Epson nella rete di questo computer e le porte USB: una decina di secondi").M(8, 0, 0, 0)).M(0, 12, 0, 0));
                if (c["trovate"] is JsonNode found)
                {
                    var box = Col(); var net = found.A("rete").ToList(); var usb = found.A("porte").ToList();
                    if (net.Count > 0)
                    {
                        box.Children.Add(T("Casse trovate in rete", size: 13.5, bold: true));
                        foreach (var f in net)
                            box.Children.Add(Row(T($"{(f.S("dispositivo") == "rch" ? "RCH Print!F" : "Epson")} · {f.S("indirizzo")}", size: 13.5).M(0, 0, 10, 0),
                                F.Small(B("Usa questa", () => { Set(dev, f.S("dispositivo")); Set(conn, "net"); ip.Text = f.S("indirizzo"); dev.BringIntoView(); }))).M(0, 6, 0, 0));
                    }
                    else box.Children.Add(Muted("Nessuna cassa in rete. È accesa, con il cavo di rete, nella stessa rete di questo computer e con il web server acceso (RCH: SRV › Connettività › Ethernet)?", 13.5));
                    if (usb.Count > 0)
                    {
                        box.Children.Add(T("Porte USB / seriali", size: 13.5, bold: true).M(0, 10, 0, 0));
                        foreach (var p in usb)
                            box.Children.Add(Row(T($"{p.S("porta")} {p.S("descrizione")}".Trim(), size: 13.5).M(0, 0, 10, 0),
                                F.Small(B("Usa questa porta", () => { Set(dev, "rch"); Set(conn, "usb"); port.Text = p.S("porta"); dev.BringIntoView(); }, "Ghost"))).M(0, 6, 0, 0));
                        box.Children.Add(Muted("Una porta USB non dice da sola se è la cassa: è quella che compare quando la attacchi. Se la cassa ha anche il cavo di rete, usa la rete").M(0, 8, 0, 0));
                    }
                    box.Children.Add(Muted("«Usa» compila i campi qui sotto: controlla e premi Salva").M(0, 8, 0, 0));
                    body.Children.Add(F.Notice(box));
                }
            }
            // ----- Prova collegamento, Stampa scontrino di prova
            if (device != "")
            {
                body.Children.Add(Row(F.Small(BA("Prova collegamento", () => Act(new { sede = id, azione = "probe" }))), F.Small(BA("Stampa scontrino di prova", () => Act(new { sede = id, azione = "test" }), "Ghost")).M(8, 0, 8, 0),
                    Muted("Dopo la prova fai un primo scontrino di pochi centesimi e annullalo")).M(0, 12, 0, 0));
                if (c["ultima"] is JsonNode last)
                    body.Children.Add(Muted($"{last.S("tipo")} del {z.Local(last.S("quando")):dd/MM HH:mm}: {(last.Bo("fatto") ? "la cassa risponde" : last.S("stato"))}" + (last.S("errore") != "" ? " · " + last.S("errore") : "")).M(0, 6, 0, 0));
            }
            var fields = new List<FrameworkElement>
            {
                Fld("Registratore", dev),
                Fld("Collegata con", conn, "Rete: la cassa ha il cavo di rete. USB: la cassa è attaccata con il cavo a " + (direct ? "questo computer, o a quello del ponte cassa" : "un computer del negozio, quello del ponte cassa")),
                Fld("Indirizzo IP nella rete del negozio", ip, "Solo per la rete. Su RCH lo trovi nel menu della cassa: SRV › Connettività › Ethernet, con il web server acceso. Su Epson chiedilo al tecnico. Meglio un indirizzo fisso"),
                Fld("Porta USB / seriale", port, "Solo per l'USB. Su Windows: tasto destro su Start, Gestione dispositivi, Porte (COM e LPT). Il ponte le elenca con python ponte_cassa.py --porte"),
                Fld("Velocità USB", baud), Fld("Protocollo USB", framing, "Lascia «con conferma». Senza conferma ogni scontrino resta da verificare a mano"),
                Fld("Matricola", serial, "Sulla targhetta della cassa e in fondo agli scontrini. Su Epson serve per annullare"),
                Fld("Ultima chiusura giornaliera n.", lastZ, "Solo RCH: la cassa non dice al programma i numeri dei documenti, li conta il programma. Scrivi il numero stampato sull'ultima chiusura: da lì conta le chiusure; i documenti li numera di seguito come la cassa, che non riparte da 1 alla chiusura. Se la cassa ha fatto scontrini da sola, correggi una volta il numero in Annulla (o in È uscito) con quello stampato in fondo, es. 2039-0013: il programma impara chiusura e scarto e da lì numera giusto"
                    + (conf.S("doc_offset") is var off && off != "" && off != "0" ? $" (documenti fatti dalla cassa da sola: {off})" : "")),
            };
            if (direct) fields.Add(Fld("Collegamento", link));
            fields.Add(Fld("Reparto per le righe libere", dd, "Il reparto della cassa per le righe scritte a mano in Cassa")); fields.Add(Fld("Reparto dei prodotti", pd, "Per i prodotti venduti in Cassa (shampoo, spazzole…)"));
            fields.Add(Fld("Reparto delle gift card", gd, "Il reparto della vendita di una gift card: quale IVA usare (o se non soggetta) lo dice il commercialista"));
            body.Children.Add(F.Grid2(fields.ToArray()).M(0, 8, 0, 0));
            // ----- RCH commands
            var rch = new Dictionary<string, TextBox>();
            if (device == "rch")
            {
                TextBox Cmd(string k) { var b = Box(c["rch"].S(k)); b.MaxLength = 80; rch[k] = b; return b; }
                body.Children.Add(F.Details("Comandi RCH da provare: sconto di riga, annullo, chiusura", Col(
                    Muted("Il manuale dei comandi RCH si legge solo con un account RCH: questi sono di serie e vanno provati sulla cassa. Se la cassa dà errore, chiedi al tecnico RCH il comando giusto e scrivilo qui. Più comandi: separati da uno spazio").M(0, 8, 0, 0),
                    F.Grid2(Fld("Sconto di riga", Cmd("rch_off"), "{importo} diventa i centesimi dello sconto. Vuoto: la riga SCONTO non esce, il prezzo esce già scontato"),
                        Fld("Annullo", Cmd("rch_void"), "{data} giorno mese anno (241026), {z} la chiusura e {numero} il documento, a 4 cifre. Vuoto: l'annullo si fa dalla cassa e qui si segna"),
                        Fld("Chiusura giornaliera", Cmd("rch_close"), "Vuoto: la chiusura si fa dalla cassa"), null))).M(0, 14, 0, 0));
            }
            // ----- Pagamenti
            body.Children.Add(T("Pagamenti", size: 16, bold: true).M(0, 18, 0, 0));
            body.Children.Add(Muted("Il numero di ogni pagamento sulla cassa: te lo dà il tecnico della cassa. Vuoto: RCH contanti 1, carta 4, altro 2, da pagare 2 (non riscosso); Epson 0, 2, 5 e 5. Se usate «Altro» per Satispay, bonifico o buoni, mettete il numero che ha quel pagamento sulla cassa: vuoto vale non riscosso"));
            var pays = c.A("pagamenti").Select(p => { var b = Box(p.S("valore")); b.MaxLength = 2; return (Id: p.S("id"), Box: b, Field: (FrameworkElement)Fld(p.S("nome"), b)); }).ToList();
            body.Children.Add(F.Grid2(pays.Select(p => p.Field).ToArray()));
            // ----- Reparti diversi per singoli servizi
            var depts = new List<(string Id, TextBox Dept, TextBox Vat)>(); var services = c.A("servizi").ToList(); var set = c.I("repartiImpostati");
            if (services.Count > 0)
            {
                var g = new Grid(); foreach (var w in new[] { 3.0, 1, 1 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
                void Put(FrameworkElement e, int row, int col, bool th) { var cell = new Border { Child = e, Padding = new Thickness(12, th ? 10 : 8, 12, th ? 10 : 8), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, th ? 2 : 1) }; Grid.SetRow(cell, row); Grid.SetColumn(cell, col); g.Children.Add(cell); }
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var heads = new[] { "Servizio", "Reparto", "IVA" };
                for (int i = 0; i < 3; i++) Put(T(heads[i], size: 13, bold: true, color: Res("Muted")), 0, i, true);
                for (int r = 0; r < services.Count; r++)
                {
                    var s = services[r]; var dept = Box(s.S("reparto")); dept.MaxLength = 2; dept.Tag = dd.Text; AgendaPage.Hint(dept); var vat = Box(s.S("iva")); vat.MaxLength = 8; vat.Tag = "22%"; AgendaPage.Hint(vat);
                    depts.Add((s.S("id"), dept, vat)); g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var name = T(s.S("nome"), size: 14); name.VerticalAlignment = VerticalAlignment.Center; Put(name, r + 1, 0, false); Put(dept, r + 1, 1, false); Put(vat, r + 1, 2, false);
                }
                body.Children.Add(F.Details("Reparti diversi per singoli servizi (facoltativo" + (set > 0 ? $": {set} impostat{(set == 1 ? "o" : "i")}" : "") + ")", Col(
                    Muted("Di solito non serve: tutti i servizi vanno sul reparto delle righe libere qui sopra. Solo se un servizio deve andare su un altro reparto della cassa (altra IVA), scrivi qui il suo numero: te lo dà il tecnico della cassa. La colonna IVA è solo un promemoria per voi").M(0, 8, 0, 8), g), set > 0).M(0, 14, 0, 0));
            }
            var save = new Button { Content = "Salva", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
            save.Click += async (_, _) =>
            {
                var args = new JsonObject
                {
                    ["sede"] = id, ["azione"] = "save", ["device"] = Val(dev), ["connection"] = Val(conn), ["address"] = ip.Text, ["port"] = port.Text, ["baud"] = Val(baud), ["framing"] = Val(framing), ["serial"] = serial.Text, ["last_z"] = lastZ.Text,
                    ["link"] = direct ? Val(link) : "bridge", ["default_dept"] = dd.Text, ["product_dept"] = pd.Text, ["gift_dept"] = gd.Text,
                    ["reparti"] = new JsonObject(depts.Select(x => KeyValuePair.Create(x.Id, (JsonNode)x.Dept.Text))), ["iva"] = new JsonObject(depts.Select(x => KeyValuePair.Create(x.Id, (JsonNode)x.Vat.Text))),
                };
                foreach (var (k, b) in rch) args[k] = b.Text;
                foreach (var p in pays) args["pay_" + p.Id] = p.Box.Text;
                save.IsEnabled = false;
                try { await Guard(() => Act(args)); } finally { save.IsEnabled = true; }
            };
            body.Children.Add(save);
            // ----- Ponte cassa
            if (device != "" && conf.S("link") == "bridge")
            {
                body.Children.Add(T("Ponte cassa", size: 16, bold: true).M(0, 18, 0, 0));
                body.Children.Add(WaitRows.Para(13, "Un piccolo programma su un computer del negozio, nella stessa rete della cassa o con la cassa attaccata via USB: prende gli scontrini da stampare e li passa alla cassa. 1. Installa Python 3 da python.org (spunta Add python.exe to PATH); per l'USB anche pip install pyserial. 2. Crea il codice qui sotto e copialo. 3. ",
                    ((string, Action))("Scarica ponte_cassa.py", async () => await Guard(async () => Downloads.Save("ponte_cassa.py", await Api.Current.Bytes("registratore.ponte", new { })))),
                    ". 4. Nella cartella del file apri il Prompt dei comandi e incolla questa riga, con il tuo codice. Lascia la finestra aperta: se è chiusa, gli scontrini restano in coda."));
                var cmd = new TextBox { Text = $"python ponte_cassa.py {d.S("ponte")} IL-CODICE", IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 8, 0, 0) };
                body.Children.Add(cmd);
                if (c.S("codice") != "")
                {
                    var code = new TextBox { Text = c.S("codice"), IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
                    body.Children.Add(F.Notice(Col(T("Copia il codice adesso: non verrà più mostrato.", size: 13.5, bold: true), code), "success"));
                }
                var acts = Row(F.Small(BA(c.Bo("haCodice") ? "Nuovo codice (il vecchio smette di valere)" : "Crea il codice del ponte", () => Act(new { sede = id, azione = "token" }))));
                if (c.Bo("haCodice")) acts.Children.Add(F.Small(BA("Revoca", () => Act(new { sede = id, azione = "revoke" }), "Ghost")).M(8, 0, 0, 0));
                body.Children.Add(acts.M(0, 10, 0, 0));
            }
            page.Children.Add(F.Card(body));
        }
        if (!any) page.Children.Add(F.Card(Muted("Nessuna sede attiva.", 14)));
        Content = F.Page(1180, page);
    }
}
