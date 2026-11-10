using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>What the pages of Configurazione del programma share: the business's mark, the small charts, the tiles.</summary>
static class Agency
{
    /// <summary>_client_mark.html: the logo, or the first letter on the business's colour.</summary>
    public static FrameworkElement Mark(JsonNode m, double size = 36)
    {
        var radius = size >= 70 ? 20 : size >= 48 ? 13 : 10;
        if (m.S("logo") != "")
            try
            {
                var img = new Image { Source = SetupPage.PictureOf(Convert.FromBase64String(m.S("logo"))), Height = size - (size >= 48 ? 8 : 0), MaxWidth = size * 2.9, Stretch = Stretch.Uniform };
                return new Border { Background = Brushes.White, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius), Padding = new Thickness(size >= 48 ? 4 : 0), Child = img, MinWidth = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException) { }
        var letter = T(m.S("nome") == "" ? "" : m.S("nome")[..1].ToUpperInvariant(), size: size * 0.42, bold: true, color: Brushes.White); letter.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"];
        letter.HorizontalAlignment = HorizontalAlignment.Center; letter.VerticalAlignment = VerticalAlignment.Center;
        Brush fill; try { fill = Hex(m.S("colore")); } catch (FormatException) { fill = Res("Brand"); }
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = fill, Child = letter, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    }
    public static StackPanel Cell(JsonNode m, string small, double size = 36)
    {
        var name = T(m.S("nome"), size: 14, bold: true); var sub = T(small, size: 12.5, color: Res("Muted"));
        var row = Row(Mark(m, size), Col(name, sub).M(12, 0, 0, 0)); ((FrameworkElement)row.Children[1]).VerticalAlignment = VerticalAlignment.Center; return row;
    }
    /// <summary>_spark.html</summary>
    public static FrameworkElement Spark(int[] values, double width = 120, double height = 32, bool stretch = false)
    {
        double top = Math.Max(values.DefaultIfEmpty(0).Max(), 1), step = width / Math.Max(1, values.Length - 1); var empty = !values.Any(v => v > 0);
        var points = new PointCollection(values.Select((v, i) => new Point(i * step, height - 3 - (height - 6) * v / top)));
        var c = new Canvas { Width = width, Height = height };
        if (!empty)
        {
            var area = new PointCollection(points); area.Insert(0, new Point(0, height)); area.Add(new Point(width, height));
            c.Children.Add(new Polygon { Points = area, Fill = Tint((SolidColorBrush)Res("Brand"), 0.14) });
        }
        c.Children.Add(new Polyline { Points = points, Stroke = Res(empty ? "Line" : "Brand"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return stretch ? new Viewbox { Child = c, Stretch = Stretch.Fill, Height = 36 } : c;
    }
    public static void Web(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } }
    public static int[] Weeks(JsonNode n) => n.A("settimane").Select(x => (int)x).ToArray();
    /// <summary>.meter-cell: the number and its share of the busiest business.</summary>
    public static FrameworkElement Meter(JsonNode n)
    {
        var g = new Grid { Width = 110, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        g.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(3) });
        if (n.I("quota") > 0) g.Children.Add(new Border { Background = Res("Brand"), CornerRadius = new CornerRadius(3), Width = 110 * n.I("quota") / 100.0, HorizontalAlignment = HorizontalAlignment.Left });
        var strong = T(n.S("b30"), size: 14, bold: true); strong.Width = 34; return Row(strong, g);
    }
    public static FrameworkElement Chart(int[] weeks, string firstDay, double width)
    {
        var first = DateTime.ParseExact(firstDay, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return DashboardPage.Bars(weeks, width, i => first.AddDays(7 * i).ToString("dd/MM"), i => i % 2 == (weeks.Length - 1) % 2, i => $"Settimana dal {first.AddDays(7 * i):dd/MM}: {weeks[i]} prenotazion{(weeks[i] == 1 ? "e" : "i")}", null);
    }
    /// <summary>.metrics: the tiles in one row.</summary>
    public static Border Metrics(params (string Label, string Value, UIElement Small, bool Accent)[] tiles)
    {
        var g = new Grid();
        for (int i = 0; i < tiles.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var strong = T(tiles[i].Value, size: 26, bold: true, color: tiles[i].Accent ? Res("Brand") : null); strong.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"]; strong.Margin = new Thickness(0, 2, 0, 0);
            var b = new Border { Padding = new Thickness(18, 12, 18, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Child = Col(T(tiles[i].Label, size: 13, bold: true, color: Res("Muted")), strong, tiles[i].Small) };
            Grid.SetColumn(b, i); g.Children.Add(b);
        }
        var card = F.Card(g); card.Padding = new Thickness(4, 6, 4, 6); return card;
    }
    public static TextBlock Small(string s) => T(s, size: 12.5, color: Res("Muted"));
    public static TextBlock Trend(JsonNode n, string key, string otherwise)
    {
        if (n[key] == null) return Small(otherwise);
        var v = n.I(key); var t = new TextBlock { FontSize = 12.5 }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var r = new Run((v > 0 ? "+" : "") + v + "%") { FontWeight = FontWeights.Bold }; r.SetResourceReference(TextElement.ForegroundProperty, v >= 0 ? "Ok" : "Danger");
        t.Inlines.Add(r); t.Inlines.Add(" sui 30 giorni prima"); return t;
    }
    public static (Brush Back, Brush Fore) Tone(string level) => level switch { "danger" => (Res("DangerSoft"), Res("Danger")), "warn" => (Res("WarnSoft"), Res("Warn")), _ => (Res("BrandSoft"), Res("Brand")) };
    public static Border Flag(string level, string text, double radius = 8)
    {
        var (back, fore) = Tone(level); var t = T(text, size: 13, bold: true, color: fore); t.TextWrapping = TextWrapping.Wrap;
        return new Border { Background = back, CornerRadius = new CornerRadius(radius), Padding = new Thickness(radius > 20 ? 12 : 10, radius > 20 ? 6 : 7, radius > 20 ? 12 : 10, radius > 20 ? 6 : 7), Child = t };
    }
    public static Border Empty(Action create) => F.Card(T("Ancora nessun cliente", "H2"), Prog.Text("Crea il primo: parte con una sede, un servizio e un orario dal lunedì al venerdì, dalle 9 alle 18, che puoi cambiare subito").M(0, 8, 0, 14), Prog.Left(B("Crea il primo cliente", create, "Primary")));
    public static Grid Table(string[] head, double[] widths, List<(FrameworkElement[] Cells, Action Open)> rows)
    {
        var g = new Grid { Margin = new Thickness(0, 10, 0, 0) }; foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Cell(FrameworkElement e, int row, int col, Action open)
        {
            if (e != null) e.VerticalAlignment = VerticalAlignment.Center;
            var b = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 9, 12, 9), Child = e, Background = Brushes.Transparent };
            if (open != null && col == 0) { b.Cursor = Cursors.Hand; b.MouseLeftButtonUp += (_, _) => open(); }
            Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b);
        }
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); for (int i = 0; i < head.Length; i++) Cell(T(head[i], size: 13, bold: true, color: Res("Muted")), 0, i, null);
        for (int r = 0; r < rows.Count; r++) { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); for (int i = 0; i < rows[r].Cells.Length; i++) Cell(rows[r].Cells[i], r + 1, i, rows[r].Open); }
        return g;
    }
    public static FrameworkElement Never(string value) => value != "" ? T(value, size: 14) : Prog.Muted("mai");
}

