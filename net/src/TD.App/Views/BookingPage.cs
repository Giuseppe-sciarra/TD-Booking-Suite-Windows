using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/booking.html with static/booking.js, at the desk: Nuova prenotazione, Senza appuntamento (walkin)
/// and Scegli un nuovo orario (moving). The client first, then who, how many and when, Forza, the free times, the
/// service chosen from one list (every pick after the first adds a service to the same visit), notes and questions.</summary>
public sealed class BookingPage : Page
{
    public string PrefillResource = "", PrefillDate = "", PrefillTime = "", PrefillClient = "";
    public bool Walkin;
    /// <summary>Moving: the booking's detail (prenotazione.dettaglio), with its «modifica».</summary>
    public JsonNode Moving;
    public Action Done;

    JsonNode config;
    List<JsonNode> services = new();
    string main = "";
    readonly List<(string Id, string Name)> extras = new();
    JsonNode selected;
    bool busy, forceOk, unpinned, loading;
    int sequence;
    string clientId = "";

    readonly ComboBox service = new(), option = new(), resource = new(), area = new();
    readonly TextBox party = Box("1"), first = Box(""), last = Box(""), phone = Box(""), email = Box(""), notes = Box("", true), search = Box(""), forceAt = Box("");
    readonly DatePicker day = new(), departure = new();
    readonly CheckBox force = new();
    readonly WrapPanel slots = new() { Margin = new Thickness(0, 14, 0, 0) }, visitList = new() { Margin = new Thickness(0, 12, 0, 0) }, allergens = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock status = T("", size: 14), serviceLabel = F.Label("Servizio"), partyLabel = F.Label("Persone"), resourceLabel = F.Label("Operatore / risorsa"), dateLabel = F.Label("Data"),
        serviceDescription = T("", size: 13), resourceDescription = T("", size: 13), submitText = new();
    readonly StackPanel serviceWrap = new(), optionWrap = new(), visitWrap = new(), resourceWrap = new(), partyWrap = new(), areaWrap = new(), departureWrap = new(), dateWrap = new(), forceTime = new(), fields = new(), results = new(), awayBox = new(), allergensBox = new();
    readonly Border chosen = new(), error = new(), forceBox = new(), resultsBox = new();
    readonly TextBlock chosenText = T("", size: 14, bold: true), errorText = T("", size: 13.5);
    readonly Button submit = new() { Style = Style("Primary") };
    readonly Dictionary<string, (FrameworkElement Input, FrameworkElement Wrap, JsonNode Field)> answers = new();
    string label = "Conferma prenotazione";
    string requestKey = Guid.NewGuid().ToString("N");
    bool Editing => Moving != null;

    string Val(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    static void Fill(ComboBox c, IEnumerable<(string Id, string Name)> items, string first)
    {
        var list = new List<ComboBoxItem>();
        if (first != null) list.Add(new ComboBoxItem { Content = first, Tag = "" });
        list.AddRange(items.Select(i => new ComboBoxItem { Content = i.Name, Tag = i.Id }));
        c.ItemsSource = list; c.SelectedIndex = list.Count > 0 ? 0 : -1;
    }
    void Set(ComboBox c, string id) { var i = c.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == id); if (i != null) c.SelectedItem = i; }
    JsonNode Current => services.FirstOrDefault(s => s.S("id") == (main != "" ? main : Val(service)));
    bool Forcing => force.IsChecked == true && Current?.S("tipo") != "day";
    bool Typed => !Walkin && Forcing && Current?.S("tipo") is "resource" or "capacity";
    string DayIso => day.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
    string DepartureIso => departure.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
    AgendaPage zoneHelper;
    DateTime Local(string iso) => zoneHelper.Local(iso);

    public override async Task Load()
    {
        zoneHelper = new AgendaPage { Main = Main }; zoneHelper.UseZone(Business.S("fuso"));
        config = await Api.Current.Call("prenotazione.catalogo");
        services = config.A("servizi").ToList();
        if (Walkin) services = services.Where(s => s.S("tipo") is "resource" or "capacity").ToList();
        Build();
        Start();
    }

