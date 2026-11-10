using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/wpsync.py: the WordPress site of the PC. This part: the archive's outbox (touch, changed) and whether
/// the site is in charge of the emails (by_site). The sync itself runs in the worker (TD.Server/Work).</summary>
public static partial class WpSync
{
    public static readonly Dictionary<string, string> KINDS = new()
    { ["contact"] = "core_contact", ["booking"] = "core_booking", ["treatment"] = "core_treatment", ["consent"] = "core_consentevent", ["sale"] = "core_sale", ["wait"] = "core_waitentry" };
    static readonly Dictionary<string, string> BYTABLE = KINDS.ToDictionary(x => x.Value, x => x.Key);
    /// <summary>QUIET: the writes of a pull from the site do not go back to it.</summary>
    public static readonly AsyncLocal<bool> Quiet = new();
    [ThreadStatic] static bool busy;

    /// <summary>Every save and delete of an archived row reaches touch, as models._archived does.</summary>
    public static void Install() => Scope.Changed = OnChanged;

    static void OnChanged(Scope s, string table, object id)
    {
        if (busy || s.TenantId == null || !BYTABLE.TryGetValue(table, out var kind)) return;
        busy = true;
        try
        {
            Touch(s, s.TenantId.Value, kind, new[] { id?.ToString() });
            if (kind == "contact" && !Quiet.Value && s.Scalar<string>("SELECT email_index FROM core_contact WHERE id=@id", new { id }) is string idx && idx.StartsWith(Clients.ERASED))
                Touch(s, s.TenantId.Value, "consent", s.Query<Guid>("SELECT id FROM core_consentevent WHERE contact_id=@id", new { id }).Select(x => x.ToString()));
        }
        finally { busy = false; }
    }

    public static void Touch(Ctx c, string kind, IEnumerable<Guid> ids) => Touch(c.S, c.Tenant.Id, kind, ids.Select(x => x.ToString()));

    /// <summary>Records of a kind changed or gone: their version moved to now, and in the outbox with the archive on.</summary>
    public static DateTime? Touch(Scope s, Guid tenant, string kind, IEnumerable<string> refs0, bool stamp = true)
    {
        var refs = refs0.Where(r => !string.IsNullOrEmpty(r)).ToList();
        if (refs.Count == 0 || Quiet.Value) return null;
        DateTime? when = null;
        var wasBusy = busy; busy = true;
        try
        {
            if (stamp && kind != "consent")
            {
                when = DateTime.UtcNow;
                for (int i = 0; i < refs.Count; i += 500)
                    s.Execute($"UPDATE {KINDS[kind]} SET updated=@w WHERE tenant_id=@t AND id::text = ANY(@r)", new { w = when, t = tenant, r = refs.Skip(i).Take(500).ToArray() });
            }
            var n = refs.Count; var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            var top = s.Scalar<long?>("UPDATE core_tenant SET wp_seq=GREATEST(wp_seq+@n, @now+@n) WHERE id=@t AND wp_archive RETURNING wp_seq", new { n = (long)n, now, t = tenant });
            if (top == null) return when;
            for (int i = 0; i < n; i += 500)
            {
                var part = refs.Skip(i).Take(500).ToList();
                s.Execute("DELETE FROM core_siteoutbox WHERE kind=@k AND ref = ANY(@r)", new { k = kind, r = part.ToArray() });
                for (int j = 0; j < part.Count; j++)
                    s.Execute("INSERT INTO core_siteoutbox (id, tenant_id, kind, ref, seq) VALUES (@id, @t, @k, @r, @q)", new { id = Guid.NewGuid(), t = tenant, k = kind, r = part[j], q = top.Value - n + 1 + i + j });
            }
            return when;
        }
        finally { busy = wasBusy; }
    }

    /// <summary>wpconnect.valid_origin: https://host[:port] with nothing else, lower case; empty when it is not.</summary>
    public static string ValidOrigin(string value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^https://([A-Za-z0-9.-]+(:[0-9]{1,5})?)/?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? "https://" + m.Groups[1].Value.ToLowerInvariant() : "";
    }
    /// <summary>panel(): what the card shows: never the secret, only whether there is one.</summary>
    public static JsonObject Panel(Ctx c)
    {
        var conf = Conf(c); var s = Json.Obj(c.Tenant.WpState); var a = s["archive"] as JsonObject ?? new JsonObject();
        return new JsonObject
        {
            ["url"] = conf.Str("url"), ["days"] = conf["days"] != null ? Till.Int(conf["days"]) : 30, ["on"] = Json.Truthy(conf["on"]), ["secret"] = Json.Truthy(conf["secret"]),
            ["at"] = s.Str("at"), ["new"] = Till.Int(s["new"]), ["error"] = s.Str("error"), ["archive"] = c.Tenant.WpArchive, ["mails"] = Json.Truthy(conf["mails"]),
            ["queue"] = c.S.Scalar<long>("SELECT count(*) FROM core_siteoutbox"), ["archive_at"] = a.Str("at"), ["records"] = Till.Int(a["records"]),
            ["size"] = Math.Round((a["size"] != null ? double.Parse(a["size"].ToString(), System.Globalization.CultureInfo.InvariantCulture) : 0) / 1048576, 1, MidpointRounding.ToEven), ["archive_error"] = a.Str("error"),
        };
    }
    public static JsonObject Conf(Ctx c) => string.IsNullOrEmpty(c.Tenant.WpSync) ? new JsonObject() : c.Decrypt(c.Tenant.WpSync, "wp-sync");
    public static void Store(Ctx c, JsonObject conf) { c.Tenant.WpSync = c.Encrypt(conf, "wp-sync"); c.S.Update(c.Tenant, "wp_sync"); }

    /// <summary>The site manages the bookings' emails: the sync, the archive and its switch on. Then the program sends none.</summary>
    public static bool BySite(Ctx c)
    {
        if (!c.Tenant.WpArchive || string.IsNullOrEmpty(c.Tenant.WpSync)) return false;
        var conf = Conf(c);
        return Json.Truthy(conf["on"]) && Json.Truthy(conf["secret"]) && Json.Truthy(conf["mails"]);
    }
}
