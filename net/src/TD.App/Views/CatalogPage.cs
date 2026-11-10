using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/catalog.html: the lists of Configura l'attività (sedi, orari, chiusure, servizi, risorse, varianti,
/// turni, date, prodotti): the tabs, Aggiungi, Modifica, Rimuovi / Elimina, the ready services and the holidays.</summary>
public sealed class CatalogPage : Page
{
    public string Kind = "services";
    int page = 1;
    public override async Task Load() => Draw(await Api.Current.Call("catalogo", new { tipo = Kind, pagina = page }));
    void New(string preset = "") => Main.ShowPage(new CatalogFormPage { Kind = Kind, Preset = preset });
    static TextBlock Muted(string text) { var t = T(text, size: 14, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }

    async Task Remove(string id, bool series = false, bool confirm = false, bool conflicts = false)
    {
        JsonNode r;
        try { r = await Api.Current.Call("catalogo.elimina", new { tipo = Kind, id, serie = series, conferma = confirm, confirm_conflicts = conflicts }); }
        catch (ApiError ex) when (ex is not NeedsLogin and not NeedsConfirm) { Main.Flash(ex.Message, true); return; }
        if (r.Bo("controlla")) { Main.ShowPage(new RemoveCheckPage { Data = r, Go = () => Remove(id, series, true), Back = () => Main.ShowPage(new CatalogPage { Kind = Kind }) }); return; }
        Main.Flash(r.S("testo")); Main.ShowPage(new CatalogPage { Kind = Kind });
    }

    void Draw(JsonNode d)
    {
        var actions = new List<FrameworkElement>();
        if (Kind == "services") actions.Add(B("Importa da Excel", () => Main.ShowPage(new ServicesImportPage())));
        actions.Add(B("Aggiungi", () => New(), "Primary"));
        var body = Col(F.Back("Configura l'attività", () => Main.Show("configura")), F.Heading(null, d.S("titolo"), d.S("aiuto"), actions.ToArray()));
        // the tabs
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var k in d.A("schede"))
        {
            var key = k.S("chiave"); var tab = new RadioButton { Style = Style("Segment"), Content = k.S("titolo"), IsChecked = key == Kind, GroupName = "catalogo" };
            tab.Checked += (_, _) => { if (key != Kind) Main.ShowPage(new CatalogPage { Kind = key }); }; tabs.Children.Add(tab);
        }
        body.Children.Add(tabs);
        if (Kind == "services" && d.Bo("profilo"))
        {
            var go = B("Tipo di attività e sedi", () => Main.Show("configura")); DockPanel.SetDock(go, Dock.Right); go.VerticalAlignment = VerticalAlignment.Top;
            var top = new DockPanel(); top.Children.Add(go);
            top.Children.Add(Col(T("Servizi già pronti per il tipo di attività", "H2"), Muted((d.S("tipoNome") != "" ? $"Ora: {d.S("tipoNome")}. " : "") + "In Configura l'attività scegli tipo e sedi e premi Applica: i servizi giusti si creano da soli in ogni sede. Qui li ritocchi uno per uno")));
            var card = Col(top);
            if (d["modelli"] is JsonArray models && models.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var m in models) { var b = F.Small(B(m.S("nome"), () => New(m.S("id")))); b.ToolTip = m.S("aiuto"); b.Margin = new Thickness(0, 0, 8, 8); wrap.Children.Add(b); }
                card.Children.Add(F.Details("Aggiungi un solo servizio da un modello", wrap, false, true).M(0, 10, 0, 0));
            }
            var box = F.Card(card); box.Background = Res("BrandSoft"); body.Children.Add(box);
        }
        if (Kind == "exceptions")
        {
            var go = B("Segna le festività", () => Main.ShowPage(new HolidaysPage())); DockPanel.SetDock(go, Dock.Right); go.VerticalAlignment = VerticalAlignment.Top;
            var top = new DockPanel(); top.Children.Add(go);
            top.Children.Add(Col(T("Festività italiane", "H2"), Muted("Capodanno, Pasqua e Pasquetta, 25 aprile, Ferragosto, Natale e le altre, più il santo patrono: chiuse in un clic per l'anno che scegli. Quelle in cui lavori le lasci senza spunta")));
            var box = F.Card(top); box.Background = Res("BrandSoft"); body.Children.Add(box);
        }
        // the rows
        var list = Col(); var rows = d.A("righe").ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var id = r.S("id"); var label = r.S("etichetta");
            var text = Col(T(label, size: 14, bold: true)); ((TextBlock)text.Children[0]).TextWrapping = TextWrapping.Wrap;
            foreach (var s in r.A("righe")) text.Children.Add(Muted(s.ToString()).Also13());
            if (r.S("spento") != "") text.Children.Add(MainWindow.Pill(r.S("spento"), "warn").M(0, 4, 0, 0).Left());
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(acts, Dock.Right);
            acts.Children.Add(F.Link("Modifica", () => Main.ShowPage(new CatalogFormPage { Kind = Kind, Id = id })));
            if (r.S("togli") == "fascia") acts.Children.Add(F.Small(BA("Rimuovi", async () => { if (Ask("Rimuovere questa fascia?")) await Remove(id); }, "Ghost")).M(10, 0, 0, 0));
            else if (r.S("togli") == "elimina")
            {
                var del = F.Small(BA("Elimina", async () => { if (Ask($"Eliminare {label}?{(Kind == "locations" ? " Con la sede vanno anche i suoi servizi e le sue risorse." : "")} Se ci sono prenotazioni collegate viene solo disattivato e le prenotazioni restano.")) await Remove(id); }, "Ghost")); del.Foreground = Res("Danger"); acts.Children.Add(del.M(10, 0, 0, 0));
                if (Kind == "occurrences" && r.Bo("serie")) { var all = F.Small(BA("Elimina da qui in poi", async () => { if (Ask("Eliminare questa data e le successive della serie?")) await Remove(id, true); }, "Ghost")); all.Foreground = Res("Danger"); acts.Children.Add(all.M(10, 0, 0, 0)); }
            }
            var line = new DockPanel(); line.Children.Add(acts);
            if (Kind == "resources")
            {
                var photo = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Res("BrandSoft"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(photo, Dock.Left);
                if (r.S("foto") != "") try { photo.Background = new ImageBrush(SetupPage.PictureOf(Convert.FromBase64String(r.S("foto")))) { Stretch = Stretch.UniformToFill }; } catch (Exception ex) when (ex is FormatException or NotSupportedException) { }
                else { var l = T(label.Length > 0 ? label[..1].ToUpper() : "", size: 16, bold: true, color: Res("Brand")); l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center; photo.Child = l; }
                line.Children.Add(photo);
            }
            line.Children.Add(text);
            list.Children.Add(new Border { Child = line, Padding = new Thickness(0, 14, 0, 14), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1) });
        }
        if (rows.Count == 0) list.Children.Add(Muted("Ancora niente qui. Usa Aggiungi per creare il primo").M(0, 14, 0, 14));
        body.Children.Add(F.Card(list, F.Pagination(d.I("pagina"), d.I("pagine"), async p => { page = p; await Guard(Load); })));
        Content = F.Page(1180, body);
    }
}

