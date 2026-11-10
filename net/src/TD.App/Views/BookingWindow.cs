using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>Nuova prenotazione at the desk: operator, day and time first, then the services one after the other from the
/// same list (the last chosen shown first), the client found or written, notes and the service's questions.</summary>
public sealed class BookingWindow : Window
{
    readonly AgendaPage agenda;
    public bool Walkin;
    JsonNode catalog;
    readonly List<JsonNode> chosen = new();
    readonly Dictionary<string, string> options = new();
    string resource;
    DateTime when;
    readonly bool exactTime;
    string slot = "";
    JsonNode client;
    readonly StackPanel servicesBox = new(), chosenBox = new(), slotsBox = new(), clientBox = new(), questionsBox = new(), allergyBox = new();
    readonly ComboBox ops = new(), partyBox = new();
    readonly DatePicker date = new();
    readonly TextBox notes = Box("", true), search = Box(""), filter = Box("");
    readonly TextBox first = Box(""), last = Box(""), phone = Box(""), email = Box(""), forceTime = Box("");
    readonly CheckBox force = new() { Content = "Forza: un orario qualsiasi" }, marketing = new() { Content = "Il cliente vuole le comunicazioni (gli arriva l'email per confermare)" };
    readonly TextBlock status = T("", "MutedText");
    readonly Dictionary<string, FrameworkElement> answers = new();

    public BookingWindow(AgendaPage page, string resourceId, DateTime at, bool exact)
    {
        agenda = page; resource = resourceId ?? ""; when = at; exactTime = exact;
        Title = "Nuova prenotazione"; Width = 1080; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = Application.Current.MainWindow;
        Loaded += async (_, _) => await Guard(Init);
    }

