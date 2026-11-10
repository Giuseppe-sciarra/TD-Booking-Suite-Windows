using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TD.App.Net;

namespace TD.App.Ui;

/// <summary>Small builders: the windows are made in code, checked by the compiler, styled by Theme.xaml.</summary>
public static class U
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static SolidColorBrush Hex(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public static TextBlock T(string text, string style = null, double size = 0, bool bold = false, Brush color = null)
    {
        var t = new TextBlock { Text = text ?? "" };
        if (style != null) t.Style = Style(style);
        if (size > 0) t.FontSize = size;
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (color != null) t.Foreground = color;
        return t;
    }
    public static Button B(string text, Action click, string style = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.Style = Style(style);
        b.Click += async (_, _) => { b.IsEnabled = false; try { click(); } finally { b.IsEnabled = true; } await Task.CompletedTask; };
        return b;
    }
    public static Button BA(string text, Func<Task> click, string style = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.Style = Style(style);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await Guard(click); } finally { b.IsEnabled = true; } };
        return b;
    }
    public static StackPanel Col(params UIElement[] items) { var s = new StackPanel(); foreach (var i in items) if (i != null) s.Children.Add(i); return s; }
    public static StackPanel Row(params UIElement[] items) { var s = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var i in items) if (i != null) s.Children.Add(i); return s; }
    public static T M<T>(this T e, double l, double t, double r, double b) where T : FrameworkElement { e.Margin = new Thickness(l, t, r, b); return e; }
    public static T M<T>(this T e, double all) where T : FrameworkElement { e.Margin = new Thickness(all); return e; }
    public static Border Card(UIElement child) => new() { Style = Style("Card"), Child = child };
    public static FrameworkElement Field(string label, FrameworkElement input, string help = null) =>
        Col(T(label, size: 13, bold: true).M(0, 0, 0, 4), input, help == null ? null : T(help, "Muted").M(0, 3, 0, 0)).M(0, 0, 0, 12);
    public static TextBox Box(string text = "", bool multi = false) =>
        new() { Text = text ?? "", AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multi ? 70 : 0, VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };

    /// <summary>Runs an action of a window: a refusal of the server is said in words, never as a crash.</summary>
    public static async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (NeedsLogin) { Views.Shell.Instance?.SignedOut(); }
        catch (ApiWarning) { throw; }
        catch (ApiError ex) { Say(ex.Message); }
        catch (Exception ex) { Say("Errore: " + ex.Message); }
    }
    public static void Say(string text, string title = "TD Booking Suite") => MessageBox.Show(Application.Current.MainWindow, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public static bool Ask(string text, string title = "TD Booking Suite") => MessageBox.Show(Application.Current.MainWindow, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>A server call that may warn (Forza): the warning asked, then the same call with force_ok.</summary>
    public static async Task<JsonNode> WithForce(string name, JsonObject args)
    {
        try { return await Api.Current.Call(name, args); }
        catch (ApiWarning w)
        {
            if (!Ask(w.Message, "Stai forzando")) return null;
            args["force_ok"] = true;
            return await Api.Current.Call(name, args);
        }
    }

    public static string S(this JsonNode n, string key) => n?[key] is JsonValue v ? v.ToString() : n?[key]?.ToJsonString() ?? "";
    public static bool Bo(this JsonNode n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static int I(this JsonNode n, string key) => n?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    public static IEnumerable<JsonNode> A(this JsonNode n, string key) => (n?[key] as JsonArray)?.Where(x => x != null) ?? Enumerable.Empty<JsonNode>();

    /// <summary>The operators' colours, the same eight as the web agenda (op-1 ... op-8).</summary>
    public static readonly string[] OpColors = { "#9AA0A6", "#7a55ad", "#d9548f", "#2f8fa6", "#d88a2b", "#3f9a62", "#5b6fd6", "#b8563c", "#8a8f2e" };
    public static SolidColorBrush Op(int i) => Hex(OpColors[Math.Clamp(i, 0, OpColors.Length - 1)]);
    public static SolidColorBrush Tint(SolidColorBrush b, double alpha)
    {
        var c = b.Color; var t = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), c.R, c.G, c.B)); t.Freeze(); return t;
    }

    public static readonly Dictionary<string, string> STATUS = new()
    {
        ["pending"] = "Da confermare", ["confirmed"] = "Confermata", ["arrived"] = "Arrivato", ["in_progress"] = "In corso",
        ["completed"] = "Completata", ["no_show"] = "Non presentato", ["cancelled"] = "Annullata",
    };
    public static readonly Dictionary<string, string> MOVE_LABEL = new()
    {
        ["confirmed"] = "Conferma", ["arrived"] = "Arrivato", ["in_progress"] = "In corso", ["completed"] = "Completata",
        ["no_show"] = "Non presentato", ["cancelled"] = "Annulla",
    };
}
