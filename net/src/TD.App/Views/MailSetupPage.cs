using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/smtp.html: Invio email, of a business or of the program (site): SMTP, Google / Gmail, Microsoft 365,
/// and the test email.</summary>
public sealed class MailSetupPage(bool site) : Page
{
    const string WHO = "amministratore del programma";
    string tab; FormView form;
    string Rpc(string name) => site ? name.Replace("smtp", "smtp.sito") : name;
    public override async Task Load() => Draw(await Api.Current.Call(Rpc("smtp"), new { }));
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static Border Pill(string text, bool soft = false) => new() { Background = Res(soft ? "BrandSoft" : "OkSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center, Child = T(text, size: 12.5, bold: true, color: Res(soft ? "Brand" : "Ok")) };
    static void Open(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say("Apri questo indirizzo nel browser: " + url); } }
    static TextBlock Step(int n, params object[] parts)
    {
        var t = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 0, 4) }; t.Inlines.Add($"{n}. ");
        foreach (var p in parts) if (p is (string text, string url)) t.Inlines.Add(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { Command = new Relay(() => Open(url)) }); else t.Inlines.Add((string)p);
        return t;
    }

    async Task Act(string action, JsonObject values = null, string testTo = null)
    {
        try
        {
            var r = await Api.Current.Call(Rpc("smtp.azione"), new JsonObject { ["azione"] = action, ["valori"] = values, ["test_to"] = testTo });
            if (r.S("apri") != "") { Open(r.S("apri")); Main.Flash("Si è aperto Google nel browser: dai il consenso, poi torna qui e premi Aggiorna."); return; }
            Main.Flash(r.S("testo"), r.Bo("errore")); await Load();
        }
        catch (FormError ex) { form?.Errors(ex.Errors); }
    }

    void Draw(JsonNode d)
    {
        tab ??= d.S("scheda"); var method = d.S("metodo");
        var page = Col(F.Heading(site ? null : d.S("nome"), d.S("titolo"), d.S("descrizione")));
        if (d.Bo("daSito")) page.Children.Add(F.Notice(Col(T("Le email delle prenotazioni le manda il sito", size: 13.5, bold: true), Muted("Con «Il sito gestisce prenotazioni ed email» acceso in Impostazioni e sito, conferme, promemoria, modifiche e annullamenti partono dal sito WordPress. Da questa casella partono solo la copia dello scontrino a chi la chiede alla cassa, il riepilogo serale, le campagne marketing e l'email di prova.", 13.5)), "warning"));

        var config = Col(F.Section("Come partono le email", null, d.Bo("proprio") ? Pill("Attivo: " + d.S("etichetta")) : d.Bo("daAgenzia") ? Pill("Ora: invio dell'agenzia", true) : null));
        // nav.method-tabs
        var tabs = new List<FrameworkElement>();
        foreach (var t in d.A("schede"))
        {
            var key = t.S("chiave"); var on = key == tab;
            var b = new Border { BorderBrush = Res(on ? "Brand" : "Line"), BorderThickness = new Thickness(on ? 2 : 1), Background = Res(on ? "BrandSoft" : "Surface"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Cursor = System.Windows.Input.Cursors.Hand, Child = Col(T(t.S("nome"), size: 14, bold: true), Muted(t.S("testo"), 12.5)) };
            b.MouseLeftButtonUp += (_, _) => { tab = key; Draw(d); }; tabs.Add(b);
        }
        config.Children.Add(F.Cols(3, 10, tabs.ToArray()).M(0, 12, 0, 18));

        Button Submit(string text, string action, string style = "Primary") { var b = BA(text, () => Act(action, form.Values()), style); b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 16, 0, 0); return b; }
        FrameworkElement Connected(string text, string who) { var t = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; t.Inlines.Add(new System.Windows.Documents.Run("✓ ") { Foreground = Res("Ok"), FontWeight = FontWeights.Bold }); t.Inlines.Add(text + " "); t.Inlines.Add(new System.Windows.Documents.Run(who) { FontWeight = FontWeights.SemiBold }); return t; }
        if (tab == "smtp")
        {
            form = new FormView(d.A("smtp"));
            var hint = Muted("", 13); hint.Visibility = Visibility.Collapsed; var list = new WrapPanel();
            foreach (var p in d.A("fornitori"))
            {
                var b = F.Small(B(p.S("nome"), () =>
                {
                    // the provider's server and port in the form, its advice under the buttons
                    if (form.Control("host") is TextBox host && p.S("host") != "") host.Text = p.S("host");
                    if (form.Control("port") is ComboBox port && port.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.S("porta")) is ComboBoxItem item) port.SelectedItem = item;
                    hint.Text = p.S("aiuto"); hint.Visibility = Visibility.Visible;
                }));
                list.Children.Add(b.M(0, 0, 8, 8));
            }
            config.Children.Add(Col(T("Con chi spedisci le email?", size: 13, bold: true).M(0, 0, 0, 6), list, hint).M(0, 0, 0, 8));
            config.Children.Add(form.Panel); config.Children.Add(Submit("Salva l'SMTP", "save_smtp"));
        }
        else if (tab == "google")
        {
            form = new FormView(d.A("google"));
            if (method == "gmail") config.Children.Add(Connected("Collegato con l'account", d.S("account")));
            var uri = new TextBox { Text = d.S("ritorno"), IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            var copy = F.Small(B("Copia", () => { Clipboard.SetText(d.S("ritorno")); Main.Flash("Copiato."); }, "Ghost"));
            config.Children.Add(F.Details("Come si prepara, una volta sola", Col(
                Step(1, "Apri ", ("Google Cloud Console", "https://console.cloud.google.com/apis/library/gmail.googleapis.com"), " con l'account Google o Workspace che invierà le email, crea un progetto e abilita la Gmail API"),
                Step(2, "In Schermata consenso OAuth (Google Auth Platform): con Google Workspace scegli Interno e basta. Con un Gmail normale scegli Esterno, aggiungi la casella fra gli utenti di test e, appena il collegamento funziona, premi Pubblica l'app: finché resta in stato di test Google scollega l'accesso dopo 7 giorni e le email si fermano. Con un Gmail normale è più semplice la scheda SMTP con una password per le app"),
                Step(3, "In Credenziali crea un ID client OAuth di tipo Applicazione web e, fra gli URI di reindirizzamento autorizzati, incolla questo indirizzo"), Row(uri, copy).M(24, 0, 0, 0),
                Step(4, "Copia qui Client ID e Client secret e premi Collega con Google: Google ti chiede di autorizzare l'invio delle email")).M(0, 8, 0, 8), open: method != "gmail"));
            config.Children.Add(form.Panel);
            var update = F.Link("Aggiorna", () => _ = Guard(Load)); update.VerticalAlignment = VerticalAlignment.Bottom; update.Margin = new Thickness(14, 0, 0, 8);
            config.Children.Add(Row(Submit(method == "gmail" ? "Ricollega con Google" : "Collega con Google", "google_connect"), update));
        }
        else
        {
            form = new FormView(d.A("graph"));
            if (method == "graph") config.Children.Add(Connected("Le email partono dalla casella", d.S("casella")));
            config.Children.Add(F.Details("Come si prepara, una volta sola", Col(
                Step(1, "In ", ("Microsoft Entra", "https://entra.microsoft.com/"), ", Registrazioni app, crea una nuova registrazione: basta il nome, per esempio Booking Suite"),
                Step(2, "In Autorizzazioni API aggiungi Microsoft Graph, Autorizzazioni applicazione, Mail.Send, poi premi Concedi il consenso amministratore"),
                Step(3, "In Certificati e segreti crea un segreto client e copia subito il Valore"),
                Step(4, "Copia qui ID directory, ID applicazione e il valore del segreto, e indica la casella da cui inviare. Per limitare l'app a quella sola casella puoi usare un criterio di accesso di Exchange Online")).M(0, 8, 0, 8), open: method != "graph"));
            config.Children.Add(form.Panel); config.Children.Add(Submit(method == "graph" ? "Salva e verifica" : "Collega Microsoft 365", "save_graph"));
        }
        // .mail-back
        var back = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        if (d.Bo("proprio"))
        {
            var remove = F.Small(BA("Rimuovi l'invio salvato", async () => { if (Ask("Rimuovere l'invio email salvato" + (site ? "" : "? Le conferme partiranno dall'invio dell'" + WHO) + "?")) await Act("remove"); }, "Ghost")); DockPanel.SetDock(remove, Dock.Right); back.Children.Add(remove);
        }
        back.Children.Add(site ? F.Link("Torna a Impostazioni generali", () => Main.Show("generali")) : new TextBlock());
        config.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 16, 0, 0), Margin = new Thickness(0, 22, 0, 0), Child = back });

        // aside: Email di prova
        var test = Col(T("Email di prova", "H2"));
        if (d.Bo("configurato"))
        {
            test.Children.Add(Muted("Parte subito con la configurazione salvata" + (d.Bo("daEnv") ? ", quella del file .env" : d.Bo("daAgenzia") ? ", cioè l'invio dell'" + WHO : "") + ": " + d.S("come")));
            test.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 12, 0, 12), Child = Col(Muted("I clienti vedono", 12.5), new TextBlock { Text = d.S("mittente"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }) });
            var to = Box(d.S("email")); test.Children.Add(F.Field("Mandala a", to));
            var send = BA("Invia l'email di prova", () => Act("test", null, to.Text)); send.HorizontalAlignment = HorizontalAlignment.Stretch; test.Children.Add(send.M(0, 12, 0, 0));
        }
        else test.Children.Add(Muted("Salva prima la configurazione: poi da qui la provi con un clic"));

        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        var left = F.Card(config); var right = F.Card(test); right.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(right, 2); grid.Children.Add(left); grid.Children.Add(right);
        page.Children.Add(grid);
        Content = F.Page(1180, page);
    }

    sealed class Relay(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object p) => true;
        public void Execute(object p) => run();
    }
}