static class CatalogBits
{
    public static TextBlock Also13(this TextBlock t) { t.FontSize = 13; return t; }
    public static T Left<T>(this T e) where T : FrameworkElement { e.HorizontalAlignment = HorizontalAlignment.Left; return e; }
}

/// <summary>templates/form.html for the catalog: Nuovo / Modifica, the fields, Salva, Torna all'elenco; when the change
/// leaves bookings to check, the tick that confirms it.</summary>
public sealed class CatalogFormPage : Page
{
    public string Kind = "services", Id = "", Preset = "", Resource = "";
    public override async Task Load() => Draw(await Api.Current.Call("catalogo.modulo", new { tipo = Kind, id = Id, preset = Preset, resource = Resource }));

    void Draw(JsonNode d)
    {
        var form = new FormView(d.A("campi")); form.Changed += form.Layout;
        var extra = d["extra"] != null ? new CatalogExtras(this, d["extra"], form) : null;
        var confirm = new CheckBox { Content = T("Ho verificato le prenotazioni segnalate e confermo la modifica", size: 14), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Salva", Style = Style("Primary") };
        void Back() => Main.ShowPage(new CatalogPage { Kind = Kind });
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var values = form.Values(); extra?.Collect(values);
                var r = await Api.Current.Call("catalogo.salva", new JsonObject { ["tipo"] = Kind, ["id"] = Id, ["preset"] = Preset, ["valori"] = values, ["confirm_conflicts"] = confirm.IsChecked == true });
                Main.Flash(r.S("testo")); Back();
            }
            catch (FormError ex) { form.Errors(ex.Errors.Where(e => extra?.Owns(e.Key) != true).ToDictionary(e => e.Key, e => e.Value)); extra?.Errors(ex.Errors); if (ex.Confirm) confirm.Visibility = Visibility.Visible; }
            catch (NeedsLogin) { Shell.Instance?.SignedOut(); }
            catch (ApiError ex) { Main.Flash(ex.Message, true); }
            finally { save.IsEnabled = true; }
        };
        var body = Col(form.Panel);
        if (extra != null) foreach (var e in extra.Sections) body.Children.Add(e);
        body.Children.Add(confirm);
        body.Children.Add(Row(save, F.Link("Torna a " + d.S("elenco"), Back).M(14, 0, 0, 0)).M(0, 18, 0, 0));
        ((TextBlock)((StackPanel)body.Children[^1]).Children[1]).VerticalAlignment = VerticalAlignment.Center;
        var page = Col(F.Heading(d.S("nome"), d.S("titolo"), d.S("descrizione")), F.Card(body));
        if (extra != null) foreach (var e in extra.After) page.Children.Add(e);
        Content = F.Page(1180, page);
    }
}

/// <summary>templates/remove_check.html: the bookings on what is being removed, to call back before.</summary>
public sealed class RemoveCheckPage : Page
{
    public JsonNode Data; public Func<Task> Go; public Action Back;
    public override Task Load()
    {
        var z = new AgendaPage { Main = Main }.Also(Business.S("fuso")); var list = Col(); var rows = Data.A("righe").ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            list.Children.Add(CleanList.Row(i == rows.Count - 1, r.S("nome") != "" ? r.S("nome") : "Senza nome", $"{z.Local(r.S("inizio")):dd/MM/yyyy HH:mm} · {r.S("servizio")}",
                r.S("telefono") != "" ? B("Chiama", () => WaitRows.Web("tel:" + r.S("telefono"))) : null, r.S("whatsapp") != "" ? B("WhatsApp", () => WaitRows.Web(r.S("whatsapp"))) : null,
                B("Prenotazione", () => Main.ShowPage(new BookingDetailPage(r.S("id"))), "Ghost")));
        }
        var go = BA(Data.S("pulsante"), Go, "DangerButton");
        Content = F.Page(1180, Col(F.Heading(null, Data.S("titolo"), Data.S("testo")), F.Card(list), Row(go, F.Link("Annulla", Back).M(14, 0, 0, 0))));
        return Task.CompletedTask;
    }
}

