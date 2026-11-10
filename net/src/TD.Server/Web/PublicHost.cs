using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;
using TD.Server.Rpc;

namespace TD.Server.Web;

/// <summary>What every public page shares (core/middleware.py and context.py for the clients' pages): the business from
/// the address, the page's language, the same context the templates expect, the headers, the program's own files.</summary>
public static class PublicHost
{
    static readonly Dictionary<string, string> URLS = new()
    {
        ["site_favicon"] = "/site/favicon.png", ["site_css"] = "/site/style.css", ["site_logo"] = "/site/logo.png", ["site_logo_dark"] = "/site/logo-dark.png", ["brand_css"] = "/brand/{0}/style.css", ["brand_app_css"] = "/brand/{0}/app.css",
        ["brand_logo"] = "/brand/{0}/logo.png", ["public_booking"] = "/book/{0}/", ["privacy"] = "/privacy/", ["pricing"] = "/prezzi/", ["why"] = "/perche-sceglierci/", ["login"] = "/login/", ["trial"] = "/prova/", ["home"] = "/",
        ["action"] = "/action/{0}/{1}/", ["oneclick"] = "/action/{0}/{1}/one-click/", ["move_public"] = "/action/{0}/{1}/sposta/", ["move_public_slots"] = "/action/{0}/{1}/sposta/orari/", ["checkin"] = "/action/{0}/{1}/check-in/",
        ["booking_ics"] = "/action/{0}/{1}/calendario.ics", ["invite"] = "/invite/{0}/",
    };
    static string Url(string name, object[] args) => URLS.TryGetValue(name, out var pattern) ? string.Format(pattern, args.Select(a => (object)Uri.EscapeDataString(a?.ToString() ?? "")).ToArray()) : "#";