    void Build()
    {
        label = Editing ? "Conferma nuovo orario" : Walkin ? "Inizia adesso" : "Conferma prenotazione";
        var word = config.S("servizio"); if (word == "") word = "Servizio";
        var intro = Walkin ? "Chi entra adesso: servizio e con chi. Nome e recapiti sono facoltativi" : Editing ? "" : "Operatore, giorno e orario, poi i servizi: uno dopo l'altro dalla stessa lista";
        // ----- Cliente
        Border clientSet = null;
        if (!Editing)
        {
            var pick = Col();
            if (config.Bo("cercaClienti"))
            {
                search.Tag = "Nome, cognome, telefono o email"; Hint(search);
                resultsBox.Child = results; resultsBox.Visibility = Visibility.Collapsed; resultsBox.Padding = new Thickness(6); resultsBox.Margin = new Thickness(0, 6, 0, 0); resultsBox.CornerRadius = new CornerRadius(12); resultsBox.BorderThickness = new Thickness(1);
                resultsBox.SetResourceReference(Border.BorderBrushProperty, "Line"); resultsBox.SetResourceReference(Border.BackgroundProperty, "Surface");
                var clear = new Button { Content = "Cambia cliente", Style = Style("Ghost"), Padding = new Thickness(11, 5, 11, 5), FontSize = 13 };
                clear.Click += (_, _) => { clientId = ""; chosen.Visibility = Visibility.Collapsed; foreach (var b in new[] { first, last, phone, email }) b.Text = ""; search.Focus(); };
                chosen.Child = Row(chosenText, clear.M(12, 0, 0, 0)); ((TextBlock)((StackPanel)chosen.Child).Children[0]).VerticalAlignment = VerticalAlignment.Center;
                chosen.Visibility = Visibility.Collapsed; chosen.CornerRadius = new CornerRadius(10); chosen.Padding = new Thickness(14, 6, 8, 6); chosen.Margin = new Thickness(0, 10, 0, 0);
                chosen.SetResourceReference(Border.BackgroundProperty, "OkSoft");
                pick.Children.Add(F.Label("Cerca un cliente già in rubrica")); pick.Children.Add(search); pick.Children.Add(resultsBox); pick.Children.Add(chosen);
                pick.Children.Add(T("Oppure scrivi qui sotto i dati di un cliente nuovo: la scheda si crea da sola", size: 13, color: Res("Muted")).M(0, 6, 0, 0));
                System.Windows.Threading.DispatcherTimer timer = null; var seq = 0;
                search.TextChanged += (_, _) =>
                {
                    timer?.Stop(); var q = search.Text.Trim();
                    if (q.Length < 2) { resultsBox.Visibility = Visibility.Collapsed; return; }
                    timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    timer.Tick += async (_, _) =>
                    {
                        timer.Stop(); var n = ++seq;
                        try
                        {
                            var data = await Api.Current.Call("clienti.cerca", new JsonObject { ["q"] = q, ["quanti"] = 8 }) as JsonArray;
                            if (n != seq) return;
                            results.Children.Clear();
                            if (data == null || data.Count == 0) results.Children.Add(T("Nessun cliente trovato: scrivi i dati qui sotto", size: 14, color: Res("Muted")).M(12, 10, 12, 10));
                            foreach (var c in data ?? new JsonArray())
                            {
                                var b = new Button { Style = Style("Ghost"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 9, 12, 9),
                                    Content = Col(T($"{c.S("nome")} {c.S("cognome")}".Trim(), size: 14, bold: true), T(string.Join(" · ", new[] { c.S("telefono"), c.S("email") }.Where(x => x != "")), size: 12.5, color: Res("Muted"))) };
                                var cc = c; b.Click += (_, _) => Choose(cc);
                                results.Children.Add(b);
                            }
                            resultsBox.Visibility = Visibility.Visible;
                        }
                        catch (ApiError) { }
                    };
                    timer.Start();
                };
            }
            phone.Tag = "333 123 4567"; Hint(phone);
            foreach (var b in new[] { first, last, phone, email }) b.TextChanged += (_, _) => { if (clientId != "" && !choosing) { clientId = ""; chosen.Visibility = Visibility.Collapsed; } };
            clientSet = F.Fieldset("Cliente", false, pick, F.Grid2(
                F.Field("Nome", first, Walkin ? "(facoltativo: senza, Cliente di passaggio)" : null), F.Field("Cognome", last, "(facoltativo)"),
                F.Field("Telefono", phone), F.Field("Email", email, "(facoltativa: senza, nessuna email di conferma)")).M(0, 10, 0, 0));
        }
        // ----- Servizio, giorno e orario
        resourceWrap.Children.Add(resourceLabel); resourceWrap.Children.Add(resource); resourceWrap.Children.Add(resourceDescription.M(0, 4, 0, 0));
        resourceDescription.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        partyWrap.Children.Add(partyLabel); partyWrap.Children.Add(party); partyWrap.Width = 130;
        areaWrap.Children.Add(F.Label("Dove preferisci sederti?")); areaWrap.Children.Add(area); areaWrap.Width = 170;
        dateWrap.Children.Add(dateLabel); dateWrap.Children.Add(day); dateWrap.Width = 340; if (Walkin) dateWrap.Visibility = Visibility.Collapsed;
        departureWrap.Children.Add(F.Label("Partenza")); departureWrap.Children.Add(departure); departureWrap.Width = 340;
        foreach (var c in new FrameworkElement[] { resource, party, area, day, departure }) c.Height = 46;
        resourceWrap.MinWidth = 200; resourceWrap.Width = 260;
        var whenRow = new WrapPanel();
        foreach (var w in new FrameworkElement[] { resourceWrap, partyWrap, areaWrap, dateWrap, departureWrap }) { w.Margin = new Thickness(0, 0, 22, 0); whenRow.Children.Add(w); }
        var forceText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        forceText.Inlines.Add(new System.Windows.Documents.Run("Forza") { FontWeight = FontWeights.Bold });
        forceText.Inlines.Add(": " + (Walkin ? "anche fuori orario, oltre i posti o sopra un altro appuntamento" : "scegli tu l'ora, anche fuori orario, oltre i posti, sopra un altro appuntamento o nel passato"));
        force.Content = forceText; force.Margin = new Thickness(0, 18, 0, 6);
        forceAt.Width = 110; forceTime.Children.Add(F.Label("Ora")); forceTime.Children.Add(forceAt); forceTime.Visibility = Visibility.Collapsed; forceTime.HorizontalAlignment = HorizontalAlignment.Left;
        forceBox.Child = Col(force, forceTime); forceBox.Visibility = config.Bo("forza") ? Visibility.Visible : Visibility.Collapsed;
        serviceWrap.Children.Add(serviceLabel); serviceWrap.Children.Add(service); serviceWrap.Children.Add(serviceDescription.M(0, 4, 0, 0)); serviceDescription.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        optionWrap.Children.Add(F.Label("Prestazione")); optionWrap.Children.Add(option); optionWrap.Visibility = Visibility.Collapsed;
        visitWrap.Children.Add(visitList); visitWrap.Children.Add(T("Nella stessa visita, uno dopo l'altro: scegli ancora dalla lista per aggiungerne", size: 13, color: Res("Muted")).M(0, 2, 0, 0)); visitWrap.Visibility = Visibility.Collapsed;
        var whatGrid = Col(F.Grid2(serviceWrap, optionWrap), visitWrap);
        status.Margin = new Thickness(0, 12, 0, 4);
        var when = F.Fieldset("Servizio, giorno e orario", Editing, whenRow, awayBox, forceBox, slots, status, whatGrid);
        // ----- Note, allergie, domande
        Border extra = null;
        if (!Editing)
        {
            allergensBox.Children.Add(T("Intolleranze e allergie", size: 14, bold: true)); allergensBox.Children.Add(T("Quelle che il cliente ha segnalato. Il resto va nelle note", size: 13, color: Res("Muted")));
            allergensBox.Children.Add(allergens); allergensBox.Visibility = Visibility.Collapsed;
            foreach (var a in config.A("allergeni")) { allergens.Children.Add(F.Chip(a.ToString())); allergensBox.Visibility = Visibility.Visible; }
            notes.Tag = "Per esempio: " + config.S("nota"); Hint(notes); notes.MinHeight = 76;
            extra = F.Fieldset(null, true, allergensBox, fields, F.Label("Note sulla prenotazione"), notes);
        }
        error.Child = errorText; error.Visibility = Visibility.Collapsed; error.CornerRadius = new CornerRadius(10); error.Padding = new Thickness(14, 8, 14, 8); error.Margin = new Thickness(0, 10, 0, 10); error.BorderThickness = new Thickness(4, 0, 0, 0);
        submit.Content = submitText; submitText.Text = label; submit.IsEnabled = false;
        submit.Click += async (_, _) => await Submit();
        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(submit, Dock.Right); bottom.Children.Add(submit);
        bottom.Children.Add(Col(T("La conferma compare subito qui", size: 14), T("La disponibilità viene ricontrollata quando confermi", size: 13, color: Res("Muted"))));
        var card = F.Card(clientSet, when, extra, error, bottom);
        var back = Editing ? F.Back("La prenotazione", () => Done?.Invoke()) : F.Back("Agenda e prenotazioni", () => Main.Show("agenda"));
        Content = F.Page(860, back, F.Heading(null, Editing ? "Scegli un nuovo orario" : Walkin ? "Senza appuntamento, adesso" : "Nuova prenotazione", intro == "" ? null : intro), card);

        // ----- what the lists hold (booking.js, the start)
        var places = services.Select(s => s.S("sede")).Distinct().Count(); var names = services.Select(s => s.S("nome")).Distinct().Count();
        if (services.Count == 1) { serviceWrap.Visibility = Visibility.Collapsed; Fill(service, services.Select(s => (s.S("id"), s.S("nome"))), null); }
        else if (names == 1 && places > 1) { serviceLabel.Text = "Sede"; status.Text = "Scegli la sede e la data."; Fill(service, services.Select(s => (s.S("id"), s.S("sede"))), "Scegli la sede"); }
        else { serviceLabel.Text = word; Fill(service, services.Select(s => (s.S("id"), (places > 1 ? $"{s.S("nome")} ({s.S("sede")})" : s.S("nome")) + (s.S("prezzo") != "" ? " · " + s.S("prezzo") : ""))), "Scegli un " + word.ToLowerInvariant()); }
        var kinds = services.Select(s => s.S("tipo")).Distinct().ToList();
        if (kinds.Count == 1) partyLabel.Text = kinds[0] is "shift" or "capacity" or "event" ? "Persone" : kinds[0] == "day" ? "Ospiti" : "Persone / quantità";
        var today = DateTime.Parse(config.S("oggi"));
        day.SelectedDate = PrefillDate != "" && string.CompareOrdinal(PrefillDate, config.S("oggi")) >= 0 ? DateTime.Parse(PrefillDate) : today;
        day.DisplayDateStart = today; departure.DisplayDateStart = today;
        partyWrap.Visibility = services.Select(s => Math.Max(1, s.I("maxPersone"))).DefaultIfEmpty(1).Max() <= 1 ? Visibility.Collapsed : Visibility.Visible;
        resourceWrap.Visibility = areaWrap.Visibility = departureWrap.Visibility = Visibility.Collapsed;
        status.Text = Walkin ? $"Inizia adesso, alle {DateTime.Now:HH:mm}." : status.Text == "" ? "Scegli servizio e data." : status.Text;

        service.SelectionChanged += (_, _) => { if (!loading) ServicePicked(); };
        foreach (var c in new[] { option, resource, area }) c.SelectionChanged += async (_, _) => { if (!loading) { if (c == resource) Mark(); await LoadSlots(); } };
        party.LostFocus += async (_, _) => await LoadSlots();
        day.SelectedDateChanged += async (_, _) => { if (!loading) { AwayNote(); await LoadSlots(); } };
        departure.SelectedDateChanged += async (_, _) => { if (!loading) await LoadSlots(); };
        force.Checked += async (_, _) => await LoadSlots(); force.Unchecked += async (_, _) => await LoadSlots();
        forceAt.TextChanged += async (_, _) => { if (Typed) await LoadSlots(); };
    }

