using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/quotes.html: Preventivi, the list with its search.</summary>
public sealed class QuotesPage : Page
{
    string q = "";
    internal static string Euro(string v) => (decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : 0).ToString("#,##0.00", AgendaPage.It) + " €";
    public override async Task Load()
    {
        var d = await Api.Current.Call("preventivi.elenco", new { q });
        var intro = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        intro.Inlines.Add("Un preventivo per il cliente con servizi, prodotti e prezzi, da stampare o salvare in PDF con l'intestazione dell'attività. Quando il cliente accetta, lo apri in Cassa. ");
        var how = new Hyperlink(new Run("Come si usa")) { Foreground = Res("Brand"), TextDecorations = null }; how.Click += (_, _) => Main.Show("guida"); intro.Inlines.Add(how);
        var add = B("Nuovo preventivo", () => Main.ShowPage(new QuoteFormPage()), "Primary"); add.VerticalAlignment = VerticalAlignment.Bottom;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(add, Dock.Right); head.Children.Add(add); head.Children.Add(Col(T("Preventivi", "H1"), intro));
        var search = Box(q); search.Tag = "Nome del cliente o numero"; AgendaPage.Hint(search); search.Width = 320;
        async Task Go() { q = search.Text.Trim(); await Load(); }
        search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Guard(Go); };
        var filters = Row(F.Field("Cerca", search), BA("Cerca", Go).M(14, 0, 0, 0), q != "" ? B("Tutti", async () => { q = ""; await Guard(Load); }, "Ghost").M(8, 0, 0, 0) : null);
        foreach (FrameworkElement e in filters.Children) e.VerticalAlignment = VerticalAlignment.Bottom; filters.Margin = new Thickness(0, 0, 0, 18);
        var list = Col(); var rows = d.A("righe").ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var id = r.S("id");
            var line = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; line.Inlines.Add(new Run(r.S("numero")) { FontWeight = FontWeights.Bold }); line.Inlines.Add(" " + r.S("chi"));
            var meta = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap }; meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            meta.Inlines.Add($"{Euro(r.S("totale"))} · del {DateOnly.Parse(r.S("creato")):dd/MM/yyyy}" + (r.S("valido") != "" ? $" · valido fino al {DateOnly.Parse(r.S("valido")):dd/MM/yyyy}" : ""));
            if (r.S("cliente") != "") { meta.Inlines.Add(" · "); var cid = r.S("cliente"); var l = new Hyperlink(new Run("scheda cliente")) { Foreground = Res("Brand"), TextDecorations = null }; l.Click += (_, _) => Main.ShowPage(new ClientPage(cid)); meta.Inlines.Add(l); }
            var acts = Row(F.Small(B("Apri", () => Main.ShowPage(new QuoteSheetPage(id)))), F.Small(B("Modifica", () => Main.ShowPage(new QuoteFormPage { Id = id }), "Ghost")).M(8, 0, 0, 0));
            var row = new DockPanel(); acts.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(acts, Dock.Right); row.Children.Add(acts); row.Children.Add(Col(line, meta));
            list.Children.Add(new Border { Child = row, Padding = new Thickness(0, 10, 0, 10), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1) });
        }
        if (rows.Count == 0) list.Children.Add(T(q != "" ? "Nessun preventivo trovato." : "Ancora nessun preventivo: premi Nuovo preventivo.", size: 14, color: Res("Muted")));
        Content = F.Page(1480, head, F.Card(filters, list));
    }
}

/// <summary>templates/quote_form.html + the quote part of app.js: who the client is and what he wants to do; the rows from
/// the price list or by hand, a row written by hand can be saved among the products.</summary>
public sealed class QuoteFormPage : Page
{
    public string Id = "", Client = "";
    sealed class Line { public TextBox Desc = Box(""), Qty = Box("1"), Price = Box(""); public string Ref = ""; public bool Save; public TextBlock Amount; public Border Box; }
    readonly List<Line> lines = new(); StackPanel rowsBox; TextBlock total; JsonNode kept; string error = "";
    static decimal Num(string v) => decimal.TryParse((v ?? "").Replace(",", ".").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : 0;
    static string Eur(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", ",") + " €";
    static void Columns(Grid g) { foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(90), new GridLength(120), new GridLength(120), new GridLength(230) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w }); }

