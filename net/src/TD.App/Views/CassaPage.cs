using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/cassa.html + the Cassa part of app.js: the client, the services and products as tiles with the
/// operators' bar, the bill (rows, discounts, «Non nello scontrino», gift card, payment and its split, codice fiscale and
/// lotteria, copy by email), Paga in cassa and Parcheggia; then the Parcheggio (stand-by, Da pagare), the receipts of
/// today, the daily closure, the summary in PDF and the annullo of another receipt.
/// The bill stays as it was left (app.js tillDraft): kept on this PC per register, also after closing the program.</summary>
public sealed class CassaPage : Page
{
    static readonly CultureInfo It = AgendaPage.It;
    /// <summary>How the Cassa is opened: on a booking, with suspended bills, a quote, a gift card to sell, a client.</summary>
    public string Booking = "", Quote = "", GiftAmount = "", GiftExpires = "", Client = "";
    public List<string> Suspended = new();
    /// <summary>From the Stand-by page: this bill of the Parcheggio is taken back as the Cassa opens, on its register.</summary>
    public string ResumeStandby = "", ResumeSede = "";

    sealed class Line
    {
        public TextBox Desc = Box(""), Qty = Box("1"), Price = Box(""), Off = Box("");
        public string Staff = "", Svc = ""; public bool Park;
        public Border Box; public TextBlock Who; public ToggleButton ParkButton; public FrameworkElement Minus, Plus;
    }
    string sede = ""; JsonNode d; AgendaPage z; bool built;
    readonly List<Line> lines = new(); Line current; string active = "", bookingId = ""; List<string> taken = new(); decimal credit; string key = "";
    StackPanel rowsBox, low, found, extra; ContentControl top;
    TextBox client, discount, giftCode, giftUse, cf, lottery, standbyNote, parkNote, giftValue, search; DatePicker giftExpires;
    string contact = ""; CheckBox copy; TextBlock total, giftTotal, parkedTotal, giftInfo; Button history;
    readonly Dictionary<string, RadioButton> pay = new(); readonly Dictionary<string, TextBox> split = new();
    readonly List<(Button Tile, string Svc, string Desc, string Label, TextBlock Name, TextBlock Price, Border Flag)> tiles = new(); readonly List<(Button B, string Name, int Color)> ops = new();
    Expander giftPay, splitBox, fiscal, parkAll; Border giftBox; System.Windows.Threading.DispatcherTimer poll, lookup, typing;

