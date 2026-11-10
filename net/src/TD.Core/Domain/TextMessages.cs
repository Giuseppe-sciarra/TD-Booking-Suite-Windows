using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>texts.TextError: the provider's refusal, by its code; temporary when it may go at the next try.</summary>
public sealed class TextError(string code, bool temporary = false) : Exception(code) { public string Code { get; } = code; public bool Temporary { get; } = temporary; }

/// <summary>core/texts.py: the reminders also as a text message: an SMS through Twilio, Skebby or Aruba, or a WhatsApp
/// Cloud API "utility" template with the SMS as fallback when WhatsApp refuses it. The keys are sealed with the
/// business's key; never between midnight and 7, within a daily budget. Only reminders, nothing promotional.</summary>
public static class TextMessages
{
    public static readonly (string Key, string Label)[] PROVIDERS = { ("", "Nessuno"), ("twilio", "Twilio"), ("skebby", "Skebby"), ("aruba", "Aruba SMS") };
    // Skebby and Aruba share one API: the address and the message type change (GP = Classic+, N = high quality, both with receipt)
    static readonly Dictionary<string, (string Base, string Kind)> SKEBBY = new() { ["skebby"] = ("https://api.skebby.it", "GP"), ["aruba"] = ("https://smspanel.aruba.it", "N") };
    public const int QUIET = 7;  // nothing before 7 in the morning
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static JsonObject Config(Ctx c) => string.IsNullOrEmpty(c.Tenant.Texts) ? new JsonObject() : c.Decrypt(c.Tenant.Texts, "texts");
    public static string Channel(JsonObject conf) => conf.Str("wa_token") != "" ? "whatsapp" : conf.Str("provider") != "" ? "sms" : "";

    /// <summary>clean(): the settings as the administrator typed them; an empty key keeps the one saved. Empty = no channel.</summary>
    public static JsonObject Clean(JsonObject post, JsonObject saved)
    {
        var conf = new JsonObject(); string P(string k) => post[k] is JsonValue v ? v.ToString().Trim() : "";
        foreach (var k in new[] { "provider", "user", "sender", "wa_phone", "wa_template", "wa_lang" }) conf[k] = P(k);
        foreach (var k in new[] { "secret", "wa_token" }) conf[k] = P(k) != "" ? P(k) : saved.Str(k);
        if (Json.Truthy(post["sms_off"])) foreach (var k in new[] { "provider", "user", "secret", "sender" }) conf[k] = "";
        if (Json.Truthy(post["wa_off"])) foreach (var k in new[] { "wa_phone", "wa_token", "wa_template" }) conf[k] = "";
        bool Full(string key, string pattern) => Regex.IsMatch(conf.Str(key), "^(?:" + pattern + @")\z");
        if (!PROVIDERS.Any(p => p.Key == conf.Str("provider"))) throw new BookingException("Scegli il servizio SMS.");
        if (conf.Str("provider") != "")
        {
            if (conf.Str("user") == "" || conf.Str("secret") == "" || conf.Str("sender") == "") throw new BookingException("Per gli SMS servono utente, chiave e mittente.");
            // they go into an address or a header: only the characters the providers use
            if (conf.Str("provider") == "twilio" && !Full("user", "AC[0-9a-f]{32}")) throw new BookingException("Twilio: l'Account SID inizia con AC e ha 34 caratteri.");
            if (!Full("user", @"[\w.\-]{1,80}") || !Full("secret", @"[\w.\-]{1,200}")) throw new BookingException("Utente o chiave SMS non validi.");
            if (!Full("sender", @"\+?[0-9]{6,15}|[A-Za-z0-9 ]{1,11}")) throw new BookingException("Mittente: un numero o un nome di al massimo 11 lettere e cifre.");
        }
        if (conf.Str("wa_token") != "" || conf.Str("wa_phone") != "")
        {
            if (conf.Str("wa_token") == "" || conf.Str("wa_phone") == "" || conf.Str("wa_template") == "") throw new BookingException("Per WhatsApp servono ID del numero, token e nome del modello.");
            if (!Full("wa_phone", "[0-9]{5,20}") || !Full("wa_template", "[a-z0-9_]{1,512}") || !Full("wa_token", @"[A-Za-z0-9_\-.]{20,1000}")) throw new BookingException("WhatsApp: ID del numero, token o nome del modello non validi.");
            if (!Full("wa_lang", "[a-z]{2}(_[A-Z]{2})?")) conf["wa_lang"] = "it";
        }
        return Channel(conf) != "" ? conf : new JsonObject();
    }