    public override async Task Load()
    {
        var d = kept ?? await Api.Current.Call("preventivo.dati", new { id = Id, cliente = Client }); kept = null; lines.Clear();
        var page = Col(F.Back("Preventivi", () => Main.Show("preventivi")), F.Heading(null, Id != "" ? "Preventivo " + d.S("numero") : "Nuovo preventivo", "Chi è il cliente e cosa vuole fare. Le righe si aggiungono dal listino o a mano: una riga scritta a mano si può salvare tra i prodotti."));
        if (error != "") { page.Children.Add(F.Notice(error, "error")); error = ""; }
        // ----- Cliente
        var first = Box(d.S("nome")); first.MaxLength = 80; var last = Box(d.S("cognome")); last.MaxLength = 80; var phone = Box(d.S("telefono")); phone.MaxLength = 40; var email = Box(d.S("email")); email.MaxLength = 160;
        var isNew = new CheckBox { IsChecked = d["nuovoCliente"] != null ? d.Bo("nuovoCliente") : Id == "", Margin = new Thickness(0, 18, 0, 0) };
        var nt = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; nt.Inlines.Add("Salva il cliente nella scheda clienti"); var sm = new Run(" Se c'è già con la stessa email, telefono o nome, il preventivo va sulla sua scheda") { FontSize = 13 }; sm.SetResourceReference(TextElement.ForegroundProperty, "Muted"); nt.Inlines.Add(sm); isNew.Content = nt;
        var find = F.ClientSearch(c => { first.Text = c.S("nome"); last.Text = c.S("cognome"); phone.Text = c.S("telefono"); email.Text = c.S("email"); isNew.Visibility = Visibility.Collapsed; });
        if (d.S("cliente") != "") { find.Set($"{d.S("nome")} {d.S("cognome")}".Trim(), d.S("cliente")); isNew.Visibility = Visibility.Collapsed; }
        find.Box.TextChanged += (_, _) => { if (find.Id() == "") isNew.Visibility = Visibility.Visible; };
        page.Children.Add(F.Card(T("Cliente", "H2"), F.Label("Cerca nella scheda clienti", "(facoltativo)"), find.Box, find.Found, F.Grid2(F.Field("Nome", first), F.Field("Cognome", last), F.Field("Telefono", phone), F.Field("Email", email)), isNew));
        // ----- Cosa vuole fare
        var pick = new ComboBox(); var items = new List<object> { new ComboBoxItem { Content = "Scegli un servizio o un prodotto", Tag = null } }; var group = "";
        foreach (var i in d.A("listino"))
        {
            if (i.S("gruppo") != group) { group = i.S("gruppo"); items.Add(new ComboBoxItem { Content = group, IsEnabled = false, FontWeight = FontWeights.Bold }); }
            items.Add(new ComboBoxItem { Content = "   " + i.S("nome") + (i.S("prezzo") != "" ? $" · {i.S("prezzo")} €" : ""), Tag = i });
        }
        pick.ItemsSource = items; pick.SelectedIndex = 0;
        pick.SelectionChanged += (_, _) => { if ((pick.SelectedItem as ComboBoxItem)?.Tag is JsonNode i) { Add(i.S("nome"), i.S("prezzo"), i.S("rif"), "1", false); pick.SelectedIndex = 0; } };
        var free = B("Riga scritta a mano", () => { var l = Add("", "", "", "1", false); l.Desc.Focus(); }); free.VerticalAlignment = VerticalAlignment.Bottom; free.HorizontalAlignment = HorizontalAlignment.Left;
        var th = new Grid { Margin = new Thickness(0, 14, 0, 0) }; Columns(th);
        foreach (var (s, col) in new[] { ("Descrizione", 0), ("Quantità", 1), ("Prezzo €", 2), ("Importo", 3) }) { var t = T(s, size: 13, bold: true, color: Res("Muted")).M(4, 0, 4, 6); Grid.SetColumn(t, col); th.Children.Add(t); }
        rowsBox = Col(); total = T("0,00 €", size: 16, bold: true);
        var foot = new Grid(); Columns(foot); var tl = T("Totale", size: 16, bold: true).M(4, 10, 4, 0); Grid.SetColumnSpan(tl, 3); foot.Children.Add(tl); total.Margin = new Thickness(4, 10, 4, 0); Grid.SetColumn(total, 3); foot.Children.Add(total);
        page.Children.Add(F.Card(F.Section("Cosa vuole fare"), F.Grid2(F.Field("Aggiungi dal listino", pick), free), th, rowsBox, new Border { BorderBrush = Res("Ink"), BorderThickness = new Thickness(0, 2, 0, 0), Child = foot, Margin = new Thickness(0, 4, 0, 0) },
            T("«Salva tra i prodotti» su una riga scritta a mano: dopo il salvataggio la trovi in Prodotti, in Cassa e nei prossimi preventivi", size: 13, color: Res("Muted")).M(0, 10, 0, 0)));
        foreach (var l in d.A("righe")) Add(l.S("desc"), l.S("price"), l.S("rif"), l["qty"]?.ToString() ?? "1", l.Bo("save"));
        Sum();
        // ----- validity, notes, save
        var valid = new DatePicker { SelectedDate = DateTime.TryParse(d.S("valido"), out var v) ? v : null }; var notes = Box(d.S("note"), true); notes.MaxLength = 2000; notes.MinHeight = 90; notes.Tag = "per esempio: prezzi indicativi, da confermare dopo la prova colore"; AgendaPage.Hint(notes);
        JsonObject Form() => new()
        {
            ["id"] = Id, ["first_name"] = first.Text, ["last_name"] = last.Text, ["phone"] = phone.Text, ["email"] = email.Text, ["notes"] = notes.Text, ["contact"] = find.Id(), ["new_client"] = isNew.IsChecked == true && find.Id() == "", ["valid_until"] = valid.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
            ["righe"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["desc"] = l.Desc.Text, ["qty"] = l.Qty.Text, ["price"] = l.Price.Text, ["rif"] = l.Ref, ["save"] = l.Save }).ToArray()),
        };
        var save = new Button { Content = "Salva e apri il PDF", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        save.Click += async (_, _) =>
        {
            var f = Form(); save.IsEnabled = false;
            try { var r = await Api.Current.Call("preventivo.salva", f.DeepClone()); Main.Flash(r.S("testo")); Main.ShowPage(new QuoteSheetPage(r.S("id"))); }
            catch (NeedsLogin) { Shell.Instance?.SignedOut(); }
            catch (ApiError ex)
            {
                // the quote as it was typed, with what stopped it
                error = ex.Message;
                kept = new JsonObject { ["numero"] = d.S("numero"), ["nome"] = first.Text, ["cognome"] = last.Text, ["telefono"] = phone.Text, ["email"] = email.Text, ["note"] = notes.Text, ["cliente"] = find.Id(), ["nuovoCliente"] = isNew.IsChecked == true, ["valido"] = f.S("valid_until"),
                    ["listino"] = d["listino"]?.DeepClone(), ["righe"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["desc"] = l.Desc.Text, ["qty"] = l.Qty.Text, ["price"] = l.Price.Text, ["rif"] = l.Ref, ["save"] = l.Save }).ToArray()) };
                await Guard(Load);
            }
            finally { save.IsEnabled = true; }
        };
        page.Children.Add(F.Card(F.Grid2(F.Field("Valido fino al", valid), null), F.Label("Note per il cliente", "(facoltative)"), notes, save));
        if (Id != "")
        {
            var del = BA("Elimina il preventivo", async () => { if (!Ask($"Eliminare il preventivo {d.S("numero")}?")) return; var r = await Api.Current.Call("preventivo.elimina", new { id = Id }); Main.Flash(r.S("testo")); Main.Show("preventivi"); }, "Ghost"); del.Foreground = Res("Danger"); del.HorizontalAlignment = HorizontalAlignment.Left;
            page.Children.Add(del);
        }
        Content = F.Page(1480, page);
    }
    Line Add(string desc, string price, string rf, string qty, bool saveIt)
    {
        var l = new Line { Ref = rf ?? "", Save = saveIt }; l.Desc.Text = desc; l.Desc.MaxLength = 120; l.Price.Text = price; l.Price.Tag = "0,00"; AgendaPage.Hint(l.Price); l.Qty.Text = qty; l.Qty.TextAlignment = TextAlignment.Center;
        l.Amount = T("0,00 €", size: 14);
        var g = new Grid(); Columns(g);
        void Put(FrameworkElement e, int col) { e.Margin = new Thickness(4, 0, 4, 0); e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, col); g.Children.Add(e); }
        Put(l.Desc, 0); Put(l.Qty, 1); Put(l.Price, 2); Put(l.Amount, 3);
        var keep = new System.Windows.Controls.Primitives.ToggleButton { Content = "Salva tra i prodotti", IsChecked = saveIt, Style = Style("Tile"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Padding = new Thickness(11, 6, 11, 6), ToolTip = "Dopo il salvataggio la riga è anche in Prodotti", Visibility = l.Ref != "" ? Visibility.Collapsed : Visibility.Visible };
        void Tone() { keep.Background = Res(l.Save ? "WarnSoft" : "Surface"); keep.BorderBrush = Res(l.Save ? "Warn" : "Line"); }
        keep.Click += (_, _) => { l.Save = keep.IsChecked == true; Tone(); }; Tone();
        var x = F.Small(B("×", () => { lines.Remove(l); rowsBox.Children.Remove(l.Box); Sum(); }, "Ghost")); x.ToolTip = "Togli la riga";
        Put(Row(keep, x.M(8, 0, 0, 0)), 4);
        l.Box = new Border { Child = g, Padding = new Thickness(0, 6, 0, 6), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0) };
        foreach (var t in new[] { l.Qty, l.Price }) t.TextChanged += (_, _) => Sum();
        lines.Add(l); rowsBox.Children.Add(l.Box); Sum();
        return l;
    }
    void Sum()
    {
        if (total == null) return; decimal t = 0;
        foreach (var l in lines) { var a = Num(l.Qty.Text) * Num(l.Price.Text); t += a; l.Amount.Text = Eur(a); }
        total.Text = Eur(t);
    }
}

