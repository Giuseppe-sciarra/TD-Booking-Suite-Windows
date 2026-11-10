using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TD.Core.Domain;

/// <summary>core/importer.py, the reading: the title row and the data rows of a CSV or Excel (.xlsx) file, every cell
/// a string, with the limits that keep a crafted file from filling the memory.</summary>
public static class Importer
{
    public const int MAX_BYTES = 20 * 1024 * 1024, MAX_ROWS = 20000, MAX_COLUMNS = 60, MAX_CELL = 4000, MAX_LINES = 200000, MAX_TEXT = 25_000_000;

    sealed class Rows
    {
        public readonly List<List<string>> List = new(); int lines; long size;
        public void Add(IEnumerable<string> cells)
        {
            if (++lines > MAX_LINES) throw new BookingException("Il file ha troppe righe, anche vuote: dividilo in più file.");
            var row = cells.Take(MAX_COLUMNS).Select(c => { var s = (c ?? "").Trim(); return s.Length > MAX_CELL ? s[..MAX_CELL] : s; }).ToList();
            if (row.All(c => c == "")) return;
            if (List.Count > MAX_ROWS + 10) throw new BookingException($"Il file ha più di {MAX_ROWS} righe: se ne importano al massimo {MAX_ROWS} alla volta, dividilo in più file.");
            size += row.Sum(c => c.Length);
            if (size > MAX_TEXT) throw new BookingException("Il file è troppo grande una volta letto: dividilo in più file.");
            List.Add(row);
        }
    }

