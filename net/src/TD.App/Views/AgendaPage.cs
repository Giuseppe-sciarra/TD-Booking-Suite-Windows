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
    const double PX = 1.6;  // pixels per minute: a quarter of hour is 24 px

    public override async Task Load()
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(Business.S("fuso")); } catch { }
        if (day == default) day = DateOnly.Parse(Business.S("oggi"));
        if (Content == null) Build();
        await Refresh();
    }

    void Build()
    {
        var seg = Row();
        foreach (var (k, label) in new[] { ("giorno", "Giorno"), ("settimana", "Settimana"), ("mese", "Mese") })
        {
            var r = new RadioButton { Style = Style("Segment"), Content = label, GroupName = "vista", IsChecked = k == view };
            r.Checked += async (_, _) => { view = k; await Guard(Refresh); };
            seg.Children.Add(r);
        }
        var picker = new DatePicker { SelectedDate = day.ToDateTime(TimeOnly.MinValue), Width = 130, VerticalContentAlignment = VerticalAlignment.Center };
        picker.SelectedDateChanged += async (_, _) => { if (picker.SelectedDate is DateTime d && DateOnly.FromDateTime(d) != day) { day = DateOnly.FromDateTime(d); await Guard(Refresh); } };
        var show = new CheckBox { Content = "Mostra annullate", VerticalAlignment = VerticalAlignment.Center };
        show.Checked += async (_, _) => { cancelled = true; await Guard(Refresh); };
        show.Unchecked += async (_, _) => { cancelled = false; await Guard(Refresh); };
        places.SelectionChanged += async (_, _) => { if (places.SelectedItem is ComboBoxItem i && (string)i.Tag != location) { location = (string)i.Tag; await Guard(Refresh); } };
        people.SelectionChanged += async (_, _) => { if (people.SelectedItem is ComboBoxItem i && (string)i.Tag != op) { op = (string)i.Tag; await Guard(Refresh); } };
        var nav = Row(BA("‹", () => Step(-1)), BA("Oggi", async () => { day = DateOnly.Parse(Business.S("oggi")); await Refresh(); }).M(6, 0, 6, 0), BA("›", () => Step(1)), picker.M(10, 0, 0, 0));
        var tools = new DockPanel { LastChildFill = false };
        var left = Col(title, subtitle);
        DockPanel.SetDock(left, Dock.Left); tools.Children.Add(left);
        var actions = Row();
        if (Can("booking.write"))
        {
            actions.Children.Add(B("Blocca orario", () => BlockDialog.Open(this, null, day, null)).M(0, 0, 8, 0));
            actions.Children.Add(B("+ Nuova prenotazione", () => NewBooking(null, null), "Primary"));
        }
        DockPanel.SetDock(actions, Dock.Right); tools.Children.Add(actions);
        var filters = Row(nav, seg.M(16, 0, 0, 0), places.M(16, 0, 0, 0), people.M(8, 0, 0, 0), show.M(16, 0, 0, 0));
        var top = Col(tools, filters.M(0, 12, 0, 12));
        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 12) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top); root.Children.Add(body);
        Content = root;
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) await Guard(() => Step(-1)); if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt) await Guard(() => Step(1)); };
    }

    async Task Step(int n)
    {
        day = view == "giorno" ? day.AddDays(n) : view == "settimana" ? day.AddDays(7 * n) : day.AddMonths(n);
        await Refresh();
    }

    public async Task Refresh()
    {
        var args = new JsonObject { ["data"] = day.ToString("yyyy-MM-dd"), ["sede"] = location, ["operatore"] = op, ["annullate"] = cancelled };
        data = await Api.Current.Call("agenda." + view, args);
        Fill(places, data.A("places").Select(p => (p.S("id"), p.S("name"))).ToList(), data.S("location"), null);
        places.Visibility = places.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var ops = data.A("operators").Select(o => (o.S("id"), o.S("name"))).ToList();
        Fill(people, ops, op, "Tutti gli operatori");
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
        title.Text = It.TextInfo.ToTitleCase(d.ToString("dddd d MMMM yyyy", It));
        var total = data.I("total"); var off = data.A("off").Select(x => x.ToString()).ToList();
        subtitle.Text = (total == 0 ? "Nessuna prenotazione" : total == 1 ? "1 prenotazione" : $"{total} prenotazioni") + (off.Count > 0 ? " · non lavorano: " + string.Join(", ", off) : "") + (data.Bo("closedDay") ? " · giorno di chiusura" : "");
        var ops = data.A("operators").ToList();
        if (!data.Bo("appointments") || ops.Count == 0) { DrawList(data.A("items").ToList()); return; }
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
            var h = Row(new Ellipse { Width = 10, Height = 10, Fill = color, VerticalAlignment = VerticalAlignment.Center }, T(o.S("name"), size: 14, bold: true).M(8, 0, 0, 0));
            var hb = new Border { Child = h, Padding = new Thickness(10, 8, 10, 8), BorderBrush = Res("Line"), BorderThickness = new Thickness(1, 0, 0, 1) };
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
                var half = new Rectangle { Width = 4000, Height = 1, Fill = Hex("#F2F0F5") }; Canvas.SetTop(half, (m + 30 - first) * PX); col.Children.Add(half);
            }
            if (day < DateOnly.FromDateTime(now) || day == DateOnly.FromDateTime(now))
            {
                var pastMin = day < DateOnly.FromDateTime(now) ? last : Math.Clamp((int)(now - d).TotalMinutes, first, last);
                var past = new Rectangle { Width = 4000, Height = Math.Max(0, (pastMin - first) * PX), Fill = new SolidColorBrush(Color.FromArgb(30, 120, 110, 140)), IsHitTestVisible = false };
                Canvas.SetTop(past, 0); col.Children.Add(past);
            }
            var opId = o.S("id");
            col.MouseLeftButtonUp += (s, e) =>
            {
                if (!writer || e.OriginalSource != col && e.OriginalSource is not Rectangle) return;
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
                    Width = 4000, Height = Math.Max(14, bottom - top), Background = new LinearGradientBrush(Color.FromRgb(236, 233, 240), Color.FromRgb(226, 222, 232), 45),
                    BorderBrush = Res("LineStrong"), BorderThickness = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand,
                    Child = T($"{a:HH:mm}–{b:HH:mm}  {bl.S("label")}{(bl.S("note") != "" ? " · " + bl.S("note") : "")}", "MutedText", size: 12).M(8, 3, 8, 0),
                };
                var blockNode = bl;
                card.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (writer) BlockDialog.Open(this, blockNode, day, opId); };
                Canvas.SetTop(card, top); col.Children.Add(card);
            }
            var mine = items.Where(x => x.S("resource") == opId).ToList();
            foreach (var (x, lane, lanes) in Lanes(mine))
                col.Children.Add(Appointment(x, d, first, last, col, lane, lanes, ops, i));
            if (day == DateOnly.FromDateTime(now) && (now - d).TotalMinutes is var nm && nm >= first && nm < last)
            {
                var line = new Rectangle { Width = 4000, Height = 2, Fill = Res("Danger"), IsHitTestVisible = false };
                Canvas.SetTop(line, (nm - first) * PX); col.Children.Add(line);
            }
            var frame = new Border { Child = col, BorderBrush = Res("Line"), BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(frame, i + 1); grid.Children.Add(frame);
        }
        // appointments without an operator (made before operators existed): listed under the grid
        var loose = items.Where(x => !ops.Any(o => o.S("id") == x.S("resource"))).ToList();
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var frameAll = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), ClipToBounds = true };
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        if (loose.Count > 0) { var l = Col(T("Senza operatore", size: 13, bold: true).M(10, 8, 0, 4)); foreach (var x in loose) l.Children.Add(ListRow(x)); DockPanel.SetDock(l, Dock.Bottom); dock.Children.Add(l); }
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
        var bg = status switch { "cancelled" => Hex("#F4F3F6"), "no_show" => Res("DangerSoft"), "arrived" or "in_progress" => Res("OkSoft"), "completed" => Hex("#F1F0F4"), _ => Tint(color, 0.13) };
        var lines = Col();
        var marks = (x.Bo("vip") ? "★ " : "") + (x.A("alert").Any() ? "⚠ " : "") + (x.Bo("forced") ? "⚑ " : "") + (x.I("noShow") > 0 ? $"✕{x.I("noShow")} " : "");
        var name = T(marks + x.S("name"), size: 13, bold: true); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
        if (status == "cancelled") name.TextDecorations = TextDecorations.Strikethrough;
        var when = T($"{x.S("from")}–{x.S("to")}" + (status is "pending" ? " · da confermare" : status != "confirmed" ? " · " + STATUS.GetValueOrDefault(status, status).ToLowerInvariant() : ""), "MutedText", size: 12);
        when.TextWrapping = TextWrapping.NoWrap;
        var svc = T(x.S("service"), size: 12); svc.TextTrimming = TextTrimming.CharacterEllipsis; svc.TextWrapping = TextWrapping.NoWrap;
        if (h < 34) { lines.Children.Add(Row(T($"{x.S("from")} ", "MutedText", size: 12), name)); }
        else { lines.Children.Add(when); lines.Children.Add(name); if (h > 50) lines.Children.Add(svc); }
        var card = new Border
        {
            Height = h, Background = bg, CornerRadius = new CornerRadius(6), BorderBrush = status == "pending" ? color : Res("Line"),
            BorderThickness = new Thickness(status == "pending" ? 1.5 : 1), Padding = new Thickness(8, 3, 6, 3), Cursor = Cursors.Hand, ClipToBounds = true,
            Child = new DockPanel { Children = { Stripe(color), lines } }, Opacity = status == "cancelled" ? 0.7 : 1,
            ToolTip = $"{x.S("from")}–{x.S("to")}  {x.S("name")}\n{x.S("service")}" + (x.S("notes") != "" ? "\n" + x.S("notes") : "") + (x.A("alert").Any() ? "\nAllergie: " + string.Join(", ", x.A("alert")) : ""),
        };
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
            if (!moved || !(dragging || resizing)) { await Guard(() => BookingDetailWindow.Open(this, id)); return; }
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
        row.MouseLeftButtonUp += async (_, _) => await Guard(() => BookingDetailWindow.Open(this, id));
        return row;
    }

    void DrawList(List<JsonNode> items)
    {
        var list = Col();
        if (items.Count == 0) list.Children.Add(T("Nessuna prenotazione in questo giorno.", "MutedText").M(16));
        foreach (var x in items.OrderBy(i => i.S("start"))) list.Children.Add(ListRow(x));
        body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), ClipToBounds = true, Child = new ScrollViewer { Content = list } };
    }

    // ---------- the week
    void DrawWeek()
    {
        title.Text = data.S("title");
        var total = data.I("total");
        subtitle.Text = total == 0 ? "Nessuna prenotazione" : total == 1 ? "1 prenotazione" : $"{total} prenotazioni";
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
            body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), Child = new ScrollViewer { Content = cols } };
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
                var t = T($"{x.S("from")} {x.S("name")}", size: 12, bold: true); t.TextTrimming = TextTrimming.CharacterEllipsis;
                var card = new Border { Height = Math.Max(16, bottom - top - 2), Background = Tint(color, 0.16), BorderBrush = color, BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 4, 2), Cursor = Cursors.Hand, Child = t, ClipToBounds = true, ToolTip = $"{x.S("from")}–{x.S("to")} {x.S("name")}\n{x.S("service")} · {x.S("who")}" };
                var ln = lane; var lns = lanes;
                void Place() { var w = col.ActualWidth > 0 ? col.ActualWidth : 120; var lw = (w - 4) / lns; card.Width = Math.Max(20, lw - 2); Canvas.SetLeft(card, 2 + ln * lw); }
                col.SizeChanged += (_, _) => Place(); Place();
                Canvas.SetTop(card, top + 1);
                var id = x.S("id"); card.MouseLeftButtonUp += async (_, _) => await Guard(() => BookingDetailWindow.Open(this, id));
                col.Children.Add(card);
            }
            var frame = new Border { Child = col, BorderBrush = Res("Line"), BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(frame, i + 1); grid.Children.Add(frame);
        }
        var dock = new DockPanel(); DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), ClipToBounds = true, Child = dock };
    }

    // ---------- the month
    void DrawMonth()
    {
        title.Text = data.S("title");
        var total = data.I("total"); subtitle.Text = total == 0 ? "Nessuna prenotazione" : total == 1 ? "1 prenotazione" : $"{total} prenotazioni";
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
        body.Content = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), ClipToBounds = true, Child = grid };
    }

    void NewBooking(string resource, DateTime? at)
    {
        var w = new BookingWindow(this, resource, at ?? day.ToDateTime(new TimeOnly(9, 0)), at != null);
        if (w.ShowDialog() == true) _ = Guard(Refresh);
    }
}
