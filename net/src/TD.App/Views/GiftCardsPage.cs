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

/// <summary>templates/standby.html: Stand-by, the Parcheggio of every register in one place.</summary>
public sealed class StandbyPage : Page
{
    public override async Task Load()
    {
        var d = await Api.Current.Call("standby"); var today = DateOnly.Parse(d.S("oggi"));
        var intro = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        intro.Inlines.Add("I conti preparati e messi da parte con "); intro.Inlines.Add(new Run("Parcheggia") { FontStyle = FontStyles.Italic }); intro.Inlines.Add(" in Cassa. "); intro.Inlines.Add(new Run("Riprendi") { FontStyle = FontStyles.Italic });
        intro.Inlines.Add(" lo apre in Cassa com'era per fare lo scontrino; se in Cassa c'è già un conto aperto, va lui nel Parcheggio.");
        var h1 = T("Stand-by", "H1"); var go = B("Vai alla Cassa", () => Main.Show("cassa")); go.VerticalAlignment = VerticalAlignment.Bottom;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(go, Dock.Right); head.Children.Add(go); head.Children.Add(Col(h1, intro));
        var page = Col(F.Back("Cassa", () => Main.Show("cassa")), head);
        foreach (var g in d.A("gruppi"))
        {
            var sede = g.S("sede"); var rows = g.A("righe").ToList(); var list = Col();
            for (int i = 0; i < rows.Count; i++)
            {
                var id = rows[i].S("id");
                // Riprendi opens the Cassa of its location with the bill as it was; the one open there goes to the Parcheggio
                var resume = F.Small(B("Riprendi", () => Main.ShowPage(new CassaPage { ResumeStandby = id, ResumeSede = sede }), "Primary"));
                var drop = F.Small(BA("×", async () => { if (!Ask("Togliere questo conto dal Parcheggio? Le righe si perdono, nessuno scontrino è stato fatto.")) return; var r = await Api.Current.Call("cassa.azione", new { sede, azione = "drop_standby", standby_id = id }); Main.Flash(r.S("testo")); await Load(); }, "Ghost")); drop.ToolTip = "Togli dal Parcheggio";
                var acts = Row(resume, drop.M(8, 0, 0, 0)); acts.VerticalAlignment = VerticalAlignment.Top; var row = new DockPanel(); DockPanel.SetDock(acts, Dock.Right); row.Children.Add(acts); row.Children.Add(CassaPage.StandbyText(rows[i], today));
                list.Children.Add(new Border { Child = row, Padding = new Thickness(0, 12, 0, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 2) });
            }
            if (rows.Count == 0) list.Children.Add(T("Nessun conto in stand-by.", size: 14, color: Res("Muted")));
            var sh = F.Section(d.Bo("piuSedi") ? g.S("nome") : "In stand-by", rows.Count > 0 ? rows.Count : null); sh.Margin = new Thickness(0, 0, 0, 12);
            var card = F.Card(sh, list); card.BorderThickness = new Thickness(1.5, 5, 1.5, 1.5); card.BorderBrush = Hex("#2f8f6b"); card.Margin = new Thickness(0, 0, 0, 22);
            page.Children.Add(card);
        }
        if (!d.A("gruppi").Any()) page.Children.Add(F.Card(T("Nessuna cassa collegata.", size: 14, color: Res("Muted"))));
        Content = F.Page(1480, page);
    }
}

