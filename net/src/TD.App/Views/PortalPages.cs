using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>trial_requests.html: who asked to try the service from the sign-in page. Attiva opens Nuovo cliente filled in.</summary>
public sealed class TrialRequestsPage : Page
{
    string show = "new";
    public override async Task Load() => Draw(await Api.Current.Call("richieste", new { stato = show }));
    async Task Act(string id, string action) { var r = await Api.Current.Call("richieste.azione", new { id, azione = action }); Main.Flash(r.S("testo")); await Main.Refresh(null); await Load(); }

    void Draw(JsonNode d)
    {
        show = d.S("stato");
        var page = Col(F.Heading(null, "Richieste di prova", "Chi ha chiesto di provare il servizio dalla pagina di accesso. Con Attiva si apre Nuovo cliente già compilato, tipo di attività compreso"));
        if (!d.Bo("attivo")) page.Children.Add(F.Notice(Row(T("Il pulsante \"Prova il servizio\" è spento: lo riaccendi in ", size: 13.5), F.Link("Impostazioni generali", () => Main.Show("generali"), 13.5)), "warning"));
        else if (d.S("destinatario") == "") page.Children.Add(F.Notice(Row(T("Le richieste arrivano qui ma non per email: indica l'indirizzo in ", size: 13.5), F.Link("Impostazioni generali › Richieste di prova", () => Main.Show("generali"), 13.5)), "warning"));
        else { var to = Prog.Muted("Ogni nuova richiesta arriva anche a "); to.Inlines.Add(new Run(d.S("destinatario")) { FontWeight = FontWeights.Bold }); page.Children.Add(to.M(0, 0, 0, 10)); }
        var counts = d["conteggi"]; var tabs = Row();
        foreach (var (key, label, n) in new[] { ("new", "Da gestire", counts.S("nuove")), ("done", "Attivate", counts.S("attivate")), ("archived", "Archiviate", counts.S("archiviate")) })
        {
            var tab = new RadioButton { Content = $"{label}  {n}", GroupName = "stato-richieste", IsChecked = show == key, Style = Style("Segment"), Margin = new Thickness(0, 0, 6, 0) };
            tab.Checked += (_, _) => { show = key; _ = Guard(Load); }; tabs.Children.Add(tab);
        }
        page.Children.Add(tabs.M(0, 0, 0, 14));
        var rows = d.A("righe").ToList();
        if (rows.Count == 0) page.Children.Add(F.Card(T(show == "new" ? "Nessuna richiesta da gestire" : "Niente qui", "H2"), Prog.Text("Le richieste arrivano dal pulsante \"Non sei ancora cliente? Prova il servizio\" sotto l'accesso").M(0, 8, 0, 0)));
        foreach (var r in rows)
        {
            var id = r.S("id"); var head = new DockPanel(); var pill = MainWindow.Pill(r.S("tipo")); pill.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(pill, Dock.Right); head.Children.Add(pill);
            head.Children.Add(Row(F.Avatar(r.S("nome")), Col(T($"{r.S("nome")} {r.S("cognome")}", size: 15, bold: true), Agency.Small(r.S("quando") + (r.Bo("avvisata") ? ", email inviata" : ""))).M(12, 0, 0, 0)));
            var phone = r.S("telefono") != "" ? (FrameworkElement)F.Link(r.S("telefono"), () => Agency.Web("tel:" + r.S("telefono"))) : T("non indicato", size: 14);
            var card = Col(head, Prog.Facts(("Attività", T(r.S("attivita") != "" ? r.S("attivita") : "non indicata", size: 14)), ("Email", F.Link(r.S("email"), () => Agency.Web("mailto:" + r.S("email")))), ("Telefono", phone)).M(0, 12, 0, 0));
            if (r.S("messaggio") != "") card.Children.Add(Prog.Text(r.S("messaggio")).M(0, 10, 0, 0));
            var actions = Row();
            if (r.S("stato") == "new")
            {
                actions.Children.Add(B("Attiva", () => Main.ShowPage(new NewClientPage { Request = id }), "Primary"));
                actions.Children.Add(BA("Archivia", () => Act(id, "archive")).M(8, 0, 0, 0));
            }
            else if (r.S("stato") == "done") { if (r["cliente"] is JsonNode cl) actions.Children.Add(B("Apri la scheda di " + cl.S("nome"), () => Main.ShowPage(new ClientCardPage { Slug = cl.S("slug") }))); }
            else actions.Children.Add(BA("Riporta tra le richieste", () => Act(id, "restore")));
            actions.Children.Add(BA("Elimina", async () => { if (Ask($"Eliminare la richiesta e i dati di {r.S("nome")}?")) await Act(id, "delete"); }, "Ghost").M(8, 0, 0, 0));
            card.Children.Add(actions.M(0, 14, 0, 0)); page.Children.Add(F.Card(card));
        }
        Content = F.Page(900, page);
    }
}

