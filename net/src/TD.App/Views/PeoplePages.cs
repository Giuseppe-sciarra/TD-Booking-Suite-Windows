using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>.clean-list: a row with its text on the left and its buttons on the right, a line between the rows.</summary>
static class CleanList
{
    public static Border Row(bool last, string strong, string small, params Button[] actions)
    {
        var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in actions) if (b != null) { b.Margin = new Thickness(8, 0, 0, 0); acts.Children.Add(F.Small(b)); }
        DockPanel.SetDock(acts, Dock.Right); var d = new DockPanel(); d.Children.Add(acts);
        var s = T(small, size: 13, color: Res("Muted")); s.TextWrapping = TextWrapping.Wrap;
        d.Children.Add(Col(T(strong, size: 14, bold: true), small == "" ? null : s));
        return new Border { Child = d, Padding = new Thickness(0, 14, 0, 14), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1) };
    }
    public static TextBlock Empty(string text) => T(text, size: 14).M(0, 14, 0, 14);
}

/// <summary>templates/members.html: Persone e permessi: who has access, Entra senza password, Ripristina password,
/// Rimuovi accesso; Aggiungi o aggiorna una persona (invito o password scelta).</summary>
public sealed class PeoplePage : Page
{
    string error = "";
    public override async Task Load() => Draw(await Api.Current.Call("persone", new { }));
    async Task Act(object args)
    {
        try { Draw(await Api.Current.Call("persone.azione", args)); }
        catch (ApiError ex) { error = ex.Message; Draw(await Api.Current.Call("persone", new { })); }
    }

    void Draw(JsonNode d)
    {
        var page = Col(F.Heading(null, "Persone e permessi", "Ogni ruolo vede solo quello che gli serve. Operatore: l'agenda delle risorse assegnate. Marketing: contatti e campagne, mai le note. Consultazione: l'agenda con i nomi abbreviati"));
        if (error != "") { Main.Flash(error, true); error = ""; } else if (d.S("testo") != "") Main.Flash(d.S("testo"));
        if (d.S("invito") != "")
        {
            var link = new TextBox { Text = d.S("invito"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0, 6, 0, 8) };
            var p = T((d.Bo("emailPronta") ? "È partito anche per email. " : "L'email non parte finché non configuri l'SMTP: ") + "Copia il link e mandalo tu, per esempio su WhatsApp", size: 13.5); p.TextWrapping = TextWrapping.Wrap;
            var copy = F.Small(B("Copia il link", () => { Clipboard.SetText(d.S("invito")); Main.Flash("Link copiato."); })); copy.HorizontalAlignment = HorizontalAlignment.Left;
            page.Children.Add(F.Notice(Col(T("Invito personale, valido 24 ore", size: 13.5, bold: true), p, link, copy)));
        }
        // ----- Chi ha accesso
        var list = Col(); var people = d.A("persone").ToList();
        for (int i = 0; i < people.Count; i++)
        {
            var m = people[i]; var id = m.I("id"); var owner = m.S("ruolo") == "owner";
            var small = (m.S("email") != "" ? m.S("email") + " · " : "") + m.S("ruoloNome") + (m.Bo("disponibilita") ? " · Gestione disponibilità" : "") + (m.A("solo").Any() ? " · Solo: " + string.Join(", ", m.A("solo").Select(x => x.ToString())) : "") + (m.Bo("rapido") && owner ? " · Entra senza password" : "");
            Button quick = null;
            if (d.Bo("senzaPassword") && owner && !m.Bo("staff"))
            {
                quick = BA(m.Bo("rapido") ? "Torna alla password" : "Entra senza password", () => Act(new { azione = m.Bo("rapido") ? "quick_off" : "quick_on", member = id }));
                quick.ToolTip = "Dal PC del programma e dalle postazioni registrate, con un clic nella pagina di accesso";
            }
            list.Children.Add(CleanList.Row(i == people.Count - 1, m.S("nome"), small, quick, BA("Ripristina password", () => Act(new { azione = "reset", member = id })),
                BA("Rimuovi accesso", async () => { if (Ask("Rimuovere l'accesso a questa attività?")) await Act(new { azione = "remove", member = id }); }, "Ghost")));
        }
        if (people.Count == 0) list.Children.Add(CleanList.Empty("Nessun utente associato."));
        page.Children.Add(F.Card(T("Chi ha accesso", "H2"), list));
        // ----- Aggiungi o aggiorna una persona
        var username = Box(); username.MaxLength = 40; username.Tag = "mario.rossi"; AgendaPage.Hint(username); var email = Box(); email.MaxLength = 254;
        var role = new ComboBox { ItemsSource = d.A("ruoli").Select(r => new ComboBoxItem { Content = r.S("nome"), Tag = r.S("id") }).ToList() };
        role.SelectedItem = role.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == "operator") ?? role.Items[0];
        ListBox Many(string key) => new() { SelectionMode = SelectionMode.Extended, Height = 104, ItemsSource = d.A(key).Select(x => new ListBoxItem { Content = x.S("nome"), Tag = x.S("id") }).ToList() };
        var locations = Many("sedi"); var resources = Many("risorse");
        JsonArray Picked(ListBox l) => new(l.SelectedItems.Cast<ListBoxItem>().Select(i => (JsonNode)(string)i.Tag).ToArray());
        var fields = new List<FrameworkElement>
        {
            F.Field("Username", username, null, "Con questo entra nel programma. Lettere, numeri, punto e trattino"),
            F.Field("Email, facoltativa", email, null, "Per inviti e avvisi. Per una persona che ha già un accesso basta l'email o lo username"), F.Field("Ruolo", role),
        };
        if (locations.Items.Count > 0) fields.Add(F.Field("Limita alle sedi", locations, null, "Nessuna selezione: tutte le sedi"));
        if (resources.Items.Count > 0) fields.Add(F.Field("Risorse assegnate", resources, null, "Obbligatorie per l'operatore"));
        var can = new CheckBox { Content = T("Può modificare servizi e disponibilità (l'amministratore può sempre)", size: 14), Margin = new Thickness(0, 14, 0, 14) };
        var password = new PasswordBox();
        var note = T("Lascia vuoto per mandare l'invito. Con una password l'accesso è pronto subito, senza email: comunicala tu alla persona." + (d.Bo("mfa") ? " Dopo l'accesso può aggiungere Authenticator o una passkey da Account e sicurezza" : ""), size: 14, color: Res("Muted")); note.TextWrapping = TextWrapping.Wrap;
        var save = new Button { Content = "Salva", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                await Guard(async () => Draw(await Api.Current.Call("persone.azione", new JsonObject
                {
                    ["azione"] = "invite", ["username"] = username.Text, ["email"] = email.Text, ["role"] = (string)(role.SelectedItem as ComboBoxItem)?.Tag, ["locations"] = Picked(locations), ["resources"] = Picked(resources),
                    ["can_schedule"] = can.IsChecked == true, ["password"] = password.Password,
                })));
            }
            finally { save.IsEnabled = true; }
        };
        page.Children.Add(F.Card(T("Aggiungi o aggiorna una persona", "H2"), F.Grid2(fields.ToArray()), can,
            F.Details("Oppure imposta tu la password", Col(note.M(0, 8, 0, 0), F.Label("Password, almeno 12 caratteri"), password)), save));
        Content = F.Page(1180, page);
    }
}

