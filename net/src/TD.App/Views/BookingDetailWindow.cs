using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>A booking: who, what, when, its status and what can be done with it (portal.booking_detail).</summary>
public sealed class BookingDetailWindow : Window
{
    readonly AgendaPage agenda; readonly string id; JsonNode b;

    public static async Task Open(AgendaPage page, string id)
    {
        var w = new BookingDetailWindow(page, id);
        await w.Reload();
        w.ShowDialog();
        await page.Refresh();
    }

    internal BookingDetailWindow(AgendaPage page, string bookingId)
    {
        agenda = page; id = bookingId;
        Title = "Prenotazione"; Width = 760; Height = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = Application.Current.MainWindow;
    }

    internal async Task Reload()
    {
        b = await Api.Current.Call("prenotazione.dettaglio", new { id });
        Draw();
    }

    bool Can(string p) => agenda.Can(p);

    void Draw()
    {
        var d = b["dati"]; var a = agenda.Local(b.S("inizio")); var z = agenda.Local(b.S("fine"));
        var name = $"{d.S("first_name")} {d.S("last_name")}".Trim();
        var head = Col(
            T(STATUS.GetValueOrDefault(b.S("stato"), b.S("stato")).ToUpperInvariant(), size: 12, bold: true, color: b.S("stato") switch { "cancelled" or "no_show" => Res("Danger"), "pending" => Res("Warn"), _ => Res("Brand") }),
            T((name == "" ? "Cliente di passaggio" : name) + (b["contatto"]?.Bo("vip") == true ? "  ★" : ""), "H1"),
            T(AgendaPage.It.TextInfo.ToTitleCase(a.ToString("dddd d MMMM yyyy", AgendaPage.It)) + $" · {a:HH:mm}–{z:HH:mm}", size: 15));
        var info = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); info.ColumnDefinitions.Add(new ColumnDefinition());
        void Line(string k, string v) { if (string.IsNullOrWhiteSpace(v)) return; var r = info.RowDefinitions.Count; info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var l = T(k, "MutedText").M(0, 3, 8, 3); var t = T(v).M(0, 3, 0, 3); Grid.SetRow(l, r); Grid.SetRow(t, r); Grid.SetColumn(t, 1); info.Children.Add(l); info.Children.Add(t); }
        Line("Servizio", b.S("servizio") + (b.S("variante") != "" ? " · " + b.S("variante") : ""));
        Line("Con", b.S("operatore")); Line("Cabina", b.S("cabina")); Line("Sede", b.S("sede"));
        if (b.I("persone") > 1) Line("Persone", b.I("persone").ToString());
        Line("Tavoli", b.S("tavoli"));
        Line("Prezzo", b.S("totale") != "" ? Euro(b.S("totale")) + (b.S("sconto") != "" ? $" (sconto {b.S("sconto")}, listino {Euro(b.S("prezzo"))})" : "") : "");
        Line("Telefono", d.S("phone")); Line("Email", d.S("email"));
        Line("Note", d.S("notes"));
        if (d["allergies"] is JsonArray al && al.Count > 0) Line("Allergie", string.Join(", ", al));
        foreach (var (k, v) in (d["answers"] as JsonObject ?? new JsonObject())) Line(b["campi"]?[k]?.ToString() ?? k, v is JsonValue jv && jv.TryGetValue<bool>(out var bb) ? (bb ? "sì" : "no") : v?.ToString());
        Line("Motivo", b.S("motivo") + (d.S("cancel_note") != "" ? " · " + d.S("cancel_note") : ""));
        if (b["forzata"] is JsonArray fz && fz.Count > 0) Line("Forzata", string.Join(", ", fz));
        Line("Arrivata da", b.S("origine") switch { "public" => "pagina di prenotazione", "wordpress" => "sito", "desk" => "banco", "agenda" => "agenda del sito", "walkin" => "senza appuntamento", "import" => "programma precedente", var o => o });
        var visit = b.A("visita").ToList();
        var visitBox = visit.Count > 1 ? Col(T("Visita con più servizi", size: 13, bold: true).M(0, 12, 0, 4)) : null;
        foreach (var v in visit) visitBox?.Children.Add(T($"{agenda.Local(v.S("inizio")):HH:mm}–{agenda.Local(v.S("fine")):HH:mm}  {v.S("servizio")} · {v.S("operatore")}"));

        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (Can("booking.write"))
        {
            foreach (var to in b.A("passaggi").Select(x => x.ToString()))
            {
                if (to == "cancelled") continue;
                var label = to == "confirmed" && b.S("stato") is "completed" or "no_show" or "cancelled" ? "Rimetti confermata" : MOVE_LABEL.GetValueOrDefault(to, to);
                actions.Children.Add(BA(label, () => Status(to), to is "arrived" or "in_progress" or "completed" || to == "confirmed" && b.S("stato") == "pending" ? "Primary" : null).M(0, 0, 8, 8));
            }
            if (b.Bo("annulla")) actions.Children.Add(BA("Torna indietro", async () => { await Api.Current.Call("prenotazione.stato", new { id, annulla = true }); await Reload(); }, "Ghost").M(0, 0, 8, 8));
            if (b.Bo("spostabile")) actions.Children.Add(B("Sposta", Move).M(0, 0, 8, 8));
            if (b.Bo("spostabile")) actions.Children.Add(B("Ripeti", Repeat).M(0, 0, 8, 8));
            if (b.A("passaggi").Any(x => x.ToString() == "cancelled")) actions.Children.Add(B(b.S("stato") == "pending" ? "Rifiuta" : "Annulla prenotazione", Cancel, "DangerButton").M(0, 0, 8, 8));
        }
        if (b.S("whatsapp") != "") actions.Children.Add(B("WhatsApp", () => Process.Start(new ProcessStartInfo(b.S("whatsapp")) { UseShellExecute = true })).M(0, 0, 8, 8));

