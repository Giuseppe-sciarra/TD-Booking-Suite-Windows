using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using static TD.App.Ui.U;

namespace TD.App.Ui;

/// <summary>templates/form.html: a form drawn from its fields as the server describes them (label, help, kind of input,
/// choices, value): two columns, the ticks and the wide fields on the whole row, the error of each field under it.</summary>
public sealed class FormView
{
    sealed class Item
    {
        public JsonNode F; public FrameworkElement Box; public Func<JsonNode> Read; public TextBlock Error, Label, Help; public string Type;
    }
    readonly Dictionary<string, Item> items = new();
    readonly List<Item> order = new();
    readonly Border top = new() { Visibility = Visibility.Collapsed };
    readonly Grid grid = new();
    public StackPanel Panel { get; } = new();
    /// <summary>The service form of the administrator: fields appear only for the kinds of booking that use them.</summary>
    public event Action Changed;

    public FormView(IEnumerable<JsonNode> fields)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var f in fields) Add(f);
        Panel.Children.Add(top); Panel.Children.Add(grid);
        Layout();
    }

    static T Small<T>(T b) where T : Control => F.Small(b);
    static TextBlock Small(string text, bool error = false) => new() { Text = text, FontSize = 13, Foreground = Res(error ? "Danger" : "Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = text == "" ? Visibility.Collapsed : Visibility.Visible };
    static IEnumerable<(string Id, string Name)> Choices(JsonNode f) => f.A("scelte").Select(c => (c.S("id"), c.S("nome")));

    void Add(JsonNode f)
    {
        var it = new Item { F = f, Type = f.S("tipo"), Error = Small("", true), Help = Small(f.S("aiuto")), Label = F.Label(f.S("etichetta")) };
        var v = f["valore"]; var text = v is JsonValue ? v.ToString() : "";
        switch (it.Type)
        {
            case "hidden": it.Read = () => text; break;
            case "check":
                {
                    var c = new CheckBox { IsChecked = v is JsonValue b && b.TryGetValue<bool>(out var on) && on, Content = new TextBlock { Text = f.S("etichetta"), FontSize = 14, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 0) };
                    c.Checked += (_, _) => Changed?.Invoke(); c.Unchecked += (_, _) => Changed?.Invoke(); it.Box = c; it.Read = () => c.IsChecked == true; it.Label = null; break;
                }
            case "photo":
                {
                    // branding.clean_photo: turned upright, 800 px at most, on white, a JPEG
                    string data = ""; var preview = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(12), Background = Res("BrandSoft"), Margin = new Thickness(0, 0, 12, 0) };
                    void Show(byte[] jpeg) { try { preview.Background = new System.Windows.Media.ImageBrush(Views.SetupPage.PictureOf(jpeg)) { Stretch = System.Windows.Media.Stretch.UniformToFill }; } catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException) { } }
                    if (text != "") try { Show(Convert.FromBase64String(text)); } catch (FormatException) { }
                    var pick = Small(B(text != "" ? "Cambia la foto" : "Scegli la foto", () =>
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Immagini (PNG, JPEG, WebP)|*.png;*.jpg;*.jpeg;*.webp" };
                        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
                        try { var jpeg = Views.Pictures.Photo(dlg.FileName); data = Convert.ToBase64String(jpeg); Show(jpeg); it.Error.Text = ""; it.Error.Visibility = Visibility.Collapsed; }
                        catch (Net.ApiError ex) { it.Error.Text = ex.Message; it.Error.Visibility = Visibility.Visible; }
                    }));
                    it.Box = Row(preview, pick); it.Read = () => data; break;
                }
            case "select":
                {
                    var c = new ComboBox { ItemsSource = Choices(f).Select(o => new ComboBoxItem { Content = o.Name, Tag = o.Id }).ToList() };
                    c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == text) ?? (c.Items.Count > 0 ? c.Items[0] : null);
                    c.SelectionChanged += (_, _) => Changed?.Invoke(); it.Box = c; it.Read = () => (string)(c.SelectedItem as ComboBoxItem)?.Tag ?? ""; break;
                }
            case "multi":
                {
                    var chosen = (v as JsonArray)?.Select(x => x.ToString()).ToHashSet() ?? new(); var p = new WrapPanel();
                    var boxes = Choices(f).Select(o => new CheckBox { Content = new TextBlock { Text = o.Name, FontSize = 14 }, Tag = o.Id, IsChecked = chosen.Contains(o.Id), Margin = new Thickness(0, 4, 18, 4) }).ToList();
                    foreach (var b in boxes) p.Children.Add(b);
                    it.Box = p; it.Read = () => new JsonArray(boxes.Where(b => b.IsChecked == true).Select(b => (JsonNode)(string)b.Tag).ToArray()); break;
                }
            case "radio":
                {
                    var p = new StackPanel(); var group = Guid.NewGuid().ToString();
                    var boxes = Choices(f).Select(o => new RadioButton { GroupName = group, Content = new TextBlock { Text = o.Name, FontSize = 14 }, Tag = o.Id, IsChecked = o.Id == text, Margin = new Thickness(0, 4, 0, 4) }).ToList();
                    foreach (var b in boxes) p.Children.Add(b);
                    it.Box = p; it.Read = () => (string)boxes.FirstOrDefault(b => b.IsChecked == true)?.Tag ?? ""; break;
                }
            case "date":
                {
                    var d = new DatePicker { SelectedDate = DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null };
                    it.Box = d; it.Read = () => d.SelectedDate?.ToString("yyyy-MM-dd") ?? (d.Text ?? ""); break;
                }
            case "datetime":
                {
                    var has = DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at);
                    var d = new DatePicker { SelectedDate = has ? at.Date : null, Width = 170 }; var t = Box(has ? at.ToString("HH:mm") : ""); t.Width = 80; t.Tag = "20:30"; Views.AgendaPage.Hint(t); t.Margin = new Thickness(8, 0, 0, 0);
                    it.Box = Row(d, t); it.Read = () => d.SelectedDate.HasValue ? d.SelectedDate.Value.ToString("yyyy-MM-dd") + "T" + (t.Text.Trim() == "" ? "00:00" : t.Text.Trim().Replace('.', ':').PadLeft(5, '0')) : ""; break;
                }
            default:
                {
                    var b = Box(text, it.Type == "area"); if (f.I("max") > 0) b.MaxLength = f.I("max"); if (it.Type == "area") b.MinHeight = 64;
                    if (it.Type == "time") { b.Tag = "09:00"; b.Width = 110; b.HorizontalAlignment = HorizontalAlignment.Left; }
                    if (f.S("segnaposto") != "") b.Tag = f.S("segnaposto");
                    if (b.Tag != null) Views.AgendaPage.Hint(b);
                    it.Box = b; it.Read = () => b.Text; break;
                }
        }
        if (it.Box != null && f.Bo("disattivo")) it.Box.IsEnabled = false;
        items[f.S("nome")] = it; order.Add(it);
    }

    /// <summary>The kind of booking chosen (the field «kind»), or null when the form has none.</summary>
    public string Kind => items.TryGetValue("kind", out var k) ? k.Read()?.ToString() : null;
    bool Shown(Item it) => it.Type != "hidden" && (it.F["tipi"] is not JsonArray kinds || Kind == null || kinds.Any(k => k.ToString() == Kind));

    /// <summary>Puts the fields in the grid again: after the kind changed, only its fields, with its labels and helps.</summary>
    public void Layout()
    {
        grid.Children.Clear(); grid.RowDefinitions.Clear(); int row = -1, col = 2; var kind = Kind;
        foreach (var it in order)
        {
            if (!Shown(it)) continue;
            if (kind != null)
            {
                var label = it.F["etichette"]?[kind]?.ToString() ?? it.F.S("etichetta"); var help = it.F["aiuti"]?[kind]?.ToString() ?? it.F.S("aiuto");
                if (it.Label != null) it.Label.Text = label; else if (it.Box is CheckBox cb) ((TextBlock)cb.Content).Text = label;
                it.Help.Text = help; it.Help.Visibility = help == "" ? Visibility.Collapsed : Visibility.Visible;
            }
            var wide = it.Type == "check" || it.F.Bo("largo");
            if (wide || col == 2) { row++; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); col = 0; } else col = 2;
            var cell = new StackPanel(); foreach (var e in new UIElement[] { it.Label, it.Box, it.Help, it.Error }) if (e != null) { (VisualParent(e) as Panel)?.Children.Remove(e); cell.Children.Add(e); }
            Grid.SetRow(cell, row); Grid.SetColumn(cell, col); if (wide) { Grid.SetColumnSpan(cell, 3); col = 2; }
            grid.Children.Add(cell);
        }
    }
    static DependencyObject VisualParent(UIElement e) => e is FrameworkElement f ? f.Parent : null;

    public JsonObject Values()
    {
        var o = new JsonObject();
        foreach (var (name, it) in items) if (it.Type == "hidden" || Shown(it)) o[name] = it.Read();
        return o;
    }
    public string Value(string name) => items.TryGetValue(name, out var it) ? it.Read()?.ToString() ?? "" : "";
    public FrameworkElement Control(string name) => items.TryGetValue(name, out var it) ? it.Box : null;

    /// <summary>The errors of a refused form: under their fields, the others on top.</summary>
    public void Errors(Dictionary<string, string> errors)
    {
        var rest = new List<string>();
        foreach (var it in order) { it.Error.Text = ""; it.Error.Visibility = Visibility.Collapsed; }
        foreach (var (name, text) in errors)
            if (name != "" && items.TryGetValue(name, out var it) && Shown(it)) { it.Error.Text = text; it.Error.Visibility = Visibility.Visible; }
            else rest.Add(text);
        top.Visibility = rest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        top.Child = rest.Count > 0 ? F.Notice(new TextBlock { Text = string.Join(" ", rest), FontSize = 13.5, TextWrapping = TextWrapping.Wrap }, "error") : null;
        if (rest.Count > 0) top.BringIntoView(); else order.FirstOrDefault(i => i.Error.Text != "")?.Error.BringIntoView();
    }
}
