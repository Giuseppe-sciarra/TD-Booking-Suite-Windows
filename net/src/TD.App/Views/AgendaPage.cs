using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>The agenda: the day with a column per operator, the week, the month. A free quarter opens a new booking
/// with that operator and time; an appointment is dragged to move it and stretched from its bottom edge.</summary>
public sealed class AgendaPage : Page
{
    public static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    string view = "giorno";
    DateOnly day;
    string location = "", op = "";
    bool cancelled;
    JsonNode data;
    TimeZoneInfo zone = TimeZoneInfo.Local;
    readonly TextBlock title = T("", "H1");
    readonly TextBlock subtitle = T("", "MutedText");
    readonly ContentControl body = new();
    readonly ComboBox places = new() { MinWidth = 150 }, people = new() { MinWidth = 170 };
    const double PX = 26.0 / 15;  // pixels per minute: a quarter of hour is 26 px, as the web agenda

    /// <summary>UseZone, chained: a helper to turn the server's times into the business's local time.</summary>
    public AgendaPage Also(string zoneName) { UseZone(zoneName); return this; }
    public void UseZone(string name) { try { zone = TimeZoneInfo.FindSystemTimeZoneById(name); } catch { } }

    public override async Task Load()
    {
        UseZone(Business.S("fuso"));
        if (day == default) day = DateOnly.Parse(Business.S("oggi"));
        if (Content == null) Build();
        await Refresh();
    }

    readonly TextBlock eyebrow = T("", "Eyebrow");
    readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    readonly WrapPanel barRow = new();
    readonly StackPanel above = new(), below = new();
    readonly TextBox quick = Box("");
    DatePicker picker;
    readonly Dictionary<string, RadioButton> segs = new();