/// <summary>agency_dashboard.html: how the businesses use the program, and what asks for attention.</summary>
public sealed class ProgramDashboardPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("programma", new { }));
    void Client(string slug) => Main.ShowPage(new ClientCardPage { Slug = slug });

    void Draw(JsonNode d)
    {
        var cloud = d.S("edizione") != "private"; var t = d["totali"];
        var page = Col(F.Heading(d.S("oggi"), "Dashboard", "Come usano il portale le attività che gestisci, e cosa chiede la tua attenzione", d.Bo("puoCreare") ? B("Nuovo cliente", () => Main.ShowPage(new NewClientPage()), "Primary") : null));
        var trials = d.A("richieste").ToList();
        if (trials.Count > 0)
        {
            var list = Col(F.Section("Richieste di prova da gestire"));
            foreach (var r in trials)
            {
                var id = r.S("id"); var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                var go = F.Small(B("Attiva", () => Main.ShowPage(new NewClientPage { Request = id }), "Primary")); DockPanel.SetDock(go, Dock.Right); row.Children.Add(go);
                row.Children.Add(Row(F.Avatar(r.S("nome"), 36), Col(T(r.S("attivita"), size: 14, bold: true), Agency.Small($"{r.S("nome")}, {r.S("tipo")}, {r.S("quando")}")).M(12, 0, 0, 0)));
                list.Children.Add(row);
            }
            page.Children.Add(F.Card(list));
        }
        if (d.I("quanti") == 0) { page.Children.Add(Agency.Empty(() => Main.ShowPage(new NewClientPage()))); Content = F.Page(1180, page); return; }

        var kinds = !cloud ? "edizione Private" : $"{t.I("attivi")} attiv{(t.I("attivi") == 1 ? "o" : "i")}" + (t.I("prova") > 0 ? $", {t.I("prova")} in prova" : "") + (t.I("sospesi") > 0 ? $", {t.I("sospesi")} sospes{(t.I("sospesi") == 1 ? "o" : "i")}" : "");
        page.Children.Add(Agency.Metrics(("Clienti", t.S("clienti"), Agency.Small(kinds), false), ("Prenotazioni in 30 giorni", t.S("b30"), Agency.Trend(t, "tendenza", "ricevute da tutti i clienti"), true),
            ("In arrivo", t.S("arrivo"), Agency.Small("prenotazioni confermate"), false), ("Email inviate", t.S("inviate"), Agency.Small("negli ultimi 30 giorni"), false)));

        var chart = F.Card(F.Section("Prenotazioni ricevute", null, Prog.Muted("Ultime 12 settimane, tutti i clienti")), Agency.Chart(Agency.Weeks(d), d.S("primo"), 640).M(0, 12, 0, 0));
        var attention = Col(T("Da controllare", "H2"));
        var alerts = d.A("avvisi").ToList();
        if (alerts.Count > 0)
        {
            foreach (var a in alerts)
            {
                var slug = a.S("slug"); var (back, fore) = Agency.Tone(a.S("livello")); var box = Col(T(a.S("nome"), size: 14, bold: true));
                foreach (var text in a.A("testi")) { var line = T(text.ToString(), size: 13, color: fore); line.TextWrapping = TextWrapping.Wrap; box.Children.Add(line); }
                var item = new Border { Background = back, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 10, 0, 0), Child = box, Cursor = Cursors.Hand };
                item.MouseLeftButtonUp += (_, _) => Client(slug); attention.Children.Add(item);
            }
            if (d.I("altri") > 0) attention.Children.Add(F.Link($"Altri {d.I("altri")} clienti da controllare", () => Main.Show("attivita")).M(0, 10, 0, 0));
        }
        else { attention.Children.Add(T("✓ Tutto in ordine", size: 15, bold: true, color: Res("Ok")).M(0, 10, 0, 2)); attention.Children.Add(Agency.Small("Prove, invii email, accessi e pulizia dei dati sono a posto")); }
        attention.Children.Add(Prog.Muted("Pulizia dei dati scaduti: " + t.S("pulizia"), 13).M(0, 12, 0, 0));
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        var aside = F.Card(attention); aside.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(aside, 2); grid.Children.Add(chart); grid.Children.Add(aside); page.Children.Add(grid);

        var head = new List<string> { "Cliente" }; if (cloud) head.Add("Stato"); head.AddRange(new[] { "Prenotazioni in 30 giorni", "Ultime 12 settimane", "In arrivo", "Utenti", "Ultimo accesso" });
        var widths = new List<double> { 2.4 }; if (cloud) widths.Add(0.9); widths.AddRange(new[] { 1.7, 1.4, 0.8, 0.7, 1.3 });
        var rows = d.A("classifica").Select(r =>
        {
            var m = r["segno"]; var n = r["numeri"]; var slug = m.S("slug");
            var cells = new List<FrameworkElement> { Agency.Cell(m, slug) }; if (cloud) { var pill = F.Status(m.S("stato")); pill.HorizontalAlignment = HorizontalAlignment.Left; cells.Add(pill); }
            cells.AddRange(new[] { Agency.Meter(n), Agency.Spark(Agency.Weeks(n)), T(n.S("arrivo"), size: 14), T(n.S("utenti"), size: 14), Agency.Never(n.S("accesso")) });
            return (cells.ToArray(), (Action)(() => Client(slug)));
        }).ToList();
        var usage = Col(F.Section("Utilizzo per cliente", null, F.Link("Tutti i clienti", () => Main.Show("attivita"))), Agency.Table(head.ToArray(), widths.ToArray(), rows));
        if (d.I("quanti") > rows.Count) usage.Children.Add(F.Link($"Vedi tutti i {d.I("quanti")} clienti", () => Main.Show("attivita")).M(0, 12, 0, 0));
        page.Children.Add(F.Card(usage));
        Content = F.Page(1180, page);
    }
}