    /// <summary>remind(): the reminder as a text, to the phone of the booking, when a channel is set and the client did not
    /// say no to the messages about his bookings; the channel the client chose when the business has it ("email" = no text).
    /// The words are the business's; for WhatsApp the values go in the order of the {fields} of the template text.</summary>
    public static Delivery Remind(Ctx c, Booking b, string kind)
    {
        if (kind is not ("reminder" or "reminder2")) return null;
        var conf = Config(c); var ch = Channel(conf); var contact = b.ContactId is Guid k ? c.S.Get<Contact>(k) : null;
        if (ch == "" || b.Snap["notices"] is JsonValue nv && nv.TryGetValue<bool>(out var no) && !no || contact != null && !contact.Notices) return null;
        var pref = b.Snap.Str("channel") is var p && p != "" ? p : contact?.Channel ?? "";
        if (pref == "email") return null;
        if (pref == "sms" && conf.Str("provider") != "" || pref == "whatsapp" && conf.Str("wa_token") != "") ch = pref;
        var bk = new Bookings(c); var data = bk.Data(b); var phone = Clients.NormalizePhone(data.Str("phone"));
        if (phone == "") return null;
        var v = ClientMails.Values(c, b, data); var own = Json.Obj(c.Tenant.ClientMails)["texts"] as JsonObject ?? new JsonObject();
        string Or(string key) => v.TryGetValue(key, out var x) && x != "" ? x : "-";
        var parameters = own.Str("wa") != "" ? Regex.Matches(own.Str("wa"), @"\{(\w+)\}").Select(m => Or(m.Groups[1].Value)).ToArray() : new[] { Or("nome"), v["servizio"], v["quando"], v["attivita"] };
        var d = bk.Queue(new JsonObject { ["to"] = phone, ["body"] = Texts.Fill(own.Str("sms") != "" ? own.Str("sms") : ClientMails.SMS_TEXT, v), ["params"] = new JsonArray(parameters.Select(x => (JsonNode)x).ToArray()) }, b.ContactId);
        d.Channel = ch; d.BookingId = b.Id; c.S.Update(d, "channel", "booking_id");
        return d;
    }

    /// <summary>spent(): the texts of the last day, against the daily budget.</summary>
    public static long Spent(Ctx c) => c.S.Scalar<long>("SELECT count(*) FROM core_delivery WHERE channel IN ('sms','whatsapp') AND status IN ('accepted','sending','unknown') AND claimed_at>=@a", new { a = c.Now.AddDays(-1) });
    /// <summary>later(): the moment the quiet night ends, while it lasts; otherwise null.</summary>
    public static DateTime? Later(Ctx c) { var local = c.Cal.ToLocal(c.Now); return local.Hour < QUIET ? c.Cal.Local(DateOnly.FromDateTime(local), TimeSpan.FromHours(QUIET)) : null; }