    bool choosing;
    void Choose(JsonNode c)
    {
        choosing = true;
        clientId = c.S("id"); first.Text = c.S("nome"); last.Text = c.S("cognome"); phone.Text = c.S("telefono"); email.Text = c.S("email");
        chosenText.Text = "Cliente: " + $"{c.S("nome")} {c.S("cognome")}".Trim() + (c.S("telefono") != "" ? " · " + c.S("telefono") : "");
        chosen.Visibility = Visibility.Visible; resultsBox.Visibility = Visibility.Collapsed; search.Text = "";
        choosing = false;
    }

    async void Start()
    {
        if (Editing)
        {
            var e = Moving["modifica"];
            foreach (var x in e.A("extra")) extras.Add((x.S("id"), x.S("name")));
            loading = true; Set(service, e.S("service")); loading = false; main = e.S("service");
            await ChangeService(true);
            loading = true; Set(option, e.S("option")); Set(resource, e.S("resource")); party.Text = e.S("party"); loading = false;
            if (extras.Count > 0) service.IsEnabled = false;
            day.SelectedDate = Local(Moving.S("inizio")).Date;
            await LoadSlots();
        }
        else if (services.Count == 1) { await ChangeService(); }
        else if (PrefillTime != "") await LoadSlots();
        if (PrefillClient != "")
        {
            try { var c = (await Api.Current.Call("clienti.cerca", new JsonObject { ["id"] = PrefillClient }) as JsonArray)?.FirstOrDefault(); if (c != null) Choose(c); } catch (ApiError) { }
        }
    }

