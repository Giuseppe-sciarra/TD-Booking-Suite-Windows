using System.Reflection;
using System.Text;
using TD.Core.Data;

namespace TD.Server.Web;

/// <summary>The portal's own public pages (core/pages.py): Privacy always, Prezzi e versioni and Perché sceglierci
/// when the Portale edition has them switched on.</summary>
public static class PublicPages
{
    static List<object> Pairs(string text) => (text ?? "").Split('\n').Where(l => l.Trim() != "").Select(l => { var cut = l.IndexOf('|'); return (object)new object[] { (cut < 0 ? l : l[..cut]).Trim(), cut < 0 ? "" : l[(cut + 1)..].Trim() }; }).ToList();
    static Dictionary<string, object> Flags(Server s, Sitesettings site) => new() { ["pricing_on"] = s.Edition == "cloud" && site.PricingEnabled, ["why_on"] = s.Edition == "cloud" && site.WhyEnabled, ["showcase"] = true };

    static string StandardPolicy(Sitesettings site)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("privacy-standard.txt"); using var reader = new StreamReader(stream!, Encoding.UTF8);
        string First(params string[] v) => v.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
        return reader.ReadToEnd().Replace("{legal_name}", First(site.LegalName, site.OwnerName, site.Name)).Replace("{email}", First(site.PrivacyEmail, site.SupportEmail, "l'indirizzo indicato dall'amministratore del programma"))
            .Replace("{address_part}", site.LegalAddress != "" ? ", " + site.LegalAddress : "").Replace("{vat_part}", site.VatNumber != "" ? ", partita IVA " + site.VatNumber : "").Replace("{location_part}", site.DataLocation != "" ? ", a " + site.DataLocation : "");
    }
    /// <summary>pages.render_text: plain text with "## " headings and "- " lists, nothing else, always escaped.</summary>
    public static string RenderText(string text)
    {
        var html = new StringBuilder(); var items = new List<string>(); var para = new List<string>();
        static string Esc(string v) => Tpl.Escape(v);
        void Close()
        {
            if (para.Count > 0) { html.Append("<p>" + string.Join(" ", para) + "</p>"); para.Clear(); }
            if (items.Count > 0) { html.Append("<ul>" + string.Concat(items.Select(i => "<li>" + i + "</li>")) + "</ul>"); items.Clear(); }
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "") { Close(); continue; }
            if (line.StartsWith("### ")) { Close(); html.Append("<h3>" + Esc(line[4..]) + "</h3>"); }
            else if (line.StartsWith("## ")) { Close(); html.Append("<h2>" + Esc(line[3..]) + "</h2>"); }
            else if (line.StartsWith("- ")) { if (para.Count > 0) Close(); items.Add(Esc(line[2..])); }
            else { if (items.Count > 0) Close(); para.Add(Esc(line)); }
        }
        Close(); return html.ToString();
    }
    static string PlanLink(Server s, Pricingplan plan, Sitesettings site) =>
        plan.CtaUrl != "" ? plan.CtaUrl : plan.Edition == "cloud" && site.TrialEnabled && s.Edition == "cloud" ? "/prova/" : site.SupportEmail != "" ? "mailto:" + site.SupportEmail : site.TrialEnabled ? "/prova/" : "/login/";

    public static void Map(WebApplication app)
    {
        app.MapGet("/privacy/", (Server s, HttpContext h) =>
        {
            var site = PublicHost.SiteRow(s); h.Items["site"] = site; var custom = site.PrivacyPolicy.Trim();
            var more = Flags(s, site); more["policy"] = new Tpl.Safe(RenderText(custom != "" ? custom : StandardPolicy(site))); more["custom"] = custom != ""; more["updated"] = site.PrivacyUpdated;
            return PublicHost.Page(s, h, "privacy.html", null, more);
        });
        // the web sign-in of the Python program is the program's window here: its links land on these words
        foreach (var path in new[] { "/", "/login/" })
            app.MapGet(path, (Server s, HttpContext h) => PublicHost.Page(s, h, "message.html", null, new() { ["title"] = "Si entra dal programma", ["text"] = "Apri TD Booking Suite sul tuo PC ed entra con le tue credenziali." }));

        // identity.invite_view: the personal link of a new user, to choose the password
        app.MapMethods("/invite/{token}/", new[] { "GET", "POST" }, async (Server s, HttpContext h, string token) =>
        {
            using var g = s.Db.Global(); var error = "";
            var obj = g.One<Invitation>("SELECT * FROM core_invitation WHERE token=@t AND NOT used AND expires>@n FOR UPDATE", new { t = TD.Core.Crypto.TokenHash(token), n = DateTime.UtcNow });
            if (obj == null) return PublicHost.Page(s, h, "message.html", null, new() { ["title"] = "Invito non valido", ["text"] = "Il collegamento è scaduto o è già stato utilizzato." }, 400);
            if (h.Request.Method == "POST")
            {
                var form = await h.Request.ReadFormAsync(); if (!PublicHost.CsrfOk(h, form)) return PublicHost.Plain(403, "Verifica di sicurezza non riuscita. Ricarica la pagina e riprova.");
                try
                {
                    s.Identity.Throttle("invite", s.Rete.Client(h), 10); var pw = form["password"].ToString(); TD.Core.Domain.Accounts.ValidatePassword(pw);
                    var u = g.One<User>("SELECT * FROM core_user WHERE id=@id FOR UPDATE", new { id = obj.UserId });
                    if (u is not { IsActive: true }) throw new TD.Core.Domain.BookingException("Account disabilitato.");
                    u.Password = TD.Core.Passwords.Make(pw); u.AuthVersion++; g.Update(u, "password", "auth_version");
                    g.Execute("UPDATE core_invitation SET used=true WHERE token=@t", new { t = obj.Token }); g.Commit();
                    return PublicHost.Page(s, h, "message.html", null, new() { ["title"] = "Accesso attivato", ["text"] = "Apri TD Booking Suite ed entra con le tue credenziali e la nuova password." });
                }
                catch (Exception ex) when (ex is TD.Core.Domain.BookingException or UnauthorizedAccessException or ArgumentException) { error = ex.Message; }
            }
            return PublicHost.Page(s, h, "invite.html", null, new() { ["error"] = error });
        });

        // trial.request_view: a business asks to try the portal (Portale edition, when the agency switched it on)
        app.MapMethods("/prova/", new[] { "GET", "POST" }, async (Server s, HttpContext h) =>
        {
            var site = PublicHost.SiteRow(s); h.Items["site"] = site; if (s.Edition != "cloud" || !site.TrialEnabled) return Results.StatusCode(404);
            var error = ""; var sent = false; IFormCollection form = null;
            if (h.Request.Method == "POST")
            {
                form = await h.Request.ReadFormAsync(); if (!PublicHost.CsrfOk(h, form)) return PublicHost.Plain(403, "Verifica di sicurezza non riuscita. Ricarica la pagina e riprova.");
                if (form["website"].ToString() != "") sent = true; // filled only by bots: answer as if it went through
                else
                {
                    try { s.Identity.Throttle("trial-ip", s.Rete.Client(h), 5, 3600); } catch (UnauthorizedAccessException ex) { return PublicHost.Plain(403, ex.Message); }
                    try
                    {
                        var data = new System.Text.Json.Nodes.JsonObject();
                        foreach (var (key, label, limit, required) in new[] { ("first_name", "Nome", 60, true), ("last_name", "Cognome", 60, true), ("email", "Email", 160, true), ("phone", "Telefono", 40, false), ("business", "Nome dell'attività", 120, false), ("message", "Messaggio", 600, false) })
                        {
                            var value = form[key].ToString().Trim();
                            if (required && value == "") throw new TD.Core.Domain.BookingException($"Indica {label.ToLowerInvariant()}.");
                            if (value.Length > limit) throw new TD.Core.Domain.BookingException($"{label}: massimo {limit} caratteri.");
                            data[key] = value;
                        }
                        TD.Core.Domain.Accounts.ValidateEmail(data["email"].ToString());
                        var preset = form["preset"].ToString(); if (preset != "" && !TD.Core.Domain.Presets.All.ContainsKey(preset)) throw new TD.Core.Domain.BookingException("Tipo di attività non valido.");
                        if (form["privacy"] != "on") throw new TD.Core.Domain.BookingException("Serve il consenso per essere ricontattato.");
                        using var g = s.Db.Global();
                        if (g.Scalar<long>("SELECT count(*) FROM core_trialrequest WHERE status='new'") >= 500) throw new TD.Core.Domain.BookingException("Al momento non riusciamo a ricevere altre richieste. Riprova tra qualche giorno.");
                        var r = new Trialrequest { Preset = preset }; data["privacy_at"] = TD.Core.Domain.Times.PyIso(DateTime.UtcNow); r.Pii = s.Crypto.Protect(data, "trial:" + r.Id); g.Insert(r); g.Commit();
                        sent = true;
                    }
                    catch (Exception ex) when (ex is TD.Core.Domain.BookingException or ArgumentException) { error = ex.Message; }
                }
            }
            var values = new Dictionary<string, object>(); if (error != "") foreach (var k in form.Keys) values[k] = form[k].ToString();
            var types = new List<object> { new object[] { "", "Altro, appuntamenti generici" } }; types.AddRange(TD.Core.Domain.Presets.All.Select(kv => (object)new object[] { kv.Key, kv.Value.Label }));
            return PublicHost.Page(s, h, "login.html", null, new() { ["pricing_on"] = site.PricingEnabled, ["trial_open"] = true, ["trial_sent"] = sent, ["trial_error"] = error, ["business_types"] = types, ["form_values"] = values });
        });

        app.MapGet("/prezzi/", (Server s, HttpContext h) =>
        {
            var site = PublicHost.SiteRow(s); h.Items["site"] = site; if (s.Edition != "cloud" || !site.PricingEnabled) return Results.StatusCode(404);
            List<Pricingplan> rows; using (var g = s.Db.Global()) rows = g.Query<Pricingplan>("SELECT * FROM core_pricingplan WHERE active ORDER BY edition, \"order\", name").ToList();
            var plans = rows.Select(p => (object)new Dictionary<string, object>
            {
                ["pk"] = p.Id, ["highlighted"] = p.Highlighted, ["badge"] = p.Badge, ["edition"] = p.Edition, ["get_edition_display"] = p.Edition == "cloud" ? "Cloud, in abbonamento" : "Private, sul server del cliente", ["name"] = p.Name,
                ["description"] = p.Description, ["price"] = p.Price, ["period"] = p.Period, ["price_note"] = p.PriceNote, ["cta_label"] = p.CtaLabel, ["link"] = PlanLink(s, p, site),
                ["free_label"] = p.FreeMonths == 0 ? "" : p.FreeMonths == 1 ? "Il primo mese è gratis" : $"I primi {p.FreeMonths} mesi sono gratis",
                ["feature_list"] = p.Features.Split('\n').Select(x => x.Trim()).Where(x => x != "").Cast<object>().ToList(),
            }).ToList();
            var more = Flags(s, site); more["plans"] = plans; more["columns"] = (long)Math.Min(plans.Count, 4); more["highlights"] = Pairs(site.PricingHighlights); more["faq"] = Pairs(site.PricingFaq); more["trial_available"] = site.TrialEnabled;
            return PublicHost.Page(s, h, "pricing.html", null, more);
        });
        app.MapGet("/perche-sceglierci/", (Server s, HttpContext h) =>
        {
            var site = PublicHost.SiteRow(s); h.Items["site"] = site; if (s.Edition != "cloud" || !site.WhyEnabled) return Results.StatusCode(404);
            var columns = site.WhyColumns.Split('|').Select(c => c.Trim()).Where(c => c != "").ToList(); var rows = new List<object>();
            foreach (var line in site.WhyRows.Split('\n'))
            {
                var cells = line.Split('|').Select(c => c.Trim()).ToList();
                if (cells.Count >= 2 && cells[0] != "") rows.Add(new object[] { cells[0], cells.Skip(1).Concat(Enumerable.Repeat("", columns.Count)).Take(columns.Count).Cast<object>().ToList() });
            }
            var more = Flags(s, site); more["points"] = Pairs(site.WhyPoints); more["columns"] = columns.Cast<object>().ToList(); more["rows"] = rows; more["trial_available"] = site.TrialEnabled;
            return PublicHost.Page(s, h, "why.html", null, more);
        });
    }
}