/// <summary>pricing_admin.html: the public page with plans, prices and editions, and the page Perché sceglierci.</summary>
public sealed class PricingPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("prezzi", new { }));

    Border Form(string title, FrameworkElement lead, FormView form, string page, Func<Task> reload)
    {
        var save = new Button { Content = "Salva", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await Guard(async () => { var r = await Api.Current.Call("prezzi.salva", new JsonObject { ["pagina"] = page, ["valori"] = form.Values() }); Main.Flash(r.S("testo")); await reload(); }); }
            catch (FormError ex) { form.Errors(ex.Errors); Main.Flash("Controlla i campi.", true); }
            finally { save.IsEnabled = true; }
        };
        return F.Card(T(title, "H2"), lead, form.Panel, save);
    }

    void Draw(JsonNode d)
    {
        var on = d.Bo("attiva"); var whyOn = d.Bo("percheAttiva");
        var page = Col(F.Heading(null, "Prezzi e versioni", "La pagina pubblica con piani, prezzi e versioni. È pensata per il portale dimostrativo: su un'installazione che serve solo i clienti lasciala spenta",
            on ? B("Apri la pagina", () => Agency.Web(d.S("pagina"))) : null));
        var state = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap }; state.Inlines.Add("La pagina è "); state.Inlines.Add(new Run(on ? "visibile" : "spenta") { FontWeight = FontWeights.Bold });
        state.Inlines.Add(on ? " su /prezzi/, con il collegamento sotto l'accesso" : ": nessuno la vede. Accendila qui sotto quando vuoi mostrarla");
        page.Children.Add(F.Notice(state, on ? "success" : "warning"));
        var plans = d.A("piani").ToList(); var list = Col(T("Piani", "H2"));
        if (plans.Count == 0) list.Children.Add(Prog.Muted("Nessun piano: aggiungine uno").M(0, 8, 0, 0));
        else list.Children.Add(Agency.Table(new[] { "Piano", "Versione", "Prezzo", "Stato", "" }, new[] { 2.4, 1.6, 1.0, 0.8, 0.6 }, plans.Select(p =>
        {
            var name = Row(T(p.S("nome"), size: 14, bold: true)); if (p.S("etichetta") != "") name.Children.Add(MainWindow.Pill(p.S("etichetta")).M(8, 0, 0, 0));
            var pill = MainWindow.Pill(p.Bo("visibile") ? "Visibile" : "Nascosto", p.Bo("visibile") ? "ok" : ""); pill.HorizontalAlignment = HorizontalAlignment.Left;
            var id = p.S("id"); Action open = () => Main.ShowPage(new PlanPage { Id = id });
            return (new FrameworkElement[] { Col(name, Agency.Small(p.S("descrizione"))), T(p.S("versione"), size: 14), T(p.S("prezzo"), size: 14), pill, F.Link("Modifica", open) }, open);
        }).ToList()));
        list.Children.Add(Prog.Left(B("Aggiungi un piano", () => Main.ShowPage(new PlanPage()), "Primary")).M(0, 14, 0, 0));
        var how = F.Card(T("Come si scrive", "H2"), Prog.Text("Punti di forza e domande vanno una per riga, con il titolo separato dal testo da una barra verticale:").M(0, 8, 0, 8), Prog.Code("Nessuna commissione | Paghi un canone fisso"),
            Prog.Muted("Le voci di ogni piano vanno una per riga, senza barra").M(0, 8, 0, 0));
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = F.Card(list); Grid.SetColumn(how, 2); top.Children.Add(left); top.Children.Add(how); page.Children.Add(top);
        page.Children.Add(Form("Testi della pagina Prezzi e versioni", null, new FormView(d.A("campi")), "pricing", Load));
        var lead = Prog.Muted("Una pagina a parte con i motivi e il confronto, collegata dalla pagina Prezzi e versioni e non dall'accesso. Ora è "); lead.Inlines.Add(new Run(whyOn ? "visibile" : "spenta") { FontWeight = FontWeights.Bold });
        lead.Inlines.Add((whyOn ? " su /perche-sceglierci/" : "") + ". Se nel confronto metti i nomi di altre piattaforme, i dati devono essere veri, verificabili e aggiornati");
        var why = Col(lead.M(0, 6, 0, 6)); if (whyOn) why.Children.Add(F.Link("Apri la pagina", () => Agency.Web(d.S("perche"))).M(0, 0, 0, 6));
        page.Children.Add(Form("Pagina Perché sceglierci", why, new FormView(d.A("campiPerche")), "why", Load));
        Content = F.Page(1100, page);
    }
}

/// <summary>plan_form.html: one card of the page Prezzi e versioni.</summary>
public sealed class PlanPage : Page
{
    public string Id = "";
    public override async Task Load() => Draw(await Api.Current.Call("piano", new { id = int.TryParse(Id, out var n) ? n : 0 }));
    void Back() => Main.ShowPage(new PricingPage());