    DateTime Local(string iso) => z.Local(iso);
    static decimal Num(string v) => decimal.TryParse((v ?? "").Replace(",", ".").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : 0;
    static string Eur(decimal v) => Math.Max(0, v).ToString("0.00", CultureInfo.InvariantCulture).Replace(".", ",") + " €";
    static string Euro(string v) => Num(v).ToString("#,##0.00", It) + " €";

    // ---------- the bill kept on this PC (localStorage tdbs-till-<sede>)
    static string DraftFile(string sede) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDBookingSuite", $"cassa-{Api.Current.Business}-{sede}.json");
    static JsonObject ReadDraft(string sede) { try { return File.Exists(DraftFile(sede)) ? JsonNode.Parse(File.ReadAllText(DraftFile(sede))) as JsonObject : null; } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; } }
    static void DropDraft(string sede) { try { File.Delete(DraftFile(sede)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    JsonObject Draft()
    {
        var f = new JsonObject
        {
            ["till-client"] = client?.Text ?? "", ["contact"] = contact, ["till-discount"] = discount.Text, ["till-gift-code"] = giftCode.Text, ["till-gift-use"] = giftUse.Text, ["till-cf"] = cf.Text, ["till-lottery"] = lottery.Text,
            ["copy"] = copy.IsChecked == true, ["till-park-note"] = parkNote.Text, ["gift-amount"] = giftValue.Text, ["gift-expires"] = giftExpires.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
        };
        foreach (var (k, r) in pay) f["pay=" + k] = r.IsChecked == true;
        foreach (var (k, t) in split) f["pay-" + k] = t.Text;
        return new JsonObject
        {
            ["lines"] = new JsonArray(lines.Where(l => l.Desc.Text != "" || l.Price.Text != "").Select(l => (JsonNode)new JsonObject { ["desc"] = l.Desc.Text, ["staff"] = l.Staff, ["qty"] = l.Qty.Text, ["price"] = l.Price.Text, ["off"] = l.Off.Text, ["park"] = l.Park ? "1" : "", ["svc"] = l.Svc }).ToArray()),
            ["fields"] = f, ["booking"] = bookingId, ["parked"] = new JsonArray(taken.Select(x => (JsonNode)x).ToArray()), ["active"] = active,
        };
    }
    /// <summary>app.js typed(): something was put in the bill, beyond what an empty Cassa has.</summary>
    static bool Typed(JsonObject x) =>
        (x["lines"] as JsonArray)?.Count > 0 || x.S("booking") != "" || (x["parked"] as JsonArray)?.Count > 0 ||
        (x["fields"] as JsonObject ?? new()).Any(p => p.Value is JsonValue v && (v.TryGetValue<bool>(out var b) ? b && !p.Key.StartsWith("pay=") : v.ToString() != "" && !p.Key.StartsWith("gift-expires")));
    void Save()
    {
        if (!built || sede == "") return;
        try
        {
            var x = Draft();
            if (Typed(x)) { Directory.CreateDirectory(Path.GetDirectoryName(DraftFile(sede))); File.WriteAllText(DraftFile(sede), x.ToJsonString()); } else DropDraft(sede);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }.Also(Business.S("fuso"));
        Unloaded += (_, _) => { Save(); poll?.Stop(); };
        if (ResumeStandby != "")
        {
            // the bill left open on that register goes to the Parcheggio, it is not lost
            sede = ResumeSede; var left = ReadDraft(sede); var id = ResumeStandby; ResumeStandby = "";
            var r = await Api.Current.Call("cassa.azione", new JsonObject { ["sede"] = sede, ["azione"] = "resume", ["standby_id"] = id, ["swap"] = left != null && Typed(left) ? left : null });
            if (r.S("testo") != "") Main.Flash(r.S("testo")); DropDraft(sede);
            await Reload(r["conto"] as JsonObject, true); return;
        }
        await Reload(null, true);
    }
    /// <summary>Asks the page again; fresh: the bill is rebuilt (first time, after a bill is sent, set aside, taken back).</summary>
    async Task Reload(JsonObject resumed, bool fresh)
    {
        d = await Api.Current.Call("cassa", new { sede, prenotazione = Booking, sospeso = Suspended, preventivo = Quote, gift = GiftAmount, scade = GiftExpires, cliente = Client });
        if (d.Bo("nessuna"))
        {
            Content = F.Page(1480, F.Heading(null, "Cassa", "Qui chiudi il conto e lo mandi alla cassa, che stampa lo scontrino. I dati restano sulla prenotazione e nella scheda del cliente."),
                F.Card(T("Nessun registratore collegato", "H2"), T("Prima va collegato il registratore di cassa di questa sede, nella pagina Registratore di cassa. Lo fa l'amministratore del programma, o il titolare se l'amministratore lo permette.", size: 14, color: Res("Muted"))));
            return;
        }
        sede = d.S("sede");
        if (fresh || !built) Build(resumed);
        DrawLow();
        poll?.Stop();
        if (d.Bo("occupata"))  // data-reload="3": the register is working, the list follows it
        {
            poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            poll.Tick += async (_, _) => { poll.Stop(); if (IsLoaded) await Guard(() => Reload(null, false)); };
            poll.Start();
        }
    }
    /// <summary>After an action: what it says, then the page again.</summary>
    async Task Do(JsonObject args, bool fresh = false)
    {
        args["sede"] = sede;
        var r = await Api.Current.Call("cassa.azione", args);
        if (r.S("testo") != "") Main.Flash(r.S("testo"));
        if (fresh) { DropDraft(sede); built = false; Booking = Quote = GiftAmount = GiftExpires = Client = ""; Suspended = new(); }
        await Reload(r["conto"] as JsonObject, fresh);
    }

    // ---------- the page: heading, the three cards of the bill, then what changes with the register (low)
    void Build(JsonObject resumed)
    {
        built = false; lines.Clear(); tiles.Clear(); ops.Clear(); pay.Clear(); split.Clear(); current = null; credit = 0;
        key = d.S("chiave"); bookingId = d["prenotazione"]?.S("id") ?? ""; taken = d.A("presi").Select(x => x.ToString()).ToList(); contact = d.S("cliente"); active = "";
        var guide = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; guide.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        guide.Inlines.Add("Qui chiudi il conto e lo mandi alla cassa, che stampa lo scontrino. I dati restano sulla prenotazione e nella scheda del cliente. ");
        var how = new Hyperlink(new Run("Come si usa")) { Foreground = Res("Brand"), TextDecorations = null }; how.Click += (_, _) => Main.Show("guida"); guide.Inlines.Add(how);
        var h1 = T("Cassa", "H1"); var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (d.A("sedi").Count() > 1)
        {
            var place = new ComboBox { ItemsSource = d.A("sedi").Select(l => new ComboBoxItem { Content = l.S("nome"), Tag = l.S("id") }).ToList(), MinWidth = 180 };
            place.SelectedItem = place.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == sede);
            var change = F.Small(BA("Cambia sede", async () => { Save(); sede = (string)((ComboBoxItem)place.SelectedItem).Tag; built = false; await Reload(null, true); }));
            var acts = Row(place, change.M(10, 0, 0, 0)); acts.VerticalAlignment = VerticalAlignment.Bottom; DockPanel.SetDock(acts, Dock.Right); head.Children.Add(acts);
        }
        head.Children.Add(Col(h1, guide));
        top = new ContentControl { Focusable = false }; low = Col();
        var page = Col(head, top, ClientCard(), ItemsCard(), BillCard(), low);
        Content = F.Page(1480, page);

        // the rows: a bill taken back from the Parcheggio, the one left here, or what the page was opened with
        var saved = resumed ?? ReadDraft(sede);
        var freshOpen = !d.A("righe").Any() && bookingId == "" && taken.Count == 0 && contact == "";
        if (saved != null && (resumed != null || freshOpen || bookingId != "" && saved.S("booking") == bookingId)) Restore(saved);
        else
        {
            foreach (var l in d.A("righe")) AddLine(l.S("desc"), l.S("price"), l.S("svc"), l.S("staff") != "" ? l.S("staff") : d.S("operatore"), l["qty"]?.ToString() ?? "1", l.S("off"), false);
            discount.Text = d.S("sconto"); cf.Text = d.S("cf"); if (cf.Text != "") fiscal.IsExpanded = true;
            if (d["chi"] != null && client != null) client.Text = $"{d["chi"].S("nome")} {d["chi"].S("cognome")}".Trim();
            Credits(d.A("gift").Select(g => (g.S("codice"), g.S("credito").Replace(".", ","))).ToList());
        }
        foreach (var l in lines.Where(l => l.Svc.StartsWith("gift:"))) Lock(l);
        built = true; SetCurrent(lines.LastOrDefault()); Changed(); ShowHistory();
        if (giftCode.Text.Trim() != "") _ = Check();
    }
    void Restore(JsonObject x)
    {
        foreach (var l in x.A("lines")) AddLine(l.S("desc"), l.S("price"), l.S("svc"), l.S("staff"), l.S("qty") != "" ? l.S("qty") : "1", l.S("off"), l.S("park") == "1");
        if (x.S("booking") != "" && bookingId == "") bookingId = x.S("booking");
        foreach (var p in x.A("parked")) if (!taken.Contains(p.ToString())) taken.Add(p.ToString());
        var f = x["fields"] as JsonObject ?? new JsonObject();
        void Text(string k, TextBox box, Expander open = null) { if (f.ContainsKey(k) && box != null) { box.Text = f.S(k); if (f.S(k) != "" && open != null) open.IsExpanded = true; } }
        Text("till-client", client); if (f.ContainsKey("contact")) contact = f.S("contact");
        Text("till-discount", discount); Text("till-gift-code", giftCode, giftPay); Text("till-gift-use", giftUse, giftPay); Text("till-cf", cf, fiscal); Text("till-lottery", lottery, fiscal);
        Text("till-park-note", parkNote, parkAll); Text("gift-amount", giftValue);
        if (f.ContainsKey("gift-expires")) giftExpires.SelectedDate = DateTime.TryParse(f.S("gift-expires"), out var ge) ? ge : null;
        if (f.ContainsKey("copy")) copy.IsChecked = f.Bo("copy");
        foreach (var (k, r) in pay) if (f.ContainsKey("pay=" + k)) r.IsChecked = f.Bo("pay=" + k);
        foreach (var (k, t) in split) Text("pay-" + k, t, splitBox);
        if (f.S("gift-amount") != "") giftBox.Visibility = Visibility.Visible;
        active = x.S("active");
        if (contact != "") _ = Guard(async () => { var r = (await Api.Current.Call("clienti.cerca", new { id = contact, cassa = true }) as JsonArray)?.FirstOrDefault(); if (r != null) Credits(r.A("gift").Select(g => (g.S("codice"), g.S("credito"))).ToList()); });
    }

    // ----- Cliente
    Border ClientCard()
    {
        var headRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) }; var h2 = T("Cliente", "H2"); h2.Margin = new Thickness(0);
        var b = d["prenotazione"]; var body = Col(headRow);
        if (b != null)
        {
            var bid = b.S("id"); var who = d["chi"];
            var link = F.Link((who != null ? $"{who.S("nome")} {who.S("cognome")}, " : "") + Local(b.S("inizio")).ToString("dd/MM HH:mm"), () => { Save(); Main.ShowPage(new BookingDetailPage(bid)); });
            var right = Row(link); right.VerticalAlignment = VerticalAlignment.Center;
            if (b.S("cliente") != "" && !d.Bo("scegliCliente")) right.Children.Add(F.Small(B("Storico", () => ClientHistoryPage.Dialog(Main, b.S("cliente")))).M(10, 0, 0, 0));
            DockPanel.SetDock(right, Dock.Right); headRow.Children.Add(right);
            if (b.Bo("haScontrino")) body.Children.Add(F.Notice("Questa prenotazione ha già uno scontrino: guarda in fondo alla pagina prima di inviarne un altro.", "warning"));
        }
        headRow.Children.Add(h2);
        if (d.S("preventivo") != "") body.Children.Add(F.Notice($"Righe del preventivo {d.S("preventivo")}: controlla prezzi e operatori prima di inviare."));
        if (d.Bo("scegliCliente"))
        {
            client = Box(""); client.Tag = "Nome, email o telefono"; AgendaPage.Hint(client); client.MaxWidth = 520; client.HorizontalAlignment = HorizontalAlignment.Left; client.MinWidth = 520;
            found = Col(); extra = null;
            history = F.Small(B("Storico", () => { if (contact != "") ClientHistoryPage.Dialog(Main, contact); }));
            var credits = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; creditsBox = credits;
            client.TextChanged += (_, _) =>
            {
                if (!built || picking) return;
                contact = ""; ShowHistory(); Credits(new()); typing?.Stop();
                var q = client.Text.Trim(); if (q.Length < 2) { found.Children.Clear(); return; }
                typing = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                typing.Tick += async (_, _) =>
                {
                    typing.Stop();
                    try
                    {
                        var r = await Api.Current.Call("clienti.cerca", new { q, quanti = 8, cassa = true }); found.Children.Clear();
                        foreach (var c in (r as JsonArray) ?? new JsonArray())
                        {
                            var name = $"{c.S("nome")} {c.S("cognome")}".Trim();
                            var pickRow = new Button { Content = name + (c.S("email") != "" ? " · " + c.S("email") : ""), Style = Style("Tile"), HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 12, 14, 12) };
                            pickRow.Click += (_, _) =>
                            {
                                picking = true; contact = c.S("id"); client.Text = name; picking = false; found.Children.Clear(); ShowHistory();
                                if (c.S("cf") != "") { cf.Text = c.S("cf"); fiscal.IsExpanded = true; }
                                Credits(c.A("gift").Select(g => (g.S("codice"), g.S("credito"))).ToList()); Save();
                            };
                            found.Children.Add(pickRow);
                        }
                    }
                    catch (ApiError) { }
                };
                typing.Start();
            };
            var foundBox = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), Child = found, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            found.LayoutUpdated += (_, _) => foundBox.Visibility = found.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(F.Label("Cliente", "(facoltativo)")); body.Children.Add(client); body.Children.Add(foundBox);
            body.Children.Add(Row(T("Lo scontrino va anche nella sua scheda", size: 13, color: Res("Muted")), history.M(10, 0, 0, 0)).M(0, 6, 0, 0)); body.Children.Add(credits);
        }
        return F.Card(body);
    }
    bool picking; WrapPanel creditsBox;
    void ShowHistory() { if (history != null) history.Visibility = contact != "" ? Visibility.Visible : Visibility.Collapsed; }
    /// <summary>His credit on the card in view as soon as the client is there.</summary>
    void Credits(List<(string Code, string Balance)> gifts)
    {
        if (creditsBox == null) return; creditsBox.Children.Clear();
        foreach (var (code, balance) in gifts)
        {
            var b = F.Small(B($"Credito card {Euro(balance)} · Usa ({code})", () => { giftCode.Text = code; giftPay.IsExpanded = true; _ = Check(); })); b.FontWeight = FontWeights.Bold; b.Margin = new Thickness(0, 0, 6, 6);
            creditsBox.Children.Add(b);
        }
        if (gifts.Count > 0 && giftPay != null) giftPay.IsExpanded = true;
    }

    // ----- Servizi e prodotti
    Border ItemsCard()
    {
        var open = F.Small(B("Gift card", () => { giftBox.Visibility = giftBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; if (giftBox.Visibility == Visibility.Visible) giftValue.Focus(); }));
        var free = F.Small(B("Riga libera", () => { var l = AddLine("", "", "", active, "1", "", false); SetCurrent(l); l.Desc.Focus(); Changed(); }));
        var head = F.Section("Servizi e prodotti", null, Row(open, free.M(10, 0, 0, 0)));
        // Vendi una gift card
        giftValue = Box(""); giftValue.MaxLength = 8; giftValue.Tag = "per esempio 70"; AgendaPage.Hint(giftValue);
        giftExpires = new DatePicker { SelectedDate = DateTime.TryParse(d.S("giftScade"), out var ge) ? ge : null };
        var err = T("", size: 13, color: Res("Danger")); err.Visibility = Visibility.Collapsed;
        var amounts = new WrapPanel { Margin = new Thickness(0, 8, 0, 12) };
        foreach (var v in d.A("giftImporti")) { var vv = v.ToString(); amounts.Children.Add(F.Small(B(vv + " €", () => { giftValue.Text = vv; err.Visibility = Visibility.Collapsed; })).M(0, 0, 8, 0)); }
        var note = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap }; note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        note.Inlines.Add("Va nel conto come una riga, sul reparto delle gift card. È intestata al cliente scelto nel conto, oppure la intesti dopo dalla pagina ");
        var gl = new Hyperlink(new Run("Gift card")) { Foreground = Res("Brand"), TextDecorations = null }; gl.Click += (_, _) => { Save(); Main.Show("giftcard"); }; note.Inlines.Add(gl);
        var add = F.Small(B("Aggiungi al conto", () =>
        {
            var v = Num(giftValue.Text);
            if (!(v >= 1 && v <= 9999)) { err.Text = "Scrivi un importo da 1 a 9.999 €."; err.Visibility = Visibility.Visible; giftValue.Focus(); return; }
            var l = AddLine("GIFT CARD", v.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", ","), "gift:" + (giftExpires.SelectedDate?.ToString("yyyy-MM-dd") ?? ""), "", "1", "", false); Lock(l);
            giftValue.Text = ""; err.Visibility = Visibility.Collapsed; giftBox.Visibility = Visibility.Collapsed; Changed();
        }, "Primary"));
        var close = F.Small(B("Chiudi", () => giftBox.Visibility = Visibility.Collapsed, "Ghost"));
        giftBox = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 10, 0, 10), Background = Res("Surface"), Visibility = Visibility.Collapsed,
            Child = Col(T("Vendi una gift card", size: 15, bold: true), note, amounts, F.Grid2(F.Field("Importo €", giftValue), F.Field("Scade il", giftExpires, null, "Proposta: tra 12 mesi. Vuoto: non scade")), err, Row(add, close.M(10, 0, 0, 0)).M(0, 12, 0, 0)) };
        // the tiles, group by group; a search leaves only the ones that match
        search = Box(""); search.Tag = "Cerca un servizio o un prodotto"; AgendaPage.Hint(search);
        var main = Col(search); var groups = new List<(TextBlock H, WrapPanel Grid)>();
        foreach (var g in d.A("gruppi"))
        {
            var h3 = T(g.S("nome"), size: 15, bold: true).M(0, 12, 0, 0); var grid = new WrapPanel { Margin = new Thickness(0, 6, 0, 12) };
            foreach (var i in g.A("voci"))
            {
                var name = T(i.S("etichetta"), size: 13, bold: true); name.TextWrapping = TextWrapping.Wrap; var price = T(i.S("prezzo") != "" ? i.S("prezzo") + " €" : "prezzo libero", size: 12, color: Res("Muted"));
                var flag = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Res("BrandInk"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, ToolTip = "Togli dal conto", Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = "✓", FontSize = 13, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                var cell = new Grid(); cell.Children.Add(Col(name, price)); cell.Children.Add(flag);
                var tile = new Button { Content = cell, Width = 124, MinHeight = 50, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 6, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, ToolTip = i.S("etichetta"),
                    BorderThickness = new Thickness(2), BorderBrush = Res("Pastel"), Background = Res("Surface"), Style = Style("Tile") };
                string svc = i.S("svc"), desc = i.S("nome"), p = i.S("prezzo");
                Line Same() => svc != "" ? lines.FirstOrDefault(l => l.Svc == svc && l.Desc.Text == desc) : null;
                flag.MouseEnter += (_, _) => ((TextBlock)flag.Child).Text = "×"; flag.MouseLeave += (_, _) => ((TextBlock)flag.Child).Text = "✓";
                flag.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (Same() is Line s) { Remove(s); SetCurrent(lines.LastOrDefault()); Changed(); } };
                // a tap puts the service on the bill once; a tap on a service already there only makes its row the current one
                tile.Click += (_, _) => { var same = Same(); SetCurrent(same ?? AddLine(desc, p, svc, active, "1", "", false)); Changed(); };
                tiles.Add((tile, svc, desc, i.S("etichetta"), name, price, flag)); grid.Children.Add(tile);
            }
            groups.Add((h3, grid)); main.Children.Add(h3); main.Children.Add(grid);
        }
        if (!d.A("gruppi").Any()) main.Children.Add(T("Nessun servizio attivo in questa sede: usa la riga libera.", size: 14, color: Res("Muted")).M(0, 10, 0, 0));
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim().ToLowerInvariant();
            foreach (var t in tiles) t.Tile.Visibility = q != "" && !t.Label.ToLowerInvariant().Contains(q) ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (h, grid) in groups) h.Visibility = grid.Children.Cast<UIElement>().Any(x => x.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        };
        // the operators as a bar beside the services: tap the service, then who did it
        var inner = new Grid(); inner.ColumnDefinitions.Add(new ColumnDefinition()); inner.Children.Add(main);
        if (d.A("operatori").Any())
        {
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            var bar = Col(T("Operatore", size: 14, bold: true), T("Tocca il servizio, poi chi lo ha fatto", size: 13, color: Res("Muted")).M(0, 0, 0, 4));
            foreach (var o in d.A("operatori"))
            {
                var name = o.S("nome"); var color = o.I("colore");
                var face = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Op(color), Margin = new Thickness(0, 0, 8, 0), Child = new TextBlock { Text = name.Length > 0 ? name[..1] : "", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                var label = T(name, size: 14, bold: true); label.VerticalAlignment = VerticalAlignment.Center;
                var b = new Button { Content = Row(face, label), Style = Style("Tile"), HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), BorderBrush = Op(color), BorderThickness = new Thickness(6, 2, 2, 2), Background = Res("Surface") };
                b.Click += (_, _) => { active = name; var r = current ?? lines.LastOrDefault(); if (r != null) r.Staff = active; Changed(); };
                ops.Add((b, name, color)); bar.Children.Add(b);
            }
            Grid.SetColumn(bar, 2); inner.Children.Add(bar);
        }
        return F.Card(head, giftBox, inner);
    }

    // ----- Conto
    Border BillCard()
    {
        var clear = F.Small(BA("Svuota il conto", async () => { if (!Ask("Svuotare il conto? Righe, cliente, sconti e pagamento si tolgono.")) return; DropDraft(sede); Booking = Quote = GiftAmount = GiftExpires = Client = ""; Suspended = new(); built = false; await Reload(null, true); }, "Ghost"));
        var head = F.Section("Conto", null, clear);
        var th = new Grid { Margin = new Thickness(0, 6, 0, 0) }; Columns(th);
        void Th(string s, int col, string small = null) { var t = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 6) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); t.Inlines.Add(s); if (small != null) t.Inlines.Add(new Run(" " + small) { FontWeight = FontWeights.Normal, FontSize = 12 }); Grid.SetColumn(t, col); th.Children.Add(t); }
        Th("Descrizione e operatore", 0); Th("Quantità", 1); Th("Prezzo €", 2); Th("Sconto", 3, "€ o %"); Th("Non nello scontrino", 4);
        rowsBox = Col();
        discount = Box(""); discount.MaxLength = 8; discount.Tag = "0 o 10%"; AgendaPage.Hint(discount); discount.TextChanged += (_, _) => Changed();
        total = T("0,00 €", size: 26, bold: true); giftTotal = T("", size: 13, color: Res("Muted")); giftTotal.Visibility = Visibility.Collapsed; giftTotal.HorizontalAlignment = HorizontalAlignment.Right;
        parkedTotal = T("", size: 13, bold: true, color: Res("Warn")); parkedTotal.Visibility = Visibility.Collapsed; parkedTotal.HorizontalAlignment = HorizontalAlignment.Right;
        var tot = Row(T("Totale", size: 14).M(0, 0, 10, 4), total); tot.HorizontalAlignment = HorizontalAlignment.Right; ((FrameworkElement)tot.Children[0]).VerticalAlignment = VerticalAlignment.Bottom;
        var totals = Col(tot, giftTotal, parkedTotal); totals.VerticalAlignment = VerticalAlignment.Bottom;
        // Paga con gift card
        giftCode = Box(""); giftCode.MaxLength = 12; giftCode.Tag = "per esempio K7QP3MZA"; AgendaPage.Hint(giftCode); giftCode.CharacterCasing = CharacterCasing.Upper;
        giftInfo = T("", size: 13, color: Res("Muted")); giftUse = Box(""); giftUse.MaxLength = 8;
        giftCode.TextChanged += (_, _) => { if (!built) return; lookup?.Stop(); lookup = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) }; lookup.Tick += async (_, _) => { lookup.Stop(); await Check(); }; lookup.Start(); };
        giftUse.TextChanged += (_, _) => Changed();
        giftPay = new Expander { Header = "Paga con gift card", Margin = new Thickness(0, 10, 0, 10), IsExpanded = d.A("gift").Any(),
            Content = F.Grid2(Col(F.Label("Codice della gift card"), giftCode, giftInfo.M(0, 4, 0, 0)), F.Field("Quanto usare €", giftUse, "(facoltativo)", "Vuoto: tutto il credito che serve. Lo scontrino scala la gift card, il resto si paga qui sotto")) };
        // Pagamento
        var choices = new WrapPanel(); var first = true;
        foreach (var p in d.A("pagamenti"))
        {
            var k = p.S("id"); var icon = k switch { "cash" => "cash", "card" => "card", "other" => "wallet", _ => "clock" };
            var r = new RadioButton { GroupName = "pay" + GetHashCode(), IsChecked = first, Margin = new Thickness(0, 0, 10, 10), Content = Row(Icons.Icon(icon, Res("Brand"), 26, 1).M(0, 0, 10, 0), T(p.S("nome"), size: 14, bold: true)), Style = Style("PayChoice") };
            ((FrameworkElement)((StackPanel)r.Content).Children[1]).VerticalAlignment = VerticalAlignment.Center;
            r.Checked += (_, _) => Save(); pay[k] = r; choices.Children.Add(r); first = false;
            var box = Box(""); box.TextChanged += (_, _) => Save(); split[k] = box;
        }
        var splitGrid = F.Grid2(d.A("pagamenti").Select(p => (FrameworkElement)F.Field(p.S("nome") + " €", split[p.S("id")])).ToArray());
        splitBox = new Expander { Header = "Dividi il pagamento", Margin = new Thickness(0, 10, 0, 10), Content = Col(splitGrid, T("La somma deve fare il totale", size: 13, color: Res("Muted")).M(0, 4, 0, 0)) };
        cf = Box(""); cf.MaxLength = 16; cf.CharacterCasing = CharacterCasing.Upper; lottery = Box(""); lottery.MaxLength = 8; lottery.CharacterCasing = CharacterCasing.Upper;
        fiscal = new Expander { Header = "Codice fiscale e lotteria degli scontrini", Margin = new Thickness(0, 10, 0, 10), Content = F.Grid2(F.Field("Codice fiscale del cliente", cf), F.Field("Codice lotteria", lottery, null, "Solo se il cliente paga con carta o bancomat")) };
        var copyText = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; copyText.Inlines.Add("Il cliente vuole la copia per email");
        var small = new Run(" Solo se la chiede. Va all'email della sua scheda, quindi scegli prima il cliente. Resta registrato il suo consenso, separato da quello per le offerte") { FontSize = 13 }; small.SetResourceReference(TextElement.ForegroundProperty, "Muted"); copyText.Inlines.Add(small);
        copy = F.Check(copyText); copy.Click += (_, _) => Save();
        // Paga in cassa, Parcheggia
        var sell = BA("Paga in cassa", () => Send("sell"), "Primary");
        standbyNote = Box(""); standbyNote.MaxLength = 80; standbyNote.Width = 260; standbyNote.Tag = "Nota per il Parcheggio"; AgendaPage.Hint(standbyNote);
        var park = BA("Parcheggia", () => Do(new JsonObject { ["azione"] = "standby", ["draft"] = Draft(), ["standby_note"] = standbyNote.Text, ["contact"] = contact, ["booking"] = bookingId }, true)); park.ToolTip = "Il conto va nel Parcheggio senza scontrino: lo riprendi dopo, anche da un altro PC";
        var send = new DockPanel { Margin = new Thickness(0, 4, 0, 0) }; var rightSide = Row(standbyNote, park.M(8, 0, 0, 0)); DockPanel.SetDock(rightSide, Dock.Right); send.Children.Add(rightSide); sell.HorizontalAlignment = HorizontalAlignment.Left; send.Children.Add(sell);
        parkNote = Box(""); parkNote.MaxLength = 120; parkNote.Tag = "per esempio: paga sabato"; AgendaPage.Hint(parkNote);
        var parkGo = BA("Segna fatto, non nello scontrino", () => Send("park")); parkGo.HorizontalAlignment = HorizontalAlignment.Left; parkGo.Margin = new Thickness(0, 12, 0, 0);
        parkAll = new Expander { Header = "Tutto il conto non nello scontrino", Margin = new Thickness(0, 10, 0, 0), Content = Col(
            T("Non esce nessuno scontrino: i trattamenti vanno nella scheda del cliente, fatti, e si pagano un'altra volta. La volta dopo li riprendi nel conto da qui sotto, con il prezzo che vuoi", size: 13, color: Res("Muted")),
            F.Label("Nota", "(facoltativa)"), parkNote, parkGo) };
        foreach (var t in new[] { cf, lottery, parkNote, giftValue }) t.TextChanged += (_, _) => Save();
        var pays = Col(T("Pagamento", size: 14, bold: true).M(0, 12, 0, 8), choices);
        return F.Card(head, th, rowsBox, T("«Non nello scontrino»: il trattamento è fatto e va nella scheda del cliente, si paga un'altra volta", size: 13, color: Res("Muted")).M(0, 8, 0, 0),
            F.Grid2(F.Field("Sconto sul totale", discount, "€ o %"), totals), giftPay, pays, splitBox, fiscal, copy, send, parkAll);
    }
    static void Columns(Grid g)
    {
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(132), new GridLength(110), new GridLength(84), new GridLength(190), new GridLength(40) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
    }

    Line AddLine(string desc, string price, string svc, string staff, string qty, string off, bool park)
    {
        var l = new Line { Staff = staff ?? "", Svc = svc ?? "", Park = park };
        l.Desc.Text = desc; l.Desc.MaxLength = 32; l.Price.Text = price; l.Qty.Text = qty; l.Qty.Width = 48; l.Qty.TextAlignment = TextAlignment.Center; l.Off.Text = off; l.Off.MaxLength = 8; l.Off.Tag = "0"; AgendaPage.Hint(l.Off);
        l.Who = T("", size: 12.5, color: Res("Muted")).M(2, 3, 0, 0);
        var g = new Grid(); Columns(g);
        void Put(FrameworkElement e, int col) { e.Margin = new Thickness(4, 0, 4, 0); e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, col); g.Children.Add(e); }
        Put(Col(l.Desc, l.Who), 0);
        Button Step(string text, int by) { var b = F.Small(B(text, () => { l.Qty.Text = Math.Clamp((int)Num(l.Qty.Text) + by, 1, 99).ToString(); Changed(); }, "Ghost")); b.Padding = new Thickness(9, 4, 9, 4); return b; }
        l.Minus = Step("−", -1); l.Plus = Step("+", 1);
        Put(Row(l.Minus, l.Qty.M(2, 0, 2, 0), l.Plus), 1); Put(l.Price, 2); Put(l.Off, 3);
        var box = new Border { Width = 18, Height = 18, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0), Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        l.ParkButton = new ToggleButton { IsChecked = park, Content = Row(box, T("Non nello scontrino", size: 12.5, bold: true)), ToolTip = "Fatto, non nello scontrino: va nella scheda del cliente e si paga un'altra volta", Padding = new Thickness(12, 8, 12, 8), Style = Style("Tile"), BorderThickness = new Thickness(1) };
        l.ParkButton.Tag = box; l.ParkButton.Click += (_, _) => { l.Park = l.ParkButton.IsChecked == true; SetCurrent(l); Changed(); };
        Put(l.ParkButton, 4);
        var x = F.Small(B("×", () => { Remove(l); if (current == l) SetCurrent(lines.LastOrDefault()); Changed(); }, "Ghost")); x.ToolTip = "Togli la riga"; Put(x, 5);
        l.Box = new Border { Child = g, Padding = new Thickness(0, 6, 0, 6), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent };
        l.Box.PreviewMouseLeftButtonDown += (_, _) => { if (lines.Contains(l)) SetCurrent(l); };
        foreach (var t in new[] { l.Desc, l.Qty, l.Price, l.Off }) t.TextChanged += (_, _) => Changed();
        lines.Add(l); rowsBox.Children.Add(l.Box);
        return l;
    }
    void Remove(Line l) { lines.Remove(l); rowsBox.Children.Remove(l.Box); if (current == l) current = null; }
    /// <summary>A gift card sold: its row is what it is, only the price and the × stay.</summary>
    void Lock(Line l) { l.Desc.IsReadOnly = true; l.Qty.IsReadOnly = true; l.Off.IsReadOnly = true; l.Minus.Visibility = l.Plus.Visibility = Visibility.Hidden; l.ParkButton.Visibility = Visibility.Hidden; }
    void SetCurrent(Line l) { current = l; Paint(); }

    /// <summary>app.js sum(): the rows «Non nello scontrino» stay out of the total, a gift card takes its credit off.</summary>
    void Sum()
    {
        decimal t = 0, later = 0;
        foreach (var l in lines)
        {
            var full = Num(l.Qty.Text) * Num(l.Price.Text); var o = l.Off.Text.Trim(); var net = full - Math.Min(full, o.EndsWith('%') ? full * Num(o[..^1]) / 100 : Num(o));
            if (l.Park) later += net; else t += net;
        }
        var dsc = discount.Text.Trim(); t -= dsc.EndsWith('%') ? t * Num(dsc[..^1]) / 100 : Num(dsc); t = Math.Max(0, t);
        var want = giftUse.Text.Trim(); var use = Math.Min(t, want != "" ? Math.Min(credit, Num(want)) : credit);
        total.Text = Eur(t - use); giftTotal.Visibility = use != 0 ? Visibility.Visible : Visibility.Collapsed; giftTotal.Text = use != 0 ? $"Gift card −{Eur(use)} su {Eur(t)}" : "";
        parkedTotal.Visibility = later != 0 ? Visibility.Visible : Visibility.Collapsed; parkedTotal.Text = later != 0 ? "Non nello scontrino: " + Eur(later) : "";
    }
    /// <summary>The row touched last is the current one; its operator is the one pressed; the tiles of the services on the
    /// bill light up in the colour of the operator of their row.</summary>
    void Paint()
    {
        if (rowsBox == null) return;
        var who = current != null ? current.Staff : active;
        foreach (var l in lines)
        {
            l.Who.Text = l.Staff != "" ? l.Staff : "Operatore?";
            l.Box.Background = l.Park ? Res("WarnSoft") : l == current ? Res("BrandSoft") : Brushes.Transparent;
            l.Box.BorderThickness = new Thickness(l == current ? 4 : 0, 1, 0, 0); l.Box.BorderBrush = l == current ? Res("Brand") : Res("Line");
            foreach (var t in new[] { l.Desc, l.Qty, l.Price, l.Off }) { t.TextDecorations = l.Park ? TextDecorations.Strikethrough : null; t.Opacity = l.Park ? .75 : 1; }
            var box = (Border)l.ParkButton.Tag; box.Background = l.Park ? Res("Warn") : Brushes.Transparent; box.BorderBrush = Res(l.Park ? "Warn" : "Muted"); ((TextBlock)box.Child).Visibility = l.Park ? Visibility.Visible : Visibility.Hidden;
            l.ParkButton.Background = l.Park ? Res("WarnSoft") : Res("Surface"); l.ParkButton.BorderBrush = Res(l.Park ? "Warn" : "Line");
        }
        foreach (var (b, name, color) in ops)
        {
            var on = who != "" && name == who; b.Background = on ? Op(color) : Res("Surface");
            var row = (StackPanel)b.Content; ((TextBlock)row.Children[1]).Foreground = on ? Brushes.White : Res("Ink");
            var face = (Border)row.Children[0]; face.Background = on ? Brushes.White : Op(color); ((TextBlock)face.Child).Foreground = on ? Op(color) : Brushes.White;
        }
        var lit = new Dictionary<string, string>();
        foreach (var l in lines) { var k = l.Svc != "" ? l.Svc : "desc:" + l.Desc.Text.Trim().ToLowerInvariant(); lit.TryAdd(k, l.Staff); }
        foreach (var t in tiles)
        {
            var k = t.Svc != "" ? t.Svc : "desc:" + t.Desc.Trim().ToLowerInvariant(); var on = lit.TryGetValue(k, out var staff);
            var tone = on && ops.FirstOrDefault(o => o.Name == staff) is var o && o.B != null ? (Brush)Op(o.Color) : Res("Brand");
            t.Tile.Background = on ? tone : Res("Surface"); t.Tile.BorderBrush = on ? tone : Res("Pastel"); t.Name.Foreground = on ? Res("BrandInk") : Res("Ink"); t.Price.Foreground = on ? Res("BrandInk") : Res("Muted");
            t.Flag.Visibility = on ? Visibility.Visible : Visibility.Collapsed; ((TextBlock)t.Flag.Child).Foreground = tone;
        }
    }
    void Changed() { if (rowsBox == null || total == null) return; Sum(); Paint(); Save(); }

    /// <summary>The code of a gift card that pays: its credit asked to the program, the total shown net of it.</summary>
    async Task Check()
    {
        var v = giftCode.Text.Trim(); credit = 0; giftInfo.Text = ""; giftInfo.Foreground = Res("Muted"); Sum(); Save();
        if (v.Length < 4) return;
        try
        {
            var r = await Api.Current.Call("giftcard.cerca", new { code = v });
            if (giftCode.Text.Trim() != v) return;
            if (r.S("errore") != "") { giftInfo.Text = r.S("errore"); giftInfo.Foreground = Res("Danger"); }
            else { credit = Num(r.S("credito")); giftInfo.Text = $"Credito {r.S("credito")} €" + (r.S("chi") != "" ? " · " + r.S("chi") : "") + (r.S("scade") != "" ? " · scade il " + r.S("scade") : ""); }
            Sum();
        }
        catch (ApiError) { }
    }

    /// <summary>Paga in cassa, or the whole bill «non nello scontrino»: a row with a description and no price stops the bill
    /// here, on that field; nothing is sent and nothing is lost.</summary>
    async Task Send(string action)
    {
        foreach (var l in lines)
            if (l.Desc.Text.Trim() != "" && Num(l.Price.Text) <= 0) { Main.Flash("Manca il prezzo", true); l.Price.BringIntoView(); l.Price.Focus(); return; }
        var args = new JsonObject
        {
            ["azione"] = action, ["key"] = key, ["booking"] = bookingId, ["contact"] = contact, ["parked"] = new JsonArray(taken.Select(x => (JsonNode)x).ToArray()),
            ["righe"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["desc"] = l.Desc.Text, ["qty"] = l.Qty.Text, ["price"] = l.Price.Text, ["svc"] = l.Svc, ["off"] = l.Off.Text, ["staff"] = l.Staff, ["park"] = l.Park ? "1" : "" }).ToArray()),
            ["discount"] = discount.Text, ["pay"] = pay.FirstOrDefault(p => p.Value.IsChecked == true).Key ?? "cash", ["cf"] = cf.Text, ["lottery"] = lottery.Text, ["gift_code"] = giftCode.Text, ["gift_use"] = giftUse.Text, ["copy"] = copy.IsChecked == true, ["park_note"] = parkNote.Text,
        };
        foreach (var (k, t) in split) args["pay_" + k] = t.Text;
        await Do(args, true);
    }

    // ---------- what follows the register: the Parcheggio, the receipts of today, closure and annulli
    void DrawLow()
    {
        var puoChiudere = d.Bo("puoChiudere"); var canVoid = d.Bo("puoAnnullare"); var counted = d.Bo("contata");
        top.Content = null;  // the reminder of the daily closure is gone (2.22.17): it kept asking for closures already done
        low.Children.Clear();
        Border Low(string color, params UIElement[] items) { var c = F.Card(items); c.BorderThickness = new Thickness(1.5, 5, 1.5, 1.5); c.BorderBrush = color == null ? Res("Brand") : Hex(color); c.Margin = new Thickness(0, 0, 0, 22); return c; }
        Border Block(params UIElement[] items) => new() { BorderBrush = Res("Pastel"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Background = Res("BrandSoft"), Child = Col(items), VerticalAlignment = VerticalAlignment.Top };
        TextBlock H3(string text, int? count = null) { var t = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }; t.Inlines.Add(text); if (count != null) t.Inlines.Add(new Run("  " + count) { FontSize = 12, Foreground = Res("Brand") }); return t; }
        TextBlock Small(string text) => T(text, size: 13, color: Res("Muted")).M(0, 0, 0, 8);
        Border Line2(FrameworkElement left, FrameworkElement right, bool last) { var row = new DockPanel(); if (right != null) { right.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(right, Dock.Right); row.Children.Add(right); } row.Children.Add(left); return new Border { Child = row, Padding = new Thickness(0, 12, 0, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 2) }; }

        // ----- Parcheggio
        var today = DateOnly.Parse(d.S("oggi"));
        var standby = Col(); var sb = d.A("standby").ToList();
        for (int i = 0; i < sb.Count; i++) standby.Children.Add(Line2(StandbyText(sb[i], today), StandbyActions(sb[i].S("id"), true), i == sb.Count - 1));
        if (sb.Count == 0) standby.Children.Add(T("Nessun conto in stand-by.", size: 14, color: Res("Muted")));
        var due = Col(); var dues = d.A("daPagare").ToList();
        for (int i = 0; i < dues.Count; i++)
        {
            var s = dues[i]; var sid = s.S("id"); var cid = s.S("cliente");
            var line = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap }; line.Inlines.Add(new Run(Euro(s.S("importo"))) { FontWeight = FontWeights.Bold }); line.Inlines.Add(" ");
            if (cid != "" && d.Bo("scegliCliente")) { var l = new Hyperlink(new Run(s.S("chi"))) { Foreground = Res("Brand"), TextDecorations = null }; l.Click += (_, _) => { Save(); Main.ShowPage(new ClientPage(cid)); }; line.Inlines.Add(l); } else line.Inlines.Add(s.S("chi"));
            var meta = new Run($" scontrino del {(s.S("stampato") != "" ? Local(s.S("stampato")).ToString("dd/MM/yyyy") : "")}" + (s.S("numero") != "" ? " · n. " + s.S("documento") : "") + (s.S("importo") != s.S("totale") ? " · totale " + Euro(s.S("totale")) : "")) { FontSize = 13 }; meta.SetResourceReference(TextElement.ForegroundProperty, "Muted"); line.Inlines.Add(meta);
            var how = new ComboBox { ItemsSource = d.A("saldi").Select(p => new ComboBoxItem { Content = p.S("nome"), Tag = p.S("id") }).ToList(), SelectedIndex = 0, MinWidth = 150 };
            var paid = F.Small(BA("Segna pagato", async () => { if (Ask($"Il cliente ha pagato {Euro(s.S("importo"))}?")) await Do(new JsonObject { ["azione"] = "settle", ["sale"] = sid, ["how"] = (string)((ComboBoxItem)how.SelectedItem).Tag }); }));
            due.Children.Add(Line2(line, Row(how, paid.M(8, 0, 0, 0)), i == dues.Count - 1));
        }
        if (dues.Count == 0) due.Children.Add(T("Nessun pagamento in sospeso.", size: 14, color: Res("Muted")));
        var parkHead = F.Section("Parcheggio", d.I("parcheggio") > 0 ? d.I("parcheggio") : null); parkHead.Margin = new Thickness(0, 0, 0, 12);
        low.Children.Add(Low("#2f8f6b", parkHead, F.Cols(2, 16,
            Block(H3("In stand-by", sb.Count), Small("I conti preparati e messi da parte con Parcheggia, da riprendere per fare lo scontrino. Li vedono tutte le postazioni di questa sede"), standby),
            Block(H3("Da pagare", dues.Count), Small("I pagamenti posticipati: gli scontrini con «Da pagare», di tutti i giorni. Quando il cliente paga, segna come ha pagato: l'importo entra nei report di quel giorno"), due))));
        // ----- the divider
        var div = new Grid { Margin = new Thickness(0, 34, 0, 14) }; div.ColumnDefinitions.Add(new ColumnDefinition()); div.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); div.ColumnDefinitions.Add(new ColumnDefinition());
        var dt = T("INCASSI E FINE GIORNATA", size: 13.5, color: Res("Muted")); dt.FontWeight = FontWeights.ExtraBold; dt.Margin = new Thickness(14, 0, 14, 0); Grid.SetColumn(dt, 1); div.Children.Add(dt);
        foreach (var col in new[] { 0, 2 }) { var bar = new Border { Height = 2, Background = Res("Line"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(bar, col); div.Children.Add(bar); }
        low.Children.Add(div);
        // ----- Scontrini di oggi
        var rows = d.A("scontrini").ToList(); var list = Col();
        foreach (var s in rows.Take(5))
        {
            var sid = s.S("id"); var kind = s.S("tipo"); var status = s.S("stato");
            var line = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new Run(kind == "void" ? "Annullo" + (s.S("annullato") != "" ? " del n. " + s.S("annullato") : "") : kind == "close" ? "Chiusura giornaliera" + (s.S("errore") == "Fatta dalla cassa" ? " fatta dalla cassa" : "") : Euro(s.S("totale"))) { FontWeight = FontWeights.Bold }); line.Inlines.Add(" ");
            if (s.S("chi") != "") { var cid = s.S("cliente"); if (d.Bo("scegliCliente")) { var l = new Hyperlink(new Run(s.S("chi"))) { Foreground = Res("Brand"), TextDecorations = null }; l.Click += (_, _) => { Save(); Main.ShowPage(new ClientPage(cid)); }; line.Inlines.Add(l); } else line.Inlines.Add(s.S("chi")); line.Inlines.Add(" "); }
            line.Inlines.Add(new InlineUIContainer(MainWindow.Pill(s.S("statoNome"), status is "failed" or "unknown" ? "warn" : "")) { BaselineAlignment = BaselineAlignment.Center });
            var meta = new Span { FontSize = 13 }; meta.SetResourceReference(TextElement.ForegroundProperty, "Muted"); meta.Inlines.Add($" {Local(s.S("creato")):HH:mm}" + (s.S("numero") != "" ? " · n. " + s.S("documento") : ""));
            if (s.S("prenotazione") != "") { meta.Inlines.Add(" · "); var bid = s.S("prenotazione"); var bl = new Hyperlink(new Run("prenotazione")) { Foreground = Res("Brand"), TextDecorations = null }; bl.Click += (_, _) => { Save(); Main.ShowPage(new BookingDetailPage(bid)); }; meta.Inlines.Add(bl); }
            if (s.Bo("email")) meta.Inlines.Add(" · copia per email inviata");
            if (Num(s.S("daPagare")) != 0 && kind == "sale")
            {
                meta.Inlines.Add(" · ");
                if (s.S("pagato") != "") meta.Inlines.Add("pagato il " + Local(s.S("pagato")).ToString("dd/MM")); else { var r = new Run("da pagare " + Euro(s.S("daPagare"))); r.SetResourceReference(TextElement.ForegroundProperty, "Danger"); meta.Inlines.Add(r); }
            }
            line.Inlines.Add(meta);
            var left = Col(line);
            if (s.S("errore") != "" && (kind != "close" || status != "done")) left.Children.Add(T(s.S("errore"), size: 13, color: Res("Danger")));
            if (s["dettaglio"] is JsonArray det && det.Count > 0)
                left.Children.Add(F.Details("Vedi cosa è stato battuto", new Border { BorderBrush = Res("Pastel"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 0, 0), Child = Col(det.Select(x => (UIElement)T(x.ToString(), size: 13)).ToArray()) }, plain: true).M(0, 6, 0, 0));
            FrameworkElement acts = null;
            if (status == "failed") acts = F.Small(BA("Riprova", () => Do(new JsonObject { ["azione"] = "retry", ["sale"] = sid })));
            else if (status == "unknown")
            {
                var n = Box(""); n.MaxLength = 12; n.Width = 250; n.Tag = "N., es. 2039-0013 (facoltativo)"; AgendaPage.Hint(n);
                acts = Row(n, F.Small(BA("È uscito", () => Do(new JsonObject { ["azione"] = "printed", ["sale"] = sid, ["number"] = n.Text }))).M(8, 0, 0, 0),
                    F.Small(BA("Non è uscito, rimanda", async () => { if (Ask("Hai guardato la cassa e lo scontrino non è uscito? Viene inviato di nuovo.")) await Do(new JsonObject { ["azione"] = "retry", ["sale"] = sid }); }, "Ghost")).M(8, 0, 0, 0));
            }
            else if (status == "done" && kind == "sale")
            {
                var row = Row(F.Small(B("Stampa la copia per il cliente", () => { Save(); Main.ShowPage(new ReceiptCopyPage(sede, sid) { Back = this }); }, "Ghost")));
                if (s.S("cliente") != "") row.Children.Add(F.Small(BA("Rimanda la copia", () => Do(new JsonObject { ["azione"] = "mail", ["sale"] = sid }), "Ghost")).M(8, 0, 0, 0));
                var doc = Box(counted ? "" : s.S("documento")); doc.MaxLength = 12; doc.Width = counted ? 290 : 150; doc.Tag = counted ? "Numero in fondo allo scontrino, es. 2039-0013" : "Documento n., es. 2026-0012"; AgendaPage.Hint(doc);
                if (canVoid) row.Children.Add(doc.M(8, 0, 0, 0));
                var ask = !canVoid ? "Su questo registratore l'annullo si fa dalla cassa. L'hai già fatto? Lo segniamo come annullato."
                    : counted ? "Mandare alla cassa l'annullo con il numero che hai scritto? Deve essere quello stampato in fondo allo scontrino (es. 2039-0013): la cassa annulla quello." : $"Mandare alla cassa l'annullo dello scontrino n. {s.S("documento")}?";
                row.Children.Add(F.Small(BA(canVoid ? "Annulla" : "Annullato dalla cassa", async () =>
                {
                    if (canVoid && counted && doc.Text.Trim() == "") { Main.Flash("Scrivi il numero stampato in fondo allo scontrino, per esempio 2039-0013: la cassa annulla quello.", true); doc.Focus(); return; }
                    if (Ask(ask)) await Do(new JsonObject { ["azione"] = "void", ["sale"] = sid, ["doc"] = canVoid ? doc.Text : "" });
                }, "Ghost")).M(8, 0, 0, 0));
                acts = row;
            }
            list.Children.Add(Line2(left, acts, s == rows.Take(5).Last()));
        }
        if (rows.Count == 0) list.Children.Add(T("Ancora nessuno scontrino oggi.", size: 14, color: Res("Muted")));
        var sh = F.Section("Scontrini di oggi", rows.Count); sh.Margin = new Thickness(0, 0, 0, 12);
        low.Children.Add(Low(null, sh, rows.Count > 5 ? Small($"Qui gli ultimi 5 di {rows.Count}: li trovi tutti nel Riepilogo di oggi (PDF), qui sotto") : null, list,
            !canVoid ? Small("Su questa cassa l'annullo si fa dalla cassa: poi premi Annullato dalla cassa, così anche qui risulta annullato.") : null));
        // ----- Chiusura, riepilogo e annulli
        var close = puoChiudere
            ? Block(H3("Chiusura giornaliera"), Row(BA("Chiusura giornaliera", async () => { if (Ask("Fare adesso la chiusura giornaliera? La cassa stampa la chiusura e manda i corrispettivi di oggi all'Agenzia delle Entrate. Si fa una volta al giorno, a fine giornata.")) await Do(new JsonObject { ["azione"] = "close" }); }),
                    F.Small(BA("L'ho fatta dalla cassa", async () => { if (Ask("Hai fatto la chiusura giornaliera dai tasti della cassa? Il programma la segna come fatta, non manda niente alla cassa.")) await Do(new JsonObject { ["azione"] = "closed_by_hand" }); }, "Ghost")).M(8, 0, 0, 0)),
                Small("A fine giornata, dopo l'ultimo scontrino. La cassa stampa la chiusura e trasmette i corrispettivi: conservala come sempre.").M(0, 8, 0, 0))
            : Block(H3("Chiusura giornaliera"), Small("Su questa cassa la chiusura giornaliera si fa dalla cassa: poi premi qui, così il programma sa che è fatta."),
                Left(F.Small(BA("Chiusura fatta dalla cassa", async () => { if (Ask("Hai fatto la chiusura dalla cassa?")) await Do(new JsonObject { ["azione"] = "closed_by_hand" }); }))));
        var from = new DatePicker { SelectedDate = DateTime.Parse(d.S("inizioMese")) }; var to = new DatePicker { SelectedDate = DateTime.Parse(d.S("oggi")) };
        async Task Summary(string a, string b)
        {
            var data = await Api.Current.Bytes("cassa.riepilogo", new { sede, dal = a, al = b });
            Downloads.Save($"riepilogo-cassa-{a}{(b == a || b == "" ? "" : "-" + b)}.pdf", data);
        }
        var sum = Block(H3("Riepilogo di cassa in PDF"), Small("Tutto quello fatto in cassa, giorno per giorno: scontrini con le righe e chi ha pagato, annulli, chiusure, totali per pagamento e per operatore, Da pagare incassati, non nello scontrino. Si scarica, non blocca niente"),
            Left(BA("Riepilogo di oggi (PDF)", () => Summary(d.S("oggi"), d.S("oggi")))), F.Grid2(F.Field("Dal", from), F.Field("Al", to)),
            Left(BA("Riepilogo del periodo (PDF)", () => Summary(from.SelectedDate?.ToString("yyyy-MM-dd") ?? d.S("oggi"), to.SelectedDate?.ToString("yyyy-MM-dd") ?? ""))).M(0, 10, 0, 0));
        var blocks = new List<FrameworkElement> { close, sum };
        if (canVoid)
        {
            var day = new DatePicker(); var doc = Box(""); doc.MaxLength = 12; doc.Tag = "2026-0012"; AgendaPage.Hint(doc);
            blocks.Add(Block(H3("Annulla un altro scontrino"), Small("Per uno scontrino di un altro giorno o fatto dalla cassa. Data e «Documento n.» sono stampati in fondo allo scontrino: nel numero 2026-0012, 2026 è la chiusura e 0012 il documento. Conserva lo scontrino sbagliato con l'annullo"),
                F.Grid2(F.Field("Data dello scontrino", day), F.Field("Documento n.", doc)),
                Left(F.Small(BA("Annulla lo scontrino", async () => { if (Ask("Mandare alla cassa l'annullo di questo scontrino?")) await Do(new JsonObject { ["azione"] = "void", ["day"] = day.SelectedDate?.ToString("yyyy-MM-dd") ?? "", ["doc"] = doc.Text }); }))).M(0, 10, 0, 0)));
        }
        var ch = F.Section("Chiusura, riepilogo e annulli"); ch.Margin = new Thickness(0, 0, 0, 12);
        low.Children.Add(Low(null, ch, F.Cols(blocks.Count, 16, blocks.ToArray())));
    }
    static T Left<T>(T e) where T : FrameworkElement { e.HorizontalAlignment = HorizontalAlignment.Left; return e; }

    /// <summary>_standby_list.html: who or the note, the total, since when, the rows.</summary>
    public static StackPanel StandbyText(JsonNode x, DateOnly today)
    {
        // the server sends the business's local time with its offset
        var when = DateTimeOffset.Parse(x.S("da")).DateTime;
        var line = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new Run(x.S("chi") != "" ? x.S("chi") : x.S("nota") != "" ? x.S("nota") : "Senza nome") { FontWeight = FontWeights.Bold });
        if (Num(x.S("totale")) != 0) line.Inlines.Add(" · " + Euro(x.S("totale")));
        var meta = new Run($" dalle {when:HH:mm}" + (DateOnly.FromDateTime(when) != today ? $" del {when:dd/MM}" : "") + (x.S("chi") != "" && x.S("nota") != "" ? " · " + x.S("nota") : "")) { FontSize = 13 }; meta.SetResourceReference(TextElement.ForegroundProperty, "Muted"); line.Inlines.Add(meta);
        var items = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }; var first = true;
        foreach (var r in x.A("righe"))
        {
            if (!first) items.Inlines.Add(" · "); first = false; items.Inlines.Add(r.S("desc"));
            if (r.S("staff") != "") { var st = new Run($" ({r.S("staff")})"); st.SetResourceReference(TextElement.ForegroundProperty, "Muted"); items.Inlines.Add(st); }
        }
        return Col(line, items);
    }
    /// <summary>Riprendi and the ×; the bill open here goes to the Parcheggio when another is taken back.</summary>
    FrameworkElement StandbyActions(string id, bool here)
    {
        var resume = F.Small(BA("Riprendi", () => Do(new JsonObject { ["azione"] = "resume", ["standby_id"] = id, ["swap"] = Typed(Draft()) ? Draft() : null }, true), "Primary"));
        var drop = F.Small(BA("×", async () => { if (Ask("Togliere questo conto dal Parcheggio? Le righe si perdono, nessuno scontrino è stato fatto.")) await Do(new JsonObject { ["azione"] = "drop_standby", ["standby_id"] = id }); }, "Ghost")); drop.ToolTip = "Togli dal Parcheggio";
        return Row(resume, drop.M(8, 0, 0, 0));
    }
}

