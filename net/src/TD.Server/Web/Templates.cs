using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Domain;

namespace TD.Server.Web;

/// <summary>The public pages are the Python program's own templates (suite/templates, built into the server), drawn by this
/// small reader of the Django template language: what those pages use of it, no more. So a page for the clients is the
/// same HTML here and there. extends/block, if/elif/else, for/empty, include with, url, static, translate,
/// blocktranslate, csrf_token, now, and the filters of the public pages.</summary>
public sealed class Tpl
{
    /// <summary>A value already HTML: not escaped again.</summary>
    public sealed record Safe(string Html) { public override string ToString() => Html; }
    public sealed class Env
    {
        public string Lang = "it"; public string Csrf = "";
        public Func<string, object[], string> Url = (name, args) => "#";
        public string Version = "";
    }

    abstract class Node { public abstract void Render(Scope s, StringBuilder o); }
    sealed class Scope(Env env, Dictionary<string, List<Node>> blocks)
    {
        public readonly Env Env = env; public readonly Dictionary<string, List<Node>> Blocks = blocks;
        readonly List<Dictionary<string, object>> stack = new();
        public void Push(Dictionary<string, object> d) => stack.Add(d);
        public void Pop() => stack.RemoveAt(stack.Count - 1);
        public void Set(string k, object v) => stack[^1][k] = v;
        public bool TryGet(string k, out object v) { for (int i = stack.Count - 1; i >= 0; i--) if (stack[i].TryGetValue(k, out v)) return true; v = null; return false; }
    }

    static readonly ConcurrentDictionary<string, (List<Node> Nodes, string Parent)> cache = new();
    static string Source(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tpl/" + name) ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s, Encoding.UTF8); return r.ReadToEnd();
    }
    static (List<Node> Nodes, string Parent) Load(string name) => cache.GetOrAdd(name, n => new Parser(Source(n)).Parse());

    public static string Render(string name, Dictionary<string, object> context, Env env)
    {
        // the child's blocks win over the parent's, down the chain of extends
        var blocks = new Dictionary<string, List<Node>>(); var (nodes, parent) = Load(name);
        void Collect(List<Node> list) { foreach (var b in list.OfType<BlockNode>()) blocks.TryAdd(b.Name, b.Body); }
        Collect(nodes);
        while (parent != null) { (nodes, parent) = Load(parent); Collect(nodes); }
        var scope = new Scope(env, blocks); scope.Push(context); var o = new StringBuilder();
        foreach (var n in nodes) n.Render(scope, o);
        return o.ToString();
    }