    // ---------- the service list: the first pick is the service, every pick after adds one to the same visit
    async void ServicePicked()
    {
        var s = services.FirstOrDefault(v => v.S("id") == Val(service)); var firstS = services.FirstOrDefault(v => v.S("id") == main);
        if (main != "" && s != null && s.S("id") == main && extras.Count > 0) return;
        if (main != "" && s != null && s.S("id") != main && !Editing && s.S("tipo") == "resource" && firstS != null && s.S("sede") == firstS.S("sede") && extras.Count < 4)
        {
            if (!extras.Any(x => x.Id == s.S("id"))) { extras.Add((s.S("id"), s.S("nome"))); VisitPick(); await LoadSlots(); }
            return;
        }
        await ChangeService();
    }

    void VisitPick()
    {
        var s = Current;
        visitWrap.Visibility = (s?.S("tipo") == "resource" && !Walkin && !Editing) || extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        visitList.Children.Clear();
        if (s != null)
            visitList.Children.Add(F.Pill(s.S("nome"), Editing ? null : async () =>
            {
                if (extras.Count > 0) { main = extras[0].Id; extras.RemoveAt(0); } else main = "";
                loading = true; Set(service, extras.Count > 0 ? extras[^1].Id : main); if (main == "") service.SelectedIndex = 0; loading = false;
                await ChangeService(true);
            }));
        for (int i = 0; i < extras.Count; i++)
        {
            var idx = i;
            visitList.Children.Add(F.Pill("+ " + extras[i].Name, Editing ? null : async () =>
            {
                extras.RemoveAt(idx);
                loading = true; Set(service, extras.Count > 0 ? extras[^1].Id : main); loading = false;
                VisitPick(); await LoadSlots();
            }));
        }
    }