/// <summary>Pictures prepared by the window before they go to the program.</summary>
public static class Pictures
{
    /// <summary>branding.clean_photo: PNG, JPEG or WebP up to 5 MB, kept as a JPEG of at most 800 px on white, upright.</summary>
    public static byte[] Photo(string path)
    {
        var data = System.IO.File.ReadAllBytes(path); if (data.Length > 5 * 1024 * 1024) throw new ApiError("Foto massimo 5 MB.");
        try
        {
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new System.IO.MemoryStream(data), System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > 40000000) throw new NotSupportedException();
            System.Windows.Media.Imaging.BitmapSource src = frame;
            // the orientation the phone wrote (EXIF 274): 3 upside down, 6 and 8 on a side
            try
            {
                if (frame.Metadata is System.Windows.Media.Imaging.BitmapMetadata m && m.ContainsQuery("/app1/ifd/{ushort=274}") && m.GetQuery("/app1/ifd/{ushort=274}") is ushort o && o is 3 or 6 or 8)
                    src = new System.Windows.Media.Imaging.TransformedBitmap(frame, new RotateTransform(o == 3 ? 180 : o == 6 ? 90 : 270));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
            var scale = Math.Min(1, 800.0 / Math.Max(src.PixelWidth, src.PixelHeight)); int w = Math.Max(1, (int)Math.Round(src.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));
            var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h)); dc.DrawImage(src, new Rect(0, 0, w, h)); }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 }; enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp)); using var ms = new System.IO.MemoryStream(); enc.Save(ms); return ms.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.FileFormatException or System.IO.IOException or ArgumentException or System.Runtime.InteropServices.COMException) { throw new ApiError("Usa una foto PNG, JPEG o WebP valida, massimo 40 megapixel."); }
    }
}

