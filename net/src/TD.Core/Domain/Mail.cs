using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>mailapi.ApiError: a clear message for the page; Status is the HTTP status, 0 when the service was not
/// reached, -1 when it did not answer after the request left.</summary>
public sealed class MailApiError(string message, int status = 0) : Exception(message) { public int Status { get; } = status; }
/// <summary>smtplib.SMTPResponseException for what does not come from the SMTP session itself: 451 a server that could
/// not be reached (nothing left, safe to retry) or a busy service, 554 a refusal.</summary>
public sealed class MailResponse(int code, string text) : Exception(text) { public int Code { get; } = code; }

/// <summary>core/mail.py and core/mailapi.py, the sending: one message through SMTP (any provider), Google / Gmail
/// (OAuth) or Microsoft 365 (Graph). The configuration is one encrypted dict; "method" says which way (none = SMTP).</summary>
public static class Mail
{
    public static readonly Dictionary<string, string> METHODS = new() { ["smtp"] = "SMTP", ["gmail"] = "Google / Gmail", ["graph"] = "Microsoft 365 / Exchange" };
    /// <summary>The .env of the server (EMAIL_HOST...), for the agency's sending without a saved one.</summary>
    public static Func<string, string> Env = Environment.GetEnvironmentVariable;

    public static string Method(JsonObject c) => c.Str("method") is var m && m != "" ? m : "smtp";
    /// <summary>formataddr: "Booking Suite TD &lt;info@...&gt;" instead of the bare address.</summary>
    public static string From(JsonObject c) => c.Str("sender_name") != "" ? $"{c.Str("sender_name")} <{c.Str("sender")}>" : c.Str("sender");
    /// <summary>describe(): one line for pages and test emails: how this configuration sends.</summary>
    public static string Describe(JsonObject c) => Method(c) switch
    {
        "gmail" => "Google / Gmail, account " + (c.Str("account") != "" ? c.Str("account") : c.Str("sender")),
        "graph" => "Microsoft 365, casella " + c.Str("mailbox"),
        _ => "server " + c.Str("host") + ", porta " + c.Str("port"),
    };

    /// <summary>site.agency_smtp: the agency's sending, from the program's settings or from .env; null without one.</summary>
    public static JsonObject Agency(Scope s, Crypto crypto)
    {
        var site = s.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1");
        if (!string.IsNullOrEmpty(site?.Smtp))
        {
            var c = (JsonObject)crypto.Reveal(site.Smtp, "site-smtp"); if (c.Str("sender_name") == "") c["sender_name"] = site.Name;
            return c;
        }
        var host = Env("EMAIL_HOST") ?? ""; var from = Env("DEFAULT_FROM_EMAIL") ?? "";
        if (host == "" || from == "") return null;
        return new JsonObject
        {
            ["host"] = host, ["port"] = int.TryParse(Env("EMAIL_PORT"), out var p) ? p : 587, ["mode"] = "starttls", ["username"] = Env("EMAIL_HOST_USER") ?? "", ["password"] = Env("EMAIL_HOST_PASSWORD") ?? "",
            ["sender"] = from, ["reply_to"] = from, ["sender_name"] = site?.Name ?? "",
        };
    }
    /// <summary>smtp_config: the business's own sending; for the service emails, without one, the agency's.</summary>
    public static JsonObject Config(Ctx c, bool marketing = false) =>
        !string.IsNullOrEmpty(c.Tenant.Smtp) ? c.Decrypt(c.Tenant.Smtp, "smtp") : marketing ? null : Agency(c.S, c.Crypto);