/// <summary>clients.html: every business, as a grid of cards or a list, with search and the filter by state.</summary>
public sealed class ProgramClientsPage : Page
{
    static bool list; string filter = "all", query = "";
    public override async Task Load() => Draw(await Api.Current.Call("programma.clienti", new { }));
    void Client(string slug) => Main.ShowPage(new ClientCardPage { Slug = slug });

    void Draw(JsonNode d)
    {
        var cloud = d.S("edizione") != "private"; var all = d.A("clienti").ToList();
        var page = Col(F.Heading(null, "Clienti", "Tutte le attività che gestisci. Apri una scheda per logo, anagrafica, referenti e numeri", d.Bo("puoCreare") ? B("Nuovo cliente", () => Main.ShowPage(new NewClientPage()), "Primary") : null));
        if (all.Count == 0) { page.Children.Add(Agency.Empty(() => Main.ShowPage(new NewClientPage()))); Content = F.Page(1180, page); return; }
        var pane = new ContentControl { Focusable = false };
        void Fill()
        {
            var shown = all.Where(x => (filter == "all" || x["segno"].S("stato") == filter) && (query == "" || x.S("cerca").Contains(query))).ToList();
            if (shown.Count == 0) { pane.Content = F.Card(T("Nessun cliente trovato", "H2"), Prog.Text("Cambia la ricerca o il filtro di stato").M(0, 8, 0, 0)); return; }
            if (list)
            {
                var head = new List<string> { "Cliente", "Referente" }; if (cloud) head.Add("Stato"); head.AddRange(new[] { "Prenotazioni in 30 giorni", "In arrivo", "Utenti", "Ultimo accesso", "" });
                var widths = new List<double> { 2.3, 1.6 }; if (cloud) widths.Add(0.9); widths.AddRange(new[] { 1.7, 0.8, 0.7, 1.1, 0.5 });
                pane.Content = F.Card(Agency.Table(head.ToArray(), widths.ToArray(), shown.Select(x =>
                {
                    var m = x["segno"]; var n = x["numeri"]; var slug = m.S("slug"); var who = x["referente"]; var flags = x.A("avvisi").ToList();
                    var cells = new List<FrameworkElement> { Agency.Cell(m, x.S("citta") != "" ? x.S("citta") : slug), who != null ? Col(T(who.S("nome"), size: 14), Agency.Small(who.S("recapito"))) : Prog.Muted("Da aggiungere") };
                    if (cloud) { var pill = F.Status(m.S("stato")); pill.HorizontalAlignment = HorizontalAlignment.Left; cells.Add(pill); }
                    FrameworkElement dot = null;
                    if (flags.Count > 0) { var (back, fore) = Agency.Tone(flags[0].S("livello")); dot = new Border { Background = back, CornerRadius = new CornerRadius(11), MinWidth = 22, Height = 22, Padding = new Thickness(6, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = flags[0].S("testo"), Child = T(flags.Count.ToString(), size: 12, bold: true, color: fore) }; ((TextBlock)((Border)dot).Child).VerticalAlignment = VerticalAlignment.Center; ((TextBlock)((Border)dot).Child).HorizontalAlignment = HorizontalAlignment.Center; }
                    cells.AddRange(new[] { Agency.Meter(n), T(n.S("arrivo"), size: 14), T(n.S("utenti"), size: 14), Agency.Never(n.S("accessoGiorno")), dot });
                    return (cells.ToArray(), (Action)(() => Client(slug)));
                }).ToList()));
                return;
            }
            var grid = new WrapPanel();
            foreach (var x in shown)
            {
                var m = x["segno"]; var n = x["numeri"]; var slug = m.S("slug"); var who = x["referente"]; var flags = x.A("avvisi").ToList();
                var top = new DockPanel(); if (cloud) { var pill = F.Status(m.S("stato")); pill.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(pill, Dock.Right); top.Children.Add(pill); }
                top.Children.Add(Agency.Mark(m, 48));
                var sub = x.S("settore") != "" || x.S("citta") != "" ? x.S("settore") + (x.S("settore") != "" && x.S("citta") != "" ? ", " : "") + x.S("citta") : slug;
                var contact = Col(Agency.Small("Referente"));
                if (who != null) { contact.Children.Add(T(who.S("nome"), size: 14, bold: true)); if (who.S("recapito") != "") contact.Children.Add(Agency.Small(who.S("recapito"))); }
                else { var none = Prog.Muted("Da aggiungere"); none.FontStyle = FontStyles.Italic; contact.Children.Add(none); }
                StackPanel Number(string v, string label) { var s = T(v, size: 18, bold: true); return Col(s, Agency.Small(label)); }
                var numbers = F.Cols(3, 8, Number(n.S("b30"), "in 30 giorni"), Number(n.S("arrivo"), "in arrivo"), Number(n.S("utenti"), n.I("utenti") == 1 ? "utente" : "utenti"));
                var body = Col(top, T(m.S("nome"), size: 18, bold: true).M(0, 16, 0, 2), Agency.Small(sub), contact.M(0, 14, 0, 14), numbers, Agency.Spark(Agency.Weeks(n), 250, 36, true).M(0, 12, 0, 0));
                if (flags.Count > 0) body.Children.Add(Agency.Flag(flags[0].S("livello"), flags[0].S("testo") + (flags.Count > 1 ? $"  +{flags.Count - 1}" : "")).M(0, 12, 0, 0));
                var card = F.Card(body); card.Width = 300; card.Margin = new Thickness(0, 0, 20, 20); card.Cursor = Cursors.Hand; card.MouseLeftButtonUp += (_, _) => Client(slug);
                grid.Children.Add(card);
            }
            pane.Content = grid;
        }
        var search = Box(query); search.Width = 340; search.Tag = "Cerca per nome, città, settore o referente"; AgendaPage.Hint(search);
        search.TextChanged += (_, _) => { query = search.Text.Trim().ToLowerInvariant(); Fill(); };
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        RadioButton Seg(string text, string group, bool on, Action pick) { var r = new RadioButton { Content = text, GroupName = group, IsChecked = on, Style = Style("Segment"), Margin = new Thickness(0, 0, 6, 0) }; r.Checked += (_, _) => { pick(); Fill(); }; return r; }
        var view = Row(Seg("Griglia", "vista", !list, () => list = false), Seg("Elenco", "vista", list, () => list = true)); DockPanel.SetDock(view, Dock.Right); bar.Children.Add(view);
        if (cloud)
        {
            var c = d["conteggi"];
            var states = Row(Seg($"Tutti {c.S("tutti")}", "stato", filter == "all", () => filter = "all"), Seg($"Attivi {c.S("attivi")}", "stato", filter == "active", () => filter = "active"),
                Seg($"In prova {c.S("prova")}", "stato", filter == "trial", () => filter = "trial"), Seg($"Sospesi {c.S("sospesi")}", "stato", filter == "suspended", () => filter = "suspended"));
            states.Margin = new Thickness(0, 0, 14, 0); DockPanel.SetDock(states, Dock.Right); bar.Children.Add(states);
        }
        bar.Children.Add(search); search.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(bar); page.Children.Add(pane); Fill();
        Content = F.Page(1180, page);
    }
}

/// <summary>client_new.html: the business's pages and the access of its administrator.</summary>
public sealed class NewClientPage : Page
{
    public string Request = "";
    JsonNode created; string link = ""; bool mailReady;
    public override async Task Load() => Draw(await Api.Current.Call("programma.nuovo", new { richiesta = Request }));