    /// <summary>middleware.public_lang: ?lang= (links, the WordPress frame), then the browser's; Italian otherwise.</summary>
    public static string Lang(HttpContext h)
    {
        var lang = h.Request.Query["lang"].ToString(); if (lang is "it" or "en") return lang;
        foreach (var part in h.Request.Headers.AcceptLanguage.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        { var code = part.Split(';')[0].Trim().ToLowerInvariant(); if (code.StartsWith("it")) return "it"; if (code.StartsWith("en")) return "en"; }
        return "it";
    }

    /// <summary>The double-submit token of the forms: the cookie and the hidden field must say the same.</summary>
    public static string Csrf(HttpContext h)
    {
        if (h.Items["csrf"] is string made) return made;
        var token = h.Request.Cookies["csrftoken"];
        if (string.IsNullOrEmpty(token) || token.Length != 43) { token = Bookings.TokenUrlSafe(32); h.Response.Cookies.Append("csrftoken", token, new CookieOptions { Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), Path = "/" }); }
        h.Items["csrf"] = token; return token;
    }
    public static bool CsrfOk(HttpContext h, IFormCollection form)
    {
        var cookie = h.Request.Cookies["csrftoken"] ?? ""; var sent = form["csrfmiddlewaretoken"].ToString();
        return cookie.Length == 43 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cookie), Encoding.ASCII.GetBytes(sent.PadRight(43)[..43])) && sent.Length == 43;
    }

    public static Sitesettings SiteRow(Server s) { using var g = s.Db.Global(); return g.One<Sitesettings>("SELECT * FROM core_sitesettings ORDER BY id LIMIT 1") ?? new Sitesettings(); }
    /// <summary>The site as the templates read it: every column by its Python name, plus the model's own properties.</summary>
    public static Dictionary<string, object> SiteContext(Sitesettings site)
    {
        bool logo = site.Logo is { Length: > 0 }, dark = site.LogoDark is { Length: > 0 };
        var ctx = typeof(Sitesettings).GetProperties().Where(p => p.PropertyType != typeof(byte[]) && p.Name != "Smtp").ToDictionary(p => Scope.Snake(p.Name), p => p.GetValue(site));
        ctx["logo"] = logo; ctx["logo_dark"] = dark; ctx["favicon"] = site.Favicon is { Length: > 0 };
        ctx["version"] = new DateTimeOffset(DateTime.SpecifyKind(site.Updated, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString();
        ctx["light_wide"] = (logo ? site.LogoRatio : dark ? site.LogoDarkRatio : 1) > 1.4; ctx["dark_wide"] = (dark ? site.LogoDarkRatio : logo ? site.LogoRatio : 1) > 1.4;
        return ctx;
    }
    public static Dictionary<string, object> TenantContext(Tenant t) => new()
    {
        ["name"] = t.Name, ["slug"] = t.Slug, ["logo"] = t.Logo is { Length: > 0 }, ["logo_wide"] = t.Logo is { Length: > 0 } && t.LogoRatio > 1.4, ["brand_color"] = t.BrandColor, ["tagline"] = t.Tagline ?? "", ["timezone"] = t.Timezone, ["website"] = t.Website ?? "", ["pk"] = t.Id.ToString(),
    };

    /// <summary>context.presentation, for a page nobody is signed in to: the same names the templates read.</summary>
    public static Dictionary<string, object> Context(Server s, HttpContext h, Tenant tenant, Dictionary<string, object> more = null)
    {
        var lang = Lang(h); var query = h.Request.Query;
        string Switch(string code) => "?" + string.Join("&", query.Where(q => q.Key != "lang").SelectMany(q => q.Value.Select(v => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(v ?? "")}".Replace("%20", "+"))).Append("lang=" + code));
        var ctx = new Dictionary<string, object>
        {
            ["site"] = SiteContext(h.Items["site"] as Sitesettings ?? SiteRow(s)), ["edition"] = s.Edition, ["edition_label"] = s.Edition == "private" ? "Windows" : "Windows Portale", ["tenant"] = tenant == null ? null : TenantContext(tenant),
            ["user"] = new Dictionary<string, object> { ["is_authenticated"] = false, ["is_staff"] = false }, ["mfa_ok"] = false, ["can"] = new List<object>(), ["app_version"] = Server.Version, ["source_url"] = s.Config.Env("TDBS_SOURCE_URL"),
            ["agency"] = new Dictionary<string, object> { ["area"] = "Configurazione del programma", ["admin"] = "Amministratore del programma", ["who"] = "amministratore del programma", ["access"] = "Accesso dell'amministratore" },
            ["public_booking"] = s.Online, ["usernames"] = true, ["mfa_enabled"] = !s.Standalone, ["extra_css"] = new List<object> { "tdwin/tdwin.css" }, ["extra_notices"] = new List<object>(),
            // request|other_languages: the other languages, each with this page's address and its ?lang=
            ["request"] = new[] { ("it", "Italiano"), ("en", "English") }.Where(l => l.Item1 != lang).Select(l => (object)new object[] { l.Item1, l.Item2, Switch(l.Item1) }).ToList(),
        };
        if (more != null) foreach (var (k, v) in more) ctx[k] = v;
        return ctx;
    }

    /// <summary>The page, with the headers of the Python program: what may load, and who may frame the booking page.</summary>
    public static IResult Page(Server s, HttpContext h, string template, Tenant tenant, Dictionary<string, object> more = null, int status = 200)
    {
        var env = new Tpl.Env { Lang = Lang(h), Csrf = Csrf(h), Url = Url, Version = Server.Version };
        var html = Tpl.Render(template, Context(s, h, tenant, more), env);
        Headers(h, tenant);
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }
    public static void Headers(HttpContext h, Tenant tenant)
    {
        var book = h.Request.Path.StartsWithSegments("/book"); var ancestors = "'self'";
        if (book && tenant != null) ancestors += " " + string.Join(" ", (JsonNode.Parse(string.IsNullOrEmpty(tenant.EmbedOrigins) ? "[]" : tenant.EmbedOrigins) as JsonArray ?? new JsonArray()).Select(o => o.ToString()));
        else h.Response.Headers["X-Frame-Options"] = "DENY";
        h.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors " + (book ? ancestors : "'none'");
        h.Response.Headers["X-Content-Type-Options"] = "nosniff"; h.Response.Headers["Referrer-Policy"] = "same-origin";
    }
    public static IResult Message(Server s, HttpContext h, Tenant tenant, string title, string text, int status = 200) =>
        Page(s, h, "message.html", tenant, new() { ["title"] = Tr.T(Lang(h), title), ["text"] = Tr.T(Lang(h), text), ["public"] = true }, status);
    public static IResult Plain(int status, string text) => Results.Text(text, "text/plain; charset=utf-8", Encoding.UTF8, status);

    /// <summary>The business of /book/slug/ and /action/slug/: unknown, or its online bookings off, is said in plain words.</summary>
    public static (Tenant Tenant, IResult Refused) Business(Server s, string slug, bool booking)
    {
        Tenant t; using (var g = s.Db.Global()) t = g.One<Tenant>("SELECT * FROM core_tenant WHERE slug=@slug", new { slug });
        if (t == null) return (null, Plain(404, "Attività non disponibile."));
        if (booking)
        {
            var open = !t.Maintenance && (s.Edition == "private" || t.Status == "active" || t.Status == "trial" && t.TrialUntil.HasValue && DateOnly.FromDateTime(t.TrialUntil.Value) >= DateOnly.FromDateTime(DateTime.Now));
            if (!open) return (null, Plain(t.Maintenance ? 503 : 402, "Le prenotazioni online non sono disponibili."));
        }
        return (t, null);
    }
    public static Ctx Ctx(Server s, HttpContext h, Tenant t) => new(s.Db, s.Crypto, t.Id) { PublicOrigin = s.PublicOrigin, Out = Outbox.Instance, Edition = s.Edition, Language = Lang(h) };

    // ---------- the program's own files and colours
    static readonly Dictionary<string, string> TYPES = new() { [".css"] = "text/css; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".svg"] = "image/svg+xml", [".woff2"] = "font/woff2", [".png"] = "image/png", [".txt"] = "text/plain; charset=utf-8", [".ico"] = "image/x-icon" };
    static string Mix(string color, string other, double amount)
    {
        int[] a = { Convert.ToInt32(color[1..3], 16), Convert.ToInt32(color[3..5], 16), Convert.ToInt32(color[5..7], 16) }, b = { Convert.ToInt32(other[1..3], 16), Convert.ToInt32(other[3..5], 16), Convert.ToInt32(other[5..7], 16) };
        return "#" + string.Concat(a.Zip(b, (x, y) => ((int)Math.Round(x + (y - x) * amount, MidpointRounding.ToEven)).ToString("x2")));
    }
    /// <summary>site.accent_css: the accent of the portal pages, and a business's own colour inside its pages.</summary>
    static string AccentCss(string color)
    {
        string light = Texts.ContrastColor(color), dark = Texts.ContrastColor(color, true), pastel = Mix(color, "#ffffff", .55), soft = Mix(color, "#ffffff", .88), softDark = Mix(color, "#16111d", .8);
        return $":root,:root[data-theme=light]{{--brand:{light};--violet:{color};--pastel:{pastel};--soft:{soft}}}:root[data-theme=dark]{{--brand:{dark};--violet:{color};--pastel:{pastel};--soft:{softDark}}}"
            + $"@media(prefers-color-scheme:dark){{:root:not([data-theme=light]){{--brand:{dark};--violet:{color};--pastel:{pastel};--soft:{softDark}}}}}";
    }
    static IResult Image(HttpContext h, byte[] data, string type, string cache)
    {
        if (data is not { Length: > 0 }) return Results.StatusCode(404);
        h.Response.Headers["X-Content-Type-Options"] = "nosniff"; if (cache != null) h.Response.Headers.CacheControl = cache;
        return Results.Bytes(data, type);
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/static/{**path}", (HttpContext h, string path) =>
        {
            if (path.Contains("..")) return Results.StatusCode(404);
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("static/" + path); if (stream == null) return Results.StatusCode(404);
            using var ms = new MemoryStream(); stream.CopyTo(ms);
            h.Response.Headers.CacheControl = h.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "public, max-age=3600"; h.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Bytes(ms.ToArray(), TYPES.GetValueOrDefault(Path.GetExtension(path).ToLowerInvariant(), "application/octet-stream"));
        });
        app.MapGet("/site/logo.png", (Server s, HttpContext h) => Image(h, SiteRow(s).Logo, "image/png", "public, max-age=86400"));
        app.MapGet("/site/logo-dark.png", (Server s, HttpContext h) => { var site = SiteRow(s); return Image(h, site.LogoDark is { Length: > 0 } ? site.LogoDark : site.Logo, "image/png", "public, max-age=86400"); });
        app.MapGet("/site/favicon.png", (Server s, HttpContext h) => { var site = SiteRow(s); return site.Favicon is { Length: > 0 } ? Image(h, site.Favicon, "image/png", "public, max-age=86400") : Results.Redirect("/static/favicon.svg"); });
        app.MapGet("/site/style.css", (Server s, HttpContext h) => { h.Response.Headers.CacheControl = "public, max-age=86400"; return Results.Text(AccentCss(SiteRow(s).BrandColor), "text/css"); });
        app.MapGet("/brand/{slug}/style.css", (Server s, string slug) =>
        {
            var (t, no) = Business(s, slug, false); if (t == null) return Results.StatusCode(404);
            string light = Texts.ContrastColor(t.BrandColor), dark = Texts.ContrastColor(t.BrandColor, true);
            return Results.Text($":root,:root[data-theme=light]{{--brand:{light};--brand-ink:#fff}}:root[data-theme=dark]{{--brand:{dark};--brand-ink:#241a30}}@media(prefers-color-scheme:dark){{:root[data-theme=auto]{{--brand:{dark};--brand-ink:#241a30}}}}", "text/css");
        });
        app.MapGet("/brand/{slug}/app.css", (Server s, HttpContext h, string slug) => { var (t, _) = Business(s, slug, false); if (t == null) return Results.StatusCode(404); h.Response.Headers.CacheControl = "public, max-age=86400"; return Results.Text(AccentCss(t.BrandColor), "text/css"); });
        app.MapGet("/brand/{slug}/logo.png", (Server s, HttpContext h, string slug) => { var (t, _) = Business(s, slug, false); return t == null ? Results.StatusCode(404) : Image(h, t.Logo, "image/png", null); });
        // the photo of an operator: public like the booking page that shows it; the address changes with the photo
        app.MapGet("/brand/{slug}/foto/{file}", (Server s, HttpContext h, string slug, string file) =>
        {
            var (t, _) = Business(s, slug, false); if (t == null || !file.EndsWith(".jpg") || !Guid.TryParse(file[..^4], out var id)) return Results.StatusCode(404);
            using var scope = s.Db.Tenant(t.Id); var photo = scope.One<Resourcephoto>("SELECT * FROM core_resourcephoto WHERE resource_id=@id", new { id });
            return Image(h, photo?.Data, "image/jpeg", "public, max-age=31536000, immutable");
        });
    }
}