    // ---------- values
    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
    static object Unwrap(object v) => v is JsonValue j ? j.TryGetValue<string>(out var s) ? s : j.TryGetValue<bool>(out var b) ? b : j.TryGetValue<long>(out var l) ? l : j.TryGetValue<decimal>(out var d) ? d : j.ToString() : v;
    public static bool Truthy(object v) => Unwrap(v) switch
    {
        null => false, bool b => b, string s => s != "", Safe s => s.Html != "", long l => l != 0, int i => i != 0, decimal d => d != 0, double d => d != 0,
        JsonObject o => o.Count > 0, JsonArray a => a.Count > 0, ICollection c => c.Count > 0, IEnumerable e => e.Cast<object>().Any(), _ => true,
    };
    static string Text(object v) => Unwrap(v) switch
    {
        null => "", string s => s, Safe s => s.Html, bool b => b ? "True" : "False", decimal d => d.ToString(CultureInfo.InvariantCulture), double d => d.ToString(CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss"), DateOnly t => t.ToString("yyyy-MM-dd"), JsonNode n => n.ToJsonString(), var x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "",
    };
    static object Lookup(object o, string key)
    {
        switch (o)
        {
            case null: return null;
            case JsonObject jo: return jo[key];
            case JsonArray ja: return int.TryParse(key, out var i) && i >= 0 && i < ja.Count ? ja[i] : null;
            case IDictionary<string, object> d: return d.TryGetValue(key, out var v) ? v : null;
            case IDictionary d: return d.Contains(key) ? d[key] : null;
            case string: return null;
            case IList l: return int.TryParse(key, out var k) && k >= 0 && k < l.Count ? l[k] : null;
            case System.Runtime.CompilerServices.ITuple t: return int.TryParse(key, out var n) && n >= 0 && n < t.Length ? t[n] : null;
        }
        var type = o.GetType(); var pascal = string.Concat(key.Split('_').Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p));
        var prop = type.GetProperty(key) ?? type.GetProperty(pascal, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null) return prop.GetValue(o);
        var field = type.GetField(key) ?? type.GetField(pascal, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return field?.GetValue(o);
    }
    static IEnumerable<object> Items(object v) => Unwrap(v) switch
    {
        null => Enumerable.Empty<object>(), string s => s.Select(ch => (object)ch.ToString()), JsonArray a => a.Cast<object>(), JsonObject o => o.Select(k => (object)k.Key), IDictionary<string, object> d => d.Keys.Cast<object>(),
        IEnumerable e => e.Cast<object>(), _ => Enumerable.Empty<object>(),
    };

    /// <summary>An expression: a literal or a dotted name, then its filters.</summary>
    sealed class Expr
    {
        readonly object literal; readonly string[] path; readonly List<(string Name, Expr Arg)> filters = new();
        public Expr(string text)
        {
            var parts = SplitTop(text.Trim(), '|'); var head = parts[0].Trim();
            if (head.Length >= 2 && (head[0] == '"' || head[0] == '\'') && head[^1] == head[0]) literal = head[1..^1];
            else if (decimal.TryParse(head, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) literal = head.Contains('.') ? n : (long)n;
            else if (head is "True" or "False") literal = head == "True";
            else if (head == "None") path = Array.Empty<string>();
            else path = head.Split('.');
            foreach (var f in parts.Skip(1)) { var at = IndexTop(f, ':'); filters.Add(at < 0 ? (f.Trim(), null) : (f[..at].Trim(), new Expr(f[(at + 1)..]))); }
        }
        public object Value(Scope s)
        {
            object v = literal;
            if (path != null) { if (path.Length > 0 && s.TryGet(path[0], out v)) foreach (var k in path.Skip(1)) v = Lookup(v, k); else v = null; }
            foreach (var (name, arg) in filters) v = Filter(name, v, arg?.Value(s), s.Env);
            return v;
        }
    }
    static int IndexTop(string s, char sep) { char q = '\0'; for (int i = 0; i < s.Length; i++) { var ch = s[i]; if (q != '\0') { if (ch == q) q = '\0'; } else if (ch is '"' or '\'') q = ch; else if (ch == sep) return i; } return -1; }
    static List<string> SplitTop(string s, char sep) { var o = new List<string>(); for (var at = IndexTop(s, sep); at >= 0; at = IndexTop(s, sep)) { o.Add(s[..at]); s = s[(at + 1)..]; } o.Add(s); return o; }
    static List<string> Words(string s) { var o = new List<string>(); var b = new StringBuilder(); char q = '\0'; foreach (var ch in s) { if (q != '\0') { b.Append(ch); if (ch == q) q = '\0'; } else if (ch is '"' or '\'') { q = ch; b.Append(ch); } else if (char.IsWhiteSpace(ch)) { if (b.Length > 0) { o.Add(b.ToString()); b.Clear(); } } else b.Append(ch); } if (b.Length > 0) o.Add(b.ToString()); return o; }

    static readonly string[] MONTHS_IT = { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" };
    static readonly string[] MONTHS_EN = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    static readonly string[] DAYS_IT = { "lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato", "domenica" };
    static readonly string[] DAYS_EN = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
    /// <summary>Django's date format letters, in the page's language.</summary>
    public static string Date(object value, string format, string lang)
    {
        DateTime t; switch (Unwrap(value)) { case DateTime d: t = d; break; case DateOnly d: t = d.ToDateTime(TimeOnly.MinValue); break; case TimeSpan ts: t = DateTime.MinValue.Add(ts); break; case TimeOnly to: t = DateTime.MinValue.Add(to.ToTimeSpan()); break;
            case string str when DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p): t = p; break; default: return ""; }
        var en = lang == "en"; var months = en ? MONTHS_EN : MONTHS_IT; var days = en ? DAYS_EN : DAYS_IT; var wd = ((int)t.DayOfWeek + 6) % 7; var o = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch == '\\' && i + 1 < format.Length) { o.Append(format[++i]); continue; }
            o.Append(ch switch
            {
                'd' => t.Day.ToString("00"), 'j' => t.Day.ToString(), 'm' => t.Month.ToString("00"), 'n' => t.Month.ToString(), 'Y' => t.Year.ToString("0000"), 'y' => (t.Year % 100).ToString("00"), 'H' => t.Hour.ToString("00"), 'G' => t.Hour.ToString(),
                'i' => t.Minute.ToString("00"), 's' => t.Second.ToString("00"), 'l' => days[wd], 'D' => days[wd][..3], 'F' => months[t.Month - 1], 'M' => months[t.Month - 1][..3], 'N' => months[t.Month - 1][..3], _ => ch.ToString(),
            });
        }
        return o.ToString();
    }