    async Task ChangeService(bool keep = false)
    {
        if (!keep) main = Val(service);
        if (services.Count == 1) main = services[0].S("id");
        var s = Current;
        if (!Editing && !keep) extras.Clear();
        VisitPick();
        loading = true;
        Fill(option, (s?.A("varianti") ?? Enumerable.Empty<JsonNode>()).Select(o => (o.S("id"), o.S("nome") + (o.S("prezzo") != "" ? " · " + o.S("prezzo") : ""))), "Prestazione standard" + (s?.S("prezzo") is { Length: > 0 } pz ? " · " + pz : ""));
        Fill(resource, (s?.A("operatori") ?? Enumerable.Empty<JsonNode>()).Select(r => (r.S("id"), r.S("nome"))), s?.S("tipo") == "day" ? "Qualsiasi unità libera" : "Il primo disponibile");
        if (PrefillResource != "" && s != null && s.A("operatori").Any(r => r.S("id") == PrefillResource)) Set(resource, PrefillResource);
        Fill(area, (s?.A("sale") ?? Enumerable.Empty<JsonNode>()).Select(a => (a.S("id"), a.S("nome"))), "Indifferente");
        loading = false;
        Mark();
        var kind = s?.S("tipo");
        optionWrap.Visibility = s != null && s.A("varianti").Any() ? Visibility.Visible : Visibility.Collapsed;
        resourceWrap.Visibility = kind is "resource" or "day" && s.A("operatori").Any() ? Visibility.Visible : Visibility.Collapsed;
        resourceLabel.Text = s?.S("etichetta") is { Length: > 0 } rl ? rl : kind == "day" ? "Unità / camera" : "Operatore / risorsa";
        departureWrap.Visibility = kind == "day" ? Visibility.Visible : Visibility.Collapsed;
        var most = Math.Max(1, s?.I("maxPersone") ?? 1);
        if (!int.TryParse(party.Text, out var p) || p < 1) party.Text = "1";
        partyWrap.Visibility = most <= 1 ? Visibility.Collapsed : Visibility.Visible;
        partyLabel.Text = kind is "shift" or "capacity" or "event" ? "Persone" : kind == "day" ? "Ospiti" : "Persone / quantità";
        serviceDescription.Text = s?.S("descrizione") ?? ""; serviceDescription.Visibility = serviceDescription.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        areaWrap.Visibility = s != null && s.A("sale").Any() && !Editing ? Visibility.Visible : Visibility.Collapsed;
        dateLabel.Text = kind == "day" ? "Arrivo" : "Data";
        BuildFields(s);
        await LoadSlots();
    }

