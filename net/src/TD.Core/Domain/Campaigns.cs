using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>mail.campaign_contacts and enqueue_campaign: who has the confirmed consent and, among them, a campaign's
/// own people; a campaign queued, one email for each with the link to unsubscribe.</summary>
public sealed class Campaigns(Ctx c)
{
    /// <summary>campaign_contacts(): consent yes and not suppressed; filters: a tag, VIP, last visit after or before a day,
    /// at least N visits (bookings that took place).</summary>
    public List<Contact> Contacts(JsonObject filters = null)
    {
        var f = filters ?? new JsonObject(); var sql = "SELECT k.* FROM core_contact k WHERE k.consent='yes' AND NOT k.suppressed"; var p = new Dapper.DynamicParameters();
        if (Json.Truthy(f["tag"])) { sql += " AND k.tag_index LIKE @tag"; p.Add("tag", new Clients(c).TagLike(f.Str("tag"))); }
        if (Json.Truthy(f["vip"])) sql += " AND k.vip";
        const string done = "FROM core_booking b WHERE b.contact_id=k.id AND b.status = ANY(@acc) AND b.start<=@now";
        if (Json.Truthy(f["after"]) || Json.Truthy(f["before"]) || Json.Truthy(f["visits"])) { p.Add("acc", Bookings.ACCEPTED); p.Add("now", c.Now); p.Add("tz", c.Tenant.Timezone); }
        if (Json.Truthy(f["after"])) { sql += $" AND (SELECT max(b.start) {done}) AT TIME ZONE @tz >= @after::date"; p.Add("after", f.Str("after")); }
        if (Json.Truthy(f["before"])) { sql += $" AND COALESCE((SELECT max(b.start) {done}) AT TIME ZONE @tz < @before::date, true)"; p.Add("before", f.Str("before")); }
        if (Json.Truthy(f["visits"])) { sql += $" AND (SELECT count(*) {done}) >= @visits"; p.Add("visits", Till.Int(f["visits"])); }
        return c.S.Query<Contact>(sql + " ORDER BY k.created", p).ToList();
    }

    public void Enqueue(Campaign campaign)
    {
        c.Lock(); var t = c.Tenant; campaign = c.S.One<Campaign>("SELECT * FROM core_campaign WHERE id=@i FOR UPDATE", new { i = campaign.Id });
        if (!t.MarketingEnabled || string.IsNullOrEmpty(t.Smtp) || !c.Operational()) throw new BookingException("Invio marketing non abilitato o SMTP non configurato.");
        if (campaign.Status != "draft") throw new BookingException("Campagna già accodata.");
        var recipients = Contacts(Json.Obj(campaign.Filters));
        if (recipients.Count > 5000) throw new BookingException("Massimo 5.000 destinatari per campagna; suddividi gli invii.");
        if (recipients.Count == 0) throw new BookingException("Nessun destinatario con consenso confermato.");
        var bk = new Bookings(c); var cl = new Clients(c);
        foreach (var contact in recipients)
        {
            var link = $"{c.PublicOrigin}/action/{t.Slug}/{bk.ActionToken("unsubscribe", contact.Id, days: 3650)}/";
            var d = bk.Queue(new JsonObject { ["to"] = cl.Data(contact).Str("email"), ["subject"] = campaign.Subject, ["body"] = campaign.Body + $"\n\n{t.Name}\nDisiscriviti: {link}", ["unsubscribe"] = link + "one-click/" }, contact.Id, campaign.Id);
            if (campaign.Scheduled > c.Now) { d.NextAttempt = campaign.Scheduled.Value; c.S.Update(d, "next_attempt"); }
        }
        campaign.Status = "queued"; c.S.Update(campaign, "status"); c.Audit("campaign.queued", campaign.Id);
    }
}