/// <summary>templates/support.html: Accesso di assistenza: the grants, the pages opened by the assistance, Concedi un accesso.</summary>
public sealed class SupportPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("assistenza", new { }));
    async Task Act(object args) { var d = await Api.Current.Call("assistenza.azione", args); if (d.S("testo") != "") Main.Flash(d.S("testo")); Draw(d); }

    void Draw(JsonNode d)
    {
        var z = new AgendaPage { Main = Main }.Also(Business.S("fuso"));
        var grants = Col(); var gs = d.A("concessioni").ToList();
        for (int i = 0; i < gs.Count; i++)
        {
            var g = gs[i]; var until = z.Local(g.S("scade"));
            grants.Children.Add(CleanList.Row(i == gs.Count - 1, g.S("nome"), g.S("motivo") + " · " + (g.Bo("revocato") ? "Revocato" : g.Bo("attivo") ? $"Attivo fino alle {until:HH:mm dd/MM/yyyy}" : $"Scaduto il {until:dd/MM/yyyy HH:mm}"),
                g.Bo("attivo") ? BA("Revoca subito", () => Act(new { azione = "revoke", grant = g.S("id") }), "Ghost") : null));
        }
        if (gs.Count == 0) grants.Children.Add(CleanList.Empty("Nessun accesso concesso."));
        var reads = Col(); var rs = d.A("letture").ToList();
        for (int i = 0; i < rs.Count; i++) reads.Children.Add(CleanList.Row(i == rs.Count - 1, rs[i].S("pagina"), $"{z.Local(rs[i].S("quando")):dd/MM/yyyy HH:mm} · {rs[i].S("metodo")}"));
        if (rs.Count == 0) reads.Children.Add(CleanList.Empty("Nessun accesso registrato."));
        var left = F.Card(T("Concessioni", "H2"), grants, T("Pagine aperte dall'assistenza", size: 16, bold: true).M(0, 14, 0, 0), reads);
        var who = Box(); who.MaxLength = 254; var reason = Box("", true); reason.MaxLength = 300; reason.MinHeight = 70;
        var hours = new ComboBox { ItemsSource = new[] { ("1", "1 ora"), ("4", "4 ore"), ("24", "24 ore"), ("72", "3 giorni") }.Select(h => new ComboBoxItem { Content = h.Item2, Tag = h.Item1 }).ToList(), SelectedIndex = 1 };
        var grant = new Button { Content = "Concedi accesso", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        grant.Click += async (_, _) => { grant.IsEnabled = false; try { await Guard(() => Act(new { azione = "grant", email = who.Text, reason = reason.Text, hours = (string)((ComboBoxItem)hours.SelectedItem).Tag })); } finally { grant.IsEnabled = true; } };
        var hint = T("Richiede di aver confermato la propria identità negli ultimi 5 minuti.", size: 14, color: Res("Muted")); hint.TextWrapping = TextWrapping.Wrap;
        var aside = new Border { Style = Style("Card"), Child = Col(T("Concedi un accesso", "H2"), F.Label("Username o email dell'amministratore"), who, F.Label("Motivo"), reason, F.Label("Durata"), hours, grant, hint.M(0, 12, 0, 0)) };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        Grid.SetColumn(aside, 2); grid.Children.Add(left); grid.Children.Add(aside); aside.VerticalAlignment = VerticalAlignment.Top;
        Content = F.Page(1480, Col(F.Heading(null, "Accesso di assistenza", "L'amministratore della piattaforma non vede prenotazioni e clienti finché non gli concedi un accesso. Ogni pagina che apre resta registrata qui sotto."), grid));
    }
}