    async Task Init()
    {
        catalog = await Api.Current.Call("prenotazione.catalogo");
        date.SelectedDate = when.Date;
        date.SelectedDateChanged += async (_, _) => await Guard(LoadSlots);
        ops.SelectionChanged += async (_, _) => { resource = (ops.SelectedItem as ComboBoxItem)?.Tag as string ?? ""; await Guard(LoadSlots); };
        filter.TextChanged += (_, _) => DrawServices();
        search.TextChanged += async (_, _) => await Guard(Search);
        force.Visibility = catalog.Bo("forza") ? Visibility.Visible : Visibility.Collapsed;
        force.Checked += (_, _) => { forceTime.Visibility = Visibility.Visible; forceTime.Text = when.ToString("HH:mm"); };
        force.Unchecked += (_, _) => forceTime.Visibility = Visibility.Collapsed;
        forceTime.Visibility = Visibility.Collapsed; forceTime.Width = 90;
        for (int i = 1; i <= 20; i++) partyBox.Items.Add(i.ToString());
        partyBox.SelectedIndex = 0; partyBox.SelectionChanged += async (_, _) => await Guard(LoadSlots);
        foreach (var a in catalog.A("allergeni")) allergyBox.Children.Add(new CheckBox { Content = a.ToString(), Tag = a.ToString() });

        var left = Col(
            T("Con chi e quando", "H2"),
            Field("Operatore", ops), Field("Giorno", date),
            T(catalog.S("servizio") + "", size: 13, bold: true).M(0, 0, 0, 4), filter.M(0, 0, 0, 6),
            new Border { Child = new ScrollViewer { Content = servicesBox, MaxHeight = 240 }, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) },
            T("Scelti", size: 13, bold: true).M(0, 12, 0, 4), chosenBox,
            Row(T("Persone ", size: 13).M(0, 6, 6, 0), partyBox).M(0, 8, 0, 0));
        var middle = Col(T("Orario", "H2"), Row(force, forceTime.M(8, 0, 0, 0)), status.M(0, 4, 0, 8), new ScrollViewer { Content = slotsBox, MaxHeight = 520 });
        var right = Col(T("Cliente", "H2"), clientBox, T("Note", size: 13, bold: true).M(0, 10, 0, 4), notes,
            allergyBox.Children.Count > 0 ? Col(T("Allergie e intolleranze", size: 13, bold: true).M(0, 10, 0, 4), allergyBox) : null, questionsBox.M(0, 10, 0, 0));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        void Put(UIElement e, int c) { var s = new ScrollViewer { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 14, 0) }; Grid.SetColumn(s, c); grid.Children.Add(s); }
        Put(left, 0); Put(middle, 1); Put(right, 2);
        var save = BA("Prenota", Submit, "Primary"); save.IsDefault = true;
        var foot = Row(save, B("Annulla", () => DialogResult = false, "Ghost").M(8, 0, 0, 0));
        foot.HorizontalAlignment = HorizontalAlignment.Right;
        var root = new DockPanel { Margin = new Thickness(24) };
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot.M(0, 12, 0, 0)); root.Children.Add(grid);
        Content = root;
        DrawClient(); DrawServices(); FillOps();
    }

    IEnumerable<JsonNode> Services => catalog.A("servizi");

    void FillOps()
    {
        // the operators of the first service chosen, or of every service
        var list = (chosen.Count > 0 ? chosen[0].A("operatori") : Services.SelectMany(s => s.A("operatori"))).GroupBy(o => o.S("id")).Select(g => g.First()).OrderBy(o => o.S("nome")).ToList();
        var items = new List<ComboBoxItem> { new() { Content = "Il primo disponibile", Tag = "" } };
        items.AddRange(list.Select(o => new ComboBoxItem { Content = o.S("nome") + (o["assente"] != null ? " (fuori ufficio)" : ""), Tag = o.S("id") }));
        ops.ItemsSource = items;
        ops.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == resource) ?? items[0];
    }

    void DrawServices()
    {
        servicesBox.Children.Clear();
        var q = filter.Text.Trim().ToLowerInvariant();
        var list = Services.Where(s => q == "" || s.S("nome").ToLowerInvariant().Contains(q))
            .Where(s => resource == "" || s.A("operatori").Any(o => o.S("id") == resource) || s.S("tipo") != "resource").ToList();
        if (chosen.Count > 0) list = list.Where(s => s.S("tipo") == "resource" && s.S("sedeId") == chosen[0].S("sedeId")).ToList();
        foreach (var s in list)
        {
            var b = new Button { Style = Style("Ghost"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6, 10, 6) };
            var dock = new DockPanel();
            var price = T(s.S("prezzo"), "MutedText"); DockPanel.SetDock(price, Dock.Right); dock.Children.Add(price);
            dock.Children.Add(T($"{s.S("nome")}  ·  {s.I("durata")} min", size: 14));
            b.Content = dock;
            var sv = s;
            b.Click += async (_, _) => { if (chosen.Count >= 5) { Say("Al massimo 5 servizi in una visita."); return; } chosen.Add(sv); DrawChosen(); DrawServices(); if (chosen.Count == 1) FillOps(); DrawQuestions(); await Guard(LoadSlots); };
            servicesBox.Children.Add(b);
        }
        if (list.Count == 0) servicesBox.Children.Add(T("Nessun servizio con questo nome.", "MutedText").M(10));
    }

    void DrawChosen()
    {
        chosenBox.Children.Clear();
        // the last chosen on top: it is the one just picked (2.22.x)
        for (int i = chosen.Count - 1; i >= 0; i--)
        {
            var s = chosen[i]; var idx = i;
            var opt = new ComboBox { MinWidth = 140, Visibility = s.A("varianti").Any() ? Visibility.Visible : Visibility.Collapsed };
            var items = new List<ComboBoxItem> { new() { Content = "Standard", Tag = "" } };
            items.AddRange(s.A("varianti").Select(v => new ComboBoxItem { Content = $"{v.S("nome")} · {v.I("durata")} min {v.S("prezzo")}", Tag = v.S("id") }));
            opt.ItemsSource = items; opt.SelectedItem = items.FirstOrDefault(x => (string)x.Tag == options.GetValueOrDefault(s.S("id"), "")) ?? items[0];
            opt.SelectionChanged += async (_, _) => { options[s.S("id")] = (string)((ComboBoxItem)opt.SelectedItem).Tag; await Guard(LoadSlots); };
            var remove = B("✕", async () => { chosen.RemoveAt(idx); DrawChosen(); DrawServices(); FillOps(); DrawQuestions(); await Guard(LoadSlots); }, "Ghost");
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            DockPanel.SetDock(opt, Dock.Right); row.Children.Add(opt.M(6, 0, 0, 0));
            row.Children.Add(T((i == 0 ? "" : "poi ") + s.S("nome"), size: 14, bold: i == chosen.Count - 1));
            chosenBox.Children.Add(new Border { Background = i == chosen.Count - 1 ? Res("BrandSoft") : Res("Surface"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 4, 4), Child = row, Margin = new Thickness(0, 0, 0, 4) });
        }
        if (chosen.Count == 0) chosenBox.Children.Add(T("Scegli uno o più servizi dalla lista.", "MutedText"));
    }

    async Task LoadSlots()
    {
        slotsBox.Children.Clear(); slot = "";
        if (chosen.Count == 0 || date.SelectedDate == null) { status.Text = "Scegli il servizio per vedere gli orari liberi."; return; }
        var s0 = chosen[0];
        var args = new JsonObject
        {
            ["service"] = s0.S("id"), ["option"] = options.GetValueOrDefault(s0.S("id"), ""), ["resource"] = resource, ["data"] = date.SelectedDate.Value.ToString("yyyy-MM-dd"),
            ["party"] = partyBox.SelectedIndex + 1,
        };
        if (chosen.Count > 1) args["extra"] = new JsonArray(chosen.Skip(1).Select(x => (JsonNode)x.S("id")).ToArray());
        status.Text = "Cerco gli orari liberi…";
        JsonNode r;
        try { r = await Api.Current.Call("prenotazione.orari", args); }
        catch (ApiError ex) { status.Text = ex.Message; return; }
        var slots = r.A("slots").ToList();
        status.Text = slots.Count == 0 ? "Nessun orario libero in questo giorno." + (catalog.Bo("forza") ? " Puoi forzare un orario." : "") : $"{slots.Count} orari liberi";
        var wrap = new WrapPanel();
        foreach (var x in slots)
        {
            var t = agenda.Local(x.S("start"));
            var label = t.ToString("HH:mm") + (resource == "" && x.S("resourceName") != "" ? "  " + x.S("resourceName") : "");
            var b = new ToggleButton { Content = label, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 6, 10, 6), Tag = x, Cursor = Cursors.Hand, Background = Res("Surface"), BorderBrush = Res("LineStrong") };
            b.Checked += (_, _) => { foreach (var o in wrap.Children.OfType<ToggleButton>()) if (o != b) o.IsChecked = false; slot = x.ToJsonString(); b.Background = Res("BrandSoft"); };
            b.Unchecked += (_, _) => { if (slot == x.ToJsonString()) slot = ""; b.Background = Res("Surface"); };
            wrap.Children.Add(b);
            if (exactTime && t.TimeOfDay == when.TimeOfDay && slot == "") b.IsChecked = true;
        }
        slotsBox.Children.Add(wrap);
    }

    // ---------- the client
    void DrawClient()
    {
        clientBox.Children.Clear();
        if (client != null)
        {
            clientBox.Children.Add(Card(Col(T($"{client.S("nome")} {client.S("cognome")}".Trim() + (client.Bo("vip") ? "  ★" : ""), size: 15, bold: true),
                T(string.Join(" · ", new[] { client.S("telefono"), client.S("email") }.Where(x => x != "")), "MutedText"),
                B("Cambia cliente", () => { client = null; DrawClient(); }, "Ghost").M(-10, 6, 0, 0))));
            return;
        }
        if (catalog.Bo("cercaClienti")) clientBox.Children.Add(Field("Cerca per nome, telefono o email", search));
        clientBox.Children.Add(new ContentControl { Name = "results" });
        clientBox.Children.Add(T("o scrivi un cliente nuovo", "MutedText").M(0, 4, 0, 8));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
        var a = Field("Nome", first); var b = Field("Cognome", last).M(8, 0, 0, 12);
        Grid.SetColumn(b, 1); g.Children.Add(a); g.Children.Add(b);
        clientBox.Children.Add(g);
        clientBox.Children.Add(Field("Telefono", phone)); clientBox.Children.Add(Field("Email (facoltativa)", email));
        clientBox.Children.Add(marketing);
    }

    async Task Search()
    {
        var holder = clientBox.Children.OfType<ContentControl>().FirstOrDefault();
        if (holder == null) return;
        if (search.Text.Trim().Length < 2) { holder.Content = null; return; }
        var found = await Api.Current.Call("clienti.cerca", new JsonObject { ["q"] = search.Text, ["quanti"] = 8 }) as JsonArray;
        var list = Col();
        foreach (var f in found ?? new JsonArray())
        {
            var b = new Button { Style = Style("Ghost"), HorizontalContentAlignment = HorizontalAlignment.Left, Content = Col(T($"{f.S("nome")} {f.S("cognome")}".Trim() + (f.Bo("vip") ? "  ★" : ""), size: 14, bold: true), T(string.Join(" · ", new[] { f.S("telefono"), f.S("email") }.Where(x => x != "")), "MutedText")) };
            var ff = f; b.Click += (_, _) => { client = ff; DrawClient(); };
            list.Children.Add(b);
        }
        if (found == null || found.Count == 0) list.Children.Add(T("Nessun cliente trovato: scrivilo qui sotto.", "MutedText").M(4));
        holder.Content = new Border { Child = list, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8) };
    }

    // ---------- the questions of the first service
    void DrawQuestions()
    {
        questionsBox.Children.Clear(); answers.Clear();
        if (chosen.Count == 0) return;
        foreach (var f in chosen[0].A("campi"))
        {
            var key = f.S("key"); var label = f.S("label") + (f.Bo("required") ? " *" : "");
            FrameworkElement input = f.S("type") switch
            {
                "checkbox" => new CheckBox { Content = f.S("label") },
                "select" => new ComboBox { ItemsSource = new[] { "" }.Concat(f.A("choices").Select(x => x.ToString())).ToList(), SelectedIndex = 0 },
                _ => Box(""),
            };
            answers[key] = input;
            questionsBox.Children.Add(f.S("type") == "checkbox" ? input.M(0, 0, 0, 8) : Field(label, input));
        }
    }

    JsonObject Answers()
    {
        var o = new JsonObject();
        foreach (var f in chosen.Count > 0 ? chosen[0].A("campi") : Enumerable.Empty<JsonNode>())
        {
            var key = f.S("key"); var input = answers[key];
            o[key] = input switch
            {
                CheckBox cb => cb.IsChecked == true,
                ComboBox combo => combo.SelectedItem?.ToString() ?? "",
                TextBox tb when f.S("type") == "number" && tb.Text.Trim() != "" => int.TryParse(tb.Text.Trim(), out var n) ? n : tb.Text,
                TextBox tb => tb.Text,
                _ => "",
            };
        }
        return o;
    }

    async Task Submit()
    {
        if (chosen.Count == 0) { Say("Scegli il servizio."); return; }
        var data = new JsonObject
        {
            ["service"] = chosen[0].S("id"), ["option"] = options.GetValueOrDefault(chosen[0].S("id"), ""), ["party"] = partyBox.SelectedIndex + 1,
            ["notes"] = notes.Text, ["answers"] = Answers(), ["request_key"] = Guid.NewGuid().ToString("N"), ["origine"] = "desk",
            ["allergies"] = new JsonArray(allergyBox.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (JsonNode)(string)c.Tag).ToArray()),
        };
        if (chosen.Count > 1) data["extra"] = new JsonArray(chosen.Skip(1).Select(x => (JsonNode)x.S("id")).ToArray());
        if (client != null) { data["contact"] = client.S("id"); data["first_name"] = client.S("nome"); data["last_name"] = client.S("cognome"); data["email"] = client.S("email"); data["phone"] = client.S("telefono"); }
        else { data["first_name"] = first.Text; data["last_name"] = last.Text; data["phone"] = phone.Text; data["email"] = email.Text; data["marketing"] = marketing.IsChecked == true; }
        if (force.IsChecked == true)
        {
            if (!TimeSpan.TryParse(forceTime.Text.Trim(), CultureInfo.InvariantCulture, out var t)) { Say("Scrivi l'ora da forzare, per esempio 10:30."); return; }
            data["force"] = true; data["resource"] = resource;
            data["start"] = date.SelectedDate.Value.Date.Add(t).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            if (slot == "") { Say("Scegli un orario libero."); return; }
            var s = JsonNode.Parse(slot);
            data["start"] = s.S("start"); data["resource"] = resource == "" ? "" : s.S("resource"); data["occurrence"] = s.S("occurrence");
            if (resource == "" && chosen.Count == 1) data["resource"] = "";
        }
        var r = await WithForce("prenotazione.nuova", data);
        if (r != null) DialogResult = true;
    }
}
