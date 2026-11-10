using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>.wait-list: the rows of Lista d'attesa, Richiami and Promemoria WhatsApp: who, what, the actions on the right.</summary>
static class WaitRows
{
    public static void Web(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public static Border Line(bool first, UIElement who, UIElement what, IEnumerable<Button> actions)
    {
        var acts = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in actions) { b.Margin = new Thickness(6, 0, 0, 0); acts.Children.Add(F.Small(b)); }
        var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(what, 2); Grid.SetColumn(acts, 4); g.Children.Add(who); g.Children.Add(what); g.Children.Add(acts);
        return new Border { Child = g, Padding = new Thickness(0, 14, 0, 14), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) };
    }
    /// <summary>A muted paragraph with links inside: strings and (text, click) pairs.</summary>
    public static TextBlock Para(double size, params object[] parts)
    {
        var t = new TextBlock { FontSize = size, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap };
        foreach (var p in parts)
            if (p is string s) t.Inlines.Add(new Run(s));
            else if (p is (string text, Action click)) { var h = new Hyperlink(new Run(text)) { Foreground = Res("Brand") }; h.Click += (_, _) => click(); t.Inlines.Add(h); }
        return t;
    }
    public static string Cap(string s) => s == "" ? s : char.ToUpper(s[0], AgendaPage.It) + s[1..];
}

/// <summary>templates/recalls.html: Richiami: the clients whose recall is due (a week ahead), without a booking to come;
/// Chiama, WhatsApp, Prenota, Fatto.</summary>
public sealed class RecallsPage : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    public override async Task Load()
    {
        var z = new AgendaPage { Main = Main }.Also(Business.S("fuso")); var d = await Api.Current.Call("richiami", new { });
        var write = Can("booking.write"); var rows = d.A("righe").ToList(); var list = Col();
        var p = WaitRows.Para(14, "I clienti da richiamare per i servizi con il richiamo, da oggi a una settimana: chi ha già un appuntamento in programma non compare. Il richiamo si imposta nel servizio",
            Can("catalog.write") ? ", l'email automatica in " : "", Can("catalog.write") ? ((string, Action))("Configurazione promemoria", () => Main.Show("promemoria")) : "");
        var heading = F.Heading(null, "Richiami", null); ((StackPanel)heading.Children[^1]).Children.Add(p);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var due = z.Local(r.S("dal"));
            var state = new Border { Background = Res(r.Bo("scaduto") ? "OkSoft" : "BrandSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Child = T(r.Bo("scaduto") ? $"Da richiamare dal {due:d/M}" : $"Dal {due:d/M}", size: 12.5, bold: true, color: Res(r.Bo("scaduto") ? "Ok" : "Muted")) };
            var who = Col(T($"{r.S("nome")} {r.S("cognome")}".Trim(), size: 14, bold: true), T(string.Join(" · ", new[] { r.S("telefono"), r.S("email") }.Where(x => x != "")), size: 13, color: Res("Muted")), state);
            var what = Col(T(r.S("servizio"), size: 14, bold: true), T("Ultima volta il " + z.Local(r.S("ultima")).ToString("d MMMM yyyy", It), size: 13, color: Res("Muted")),
                r.S("emailInviata") != "" ? T("Email inviata il " + z.Local(r.S("emailInviata")).ToString("dd/MM/yyyy HH:mm"), size: 13, color: Res("Muted")) : null);
            var acts = new List<Button>();
            if (r.S("telefono") != "" && !r.Bo("mascherato")) acts.Add(B("Chiama", () => WaitRows.Web("tel:" + r.S("telefono"))));
            if (r.S("whatsapp") != "") acts.Add(B("WhatsApp", () => WaitRows.Web(r.S("whatsapp"))));
            if (write)
            {
                acts.Add(B("Prenota", () => Main.ShowPage(new BookingPage { PrefillClient = r.S("cliente"), PrefillService = r.S("servizioId") })));
                acts.Add(BA("Fatto", async () => { var a = await Api.Current.Call("richiamo.fatto", new { booking = r.S("prenotazione") }); Main.Flash(a.S("testo")); await Load(); }, "Ghost"));
            }
            list.Children.Add(WaitRows.Line(i == 0, who, what, acts));
        }
        if (rows.Count == 0) list.Children.Add(F.EmptyState("Nessuno da richiamare", T("Quando passa il tempo del richiamo di un servizio, il cliente compare qui finché non prenota di nuovo", size: 13, color: Res("Muted"))));
        Content = F.Page(1480, Col(heading, F.Card(F.Section("Da richiamare", rows.Count), list)));
    }
}