/// <summary>templates/gift_cards.html + gift_print.html: the gift cards sold, with the credit left; one sold from here
/// opens the Cassa with the card already in the bill; a card given to a client, its expiry, the card to print.</summary>
public sealed class GiftCardsPage : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    string q = ""; AgendaPage z;
    static string Euro(string v) => (decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : 0).ToString("#,##0.00", It) + " €";

    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }.Also(Business.S("fuso"));
        Draw(await Api.Current.Call("giftcard.elenco", new { q }));
    }
    async Task Act(object args) { var r = await Api.Current.Call("giftcard.azione", args); if (r.S("testo") != "") Main.Flash(r.S("testo")); await Load(); }

    void Draw(JsonNode d)
    {
        var names = d.Bo("nomi"); var today = DateOnly.Parse(d.S("oggi"));
        var intro = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        intro.Inlines.Add("Le gift card vendute, con il credito che resta. Si vendono da qui o dal bottone Gift card della Cassa, sempre con lo scontrino, e si usano nel conto con il loro codice. ");
        var how = new Hyperlink(new Run("Come si usa")) { Foreground = Res("Brand"), TextDecorations = null }; how.Click += (_, _) => Main.Show("guida"); intro.Inlines.Add(how);
        var page = Col(F.Back("Cassa", () => Main.Show("cassa")), Col(T("Gift card", "H1"), intro).M(0, 0, 0, 16));
        // ----- Vendi una gift card
        var amount = Box(""); amount.MaxLength = 8; amount.Tag = "per esempio 50"; AgendaPage.Hint(amount);
        var expires = new DatePicker { SelectedDate = DateTime.TryParse(d.S("scade"), out var ex) ? ex : null };
        var presets = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var v in d.A("importi")) { var vv = v.ToString(); presets.Children.Add(F.Small(B(vv + " €", () => { amount.Text = vv; amount.Focus(); })).M(0, 0, 8, 0)); }
        var who = names ? F.ClientSearch() : default;
        var sell = B("Apri in Cassa con la gift card", () => Main.ShowPage(new CassaPage { GiftAmount = amount.Text.Trim() == "" ? "0" : amount.Text, GiftExpires = expires.SelectedDate?.ToString("yyyy-MM-dd") ?? "", Client = names ? who.Id() : "" }), "Primary"); sell.HorizontalAlignment = HorizontalAlignment.Left; sell.Margin = new Thickness(0, 14, 0, 0);
        var fields = new List<FrameworkElement> { F.Field("Importo in euro", amount), F.Field("Scade il", expires, null, "Proposti 12 mesi. Vuoto: non scade") };
        if (names) fields.Add(Col(F.Label("Intestata a", "(facoltativo)"), who.Box, who.Found));
        page.Children.Add(F.Card(T("Vendi una gift card", "H2"), T("Importo, scadenza e, se vuoi, a chi è intestata: poi si apre la Cassa con la gift card già nel conto, per lo scontrino.", size: 14, color: Res("Muted")), presets, F.Cols(names ? 3 : 2, 18, fields.ToArray()), sell));
        // ----- the numbers and the search
        Border Kpi(string label, string value) => new() { Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16, 18, 16), Child = Col(T(label, size: 12.5, color: Res("Muted")), T(value, size: 24, bold: true)) };
        var search = Box(q); search.MaxLength = 12; search.Tag = "per esempio K7QP3MZA"; AgendaPage.Hint(search); search.CharacterCasing = CharacterCasing.Upper; search.Width = 260;
        async Task Go() { q = search.Text.Trim(); await Load(); }
        search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Guard(Go); };
        var filters = Row(F.Field("Cerca il codice", search), BA("Cerca", Go).M(14, 0, 0, 0), q != "" ? B("Tutte", async () => { q = ""; await Guard(Load); }, "Ghost").M(8, 0, 0, 0) : null);
        foreach (FrameworkElement e in filters.Children) e.VerticalAlignment = VerticalAlignment.Bottom;
        page.Children.Add(F.Card(F.Cols(2, 14, Kpi("Credito ancora da usare", Euro(d.S("credito"))), Kpi("Gift card con credito", d.S("conCredito"))).M(0, 0, 0, 14), filters));
        // ----- the cards
        var list = Col(); var cards = d.A("righe").ToList();
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i]; var id = c.S("id");
            var line = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new Run(c.S("codice")) { FontWeight = FontWeights.Bold, FontSize = 16 }); line.Inlines.Add(" "); line.Inlines.Add(new InlineUIContainer(MainWindow.Pill(c.S("statoNome"), c.Bo("inSospeso") ? "warn" : "")) { BaselineAlignment = BaselineAlignment.Center });
            var meta = new Span { FontSize = 13 }; meta.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            meta.Inlines.Add($"  Valore {Euro(c.S("valore"))} · credito "); meta.Inlines.Add(new Run(Euro(c.S("credito"))) { FontWeight = FontWeights.Bold }); meta.Inlines.Add($" · venduta il {z.Local(c.S("venduta")):dd/MM/yyyy}" + (c.S("scade") != "" ? $" · scade il {DateOnly.Parse(c.S("scade")):dd/MM/yyyy}" : " · non scade"));
            if (c.S("chi") != "") { meta.Inlines.Add(" · intestata a "); if (names) { var cid = c.S("cliente"); var l = new Hyperlink(new Run(c.S("chi"))) { Foreground = Res("Brand"), TextDecorations = null }; l.Click += (_, _) => Main.ShowPage(new ClientPage(cid)); meta.Inlines.Add(l); } else meta.Inlines.Add(c.S("chi")); }
            else meta.Inlines.Add(" · non intestata");
            line.Inlines.Add(meta);
            var print = F.Small(B("Stampa per il cliente", () => Main.ShowPage(new GiftPrintPage(c))));
            var top = new DockPanel(); print.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(print, Dock.Right); top.Children.Add(print); top.Children.Add(line);
            var body = Col(top);
            var moves = c.A("movimenti").ToList();
            if (moves.Count > 0)
                body.Children.Add(F.Details($"Movimenti ({moves.Count})", Col(moves.Select(m => (UIElement)T($"• {z.Local(m.S("quando")):dd/MM/yyyy HH:mm}: " + (m.I("centesimi") < 0 ? $"usati {Euro((Math.Abs(m.I("centesimi")) / 100m).ToString(CultureInfo.InvariantCulture))}" : $"restituiti {Euro((m.I("centesimi") / 100m).ToString(CultureInfo.InvariantCulture))} (scontrino non emesso o annullato)"), size: 13)).ToArray()).M(0, 6, 0, 0), plain: true).M(0, 8, 0, 0));
            var exp = new DatePicker { SelectedDate = DateTime.TryParse(c.S("scade"), out var e0) ? e0 : null };
            var saveExp = F.Small(BA("Salva la scadenza", () => Act(new { card = id, azione = "expires", expires = exp.SelectedDate?.ToString("yyyy-MM-dd") ?? "" }))); saveExp.HorizontalAlignment = HorizontalAlignment.Left; saveExp.Margin = new Thickness(0, 10, 0, 0);
            var edit = new List<FrameworkElement>();
            if (names)
            {
                var pick = F.ClientSearch(); var assign = F.Small(BA("Intesta", () => Act(new { card = id, azione = "assign", contact = pick.Id() }))); assign.HorizontalAlignment = HorizontalAlignment.Left; assign.Margin = new Thickness(0, 10, 0, 0);
                edit.Add(Col(F.Label("Intesta a"), pick.Box, pick.Found, assign));
            }
            edit.Add(Col(F.Label("Scade il"), exp, T("Vuoto: non scade", size: 13, color: Res("Muted")).M(0, 4, 0, 0), saveExp));
            body.Children.Add(F.Details("Intesta o cambia la scadenza", F.Cols(2, 18, edit.ToArray()), plain: true).M(0, 8, 0, 0));
            list.Children.Add(new Border { Child = body, Padding = new Thickness(0, 12, 0, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == cards.Count - 1 ? 0 : 1) });
        }
        if (cards.Count == 0) list.Children.Add(T(q != "" ? "Nessuna gift card con questo codice." : "Ancora nessuna gift card: si vendono dalla Cassa, bottone Gift card.", size: 14, color: Res("Muted")));
        page.Children.Add(F.Card(list));
        Content = F.Page(1480, page);
    }
}

