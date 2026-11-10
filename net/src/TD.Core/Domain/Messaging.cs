using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>gettext for the client's pages and emails: Italian is the source, English from the program's django.po.</summary>
public static class Tr
{
    static readonly Dictionary<string, string> en = Load();
    static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TD.Core.en.po");
        if (s == null) return d;
        string id = null, str = null, mode = null; var text = new StreamReader(s).ReadToEnd();
        string Unq(string x) => Regex.Unescape(x.Trim()[1..^1]);
        void Flush() { if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(str)) d[id] = str; id = str = null; }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("msgid ")) { Flush(); id = Unq(line[6..]); mode = "id"; }
            else if (line.StartsWith("msgstr ")) { str = Unq(line[7..]); mode = "str"; }
            else if (line.StartsWith("\"")) { if (mode == "id") id += Unq(line); else if (mode == "str") str += Unq(line); }
            else if (line.Trim() == "") mode = null;
        }
        Flush();
        return d;
    }
    public static string T(string lang, string text) => lang == "en" && en.TryGetValue(text, out var t) ? t : text;
}

/// <summary>Little helpers of the texts: {fields}, the day and time in words, the business's colour, the calendar file.</summary>
public static class Texts
{
    public static readonly string[] WEEKDAYS = { "lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato", "domenica" };
    /// <summary>{nome}, {quando}...: a name not in the list stays as it was written.</summary>
    public static string Fill(string text, IDictionary<string, string> values) =>
        Regex.Replace(text ?? "", @"\{(\w+)\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static string When(DateTime start, DateTime end, bool stay, string lang = "it")
    {
        if (stay) return Tr.T(lang, "dal {inizio}, arrivo {arrivo}, al {fine}, partenza {partenza}")
            .Replace("{inizio}", $"{start:dd/MM/yyyy}").Replace("{arrivo}", $"{start:HH:mm}").Replace("{fine}", $"{end:dd/MM/yyyy}").Replace("{partenza}", $"{end:HH:mm}");
        return Tr.T(lang, "{giorno} {data} alle {ora}").Replace("{giorno}", Tr.T(lang, WEEKDAYS[Calendar.Weekday(DateOnly.FromDateTime(start))]))
            .Replace("{data}", $"{start:dd/MM/yyyy}").Replace("{ora}", $"{start:HH:mm}");
    }

    static double Lum(int[] rgb)
    {
        double C(int v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(rgb[0]) + 0.7152 * C(rgb[1]) + 0.0722 * C(rgb[2]);
    }
    /// <summary>branding.contrast_color: the brand colour darkened (or lightened) until it reads at 4.5:1.</summary>
    public static string ContrastColor(string color, bool dark = false)
    {
        if (string.IsNullOrEmpty(color) || color.Length != 7) color = "#7a55ad";
        var rgb = new[] { Convert.ToInt32(color[1..3], 16), Convert.ToInt32(color[3..5], 16), Convert.ToInt32(color[5..7], 16) };
        var bg = dark ? .05 : 1;
        for (int i = 0; i < 60; i++)
        {
            var l = Lum(rgb); var ratio = (Math.Max(l, bg) + .05) / (Math.Min(l, bg) + .05);
            if (ratio >= 4.5) break;
            rgb = rgb.Select(v => dark ? (int)Math.Round(v * .94 + 255 * .06, MidpointRounding.ToEven) : (int)Math.Round(v * .94, MidpointRounding.ToEven)).ToArray();
        }
        return "#" + string.Concat(rgb.Select(v => v.ToString("x2")));
    }

    public static string IcsEscape(string t) => (t ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\n", "\\n");
    public static string Fold(string line)
    {
        var outp = new List<StringBuilder> { new() };
        foreach (var ch in line)
        {
            var cur = outp[^1];
            if (Encoding.UTF8.GetByteCount(cur.ToString() + ch) > (outp.Count == 1 ? 75 : 74)) outp.Add(new StringBuilder().Append(ch));
            else cur.Append(ch);
        }
        return string.Join("\r\n ", outp.Select(x => x.ToString()));
    }
    public static string Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Ics(Ctx c, Booking b)
    {
        var host = Host(c.PublicOrigin);
        var loc = c.Row<Location>(b.LocationId);
        var title = $"{(b.Snap.Str("service") is var sv && sv != "" ? sv : c.Row<Service>(b.ServiceId).Name)} · {c.Tenant.Name}";
        var place = string.Join(", ", new[] { loc.Name, loc.Address }.Where(x => !string.IsNullOrEmpty(x)));
        var lines = new[] { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Tastiere Digitali//TD Booking Suite//IT", "METHOD:PUBLISH", "BEGIN:VEVENT",
            $"UID:{b.Id}@{host}", "DTSTAMP:" + Utc(c.Now), "DTSTART:" + Utc(b.Start), "DTEND:" + Utc(b.End), "SUMMARY:" + IcsEscape(title), "LOCATION:" + IcsEscape(place), "END:VEVENT", "END:VCALENDAR" };
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }
    public static string Host(string origin) { try { return new Uri(origin).Host; } catch { return "localhost"; } }

    /// <summary>Django's urlize + linebreaksbr, enough for the emails' text: escaped, links clickable, new lines as br.</summary>
    public static string HtmlBody(string text)
    {
        var e = WebUtility.HtmlEncode(text ?? "");
        e = Regex.Replace(e, @"(https?://[^\s<]+[^\s<.,;:!?)\]'""])", m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        return e.Replace("\r\n", "\n").Replace("\n", "<br>");
    }

    public static string EmailHtml(Tenant t, string body, List<(string Label, string Url)> links, string lang = "it")
    {
        var color = ContrastColor(t.BrandColor);
        var name = WebUtility.HtmlEncode(t.Name);
        var sb = new StringBuilder();
        sb.Append($"<!DOCTYPE html><html lang=\"{lang}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{name}</title></head>");
        sb.Append("<body style=\"margin:0;padding:24px 12px;background:#f5f4f7;font-family:Arial,Helvetica,sans-serif;color:#222\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\">");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-radius:12px;border-top:4px solid {color}\"><tr><td style=\"padding:24px 28px 4px\">");
        sb.Append(t.Logo is { Length: > 0 } ? $"<img src=\"cid:logo\" alt=\"{name}\" height=\"48\" style=\"display:block;max-height:48px;max-width:220px;height:auto;width:auto;border:0\">" : $"<strong style=\"font-size:18px;color:{color}\">{name}</strong>");
        sb.Append($"</td></tr><tr><td style=\"padding:12px 28px 4px;font-size:15px;line-height:1.6\">{HtmlBody(body)}</td></tr>");
        if (links.Count > 0)
        {
            sb.Append("<tr><td style=\"padding:16px 28px 4px\">");
            for (int i = 0; i < links.Count; i++)
                sb.Append($"<a href=\"{WebUtility.HtmlEncode(links[i].Url)}\" style=\"display:inline-block;margin:0 8px 8px 0;padding:11px 22px;border-radius:8px;border:1px solid {color};{(i == 0 ? $"background:{color};color:#ffffff" : $"background:#ffffff;color:{color}")};font-weight:bold;text-decoration:none\">{WebUtility.HtmlEncode(links[i].Label)}</a>");
            sb.Append("</td></tr>");
        }
        sb.Append($"<tr><td style=\"padding:20px 28px 24px;font-size:12px;color:#777777\">{name}</td></tr></table></td></tr></table></body></html>");
        return sb.ToString();
    }
}

/// <summary>core/client_mails.py: the emails to the client about his booking, and the timed ones (reminders, review, recall).</summary>
public static class ClientMails
{
    public static readonly (string Key, string Label, string Help)[] KINDS =
    {
        ("confirmed","Conferma","Appena la prenotazione è fatta, online o da voi"),
        ("reminder","Promemoria","Prima dell'appuntamento, con i tasti Confermo e Non vengo"),
        ("reminder2","Secondo promemoria","Poche ore prima, con gli stessi tasti: facoltativo"),
        ("requested","Richiesta ricevuta","Quando una prenotazione online aspetta la vostra conferma"),
        ("accepted","Richiesta accettata","Quando confermate una richiesta"),
        ("rejected","Richiesta rifiutata","Quando rifiutate una richiesta"),
        ("moved","Modifica","Quando la prenotazione cambia giorno, orario o servizio, da voi o dal cliente"),
        ("cancelled","Annullamento","Quando la prenotazione viene annullata, da voi o dal cliente"),
        ("arrival","Prima dell'arrivo","Soggiorni ed eventi: le istruzioni scritte nel servizio, per esempio check-in o punto d'incontro"),
        ("review","Dopo la visita","Il grazie con il link per la recensione, solo a chi è iscritto alle comunicazioni, al massimo una volta a settimana"),
        ("waitlist","Si è liberato un posto","A chi è in lista d'attesa, quando si libera un posto che aspetta, con il link per prenotarlo"),
        ("recall","Richiamo","Quando è ora di tornare per un servizio con il richiamo, solo a chi è iscritto alle comunicazioni e non ha già un appuntamento"),
    };
    public static readonly string[] STANDARD = { "confirmed", "requested", "accepted", "rejected", "moved", "cancelled" };
    public static readonly string[] ADMIN = { "reminder", "reminder2", "arrival", "review" };
    public static readonly int[] HOURS = { 2, 3, 6, 12, 24, 48, 72 };
    public static readonly Dictionary<string, int[]> TIMING = new()
    { ["reminder"] = HOURS, ["reminder2"] = new[] { 1, 2, 3, 4, 5, 6 }, ["arrival"] = HOURS.Append(168).ToArray(), ["review"] = new[] { 1, 2, 3, 6, 12, 24, 48 } };
    public static readonly int[] AUTOCANCEL = { 0, 2, 3, 6, 12 };
    public const string WHATSAPP = "Ciao {nome}, ti scriviamo da {attivita} per la prenotazione: {servizio}, {quando}.";
    public const string REMINDER = "Ciao {nome}, ti ricordiamo l'appuntamento da {attivita} {quando}: {servizio}. Se non puoi venire, rispondi a questo messaggio. A presto!";
    public const string THANKS = "Ciao {nome}, grazie di essere passato da {attivita}! Se ti va, lasciaci una recensione: {link}. A presto!";

    public static JsonObject Defaults() => (JsonObject)JsonNode.Parse("""
    {
     "confirmed":{"on":true,"price":false,"subject":"Prenotazione confermata · {attivita}","body":"La prenotazione è confermata.\n{servizio}\n{quando}\nGestisci o annulla: {link}"},
     "moved":{"on":true,"subject":"Prenotazione aggiornata · {attivita}","body":"Nuovo orario: {quando}.\nContatta l'attività per informazioni."},
     "cancelled":{"on":true,"subject":"Prenotazione annullata · {attivita}","body":"La prenotazione è stata annullata. Contatta l'attività per prenotare nuovamente."},
     "requested":{"on":true,"subject":"Richiesta ricevuta · {attivita}","body":"Abbiamo ricevuto la tua richiesta: {servizio}, {quando}.\nTi scriviamo appena la confermiamo. Fino ad allora il posto resta tenuto per te.\nPer annullarla: {link}"},
     "accepted":{"on":true,"subject":"Prenotazione confermata · {attivita}","body":"La tua richiesta è confermata.\n{servizio}\n{quando}\nGestisci o annulla: {link}"},
     "rejected":{"on":true,"subject":"Richiesta non confermata · {attivita}","body":"Ci dispiace, non possiamo confermare la tua richiesta: {servizio}, {quando}.\nContatta l'attività per trovare un'altra soluzione."},
     "reminder":{"on":true,"hours":24,"at":"","autocancel":0,"subject":"Promemoria · {attivita}","body":"Ciao {nome},\nti ricordiamo la prenotazione: {servizio}, {quando}.\nSe non puoi venire, gestisci o annulla: {link}"},
     "reminder2":{"on":false,"hours":2,"at":"","subject":"A tra poco · {attivita}","body":"Ciao {nome},\nti aspettiamo tra poco: {servizio}, {quando}.\nSe non puoi venire, gestisci o annulla: {link}"},
     "arrival":{"on":false,"hours":48,"subject":"Prima del tuo arrivo · {attivita}","body":"Ciao {nome},\nti aspettiamo: {servizio}, {quando}.\n{istruzioni}\nPer gestire la prenotazione: {link}"},
     "review":{"on":false,"hours":3,"url":"","subject":"Com'è andata? · {attivita}","body":"Ciao {nome},\ngrazie di aver scelto {attivita}. Se ti va, lasciaci una recensione: per noi conta molto."},
     "recall":{"on":false,"subject":"È ora di tornare · {attivita}","body":"Ciao {nome},\nè il momento giusto per {servizio}. Prenota quando vuoi: {link}"},
     "waitlist":{"on":true,"subject":"Si è liberato un posto · {attivita}","body":"Ciao {nome},\nsi è liberato un posto che aspettavi: {servizio}, {quando}.\nPrenota da qui: {link}"}
    }
    """);

    /// <summary>The Windows program: the shop itself sets everything up (SHOP_MAILS).</summary>
    public static bool ShopMails = true;
    public static bool Free(Tenant t) => t.ClientMailsEnabled || ShopMails;

    /// <summary>What goes and with which words.</summary>
    public static JsonObject Current(Tenant t)
    {
        var own = Json.Obj(t.ClientMails); var d = Defaults(); var free = Free(t);
        foreach (var (k, v) in d.ToList())
        {
            var conf = (JsonObject)v;
            if (own[k] is JsonObject mine)
                foreach (var (x, y) in mine)
                    if (free || x is not ("subject" or "body" or "subject_en" or "body_en") && !(x == "on" && STANDARD.Contains(k))) conf[x] = y?.DeepClone();
        }
        return d;
    }

    public static Dictionary<string, string> Values(Ctx c, Booking b, JsonObject data, string lang = "it")
    {
        var rows = new Bookings(c).VisitRows(b);
        var start = c.Cal.ToLocal(rows[0].Start); var end = c.Cal.ToLocal(rows[^1].End);
        var service = c.Row<Service>(b.ServiceId);
        var token = data.Str("cancel_token");
        string Name(Booking r)
        {
            var s = c.Row<Service>(r.ServiceId);
            var n = lang == "en" && !string.IsNullOrEmpty(s.NameEn) ? s.NameEn : r.Snap.Str("service") is var x && x != "" ? x : s.Name;
            return string.Join(" · ", new[] { n, r.Snap.Str("option") }.Where(y => y != ""));
        }
        return new Dictionary<string, string>
        {
            ["nome"] = data.Str("first_name"), ["servizio"] = string.Join(" + ", rows.Select(Name)),
            ["quando"] = Texts.When(start, end, service.Kind == "day", lang), ["data"] = $"{start:dd/MM/yyyy}", ["ora"] = $"{start:HH:mm}",
            ["sede"] = b.Snap.Str("location"), ["operatore"] = string.Join(", ", rows.Select(r => r.Snap.Str("resource")).Where(x => x != "").Distinct()),
            ["attivita"] = c.Tenant.Name, ["istruzioni"] = service.Instructions ?? "",
            ["link"] = token != "" ? $"{c.PublicOrigin}/action/{c.Tenant.Slug}/{token}/" : "",
        };
    }

    public static string CodesLine(Location l) => string.Join(" · ", new[] { ("CIN", l.Cin), ("CIR", l.Cir) }.Where(x => !string.IsNullOrEmpty(x.Item2)).Select(x => $"{x.Item1} {x.Item2}"));

    /// <summary>The email of this moment, if it goes and the client wants it.</summary>
    public static Delivery Send(Ctx c, Booking b, string kind)
    {
        if (WpSync.BySite(c)) return null;
        var mails = Current(c.Tenant); var conf = (JsonObject)mails[kind]; var bk = new Bookings(c); var data = bk.Data(b);
        if (!Json.Truthy(conf["on"]) || data.Str("email") == "" || b.Snap["notices"] is JsonValue nv && nv.TryGetValue<bool>(out var no) && !no) return null;
        var contact = c.Row<Contact>(b.ContactId);
        if (contact != null && !contact.Notices) return null;
        var lang = b.Snap.Str("lang") is var l && l != "" ? l : "it";
        var en = lang == "en" && conf.Str("body_en") != "";
        var v = Values(c, b, data, lang);
        var body = Texts.Fill(en ? conf.Str("body_en") : Tr.T(lang, conf.Str("body")), v);
        var links = new List<(string, string)>();
        if (kind is "confirmed" or "accepted" && Json.Truthy(mails["confirmed"]?["price"]) && b.Snap.Str("total") != "")
            body += "\n" + Tr.T(lang, "Prezzo indicativo: {prezzo}, si paga in sede").Replace("{prezzo}", Prices.Euro(b.Snap.Str("total")));
        var codes = CodesLine(c.Row<Location>(b.LocationId));
        if (codes != "") body += "\n" + codes;
        if (kind == "cancelled" && b.Snap.Str("cancel_reason") == "unconfirmed") body = Tr.T(lang, "Non abbiamo ricevuto la tua conferma.") + "\n" + body;
        if (kind is "reminder" or "reminder2")
            foreach (var (label, what) in new[] { ("Confermo", "confirm"), ("Non vengo", "decline") })
                links.Add((Tr.T(lang, label), $"{c.PublicOrigin}/action/{c.Tenant.Slug}/{bk.ActionToken(what, b.ContactId, b.Id, 4)}/"));
        if (kind == "review") links.Add((Tr.T(lang, "Lascia una recensione"), conf.Str("url")));
        if (kind == "recall")
        {
            v["link"] = $"{c.PublicOrigin}/book/{c.Tenant.Slug}/"; body = Texts.Fill(conf.Str("body"), v); links.Add(("Prenota", v["link"]));
        }
        var service = c.Row<Service>(b.ServiceId);
        if (!string.IsNullOrEmpty(service.RemoteUrl) && kind is "confirmed" or "accepted" or "moved" or "reminder" or "reminder2") links.Insert(0, ("Entra nella videochiamata", service.RemoteUrl));
        var subject = string.Join(" ", Texts.Fill(en && conf.Str("subject_en") != "" ? conf.Str("subject_en") : Tr.T(lang, conf.Str("subject")), v).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        var payload = new JsonObject
        {
            ["to"] = data.Str("email"), ["subject"] = subject, ["body"] = body + string.Concat(links.Select(x => $"\n{x.Item1}: {x.Item2}")),
            ["html"] = Texts.EmailHtml(c.Tenant, body, links, lang),
        };
        if (kind is "confirmed" or "accepted" or "moved") payload["ics"] = Texts.Ics(c, b);
        return bk.Queue(payload, b.ContactId);
    }

    public static readonly (string Id, string Label)[] WA_WHEN = { ("", "Spenti"), ("d1", "Il giorno di apertura prima (consigliato): il sabato per il martedì, se domenica e lunedì è chiuso"),
        ("t0", "La mattina stessa: gli appuntamenti di oggi"), ("h24", "Nelle prossime 24 ore"), ("h3", "Nelle prossime 3 ore"), ("h2", "Nelle prossime 2 ore") };
    /// <summary>Which of the n days from first the business works: opening hours on that weekday, no closure of the whole
    /// business. Without any opening hours written, every day.</summary>
    public static List<DateOnly> OpenDays(Ctx c, DateOnly first, int n = 7)
    {
        var days = Enumerable.Range(0, n).Select(first.AddDays).ToList();
        var hours = c.S.Query<(short, DateTime?, DateTime?)>("SELECT weekday, valid_from, valid_until FROM core_schedule WHERE purpose='service'").ToList();
        if (hours.Count == 0) return days;
        var shut = c.S.Query<(DateTime, DateTime?)>(@"SELECT date, until FROM core_exceptionday WHERE purpose='service' AND closed AND location_id IS NULL AND service_id IS NULL AND resource_id IS NULL
            AND date<=@z AND (until>=@a OR until IS NULL AND date>=@a)", new { a = first.ToDateTime(TimeOnly.MinValue), z = days[^1].ToDateTime(TimeOnly.MinValue) }).ToList();
        DateOnly D(DateTime x) => DateOnly.FromDateTime(x);
        return days.Where(d => hours.Any(h => h.Item1 == Calendar.Weekday(d) && !(h.Item2.HasValue && D(h.Item2.Value) > d) && !(h.Item3.HasValue && D(h.Item3.Value) < d))
            && !shut.Any(x => D(x.Item1) <= d && d <= D(x.Item2 ?? x.Item1))).ToList();
    }
    /// <summary>The start and end (UTC) of the appointments to remind now, or null when the business does not want them.</summary>
    public static (DateTime A, DateTime B)? WaWindow(Ctx c, DateTime now)
    {
        var w = c.Tenant.WaWhen ?? "";
        if (w == "" || !WA_WHEN.Any(x => x.Id == w)) return null;
        DateTime Mid(DateOnly d) => c.Cal.Local(d, TimeSpan.Zero).Value;
        if (w[0] == 'h') return (now, now.AddHours(int.Parse(w[1..])));
        var tomorrow = c.Today.AddDays(1);
        if (w == "t0") return (now, Mid(tomorrow));
        // d1: tomorrow and the closed days after it, up to the next day the business works (a week at most)
        var last = OpenDays(c, tomorrow).DefaultIfEmpty(tomorrow).First();
        return (Mid(tomorrow), Mid(last.AddDays(1)));
    }

    /// <summary>The wa.me link to this phone, empty without a usable number; with a booking, the business's text filled.</summary>
    public static string WhatsApp(Ctx c, string phone, Booking b = null, string text = null)
    {
        var number = Clients.NormalizePhone(phone);
        if (number == "") return "";
        number = number[1..];
        var t = b != null ? Texts.Fill(text ?? (string.IsNullOrEmpty(c.Tenant.WhatsappText) ? WHATSAPP : c.Tenant.WhatsappText), Values(c, b, new Bookings(c).Data(b))) : "";
        return $"https://wa.me/{number}" + (t != "" ? "?text=" + Uri.EscapeDataString(t) : "");
    }

    static readonly Dictionary<string, string> BEFORE = new() { ["reminder"] = "reminded", ["reminder2"] = "reminded2", ["arrival"] = "arrival" };

    /// <summary>remind(): the messages due for one business, each once. Returns how many went out.</summary>
    public static int Remind(Ctx c, Func<Ctx, Booking, string, bool> sms = null)
    {
        var now = c.Now; var conf = Current(c.Tenant); var sent = 0; var bk = new Bookings(c);
        foreach (var (kind, mark) in BEFORE)
        {
            var k = (JsonObject)conf[kind];
            if (!Json.Truthy(k["on"])) continue;
            var at = k.Str("at");
            var window = TimeSpan.FromHours(at != "" ? 48 : (int)k["hours"]);
            var sql = "SELECT b.* FROM core_booking b JOIN core_service s ON s.id=b.service_id WHERE b.status='confirmed' AND b.start>@n AND b.start<=@w AND NOT (b.snapshot ? @m)"
                + (kind == "arrival" ? " AND s.kind IN ('day','event') AND s.instructions<>''" : "") + " FOR UPDATE OF b SKIP LOCKED";
            foreach (var b in c.S.Query<Booking>(sql, new { n = now, w = now + window, m = mark }).ToList())
            {
                var due = at != "" ? c.Cal.Local(c.Cal.DayOf(b.Start).AddDays(-1), TimeSpan.Parse(at))!.Value : b.Start - window;
                if (due > now) continue;
                var later = b.Visit != null && c.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE visit=@v AND start<@s AND status<>'cancelled'", new { v = b.Visit, s = b.Start }) > 0;
                if (!later && (kind == "arrival" || b.Created <= due))
                {
                    var a = Send(c, b, kind) != null; var t = sms?.Invoke(c, b, kind) ?? false;
                    if (a | t) { sent++; if (kind != "arrival") b.Snap["asked"] = bk.Iso(now); }
                }
                b.Snap[mark] = bk.Iso(now); c.S.Update(b, "snapshot");
            }
        }
        sent += Unconfirmed(c, (int?)conf["reminder"]?["autocancel"] ?? 0) + Reviews(c, (JsonObject)conf["review"]) + Recall(c, (JsonObject)conf["recall"]);
        return sent;
    }

    static int Unconfirmed(Ctx c, int hours)
    {
        if (hours == 0) return 0;
        var window = TimeSpan.FromHours(hours); var done = 0; var bk = new Bookings(c);
        foreach (var b in c.S.Query<Booking>("SELECT * FROM core_booking WHERE status='confirmed' AND start>@n AND start<=@w AND snapshot ? 'asked' AND NOT (snapshot ? 'client_confirmed')", new { n = c.Now, w = c.Now + window }).ToList())
            if (Times.ParseAware(b.Snap.Str("asked"), out _) <= b.Start - window) { bk.ChangeStatus(b, "cancelled", "unconfirmed"); done++; }
        return done;
    }

    static int Reviews(Ctx c, JsonObject conf)
    {
        if (!Json.Truthy(conf["on"]) || conf.Str("url") == "") return 0;
        var now = c.Now; var after = now.AddHours(-(int)conf["hours"]); var sent = 0; var bk = new Bookings(c);
        var rows = c.S.Query<Booking>(@"SELECT b.* FROM core_booking b JOIN core_contact ct ON ct.id=b.contact_id WHERE b.status='completed' AND b.""end""<=@a AND b.""end"">@a2
            AND ct.consent='yes' AND NOT ct.suppressed AND NOT (b.snapshot ? 'review') FOR UPDATE OF b SKIP LOCKED", new { a = after, a2 = after.AddDays(-2) }).ToList();
        foreach (var b in rows)
        {
            var recent = c.S.Query<string>("SELECT snapshot::text FROM core_booking WHERE contact_id=@c AND \"end\">@e AND snapshot ? 'review'", new { c = b.ContactId, e = now.AddDays(-14) });
            var go = !recent.Any(s => Json.Obj(s).Str("review") is var r && r != "" && Times.ParseAware(r, out _) > now.AddDays(-7));
            b.Snap["review"] = go && Send(c, b, "review") != null ? bk.Iso(now) : "";
            if (b.Snap.Str("review") != "") sent++;
            c.S.Update(b, "snapshot");
        }
        return sent;
    }

    static int Recall(Ctx c, JsonObject conf)
    {
        if (!Json.Truthy(conf["on"])) return 0;
        var sent = 0; var bk = new Bookings(c);
        foreach (var r in new Clients(c).Recalls(c.Now, 0))
        {
            var b = r.Booking; var ct = c.Row<Contact>(b.ContactId);
            if (b.Snap.ContainsKey("recall_sent") || ct.Consent != "yes" || ct.Suppressed) continue;
            b.Snap["recall_sent"] = Send(c, b, "recall") != null ? bk.Iso(c.Now) : "";
            if (b.Snap.Str("recall_sent") != "") sent++;
            c.S.Update(b, "snapshot");
        }
        return sent;
    }
}

/// <summary>core/notify.py: the business's notices (email and Telegram), minimised: no email, phone, notes or answers.</summary>
public static class Notify
{
    public static readonly Dictionary<string, string> TITLES = new()
    { ["created"] = "Nuova prenotazione", ["requested"] = "Richiesta da confermare", ["moved"] = "Prenotazione spostata", ["cancelled"] = "Prenotazione annullata", ["declined"] = "Il cliente dice che non viene" };

    public static string Short(JsonObject d) => (d.Str("first_name").Trim() + (d.Str("last_name") != "" ? " " + d.Str("last_name").Trim()[..1].ToUpperInvariant() + "." : "")).Trim();

    public static string Summary(Ctx c, Booking b, string ev)
    {
        var bk = new Bookings(c); var d = bk.Data(b); var s = b.Snap; var service = c.Row<Service>(b.ServiceId);
        var rows = bk.VisitRows(b);
        var lines = new List<string> { TITLES[ev] + " · " + c.Tenant.Name,
            rows.Count < 2 ? "Servizio: " + s.Str("service") : "Servizi: " + string.Join(" + ", rows.Select(r => $"{r.Snap.Str("service")} {c.Cal.ToLocal(r.Start):HH:mm} ({r.Snap.Str("resource")})")) };
        if (s.Str("option") != "") lines.Add("Prestazione: " + s.Str("option"));
        if (s.Str("location") != "") lines.Add("Sede: " + s.Str("location"));
        if (s.Str("resource") != "" && rows.Count < 2) lines.Add((string.IsNullOrEmpty(service.ResourceLabel) ? "Risorsa" : service.ResourceLabel) + ": " + s.Str("resource"));
        if (service.Kind == "day")
        {
            var nights = c.Cal.DayOf(b.End).DayNumber - c.Cal.DayOf(b.Start).DayNumber;
            lines.Add($"Arrivo: {c.Cal.ToLocal(b.Start):dd/MM/yyyy HH:mm}"); lines.Add($"Partenza: {c.Cal.ToLocal(b.End):dd/MM/yyyy HH:mm}"); lines.Add($"Notti: {nights}");
        }
        else lines.Add($"Quando: {c.Cal.ToLocal(b.Start):dd/MM/yyyy HH:mm}");
        if (b.Party > 1 || service.Kind != "resource") lines.Add($"Persone / quantità: {b.Party}");
        lines.Add("Cliente: " + (Short(d) is var n && n != "" ? n : "—"));
        lines.Add($"Dettagli: {c.PublicOrigin}/t/{c.Tenant.Slug}/bookings/{b.Id}/");
        return string.Join("\n", lines);
    }

    /// <summary>The people limited to their own operators who have an email other than the business's.</summary>
    public static List<(string Email, List<string> Resources)> Operators(Ctx c, Guid? resource, Func<Guid, string> emailOf)
    {
        var outp = new List<(string, List<string>)>();
        if (!c.Tenant.NotifyOperators || emailOf == null) return outp;
        foreach (var m in c.S.Query<Membership>("SELECT m.* FROM core_membership m JOIN core_user u ON u.id=m.user_id WHERE m.tenant_id=@t AND u.is_active", new { t = c.Tenant.Id }))
        {
            var res = Json.Strings(m.Resources);
            var email = res.Count > 0 ? emailOf(m.UserId) : "";
            if (email != "" && email != c.Tenant.NotifyEmail && (resource == null || res.Contains(resource.ToString()))) outp.Add((email, res));
        }
        return outp;
    }
    /// <summary>The emails of the accounts, set by the server (they are sealed with the master key, not the business's).</summary>
    public static Func<Guid, string> EmailOf;

    public static void Staff(Ctx c, Booking b, string ev)
    {
        if (string.IsNullOrEmpty(c.Tenant.NotifyEmail) && string.IsNullOrEmpty(c.Tenant.Telegram) && !c.Tenant.NotifyOperators) return;
        var text = Summary(c, b, ev); var subject = TITLES[ev] + " · " + b.Snap.Str("service");
        var to = new List<string>(); if (!string.IsNullOrEmpty(c.Tenant.NotifyEmail)) to.Add(c.Tenant.NotifyEmail);
        var bk = new Bookings(c);
        foreach (var rid in bk.VisitRows(b).Where(x => x.ResourceId.HasValue).Select(x => x.ResourceId.Value).Distinct())
            foreach (var (e, _) in Operators(c, rid, EmailOf)) if (!to.Contains(e)) to.Add(e);
        if (WpSync.BySite(c)) to.Clear();
        foreach (var a in to) bk.Queue(new JsonObject { ["to"] = a, ["subject"] = subject, ["body"] = text });
        if (!string.IsNullOrEmpty(c.Tenant.Telegram))
        {
            var d = bk.Queue(new JsonObject { ["text"] = text }); d.Channel = "telegram"; c.S.Update(d, "channel");
        }
    }
}

/// <summary>core/api.py emit: the webhook of a booking, when the business has one (no client data).</summary>
public static class Webhooks
{
    static readonly Dictionary<string, string> EVENTS = new() { ["created"] = "booking.created", ["moved"] = "booking.moved", ["cancelled"] = "booking.cancelled" };
    public static void Emit(Ctx c, Booking b, string ev)
    {
        if (string.IsNullOrEmpty(c.Tenant.Webhook) || !EVENTS.ContainsKey(ev)) return;
        var bk = new Bookings(c);
        var payload = new JsonObject
        {
            ["id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), ["event"] = EVENTS[ev], ["at"] = Times.Iso(c.Now, TimeZoneInfo.Utc),
            ["booking"] = new JsonObject
            {
                ["id"] = b.Id.ToString(), ["status"] = b.Status, ["start"] = bk.Iso(b.Start), ["end"] = bk.Iso(b.End), ["service"] = b.ServiceId.ToString(),
                ["resource"] = b.ResourceId?.ToString() ?? "", ["party"] = b.Party, ["origin"] = b.Origin, ["source"] = b.Source,
            },
        };
        var d = bk.Queue(payload); d.Channel = "webhook"; c.S.Update(d, "channel");
    }
}
