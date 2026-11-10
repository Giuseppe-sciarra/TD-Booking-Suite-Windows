using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/reports.html: Report: how the period went: the figures and the bars of the dashboard, then every
/// table with its Excel and its PDF (both into Download).</summary>
public sealed class ReportsPage : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    static readonly Dictionary<string, string> NOTES = new()
    {
        ["lavori_anno"] = "Tutti gli anni dello storico, anche fuori dal periodo scelto: visite, incassato e la parte di ogni operatore",
        ["lavori_servizio"] = "Quante volte è stato fatto ogni servizio nel periodo, dallo storico dei clienti",
        ["elenco"] = "Ogni trattamento del periodo, dal più recente",
        ["lavori_operatore"] = "Dallo storico dei clienti: gli scontrini con il cliente e lo storico importato dal programma di prima. Una visita con più operatori conta per ognuno, con la sua parte",
        ["lavori_mese"] = "Le visite segnate nello storico dei clienti e quanto è stato pagato; gli annulli tolgono il loro scontrino",
        ["da_incassare"] = "Gli scontrini con «Da pagare» non ancora pagati, a oggi, di qualsiasi periodo. Non sono nell'incassato: ci entrano il giorno in cui il cliente paga",
        ["sospesi"] = "Trattamenti e prodotti fatti e non ancora in uno scontrino, a oggi. Sono già contati tra i lavori del giorno in cui sono stati fatti; lo scontrino esce quando entrano in un conto",
        ["gift_card"] = "Le gift card vendute nel periodo, con il credito che resta. L'incasso è nel giorno della vendita: quando si usano lo scontrino è a zero",
        ["lavori_cliente"] = "Chi ha speso di più nel periodo, dallo storico dei clienti",
        ["occupazione"] = "Minuti prenotati sui minuti di apertura di ciascuno, senza pause e blocchi. Le annullate non contano",
        ["assenze"] = "Disdette all'ultimo momento: annullate dal cliente, o non confermate, nelle 24 ore prima. Cliente nuovo: alla sua prima prenotazione",
        ["clienti"] = "Di ritorno: con prenotazioni prima del periodo. Prossimo appuntamento: preso entro il giorno della visita segnata Fatta",
        ["origine"] = "Le prenotazioni più vecchie non hanno l'origine; la fonte è il tag del link, per esempio instagram. A locale chiuso: ricevute fuori dagli orari di apertura",
        ["valore"] = "È una stima: il prezzo di listino al momento della prenotazione, senza assenze e annullate. Non sono incassi",
    };
    string from = "", to = ""; bool occupancy;
    readonly Dictionary<string, FrameworkElement> anchors = new();

    public override async Task Load() => Draw(await Api.Current.Call("report", Args()));
    object Args(string table = null) => from == "" ? new { occupazione = occupancy, tabella = table } : new { dal = from, al = to, occupazione = occupancy, tabella = table };
    /// <summary>floatformat:"2g": 1.234,50</summary>
    static string Money(JsonNode n) => (n?.GetValue<decimal>() ?? 0).ToString("#,##0.00", It) + " €";
    static Button Act(string text, Func<Task> run) { var b = F.Small(BA(text, run)); b.MinWidth = 76; return b; }

    void Draw(JsonNode d)
    {
        anchors.Clear(); from = d.S("dal"); to = d.S("al"); var first = DateOnly.Parse(from); var last = DateOnly.Parse(to);
        var page = Col(F.Heading(null, "Report", $"Come è andato il periodo, per prenotazioni con inizio dal {first.ToString("d MMMM yyyy", It)} al {last.ToString("d MMMM yyyy", It)}. Ogni tabella si scarica in Excel o si stampa in PDF."));
        // ----- the period
        var a = new DatePicker { SelectedDate = first.ToDateTime(TimeOnly.MinValue), Width = 170 }; var b = new DatePicker { SelectedDate = last.ToDateTime(TimeOnly.MinValue), Width = 170 };
        async Task Show(string f, string t) { var (of, ot, oo) = (from, to, occupancy); from = f; to = t; occupancy = false; try { await Load(); } catch (ApiError ex) { (from, to, occupancy) = (of, ot, oo); Main.Flash(ex.Message, true); } }
        var show = BA("Mostra periodo", () => Show(a.SelectedDate?.ToString("yyyy-MM-dd") ?? from, b.SelectedDate?.ToString("yyyy-MM-dd") ?? to)); show.VerticalAlignment = VerticalAlignment.Bottom;
        page.Children.Add(Row(F.Field("Dal", a), F.Field("Al", b).M(12, 0, 12, 0), show).M(0, 0, 0, 14));
        var years = d.A("anni").Select(y => y.ToString()).ToList();
        if (years.Count > 1)
        {
            var line = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) }; line.Children.Add(T("Anno intero: ", size: 14).M(0, 0, 6, 0)); ((TextBlock)line.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            foreach (var y in years) line.Children.Add(F.Small(BA(y, () => Show(y + "-01-01", y + "-12-31"))).M(0, 0, 6, 0));
            page.Children.Add(line);
        }
        // ----- the dashboard
        var dash = d["cruscotto"];
        if (dash != null)
        {
            Border Kpi(string label, string value, string small = null, string anchor = null)
            {
                var k = new Border { Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16, 18, 16),
                    Child = Col(T(label, size: 13, color: Res("Muted")), T(value, size: 24, bold: true).M(0, 6, 0, 0), small == null ? null : T(small, size: 13, color: Res("Muted")).M(0, 6, 0, 0)) };
                if (anchor != null) { k.Cursor = System.Windows.Input.Cursors.Hand; k.MouseLeftButtonUp += (_, _) => { if (anchors.TryGetValue(anchor, out var e)) e.BringIntoView(); }; }
                return k;
            }
            var kpis = new List<FrameworkElement> { Kpi("Incassato", Money(dash["incassato"])), Kpi("Visite", dash.S("visite")), Kpi("Media a visita", Money(dash["media"])), Kpi("Clienti", dash.S("clienti")) };
            if (dash.I("daIncassareN") > 0) kpis.Add(Kpi("Da incassare", Money(dash["daIncassare"]), $"{dash.I("daIncassareN")} scontrin{(dash.I("daIncassareN") == 1 ? "o" : "i")}", "da_incassare"));
            if (dash.I("sospesiN") > 0) kpis.Add(Kpi("Non ancora in uno scontrino", Money(dash["sospesi"]), $"{dash.I("sospesiN")} cont{(dash.I("sospesiN") == 1 ? "o" : "i")} in sospeso", "sospesi"));
            page.Children.Add(F.Cols(kpis.Count, 14, kpis.ToArray()).M(0, 0, 0, 14));
            // Incassi per mese: a bar a month, as high as its money on the best month
            var months = dash.A("mesi").ToList(); var top = months.Select(m => m["incassato"].GetValue<decimal>()).DefaultIfEmpty(0).Max(); if (top == 0) top = 1;
            var bars = new UniformGrid { Rows = 1, Height = 180 }; var labels = new UniformGrid { Rows = 1, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var m in months)
            {
                var v = m["incassato"].GetValue<decimal>(); var h = Math.Max(1, (double)Math.Round(100 * v / top, MidpointRounding.ToEven)) * 180 / 102;
                var cell = new Grid { ToolTip = $"{m.S("nome")}: {Money(m["incassato"])}", Background = System.Windows.Media.Brushes.Transparent };
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) }); cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                var bar = new Border { Background = Res("Brand"), Height = h, VerticalAlignment = VerticalAlignment.Bottom }; Grid.SetColumn(bar, 1); cell.Children.Add(bar); bars.Children.Add(cell);
                var l = T(m.S("nome"), size: 10, color: Res("Muted")); l.TextAlignment = TextAlignment.Center; l.ToolTip = Money(m["incassato"]); labels.Children.Add(l);
            }
            var staff = dash.A("operatori").ToList(); var best = staff.Select(o => o["incassato"].GetValue<decimal>()).DefaultIfEmpty(0).Max(); if (best == 0) best = 1;
            var pdf = Act("PDF", () => { DashPdf(dash, months, staff); return Task.CompletedTask; });
            var left = new Border { Style = Style("Card"), Child = Col(F.Section("Incassi per mese", null, pdf), bars.M(0, 10, 0, 0), labels) };
            var list = Col();
            foreach (var o in staff)
            {
                var w = (double)Math.Max(0, Math.Round(100 * o["incassato"].GetValue<decimal>() / best, MidpointRounding.ToEven));
                var track = new Grid { Height = 14 }; track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - Math.Min(w, 100), GridUnitType.Star) });
                var back = new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(7) }; Grid.SetColumnSpan(back, 2); track.Children.Add(back); track.Children.Add(new Border { Background = Res("Brand"), CornerRadius = new CornerRadius(7, 0, 0, 7) });
                var name = T(o.S("nome"), size: 14); name.TextTrimming = TextTrimming.CharacterEllipsis; var money = T(Money(o["incassato"]), size: 14); money.TextAlignment = TextAlignment.Right;
                var g = new Grid { Margin = new Thickness(0, 10, 0, 0) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                Grid.SetColumn(track, 2); Grid.SetColumn(money, 4); track.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(name); g.Children.Add(track); g.Children.Add(money); list.Children.Add(g);
            }
            var right = new Border { Style = Style("Card"), Child = Col(T("Incassi per operatore", "H2"), list) };
            var charts = new Grid { Margin = new Thickness(0, 0, 0, 18) }; charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) }); charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); charts.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(right, 2); charts.Children.Add(left); charts.Children.Add(right); page.Children.Add(charts);
        }
        // ----- the tables
        foreach (var s in d.A("sezioni"))
        {
            var key = s.S("chiave"); var head = s.A("testa").Select(h => h.ToString()).ToArray(); var rows = s.A("righe").Select(r => r.AsArray().Select(x => x.ToString()).ToArray()).ToList();
            var note = NOTES[key] + (s.I("altri") > 0 ? key == "elenco" ? $": qui i primi 200, tutti gli altri ({s.I("altri")}) nell'Excel" : ": qui i primi 200, tutti nell'Excel" : "");
            var acts = Row(Act("Excel", async () => Downloads.Save($"report-{key}-{from}-{to}.xlsx", await Api.Current.Bytes("report.excel", Args(key)))),
                Act("PDF", () => { TablePdf(s.S("titolo"), note, head, rows, s.A("numeri").Select(n => n.GetValue<int>()).ToArray()); return Task.CompletedTask; }).M(8, 0, 0, 0));
            acts.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(acts, Dock.Right);
            var top = new DockPanel(); top.Children.Add(acts); var p = T(note, size: 14, color: Res("Muted")); p.TextWrapping = TextWrapping.Wrap; top.Children.Add(Col(T(s.S("titolo"), "H2"), p));
            var body = Col(top);
            if (key == "occupazione" && d.Bo("pesante"))
                body.Children.Add(Row(F.Small(BA("Calcola l'occupazione del periodo", async () => { occupancy = true; await Load(); if (anchors.TryGetValue("occupazione", out var e)) e.BringIntoView(); })), T("Su più di tre mesi ci vuole qualche secondo", size: 13, color: Res("Muted")).M(8, 0, 0, 0)).M(0, 12, 0, 0));
            else if (rows.Count > 0) body.Children.Add(F.Table(head, rows).M(0, 8, 0, 0));
            else body.Children.Add(T("Niente da mostrare in questo periodo.", size: 14, color: Res("Muted")).M(0, 10, 0, 0));
            var card = F.Card(body); anchors[key] = card; page.Children.Add(card);
        }
        Content = F.Page(1480, page);
    }

    // ----- PDF: only that card, on A4, into Download («Titolo dal al.pdf»)
    void TablePdf(string title, string note, string[] head, List<string[]> rows, int[] numbers)
    {
        var doc = Printing.Doc(); doc.Blocks.Add(Printing.P(title, 18, true)); doc.Blocks.Add(Printing.P(note, 10, false, Printing.Grey, 8));
        if (rows.Count > 0) doc.Blocks.Add(Printing.Table(F.Widths(head, rows), head, rows, numbers)); else doc.Blocks.Add(Printing.P("Niente da mostrare in questo periodo.", 11, false, Printing.Grey));
        Printing.SavePdf(doc, $"{title} {from} {to}");
    }
    void DashPdf(JsonNode dash, List<JsonNode> months, List<JsonNode> staff)
    {
        var doc = Printing.Doc(); doc.Blocks.Add(Printing.P("Incassi per mese", 18, true));
        var kpi = new List<string[]> { new[] { "Incassato", Money(dash["incassato"]) }, new[] { "Visite", dash.S("visite") }, new[] { "Media a visita", Money(dash["media"]) }, new[] { "Clienti", dash.S("clienti") } };
        if (dash.I("daIncassareN") > 0) kpi.Add(new[] { "Da incassare", Money(dash["daIncassare"]) });
        if (dash.I("sospesiN") > 0) kpi.Add(new[] { "Non ancora in uno scontrino", Money(dash["sospesi"]) });
        doc.Blocks.Add(Printing.Table(new[] { 3.0, 2 }, new[] { "Voce", "Valore" }, kpi, new[] { 1 }));
        doc.Blocks.Add(Printing.Table(new[] { 3.0, 2 }, new[] { "Mese", "Incassato" }, months.Select(m => new[] { m.S("nome"), Money(m["incassato"]) }), new[] { 1 }));
        doc.Blocks.Add(Printing.P("Incassi per operatore", 14, true, null, 0));
        doc.Blocks.Add(Printing.Table(new[] { 3.0, 2 }, new[] { "Operatore", "Incassato" }, staff.Select(o => new[] { o.S("nome"), Money(o["incassato"]) }), new[] { 1 }));
        Printing.SavePdf(doc, $"Incassi per mese {from} {to}");
    }
}
