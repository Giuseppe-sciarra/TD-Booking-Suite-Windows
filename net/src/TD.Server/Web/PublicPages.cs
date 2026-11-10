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
        app.MapGet("/prezzi/", (Server s, HttpContext h) =>
        {
            var site = PublicHost.SiteRow(s); h.Items["site"] = site; if (s.Edition != "cloud" || !site.PricingEnabled) return Results.StatusCode(404);
            List<Pricingplan> rows; using (var g = s.Db.Global()) rows = g.Query<Pricingplan>("SELECT * FROM core_pricingplan WHERE active ORDER BY \"order\", id").ToList();
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