/// <summary>templates/quote_sheet.html: the quote as the client gets it, with the business's header; PDF, Apri in Cassa.</summary>
public sealed class QuoteSheetPage(string id) : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    public override async Task Load()
    {
        var d = await Api.Current.Call("preventivo.foglio", new { id }); var h = d["intestazione"]; string Euro(string v) => QuotesPage.Euro(v);
        var page = Col(F.Back("Preventivi", () => Main.Show("preventivi")), F.Heading(null, "Preventivo " + d.S("numero"), $"{d.S("chi")} · {Euro(d.S("totale"))}",
            B("Modifica", () => Main.ShowPage(new QuoteFormPage { Id = id }), "Ghost"), Can("sale.write") ? B("Apri in Cassa", () => Main.ShowPage(new CassaPage { Quote = id })) : null, B("PDF", () => Print(d), "Primary")));
        if (d.A("mancano").Any())
            page.Children.Add(F.Notice($"Per l'intestazione mancano questi dati: {string.Join(", ", d.A("mancano").Select(x => x.ToString()))}" + (Business.Bo("staff") ? ": li scrivi nella scheda dell'attività." : ": li inserisce l'amministratore del programma."), "warning"));
        var brand = Col(F.Logo(Business, 64), T(h.S("nome"), size: 15, bold: true).M(0, 6, 0, 0));
        foreach (var line in Header(h)) brand.Children.Add(T(line, size: 13.5));
        var meta = Col(T("Preventivo", "Eyebrow"), T("n. " + d.S("numero"), size: 20, bold: true), T("del " + DateOnly.Parse(d.S("creato")).ToString("d MMMM yyyy", It), size: 13.5), d.S("valido") != "" ? T("valido fino al " + DateOnly.Parse(d.S("valido")).ToString("d MMMM yyyy", It), size: 13.5) : null);
        foreach (FrameworkElement e in meta.Children) e.HorizontalAlignment = HorizontalAlignment.Right;
        var top = new DockPanel(); DockPanel.SetDock(meta, Dock.Right); top.Children.Add(meta); top.Children.Add(brand);
        var client = Col(T("Per", "Eyebrow"), T(d.S("chi"), size: 15, bold: true), d.S("telefono") != "" ? T("Tel. " + d.S("telefono"), size: 13.5) : null, d.S("email") != "" ? T(d.S("email"), size: 13.5) : null); client.Margin = new Thickness(0, 22, 0, 14);
        var table = new Grid(); foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(70), new GridLength(120), new GridLength(130) }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Cells(int kind, bool even, params string[] cells)
        {
            var r = table.RowDefinitions.Count; table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < cells.Length; i++)
            {
                var t = kind == 0 ? T(cells[i].ToUpperInvariant(), size: 11.5, bold: true, color: Res("Muted")) : T(cells[i], size: kind == 2 ? (i == 3 ? 19 : 17) : 14, bold: kind == 2); if (i > 0) t.TextAlignment = TextAlignment.Right;
                var cell = new Border { Child = t, Padding = new Thickness(12, kind == 2 ? 16 : 11, 12, 11), BorderBrush = Res(kind == 2 ? "Ink" : "Line"), BorderThickness = new Thickness(0, kind == 2 ? 2 : 0, 0, kind == 2 ? 0 : 1), Background = kind == 0 ? Res("BrandSoft") : even ? Tint((System.Windows.Media.SolidColorBrush)Res("BrandSoft"), .45) : null };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, i); table.Children.Add(cell);
            }
        }
        Cells(0, false, "Descrizione", "Q.tà", "Prezzo", "Importo"); var n = 0;
        foreach (var l in d.A("righe")) Cells(1, ++n % 2 == 0, l.S("desc"), l.S("qty"), Euro(l.S("prezzo")), Euro(l.S("importo")));
        Cells(2, false, "Totale", "", "", Euro(d.S("totale")));
        var sheet = Col(top, client, table);
        if (d.S("note") != "") { sheet.Children.Add(T("Note", "Eyebrow").M(0, 18, 0, 4)); sheet.Children.Add(T(d.S("note"), size: 14)); }
        sheet.Children.Add(T("Prezzi IVA inclusa. Questo preventivo non è un documento fiscale: lo scontrino si emette al pagamento.", size: 13, color: Res("Muted")).M(0, 18, 0, 0));
        page.Children.Add(new Border { Style = Style("Card"), Child = sheet, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(36) });
        Content = F.Page(1480, page);
    }
    static IEnumerable<string> Header(JsonNode h)
    {
        if (h.S("indirizzo") != "") yield return h.S("indirizzo");
        if (h.S("telefono") != "") yield return "Tel. " + h.S("telefono");
        if (h.S("email") != "") yield return h.S("email");
        if (h.S("piva") != "") yield return "P. IVA " + h.S("piva");
        if (h.S("cf") != "" && h.S("cf") != h.S("piva")) yield return "C.F. " + h.S("cf");
    }
    void Print(JsonNode d)
    {
        var h = d["intestazione"]; var doc = Printing.Doc(); string Euro(string v) => QuotesPage.Euro(v);
        if (F.Logo(Business, 56) is Image logo) doc.Blocks.Add(new BlockUIContainer(logo) { Margin = new Thickness(0, 0, 0, 6) });
        doc.Blocks.Add(Printing.P(h.S("nome"), 13, true, null, 2)); foreach (var line in Header(h)) doc.Blocks.Add(Printing.P(line, 10, false, null, 1));
        doc.Blocks.Add(Printing.P("PREVENTIVO", 9, true, Printing.Grey, 2)); ((Paragraph)doc.Blocks.LastBlock).Margin = new Thickness(0, 14, 0, 2);
        doc.Blocks.Add(Printing.P("n. " + d.S("numero"), 16, true, null, 2)); doc.Blocks.Add(Printing.P("del " + DateOnly.Parse(d.S("creato")).ToString("d MMMM yyyy", It) + (d.S("valido") != "" ? " · valido fino al " + DateOnly.Parse(d.S("valido")).ToString("d MMMM yyyy", It) : ""), 10, false, null, 12));
        doc.Blocks.Add(Printing.P("PER", 9, true, Printing.Grey, 2)); doc.Blocks.Add(Printing.P(d.S("chi"), 12, true, null, 1));
        if (d.S("telefono") != "") doc.Blocks.Add(Printing.P("Tel. " + d.S("telefono"), 10, false, null, 1)); if (d.S("email") != "") doc.Blocks.Add(Printing.P(d.S("email"), 10, false, null, 1));
        doc.Blocks.Add(Printing.Table(new[] { 5, 1, 1.6, 1.8 }, new[] { "Descrizione", "Q.tà", "Prezzo", "Importo" }, d.A("righe").Select(l => new[] { l.S("desc"), l.S("qty"), Euro(l.S("prezzo")), Euro(l.S("importo")) }), new[] { 1, 2, 3 }, new[] { "Totale", "", "", Euro(d.S("totale")) }));
        if (d.S("note") != "") { doc.Blocks.Add(Printing.P("NOTE", 9, true, Printing.Grey, 2)); doc.Blocks.Add(Printing.P(d.S("note"), 11, false, null, 10)); }
        doc.Blocks.Add(Printing.P("Prezzi IVA inclusa. Questo preventivo non è un documento fiscale: lo scontrino si emette al pagamento.", 9, false, Printing.Grey));
        Printing.SavePdf(doc, "Preventivo " + d.S("numero"));
    }
}