    void Build()
    {
        // .page-heading: eyebrow, h1, p on the left; the actions on the right
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Right); heading.Children.Add(actions);
        subtitle.FontSize = 14;
        heading.Children.Add(Col(eyebrow, title, subtitle));
        // .agenda-bar
        picker = new DatePicker { Width = 150 };
        picker.SelectedDateChanged += async (_, _) => { if (picker.SelectedDate is DateTime d && DateOnly.FromDateTime(d) != day) { day = DateOnly.FromDateTime(d); await Guard(Refresh); } };
        places.SelectionChanged += async (_, _) => { if (places.SelectedItem is ComboBoxItem i && (string)i.Tag != location) { location = (string)i.Tag; op = ""; await Guard(Refresh); } };
        people.SelectionChanged += async (_, _) => { if (people.SelectedItem is ComboBoxItem i && (string)i.Tag != op) { op = (string)i.Tag; await Guard(Refresh); } };
        quick.Width = 190; quick.Tag = "Cerca nel giorno"; Hint(quick);
        quick.TextChanged += (_, _) => Filter();
        var segRow = Row();
        foreach (var (k, label) in new[] { ("giorno", "Giorno"), ("settimana", "Settimana"), ("mese", "Mese"), ("elenco", "Elenco") })
        {
            var r = new RadioButton { Style = Style("Segment"), Content = label, GroupName = "vista" + GetHashCode() };
            r.Click += async (_, _) => { if (k == "elenco") { Main.Show("prenotazioni"); return; } view = k; await Guard(Refresh); };
            segs[k] = r; segRow.Children.Add(r);
        }
        var seg = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(4), Child = segRow };
        seg.SetResourceReference(Border.BackgroundProperty, "Surface"); seg.SetResourceReference(Border.BorderBrushProperty, "Line");
        var help = new Button { Content = "?", Width = 36, Height = 36, Padding = new Thickness(0), Style = Style("Quiet"), ToolTip = "Scorciatoie da tastiera" };
        help.Click += (_, _) => Shortcuts();
        var dayNav = Row(Small("‹", () => Step(-1)), Small("Oggi", async () => { day = DateOnly.Parse(Business.S("oggi")); await Refresh(); }).M(6, 0, 6, 0), Small("›", () => Step(1)));
        var pick = Row(picker, places.M(8, 0, 0, 0), people.M(8, 0, 0, 0), Small("Vai", Refresh).M(8, 0, 0, 0));
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = false };
        DockPanel.SetDock(help, Dock.Right); bar.Children.Add(help);
        DockPanel.SetDock(seg, Dock.Right); bar.Children.Add(seg.M(0, 0, 16, 0));
        foreach (var e in new FrameworkElement[] { dayNav.M(0, 0, 16, 0), pick.M(0, 0, 16, 0), quick }) { e.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(e, Dock.Left); bar.Children.Add(e); }
        var top = Col(heading, bar, above);
        var root = new DockPanel { Margin = new Thickness(26, 20, 26, 16), MaxWidth = 1480 };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(below, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(below); root.Children.Add(body);
        Content = root;
        Focusable = true;
        PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox or PasswordBox or DatePickerTextBox) { if (e.Key == Key.Escape) Focus(); return; }
            switch (e.Key)
            {
                case Key.Left: await Guard(() => Step(-1)); break;
                case Key.Right: await Guard(() => Step(1)); break;
                case Key.T: day = DateOnly.Parse(Business.S("oggi")); await Guard(Refresh); break;
                case Key.N when Can("booking.write"): NewBooking(null, null); break;
                case Key.G when data.Bo("appointments") || view != "giorno": view = "giorno"; await Guard(Refresh); break;
                case Key.S: view = "settimana"; await Guard(Refresh); break;
                case Key.M: view = "mese"; await Guard(Refresh); break;
                case Key.OemQuestion or Key.Divide when Keyboard.Modifiers == ModifierKeys.None: quick.Focus(); e.Handled = true; break;
                case Key.OemQuestion or Key.Oem4: Shortcuts(); break;
            }
        };
    }

    public static void Hint(TextBox box)
    {
        // the placeholder of the web's inputs
        var hint = new TextBlock { Text = (string)box.Tag, IsHitTestVisible = false, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); hint.Opacity = 0.8;
        box.Loaded += (_, _) =>
        {
            if (System.Windows.Documents.AdornerLayer.GetAdornerLayer(box) is not { } layer) return;
            var ad = new HintAdorner(box, hint); ad.Init(); layer.Add(ad);
            void Upd() => ad.Visibility = box.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (_, _) => Upd(); Upd();
        };
    }
    sealed class HintAdorner(UIElement el, TextBlock t) : System.Windows.Documents.Adorner(el)
    {
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int i) => t;
        protected override Size ArrangeOverride(Size s) { t.Arrange(new Rect(s)); return s; }
        protected override Size MeasureOverride(Size c) { t.Measure(c); return base.MeasureOverride(c); }
        public void Init() => AddVisualChild(t);
    }

    Button Small(string text, Func<Task> click) { var b = BA(text, click); b.Padding = new Thickness(11, 5, 11, 5); b.FontSize = 13; b.MinWidth = 40; return b; }

    void Shortcuts()
    {
        var keys = new List<(string, string)> { ("T", "Oggi"), ("← →", "Indietro e avanti") };
        if (Can("booking.write")) keys.Add(("N", "Nuova prenotazione"));
        keys.Add(("/", "Cerca un cliente o un servizio")); keys.Add(("G", "Giorno")); keys.Add(("S", "Settimana")); keys.Add(("M", "Mese")); keys.Add(("?", "Questo elenco")); keys.Add(("Esc", "Chiude, ferma un trascinamento"));
        var list = Col(T("Scorciatoie da tastiera", "H2"));
        foreach (var (k, v) in keys) list.Children.Add(Row(new Border { MinWidth = 44, Child = T(k, size: 13, bold: true) }, T(v)).M(0, 3, 0, 3));
        var w = new Window { Title = "Scorciatoie", SizeToContent = SizeToContent.WidthAndHeight, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        list.Children.Add(B("Chiudi", () => w.Close()).M(0, 12, 0, 0));
        w.Content = list.M(26); w.ShowDialog();
    }

    /// <summary>Cerca nel giorno: the appointments that do not match fade.</summary>
    void Filter()
    {
        var q = quick.Text.Trim().ToLowerInvariant();
        foreach (var (el, text) in searchable) el.Opacity = q == "" || text.Contains(q) ? (el.Tag as double? ?? 1) : 0.18;
    }
    readonly List<(FrameworkElement, string)> searchable = new();

    void Heading(string label, int total, bool writerHint)
    {
        var today = day == DateOnly.Parse(data.S("today") != "" ? data.S("today") : Business.S("oggi"));
        var placeName = data.A("places").Count() > 1 ? data.A("places").FirstOrDefault(p => p.S("id") == data.S("location"))?.S("name") : null;
        var opName = op != "" ? data.A("operators").FirstOrDefault(o => o.S("id") == op)?.S("name") : null;
        eyebrow.Text = (view == "giorno" && today ? "Oggi · " : "") + "Agenda" + (placeName != null ? " · " + placeName : "") + (opName != null ? " · " + opName : "");
        title.Text = label;
        subtitle.Text = (total == 0 ? "Nessun appuntamento" : total == 1 ? "1 appuntamento" : $"{total} appuntamenti") + (writerHint ? ". Clic su uno spazio libero per prenotare in quella colonna a quell'ora" : "");
        actions.Children.Clear();
        if (Can("booking.write"))
        {
            if (view == "giorno" && today) actions.Children.Add(B("Senza appuntamento", () => NewBooking(null, null, true)).M(0, 0, 10, 0));
            if (view == "giorno" && data.A("operators").Any()) actions.Children.Add(B("Blocca orario", () => BlockDialog.Open(this, null, day, op == "" ? null : op)).M(0, 0, 10, 0));
        }
        if (view == "giorno") actions.Children.Add(B("Stampa", () => Say("La stampa del foglio del giorno arriva con i prossimi passaggi.")).M(0, 0, 10, 0));
        if (Can("booking.write") && Can("contacts.read") && Can("pii.read") && view == "giorno") actions.Children.Add(B("Importa", () => Main.ShowPage(new ImportBookingsPage())).M(0, 0, 10, 0));
        if (Can("booking.write")) actions.Children.Add(B("Nuova prenotazione", () => NewBooking(null, null), "Primary"));
        quick.Tag = view == "mese" ? "Cerca nel mese" : view == "settimana" ? "Cerca nella settimana" : "Cerca nel giorno";
        foreach (var (k, r) in segs) r.IsChecked = k == view;
        if (picker.SelectedDate != day.ToDateTime(TimeOnly.MinValue)) picker.SelectedDate = day.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>_agenda_states.html: what the colours and the small signs mean, the cancelled on request, how to drag.</summary>
    void Legend(bool writer)
    {
        below.Children.Clear();
        var w = new WrapPanel { Margin = new Thickness(2, 12, 2, 0) };
        FrameworkElement Item(FrameworkElement sign, string text) { var r = Row(sign, T(text, size: 12.5, color: Res("Muted")).M(6, 0, 14, 0)); r.Margin = new Thickness(0, 0, 0, 6); ((FrameworkElement)r.Children[0]).VerticalAlignment = VerticalAlignment.Center; return r; }
        Border Sw(Brush bg, Brush edge = null, bool dashed = false) => new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = bg, BorderBrush = edge, BorderThickness = new Thickness(edge == null ? 0 : dashed ? 1.5 : 3, 0, 0, 0) };
        w.Children.Add(Item(Sw(Res("WarnSoft"), Res("Warn"), true), "Da confermare"));
        w.Children.Add(Item(Sw(Res("Brand")), "Confermata"));
        w.Children.Add(Item(Sw(Res("Brand"), Res("BrandEdge")), "Arrivato"));
        w.Children.Add(Item(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = Res("Brand"), BorderBrush = Res("Surface"), BorderThickness = new Thickness(2) }, "In corso"));
        w.Children.Add(Item(Sw(Res("OkSoft"), Res("Ok")), "Completata"));
        w.Children.Add(Item(Sw(Res("DangerSoft"), Res("Danger")), "Assente"));
        if (cancelled) w.Children.Add(Item(Sw(Res("Surface"), Res("Muted"), true), "Annullata"));
        w.Children.Add(Item(Sign("✓", "OkSoft", "Ok"), "Confermata dal cliente"));
        w.Children.Add(Item(IconSign("bolt", "DangerSoft", "Danger"), "Forzata al banco"));
        w.Children.Add(Item(Icons.Icon("user", Res("Muted"), 14), "Senza appuntamento"));
        if (Can("contacts.read"))
        {
            w.Children.Add(Item(Icons.Icon("star", Res("Warn"), 14, 1), "Cliente VIP"));
            w.Children.Add(Item(Icons.Icon("absent", Res("Danger"), 14, 1), "Assenze del cliente"));
            w.Children.Add(Item(Icons.Icon("late", Res("Warn"), 14, 1), "Disdette all'ultimo momento"));
        }
        if (Can("notes.read")) w.Children.Add(Item(Sign("!", "Warn", "Surface"), "Allergie o intolleranze"));
        var toggle = T(cancelled ? "Nascondi le annullate" : "Mostra le annullate", size: 12.5, color: Res("Brand")); toggle.FontWeight = FontWeights.Medium; toggle.Cursor = Cursors.Hand;
        toggle.MouseLeftButtonUp += async (_, _) => { cancelled = !cancelled; await Guard(Refresh); };
        w.Children.Add(toggle.M(4, 0, 18, 6));
        if (writer) w.Children.Add(T("Trascina un appuntamento per spostarlo. Il bordo in basso cambia la durata", size: 12.5, color: Res("Muted")));
        below.Children.Add(w);
    }
    static Border Sign(string ch, string bg, string fg, double size = 15) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Res(bg), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
        Child = new TextBlock { Text = ch, FontSize = 10, FontWeight = FontWeights.ExtraBold, Foreground = Res(fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };
    static Border IconSign(string icon, string bg, string fg) => new()
    { Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Res(bg), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Child = Icons.Icon(icon, Res(fg), 10, 1) };

    /// <summary>For the demo's screenshots: the same as choosing the view above the agenda.</summary>
    public async Task SetView(string v) { view = v; await Refresh(); }

    async Task Step(int n)
    {
        day = view == "giorno" ? day.AddDays(n) : view == "settimana" ? day.AddDays(7 * n) : day.AddMonths(n);
        await Refresh();
    }

    public async Task Refresh()
    {
        var args = new JsonObject { ["data"] = day.ToString("yyyy-MM-dd"), ["sede"] = location, ["operatore"] = op, ["annullate"] = cancelled };
        data = await Api.Current.Call("agenda." + view, args);
        if (view == "giorno" && DateOnly.TryParse(data.S("day"), out var shown)) day = shown;
        Fill(places, data.A("places").Select(p => (p.S("id"), p.S("name"))).ToList(), data.S("location"), null);
        places.Visibility = places.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var ops = data.A("operators").Select(o => (o.S("id"), o.S("name"))).ToList();
        Fill(people, ops, op, Business.S("molti") + ": tutti");
        people.Visibility = ops.Count > 1 || op != "" ? Visibility.Visible : Visibility.Collapsed;
        if (view == "giorno") DrawDay();
        else if (view == "settimana") DrawWeek();
        else DrawMonth();
    }

    static void Fill(ComboBox box, List<(string Id, string Name)> items, string chosen, string all)
    {
        var list = new List<ComboBoxItem>();
        if (all != null) list.Add(new ComboBoxItem { Content = all, Tag = "" });
        list.AddRange(items.Select(i => new ComboBoxItem { Content = i.Name, Tag = i.Id }));
        box.ItemsSource = list;
        box.SelectedItem = list.FirstOrDefault(i => (string)i.Tag == (chosen ?? "")) ?? list.FirstOrDefault();
    }

    public DateTime Local(string iso) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.Parse(iso, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), zone);
    string LocalIso(DateTime local)
    {
        var off = zone.GetUtcOffset(local);
        return local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (off < TimeSpan.Zero ? "-" : "+") + off.Duration().ToString(@"hh\:mm");
    }

    // ---------- the day
    void DrawDay()
    {
        var d = day.ToDateTime(TimeOnly.MinValue);
        var long_ = d.ToString("dddd d MMMM yyyy", It);
        var total = data.I("total"); var off = data.A("off").Select(x => x.ToString()).ToList();
        var ops = data.A("operators").ToList();
        Heading(char.ToUpper(long_[0]) + long_[1..], total, data.Bo("writer") && ops.Count > 0);
        above.Children.Clear(); searchable.Clear();
        if (off.Count > 0) above.Children.Add(T($"Non lavora{(off.Count > 1 ? "no" : "")} oggi: {string.Join(", ", off)}", size: 13, color: Res("Muted")).M(0, -4, 0, 12));
        if (data.Bo("closedDay")) above.Children.Add(Notice((op != "" ? ops.FirstOrDefault()?.S("name") + " non lavora in questo giorno." : "Giorno di chiusura: nessuno lavora oggi.") + " Gli orari si cambiano in Orari di apertura"));
        Legend(data.Bo("writer"));
        if (ops.Count == 0)
        {
            body.Content = Col(Card(T($"Per vedere l'agenda di ciascuno serve almeno un {Business.S("uno").ToLowerInvariant()} collegato ai servizi su appuntamento.")));
            below.Children.Clear(); return;
        }
        // the appointments without an operator (made before operators existed) get their own column, as the web
        if (data.A("items").Any(x => !ops.Any(o => o.S("id") == x.S("resource"))))
            ops.Add(new JsonObject { ["id"] = "", ["name"] = "Senza " + Business.S("uno").ToLowerInvariant(), ["color"] = 0, ["open"] = new JsonArray() });
        int first = data.I("firstMinute"), last = data.I("lastMinute");
        var height = (last - first) * PX;
        var writer = data.Bo("writer");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        foreach (var _ in ops) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        var head = new Grid(); foreach (var cd in grid.ColumnDefinitions) head.ColumnDefinitions.Add(new ColumnDefinition { Width = cd.Width, MinWidth = cd.MinWidth });
        var ruler = new Canvas { Height = height };
        for (var m = first; m < last; m += 60)
        {
            var label = T($"{m / 60:00}:00", "MutedText", size: 12);
            Canvas.SetTop(label, (m - first) * PX - 8); Canvas.SetLeft(label, 8);
            ruler.Children.Add(label);
        }
        grid.Children.Add(ruler);
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var items = data.A("items").ToList(); var blocks = data.A("blocks").ToList();
        for (int i = 0; i < ops.Count; i++)
        {
            var o = ops[i]; var color = Op(o.I("color"));
            var h = Row(new Ellipse { Width = 9, Height = 9, Fill = o.I("color") == 0 ? Res("Muted") : color, VerticalAlignment = VerticalAlignment.Center }, T(o.S("name"), size: 14, bold: true).M(7, 0, 0, 0));
            var hb = new Border { Child = h, Padding = new Thickness(12, 10, 12, 10), BorderBrush = Res("Line"), BorderThickness = new Thickness(1, 0, 0, 1) };
            Grid.SetColumn(hb, i + 1); head.Children.Add(hb);
            var col = new Canvas { Height = height, Background = Res("Surface"), ClipToBounds = true, Tag = o.S("id") };
            // closed time in grey, the free quarters clickable
            var open = o.A("open").Select(s => (A: Local(s.S("a")), B: Local(s.S("b")))).ToList();
            for (var m = first; m < last; m += 15)
            {
                var a = d.AddMinutes(m); var b = a.AddMinutes(15);
                var isOpen = open.Any(s => s.A <= a && b <= s.B);
                if (!isOpen) { var r = new Rectangle { Width = 4000, Height = 15 * PX, Fill = Res("Closed") }; Canvas.SetTop(r, (m - first) * PX); col.Children.Add(r); }
            }
            for (var m = first; m < last; m += 60)
            {
                var line = new Rectangle { Width = 4000, Height = 1, Fill = Res("Line") }; Canvas.SetTop(line, (m - first) * PX); col.Children.Add(line);
            }
            if (day < DateOnly.FromDateTime(now) || day == DateOnly.FromDateTime(now))
            {
                var pastMin = day < DateOnly.FromDateTime(now) ? last : Math.Clamp((int)(now - d).TotalMinutes, first, last);
                var past = new Rectangle { Width = 4000, Height = Math.Max(0, (pastMin - first) * PX), Fill = Res("Closed"), Opacity = 0.9, IsHitTestVisible = false };
                Canvas.SetTop(past, 0); col.Children.Add(past);
            }
            var opId = o.S("id");
            col.MouseLeftButtonUp += (s, e) =>
            {
                if (!writer || opId == "" || e.OriginalSource != col && e.OriginalSource is not Rectangle) return;
                var y = e.GetPosition(col).Y; var minute = first + (int)(y / PX) / 15 * 15;
                var at = d.AddMinutes(minute);
                if (at < now.AddMinutes(-15) && !Can("booking.force")) return;
                NewBooking(opId, at);
            };
            foreach (var bl in blocks.Where(x => x.S("resource") == opId))
            {
                var a = Local(bl.S("start")); var b = Local(bl.S("end"));
                var top = Math.Max(0, (a - d).TotalMinutes - first) * PX; var bottom = Math.Min(last - first, (b - d).TotalMinutes - first) * PX;
                var card = new Border
                {
                    Height = Math.Max(14, bottom - top - 2), Background = Res("Closed"), CornerRadius = new CornerRadius(7), Margin = new Thickness(3, 1, 3, 1), Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand,
                    Child = Col(T(bl.S("label"), size: 12.5, bold: true), T($"{a:HH:mm}–{b:HH:mm}" + (bl.Bo("weekly") ? " · ogni settimana" : "") + (bl.S("note") != "" ? " · " + bl.S("note") : ""), size: 12.5, color: Res("Muted"))),
                };
                col.SizeChanged += (_, _) => card.Width = Math.Max(20, col.ActualWidth); card.Width = 400;
                var blockNode = bl;
                card.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (writer) BlockDialog.Open(this, blockNode, day, opId); };
                Canvas.SetTop(card, top); col.Children.Add(card);
            }
            var mine = opId == "" ? items.Where(x => !ops.Any(oo => oo.S("id") != "" && oo.S("id") == x.S("resource"))).ToList() : items.Where(x => x.S("resource") == opId).ToList();
            foreach (var (x, lane, lanes) in Lanes(mine))
                col.Children.Add(Appointment(x, d, first, last, col, lane, lanes, ops, i));
            if (day == DateOnly.FromDateTime(now) && (now - d).TotalMinutes is var nm && nm >= first && nm < last)
            {
                var line = new Rectangle { Width = 4000, Height = 2, Fill = Res("Pink"), IsHitTestVisible = false };
                Canvas.SetTop(line, (nm - first) * PX); col.Children.Add(line);
            }
            var frame = new Border { Child = col, BorderBrush = Res("Line"), BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(frame, i + 1); grid.Children.Add(frame);
        }
        // appointments without an operator (made before operators existed): listed under the grid
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var frameAll = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(14), Background = Res("Surface"), ClipToBounds = true };
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        dock.Children.Add(scroll);
        frameAll.Child = dock;
        body.Content = frameAll;
        // open at the current hour, or at the first booking
        scroll.Loaded += (_, _) =>
        {
            var target = day == DateOnly.FromDateTime(now) ? (now - d).TotalMinutes - 60 : items.Count > 0 ? items.Min(x => (Local(x.S("start")) - d).TotalMinutes) - 30 : first;
            scroll.ScrollToVerticalOffset(Math.Max(0, (target - first) * PX));
        };
    }

    /// <summary>Appointments that overlap side by side: each gets a lane, one alone takes the width.</summary>
    IEnumerable<(JsonNode, int, int)> Lanes(List<JsonNode> items)
    {
        var sorted = items.OrderBy(x => x.S("start")).ThenBy(x => x.S("end")).ToList();
        var ends = new List<string>(); var lane = new Dictionary<JsonNode, int>();
        foreach (var x in sorted)
        {
            var free = ends.FindIndex(e => string.CompareOrdinal(e, x.S("start")) <= 0);
            if (free < 0) { ends.Add(x.S("end")); free = ends.Count - 1; } else ends[free] = x.S("end");
            lane[x] = free;
        }
        foreach (var x in sorted)
        {
            var overl = sorted.Where(y => string.CompareOrdinal(y.S("start"), x.S("end")) < 0 && string.CompareOrdinal(x.S("start"), y.S("end")) < 0).ToList();
            yield return (x, overl.Count == 1 ? 0 : lane[x], overl.Count == 1 ? 1 : overl.Max(y => lane[y]) + 1);
        }
    }

    FrameworkElement Appointment(JsonNode x, DateTime d, int first, int last, Canvas col, int lane, int lanes, List<JsonNode> ops, int opIndex)
    {
        var a = Local(x.S("start")); var b = Local(x.S("end"));
        var top = Math.Max(0, (a - d).TotalMinutes - first) * PX;
        var bottom = Math.Min(last - first, (b - d).TotalMinutes - first) * PX;
        var h = Math.Max(20, bottom - top - 2);
        var color = Op(x.I("color")); var status = x.S("status");
        // .agenda-item: confirmed, arrived and in progress filled with the operator's colour (op-full); a request hatched in
        // amber with a dashed edge; done green, absent red, cancelled dashed and struck
        var full = status is "confirmed" or "arrived" or "in_progress";
        var ink = full ? Brushes.White : status == "cancelled" ? Res("Muted") : Res("Ink");
        var sub = full ? Brushes.White : Res("Muted");
        Brush bg = status switch { "cancelled" => Res("Surface"), "no_show" => Res("DangerSoft"), "completed" => Res("OkSoft"), "pending" => Res("WarnSoft"), _ => color };
        var strong = new WrapPanel { Orientation = Orientation.Horizontal };
        if (x.A("alert").Any()) strong.Children.Add(Sign("!", "Warn", "Surface"));
        if (x.Bo("forced")) strong.Children.Add(IconSign("bolt", "DangerSoft", "Danger"));
        var originIcon = x.S("origin") switch { "public" => "globe", "wordpress" => "plug", "desk" => "shop", "walkin" => "user", "import" => "archive", "agenda" => "day", _ => "" };
        if (originIcon != "") strong.Children.Add(Icons.Icon(originIcon, ink, 14).M(0, 0, 4, 0));
        if (x.Bo("clientOk")) strong.Children.Add(Sign("✓", "OkSoft", "Ok", 16));
        if (x.Bo("vip")) strong.Children.Add(Icons.Icon("star", full ? Brushes.White : Res("Warn"), 13, 1).M(0, 0, 3, 0));
        if (x.I("noShow") > 0) { strong.Children.Add(Icons.Icon("absent", full ? Brushes.White : Res("Danger"), 13, 1)); strong.Children.Add(T(x.I("noShow").ToString(), size: 11, bold: true, color: ink).M(1, 0, 4, 0)); }
        if (x.I("late") > 0) { strong.Children.Add(Icons.Icon("late", full ? Brushes.White : Res("Warn"), 13, 1)); strong.Children.Add(T(x.I("late").ToString(), size: 11, bold: true, color: ink).M(1, 0, 4, 0)); }
        var name = T($"{x.S("from")}–{x.S("to")} {x.S("name")}", size: 12.5, bold: true, color: ink); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
        if (status == "cancelled") name.TextDecorations = TextDecorations.Strikethrough;
        var first1 = new DockPanel(); DockPanel.SetDock(strong, Dock.Left); first1.Children.Add(strong); first1.Children.Add(name);
        var small = new DockPanel();
        if (status != "confirmed")
        {
            var (sb, sf) = status switch { "pending" => ("Warn", "Surface"), "arrived" => ("Surface", "Brand"), "in_progress" => ("Ok", "Surface"), "completed" => ("Surface", "Ok"), "no_show" => ("Danger", "Surface"), _ => ("BrandSoft", "Muted") };
            var chip = new Border { Background = Res(sb), CornerRadius = new CornerRadius(999), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0), Height = 15, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = status == "pending" ? "Da confermare" : status == "no_show" ? "Assente" : STATUS.GetValueOrDefault(status, status), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res(sf), VerticalAlignment = VerticalAlignment.Center } };
            DockPanel.SetDock(chip, Dock.Left); small.Children.Add(chip);
        }
        var svc = T(x.S("service"), size: 12.5, color: sub); svc.TextTrimming = TextTrimming.CharacterEllipsis; svc.TextWrapping = TextWrapping.NoWrap;
        small.Children.Add(svc);
        var shortRow = h < 30;
        FrameworkElement inside;
        if (shortRow) { var one = new DockPanel(); DockPanel.SetDock(first1, Dock.Left); first1.MaxWidth = 9999; one.Children.Add(first1); one.Children.Add(small.M(8, 0, 0, 0)); inside = one; }
        else inside = Col(first1, small);
        var edge = status switch { "pending" => Res("Warn"), "completed" => Res("Ok"), "no_show" => Res("Danger"), "cancelled" => Res("Muted"), _ => color };
        var card = new Border
        {
            Height = h, Background = bg, CornerRadius = new CornerRadius(7), BorderBrush = status == "in_progress" ? Res("Surface") : edge,
            BorderThickness = status == "in_progress" ? new Thickness(2) : full ? new Thickness(status == "arrived" ? 6 : 0, 0, 0, 0) : new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, shortRow ? 0 : 3, 8, shortRow ? 0 : 3), Cursor = Cursors.Hand, ClipToBounds = true, Child = inside, Tag = status == "cancelled" ? 0.75 : 1.0, Opacity = status == "cancelled" ? 0.75 : 1,
            ToolTip = $"{x.S("from")}–{x.S("to")}  {x.S("name")}\n{x.S("service")}" + (x.S("notes") != "" ? "\n" + x.S("notes") : "") + (x.A("alert").Any() ? "\nHa segnalato: " + string.Join(", ", x.A("alert")) : "") + (x.Bo("forced") ? "\nForzata al banco: " + string.Join(", ", x.A("forcedWhy")) : ""),
        };
        if (status == "arrived") card.BorderBrush = Res("BrandEdge");
        searchable.Add((card, (x.S("name") + " " + x.S("service")).ToLowerInvariant()));
        void Place()
        {
            var w = col.ActualWidth > 0 ? col.ActualWidth : 200;
            var lw = (w - 6) / lanes; card.Width = Math.Max(30, lw - 3); Canvas.SetLeft(card, 3 + lane * lw);
        }
        col.SizeChanged += (_, _) => Place(); Place();
        Canvas.SetTop(card, top + 1);
        var id = x.S("id");
        // drag to move, the bottom edge to stretch; a click opens the booking
        bool dragging = false, resizing = false; Point start = default; double origTop = top + 1, origH = h;
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; start = e.GetPosition(col);
            if (!x.Bo("movable") || !Can("booking.write")) { dragging = false; resizing = false; card.CaptureMouse(); return; }
            resizing = e.GetPosition(card).Y > card.ActualHeight - 7; dragging = !resizing;
            origTop = Canvas.GetTop(card); origH = card.Height; card.CaptureMouse();
        };
        card.MouseMove += (_, e) =>
        {
            if (!card.IsMouseCaptured) { card.Cursor = x.Bo("movable") && e.GetPosition(card).Y > card.ActualHeight - 7 ? Cursors.SizeNS : Cursors.Hand; return; }
            var p = e.GetPosition(col); var dy = p.Y - start.Y;
            var snap = Math.Max(5, x.I("snap")) * PX;
            if (resizing) card.Height = Math.Max(snap, Math.Round((origH + dy) / snap) * snap);
            else if (dragging && (Math.Abs(dy) > 4 || Math.Abs(p.X - start.X) > 8)) { Canvas.SetTop(card, Math.Round((origTop + dy) / snap) * snap); card.Opacity = 0.8; Panel.SetZIndex(card, 10); }
        };
        card.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true; var p = e.GetPosition(col); card.ReleaseMouseCapture();
            var moved = Math.Abs(p.Y - start.Y) > 4 || Math.Abs(p.X - start.X) > 8;
            if (!moved || !(dragging || resizing)) { Main.ShowPage(new BookingDetailPage(id)); await Task.CompletedTask; return; }
            var newTop = Canvas.GetTop(card) - 1; var minute = first + (int)Math.Round(newTop / PX);
            var startAt = resizing ? a : d.AddMinutes(minute);
            // dropped on another operator's column
            var target = ops[opIndex].S("id");
            if (dragging && col.Parent is Border fb && fb.Parent is Grid g)
            {
                var gx = e.GetPosition(g).X; double acc = 58;
                for (int k = 1; k < g.ColumnDefinitions.Count; k++) { var cw = g.ColumnDefinitions[k].ActualWidth; if (gx >= acc && gx < acc + cw) { target = ops[k - 1].S("id"); break; } acc += cw; }
            }
            var args = new JsonObject { ["id"] = id, ["start"] = LocalIso(startAt), ["operatore"] = target };
            if (resizing) args["minuti"] = (int)Math.Round(card.Height / PX);
            try { await WithForceOrSay("agenda.sposta", args); }
            finally { await Guard(Refresh); }
        };
        return card;
    }

    async Task WithForceOrSay(string name, JsonObject args)
    {
        try { await WithForce(name, args); }
        catch (ApiWarning w) { Say(w.Message); }
        catch (ApiError ex)
        {
            if (Can("booking.force") && ex.Message.StartsWith("Adesso non si può") || ex.Message.Contains("Orario non disponibile") && Can("booking.force"))
            {
                if (Ask(ex.Message + "\n\nVuoi forzare lo spostamento?", "Forza")) { args["force"] = true; await WithForce(name, args); }
            }
            else Say(ex.Message);
        }
    }

    static Border Stripe(Brush color)
    {
        var s = new Border { Width = 3, Background = color, CornerRadius = new CornerRadius(2), Margin = new Thickness(-4, 1, 6, 1) };
        DockPanel.SetDock(s, Dock.Left); return s;
    }

    FrameworkElement ListRow(JsonNode x)
    {
        var a = Local(x.S("start"));
        var row = new Border
        {
            Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand,
            Child = new DockPanel
            {
                Children =
                {
                    Stripe(Op(x.I("color"))),
                    Row(T($"{(x.S("phaseLabel") != "" ? x.S("phaseLabel") : a.ToString("HH:mm"))}", size: 13, bold: true).M(0, 0, 12, 0), T(x.S("name"), size: 13, bold: true).M(0, 0, 10, 0),
                        T(x.S("service") + (x.S("who") != "" ? " · " + x.S("who") : "") + (x.I("party") > 1 ? $" · {x.I("party")} persone" : ""), "MutedText"),
                        T(x.S("status") != "confirmed" ? "  · " + STATUS.GetValueOrDefault(x.S("status")) : "", "MutedText")),
                },
            },
        };
        var id = x.S("id");
        row.MouseLeftButtonUp += async (_, _) => Main.ShowPage(new BookingDetailPage(id));
        return row;
    }

    void DrawList(List<JsonNode> items)
    {
        var list = Col();
        if (items.Count == 0) list.Children.Add(T("Nessuna prenotazione in questo giorno.", "MutedText").M(16));
        foreach (var x in items.OrderBy(i => i.S("start"))) list.Children.Add(ListRow(x));
        body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(14), Background = Res("Surface"), ClipToBounds = true, Child = new ScrollViewer { Content = list } };
    }

    // ---------- the week
    void DrawWeek()
    {
        var total = data.I("total");
        Heading(data.S("title"), total, false); above.Children.Clear(); searchable.Clear(); Legend(false);
        var monday = DateOnly.Parse(data.S("monday")); var today = DateOnly.Parse(data.S("today"));
        var days = data.A("days").ToList();
        if (!data.Bo("grid"))
        {
            var cols = new UniformGrid { Columns = 7 };
            for (int i = 0; i < 7; i++)
            {
                var d = monday.AddDays(i); var c = Col(T(It.TextInfo.ToTitleCase(d.ToDateTime(TimeOnly.MinValue).ToString("ddd d", It)), size: 13, bold: true, color: d == today ? Res("Brand") : null).M(8, 8, 8, 6));
                foreach (var x in days[i].AsArray().Where(n => n != null)) c.Children.Add(ListRow(x));
                cols.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = c });
            }
            body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(14), Background = Res("Surface"), Child = new ScrollViewer { Content = cols } };
            return;
        }
        int first = data.I("firstMinute"), last = data.I("lastMinute"); var height = (last - first) * PX * 0.75; var px = PX * 0.75;
        var grid = new Grid(); var head = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var ruler = new Canvas { Height = height };
        for (var m = first; m < last; m += 60) { var l = T($"{m / 60:00}:00", "MutedText", size: 12); Canvas.SetTop(l, (m - first) * px - 8); Canvas.SetLeft(l, 8); ruler.Children.Add(l); }
        grid.Children.Add(ruler);
        var open = data.A("open").ToList();
        for (int i = 0; i < 7; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var d = monday.AddDays(i); var dt = d.ToDateTime(TimeOnly.MinValue);
            var hb = new Border
            {
                Padding = new Thickness(8), BorderBrush = Res("Line"), BorderThickness = new Thickness(1, 0, 0, 1), Cursor = Cursors.Hand,
                Child = T(It.TextInfo.ToTitleCase(dt.ToString("ddd d", It)), size: 14, bold: true, color: d == today ? Res("Brand") : null),
            };
            var dd = d; hb.MouseLeftButtonUp += async (_, _) => { day = dd; view = "giorno"; Content = null; Build(); await Guard(Refresh); };
            Grid.SetColumn(hb, i + 1); head.Children.Add(hb);
            var col = new Canvas { Height = height, Background = Res("Surface"), ClipToBounds = true };
            var spans = open.Count > i ? open[i].AsArray().Where(s => s != null).Select(s => (A: Local(s.S("a")), B: Local(s.S("b")))).ToList() : new();
            for (var m = first; m < last; m += 15)
            {
                var a = dt.AddMinutes(m);
                if (!spans.Any(s => s.A <= a && a.AddMinutes(15) <= s.B)) { var r = new Rectangle { Width = 4000, Height = 15 * px, Fill = Res("Closed") }; Canvas.SetTop(r, (m - first) * px); col.Children.Add(r); }
            }
            for (var m = first; m < last; m += 60) { var line = new Rectangle { Width = 4000, Height = 1, Fill = Res("Line") }; Canvas.SetTop(line, (m - first) * px); col.Children.Add(line); }
            var items = days[i].AsArray().Where(n => n != null).ToList();
            foreach (var (x, lane, lanes) in Lanes(items))
            {
                var a = Local(x.S("start")); var b = Local(x.S("end"));
                var top = Math.Max(0, (a - dt).TotalMinutes - first) * px; var bottom = Math.Min(last - first, (b - dt).TotalMinutes - first) * px;
                var color = Op(x.I("color"));
                var t = T($"{x.S("from")} {x.S("name")}", size: 12, bold: true, color: Brushes.White); t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap;
                var card = new Border { Height = Math.Max(16, bottom - top - 2), Background = color, CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 2, 4, 2), Cursor = Cursors.Hand, Child = t, ClipToBounds = true, ToolTip = $"{x.S("from")}–{x.S("to")} {x.S("name")}\n{x.S("service")} · {x.S("who")}" };
                var ln = lane; var lns = lanes;
                void Place() { var w = col.ActualWidth > 0 ? col.ActualWidth : 120; var lw = (w - 4) / lns; card.Width = Math.Max(20, lw - 2); Canvas.SetLeft(card, 2 + ln * lw); }
                col.SizeChanged += (_, _) => Place(); Place();
                Canvas.SetTop(card, top + 1);
                var id = x.S("id"); card.MouseLeftButtonUp += async (_, _) => Main.ShowPage(new BookingDetailPage(id));
                col.Children.Add(card);
            }
            var frame = new Border { Child = col, BorderBrush = Res("Line"), BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(frame, i + 1); grid.Children.Add(frame);
        }
        var dock = new DockPanel(); DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(14), Background = Res("Surface"), ClipToBounds = true, Child = dock };
    }

    // ---------- the month
    void DrawMonth()
    {
        var total = data.I("total");
        Heading(data.S("title"), total, false); above.Children.Clear(); searchable.Clear(); below.Children.Clear();
        var grid = new Grid();
        var weeks = data.A("weeks").ToList();
        for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var names = new[] { "Lun", "Mar", "Mer", "Gio", "Ven", "Sab", "Dom" };
        for (int i = 0; i < 7; i++) { var t = T(names[i], "MutedText", size: 12, bold: true).M(10, 8, 0, 6); Grid.SetColumn(t, i); grid.Children.Add(t); }
        for (int w = 0; w < weeks.Count; w++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 96 });
            var cells = weeks[w].AsArray().ToList();
            for (int i = 0; i < 7; i++)
            {
                var c = cells[i]; var d = DateOnly.Parse(c.S("day"));
                var col = Col(T(d.Day.ToString(), size: 13, bold: c.Bo("isToday"), color: c.Bo("isToday") ? Res("Brand") : c.Bo("inside") ? null : Res("Muted")).M(0, 0, 0, 4));
                foreach (var x in c.A("items").Take(3))
                {
                    var t = T($"{(x.S("phaseLabel") != "" ? x.S("phaseLabel") : x.S("from"))} {x.S("name")}", size: 12); t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap;
                    col.Children.Add(new Border { Background = Tint(Op(x.I("color")), 0.15), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 0, 2), Child = t });
                }
                if (c.I("count") > 3) col.Children.Add(T($"+{c.I("count") - 3} altre", "MutedText", size: 12));
                var cell = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, i < 6 ? 1 : 0, 0), Padding = new Thickness(8, 6, 6, 6), Background = c.Bo("inside") ? Res("Surface") : Res("Bg"), Child = col, Cursor = Cursors.Hand };
                var dd = d; cell.MouseLeftButtonUp += async (_, _) => { day = dd; view = "giorno"; Content = null; Build(); await Guard(Refresh); };
                Grid.SetRow(cell, w + 1); Grid.SetColumn(cell, i); grid.Children.Add(cell);
            }
        }
        body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(14), Background = Res("Surface"), ClipToBounds = true, Child = grid };
    }

    static Border Notice(string text) => new() { Background = Res("BrandSoft"), BorderBrush = Res("Brand"), BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 14), Child = T(text, size: 14) };

    void NewBooking(string resource, DateTime? at, bool walkin = false) =>
        Main.ShowPage(new BookingPage { Walkin = walkin, PrefillResource = resource ?? "", PrefillDate = (at?.Date ?? day.ToDateTime(TimeOnly.MinValue)).ToString("yyyy-MM-dd"), PrefillTime = at?.ToString("HH:mm") ?? "" });
}
