using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>api.settings_view: API e webhook: the read-only key and the address called at every booking created, moved
/// or cancelled. The administrator of the program's; the owner's too when the business has the API on.</summary>
public static class ApiWebhook
{
    static void Allowed(Call c) { if (!(c.User.IsStaff || c.Tenant.ApiEnabled && c.Can("users.manage"))) throw new UnauthorizedAccessException("Non hai i permessi per questa operazione."); }
    static object View(Call c, string key = "", string text = "")
    {
        var t = c.Tenant; var conf = string.IsNullOrEmpty(t.Webhook) ? new JsonObject() : c.C.Decrypt(t.Webhook, "webhook");
        return new
        {
            chiave = key, testo = text, haChiave = !string.IsNullOrEmpty(t.ApiKey), url = conf.Str("url"), segreto = conf.Str("secret"), nome = t.Name,
            falliti = c.C.S.Scalar<long>("SELECT count(*) FROM core_delivery WHERE channel='webhook' AND status='failed'"), @base = c.PublicOrigin.TrimEnd('/') + $"/book/{t.Slug}/api/v1/",
        };
    }

    [Rpc("api", Fresh = true)]
    public static object Page(Call c) { Allowed(c); return View(c); }

    [Rpc("api.azione", Fresh = true)]
    public static object Action(Call c)
    {
        Allowed(c); var x = c.C; var t = c.Tenant; var action = c.Str("azione");
        x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR UPDATE", new { t = t.Id });
        if (action == "key")
        {
            var key = "tdbs_" + Bookings.TokenUrlSafe(32); t.ApiKey = Crypto.TokenHash(key);
            x.S.Execute("UPDATE core_tenant SET api_key=@k WHERE id=@t", new { k = t.ApiKey, t = t.Id }); x.Audit("api.key", t.Id);
            return View(c, key);
        }
        if (action == "revoke")
        {
            t.ApiKey = ""; x.S.Execute("UPDATE core_tenant SET api_key='' WHERE id=@t", new { t = t.Id }); x.Audit("api.revoked", t.Id);
            return View(c, "", "Chiave revocata: chi la usava non entra più.");
        }
        if (action == "webhook")
        {
            var url = c.Str("url").Trim();
            if (url != "")
            {
                Network.CheckWebhook(url);
                var old = string.IsNullOrEmpty(t.Webhook) ? new JsonObject() : x.Decrypt(t.Webhook, "webhook");
                t.Webhook = x.Encrypt(new JsonObject { ["url"] = url.Length > 500 ? url[..500] : url, ["secret"] = old.Str("secret") != "" ? old.Str("secret") : Bookings.TokenUrlSafe(32) }, "webhook");
            }
            else t.Webhook = "";
            x.S.Execute("UPDATE core_tenant SET webhook=@w WHERE id=@t", new { w = t.Webhook, t = t.Id }); x.Audit("api.webhook", t.Id, new { on = url != "" });
            return View(c, "", url != "" ? "Webhook salvato." : "Webhook tolto.");
        }
        return View(c);
    }
}