    void Draw(JsonNode d)
    {
        var values = d["valori"]; var trial = d.S("richiesta") != "";
        var page = Col(F.Back("Clienti", () => Main.Show("attivita")), F.Heading(null, "Nuovo cliente", "Crea il portale dell'attività e l'accesso del suo amministratore. Anagrafica e altri referenti li completi dopo, nella scheda"));
        if (created != null)
        {
            var slug = created.S("slug"); var top = new DockPanel();
            var open = B("Apri la scheda del cliente", () => Main.ShowPage(new ClientCardPage { Slug = slug }), "Primary"); open.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(open, Dock.Right); top.Children.Add(open);
            top.Children.Add(Agency.Cell(created, "Indirizzo breve " + slug, 48));
            var card = Col(top);
            if (link != "")
                card.Children.Add(F.Notice(Col(T("Invito personale, valido 24 ore", size: 14, bold: true), Prog.Text((mailReady ? "È partito anche per email. " : "L'email non parte finché non configuri l'SMTP: ") + "Copia il link e mandalo tu, per esempio su WhatsApp").M(0, 4, 0, 6),
                    Prog.Code(link), Prog.Left(F.Small(B("Copia il link", () => { Clipboard.SetText(link); Main.Flash("Copiato."); }))))).M(0, 14, 0, 0));
            page.Children.Add(F.Card(card));
        }
        if (!d.Bo("puoCreare") && created == null || created != null && !created.Bo("ancora"))
        {
            if (created == null) page.Children.Add(F.Card(T(d.S("edizione") == "private" ? "L'edizione Private gestisce una sola attività" : "Hai raggiunto il numero massimo di attività", "H2"), Prog.Muted(d.S("suggerimento")).M(0, 8, 0, 0)));
            Content = F.Page(1180, page); return;
        }
        var main = new FormView(d.A("campi")); var plan = new FormView(d.A("piano"));
        var username = Box(); username.MaxLength = 40; username.Tag = "mario.rossi"; AgendaPage.Hint(username);
        var email = Box(values.S("email")); email.MaxLength = 254;
        var types = new List<(string Id, string Name)> { ("", "Appuntamenti generici") }; types.AddRange(d.A("tipi").Select(x => (x.S("id"), x.S("nome"))));
        var preset = new ComboBox { ItemsSource = types.Select(x => x.Name).ToList(), SelectedIndex = Math.Max(0, types.FindIndex(x => x.Id == values.S("preset"))) };
        var contact = Box(values.S("contact_name")); contact.MaxLength = 100; contact.Tag = "Facoltativo"; AgendaPage.Hint(contact);
        var phone = Box(values.S("contact_phone")); phone.MaxLength = 40; phone.Tag = "Facoltativo"; AgendaPage.Hint(phone);
        var password = new PasswordBox();
        var save = new Button { Content = "Crea il cliente", Style = Style("Primary"), Margin = new Thickness(0, 18, 14, 0) };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                await Guard(async () =>
                {
                    var all = main.Values(); foreach (var kv in plan.Values()) all[kv.Key] = kv.Value?.DeepClone();
                    var r = await Api.Current.Call("programma.crea", new JsonObject { ["valori"] = all, ["richiesta"] = Request, ["username"] = username.Text, ["email"] = email.Text, ["preset"] = types[Math.Max(0, preset.SelectedIndex)].Id,
                        ["contact_name"] = contact.Text, ["contact_phone"] = phone.Text, ["password"] = password.Password });
                    created = r["creato"]; ((JsonObject)created)["ancora"] = r.Bo("puoCreare"); link = r.S("link"); mailReady = r.Bo("mailPronta"); Request = ""; Main.Flash(r.S("testo")); await Load();
                });
            }
            catch (FormError ex) { main.Errors(ex.Errors); plan.Errors(ex.Errors); Main.Flash("Controlla i campi.", true); }
            finally { save.IsEnabled = true; }
        };
        var cancel = F.Link("Annulla", () => Main.Show("attivita")); cancel.VerticalAlignment = VerticalAlignment.Center; cancel.Margin = new Thickness(0, 18, 0, 0);
        var form = Col(T(created != null ? "Crea un altro cliente" : "Dati per partire", "H2"), Prog.Muted("L'amministratore dell'attività riceve un link per creare il suo accesso, oppure gli imposti tu la password").M(0, 6, 0, 6));
        if (trial) form.Children.Add(F.Notice(Col(T("Dalla richiesta di prova di " + values.S("contact_name"), size: 14, bold: true), Prog.Text($"Attività, email, tipo, referente e una prova di {values.S("giorni")} giorni sono già inseriti: controlla e crea il cliente").M(0, 4, 0, 0))).M(0, 6, 0, 6));
        form.Children.Add(main.Panel);
        form.Children.Add(F.Grid2(F.Field("Username dell'amministratore dell'attività", username), F.Field("Sua email, facoltativa", email), F.Field("Tipo di attività", preset), F.Field("Referente, nome e cognome", contact), F.Field("Telefono del referente", phone)));
        form.Children.Add(F.Details("Oppure imposta tu la password", Col(Prog.Muted("Lascia vuoto per mandare l'invito. Con una password l'accesso è pronto subito, senza email: comunicala tu alla persona").M(0, 8, 0, 0), F.Field("Password, almeno 12 caratteri", password))).M(0, 14, 0, 0));
        form.Children.Add(F.Details("Piano, privacy e personalizzazione", plan.Panel, trial).M(0, 10, 0, 0));
        form.Children.Add(Row(save, cancel));
        page.Children.Add(F.Card(form));
        Content = F.Page(1180, page);
    }
}

