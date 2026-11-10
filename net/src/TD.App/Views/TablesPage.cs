using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/tables.html: Sale e tavoli: the floor plan of a room at a day and time; tables dragged into place,
/// bookings and walk-ins seated with a click.</summary>
public sealed class TablesPage : Page
{
    public string Room = "";
    string day = "", clock = "", picked; Button chosen; readonly List<Button> picks = new(), spots = new(); TextBlock error;
    public override async Task Load() => Draw(await Api.Current.Call("tavoli", new { sala = Room, data = day, ora = clock }));
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static Border Dot(string state) => new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), BorderBrush = Res(Edge(state)), Background = Res(Fill(state)), Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    static string Edge(string state) => state == "booked" ? "Warn" : state == "seated" ? "Brand" : "Ok";
    static string Fill(string state) => state == "booked" ? "WarnSoft" : state == "seated" ? "BrandSoft" : "OkSoft";

    async Task Post(string name, object args)
    {
        try { await Api.Current.Call(name, args); await Load(); }
        catch (ApiError ex) when (ex is not NeedsLogin) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
    }
    Task Give(string booking, string table) => Post("tavoli.assegna", new JsonObject { ["booking"] = booking, ["tables"] = table != null ? new JsonArray(table) : new JsonArray() });
    void Pick(string id)
    {
        picked = id; foreach (var b in picks) { b.BorderBrush = Res((string)b.Tag == picked ? "Brand" : "Line"); b.BorderThickness = new Thickness((string)b.Tag == picked ? 2 : 1); }
    }
    void Choose(Button t)
    {
        chosen = t; foreach (var b in spots) b.Effect = b == chosen ? new System.Windows.Media.Effects.DropShadowEffect { Color = ((System.Windows.Media.SolidColorBrush)Res("Brand")).Color, ShadowDepth = 0, BlurRadius = 10, Opacity = 1 } : null;
    }

    void Draw(JsonNode d)
    {
        picks.Clear(); spots.Clear(); picked = null; chosen = null;
        Room = d.S("sala"); day = d.S("giorno"); clock = d.S("ora"); bool canEdit = d.Bo("modifica"), canSeat = d.Bo("siede"); var tables = d.A("tavoli").ToList(); var areas = d.A("sale").ToList();
        var actions = new List<FrameworkElement>();
        if (canEdit)
        {
            actions.Add(B("Nuova sala", () => Main.ShowPage(new TableFormPage { What = "sala" })));
            if (Room != "") actions.Add(B("Nuovo tavolo", () => Main.ShowPage(new TableFormPage { What = "tavolo", Room = Room }), "Primary").M(10, 0, 0, 0));
        }
        var page = Col(F.Heading(d.S("nome"), "Sale e tavoli", "La piantina di ogni sala all'ora che scegli: chi è seduto, i tavoli prenotati e quelli liberi. Il cliente sceglie al massimo la sala, mai il tavolo", actions.ToArray()));
        if (areas.Count == 0)
        {
            page.Children.Add(F.Card(F.Empty("▦", "Ancora nessuna sala", "Crea la prima sala, per esempio Sala o Dehors, poi i suoi tavoli. Come si assegnano i tavoli si sceglie nella scheda della sede, in Configura l'attività")));
            Content = F.Page(1180, page); return;
        }
        if (d.Bo("spenti")) page.Children.Add(F.Notice($"In {d.S("sede")} la gestione dei tavoli è spenta: la piantina si vede, ma le prenotazioni non ricevono un tavolo. Si accende nella scheda della sede"));

        // nav.tabs
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var a in areas)
        {
            var id = a.S("id"); var label = new TextBlock { FontSize = 14 }; label.Inlines.Add(a.S("nome")); if (a.S("sede") != "") label.Inlines.Add(new System.Windows.Documents.Run("  " + a.S("sede")) { Foreground = Res("Muted"), FontSize = 12.5 });
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) }; if (id == Room) b.Style = Style("Primary");
            b.Click += async (_, _) => { Room = id; await Guard(Load); }; tabs.Children.Add(b);
        }
        page.Children.Add(tabs);

        // form.floor-when
        var date = new DatePicker { SelectedDate = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture), Width = 150, Margin = new Thickness(8, 0, 12, 0) }; var time = Box(clock); time.Width = 80; time.Margin = new Thickness(8, 0, 12, 0);
        var when = Row(T("Giorno", size: 13, bold: true), date, T("Ora", size: 13, bold: true), time, F.Small(BA("Mostra", async () => { day = date.SelectedDate?.ToString("yyyy-MM-dd") ?? day; clock = time.Text.Trim().Replace('.', ':'); await Load(); })));
        foreach (var t in when.Children.OfType<TextBlock>()) t.VerticalAlignment = VerticalAlignment.Center;
        if (canEdit) { var edit = F.Link($"Modifica {d.S("salaNome")}" + (d.I("coperti") > 0 ? $" · {d.I("coperti")} coperti" : ""), () => Main.ShowPage(new TableFormPage { What = "sala", Id = Room, Room = Room })); edit.VerticalAlignment = VerticalAlignment.Center; edit.Margin = new Thickness(14, 0, 0, 0); when.Children.Add(edit); }
        page.Children.Add(when.M(0, 0, 0, 16));

        // div.floor: 4:3, the tables at their percent of the plan
        var floor = new Canvas { ClipToBounds = true, MinHeight = 320, Background = Res("Bg") };
        void Put(Button b, double x, double y) { Canvas.SetLeft(b, floor.ActualWidth * x / 100); Canvas.SetTop(b, floor.ActualHeight * y / 100); }
        floor.SizeChanged += (_, e) => { if (e.WidthChanged) floor.Height = Math.Max(320, floor.ActualWidth * 3 / 4); foreach (var b in spots) { var (x, y) = ((double, double))b.Resources["at"]; Put(b, x, y); } };
        foreach (var x in tables)
        {
            var id = x.S("id"); var who = x["chi"]; var state = x.S("stato");
            var inner = Col(T(x.S("nome"), size: 15, bold: true), T($"{x.I("min")}–{x.I("max")}", size: 12));
            if (who != null) { var n = T(who.S("nome"), size: 12, bold: true); n.MaxWidth = 110; n.TextTrimming = TextTrimming.CharacterEllipsis; inner.Children.Add(n); inner.Children.Add(T($"{who.S("da")} · {who.I("persone")}", size: 12)); }
            foreach (var t in inner.Children.OfType<TextBlock>()) t.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Button { Content = inner, MinWidth = 76, MinHeight = 0, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(2), BorderBrush = Res(Edge(state)), Background = Res(Fill(state)), Foreground = Res("Ink"), Cursor = canEdit ? Cursors.SizeAll : Cursors.Hand, AllowDrop = canSeat, Tag = id,
                ToolTip = $"Tavolo {x.S("nome")}, {x.I("min")}–{x.I("max")} persone, {(state == "free" ? "libero" : state == "seated" ? "seduti" : "prenotato")}" };
            b.Resources["at"] = (double.Parse(x.S("x"), CultureInfo.InvariantCulture), double.Parse(x.S("y"), CultureInfo.InvariantCulture));
            Point? from = null; var moved = false;
            b.PreviewMouseLeftButtonDown += (_, e) => { from = e.GetPosition(floor); moved = false; };
            b.PreviewMouseMove += (_, e) =>
            {
                // a table dragged on the plan: where it stands, in percent
                if (!canEdit || from == null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(floor); if (!moved && (p - from.Value).Length < 6) return;
                if (!moved) { moved = true; b.CaptureMouse(); Panel.SetZIndex(b, 2); }
                var at = (Math.Round(Math.Clamp(p.X / floor.ActualWidth * 100 - 4, 0, 95), 1), Math.Round(Math.Clamp(p.Y / floor.ActualHeight * 100 - 4, 0, 95), 1)); b.Resources["at"] = at; Put(b, at.Item1, at.Item2);
            };
            b.PreviewMouseLeftButtonUp += async (_, e) =>
            {
                from = null; if (!moved) return;
                b.ReleaseMouseCapture(); Panel.SetZIndex(b, 0); e.Handled = true; var (px, py) = ((double, double))b.Resources["at"];
                try { await Api.Current.Call("tavoli.sposta", new { table = id, x = px.ToString(CultureInfo.InvariantCulture), y = py.ToString(CultureInfo.InvariantCulture) }); }
                catch (ApiError ex) when (ex is not NeedsLogin) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            b.Click += async (_, _) =>
            {
                if (moved) { moved = false; return; }
                if (picked != null) { await Give(picked, id); return; }
                Choose(who != null ? b : null); if (who != null) b.Resources["booking"] = who.S("prenotazione");
            };
            b.Drop += async (_, e) => { if (e.Data.GetData(DataFormats.StringFormat) is string booking) await Give(booking, id); };
            spots.Add(b); floor.Children.Add(b);
        }
        if (tables.Count == 0) { var none = Muted("Nessun tavolo in questa sala" + (canEdit ? ": aggiungili con Nuovo tavolo" : "")); none.Margin = new Thickness(20); floor.Children.Add(none); }
        var legend = Row(Dot("free"), T("Libero", size: 13), Dot("booked"), T("Prenotato", size: 13), Dot("seated"), T("Seduti", size: 13),
            Muted((canEdit ? " · Trascina un tavolo per spostarlo sulla piantina" : "") + (canSeat ? " · Scegli una prenotazione qui accanto, poi tocca il tavolo" : ""), 13));
        error = new TextBlock { Foreground = Res("Danger"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var plan = F.Card(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = floor }, legend.M(0, 10, 0, 0), error);

        // aside: Senza tavolo alle HH:MM
        var loose = Col(T($"Senza tavolo alle {clock}", "H2"));
        foreach (var x in d.A("senza"))
        {
            var id = x.S("prenotazione"); var row = new DockPanel(); var right = T($"{x.S("da")} · {x.I("persone")} pers.", size: 13); DockPanel.SetDock(right, Dock.Right); row.Children.Add(right); row.Children.Add(T(x.S("nome"), size: 14, bold: true));
            var b = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(12, 8, 12, 8), IsEnabled = canSeat, Tag = id, Cursor = Cursors.Hand };
            b.Click += (_, _) => Pick(picked == id ? null : id);
            Point? start = null; b.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(null);
            // dragged onto a table: the same as picking it and touching the table
            b.PreviewMouseMove += (_, e) => { if (start != null && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(null) - start.Value).Length >= 6) { start = null; DragDrop.DoDragDrop(b, id, DragDropEffects.Move); } };
            picks.Add(b); loose.Children.Add(b);
        }
        if (picks.Count == 0) loose.Children.Add(Muted("Tutte le prenotazioni di quest'ora hanno un tavolo"));
        if (canSeat)
        {
            var un = F.Small(BA("Togli il tavolo", async () => { if (chosen?.Resources["booking"] is string booking) await Give(booking, null); }, "Ghost"));
            loose.Children.Add(Row(Muted("Per togliere un tavolo: scegli il tavolo prenotato, poi "), un).M(0, 8, 0, 0));
            foreach (var t in ((StackPanel)loose.Children[^1]).Children.OfType<TextBlock>()) { t.VerticalAlignment = VerticalAlignment.Center; t.MaxWidth = 170; }
        }
        var side = Col(F.Card(loose));
        var free = tables.Where(x => x.S("stato") == "free").ToList(); var services = d.A("servizi").ToList();
        if (canSeat && services.Count > 0 && tables.Count > 0 && !d.Bo("spenti"))
        {
            ComboBox Pickbox(IEnumerable<(string Id, string Name)> items) { var c = new ComboBox { ItemsSource = items.Select(i => new ComboBoxItem { Content = i.Name, Tag = i.Id }).ToList() }; if (c.Items.Count > 0) c.SelectedIndex = 0; return c; }
            string Val(ComboBox c) => (string)(c.SelectedItem as ComboBoxItem)?.Tag ?? "";
            var table = Pickbox(free.Select(x => (x.S("id"), $"{x.S("nome")} ({x.I("min")}–{x.I("max")})"))); var service = Pickbox(services.Select(s => (s.S("id"), s.S("nome")))); var party = Box("2");
            var seat = BA("Siedi adesso", async () => { var r = await Api.Current.Call("tavoli.entra", new { sala = Room, table = Val(table), service = Val(service), party = party.Text.Trim() }); Main.Flash(r.S("testo")); await Load(); }, "Primary"); seat.HorizontalAlignment = HorizontalAlignment.Stretch;
            side.Children.Add(F.Card(T("Entra senza prenotare", "H2"), F.Field("Tavolo", table), services.Count > 1 ? F.Field("Servizio", service) : null, F.Field("Persone", party), seat.M(0, 14, 0, 0)));
        }
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        side.VerticalAlignment = VerticalAlignment.Top; plan.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(side, 2); grid.Children.Add(plan); grid.Children.Add(side);
        page.Children.Add(grid);
        Content = F.Page(1180, page);
    }
}

