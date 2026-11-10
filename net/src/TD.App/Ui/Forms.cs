using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using static TD.App.Ui.U;

namespace TD.App.Ui;

/// <summary>The pieces every page of the web program is made of (app.css): the page with its width and air, the back
/// link, the heading, the card, fieldset and legend, label with its muted note, the two-column form grid, notices,
/// pills, chips. Sizes are the desktop ones (the @media min-width:851px block).</summary>
public static class F
{
    /// <summary>main: the page scrolls as a whole, max width and padding as the web (1480, 20 26 16); narrow for forms (860).</summary>
    public static ScrollViewer Page(double maxWidth, params UIElement[] items)
    {
        var col = Col(items); col.MaxWidth = maxWidth; col.Margin = new Thickness(26, 20, 26, 16);
        return new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    public static TextBlock Link(string text, Action click, double size = 14)
    {
        var t = T(text, size: size, color: Res("Brand")); t.FontWeight = FontWeights.Medium; t.Cursor = Cursors.Hand;
        t.MouseEnter += (_, _) => t.TextDecorations = TextDecorations.Underline; t.MouseLeave += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonUp += (_, _) => click();
        return t;
    }
    /// <summary>.back: ‹ Agenda e prenotazioni</summary>
    public static FrameworkElement Back(string text, Action click) => Link("‹ " + text, click).M(0, 0, 0, 6);
    /// <summary>.page-heading: (eyebrow) h1 p on the left, the actions on the right, at the bottom.</summary>
    public static DockPanel Heading(string eyebrow, string h1, string p, params FrameworkElement[] actions)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var a = Row(); a.VerticalAlignment = VerticalAlignment.Bottom;
        for (int i = 0; i < actions.Length; i++) if (actions[i] != null) a.Children.Add(actions[i].M(i == 0 ? 0 : 10, 0, 0, 0));
        DockPanel.SetDock(a, Dock.Right); d.Children.Add(a);
        d.Children.Add(Col(eyebrow == null ? null : T(eyebrow, "Eyebrow"), T(h1, "H1"), p == null ? null : T(p, size: 14, color: Res("Muted"))));
        return d;
    }
    public static Border Card(params UIElement[] items) => new() { Style = Style("Card"), Child = Col(items), Margin = new Thickness(0, 0, 0, 14), Effect = (System.Windows.Media.Effects.Effect)Application.Current.Resources["CardShadow"] };
    /// <summary>fieldset + legend: a part of a form, closed by a line unless it is the last.</summary>
    public static Border Fieldset(string legend, bool last, params UIElement[] items)
    {
        var col = Col(); if (legend != null) col.Children.Add(Legend(legend));
        foreach (var i in items) if (i != null) col.Children.Add(i);
        return new Border { Child = col, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Padding = new Thickness(0, 0, 0, last ? 0 : 26), Margin = new Thickness(0, 0, 0, last ? 0 : 26) };
    }
    public static TextBlock Legend(string text) { var t = T(text, size: 19, bold: true); t.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"]; t.Margin = new Thickness(0, 0, 0, 4); return t; }
    /// <summary>label: 13 px semibold, 12 above and 5 below; a note in muted regular after it.</summary>
    public static TextBlock Label(string text, string muted = null)
    {
        var t = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap };
        t.Inlines.Add(text);
        if (muted != null) { var r = new Run(" " + muted) { FontWeight = FontWeights.Normal }; r.SetResourceReference(TextElement.ForegroundProperty, "Muted"); t.Inlines.Add(r); }
        return t;
    }
    public static StackPanel Field(string label, FrameworkElement input, string muted = null, string small = null) =>
        Col(Label(label, muted), input, small == null ? null : new TextBlock { Text = small, FontSize = 13, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
    /// <summary>.form-grid: two equal columns, 18 px between.</summary>
    public static Grid Grid2(params FrameworkElement[] items)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < items.Length; i++)
        {
            if (i % 2 == 0) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (items[i] == null) continue;
            Grid.SetRow(items[i], i / 2); Grid.SetColumn(items[i], i % 2 == 0 ? 0 : 2); g.Children.Add(items[i]);
        }
        return g;
    }
    /// <summary>.notice: soft, a 4 px edge on the left; success, warning, error.</summary>
    public static Border Notice(UIElement child, string kind = "")
    {
        var (bg, edge) = kind switch { "success" => ("OkSoft", "Ok"), "warning" => ("WarnSoft", "Warn"), "error" => ("DangerSoft", "Danger"), _ => ("BrandSoft", "Brand") };
        return new Border { Background = Res(bg), BorderBrush = Res(edge), BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 10, 0, 10), Child = child };
    }
    public static Border Notice(string text, string kind = "") => Notice(T(text, size: 13.5), kind);
    /// <summary>.pill, with × when it can be taken away.</summary>
    public static Border Pill(string text, Action drop = null)
    {
        var row = Row(T(text, size: 12, bold: true, color: Res("Brand")));
        if (drop != null) { var x = T("×", size: 14, bold: true, color: Res("Brand")).M(8, -2, 0, 0); x.Cursor = Cursors.Hand; x.MouseLeftButtonUp += (_, _) => drop(); row.Children.Add(x); }
        return new Border { Background = Res("BrandSoft"), BorderBrush = Res("Pastel"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 8), Child = row };
    }
    /// <summary>.chip-check: a rounded chip to tick, amber with ✓ when chosen (allergies).</summary>
    public static System.Windows.Controls.Primitives.ToggleButton Chip(string text)
    {
        var b = new System.Windows.Controls.Primitives.ToggleButton { Content = text, Tag = text, Style = Style("Chip"), Margin = new Thickness(0, 0, 8, 8) };
        return b;
    }
    /// <summary>.slot: a time to press; filled with the brand when chosen. sub: the small line under it.</summary>
    public static System.Windows.Controls.Primitives.ToggleButton Slot(string time, string sub)
    {
        var c = Col(new TextBlock { Text = time, FontSize = 15 }, string.IsNullOrEmpty(sub) ? null : new TextBlock { Text = sub, FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
        return new System.Windows.Controls.Primitives.ToggleButton { Content = c, Style = Style("Slot"), Margin = new Thickness(0, 0, 8, 8), Width = 112 };
    }
    /// <summary>.check: a box and its sentence, 18 px above and below.</summary>
    public static CheckBox Check(UIElement content) => new() { Content = content, Margin = new Thickness(0, 18, 0, 18) };
    /// <summary>button.small: 5 11, 13 px.</summary>
    public static T Small<T>(T b) where T : Control { b.Padding = new Thickness(11, 5, 11, 5); b.FontSize = 13; b.MinHeight = 0; b.VerticalAlignment = VerticalAlignment.Center; return b; }
    /// <summary>.grid.two / .three: equal columns with a gap, as many rows as needed.</summary>
    public static Grid Cols(int n, double gap, params FrameworkElement[] items)
    {
        var g = new Grid(); var list = items.Where(i => i != null).ToList();
        for (int i = 0; i < n; i++) { if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) }); g.ColumnDefinitions.Add(new ColumnDefinition()); }
        for (int i = 0; i < list.Count; i++)
        {
            if (i % n == 0) { if (i > 0) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) }); g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            Grid.SetRow(list[i], i / n * 2); Grid.SetColumn(list[i], i % n * 2); g.Children.Add(list[i]);
        }
        return g;
    }
    /// <summary>.section-heading: h2 (with its count) on the left, a link or a control on the right.</summary>
    public static DockPanel Section(string h2, int? count = null, FrameworkElement right = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        if (right != null) { right.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
        var h = T(h2, "H2"); h.Margin = new Thickness(0);
        d.Children.Add(Row(h, count == null ? null : new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = T(count.ToString(), size: 12, bold: true, color: Res("Brand")) }));
        return d;
    }
    /// <summary>.pill with the colour of a booking's status.</summary>
    public static Border Status(string status) => Views.MainWindow.Pill(StatusName(status), status switch { "confirmed" or "sent" or "active" or "yes" => "ok", "pending" or "trial" => "warn", "cancelled" or "failed" or "suspended" or "revoked" => "danger", _ => "" });
    /// <summary>.cmark: VIP, absences, late cancellations, not bookable online, small beside the client's name.</summary>
    public static Border Mark(string kind, string icon, string n, string tip)
    {
        var (bg, fg) = kind switch { "vip" => (Hex("#F7EFD6"), Hex("#9C7412")), "late" => (Res("WarnSoft"), Res("Warn")), "absent" or "blocked" => (Res("DangerSoft"), Res("Danger")), _ => (Res("BrandSoft"), Res("Brand")) };
        return new Border { Background = bg, CornerRadius = new CornerRadius(999), Height = 18, Padding = new Thickness(3, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
            Child = Row(Icons.Icon(icon, fg, 12, 1), n == "" ? null : new TextBlock { Text = n, FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = fg, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }) };
    }
    /// <summary>presentation.client_marks for a row that carries vip, assenze, tardive, bloccato.</summary>
    public static IEnumerable<Border> Marks(System.Text.Json.Nodes.JsonNode r)
    {
        if (r.Bo("vip")) yield return Mark("vip", "star", "", "Cliente VIP");
        if (r.I("assenze") > 0) yield return Mark("absent", "absent", r.I("assenze").ToString(), $"{r.I("assenze")} assenz{(r.I("assenze") == 1 ? "a" : "e")}");
        if (r.I("tardive") > 0) yield return Mark("late", "late", r.I("tardive").ToString(), $"{r.I("tardive")} disdett{(r.I("tardive") == 1 ? "a" : "e")} all'ultimo momento");
        if (r.Bo("bloccato")) yield return Mark("blocked", "absent", "", "Non prenotabile online");
    }
    /// <summary>.empty: an icon, a title, a line, in the middle.</summary>
    public static StackPanel Empty(string icon, string h3, string p)
    {
        var c = Col(T(icon, size: 32, color: Res("Pastel")), T(h3, size: 15, bold: true).M(0, 6, 0, 4), T(p, size: 14, color: Res("Muted")));
        foreach (FrameworkElement e in c.Children) e.HorizontalAlignment = HorizontalAlignment.Center;
        c.Margin = new Thickness(20, 40, 20, 40); return c;
    }
    /// <summary>.allergy-chip.small</summary>
    public static Border Allergy(string text) => new() { Background = Res("WarnSoft"), BorderBrush = Res("Warn"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 1, 9, 1), Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = T(text, size: 12, bold: true, color: Res("Warn")) };
    /// <summary>.avatar: the first letter in a soft square (44, 12 round; xl 64).</summary>
    public static Border Avatar(string name, double size = 44)
    {
        var t = T(string.IsNullOrEmpty(name) ? "" : name[..1].ToUpperInvariant(), size: size * 0.42, bold: true, color: Res("Brand")); t.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"];
        t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size >= 40 ? 12 : size / 2), Background = Res("BrandSoft"), Child = t };
    }
    /// <summary>.chip (client-chips): discount pink, new amber, the consent green, red or outlined.</summary>
    public static Border ClientChip(string text, string kind = "", string icon = null)
    {
        var (bg, fg, line) = kind switch
        {
            "discount" => ((Brush)Hex("#FBEAF3"), (Brush)Hex("#B8457F"), (Brush)null), "new" => (Res("WarnSoft"), Res("Warn"), null), "ok" => (Res("OkSoft"), Res("Ok"), null), "danger" => (Res("DangerSoft"), Res("Danger"), null),
            "vip" => (Hex("#F7EFD6"), Hex("#9C7412"), null), "none" => (Brushes.Transparent, Res("Muted"), Res("Line")), _ => (Res("BrandSoft"), Res("Brand"), null),
        };
        return new Border { Background = bg, BorderBrush = line, BorderThickness = new Thickness(line == null ? 0 : 1), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6),
            Child = Row(icon == null ? null : Icons.Icon(icon, fg, 13, 1).M(0, 0, 5, 0), T(text, size: 12.5, bold: true, color: fg)) };
    }
    /// <summary>.pagination: ← before, n / of, after →.</summary>
    public static FrameworkElement Pagination(int page, int pages, Action<int> go, string before = "← Precedente", string after = "Successiva →")
    {
        if (pages <= 1) return null;
        var r = Row(page > 1 ? Link(before, () => go(page - 1)) : null, T($"{page} / {pages}", size: 14).M(24, 0, 24, 0), page < pages ? Link(after, () => go(page + 1)) : null);
        r.HorizontalAlignment = HorizontalAlignment.Center; r.Margin = new Thickness(0, 20, 0, 0); return r;
    }
    /// <summary>.empty-state: a strong line and a small one, in the middle.</summary>
    /// <summary>The widths of a table laid out as the browser does: each column as wide as its longest text asks.</summary>
    public static double[] Widths(string[] head, List<string[]> rows) =>
        head.Select((h, i) => (double)Math.Clamp(Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => i < r.Length ? (r[i] ?? "").Length : 0)), 4, 60)).ToArray();
    /// <summary>table: th muted 13 with a line of 2 below, td 14 with a line of 1, the last row without.</summary>
    public static Grid Table(string[] head, List<string[]> rows, double pad = 12)
    {
        var g = new Grid();
        foreach (var w in Widths(head, rows)) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Put(string text, int row, int col, bool th, bool last)
        {
            var t = T(text, size: th ? 13 : 14, bold: th, color: th ? Res("Muted") : null); t.TextWrapping = th ? TextWrapping.NoWrap : TextWrapping.Wrap; t.VerticalAlignment = VerticalAlignment.Center;
            var cell = new Border { Child = t, Padding = new Thickness(12, th ? 10 : pad, 12, th ? 10 : pad), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, th ? 2 : last ? 0 : 1) };
            Grid.SetRow(cell, row); Grid.SetColumn(cell, col); g.Children.Add(cell);
        }
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < head.Length; i++) Put(head[i], 0, i, true, false);
        for (int r = 0; r < rows.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < head.Length; i++) Put(i < rows[r].Length ? rows[r][i] : "", r + 1, i, false, r == rows.Count - 1);
        }
        return g;
    }
    public static StackPanel EmptyState(string strong, FrameworkElement small)
    {
        var c = Col(T(strong, size: 14, bold: true), small); foreach (FrameworkElement e in c.Children) e.HorizontalAlignment = HorizontalAlignment.Center;
        c.Margin = new Thickness(10, 18, 10, 18); return c;
    }
    /// <summary>details &gt; summary.button.secondary.small: a small button that opens what is under it.</summary>
    public static StackPanel Details(string summary, UIElement body, bool open = false, bool plain = false)
    {
        var box = new ContentControl { Content = body, Visibility = open ? Visibility.Visible : Visibility.Collapsed, Focusable = false };
        FrameworkElement head = plain ? Link(summary, () => { }) : Small(new Button { Content = summary });
        void Toggle() => box.Visibility = box.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (head is Button b) b.Click += (_, _) => Toggle(); else head.MouseLeftButtonUp += (_, _) => Toggle();
        head.HorizontalAlignment = HorizontalAlignment.Left;
        return Col(head, box);
    }
    /// <summary>input list=…: free text with the suggestions under it.</summary>
    public static ComboBox Suggest(IEnumerable<string> options, string text = "", int max = 0)
    {
        var c = new ComboBox { IsEditable = true, ItemsSource = options.ToList(), Text = text ?? "", IsTextSearchEnabled = false };
        if (max > 0) c.Loaded += (_, _) => { if (c.Template.FindName("PART_EditableTextBox", c) is TextBox t) t.MaxLength = max; };
        return c;
    }
    /// <summary>[data-client-search] of app.js: a client chosen by typing (gift card, quote): the box, what is found under
    /// it, the id chosen (empty again as soon as the text changes); picked: for who wants the rest of the client.</summary>
    public static (TextBox Box, FrameworkElement Found, Func<string> Id, Action<string, string> Set) ClientSearch(Action<System.Text.Json.Nodes.JsonNode> picked = null)
    {
        var box = Box(""); box.Tag = "Nome, email o telefono"; Views.AgendaPage.Hint(box); var id = ""; var quiet = false; var found = Col(); System.Windows.Threading.DispatcherTimer timer = null;
        var foundBox = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), Child = found, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        box.TextChanged += (_, _) =>
        {
            if (quiet) return; id = ""; timer?.Stop(); var text = box.Text.Trim();
            if (text.Length < 2) { found.Children.Clear(); foundBox.Visibility = Visibility.Collapsed; return; }
            timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    var r = await Net.Api.Current.Call("clienti.cerca", new { q = text, quanti = 8 }) as System.Text.Json.Nodes.JsonArray ?? new();
                    if (box.Text.Trim() != text) return; found.Children.Clear();
                    foreach (var c in r)
                    {
                        var name = $"{c.S("nome")} {c.S("cognome")}".Trim();
                        var b = new Button { Content = name + (c.S("telefono") != "" ? " · " + c.S("telefono") : c.S("email") != "" ? " · " + c.S("email") : ""), Style = Style("Tile"), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 12, 14, 12) };
                        b.Click += (_, _) => { quiet = true; id = c.S("id"); box.Text = name; quiet = false; found.Children.Clear(); foundBox.Visibility = Visibility.Collapsed; picked?.Invoke(c); };
                        found.Children.Add(b);
                    }
                    foundBox.Visibility = r.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                catch (Net.ApiError) { }
            };
            timer.Start();
        };
        return (box, foundBox, () => id, (name, cid) => { quiet = true; box.Text = name; id = cid; quiet = false; });
    }
    /// <summary>The business's logo (attivita.io sends it in base64), null without one.</summary>
    public static Image Logo(System.Text.Json.Nodes.JsonNode business, double height)
    {
        if (business.S("logo") == "") return null;
        try { var bi = new System.Windows.Media.Imaging.BitmapImage(); bi.BeginInit(); bi.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(business.S("logo"))); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze(); return new Image { Source = bi, Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or System.IO.IOException) { return null; }
    }
}