    /// <summary>The message as it leaves: the text is the plain part, the HTML with the logo inside the message, the calendar file.</summary>
    public static MimeMessage Build(JsonObject config, JsonObject payload, string messageId, byte[] logo = null)
    {
        var m = new MimeMessage(); var sender = config.Str("sender");
        m.From.Add(new MailboxAddress(config.Str("sender_name"), sender)); m.To.Add(MailboxAddress.Parse(payload.Str("to")));
        m.Subject = payload.Str("subject"); m.ReplyTo.Add(MailboxAddress.Parse(config.Str("reply_to") != "" ? config.Str("reply_to") : sender));
        m.MessageId = messageId.Trim('<', '>');
        if (payload.Str("unsubscribe") != "") { m.Headers.Add("List-Unsubscribe", "<" + payload.Str("unsubscribe") + ">"); m.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click"); }
        var b = new BodyBuilder { TextBody = payload.Str("body") };
        if (payload.Str("html") != "")
        {
            b.HtmlBody = payload.Str("html");
            if (logo != null) b.LinkedResources.Add("logo.png", logo, new ContentType("image", "png")).ContentId = "logo";
        }
        if (payload.Str("ics") != "") { var type = new ContentType("text", "calendar"); type.Parameters.Add("method", "PUBLISH"); b.Attachments.Add("prenotazione.ics", Encoding.UTF8.GetBytes(payload.Str("ics")), type); }
        m.Body = b.ToMessageBody();
        return m;
    }

    /// <summary>send_message. SMTP: to an address checked to be public (never a local network), the envelope from the
    /// authenticated mailbox and the From the clients see, so SPF and DMARC stay aligned (Register.it, Aruba).
    /// Refused, DNS, timeouts before the message is handed over: MailResponse 451, nothing has left yet.</summary>
    public static void Send(JsonObject config, JsonObject payload, string messageId, byte[] logo = null)
    {
        var message = Build(config, payload, messageId, logo);
        if (Method(config) != "smtp") { ApiSend(config, message); return; }
        var host = config.Str("host"); var port = Till.Int(config["port"]); var user = config.Str("username");
        var address = Network.PublicAddresses(host)[0];
        using var smtp = new SmtpClient { Timeout = 15000 };
        try
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { SendTimeout = 15000, ReceiveTimeout = 15000 };
            try { if (!socket.ConnectAsync(address, port).Wait(15000)) throw new SocketException((int)SocketError.TimedOut); }
            catch (AggregateException ex) { socket.Dispose(); throw ex.InnerException ?? ex; }
            catch { socket.Dispose(); throw; }
            smtp.Connect(socket, host, port, config.Str("mode") == "ssl" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
            if (user != "") smtp.Authenticate(user, config.Str("password"));
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException) { throw new MailResponse(451, "Server non raggiungibile: " + Cut(ex.Message, 200)); }
        smtp.Send(message, new MailboxAddress("", user.Contains('@') ? user : config.Str("sender")), new[] { MailboxAddress.Parse(payload.Str("to")) });
        smtp.Disconnect(true);
    }
    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>send_test: one email straight away, to check a configuration. Returns an error text or "".</summary>
    public static string SendTest(JsonObject config, string to, string name, string origin)
    {
        if (config == null) return "Nessun SMTP configurato: salvalo prima di provarlo.";
        var body = "Questa è un'email di prova di " + name + ".\n\nSe la leggi, l'invio funziona: " + Describe(config) + ", mittente " + From(config) + ".\n";
        try
        {
            Send(config, new JsonObject { ["to"] = to, ["subject"] = "Email di prova da " + name, ["body"] = body }, "<test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + "@" + origin.Split("//")[^1] + ">");
            return "";
        }
        catch (MailApiError ex) { return ex.Message; }
        catch (MailResponse ex) { return ex.Message; }
        catch (AuthenticationException) { return "Il server ha rifiutato utente o password."; }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted) { return "Il server ha rifiutato il destinatario."; }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.SenderNotAccepted) { return "Il server non accetta questo mittente: deve essere un indirizzo autorizzato sull'account SMTP."; }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or SslHandshakeException or MailKit.ServiceNotAuthenticatedException or NotSupportedException) { return "Il server ha risposto con un errore: " + Cut(ex.Message, 200); }
        catch (Exception ex) when (ex is IOException or SocketException or BookingException or FormatException or InvalidOperationException) { return "Server non raggiungibile: " + Cut(ex.Message, 200); }
    }

    // ---------- Google (Gmail API, OAuth) and Microsoft 365 (Graph): plain HTTPS to their fixed addresses, no SDK
    const string GOOGLE_AUTH = "https://accounts.google.com/o/oauth2/v2/auth", GOOGLE_TOKEN = "https://oauth2.googleapis.com/token", GOOGLE_USERINFO = "https://openidconnect.googleapis.com/v1/userinfo",
        GOOGLE_SEND = "https://gmail.googleapis.com/gmail/v1/users/me/messages/send", GOOGLE_REVOKE = "https://oauth2.googleapis.com/revoke", GOOGLE_SCOPES = "openid email https://www.googleapis.com/auth/gmail.send";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly ConcurrentDictionary<string, (string Token, DateTime Until)> tokens = new();

    /// <summary>POST (form or raw body) or GET; returns the status and the parsed JSON (or {}).</summary>
    static (int Status, JsonObject Data) Http(string url, Dictionary<string, string> form = null, byte[] body = null, string contentType = null, string bearer = null)
    {
        using var req = new HttpRequestMessage(form != null || body != null ? HttpMethod.Post : HttpMethod.Get, url);
        if (form != null) req.Content = new FormUrlEncodedContent(form);
        else if (body != null) { req.Content = new ByteArrayContent(body); req.Content.Headers.ContentType = new(contentType); }
        if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        try
        {
            using var res = http.Send(req); using var reader = new StreamReader(res.Content.ReadAsStream()); var raw = reader.ReadToEnd();
            JsonObject parsed = null; try { parsed = JsonNode.Parse(raw == "" ? "{}" : raw) as JsonObject; } catch (System.Text.Json.JsonException) { }
            return ((int)res.StatusCode, parsed ?? new JsonObject());
        }
        // the request did not get through (DNS, refused): nothing left. Any other error came while waiting for the answer:
        // the service may have acted on it (status -1, never retried for a send)
        catch (HttpRequestException ex) { throw new MailApiError("Servizio non raggiungibile: " + Cut(ex.InnerException?.Message ?? ex.Message, 160)); }
        catch (Exception ex) when (ex is IOException or TaskCanceledException) { throw new MailApiError("Il servizio non ha risposto: " + Cut(ex.Message, 160), -1); }
    }
    static string Detail(JsonObject data) =>
        Cut(data["error"] is JsonObject e ? (e.Str("message") != "" ? e.Str("message") : e.Str("code")) : data.Str("error_description") != "" ? data.Str("error_description") : data.Str("error"), 200);

    public static string GoogleAuthUrl(string clientId, string redirectUri, string state) => GOOGLE_AUTH + "?" + string.Join("&", new (string, string)[]
        { ("response_type", "code"), ("client_id", clientId), ("redirect_uri", redirectUri), ("scope", GOOGLE_SCOPES), ("access_type", "offline"), ("prompt", "consent"), ("include_granted_scopes", "false"), ("state", state) }
        .Select(x => x.Item1 + "=" + Uri.EscapeDataString(x.Item2)));

    /// <summary>The code of the consent page → the refresh token and the account email.</summary>
    public static (string RefreshToken, string Account) GoogleExchange(string clientId, string secret, string code, string redirectUri)
    {
        var (status, data) = Http(GOOGLE_TOKEN, new() { ["code"] = code, ["client_id"] = clientId, ["client_secret"] = secret, ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code" });
        if (status != 200) throw new MailApiError("Google ha rifiutato il collegamento: " + (Detail(data) is var d && d != "" ? d : status.ToString()), status);
        if (!data.Str("scope").Contains("gmail.send")) throw new MailApiError("Durante il consenso non è stato autorizzato l'invio delle email: ricollega e spunta il permesso di Gmail.");
        if (data.Str("refresh_token") == "") throw new MailApiError("Google non ha dato l'accesso permanente: togli l'app da myaccount.google.com, Sicurezza, Accessi di terze parti, e ricollega.");
        var (s2, info) = Http(GOOGLE_USERINFO, bearer: data.Str("access_token"));
        return (data.Str("refresh_token"), s2 == 200 ? info.Str("email") : "");
    }
    public static void GoogleRevoke(string token) { try { Http(GOOGLE_REVOKE, new() { ["token"] = token }); } catch (MailApiError) { } }

    static string CacheKey(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    static string Token(string key, Func<(int, JsonObject)> ask, Func<int, JsonObject, MailApiError> refused)
    {
        if (tokens.TryGetValue(key, out var t) && t.Until > DateTime.UtcNow) return t.Token;
        var (status, data) = ask();
        if (status != 200 || data.Str("access_token") == "") throw refused(status, data);
        tokens[key] = (data.Str("access_token"), DateTime.UtcNow.AddSeconds(Math.Max(60, (data["expires_in"] != null ? Till.Int(data["expires_in"]) : 3600) - 120)));
        return data.Str("access_token");
    }
    static string GoogleKey(JsonObject c) => CacheKey("google", c.Str("client_id"), c.Str("refresh_token"));
    static string GraphKey(JsonObject c) => CacheKey("graph", c.Str("tenant_id"), c.Str("client_id"), c.Str("client_secret"));
    static string GoogleToken(JsonObject c) => Token(GoogleKey(c),
        () => Http(GOOGLE_TOKEN, new() { ["client_id"] = c.Str("client_id"), ["client_secret"] = c.Str("client_secret"), ["refresh_token"] = c.Str("refresh_token"), ["grant_type"] = "refresh_token" }),
        (status, data) => data.Str("error") == "invalid_grant" ? new MailApiError("Il collegamento con Google non è più valido: ricollega l'account.", status) : new MailApiError("Google non ha dato l'accesso: " + (Detail(data) is var d && d != "" ? d : status.ToString()), status));
    public static string GraphToken(JsonObject c) => Token(GraphKey(c),
        () => Http($"https://login.microsoftonline.com/{Uri.EscapeDataString(c.Str("tenant_id"))}/oauth2/v2.0/token", new() { ["client_id"] = c.Str("client_id"), ["client_secret"] = c.Str("client_secret"), ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials" }),
        (status, data) => new MailApiError("Microsoft ha rifiutato le credenziali dell'app: " + (Detail(data) is var d && d != "" ? d : status.ToString()), status));

    /// <summary>raise_for: the HTTP result as the outbox understands it (429 and 5xx temporary, the other 4xx permanent).</summary>
    static void RaiseFor(int status, JsonObject data, string service)
    {
        if (status is >= 200 and < 300) return;
        var text = service + ": " + (Detail(data) is var d && d != "" ? d : status.ToString());
        if (status is 401 or 403) throw new MailApiError(text, status);  // configuration: the worker marks it failed, no retry
        throw new MailResponse(status == 429 || status >= 500 ? 451 : 554, text);
    }
    /// <summary>mailapi.send: a service that cannot be reached is a temporary failure (the outbox retries); refused
    /// credentials are a configuration error; no answer after the message was handed over is uncertain (no double send).</summary>
    static void ApiSend(JsonObject c, MimeMessage message)
    {
        using var ms = new MemoryStream(); message.WriteTo(ms); var mime = ms.ToArray();
        try
        {
            if (Method(c) == "gmail")
            {
                var raw = Convert.ToBase64String(mime).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                var (status, data) = Http(GOOGLE_SEND, body: Encoding.UTF8.GetBytes(new JsonObject { ["raw"] = raw }.ToJsonString()), contentType: "application/json", bearer: GoogleToken(c));
                if (status == 401) tokens.TryRemove(GoogleKey(c), out _);
                RaiseFor(status, data, "Gmail");
            }
            else if (Method(c) == "graph")
            {
                var (status, data) = Http($"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(c.Str("mailbox")).Replace("%40", "@")}/sendMail", body: Encoding.ASCII.GetBytes(Convert.ToBase64String(mime)), contentType: "text/plain", bearer: GraphToken(c));
                if (status == 401) tokens.TryRemove(GraphKey(c), out _);
                if (status == 403) throw new MailApiError("Microsoft 365 non permette all'app di inviare da " + c.Str("mailbox") + ": serve il permesso Mail.Send di tipo applicazione, con il consenso dell'amministratore.", 403);
                RaiseFor(status, data, "Microsoft 365");
            }
            else throw new BookingException("Metodo di invio sconosciuto.");
        }
        catch (MailApiError ex) when (ex.Status == 0) { throw new MailResponse(451, ex.Message); }
        catch (MailApiError ex) when (ex.Status == -1) { throw new InvalidOperationException(ex.Message); }
    }
}
