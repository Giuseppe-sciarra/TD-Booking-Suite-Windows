using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/marketing.html: Marketing: the subscribers' CSV, the campaigns and the last outcomes.</summary>
public sealed class MarketingPage : Page
{
    const string WHO = "amministratore del programma";
    public override async Task Load() => Draw(await Api.Current.Call("marketing", new { }));
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static async Task Csv(string profile) => Downloads.Save("td-booking-suite-marketing.csv", await Api.Current.Bytes("esporta", new { kind = "marketing", profile }));

    void Draw(JsonNode d)
    {
        var on = d.Bo("attivo");
        var page = Col(F.Heading(null, "Marketing", "Novità e messaggi utili, alle persone che hanno scelto di riceverli.", on ? B("Nuova comunicazione", () => Main.ShowPage(new CampaignFormPage()), "Primary") : null));
        FrameworkElement export = null;
        if (d.Bo("esporta"))
        {
            var sheet = F.Link("CSV per fogli ↓", () => _ = Guard(() => Csv("sheet"))); sheet.VerticalAlignment = VerticalAlignment.Center; sheet.Margin = new Thickness(12, 0, 0, 0);
            var card = F.Card(T($"{d.I("iscritti")} contatti iscritti", "H2"), T("Il file dei clienti con il consenso confermato, pronto per il tuo programma di email.", size: 14).M(0, 4, 0, 12),
                Row(BA("CSV Brevo ↓", () => Csv("brevo")), BA("CSV Mailchimp ↓", () => Csv("mailchimp")).M(10, 0, 0, 0), sheet));
            card.Background = Res("BrandSoft"); export = card;
        }
        Border how;
        if (on) how = F.Card(T("Invio delle campagne attivo", "H2"), T($"L'{WHO} gestisce mittente e limiti. Tu scegli messaggio e momento.", size: 14).M(0, 4, 0, 0));
        else
        {
            var ask = BA(d.Bo("richiesto") ? "Richiesta già registrata" : "Chiedi di attivarle", async () => { var r = await Api.Current.Call("marketing.richiedi", new { }); if (r.S("testo") != "") Main.Flash(r.S("testo")); await Load(); }, "Primary");
            ask.IsEnabled = !d.Bo("richiesto"); ask.HorizontalAlignment = HorizontalAlignment.Left;
            how = F.Card(T("Vuoi inviarle da qui?", "H2"), T($"Le campagne email partono da qui quando l'{WHO} le attiva e configura il mittente.", size: 14).M(0, 4, 0, 12), ask);
        }
        foreach (var t in ((StackPanel)how.Child).Children.OfType<TextBlock>()) t.TextWrapping = TextWrapping.Wrap;
        page.Children.Add(F.Cols(2, 22, export, how));
        if (on)
        {
            var list = Col(T("Le tue comunicazioni", "H2"));
            foreach (var k in d.A("campagne"))
            {
                var open = F.Link("Apri", () => Main.ShowPage(new CampaignPreviewPage { Id = k.S("id") })); open.VerticalAlignment = VerticalAlignment.Center; open.Margin = new Thickness(14, 0, 0, 0); DockPanel.SetDock(open, Dock.Right);
                var pill = F.Status(k.S("stato")); DockPanel.SetDock(pill, Dock.Right);
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; row.Children.Add(open); row.Children.Add(pill); row.Children.Add(Col(T(k.S("nome"), size: 14, bold: true), Muted(k.S("oggetto"), 13))); list.Children.Add(row);
            }
            if (!d.A("campagne").Any()) list.Children.Add(Muted("La tua prima comunicazione comincia da un messaggio semplice.").M(0, 10, 0, 0));
            page.Children.Add(F.Card(list));
            var sent = Col(T("Ultimi esiti", "H2"), Muted("“Accettata da SMTP” indica la presa in carico del server, non il recapito nella posta in arrivo."));
            foreach (var x in d.A("esiti"))
            {
                var pill = F.Status(x.S("stato")); DockPanel.SetDock(pill, Dock.Right);
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; row.Children.Add(pill); row.Children.Add(Col(T(x.S("nome"), size: 14), Muted(x.S("quando"), 13))); sent.Children.Add(row);
            }
            if (!d.A("esiti").Any()) sent.Children.Add(T("Nessun invio.", size: 14).M(0, 10, 0, 0));
            page.Children.Add(F.Card(sent));
        }
        Content = F.Page(1180, page);
    }
}

