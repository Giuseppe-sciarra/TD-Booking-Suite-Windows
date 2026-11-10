using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>stays.html: Alloggi: the day of the structure (arrivals, departures, in house, rooms to clean, breakfasts), the
/// portals' calendars, the rules for a period and the files for Questura, ISTAT and the tourist tax.</summary>
public sealed class StaysPage : Page
{
    string day = "", year = "";
    public override async Task Load() => Draw(await Api.Current.Call("alloggi", new { giorno = day, anno = year }));
    async Task Act(JsonObject args)
    {
        var r = await Api.Current.Call("alloggi.azione", args);
        if (r.S("avviso") != "") Main.Flash(r.S("avviso"), true); else if (r.S("testo") != "") Main.Flash(r.S("testo"));
        foreach (var e in r.A("errori")) Main.Flash(e.ToString(), true);
        await Load();
    }
    async Task File(object args)
    {
        var r = await Api.Current.Call("alloggi.file", args);
        Downloads.Save(r.S("nome"), Convert.FromBase64String(r.S("dati")));
        if (r.S("avviso") != "") Main.Flash(r.S("avviso"), true);
    }
    static TextBlock H3(string text) => T(text, size: 16, bold: true).M(0, 16, 0, 6);
    static Border Count(int n) { var t = T(n.ToString(), size: 12.5, bold: true, color: Res("Brand")); return new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = t }; }
    static TextBlock Missing(string text) => T(text, size: 13, bold: true, color: Res("Danger"));
    static DatePicker Date(DateTime? value = null) => new() { SelectedDate = value, Width = 160 };
    static string Iso(DatePicker p) => p.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
    static ComboBox Choice(List<(string Id, string Name)> items, string current, double width = 0)
    {
        var box = new ComboBox { ItemsSource = items.Select(x => x.Name).ToList(), SelectedIndex = Math.Max(0, items.FindIndex(x => x.Id == current)), Tag = items }; if (width > 0) box.Width = width; return box;
    }
    static string Picked(ComboBox box) => ((List<(string Id, string Name)>)box.Tag)[Math.Max(0, box.SelectedIndex)].Id;
    static Border Row2(params UIElement[] items) { var row = new DockPanel(); foreach (var i in items.Skip(1).Reverse()) { if (i == null) continue; DockPanel.SetDock(i, Dock.Right); row.Children.Add(i); } row.Children.Add(items[0]); return new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 9), Child = row }; }

    void Draw(JsonNode d)
    {
        day = d.S("giorno"); var when = DateOnly.ParseExact(day, "yyyy-MM-dd"); var today = d.S("oggi");
        var pick = Date(when.ToDateTime(TimeOnly.MinValue)); pick.SelectedDateChanged += (_, _) => { if (Iso(pick) is { Length: > 0 } v && v != day) { day = v; _ = Guard(Load); } };
        void Go(int delta) { day = when.AddDays(delta).ToString("yyyy-MM-dd"); _ = Guard(Load); }
        var nav = Row(F.Small(B("‹", () => Go(-1))), day != today ? F.Small(B("Oggi", () => { day = ""; _ = Guard(Load); })).M(6, 0, 0, 0) : null, F.Small(B("›", () => Go(1))).M(6, 0, 10, 0), pick);
        var page = Col(F.Heading(null, "Alloggi", "Il giorno della struttura: chi arriva, chi parte, chi è in casa, le camere da pulire e le colazioni. Sotto, i calendari dei portali, le regole per periodo e i file per Questura, ISTAT e imposta di soggiorno", nav));

        // the three lists of the day
        var lists = new List<FrameworkElement>();
        foreach (var l in d.A("liste"))
        {
            var rows = l.A("righe").ToList(); var card = Col(Row(T(l.S("titolo"), "H2"), Count(rows.Count)));
            foreach (var b in rows)
            {
                var id = b.S("id"); var head = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Cursor = System.Windows.Input.Cursors.Hand };
                head.Inlines.Add(new System.Windows.Documents.Run(b.S("unita")) { FontWeight = FontWeights.Bold }); head.Inlines.Add(" " + b.S("nome")); head.MouseLeftButtonUp += (_, _) => Main.ShowPage(new BookingDetailPage(id));
                var small = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap }; small.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                small.Inlines.Add($"{b.I("persone")} {(b.I("persone") == 1 ? "ospite" : "ospiti")} · dal {b.S("dal")} al {b.S("al")}");
                if (!b.Bo("mascherato")) { small.Inlines.Add(" · "); if (b.I("dati") > 0) small.Inlines.Add($"dati di {b.I("dati")} su {b.I("persone")}"); else small.Inlines.Add(new System.Windows.Documents.Run("senza dati degli ospiti") { Foreground = Res("Danger"), FontWeight = FontWeights.SemiBold }); }
                if (b.Bo("inviato")) small.Inlines.Add(" · inviato ad Alloggiati");
                else if (b.S("errore") != "") { small.Inlines.Add(" · "); small.Inlines.Add(new System.Windows.Documents.Run("Alloggiati Web lo rifiuta: " + b.S("errore")) { Foreground = Res("Danger"), FontWeight = FontWeights.SemiBold }); }
                var guests = !b.Bo("mascherato") && Can("booking.write") ? F.Small(B("Ospiti", () => Main.ShowPage(new GuestsPage { Id = id }), "Ghost")) : null; if (guests != null) guests.VerticalAlignment = VerticalAlignment.Center;
                card.Children.Add(Row2(Col(head, small), guests).M(0, card.Children.Count == 1 ? 10 : 0, 0, 0));
            }
            if (rows.Count == 0) card.Children.Add(Prog.Muted(l.S("vuoto")).M(0, 10, 0, 0));
            var box = F.Card(card); box.Margin = new Thickness(0); lists.Add(box);
        }
        page.Children.Add(F.Cols(3, 20, lists.ToArray()).M(0, 0, 0, 20));

        // Camere e pulizie, Colazioni
        var units = d.A("unita").ToList(); var rooms = Col(T("Camere e pulizie", "H2")); var states = new List<(string, string)> { ("clean", "Pulita"), ("dirty", "Da pulire"), ("out", "Fuori servizio") };
        foreach (var u in units)
        {
            var id = u.S("id"); var pill = MainWindow.Pill(u.S("statoNome"), u.S("stato") switch { "clean" => "ok", "dirty" => "warn", _ => "danger" }); pill.VerticalAlignment = VerticalAlignment.Center; pill.Margin = new Thickness(10, 0, 10, 0);
            var line = Col(Row(T(u.S("nome"), size: 14, bold: true), pill));
            if (u.S("stato") == "out")
            {
                var moved = u.A("daSpostare").ToList(); line.Children.Add(Prog.Muted("Fuori servizio: non si prenota finché non torna pulita o da pulire." + (moved.Count > 0 ? " Da spostare:" : ""), 12.5));
                var links = new WrapPanel(); foreach (var b in moved) { var bid = b.S("id"); var link = F.Link($"dal {b.S("dal")} al {b.S("al")}", () => Main.ShowPage(new BookingDetailPage(bid)), 13); link.Foreground = Res("Danger"); link.Margin = new Thickness(0, 0, 12, 0); links.Children.Add(link); }
                if (moved.Count > 0) line.Children.Add(links);
            }
            FrameworkElement form = null;
            if (Can("booking.write")) { var box = Choice(states, u.S("stato"), 150); form = Row(box, F.Small(BA("Salva", () => Act(new JsonObject { ["azione"] = "room", ["unit"] = id, ["status"] = Picked(box) }))).M(8, 0, 0, 0)); form.VerticalAlignment = VerticalAlignment.Center; }
            rooms.Children.Add(Row2(line, form).M(0, rooms.Children.Count == 1 ? 10 : 0, 0, 0));
        }
        if (units.Count == 0) rooms.Children.Add(Prog.Muted("Nessuna camera o alloggio collegato a un soggiorno.").M(0, 10, 0, 0));
        var breakfasts = d.A("colazioni").ToList(); var meal = Col(Row(T($"Colazioni del {d.S("titoloColazioni")}", "H2"), Count(d.I("colazioniPersone"))));
        foreach (var b in breakfasts)
        {
            var who = T($"{b.I("persone")} {(b.I("persone") == 1 ? "persona" : "persone")}", size: 14); who.Margin = new Thickness(12, 0, 0, 0); who.VerticalAlignment = VerticalAlignment.Center;
            meal.Children.Add(Row2(Col(T(b.S("unita"), size: 14, bold: true), b.S("allergie") != "" ? F.Allergy(b.S("allergie")) : null), who).M(0, meal.Children.Count == 1 ? 10 : 0, 0, 0));
        }
        if (breakfasts.Count == 0) meal.Children.Add(Prog.Muted("Nessuno ha dormito qui la notte prima.").M(0, 10, 0, 0));
        else if (d.Bo("noteNascoste")) meal.Children.Add(Prog.Muted("Allergie e intolleranze le vede chi ha il permesso sulle note").M(0, 10, 0, 0));
        var c1 = F.Card(rooms); c1.Margin = new Thickness(0); var c2 = F.Card(meal); c2.Margin = new Thickness(0); c1.VerticalAlignment = c2.VerticalAlignment = VerticalAlignment.Top;
        page.Children.Add(F.Cols(2, 20, c1, c2).M(0, 0, 0, 20));

        // Calendari dei portali
        if (Can("catalog.write") && units.Count > 0)
        {
            var feeds = Col(T("Calendari dei portali", "H2"), Prog.Muted("Per ogni alloggio: il link da incollare in Airbnb, Booking.com e negli altri portali (porta solo le notti occupate, senza nomi), e sotto i link dei loro calendari, uno per riga. Le notti prese sui portali diventano chiusure dell'alloggio: le leggiamo ogni ora o quando premi Aggiorna ora. I portali a loro volta leggono il nostro calendario ogni qualche ora: per un po' una notte può risultare libera da loro, quindi inserite subito anche le prenotazioni prese per telefono").M(0, 6, 0, 0));
            foreach (var u in units)
            {
                var id = u.S("id"); var urls = Box(u.S("indirizzi"), true); urls.MinHeight = 54; urls.Tag = "https://www.airbnb.it/calendar/ical/…"; AgendaPage.Hint(urls);
                var read = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) }; read.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); read.Inlines.Add(u.S("letto") != "" ? "Ultima lettura: " + u.S("letto") : "Mai letto");
                if (u.S("errore") != "") { read.Inlines.Add(" · "); read.Inlines.Add(new System.Windows.Documents.Run(u.S("errore")) { Foreground = Res("Danger"), FontWeight = FontWeights.SemiBold }); }
                var buttons = Row(F.Small(BA("Salva i link", () => Act(new JsonObject { ["azione"] = "feeds", ["unit"] = id, ["urls"] = urls.Text }))));
                if (u.S("indirizzi") != "") buttons.Children.Add(F.Small(BA("Aggiorna ora", () => Act(new JsonObject { ["azione"] = "refresh", ["unit"] = id }), "Ghost")).M(8, 0, 0, 0));
                buttons.Margin = new Thickness(0, 10, 0, 0);
                feeds.Children.Add(Col(H3(u.S("nome")), F.Field($"Il calendario di {u.S("nome")} per i portali", Prog.Code(u.S("link"), 13)), F.Field("Calendari dei portali da leggere", urls), read, buttons));
            }
            page.Children.Add(F.Card(feeds));
        }

        // Regole per periodo
        var weekdays = d.A("giorni").Select(x => x.ToString()).ToList(); var services = d.A("servizi").ToList();
        (WrapPanel Panel, Func<JsonArray> Read) Days()
        {
            var boxes = weekdays.Select(n => new CheckBox { Content = n, Margin = new Thickness(0, 0, 14, 6) }).ToList(); var panel = new WrapPanel(); foreach (var b in boxes) panel.Children.Add(b);
            return (panel, () => new JsonArray(boxes.Select((b, i) => (b, i)).Where(x => x.b.IsChecked == true).Select(x => (JsonNode)x.i.ToString()).ToArray()));
        }
        if (Can("catalog.write") && services.Count > 0)
        {
            var rules = Col(T("Regole per periodo", "H2"), Prog.Muted("Per esempio ad agosto almeno 7 notti, arrivi e partenze solo di sabato. Valgono per la pagina online e per il banco").M(0, 6, 0, 0));
            foreach (var s in services)
            {
                var id = s.S("id"); rules.Children.Add(H3(s.S("nome"))); var n = 0;
                foreach (var r in s.A("regole")) { var index = (n++).ToString(); rules.Children.Add(Row2(Prog.Text(r.ToString()), F.Small(BA("Togli", () => Act(new JsonObject { ["azione"] = "rule_del", ["service"] = id, ["rule"] = index }), "Ghost")))); }
                var from = Date(); var until = Date(); var min = Box("0"); min.Width = 90; var (noIn, readIn) = Days(); var (noOut, readOut) = Days();
                rules.Children.Add(F.Details($"Aggiungi una regola a {s.S("nome")}", Col(Row(F.Field("Dal", from).M(0, 0, 16, 0), F.Field("Al", until).M(0, 0, 16, 0), F.Field("Notti minime", min)),
                    T("Niente arrivi di", size: 13, bold: true).M(0, 12, 0, 6), noIn, T("Niente partenze di", size: 13, bold: true).M(0, 8, 0, 6), noOut,
                    Prog.Left(F.Small(BA("Aggiungi la regola", () => Act(new JsonObject { ["azione"] = "rule_add", ["service"] = id, ["from"] = Iso(from), ["until"] = Iso(until), ["min"] = min.Text.Trim(), ["no_arrival"] = readIn(), ["no_departure"] = readOut() })))))).M(0, 8, 0, 0));
            }
            page.Children.Add(F.Card(rules));
        }

        // Questura, ISTAT e imposta di soggiorno
        var locations = d.A("sedi").ToList(); var categories = d.A("categorie").Select(x => (x.S("id"), x.S("nome"))).ToList(); var twoDays = new List<(string, string)> { (today, "Oggi"), (d.S("ieri"), "Ieri") };
        if (d.Bo("file"))
        {
            var card = Col(T("Questura, ISTAT e imposta di soggiorno", "H2"), H3("Alloggiati Web"),
                Prog.Muted("Il file delle schedine degli arrivi, da caricare su Alloggiati Web entro 24 ore dall'arrivo: il portale accetta solo arrivi di oggi o di ieri. Dopo l'invio premi Inviato: i dati dei documenti vengono cancellati, come chiede la legge"));
            var sentDay = Choice(twoDays, today, 110);
            card.Children.Add(Row(F.Small(BA("File di oggi", () => File(new { cosa = "alloggiati", giorno = today }))), F.Small(BA("File di ieri", () => File(new { cosa = "alloggiati", giorno = d.S("ieri") }), "Ghost")).M(8, 0, 16, 0), sentDay,
                F.Small(BA("Inviato", async () => { if (Ask("Hai caricato il file su Alloggiati Web? I dati dei documenti di quegli ospiti vengono cancellati.")) await Act(new JsonObject { ["azione"] = "sent", ["day"] = Picked(sentDay) }); }, "Ghost")).M(8, 0, 0, 0)).M(0, 10, 0, 0));
            var ws = d["ws"];
            if (ws != null)
            {
                card.Children.Add(H3("Invio diretto ad Alloggiati Web"));
                card.Children.Add(Prog.Muted("Le schedine partono dal programma, senza file: prima il controllo, poi l'invio dei soggiorni con tutti i dati giusti. Per quelli inviati i dati dei documenti vengono cancellati subito; la ricevuta della Polizia si scarica da sola dal giorno dopo e resta qui per cinque anni"
                    + (ws.Bo("auto") ? ". L'invio automatico è acceso: ogni ora partono gli arrivi di oggi già entrati e quelli di ieri" : "")));
                if (ws.S("errore") != "") card.Children.Add(F.Notice("Ultimo invio automatico non riuscito: " + ws.S("errore"), "danger").M(0, 10, 0, 0));
                var sendDay = Choice(new List<(string, string)> { (today, "Arrivi di oggi"), (d.S("ieri"), "Arrivi di ieri") }, today, 160);
                card.Children.Add(Row(sendDay, F.Small(BA("Invia ad Alloggiati", () => Act(new JsonObject { ["azione"] = "ws_send", ["day"] = Picked(sendDay) }))).M(8, 0, 0, 0)).M(0, 10, 0, 0));
                foreach (var r in d.A("ricevute"))
                {
                    var g = r.S("giorno");
                    card.Children.Add(Row2(Prog.Text($"{r.S("data")} · {r.I("righe")} schedine"), r.Bo("pdf") ? F.Small(BA("Ricevuta", () => File(new { cosa = "ricevuta", giorno = g }), "Ghost")) : Prog.Muted("ricevuta da domani", 12.5)));
                }
            }
            card.Children.Add(H3("ISTAT con Ross1000"));
            card.Children.Add(Prog.Muted("Il file dei movimenti del mese, un giorno dopo l'altro anche senza ospiti (movimento zero), da importare in Ross1000 entro il 5 del mese dopo. Serve il codice struttura nella sede"));
            var month = Box(d.S("mese")); month.Width = 100; month.MaxLength = 7; month.ToolTip = "Mese, per esempio 2026-11";
            var place = Choice(locations.Select(l => (l.S("id"), l.S("nome"))).ToList(), locations.FirstOrDefault()?.S("id") ?? "", 200);
            card.Children.Add(Row(month, locations.Count > 1 ? place.M(8, 0, 0, 0) : null, F.Small(BA("Scarica il file", () => File(new { cosa = "ross1000", mese = month.Text.Trim(), sede = Picked(place) }))).M(8, 0, 0, 0)).M(0, 10, 0, 0));
            var taxes = d["imposta"] as JsonArray;
            if (taxes != null)
            {
                card.Children.Add(H3($"Imposta di soggiorno {d.S("anno")}"));
                card.Children.Add(Prog.Muted("Il conto per trimestre come nella dichiarazione annuale: notti a tariffa ordinaria (una cifra per tariffa, nello stesso ordine), ridotte ed esenti. Ogni ospite notte per notte, con la tariffa del periodo e della categoria, fino alle notti massime; esenti i più giovani dell'età della sede e chi ha un motivo di esenzione. È un calcolo per le comunicazioni e le dichiarazioni, non un incasso"));
                foreach (var x in taxes)
                {
                    card.Children.Add(H3(x.S("sede")));
                    if (x.I("ignoti") > 0) card.Children.Add(Prog.Muted($"{x.I("ignoti")} soggiorni non hanno i dati degli ospiti: contati tutti a tariffa ordinaria"));
                    var rows = x.A("trimestri").Select(q => new[] { $"{q.S("q")}° trimestre", q.S("tariffe") != "" ? q.S("tariffe") : "-", q.S("notti") != "" ? q.S("notti") : "0", q.S("ridotta") != "" ? q.S("ridotta") : "-", q.S("nottiRidotte"), q.S("esenti"), "€ " + q.S("imposta") }).ToList();
                    var t = x["totale"]; rows.Add(new[] { $"Anno {d.S("anno")}", "", t.S("ordinarie"), "", t.S("ridotte"), t.S("esenti"), "€ " + t.S("imposta") });
                    card.Children.Add(F.Table(new[] { "Trimestre", "Imposta a notte", "Notti ordinarie", "Imposta ridotta", "Notti ridotte", "Notti esenti", "Imposta" }, rows, 8));
                    if (x.S("motivi") != "") card.Children.Add(Prog.Muted("Notti esenti per motivo: " + x.S("motivi")).M(0, 8, 0, 0));
                }
                card.Children.Add(F.Link("Anno prima", () => { year = (d.I("anno") - 1).ToString(); _ = Guard(Load); }).M(0, 12, 0, 0));
            }
            else card.Children.Add(Prog.Muted("Per il calcolo dell'imposta di soggiorno scrivi la tariffa nella sede, in Configura l'attività, o aggiungi qui sotto le tariffe del comune.").M(0, 12, 0, 0));
            page.Children.Add(F.Card(card));
        }

        // Regole dell'imposta di soggiorno
        if (Can("catalog.write"))
        {
            var card = Col(T("Regole dell'imposta di soggiorno", "H2"), Prog.Muted("Le tariffe del comune per periodo o stagione e per categoria della struttura, per esempio a Roma B&B 6 € fino a 10 notti. Vale la prima regola che comprende la notte; se nessuna la comprende, la tariffa scritta nella sede").M(0, 6, 0, 0));
            foreach (var l in locations)
            {
                var id = l.S("id"); card.Children.Add(H3(l.S("nome")));
                var cat = Choice(new[] { ("", "Non indicata") }.Concat(categories).ToList(), l.S("categoria")); var nights = Box(l.S("notti")); var escort = Box(l.S("accompagnatori")); var cut = Box(l.S("riduzione"));
                card.Children.Add(F.Grid2(F.Field("Categoria della struttura", cat), F.Field("Lavoro e studio: notti tassate all'anno", nights, null, "0 = nessun limite. Roma: 10"),
                    F.Field("Un accompagnatore esente ogni … ospiti", escort, null, "Roma: 25. 0 = tutti esenti"), F.Field("Riduzione in percentuale", cut, null, "Per gli ospiti segnati con riduzione. Firenze: 50")));
                card.Children.Add(Prog.Left(F.Small(BA("Salva", () => Act(new JsonObject { ["azione"] = "tax_location", ["location"] = id, ["tax_category"] = Picked(cat), ["tax_year_nights"] = nights.Text.Trim(), ["tax_escort"] = escort.Text.Trim(), ["tax_reduction"] = cut.Text.Trim() })))));
                var n = 0;
                foreach (var r in l.A("regole")) { var index = (n++).ToString(); card.Children.Add(Row2(Prog.Text(r.ToString()), F.Small(BA("Togli", () => Act(new JsonObject { ["azione"] = "tax_del", ["location"] = id, ["rule"] = index }), "Ghost"))).M(0, n == 1 ? 10 : 0, 0, 0)); }
                var from = Date(); var until = Date(); var kind = Choice(new[] { ("", "Tutte") }.Concat(categories).ToList(), ""); var rate = Box(); rate.Tag = "6,00"; AgendaPage.Hint(rate); var max = Box("0");
                var yearly = new CheckBox { Content = T("Stagione: vale ogni anno in questi giorni (l'anno delle date non conta)", size: 14), Margin = new Thickness(0, 12, 0, 0) };
                card.Children.Add(F.Details($"Aggiungi una tariffa a {l.S("nome")}", Col(F.Grid2(F.Field("Dal", from), F.Field("Al", until), F.Field("Categoria", kind), F.Field("€ a persona a notte", rate), F.Field("Notti consecutive tassate al massimo", max, null, "0 = tutte")), yearly,
                    Prog.Left(F.Small(BA("Aggiungi la tariffa", () => Act(new JsonObject { ["azione"] = "tax_add", ["location"] = id, ["from"] = Iso(from), ["until"] = Iso(until), ["category"] = Picked(kind), ["rate"] = rate.Text.Trim(), ["nights"] = max.Text.Trim(), ["yearly"] = yearly.IsChecked == true })))))).M(0, 10, 0, 0));
            }
            page.Children.Add(F.Card(card));
        }

        // Alloggiati Web: invio diretto
        if (d.Bo("wsEditor"))
        {
            var ws = d["ws"]; var user = Box(ws?.S("utente") ?? ""); var password = new PasswordBox(); var key = new PasswordBox(); if (ws != null) password.ToolTip = key.ToolTip = "lascia vuoto per non cambiarla";
            var auto = new CheckBox { Content = T("Invio automatico ogni ora degli arrivi con i dati degli ospiti completi", size: 14), IsChecked = ws != null && ws.Bo("auto"), Margin = new Thickness(0, 12, 0, 0) };
            var off = new CheckBox { Content = T("Spegni l'invio diretto e cancella le credenziali", size: 14), Margin = new Thickness(0, 8, 0, 0) };
            page.Children.Add(F.Card(T("Alloggiati Web: invio diretto", "H2"),
                Prog.Muted("Utente e password del portale della Polizia e la chiave web service (nel portale: menu utente, Chiave Web Service, Genera nuovo codice; dopo ogni cambio di password ne serve una nuova). Restano cifrati. Il file da scaricare resta comunque disponibile").M(0, 6, 0, 0),
                F.Cols(3, 20, F.Field("Utente", user), F.Field("Password", password, null, ws != null ? "lascia vuoto per non cambiarla" : null), F.Field("Chiave web service", key, null, ws != null ? "lascia vuoto per non cambiarla" : null)), auto, ws != null ? off : null,
                Prog.Left(F.Small(BA("Salva", async () => { var r = await Api.Current.Call("alloggi.credenziali", new { ws_user = user.Text, ws_password = password.Password, ws_wskey = key.Password, ws_auto = auto.IsChecked == true, ws_off = off.IsChecked == true }); Main.Flash(r.S("testo")); await Load(); })))));
        }
        Content = F.Page(1180, page);
    }
}

