using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/notifications.html: Configurazione promemoria: the WhatsApp texts, the notices for the business,
/// the evening summary, the emails to the clients with their preview.</summary>
public sealed class NotificationsPage : Page
{
    const string WHO = "amministratore del programma";
    public override async Task Load() => Draw(await Api.Current.Call("promemoria", new { }));
    async Task Act(JsonObject args) { var r = await Api.Current.Call("promemoria.azione", args); Main.Flash(r.S("testo")); await Load(); }
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static TextBox Area(string text, int rows, int max) { var b = Box(text, true); b.MinHeight = rows * 22 + 22; b.MaxLength = max; return b; }
    static ComboBox Pick(IEnumerable<(string Id, string Label)> items, string selected)
    {
        var c = new ComboBox { ItemsSource = items.Select(i => new ComboBoxItem { Content = i.Label, Tag = i.Id }).ToList() };
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? c.Items[0]; return c;
    }
    static string Val(ComboBox c) => (string)(c.SelectedItem as ComboBoxItem)?.Tag ?? "";
    static Button Left(Button b) { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 14, 0, 0); return b; }
    static StackPanel Title(string h2, string p) => Col(T(h2, "H2"), Muted(p)).M(0, 34, 0, 12);
    static string Hours(string key, int h) => key == "review"
        ? h < 24 ? $"{h} or{(h == 1 ? "a" : "e")} dopo la fine" : h == 24 ? "Il giorno dopo" : $"{Math.Round(h / 24.0)} giorni dopo"
        : h < 24 ? $"{h} or{(h == 1 ? "a" : "e")} prima" : h == 24 ? "Il giorno prima, 24 ore prima" : $"{Math.Round(h / 24.0)} giorni prima";

    void Draw(JsonNode d)
    {
        var fields = d.A("campi").Select(f => (Key: f.S("chiave"), Label: f.S("nome"))).ToList(); var sample = d["esempio"];
        var usable = "Puoi usare " + string.Join(", ", fields.Select(f => "{" + f.Key + "} " + f.Label)) + ". Vuoto torna al testo standard";
        string Fill(string t) => Regex.Replace(t ?? "", @"\{(\w+)\}", m => sample?[m.Groups[1].Value] is JsonValue v ? v.ToString() : m.Value);
        var page = Col(F.Heading(null, "Configurazione promemoria", "Qui decidete come vi avvisa il programma (prenotazioni nuove, spostate o annullate, il riepilogo della sera) e vedete le email e i promemoria che ricevono i clienti."));

        // Messaggio WhatsApp
        var text = Area(d.S("whatsapp"), 3, 1000); var again = Area(d.S("promemoria"), 3, 1000); var thanks = Area(d.S("grazie"), 3, 1000);
        var review = Box(d.S("recensione")); review.MaxLength = 500; review.Tag = "https://g.page/r/…/review"; AgendaPage.Hint(review);
        var small = Muted("Il testo della pagina ", 13); small.Inlines.Add(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Promemoria WhatsApp")) { Command = new Relay(() => Main.Show("whatsapp")) });
        small.Inlines.Add(": gli appuntamenti di domani, ognuno con la chat già scritta, da mandare con Invio");
        page.Children.Add(Title("Messaggio WhatsApp", "Il tasto WhatsApp di una prenotazione e della scheda del cliente apre la chat con il suo numero e questo testo, da mandare voi: il programma non invia niente. Compare quando c'è il telefono; dalla scheda il testo parla della prossima prenotazione, senza prenotazioni la chat si apre vuota."));
        page.Children.Add(F.Card(F.Field("Testo", text), Col(F.Label("Promemoria del giorno prima"), again, small.M(0, 4, 0, 0)),
            F.Field("Grazie del giorno dopo", thanks, null, "Nella stessa pagina, le visite di ieri: il grazie con il link alla recensione, da mandare con Invio. {link} qui è il link qui sotto"),
            F.Field("Link alla recensione", review, null, "Quello di Google: profilo dell'attività › Chiedi recensioni. Vale anche per l'email Dopo la visita. Senza link il grazie non compare"),
            Muted(usable, 13).M(0, 12, 0, 0),
            Left(BA("Salva i messaggi", () => Act(new JsonObject { ["azione"] = "whatsapp", ["whatsapp_text"] = text.Text, ["whatsapp_reminder"] = again.Text, ["whatsapp_thanks"] = thanks.Text, ["review_url"] = review.Text }), "Primary"))));

        // Avvisi per voi: the administrator's
        if (d.Bo("admin"))
        {
            var has = d.Bo("haToken"); var email = Box(d.S("email")); email.Tag = "prenotazioni@tuaattivita.it"; AgendaPage.Hint(email);
            var token = new PasswordBox(); var chat = Box(d.S("chat")); chat.Tag = "-1001234567890"; AgendaPage.Hint(chat);
            var off = new CheckBox { Content = "Disattiva Telegram", Margin = new Thickness(0, 14, 0, 0) };
            var ops = new CheckBox { IsChecked = d.Bo("avvisaOperatori"), Margin = new Thickness(0, 14, 0, 0), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Avvisa anche l'operatore della prenotazione, alla sua email: vale per le persone limitate ai propri operatori in Persone e permessi" + (d["operatori"] is JsonValue ? $" (ora {d.I("operatori")})" : "") } };
            page.Children.Add(F.Card(T("Avvisi per voi", "H2"), Muted("Per email, su Telegram o tutti e due. Contengono servizio, orario, risorsa e nome abbreviato; recapiti e note restano nel portale."),
                F.Grid2(F.Field("Email dell'attività", email, null, d.Bo("sito") ? "Gli avvisi email delle prenotazioni li manda il sito: qui serve per il riepilogo serale e la prova" : null),
                    F.Field("Token del bot Telegram" + (has ? " (salvato, lascia vuoto per mantenerlo)" : ""), token), F.Field("Chat o gruppo Telegram", chat)),
                has ? off : null, ops,
                Left(BA("Salva", () => Act(new JsonObject { ["azione"] = "save", ["notify_email"] = email.Text, ["telegram_token"] = token.Password, ["telegram_chat"] = chat.Text, ["telegram_off"] = off.IsChecked == true, ["notify_operators"] = ops.IsChecked == true }), "Primary")),
                Left(BA("Invia una prova", () => Act(new JsonObject { ["azione"] = "test" }))),
                Muted("Il token resta cifrato e non viene più mostrato. Le modifiche richiedono un accesso MFA recente.").M(0, 12, 0, 0)).M(0, 14, 0, 0));
        }
        else if (d.Bo("telegramPronto")) page.Children.Add(Muted("Avvisi delle prenotazioni su Telegram: attivi, impostati dall'amministratore del programma.", 13).M(0, 12, 0, 0));

        // Riepilogo serale
        if (d.Bo("admin") || d.Bo("emailPronta") || d.Bo("telegramPronto"))
        {
            var hour = Pick(new[] { ("", "Non mandarlo") }.Concat(d.A("oreRiepilogo").Select(h => (h.ToString(), $"Alle {h}:00"))), d.S("riepilogo"));
            page.Children.Add(Title("Riepilogo serale", "Ogni sera, all'ora scelta, l'elenco delle prenotazioni di domani: all'email dell'attività e su Telegram, e a ogni operatore le sue se è acceso \"Avvisa anche l'operatore\"."));
            page.Children.Add(F.Card(F.Field("Quando arriva", hour), Left(BA("Salva il riepilogo", () => Act(new JsonObject { ["azione"] = "summary", ["summary_hour"] = Val(hour) }), "Primary"))));
        }

        // Email ai clienti
        if (d.Bo("admin") || d.Bo("emailPronta") || d.Bo("messaggi"))
        {
            var site = d.Bo("sito"); var free = d.Bo("libere"); var read = new List<Action<JsonObject>>();
            if (site) page.Children.Add(F.Notice(Col(T("Le email le manda il sito", size: 13.5, bold: true), Muted("Con «Il sito gestisce prenotazioni ed email» acceso in Impostazioni e sito, conferme, promemoria, modifiche, annullamenti e gli avvisi email a voi li manda il sito WordPress, con i testi del plugin (WordPress › Impostazioni › TD Booking Suite). Le email ai clienti qui sotto sono disattivate e il programma non ne manda nessuna: tornano attive se spegni quell'interruttore. Anche Prima dell'arrivo, Dopo la visita, Richiamo e Si è liberato un posto le decide e le manda il sito, dai dati che il programma gli sincronizza, con gli interruttori e i testi del plugin.", 13.5)), "warning").M(0, 24, 0, 0));
            page.Children.Add(Title("Email ai clienti", "Arrivano ai clienti che lasciano l'email e non hanno detto di no: lo scelgono nel modulo di prenotazione, e lo cambiate dalla loro scheda in Clienti. Hanno il vostro logo e il vostro colore."));
            if (!free) page.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 16), Child = Muted($"Conferma, modifica e annullamento partono sempre, con il testo standard; per i servizi su richiesta anche la ricevuta della richiesta, l'accettazione e il rifiuto. I promemoria, il messaggio prima dell'arrivo e quello dopo la visita, e quando partono, li imposta l'{WHO}; lista d'attesa e richiami li accendete voi qui sotto. Per scegliere quali email partono e scrivere oggetto e testo, chiedi all'{WHO} di attivarlo.") });
            var set = Col(); if (site) { set.IsEnabled = false; set.Opacity = .55; }
            foreach (var g in d.A("gruppi"))
            {
                set.Children.Add(T(g.S("nome"), size: 18, bold: true).M(0, 18, 0, 16));
                foreach (var m in g.A("email")) set.Children.Add(Mail(m, d, free, fields, Fill, read));
            }
            if (d.Bo("messaggi"))
            {
                var sms = Area(d.S("sms"), 3, 640); var wa = Area(d.S("wa"), 3, 1024); var count = Muted("", 13);
                void Count()
                {
                    var n = sms.Text.Length; var gsm = Regex.IsMatch(sms.Text, @"^[\x20-\x7e\n£¥èéùìòÇØøÅåÄÖÑÜ§äöñüà€]*$"); int one = gsm ? 160 : 70, many = gsm ? 153 : 67;
                    count.Text = $"{n} caratteri · {(n <= one ? 1 : (int)Math.Ceiling(n / (double)many))} SMS{(gsm ? "" : " (con caratteri speciali valgono di più)")} · i campi cambiano la lunghezza";
                }
                sms.TextChanged += (_, _) => Count(); Count();
                set.Children.Add(T("SMS e WhatsApp", size: 18, bold: true).M(0, 18, 0, 16));
                set.Children.Add(F.Card(Muted("Il promemoria che parte per SMS o WhatsApp, insieme a quello per email. " + usable), Col(F.Label("Testo dell'SMS"), sms, count.M(0, 4, 0, 0)),
                    F.Field("Testo del modello WhatsApp approvato", wa, null, "Copia qui il testo del modello approvato da Meta scrivendo i campi al posto di {{1}}, {{2}}…: i valori partono nello stesso ordine. Vuoto: nome, servizio, giorno e ora, attività")));
                read.Add(o => { o["text_sms"] = sms.Text; o["text_wa"] = wa.Text; });
            }
            page.Children.Add(set);
            if (!site)
            {
                var save = BA("Salva le email ai clienti", () => { var o = new JsonObject(); foreach (var r in read) r(o); return Act(new JsonObject { ["azione"] = "client_mails", ["valori"] = o }); }, "Primary");
                page.Children.Add(Row(save, free ? Muted("Oggetto o testo vuoti tornano a quelli standard").M(14, 0, 0, 0) : null).M(0, 2, 0, 0));
                foreach (var t in page.Children.OfType<StackPanel>().Last().Children.OfType<TextBlock>()) t.VerticalAlignment = VerticalAlignment.Center;
            }
        }
        Content = F.Page(1180, page);
    }

    /// <summary>article.mail-kind: one email: the switch, when it goes, its words and the preview with the example booking.</summary>
    FrameworkElement Mail(JsonNode m, JsonNode d, bool free, List<(string Key, string Label)> fields, Func<string, string> fill, List<Action<JsonObject>> read)
    {
        var key = m.S("chiave"); var locked = m.Bo("bloccata");
        var on = F.Switch(m.Bo("accesa"), "Parte", "Spenta"); on.IsEnabled = !(!m.Bo("libera") && !free || locked);
        var headText = Col(T(m.S("nome"), size: 17, bold: true), Muted(m.S("aiuto")).M(0, 3, 0, 0));
        if (locked) headText.Children.Add(Muted($"Se parte e quando: li imposta l'{WHO}. Per cambiarli chiedi all'{WHO}", 13));
        var head = new DockPanel(); DockPanel.SetDock(on, Dock.Right); head.Children.Add(on.M(16, 0, 0, 0)); head.Children.Add(headText);
        var edit = Col(); read.Add(o => o[key + "_on"] = on.IsChecked == true);

        if (m["scelte"] is JsonArray choices)
        {
            var hasAt = m.Bo("haOra"); var items = choices.Select(h => (h.ToString(), Hours(key, (int)h))).ToList(); if (hasAt) items.Add(("at", "Il giorno prima, a un'ora fissa"));
            var when = Pick(items, m.S("alle") != "" ? "at" : m.S("ore")); when.IsEnabled = !locked;
            edit.Children.Add(F.Field("Quando parte", when, null, key == "reminder" ? "A chi prenota quando mancano meno ore, il promemoria non serve e non parte" : null));
            var at = Box(m.S("alle") != "" ? m.S("alle") : "18:00"); at.IsEnabled = !locked; at.Width = 110; at.HorizontalAlignment = HorizontalAlignment.Left;
            if (hasAt)
            {
                var atField = F.Field("Il giorno prima alle", at, null, "Parte il giorno prima a quest'ora, ora dell'attività. A chi prenota dopo quell'ora non parte");
                void Show() => atField.Visibility = Val(when) == "at" ? Visibility.Visible : Visibility.Collapsed;
                when.SelectionChanged += (_, _) => Show(); Show(); edit.Children.Add(atField);
            }
            read.Add(o => { o[key + "_hours"] = Val(when); if (hasAt) o[key + "_at"] = at.Text; });
        }
        if (key == "reminder")
        {
            var cancel = Pick(d.A("annulli").Select(h => (h.ToString(), (int)h > 0 ? $"Annullala da sola {h} ore prima, con un'email al cliente" : "Resta confermata e compare in Da chiamare")), m.S("annulla")); cancel.IsEnabled = !locked;
            edit.Children.Add(F.Field("Se il cliente non preme Confermo", cancel, null, "Con Non vengo la prenotazione si annulla e il posto torna libero; oltre il termine per annullare online, vi avvisa e la decidete voi"));
            read.Add(o => o["reminder_autocancel"] = Val(cancel));
        }
        if (key == "confirmed")
        {
            var price = new CheckBox { IsChecked = m.Bo("prezzo"), Margin = new Thickness(0, 12, 0, 0), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Aggiungi il prezzo indicativo, si paga in sede, quando il servizio ha un prezzo" } };
            edit.Children.Add(price); read.Add(o => o["confirmed_price"] = price.IsChecked == true);
        }
        if (key == "arrival") edit.Children.Add(Muted("Parte per soggiorni ed eventi con le istruzioni scritte nel servizio, in Servizi › Istruzioni prima dell'arrivo").M(0, 12, 0, 0));
        if (key == "review")
        {
            var url = Box(m.S("url")); url.MaxLength = 500; url.Tag = "https://g.page/r/…/review"; AgendaPage.Hint(url);
            edit.Children.Add(F.Field("Link per la recensione", url, null, "Per esempio quello di Google: profilo dell'attività › Chiedi recensioni. Parte solo per le prenotazioni segnate Fatta"));
            read.Add(o => o["review_url"] = url.Text);
        }

        // the preview: the words with the values of an example booking
        var pSubject = T("", size: 14, bold: true); pSubject.TextWrapping = TextWrapping.Wrap; var pBody = T("", size: 14); pBody.TextWrapping = TextWrapping.Wrap; pBody.LineHeight = 21.7;
        var subject = Box(m.S("oggetto")); subject.MaxLength = 150; var body = Area(m.S("testo"), 6, 4000); var last = body;
        void Preview()
        {
            pSubject.Text = fill(subject.Text != "" ? subject.Text : m.S("oggettoStandard")); pBody.Text = fill(body.Text != "" ? body.Text : m.S("testoStandard"));
        }
        if (free)
        {
            var subjectEn = Box(m.S("oggettoEn")); subjectEn.MaxLength = 150; var bodyEn = Area(m.S("testoEn"), 4, 4000);
            foreach (var f in new[] { subject, body }) { f.TextChanged += (_, _) => Preview(); f.GotKeyboardFocus += (_, _) => last = f; }
            edit.Children.Add(F.Field("Oggetto", subject)); edit.Children.Add(F.Field("Testo", body));
            edit.Children.Add(F.Details("In inglese (facoltativo)", Col(F.Field("Oggetto in inglese", subjectEn), F.Field("Testo in inglese", bodyEn, null, "Va a chi ha prenotato dalla pagina in inglese. Vuoto: il testo standard tradotto")), plain: true).M(0, 12, 0, 0));
            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 12) }; chips.Children.Add(Muted("Aggiungi", 13).M(0, 4, 8, 0));
            foreach (var (k, label) in fields)
            {
                var chip = new Button { Content = label, ToolTip = "Inserisce " + label, FontSize = 13, FontWeight = FontWeights.SemiBold, MinHeight = 0, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), Foreground = Res("Brand"), Background = Res("Surface"), BorderBrush = Res("Line") };
                chip.Resources.Add(typeof(Border), new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(999)) } });
                // the field goes where the cursor is, in the subject or the text, whichever was used last
                chip.Click += (_, _) => { var box = last; var a = box.SelectionStart; box.SelectedText = "{" + k + "}"; box.SelectionLength = 0; box.SelectionStart = a + k.Length + 2; box.Focus(); };
                chips.Children.Add(chip);
            }
            edit.Children.Add(chips);
            var reset = F.Small(B("Torna al testo standard", () => { subject.Text = m.S("oggettoStandard"); body.Text = m.S("testoStandard"); }, "Ghost")); reset.HorizontalAlignment = HorizontalAlignment.Left; edit.Children.Add(reset);
            read.Add(o => { o[key + "_subject"] = subject.Text; o[key + "_body"] = body.Text; o[key + "_subject_en"] = subjectEn.Text; o[key + "_body_en"] = bodyEn.Text; });
        }
        Preview();
        var from = Muted("", 13); from.Inlines.Add(new System.Windows.Documents.Run("Da ") { FontWeight = FontWeights.SemiBold }); from.Inlines.Add(d.S("nome"));
        var sheet = Col(from.M(0, 0, 0, 6), pSubject.M(0, 0, 0, 12), pBody);
        Button Fake(string text, string style) { var b = F.Small(new Button { Content = text, IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 14, 8, 0) }); if (style != null) b.Style = Style(style); b.Padding = new Thickness(11, 5, 11, 5); b.MinHeight = 0; b.FontSize = 13; return b; }
        if (key is "reminder" or "reminder2") sheet.Children.Add(Row(Fake("Confermo", "Primary"), Fake("Non vengo", null)));
        else if (key == "review") sheet.Children.Add(Row(Fake("Lascia una recensione", "Primary")));
        var caption = T("ANTEPRIMA", size: 12, bold: true, color: Res("Muted"));
        var preview = Col(caption.M(0, 0, 0, 8), new Border { Background = Res("Bg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 16, 18, 16), Child = sheet });

        var grid = new Grid { Margin = new Thickness(22, 18, 22, 22) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (edit.Children.Count > 0 && edit.Children[0] is FrameworkElement first) first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
        Grid.SetColumn(preview, 2); grid.Children.Add(edit); grid.Children.Add(preview);
        void Dim() => grid.Opacity = on.IsChecked == true ? 1 : .55;
        on.Checked += (_, _) => Dim(); on.Unchecked += (_, _) => Dim(); Dim();
        var card = F.Card(new Border { Padding = new Thickness(22, 18, 22, 18), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = head }, grid);
        card.Padding = new Thickness(0); card.Margin = new Thickness(0, 0, 0, 16);
        return card;
    }

    sealed class Relay(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object p) => true;
        public void Execute(object p) => run();
    }
}
