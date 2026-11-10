using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/catalog.html: the lists of Configura l'attività (sedi, orari, chiusure, servizi, risorse, varianti,
/// turni, date, prodotti): the tabs, Aggiungi, Modifica, Rimuovi / Elimina, the ready services and the holidays.</summary>
public sealed class CatalogPage : Page
{
    public string Kind = "services";
    int page = 1;
    public override async Task Load() => Draw(await Api.Current.Call("catalogo", new { tipo = Kind, pagina = page }));
    void New(string preset = "") => Main.ShowPage(new CatalogFormPage { Kind = Kind, Preset = preset });
    static TextBlock Muted(string text) { var t = T(text, size: 14, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }

    async Task Remove(string id, bool series = false, bool confirm = false, bool conflicts = false)
    {
        JsonNode r;
        try { r = await Api.Current.Call("catalogo.elimina", new { tipo = Kind, id, serie = series, conferma = confirm, confirm_conflicts = conflicts }); }
        catch (ApiError ex) when (ex is not NeedsLogin and not NeedsConfirm) { Main.Flash(ex.Message, true); return; }
        if (r.Bo("controlla")) { Main.ShowPage(new RemoveCheckPage { Data = r, Go = () => Remove(id, series, true), Back = () => Main.ShowPage(new CatalogPage { Kind = Kind }) }); return; }
        Main.Flash(r.S("testo")); Main.ShowPage(new CatalogPage { Kind = Kind });
    }

    void Draw(JsonNode d)
    {
        var actions = new List<FrameworkElement>();
        if (Kind == "services") actions.Add(B("Importa da Excel", () => Main.ShowPage(new ServicesImportPage())));
        actions.Add(B("Aggiungi", () => New(), "Primary"));
        var body = Col(F.Back("Configura l'attività", () => Main.Show("configura")), F.Heading(null, d.S("titolo"), d.S("aiuto"), actions.ToArray()));
        // the tabs
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var k in d.A("schede"))
        {
            var key = k.S("chiave"); var tab = new RadioButton { Style = Style("Segment"), Content = k.S("titolo"), IsChecked = key == Kind, GroupName = "catalogo" };
            tab.Checked += (_, _) => { if (key != Kind) Main.ShowPage(new CatalogPage { Kind = key }); }; tabs.Children.Add(tab);
        }
        body.Children.Add(tabs);
        if (Kind == "services" && d.Bo("profilo"))
        {
            var go = B("Tipo di attività e sedi", () => Main.Show("configura")); DockPanel.SetDock(go, Dock.Right); go.VerticalAlignment = VerticalAlignment.Top;
            var top = new DockPanel(); top.Children.Add(go);
            top.Children.Add(Col(T("Servizi già pronti per il tipo di attività", "H2"), Muted((d.S("tipoNome") != "" ? $"Ora: {d.S("tipoNome")}. " : "") + "In Configura l'attività scegli tipo e sedi e premi Applica: i servizi giusti si creano da soli in ogni sede. Qui li ritocchi uno per uno")));
            var card = Col(top);
            if (d["modelli"] is JsonArray models && models.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var m in models) { var b = F.Small(B(m.S("nome"), () => New(m.S("id")))); b.ToolTip = m.S("aiuto"); b.Margin = new Thickness(0, 0, 8, 8); wrap.Children.Add(b); }
                card.Children.Add(F.Details("Aggiungi un solo servizio da un modello", wrap, false, true).M(0, 10, 0, 0));
            }
            var box = F.Card(card); box.Background = Res("BrandSoft"); body.Children.Add(box);
        }
        if (Kind == "exceptions")
        {
            var go = B("Segna le festività", () => Main.ShowPage(new HolidaysPage())); DockPanel.SetDock(go, Dock.Right); go.VerticalAlignment = VerticalAlignment.Top;
            var top = new DockPanel(); top.Children.Add(go);
            top.Children.Add(Col(T("Festività italiane", "H2"), Muted("Capodanno, Pasqua e Pasquetta, 25 aprile, Ferragosto, Natale e le altre, più il santo patrono: chiuse in un clic per l'anno che scegli. Quelle in cui lavori le lasci senza spunta")));
            var box = F.Card(top); box.Background = Res("BrandSoft"); body.Children.Add(box);
        }
        // the rows
        var list = Col(); var rows = d.A("righe").ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var id = r.S("id"); var label = r.S("etichetta");
            var text = Col(T(label, size: 14, bold: true)); ((TextBlock)text.Children[0]).TextWrapping = TextWrapping.Wrap;
            foreach (var s in r.A("righe")) text.Children.Add(Muted(s.ToString()).Also13());
            if (r.S("spento") != "") text.Children.Add(MainWindow.Pill(r.S("spento"), "warn").M(0, 4, 0, 0).Left());
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(acts, Dock.Right);
            acts.Children.Add(F.Link("Modifica", () => Main.ShowPage(new CatalogFormPage { Kind = Kind, Id = id })));
            if (r.S("togli") == "fascia") acts.Children.Add(F.Small(BA("Rimuovi", async () => { if (Ask("Rimuovere questa fascia?")) await Remove(id); }, "Ghost")).M(10, 0, 0, 0));
            else if (r.S("togli") == "elimina")
            {
                var del = F.Small(BA("Elimina", async () => { if (Ask($"Eliminare {label}?{(Kind == "locations" ? " Con la sede vanno anche i suoi servizi e le sue risorse." : "")} Se ci sono prenotazioni collegate viene solo disattivato e le prenotazioni restano.")) await Remove(id); }, "Ghost")); del.Foreground = Res("Danger"); acts.Children.Add(del.M(10, 0, 0, 0));
                if (Kind == "occurrences" && r.Bo("serie")) { var all = F.Small(BA("Elimina da qui in poi", async () => { if (Ask("Eliminare questa data e le successive della serie?")) await Remove(id, true); }, "Ghost")); all.Foreground = Res("Danger"); acts.Children.Add(all.M(10, 0, 0, 0)); }
            }
            var line = new DockPanel(); line.Children.Add(acts);
            if (Kind == "resources")
            {
                var photo = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Res("BrandSoft"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(photo, Dock.Left);
                if (r.S("foto") != "") try { photo.Background = new ImageBrush(SetupPage.PictureOf(Convert.FromBase64String(r.S("foto")))) { Stretch = Stretch.UniformToFill }; } catch (Exception ex) when (ex is FormatException or NotSupportedException) { }
                else { var l = T(label.Length > 0 ? label[..1].ToUpper() : "", size: 16, bold: true, color: Res("Brand")); l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center; photo.Child = l; }
                line.Children.Add(photo);
            }
            line.Children.Add(text);
            list.Children.Add(new Border { Child = line, Padding = new Thickness(0, 14, 0, 14), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1) });
        }
        if (rows.Count == 0) list.Children.Add(Muted("Ancora niente qui. Usa Aggiungi per creare il primo").M(0, 14, 0, 14));
        body.Children.Add(F.Card(list, F.Pagination(d.I("pagina"), d.I("pagine"), async p => { page = p; await Guard(Load); })));
        Content = F.Page(1180, body);
    }
}