/// <summary>form.html with CampaignForm: Crea una comunicazione.</summary>
public sealed class CampaignFormPage : Page
{
    public string Id = "";
    public override async Task Load() => Draw(await Api.Current.Call("campagna.modulo", new { id = Id }));
    void Draw(JsonNode d)
    {
        var form = new FormView(d.A("campi"));
        var save = new Button { Content = "Salva", Style = Style("Primary"), Margin = new Thickness(0, 18, 14, 0) };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await Guard(async () => { var r = await Api.Current.Call("campagna.salva", new JsonObject { ["id"] = Id, ["valori"] = form.Values() }); Main.ShowPage(new CampaignPreviewPage { Id = r.S("id") }); }); }
            catch (FormError ex) { form.Errors(ex.Errors); }
            finally { save.IsEnabled = true; }
        };
        var back = F.Link("Torna alla dashboard", () => Main.Show("dashboard")); back.VerticalAlignment = VerticalAlignment.Center; back.Margin = new Thickness(0, 18, 0, 0);
        Content = F.Page(1180, Col(F.Heading(d.S("nome"), d.S("titolo"), d.S("descrizione")), F.Card(form.Panel, Row(save, back))));
    }
}

/// <summary>templates/campaign_preview.html: the campaign as it will leave, the test, queue, pause and resume.</summary>
public sealed class CampaignPreviewPage : Page
{
    public string Id = "";
    public override async Task Load() => Draw(await Api.Current.Call("campagna", new { id = Id }));
    async Task Act(string action, string confirm = null)
    {
        if (confirm != null && !Ask(confirm)) return;
        var r = await Api.Current.Call("campagna.azione", new { id = Id, azione = action });
        if (r.S("testo") != "") Main.Flash(r.S("testo"));
        Main.Show("marketing");
    }
    void Draw(JsonNode d)
    {
        var status = d.S("stato");
        var body = new TextBlock { Text = d.S("testo"), FontSize = 14.5, TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
        Border Rule() => new() { Height = 1, Background = Res("Line"), Margin = new Thickness(0, 14, 0, 14) };
        var mail = F.Card(T("OGGETTO", size: 12, color: Res("Muted")), T(d.S("oggetto"), "H2"), Rule(), body, Rule(), T("Disiscrizione aggiunta automaticamente. Consensi ricontrollati prima dell'invio.", size: 12.5, color: Res("Muted")));
        mail.MaxWidth = 820; mail.Padding = new Thickness(40);
        var acts = Row(BA("Invia una prova al mio indirizzo", () => Act("test")));
        if (status == "draft")
        {
            acts.Children.Add(BA(d.S("programmata") != "" ? "Programma per " + d.S("programmata") : "Conferma e accoda", () => Act("queue", "Accodare ai contatti con consenso confermato?"), "Primary").M(10, 0, 0, 0));
            var edit = F.Link("Modifica", () => Main.ShowPage(new CampaignFormPage { Id = Id })); edit.VerticalAlignment = VerticalAlignment.Center; edit.Margin = new Thickness(14, 0, 0, 0); acts.Children.Add(edit);
        }
        else if (status == "queued") acts.Children.Add(BA("Metti in pausa", () => Act("pause")).M(10, 0, 0, 0));
        else if (status == "paused") acts.Children.Add(BA("Riprendi", () => Act("resume"), "Primary").M(10, 0, 0, 0));
        Content = F.Page(1180, Col(F.Heading("Anteprima", d.S("nome"), $"{d.I("idonei")} destinatari attualmente idonei · {StatusName(status)}"), mail, F.Card(acts)));
    }
}
