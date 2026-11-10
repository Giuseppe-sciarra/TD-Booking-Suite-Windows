using System.Globalization;
using System.Text;

namespace TD.Core;

/// <summary>core/pdf.py: a PDF of text, written here by hand: A4 pages, Helvetica, the letters of the receipts (Latin-1
/// and €). Enough for the Cassa's summaries; a table is lines with columns at fixed positions. No library.</summary>
public sealed class Pdf(string title)
{
    public const double W = 595.28, H = 841.89, MARGIN = 48, LINE = 14;
    // Helvetica widths (per 1000 em) of the characters 32..126; other letters count as 556
    static readonly int[] Widths = new[] { 278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278 }.Concat(Enumerable.Repeat(556, 10)).Concat(new[] { 278, 278, 584, 584, 584, 556, 1015,
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
        278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584 }).ToArray();
    static readonly Encoding Ansi = MakeAnsi();
    static Encoding MakeAnsi() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?")); }

    public static double Width(string s, double size) => s.Sum(ch => ch is >= (char)32 and < (char)127 ? Widths[ch - 32] : 556) * size / 1000;
    static byte[] Esc(string s)
    {
        var o = new List<byte>();
        foreach (var b in Ansi.GetBytes(s)) { if (b is (byte)'\\' or (byte)'(' or (byte)')') o.Add((byte)'\\'); o.Add(b); }
        return o.ToArray();
    }
    static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    readonly List<List<byte[]>> pages = new(); double y;
    public void Page() { pages.Add(new()); y = H - MARGIN; }
    /// <summary>A piece of text at x,y (points from the bottom left); right: ending at x.</summary>
    public void Put(double x, double py, string s, int size = 10, bool bold = false, bool right = false)
    {
        if (right) x -= Width(s, size);
        pages[^1].Add(Cat(A($"BT /F{(bold ? 2 : 1)} {size} Tf {F1(x)} {F1(py)} Td ("), Esc(s), A(") Tj ET")));
    }
    /// <summary>A new page unless n more lines fit on this one.</summary>
    public void Room(double n = 1) { if (pages.Count == 0 || y - n * LINE < MARGIN + LINE) Page(); }
    /// <summary>One row: cells as (x, text, right); a new page when the one in hand is full.</summary>
    public void Line(IEnumerable<(double X, string Text, bool Right)> cells, int size = 10, bool bold = false, double gap = LINE)
    {
        Room(); y -= gap;
        foreach (var (x, text, right) in cells) Put(x, y, text, size, bold, right);
    }
    public void Line(double x, string text, int size = 10, bool bold = false, double gap = LINE) => Line(new[] { (x, text, false) }, size, bold, gap);
    public void Rule() { Room(); y -= 6; pages[^1].Add(A($"0.7 w {F1(MARGIN)} {F1(y)} m {F1(W - MARGIN)} {F1(y)} l S")); y -= 2; }
    public void Space(double n = 1) => y -= n * LINE;

    public byte[] Render()
    {
        var n = pages.Count;
        var objs = new List<byte[]>
        {
            A("<< /Type /Catalog /Pages 2 0 R >>"), A($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{5 + 2 * i} 0 R"))}] /Count {n} >>"),
            A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"), A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
        };
        for (int i = 0; i < n; i++)
        {
            var label = $"Pagina {i + 1} di {n}";
            var foot = Cat(A($"BT /F1 8 Tf {F1(W - MARGIN - Width(label, 8))} {F1(MARGIN / 2)} Td ("), Esc(label), A(") Tj ET"));
            var body = new List<byte>(); foreach (var op in pages[i].Append(foot)) { if (body.Count > 0) body.Add((byte)'\n'); body.AddRange(op); }
            objs.Add(A($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W.ToString("0.00", CultureInfo.InvariantCulture)} {H.ToString("0.00", CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + 2 * i} 0 R >>"));
            objs.Add(Cat(A($"<< /Length {body.Count} >>\nstream\n"), body.ToArray(), A("\nendstream")));
        }
        objs.Add(Cat(A("<< /Title ("), Esc(title), A(") /Producer (TD Booking Suite) >>")));
        var o = new List<byte>(); o.AddRange(A("%PDF-1.4\n%")); o.AddRange(new byte[] { 0xe2, 0xe3, 0xcf, 0xd3 }); o.Add((byte)'\n'); var at = new List<int>();
        for (int i = 0; i < objs.Count; i++) { at.Add(o.Count); o.AddRange(A($"{i + 1} 0 obj\n")); o.AddRange(objs[i]); o.AddRange(A("\nendobj\n")); }
        var x = o.Count; o.AddRange(A($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n")); foreach (var a in at) o.AddRange(A($"{a:0000000000} 00000 n \n"));
        o.AddRange(A($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R /Info {objs.Count} 0 R >>\nstartxref\n{x}\n%%EOF\n"));
        return o.ToArray();
    }
}
