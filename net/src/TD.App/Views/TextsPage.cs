using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/texts.html: SMS, WhatsApp e calendari: the outside services, set by the administrator.</summary>
public sealed class TextsPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("testi", new { }));
    async Task Act(JsonObject args) { var r = await Api.Current.Call("testi.azione", args); Main.Flash(r.S("testo")); await Load(); }
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static Border Pill(string text, bool soft = false) => new() { Background = Res(soft ? "BrandSoft" : "OkSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center, Child = T(text, size: 12.5, bold: true, color: Res(soft ? "Brand" : "Ok")) };
    static TextBox Hinted(string value, string hint, int max = 0) { var b = Box(value); if (max > 0) b.MaxLength = max; if (hint != null) { b.Tag = hint; AgendaPage.Hint(b); } return b; }
    static Button Left(Button b) { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 16, 0, 0); return b; }
    static CheckBox Off(string text) => new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 0) };
    static TextBlock Step(int n, string text) { var t = Muted($"{n}. {text}"); t.Foreground = Res("Ink"); t.Margin = new Thickness(8, 3, 0, 3); return t; }

    void Draw(JsonNode d)
    {
        var channel = d.S("canale");
        var page = Col(F.Heading(d.S("nome"), "SMS, WhatsApp e calendari", "Qui si collegano i servizi esterni: SMS e WhatsApp per i promemoria ai clienti, Google Calendar e Outlook per gli operatori. I promemoria per SMS o WhatsApp partono insieme a quelli per email impostati in Configurazione promemoria, al numero della prenotazione, mai tra mezzanotte e le 7, e solo promemoria, niente pubblicità. Le chiavi restano cifrate e gli utenti dell'attività non le vedono"));

        // Come partono
        var provider = new ComboBox { ItemsSource = d.A("fornitori").Select(p => new ComboBoxItem { Content = p.S("nome"), Tag = p.S("id") }).ToList() };
        provider.SelectedItem = provider.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == d.S("provider")) ?? provider.Items[0];
        var user = Hinted(d.S("user"), null); var secret = new PasswordBox(); var sender = Hinted(d.S("sender"), "+39… o NomeAttività", 16);
        var smsOff = Off("Spegni gli SMS"); var waOff = Off("Spegni WhatsApp");
        var waPhone = Hinted(d.S("waPhone"), null); var waToken = new PasswordBox(); var waTemplate = Hinted(d.S("waTemplate"), "promemoria_prenotazione"); var waLang = Hinted(d.S("waLang"), null, 5); var limit = Box(d.S("limite"));
        var how = Col(F.Section("Come partono", null, channel == "whatsapp" ? Pill("WhatsApp" + (d.S("provider") != "" ? ", poi SMS" : "")) : channel != "" ? Pill("SMS") : Pill("Spento", true)),
            T("SMS", size: 17, bold: true).M(0, 12, 0, 0),
            F.Grid2(F.Field("Servizio", provider), F.Field("Twilio: Account SID · Skebby e Aruba: user_key", user), F.Field("Twilio: Auth Token · Skebby e Aruba: Access token" + (d.Bo("haSecret") ? " (salvato, vuoto per tenerlo)" : ""), secret), F.Field("Mittente", sender)),
            d.S("provider") != "" ? smsOff : null,
            T("WhatsApp Cloud API", size: 17, bold: true).M(0, 20, 0, 4),
            Muted("Serve un modello di tipo Utilità approvato da Meta, con quattro valori nel testo: {{1}} nome, {{2}} servizio, {{3}} giorno e ora, {{4}} attività. Se WhatsApp rifiuta il messaggio e gli SMS sono impostati, parte l'SMS"),
            F.Grid2(F.Field("ID del numero di telefono", waPhone), F.Field("Token di accesso" + (d.Bo("haWaToken") ? " (salvato, vuoto per tenerlo)" : ""), waToken), F.Field("Nome del modello", waTemplate), F.Field("Lingua del modello", waLang)),
            d.Bo("haWaToken") ? waOff : null,
            F.Grid2(F.Field("Messaggi al giorno, al massimo", limit, null, "Oltre, i promemoria aspettano il giorno dopo: tiene sotto controllo la spesa")),
            Left(BA("Salva", () => Act(new JsonObject
            {
                ["azione"] = "save", ["provider"] = (string)(provider.SelectedItem as ComboBoxItem)?.Tag ?? "", ["user"] = user.Text, ["secret"] = secret.Password, ["sender"] = sender.Text, ["sms_off"] = smsOff.IsChecked == true,
                ["wa_phone"] = waPhone.Text, ["wa_token"] = waToken.Password, ["wa_template"] = waTemplate.Text, ["wa_lang"] = waLang.Text, ["wa_off"] = waOff.IsChecked == true, ["texts_daily_limit"] = limit.Text,
            }), "Primary")),
            new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 16, 0, 0), Margin = new Thickness(0, 22, 0, 0), Child = F.Link("Torna alla dashboard", () => Main.Show("dashboard")) });

        // Messaggio di prova
        var test = Col(T("Messaggio di prova", "H2"));
        if (channel != "")
        {
            var to = Hinted("", "333 123 4567"); test.Children.Add(Muted("Parte subito con il canale salvato.")); test.Children.Add(F.Field("Mandalo al cellulare", to));
            var send = BA("Invia la prova", () => Act(new JsonObject { ["azione"] = "test", ["test_to"] = to.Text })); send.HorizontalAlignment = HorizontalAlignment.Stretch; test.Children.Add(send.M(0, 12, 0, 0));
        }
        else test.Children.Add(Muted("Salva prima un canale."));
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        var right = F.Card(test); right.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(right, 2); grid.Children.Add(F.Card(how)); grid.Children.Add(right);
        page.Children.Add(grid);

        // Google Calendar degli operatori
        var gId = Hinted(d.S("googleId"), "….apps.googleusercontent.com"); var gSecret = new PasswordBox(); var gOff = Off("Spegni Google Calendar: gli operatori collegati vengono scollegati");
        page.Children.Add(F.Card(F.Section("Google Calendar degli operatori", null, d.Bo("google") ? Pill("Attivo") : Pill("Spento", true)),
            Muted($"Con un client OAuth di Google Cloud, nella scheda di ogni operatore compare Collega Google Calendar: i suoi impegni su Google diventano orari occupati e le sue prenotazioni vanno nel suo calendario. In Google Cloud: attiva la Google Calendar API, nella schermata di consenso scegli Esterno e pubblica l'app «In produzione» (in prova il collegamento scade dopo 7 giorni), poi crea un ID client di tipo Applicazione web con questo URI di reindirizzamento: {d.S("googleUri")}. Sul PC Windows senza indirizzo pubblico usa un client di tipo App desktop e collega aprendo il programma da questo PC, all'indirizzo 127.0.0.1").M(0, 6, 0, 0),
            F.Grid2(F.Field("ID client", gId), F.Field("Client secret" + (d.Bo("googleSecret") ? " (salvato, vuoto per tenerlo)" : ""), gSecret)), d.Bo("google") ? gOff : null,
            Left(BA("Salva", () => Act(new JsonObject { ["azione"] = "google", ["client_id"] = gId.Text, ["client_secret"] = gSecret.Password, ["google_off"] = gOff.IsChecked == true }), "Primary"))));

        // Outlook e Microsoft 365 degli operatori
        var mId = Hinted(d.S("microsoftId"), "1a2b3c4d-…"); var mSecret = new PasswordBox(); var mOff = Off("Spegni Outlook: gli operatori collegati vengono scollegati");
        page.Children.Add(F.Card(F.Section("Outlook e Microsoft 365 degli operatori", null, d.Bo("microsoft") ? Pill("Attivo") : Pill("Spento", true)),
            Muted("Con un'app registrata in Microsoft Entra, nella scheda di ogni operatore compare Collega Outlook, per gli account Outlook.com e Microsoft 365: i suoi impegni diventano orari occupati e le sue prenotazioni vanno nel suo calendario.").M(0, 6, 0, 6),
            Step(1, $"Su entra.microsoft.com: Registrazioni app › Nuova registrazione. Tipi di account: account in qualsiasi directory organizzativa e account Microsoft personali. URI di reindirizzamento: piattaforma Web, {d.S("microsoftUri")}"),
            Step(2, "Certificati e segreti › Nuovo segreto client: copia subito la colonna Valore, non l'ID. Segna la scadenza: quando scade, metti qui un segreto nuovo"),
            Step(3, "Autorizzazioni API › Microsoft Graph › Autorizzazioni delegate: Calendars.ReadWrite, User.Read, offline_access"),
            Step(4, "Qui sotto incolla l'ID applicazione (client), dalla Panoramica dell'app, e il valore del segreto, poi Salva"),
            F.Grid2(F.Field("ID applicazione (client)", mId), F.Field("Valore del segreto client" + (d.Bo("microsoftSecret") ? " (salvato, vuoto per tenerlo)" : ""), mSecret)), d.Bo("microsoft") ? mOff : null,
            Left(BA("Salva", () => Act(new JsonObject { ["azione"] = "microsoft", ["ms_client_id"] = mId.Text, ["ms_client_secret"] = mSecret.Password, ["microsoft_off"] = mOff.IsChecked == true }), "Primary"))));
        Content = F.Page(1180, page);
    }
}
