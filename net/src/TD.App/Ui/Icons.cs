using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TD.App.Ui;

public static partial class Icons
{
    static readonly Dictionary<string, Geometry> cache = new();
    /// <summary>An icon of the web program (ICONS), stroked with the given brush, 17 px as .nav-ico unless said.</summary>
    public static FrameworkElement Icon(string name, Brush stroke, double size = 17, double opacity = 0.85)
    {
        if (!cache.TryGetValue(name, out var g)) { g = Geometry.Parse(Data.TryGetValue(name, out var d) ? d : Data["sliders"]); g.Freeze(); cache[name] = g; }
        var path = new Path { Data = g, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 24, Height = 24 };
        if (stroke != null) path.Stroke = stroke;
        // no brush given: the colour of the text around it (the menu's entries change with hover and choice)
        if (stroke == null) path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding { RelativeSource = System.Windows.Data.RelativeSource.Self, Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty) });
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } }, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center };
    }
}
