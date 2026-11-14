using System.Diagnostics;
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

/// <summary>templates/dashboard.html: the business at a glance: the four numbers, what to do (to call, requests, to
/// close), waiting list and birthdays, the turns of a restaurant, today and the next 7 days, the charts, the cards.</summary>
public sealed class DashboardPage : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    /// <summary>The last quick action, to take back for a few minutes (the web keeps it in the session).</summary>
    static string undoId = "";
    JsonNode d; string tab = "oggi"; DateOnly? turnDay;
    AgendaPage z;
    DateTime Local(string iso) => z.Local(iso);

    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }; z.UseZone(Business.S("fuso"));
        d = await Api.Current.Call("cruscotto", new { annullabile = undoId, turni = turnDay?.ToString("yyyy-MM-dd") });
        Draw();
    }
    async Task Quick(string id, string to)
    {
        await Api.Current.Call("prenotazione.stato", new { id, stato = to });
        undoId = id; await Load();
    }
    void Open(string id) => Main.ShowPage(new BookingDetailPage(id));
    static void Web(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    void Draw()
    {
        var today = DateOnly.Parse(d.S("oggi")); var n = d["numeri"]; var stats = d["statistiche"];
        var eyebrow = today.ToString("dddd dd MMMM yyyy", It);
        var page = Col(F.Heading(eyebrow, "Dashboard", $"Prenotazioni, disdette e clienti di {Business.S("nome")} a colpo d'occhio",
            Can("booking.write") && d.Bo("senzaAppuntamento") ? B("Senza appuntamento", () => Main.ShowPage(new BookingPage { Walkin = true })) : null,
            Can("booking.write") ? B("Nuova prenotazione", () => Main.ShowPage(new BookingPage()), "Primary") : null));
        // ----- the last quick action, to take back
        if (d["annulla"] is JsonNode u)
        {
            var txt = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            txt.Inlines.Add($"{u.S("nome")} {u.S("cognome")}, {Local(u.S("inizio")):dd/MM HH:mm}: ora è "); txt.Inlines.Add(new Run(StatusName(u.S("stato")).ToLowerInvariant()) { FontWeight = FontWeights.Bold });
            var back = F.Small(BA("Torna indietro", async () => { await Api.Current.Call("prenotazione.stato", new { id = u.S("id"), annulla = true }); undoId = ""; await Load(); }));
            var bar = new DockPanel(); DockPanel.SetDock(back, Dock.Right); bar.Children.Add(back); bar.Children.Add(txt);
            page.Children.Add(F.Notice(bar, "success"));
        }
        // ----- the four numbers
        StackPanel list = null; RadioButton tabToday = null, tabWeek = null;
        if (stats != null)
        {
            var g = new Grid();
            Border Metric(int i, string label, string value, UIElement small, string link = null)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var strong = T(value, size: 26, bold: true); strong.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"]; strong.Margin = new Thickness(0, 2, 0, 0);
                var b = new Border { Padding = new Thickness(18, 12, 18, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Background = Brushes.Transparent, Child = Col(T(label, size: 13, bold: true, color: Res("Muted")), strong, small) };
                if (link != null)
                {
                    b.Cursor = Cursors.Hand; b.Tag = strong;
                    b.MouseLeftButtonUp += (_, _) => { tab = link; (link == "oggi" ? tabToday : tabWeek).IsChecked = true; };
                }
                Grid.SetColumn(b, i); g.Children.Add(b); return b;
            }
            TextBlock Small(string s) => T(s, size: 12.5, color: Res("Muted"));
            var m1 = Metric(0, "Oggi", n.S("oggi"), Small("prenotazioni confermate"), "oggi"); m1.ToolTip = "Chi viene oggi e a che ora";
            var m2 = Metric(1, "Prossimi 7 giorni", n.S("settimana"), Small($"{n.S("inArrivo")} in arrivo da domani in poi"), "settimana"); m2.ToolTip = "Chi viene nei prossimi 7 giorni e a che ora";
            TextBlock trend;
            if (n["andamento"] != null)
            {
                var v = n.I("andamento"); trend = new TextBlock { FontSize = 12.5 }; trend.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                var r = new Run((v > 0 ? "+" : "") + v + "%") { FontWeight = FontWeights.Bold }; r.SetResourceReference(TextElement.ForegroundProperty, v >= 0 ? "Ok" : "Danger");
                trend.Inlines.Add(r); trend.Inlines.Add(" sui 30 giorni prima");
            }
            else trend = Small("online e inserite a mano");
            Metric(2, "Ricevute in 30 giorni", n.S("ricevute"), trend);
            Metric(3, "Disdette in 30 giorni", n.S("disdette"), Small(n.I("ricevute") > 0 ? $"{n.I("percDisdette")}% delle ricevute" : "nessuna prenotazione ricevuta"));
            void Current() { foreach (var (b, k) in new[] { (m1, "oggi"), (m2, "settimana") }) { b.Background = tab == k ? Res("BrandSoft") : Brushes.Transparent; ((TextBlock)b.Tag).Foreground = tab == k ? Res("Brand") : Res("Ink"); } }
            Current(); g.Tag = (Action)Current;
            page.Children.Add(new Border { Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 14), Child = g, ClipToBounds = true });
            metrics = g;
        }
        // ----- the site fed by this PC
        if (d["sito"] is JsonNode w)
        {
            if (w.Bo("inCorso")) page.Children.Add(F.Notice("Sincronizzazione con il sito in corso…"));
            else if (w.S("errore") != "") page.Children.Add(F.Notice($"Sincronizzazione con il sito non riuscita alle {Local(w.S("alle")):HH:mm}: {w.S("errore")}", "error"));
            else
            {
                var line = Row(T($"Sincronizzato con il sito alle {Local(w.S("alle")):HH:mm}: ", size: 13.5));
                if (w.I("nuove") > 0) line.Children.Add(F.Link($"{w.I("nuove")} prenotazion{(w.I("nuove") == 1 ? "e nuova" : "i nuove")}", () => { if (w.S("una") != "") Open(w.S("una")); else Main.Show("prenotazioni"); }, 13.5));
                else line.Children.Add(T("nessuna prenotazione nuova", size: 13.5));
                page.Children.Add(F.Notice(line, "success"));
            }
        }
        // ----- what to do
        Border Todo(string h2, int count, string note, IEnumerable<JsonNode> rows, string kind)
        {
            var ul = Col(); var all = rows.ToList();
            for (int i = 0; i < all.Count; i++) ul.Children.Add(QuickRow(all[i], kind, i == all.Count - 1));
            return F.Card(F.Section(h2, count), Note(note), ul);
        }
        var todo = new List<FrameworkElement>();
        if (d.A("daChiamare").Any()) todo.Add(Todo("Da chiamare", d.I("daChiamareN"), "Nelle prossime 48 ore: hanno avuto il promemoria e non hanno premuto Confermo. Una telefonata evita il posto vuoto", d.A("daChiamare"), "call"));
        if (d.A("richieste").Any()) todo.Add(Todo("Richieste da confermare", d.I("richiesteN"), "Prenotazioni online che aspettano il vostro sì e intanto tengono il posto. Il cliente riceve un'email in tutti e due i casi", d.A("richieste"), "ask"));
        if (d.A("daChiudere").Any()) todo.Add(Todo("Da chiudere", d.I("daChiudereN"), "Già passate e ancora aperte, negli ultimi 30 giorni: com'è andata?", d.A("daChiudere"), "close"));
        if (todo.Count > 0) page.Children.Add(Grid2(todo));
        // ----- waiting list, birthdays
        var more = new List<FrameworkElement>();
        if (d["attesa"].I("n") > 0)
            more.Add(F.Card(F.Section("Lista d'attesa", d["attesa"].I("n"), F.Link("Apri la lista", () => Main.Show("attesa"))),
                Note(d["attesa"].I("liberi") > 0 ? $"Per {d["attesa"].I("liberi")} si è liberato un posto: chi non ha l'email va chiamato." : "Aspettano un posto: appena se ne libera uno lo scriviamo a chi ha l'email.")));
        if (d.A("compleanni").Any())
        {
            var ul = Col(); var first = true;
            foreach (var b in d.A("compleanni"))
            {
                var day = DateOnly.Parse(b.S("giorno"));
                var right = T($"{(day == today ? "oggi" : day.ToString("dddd d", It))}, {b.I("anni")} anni", size: 14, color: Res("Muted"));
                var row = new DockPanel(); DockPanel.SetDock(right, Dock.Right); row.Children.Add(right);
                row.Children.Add(Row(b.Bo("vip") ? F.Mark("vip", "star", "", "Cliente VIP") : null, F.Link($"{b.S("nome")} {b.S("cognome")}", () => Main.ShowPage(new ClientPage(b.S("id"))))));
                ul.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 8, 0, 8), Child = row }); first = false;
            }
            more.Add(F.Card(F.Section("Compleanni della settimana"), ul));
        }
        if (more.Count > 0) page.Children.Add(Grid2(more));
        // ----- the turns of a restaurant
        if (d["turni"] is JsonArray turns)
        {
            var day = DateOnly.Parse(d.S("giornoTurni"));
            var pick = new DatePicker { SelectedDate = day.ToDateTime(TimeOnly.MinValue), Width = 150 };
            var go = F.Small(BA("Vai", async () => { turnDay = pick.SelectedDate is DateTime x ? DateOnly.FromDateTime(x) : null; await Load(); }));
            var ul = Col(); var k = 0;
            foreach (var x in turns)
            {
                k++; var a = Local(x.S("inizio")); var closed = x.Bo("chiuso");
                async Task Change(bool open)
                {
                    var r = await WithForce("turno.cambia", new JsonObject { ["servizio"] = x.S("servizio"), ["giorno"] = day.ToString("yyyy-MM-dd"), ["inizio"] = x.S("inizio"), ["apri"] = open });
                    if (r == null) return; if (r.S("testo") != "") Main.Flash(r.S("testo")); await Load();
                }
                var button = closed ? F.Small(BA("Riapri", () => Change(true)))
                    : F.Small(BA("Chiudi", async () => { if (Ask($"Chiudere {x.S("nome").ToLowerInvariant()} delle {a:HH:mm} solo per quel giorno?")) await Change(false); }, "Ghost"));
                ul.Children.Add(TodayRow(Time($"{a:HH:mm}", $"{Local(x.S("fine")):HH:mm}"),
                    Who(T(x.S("nome"), size: 14, bold: true), $"{x.S("servizioNome")}{(d.Bo("piuSedi") ? " · " + x.S("sede") : "")} · {x.I("persone")} person{(x.I("persone") == 1 ? "a" : "e")}"),
                    closed ? MainWindow.Pill("Chiuso", "danger") : null, button, k == turns.Count));
            }
            if (turns.Count == 0) ul.Children.Add(T("Nessun turno in questo giorno: è un giorno di chiusura", size: 14, color: Res("Muted")).M(0, 12, 0, 12));
            page.Children.Add(F.Card(F.Section("Turni " + (day == today ? "di oggi" : "di " + day.ToString("dddd d MMMM", It)), null, Row(pick, go.M(8, 0, 0, 0))),
                Note("Chiudi un turno solo per quel giorno, per esempio per una festa privata: online e al banco non si prenota più, le prenotazioni già fatte restano. Lo riapri quando vuoi"), ul));
        }
        // ----- today and the next 7 days, the charts, how it went, the services, the clients
        var cells = new List<FrameworkElement>();
        if (stats != null)
        {
            list = Col();
            tabToday = new RadioButton { Style = Style("Segment"), Content = "Oggi", GroupName = "dash" + GetHashCode(), IsChecked = tab == "oggi" };
            tabWeek = new RadioButton { Style = Style("Segment"), Content = "Prossimi 7 giorni", GroupName = "dash" + GetHashCode(), IsChecked = tab == "settimana" };
            var days = new Dictionary<DateOnly, FrameworkElement>(); var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
            void Fill()
            {
                list.Children.Clear(); days.Clear();
                if (tab == "oggi") TodayList(list, d.A("righe").ToList(), "Nessuna prenotazione oggi");
                else if (!d.A("settimana").Any()) list.Children.Add(F.Empty("◷", "Nessuna prenotazione nei prossimi 7 giorni", "Le nuove compaiono qui appena arrivano"));
                else foreach (var g in d.A("settimana"))
                {
                    var day = DateOnly.Parse(g.S("giorno")); var name = day.ToString("dddd d MMMM", It);
                    var h = Row(T(g.S("nome") != "" ? $"{g.S("nome")}, {name}" : char.ToUpper(name[0]) + name[1..], size: 14, bold: true), T(g.A("righe").Count().ToString(), size: 12, bold: true, color: Res("Muted")).M(8, 0, 0, 0));
                    h.Margin = new Thickness(0, list.Children.Count == 0 ? 4 : 18, 0, 6); days[day] = h; list.Children.Add(h);
                    TodayList(list, g.A("righe").ToList(), "");
                }
                (metrics?.Tag as Action)?.Invoke();
            }
            tabToday.Checked += (_, _) => { tab = "oggi"; Fill(); }; tabWeek.Checked += (_, _) => { tab = "settimana"; Fill(); };
            Fill();
            var seg = new Border { Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(4), Child = Row(tabToday, tabWeek), HorizontalAlignment = HorizontalAlignment.Left };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) }; var link = F.Link("Apri l'agenda", () => Main.Show("agenda")); link.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(link, Dock.Right); head.Children.Add(link); head.Children.Add(seg);
            cells.Add(Tall(head, scroll));
            // the bars: a click on a day opens it in the list
            var next = stats.A("giorni").Select(v => v.GetValue<int>()).ToArray();
            var labels = new[] { "Oggi", "Domani" }.Concat(Enumerable.Range(2, 5).Select(i => today.AddDays(i).ToString("dd/MM"))).ToArray();
            cells.Add(Tall(F.Section("Prossimi 7 giorni").M(0, 0, 0, 14), Bars(next, 300, i => labels[i], _ => true, i => $"{labels[i]}: {next[i]} prenotazion{(next[i] == 1 ? "e" : "i")}",
                i => { tabWeek.IsChecked = true; if (days.TryGetValue(today.AddDays(i), out var h)) h.BringIntoView(); })));
            var weeks = stats.A("settimane").Select(v => v.GetValue<int>()).ToArray(); var first = DateOnly.Parse(stats.S("primaSettimana"));
            var chart = Bars(weeks, 440, i => first.AddDays(7 * i).ToString("dd/MM"), i => i % 2 == (weeks.Length - 1) % 2, i => $"Settimana dal {first.AddDays(7 * i):dd/MM}: {weeks[i]} prenotazion{(weeks[i] == 1 ? "e" : "i")}", null);
            cells.Add(Tall(F.Section("Prenotazioni ricevute", null, T("Ultime 12 settimane", size: 13, color: Res("Muted"))).M(0, 0, 0, 14),
                weeks.Any(v => v > 0) ? chart : Col(chart, T("Ancora nessuna prenotazione in queste settimane", size: 14, color: Res("Muted")))));
            // how the last 30 days went
            var outcomes = Col();
            foreach (var o in stats.A("esiti")) outcomes.Children.Add(Meter(o.S("nome"), o.S("valore"), o.I("perc"), o.S("tono") switch { "ok" => "Ok", "danger" => "Danger", "warn" => "Warn", _ => "Muted" }, o.I("perc") + "%"));
            cells.Add(Tall(Col(T("Come sono andate", "H2"), Note("Appuntamenti degli ultimi 30 giorni" + (stats.I("esitiTotale") > 0 ? $", {stats.I("esitiTotale")} in tutto" : ""))),
                stats.I("esitiTotale") > 0 ? new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = outcomes } : T("Ancora nessun appuntamento passato da contare", size: 14, color: Res("Muted"))));
            var services = Col();
            foreach (var s in stats.A("servizi")) services.Children.Add(Meter(s.S("nome"), s.S("n"), s.I("quota"), "Brand", null));
            cells.Add(Tall(Col(T("Servizi più richiesti", "H2"), Note("Prenotazioni ricevute negli ultimi 30 giorni")),
                stats.A("servizi").Any() ? new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = services } : T("Nessuna prenotazione negli ultimi 30 giorni", size: 14, color: Res("Muted"))));
        }
        if (n["clienti"] != null)
        {
            Border Box(string v, string s)
            {
                var strong = T(v, size: 26, bold: true); strong.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"];
                return new Border { Background = Res("Bg"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Child = Col(strong, T(s, size: 13, color: Res("Muted"))) };
            }
            cells.Add(Tall(F.Section("Clienti", null, F.Link("Apri i clienti", () => Main.Show("clienti"))),
                F.Cols(3, 16, Box(n.S("clienti"), "nella rubrica"), Box(n.S("nuovi"), "nuovi negli ultimi 30 giorni"), Box(n.S("iscritti"), "iscritti alle comunicazioni")).M(0, 8, 0, 0)));
        }
        if (cells.Count > 0) page.Children.Add(F.Cols(2, 20, cells.ToArray()).M(0, 6, 0, 0));
        // ----- the three cards
        var three = new List<FrameworkElement>();
        Border Three(string h2, UIElement body, FrameworkElement button) { var c = F.Card(T(h2, "H2"), body, button); c.Margin = new Thickness(0); if (button != null) { button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 12, 0, 0); } return c; }
        if (Can("catalog.write")) three.Add(Three("Configura l'attività", T("Sedi, orari, servizi e risorse, con quello che manca", size: 14), B("Apri", () => Main.Show("configura"))));
        if (Business.S("paginaPubblica") != "") three.Add(Three("Pagina prenotazioni", T("Condividi il link o mostrala nel sito WordPress con il plugin", size: 14), B("Apri la pagina", () => Web(Business.S("paginaPubblica")))));
        if (Can("users.manage"))
        {
            var p = new TextBlock { FontSize = 14 }; p.Inlines.Add(new Run(d.S("inAttesa")) { FontWeight = FontWeights.Bold }); p.Inlines.Add(" in attesa di invio");
            three.Add(Three("Email ai clienti", Col(p, d.I("daVerificare") > 0 ? F.Notice($"{d.I("daVerificare")} invii da verificare.", "error") : null, T("La prenotazione resta valida anche se l'email è in attesa", size: 13, color: Res("Muted")).M(0, 6, 0, 0)), null));
        }
        if (three.Count > 0) page.Children.Add(F.Cols(three.Count, 20, three.ToArray()).M(0, 20, 0, 0));
        Content = F.Page(1480, page);
    }
    Grid metrics;

    static TextBlock Note(string text) { var t = T(text, size: 14, color: Res("Muted")); t.Margin = new Thickness(0, 0, 0, 14); return t; }
    static Grid Grid2(List<FrameworkElement> cards) { foreach (var c in cards) c.Margin = new Thickness(0); return F.Cols(2, 20, cards.ToArray()).M(0, 0, 0, 20); }
    /// <summary>.dash-grid > .card: 360 px tall, the heading on top and what follows filling the rest.</summary>
    static Border Tall(FrameworkElement head, FrameworkElement body)
    {
        var dock = new DockPanel(); DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head); dock.Children.Add(body);
        return new Border { Style = Style("Card"), Child = dock, Height = 360, Effect = (System.Windows.Media.Effects.Effect)Application.Current.Resources["CardShadow"] };
    }
    static FrameworkElement Time(string big, string small)
    {
        var t = T(big, size: 16, bold: true); t.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"];
        return Col(t, small == null ? null : T(small, size: 11.5, color: Res("Muted")).M(0, 3, 0, 0));
    }
    static StackPanel Who(FrameworkElement strong, string small, params FrameworkElement[] chips)
    {
        var s = T(small, size: 12.5, color: Res("Muted")); s.TextTrimming = TextTrimming.CharacterEllipsis;
        var c = Col(strong, s); foreach (var x in chips) if (x != null) c.Children.Add(x); return c;
    }
    /// <summary>.today-list li: 52 px for the time, who, the status or the buttons, Apri; a line under each but the last.</summary>
    static Border TodayRow(FrameworkElement time, FrameworkElement who, FrameworkElement third, FrameworkElement fourth, bool last)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Put(FrameworkElement e, int col, double left = 0) { if (e == null) return; e.VerticalAlignment = VerticalAlignment.Center; if (left > 0) e.Margin = new Thickness(left, 0, 0, 0); Grid.SetColumn(e, col); g.Children.Add(e); }
        Put(time, 0); Put(who, 2); Put(third, 3, 12); Put(fourth, 4, 12);
        return new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Padding = new Thickness(0, 12, 0, 12), Child = g };
    }
    static string Service(JsonNode r, bool max) =>
        r.S("servizio") + (r.I("persone") > 1 || max && r.I("maxPersone") > 1 ? $" · {r.I("persone")} person{(r.I("persone") == 1 ? "a" : "e")}" : "") + (max && r.S("operatore") != "" ? " · " + r.S("operatore") : "");
    static FrameworkElement Allergies(JsonNode r) => r.A("allergie").Any() ? F.Allergy(string.Join(", ", r.A("allergie").Select(a => a.ToString()))) : null;

    /// <summary>_today_list.html</summary>
    void TodayList(StackPanel list, List<JsonNode> rows, string empty)
    {
        if (rows.Count == 0 && empty != "") { list.Children.Add(F.Empty("◷", empty, "Le nuove compaiono qui appena arrivano")); return; }
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var id = r.S("id");
            var name = Row(); if (r.Bo("clienteOk")) name.Children.Add(T("✓ ", size: 14, bold: true, color: Res("Ok")));
            foreach (var m in F.Marks(r)) name.Children.Add(m);
            name.Children.Add(T($"{r.S("nome")} {r.S("cognome")}", size: 14, bold: true));
            list.Children.Add(TodayRow(Time($"{Local(r.S("inizio")):HH:mm}", null), Who(name, Service(r, true), Allergies(r)), F.Status(r.S("stato")), F.Link("Apri", () => Open(id)), i == rows.Count - 1));
        }
    }
    /// <summary>_quick_row.html: a request to answer, an appointment to close, a client to call, each with its buttons.</summary>
    Border QuickRow(JsonNode r, string kind, bool last)
    {
        var id = r.S("id"); var a = Local(r.S("inizio"));
        var declined = kind == "call" && r.Bo("rifiutata") ? new Border { Background = Res("WarnSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = T("Ha scritto che non viene", size: 12, bold: true, color: Res("Warn")) } : null;
        var who = Who(T($"{r.S("nome")} {r.S("cognome")}", size: 14, bold: true), r.S("servizio") + (r.I("persone") > 1 ? $" · {r.I("persone")} persone" : ""), Allergies(r), declined);
        FrameworkElement third;
        if (kind == "call")
            third = Row(r.S("telefono") != "" ? F.Small(B("Chiama", () => Web("tel:" + r.S("telefono")))) : null, r.S("whatsapp") != "" ? F.Small(B("WhatsApp", () => Web(r.S("whatsapp")))).M(6, 0, 0, 0) : null);
        else if (Can("booking.cancel"))
            third = kind == "ask"
                ? Row(F.Small(BA("Accetta", () => Quick(id, "confirmed"), "Primary")), F.Small(BA("Rifiuta", async () => { if (Ask($"Rifiutare la richiesta di {r.S("nome")}?")) await Quick(id, "reject"); }, "Ghost")).M(6, 0, 0, 0))
                : Row(F.Small(BA("Fatta", () => Quick(id, "completed"))), r.S("stato") == "confirmed" ? F.Small(BA("Non venuto", () => Quick(id, "no_show"), "Ghost")).M(6, 0, 0, 0) : null);
        else third = F.Status(r.S("stato"));
        return TodayRow(Time($"{a:HH:mm}", $"{a:dd/MM}"), who, third, F.Link("Apri", () => Open(id)), last);
    }
    /// <summary>.outcomes / .top-services li: the name and the number, a meter under them, the percentage.</summary>
    static StackPanel Meter(string label, string value, int pct, string tone, string small)
    {
        var strong = T(value, size: 16, bold: true); strong.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"];
        var top = new DockPanel(); DockPanel.SetDock(strong, Dock.Right); top.Children.Add(strong); top.Children.Add(T(label, size: 14));
        var track = new Grid { Height = 8, Margin = new Thickness(0, 6, 0, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0), GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0), GridUnitType.Star) });
        var back = new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(4) }; Grid.SetColumnSpan(back, 2); track.Children.Add(back);
        if (pct > 0) track.Children.Add(new Border { Background = Res(tone), CornerRadius = new CornerRadius(4) });
        return Col(top, track, small == null ? null : T(small, size: 12, color: Res("Muted")).M(0, 4, 0, 0)).M(0, 0, 0, 14);
    }
    /// <summary>agency.bars + _bars.html: the same numbers of the SVG (slot, 60 % bar, 22 under the base, 18 above).</summary>
    internal static FrameworkElement Bars(int[] values, double width, Func<int, string> label, Func<int, bool> show, Func<int, string> tip, Action<int> click)
    {
        const double height = 170; double top = Math.Max(values.DefaultIfEmpty(0).Max(), 1), slot = width / values.Length, bar = slot * .6, bas = height - 22;
        var c = new Canvas { Width = width, Height = height };
        c.Children.Add(new Line { X1 = 0, X2 = width, Y1 = bas, Y2 = bas, Stroke = Res("Line"), StrokeThickness = 1 });
        void Text(string s, double x, double y, Brush color)
        {
            var t = new TextBlock { Text = s, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = color, Width = slot + 20, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(t, x - (slot + 20) / 2); Canvas.SetTop(t, y - 12); c.Children.Add(t);
        }
        for (int i = 0; i < values.Length; i++)
        {
            int n = i; var v = values[i]; var h = v > 0 ? (bas - 18) * v / top : 0; var x = i * slot + (slot - bar) / 2;
            var r = v > 0 ? new Rectangle { Width = bar, Height = h, RadiusX = 5, RadiusY = 5, Fill = Res("Brand"), ToolTip = tip(i) } : new Rectangle { Width = bar, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Res("Line"), ToolTip = tip(i) };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, v > 0 ? bas - h : bas - 3); c.Children.Add(r);
            if (v > 0)
            {
                r.MouseEnter += (_, _) => r.Fill = Res("Pink"); r.MouseLeave += (_, _) => r.Fill = Res("Brand");
                if (click != null) { r.Cursor = Cursors.Hand; r.MouseLeftButtonUp += (_, _) => click(n); }
                Text(v.ToString(), i * slot + slot / 2, bas - h - 6, Res("Ink"));
            }
            if (show(i)) Text(label(i), i * slot + slot / 2, height - 4, Res("Muted"));
        }
        return new Viewbox { Child = c, Stretch = Stretch.Uniform, MaxHeight = 250, VerticalAlignment = VerticalAlignment.Center };
    }
}
