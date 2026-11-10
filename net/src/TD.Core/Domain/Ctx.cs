using System.Text.Json;
using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>What a booking, a sale or a client change asks of the world outside the database (emails, staff notices,
/// webhooks, the WordPress site). The server plugs in the real ones; tests record them.</summary>
public interface IOutbox
{
    void ClientMail(Ctx c, Booking b, string kind) { }
    void NotifyStaff(Ctx c, Booking b, string kind) { }
    void Emit(Ctx c, Booking b, string kind) { }
    /// <summary>wpsync.touch: rows the WordPress site's archive must get again.</summary>
    void Touch(Ctx c, string kind, IEnumerable<Guid> ids) { }
}
public sealed class NoOutbox : IOutbox { public static readonly NoOutbox Instance = new(); }

/// <summary>One unit of work inside a business: its transaction with row-level security, its key, its calendar, the
/// rows read so far. Python's request.tenant plus the scope() of core/tenancy.</summary>
public sealed class Ctx : IDisposable
{
    public Scope S { get; }
    public Tenant Tenant { get; private set; }
    public Crypto Crypto { get; }
    public IOutbox Out { get; set; } = NoOutbox.Instance;
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public DateTime Now => DateTime.SpecifyKind(Clock(), DateTimeKind.Utc);
    /// <summary>Who acts: the user's id, null for the client on the public page and for the worker.</summary>
    public Guid? Actor { get; set; }
    /// <summary>"private" on the Windows edition: no trial or subscription stops the bookings.</summary>
    public string Edition { get; set; } = "private";
    /// <summary>The language of the client's page (it, en...), for the emails that follow.</summary>
    public string Language { get; set; } = "it";
    public string PublicOrigin { get; set; } = "";

    Calendar cal;
    public Calendar Cal => cal ??= new Calendar(S, Tenant.Timezone);
    public TimeZoneInfo Zone => Cal.Zone;
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, Zone));
    public void CalendarChanged() => cal = null;

    public Ctx(Db db, Crypto crypto, Guid tenant)
    {
        Crypto = crypto;
        S = db.Tenant(tenant);
        Tenant = S.Get<Tenant>(tenant) ?? throw new InvalidOperationException("Attività inesistente");
    }
    public Ctx(Scope scope, Crypto crypto, Tenant tenant) { S = scope; Crypto = crypto; Tenant = tenant; }

    /// <summary>SELECT ... FOR UPDATE of the business row: every writer of the inventory takes the same lock.</summary>
    public void Lock() => Tenant = S.One<Tenant>("SELECT * FROM core_tenant WHERE id=@id FOR UPDATE", new { id = Tenant.Id });
    public void Commit() => S.Commit();
    public void Dispose() => S.Dispose();

    // ---------- the business key
    public byte[] Key => Crypto.TenantKey(Tenant.Id, Tenant.WrappedKey);
    public string Encrypt(JsonNode data, string label) => Crypto.Encrypt(Tenant.Id, Tenant.WrappedKey, data, label);
    public JsonObject Decrypt(string data, string label) =>
        string.IsNullOrEmpty(data) ? new JsonObject() : Crypto.Decrypt(Tenant.Id, Tenant.WrappedKey, data, label) as JsonObject ?? new JsonObject();
    public JsonNode DecryptNode(string data, string label) => string.IsNullOrEmpty(data) ? null : Crypto.Decrypt(Tenant.Id, Tenant.WrappedKey, data, label);
    public string DecryptText(string data, string label) => DecryptNode(data, label) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public string Index(string value, string purpose) => Crypto.Index(value, Key, purpose);

    // ---------- rows by id, read once per unit of work
    readonly Dictionary<(Type, Guid), object> rows = new();
    public T Row<T>(Guid id) where T : class
    {
        if (rows.TryGetValue((typeof(T), id), out var o)) return (T)o;
        var r = S.Get<T>(id);
        if (r != null) rows[(typeof(T), id)] = r;
        return r;
    }
    public T Row<T>(Guid? id) where T : class => id.HasValue ? Row<T>(id.Value) : null;
    public void Forget<T>(Guid id) => rows.Remove((typeof(T), id));

    // ---------- audit, as core.models.audit
    public void Audit(string action, object target = null, object detail = null) =>
        S.Insert(new Audit
        {
            TenantId = Tenant.Id, Actor = Actor?.ToString() ?? "", Action = action, Target = target?.ToString() ?? "",
            Detail = detail == null ? "{}" : JsonSerializer.Serialize(detail, Crypto.Json), Created = Now,
        });

    /// <summary>core/middleware.operational: may the business take bookings now.</summary>
    public bool Operational()
    {
        if (Tenant.Maintenance) return false;
        if (Edition == "private") return true;
        return Tenant.Status == "active" || Tenant.Status == "trial" && Tenant.TrialUntil.HasValue && DateOnly.FromDateTime(Tenant.TrialUntil.Value) >= Today;
    }
}

public static class Json
{
    public static JsonObject Obj(string s) => string.IsNullOrEmpty(s) ? new JsonObject() : JsonNode.Parse(s) as JsonObject ?? new JsonObject();
    public static JsonArray Arr(string s) => string.IsNullOrEmpty(s) ? new JsonArray() : JsonNode.Parse(s) as JsonArray ?? new JsonArray();
    public static List<string> Strings(string s) => Arr(s).Select(x => x?.ToString() ?? "").ToList();
    public static string Dump(JsonNode n) => n.ToJsonString(Crypto.Json);
    public static string Str(this JsonNode n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?[key]?.ToString() ?? "";
    public static bool Has(this JsonObject o, string key) => o.ContainsKey(key) && o[key] != null;
    /// <summary>Python's truthiness of a JSON value.</summary>
    public static bool Truthy(JsonNode n) => n switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true,
    };

    /// <summary>json.dumps(data, sort_keys=True, separators=(",",":")) with ensure_ascii, for the fingerprints the Python made.</summary>
    public static string PyDumps(JsonNode n)
    {
        var sb = new System.Text.StringBuilder(); Write(n, sb); return sb.ToString();
        static void Write(JsonNode n, System.Text.StringBuilder sb)
        {
            switch (n)
            {
                case null: sb.Append("null"); break;
                case JsonObject o:
                    sb.Append('{'); var first = true;
                    foreach (var kv in o.OrderBy(x => x.Key, StringComparer.Ordinal))
                    { if (!first) sb.Append(','); first = false; Str(kv.Key, sb); sb.Append(':'); Write(kv.Value, sb); }
                    sb.Append('}'); break;
                case JsonArray a:
                    sb.Append('['); for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(a[i], sb); } sb.Append(']'); break;
                case JsonValue v when v.TryGetValue<string>(out var s): Str(s, sb); break;
                case JsonValue v when v.TryGetValue<bool>(out var b): sb.Append(b ? "true" : "false"); break;
                default: sb.Append(n.ToJsonString()); break;
            }
        }
        static void Str(string s, System.Text.StringBuilder sb)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