/// <summary>templates/cassa_copy.html: a courtesy copy of the receipt for the client, to print.</summary>
public sealed class ReceiptCopyPage(string sede, string id) : Page
{
    public Page Back;
    public override async Task Load()
    {
        var d = await Api.Current.Call("cassa.copia", new { sede, id });
        var body = Col(T(Business.S("nome"), "H2"));
        foreach (var x in d.A("testa")) body.Children.Add(T(x.ToString(), size: 14).M(0, 0, 0, 6));
        foreach (var x in d.A("righe")) body.Children.Add(T(x.ToString(), size: 14).M(0, 2, 0, 2));
        if (d.S("stato") == "void") body.Children.Add(T("Annullato", size: 14, bold: true).M(0, 8, 0, 0));
        body.Children.Add(T("Copia non valida ai fini fiscali", size: 13, color: Res("Muted")).M(0, 8, 0, 0));
        var card = new Border { Style = Style("Card"), Child = body, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        void Print()
        {
            var doc = Printing.Doc(); doc.Blocks.Add(Printing.P(Business.S("nome"), 15, true, null, 8));
            foreach (var x in d.A("testa")) doc.Blocks.Add(Printing.P(x.ToString()));
            foreach (var x in d.A("righe")) doc.Blocks.Add(Printing.P(x.ToString(), 11, false, null, 1));
            if (d.S("stato") == "void") doc.Blocks.Add(Printing.P("Annullato", 11, true));
            doc.Blocks.Add(Printing.P("Copia non valida ai fini fiscali", 9, false, Printing.Grey));
            Printing.SavePdf(doc, "Copia dello scontrino");
        }
        Content = F.Page(1480, F.Heading(null, "Copia dello scontrino", "Una copia di cortesia per il cliente: il documento commerciale valido è quello emesso dal registratore",
            B("Torna alla cassa", () => Main.ShowPage(new CassaPage()), "Ghost"), B("Stampa", Print, "Primary")), card);
    }
}
