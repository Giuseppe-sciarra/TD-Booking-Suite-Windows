using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/contact_history.html + _client_history.html: Storico del cliente: everything recorded on his card
/// in a period (or always): services, products, amounts, with whom; to print or save as PDF. Also the «Storico» window
/// of the Cassa (Dialog), with the bill behind it.</summary>
public sealed class ClientHistoryPage(string id) : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    string since = "", until = ""; JsonNode d; bool modal; ContentControl host;
    static string Euro(string v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x.ToString("#,##0.00", It) + " €" : "0,00 €";
    static bool Money(string v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) && x != 0;

    public override async Task Load()
    {
        d = await Api.Current.Call("cliente.storico", new { id, dal = since, al = until });
        var sheet = Sheet();
        if (modal) { host.Content = sheet; return; }
        var pdf = B("PDF", Print, "Primary");
        Content = F.Page(1480, F.Back($"Scheda di {d.S("nome")} {d.S("cognome")}".Trim(), () => Main.ShowPage(new ClientPage(id))),
            F.Heading(null, "Storico del cliente", "Tutto quello che è registrato nella scheda: servizi, prodotti, importi, con chi. Da stampare o salvare in PDF.", pdf), sheet);
    }

    /// <summary>The sheet alone in a window over the Cassa: the bill stays behind it.</summary>
    public static void Dialog(MainWindow main, string id)
    {
        var page = new ClientHistoryPage(id) { Main = main, modal = true, host = new ContentControl { Focusable = false } };
        var w = new Window { Title = "Storico del cliente", Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 960, Height = Math.Min(820, main.ActualHeight * 0.92), Background = Res("Bg"), ShowInTaskbar = false,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(22, 18, 22, 22), Content = page.host } };
        w.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) w.Close(); };
        _ = Guard(page.Load);
        w.ShowDialog();
    }

    StackPanel Sheet()
    {
        var today = DateOnly.Parse(d.S("oggi"));
        var from = new DatePicker { SelectedDate = DateTime.TryParse(d.S("dal"), out var a) ? a : null, Width = 150 }; var to = new DatePicker { SelectedDate = DateTime.TryParse(d.S("al"), out var b) ? b : null, Width = 150 };
        async Task Go(string s, string u) { since = s; until = u; await Load(); }
        string Iso(DateOnly x) => x.ToString("yyyy-MM-dd");
        var filters = Row(F.Field("Dal", from), F.Field("Al", to).M(14, 0, 0, 0), BA("Mostra", () => Go(from.SelectedDate?.ToString("yyyy-MM-dd") ?? "", to.SelectedDate?.ToString("yyyy-MM-dd") ?? "")).M(14, 0, 0, 0),
            F.Small(BA("Oggi", () => Go(Iso(today), Iso(today)), "Ghost")).M(8, 0, 0, 0), F.Small(BA("Questo mese", () => Go(Iso(new DateOnly(today.Year, today.Month, 1)), Iso(today)), "Ghost")).M(8, 0, 0, 0),
            F.Small(BA("Quest'anno", () => Go(Iso(new DateOnly(today.Year, 1, 1)), Iso(today)), "Ghost")).M(8, 0, 0, 0), since != "" || until != "" ? F.Small(BA("Sempre", () => Go("", ""), "Ghost")).M(8, 0, 0, 0) : null);
        foreach (FrameworkElement e in filters.Children) e.VerticalAlignment = VerticalAlignment.Bottom;
        var body = Col(T($"Storico {d.S("nome")} {d.S("cognome")}".Trim(), "H2"), T($"{d.S("attivita")} · {Period()} · stampato il {today:dd/MM/yyyy}", size: 14, color: Res("Muted")),
            T(string.Join(" · ", new[] { d.S("telefono") != "" ? "Tel. " + d.S("telefono") : "", d.S("email") }.Where(x => x != "")), size: 13).M(0, 4, 0, 10));
        var kpis = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (label, value) in Kpis())
        {
            var strong = T(value, size: 22, bold: true);
            kpis.Children.Add(new Border { Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 14, 8), MinWidth = 160, Child = Col(T(label, size: 12.5, color: Res("Muted")), strong) });
        }
        body.Children.Add(kpis);
        var table = new Grid();
        foreach (var w in new[] { new GridLength(100), new GridLength(1, GridUnitType.Star), new GridLength(150), new GridLength(110) }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Cells(bool head, bool foot, bool even, params UIElement[] cells)
        {
            var r = table.RowDefinitions.Count; table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = new Border { Child = cells[i], Padding = new Thickness(12, 11, 12, 11), BorderBrush = Res(foot ? "Ink" : "Line"), BorderThickness = new Thickness(0, foot ? 2 : 0, 0, foot ? 0 : 1), Background = head || even ? Res("BrandSoft") : null, Opacity = 1 };
                if (even && !head) cell.Background = Tint((System.Windows.Media.SolidColorBrush)Res("BrandSoft"), 0.45);
                Grid.SetRow(cell, r); Grid.SetColumn(cell, i); if (foot && i == 0) Grid.SetColumnSpan(cell, 3); if (foot && i == 1) Grid.SetColumn(cell, 3);
                table.Children.Add(cell);
            }
        }
        TextBlock Th(string s, bool right = false) { var t = T(s.ToUpperInvariant(), size: 11.5, bold: true, color: Res("Muted")); if (right) t.TextAlignment = TextAlignment.Right; return t; }
        TextBlock Num(string s, double size = 14, bool bold = false) { var t = T(s, size: size, bold: bold); t.TextAlignment = TextAlignment.Right; return t; }
        Cells(true, false, false, Th("Data"), Th("Cosa"), Th("Con"), Th("Importo", true));
        var rows = d.A("righe").ToList(); var n = 0;
        foreach (var r in rows)
            Cells(false, false, ++n % 2 == 0, T(DateOnly.Parse(r.S("data")).ToString("dd/MM/yyyy"), size: 14),
                Col(T(r.S("servizio") + (r.S("tipo") == "parked" ? " *" : ""), size: 14, bold: true), r.S("dettagli") != "" ? T(r.S("dettagli"), size: 12.5, color: Res("Muted")) : null), T(r.S("operatore"), size: 14), Num(r["importo"] != null ? Euro(r.S("importo")) : ""));
        if (rows.Count == 0) { var none = T("Niente registrato" + (since != "" || until != "" ? " in questo periodo" : "") + ".", size: 14, color: Res("Muted")); Cells(false, false, false, none); Grid.SetColumnSpan((UIElement)table.Children[^1], 4); }
        else Cells(false, true, false, T("Totale", size: 17, bold: true), Num(Euro(d.S("speso")), 19, true));
        body.Children.Add(table);
        if (d.A("giftcard").Any())
        {
            body.Children.Add(T("Gift card", size: 15, bold: true).M(0, 16, 0, 6));
            foreach (var g in d.A("giftcard")) body.Children.Add(T("• " + GiftLine(g), size: 13));
        }
        if (d.A("prossimi").Any())
        {
            body.Children.Add(T("Prossimi appuntamenti", size: 15, bold: true).M(0, 16, 0, 6));
            foreach (var x in d.A("prossimi")) body.Children.Add(T("• " + NextLine(x), size: 13));
        }
        var card = F.Card(filters); card.Margin = new Thickness(0, 8, 0, 14);
        var outp = Col(card, new Border { Style = Style("Card"), Child = body });
        if (modal) { var pdf = F.Small(B("PDF", Print, "Primary")); pdf.HorizontalAlignment = HorizontalAlignment.Right; outp.Children.Insert(0, pdf); }
        return outp;
    }
    string Period() => since != "" || until != "" ? ((d.S("dal") != "" ? $"dal {DateOnly.Parse(d.S("dal")):dd/MM/yyyy} " : "") + (d.S("al") != "" ? $"al {DateOnly.Parse(d.S("al")):dd/MM/yyyy}" : "")).Trim() : "tutto lo storico";
    List<(string, string)> Kpis()
    {
        var k = new List<(string, string)> { ("Visite", d.S("visite")), ("Speso", Euro(d.S("speso"))) };
        if (Money(d.S("deve"))) k.Add(("Deve (da pagare)", Euro(d.S("deve"))));
        if (Money(d.S("fuoriScontrino"))) k.Add(("Non ancora in uno scontrino *", Euro(d.S("fuoriScontrino"))));
        if (Money(d.S("credito"))) k.Add(("Credito gift card", Euro(d.S("credito"))));
        return k;
    }
    static string GiftLine(JsonNode g) => $"{g.S("codice")}: credito {Euro(g.S("credito"))} su {Euro(g.S("importo"))}" + (g.S("scade") != "" ? $", {(g.Bo("scaduta") ? "scaduta il" : "scade il")} {DateOnly.Parse(g.S("scade")):dd/MM/yyyy}" : "");
    string NextLine(JsonNode x) => $"{new AgendaPage { Main = Main }.Also(Business.S("fuso")).Local(x.S("inizio")):dd/MM/yyyy HH:mm} · {x.S("servizio")}" + (x.S("operatore") != "" ? " con " + x.S("operatore") : "");

    void Print()
    {
        var today = DateOnly.Parse(d.S("oggi")); var doc = Printing.Doc();
        doc.Blocks.Add(Printing.P($"Storico {d.S("nome")} {d.S("cognome")}".Trim(), 18, true));
        doc.Blocks.Add(Printing.P($"{d.S("attivita")} · {Period()} · stampato il {today:dd/MM/yyyy}", 10, false, Printing.Grey));
        doc.Blocks.Add(Printing.P(string.Join(" · ", new[] { d.S("telefono") != "" ? "Tel. " + d.S("telefono") : "", d.S("email") }.Where(x => x != "")), 10, false, null, 10));
        doc.Blocks.Add(Printing.P(string.Join("     ", Kpis().Select(k => $"{k.Item1}: {k.Item2}")), 11, true, null, 8));
        var rows = d.A("righe").Select(r => new[] { DateOnly.Parse(r.S("data")).ToString("dd/MM/yyyy"), r.S("servizio") + (r.S("tipo") == "parked" ? " *" : "") + (r.S("dettagli") != "" ? "\n" + r.S("dettagli") : ""), r.S("operatore"), r["importo"] != null ? Euro(r.S("importo")) : "" }).ToList();
        if (rows.Count == 0) rows.Add(new[] { "Niente registrato" + (since != "" || until != "" ? " in questo periodo" : "") + ".", "", "", "" });
        doc.Blocks.Add(Printing.Table(new[] { 1.1, 4, 1.6, 1.3 }, new[] { "Data", "Cosa", "Con", "Importo" }, rows, new[] { 3 }, d.A("righe").Any() ? new[] { "Totale", "", "", Euro(d.S("speso")) } : null));
        if (d.A("giftcard").Any()) { doc.Blocks.Add(Printing.P("Gift card", 13, true)); foreach (var g in d.A("giftcard")) doc.Blocks.Add(Printing.P("• " + GiftLine(g), 10, false, null, 2)); }
        if (d.A("prossimi").Any()) { doc.Blocks.Add(Printing.P("Prossimi appuntamenti", 13, true)); foreach (var x in d.A("prossimi")) doc.Blocks.Add(Printing.P("• " + NextLine(x), 10, false, null, 2)); }
        Printing.SavePdf(doc, $"Storico {d.S("nome")} {d.S("cognome")}".Trim());
    }
}