    void Mark()
    {
        var r = Current?.A("operatori").FirstOrDefault(x => x.S("id") == Val(resource));
        resourceDescription.Text = r?.S("descrizione") ?? ""; resourceDescription.Visibility = resourceDescription.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        AwayNote();
    }

    /// <summary>The operator chosen is fuori ufficio that day: his stand-in offered in his place.</summary>
    void AwayNote()
    {
        awayBox.Children.Clear();
        var r = Current?.A("operatori").FirstOrDefault(x => x.S("id") == Val(resource)); var a = r?["assente"];
        if (a == null || day.SelectedDate == null) return;
        var from = DateTime.Parse(a.S("dal")).Date; var to = DateTime.Parse(a.S("al")).Date; var d = day.SelectedDate.Value.Date;
        if (d < from || d > to) return;
        var row = Row(T($"{r.S("nome")} è fuori ufficio fino al {to:dd/MM/yyyy}.", size: 13.5));
        if (a.S("sostituto") != "" && Current.A("operatori").Any(x => x.S("id") == a.S("sostituto")))
        {
            var b = new Button { Content = "Prenota con " + a.S("sostitutoNome"), Padding = new Thickness(11, 5, 11, 5), FontSize = 13, Margin = new Thickness(10, 0, 0, 0) };
            b.Click += (_, _) => Set(resource, a.S("sostituto"));
            row.Children.Add(b);
        }
        awayBox.Children.Add(F.Notice(row));
    }