/// <summary>form.html with AreaForm or TableForm: a room or a table, new or changed, and its removal.</summary>
public sealed class TableFormPage : Page
{
    public string What = "sala", Id = "", Room = "";
    public override async Task Load() => Draw(await Api.Current.Call("tavoli.modulo", new { cosa = What, id = Id, sala = Room }));
    void Back(string room = null) => Main.ShowPage(new TablesPage { Room = room ?? Room });

    void Draw(JsonNode d)
    {
        var form = new FormView(d.A("campi"));
        var save = new Button { Content = "Salva", Style = Style("Primary"), Margin = new Thickness(0, 18, 14, 0) };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await Guard(async () => { var r = await Api.Current.Call("tavoli.salva", new JsonObject { ["cosa"] = What, ["id"] = Id, ["valori"] = form.Values() }); Main.Flash(r.S("testo")); Back(r.S("sala")); }); }
            catch (FormError ex) { form.Errors(ex.Errors); }
            finally { save.IsEnabled = true; }
        };
        var back = F.Link("Torna a Sale e tavoli", () => Back()); back.VerticalAlignment = VerticalAlignment.Center; back.Margin = new Thickness(0, 18, 0, 0);
        var card = Col(form.Panel, Row(save, back));
        if (d.S("elimina") != "")
        {
            var remove = F.Small(BA(d.S("elimina"), async () => { if (!Ask(d.S("elimina") + "?")) return; var r = await Api.Current.Call("tavoli.elimina", new { cosa = What, id = Id }); Main.Flash(r.S("testo")); Back(What == "sala" ? "" : null); }, "Ghost"));
            remove.Foreground = Res("Danger"); remove.HorizontalAlignment = HorizontalAlignment.Left; card.Children.Add(remove.M(0, 22, 0, 0));
        }
        Content = F.Page(1180, Col(F.Heading(d.S("nome"), d.S("titolo"), d.S("descrizione")), F.Card(card)));
    }
}