/// <summary>client.html: the card of a business: numbers, company data and logo, contacts, the users of its pages.</summary>
public sealed class ClientCardPage : Page
{
    public string Slug = "";
    public override async Task Load() => Draw(await Api.Current.Call("programma.cliente", new { slug = Slug }));
    async Task Act(JsonObject args) { args["slug"] = Slug; var r = await Api.Current.Call("programma.cliente.azione", args); Main.Flash(r.S("testo")); await Load(); }
    void Enter(string key) => _ = Guard(() => Main.Open(Slug, key));

    FrameworkElement ContactForm(JsonNode c, string index)
    {
        TextBox In(string key, int max, string hint = null) { var b = Box(c?.S(key) ?? ""); b.MaxLength = max; if (hint != null) { b.Tag = hint; AgendaPage.Hint(b); } return b; }
        var name = In("name", 100); var role = In("role", 80, "Titolare, marketing, amministrazione"); var email = In("email", 160); var phone = In("phone", 40); var notes = In("notes", 300, "Orari in cui risponde, a cosa pensa");
        var primary = new CheckBox { Content = T("Referente principale, compare nell'elenco dei clienti", size: 14), IsChecked = c != null && c.Bo("primary"), Margin = new Thickness(0, 12, 0, 0) };
        return Col(F.Grid2(F.Field("Nome e cognome", name), F.Field("Ruolo", role), F.Field("Email", email), F.Field("Telefono", phone)), F.Field("Note", notes), primary,
            Prog.Left(BA(index == "" ? "Aggiungi il referente" : "Salva il referente", () => Act(new JsonObject { ["azione"] = "contact", ["index"] = index, ["name"] = name.Text, ["role"] = role.Text, ["email"] = email.Text, ["phone"] = phone.Text, ["notes"] = notes.Text, ["primary"] = primary.IsChecked == true }), "Primary")));
    }

