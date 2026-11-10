using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/setup.html: Configura l'attività: type of business and locations, the steps with their state,
/// Logo e aspetto with its preview, intolerances, the usual questions, Accessi sito ed email, Al banco, the booking
/// page with a link for each channel, the QR and the code for a site.</summary>
public sealed class SetupPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("configura", new { }));
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static TextBox Code(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 13, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    static ComboBox Pick(IEnumerable<(string Id, string Label)> items, string selected)
    {
        var c = new ComboBox { ItemsSource = items.Select(i => new ComboBoxItem { Content = i.Label, Tag = i.Id }).ToList() };
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? c.Items[0]; return c;
    }
    static string Val(ComboBox c) => (string)(c.SelectedItem as ComboBoxItem)?.Tag ?? "";
    async Task Done(JsonNode r) { Main.Flash(r.S("testo")); await Main.Refresh("configura"); }
    void Catalog(string kind) => Main.ShowPage(new CatalogPage { Kind = kind });

    /// <summary>site.clean_brand: the logo keeps its shape: the empty transparent margins cut, 800 x 300 at most, a PNG.</summary>
    public static byte[] CleanLogo(string path)
    {
        var data = File.ReadAllBytes(path); if (data.Length > 1024 * 1024) throw new ApiError("Logo massimo 1 MB.");
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > 8000000) throw new NotSupportedException();
            var src = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); int w = src.PixelWidth, h = src.PixelHeight; var px = new byte[w * h * 4]; src.CopyPixels(px, w * 4, 0);
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (px[(y * w + x) * 4 + 3] != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            BitmapSource cut = x1 >= 0 ? new CroppedBitmap(src, new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1)) : src;
            var scale = Math.Min(1, Math.Min(800.0 / cut.PixelWidth, 300.0 / cut.PixelHeight));
            if (scale < 1) cut = new TransformedBitmap(cut, new ScaleTransform(scale, scale));
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(cut)); using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or System.Runtime.InteropServices.COMException) { throw new ApiError("Usa un'immagine PNG, JPEG o WebP valida."); }
    }
    static ImageSource Picture(byte[] png) { var bi = new BitmapImage(); bi.BeginInit(); bi.StreamSource = new MemoryStream(png); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); return bi; }
    static Brush Hex(string c) { try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); } catch (FormatException) { return Res("Brand"); } }

    void Draw(JsonNode d)
    {
        var name = d.S("nome"); var profile = d["profilo"]; var type = profile.S("tipo"); var count = profile.I("quante");
        var guide = B("Come si fa", () => Main.Show("guida"));
        var page = Col(F.Heading(null, "Configura l'attività", $"I passi in ordine per ricevere prenotazioni. {d.I("fatti")} su {d.I("totale")} sono a posto", guide));

        // ----- Tipo di attività e sedi
        {
            var places = profile.A("sedi").Select(s => s.ToString()).ToList();
            var head = F.Section("Tipo di attività e sedi", null, type != "" ? F.Pill(profile.S("nome")) : null);
            var body = Col(head, Muted($"{(type != "" ? profile.S("nome") : "Tipo non ancora scelto")}, {count} {(count == 1 ? "sede" : "sedi")}{(places.Count > 0 ? ": " + string.Join(", ", places) : "")}. {profile.I("servizi")} servizi attivi"));
            if (d.Bo("puoProfilo"))
            {
                var chosen = type; var tiles = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var names = d.A("nomi").Select(n => n.ToString()).ToList(); var boxes = new List<TextBox>(); var namesPanel = new Grid();
                var confirm = new CheckBox { VerticalAlignment = VerticalAlignment.Top };
                var warnText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(8, 0, 0, 0) };
                warnText.Inlines.Add(new System.Windows.Documents.Run("Cambio il tipo di attività.") { FontWeight = FontWeights.SemiBold });
                warnText.Inlines.Add($" Servizi, operatori e orari di {name} vengono sostituiti da quelli del tipo nuovo, pronti da ritoccare. Le prenotazioni restano tutte: quello che usano viene solo disattivato");
                var wrow = new DockPanel(); DockPanel.SetDock(confirm, Dock.Left); wrow.Children.Add(confirm); wrow.Children.Add(warnText); var warn = F.Notice(wrow, "warning"); warn.Visibility = Visibility.Collapsed;
                var same = Muted("Con lo stesso tipo servizi, operatori e orari restano come li hai impostati: cambiano solo le sedi, e una sede nuova parte con i servizi del tipo");
                var fresh = Muted("Servizi, orari e, dove servono, operatori, camere o campi vengono preparati per il tipo scelto, con valori pronti da ritoccare. La pagina prenotazioni e il modulo nel sito si aggiornano subito");
                void Chosen() { var changed = type != "" && chosen != type; warn.Visibility = changed ? Visibility.Visible : Visibility.Collapsed; same.Visibility = type != "" && !changed ? Visibility.Visible : Visibility.Collapsed; fresh.Visibility = type == "" || changed ? Visibility.Visible : Visibility.Collapsed; }
                foreach (var p in d.A("tipi"))
                {
                    var small = Muted(p.S("aiuto"), 12.5); var tile = new RadioButton { GroupName = "tipo", Style = Style("PayChoice"), Width = 232, Margin = new Thickness(0, 0, 10, 10), IsChecked = p.S("id") == type, Padding = new Thickness(14, 12, 14, 12),
                        Content = Col(T(p.S("nome"), size: 14, bold: true), small.M(0, 3, 0, 0)), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top };
                    tile.Checked += (_, _) => { chosen = p.S("id"); Chosen(); }; tiles.Children.Add(tile);
                }
                // Quante sedi?
                var n = Math.Max(1, count); var custom = Box(count > 5 ? count.ToString() : "6"); custom.Width = 70; var customRow = Row(T("Quante", size: 14).M(12, 0, 8, 0), custom); customRow.Visibility = count > 5 ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)customRow.Children[0]).VerticalAlignment = VerticalAlignment.Center;
                void Names()
                {
                    for (int i = 0; i < boxes.Count && i < names.Count; i++) names[i] = boxes[i].Text;
                    while (names.Count < n) names.Add(names.Count < 5 ? new[] { "Sede principale", "Seconda sede", "Terza sede", "Quarta sede", "Quinta sede" }[names.Count] : $"Sede {names.Count + 1}");
                    boxes.Clear(); var fields = new List<FrameworkElement>();
                    for (int i = 0; i < n; i++) { var b = Box(names[i]); b.MaxLength = 100; boxes.Add(b); fields.Add(F.Field($"Nome della sede {i + 1}", b)); }
                    namesPanel.Children.Clear(); namesPanel.Children.Add(F.Grid2(fields.ToArray()));
                }
                var pills = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 1; i <= 5; i++)
                {
                    var k = i; var r = new RadioButton { GroupName = "sedi", Style = Style("Segment"), Content = i == 1 ? "Una sede" : i.ToString(), IsChecked = count <= 5 && i == n };
                    r.Checked += (_, _) => { n = k; customRow.Visibility = Visibility.Collapsed; Names(); }; pills.Children.Add(r);
                }
                var more = new RadioButton { GroupName = "sedi", Style = Style("Segment"), Content = "Di più", IsChecked = count > 5 };
                void Custom() { n = int.TryParse(custom.Text, out var v) ? Math.Clamp(v, 6, d.I("massimoSedi")) : 6; Names(); }
                more.Checked += (_, _) => { customRow.Visibility = Visibility.Visible; Custom(); }; custom.LostFocus += (_, _) => { if (more.IsChecked == true) Custom(); }; pills.Children.Add(more);
                var pillBox = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(4), Background = Res("Surface"), Child = pills, HorizontalAlignment = HorizontalAlignment.Left };
                Names(); Chosen();
                var apply = new Button { Content = type != "" ? "Salva" : "Applica la configurazione", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                apply.Click += async (_, _) =>
                {
                    if (chosen == "") { Main.Flash("Scegli un tipo di attività.", true); return; }
                    if (more.IsChecked == true) Custom();
                    apply.IsEnabled = false;
                    try { await Guard(async () => await Done(await Api.Current.Call("configura.profilo", new JsonObject { ["business_type"] = chosen, ["locations"] = n, ["names"] = new JsonArray(boxes.Select(b => (JsonNode)b.Text).ToArray()), ["confirm_type"] = confirm.IsChecked == true }))); }
                    finally { apply.IsEnabled = true; }
                };
                body.Children.Add(F.Legend("Che attività è?").M(0, 14, 0, 0)); body.Children.Add(tiles);
                body.Children.Add(F.Legend("Quante sedi?").M(0, 8, 0, 6)); body.Children.Add(Row(pillBox, customRow)); body.Children.Add(namesPanel);
                body.Children.Add(warn); body.Children.Add(same.M(0, 12, 0, 0)); body.Children.Add(fresh.M(0, 12, 0, 0)); body.Children.Add(apply);
            }
            else body.Children.Add(Muted($"Il tipo di attività e le sedi li imposta l'amministratore del programma: se devi cambiarli, chiedi all'amministratore del programma. Qui tieni aggiornati servizi e prezzi, orari, chiusure e {d.S("molti").ToLower()}").M(0, 10, 0, 0));
            page.Children.Add(F.Card(body));
        }

        // ----- the steps
        var stepNo = 0;
        foreach (var s in d.A("passi"))
        {
            stepNo++; var done = s.Bo("fatto");
            var state = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Res(done ? "OkSoft" : "BrandSoft"), VerticalAlignment = VerticalAlignment.Top, Child = T(done ? "✓" : stepNo.ToString(), size: 16, bold: true, color: Res(done ? "Ok" : "Brand")) };
            ((TextBlock)state.Child).HorizontalAlignment = HorizontalAlignment.Center; ((TextBlock)state.Child).VerticalAlignment = VerticalAlignment.Center;
            var go = F.Small(B(s.I("quanti") > 0 ? "Gestisci" : "Aggiungi", () => Catalog(s.S("chiave")), done ? null : "Primary"));
            var h = T(s.S("titolo"), "H2"); h.Margin = new Thickness(0);
            var body = Col(F.Section(s.S("titolo"), null, go), T(s.S("testo"), size: 14).M(0, 4, 0, 2), Muted(s.S("aiuto")));
            ((TextBlock)body.Children[1]).TextWrapping = TextWrapping.Wrap;
            if (s["altro"] is JsonNode extra) body.Children.Add(F.Link(extra.S("testo"), () => Catalog(extra.S("chiave"))).M(0, 6, 0, 0));
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(body, 2); g.Children.Add(state); g.Children.Add(body);
            page.Children.Add(new Border { Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 20, 22, 20), Margin = new Thickness(0, 0, 0, 12), Child = g });
        }

        // ----- Logo e aspetto
        if (d.Bo("scrive"))
        {
            byte[] logo = null; var has = d.Bo("haLogo"); var current = has ? F.Logo(Business, 44)?.Source : null;
            var color = Box(d.S("colore")); color.MaxLength = 7; color.Tag = "#7a55ad"; AgendaPage.Hint(color); color.Width = 110;
            var tagline = Box(d.S("frase")); tagline.MaxLength = 180; tagline.Tag = "Per esempio: parrucchiere in centro dal 1998"; AgendaPage.Hint(tagline);
            var site = Box(d.S("sito")); site.MaxLength = 200; site.Tag = "https://"; AgendaPage.Hint(site); var thanks = Box(d.S("grazie")); thanks.MaxLength = 500; thanks.Tag = "https://"; AgendaPage.Hint(thanks);
            var remove = new CheckBox { Content = T("Togli il logo", size: 14), Margin = new Thickness(0, 8, 0, 0) };
            // the marks of the preview: the logo, or the first letter on the colour
            var marks = new List<Border>(); var chip = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 0) };
            var slot = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(3, 0, 3, 0), Child = T("10:00", size: 12.5, bold: true, color: Brushes.White) };
            var mockButton = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(16, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Child = T("Prenota", size: 13, bold: true, color: Brushes.White) };
            var tagOut = Muted(d.S("frase"), 13);
            Border Mark() { var m = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(12), ClipToBounds = true }; marks.Add(m); return m; }
            void Paint()
            {
                var brush = Hex(color.Text.Trim()); chip.Background = brush; slot.Background = brush; mockButton.Background = brush; tagOut.Text = tagline.Text;
                var source = remove.IsChecked == true ? null : logo != null ? Picture(logo) : current;
                foreach (var m in marks)
                {
                    if (source != null) { m.Background = Brushes.Transparent; m.Child = new Image { Source = source, Stretch = Stretch.Uniform }; }
                    else { m.Background = brush; var l = T(name.Length > 0 ? name[..1].ToUpper() : "", size: 20, bold: true, color: Brushes.White); l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center; m.Child = l; }
                }
            }
            var pickText = T(has ? "Cambia il logo" : "Carica il logo", size: 14, bold: true);
            var drop = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Cursor = System.Windows.Input.Cursors.Hand, Background = Res("Surface"),
                Child = Row(Mark(), Col(pickText, Muted("PNG trasparente, JPEG o WebP, fino a 1 MB. Va bene anche orizzontale: i margini vuoti si tolgono da soli", 13)).M(12, 0, 0, 0)) };
            ((StackPanel)((StackPanel)drop.Child).Children[1]).MaxWidth = 380;
            drop.MouseLeftButtonUp += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Immagini (PNG, JPEG, WebP)|*.png;*.jpg;*.jpeg;*.webp" };
                if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
                try { logo = CleanLogo(dlg.FileName); remove.IsChecked = false; pickText.Text = "Cambia il logo"; Paint(); } catch (ApiError ex) { Main.Flash(ex.Message, true); }
            };
            var swatches = new WrapPanel();
            foreach (var c in d.A("campioni"))
            {
                var code = c.ToString(); var b = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Hex(code), Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = code };
                b.MouseLeftButtonUp += (_, _) => { color.Text = code; Paint(); }; swatches.Children.Add(b);
            }
            color.TextChanged += (_, _) => Paint(); tagline.TextChanged += (_, _) => Paint(); remove.Checked += (_, _) => Paint(); remove.Unchecked += (_, _) => Paint();
            var save = new Button { Content = "Salva logo e aspetto", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
            save.Click += async (_, _) =>
            {
                save.IsEnabled = false;
                try { await Guard(async () => await Done(await Api.Current.Call("configura.aspetto", new { brand_color = color.Text.Trim(), tagline = tagline.Text, website = site.Text, thanks_url = thanks.Text, logo = logo != null ? Convert.ToBase64String(logo) : "", logo_remove = remove.IsChecked == true }))); }
                finally { save.IsEnabled = true; }
            };
            var form = Col(F.Label("Logo"), drop, has ? remove : null, F.Label("Colore"), Row(chip, color), Muted("Un colore pronto, o il codice del tuo (sei cifre dopo #)", 13).M(0, 4, 0, 8), swatches,
                F.Field("Frase sotto il nome", tagline, null, "Compare nella pagina prenotazioni, sotto il nome"), F.Field("Il vostro sito", site),
                F.Field("Pagina di ringraziamento", thanks, null, "Facoltativa: dopo la prenotazione online il cliente va lì, per esempio la pagina Grazie del vostro sito. Un servizio può averne una sua"), save);
            // the preview: in the menu, in the booking page
            var side = new Border { Background = Res("Closed"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = Row(Mark(), Col(T(name, size: 14, bold: true, color: Res("Pastel")), T(Business.S("ruoloNome") != "" ? Business.S("ruoloNome") : "Il vostro portale", size: 12.5, color: Res("SideMuted"))).M(10, 0, 0, 0)) };
            var slots = Row(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 5, 12, 5), Child = T("09:30", size: 12.5) }, slot,
                new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 5, 12, 5), Child = T("10:30", size: 12.5) });
            var mock = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Background = Res("Surface"), Child = Col(Row(Mark(), T(name, size: 15, bold: true).M(10, 0, 0, 0)), tagOut.M(0, 8, 0, 8), slots, mockButton) };
            ((TextBlock)((StackPanel)((StackPanel)mock.Child).Children[0]).Children[1]).VerticalAlignment = VerticalAlignment.Center;
            var preview = Col(Muted("Nel menu", 13), side.M(0, 6, 0, 16), Muted("Nella pagina prenotazioni", 13), mock.M(0, 6, 0, 0));
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            Grid.SetColumn(preview, 2); grid.Children.Add(form); grid.Children.Add(preview); Paint();
            page.Children.Add(F.Card(T("Logo e aspetto", "H2"), Muted("Il logo va in alto nel menu, nel portale e nella pagina prenotazioni; il colore nei pulsanti e nei dettagli della pagina prenotazioni"), grid.M(0, 10, 0, 0)));
        }

        // ----- Intolleranze e allergie
        if (d.Bo("allergie"))
        {
            var on = d.Bo("allergieAccese"); var list = d.A("allergeni").Select(a => a.ToString()).ToList();
            var text = on && list.Count > 0 ? $"Il modulo di prenotazione chiede {list.Count} voci: {string.Join(", ", list.Take(5))}{(list.Count > 5 ? " e altre" : "")}" : "Spento: il modulo di prenotazione non le chiede";
            page.Children.Add(F.Card(F.Section("Intolleranze e allergie", null, F.Small(B(on ? "Gestisci" : "Attiva", () => Main.ShowPage(new AllergensPage()), on ? null : "Primary"))), Muted(text)));
        }

        // ----- Domande consigliate
        if (d["consigliate"] is JsonArray rec && rec.Count > 0)
        {
            var checks = rec.Select(q => new CheckBox { IsChecked = true, Tag = q.S("chiave"), Margin = new Thickness(0, 8, 0, 0), Content = Col(T(q.S("testo"), size: 14), Muted(q.S("tipo"), 12.5)) }).ToList();
            var add = BA("Aggiungi ai servizi", async () => await Done(await Api.Current.Call("configura.domande", new JsonObject { ["q"] = new JsonArray(checks.Where(c => c.IsChecked == true).Select(c => (JsonNode)(string)c.Tag).ToArray()) }))); add.HorizontalAlignment = HorizontalAlignment.Left;
            var body = Col(T($"Domande consigliate per {profile.S("nome")}", "H2"), Muted("Quello che chiede di solito chi fa questo lavoro. Si aggiungono ai servizi attivi che non le hanno ancora; poi le cambi o le togli in ogni servizio, alla voce Richieste aggiuntive"));
            foreach (var c in checks) body.Children.Add(c);
            body.Children.Add(add.M(0, 14, 0, 0)); page.Children.Add(F.Card(body));
        }

        // ----- Accessi, sito ed email
        var agency = d.A("agenzia").ToList();
        if (agency.Count > 0)
        {
            var list = Col();
            for (int i = 0; i < agency.Count; i++) { var a = agency[i]; list.Children.Add(CleanList.Row(i == agency.Count - 1, (a.Bo("fatto") ? "✓ " : "") + a.S("titolo"), a.S("testo"), a.S("pagina") != "" ? B("Apri", () => Main.Show(a.S("pagina"))) : null)); }
            page.Children.Add(F.Card(T("Accessi, sito ed email", "H2"), list));
        }

        // ----- Al banco
        if (d["banco"] is JsonNode desk)
        {
            var roles = desk["ruoli"];
            var force = new CheckBox { IsChecked = desk.Bo("forza"), Content = T($"Anche {roles.S("reception")} e {roles.S("operator")} possono forzare", size: 14), Margin = new Thickness(0, 12, 0, 0) };
            var till = new CheckBox { IsChecked = desk.Bo("operatori"), Content = T($"Anche {roles.S("operator")} chiude il conto alla Cassa", size: 14), Margin = new Thickness(0, 10, 0, 0) };
            var absences = Pick(Enumerable.Range(0, 6).Select(n => (n.ToString(), n > 0 ? $"Dopo {n} assenz{(n == 1 ? "a" : "e")}, le sue prenotazioni online aspettano il vostro sì" : "Si prenotano online come tutti")), desk.S("assenze"));
            var mode = Pick(new[] { ("all", "Avvisa tutti: lo prende chi prenota per primo"), ("queue", "Uno alla volta, in ordine: il posto resta tenuto per lui") }, desk.S("attesa"));
            var minutes = Pick(desk.A("minutiScelte").Select(m => { var v = m.GetValue<int>(); return (v.ToString(), v < 60 ? $"{v} minuti" : v == 60 ? "1 ora" : $"{v / 60} ore"); }), desk.S("minuti"));
            var wa = Pick(desk.A("whatsappScelte").Select(w => (w.S("id"), w.S("nome"))), desk.S("whatsapp"));
            var cats = Box(desk.S("categorie"), true); cats.MaxLength = 2000; cats.MinHeight = 70; cats.Tag = "Socio, 14, 3\nOspite, 2, 1"; AgendaPage.Hint(cats);
            var intro = Muted($"Al banco preavviso, prenotabile fino a, ultimo arrivo e persone massime valgono solo per la pagina online. Con Forza si prenota anche fuori orario, oltre i posti, sopra un altro appuntamento o nel passato: il programma dice cosa si supera e lo scrive nel registro. Possono forzare {roles.S("owner")} e {roles.S("manager")}");
            var save = BA("Salva", async () => await Done(await Api.Current.Call("configura.banco", new { force_all = force.IsChecked == true, till_operators = till.IsChecked == true, absences_request = Val(absences), wait_mode = Val(mode), wait_minutes = Val(minutes), wa_when = Val(wa), client_categories = cats.Text })));
            save.HorizontalAlignment = HorizontalAlignment.Left;
            page.Children.Add(F.Card(T("Al banco", "H2"), intro, force, desk.Bo("cassa") ? till : null,
                F.Grid2(F.Field("Clienti che non si presentano", absences, null, "Le richieste arrivano in Richieste da confermare. Per fermare del tutto un cliente: Non prenotabile online nella sua scheda"), F.Field("Lista d'attesa, quando si libera un posto", mode),
                    F.Field("Per quanto lo teniamo, uno alla volta", minutes, null, "Poi passa al prossimo della lista"),
                    F.Field("Promemoria WhatsApp", wa, null, "Quando c'è qualcuno da avvisare, in cima a ogni pagina compare l'avviso: un clic per cliente apre WhatsApp con il messaggio pronto. Il testo si cambia in Configurazione promemoria")),
                F.Field("Categorie di clienti", cats, "(facoltative)", "Una per riga: nome, giorni di anticipo con cui prenota online, prenotazioni in programma al massimo (0 vuol dire come il servizio, nessun limite). La categoria si sceglie nella scheda del cliente; al banco non conta"),
                save.M(0, 14, 0, 0)));
        }

        // ----- La pagina prenotazioni
        if (d.S("pagina") != "")
        {
            var body = Col(T("La pagina prenotazioni", "H2"), T("Quando i passi sono fatti, prova a prenotare come farebbe un cliente", size: 14).M(0, 0, 0, 10));
            var open = B("Apri la pagina prenotazioni", () => WaitRows.Web(d.S("pagina"))); open.HorizontalAlignment = HorizontalAlignment.Left; body.Children.Add(open);
            body.Children.Add(T("Un link per ogni canale", size: 16, bold: true).M(0, 18, 0, 0));
            body.Children.Add(Muted("Usa questi link al posto di quello semplice: ogni prenotazione ricorda da dove è arrivata, e lo vedi nella prenotazione e nel file delle prenotazioni"));
            void Copy(string text) { Clipboard.SetText(text); Main.Flash("Copiato."); }
            foreach (var k in d.A("canali"))
            {
                var copy = F.Small(B("Copia", () => Copy(k.S("link")))); DockPanel.SetDock(copy, Dock.Right); var label = T(k.S("nome"), size: 14, bold: true); label.Width = 220; label.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(label, Dock.Left);
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; row.Children.Add(label); row.Children.Add(copy); row.Children.Add(Code(k.S("link"))); body.Children.Add(row);
            }
            var qr = new Image { Width = 140, Height = 140, Margin = new Thickness(0, 0, 16, 0) }; byte[] qrData = null;
            _ = Guard(async () => { qrData = await Api.Current.Bytes("configura.qr", new { }); qr.Source = Picture(qrData); });
            var download = F.Small(B("Scarica il QR", () => { if (qrData != null) Downloads.Save($"prenota-{Business.S("slug")}.png", qrData); })); download.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(Row(qr, Col(T("QR da stampare", size: 14, bold: true), Muted("Per il banco, i volantini o il menu: chi lo inquadra arriva alla pagina prenotazioni").M(0, 4, 0, 10), download)).M(0, 18, 0, 0));
            ((StackPanel)((StackPanel)body.Children[^1]).Children[1]).VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(T("Nel sito, senza WordPress", size: 16, bold: true).M(0, 18, 0, 0));
            var sites = d.A("siti").Select(s => s.ToString()).ToList();
            body.Children.Add(Muted("Incolla questo codice nella pagina del sito: il modulo si allunga da solo quanto serve. Prima l'amministratore del programma deve autorizzare l'indirizzo del sito" + (sites.Count > 0 ? $" (ora: {string.Join(", ", sites)})" : "")));
            body.Children.Add(new Border { Background = Res("Bg"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 8), Child = Code(d.S("incorpora")) });
            var copyCode = F.Small(B("Copia il codice", () => Copy(d.S("incorpora")))); copyCode.HorizontalAlignment = HorizontalAlignment.Left; body.Children.Add(copyCode);
            var card = F.Card(body); card.Background = Res("BrandSoft"); page.Children.Add(card);
        }
        Content = F.Page(1180, page);
    }
}
