using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/api.html: API e webhook.</summary>
public sealed class ApiPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("api", new { }));
    async Task Act(object args) { var d = await Api.Current.Call("api.azione", args); if (d.S("testo") != "") Main.Flash(d.S("testo")); Draw(d); }
    static TextBlock Muted(string text) { var t = T(text, size: 14, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static TextBox Code(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0) };

    void Draw(JsonNode d)
    {
        var has = d.Bo("haChiave"); var b = d.S("base");
        var key = Col(T("Chiave API", "H2"), Muted("Sola lettura: prenotazioni con i recapiti del cliente, servizi e orari liberi. Chi ha la chiave vede questi dati: dalla solo a chi deve collegare il programma."));
        if (d.S("chiave") != "") key.Children.Add(F.Notice(Col(T("Copia la chiave adesso: non verrà più mostrata.", size: 13.5, bold: true), Code(d.S("chiave"))), "success"));
        key.Children.Add(T(has ? "Chiave attiva." : "Nessuna chiave: l'API è spenta.", size: 14).M(0, 10, 0, 10));
        var acts = Row(BA(has ? "Crea una chiave nuova (la vecchia smette di valere)" : "Crea la chiave", () => Act(new { azione = "key" }), "Primary"));
        if (has) acts.Children.Add(BA("Revoca la chiave", () => Act(new { azione = "revoke" })).M(10, 0, 0, 0));
        key.Children.Add(acts);
        key.Children.Add(Muted("Ogni chiamata manda l'intestazione Authorization: Bearer la-chiave, al massimo 120 al minuto. Indirizzi, sempre in GET:").M(0, 14, 0, 6));
        foreach (var (url, what) in new[] { (b + "bookings/?from=AAAA-MM-GG&to=AAAA-MM-GG", "le prenotazioni (senza date, i prossimi 30 giorni)"), (b + "services/", "i servizi attivi con le risorse"), (b + "availability/?service=ID&day=AAAA-MM-GG", "gli orari liberi, anche con resource e party") })
            key.Children.Add(Col(Code(url), T(what, size: 14)).M(16, 4, 0, 4));
        var hook = Col(T("Webhook", "H2"), Muted("A ogni prenotazione creata, spostata o annullata parte un POST in JSON con l'evento, l'ID, l'orario, il servizio e la risorsa, senza dati del cliente: per quelli si legge la prenotazione dall'API. Se il server non risponde si riprova più volte, a intervalli crescenti."));
        var address = Box(d.S("url")); address.MaxLength = 500; address.Tag = "https://esempio.it/td-booking"; AgendaPage.Hint(address);
        hook.Children.Add(F.Field("Indirizzo https", address, null, "Vuoto per toglierlo"));
        var save = BA("Salva il webhook", () => Act(new { azione = "webhook", url = address.Text }), "Primary"); save.HorizontalAlignment = HorizontalAlignment.Left; hook.Children.Add(save.M(0, 12, 0, 0));
        if (d.S("segreto") != "") { hook.Children.Add(Muted("Firma: l'intestazione X-TDBS-Signature vale sha256= più l'HMAC-SHA256, con questo segreto, di X-TDBS-Timestamp, un punto e il corpo. Segreto:").M(0, 14, 0, 0)); hook.Children.Add(Code(d.S("segreto"))); }
        var n = d.I("falliti"); if (n > 0) hook.Children.Add(F.Notice($"{n} invi{(n == 1 ? "o" : "i")} non riuscit{(n == 1 ? "o" : "i")} dopo tutti i tentativi.", "warning"));
        Content = F.Page(1180, Col(F.Heading(null, "API e webhook", $"Per collegare {d.S("nome")} a un CRM, a un foglio di calcolo o a un'automazione. Riservato all'amministratore."), F.Card(key), F.Card(hook)));
    }
}
