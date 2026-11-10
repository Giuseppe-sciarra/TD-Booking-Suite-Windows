using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/contact_detail.html: who the client is on the left, always in view (contacts, alerts, actions,
/// notes, discount, receipts, signed consents, tags and preferences, fiscal data, the card's data); on the right the
/// tabs: Panoramica, Prenotazioni, the record, Consensi e privacy.</summary>
public sealed class ClientPage(string id) : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    JsonNode d; AgendaPage z; string tab; int page = 1, tp = 1; string from = "";
    /// <summary>The tab to open on (#foto, #scheda in the links of the web).</summary>
    public string StartTab { init => tab = value; }
    DateTime Local(string iso) => z.Local(iso);
    static string Euro(string v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x.ToString("#,##0.00", It) + " €" : v;
    static void Web(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    static FontFamily Head => (FontFamily)Application.Current.Resources["FontDisplay"];

    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }; z.UseZone(Business.S("fuso"));
        d = await Api.Current.Call("cliente.scheda", new { id, pagina = page, tp, da = from });
        tab ??= "panoramica";
        if (d["scheda"] == null && tab == "scheda" || d["foto"] == null && tab == "foto") tab = "panoramica";
        Draw();
    }
    async Task Act(JsonObject args, string open = null)
    {
        args["id"] = id;
        var r = await Api.Current.Call("cliente.azione", args);
        if (r.S("testo") != "") Main.Flash(r.S("testo"));
        if (open != null) tab = open;
        await Load();
    }
    Button Save(string text, Func<JsonObject> args, string style = null, string open = null, bool small = true)
    {
        var b = BA(text, () => Act(args(), open), style); b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 12, 0, 0);
        return small ? F.Small(b) : b;
    }
    static Border SideCard(params UIElement[] items) { var c = F.Card(items); c.Margin = new Thickness(0, 0, 0, 14); return c; }
    static TextBlock H2(string text) { var t = T(text, "H2"); t.FontSize = 16; t.Margin = new Thickness(0, 0, 0, 2); return t; }
    static TextBlock Small(string text) => T(text, size: 13, color: Res("Muted")).M(0, 0, 0, 8);
    static Button Wide(Button b) { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = new Thickness(0, 8, 0, 0); return b; }

    void Draw()
    {
        var data = d["dati"]; var raw = d["grezzi"] as JsonObject ?? new JsonObject(); var hasRaw = raw.Count > 0; var n = d["numeri"]; var spec = d["scheda"];
        var write = Can("booking.write"); var name = data.S("first_name");
        var side = Col();
        // ----- who
        var h1 = T($"{data.S("first_name")} {data.S("last_name")}".Trim(), "H1"); h1.FontSize = 22; h1.Margin = new Thickness(0);
        var since = "Cliente dal " + Local(d.S("creato")).ToString("MMMM yyyy", It) + (raw.S("birthday") != "" && DateOnly.TryParse(raw.S("birthday"), out var bd) ? " · compleanno il " + bd.ToString("d MMMM", It) : "");
        var who = Col(Row(F.Avatar(name, 64).M(0, 0, 14, 0), Col(h1, T(since, size: 13, color: Res("Muted")).M(0, 2, 0, 0))));
        ((FrameworkElement)((StackPanel)who.Children[0]).Children[1]).VerticalAlignment = VerticalAlignment.Center;
        var chips = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (d.Bo("vip")) chips.Children.Add(F.ClientChip("VIP", "vip", "star"));
        if (d.Bo("bloccato")) chips.Children.Add(F.ClientChip("Non prenotabile online", "danger", "absent"));
        if (n.I("assenze") > 0) chips.Children.Add(F.ClientChip($"{n.I("assenze")} assenz{(n.I("assenze") == 1 ? "a" : "e")}", "danger", "absent"));
        foreach (var t in raw.A("tags")) chips.Children.Add(F.ClientChip(t.ToString()));
        if (d.S("sconto") != "") chips.Children.Add(F.ClientChip("Sconto " + d.S("sconto"), "discount"));
        if (n.I("visite") == 0) chips.Children.Add(F.ClientChip("Nuovo cliente", "new"));
        chips.Children.Add(F.ClientChip("Comunicazioni: " + StatusName(d.S("consenso")).ToLowerInvariant(), d.S("consenso") switch { "yes" => "ok", "revoked" => "danger", "none" => "none", _ => "" }));
        who.Children.Add(chips);
        if (data.S("phone") != "" || data.S("email") != "")
        {
            var list = Col();
            FrameworkElement Line(string icon, string text, string url)
            {
                var box = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Res("BrandSoft"), Child = Icons.Icon(icon, Res("Brand"), 16, 1), Margin = new Thickness(0, 0, 10, 0) };
                var link = F.Link(text, () => Web(url)); link.FontWeight = FontWeights.SemiBold; link.VerticalAlignment = VerticalAlignment.Center; link.TextTrimming = TextTrimming.CharacterEllipsis;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(box, Dock.Left); row.Children.Add(box); row.Children.Add(link); return row;
            }
            if (data.S("phone") != "") list.Children.Add(Line("phone", data.S("phone"), "tel:" + data.S("phone")));
            if (d.S("whatsapp") != "") list.Children.Add(Line("chat", "Scrivi su WhatsApp", d.S("whatsapp")));
            if (data.S("email") != "") list.Children.Add(Line("mail", data.S("email"), "mailto:" + data.S("email")));
            who.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 14, 0, 0), Child = list });
        }
        Border Alert(UIElement mark, bool soft, params UIElement[] body)
        {
            var m = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res(soft ? "Brand" : "Warn"), Child = mark, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            var row = new DockPanel(); DockPanel.SetDock(m, Dock.Left); row.Children.Add(m); row.Children.Add(Col(body));
            return new Border { Background = Res(soft ? "BrandSoft" : "WarnSoft"), BorderBrush = Res(soft ? "Pastel" : "Warn"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 14, 0, 0), Child = row };
        }
        TextBlock Glyph(string s) => new() { Text = s, FontSize = 13, FontWeight = FontWeights.ExtraBold, Foreground = Res("Surface"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        TextBlock P(string s) { var t = T(s, size: 13); t.Margin = new Thickness(0, 2, 0, 0); return t; }
        if (d.A("allergie").Any())
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var a in d.A("allergie")) { var c = F.Allergy(a.ToString()); c.Background = Res("Surface"); c.Margin = new Thickness(0, 0, 6, 4); wrap.Children.Add(c); }
            who.Children.Add(Alert(Glyph("!"), false, T("Ha segnalato", size: 13.5, bold: true), wrap));
        }
        if (d.A("deve").Any())
            who.Children.Add(Alert(Glyph("€"), false, T("Deve " + Euro(d.S("deveTotale")), size: 13.5, bold: true),
                P(string.Join(", ", d.A("deve").Select(s => $"{Euro(s.S("importo"))} dello scontrino del {(s.S("stampato") != "" ? Local(s.S("stampato")).ToString("dd/MM/yyyy") : "")}"))),
                Can("sale.write") ? F.Link("Segna pagato in cassa", () => Main.Show("cassa"), 13) : null));
        if (d.A("giftcard").Any())
            who.Children.Add(Alert(Icons.Icon("gift", Res("Surface"), 13, 1), true, T("Gift card", size: 13.5, bold: true),
                P(string.Join(" · ", d.A("giftcard").Select(g => $"{g.S("codice")}: {Euro(g.S("credito"))}" + (g.Bo("scaduta") ? " (scaduta)" : g.S("scade") != "" ? " fino al " + DateOnly.Parse(g.S("scade")).ToString("dd/MM/yyyy") : "")))),
                Can("sale.write") ? F.Link("Tutte le gift card", () => Main.Show("giftcard"), 13) : null));
        if (d.A("preventivi").Any())
        {
            var line = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) }; var first = true;
            foreach (var qv in d.A("preventivi")) { if (!first) line.Children.Add(T(" · ", size: 13)); first = false; { var qid = qv.S("id"); line.Children.Add(F.Link(qv.S("numero"), () => Main.ShowPage(new QuoteSheetPage(qid)), 13)); } line.Children.Add(T(" " + Euro(qv.S("totale")), size: 13)); }
            who.Children.Add(Alert(Icons.Icon("briefcase", Res("Surface"), 13, 1), true, T("Preventivi", size: 13.5, bold: true), line));
        }
        if (write)
        {
            var acts = Col(); acts.Margin = new Thickness(0, 8, 0, 0);
            acts.Children.Add(Wide(B("Nuova prenotazione", () => Main.ShowPage(new BookingPage { PrefillClient = id }), "Primary")));
            if (spec != null) acts.Children.Add(Wide(B($"Registra {spec.S("uno")}", async () => { tab = "scheda"; openNew = true; await Guard(Load); })));
            acts.Children.Add(Wide(B("Metti in lista d'attesa", () => Main.ShowPage(new WaitlistPage { Client = id }), "Ghost")));
            acts.Children.Add(Wide(B("Nuovo preventivo", () => Main.ShowPage(new QuoteFormPage { Client = id }), "Ghost")));
            if (d["note"] != null) acts.Children.Add(Wide(B("Storico in PDF", () => Main.ShowPage(new ClientHistoryPage(id)), "Ghost")));
            who.Children.Add(acts);
        }
        side.Children.Add(SideCard(who));
        // ----- notes
        if (d["note"] != null)
        {
            if (write)
            {
                var text = Box(d.S("note"), true); text.MaxLength = 4000; text.MinHeight = 84; text.Tag = "Per esempio: preferisce il sabato, non ama aspettare"; AgendaPage.Hint(text);
                side.Children.Add(SideCard(H2("Note sul cliente"), Small("Compaiono in ogni sua prenotazione. Il cliente non le vede"), text, Save("Salva le note", () => new JsonObject { ["azione"] = "notes", ["staff_notes"] = text.Text })));
            }
            else side.Children.Add(SideCard(H2("Note sul cliente"), Small("Compaiono in ogni sua prenotazione. Il cliente non le vede"), T(d.S("note") != "" ? d.S("note") : "Nessuna nota", size: 14)));
        }
        // ----- discount
        if (Can("catalog.write") && (d.Bo("prezzi") || d.S("sconto") != ""))
        {
            var kind = new ComboBox { ItemsSource = new[] { ("", "Nessuno"), ("percent", "Percentuale %"), ("amount", "Importo fisso €") }.Select(k => new ComboBoxItem { Content = k.Item2, Tag = k.Item1 }).ToList() };
            kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == d.S("scontoTipo"));
            var value = Box(d.S("scontoTipo") != "" ? d.S("scontoValore").Replace(".", ",") : ""); value.MaxLength = 10; value.Tag = "10"; AgendaPage.Hint(value);
            var how = F.Field("Quanto", value); how.Visibility = d.S("scontoTipo") != "" ? Visibility.Visible : Visibility.Hidden;
            kind.SelectionChanged += (_, _) => how.Visibility = (string)((ComboBoxItem)kind.SelectedItem).Tag != "" ? Visibility.Visible : Visibility.Hidden;
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var f1 = F.Field("Tipo", kind); Grid.SetColumn(how, 2); g.Children.Add(f1); g.Children.Add(how);
            side.Children.Add(SideCard(H2("Sconto"), Small("Vale su ogni sua prenotazione, anche quelle già in programma. Le passate restano come sono state pagate"), g,
                Save("Salva lo sconto", () => new JsonObject { ["azione"] = "discount", ["discount_kind"] = (string)((ComboBoxItem)kind.SelectedItem).Tag, ["discount"] = value.Text })));
        }
        else if (d.S("sconto") != "") side.Children.Add(SideCard(H2("Sconto"), F.ClientChip("Sconto " + d.S("sconto"), "discount")));
        // ----- receipts
        if (d.A("scontrini").Any())
        {
            var ul = Col();
            foreach (var s in d.A("scontrini"))
            {
                var meta = Local(s.S("creato")).ToString("dd/MM/yyyy HH:mm") + (s.S("numero") != "" ? " · n. " + s.S("documento") : "") + (s.S("matricola") != "" ? " · RT " + s.S("matricola") : "") + (s.Bo("email") ? " · copia per email" : "");
                var small = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap }; small.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); small.Inlines.Add(meta);
                if (decimal.TryParse(s.S("daPagare"), NumberStyles.Number, CultureInfo.InvariantCulture, out var due) && due > 0)
                {
                    if (s.S("pagato") != "") small.Inlines.Add(" · pagato il " + Local(s.S("pagato")).ToString("dd/MM/yyyy"));
                    else if (s.S("stato") == "done") { small.Inlines.Add(" · "); var r = new Run("da pagare " + Euro(s.S("daPagare"))); r.SetResourceReference(TextElement.ForegroundProperty, "Danger"); small.Inlines.Add(r); }
                }
                var copy = s.S("stato") is "done" or "void";
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
                if (Can("sale.write")) { var b = F.Small(B(copy ? "Copia" : "Alla cassa", () => Main.Show("cassa"), "Ghost")); DockPanel.SetDock(b, Dock.Right); row.Children.Add(b); }
                row.Children.Add(Col(Row(T(Euro(s.S("totale")), size: 14, bold: true).M(0, 0, 6, 0), MainWindow.Pill(s.S("statoNome"), s.S("stato") is "failed" or "unknown" ? "warn" : "")), small));
                ul.Children.Add(row);
            }
            side.Children.Add(SideCard(H2("Scontrini"), ul));
        }
        // ----- waiting list
        if (d.A("attese").Any())
        {
            var ul = Col();
            foreach (var w in d.A("attese"))
            {
                var a = DateOnly.Parse(w.S("dal")); var b = DateOnly.Parse(w.S("al"));
                ul.Children.Add(Col(T(w.S("servizio"), size: 14, bold: true), T(a == b ? a.ToString("dddd d MMMM", It) : $"dal {a:d/MM} al {b:d/MM}", size: 13, color: Res("Muted"))).M(0, 6, 0, 6));
            }
            side.Children.Add(SideCard(H2("In lista d'attesa"), ul, F.Link("Apri la lista d'attesa", () => Main.Show("attesa"))));
        }
        // ----- signed consents
        if (hasRaw && write)
        {
            var body = Col(H2("Consensi firmati"));
            if (raw.A("signed").Any())
                foreach (var s in raw.A("signed"))
                {
                    var when = DateTime.TryParse(s.S("at"), null, DateTimeStyles.RoundtripKind, out var at) ? Local(at.ToUniversalTime().ToString("o")).ToString("dd/MM/yyyy") : "";
                    var img = new Image { MaxHeight = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
                    try { var bi = new BitmapImage(); bi.BeginInit(); bi.StreamSource = new MemoryStream(Convert.FromBase64String(s.S("sign")[(s.S("sign").IndexOf(',') + 1)..])); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); img.Source = bi; } catch (Exception) { }
                    body.Children.Add(F.Details($"{s.S("title")} · firmato il {when}", Col(T(s.S("text"), size: 13.5), new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Child = img, HorizontalAlignment = HorizontalAlignment.Left }).M(0, 6, 0, 8), plain: true).M(0, 6, 0, 6));
                }
            else body.Children.Add(T("Nessun consenso firmato al banco", size: 14, color: Res("Muted")).M(0, 0, 0, 8));
            var title = F.Suggest(d.A("titoliConsenso").Select(t => t.ToString()), "", 120); var text = Box("", true); text.MaxLength = 8000; text.MinHeight = 120;
            var ink = new InkCanvas { Height = 150, Background = Brushes.White, Cursor = Cursors.Pen };
            ink.DefaultDrawingAttributes.Color = Colors.Black; ink.DefaultDrawingAttributes.Width = ink.DefaultDrawingAttributes.Height = 2.2; ink.DefaultDrawingAttributes.FitToCurve = true;
            var pad = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10), Child = ink, ClipToBounds = true };
            string Signature()
            {
                if (ink.Strokes.Count == 0) return "";
                // the same 900×300 picture the web pad makes, white behind the strokes
                var w = Math.Max(ink.ActualWidth, 1); var h = Math.Max(ink.ActualHeight, 1);
                var bmp = new RenderTargetBitmap(900, 300, 96, 96, PixelFormats.Pbgra32); var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 300)); dc.PushTransform(new ScaleTransform(900 / w, 300 / h)); ink.Strokes.Draw(dc); dc.Pop(); }
                bmp.Render(dv); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var ms = new MemoryStream(); enc.Save(ms);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
            var clear = F.Small(B("Cancella la firma", () => ink.Strokes.Clear(), "Ghost")); var sign = F.Small(BA("Salva il consenso firmato", () => Act(new JsonObject { ["azione"] = "sign", ["title"] = title.Text, ["text"] = text.Text, ["signature"] = Signature() }), "Primary"));
            body.Children.Add(F.Details("Fai firmare un consenso", Col(F.Label("Titolo"), title, F.Label("Testo che il cliente legge"), text, F.Label("Firma del cliente"), pad, Row(clear, sign.M(8, 0, 0, 0)).M(0, 12, 0, 0))));
            side.Children.Add(SideCard(body));
        }
        // ----- tags and preferences
        if (hasRaw && write)
        {
            var tags = Box(string.Join(", ", raw.A("tags").Select(t => t.ToString()))); tags.MaxLength = 900; tags.Tag = "Per esempio: capelli ricci, sposa 2027"; AgendaPage.Hint(tags);
            var birth = new DatePicker { SelectedDate = DateTime.TryParse(raw.S("birthday"), out var b0) ? b0 : null, DisplayDateEnd = DateTime.Today };
            var cats = d.A("categorie").Select(c => c.ToString()).ToList();
            var cat = new ComboBox { ItemsSource = cats.Prepend("Nessuna").ToList(), SelectedItem = cats.Contains(raw.S("category")) ? raw.S("category") : "Nessuna" };
            var tests = d.A("test").Select(t => t.ToString()).ToList();
            var test = new DatePicker { SelectedDate = DateTime.TryParse(raw.S("test_date"), out var t0) ? t0 : null, DisplayDateEnd = DateTime.Today };
            var vip = new CheckBox { Content = "Cliente VIP", IsChecked = d.Bo("vip"), Margin = new Thickness(0, 12, 0, 0) }; var blocked = new CheckBox { Content = "Non prenotabile online", IsChecked = d.Bo("bloccato"), Margin = new Thickness(0, 8, 0, 4) };
            side.Children.Add(SideCard(H2("Etichette e preferenze"), F.Label("Etichette"), tags, T("Separate da virgole. Le usi per cercare i clienti e scegliere a chi mandare una comunicazione", size: 13, color: Res("Muted")).M(0, 4, 0, 0),
                F.Label("Data di nascita", "(facoltativa)"), birth, cats.Count > 0 ? F.Field("Categoria", cat) : null,
                tests.Count > 0 ? F.Field("Ultimo test fatto il", test, $"(per {string.Join(", ", tests)})") : null, vip, blocked,
                T("Sulla pagina online gli diciamo di contattare l'attività, con il vostro telefono. Al banco lo prenotate come sempre", size: 13, color: Res("Muted")),
                Save("Salva", () => new JsonObject
                {
                    ["azione"] = "profile", ["tags"] = tags.Text, ["birthday"] = birth.SelectedDate?.ToString("yyyy-MM-dd") ?? "", ["category"] = cat.SelectedItem as string is "Nessuna" or null ? "" : (string)cat.SelectedItem,
                    ["test_date"] = test.SelectedDate?.ToString("yyyy-MM-dd") ?? "", ["vip"] = vip.IsChecked == true, ["online_blocked"] = blocked.IsChecked == true,
                })));
        }
        // ----- fiscal data
        if (d.A("fiscali").Any() || hasRaw && write)
        {
            var body = Col(H2("Dati fiscali"));
            if (d.A("fiscali").Any())
            {
                var facts = d.A("fiscali").ToList();
                for (int i = 0; i < facts.Count; i++)
                {
                    var v = T(facts[i].S("valore"), size: 13.5, bold: true); v.TextAlignment = TextAlignment.Right; v.TextWrapping = TextWrapping.Wrap;
                    var row = new DockPanel(); var l = T(facts[i].S("nome"), size: 13.5, color: Res("Muted")).M(0, 0, 12, 0); DockPanel.SetDock(l, Dock.Left); row.Children.Add(l); row.Children.Add(v);
                    body.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == facts.Count - 1 ? 0 : 1), Padding = new Thickness(0, 7, 0, 7), Child = row });
                }
            }
            else body.Children.Add(Small("Per la fattura e per il codice fiscale sullo scontrino"));
            if (hasRaw && write)
            {
                var kinds = new ComboBox { ItemsSource = d.A("tipiFiscali").Select(k => new ComboBoxItem { Content = k.S("nome"), Tag = k.S("id") }).ToList() };
                kinds.SelectedItem = kinds.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == raw.S("fiscal_kind")) ?? kinds.Items[0];
                TextBox In(string key, int max, string hint = null, string fallback = "") { var t = Box(raw.S(key) != "" ? raw.S(key) : fallback); t.MaxLength = max; if (hint != null) { t.Tag = hint; AgendaPage.Hint(t); } return t; }
                var company = In("company", 160); var tax = In("tax_code", 16); var vat = In("vat", 30); var address = In("address", 200, "Via e numero civico"); var zip = In("zip", 12); var city = In("city", 80);
                var province = In("province", 2, "RM"); var country = In("country", 2, null, "IT"); var sdi = In("sdi", 7); var pec = In("pec", 254);
                var form = Col(F.Label("Tipo"), kinds, F.Label("Ragione sociale", "(aziende e professionisti)"), company, F.Label("Codice fiscale"), tax, F.Label("Partita IVA"), vat, F.Label("Indirizzo"), address, F.Label("CAP"), zip,
                    F.Label("Comune"), city, F.Label("Provincia"), province, F.Label("Nazione"), country, T("La sigla: IT per l'Italia. Per l'estero partita IVA e CAP restano liberi", size: 13, color: Res("Muted")).M(0, 4, 0, 0),
                    F.Label("Codice destinatario SDI"), sdi, F.Label("PEC"), pec,
                    Save("Salva i dati fiscali", () => new JsonObject
                    {
                        ["azione"] = "fiscal", ["fiscal_kind"] = (string)((ComboBoxItem)kinds.SelectedItem).Tag, ["company"] = company.Text, ["tax_code"] = tax.Text, ["vat"] = vat.Text, ["address"] = address.Text, ["zip"] = zip.Text,
                        ["city"] = city.Text, ["province"] = province.Text, ["country"] = country.Text, ["sdi"] = sdi.Text, ["pec"] = pec.Text,
                    }));
                body.Children.Add(F.Details(d.A("fiscali").Any() ? "Modifica i dati fiscali" : "Aggiungi i dati fiscali", form).M(0, 8, 0, 0));
            }
            side.Children.Add(SideCard(body));
        }
        if (d.Bo("unisci")) side.Children.Add(SideCard(H2("Scheda doppia?"), Small("Se la stessa persona ha due schede, uniscile: prenotazioni, scheda e consensi finiscono in una"), Wide(B("Unisci con un'altra scheda", () => Main.ShowPage(new ClientMergePage(id))))));
        if (hasRaw && write)
        {
            var first = Box(raw.S("first_name")); first.MaxLength = 100; var last = Box(raw.S("last_name")); last.MaxLength = 100; var phone = Box(raw.S("phone")); phone.MaxLength = 30; var email = Box(raw.S("email")); email.MaxLength = 254;
            var form = Col(F.Label("Nome"), first, F.Label("Cognome"), last, F.Label("Telefono"), phone, F.Label("Email"), email,
                Save("Salva i dati", () => new JsonObject { ["azione"] = "card", ["first_name"] = first.Text, ["last_name"] = last.Text, ["phone"] = phone.Text, ["email"] = email.Text }, small: false));
            var det = F.Details("Modifica i dati", form, plain: true); ((TextBlock)det.Children[0]).FontWeight = FontWeights.SemiBold; ((TextBlock)det.Children[0]).Foreground = Res("Ink");
            side.Children.Add(SideCard(det));
        }

        // ===== the tabs
        var main = Col(); var panel = new ContentControl { Focusable = false };
        var tabs = Row(); var all = new List<(string Key, Border Box, TextBlock Text)>();
        void Tab(string key, string text, int? count = null)
        {
            var t = T(text, size: 14, bold: true);
            var b = new Border { Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(0, 0, 0, 2), Margin = new Thickness(0, 0, 2, -1), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                Child = Row(t, count is > 0 ? new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(7, 0, 0, 0), Child = T(count.ToString(), size: 11.5, bold: true, color: Res("Brand")) } : null) };
            b.MouseLeftButtonUp += (_, _) => { tab = key; Show(); };
            all.Add((key, b, t)); tabs.Children.Add(b);
        }
        void Show()
        {
            foreach (var (k, b, t) in all) { b.BorderBrush = k == tab ? Res("Brand") : Brushes.Transparent; t.Foreground = Res(k == tab ? "Ink" : "Muted"); }
            panel.Content = tab switch { "prenotazioni" => Bookings(), "scheda" => Record(), "privacy" => PrivacyTab(), "foto" when d["foto"] != null => PhotosTab(), _ => Overview() };
        }
        Tab("panoramica", "Panoramica"); Tab("prenotazioni", "Prenotazioni", n.I("totale"));
        if (spec != null) Tab("scheda", spec.S("titolo"), spec.I("voci"));
        if (d["foto"] != null) Tab("foto", "Foto", d["foto"].I("quante"));
        Tab("privacy", "Consensi e privacy");
        show = Show;
        main.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 16), Child = tabs });
        main.Children.Add(panel); Show();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(main, 2); grid.Children.Add(side); grid.Children.Add(main);
        Content = F.Page(1480, F.Back("Clienti", () => Main.Show("clienti")), grid);
    }
    Action show; bool openNew;
    void Go(string key) { tab = key; show(); }

    // ---------- Panoramica
    UIElement Overview()
    {
        var n = d["numeri"]; var spec = d["scheda"]; var priced = d.Bo("prezzi"); var stays = d.S("parolaVisite") == "Soggiorni";
        var g = new Grid(); var i = 0;
        void Metric(string label, UIElement strong, string small, bool accent = false)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var b = new Border { Padding = new Thickness(18, 12, 18, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Background = accent ? Res("BrandSoft") : Brushes.Transparent,
                Child = Col(T(label, size: 13, bold: true, color: Res("Muted")), strong, T(small, size: 12.5, color: Res("Muted"))) };
            Grid.SetColumn(b, i++); g.Children.Add(b);
        }
        TextBlock Strong(string text, double size = 26, bool brand = false) { var t = T(text, size: size, bold: true, color: brand ? Res("Brand") : null); t.FontFamily = Head; t.Margin = new Thickness(0, 2, 0, 0); t.TextTrimming = TextTrimming.CharacterEllipsis; return t; }
        if (priced) Metric("Speso", Strong(n.S("speso") != "" ? Euro(n.S("speso")) : "—", 24, true), n.S("speso") != "" ? "nelle visite fatte" : "nessun prezzo ancora", true);
        Metric(d.S("parolaVisite"), Strong(n.S("visite"), 26, !priced), n.S("ultima") != "" ? $"{(stays ? "ultimo" : "ultima")} il {Local(n.S("ultima")):dd/MM/yyyy}" : $"ancora {(stays ? "nessuno" : "nessuna")}", !priced);
        var next = n["prossima"];
        Metric("Prossima", Strong(next != null ? Local(next.S("inizio")).ToString("dd/MM") : "—", 30), next != null ? $"{Local(next.S("inizio")):HH:mm}, {next.S("servizio")}" : "nessuna in programma");
        Metric("Preferisce", Strong(n.S("servizio") != "" ? n.S("servizio") : "—", 20), n.S("operatore") != "" ? "di solito con " + n.S("operatore") : n.S("servizio") != "" ? $"{n.I("servizioN")} volt{(n.I("servizioN") == 1 ? "a" : "e")}" : "ancora niente");
        var both = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = Head, Margin = new Thickness(0, 2, 0, 0) };
        both.Inlines.Add(n.S("disdette")); both.Inlines.Add(new Run(" / ") { Foreground = Res("Pastel") }); both.Inlines.Add(n.S("assenze"));
        Metric("Disdette e assenze", both, "disdette / non presentato" + (n.I("tardive") > 0 ? $" · {n.I("tardive")} all'ultimo momento" : ""));
        var metrics = new Border { Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 14), Child = g, ClipToBounds = true };
        // In programma
        var coming = Col(F.Section("In programma", null, F.Link("Tutte le prenotazioni", () => Go("prenotazioni"))));
        if (d.A("inProgramma").Any())
            foreach (var b in d.A("inProgramma"))
            {
                var a = Local(b.S("inizio")); var bid = b.S("id");
                var dd = T(a.ToString("dd"), size: 17, bold: true, color: Res("Brand")); dd.FontFamily = Head; dd.HorizontalAlignment = HorizontalAlignment.Center;
                var mm = T(a.ToString("MMM", It).ToUpperInvariant(), size: 11, bold: true, color: Res("Brand")); mm.HorizontalAlignment = HorizontalAlignment.Center;
                var date = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(11), Background = Res("BrandSoft"), Child = Col(dd, mm), Margin = new Thickness(0, 0, 12, 0) }; ((StackPanel)date.Child).VerticalAlignment = VerticalAlignment.Center;
                var when = a.ToString("dddd HH:mm", It); when = char.ToUpper(when[0]) + when[1..];
                var small = b.S("tipo") == "day" ? $"{Nights(b.I("notti"))}, fino al {Local(b.S("fine")):dd/MM}" + (b.S("operatore") != "" ? " · " + b.S("operatore") : "") : when + (b.S("operatore") != "" ? " · con " + b.S("operatore") : "");
                var what = T(b.S("servizio") + (b.S("variante") != "" ? " · " + b.S("variante") : ""), size: 14, bold: true); what.TextTrimming = TextTrimming.CharacterEllipsis;
                var row = new DockPanel(); DockPanel.SetDock(date, Dock.Left); row.Children.Add(date);
                if (b.S("totale") != "") { var p = T(Euro(b.S("totale")), size: 14, bold: true); p.VerticalAlignment = VerticalAlignment.Center; p.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(p, Dock.Right); row.Children.Add(p); }
                var mid = Col(what, T(small, size: 12.5, color: Res("Muted"))); mid.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(mid);
                var box = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 6, 0, 0), Child = row, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                box.MouseEnter += (_, _) => { box.BorderBrush = Res("Brand"); box.Background = Res("BrandSoft"); }; box.MouseLeave += (_, _) => { box.BorderBrush = Res("Line"); box.Background = Brushes.Transparent; };
                box.MouseLeftButtonUp += (_, _) => Main.ShowPage(new BookingDetailPage(bid));
                coming.Children.Add(box);
            }
        else coming.Children.Add(F.EmptyState("Niente in programma", Can("booking.write") ? F.Link("Prenota il prossimo appuntamento", () => Main.ShowPage(new BookingPage { PrefillClient = id }), 13) : T("Nessuna prenotazione futura", size: 13, color: Res("Muted"))));
        var cards = new List<FrameworkElement> { new Border { Style = Style("Card"), Child = coming } };
        if (spec != null)
        {
            var last = Col(F.Section("Ultimo " + spec.S("uno"), null, F.Link(spec.S("titolo"), () => Go("scheda"))));
            var t = spec.I("pagina") == 1 ? spec.A("righe").FirstOrDefault() : null;
            if (t != null)
            {
                var details = t.S("dettagli"); if (details.Length > 280) details = details[..279] + "…";
                last.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0),
                    Child = Col(T(t.S("servizio"), size: 14, bold: true), T(DateOnly.Parse(t.S("data")).ToString("d MMMM yyyy", It) + (t.S("operatore") != "" ? " · con " + t.S("operatore") : ""), size: 12.5, color: Res("Muted")).M(0, 0, 0, 6), details != "" ? T(details, size: 13.5) : null) });
            }
            else last.Children.Add(F.EmptyState("La scheda è ancora vuota", Can("booking.write") ? F.Link($"Registra il primo {spec.S("uno")}", () => { openNew = true; Go("scheda"); }, 13) : T($"Qui comparirà l'ultimo {spec.S("uno")} registrato", size: 13, color: Res("Muted"))));
            cards.Add(new Border { Style = Style("Card"), Child = last });
        }
        return Col(metrics, F.Cols(2, 14, cards.ToArray()));
    }
    static string Nights(int n) => n == 0 ? "in giornata" : $"{n} nott{(n == 1 ? "e" : "i")}";

    // ---------- Prenotazioni
    UIElement Bookings()
    {
        var n = d["numeri"]; var spec = d["scheda"]; var rows = d.A("prenotazioni").ToList();
        var list = Col();
        for (int i = 0; i < rows.Count; i++)
        {
            var b = rows[i]; var bid = b.S("id"); var a = Local(b.S("inizio")); var day = b.S("tipo") == "day"; var many = b.I("persone") > 1 || b.I("maxPersone") > 1;
            var g = new Grid();
            foreach (var w in new[] { new GridLength(96), new GridLength(14), new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            g.Children.Add(Col(T(a.ToString("dd/MM/yyyy"), size: 14, bold: true), T(a.ToString("HH:mm"), size: 13, color: Res("Muted"))));
            var small = (day ? $"fino al {Local(b.S("fine")):dd/MM/yyyy}, {Nights(b.I("notti"))}" + (b.S("operatore") != "" ? " · " + b.S("operatore") : "") : b.S("operatore") != "" ? "con " + b.S("operatore") : "")
                + (many ? (b.S("operatore") != "" || day ? " · " : "") + $"{b.I("persone")} person{(b.I("persone") == 1 ? "a" : "e")}" : "")
                + (b.S("sede") != "" ? (b.S("operatore") != "" || day || many ? " · " : "") + b.S("sede") : "");
            var what = Col(T(b.S("servizio") + (b.S("variante") != "" ? " · " + b.S("variante") : ""), size: 14, bold: true), T(small, size: 13, color: Res("Muted")));
            if (b.A("allergie").Any()) { var wrap = new WrapPanel(); foreach (var al in b.A("allergie")) { var c = F.Allergy(al.ToString()); c.Margin = new Thickness(0, 5, 6, 0); wrap.Children.Add(c); } what.Children.Add(wrap); }
            if (b.S("note") != "") what.Children.Add(T($"«{b.S("note")}»", size: 13.5, color: Res("Muted")).M(0, 6, 0, 0));
            if (b.S("noteInterne") != "") what.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = T(b.S("noteInterne"), size: 13.5) });
            if (spec != null && b.Bo("registrata")) what.Children.Add(T("✓ Nella scheda", size: 13, bold: true, color: Res("Ok")).M(0, 6, 0, 0));
            else if (spec != null && b.Bo("registrabile") && Can("booking.write")) { var l = F.Link($"Registra il {spec.S("uno")}", async () => { from = bid; tab = "scheda"; openNew = true; await Guard(Load); }, 13); l.FontWeight = FontWeights.SemiBold; l.Margin = new Thickness(0, 6, 0, 0); what.Children.Add(l); }
            if (b.S("stato") is "cancelled" or "no_show") what.Opacity = 0.65;
            Grid.SetColumn(what, 2); g.Children.Add(what);
            if (b.S("totale") != "")
            {
                var price = Col(); if (b.S("sconto") != "") { var s = T(Euro(b.S("prezzo")), size: 12, color: Res("Muted")); s.TextDecorations = TextDecorations.Strikethrough; s.HorizontalAlignment = HorizontalAlignment.Right; price.Children.Add(s); }
                var tot = T(Euro(b.S("totale")), size: 14, bold: true); tot.HorizontalAlignment = HorizontalAlignment.Right; price.Children.Add(tot); price.Margin = new Thickness(14, 0, 0, 0);
                Grid.SetColumn(price, 3); g.Children.Add(price);
            }
            var pill = F.Status(b.S("stato")); pill.Margin = new Thickness(14, 0, 0, 0); pill.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(pill, 4); g.Children.Add(pill);
            var open = F.Link("Apri", () => Main.ShowPage(new BookingDetailPage(bid))); open.Margin = new Thickness(14, 0, 0, 0); Grid.SetColumn(open, 5); g.Children.Add(open);
            list.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1), Padding = new Thickness(0, 14, 0, 14), Child = g });
        }
        if (rows.Count == 0) list.Children.Add(F.EmptyState("Nessuna prenotazione ancora", Can("booking.write") ? F.Link("Fai la prima prenotazione", () => Main.ShowPage(new BookingPage { PrefillClient = id }), 13) : null));
        return F.Card(F.Section("Prenotazioni", null, T($"{n.I("totale")} prenotazion{(n.I("totale") == 1 ? "e" : "i")}, dalla più recente", size: 14, color: Res("Muted"))), list,
            F.Pagination(d.I("pagina"), d.I("pagine"), async p => { page = p; tab = "prenotazioni"; await Guard(Load); }, "← Più recenti", "Meno recenti →"));
    }

    // ---------- the record
    UIElement Record()
    {
        var spec = d["scheda"]; var write = Can("booking.write"); var count = spec.I("voci"); var pre = spec["precompilata"]; var today = DateTime.Parse(spec.S("oggi"));
        var whats = spec.A("cose").Select(x => x.ToString()).ToList(); var people = spec.A("persone").Select(x => x.ToString()).ToList();
        var h2 = T(spec.S("titolo"), "H2"); h2.FontSize = 22; h2.Margin = new Thickness(0, 2, 0, 4);
        var body = Col(T("Scheda cliente", "Eyebrow"), h2, T(count > 0 ? $"{count} {(count == 1 ? "voce" : "voci")}, dalla più recente" : $"Data, {spec.S("cosa").ToLowerInvariant()}, con chi e i dettagli: restano qui per le prossime volte", size: 14, color: Res("Muted")));
        StackPanel Form(JsonNode t, Func<DatePicker, ComboBox, ComboBox, TextBox, UIElement> actions, string hint)
        {
            var date = new DatePicker { SelectedDate = t != null && DateTime.TryParse(t.S(t["data"] != null ? "data" : "date"), out var dt) ? dt : today, DisplayDateEnd = today };
            var what = F.Suggest(whats, t?.S(t["servizio"] != null ? "servizio" : "service"), 120); var staff = F.Suggest(people, t?.S(t["operatore"] != null ? "operatore" : "staff"), 100);
            var details = Box(t?.S("dettagli"), true); details.MaxLength = 4000; details.MinHeight = 100; if (hint != null) { details.Tag = hint; AgendaPage.Hint(details); }
            var g = new Grid();
            foreach (var w in new[] { new GridLength(165), new GridLength(14), new GridLength(1.4, GridUnitType.Star), new GridLength(14), new GridLength(1, GridUnitType.Star) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var f1 = F.Field("Data", date); var f2 = F.Field(spec.S("cosa"), what); var f3 = F.Field("Con chi", staff, t == null || t["id"] == null ? "(facoltativo)" : null);
            Grid.SetColumn(f2, 2); Grid.SetColumn(f3, 4); g.Children.Add(f1); g.Children.Add(f2); g.Children.Add(f3);
            return Col(g, F.Label("Dettagli"), details, actions(date, what, staff, details));
        }
        JsonObject Entry(DatePicker date, ComboBox what, ComboBox staff, TextBox details) => new() { ["azione"] = "treatment", ["date"] = date.SelectedDate?.ToString("yyyy-MM-dd") ?? "", ["service"] = what.Text, ["staff"] = staff.Text, ["details"] = details.Text };
        if (write)
        {
            var open = pre != null || count == 0 || openNew; openNew = false;
            var plus = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Res("Brand"), Child = new TextBlock { Text = "+", FontSize = 19, Foreground = Res("BrandInk"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }, Margin = new Thickness(0, 0, 12, 0) };
            var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            if (pre != null) { var s = T($"dalla prenotazione del {DateTime.Parse(pre.S("date")):dd/MM/yyyy}", size: 13, color: Res("Brand")); s.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(s, Dock.Right); head.Children.Add(s); }
            DockPanel.SetDock(plus, Dock.Left); head.Children.Add(plus); var title = T($"Registra {spec.S("uno")}", size: 14, bold: true, color: Res("Brand")); title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
            var form = Form(pre, (date, what, staff, details) =>
            {
                var save = BA("Salva nella scheda", async () => { var a = Entry(date, what, staff, details); if (pre != null) a["booking"] = pre.S("booking"); from = ""; await Act(a, "scheda"); }, "Primary");
                var row = Row(save, pre != null ? B("Annulla", async () => { from = ""; tab = "scheda"; await Guard(Load); }, "Ghost").M(10, 0, 0, 0) : null); row.Margin = new Thickness(0, 14, 0, 0); return row;
            }, spec.S("suggerimento"));
            form.Margin = new Thickness(18, 2, 18, 18);
            var headBox = new Border { Padding = new Thickness(18, 14, 18, 14), Child = head, BorderBrush = Res("Line") };
            var box = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 16, 0, 24), Child = Col(headBox, form) };
            void Paint(bool on)
            {
                form.Visibility = on ? Visibility.Visible : Visibility.Collapsed; headBox.BorderThickness = new Thickness(0, 0, 0, on ? 1 : 0);
                box.Background = Res(on ? "Surface" : "BrandSoft"); box.BorderThickness = new Thickness(1.5); box.BorderBrush = on ? Res("Line") : new SolidColorBrush(((SolidColorBrush)Res("Pastel")).Color);
                if (!on) box.BorderBrush = new DrawingBrush { TileMode = TileMode.None, Drawing = new GeometryDrawing(null, new Pen(Res("Pastel"), 3) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }, new RectangleGeometry(new Rect(0, 0, 100, 40), 6, 6)) };
                plus.RenderTransformOrigin = new Point(.5, .5); plus.RenderTransform = new RotateTransform(on ? 45 : 0);
            }
            Paint(open); head.MouseLeftButtonUp += (_, _) => Paint(form.Visibility != Visibility.Visible);
            body.Children.Add(box);
        }
        // the timeline: the year, then each entry with its date on the left
        var line = Col(); int year = 0; var first = true;
        foreach (var t in spec.A("righe"))
        {
            var date = DateOnly.Parse(t.S("data")); var tid = t.S("id");
            if (date.Year != year)
            {
                year = date.Year; var y = T(year.ToString(), size: 12, bold: true, color: Res("Surface")); y.FontFamily = Head;
                line.Children.Add(new Border { Background = Res("Ink"), CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(7, line.Children.Count == 0 ? 4 : 20, 0, 12), HorizontalAlignment = HorizontalAlignment.Left, Child = y });
            }
            var dd = T(date.ToString("dd"), size: 23, bold: true, color: Res("Brand")); dd.FontFamily = Head; dd.FontWeight = FontWeights.Bold; dd.HorizontalAlignment = HorizontalAlignment.Center;
            var mm = T(date.ToString("MMM", It).ToUpperInvariant(), size: 11, bold: true, color: Res("Muted")); mm.HorizontalAlignment = HorizontalAlignment.Center; mm.Margin = new Thickness(0, 4, 0, 0);
            var dateBox = new Border { Width = 64, Padding = new Thickness(0, 9, 0, 8), CornerRadius = new CornerRadius(14), Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Child = Col(dd, mm), VerticalAlignment = VerticalAlignment.Top };
            var h3 = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = Head, TextWrapping = TextWrapping.Wrap }; h3.Inlines.Add(t.S("servizio"));
            if (t.S("tipo") == "parked") h3.Inlines.Add(new Run(" *") { ToolTip = "Fatto, non ancora in uno scontrino" });
            var headRow = new DockPanel();
            if (t.S("operatore") != "") { var st = Row(F.Avatar(t.S("operatore"), 24).M(0, 0, 7, 0), T("con " + t.S("operatore"), size: 13, bold: true, color: Res("Muted"))); ((FrameworkElement)st.Children[1]).VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(st, Dock.Right); headRow.Children.Add(st); }
            headRow.Children.Add(h3);
            var card = Col(headRow);
            if (t.S("dettagli") != "") card.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 10, 13, 10), Margin = new Thickness(0, 10, 0, 0), Child = T(t.S("dettagli"), size: 14.5) });
            if (d["foto"] != null && t["foto"] != null && (t.A("foto").Any() || d["foto"].Bo("scrivi")))
                card.Children.Add(PhotoGrid.Make(t.A("foto").Select(x => x.ToString()), true, d["foto"].Bo("scrivi"), new JsonObject { ["cliente"] = id, ["voce"] = tid }, t.I("fotoLiberi"), d["foto"].I("max"), async () => { tab = "scheda"; await Load(); }).M(0, 6, 0, 2));
            var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; var editBox = new ContentControl { Visibility = Visibility.Collapsed, Focusable = false };
            if (write)
            {
                var edit = F.Link("Modifica", () => editBox.Visibility = editBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, 13); DockPanel.SetDock(edit, Dock.Right); foot.Children.Add(edit);
                editBox.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 4, 0, 0), Child = Form(t, (date2, what, staff, details) =>
                {
                    var save = BA("Salva la modifica", async () => { var a = Entry(date2, what, staff, details); a["treatment"] = tid; await Act(a, "scheda"); });
                    var del = F.Small(BA("Elimina la voce", async () => { if (Ask($"Eliminare questa voce dalla scheda di {d["dati"].S("first_name")}?")) await Act(new JsonObject { ["azione"] = "treatment_remove", ["treatment"] = tid }, "scheda"); }, "Ghost")); del.Foreground = Res("Danger");
                    var col = Col(save, del.M(0, 8, 0, 0)); save.HorizontalAlignment = HorizontalAlignment.Left; del.HorizontalAlignment = HorizontalAlignment.Left; col.Margin = new Thickness(0, 14, 0, 0); return col;
                }, null) };
            }
            var when = new TextBlock { FontSize = 12.5 }; when.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); when.Inlines.Add(date.ToString("dddd d MMMM yyyy", It));
            if (t.S("prenotazione") != "") { when.Inlines.Add(" · "); var bidd = t.S("prenotazione"); var l = new Hyperlink(new Run("dalla prenotazione")) { Foreground = Res("Brand"), TextDecorations = null }; l.Click += (_, _) => Main.ShowPage(new BookingDetailPage(bidd)); when.Inlines.Add(l); }
            foot.Children.Add(when); card.Children.Add(foot); card.Children.Add(editBox);
            var cardBox = new Border { BorderBrush = Res(first ? "Brand" : "Line"), BorderThickness = new Thickness(first ? 3 : 1, 1, 1, 1), CornerRadius = new CornerRadius(14), Background = Res("Surface"), Padding = new Thickness(16, 14, 16, 14), Child = card, Margin = new Thickness(16, 0, 0, 0) };
            first = false;
            var item = new DockPanel { Margin = new Thickness(0, 0, 0, 14) }; DockPanel.SetDock(dateBox, Dock.Left); item.Children.Add(dateBox); item.Children.Add(cardBox);
            line.Children.Add(item);
        }
        if (!spec.A("righe").Any())
        {
            var e = Col(T("✎", size: 32, color: Res("Pastel")), T("La scheda è ancora vuota", size: 14, bold: true), T(write ? $"Registra qui sopra il primo {spec.S("uno")}: oppure dalle prenotazioni, con un clic" : $"Qui compariranno {spec.S("titolo").ToLowerInvariant()} registrati dallo staff", size: 13, color: Res("Muted")));
            foreach (FrameworkElement x in e.Children) x.HorizontalAlignment = HorizontalAlignment.Center; e.Margin = new Thickness(12, 30, 12, 30); line.Children.Add(e);
        }
        // the line behind the dates
        var rail = new Grid(); rail.Children.Add(new Border { Width = 2, Background = Res("Pastel"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(31, 8, 0, 8), Visibility = spec.A("righe").Any() ? Visibility.Visible : Visibility.Collapsed }); rail.Children.Add(line);
        body.Children.Add(rail);
        var pages = F.Pagination(spec.I("pagina"), spec.I("pagine"), async p => { tp = p; tab = "scheda"; await Guard(Load); }, "← Più recenti", "Meno recenti →"); if (pages != null) body.Children.Add(pages);
        return new Border { Style = Style("Card"), Child = body };
    }

    // ---------- Foto
    UIElement PhotosTab()
    {
        var f = d["foto"]; var spec = d["scheda"]; var today = f.S("oggi"); var write = f.Bo("scrivi");
        var body = Col(T("Foto", "H2"), T($"Il lavoro fatto, giorno per giorno: al massimo {f.I("max")} foto per giorno, rimpicciolite da sole. Restano in questo programma, non vanno sul sito.", size: 13, color: Res("Muted")));
        var first = true;
        foreach (var g in f.A("giorni"))
        {
            var date = DateOnly.Parse(g.S("data")); var isToday = g.S("data") == today; var name = date.ToString("dddd d MMMM yyyy", It);
            var h3 = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }; h3.Inlines.Add(isToday ? "Oggi" : char.ToUpper(name[0]) + name[1..]);
            if (g.S("etichetta") != "") { var r = new Run(" · " + g.S("etichetta")) { FontWeight = FontWeights.Normal }; r.SetResourceReference(TextElement.ForegroundProperty, "Muted"); h3.Inlines.Add(r); }
            var day = Col(h3);
            if (g.S("prenotazione") != "" || g.Bo("voce"))
            {
                var links = Row(); var bid = g.S("prenotazione");
                if (g.Bo("voce") && spec != null) links.Children.Add(F.Link($"Vedi il {spec.S("uno")} di quel giorno", () => Go("scheda"), 13));
                if (bid != "") { if (links.Children.Count > 0) links.Children.Add(T(" · ", size: 13)); links.Children.Add(F.Link("Apri l'appuntamento", () => Main.ShowPage(new BookingDetailPage(bid)), 13)); }
                day.Children.Add(links);
            }
            day.Children.Add(PhotoGrid.Make(g.A("foto").Select(x => x.ToString()), false, write, isToday ? new JsonObject { ["cliente"] = id } : null, g.I("liberi"), f.I("max"), async () => { tab = "foto"; await Load(); }));
            body.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, first ? 0 : 2, 0, 0), Padding = new Thickness(0, 14, 0, 14), Child = day }); first = false;
        }
        if (!f.A("giorni").Any()) body.Children.Add(T(write ? "Nessuna foto ancora: si aggiungono qui per oggi o dall'appuntamento." : "Nessuna foto.", size: 14, color: Res("Muted")).M(0, 10, 0, 0));
        return new Border { Style = Style("Card"), Child = body };
    }

    // ---------- Consensi e privacy
    UIElement PrivacyTab()
    {
        var spec = d["scheda"]; var write = Can("booking.write"); var raw = d["grezzi"];
        var events = Col();
        foreach (var e in d.A("eventi")) events.Children.Add(Col(T(e.S("tipo"), size: 14, bold: true), T($"{Local(e.S("quando")):dd/MM/yyyy HH:mm} · {e.S("canale")}" + (e.S("testo") != "" ? " · testo informativa " + e.S("testo") : ""), size: 13, color: Res("Muted"))).M(0, 6, 0, 6));
        if (!d.A("eventi").Any()) events.Children.Add(T("Mai richiesto", size: 14, color: Res("Muted")));
        var pill = F.Status(d.S("consenso")); pill.HorizontalAlignment = HorizontalAlignment.Left; pill.Margin = new Thickness(0, 4, 0, 8);
        var consent = new Border { Style = Style("Card"), Child = Col(T("Consenso alle comunicazioni", "H2"), pill, events) };
        var mails = Col(T("Email delle prenotazioni", "H2"), T("Conferma, promemoria, modifiche e annullamento delle sue prenotazioni." + (raw.S("email") == "" && Can("pii.read") ? " Ora non ha un'email: partono quando gliela aggiungi." : ""), size: 14, color: Res("Muted")));
        if (write)
        {
            var sw = new CheckBox { Content = d.Bo("avvisi") ? "Le riceve" : "Non le riceve", IsChecked = d.Bo("avvisi"), Margin = new Thickness(0, 10, 0, 0) };
            sw.Click += async (_, _) => await Guard(() => Act(new JsonObject { ["azione"] = "notices", ["notices"] = sw.IsChecked == true }, "privacy"));
            mails.Children.Add(sw);
        }
        else { var p = MainWindow.Pill(d.Bo("avvisi") ? "Le riceve" : "Non le riceve", d.Bo("avvisi") ? "ok" : "warn"); p.HorizontalAlignment = HorizontalAlignment.Left; p.Margin = new Thickness(0, 10, 0, 0); mails.Children.Add(p); }
        if (d.Bo("testi"))
        {
            var ch = new ComboBox { ItemsSource = d.A("canali").Select(k => new ComboBoxItem { Content = k.S("nome"), Tag = k.S("id") }).ToList(), IsEnabled = write };
            ch.SelectedItem = ch.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == d.S("canale"));
            mails.Children.Add(F.Field("Promemoria preferiti", ch, null, "Con WhatsApp, se il numero non lo accetta parte un SMS"));
            if (write) mails.Children.Add(Save("Salva", () => new JsonObject { ["azione"] = "channel", ["channel"] = (string)((ComboBoxItem)ch.SelectedItem)?.Tag ?? "" }, open: "privacy"));
        }
        var cards = new List<FrameworkElement> { consent, new Border { Style = Style("Card"), Child = mails } };
        if (Can("privacy.manage"))
        {
            var download = Wide(BA("Scarica i dati del cliente", async () =>
            {
                var r = await Api.Current.Call("cliente.esporta", new { id });
                Downloads.Save(r.S("nome"), System.Text.Encoding.UTF8.GetBytes(r.S("testo")));
            }));
            var confirm = Box("");
            var erase = BA("Cancella i dati", async () =>
            {
                var r = await Api.Current.Call("cliente.cancella", new { id, conferma = confirm.Text });
                Main.Flash(r.S("testo")); Main.Show("clienti");
            }, "DangerButton"); erase.HorizontalAlignment = HorizontalAlignment.Left; erase.Margin = new Thickness(0, 12, 0, 0);
            var h3 = T("Cancellazione", size: 15, bold: true).M(0, 18, 0, 6);
            cards.Add(new Border { Style = Style("Card"), Child = Col(T("Richieste privacy", "H2"), T($"Accesso: scarica in un file tutto ciò che conserviamo su questo cliente{(spec != null ? ", scheda compresa" : "")}.", size: 14), download, h3,
                T($"Rimuove nomi, recapiti, note{(spec != null ? ", la scheda" : "")} e i collegamenti da tutte le prenotazioni. Lo storico del consenso resta, ma non porta più a una persona. Non si può annullare.", size: 14, color: Res("Muted")),
                F.Label("Scrivi CANCELLA per confermare"), confirm, erase) });
        }
        return F.Cols(2, 14, cards.ToArray());
    }
}