static class CatalogBits
{
    public static TextBlock Also13(this TextBlock t) { t.FontSize = 13; return t; }
    public static T Left<T>(this T e) where T : FrameworkElement { e.HorizontalAlignment = HorizontalAlignment.Left; return e; }
}

/// <summary>templates/form.html for the catalog: Nuovo / Modifica, the fields, Salva, Torna all'elenco; when the change
/// leaves bookings to check, the tick that confirms it.</summary>
public sealed class CatalogFormPage : Page
{
    public string Kind = "services", Id = "", Preset = "", Resource = "";
    public override async Task Load() => Draw(await Api.Current.Call("catalogo.modulo", new { tipo = Kind, id = Id, preset = Preset, resource = Resource }));

    void Draw(JsonNode d)
    {
        var form = new FormView(d.A("campi")); form.Changed += form.Layout;
        var extra = d["extra"] != null ? new CatalogExtras(this, d["extra"], form) : null;
        var confirm = new CheckBox { Content = T("Ho verificato le prenotazioni segnalate e confermo la modifica", size: 14), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Salva", Style = Style("Primary") };
        void Back() => Main.ShowPage(new CatalogPage { Kind = Kind });
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var values = form.Values(); extra?.Collect(values);
                var r = await Api.Current.Call("catalogo.salva", new JsonObject { ["tipo"] = Kind, ["id"] = Id, ["preset"] = Preset, ["valori"] = values, ["confirm_conflicts"] = confirm.IsChecked == true });
                Main.Flash(r.S("testo")); Back();
            }
            catch (FormError ex) { form.Errors(ex.Errors); extra?.Errors(ex.Errors); if (ex.Confirm) confirm.Visibility = Visibility.Visible; }
            catch (NeedsLogin) { Shell.Instance?.SignedOut(); }
            catch (ApiError ex) { Main.Flash(ex.Message, true); }
            finally { save.IsEnabled = true; }
        };
        var body = Col(form.Panel);
        if (extra != null) foreach (var e in extra.Sections) body.Children.Add(e);
        body.Children.Add(confirm);
        body.Children.Add(Row(save, F.Link("Torna a " + d.S("elenco"), Back).M(14, 0, 0, 0)).M(0, 18, 0, 0));
        ((TextBlock)((StackPanel)body.Children[^1]).Children[1]).VerticalAlignment = VerticalAlignment.Center;
        var page = Col(F.Heading(d.S("nome"), d.S("titolo"), d.S("descrizione")), F.Card(body));
        if (extra != null) foreach (var e in extra.After) page.Children.Add(e);
        Content = F.Page(1180, page);
    }
}

