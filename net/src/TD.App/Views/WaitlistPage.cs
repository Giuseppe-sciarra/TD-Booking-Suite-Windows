using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/waitlist.html: Lista d'attesa: who waits for a place that is full today, with what freed for each;
/// Chiama, WhatsApp, Prenota, Fatta, Togli; on the right «Aggiungi alla lista».</summary>
public sealed class WaitlistPage : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    /// <summary>?cliente=: the client already chosen (from his card).</summary>
    public string Client = "";
    JsonObject form = new(); string error = ""; AgendaPage z;
    static void Web(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }.Also(Business.S("fuso"));
        Draw(await Api.Current.Call("attesa", new { cliente = Client }));
    }
    async Task Act(object args) { var r = await Api.Current.Call("attesa.azione", args); Main.Flash(r.S("testo")); await Load(); }

    void Draw(JsonNode d)
    {
        var write = Can("booking.write"); var many = d.Bo("piuSedi");
        var first = Box(form.S("first_name")); first.MaxLength = 100;
        var heading = F.Heading(null, "Lista d'attesa", "Chi aspetta un posto che oggi è pieno. Quando se ne libera uno" + (d.Bo("coda") ? " lo teniamo per il primo della lista e gli scriviamo, poi per il successivo" : " lo scriviamo a tutti quelli che lo aspettano e lo prende chi prenota per primo") + ". Chi non ha l'email lo chiamate voi",
            write ? B("Aggiungi", () => first.Focus(), "Primary") : null);
        var page = Col(heading); if (error != "") { page.Children.Add(F.Notice(error, "error")); error = ""; }
        // ----- In attesa
        var list = Col(); var rows = d.A("righe").ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i]; var id = e.S("id"); var free = e.A("liberi").Select(f => z.Local(f.ToString())).ToList();
            Border State(string text, string kind) => new() { Background = Res(kind == "free" ? "OkSoft" : kind == "held" ? "WarnSoft" : "BrandSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = T(text, size: 12.5, bold: true, color: Res(kind == "free" ? "Ok" : kind == "held" ? "Warn" : "Muted")) };
            var state = e.S("tenuto") != "" ? State($"Posto tenuto fino alle {z.Local(e.S("tenuto")):HH:mm}", "held") : free.Count > 0 ? State($"Si è liberato: {free[0].ToString("ddd d/MM", It)} {string.Join(", ", free.Select(f => f.ToString("HH:mm")))}", "free") : State("In attesa", "");
            var who = Col(T($"{e.S("nome")} {e.S("cognome")}".Trim(), size: 14, bold: true), T(string.Join(" · ", new[] { e.S("telefono"), e.S("email") }.Where(x => x != "")), size: 13, color: Res("Muted")), state);
            var a = DateOnly.Parse(e.S("dal")); var b = DateOnly.Parse(e.S("al"));
            var when = (e.S("tipo") == "day" || a != b ? $"dal {a:d/MM} al {b:d/MM}" : a.ToString("dddd d MMMM", It))
                + (e.S("dalle") != "" || e.S("alle") != "" ? "," + (e.S("dalle") != "" ? " dalle " + e.S("dalle") : "") + (e.S("alle") != "" ? " alle " + e.S("alle") : "") : "")
                + (e.S("operatore") != "" ? " · con " + e.S("operatore") : "") + (e.I("persone") > 1 ? $" · {e.I("persone")} persone" : "");
            var what = Col(T(e.S("servizio") + (many ? " · " + e.S("sede") : ""), size: 14, bold: true), T(when, size: 13, color: Res("Muted")), T($"{(e.Bo("online") ? "Dalla pagina online" : "Aggiunto al banco")} il {z.Local(e.S("creato")):dd/MM HH:mm}", size: 13, color: Res("Muted")));
            var acts = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            void Add(Button btn) { btn.Margin = new Thickness(6, 0, 0, 0); acts.Children.Add(F.Small(btn)); }
            if (e.S("telefono") != "" && !e.Bo("mascherato")) Add(B("Chiama", () => Web("tel:" + e.S("telefono"))));
            if (e.S("whatsapp") != "") Add(B("WhatsApp", () => Web(e.S("whatsapp"))));
            if (write)
            {
                Add(B("Prenota", () => Main.ShowPage(new BookingPage { PrefillClient = e.Bo("mascherato") ? "" : e.S("cliente"), PrefillDate = (free.Count > 0 ? DateOnly.FromDateTime(free[0]) : a).ToString("yyyy-MM-dd"), PrefillResource = e.S("operatoreId"), PrefillService = e.S("servizioId") })));
                Add(BA("Fatta", () => Act(new { azione = "done", entry = id }), "Ghost"));
                var rm = BA("Togli", async () => { if (Ask($"Togliere {e.S("nome")} dalla lista d'attesa?")) await Act(new { azione = "remove", entry = id }); }, "Ghost"); rm.Foreground = Res("Danger"); Add(rm);
            }
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(what, 2); Grid.SetColumn(acts, 4); g.Children.Add(who); g.Children.Add(what); g.Children.Add(acts);
            list.Children.Add(new Border { Child = g, Padding = new Thickness(0, 14, 0, 14), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) });
        }
        if (rows.Count == 0) list.Children.Add(F.EmptyState("Nessuno in lista d'attesa", T("Quando un giorno è pieno, la pagina online propone «Avvisami se si libera». Al banco li aggiungi qui", size: 13, color: Res("Muted"))));
        var left = F.Card(F.Section("In attesa", rows.Count), list);
        // ----- Aggiungi alla lista
        var aside = Col();
        if (write)
        {
            var body = Col(T("Aggiungi alla lista", "H2"), T("Per chi chiama o passa e trova pieno. Gli scriviamo se ha l'email, altrimenti lo vedete qui", size: 14, color: Res("Muted")));
            var last = Box(form.S("last_name")); last.MaxLength = 100; var phone = Box(form.S("phone")); phone.MaxLength = 30; phone.Tag = "333 123 4567"; AgendaPage.Hint(phone); var email = Box(form.S("email")); email.MaxLength = 254;
            var client = d["cliente"];
            if (client != null)
            {
                var change = F.Link("Cambia", async () => { Client = ""; await Guard(Load); }); change.Margin = new Thickness(12, 0, 0, 0);
                body.Children.Add(new Border { Background = Res("OkSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 10, 0, 0),
                    Child = Row(T($"Cliente: {client.S("nome")} {client.S("cognome")}" + (client.S("telefono") != "" ? " · " + client.S("telefono") : ""), size: 14, bold: true), change) });
            }
            else foreach (var e in new UIElement[] { F.Label("Nome"), first, F.Label("Cognome"), last, F.Label("Telefono"), phone, F.Label("Email"), email }) body.Children.Add(e);
            var service = new ComboBox { ItemsSource = d.A("servizi").Select(s => new ComboBoxItem { Content = s.S("nome") + (many ? $" ({s.S("sede")})" : ""), Tag = s.S("id") }).ToList() };
            service.SelectedItem = service.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == form.S("service")) ?? service.Items.Cast<ComboBoxItem>().FirstOrDefault();
            var ops = d.A("operatori").ToList();
            var resource = new ComboBox { ItemsSource = ops.Select(r => new ComboBoxItem { Content = r.S("nome"), Tag = r.S("id") }).Prepend(new ComboBoxItem { Content = "Chiunque sia libero", Tag = "" }).ToList() };
            resource.SelectedItem = resource.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == form.S("resource")) ?? resource.Items[0];
            var today = DateTime.Parse(d.S("oggi"));
            var from = new DatePicker { DisplayDateStart = today, SelectedDate = DateTime.TryParse(form.S("first"), out var f0) ? f0 : null }; var to = new DatePicker { DisplayDateStart = today, SelectedDate = DateTime.TryParse(form.S("last"), out var l0) ? l0 : null };
            var tf = Box(form.S("time_from")); tf.Tag = "09:00"; AgendaPage.Hint(tf); var tt = Box(form.S("time_to")); tt.Tag = "13:00"; AgendaPage.Hint(tt); var party = Box(form.S("party") != "" ? form.S("party") : "1");
            body.Children.Add(F.Label("Servizio")); body.Children.Add(service);
            if (ops.Count > 0) { body.Children.Add(F.Label("Con", "(facoltativo)")); body.Children.Add(resource); }
            body.Children.Add(F.Grid2(F.Field("Dal", from), F.Field("Al", to, "(facoltativo)"), F.Field("Dalle", tf, "(facoltativo)"), F.Field("Alle", tt), F.Field("Persone", party), null));
            body.Children.Add(T("Per un soggiorno, Dal e Al sono arrivo e partenza", size: 13, color: Res("Muted")).M(0, 8, 0, 0));
            var add = new Button { Content = "Aggiungi alla lista", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
            add.Click += async (_, _) =>
            {
                form = new JsonObject
                {
                    ["azione"] = "add", ["contact"] = client?.S("id") ?? "", ["first_name"] = first.Text, ["last_name"] = last.Text, ["phone"] = phone.Text, ["email"] = email.Text, ["service"] = (string)(service.SelectedItem as ComboBoxItem)?.Tag ?? "",
                    ["resource"] = (string)(resource.SelectedItem as ComboBoxItem)?.Tag ?? "", ["first"] = from.SelectedDate?.ToString("yyyy-MM-dd") ?? "", ["last"] = to.SelectedDate?.ToString("yyyy-MM-dd") ?? "", ["time_from"] = tf.Text.Trim(), ["time_to"] = tt.Text.Trim(), ["party"] = party.Text.Trim(),
                };
                add.IsEnabled = false;
                try { var r = await Api.Current.Call("attesa.azione", form.DeepClone()); Main.Flash(r.S("testo")); form = new(); Client = ""; await Guard(Load); }
                catch (NeedsLogin) { Shell.Instance?.SignedOut(); }
                catch (ApiError ex) { error = ex.Message; await Guard(Load); }
                finally { add.IsEnabled = true; }
            };
            body.Children.Add(add);
            aside.Children.Add(new Border { Style = Style("Card"), Child = body });
        }
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetColumn(aside, 2); grid.Children.Add(left); grid.Children.Add(aside);
        page.Children.Add(grid);
        Content = F.Page(1480, page);
    }
}
