using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/contacts.html: the address book: search, tag and VIP filters, each client with visits and last
/// visit, the pages; on the right Nuovo cliente.</summary>
public sealed class ClientsPage : Page
{
    string q = "", tag = ""; bool vip; int page = 1;
    JsonObject form = new(); string error = "";

    public override async Task Load()
    {
        var d = await Api.Current.Call("clienti.elenco", new { q, tag, vip, pagina = page });
        Draw(d);
    }

    void Draw(JsonNode d)
    {
        var heading = F.Heading(null, "Clienti", "La rubrica dell'attività: apri un cliente per vedere tutto quello che ha prenotato, le note e le prossime prenotazioni",
            Can("pii.read") && Can("notes.read") && Can("booking.write") ? B("Importa", () => Main.ShowPage(new Placeholder("Importa clienti"))) : null,
            Can("export.marketing") ? BA("Esporta", async () => Downloads.Save("td-booking-suite-contacts.csv", await Api.Current.Bytes("esporta", new { kind = "contacts" }))) : null);
        // ----- filters
        var search = Box(q); search.Tag = "Nome, cognome, telefono o email"; AgendaPage.Hint(search);
        var tags = d.A("etichette").Select(t => t.ToString()).ToList();
        var pick = new ComboBox { ItemsSource = tags.Prepend("Tutte").ToList(), SelectedItem = tag == "" ? "Tutte" : tag, MinWidth = 150 };
        var only = new CheckBox { Content = "Solo VIP", IsChecked = vip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 9) };
        async Task Go() { q = search.Text.Trim(); tag = pick.SelectedItem as string is "Tutte" or null ? "" : (string)pick.SelectedItem; vip = only.IsChecked == true; page = 1; await Load(); }
        search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Guard(Go); };
        var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var right = Row(tags.Count > 0 ? F.Field("Etichetta", pick).M(10, 0, 0, 0) : null, only.M(10, 0, 0, 0), BA("Cerca", Go).M(10, 0, 0, 0),
            d.Bo("filtrato") ? F.Link("Mostra tutti", async () => { q = tag = ""; vip = false; page = 1; await Guard(Load); }).M(10, 0, 0, 12) : null);
        foreach (FrameworkElement e in right.Children) e.VerticalAlignment = VerticalAlignment.Bottom;
        DockPanel.SetDock(right, Dock.Right); filters.Children.Add(right); filters.Children.Add(F.Field("Cerca un cliente", search));
        // ----- rows
        var list = Col(); var rows = d.A("righe").ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var id = r.S("id");
            var g = new Grid { Background = Brushes.Transparent };
            foreach (var w in new[] { new GridLength(40), new GridLength(14), new GridLength(1, GridUnitType.Star), new GridLength(14), new GridLength(70), new GridLength(14), new GridLength(140) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            void Put(FrameworkElement e, int col) { e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, col); g.Children.Add(e); }
            Put(F.Avatar(r.S("nome"), 40), 0);
            var contacts = string.Join(" · ", new[] { r.S("telefono"), r.S("email") }.Where(x => x != ""));
            var small = T(contacts, size: 13, color: Res("Muted")); small.TextTrimming = TextTrimming.CharacterEllipsis;
            Put(Col(Row(r.Bo("vip") ? F.Mark("vip", "star", "", "Cliente VIP") : null, T($"{r.S("nome")} {r.S("cognome")}", size: 14, bold: true)), small), 2);
            var n = T(r.S("visite"), size: 18, bold: true); n.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"]; n.HorizontalAlignment = HorizontalAlignment.Center;
            var word = T(r.I("visite") == 1 ? "visita" : "visite", size: 12, color: Res("Muted")); word.HorizontalAlignment = HorizontalAlignment.Center;
            Put(Col(n, word), 4);
            var last = Col();
            if (r.S("ultima") != "") { last.Children.Add(T("Ultima", size: 12, color: Res("Muted"))); last.Children.Add(T(DateOnly.Parse(r.S("ultima")).ToString("dd/MM/yyyy"), size: 14)); }
            else last.Children.Add(T("Ancora nessuna visita", size: 12, color: Res("Muted")));
            if (r.Bo("inArrivo")) { var p = MainWindow.Pill("In arrivo", "ok"); p.HorizontalAlignment = HorizontalAlignment.Left; p.Margin = new Thickness(0, 4, 0, 0); last.Children.Add(p); }
            Put(last, 6);
            var row = new Border { Child = g, Padding = new Thickness(4, 12, 4, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.MouseEnter += (_, _) => row.Background = Res("BrandSoft"); row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => Main.ShowPage(new ClientPage(id));
            list.Children.Add(row);
        }
        if (rows.Count == 0)
        {
            var t = T(q != "" ? $"Nessun cliente trovato con «{q}»" : d.Bo("filtrato") ? "Nessun cliente con questi filtri" : "I clienti compaiono con le prime prenotazioni, oppure aggiungili qui accanto", size: 14, color: Res("Muted"));
            t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(20, 40, 20, 40); list.Children.Add(t);
        }
        var left = F.Card(filters, list, F.Pagination(d.I("pagina"), d.I("pagine"), async p => { page = p; await Guard(Load); }));
        // ----- Nuovo cliente
        var aside = Col();
        if (Can("booking.write"))
        {
            var first = Box(form.S("first_name")); first.MaxLength = 100; var lastName = Box(form.S("last_name")); lastName.MaxLength = 100;
            var phone = Box(form.S("phone")); phone.MaxLength = 30; phone.Tag = "333 123 4567"; AgendaPage.Hint(phone); var email = Box(form.S("email")); email.MaxLength = 254;
            var add = new Button { Content = "Aggiungi il cliente", Style = Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
            add.Click += async (_, _) =>
            {
                form = new JsonObject { ["first_name"] = first.Text, ["last_name"] = lastName.Text, ["phone"] = phone.Text, ["email"] = email.Text };
                try
                {
                    var r = await Api.Current.Call("clienti.nuovo", form.DeepClone());
                    if (!r.Bo("nuovo")) Main.Flash("Questo cliente c'era già: ecco la sua scheda.");
                    Main.ShowPage(new ClientPage(r.S("id")));
                }
                catch (NeedsLogin) { Shell.Instance?.SignedOut(); }
                catch (ApiError ex) { error = ex.Message; await Guard(Load); }
            };
            aside.Children.Add(F.Card(T("Nuovo cliente", "H2"), T("Per chi chiama o passa in negozio. Serve il telefono o l'email", size: 14, color: Res("Muted")), error != "" ? F.Notice(error, "error") : null,
                F.Label("Nome"), first, F.Label("Cognome"), lastName, F.Label("Telefono"), phone, F.Label("Email"), email, add));
            error = "";
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetColumn(aside, 2); grid.Children.Add(left); grid.Children.Add(aside);
        Content = F.Page(1480, heading, grid);
    }
}