/// <summary>templates/gift_print.html: the card to print or save as PDF and give to the client with the receipt.</summary>
public sealed class GiftPrintPage(JsonNode card) : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    public override Task Load()
    {
        string Euro(string v) => (decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : 0).ToString("#,##0.00", It) + " €";
        var code = card.S("codice"); var valid = (card.S("scade") != "" ? $"Valida fino al {DateOnly.Parse(card.S("scade")).ToString("d MMMM yyyy", It)}" : "Senza scadenza") + ". Si usa in negozio dicendo il codice" + (card.S("credito") != card.S("valore") ? $": credito ancora disponibile {Euro(card.S("credito"))}" : "") + ".";
        var value = T(Euro(card.S("valore")), size: 40, bold: true, color: Res("Brand")); var big = T(code, size: 30, bold: true); big.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        var body = Col(F.Logo(Business, 56), T(Business.S("nome"), "Eyebrow"), T("Gift card " + code, "H2"), value, card.S("chi") != "" ? T("Per " + card.S("chi"), size: 15, bold: true).M(0, 6, 0, 0) : null, big.M(0, 14, 0, 10), T(valid, size: 14, color: Res("Muted")));
        foreach (FrameworkElement e in body.Children) e.HorizontalAlignment = HorizontalAlignment.Center;
        var sheet = new Border { Style = Style("Card"), Child = body, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(36) };
        void Print()
        {
            var doc = Printing.Doc(); Paragraph C(Paragraph p) { p.TextAlignment = TextAlignment.Center; return p; }
            doc.Blocks.Add(C(Printing.P(Business.S("nome").ToUpperInvariant(), 10, true, Printing.Grey, 6))); doc.Blocks.Add(C(Printing.P("Gift card " + code, 20, true, null, 10))); doc.Blocks.Add(C(Printing.P(Euro(card.S("valore")), 36, true, null, 10)));
            if (card.S("chi") != "") doc.Blocks.Add(C(Printing.P("Per " + card.S("chi"), 14, true, null, 10)));
            var codeP = C(Printing.P(code, 28, true, null, 12)); codeP.FontFamily = new System.Windows.Media.FontFamily("Consolas"); doc.Blocks.Add(codeP); doc.Blocks.Add(C(Printing.P(valid, 11, false, Printing.Grey)));
            Printing.SavePdf(doc, "Gift card " + code);
        }
        Content = F.Page(1480, F.Heading(null, "Gift card " + code, "Da stampare o salvare in PDF e dare al cliente insieme allo scontrino.", B("Torna alle gift card", () => Main.Show("giftcard"), "Ghost"), B("PDF", Print, "Primary")), sheet);
        return Task.CompletedTask;
    }
}