/// <summary>What a form has besides its fields. A resource: what it does (a row a service, with its own duration, price
/// and posa) and its working days; a service: Richieste aggiuntive alla prenotazione, the questions of the booking form.</summary>
public sealed class CatalogExtras
{
    public List<FrameworkElement> Sections { get; } = new();
    public List<FrameworkElement> After { get; } = new();
    readonly List<Action<JsonObject>> collect = new();
    readonly TextBlock error = new() { FontSize = 13, Foreground = Res("Danger"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    readonly List<string> mine = new();
    static TextBlock Muted(string text) { var t = T(text, size: 14, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static TextBox Small(string value, string hint, double width = 74) { var b = Box(value); b.Width = width; b.Tag = hint; AgendaPage.Hint(b); return b; }

    public CatalogExtras(CatalogFormPage page, JsonNode extra, FormView form)
    {
        if (extra.Bo("risorsa")) Resource(page, extra); else if (extra.Bo("servizio")) Service(extra);
    }
    public bool Owns(string key) => mine.Any(m => key == m || m.EndsWith('_') && key.StartsWith(m));
    public void Collect(JsonObject values) { foreach (var c in collect) c(values); }
    /// <summary>The errors of the rows drawn here, which the list of fields does not know.</summary>
    public void Errors(Dictionary<string, string> errors)
    {
        var rest = errors.Where(e => Owns(e.Key)).Select(e => e.Value).ToList();
        error.Text = string.Join(" ", rest); error.Visibility = rest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>form.html, #google and #outlook: the operator's own calendar, both ways. Collega opens the consent page in
    /// the browser; coming back to the card shows it connected.</summary>
    void Calendars(CatalogFormPage page, JsonNode x)
    {
        async Task Act(string service, string action, string read = "", string write = "")
        {
            var r = await Api.Current.Call("risorsa.calendario", new { id = page.Id, servizio = service, azione = action, read, write });
            if (r.S("apri") != "") { WaitRows.Web(r.S("apri")); page.Main.Flash("Si è aperta la pagina del permesso nel browser: quando hai finito torna qui e riapri la scheda."); return; }
            page.Main.Flash(r.S("testo")); await page.Load();
        }
        foreach (var (key, title, who, button, where) in new[] { ("google", "Google Calendar", "Google", "Collega Google Calendar", "nel calendario scelto qui sotto"), ("outlook", "Outlook e Microsoft 365", "Outlook", "Collega Outlook", "nel suo calendario") })
        {
            if (x[key] is not JsonNode g) continue;
            var card = Col(T(title, "H2")); bool on = g.Bo("attivo");
            if (g.S("errore") != "") card.Children.Add(F.Notice(g.S("errore"), "error").M(0, 8, 0, 0));
            if (on)
            {
                var p = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; p.Inlines.Add("Collegato");
                if (g.S("account") != "") { p.Inlines.Add(" a "); p.Inlines.Add(new System.Windows.Documents.Run(g.S("account")) { FontWeight = FontWeights.Bold }); }
                p.Inlines.Add($": ogni 5 minuti i suoi impegni su {who} diventano orari occupati, senza titoli, e le sue prenotazioni vanno {where} con servizio e orario" + (x.Bo("nomiNelCalendario") ? " e il nome del cliente" : "") + "."
                    + (g.S("quando") != "" ? $" Ultimo allineamento il {g.S("quando")}." : ""));
                card.Children.Add(p);
            }
            else card.Children.Add(Muted(key == "google" ? "I suoi impegni su Google diventano orari occupati e le sue prenotazioni compaiono nel suo calendario Google, anche sul telefono. Si apre Google per dare il permesso"
                : "I suoi impegni su Outlook.com o Microsoft 365 diventano orari occupati e le sue prenotazioni compaiono nel suo calendario, anche sul telefono. Si apre Microsoft per dare il permesso").M(0, 8, 0, 0));
            var actions = Row();
            if (g.Bo("pronto") && (!on || g.S("errore") != "")) actions.Children.Add(BA(on ? "Collega di nuovo" : button, () => Act(key, "connect")));
            if (on) actions.Children.Add(BA("Scollega", () => Act(key, "disconnect"), "Ghost").M(actions.Children.Count > 0 ? 8 : 0, 0, 0, 0));
            card.Children.Add(actions.M(0, 12, 0, 0));
            var list = g.A("calendari").ToList();
            if (key == "google" && on && list.Count > 0)
            {
                ComboBox Pick(IEnumerable<JsonNode> items, string chosen)
                {
                    var box = new ComboBox(); box.Items.Add(new ComboBoxItem { Content = "Il calendario principale", Tag = "primary" });
                    foreach (var c in items) box.Items.Add(new ComboBoxItem { Content = c.S("nome"), Tag = c.S("id") });
                    box.SelectedIndex = Math.Max(0, box.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == chosen && i != box.Items[0])); return box;
                }
                var read = Pick(list, g.S("leggi")); var write = Pick(list.Where(c => c.Bo("scrivibile")), g.S("scrivi"));
                card.Children.Add(F.Grid2(F.Field("Impegni letti da", read), F.Field("Prenotazioni scritte in", write)).M(0, 14, 0, 0));
                card.Children.Add(Row(BA("Salva i calendari", () => Act("google", "calendars", (string)((ComboBoxItem)read.SelectedItem).Tag, (string)((ComboBoxItem)write.SelectedItem).Tag))).M(0, 10, 0, 0));
                card.Children.Add(Muted("Se su Google qualcuno sposta o cancella una prenotazione scritta da qui, la prenotazione non cambia: la trovate segnalata in agenda").Also13().M(0, 8, 0, 0));
            }
            After.Add(F.Card(card));
        }
    }

    void Resource(CatalogFormPage page, JsonNode x)
    {
        Calendars(page, x);
        mine.AddRange(new[] { "services", "dur_", "price_", "pa_", "pm_" });
        // ----- Cosa fa
        var appointments = x.Bo("appuntamenti"); var body = Col(F.Legend(x.S("etichettaServizi")).M(0, 18, 0, 0), Muted(x.S("aiutoServizi")), error);
        var rows = new List<(string Id, CheckBox On, TextBox Dur, TextBox Price, TextBox At, TextBox Pause)>(); var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); foreach (var _ in Enumerable.Range(0, 4)) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Put(FrameworkElement e, int row, int col) { e.Margin = new Thickness(col == 0 ? 0 : 10, 4, 0, 4); e.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }
        var r = 0;
        if (appointments) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var heads = new[] { "", "Durata", "Prezzo", "Prima della posa", "Posa" }; for (int i = 1; i < 5; i++) Put(T(heads[i], size: 12.5, bold: true, color: Res("Muted")), 0, i); r = 1; }
        foreach (var s in x.A("servizi"))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var on = new CheckBox { IsChecked = s.Bo("spuntato"), Content = T(s.S("nome"), size: 14) }; Put(on, r, 0);
            TextBox dur = null, price = null, at = null, pause = null;
            if (s.Bo("appuntamento"))
            {
                dur = Small(s.S("durata"), s.S("durataDi")); price = Small(s.S("prezzo"), s.S("prezzoDi")); at = Small(s.S("prima"), s.S("primaDi")); pause = Small(s.S("posa"), s.S("posaDi"));
                Put(Row(dur, T(" min", size: 13, color: Res("Muted"))), r, 1); Put(Row(price, T(" €", size: 13, color: Res("Muted"))), r, 2); Put(Row(at, T(" min", size: 13, color: Res("Muted"))), r, 3); Put(Row(pause, T(" min", size: 13, color: Res("Muted"))), r, 4);
            }
            rows.Add((s.S("id"), on, dur, price, at, pause)); r++;
        }
        if (rows.Count == 0) body.Children.Add(Muted("Ancora nessun servizio: si creano in Servizi")); else body.Children.Add(grid);
        void Away(string label) => body.Children.Add(F.Link(label, () => page.Main.ShowPage(new CatalogFormPage { Kind = "exceptions", Resource = page.Id })).M(0, 8, 0, 0));
        if (!x.Bo("orari") && !x.Bo("nuovo")) Away("Segna i giorni in cui non si prenota");
        Sections.Add(body);
        collect.Add(v =>
        {
            v["services"] = new JsonArray(rows.Where(k => k.On.IsChecked == true).Select(k => (JsonNode)k.Id).ToArray());
            foreach (var k in rows.Where(k => k.Dur != null)) { v["dur_" + k.Id] = k.Dur.Text; v["price_" + k.Id] = k.Price.Text; v["pa_" + k.Id] = k.At.Text; v["pm_" + k.Id] = k.Pause.Text; }
        });
        // ----- Giorni e orari di lavoro
        if (!x.Bo("orari")) return;
        var split = x.Bo("spezzati"); var own = new CheckBox { IsChecked = x.Bo("propri"), IsEnabled = !split, Content = Col(T("Ha giorni e orari suoi", size: 14), Muted("Spento: lavora negli orari di apertura dell'attività. Le ferie e i giorni liberi si segnano in Chiusure e giorni speciali").Also13()) };
        var hours = Col(F.Legend("Giorni e orari di lavoro").M(0, 18, 0, 0), own);
        void Schedules() => page.Main.ShowPage(new CatalogPage { Kind = "schedules" });
        hours.Children.Add(split ? F.Notice(WaitRows.Para(13.5, "Ha più di due fasce nello stesso giorno o settimane alterne: si cambiano da ", ((string, Action))("Orari di apertura", Schedules)))
            : WaitRows.Para(14, "La seconda fascia è facoltativa, per esempio per la pausa pranzo. Settimane alterne o più di due fasce si fanno in ", ((string, Action))("Orari di apertura", Schedules), ", scegliendo Solo per la risorsa").M(0, 6, 0, 0));
        var week = new List<(CheckBox On, TextBox A, TextBox B, TextBox A2, TextBox B2)>(); var list = Col();
        foreach (var d in x.A("giorni"))
        {
            var on = new CheckBox { IsChecked = d.Bo("acceso"), Content = T(d.S("nome"), size: 14), Width = 120, VerticalAlignment = VerticalAlignment.Center };
            var a = Small(d.S("inizio"), "09:00", 66); var b = Small(d.S("fine"), "18:00", 66); var a2 = Small(d.S("inizio2"), "", 66); var b2 = Small(d.S("fine2"), "", 66);
            TextBlock Sep(string s) { var t = T(s, size: 14, color: Res("Muted")); t.Margin = new Thickness(6, 0, 6, 0); t.VerticalAlignment = VerticalAlignment.Center; return t; }
            var line = Row(on, a, Sep("–"), b, Sep("e"), a2, Sep("–"), b2); line.Margin = new Thickness(0, 6, 0, 0); line.IsEnabled = !split; list.Children.Add(line); week.Add((on, a, b, a2, b2));
        }
        hours.Children.Add(list); Sections.Add(hours);
        if (!x.Bo("nuovo")) { var away = F.Link("Segna ferie o un giorno libero", () => page.Main.ShowPage(new CatalogFormPage { Kind = "exceptions", Resource = page.Id })); hours.Children.Add(away.M(0, 10, 0, 0)); }
        collect.Add(v =>
        {
            v["own_hours"] = own.IsChecked == true;
            for (int n = 0; n < week.Count; n++) { var k = week[n]; v[$"day_{n}"] = k.On.IsChecked == true; v[$"start_{n}"] = k.A.Text; v[$"end_{n}"] = k.B.Text; v[$"start2_{n}"] = k.A2.Text; v[$"end2_{n}"] = k.B2.Text; }
        });
    }

    /// <summary>app.js, #custom-fields: the questions of the booking form: label, kind of answer, choices, required,
    /// shown only when an earlier one has that answer; 12 at most.</summary>
    void Service(JsonNode x)
    {
        mine.Add("fields");
        var fields = x.A("domande").Select(q => (JsonObject)q.DeepClone()).ToList(); var holder = Col(); var add = new Button { Content = "Aggiungi una domanda", HorizontalAlignment = HorizontalAlignment.Left };
        void Render()
        {
            holder.Children.Clear();
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i]; var n = i;
                var label = Box(f.S("label")); label.MaxLength = 80; label.TextChanged += (_, _) => f["label"] = label.Text;
                var type = new ComboBox { ItemsSource = new[] { ("text", "Testo"), ("number", "Numero"), ("checkbox", "Casella sì/no"), ("select", "Scelta da elenco") }.Select(o => new ComboBoxItem { Content = o.Item2, Tag = o.Item1 }).ToList() };
                type.SelectedItem = type.Items.Cast<ComboBoxItem>().FirstOrDefault(o => (string)o.Tag == (f.S("type") == "" ? "text" : f.S("type"))) ?? type.Items[0];
                var choices = Box(string.Join("\n", (f["choices"] as JsonArray)?.Select(c => c.ToString()) ?? Array.Empty<string>()), true); choices.MinHeight = 64;
                var choicesField = F.Field("Scelte, una per riga", choices); choicesField.Visibility = f.S("type") == "select" ? Visibility.Visible : Visibility.Collapsed;
                choices.TextChanged += (_, _) => f["choices"] = new JsonArray(choices.Text.Split('\n').Select(v => v.Trim()).Where(v => v != "").Select(v => (JsonNode)v).ToArray());
                type.SelectionChanged += (_, _) => { f["type"] = (string)((ComboBoxItem)type.SelectedItem).Tag; choicesField.Visibility = f.S("type") == "select" ? Visibility.Visible : Visibility.Collapsed; };
                var required = new CheckBox { IsChecked = f.Bo("required"), Content = T("Risposta obbligatoria", size: 14), Margin = new Thickness(0, 12, 0, 0) }; required.Checked += (_, _) => f["required"] = true; required.Unchecked += (_, _) => f["required"] = false;
                var when = new ComboBox { ItemsSource = fields.Take(i).Select((p, j) => new ComboBoxItem { Content = p.S("label") != "" ? p.S("label") : $"Domanda {j + 1}", Tag = p.S("key") }).Prepend(new ComboBoxItem { Content = "Sempre", Tag = "" }).ToList() };
                when.SelectedItem = when.Items.Cast<ComboBoxItem>().FirstOrDefault(o => (string)o.Tag == (f["show_if"]?.S("key") ?? "")) ?? when.Items[0];
                var isBox = Box(f["show_if"]?.S("value") ?? ""); isBox.MaxLength = 80; isBox.Tag = "Per esempio sì, oppure una delle scelte"; AgendaPage.Hint(isBox);
                var isField = F.Field("ha come risposta", isBox); isField.Visibility = (string)((ComboBoxItem)when.SelectedItem).Tag != "" ? Visibility.Visible : Visibility.Collapsed;
                void Cond()
                {
                    var key = (string)((ComboBoxItem)when.SelectedItem).Tag;
                    if (key != "" && isBox.Text.Trim() != "") f["show_if"] = new JsonObject { ["key"] = key, ["value"] = isBox.Text.Trim() }; else f.Remove("show_if");
                    isField.Visibility = key != "" ? Visibility.Visible : Visibility.Collapsed;
                }
                when.SelectionChanged += (_, _) => Cond(); isBox.TextChanged += (_, _) => Cond();
                var remove = F.Small(B("Rimuovi domanda", () => { var key = f.S("key"); fields.RemoveAt(n); foreach (var o in fields) if (o["show_if"]?.S("key") == key) o.Remove("show_if"); Render(); }, "Ghost")); remove.HorizontalAlignment = HorizontalAlignment.Left;
                holder.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 4, 14, 14), Margin = new Thickness(0, 0, 0, 10),
                    Child = Col(F.Grid2(F.Field($"Domanda {i + 1}", label), F.Field("Tipo di risposta", type), choicesField, F.Field("Mostrala solo se", when), isField, null), required, remove.M(0, 10, 0, 0)) });
            }
            add.IsEnabled = fields.Count < 12;
        }
        add.Click += (_, _) => { if (fields.Count >= 12) return; fields.Add(new JsonObject { ["key"] = "campo_" + Guid.NewGuid().ToString("N")[..12], ["label"] = "", ["type"] = "text", ["required"] = false, ["choices"] = new JsonArray() }); Render(); };
        Render();
        var p = T("Domande facoltative o obbligatorie che il cliente trova nel modulo, per esempio allergie, occasione o preferenze", size: 14); p.TextWrapping = TextWrapping.Wrap;
        Sections.Add(Col(T("Richieste aggiuntive alla prenotazione", "H2").M(0, 22, 0, 0), p.M(0, 0, 0, 10), error, holder, add));
        collect.Add(v => v["fields"] = new JsonArray(fields.Select(f => f.DeepClone()).ToArray()));
    }
}

