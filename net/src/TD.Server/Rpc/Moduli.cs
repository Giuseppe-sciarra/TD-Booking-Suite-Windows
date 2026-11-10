using System.Globalization;
using System.Text.Json.Nodes;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>A field of a form as the window draws it: what Django's form.html reads from a bound field.</summary>
public sealed class Fld
{
    public string Nome { get; init; }
    public string Etichetta { get; set; }
    public string Aiuto { get; set; } = "";
    /// <summary>text, area, number, decimal, check, select, multi, radio, date, time, datetime, hidden.</summary>
    public string Tipo { get; init; } = "text";
    public object Valore { get; set; }
    public IEnumerable<object> Scelte { get; set; }
    public bool Largo { get; set; }
    public int Max { get; set; }
    public string Segnaposto { get; set; } = "";
    public bool Disattivo { get; set; }
    /// <summary>The service form of the administrator: the kinds of booking that use the field, and its label and help for each.</summary>
    public string[] Tipi { get; set; }
    public Dictionary<string, string> Etichette { get; set; }
    public Dictionary<string, string> Aiuti { get; set; }
}

/// <summary>Reads what a form sent as Django's fields clean it, with the same words for what is wrong.</summary>
public sealed class Posted(Call c, JsonObject values)
{
    public readonly Dictionary<string, string> Errors = new();
    public const string REQUIRED = "Questo campo è obbligatorio.";
    public bool Has(string name) => values[name] != null;
    public void Error(string name, string text) { if (!Errors.ContainsKey(name)) Errors[name] = text; else if (name == "") Errors[name] += " " + text; }
    public void Whole(string text) => Error("", text);
    public string Raw(string name) => values[name] is JsonValue v ? v.TryGetValue<string>(out var s) ? s.Trim() : v.ToString() : "";
    public string Text(string name, bool required = false, int max = 0)
    {
        var s = Raw(name);
        if (s == "" && required) Error(name, REQUIRED);
        if (max > 0 && s.Length > max) Error(name, $"Assicurati che questo valore non contenga più di {max} caratteri (ne ha {s.Length}).");
        return s;
    }
    public bool Bool(string name) => Json.Truthy(values[name]);
    public int? Int(string name, bool required = false, int? min = 0, int? max = 2147483647)
    {
        var s = Raw(name);
        if (s == "") { if (required) Error(name, REQUIRED); return null; }
        if (!int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) { Error(name, "Inserisci un numero intero."); return null; }
        if (min.HasValue && n < min) { Error(name, $"Assicurati che questo valore sia maggiore o uguale a {min}."); return null; }
        if (max.HasValue && n > max) { Error(name, $"Assicurati che questo valore sia minore o uguale a {max}."); return null; }
        return n;
    }
    /// <summary>A price written as in Italy: 25,50 (a dot is taken for the comma when there is no comma).</summary>
    public decimal? Dec(string name, bool required = false, int digits = 8, int places = 2)
    {
        var s = Raw(name).Replace(" ", "");
        if (s == "") { if (required) Error(name, REQUIRED); return null; }
        if (s.Contains(',')) s = s.Replace(".", "").Replace(',', '.');
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) { Error(name, "Inserisci un numero."); return null; }
        if (d != Math.Round(d, places)) { Error(name, $"Assicurati che non vi siano più di {places} cifre decimali."); return null; }
        if (Math.Abs(Math.Truncate(d)) >= (decimal)Math.Pow(10, digits - places)) { Error(name, $"Assicurati che non vi siano più di {digits - places} cifre prima della virgola."); return null; }
        return Math.Round(d, places);
    }
    public TimeSpan? Time(string name, bool required = false)
    {
        var s = Raw(name);
        if (s == "") { if (required) Error(name, REQUIRED); return null; }
        if (TimeOnly.TryParseExact(s, new[] { "HH:mm", "H:mm", "HH:mm:ss", "HH.mm", "H.mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t.ToTimeSpan();
        Error(name, "Inserisci un'ora valida."); return null;
    }
    public DateOnly? Date(string name, bool required = false)
    {
        var s = Raw(name);
        if (s == "") { if (required) Error(name, REQUIRED); return null; }
        if (DateOnly.TryParseExact(s, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        Error(name, "Inserisci una data valida."); return null;
    }
    /// <summary>datetime-local in the business's zone → the instant in UTC.</summary>
    public DateTime? Moment(string name, bool required = false)
    {
        var s = Raw(name);
        if (s == "") { if (required) Error(name, REQUIRED); return null; }
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && c.C.Cal.Local(DateOnly.FromDateTime(d), d.TimeOfDay) is DateTime utc) return utc;
        Error(name, "Inserisci una data/ora valida."); return null;
    }
    public string Choice(string name, IEnumerable<string> allowed, bool required = false, string empty = "")
    {
        var s = Raw(name);
        if (s == "") { if (required) Error(name, REQUIRED); return empty; }
        if (!allowed.Contains(s)) { Error(name, $"Scegli un'opzione valida. {s} non è tra quelle disponibili."); return empty; }
        return s;
    }
    /// <summary>ModelChoiceField: the id of a row of that table (where: the queryset), null when empty.</summary>
    public Guid? Ref(string name, string table, bool required = false, string where = "true")
    {
        var s = Raw(name);
        if (s == "") { if (required) Error(name, REQUIRED); return null; }
        if (Guid.TryParse(s, out var id) && c.C.S.Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {table} WHERE id=@id AND {where})", new { id })) return id;
        Error(name, "Scegli un'opzione valida. La scelta effettuata non compare tra quelle disponibili."); return null;
    }
    public List<Guid> Refs(string name, string table, string where = "true")
    {
        var ids = (values[name] as JsonArray)?.Select(v => Guid.TryParse(v?.ToString(), out var g) ? g : (Guid?)null).Where(g => g.HasValue).Select(g => g.Value).Distinct().ToArray() ?? Array.Empty<Guid>();
        var found = c.C.S.Query<Guid>($"SELECT id FROM {table} WHERE id = ANY(@i) AND {where}", new { i = ids }).ToHashSet();
        if (found.Count < ids.Length) Error(name, "Scegli un'opzione valida. Una delle scelte non compare tra quelle disponibili.");
        return ids.Where(found.Contains).ToList();
    }
    public List<string> Many(string name) => (values[name] as JsonArray)?.Select(v => v?.ToString() ?? "").ToList() ?? new();
    public void Check() { if (Errors.Count > 0) throw new FormErrors(Errors); }
}
