using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>platform.texts_view: SMS, WhatsApp e calendari: the outside services, set by the administrator: SMS and
/// WhatsApp for the reminders, the OAuth clients of Google Calendar and Outlook for the operators.</summary>
public static class Testi
{
    static JsonObject Google(Ctx x) => string.IsNullOrEmpty(x.Tenant.Google) ? new JsonObject() : x.Decrypt(x.Tenant.Google, "google");
    static JsonObject Microsoft(Ctx x) => string.IsNullOrEmpty(x.Tenant.Microsoft) ? new JsonObject() : x.Decrypt(x.Tenant.Microsoft, "microsoft");

    [Rpc("testi", Staff = true)]
    public static object Page(Call c)
    {
        var x = c.C; var t = c.Tenant; var conf = TextMessages.Config(x); var g = Google(x); var m = Microsoft(x);
        return new
        {
            nome = t.Name, canale = TextMessages.Channel(conf), fornitori = TextMessages.PROVIDERS.Select(p => new { id = p.Key, nome = p.Label }),
            provider = conf.Str("provider"), user = conf.Str("user"), haSecret = conf.Str("secret") != "", sender = conf.Str("sender"),
            waPhone = conf.Str("wa_phone"), haWaToken = conf.Str("wa_token") != "", waTemplate = conf.Str("wa_template"), waLang = conf.Str("wa_lang") != "" ? conf.Str("wa_lang") : "it", limite = t.TextsDailyLimit,
            google = g.Count > 0, googleId = g.Str("client_id"), googleSecret = g.Str("client_secret") != "", googleUri = c.PublicOrigin + "/oauth/google-calendar/",
            microsoft = m.Count > 0, microsoftId = m.Str("client_id"), microsoftSecret = m.Str("client_secret") != "", microsoftUri = c.PublicOrigin + "/oauth/outlook-calendar/",
        };
    }

    [Rpc("testi.azione", Staff = true, Fresh = true)]
    public static object Action(Call c)
    {
        var x = c.C; var t = c.Tenant; var saved = TextMessages.Config(x); string P(string k) => c.Str(k).Trim();
        x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = t.Id });
        switch (c.Str("azione"))
        {
            case "google":
                {
                    // gcal.clean: the OAuth client as typed; an empty secret keeps the saved one, off removes it
                    var conf = new JsonObject();
                    if (!c.Bool("google_off") && !(P("client_id") == "" && P("client_secret") == ""))
                    {
                        conf["client_id"] = P("client_id"); conf["client_secret"] = P("client_secret") != "" ? P("client_secret") : Google(x).Str("client_secret");
                        // they go into Google's addresses and forms: only the characters Google uses
                        if (!Regex.IsMatch(conf.Str("client_id"), @"^[\w.\-]{5,200}\.apps\.googleusercontent\.com\z")) throw new BookingException("L'ID client finisce con .apps.googleusercontent.com.");
                        if (!Regex.IsMatch(conf.Str("client_secret"), @"^[\w\-]{0,200}\z")) throw new BookingException("Il client secret non è valido.");
                    }
                    x.S.Execute("UPDATE core_tenant SET google=@g WHERE id=@t", new { g = conf.Count > 0 ? x.Encrypt(conf, "google") : "", t = t.Id });
                    if (conf.Count == 0)
                    {
                        // switched off: the operators' connections go with it, and the blocks read
                        x.S.Execute("UPDATE core_resource SET google='', google_at=NULL, google_error='Google Calendar spento dall''agenzia.' WHERE google<>''");
                        x.S.Execute("DELETE FROM core_timeblock WHERE reason='google'");
                    }
                    x.Audit("google.saved", null, new { on = conf.Count > 0 });
                    return new { testo = "Google Calendar salvato." };
                }
            case "microsoft":
                {
                    var conf = new JsonObject();
                    if (!c.Bool("microsoft_off") && !(P("ms_client_id") == "" && P("ms_client_secret") == ""))
                    {
                        conf["client_id"] = P("ms_client_id"); conf["client_secret"] = P("ms_client_secret") != "" ? P("ms_client_secret") : Microsoft(x).Str("client_secret");
                        if (!Regex.IsMatch(conf.Str("client_id"), @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")) throw new BookingException("L'ID applicazione (client) di Microsoft è come 1a2b3c4d-1234-5678-9abc-1234567890ab.");
                        if (!Regex.IsMatch(conf.Str("client_secret"), @"^[\w.~\-]{0,200}\z")) throw new BookingException("Il valore del segreto client non è valido.");
                    }
                    x.S.Execute("UPDATE core_tenant SET microsoft=@g WHERE id=@t", new { g = conf.Count > 0 ? x.Encrypt(conf, "microsoft") : "", t = t.Id });
                    if (conf.Count == 0)
                    {
                        x.S.Execute("UPDATE core_resource SET microsoft='', microsoft_at=NULL, microsoft_error='Outlook spento dall''agenzia.' WHERE microsoft<>''");
                        x.S.Execute("DELETE FROM core_timeblock WHERE reason='outlook'");
                    }
                    x.Audit("microsoft.saved", null, new { on = conf.Count > 0 });
                    return new { testo = "Outlook / Microsoft 365 salvato." };
                }
            case "test":
                {
                    var to = Clients.NormalizePhone(c.Str("test_to"));
                    if (to == "" || saved.Count == 0) throw new BookingException("Salva prima un canale e scrivi un numero di cellulare.");
                    try
                    {
                        var (used, _) = TextMessages.Deliver(saved, TextMessages.Channel(saved), new JsonObject { ["to"] = to, ["body"] = $"Messaggio di prova di {t.Name}: l'invio funziona.", ["params"] = new JsonArray("Prova", "Servizio", "domani alle 10:30", t.Name) });
                        return new { testo = "Prova inviata via " + (used == "whatsapp" ? "WhatsApp" : "SMS") + "." };
                    }
                    catch (TextError ex) { throw new BookingException("Il servizio ha rifiutato il messaggio: " + ex.Code); }
                }
            default:
                {
                    var conf = TextMessages.Clean(c.Args, saved);
                    if (!(int.TryParse(c.Str("texts_daily_limit"), out var limit) && limit is >= 0 and <= 5000)) throw new BookingException("Messaggi al giorno: da 0 a 5000.");
                    x.S.Execute("UPDATE core_tenant SET texts=@x, texts_daily_limit=@l WHERE id=@t", new { x = conf.Count > 0 ? x.Encrypt(conf, "texts") : "", l = limit, t = t.Id });
                    x.Audit("texts.saved", null, new { channel = TextMessages.Channel(conf), limit });
                    return new { testo = "SMS e WhatsApp salvati." };
                }
        }
    }
}