/// <summary>templates/holidays.html: the Italian public holidays of a year, Easter and the patron saint: the ticked ones
/// become closures of the whole business; again on the same year it adds only what is missing.</summary>
public sealed class HolidaysPage : Page
{
    int year;
    public override async Task Load() => Draw(await Api.Current.Call("festivita", new { anno = year }));
    void Back() => Main.ShowPage(new CatalogPage { Kind = "exceptions" });

    void Draw(JsonNode d)
    {
        year = d.I("anno"); var It = AgendaPage.It;
        var years = new ComboBox { ItemsSource = d.A("anni").Select(y => new ComboBoxItem { Content = y.ToString(), Tag = y.GetValue<int>() }).ToList(), Width = 100 };
        years.SelectedItem = years.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == year);
        years.SelectionChanged += async (_, _) => { year = (int)((ComboBoxItem)years.SelectedItem).Tag; await Guard(Load); };
        var top = new Border { Visibility = Visibility.Collapsed }; var list = Col(); var rows = d.A("righe").ToList(); var boxes = new List<CheckBox>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var day = DateOnly.Parse(r.S("giorno")); var label = day.ToString("dddd d MMMM", It);
            var box = new CheckBox { IsChecked = r.Bo("segnata") || r.Bo("spuntata"), IsEnabled = !r.Bo("segnata"), Tag = r.S("giorno"), Content = Col(T(r.S("nome"), size: 14, bold: true), T(char.ToUpper(label[0], It) + label[1..], size: 13, color: Res("Muted"))) };
            if (!r.Bo("segnata")) boxes.Add(box);
            var line = new DockPanel(); if (r.Bo("segnata")) { var pill = MainWindow.Pill("Già segnata", "ok"); DockPanel.SetDock(pill, Dock.Right); line.Children.Add(pill); }
            line.Children.Add(box);
            list.Children.Add(new Border { Child = line, Padding = new Thickness(0, 12, 0, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1) });
        }
        var patron = new DatePicker { SelectedDate = DateTime.TryParse(d.S("patrono"), out var pd) ? pd : null }; var name = Box(d.S("nomePatrono")); name.MaxLength = 60; name.Tag = "Santi Pietro e Paolo"; AgendaPage.Hint(name);
        var hint = T("Facoltativo: il giorno e il nome, per esempio 29 giugno, Santi Pietro e Paolo a Roma. Resta per gli anni dopo", size: 14, color: Res("Muted")); hint.TextWrapping = TextWrapping.Wrap;
        var confirm = new CheckBox { Content = T("Ho verificato le prenotazioni segnalate e confermo la chiusura", size: 14), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Segna come chiusure", Style = Style("Primary") };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var r = await Api.Current.Call("festivita.segna", new JsonObject { ["anno"] = year, ["day"] = new JsonArray(boxes.Where(b => b.IsChecked == true).Select(b => (JsonNode)(string)b.Tag).ToArray()),
                    ["patron_day"] = patron.SelectedDate?.ToString("yyyy-MM-dd") ?? "", ["patron_name"] = name.Text, ["confirm_conflicts"] = confirm.IsChecked == true });
                Main.Flash(r.S("testo")); Back();
            }
            catch (FormError ex) { top.Child = F.Notice(new TextBlock { Text = ex.Message, FontSize = 13.5, TextWrapping = TextWrapping.Wrap }, "error"); top.Visibility = Visibility.Visible; top.BringIntoView(); if (ex.Confirm) confirm.Visibility = Visibility.Visible; }
            catch (NeedsLogin) { Shell.Instance?.SignedOut(); }
            catch (ApiError ex) { Main.Flash(ex.Message, true); }
            finally { save.IsEnabled = true; }
        };
        var actions = Row(save, F.Link("Torna a Chiusure e giorni speciali", Back).M(14, 0, 0, 0)); ((TextBlock)actions.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        Content = F.Page(1180, Col(F.Back("Chiusure e giorni speciali", Back),
            F.Heading(null, $"Festività del {year}", "Le feste nazionali, Pasqua e Pasquetta già calcolate e il santo patrono della tua città: quelle spuntate diventano giorni di chiusura di tutta l'attività. Quelle in cui lavori le lasci senza spunta. Rifarlo non crea doppioni", years),
            F.Card(top, list, F.Legend("Santo patrono").M(0, 18, 0, 0), hint, F.Grid2(F.Field("Giorno", patron), F.Field("Nome", name)), confirm, actions.M(0, 18, 0, 0))));
    }
}
/// <summary>templates/services_import.html: Importa servizi: the price list of the old program, from Excel or CSV.</summary>
public sealed class ServicesImportPage : Page
{
    string path = ""; JsonNode done; string error = "";
    public override async Task Load() => Draw(await Api.Current.Call("servizi.importa", new { }));
    static TextBlock Muted(string text) { var t = T(text, size: 14, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }

    void Draw(JsonNode d)
    {
        void Services() => Main.ShowPage(new CatalogPage { Kind = "services" });
        var body = Col();
        if (error != "") body.Children.Add(F.Notice(error, "warning"));
        if (done != null)
        {
            var text = WaitRows.Para(13.5, $"Creati {done.I("creati")} servizi", done.I("prodotti") > 0 ? $" e {done.I("prodotti")} " : "", done.I("prodotti") > 0 ? ((string, Action))("prodotti", () => Main.ShowPage(new CatalogPage { Kind = "products" })) : "",
                done.I("prodotti") > 0 ? " (le righe del gruppo Rivendita)" : "", done.I("uguali") > 0 ? $" · già presenti e saltati: {done.I("uguali")}" : "", done.I("quantiErrori") > 0 ? $" · con errori: {done.I("quantiErrori")}" : "",
                ". Controllali in ", ((string, Action))("Servizi", Services), ": tipo, durata e varianti si ritoccano lì.");
            text.Foreground = Res("Ink"); body.Children.Add(F.Notice(text));
            foreach (var e in done.A("errori")) body.Children.Add(T("• " + e, size: 13));
        }
        var file = T(path != "" ? System.IO.Path.GetFileName(path) : "Nessun file scelto", size: 14, color: Res("Muted")); file.VerticalAlignment = VerticalAlignment.Center; file.Margin = new Thickness(10, 0, 0, 0);
        var pick = B("Scegli il file", () => { var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Excel o CSV|*.xlsx;*.csv;*.txt" }; if (dlg.ShowDialog(Application.Current.MainWindow) == true) { path = dlg.FileName; file.Text = System.IO.Path.GetFileName(path); } });
        var place = new ComboBox { ItemsSource = d.A("sedi").Select(l => new ComboBoxItem { Content = l.S("nome"), Tag = l.S("id") }).ToList(), SelectedIndex = 0 };
        var staff = new CheckBox { IsChecked = true, Margin = new Thickness(0, 14, 0, 0), Content = Col(T("Tutti gli operatori della sede fanno questi servizi", size: 14), Muted("Dopo, nella scheda di ogni servizio, togli chi non lo fa").Also13()) };
        var go = new Button { Content = "Importa i servizi", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        go.Click += async (_, _) =>
        {
            go.IsEnabled = false; error = ""; done = null;
            try
            {
                if (path == "") throw new ApiError("Scegli il file da importare.");
                var bytes = System.IO.File.ReadAllBytes(path); if (bytes.Length > 20 * 1024 * 1024) throw new ApiError("File troppo grande: al massimo 20 MB.");
                var r = await Api.Current.Call("servizi.importa", new { file = Convert.ToBase64String(bytes), location = (string)(place.SelectedItem as ComboBoxItem)?.Tag ?? "", staff = staff.IsChecked == true });
                done = r["fatto"]; path = ""; Draw(r);
            }
            catch (NeedsLogin) { Shell.Instance?.SignedOut(); }
            catch (Exception ex) when (ex is ApiError or System.IO.IOException or UnauthorizedAccessException) { error = ex.Message; Draw(d); }
            finally { go.IsEnabled = true; }
        };
        body.Children.Add(F.Label("File Excel (.xlsx) o CSV")); body.Children.Add(Row(pick, file)); body.Children.Add(F.Field("Sede", place)); body.Children.Add(staff); body.Children.Add(go);
        var tips = Col(T("Come deve essere il file", "H2"));
        foreach (var tip in new[] { "La prima riga sono i titoli: Servizio, Prezzo, Durata in minuti. Le altre colonne si ignorano", "Prezzo e durata possono mancare: senza durata il servizio dura 30 minuti, senza prezzo non lo mostra", "I servizi nascono come appuntamento con un operatore, ogni 15 minuti", "Prima crea gli operatori, se vuoi che li facciano subito" })
        { var t = T(tip, size: 14); t.TextWrapping = TextWrapping.Wrap; tips.Children.Add(new Border { Child = t, Padding = new Thickness(0, 10, 0, 10), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) }); }
        var aside = new Border { Style = Style("Card"), Child = tips, VerticalAlignment = VerticalAlignment.Top }; var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        var left = F.Card(body); Grid.SetColumn(aside, 2); grid.Children.Add(left); grid.Children.Add(aside);
        Content = F.Page(1180, Col(F.Back("Servizi", Services), F.Heading(null, "Importa servizi", "Il listino del programma di prima, da Excel o CSV: un servizio per riga, con prezzo e durata. I servizi che hanno già lo stesso nome si saltano, quindi si può caricare due volte senza doppioni"), grid));
    }
}
/// <summary>templates/allergens.html: Intolleranze e allergie: on or off in the booking form, the items, the standard
/// list, and how the client sees them.</summary>
public sealed class AllergensPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("allergie", new { }));
    async Task Act(object args)
    {
        try { var d = await Api.Current.Call("allergie.azione", args); Main.Flash(d.S("testo")); Draw(d); }
        catch (ApiError ex) when (ex is not NeedsLogin and not NeedsConfirm) { Main.Flash(ex.Message, true); }
    }
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }

