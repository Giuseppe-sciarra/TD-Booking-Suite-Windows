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

    /// <summary>Every «PDF» and «Stampa» of the program does the same thing: the document becomes a PDF in the Download
    /// folder (as the program of before did), and the line on top says where. No print dialog. The pages are laid out for
    /// A4 and kept as they look on screen (logo, fonts), one picture a page.</summary>
    public static void SavePdf(FlowDocument doc, string name)
    {
        const double W = 793.7, H = 1122.5, SCALE = 2;  // A4 at 96 dpi, drawn at 192
        doc.PageWidth = W; doc.PageHeight = H; doc.ColumnWidth = W;
        var pages = ((IDocumentPaginatorSource)doc).DocumentPaginator; pages.PageSize = new Size(W, H); pages.ComputePageCount();
        var jpegs = new List<byte[]>(); int pw = (int)(W * SCALE), ph = (int)(H * SCALE);
        for (int i = 0; i < pages.PageCount; i++)
        {
            var page = pages.GetPage(i); var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H)); dc.DrawRectangle(new VisualBrush(page.Visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, W, H)); }
            dv.Transform = new ScaleTransform(SCALE, SCALE);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 }; enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new System.IO.MemoryStream(); enc.Save(ms); jpegs.Add(ms.ToArray());
        }
        Downloads.Save(name + ".pdf", PdfOfPictures(jpegs, pw, ph, name));
    }

    /// <summary>A PDF whose pages are JPEG pictures filling an A4 page each.</summary>
    static byte[] PdfOfPictures(List<byte[]> jpegs, int w, int h, string title)
    {
        static byte[] A(string s) => System.Text.Encoding.ASCII.GetBytes(s);
        var n = jpegs.Count; var objs = new List<byte[]> { A("<< /Type /Catalog /Pages 2 0 R >>"), A($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + 3 * i} 0 R"))}] /Count {n} >>") };
        for (int i = 0; i < n; i++)
        {
            var content = A("q 595.28 0 0 841.89 0 0 cm /Im0 Do Q");
            objs.Add(A($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /XObject << /Im0 {5 + 3 * i} 0 R >> >> /Contents {4 + 3 * i} 0 R >>"));
            objs.Add(A($"<< /Length {content.Length} >>\nstream\n").Concat(content).Concat(A("\nendstream")).ToArray());
            objs.Add(A($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegs[i].Length} >>\nstream\n").Concat(jpegs[i]).Concat(A("\nendstream")).ToArray());
        }
        var safe = new string(title.Select(ch => ch is >= ' ' and < (char)127 && ch is not ('(' or ')' or '\\') ? ch : ' ').ToArray());
        objs.Add(A($"<< /Title ({safe}) /Producer (TD Booking Suite) >>"));
        var o = new List<byte>(); o.AddRange(A("%PDF-1.4\n%")); o.AddRange(new byte[] { 0xe2, 0xe3, 0xcf, 0xd3 }); o.Add((byte)'\n'); var at = new List<int>();
        for (int i = 0; i < objs.Count; i++) { at.Add(o.Count); o.AddRange(A($"{i + 1} 0 obj\n")); o.AddRange(objs[i]); o.AddRange(A("\nendobj\n")); }
        var x = o.Count; o.AddRange(A($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n")); foreach (var a in at) o.AddRange(A($"{a:0000000000} 00000 n \n"));
        o.AddRange(A($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R /Info {objs.Count} 0 R >>\nstartxref\n{x}\n%%EOF\n"));
        return o.ToArray();
    }
}

/// <summary>What the program gives as a file (a PDF, the data of a client, a photo): always into the Download folder of
/// this PC, under a name not taken, with the same line on top telling where it is and a button to open it.</summary>
public static class Downloads
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    static extern string SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token);
    static string Folder()
    {
        try { return SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero); }
        catch (Exception) { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
    }
    public static string Save(string name, byte[] data)
    {
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '-');
        var dir = Folder(); System.IO.Directory.CreateDirectory(dir);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name); var ext = System.IO.Path.GetExtension(name); var path = System.IO.Path.Combine(dir, name);
        for (int i = 2; System.IO.File.Exists(path); i++) path = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
        System.IO.File.WriteAllBytes(path, data);
        Views.Shell.Instance?.Saved(path);
        return path;
    }
}
