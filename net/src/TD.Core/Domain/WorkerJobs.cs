using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MailKit.Net.Smtp;
using MailKit.Security;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>The background work of mail_worker.py, one function per job: the outbox (email, Telegram, webhook, SMS and
/// WhatsApp), the waiting list, the evening list, the texts' receipts, the turns, the retention. Each business in its
/// own unit of work; what leaves the PC leaves outside any transaction.</summary>
public sealed class WorkerJobs(Db db, Crypto crypto, string origin, IOutbox outbox)
{
    Ctx Open(Guid tenant) => new(db, crypto, tenant) { PublicOrigin = origin, Out = outbox ?? NoOutbox.Instance };
    T Unit<T>(Guid tenant, Func<Ctx, T> f) { using var c = Open(tenant); var r = f(c); c.Commit(); return r; }
    List<Guid> Tenants(string where = "true", object args = null) { using var g = db.Global(); return g.Query<Guid>($"SELECT id FROM core_tenant WHERE {where} ORDER BY created", args).ToList(); }

    // ---------- mail.worker_once
    sealed record Claimed(string Channel, JsonObject Payload, JsonObject Config, byte[] Logo, int Attempts, Guid? Contact);

    /// <summary>Durable outbox. Each recipient rechecked at dispatch; no automatic retry after an uncertain hand-over.</summary>
    public Dictionary<string, int> Outgoing()
    {
        var counts = new Dictionary<string, int> { ["accepted"] = 0, ["failed"] = 0, ["skipped"] = 0, ["unknown"] = 0 };
        var host = origin.Contains("://") ? origin.Split("://", 2)[1].Split(':')[0] : "localhost";
        foreach (var tenant in Tenants())
        {
            var candidates = Unit(tenant, c =>
            {
                // a crashed worker after the hand-over has uncertain delivery: never retry automatically
                c.S.Execute("UPDATE core_delivery SET status='unknown', last_code='worker_interrupted' WHERE status='sending' AND claimed_at<@a", new { a = c.Now.AddMinutes(-10) });
                var t = c.Tenant; var eligible = "d.campaign_id IS NULL";
                if (t.MarketingEnabled && c.Operational()) eligible = "(d.campaign_id IS NULL OR k.status='queued')";
                // texts waiting for their daily budget stay out of the batch, or 50 of them hold back every email
                if (!string.IsNullOrEmpty(t.Texts) && TextMessages.Spent(c) >= t.TextsDailyLimit) eligible += " AND d.channel NOT IN ('sms','whatsapp')";
                return c.S.Query<Guid>($"SELECT d.id FROM core_delivery d LEFT JOIN core_campaign k ON k.id=d.campaign_id WHERE {eligible} AND d.status='pending' AND d.next_attempt<=@n ORDER BY (d.campaign_id IS NOT NULL), d.next_attempt, d.id LIMIT 50", new { n = c.Now }).ToList();
            });
            foreach (var id in candidates)
            {
                var claim = Unit(tenant, c => Claim(c, id, counts));
                if (claim == null) continue;
                var status = "accepted"; var code = ""; var retry = false; var used = claim.Channel; var reference = "";
                if (claim.Channel != "email")
                {
                    try
                    {
                        if (claim.Channel == "telegram") SendTelegram(claim.Config, claim.Payload);
                        else if (claim.Channel == "webhook") SendWebhook(claim.Config, claim.Payload);
                        else (used, reference) = TextMessages.Deliver(claim.Config, claim.Channel, claim.Payload);
                    }
                    catch (TextError ex) { code = ex.Code; retry = ex.Temporary && claim.Attempts < (claim.Channel == "webhook" ? 8 : 5); status = retry ? "pending" : "failed"; }
                    catch (Exception) { status = "unknown"; code = "delivery_uncertain"; }
                }
                else
                {
                    try { Mail.Send(claim.Config, claim.Payload, $"<{id}@{host}>", claim.Logo); }
                    catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted)
                    {
                        var temporary = (int)ex.StatusCode is >= 400 and < 500; retry = temporary && claim.Attempts < 5; status = retry ? "pending" : "failed";
                        code = temporary ? "recipient_temporary" : "recipient_rejected";  // only permanent recipient failures suppress future marketing
                    }
                    catch (SmtpCommandException ex) { code = ((int)ex.StatusCode).ToString(); retry = (int)ex.StatusCode is >= 400 and < 500 && claim.Attempts < 5; status = retry ? "pending" : "failed"; }
                    catch (MailResponse ex) { code = ex.Code.ToString(); retry = ex.Code is >= 400 and < 500 && claim.Attempts < 5; status = retry ? "pending" : "failed"; }
                    catch (AuthenticationException) { status = "failed"; code = "535"; }
                    catch (Exception ex) when (ex is BookingException or SslHandshakeException or MailApiError) { status = "failed"; code = "smtp_config"; }
                    catch (Exception) { status = "unknown"; code = "delivery_uncertain"; }
                }
                Unit(tenant, c =>
                {
                    var d = c.S.One<Delivery>("SELECT * FROM core_delivery WHERE id=@i FOR UPDATE", new { i = id });
                    d.Status = status; d.LastCode = code; d.Channel = used; d.Ref = reference;
                    if (status == "accepted") d.SentAt = c.Now;
                    if (retry) d.NextAttempt = c.Now.AddMinutes(Math.Pow(2, d.Attempts));
                    c.S.Update(d, "status", "last_code", "channel", "ref", "sent_at", "next_attempt");
                    if (code == "recipient_rejected" && d.ContactId is Guid contact && c.S.Execute("UPDATE core_contact SET suppressed=true WHERE id=@i AND NOT suppressed", new { i = contact }) > 0)
                        new Privacy(c).Record(contact, "suppressed", "smtp", reference: d.Id.ToString());
                    return 0;
                });
                if (counts.ContainsKey(status)) counts[status]++;
            }
        }
        return counts;
    }

    /// <summary>One delivery taken for sending, with what it needs outside the transaction; null when it must wait or is skipped.</summary>
    static Claimed Claim(Ctx c, Guid id, Dictionary<string, int> counts)
    {
        c.Lock(); var t = c.Tenant;
        var d = c.S.One<Delivery>("SELECT * FROM core_delivery WHERE id=@i FOR UPDATE", new { i = id });
        if (d == null || d.Status != "pending") return null;
        if (d.CampaignId is Guid campaign)
        {
            if (!t.MarketingEnabled || !c.Operational() || c.S.Scalar<string>("SELECT status FROM core_campaign WHERE id=@i", new { i = campaign }) != "queued") return null;
            var contact = d.ContactId is Guid k ? c.S.Get<Contact>(k) : null;
            if (contact == null || contact.Consent != "yes" || contact.Suppressed) { d.Status = "skipped"; d.LastCode = "not_eligible"; c.S.Update(d, "status", "last_code"); counts["skipped"]++; return null; }
        }
        JsonObject config;
        if (d.Channel is "sms" or "whatsapp")
        {
            // the texts wait for the morning and for room in their own daily budget
            if (TextMessages.Later(c) is DateTime wait) { d.NextAttempt = wait; c.S.Update(d, "next_attempt"); return null; }
            if (TextMessages.Spent(c) >= t.TextsDailyLimit) return null;
        }
        if (d.Channel != "email")
            config = d.Channel == "telegram" ? (string.IsNullOrEmpty(t.Telegram) ? null : c.Decrypt(t.Telegram, "telegram")) : d.Channel == "webhook" ? (string.IsNullOrEmpty(t.Webhook) ? null : c.Decrypt(t.Webhook, "webhook")) : TextMessages.Config(c);
        else
        {
            // separate budgets reserve notification capacity even after a campaign
            var recent = "FROM core_delivery WHERE channel='email' AND status IN ('accepted','sending','unknown') AND (campaign_id IS NULL) = @own AND claimed_at>=@a";
            if (c.S.Scalar<long>("SELECT count(*) " + recent, new { own = d.CampaignId == null, a = c.Now.AddDays(-1) }) >= t.SmtpDailyLimit || c.S.Scalar<long>("SELECT count(*) " + recent, new { own = d.CampaignId == null, a = c.Now.AddHours(-1) }) >= t.SmtpHourlyLimit) return null;
            config = Mail.Config(c, marketing: d.CampaignId != null);
            if (config == null) { d.LastCode = "smtp_missing"; d.NextAttempt = c.Now.AddMinutes(10); c.S.Update(d, "last_code", "next_attempt"); return null; }
        }
        d.Status = "sending"; d.ClaimedAt = c.Now; d.Attempts += 1; c.S.Update(d, "status", "claimed_at", "attempts");
        var payload = c.Decrypt(d.Payload, "delivery:" + d.Id);
        // the logo goes with the message, not in every queued email
        var logo = d.Channel == "email" && payload.Str("html").Contains("cid:logo") ? c.S.Scalar<byte[]>("SELECT logo FROM core_tenant WHERE id=@t", new { t = t.Id }) : null;
        return new Claimed(d.Channel, payload, config, logo is { Length: > 0 } ? logo : null, d.Attempts, d.ContactId);
    }

    static readonly HttpClient telegram = new() { Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>notify.send_telegram: POST to the fixed Telegram API host; the bot token never leaves the sealed settings.</summary>
    static void SendTelegram(JsonObject conf, JsonObject payload)
    {
        if (conf == null) throw new TextError("telegram_missing");
        var text = payload.Str("text"); if (text.Length > 4000) text = text[..4000];
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{conf.Str("token")}/sendMessage") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = conf.Str("chat"), ["text"] = text, ["disable_web_page_preview"] = "true" }) };
        try
        {
            using var res = telegram.Send(req); var code = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode) throw new TextError($"telegram_{code}", code == 429 || code >= 500);
            using var reader = new StreamReader(res.Content.ReadAsStream()); JsonNode got = null; try { got = JsonNode.Parse(reader.ReadToEnd()); } catch (System.Text.Json.JsonException) { }
            if (!Json.Truthy(got?["ok"])) throw new TextError("telegram_rejected");
        }
        catch (HttpRequestException) { throw new TextError("telegram_unreachable", true); }  // network down: retry instead of losing it
    }

    // a redirect could lead to a local address: it fails instead; the connection goes to an address checked as public in
    // this very moment, not to a second lookup of the name (TLS still checks the name)
    static readonly HttpClient webhook = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (ctx, stop) =>
        {
            var ip = Network.PublicAddresses(ctx.DnsEndPoint.Host)[0]; var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(ip, ctx.DnsEndPoint.Port, stop); return new NetworkStream(s, true); } catch { s.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>api.send_webhook: the event in JSON, signed sha256= HMAC of "timestamp.body" with the business's secret.</summary>
    static void SendWebhook(JsonObject conf, JsonObject payload)
    {
        if (conf == null) throw new TextError("webhook_missing");
        try { Network.CheckWebhook(conf.Str("url")); } catch (BookingException) { throw new TextError("webhook_address", true); }
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString()); var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sign = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(conf.Str("secret")), Encoding.UTF8.GetBytes(stamp + ".").Concat(body).ToArray())).ToLowerInvariant();
        using var req = new HttpRequestMessage(HttpMethod.Post, conf.Str("url")) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("application/json"); req.Headers.UserAgent.ParseAdd("TD-Booking-Suite");
        req.Headers.Add("X-TDBS-Event", payload.Str("event")); req.Headers.Add("X-TDBS-Timestamp", stamp); req.Headers.Add("X-TDBS-Signature", "sha256=" + sign);
        try
        {
            using var res = webhook.Send(req, HttpCompletionOption.ResponseHeadersRead); var code = (int)res.StatusCode;
            if (code >= 300) throw new TextError($"webhook_{code}", code is 408 or 429 || code >= 500);
        }
        catch (HttpRequestException ex) when (ex.InnerException is BookingException) { throw new TextError("webhook_address", true); }  // now resolving to a local address
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { throw new TextError("webhook_unreachable", true); }
    }

    // ---------- the timed jobs
    /// <summary>client_mails.remind with texts.remind: the reminders and the other timed messages of every business.</summary>
    public int Remind() => Tenants().Sum(t => Unit(t, c => c.Operational() ? ClientMails.Remind(c, (x, b, kind) => TextMessages.Remind(x, b, kind) != null) : 0));

    /// <summary>notify.evening: tomorrow's bookings, once a day from the hour the business chose: to its address and
    /// Telegram, and to each operator his own.</summary>
    public int Evening() => Tenants("summary_hour IS NOT NULL").Sum(id => Unit(id, c =>
    {
        c.Lock(); var t = c.Tenant; var today = c.Cal.ToLocal(c.Now);
        if (today.Hour < t.SummaryHour || t.SummarySent?.Date == today.Date || !c.Operational()) return 0;
        var day = DateOnly.FromDateTime(today).AddDays(1); var start = c.Cal.Local(day, TimeSpan.Zero)!.Value; var bk = new Bookings(c);
        var rows = c.S.Query<Booking>("SELECT * FROM core_booking WHERE start>=@a AND start<@b AND status IN ('pending','confirmed') ORDER BY start", new { a = start, b = start.AddDays(1) }).ToList();
        var title = $"Domani, {day:dd/MM} · {t.Name}";
        string Line(Booking b)
        {
            var parts = new[] { $"{c.Cal.ToLocal(b.Start):HH:mm}", b.Snap.Str("service"), b.Snap.Str("resource"), b.Party > 1 ? $"{b.Party} pers." : "", Notify.Short(bk.Data(b)) is var n && n != "" ? n : "—" };
            return string.Join(" · ", parts.Where(x => x != "")) + (b.Status == "pending" ? " (da confermare)" : "");
        }
        string Text(List<Booking> rs) => title + "\n" + (rs.Count > 0 ? string.Join("\n", rs.Select(Line)) : "Nessuna prenotazione.");
        if (!string.IsNullOrEmpty(t.NotifyEmail)) bk.Queue(new JsonObject { ["to"] = t.NotifyEmail, ["subject"] = title, ["body"] = Text(rows) });
        if (!string.IsNullOrEmpty(t.Telegram)) { var d = bk.Queue(new JsonObject { ["text"] = Text(rows) }); d.Channel = "telegram"; c.S.Update(d, "channel"); }
        foreach (var (email, mine) in Notify.Operators(c, null, Notify.EmailOf))
            if (rows.Where(b => b.ResourceId is Guid r && mine.Contains(r.ToString())).ToList() is { Count: > 0 } own) bk.Queue(new JsonObject { ["to"] = email, ["subject"] = title, ["body"] = Text(own) });
        c.S.Execute("UPDATE core_tenant SET summary_sent=@d::date WHERE id=@t", new { d = today.ToString("yyyy-MM-dd"), t = t.Id });
        return 1;
    }));

    /// <summary>waitlist.run: the businesses with something due or a kept place that expires. Returns the emails queued.</summary>
    public int Waiting() => Tenants("wait_check<=@n", new { n = DateTime.UtcNow }).Sum(id => Unit(id, c =>
    {
        if (!c.Operational() || WpSync.BySite(c)) return 0;  // the site in charge tells the list itself
        c.Lock(); return new Waitlist(c).Step();
    }));

    /// <summary>texts.receipts: what became of the SMS of the last two days: on the delivery.</summary>
    public int Receipts() => Tenants("texts<>''").Sum(id => Unit(id, c =>
    {
        var conf = TextMessages.Config(c); var done = 0;
        if (!c.Operational() || conf.Str("provider") == "") return 0;
        foreach (var d in c.S.Query<Delivery>("SELECT * FROM core_delivery WHERE channel='sms' AND status='accepted' AND receipt='' AND sent_at>=@a AND ref<>'' LIMIT 200", new { a = c.Now.AddDays(-2) }).ToList())
        {
            string state; try { state = TextMessages.Receipt(conf, d.Ref); } catch (Exception ex) when (ex is TextError or TaskCanceledException or IOException) { break; }  // the provider is down: next round
            if (state != "") { d.Receipt = state; c.S.Update(d, "receipt"); done++; }
        }
        return done;
    }));

    /// <summary>refresh_turns: only the missing future turns of the recurring rules; bookings and existing dates untouched.</summary>
    public void Turns()
    {
        foreach (var id in Tenants()) Unit(id, c =>
        {
            c.Lock(); var today = c.Today;
            foreach (var rule in c.S.Query<Shiftrule>("SELECT r.* FROM core_shiftrule r JOIN core_service s ON s.id=r.service_id WHERE r.active AND s.active").ToList())
            {
                var service = c.Row<Service>(rule.ServiceId); DateOnly D(DateTime? v, DateOnly other) => v.HasValue ? DateOnly.FromDateTime(v.Value) : other;
                var first = D(rule.ValidFrom, today) > today ? D(rule.ValidFrom, today) : today; var horizon = today.AddDays(service.HorizonDays); var until = D(rule.ValidUntil, today.AddDays(730)); var last = horizon < until ? horizon : until;
                for (var day = first; day <= last; day = day.AddDays(1))
                {
                    if (Calendar.Weekday(day) != rule.Weekday) continue;
                    var a = c.Cal.Local(day, rule.Start); var b = c.Cal.Local(rule.End < rule.Start ? day.AddDays(1) : day, rule.End, 1);
                    if (a.HasValue && b.HasValue && !c.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_occurrence WHERE rule_id=@r AND start=@a)", new { r = rule.Id, a }))
                        c.S.Insert(new Occurrence { TenantId = c.Tenant.Id, RuleId = rule.Id, ServiceId = rule.ServiceId, Start = a.Value, End = b.Value, Capacity = rule.Capacity });
                }
            }
            return 0;
        });
    }

    /// <summary>privacy.run_retention: the retention of every business once a day; when it last went through, or why not,
    /// stays on the business; a failed one is tried again at the next round. Returns how many went through.</summary>
    public int Retention(Action<string, Exception> log = null)
    {
        var done = 0;
        foreach (var id in Tenants("retention_at IS NULL OR retention_at<=@a", new { a = DateTime.UtcNow.AddDays(-1) }))
        {
            try
            {
                Unit(id, c => { new Privacy(c).ApplyRetention(true); c.S.Execute("UPDATE core_tenant SET retention_at=@n, retention_error='' WHERE id=@t", new { n = c.Now, t = id }); return 0; });
                done++;
            }
            catch (Exception ex)  // one business that fails does not stop the others; the dashboard gets the kind of error only
            {
                log?.Invoke("Conservazione dei dati non riuscita", ex);
                using var g = db.Global(); g.Execute("UPDATE core_tenant SET retention_error=@e WHERE id=@t", new { e = ex.GetType().Name, t = id }); g.Commit();
            }
        }
        if (done > 0) { using var g = db.Global(); g.Execute("DELETE FROM core_ratebucket WHERE ends<now()"); g.Execute("DELETE FROM core_ceremony WHERE expires<now()"); g.Commit(); }
        return done;
    }
}
