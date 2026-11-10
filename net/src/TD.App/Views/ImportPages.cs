using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>contacts_import.html: Importa clienti: the file, what each column holds, the preview, the import.</summary>
public sealed class ImportClientsPage : Page
{
    string token = ""; List<string> columns; bool ask;
    public override async Task Load() => Draw(await Api.Current.Call("clienti.importa", new { token }));
    async Task Send(string action, string check = "")
    {
        var d = await Api.Current.Call("clienti.importa", new JsonObject { ["azione"] = action, ["token"] = token, ["colonne"] = new JsonArray((columns ?? new()).Select(v => (JsonNode)v).ToArray()), ["ask"] = ask, ["checked"] = check });
        if (d.S("fatto") != "") { Main.Flash(d.S("fatto")); Main.Show("clienti"); return; }
        Draw(d);
    }
    static TextBlock Li(string text) { var t = Prog.Text("• " + text); t.Margin = new Thickness(0, 6, 0, 0); return t; }
    static FrameworkElement Reasons(string title, IEnumerable<JsonNode> items)
    {
        var box = Col(T(title, size: 15, bold: true).M(0, 16, 0, 4));
        foreach (var r in items) { var t = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }; t.Inlines.Add(new System.Windows.Documents.Run(r.S("motivo")) { FontWeight = FontWeights.Bold }); t.Inlines.Add(": " + r.S("righe")); box.Children.Add(t); }
        return box;
    }

    void Draw(JsonNode d)
    {
        var spec = d["spec"]; var file = d["file"]; var consent = d.Bo("consensi"); string title = spec?.S("titolo") ?? "", one = spec?.S("uno") ?? "";
        var page = Col(F.Heading(null, "Importa clienti", "Dal programma che usavi prima: esporta l'anagrafica" + (spec != null ? " e lo storico dei " + title.ToLowerInvariant() : "") + " in Excel o CSV e caricali qui. Prima vedi l'anteprima, poi importi",
            B("Torna ai clienti", () => Main.Show("clienti"))));
        if (d.S("errore") != "") page.Children.Add(F.Notice(d.S("errore"), "danger").M(0, 0, 0, 14));
        if (file == null)
        {
            token = ""; columns = null;
            var pick = BA("Carica e scegli le colonne", async () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Excel o CSV|*.xlsx;*.csv;*.txt" }; if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
                if (new System.IO.FileInfo(dlg.FileName).Length > 20 * 1024 * 1024) { Main.Flash("File troppo grande: al massimo 20 MB.", true); return; }
                Draw(await Api.Current.Call("clienti.importa", new { azione = "upload", nome = System.IO.Path.GetFileName(dlg.FileName), file = Convert.ToBase64String(System.IO.File.ReadAllBytes(dlg.FileName)) }));
            }, "Primary");
            var tips = Col(T("Come funziona", "H2"), Li("Una riga per cliente" + (spec != null ? $", oppure una riga per ogni {one.ToLowerInvariant()} con il cliente accanto" : "") + ". La prima riga sono i titoli delle colonne"),
                Li("Un cliente nuovo serve con il telefono o l'email. Chi c'è già si riconosce dall'email, dal telefono, dal codice cliente o dal nome: la sua scheda prende solo i dati che le mancano, niente viene sovrascritto"));
            if (spec != null) tips.Children.Add(Li("Storico in un file a parte? Importa prima l'anagrafica, poi lo storico: le righe si collegano con il codice cliente, il telefono, l'email o il nome"));
            tips.Children.Add(Li("Le colonne che non hanno un posto nella scheda, come il compleanno o l'indirizzo, finiscono nelle note con il loro titolo"));
            if (consent) tips.Children.Add(Li("Il consenso marketing del file resta da confermare: vale quando il cliente lo conferma dal link nell'email"));
            tips.Children.Add(Li("File .xls del vecchio Excel: aprilo e salvalo come .xlsx o .csv"));
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            var left = F.Card(T("Scegli il file", "H2"), Prog.Muted("File Excel (.xlsx) o CSV, fino a 20 MB").M(0, 8, 0, 0), Prog.Left(pick)); left.VerticalAlignment = VerticalAlignment.Top;
            var right = F.Card(tips); Grid.SetColumn(right, 2); g.Children.Add(left); g.Children.Add(right); page.Children.Add(g);
            Content = F.Page(1180, page); return;
        }
        token = file.S("token"); var plan = file["piano"]; var cols = file.A("colonne").ToList(); var fields = file.A("campi").Select(f => (Id: f.S("id"), Name: f.S("nome"))).ToList();
        columns = cols.Select(c => c.S("campo")).ToList(); ask = file.Bo("ask");
        var change = F.Small(BA("Cambia file", () => Send("reset"), "Ghost")); change.VerticalAlignment = VerticalAlignment.Top;
        var top = new DockPanel(); DockPanel.SetDock(change, Dock.Right); top.Children.Add(change);
        top.Children.Add(Col(T(file.S("nome"), "H2"), Prog.Muted($"{file.S("righe")} righe. Per ogni colonna scegli cosa contiene: le proposte vengono dai titoli")));
        var map = new Grid { Margin = new Thickness(0, 12, 0, 0) }; foreach (var w in new[] { 1.4, 2.2, 1.6 }) map.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Cell(FrameworkElement e, int row, int col) { e.VerticalAlignment = VerticalAlignment.Center; var b = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 7, 12, 7), Child = e }; Grid.SetRow(b, row); Grid.SetColumn(b, col); map.Children.Add(b); }
        map.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var heads = new[] { "Colonna del file", "Primi valori", "Cosa contiene" };
        for (int i = 0; i < 3; i++) Cell(T(heads[i], size: 13, bold: true, color: Res("Muted")), 0, i);
        for (int i = 0; i < cols.Count; i++)
        {
            var n = i; map.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new ComboBox { ItemsSource = fields.Select(f => f.Name).ToList(), SelectedIndex = Math.Max(0, fields.FindIndex(f => f.Id == columns[n])) };
            box.SelectionChanged += (_, _) => columns[n] = fields[Math.Max(0, box.SelectedIndex)].Id;
            Cell(T(cols[i].S("titolo"), size: 14, bold: true), i + 1, 0); Cell(Prog.Muted(cols[i].S("esempi") != "" ? cols[i].S("esempi") : "vuota", 13), i + 1, 1); Cell(box, i + 1, 2);
        }
        var card = Col(top, map);
        if (consent && d.Bo("marketing"))
        {
            var send = new CheckBox { Content = Prog.Text("Manda a chi ha il consenso nel file l'email per confermarlo. Chi non conferma non riceve campagne"), IsChecked = ask, Margin = new Thickness(0, 14, 0, 0) };
            send.Checked += (_, _) => ask = true; send.Unchecked += (_, _) => ask = false; card.Children.Add(send);
        }
        var buttons = Row(BA(plan != null ? "Aggiorna l'anteprima" : "Anteprima", () => Send("preview"), plan != null ? null : "Primary")); buttons.Margin = new Thickness(0, 16, 0, 0);
        var r = plan?["numeri"];
        if (plan != null)
            buttons.Children.Add(BA("Importa", async () =>
            {
                if (Ask($"Importare {r.I("created")} clienti nuovi" + (r.I("treatments") > 0 ? $" e {r.I("treatments")} {title.ToLowerInvariant()}" : "") + "?")) await Send("import", file.S("verificato"));
            }, "Primary").M(10, 0, 0, 0));
        card.Children.Add(buttons); page.Children.Add(F.Card(card));
        if (plan != null)
        {
            var head = new List<string> { "Riga", "Cliente", "Recapiti" }; if (spec != null) head.Add(char.ToUpper(one[0]) + one[1..]);
            var samples = plan.A("esempi").ToList();
            var rows = samples.Select(s =>
            {
                var cells = new List<FrameworkElement> { T(s.S("riga"), size: 14), Col(T(s.S("nome") != "" ? s.S("nome") : "—", size: 14, bold: true), Prog.Muted(s.S("stato"), 13)), Prog.Muted(s.S("telefono") + (s.S("telefono") != "" && s.S("email") != "" ? " · " : "") + s.S("email"), 13) };
                if (spec != null) cells.Add(Prog.Muted(s.S("voce") != "" ? s.S("voce") : "—", 13));
                return (cells.ToArray(), (Action)null);
            }).ToList();
            var preview = Col(T("Anteprima", "H2"), Prog.Muted("Ancora niente è stato scritto. Le prime righe come diventano:").M(0, 6, 0, 0),
                samples.Count > 0 ? Agency.Table(head.ToArray(), spec != null ? new[] { 0.5, 2.0, 2.2, 2.0 } : new[] { 0.5, 2.0, 2.2 }, rows) : Prog.Text("Nessuna riga importabile con queste colonne").M(0, 10, 0, 0));
            if (plan.A("problemi").Any()) preview.Children.Add(Reasons("Righe che non entrano", plan.A("problemi")));
            if (plan.A("scarti").Any()) preview.Children.Add(Reasons($"{title} che non entrano, il cliente sì", plan.A("scarti")));
            var facts = new List<(string, FrameworkElement)> { ("Clienti nuovi", Prog.Text(r.I("created").ToString())), ("Schede già presenti completate", Prog.Text(r.I("updated").ToString())) };
            if (spec != null) { facts.Add((title, Prog.Text(r.I("treatments").ToString()))); if (r.I("same_treatment") > 0) facts.Add(("Già nella scheda, saltati", Prog.Text(r.I("same_treatment").ToString()))); }
            if (consent) facts.Add(("Consensi da confermare", Prog.Text(r.I("consents").ToString())));
            facts.Add(("Righe che non entrano", Prog.Text(r.I("problems").ToString())));
            if (r.I("issues") > 0) facts.Add(($"{title} che non entrano", Prog.Text(r.I("issues").ToString())));
            var what = Col(T("Cosa succede", "H2"), Prog.Facts(facts.ToArray()));
            if (r.I("too_old") > 0) what.Children.Add(F.Notice(Prog.Text($"{r.I("too_old")} {title.ToLowerInvariant()} sono prima del {plan.S("dal")} e non vengono importati: la conservazione dell'attività ({d.S("conservazione")} giorni) li cancellerebbe stanotte. Per tenerli alza la conservazione in Impostazioni, fino a 3650 giorni, e rifai l'anteprima."), "warning").M(0, 12, 0, 0));
            if (r.I("bad_phone") > 0 || r.I("bad_email") > 0)
                what.Children.Add(F.Notice(Prog.Text((r.I("bad_phone") > 0 ? $"{r.I("bad_phone")} telefoni" : "") + (r.I("bad_phone") > 0 && r.I("bad_email") > 0 ? " e " : "") + (r.I("bad_email") > 0 ? $"{r.I("bad_email")} email" : "") + " non validi: restano fuori, il resto della riga entra."), "warning").M(0, 12, 0, 0));
            if (r.I("bad_fiscal") > 0) what.Children.Add(F.Notice(Prog.Text($"{r.I("bad_fiscal")} dati fiscali non validi (codice fiscale, partita IVA, CAP, provincia, SDI o PEC): restano fuori, il resto della riga entra."), "warning").M(0, 12, 0, 0));
            if (r.I("consent_no_email") > 0 || r.I("consent_revoked") > 0 || r.I("consent_known") > 0)
            {
                var parts = new List<string>(); if (r.I("consent_known") > 0) parts.Add($"{r.I("consent_known")} già confermati o già importati"); if (r.I("consent_revoked") > 0) parts.Add($"{r.I("consent_revoked")} revocati o esclusi dagli invii"); if (r.I("consent_no_email") > 0) parts.Add($"{r.I("consent_no_email")} senza email");
                what.Children.Add(F.Notice(Prog.Text("Consensi non toccati: " + string.Join(", ", parts) + ".")).M(0, 12, 0, 0));
            }
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
            var right = F.Card(what); right.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(right, 2); g.Children.Add(F.Card(preview)); g.Children.Add(right); page.Children.Add(g);
        }
        Content = F.Page(1180, page);
    }
}