    /// <summary>One call to the provider (a GET without body); its JSON answer, {} when it is not JSON.</summary>
    static JsonObject Post(string url, JsonNode body, Dictionary<string, string> headers, Dictionary<string, string> form = null)
    {
        using var req = new HttpRequestMessage(body != null || form != null ? HttpMethod.Post : HttpMethod.Get, url);
        if (form != null) req.Content = new FormUrlEncodedContent(form);
        else if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        try
        {
            using var res = http.Send(req); var code = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode) throw new TextError($"http_{code}", code == 429 || code >= 500);
            using var reader = new StreamReader(res.Content.ReadAsStream()); var raw = reader.ReadToEnd();
            try { return JsonNode.Parse(raw == "" ? "{}" : raw) as JsonObject ?? new JsonObject(); } catch (System.Text.Json.JsonException) { return new JsonObject(); }
        }
        catch (HttpRequestException) { throw new TextError("unreachable", true); }  // not connected: nothing left yet (a read timeout stays uncertain)
    }
    static string Basic(JsonObject conf) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(conf.Str("user") + ":" + conf.Str("secret")));
    static Dictionary<string, string> Keys(JsonObject conf) => new() { ["user_key"] = conf.Str("user"), ["Access_token"] = conf.Str("secret") };

    static string Sms(JsonObject conf, JsonObject p)
    {
        if (conf.Str("provider") == "twilio")
            return Post($"https://api.twilio.com/2010-04-01/Accounts/{conf.Str("user")}/Messages.json", null, new() { ["Authorization"] = Basic(conf) }, new() { ["To"] = p.Str("to"), ["From"] = conf.Str("sender"), ["Body"] = p.Str("body") }).Str("sid");
        var (root, kind) = SKEBBY[conf.Str("provider")];
        return Post(root + "/API/v1.0/REST/sms", new JsonObject { ["message_type"] = kind, ["message"] = p.Str("body"), ["recipient"] = new JsonArray(p.Str("to")), ["sender"] = conf.Str("sender"), ["returnCredits"] = false }, Keys(conf)).Str("order_id");
    }
    /// <summary>The approved template with four values in its text: {{1}} name, {{2}} service, {{3}} day and time, {{4}} business.</summary>
    static string WhatsApp(JsonObject conf, JsonObject p)
    {
        var body = new JsonObject
        {
            ["messaging_product"] = "whatsapp", ["to"] = p.Str("to").TrimStart('+'), ["type"] = "template",
            ["template"] = new JsonObject
            {
                ["name"] = conf.Str("wa_template"), ["language"] = new JsonObject { ["code"] = conf.Str("wa_lang") != "" ? conf.Str("wa_lang") : "it" },
                ["components"] = new JsonArray(new JsonObject { ["type"] = "body", ["parameters"] = new JsonArray((p["params"] as JsonArray ?? new JsonArray()).Select(x => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = x?.ToString() ?? "" }).ToArray()) }),
            },
        };
        var got = Post($"https://graph.facebook.com/v26.0/{conf.Str("wa_phone")}/messages", body, new() { ["Authorization"] = "Bearer " + conf.Str("wa_token") });
        return (got["messages"] as JsonArray)?.FirstOrDefault()?.Str("id") ?? "";
    }
    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>deliver(): sends one message; the channel it went by and the provider's id of it. WhatsApp refused by the
    /// API (number or template) falls back to SMS when set; a number without WhatsApp that Meta accepts and then does
    /// not deliver is not sent again by SMS, as the client chose WhatsApp.</summary>
    public static (string Channel, string Ref) Deliver(JsonObject conf, string channel, JsonObject p)
    {
        if (channel == "whatsapp" && conf.Str("wa_token") != "")
        {
            try { return ("whatsapp", Cut(WhatsApp(conf, p), 80)); }
            catch (TextError ex) when (!ex.Temporary && conf.Str("provider") != "") { }
        }
        if (conf.Str("provider") == "") throw new TextError("not_configured");
        return ("sms", Cut(Sms(conf, p), 80));
    }

    // the provider's words for a text that will not arrive (Twilio, then Skebby and Aruba)
    static readonly HashSet<string> FAILED = new() { "undelivered", "failed", "ERROR", "TIMEOUT", "TOOM4USER", "TOOM4NUM", "UNKNRCPT", "UNKNPFX", "INVALIDDST", "BLACKLISTED", "KO", "INVALIDCONTENTS" };
    /// <summary>receipt(): delivered, failed or "" (not yet) for one SMS, asked to its provider (no public address needed).</summary>
    public static string Receipt(JsonObject conf, string reference)
    {
        if (!Regex.IsMatch(reference, @"^[\w.\-]{1,80}\z")) return "failed";
        var state = conf.Str("provider") == "twilio"
            ? Post($"https://api.twilio.com/2010-04-01/Accounts/{conf.Str("user")}/Messages/{reference}.json", null, new() { ["Authorization"] = Basic(conf) }).Str("status")
            : (Post(SKEBBY[conf.Str("provider")].Base + $"/API/v1.0/REST/sms/{reference}", null, Keys(conf))["recipients"] as JsonArray)?.FirstOrDefault()?.Str("status") ?? "";
        return state is "delivered" or "DLVRD" ? "delivered" : FAILED.Contains(state) ? "failed" : "";
    }
}