    // ---------- the service's own questions, with the ones shown only for an answer to an earlier one
    void BuildFields(JsonNode s)
    {
        fields.Children.Clear(); answers.Clear();
        if (Editing) return;
        var cells = new List<FrameworkElement>();
        void Flush() { if (cells.Count > 0) { fields.Children.Add(F.Grid2(cells.ToArray())); cells.Clear(); } }
        foreach (var f in s?.A("campi") ?? Enumerable.Empty<JsonNode>())
        {
            var key = f.S("key"); var lab = f.S("label") + (f.Bo("required") ? " *" : "");
            if (f.S("type") == "checkbox")
            {
                Flush();
                var box = F.Check(new TextBlock { Text = lab, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
                box.Checked += (_, _) => Conditions(); box.Unchecked += (_, _) => Conditions();
                answers[key] = (box, box, f); fields.Children.Add(box); continue;
            }
            FrameworkElement input;
            if (f.S("type") == "select") { var c = new ComboBox(); Fill(c, f.A("choices").Select(x => (x.ToString(), x.ToString())), "Seleziona"); c.SelectionChanged += (_, _) => Conditions(); input = c; }
            else { var t = Box(""); t.MaxLength = 500; t.TextChanged += (_, _) => Conditions(); input = t; }
            var wrap = F.Field(lab, input);
            answers[key] = (input, wrap, f); cells.Add(wrap);
        }
        Flush(); Conditions();
    }
    string Answer(string key)
    {
        if (!answers.TryGetValue(key, out var a) || a.Wrap.Visibility != Visibility.Visible) return "";
        return a.Input switch { CheckBox c => c.IsChecked == true ? "sì" : "no", ComboBox c => Val(c), TextBox t => t.Text, _ => "" };
    }
    void Conditions()
    {
        foreach (var (_, a) in answers)
            if (a.Field["show_if"] is JsonObject cond) a.Wrap.Visibility = Answer(cond.S("key")) == cond.S("value") ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- the free times
    bool Pinned => PrefillTime != "" && !unpinned && DayIso == PrefillDate && (PrefillResource == "" || Val(resource) == "" || Val(resource) == PrefillResource);

    ToggleButton AddSlot(JsonNode slot, string time, string sub)
    {
        var b = F.Slot(time, sub); b.Tag = slot;
        b.Click += (_, _) =>
        {
            foreach (var o in slots.Children.OfType<ToggleButton>()) o.IsChecked = o == b;
            selected = slot; forceOk = false; submitText.Text = label; submit.IsEnabled = !busy;
        };
        slots.Children.Add(b); return b;
    }

    async Task LoadSlots()
    {
        if (config == null || loading) return;
        var was = selected?.S("start") ?? ""; var tall = slots.ActualHeight;
        var seq = ++sequence; selected = null; submit.IsEnabled = false; slots.Children.Clear();
        var s = Current; forceOk = false; submitText.Text = label;
        forceTime.Visibility = Typed ? Visibility.Visible : Visibility.Collapsed;
        if (config.Bo("forza")) forceBox.Visibility = s?.S("tipo") == "day" ? Visibility.Collapsed : Visibility.Visible;
        day.DisplayDateStart = Forcing ? null : DateTime.Parse(config.S("oggi"));
        if (Walkin) { if (s != null) { selected = new JsonObject { ["start"] = "" }; submit.IsEnabled = !busy; } return; }
        if (Typed)
        {
            if (DayIso != "" && TimeSpan.TryParse(forceAt.Text.Trim(), CultureInfo.InvariantCulture, out var tt))
            { selected = new JsonObject { ["start"] = $"{DayIso}T{tt:hh\\:mm}", ["resource"] = "", ["occurrence"] = "" }; submit.IsEnabled = !busy; }
            status.Text = "Scrivi giorno e ora: quando confermi il programma dice cosa stai superando."; return;
        }
        if (s == null && Pinned)
        {
            var b = F.Slot(PrefillTime, null); b.IsChecked = true; b.IsHitTestVisible = false; slots.Children.Add(b);
            var who = services.SelectMany(x => x.A("operatori")).FirstOrDefault(r => r.S("id") == PrefillResource)?.S("nome");
            status.Text = "Orario scelto dall'agenda" + (who != null ? " con " + who : "") + ": ora scegli il servizio."; return;
        }
        if (s == null || DayIso == "" || s.S("tipo") == "day" && DepartureIso == "")
        {
            status.Text = serviceLabel.Text == "Sede" ? "Scegli la sede e la data." : s != null && s.S("tipo") == "day" ? "Scegli le date per vedere le disponibilità." : serviceWrap.Visibility != Visibility.Visible ? "Scegli la data per vedere gli orari liberi." : "Scegli servizio e data per vedere gli orari liberi.";
            return;
        }
        status.Text = "Controllo delle disponibilità…"; slots.MinHeight = tall;
        try
        {
            var args = new JsonObject { ["service"] = s.S("id"), ["data"] = DayIso, ["option"] = Val(option), ["resource"] = Val(resource), ["party"] = party.Text.Trim(), ["end_date"] = DepartureIso, ["area"] = Val(area) };
            if (Forcing) args["forza"] = true;
            if (extras.Count > 0) args["extra"] = string.Join(",", extras.Select(x => x.Id));
            if (Editing) args["prenotazione"] = Moving.S("id");
            var result = await Api.Current.Call("prenotazione.orari", args);
            if (seq != sequence) return;
            var all = result.A("slots").ToList();
            var units = s.S("tipo") == "day" && s.A("operatori").Any();
            var shown = Val(resource) != "" || units ? all : all.GroupBy(v => v.S("start")).Select(g => g.First()).ToList();
            foreach (var slot in shown)
            {
                var sub = units ? slot.S("resourceName") + (slot.I("capacity") > 0 ? $" · fino a {slot.I("capacity")} ospiti" : "")
                    : Val(resource) != "" ? slot.S("resourceName") : s.S("tipo") == "capacity" && !s.Bo("turnover") ? "" : "fino alle " + Local(slot.S("end")).ToString("HH:mm");
                AddSlot(slot, Local(slot.S("start")).ToString("HH:mm"), sub);
            }
            var said = false; var buttons = slots.Children.OfType<ToggleButton>().ToList();
            if (Pinned && selected == null)
            {
                said = true;
                var hit = buttons.FirstOrDefault(b => Local(((JsonNode)b.Tag).S("start")).ToString("HH:mm") == PrefillTime);
                if (hit != null)
                {
                    hit.IsChecked = true; selected = (JsonNode)hit.Tag; submit.IsEnabled = !busy;
                    foreach (var b in buttons) b.Visibility = b == hit ? Visibility.Visible : Visibility.Collapsed;
                    var more = new Button { Content = "Altri orari", Style = Style("Ghost"), Padding = new Thickness(11, 5, 11, 5), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                    more.Click += (_, _) => { unpinned = true; foreach (var b in buttons) b.Visibility = Visibility.Visible; slots.Children.Remove(more); status.Text = "Scegli uno degli orari disponibili."; };
                    slots.Children.Add(more); status.Text = "Orario scelto dall'agenda.";
                }
                else status.Text = $"Le {PrefillTime} non è libero per questo servizio: scegli un altro orario.";
            }
            if (!said && was != "")
            {
                var b = buttons.FirstOrDefault(x => ((JsonNode)x.Tag).S("start") == was);
                if (b != null) { b.IsChecked = true; selected = (JsonNode)b.Tag; submit.IsEnabled = !busy; }
            }
            if (!said) status.Text = all.Count > 0 ? (units ? "Scegli l'unità che preferisci." : "Scegli uno degli orari disponibili.") : units ? "Nessuna unità libera per tutte le date scelte." : "Nessun orario disponibile: prova un'altra data.";
        }
        catch (NeedsLogin) { Shell.Instance.SignedOut(); }
        catch (ApiError e) { if (seq == sequence) status.Text = e.Message; }
        finally { if (seq == sequence) slots.MinHeight = 0; }
    }

    void Error(string text, bool warning = false)
    {
        errorText.Text = text; error.Visibility = Visibility.Visible;
        error.SetResourceReference(Border.BackgroundProperty, warning ? "WarnSoft" : "DangerSoft"); error.SetResourceReference(Border.BorderBrushProperty, warning ? "Warn" : "Danger");
        error.BringIntoView();
    }

    async Task Submit()
    {
        if (busy || selected == null) return;
        var s = Current;
        if (!Editing && !Walkin && first.Text.Trim() == "") { Error("Scrivi almeno il nome del cliente."); first.Focus(); return; }
        foreach (var (_, a) in answers)
            if (a.Field.Bo("required") && a.Wrap.Visibility == Visibility.Visible && (a.Input is CheckBox cb ? cb.IsChecked != true : Answer(a.Field.S("key")) == "")) { Error("Compila: " + a.Field.S("label")); return; }
        busy = true; submit.IsEnabled = false; error.Visibility = Visibility.Collapsed;
        try
        {
            var payload = new JsonObject
            {
                ["service"] = s.S("id"), ["option"] = Val(option), ["resource"] = Val(resource) != "" ? Val(resource) : selected.S("resource"), ["start"] = selected.S("start"),
                ["occurrence"] = selected.S("occurrence"), ["end_date"] = DepartureIso, ["party"] = party.Text.Trim(), ["area"] = Val(area),
            };
            if (!Editing)
            {
                var ans = new JsonObject();
                foreach (var (k, a) in answers)
                    if (a.Wrap.Visibility == Visibility.Visible) ans[k] = a.Input switch { CheckBox c => c.IsChecked == true, ComboBox c => Val(c), TextBox t => t.Text, _ => "" };
                payload["first_name"] = first.Text; payload["last_name"] = last.Text; payload["email"] = email.Text; payload["phone"] = phone.Text; payload["notes"] = notes.Text;
                payload["answers"] = ans; payload["marketing"] = false; payload["notices"] = true; payload["channel"] = "";
                payload["allergies"] = new JsonArray(allergens.Children.OfType<ToggleButton>().Where(b => b.IsChecked == true).Select(b => (JsonNode)(string)b.Tag).ToArray());
                if (clientId != "") payload["contact"] = clientId;
            }
            if (Walkin) payload["walkin"] = true;
            if (extras.Count > 0) { payload["extra"] = new JsonArray(extras.Select(x => (JsonNode)x.Id).ToArray()); payload["visit"] = true; }
            if (Forcing) { payload["force"] = true; if (forceOk) payload["force_ok"] = true; }
            payload["request_key"] = requestKey;
            if (Editing) payload["id"] = Moving.S("id");
            var result = await Api.Current.Call(Editing ? "prenotazione.sposta" : "prenotazione.nuova", payload);
            requestKey = Guid.NewGuid().ToString("N");
            if (Editing) { Main.Flash("Prenotazione spostata."); Done?.Invoke(); }
            else Main.ShowPage(new BookingDetailPage(result.S("id")));
        }
        catch (ApiWarning w) { forceOk = true; submitText.Text = "Conferma e forza"; Error(w.Message, true); }
        catch (NeedsLogin) { Shell.Instance.SignedOut(); }
        catch (ApiError e) { Error(e.Message); requestKey = Guid.NewGuid().ToString("N"); }
        finally { busy = false; submit.IsEnabled = selected != null; }
    }

    public static void Hint(TextBox box) => AgendaPage.Hint(box);
}