/// <summary>templates/remove_check.html: the bookings on what is being removed, to call back before.</summary>
public sealed class RemoveCheckPage : Page
{
    public JsonNode Data; public Func<Task> Go; public Action Back;
    public override Task Load()
    {
        var z = new AgendaPage { Main = Main }.Also(Business.S("fuso")); var list = Col(); var rows = Data.A("righe").ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            list.Children.Add(CleanList.Row(i == rows.Count - 1, r.S("nome") != "" ? r.S("nome") : "Senza nome", $"{z.Local(r.S("inizio")):dd/MM/yyyy HH:mm} · {r.S("servizio")}",
                r.S("telefono") != "" ? B("Chiama", () => WaitRows.Web("tel:" + r.S("telefono"))) : null, r.S("whatsapp") != "" ? B("WhatsApp", () => WaitRows.Web(r.S("whatsapp"))) : null,
                B("Prenotazione", () => Main.ShowPage(new BookingDetailPage(r.S("id"))), "Ghost")));
        }
        var go = BA(Data.S("pulsante"), Go, "DangerButton");
        Content = F.Page(1180, Col(F.Heading(null, Data.S("titolo"), Data.S("testo")), F.Card(list), Row(go, F.Link("Annulla", Back).M(14, 0, 0, 0))));
        return Task.CompletedTask;
    }
}

/// <summary>What a form has besides its fields (a resource: what it does and its hours; a service: its questions).
/// Still to come: the forms of services and resources.</summary>
public sealed class CatalogExtras(CatalogFormPage page, JsonNode extra, FormView form)
{
    public List<FrameworkElement> Sections { get; } = new();
    public List<FrameworkElement> After { get; } = new();
    public void Collect(JsonObject values) { }
    public void Errors(Dictionary<string, string> errors) { }
}

/// <summary>templates/holidays.html. Still to come in this build.</summary>
public sealed class HolidaysPage : Page
{
    public override Task Load() { Content = F.Page(1180, Col(F.Back("Chiusure e giorni speciali", () => Main.ShowPage(new CatalogPage { Kind = "exceptions" })), F.Heading(null, "Festività italiane", "Questa pagina arriva con il prossimo pezzo."))); return Task.CompletedTask; }
}
/// <summary>templates/services_import.html. Still to come in this build.</summary>
public sealed class ServicesImportPage : Page
{
    public override Task Load() { Content = F.Page(1180, Col(F.Back("Servizi", () => Main.ShowPage(new CatalogPage { Kind = "services" })), F.Heading(null, "Importa i servizi da Excel", "Questa pagina arriva con il prossimo pezzo."))); return Task.CompletedTask; }
}
/// <summary>templates/allergens.html: Intolleranze e allergie. Still to come in this build.</summary>
public sealed class AllergensPage : Page
{
    public override Task Load() { Content = F.Page(1180, Col(F.Back("Configura l'attività", () => Main.Show("configura")), F.Heading(null, "Intolleranze e allergie", "Questa pagina arriva con il prossimo pezzo."))); return Task.CompletedTask; }
}