/// <summary>guests.html: Dati degli ospiti of a stay, typed at the desk: why and how they came, then one form for each person.</summary>
public sealed class GuestsPage : Page
{
    public string Id = "";
    public override async Task Load() => Draw(await Api.Current.Call("alloggi.ospiti", new { id = Id }));

    static ComboBox Choice(List<(string Id, string Name)> items, string current) => new() { ItemsSource = items.Select(x => x.Name).ToList(), SelectedIndex = Math.Max(0, items.FindIndex(x => x.Id == current)), Tag = items };
    static string Picked(ComboBox box) => ((List<(string Id, string Name)>)box.Tag)[Math.Max(0, box.SelectedIndex)].Id;

    void Draw(JsonNode d)
    {
        var page = Col(F.Back("La prenotazione", () => Main.ShowPage(new BookingDetailPage(Id))),
            F.Heading(null, "Dati degli ospiti", $"{d.S("servizio")}{(d.S("risorsa") != "" ? " · " + d.S("risorsa") : "")}, dal {d.S("dal")} al {d.S("al")}. La legge chiede questi dati per la Questura (Alloggiati Web) e per le statistiche del turismo. Il primo ospite mostra il documento; familiari e membri del gruppo viaggiano sul suo.  Nessuna foto o copia del documento: solo tipo e numero, cancellati dopo l'invio alla Questura"));
        List<(string, string)> List(string key) => d.A(key).Select(x => (x.S("id"), x.S("nome"))).ToList();
        var kinds = List("tipi"); var docs = new[] { ("", "Nessuno") }.Concat(List("documenti")).ToList(); var exemptions = List("esenzioni");
        var tourismItems = List("turismo"); var transportItems = List("trasporto");
        var tourism = Choice(tourismItems, d.S("turismoScelto") != "" ? d.S("turismoScelto") : tourismItems[^1].Item1); var transport = Choice(transportItems, d.S("trasportoScelto") != "" ? d.S("trasportoScelto") : transportItems[^1].Item1);
        var form = Col(T("Il viaggio", size: 16, bold: true), F.Grid2(F.Field("Motivo del viaggio", tourism), F.Field("Mezzo di trasporto", transport)));
        var readers = new List<Action<JsonObject>>(); var n = 0;
        foreach (var g in d.A("righe"))
        {
            var i = n++;
            TextBox In(string key, int max, string hint = null) { var b = Box(g.S(key)); b.MaxLength = max; if (hint != null) { b.Tag = hint; AgendaPage.Hint(b); } return b; }
            var kind = Choice(kinds, g.S("kind")); var sex = Choice(new List<(string, string)> { ("", "Scegli"), ("M", "Maschio"), ("F", "Femmina") }, g.S("sex")); var first = In("first_name", 30); var last = In("last_name", 50);
            var born = new DatePicker { SelectedDate = DateTime.TryParseExact(g.S("birth_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd) ? bd : null };
            var place = In("birth_place", 60, "Roma, Germania…"); var citizen = In("citizenship", 60, "Italia"); var home = In("residence", 60);
            var doc = Choice(docs, g.S("doc_type")); var number = In("doc_number", 20); var issued = In("doc_place", 60); var exemption = Choice(exemptions, g.S("exemption"));
            readers.Add(o =>
            {
                o[$"g{i}-kind"] = Picked(kind); o[$"g{i}-last_name"] = last.Text; o[$"g{i}-first_name"] = first.Text; o[$"g{i}-sex"] = Picked(sex); o[$"g{i}-birth_date"] = born.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
                o[$"g{i}-birth_place"] = place.Text; o[$"g{i}-citizenship"] = citizen.Text; o[$"g{i}-residence"] = home.Text; o[$"g{i}-doc_type"] = Picked(doc); o[$"g{i}-doc_number"] = number.Text; o[$"g{i}-doc_place"] = issued.Text; o[$"g{i}-exemption"] = Picked(exemption);
            });
            form.Children.Add(T($"Ospite {i + 1}", size: 16, bold: true).M(0, 22, 0, 0));
            form.Children.Add(F.Grid2(F.Field("Chi è", kind), F.Field("Sesso", sex), F.Field("Nome", first), F.Field("Cognome", last), F.Field("Data di nascita", born), F.Field("Nato a", place, "(comune italiano o Stato estero)"),
                F.Field("Cittadinanza", citizen, "(Stato)"), F.Field("Residenza", home, "(comune italiano o Stato estero)"), F.Field("Documento", doc, "(solo il primo ospite o chi viaggia da solo)"), F.Field("Numero del documento", number),
                F.Field("Rilasciato a", issued, "(comune o Stato)"), F.Field("Imposta di soggiorno", exemption, "(l'età la contiamo da soli)")));
        }
        form.Children.Add(Prog.Muted("Lascia vuoti nome e cognome di chi non viene. I dati restano cifrati nella prenotazione").M(0, 16, 0, 0));
        form.Children.Add(Prog.Left(BA("Salva i dati degli ospiti", async () =>
        {
            var values = new JsonObject { ["tourism"] = Picked(tourism), ["transport"] = Picked(transport) }; foreach (var read in readers) read(values);
            var r = await Api.Current.Call("alloggi.ospiti.salva", new JsonObject { ["id"] = Id, ["valori"] = values }); Main.Flash(r.S("testo")); Main.ShowPage(new BookingDetailPage(Id));
        }, "Primary")));
        page.Children.Add(F.Card(form));
        Content = F.Page(900, page);
    }
}