    void Draw(JsonNode d)
    {
        var m = d["segno"]; var u = d["uso"]; var cloud = d.S("edizione") != "private"; var name = m.S("nome");
        var page = Col(F.Back("Clienti", () => Main.Show("attivita")));
        // .client-hero
        var title = Row(T(name, "H1")); if (cloud) title.Children.Add(F.Status(m.S("stato")).M(12, 0, 0, 0));
        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        meta.Children.Add(Row(Agency.Small("Indirizzo breve "), Prog.Code(Slug, 12.5)).M(0, 0, 18, 0)); meta.Children.Add(Agency.Small("Cliente dal " + d.S("dal")).M(0, 0, 18, 0));
        if (d.S("sito") != "") meta.Children.Add(F.Link(d.S("sito"), () => Agency.Web(d.S("sito")), 12.5));
        var actions = Col(B("Impostazioni e sito", () => Enter("impostazioni"), "Primary"), B("Persone e permessi", () => Enter("persone")).M(0, 8, 0, 0));
        if (d.S("paginaPubblica") != "") actions.Children.Add(B("Pagina prenotazioni", () => Agency.Web(d.S("paginaPubblica"))).M(0, 8, 0, 0));
        if (d.Bo("concesso")) actions.Children.Add(B("Apri il portale", () => Enter("dashboard")).M(0, 8, 0, 0));
        var hero = new DockPanel(); DockPanel.SetDock(actions, Dock.Right); hero.Children.Add(actions); var mark = Agency.Mark(m, 76); DockPanel.SetDock(mark, Dock.Left); hero.Children.Add(mark);
        hero.Children.Add(Col(title, Prog.Muted((d.S("ragione") != "" ? d.S("ragione") : "Ragione sociale da aggiungere") + (d.S("citta") != "" ? ", " + d.S("citta") : "")).M(0, 4, 0, 0), meta).M(22, 0, 22, 0));
        ((FrameworkElement)hero.Children[2]).VerticalAlignment = VerticalAlignment.Center;
        page.Children.Add(F.Card(hero));
        var flags = d.A("avvisi").ToList();
        if (flags.Count > 0) { var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) }; foreach (var f in flags) wrap.Children.Add(Agency.Flag(f.S("livello"), f.S("testo"), 999).M(0, 0, 8, 6)); page.Children.Add(wrap); }

        // the sections of the card, one under the other with the links that reach them
        var sections = new Dictionary<string, FrameworkElement>(); var tabs = Row(); tabs.Margin = new Thickness(0, 0, 0, 16);
        void Section(string label, FrameworkElement first) { sections[label] = first; tabs.Children.Add(F.Link(label, () => first.BringIntoView(), 14).M(0, 0, 22, 0)); }

        var metrics = Agency.Metrics(("Prenotazioni in 30 giorni", u.S("b30"), Agency.Trend(u, "tendenza", "ricevute online e a mano"), true), ("In arrivo", u.S("arrivo"), Agency.Small("prenotazioni confermate"), false),
            ("Clienti dell'attività", u.S("contatti"), Agency.Small("nella sua rubrica"), false), ("Email inviate", u.S("inviate"), Agency.Small("negli ultimi 30 giorni"), false));
        Section("Panoramica", metrics);
        var facts = new List<(string, FrameworkElement)>
        {
            ("Tipo di attività", Prog.Text(d.S("tipo") != "" ? d.S("tipo") : "da scegliere")), ("Sedi attive", Prog.Text(u.S("sedi"))), ("Servizi attivi", Prog.Text(u.S("servizi"))), ("Prenotazioni totali", Prog.Text(u.S("totale"))),
            ("Ultima prenotazione", Prog.Text(u.S("ultima") != "" ? u.S("ultima") : "nessuna")), ("Ultimo accesso", Prog.Text(u.S("accesso") != "" ? u.S("accesso") : "mai")),
            ("Email in coda", u.I("problemi") > 0 ? Row(Prog.Text(u.S("coda") + ", "), T($"{u.S("problemi")} da verificare", size: 14, bold: true, color: Res("Danger"))) : Prog.Text(u.S("coda"))),
        };
        if (cloud) facts.Add(("Marketing", Prog.Text(d.S("marketing"))));
        var setup = F.Card(F.Section("Come è configurato", null, F.Link("Cambia", () => Enter("configura"))), Prog.Facts(facts.ToArray()), Agency.Small("Solo numeri: nomi e contatti dei clienti dell'attività restano nel suo portale").M(0, 10, 0, 0));
        Grid Side(FrameworkElement left, FrameworkElement right) { var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) }); right.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(right, 2); g.Children.Add(left); g.Children.Add(right); return g; }

        // Anagrafica e logo
        var inputs = new Dictionary<string, FrameworkElement>(); var grid = new List<FrameworkElement>();
        foreach (var r in d.A("righe"))
        {
            FrameworkElement input;
            if (r.S("tipo") == "date") input = new DatePicker { SelectedDate = DateTime.TryParseExact(r.S("valore"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null };
            else { var b = Box(r.S("valore")); b.MaxLength = r.I("limite"); input = b; }
            inputs[r.S("chiave")] = input; grid.Add(F.Field(r.S("etichetta"), input));
        }
        var website = Box(d.S("sito")); website.MaxLength = 200; website.Tag = "https://"; AgendaPage.Hint(website); grid.Add(F.Field("Sito web", website));
        var notes = Box(d.S("note"), true); notes.MinHeight = 110; notes.MaxLength = 4000; notes.Tag = "Accordi, servizi che seguiamo, preferenze del cliente"; AgendaPage.Hint(notes);
        var registry = F.Card(F.Section("Anagrafica", null, MainWindow.Pill("Visibile solo all'amministratore del programma", "")), F.Grid2(grid.ToArray()), F.Field("Note interne", notes),
            Prog.Left(BA("Salva l'anagrafica", () =>
            {
                var args = new JsonObject { ["azione"] = "profile", ["website"] = website.Text, ["notes"] = notes.Text };
                foreach (var (k, v) in inputs) args[k] = v is DatePicker p ? p.SelectedDate?.ToString("yyyy-MM-dd") ?? "" : ((TextBox)v).Text;
                return Act(args);
            }, "Primary")));
        Section("Anagrafica e logo", registry);
        var preview = Agency.Mark(m, 76); preview.HorizontalAlignment = HorizontalAlignment.Left; preview.Margin = new Thickness(0, 12, 0, 12);
        var logo = Col(T("Logo", "H2"), Prog.Muted("Compare qui, nell'elenco clienti, nel portale dell'attività e nella pagina prenotazioni").M(0, 6, 0, 0), preview,
            Prog.Muted("PNG trasparente, JPEG o WebP, massimo 1 MB: anche orizzontale, i margini vuoti si tolgono da soli", 13),
            Prog.Left(BA("Carica il logo", async () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Immagini (PNG, JPEG, WebP)|*.png;*.jpg;*.jpeg;*.webp" }; if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
                await Act(new JsonObject { ["azione"] = "logo", ["logo"] = Convert.ToBase64String(SetupPage.CleanLogo(dlg.FileName)) });
            })));
        if (m.S("logo") != "") logo.Children.Add(Prog.Left(F.Small(BA("Rimuovi il logo", async () => { if (Ask($"Rimuovere il logo di {name}?")) await Act(new JsonObject { ["azione"] = "logo_remove" }); }, "Ghost"))));
        var mail = d["posta"]; bool own = mail.Bo("proprio"), agency = mail.Bo("agenzia");
        var post = Col(F.Section("Invio email", null, own ? MainWindow.Pill("Invio proprio", "ok") : agency ? MainWindow.Pill("SMTP dell'amministratore del programma", "") : MainWindow.Pill("Da configurare", "warn")),
            Prog.Muted(own ? "Conferme e notifiche partono dalla casella dell'attività" + (mail.S("come") != "" ? ": " + mail.S("come") : "")
                : agency ? "Conferme e notifiche partono dall'invio dell'amministratore del programma: puoi dare all'attività il suo, con Gmail, Microsoft 365 o la casella del suo dominio"
                : "Nessun invio configurato: le conferme restano in attesa finché non ne imposti uno").M(0, 8, 0, 0));
        if (mail.S("mittente") != "") post.Children.Add(Col(Agency.Small("I clienti vedono"), T(mail.S("mittente"), size: 14, bold: true)).M(0, 10, 0, 0));
        post.Children.Add(Prog.Left(B(own ? "Modifica o prova l'invio" : "Configura l'invio dell'attività", () => Enter("smtp"))));
        var aside = Col(F.Card(logo), F.Card(post));
        if (d.S("edizione") == "cloud")
        {
            var sites = d.A("siti").Select(x => x.ToString()).ToList();
            aside.Children.Add(F.Card(T("Plugin WordPress", "H2"), Row(Prog.Text("Indirizzo breve da inserire nel plugin: "), Prog.Code(Slug)).M(0, 8, 0, 0),
                Prog.Muted(sites.Count > 0 ? "Siti collegati: " + string.Join(", ", sites) : "Nessun sito collegato: genera il codice e inseriscilo nel plugin, si configura da solo").M(0, 6, 0, 0),
                Prog.Left(B(sites.Count > 0 ? "Gestisci il collegamento" : "Collega il sito", () => Enter("impostazioni")))));
        }

        // Referenti
        var contacts = d.A("referenti").ToList();
        var contactHead = F.Section("Referenti", null, Prog.Muted("Le persone da chiamare per questa attività")); Section($"Referenti {contacts.Count}", contactHead);
        var cards = new WrapPanel();
        for (int i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i]; var index = i.ToString(); var head = new DockPanel();
            if (c.Bo("primary")) { var pill = MainWindow.Pill("Principale", "ok"); pill.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(pill, Dock.Right); head.Children.Add(pill); }
            head.Children.Add(Row(F.Avatar(c.S("name"), 40), Col(T(c.S("name"), size: 14, bold: true), Agency.Small(c.S("role") != "" ? c.S("role") : "Ruolo non indicato")).M(12, 0, 0, 0)));
            var lines = Col(); lines.Margin = new Thickness(0, 12, 0, 8);
            void Line(string label, FrameworkElement value) { var k = Agency.Small(label); k.Width = 80; lines.Children.Add(Row(k, value).M(0, 3, 0, 3)); }
            if (c.S("phone") != "") Line("Telefono", F.Link(c.S("phone"), () => Agency.Web("tel:" + c.S("phone"))));
            if (c.S("email") != "") Line("Email", F.Link(c.S("email"), () => Agency.Web("mailto:" + c.S("email"))));
            if (c.S("notes") != "") { var n = Prog.Text(c.S("notes")); n.MaxWidth = 240; Line("Note", n); }
            var edit = Col(ContactForm(c, index), Prog.Left(F.Small(BA("Rimuovi il referente", async () => { if (Ask($"Rimuovere {c.S("name")} dai referenti?")) await Act(new JsonObject { ["azione"] = "contact_remove", ["index"] = index }); }, "DangerButton"))));
            var card = F.Card(head, lines, F.Details("Modifica", edit)); card.Width = 370; card.Margin = new Thickness(0, 0, 20, 20); card.VerticalAlignment = VerticalAlignment.Top;
            if (c.Bo("primary")) card.BorderBrush = Res("Pastel");
            cards.Children.Add(card);
        }
        if (!d.Bo("pieni")) { var add = F.Card(F.Details("Aggiungi un referente", ContactForm(null, ""), contacts.Count == 0)); add.Width = 370; add.Margin = new Thickness(0, 0, 20, 20); add.VerticalAlignment = VerticalAlignment.Top; cards.Children.Add(add); }

        // Utenti del portale
        var users = d.A("utenti").ToList();
        var people = Col(F.Section("Utenti del portale", null, F.Link("Gestisci persone e permessi", () => Enter("persone"))));
        Section($"Utenti del portale {users.Count}", people);
        if (users.Count > 0)
            people.Children.Add(Agency.Table(new[] { "Email", "Ruolo", "Secondo fattore", "Ultimo accesso" }, new[] { 2.4, 1.3, 1.2, 1.3 }, users.Select(x =>
            {
                var pill = x.Bo("mfa") ? MainWindow.Pill("Attivo", "ok") : MainWindow.Pill("Solo password", "warn"); pill.HorizontalAlignment = HorizontalAlignment.Left;
                return (new FrameworkElement[] { T(x.S("nome"), size: 14), T(x.S("ruolo"), size: 14), pill, Agency.Never(x.S("accesso")) }, (Action)null);
            }).ToList()));
        else people.Children.Add(Prog.Muted("Nessun utente: aggiungine uno da Persone e permessi").M(0, 10, 0, 0));

        page.Children.Add(tabs); page.Children.Add(metrics);
        page.Children.Add(Side(F.Card(F.Section("Prenotazioni ricevute", null, Prog.Muted("Ultime 12 settimane")), Agency.Chart(Agency.Weeks(u), u.S("primo"), 560).M(0, 12, 0, 0)), setup));
        page.Children.Add(Side(registry, aside)); page.Children.Add(contactHead.M(0, 10, 0, 12)); page.Children.Add(cards); page.Children.Add(F.Card(people));
        Content = F.Page(1180, page);
    }
}

