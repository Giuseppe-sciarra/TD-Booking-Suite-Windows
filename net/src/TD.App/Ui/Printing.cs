using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TD.App.Ui;

/// <summary>What the web program printed with the browser (Stampa, «PDF»): a document laid out for A4 and sent to the
/// Windows print dialog, where «Microsoft Print to PDF» saves it as a file.</summary>
public static class Printing
{
    public static FlowDocument Doc() => new() { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, PagePadding = new Thickness(48), ColumnWidth = double.MaxValue, Foreground = Brushes.Black, Background = Brushes.White };
    public static Paragraph P(string text, double size = 11, bool bold = false, Brush color = null, double below = 4) =>
        new(new Run(text ?? "")) { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? Brushes.Black, Margin = new Thickness(0, 0, 0, below) };
    public static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(90, 82, 102));

    /// <summary>A table: the widths in stars, the head in grey capitals, numbers on the right (right: the columns).</summary>
    public static Table Table(double[] widths, string[] head, IEnumerable<string[]> rows, int[] right = null, string[] foot = null)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 8) }; right ??= Array.Empty<int>();
        foreach (var w in widths) t.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
        var g = new TableRowGroup(); t.RowGroups.Add(g);
        TableRow Row(string[] cells, bool headRow, bool footRow)
        {
            var r = new TableRow();
            for (int i = 0; i < cells.Length; i++)
                r.Cells.Add(new TableCell(new Paragraph(new Run(cells[i] ?? "")) { TextAlignment = right.Contains(i) ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0),
                    FontSize = headRow ? 9 : footRow ? 13 : 11, FontWeight = headRow || footRow ? FontWeights.SemiBold : FontWeights.Normal, Foreground = headRow ? Grey : Brushes.Black })
                { Padding = new Thickness(6, 5, 6, 5), BorderBrush = footRow ? Brushes.Black : Brushes.LightGray, BorderThickness = new Thickness(0, footRow ? 1.5 : 0, 0, footRow ? 0 : 0.75) });
            return r;
        }
        g.Rows.Add(Row(head.Select(h => h.ToUpperInvariant()).ToArray(), true, false));
        foreach (var r in rows) g.Rows.Add(Row(r, false, false));
        if (foot != null) g.Rows.Add(Row(foot, false, true));
        return t;
    }

    /// <summary>The Windows print dialog; false when the user closes it.</summary>
    public static bool Print(FlowDocument doc, string title)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return false;
        doc.PageWidth = dlg.PrintableAreaWidth; doc.PageHeight = dlg.PrintableAreaHeight; doc.ColumnWidth = dlg.PrintableAreaWidth;
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, title);
        return true;
    }
}