    void Draw(JsonNode d)
    {
        var on = d.Bo("accese"); var items = d.A("voci").Select(v => v.ToString()).ToList();
        var toggle = BA(on ? "Non chiederle più" : "Chiedile nel modulo", () => Act(new { azione = "toggle", on = !on }), on ? null : "Primary"); DockPanel.SetDock(toggle, Dock.Right); toggle.VerticalAlignment = VerticalAlignment.Center;
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Res(on ? "Ok" : "Line"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(dot, Dock.Left);
        var sw = new DockPanel(); sw.Children.Add(toggle); sw.Children.Add(dot);
        sw.Children.Add(Col(T(on ? "Il modulo le chiede" : "Il modulo non le chiede", size: 14, bold: true), Muted(on ? "Il cliente spunta quelle di chi viene: sono facoltative" : "Accendile per far segnare al cliente le intolleranze già quando prenota", 13)));
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var i in items) { var chip = F.Pill(i, () => _ = Guard(() => Act(new { azione = "remove", name = i }))); chip.Margin = new Thickness(0, 0, 8, 8); chip.ToolTip = "Togli " + i; chips.Children.Add(chip); }
        if (items.Count == 0) chips.Children.Add(Muted("Nessuna voce: aggiungine qui sotto o ripristina l'elenco standard"));
        var fresh = Box(); fresh.MaxLength = 60; fresh.Tag = "Per esempio: Vegano, Senza aglio, Kiwi"; AgendaPage.Hint(fresh);
        var add = BA("Aggiungi", () => Act(new { azione = "add", name = fresh.Text })); DockPanel.SetDock(add, Dock.Right); add.Margin = new Thickness(8, 0, 0, 0);
        var addRow = new DockPanel(); addRow.Children.Add(add); addRow.Children.Add(fresh);
        var reset = F.Small(BA("Ripristina l'elenco standard", async () => { if (Ask("Rimettere l'elenco standard? Le voci aggiunte a mano vengono tolte, le prenotazioni non cambiano.")) await Act(new { azione = "reset" }); }, "Ghost")); reset.HorizontalAlignment = HorizontalAlignment.Left;
        var left = F.Card(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 14), Child = sw },
            F.Section("Le voci", null, Muted($"{items.Count} su 40")), chips, F.Label("Aggiungi una voce"), addRow, reset.M(0, 12, 0, 0));
        var preview = Col(T("Come lo vede il cliente", "H2"));
        if (on && items.Count > 0)
        {
            preview.Children.Add(T("Intolleranze o allergie?", size: 14, bold: true)); preview.Children.Add(Muted("Segna quelle di chi viene, le passiamo alla cucina. Altro? Scrivilo qui sotto", 13));
            var boxes = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; foreach (var i in items) boxes.Children.Add(new CheckBox { IsEnabled = false, Content = T(i, size: 13.5), Margin = new Thickness(0, 0, 14, 6) }); preview.Children.Add(boxes);
        }
        else preview.Children.Add(Muted("Nel modulo non compare nulla"));
        var soft = new Border { Style = Style("Card"), Background = Res("BrandSoft"), Child = preview, Margin = new Thickness(0, 0, 0, 14) };
        var why = new Border { Style = Style("Card"), Child = Col(T("Perché queste voci", size: 16, bold: true),
            Muted("L'elenco standard sono i 14 allergeni che il Regolamento UE 1169/2011 obbliga a indicare nel menu (cereali con glutine, crostacei, uova, pesce, arachidi, soia, latte, frutta a guscio, sedano, senape, sesamo, solfiti, lupini e molluschi), più il lattosio, l'intolleranza che si chiede più spesso.").M(0, 6, 0, 8),
            Muted("Sono dati sulla salute: li vede solo chi nel portale vede le note, non finiscono nelle notifiche email e Telegram e si cancellano con i dati del cliente. Citali nell'informativa delle prenotazioni.")) };
        var aside = Col(soft, why); var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        Grid.SetColumn(aside, 2); grid.Children.Add(left); grid.Children.Add(aside);
        Content = F.Page(1180, Col(F.Back("Configura l'attività", () => Main.Show("configura")),
            F.Heading(null, "Intolleranze e allergie", "Le caselle che il cliente trova nel modulo di prenotazione, anche nel sito WordPress. Quelle segnate compaiono evidenziate nella prenotazione, nell'agenda e nella scheda del cliente"), grid));
    }
}
