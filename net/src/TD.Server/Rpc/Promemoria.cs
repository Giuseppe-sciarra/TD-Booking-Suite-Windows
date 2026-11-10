using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>platform.notifications_view: Configurazione promemoria: the WhatsApp texts, the notices for the business
/// (email, Telegram), the evening summary, the emails to the clients with their words and timing.</summary>
public static class Promemoria
{
    static readonly Regex TOKEN = new(@"^\d{5,12}:[A-Za-z0-9_-]{30,60}\z"), CHAT = new(@"^(-?\d{1,20}|@[A-Za-z0-9_]{5,32})\z");
    static void Allowed(Call c) { if (!(c.Can("users.manage") || c.User.IsStaff)) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione."); }

    [Rpc("promemoria")]
    public static object Page(Call c)
    {
        Allowed(c); var x = c.C; var t = c.Tenant; var telegram = string.IsNullOrEmpty(t.Telegram) ? new JsonObject() : x.Decrypt(t.Telegram, "telegram");
        var now = ClientMails.Current(t); var defaults = ClientMails.Defaults(); var own = Json.Obj(t.ClientMails); var free = ClientMails.Free(t);
        var operators = !t.NotifyOperators ? (int?)null : x.S.Query<Membership>("SELECT m.* FROM core_membership m JOIN core_user u ON u.id=m.user_id WHERE m.tenant_id=@t AND u.is_active", new { t = t.Id })
            .Count(m => Permissions.Ids(m.Resources).Count > 0 && c.S.Identity.Email(x.S.One<User>("SELECT * FROM core_user WHERE id=@u", new { u = m.UserId })) is var e && e != "" && e != t.NotifyEmail);
        object Mail(string k)
        {
            var (_, label, help) = ClientMails.KINDS.First(q => q.Key == k); var m = (JsonObject)now[k]; var d = (JsonObject)defaults[k];
            return new
            {
                chiave = k, nome = label, aiuto = help, accesa = Json.Truthy(m["on"]), libera = !ClientMails.STANDARD.Contains(k), bloccata = ClientMails.ADMIN.Contains(k) && !(c.User.IsStaff || ClientMails.ShopMails),
                scelte = ClientMails.TIMING.GetValueOrDefault(k), ore = m["hours"] != null ? Till.Int(m["hours"]) : 0, haOra = d.ContainsKey("at"), alle = m.Str("at"), annulla = Till.Int(m["autocancel"]), prezzo = Json.Truthy(m["price"]), url = m.Str("url"),
                oggetto = m.Str("subject"), testo = m.Str("body"), oggettoEn = m.Str("subject_en"), testoEn = m.Str("body_en"), oggettoStandard = d.Str("subject"), testoStandard = d.Str("body"),
            };
        }
        return new
        {
            email = t.NotifyEmail, chat = telegram.Str("chat"), haToken = telegram.Str("token") != "", avvisaOperatori = t.NotifyOperators, operatori = operators, nome = t.Name,
            gruppi = ClientMails.GROUPS.Select(g => new { nome = g.Name, email = g.Keys.Select(Mail) }), campi = ClientMails.FIELDS.Select(f => new { chiave = f.Key, nome = f.Label }), annulli = ClientMails.AUTOCANCEL, esempio = ClientMails.Sample(x),
            whatsapp = string.IsNullOrEmpty(t.WhatsappText) ? ClientMails.WHATSAPP : t.WhatsappText, promemoria = string.IsNullOrEmpty(t.WhatsappReminder) ? ClientMails.REMINDER : t.WhatsappReminder, grazie = string.IsNullOrEmpty(t.WhatsappThanks) ? ClientMails.THANKS : t.WhatsappThanks,
            recensione = now["review"].Str("url"), sms = own["texts"].Str("sms") is var sms && sms != "" ? sms : ClientMails.SMS_TEXT, wa = own["texts"].Str("wa"), messaggi = !string.IsNullOrEmpty(t.Texts),
            oreRiepilogo = Enumerable.Range(16, 7), riepilogo = t.SummaryHour, libere = free, sito = WpSync.BySite(x),
            // the owner sees WhatsApp and, only once the administrator has set them up, the email and Telegram parts
            admin = c.User.IsStaff, emailPronta = Accounts.MailReady(x.S, t), telegramPronto = !string.IsNullOrEmpty(t.Telegram),
        };
    }

    [Rpc("promemoria.azione", Fresh = true)]
    public static object Action(Call c)
    {
        Allowed(c); var x = c.C; var t = c.Tenant; var action = c.Str("azione", "save"); var bk = new Bookings(x);
        string Text(string key, string what)
        {
            var v = c.Str(key).Replace("\r\n", "\n").Trim();
            if (v.Length > 1000 || v.Any(ch => ch < 32 && ch != '\n')) throw new BookingException($"{what}: al massimo 1000 caratteri, senza caratteri speciali.");
            return v;
        }
        switch (action)
        {
            case "save":
                {
                    if (!c.User.IsStaff) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione.");  // the administrator's settings
                    var current = string.IsNullOrEmpty(t.Telegram) ? new JsonObject() : x.Decrypt(t.Telegram, "telegram");
                    var email = c.Str("notify_email").Trim(); if (email != "") Accounts.ValidateEmail(email);
                    var token = c.Str("telegram_token").Trim() is var tk && tk != "" ? tk : current.Str("token"); var chat = c.Str("telegram_chat").Trim();
                    if (c.Bool("telegram_off")) token = chat = "";
                    if (token != "" != (chat != "")) throw new BookingException("Per Telegram servono sia il token del bot sia la chat.");
                    if (token != "" && (!TOKEN.IsMatch(token) || !CHAT.IsMatch(chat))) throw new BookingException("Token del bot o chat Telegram non validi.");
                    x.S.Execute("UPDATE core_tenant SET notify_email=@e, telegram=@g, notify_operators=@o WHERE id=@t", new { e = email, g = token != "" ? x.Encrypt(new JsonObject { ["token"] = token, ["chat"] = chat }, "telegram") : "", o = c.Bool("notify_operators"), t = t.Id });
                    x.Audit("notifications.saved", null, new { telegram = token != "", email = email != "", operators = c.Bool("notify_operators") });
                    return new { testo = "Avvisi salvati." };
                }
            case "client_mails":
                {
                    // which emails reach the clients and with which words, the reminders and when they go
                    if (WpSync.BySite(x)) throw new BookingException("Le email ai clienti le manda il sito: i testi si cambiano nel plugin, in WordPress › Impostazioni › TD Booking Suite.");
                    x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = t.Id });
                    t.ClientMails = Json.Dump(ClientMails.Clean(c.Obj("valori"), ClientMails.Free(t), Json.Obj(t.ClientMails), c.User.IsStaff || ClientMails.ShopMails));
                    x.S.Execute("UPDATE core_tenant SET client_mails=@m::jsonb WHERE id=@t", new { m = t.ClientMails, t = t.Id });
                    x.Audit("client_mails.saved", null, new { on = ClientMails.Current(t).Where(k => Json.Truthy(k.Value["on"])).Select(k => k.Key).ToArray() });
                    return new { testo = "Email ai clienti salvate." };
                }
            case "whatsapp":
                {
                    // the owner's text of the WhatsApp button; empty, or the standard one, goes back to the standard
                    var text = Text("whatsapp_text", "Messaggio WhatsApp"); var again = Text("whatsapp_reminder", "Promemoria WhatsApp"); var thanks = Text("whatsapp_thanks", "Grazie WhatsApp");
                    var url = c.Str("review_url").Trim(); if (url.Length > 500) url = url[..500];
                    if (url != "" && !url.StartsWith("https://") && !url.StartsWith("http://")) throw new BookingException("Link alla recensione: deve iniziare con https://");
                    var own = Json.Obj(t.ClientMails); var review = own["review"] as JsonObject ?? new JsonObject(); review["url"] = url; if (review.Parent == null) own["review"] = review;
                    x.S.Execute("UPDATE core_tenant SET whatsapp_text=@a, whatsapp_reminder=@b, whatsapp_thanks=@k, client_mails=@m::jsonb WHERE id=@t",
                        new { a = text == ClientMails.WHATSAPP ? "" : text, b = again == ClientMails.REMINDER ? "" : again, k = thanks == ClientMails.THANKS ? "" : thanks, m = Json.Dump(own), t = t.Id });
                    x.Audit("whatsapp.saved", t.Id);
                    return new { testo = "Messaggio WhatsApp salvato." };
                }
            case "summary":
                {
                    // tomorrow's bookings every evening at this hour; empty = never
                    var hour = c.Str("summary_hour"); if (hour != "" && !(int.TryParse(hour, out var h) && h is >= 16 and <= 22)) throw new BookingException("Scegli a che ora mandare il riepilogo.");
                    x.S.Execute("UPDATE core_tenant SET summary_hour=@h WHERE id=@t", new { h = hour != "" ? (short?)short.Parse(hour) : null, t = t.Id });
                    x.Audit("summary.saved", t.Id, new { hour });
                    return new { testo = "Riepilogo serale salvato." };
                }
            case "test":
                {
                    if (string.IsNullOrEmpty(t.NotifyEmail) && string.IsNullOrEmpty(t.Telegram)) throw new BookingException("Configura prima un indirizzo email o Telegram.");
                    var text = "Prova notifiche · " + t.Name + "\nSe leggi questo messaggio, le notifiche funzionano.";
                    if (!string.IsNullOrEmpty(t.NotifyEmail)) bk.Queue(new JsonObject { ["to"] = t.NotifyEmail, ["subject"] = "Prova notifiche · " + t.Name, ["body"] = text });
                    if (!string.IsNullOrEmpty(t.Telegram)) { var d = bk.Queue(new JsonObject { ["text"] = text }); d.Channel = "telegram"; x.S.Update(d, "channel"); }
                    return new { testo = "Prova accodata: arriva al prossimo giro di invii." };
                }
        }
        throw new BookingException("Operazione non valida.");
    }
}