/// <summary>templates/wa_reminders.html: Promemoria WhatsApp: the appointments of the next days (or of one day), each with
/// WhatsApp open on its text, and the thanks with the review link to who came; the program marks who was told.</summary>
public sealed class WhatsAppPage : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    string day = "", from = "", thanks = ""; AgendaPage z;

    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }.Also(Business.S("fuso"));
        Draw(await Api.Current.Call("whatsapp.promemoria", new { giorno = day, da = from, grazie = thanks }));
    }
    Button Go(string text, Action set) => F.Small(BA(text, async () => { set(); await Load(); }, "Ghost"));
    async Task Mark(string id, bool on, bool isThanks) { await Api.Current.Call("whatsapp.segna", new { booking = id, sent = on ? "1" : "0", kind = isThanks ? "thanks" : "" }); await Load(); }

    Border Line(JsonNode r, bool first, bool isThanks, bool writer)
    {
        var sent = r.S("mandato") != ""; var wa = r.S("whatsapp"); var id = r.S("id");
        var who = Col(T(r.S("chi") != "" ? r.S("chi") : "Senza nome", size: 14, bold: true), T($"{z.Local(r.S("inizio")):HH:mm} · {r.S("servizio")}" + (r.S("operatore") != "" ? " con " + r.S("operatore") : ""), size: 13, color: Res("Muted")));
        var text = sent ? (isThanks ? "Mandato alle " : "Avvisato alle ") + z.Local(r.S("mandato")).ToString("HH:mm") : isThanks ? (wa == "" ? "Senza telefono" : "Da ringraziare")
            : wa == "" ? "Senza telefono: avvisalo in un altro modo" : r.Bo("daAvvisare") ? "Da avvisare" : "In programma";
        var state = T(text, size: 13, bold: sent, color: Res(sent ? "Ok" : "Muted")); state.VerticalAlignment = VerticalAlignment.Center;
        var acts = new List<Button>();
        if (wa != "")
        {
            // the chat opened is marked as sent (the desk presses Invio in WhatsApp); undone by hand
            acts.Add(BA("WhatsApp", async () => { WaitRows.Web(wa); if (!sent && writer) await Mark(id, true, isThanks); }, !sent && (isThanks || r.Bo("daAvvisare")) ? "Primary" : null));
            if (writer && sent) acts.Add(BA(isThanks ? "Non mandato" : "Segna da avvisare", () => Mark(id, false, isThanks), "Ghost"));
            if (writer && !sent && isThanks) acts.Add(BA("Mandato a mano", () => Mark(id, true, true), "Ghost"));
        }
        else if (writer && !isThanks) acts.Add(sent ? BA("Segna da avvisare", () => Mark(id, false, false), "Ghost") : BA("Avvisato", () => Mark(id, true, false)));
        if (!isThanks) acts.Add(B("Prenotazione", () => Main.ShowPage(new BookingDetailPage(id)), "Ghost"));
        var line = WaitRows.Line(first, who, state, acts); if (sent) line.Opacity = .72;
        return line;
    }

    void Draw(JsonNode d)
    {
        var single = d.Bo("singolo"); var today = DateOnly.Parse(d.S("oggi")); var writer = d.Bo("scrive"); var manage = Can("users.manage"); var days = d.A("giorni").ToList();
        string Iso(string k) => d.S(k);
        var actions = new List<FrameworkElement> { Go(single ? "‹ Giorno prima" : "‹ Settimana prima", () => { if (single) day = Iso("prima"); else from = Iso("prima"); }) };
        if (single || DateOnly.Parse(days[0].S("data")) != today) actions.Add(Go("Da oggi", () => { day = ""; from = ""; }));
        actions.Add(Go(single ? "Giorno dopo ›" : "Settimana dopo ›", () => { if (single) day = Iso("dopo"); else from = Iso("dopo"); }));
        var heading = F.Heading(null, "Promemoria WhatsApp", null, actions.ToArray());
        ((StackPanel)heading.Children[^1]).Children.Add(WaitRows.Para(14, "Gli appuntamenti dei prossimi giorni, ognuno con WhatsApp già aperto sul suo messaggio: premi WhatsApp, poi Invio nella chat. Il programma segna chi hai avvisato. Il testo si cambia in ",
            manage ? ((string, Action))("Configurazione promemoria", () => Main.Show("promemoria")) : "Configurazione promemoria", "."));
        var page = Col(heading);
        var todo = days.Sum(x => x.I("daFare")); var all = todo > 0 ? "Da avvisare in questi giorni: " + todo : "Tutti avvisati";
        (string, Action) setup = ("Configura l'attività › Al banco", () => Main.Show("configura"));
        if (d.S("quando") != "") page.Children.Add(WaitRows.Para(13, $"Avvisi: {d.S("quando").ToLower(It)}. Si cambia in ", setup, ". " + all).M(0, 0, 0, 12));
        else if (manage) page.Children.Add(WaitRows.Para(13, "L'avviso in cima alle pagine è spento: si accende in ", setup, ". " + all).M(0, 0, 0, 12));

        // ----- Grazie e recensione
        var tday = DateOnly.Parse(d.S("giornoGrazie")); var review = d.S("recensione") != ""; var rows = d.A("grazie").ToList();
        var label = (tday == today.AddDays(-1) ? "Ieri, " : tday == today ? "Oggi, " : "") + tday.ToString("dddd d MMMM", It);
        var nav = Row(Go("‹", () => thanks = tday.AddDays(-1).ToString("yyyy-MM-dd")));
        if (tday != today) nav.Children.Add(Go("Oggi", () => thanks = today.ToString("yyyy-MM-dd")).M(6, 0, 0, 0));
        nav.Children.Add(Go("›", () => thanks = tday.AddDays(1).ToString("yyyy-MM-dd")).M(6, 0, 0, 0));
        var card = Col(F.Section("Grazie e recensione · " + WaitRows.Cap(label), review ? rows.Count : null, nav));
        if (!review) card.Children.Add(WaitRows.Para(14, "Il giorno dopo la visita, il grazie con il link alla recensione: metti il link in ",
            manage ? ((string, Action))("Configurazione promemoria › Link alla recensione", () => Main.Show("promemoria")) : "Configurazione promemoria", " e qui compaiono le visite di ieri con la chat già scritta.").M(0, 8, 0, 0));
        else if (rows.Count > 0)
        {
            var left = rows.Count(r => r.S("mandato") == "" && r.S("whatsapp") != "");
            card.Children.Add(T("Chi è venuto quel giorno, con la chat già scritta: premi WhatsApp, poi Invio. " + (left > 0 ? "Da ringraziare: " + left : "Tutti ringraziati"), size: 13, color: Res("Muted")).M(0, 4, 0, 0));
            for (int i = 0; i < rows.Count; i++) card.Children.Add(Line(rows[i], i == 0, true, writer));
        }
        else card.Children.Add(T("Nessuna visita quel giorno.", size: 14, color: Res("Muted")).M(0, 8, 0, 0));
        page.Children.Add(F.Card(card));

        // ----- the days
        foreach (var g in days)
        {
            var date = DateOnly.Parse(g.S("data")); var list = g.A("righe").ToList(); var due = g.Bo("daAvvisare");
            var name = WaitRows.Cap((date == today ? "Oggi, " : date == today.AddDays(1) ? "Domani, " : "") + date.ToString("dddd d MMMM", It));
            var head = F.Section(name, list.Count, list.Count > 0 ? T(g.I("daFare") > 0 ? "Da avvisare: " + g.I("daFare") : "Tutti avvisati", size: 13, color: Res("Muted")) : null);
            if (due && g.I("daFare") > 0) ((StackPanel)head.Children[^1]).Children.Add(MainWindow.Pill(WaitRows.Cap(d.S("etichetta"))).M(8, 0, 0, 0));
            var body = Col(head);
            for (int i = 0; i < list.Count; i++) body.Children.Add(Line(list[i], i == 0, false, writer));
            if (list.Count == 0) body.Children.Add(T("Nessun appuntamento confermato.", size: 14, color: Res("Muted")).M(0, 8, 0, 0));
            var box = F.Card(body); if (due) box.BorderBrush = Res("Brand");
            page.Children.Add(box);
        }
        Content = F.Page(1480, page);
    }
}
