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
        Col(Label(label, muted), input, small == null ? null : T(small, size: 13, color: Res("Muted")).M(0, 4, 0, 0));
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
}
