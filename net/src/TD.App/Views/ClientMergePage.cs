using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/contact_merge.html: two cards of the same person made one: the other card searched, then field by
/// field which value stays, which card is the main one, and UNISCI to confirm.</summary>
public sealed class ClientMergePage(string id) : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    string q = "", other = "", error = "";

    public override async Task Load() => Draw(await Api.Current.Call("cliente.unisci.dati", new { id, con = other, q }));

    void Draw(JsonNode d)
    {
        var page = Col(F.Back($"{d.S("nome")} {d.S("cognome")}".Trim(), () => Main.ShowPage(new ClientPage(id))),
            F.Heading("Clienti", "Unisci due schede", "Quando la stessa persona ha due schede: scegli quali dati tenere, tutto il resto finisce in una scheda sola. Non si può annullare"));
        if (error != "") { page.Children.Add(F.Notice(error, "error")); error = ""; }
        if (d["altra"] == null)
        {
            var search = Box(q); search.Tag = "Nome, cognome, telefono o email"; AgendaPage.Hint(search);
            async Task Go() { q = search.Text.Trim(); if (q != "") await Load(); }
            search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Guard(Go); };
            var go = BA("Cerca", Go); go.VerticalAlignment = VerticalAlignment.Bottom; go.Margin = new Thickness(10, 0, 0, 0);
            var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(go, Dock.Right); filters.Children.Add(go); filters.Children.Add(F.Field("Cerca l'altra scheda", search));
            var list = Col();
            if (d["trovati"] is JsonArray found)
            {
                foreach (var c in found)
                {
                    var cid = c.S("id");
                    var g = new Grid { Background = Brushes.Transparent };
                    foreach (var w in new[] { new GridLength(40), new GridLength(14), new GridLength(1, GridUnitType.Star), new GridLength(14), new GridLength(140) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                    g.Children.Add(F.Avatar(c.S("nome"), 40));
                    var main = Col(T($"{c.S("nome")} {c.S("cognome")}", size: 14, bold: true), T(string.Join(" · ", new[] { c.S("telefono"), c.S("email") }.Where(x => x != "")), size: 13, color: Res("Muted"))); main.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(main, 2); g.Children.Add(main);
                    var last = Col(T("Cliente dal", size: 12, color: Res("Muted")), T(DateOnly.Parse(c.S("creato")).ToString("dd/MM/yyyy"), size: 14)); Grid.SetColumn(last, 4); g.Children.Add(last);
                    var row = new Border { Child = g, Padding = new Thickness(4, 12, 4, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                    row.MouseEnter += (_, _) => row.Background = Res("BrandSoft"); row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                    row.MouseLeftButtonUp += async (_, _) => { other = cid; await Guard(Load); };
                    list.Children.Add(row);
                }
                if (found.Count == 0) { var t = T($"Nessun'altra scheda trovata con «{q}»", size: 14, color: Res("Muted")); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(20, 40, 20, 40); list.Children.Add(t); }
            }
            page.Children.Add(F.Card(T("Con quale scheda?", "H2"), filters, list));
            Content = F.Page(1480, page); return;
        }
        // ----- the two cards side by side
        var mine = d["questa"]; var theirs = d["altra"];
        var table = new Grid(); table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); table.ColumnDefinitions.Add(new ColumnDefinition()); table.ColumnDefinitions.Add(new ColumnDefinition());
        void Row3(UIElement head, UIElement a, UIElement b, bool header = false)
        {
            var r = table.RowDefinitions.Count; table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var i = 0;
            foreach (var e in new[] { head, a, b })
            {
                var cell = new Border { Child = e, Padding = new Thickness(10, 10, 10, 10), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, i++); table.Children.Add(cell);
            }
        }
        TextBlock Th(string s) => T(s, size: 13, bold: true, color: Res("Muted"));
        Row3(null, Th("Questa scheda"), Th("L'altra scheda"), true);
        RadioButton Radio(string group, UIElement content, bool on) => new() { GroupName = group + GetHashCode(), Content = content, IsChecked = on };
        TextBlock Keep(string text, JsonNode k)
        {
            var t = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; t.Inlines.Add(text + " ");
            var small = new System.Windows.Documents.Run($"cliente dal {DateOnly.Parse(k.S("creato")):dd/MM/yyyy}, {k.I("prenotazioni")} prenotazion{(k.I("prenotazioni") == 1 ? "e" : "i")}") { FontSize = 13 }; small.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
            t.Inlines.Add(small); return t;
        }
        var mainThis = Radio("main", Keep("Tieni questa", mine), true); var mainOther = Radio("main", Keep("Tieni l'altra", theirs), false);
        Row3(T("Scheda principale", size: 14, bold: true), mainThis, mainOther);
        var picks = new Dictionary<string, RadioButton>();
        string Shown(string key, string v) => v == "" ? "—" : key == "birthday" && DateOnly.TryParse(v, out var b) ? b.ToString("d MMMM", It) + " " + v[..4] : v;
        foreach (var r in d.A("righe"))
        {
            var key = r.S("chiave");
            var a = Radio("keep_" + key, T(Shown(key, r.S("mio")), size: 14), r.S("scelta") == "this"); var b = Radio("keep_" + key, T(Shown(key, r.S("suo")), size: 14), r.S("scelta") == "other");
            picks[key] = b; Row3(T(r.S("nome"), size: 14, bold: true), a, b);
        }
        var p1 = F.Status(mine.S("consenso")); p1.HorizontalAlignment = HorizontalAlignment.Left; var p2 = F.Status(theirs.S("consenso")); p2.HorizontalAlignment = HorizontalAlignment.Left;
        Row3(T("Comunicazioni", size: 14, bold: true), p1, p2);
        var confirm = Box("");
        var merge = BA("Unisci le schede", async () =>
        {
            if (!Ask("Unire le due schede? Non si può annullare.")) return;
            var keep = new JsonObject(); foreach (var (k, b) in picks) keep[k] = b.IsChecked == true ? "other" : "this";
            try
            {
                var r = await Api.Current.Call("cliente.unisci", new JsonObject { ["id"] = id, ["con"] = other, ["main"] = mainOther.IsChecked == true ? "other" : "this", ["keep"] = keep, ["conferma"] = confirm.Text });
                Main.Flash(r.S("testo")); Main.ShowPage(new ClientPage(r.S("id")));
            }
            catch (NeedsLogin) { throw; }
            catch (ApiError ex) { error = ex.Message; await Load(); }
        }, "DangerButton");
        var another = F.Link("Scegli un'altra scheda", async () => { other = ""; await Guard(Load); }); another.VerticalAlignment = VerticalAlignment.Center; another.Margin = new Thickness(14, 0, 0, 0);
        page.Children.Add(F.Card(table,
            T($"Note, etichette, prenotazioni, scheda {(d.Bo("scheda") ? "trattamenti " : "")}e lista d'attesa delle due schede finiscono nella principale; VIP e Non prenotabile online restano se una delle due li ha. Il consenso alle comunicazioni segue l'email che tieni: la conferma è legata a quella casella, quindi non passa a un'altra. Lo storico dei consensi delle due schede resta tutto", size: 14, color: Res("Muted")).M(0, 14, 0, 0),
            F.Label("Scrivi UNISCI per confermare"), confirm, Row(merge, another).M(0, 16, 0, 0)));
        Content = F.Page(1480, page);
    }
}