    /// <summary>plain(): a column title without spaces, accents and capitals.</summary>
    public static string Plain(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in (title ?? "").Normalize(NormalizationForm.FormKD)) if (ch < 128) sb.Append(ch);
        return Regex.Replace(sb.ToString().ToLowerInvariant(), "[^a-z0-9]", "");
    }
    public static string Line(string v, int limit) { var s = Regex.Replace(v ?? "", @"[\x00-\x1f\x7f]", " ").Trim(); return s.Length > limit ? s[..limit] : s; }
    /// <summary>name(): ROSSI MARIA from the old programs becomes Rossi Maria.</summary>
    public static string Name(string v)
    {
        v = Line(v, 100);
        if (!v.Any(char.IsLetter) || v.Any(char.IsLower)) return v;
        var sb = new StringBuilder(); var start = true;
        foreach (var ch in v) { sb.Append(char.IsLetter(ch) ? start ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch) : ch); start = !char.IsLetter(ch); }
        return sb.ToString();
    }

    public static (List<string> Head, List<List<string>> Rows) Read(byte[] raw)
    {
        List<List<string>> rows;
        if (raw.Length >= 2 && raw[0] == 'P' && raw[1] == 'K') rows = XlsxRows(raw);
        else if (raw.Length >= 8 && raw.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })) throw new BookingException("È un file Excel del vecchio formato (.xls): aprilo con Excel e salvalo come .xlsx o .csv.");
        else rows = CsvRows(raw);
        // some programs put a title line ("Elenco clienti al ...") above the column titles
        var first = rows.Take(10).ToList().FindIndex(r => r.Count(c => c != "") >= 2); rows = rows.Skip(Math.Max(first, 0)).ToList();
        if (rows.Count < 2) throw new BookingException("Nel file non ci sono righe da importare: servono la riga dei titoli e almeno un cliente.");
        if (rows.Count - 1 > MAX_ROWS) throw new BookingException($"Il file ha {rows.Count - 1} righe: se ne importano al massimo {MAX_ROWS} alla volta, dividilo in più file.");
        var width = rows.Max(r => r.Count);
        var head = Enumerable.Range(0, width).Select(i => i < rows[0].Count && rows[0][i] != "" ? rows[0][i] : $"Colonna {i + 1}").ToList();
        return (head, rows.Skip(1).Select(r => r.Concat(Enumerable.Repeat("", width - r.Count)).ToList()).ToList());
    }

    // ---------- CSV
    /// <summary>Python's csv.reader, dialect excel: quotes around a cell, doubled inside it, a new line inside quotes kept.</summary>
    static IEnumerable<List<string>> Csv(string text, char delimiter)
    {
        var row = new List<string>(); var cell = new StringBuilder(); var quoted = false; var any = false;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; }
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) { quoted = true; any = true; }
            else if (ch == delimiter) { row.Add(cell.ToString()); cell.Clear(); any = true; }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (any || cell.Length > 0) row.Add(cell.ToString());
                yield return row; row = new(); cell.Clear(); any = false;
            }
            else { cell.Append(ch); any = true; }
        }
        if (any || cell.Length > 0) { row.Add(cell.ToString()); yield return row; }
    }
    /// <summary>The separator that splits the first lines into the same number of columns most often.</summary>
    static char Delimiter(string text)
    {
        var top = string.Join("\n", (text.Length > 65536 ? text[..65536] : text).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).Take(30));
        (int Times, int Width, char D) best = (0, 0, ',');
        foreach (var d in ";,\t|")
        {
            var counts = Csv(top, d).Where(r => r.Count > 1).GroupBy(r => r.Count).Select(g => (Width: g.Key, Times: g.Count())).OrderByDescending(g => g.Times).FirstOrDefault();
            if (counts.Times > 0 && (counts.Times, counts.Width, d).CompareTo(best) > 0) best = (counts.Times, counts.Width, d);
        }
        return best.D;
    }
    static List<List<string>> CsvRows(byte[] raw)
    {
        string text;
        if (raw.Length >= 2 && (raw[0] == 0xff && raw[1] == 0xfe || raw[0] == 0xfe && raw[1] == 0xff)) text = (raw[0] == 0xff ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(raw, 2, raw.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(raw); if (text.Length > 0 && text[0] == '﻿') text = text[1..]; }
            catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding(1252).GetString(raw); }
        }
        text = text.Replace("\0", ""); var outp = new Rows();
        foreach (var row in Csv(text, Delimiter(text))) outp.Add(row);
        return outp.List;
    }

    // ---------- Excel
    static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly HashSet<int> DATE_FORMATS = new() { 14, 15, 16, 17, 22 };  // Excel's own date formats; 18-21 and 45-47 are times
    /// <summary>3331234567 and not 3.331234567E9, 45000 and not 45000.0: Excel keeps numbers as floats.</summary>
    static string Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f == Math.Floor(f) && Math.Abs(f) < 1e15 ? ((long)f).ToString(CultureInfo.InvariantCulture) : value;
    static string ExcelDate(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 && n < 2958466 ? new DateTime(1899, 12, 30).AddDays((int)n).ToString("dd/MM/yyyy") : Number(value);

    /// <summary>The first sheet of an .xlsx, without libraries: shared strings, inline strings, numbers as written, dates as 31/12/2024.</summary>
    static List<List<string>> XlsxRows(byte[] raw)
    {
        try
        {
            using var z = new ZipArchive(new MemoryStream(raw), ZipArchiveMode.Read);
            Stream Part(string name, long limit)
            {
                var e = z.GetEntry(name) ?? throw new InvalidDataException(name);
                if (e.Length > limit) throw new BookingException("Il file Excel è troppo grande una volta aperto: dividilo in più file.");
                return e.Open();
            }
            XElement Xml(string name) { using var s = Part(name, 5 * 1024 * 1024); return XDocument.Load(s).Root; }
            var sheet = Xml("xl/workbook.xml").Element(M + "sheets").Element(M + "sheet");
            var rels = Xml("xl/_rels/workbook.xml.rels").Elements().ToDictionary(r => (string)r.Attribute("Id"), r => (string)r.Attribute("Target"));
            var target = rels[(string)sheet.Attribute(R + "id")].TrimStart('/'); if (!target.StartsWith("xl/")) target = "xl/" + target;
            var shared = new List<string>(); long total = 0;
            if (z.GetEntry("xl/sharedStrings.xml") != null)
            {
                using var s = Part("xl/sharedStrings.xml", 80 * 1024 * 1024); using var xr = XmlReader.Create(s);
                // ReadFrom leaves the reader on what follows the element: no Read() after it, or every other one is lost
                for (var more = xr.Read(); more;)
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "si") more = xr.Read();
                    else
                    {
                        var el = (XElement)XNode.ReadFrom(xr);
                        var text = string.Concat(el.Elements(M + "t").Concat(el.Elements(M + "r").Elements(M + "t")).Select(t => t.Value)); if (text.Length > MAX_CELL) text = text[..MAX_CELL];
                        total += text.Length; shared.Add(text);
                        if (total > MAX_TEXT || shared.Count > 2 * MAX_LINES) throw new BookingException("Il file è troppo grande una volta letto: dividilo in più file.");
                    }
            }
            var dates = new HashSet<int>();
            if (z.GetEntry("xl/styles.xml") != null)
            {
                var styles = Xml("xl/styles.xml"); var custom = styles.Descendants(M + "numFmt").ToDictionary(n => (int)n.Attribute("numFmtId"), n => (string)n.Attribute("formatCode") ?? "");
                bool IsDate(int fid) => custom.TryGetValue(fid, out var code) ? Regex.IsMatch(Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "").ToLowerInvariant(), "[dy]") : DATE_FORMATS.Contains(fid);
                var xfs = styles.Element(M + "cellXfs")?.Elements(M + "xf").ToList();
                if (xfs != null) for (int i = 0; i < xfs.Count; i++) if (IsDate((int?)xfs[i].Attribute("numFmtId") ?? 0)) dates.Add(i);
            }
            var outp = new Rows();
            using (var s = Part(target, 200 * 1024 * 1024)) using (var xr = XmlReader.Create(s))
                for (var more = xr.Read(); more;)
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "row") { more = xr.Read(); continue; }
                    var row = (XElement)XNode.ReadFrom(xr); var cells = new Dictionary<int, string>(); var col = -1;
                    foreach (var cEl in row.Elements(M + "c"))
                    {
                        var m = Regex.Match((string)cEl.Attribute("r") ?? "", "^[A-Z]{1,3}");
                        col = m.Success ? m.Value.Aggregate(0, (a, ch) => a * 26 + (ch - 64)) - 1 : col + 1;
                        if (col >= MAX_COLUMNS) continue;
                        var kind = (string)cEl.Attribute("t"); var value = cEl.Element(M + "v")?.Value ?? "";
                        if (kind == "s") value = value != "" ? shared[int.Parse(value)] : "";
                        else if (kind == "inlineStr") value = string.Concat(cEl.Descendants(M + "t").Select(t => t.Value));
                        else if (kind == "e") value = "";
                        else if (kind is null or "n" && value != "") value = dates.Contains((int?)cEl.Attribute("s") ?? 0) ? ExcelDate(value) : Number(value);
                        cells[col] = value;
                    }
                    outp.Add(cells.Count > 0 ? Enumerable.Range(0, cells.Keys.Max() + 1).Select(i => cells.GetValueOrDefault(i, "")) : Enumerable.Empty<string>());
                }
            return outp.List;
        }
        catch (BookingException) { throw; }
        catch (Exception) { throw new BookingException("File Excel non leggibile: aprilo e salvalo di nuovo come .xlsx o come .csv."); }
    }
}