/// <summary>site_settings.html: Impostazioni generali: what holds for the whole program and for every new business.</summary>
public sealed class GeneralPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("generali", new { }));

    static FrameworkElement Preview(string base64, string caption, bool dark, bool tile = false, double height = 44)
    {
        FrameworkElement inner;
        if (base64 != "") { var img = new Image { Source = SetupPage.PictureOf(Convert.FromBase64String(base64)), Height = height, MaxWidth = 180, Stretch = Stretch.Uniform }; inner = tile ? new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = img } : img; }
        else { var mark = T("TD", size: 16, bold: true, color: Brushes.White); mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Center; inner = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Background = Res("Brand"), Child = mark }; }
        inner.HorizontalAlignment = HorizontalAlignment.Center;
        var box = new Border { Background = dark ? Hex("#201a29") : Brushes.White, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 16, 22, 16), MinWidth = 150, Child = inner };
        var text = Agency.Small(caption); text.TextWrapping = TextWrapping.Wrap; text.MaxWidth = 220; text.Margin = new Thickness(0, 6, 0, 0);
        return Col(box, text).M(0, 0, 16, 0);
    }

    void Draw(JsonNode d)
    {
        var page = Col(F.Heading(null, "Impostazioni generali", "Valgono per tutto il portale e per ogni nuova attività: si impostano una volta sola"));
        var forms = new List<FormView>(); var groups = d.A("gruppi").ToList();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i]; var form = new FormView(g.A("campi")); forms.Add(form); var card = Col(T(g.S("titolo"), "H2"));
            if (i == 0)
            {
                card.Children.Add(Prog.Muted("Nome, logo, icona e colore compaiono nel portale e nella pagina di accesso. La pagina prenotazioni di ogni attività usa invece i colori e il logo dell'attività").M(0, 6, 0, 12));
                string logo = d.S("logo"), dark = d.S("logoScuro"), icon = d.S("icona"); var previews = Row(Preview(logo, "Su sfondo chiaro", false));
                previews.Children.Add(Preview(dark != "" ? dark : logo, "Su sfondo scuro" + (logo != "" && dark == "" ? ": senza un logo chiaro usa il riquadro bianco" : ""), true, dark == "" && logo != ""));
                if (icon != "") previews.Children.Add(Preview(icon, "Icona", false, false, 32));
                card.Children.Add(previews);
            }
            else if (g.S("titolo") == "Dati dell'azienda e privacy")
            {
                var note = Prog.Muted("Compaiono nella "); note.Inlines.Add(new Hyperlink(new Run("pagina privacy")) { Command = new Relay(() => Agency.Web(d.S("privacy"))) });
                note.Inlines.Add(", collegata dall'accesso e dal modulo di prova. Nel testo, una riga che inizia con ## diventa un titolo e una che inizia con un trattino diventa una voce di elenco");
                card.Children.Add(note.M(0, 6, 0, 6));
            }
            else if (i == groups.Count - 1) card.Children.Add(Prog.Muted("Precompilano il modulo Nuova attività. Le attività già create non cambiano: per loro c'è Impostazioni e sito").M(0, 6, 0, 6));
            card.Children.Add(form.Panel); page.Children.Add(F.Card(card));
        }
        var save = new Button { Content = "Salva le impostazioni", Style = Style("Primary"), Margin = new Thickness(0, 0, 0, 20), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                await Guard(async () =>
                {
                    var all = new JsonObject(); foreach (var f in forms) foreach (var kv in f.Values()) all[kv.Key] = kv.Value?.DeepClone();
                    var r = await Api.Current.Call("generali.salva", new JsonObject { ["valori"] = all }); Main.Flash(r.S("testo")); await Load();
                });
            }
            catch (FormError ex) { foreach (var f in forms) f.Errors(ex.Errors); Main.Flash("Controlla i campi.", true); }
            finally { save.IsEnabled = true; }
        };
        page.Children.Add(save);
        var smtp = d["smtp"]; var env = smtp != null && smtp.Bo("daEnv");
        var state = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        if (smtp != null) { state.Inlines.Add("Attivo: le email di servizio partono da "); state.Inlines.Add(new Run(smtp.S("mittente")) { FontWeight = FontWeights.Bold }); if (env) state.Inlines.Add(", impostato nel file .env del server"); }
        else state.Inlines.Add("Non impostato: inviti, conferme e notifiche delle attività senza un SMTP proprio restano in attesa");
        var buttons = Row(B(smtp != null && !env ? "Modifica e prova l'SMTP" : smtp != null ? "Prova o salva l'SMTP" : "Imposta l'SMTP", () => Main.ShowPage(new MailSetupPage(true)), smtp != null ? null : "Primary"));
        if (smtp != null && !env) buttons.Children.Add(BA("Togli", async () => { if (!Ask("Togliere l'SMTP dell'amministratore del programma?")) return; var r = await Api.Current.Call("smtp.sito.azione", new { azione = "remove" }); Main.Flash(r.S("testo")); await Load(); }, "Ghost").M(10, 0, 0, 0));
        buttons.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(F.Card(T("Invio email dell'amministratore del programma", "H2"), state,
            Prog.Muted("Vale per inviti, conferme di prenotazione, notifiche e richieste di prova. Nella pagina dell'SMTP scegli il nome del mittente e mandi un'email di prova. Le campagne marketing partono sempre e solo dall'SMTP dell'attività").M(0, 8, 0, 0), buttons));
        Content = F.Page(1180, page);
    }
}
