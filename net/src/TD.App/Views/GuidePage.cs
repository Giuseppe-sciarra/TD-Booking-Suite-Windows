using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/guide.html: the Guida: the index on the left, the chapters as cards; the text comes from the
/// server as the tree of the web page (tools/gen_guide.py), drawn here with the program's own styles.</summary>
public sealed class GuidePage : Page
{
    readonly Dictionary<string, FrameworkElement> anchors = new();
    public override async Task Load() => Draw(await Api.Current.Call("guida", new { }));
    static IEnumerable<JsonNode> Kids(JsonNode n) => (n?["c"] as JsonArray)?.Where(x => x != null) ?? Enumerable.Empty<JsonNode>();
    static IEnumerable<JsonNode> Tags(JsonNode n, string tag) => Kids(n).Where(k => k is JsonObject && k.S("t") == tag);
    static JsonNode Find(JsonNode n, Func<JsonNode, bool> match) => n is not JsonObject ? null : match(n) ? n : Kids(n).Select(k => Find(k, match)).FirstOrDefault(x => x != null);
    void Go(string href) { if (href.StartsWith('#') && anchors.TryGetValue(href[1..], out var e)) e.BringIntoView(); }

    /// <summary>The text of a paragraph with its em, strong, code, kbd and links.</summary>
    void Inline(InlineCollection into, JsonNode n)
    {
        foreach (var k in Kids(n))
        {
            if (k is JsonValue) { into.Add(new Run(k.ToString())); continue; }
            Span span = k.S("t") switch { "em" => new Italic(), "strong" or "dt" or "th" => new Bold { FontWeight = FontWeights.SemiBold }, "a" => new Hyperlink(), _ => new Span() };
            if (k.S("t") is "code" or "kbd") { span.FontFamily = new System.Windows.Media.FontFamily("Consolas"); span.Background = Res("Bg"); span.FontSize = 13; }
            if (span is Hyperlink link) { var href = k.S("href"); link.Click += (_, _) => Go(href); }
            Inline(span.Inlines, k); into.Add(span);
        }
    }
    TextBlock Text(JsonNode n, double size = 14.5, bool muted = false)
    {
        var t = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.6, Foreground = Res(muted ? "Muted" : "Ink") }; Inline(t.Inlines, n); return t;
    }

    /// <summary>A block of a chapter: title, paragraph, list, the three levels, a table.</summary>
    IEnumerable<FrameworkElement> Block(JsonNode n)
    {
        switch (n.S("t"))
        {
            case "h2": { var h = T(string.Concat(Kids(n).Select(k => k.ToString())), "H2"); h.TextWrapping = TextWrapping.Wrap; yield return h; break; }
            case "h3": { var h = Text(n, 16); h.FontWeight = FontWeights.SemiBold; yield return h.M(0, 14, 0, 4); break; }
            case "p": yield return Text(n, muted: n.S("class").Contains("muted")).M(0, 6, 0, 6); break;
            case "ol" or "ul":
                {
                    var list = new Grid { Margin = new Thickness(0, 8, 0, 12) }; list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); list.ColumnDefinitions.Add(new ColumnDefinition()); var i = 0;
                    foreach (var li in Tags(n, "li"))
                    {
                        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var mark = T(n.S("t") == "ol" ? $"{i + 1}." : "•", size: 14.5); var text = Text(li).M(0, 0, 0, 10);
                        Grid.SetRow(mark, i); Grid.SetRow(text, i); Grid.SetColumn(text, 1); list.Children.Add(mark); list.Children.Add(text); i++;
                    }
                    yield return list; break;
                }
            case "dl":
                foreach (var div in Tags(n, "div"))
                {
                    var dt = Tags(div, "dt").FirstOrDefault(); var dd = Tags(div, "dd").FirstOrDefault(); var title = Text(dt); title.FontWeight = FontWeights.SemiBold;
                    yield return new Border { Background = Res("Bg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 5, 0, 5), Child = Col(title, Text(dd, muted: true).M(0, 2, 0, 0)) };
                }
                break;
            case "div":
                foreach (var k in Kids(n).Where(k => k is JsonObject)) foreach (var e in Block(k)) yield return e;
                break;
            case "table":
                {
                    var rows = Kids(n).Where(k => k is JsonObject).SelectMany(part => part.S("t") == "tr" ? new[] { part } : Tags(part, "tr")).ToList(); if (rows.Count == 0) break;
                    var cols = rows.Max(r => Kids(r).Count(k => k is JsonObject)); var grid = new Grid { Margin = new Thickness(0, 8, 0, 12) };
                    for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                    for (int r = 0; r < rows.Count; r++)
                    {
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var c = 0;
                        foreach (var cell in Kids(rows[r]).Where(k => k is JsonObject))
                        {
                            var text = Text(cell, 14); if (cell.S("t") == "th") { text.FontWeight = FontWeights.SemiBold; text.Foreground = Res("Muted"); }
                            var box = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 16, 8), Child = text, MaxWidth = c == 0 ? 260 : double.PositiveInfinity };
                            Grid.SetRow(box, r); Grid.SetColumn(box, c++); grid.Children.Add(box);
                        }
                    }
                    yield return grid; break;
                }
        }
    }

    void Draw(JsonNode d)
    {
        anchors.Clear();
        var heading = Find(d, n => n.S("class") == "page-heading"); var h1 = Find(heading, n => n.S("t") == "h1"); var intro = Find(heading, n => n.S("t") == "p");
        var nav = Find(d, n => n.S("t") == "nav"); var body = Find(d, n => n.S("class") == "guide-body");
        var chapters = new StackPanel { MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var section in Tags(body, "section"))
        {
            var card = F.Card(Kids(section).Where(k => k is JsonObject).SelectMany(Block).ToArray());
            if (section.S("id") != "") anchors[section.S("id")] = card;
            chapters.Children.Add(card);
        }
        // nav.guide-toc: it stays while the chapters scroll
        var toc = Col(T(string.Concat(Kids(Tags(nav, "h2").FirstOrDefault()).Select(k => k.ToString())), size: 16, bold: true).M(0, 0, 0, 8)); var i = 1;
        foreach (var li in Tags(Tags(nav, "ol").FirstOrDefault(), "li"))
        {
            var a = Tags(li, "a").FirstOrDefault(); var href = a.S("href");
            var link = F.Link(string.Concat(Kids(a).Select(k => k.ToString())), () => Go(href)); link.TextWrapping = TextWrapping.Wrap;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) }; var n = T($"{i++}.", size: 14); n.Width = 24; row.Children.Add(n); row.Children.Add(link); toc.Children.Add(row);
        }
        var left = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = F.Card(toc), VerticalAlignment = VerticalAlignment.Top };
        var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = chapters, Padding = new Thickness(0, 0, 12, 0) };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 2); grid.Children.Add(left); grid.Children.Add(right);
        var top = F.Heading(null, string.Concat(Kids(h1).Select(k => k.ToString())), string.Concat(Kids(intro).Select(k => k.ToString())));
        var all = new DockPanel { Margin = new Thickness(26, 20, 26, 0) }; DockPanel.SetDock(top, Dock.Top); all.Children.Add(top); all.Children.Add(grid);
        Content = all;
    }
}