        var hist = Col(T("Storia", size: 13, bold: true).M(0, 16, 0, 4));
        foreach (var h in b.A("storia")) hist.Children.Add(T($"{agenda.Local(h.S("quando")):dd/MM HH:mm}  {Action(h.S("azione"))} · {h.S("chi")}", "MutedText"));
        Content = new ScrollViewer { Content = Col(head, info, visitBox, actions, hist).M(28), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static string Euro(string v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d.ToString("#,##0.00", CultureInfo.GetCultureInfo("it-IT")) + " €" : v;
    static string Action(string a) => a switch
    {
        "booking.created" => "creata", "booking.moved" => "spostata", "booking.resized" => "durata cambiata", "booking.cancelled" => "annullata", "booking.confirmed" => "confermata",
        "booking.arrived" => "arrivato", "booking.in_progress" => "in corso", "booking.completed" => "completata", "booking.no_show" => "non presentato", "booking.undo" => "tornata indietro",
        "booking.series" => "ripetuta", var x => x,
    };

    async Task Status(string to)
    {
        await Api.Current.Call("prenotazione.stato", new { id, stato = to });
        await Reload();
    }

    void Cancel()
    {
        var reasons = new ComboBox { ItemsSource = b.A("motivi").Select(r => new ComboBoxItem { Content = r.S("nome"), Tag = r.S("id") }).ToList(), SelectedIndex = 0 };
        if (b.S("stato") == "pending") reasons.ItemsSource = new List<ComboBoxItem> { new() { Content = "Richiesta rifiutata", Tag = "rejected" } };
        reasons.SelectedIndex = 0;
        var note = Box("", true);
        var w = new Window { Title = "Annulla prenotazione", Width = 440, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        w.Content = Col(T("Perché si annulla?", "H2"), Field("Motivo", reasons), Field("Nota (facoltativa, la vede solo lo staff)", note),
            Row(BA("Annulla la prenotazione", async () =>
            {
                await Api.Current.Call("prenotazione.stato", new { id, stato = "cancelled", motivo = (string)((ComboBoxItem)reasons.SelectedItem).Tag, nota = note.Text });
                w.DialogResult = true;
            }, "DangerButton"), B("Indietro", () => w.DialogResult = false, "Ghost").M(8, 0, 0, 0))).M(24);
        if (w.ShowDialog() == true) _ = Guard(Reload);
    }

    void Move()
    {
        var w = new MoveWindow(agenda, b);
        if (w.ShowDialog() == true) _ = Guard(Reload);
    }

    void Repeat()
    {
        var every = new ComboBox { ItemsSource = new[] { "Ogni settimana", "Ogni 2 settimane", "Ogni 3 settimane", "Ogni 4 settimane" }, SelectedIndex = 0 };
        var until = new DatePicker { SelectedDate = agenda.Local(b.S("inizio")).AddMonths(2) };
        var list = Col();
        var w = new Window { Title = "Ripeti", Width = 480, Height = 560, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Button make = null;
        make = BA("Crea le date libere", async () =>
        {
            var r = await Api.Current.Call("prenotazione.serie", new { id, ogni = every.SelectedIndex + 1, fino = until.SelectedDate?.ToString("yyyy-MM-dd"), crea = true });
            Say(r.I("fatte") > 0 ? $"Serie creata: {r.I("fatte")} date in più." : "Nessuna data libera da aggiungere."); w.DialogResult = true;
        }, "Primary");
        make.IsEnabled = false;
        var check = BA("Controlla le date", async () =>
        {
            var r = await Api.Current.Call("prenotazione.serie", new { id, ogni = every.SelectedIndex + 1, fino = until.SelectedDate?.ToString("yyyy-MM-dd") });
            list.Children.Clear(); var free = 0;
            foreach (var x in r.A("date"))
            {
                var why = x.S("perche"); if (why == "") free++;
                list.Children.Add(T($"{agenda.Local(x.S("inizio")):ddd dd/MM/yyyy HH:mm}  " + (why == "" ? "libera" : why), why == "" ? null : "Muted"));
            }
            make.IsEnabled = free > 0;
        });
        w.Content = new ScrollViewer { Content = Col(T("Ripeti questo appuntamento", "H2"), Field("Ogni", every), Field("Fino al", until), Row(check, make.M(8, 0, 0, 0)), list.M(0, 12, 0, 0)).M(24) };
        if (w.ShowDialog() == true) _ = Guard(Reload);
    }
}

/// <summary>Sposta: the same booking on another day, time or operator, from the free times (Forza for the others).</summary>
public sealed class MoveWindow : Window
{
    readonly AgendaPage agenda; readonly JsonNode b; readonly DatePicker date = new(); readonly ComboBox ops = new(); readonly StackPanel slots = new();
    JsonNode catalog; string slot = "";
    public MoveWindow(AgendaPage page, JsonNode booking)
    {
        agenda = page; b = booking;
        Title = "Sposta"; Width = 560; Height = 620; Owner = Application.Current.MainWindow; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Loaded += async (_, _) => await Guard(Init);
    }
    async Task Init()
    {
        catalog = await Api.Current.Call("prenotazione.catalogo");
        var service = catalog.A("servizi").FirstOrDefault(s => s.S("id") == b.S("serviceId"));
        var items = new List<ComboBoxItem> { new() { Content = "Il primo disponibile", Tag = "" } };
        items.AddRange((service?.A("operatori") ?? Enumerable.Empty<JsonNode>()).Select(o => new ComboBoxItem { Content = o.S("nome"), Tag = o.S("id") }));
        ops.ItemsSource = items; ops.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == b.S("resourceId")) ?? items[0];
        date.SelectedDate = agenda.Local(b.S("inizio")).Date;
        date.SelectedDateChanged += async (_, _) => await Guard(Load);
        ops.SelectionChanged += async (_, _) => await Guard(Load);
        var go = BA("Sposta qui", async () =>
        {
            if (slot == "") { Say("Scegli un orario."); return; }
            var s = JsonNode.Parse(slot);
            var args = new JsonObject { ["id"] = b.S("id"), ["service"] = b.S("serviceId"), ["option"] = b.S("optionId"), ["resource"] = (string)((ComboBoxItem)ops.SelectedItem).Tag == "" ? s.S("resource") : (string)((ComboBoxItem)ops.SelectedItem).Tag, ["party"] = b.I("persone"), ["start"] = s.S("start"), ["visit"] = b.A("visita").Count() > 1 };
            if (await WithForce("prenotazione.sposta", args) != null) DialogResult = true;
        }, "Primary");
        Content = new DockPanel { Margin = new Thickness(24), Children = { Dock(Row(go, B("Annulla", () => DialogResult = false, "Ghost").M(8, 0, 0, 0)).M(0, 12, 0, 0)), Col(T("Sposta la prenotazione", "H2"), Field("Operatore", ops), Field("Giorno", date), new ScrollViewer { Content = slots, Height = 330 }) } };
        await Load();
    }
    static UIElement Dock(FrameworkElement e) { DockPanel.SetDock(e, System.Windows.Controls.Dock.Bottom); return e; }
    async Task Load()
    {
        slots.Children.Clear(); slot = "";
        var args = new JsonObject { ["prenotazione"] = b.S("id"), ["service"] = b.S("serviceId"), ["option"] = b.S("optionId"), ["resource"] = (string)((ComboBoxItem)ops.SelectedItem).Tag, ["party"] = b.I("persone"), ["data"] = date.SelectedDate?.ToString("yyyy-MM-dd") };
        var r = await Api.Current.Call("prenotazione.orari", args);
        var wrap = new WrapPanel();
        foreach (var x in r.A("slots"))
        {
            var t = new System.Windows.Controls.Primitives.ToggleButton { Content = agenda.Local(x.S("start")).ToString("HH:mm") + (x.S("resourceName") != "" && (string)((ComboBoxItem)ops.SelectedItem).Tag == "" ? " " + x.S("resourceName") : ""), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 6, 10, 6) };
            var xx = x; t.Checked += (_, _) => { foreach (var o in wrap.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()) if (o != t) o.IsChecked = false; slot = xx.ToJsonString(); };
            wrap.Children.Add(t);
        }
        if (wrap.Children.Count == 0) slots.Children.Add(T("Nessun orario libero in questo giorno.", "MutedText"));
        slots.Children.Add(wrap);
    }
}

/// <summary>Blocca orario: an operator not bookable for a while, once or every week.</summary>
public static class BlockDialog
{
    public static async void Open(AgendaPage page, JsonNode existing, DateOnly day, string resource)
    {
        await Guard(async () =>
        {
            var f = await Api.Current.Call("blocco.dati", new { id = existing?.S("id") });
            var bl = f["blocco"];
            var ops = new ComboBox { ItemsSource = f.A("operatori").Select(o => new ComboBoxItem { Content = o.S("nome"), Tag = o.S("id") }).ToList() };
            ops.SelectedItem = ops.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (bl?.S("operatore") ?? resource)) ?? ops.Items.Cast<ComboBoxItem>().FirstOrDefault();
            var date = new DatePicker { SelectedDate = bl != null ? DateTime.Parse(bl.S("giorno")) : day.ToDateTime(TimeOnly.MinValue) };
            var start = Box(bl?.S("inizio") ?? "13:00"); var end = Box(bl?.S("fine") ?? "14:00");
            var reason = new ComboBox { ItemsSource = f.A("motivi").Select(r => new ComboBoxItem { Content = r.S("nome"), Tag = r.S("id") }).ToList() };
            reason.SelectedItem = reason.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (bl?.S("motivo") ?? "break")) ?? reason.Items[0];
            var note = Box(bl?.S("nota") ?? ""); var weekly = new CheckBox { Content = "Ogni settimana, lo stesso giorno alla stessa ora", IsChecked = bl?.Bo("settimanale") == true };
            var until = new DatePicker { SelectedDate = bl != null && bl.S("fino") != "" ? DateTime.Parse(bl.S("fino")) : null };
            var w = new Window { Title = "Blocca orario", Width = 460, SizeToContent = SizeToContent.Height, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var times = new Grid(); times.ColumnDefinitions.Add(new ColumnDefinition()); times.ColumnDefinitions.Add(new ColumnDefinition());
            var e2 = Field("Fine", end).M(8, 0, 0, 12); Grid.SetColumn(e2, 1); times.Children.Add(Field("Inizio", start)); times.Children.Add(e2);
            var save = BA("Blocca", async () =>
            {
                var args = new JsonObject
                {
                    ["id"] = bl?.S("id"), ["operatore"] = (string)((ComboBoxItem)ops.SelectedItem).Tag, ["giorno"] = date.SelectedDate?.ToString("yyyy-MM-dd"), ["inizio"] = start.Text, ["fine"] = end.Text,
                    ["motivo"] = (string)((ComboBoxItem)reason.SelectedItem).Tag, ["nota"] = note.Text, ["settimanale"] = weekly.IsChecked == true, ["fino"] = until.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
                };
                if (await WithForce("blocco.salva", args) != null) w.DialogResult = true;
            }, "Primary");
            var remove = bl == null ? null : BA("Togli il blocco", async () => { if (Ask("Togliere il blocco? Quell'orario si prenota di nuovo.")) { await Api.Current.Call("blocco.togli", new { id = bl.S("id") }); w.DialogResult = true; } }, "DangerButton").M(8, 0, 0, 0);
            w.Content = Col(T("Blocca orario", "H2"), T("Online e al banco quell'orario non si prenota: non conta come una prenotazione.", "MutedText").M(0, 0, 0, 12),
                Field(f.S("etichetta"), ops), Field("Giorno", date), times, Field("Motivo", reason), Field("Nota (facoltativa)", note), weekly, Field("Fino al (vuoto: senza fine)", until).M(0, 6, 0, 12),
                Row(save, remove, B("Annulla", () => w.DialogResult = false, "Ghost").M(8, 0, 0, 0))).M(24);
            if (w.ShowDialog() == true) await page.Refresh();
        });
    }
}