    void Draw(JsonNode d)
    {
        var existing = d.S("id") != "0"; var form = new FormView(d.A("campi")); int id = int.Parse(d.S("id"));
        var save = new Button { Content = "Salva il piano", Style = Style("Primary") };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await Guard(async () => { var r = await Api.Current.Call("piano.salva", new JsonObject { ["id"] = id, ["valori"] = form.Values() }); Main.Flash(r.S("testo")); Back(); }); }
            catch (FormError ex) { form.Errors(ex.Errors); Main.Flash("Controlla i campi.", true); }
            finally { save.IsEnabled = true; }
        };
        var card = Col(form.Panel, Row(save, B("Annulla", Back, "Quiet").M(10, 0, 0, 0)).M(0, 12, 0, 0));
        if (existing) card.Children.Add(Prog.Left(BA("Elimina il piano", async () =>
        {
            if (!Ask($"Eliminare il piano {d.S("nome")}?")) return;
            var r = await Api.Current.Call("piano.salva", new JsonObject { ["id"] = id, ["azione"] = "delete" }); Main.Flash(r.S("testo")); Back();
        }, "Ghost")).M(0, 14, 0, 0));
        Content = F.Page(900, Col(F.Back("Prezzi e versioni", Back), F.Heading(null, existing ? d.S("nome") : "Nuovo piano", "Compare come una card nella pagina Prezzi e versioni, nel gruppo della sua versione"), F.Card(card)));
    }
}

/// <summary>plugin.html: the connector to install in the site of each business, and which sites are connected.</summary>
public sealed class PluginPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("plugin", new { }));

    void Draw(JsonNode d)
    {
        var version = d.S("versione");
        var page = Col(F.Heading(null, "Plugin WordPress", "Il connettore da installare nel sito di ogni attività: mostra la pagina prenotazioni dentro il sito",
            BA("Scarica il plugin " + version, async () => Downloads.Save($"td-booking-suite-{version}-wordpress.zip", await Api.Current.Bytes("impostazioni.plugin", new { })), "Primary")));
        var steps = Col(T("Come si installa", "H2"));
        var n = 1;
        foreach (var text in new[]
        {
            "In WordPress: Plugin › Aggiungi nuovo › Carica plugin, scegli lo zip appena scaricato e attivalo",
            "Nel programma, dalla scheda del cliente: Collega il sito, cioè Impostazioni e sito › Sito WordPress, e genera il codice di collegamento",
            "In WordPress, Impostazioni › TD Booking Suite › Collegati al portale: indirizzo del portale qui accanto e codice. Il sito si autorizza, legge sedi e servizi e crea la pagina Prenota",
        }) steps.Children.Add(Prog.Text($"{n++}. {text}").M(0, 8, 0, 0));
        steps.Children.Add(Prog.Muted("In alternativa, nelle impostazioni manuali del plugin: indirizzo del portale e indirizzo breve dell'attività della tabella qui sotto, con il sito aggiunto fra i siti autorizzati.").M(0, 10, 0, 0));
        steps.Children.Add(Prog.Muted("Per aggiornarlo, carica lo zip della nuova versione: WordPress sostituisce quella installata e le impostazioni restano").M(0, 6, 0, 0));
        var portal = F.Card(T("Indirizzo del portale", "H2"), Prog.Muted("Da scrivere nel plugin, in ogni sito").M(0, 6, 0, 8), Prog.Code(d.S("portale")));
        page.Children.Add(F.Cols(2, 16, F.Card(steps), portal));
        var rows = d.A("attivita").ToList(); var list = Col(T("Attività e siti collegati", "H2"));
        if (rows.Count == 0) list.Children.Add(Prog.Muted("Nessun cliente ancora: crealo in Clienti › Nuovo cliente").M(0, 8, 0, 0));
        else list.Children.Add(Agency.Table(new[] { "Attività", "Indirizzo breve", "Siti autorizzati", "" }, new[] { 1.6, 1.0, 2.2, 0.9 }, rows.Select(t =>
        {
            var sites = t.A("siti").Select(s => s.ToString()).ToList(); var slug = t.S("slug"); Action open = () => _ = Guard(() => Main.Open(slug, "impostazioni"));
            var shown = sites.Count > 0 ? (FrameworkElement)Col(sites.Select(s => (UIElement)T(s, size: 14)).ToArray()) : Agency.Small("Nessuno: genera il codice e collega il sito");
            return (new FrameworkElement[] { T(t.S("nome"), size: 14, bold: true), Prog.Code(slug, 13), shown, F.Link(sites.Count > 0 ? "Gestisci" : "Collega il sito", open) }, open);
        }).ToList()));
        page.Children.Add(F.Card(list));
        Content = F.Page(1100, page);
    }
}
