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

    void Resource(CatalogFormPage page, JsonNode x)
    {
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

/// <summary>templates/holidays.html. Still to come in this build.</summary>
public sealed class HolidaysPage : Page
{
    public override Task Load() { Content = F.Page(1180, Col(F.Back("Chiusure e giorni speciali", () => Main.ShowPage(new CatalogPage { Kind = "exceptions" })), F.Heading(null, "Festività italiane", "Questa pagina arriva con il prossimo pezzo."))); return Task.CompletedTask; }
}
/// <summary>templates/services_import.html. Still to come in this build.</summary>
public sealed class ServicesImportPage : Page
{
    public override Task Load() { Content = F.Page(1180, Col(F.Back("Servizi", () => Main.ShowPage(new CatalogPage { Kind = "services" })), F.Heading(null, "Importa i servizi da Excel", "Questa pagina arriva con il prossimo pezzo."))); return Task.CompletedTask; }
}
/// <summary>templates/allergens.html: Intolleranze e allergie. Still to come in this build.</summary>
public sealed class AllergensPage : Page
{
    public override Task Load() { Content = F.Page(1180, Col(F.Back("Configura l'attività", () => Main.Show("configura")), F.Heading(null, "Intolleranze e allergie", "Questa pagina arriva con il prossimo pezzo."))); return Task.CompletedTask; }
}