    static object Filter(string name, object v, object arg, Env env)
    {
        var u = Unwrap(v);
        switch (name)
        {
            case "default": return Truthy(u) ? v : arg;
            case "default_if_none": return u ?? arg;
            case "lower": return Text(u).ToLowerInvariant();
            case "upper": return Text(u).ToUpperInvariant();
            case "capfirst": { var s = Text(u); return s == "" ? s : char.ToUpperInvariant(s[0]) + s[1..]; }
            case "first": return u is string str ? (str == "" ? "" : str[..1]) : Items(u).FirstOrDefault();
            case "length": return u is string sl ? (long)sl.Length : (long)Items(u).Count();
            case "join": return string.Join(Text(arg), Items(u).Select(Text));
            case "add": return decimal.TryParse(Text(u), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(Text(arg), NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? (object)(long)(a + b) : Text(u) + Text(arg);
            case "date": case "time": return Date(u, Text(arg), env.Lang);
            case "safe": return new Safe(Text(u));
            case "escape": return u;
            case "linebreaksbr": return new Safe(Escape(Text(u)).Replace("\r\n", "\n").Replace("\n", "<br>"));
            case "floatformat": { var d = decimal.TryParse(Text(u), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0; var places = int.TryParse(Text(arg), out var p) ? p : 1; return Math.Round(d, Math.Abs(places), MidpointRounding.ToEven).ToString("F" + Math.Abs(places), CultureInfo.GetCultureInfo(env.Lang == "en" ? "en-US" : "it-IT")); }
            case "pluralize":
                {
                    var parts = Text(arg == null ? "s" : arg).Split(','); var (one, many) = parts.Length > 1 ? (parts[0], parts[1]) : ("", parts[0]);
                    var count = u is string or Safe ? (decimal.TryParse(Text(u), out var n) ? n : 0) : u is IEnumerable e and not string ? e.Cast<object>().Count() : Convert.ToDecimal(u ?? 0, CultureInfo.InvariantCulture);
                    return count == 1 ? one : many;
                }
            case "slice":
                {
                    var parts = Text(arg).Split(':'); int? From = parts[0] != "" ? int.Parse(parts[0]) : null, To = parts.Length > 1 && parts[1] != "" ? int.Parse(parts[1]) : null;
                    if (u is string s) { int sa = Math.Clamp(From is < 0 ? s.Length + From.Value : From ?? 0, 0, s.Length), sz = Math.Clamp(To is < 0 ? s.Length + To.Value : To ?? s.Length, sa, s.Length); return s[sa..sz]; }
                    var list = Items(u).ToList(); int la = Math.Clamp(From is < 0 ? list.Count + From.Value : From ?? 0, 0, list.Count), lz = Math.Clamp(To is < 0 ? list.Count + To.Value : To ?? list.Count, la, list.Count); return list.GetRange(la, lz - la);
                }
            case "json_script": return new Safe($"<script id=\"{Escape(Text(arg))}\" type=\"application/json\">{JsonScript(v)}</script>");
            case "role_label": return Permissions.RoleLabel(Text(u));
            case "other_languages": return u; // PublicHost.Context puts the list (code, name, address) under "request"
            case "truncatechars": { var s = Text(u); var n = int.TryParse(Text(arg), out var k) ? k : s.Length; return s.Length > n ? s[..Math.Max(0, n - 1)] + "…" : s; }
            case "urlencode": return Uri.EscapeDataString(Text(u));
            case "yesno": { var parts = Text(arg ?? "yes,no").Split(','); return Truthy(u) ? parts[0] : parts.ElementAtOrDefault(1) ?? ""; }
        }
        throw new InvalidOperationException("filtro sconosciuto: " + name);
    }
    /// <summary>json_script: the JSON with the characters that could close the script tag written as escapes.</summary>
    static string JsonScript(object v)
    {
        var json = v is JsonNode n ? n.ToJsonString(Py) : JsonSerializer.Serialize(v, Py);
        return json.Replace("<", "\\u003C").Replace(">", "\\u003E").Replace("&", "\\u0026");
    }
    static readonly JsonSerializerOptions Py = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------- conditions
    abstract class Cond { public abstract object Eval(Scope s); }
    sealed class Val(Expr e) : Cond { public override object Eval(Scope s) => e.Value(s); }
    sealed class Not(Cond c) : Cond { public override object Eval(Scope s) => !Truthy(c.Eval(s)); }
    sealed class Bin(string op, Cond a, Cond b) : Cond
    {
        public override object Eval(Scope s)
        {
            if (op == "and") { var l = a.Eval(s); return Truthy(l) ? b.Eval(s) : l; }
            if (op == "or") { var l = a.Eval(s); return Truthy(l) ? l : b.Eval(s); }
            object x = Unwrap(a.Eval(s)), y = Unwrap(b.Eval(s));
            switch (op)
            {
                case "==": return Same(x, y);
                case "!=": return !Same(x, y);
                case "in": return In(x, y);
                case "not in": return !In(x, y);
            }
            if (x == null || y == null) return false;
            var cmp = Num(x, out var dx) && Num(y, out var dy) ? dx.CompareTo(dy) : string.CompareOrdinal(Text(x), Text(y));
            return op switch { "<" => cmp < 0, ">" => cmp > 0, "<=" => cmp <= 0, ">=" => cmp >= 0, _ => throw new InvalidOperationException(op) };
        }
        static bool Num(object v, out decimal d) { d = 0; return v is long or int or decimal or double or short && decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d); }
        static bool Same(object x, object y)
        {
            if (x == null || y == null) return x == null && y == null;
            if (Num(x, out var dx) && Num(y, out var dy)) return dx == dy;
            if (x is bool bx && y is bool by) return bx == by;
            if (x is bool || y is bool) return false;
            return Text(x) == Text(y);
        }
        static bool In(object x, object y) => y switch
        {
            null => false, string s => s.Contains(Text(x)), JsonObject o => o.ContainsKey(Text(x)), IDictionary<string, object> d => d.ContainsKey(Text(x)),
            _ => Items(y).Any(i => Same(x, Unwrap(i))),
        };
    }
    static Cond ParseCond(List<string> w)
    {
        int i = 0;
        Cond Or() { var l = And(); while (i < w.Count && w[i] == "or") { i++; l = new Bin("or", l, And()); } return l; }
        Cond And() { var l = Neg(); while (i < w.Count && w[i] == "and") { i++; l = new Bin("and", l, Neg()); } return l; }
        Cond Neg() { if (i < w.Count && w[i] == "not") { i++; return new Not(Neg()); } return Cmp(); }
        Cond Cmp()
        {
            var l = new Val(new Expr(w[i++]));
            if (i < w.Count && w[i] is "==" or "!=" or "<" or ">" or "<=" or ">=" or "in") { var op = w[i++]; return new Bin(op, l, new Val(new Expr(w[i++]))); }
            if (i + 1 < w.Count && w[i] == "not" && w[i + 1] == "in") { i += 2; return new Bin("not in", l, new Val(new Expr(w[i++]))); }
            return l;
        }
        return Or();
    }

    // ---------- nodes
    sealed class TextNode(string text) : Node { public override void Render(Scope s, StringBuilder o) => o.Append(text); }
    sealed class VarNode(Expr e) : Node { public override void Render(Scope s, StringBuilder o) { var v = e.Value(s); o.Append(Unwrap(v) is Safe safe ? safe.Html : Escape(Text(v))); } }
    sealed class IfNode(List<(Cond Test, List<Node> Body)> branches) : Node
    {
        public override void Render(Scope s, StringBuilder o) { foreach (var (test, body) in branches) if (test == null || Truthy(test.Eval(s))) { foreach (var n in body) n.Render(s, o); return; } }
    }
    sealed class ForNode(string[] names, Expr list, List<Node> body, List<Node> empty) : Node
    {
        public override void Render(Scope s, StringBuilder o)
        {
            var items = Items(list.Value(s)).ToList();
            if (items.Count == 0) { foreach (var n in empty) n.Render(s, o); return; }
            s.TryGet("forloop", out var parent);
            for (int i = 0; i < items.Count; i++)
            {
                var frame = new Dictionary<string, object> { ["forloop"] = new Dictionary<string, object> { ["counter"] = (long)(i + 1), ["counter0"] = (long)i, ["revcounter"] = (long)(items.Count - i), ["first"] = i == 0, ["last"] = i == items.Count - 1, ["parentloop"] = parent } };
                if (names.Length == 1) frame[names[0]] = items[i];
                else for (int k = 0; k < names.Length; k++) frame[names[k]] = Lookup(items[i] is KeyValuePair<string, JsonNode> kv ? new object[] { kv.Key, kv.Value } : items[i], k.ToString());
                s.Push(frame); foreach (var n in body) n.Render(s, o); s.Pop();
            }
        }
    }
    sealed class BlockNode(string name, List<Node> body) : Node
    {
        public string Name => name; public List<Node> Body => body;
        public override void Render(Scope s, StringBuilder o) { foreach (var n in s.Blocks.TryGetValue(name, out var mine) ? mine : body) n.Render(s, o); }
    }
    sealed class IncludeNode(Expr name, List<(string Key, Expr Value)> with) : Node
    {
        public override void Render(Scope s, StringBuilder o)
        {
            var frame = new Dictionary<string, object>(); foreach (var (k, v) in with) frame[k] = v.Value(s);
            s.Push(frame); foreach (var n in Load(Text(name.Value(s))).Nodes) n.Render(s, o); s.Pop();
        }
    }
    sealed class UrlNode(Expr name, List<Expr> args, string into) : Node
    {
        public override void Render(Scope s, StringBuilder o)
        {
            var url = s.Env.Url(Text(name.Value(s)), args.Select(a => Unwrap(a.Value(s))).ToArray());
            if (into != null) s.Set(into, url); else o.Append(Escape(url));
        }
    }
    sealed class StaticNode(Expr path) : Node { public override void Render(Scope s, StringBuilder o) => o.Append(Escape("/static/" + Text(path.Value(s)) + (s.Env.Version != "" ? "?v=" + s.Env.Version : ""))); }
    sealed class TransNode(Expr text, string into) : Node
    {
        public override void Render(Scope s, StringBuilder o) { var t = Tr.T(s.Env.Lang, Text(text.Value(s))); if (into != null) s.Set(into, t); else o.Append(t); }
    }
    /// <summary>blocktranslate with a=b: the words between are the text to translate, with %(a)s where {{ a }} stands.</summary>
    sealed class BlockTransNode(List<(string Key, Expr Value)> with, string text) : Node
    {
        public override void Render(Scope s, StringBuilder o)
        {
            var values = with.ToDictionary(w => w.Key, w => Text(w.Value.Value(s))); var id = Regex.Replace(text, @"\{\{\s*(\w+)\s*\}\}", "%($1)s");
            o.Append(Regex.Replace(Tr.T(s.Env.Lang, id), @"%\((\w+)\)s", m => Escape(values.TryGetValue(m.Groups[1].Value, out var v) ? v : s.TryGet(m.Groups[1].Value, out var x) ? Text(x) : "")));
        }
    }
    sealed class CsrfNode : Node { public override void Render(Scope s, StringBuilder o) => o.Append($"<input type=\"hidden\" name=\"csrfmiddlewaretoken\" value=\"{Escape(s.Env.Csrf)}\">"); }
    sealed class NowNode(string format) : Node { public override void Render(Scope s, StringBuilder o) => o.Append(Escape(Date(DateTime.Now, format, s.Env.Lang))); }
    sealed class LangNode(string into) : Node { public override void Render(Scope s, StringBuilder o) => s.Set(into, s.Env.Lang); }
    sealed class WithNode(List<(string Key, Expr Value)> with, List<Node> body) : Node
    {
        public override void Render(Scope s, StringBuilder o) { var frame = new Dictionary<string, object>(); foreach (var (k, v) in with) frame[k] = v.Value(s); s.Push(frame); foreach (var n in body) n.Render(s, o); s.Pop(); }
    }

    sealed class Parser(string source)
    {
        readonly List<string> tokens = Regex.Split(source, @"(\{%.*?%\}|\{\{.*?\}\}|\{#.*?#\})", RegexOptions.Singleline).ToList(); int at; string parent;
        public (List<Node>, string) Parse() { var nodes = Until(out _); return (nodes, parent); }
        static List<(string, Expr)> Pairs(IEnumerable<string> words) => words.Where(w => w.Contains('=')).Select(w => (w[..w.IndexOf('=')], new Expr(w[(w.IndexOf('=') + 1)..]))).ToList();

        List<Node> Until(out string closing, params string[] ends)
        {
            var nodes = new List<Node>(); closing = null;
            while (at < tokens.Count)
            {
                var t = tokens[at++];
                if (t.StartsWith("{{")) { nodes.Add(new VarNode(new Expr(t[2..^2]))); continue; }
                if (t.StartsWith("{#")) continue;
                if (!t.StartsWith("{%")) { if (t != "") nodes.Add(new TextNode(t)); continue; }
                var body = t[2..^2].Trim(); var w = Words(body); var tag = w[0];
                if (ends.Contains(tag)) { closing = body; return nodes; }
                switch (tag)
                {
                    case "load": case "comment": break;
                    case "extends": parent = w[1].Trim('\'', '"'); break;
                    case "block": nodes.Add(new BlockNode(w[1], Until(out _, "endblock"))); break;
                    case "if":
                        {
                            var branches = new List<(Cond, List<Node>)>(); var test = ParseCond(w.Skip(1).ToList());
                            while (true)
                            {
                                var part = Until(out var end, "elif", "else", "endif"); branches.Add((test, part));
                                if (end == null || end == "endif") break;
                                test = end == "else" ? null : ParseCond(Words(end).Skip(1).ToList());
                            }
                            nodes.Add(new IfNode(branches)); break;
                        }
                    case "for":
                        {
                            var i = w.IndexOf("in"); var names = string.Join("", w.Skip(1).Take(i - 1)).Split(',', StringSplitOptions.RemoveEmptyEntries);
                            var loop = Until(out var end, "empty", "endfor"); var empty = end == "empty" ? Until(out _, "endfor") : new List<Node>();
                            nodes.Add(new ForNode(names, new Expr(string.Join(" ", w.Skip(i + 1))), loop, empty)); break;
                        }
                    case "include": nodes.Add(new IncludeNode(new Expr(w[1]), Pairs(w.Skip(2)))); break;
                    case "with": nodes.Add(new WithNode(Pairs(w.Skip(1)), Until(out _, "endwith"))); break;
                    case "url": { var args = w.Skip(2).ToList(); string into = null; if (args.Count >= 2 && args[^2] == "as") { into = args[^1]; args = args.Take(args.Count - 2).ToList(); } nodes.Add(new UrlNode(new Expr(w[1]), args.Select(a => new Expr(a)).ToList(), into)); break; }
                    case "static": nodes.Add(new StaticNode(new Expr(w[1]))); break;
                    case "translate": case "trans": nodes.Add(new TransNode(new Expr(w[1]), w.Count >= 4 && w[2] == "as" ? w[3] : null)); break;
                    case "blocktranslate": case "blocktrans":
                        {
                            var text = new StringBuilder(); while (at < tokens.Count && !Regex.IsMatch(tokens[at], @"^\{%\s*endblocktrans(late)?\s*%\}$")) text.Append(tokens[at++]); at++;
                            nodes.Add(new BlockTransNode(Pairs(w.Skip(1)), text.ToString())); break;
                        }
                    case "csrf_token": nodes.Add(new CsrfNode()); break;
                    case "now": nodes.Add(new NowNode(w[1].Trim('\'', '"'))); break;
                    case "get_current_language": nodes.Add(new LangNode(w[2])); break;
                    case "icon": break;  // the menu of the staff's pages: never drawn here
                    default: throw new InvalidOperationException("tag sconosciuto: " + tag);
                }
            }
            return nodes;
        }
    }
}
