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

/// <summary>templates/booking_detail.html: the booking with all its details on the left (visit, Ripeti, Sposta la serie,
/// Partecipanti), and on the right Conto, Azioni and Note interne.</summary>
public sealed class BookingDetailPage(string id) : Page
{
    JsonNode b;
    AgendaPage z;
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    DateTime Local(string iso) => z.Local(iso);
    static string Euro(string v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d.ToString("#,##0.00", It) + " €" : v;

    public override async Task Load()
    {
        z = new AgendaPage { Main = Main }; z.UseZone(Business.S("fuso"));
        b = await Api.Current.Call("prenotazione.dettaglio", new { id });
        Draw();
    }
    async Task Do(string name, object args, string done = null)
    {
        await Api.Current.Call(name, args);
        if (done != null) Main.Flash(done);
        await Load();
    }

    void Draw()
    {
        var d = b["dati"]; var a = Local(b.S("inizio")); var e = Local(b.S("fine")); var status = b.S("stato"); var kind = b.S("tipo");
        var write = Can("booking.write"); var cancel = Can("booking.cancel");
        // ----- heading
        var acts = new List<FrameworkElement> { F.Status(status) };
        if (b.Bo("clienteOk")) acts.Add(MainWindow.Pill("✓ Confermata dal cliente", "ok"));
        var m = b["marchi"];
        if (m != null)
        {
            if (m.Bo("vip")) acts.Add(MainWindow.Pill("★ VIP", "warn"));
            if (m.I("assenze") > 0) acts.Add(MainWindow.Pill($"{m.I("assenze")} assenz{(m.I("assenze") == 1 ? "a" : "e")}", "danger"));
            if (m.I("tardive") > 0) acts.Add(MainWindow.Pill($"{m.I("tardive")} disdett{(m.I("tardive") == 1 ? "a" : "e")} all'ultimo", "warn"));
            if (m.Bo("bloccato")) acts.Add(MainWindow.Pill("Non prenotabile online", "danger"));
            foreach (var t in m.A("etichette")) acts.Add(MainWindow.Pill(t.ToString()));
        }
        if (b["contatto"] != null && Can("contacts.read")) acts.Add(B("Scheda cliente e storico", () => Main.ShowPage(new ClientPage(b["contatto"].S("id")))));
        if (b.S("whatsapp") != "") acts.Add(B("WhatsApp", () => Process.Start(new ProcessStartInfo(b.S("whatsapp")) { UseShellExecute = true })));
        var when = a.ToString("dddd dd MMMM yyyy, HH:mm", It); when = char.ToUpper(when[0]) + when[1..];
        var heading = F.Heading("Prenotazione " + b.S("codice"), $"{d.S("first_name")} {d.S("last_name")}".Trim(), $"{b.S("servizio")} · {when}", acts.ToArray());
        var top = Col(F.Back("Agenda e prenotazioni", () => Main.Show("agenda")), heading);
        // ----- notices
        if (b.S("notaCliente") != "") top.Children.Add(F.Notice(Col(T("✎ Nota sul cliente", size: 13.5, bold: true), T(b.S("notaCliente"), size: 13.5)), "warning"));
        if (d["allergies"] is JsonArray al && al.Count > 0) top.Children.Add(F.Notice(Col(T("! Intolleranze e allergie segnalate", size: 13.5, bold: true), T(string.Join(" · ", al.Select(x => x.ToString())), size: 13.5)), "warning"));
        if (status == "cancelled" && b.S("motivo") != "") top.Children.Add(F.Notice($"Annullata: {b.S("motivo").ToLowerInvariant()}" + (d.S("cancel_note") != "" ? " · " + d.S("cancel_note") : "")));
        if (b.Bo("rifiutata") && status != "cancelled") top.Children.Add(F.Notice("Il cliente ha scritto che non viene, dal promemoria, quando il termine per annullare online era già passato: annullala tu, se va bene, o chiamalo", "warning"));
        if (b["forzata"] is JsonArray fz && fz.Count > 0) top.Children.Add(F.Notice("Forzata al banco: " + string.Join(", ", fz.Select(x => x.ToString())), "warning"));
        if (b.S("testDaFare") != "") top.Children.Add(F.Notice($"Test da fare: {b.S("testDaFare")}. Segnalo nella scheda del cliente quando è fatto", "warning"));

        // ----- Tutti i dettagli
        var dl = new Grid(); dl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); dl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); dl.ColumnDefinitions.Add(new ColumnDefinition());
        void Row2(string k, FrameworkElement v)
        {
            var r = dl.RowDefinitions.Count; dl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = T(k, size: 15, color: Res("Muted")).M(0, 0, 0, 10); v.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 2); dl.Children.Add(l); dl.Children.Add(v);
        }
        void Line(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) Row2(k, T(v, size: 15)); }
        var whenText = kind == "capacity" && !b.Bo("turnover") ? $"{a:dd/MM/yyyy HH:mm}"
            : kind == "day" ? $"dal {a:dd/MM/yyyy}, arrivo {a:HH:mm}, al {e:dd/MM/yyyy}, partenza {e:HH:mm} · {(e.Date - a.Date).Days} nott{((e.Date - a.Date).Days == 1 ? "e" : "i")}"
            : a.Date == e.Date ? $"{a:dd/MM/yyyy HH:mm}–{e:HH:mm}" : $"{a:dd/MM/yyyy HH:mm} – {e:dd/MM/yyyy HH:mm}";
        Line("Quando", whenText); Line("Sede", b.S("sede"));
        if (b.S("videochiamata") != "") Row2("A distanza", F.Link("Link della videochiamata", () => Process.Start(new ProcessStartInfo(b.S("videochiamata")) { UseShellExecute = true }), 15));
        var origin = b.S("origine") switch { "public" => ("globe", "Pagina di prenotazione"), "wordpress" => ("plug", "Sito"), "desk" => ("shop", "Al banco"), "walkin" => ("user", "Senza appuntamento"), "import" => ("archive", "Programma precedente"), "agenda" => ("day", "Agenda del sito"), _ => ("", "") };
        if (origin.Item1 != "") Row2("Arrivata da", Row(Icons.Icon(origin.Item1, Res("Ink"), 14).M(0, 0, 6, 0), T(origin.Item2 + (b.S("fonte") != "" ? " · " + b.S("fonte") : ""), size: 15)));
        Line(kind == "day" ? b.S("unita") : "Con", b.S("operatore"));
        if (b.S("sostituto") != "") Line("Sostituto", b.S("sostituto") + (b.S("originale") != "" ? $" al posto di {b.S("originale")}" : ""));
        if (d["participants"] is JsonArray pa && pa.Count > 0 && !d.Bo("masked")) Line("Partecipanti", string.Join(", ", pa.Select(x => x.ToString())));
        Line("Insieme a", b.S("cabina")); Line("Posa", b.S("posa")); Line("Prestazione", b.S("variante"));
        if (b.S("totale") != "")
        {
            if (b.S("sconto") != "")
            {
                var old = T(Euro(b.S("prezzo")), size: 15, color: Res("Muted")); old.TextDecorations = TextDecorations.Strikethrough;
                Row2("Prezzo", Row(old, T(Euro(b.S("totale")), size: 15, bold: true).M(6, 0, 8, 0), MainWindow.Pill("sconto " + b.S("sconto"))));
            }
            else Line("Prezzo", Euro(b.S("totale")));
        }
        if (b.I("persone") > 1 || b.I("maxPersone") > 1) Line("Persone", b.I("persone").ToString());
        if (b.S("sala") != "" || b.S("tavoli") != "") Line("Tavolo", (b.S("tavoli") != "" ? b.S("tavoli") : "da assegnare") + (b.S("sala") != "" ? " · " + b.S("sala") : ""));
        if (!d.Bo("masked")) { Line("Email", d.S("email")); Line("Telefono", d.S("phone")); }
        foreach (var (k, v) in d["answers"] as JsonObject ?? new JsonObject())
        {
            if (v == null || v is JsonValue jv && (jv.TryGetValue<bool>(out var bb) && !bb || jv.TryGetValue<string>(out var ss) && ss == "")) continue;
            Line(b["campi"]?[k]?.ToString() ?? k, v is JsonValue j2 && j2.TryGetValue<bool>(out _) ? "Sì" : v.ToString());
        }
        var details = F.Card(T("Tutti i dettagli", "H2"), dl);
        if (!d.Bo("notes_hidden")) { ((StackPanel)details.Child).Children.Add(T("Note del cliente", size: 15, bold: true).M(0, 8, 0, 8)); ((StackPanel)details.Child).Children.Add(T(d.S("notes") != "" ? d.S("notes") : "Nessuna nota.", size: 14)); }
        var left = Col(details);

        // ----- the client record: what was written for this booking, or the form, and the times before
        if (b["scheda"] is JsonNode rec && !d.Bo("notes_hidden"))
        {
            var h2 = T(rec.S("titolo"), "H2"); h2.Margin = new Thickness(0, 2, 0, 0);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            if (b["contatto"] != null && Can("contacts.read")) { var all = F.Small(B("Tutta la scheda", () => Main.ShowPage(new ClientPage(b["contatto"].S("id"))), "Ghost")); DockPanel.SetDock(all, Dock.Right); head.Children.Add(all); }
            head.Children.Add(Col(T("Scheda cliente", "Eyebrow"), h2));
            var box = Col(head);
            FrameworkElement Staff(string name, bool avatar) => name == "" ? null : Row(avatar ? F.Avatar(name, 24).M(0, 0, 7, 0) : null, T("con " + name, size: 13, bold: true, color: Res("Muted")));
            Border Entry(JsonNode t, bool linked, bool small)
            {
                var h3 = T(t.S("servizio"), size: 16, bold: true); h3.TextWrapping = TextWrapping.Wrap;
                var top = new DockPanel(); if (Staff(t.S("operatore"), linked) is FrameworkElement st) { st.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(st, Dock.Right); top.Children.Add(st); }
                top.Children.Add(h3); var col = Col(top);
                if (t.S("dettagli") != "") col.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 10, 13, 10), Margin = new Thickness(0, 10, 0, 0), Child = T(t.S("dettagli"), size: 14.5) });
                if (!linked && t.A("foto").Any()) col.Children.Add(PhotoGrid.Make(t.A("foto").Select(x => x.ToString()), true, false, null, 0, 0, Load));
                var date = DateOnly.Parse(t.S("data")).ToString("d MMMM yyyy", It);
                col.Children.Add(T(linked ? "✓ Registrato per questa prenotazione, " + date : date, size: 12.5, color: Res("Muted")).M(0, 10, 0, 0));
                return new Border { BorderBrush = Res(linked ? "Ok" : "Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Background = Res(linked ? "OkSoft" : "Surface"), Padding = small ? new Thickness(13, 10, 13, 10) : new Thickness(16, 14, 16, 14), Child = col, Margin = new Thickness(0, 0, 0, 10) };
            }
            foreach (var t in rec.A("collegate")) box.Children.Add(Entry(t, true, false));
            if (!rec.A("collegate").Any())
            {
                if (rec.Bo("aperta") && write)
                {
                    var pre = rec["precompilata"];
                    var date = new DatePicker { SelectedDate = DateTime.Parse(pre.S("date")) }; var what = F.Suggest(rec.A("cose").Select(x => x.ToString()), pre.S("service"), 120); var staff = F.Suggest(rec.A("persone").Select(x => x.ToString()), pre.S("staff"), 100);
                    var notesBox = Box("", true); notesBox.MaxLength = 4000; notesBox.MinHeight = 100; notesBox.Tag = rec.S("suggerimento"); AgendaPage.Hint(notesBox);
                    var g = new Grid();
                    foreach (var w in new[] { new GridLength(165), new GridLength(14), new GridLength(1.4, GridUnitType.Star), new GridLength(14), new GridLength(1, GridUnitType.Star) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                    var f1 = F.Field("Data", date); var f2 = F.Field(rec.S("cosa"), what); var f3 = F.Field("Con chi", staff); Grid.SetColumn(f2, 2); Grid.SetColumn(f3, 4); g.Children.Add(f1); g.Children.Add(f2); g.Children.Add(f3);
                    var save = BA("Salva nella scheda", async () =>
                    {
                        var r = await Api.Current.Call("prenotazione.voce", new { id, azione = "treatment", date = date.SelectedDate?.ToString("yyyy-MM-dd") ?? "", service = what.Text, staff = staff.Text, details = notesBox.Text });
                        Main.Flash(r.S("testo")); await Load();
                    }, "Primary"); save.HorizontalAlignment = HorizontalAlignment.Left; save.Margin = new Thickness(0, 14, 0, 0);
                    box.Children.Add(T("Cosa è stato fatto in questo appuntamento: resta nella scheda del cliente per le prossime volte", size: 14, color: Res("Muted"))); box.Children.Add(g); box.Children.Add(F.Label("Dettagli")); box.Children.Add(notesBox); box.Children.Add(save);
                }
                else if (!rec.Bo("aperta")) box.Children.Add(T("Dopo l'appuntamento qui registri cosa è stato fatto.", size: 14, color: Res("Muted")));
            }
            if (rec.A("precedenti").Any())
            {
                box.Children.Add(T("Le volte precedenti", size: 15, bold: true).M(0, 16, 0, 10));
                foreach (var t in rec.A("precedenti"))
                {
                    var day = DateOnly.Parse(t.S("data"));
                    var dd = T(day.ToString("dd"), size: 17, bold: true, color: Res("Brand")); dd.HorizontalAlignment = HorizontalAlignment.Center; var mm = T(day.ToString("MMM", It).ToUpperInvariant(), size: 11, bold: true, color: Res("Muted")); mm.HorizontalAlignment = HorizontalAlignment.Center;
                    var dateBox = new Border { Width = 48, Padding = new Thickness(0, 6, 0, 6), CornerRadius = new CornerRadius(14), Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Child = Col(dd, mm), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
                    var row = new DockPanel(); DockPanel.SetDock(dateBox, Dock.Left); row.Children.Add(dateBox); row.Children.Add(Entry(t, false, true));
                    box.Children.Add(row);
                }
            }
            left.Children.Add(new Border { Style = Style("Card"), Child = box, Margin = new Thickness(0, 0, 0, 14) });
        }
        // ----- the photos of the work of that day
        if (b["foto"] is JsonNode ph)
        {
            var head = new DockPanel();
            if (b["contatto"] != null && Can("contacts.read")) { var all = F.Small(B("Tutte le sue foto", () => Main.ShowPage(new ClientPage(b["contatto"].S("id")) { StartTab = "foto" }), "Ghost")); all.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(all, Dock.Right); head.Children.Add(all); }
            head.Children.Add(Col(T("Foto del " + DateOnly.Parse(ph.S("giorno")).ToString("d MMMM yyyy", It), "H2"), T($"Il lavoro fatto, nella scheda del cliente: al massimo {ph.I("max")} per giorno, rimpicciolite da sole.", size: 13, color: Res("Muted"))));
            left.Children.Add(F.Card(head, PhotoGrid.Make(ph.A("righe").Select(x => x.ToString()), false, ph.Bo("scrivi"), new JsonObject { ["cliente"] = b["contatto"].S("id"), ["prenotazione"] = id }, ph.I("liberi"), ph.I("max"), Load)));
        }
        // ----- Visita
        var visit = b.A("visita").ToList();
        if (visit.Count > 1)
        {
            var list = Col();
            foreach (var v in visit)
            {
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
                FrameworkElement right = v.S("id") == id ? MainWindow.Pill("Questa") : F.Link("Apri", () => Main.ShowPage(new BookingDetailPage(v.S("id"))));
                DockPanel.SetDock(right, Dock.Right); row.Children.Add(right);
                row.Children.Add(Col(T($"{Local(v.S("inizio")):HH:mm}–{Local(v.S("fine")):HH:mm} · {v.S("servizio")}", size: 14, bold: true), v.S("operatore") != "" ? T("con " + v.S("operatore"), size: 13, color: Res("Muted")) : null));
                list.Children.Add(row);
            }
            left.Children.Add(F.Card(T($"Visita con {visit.Count} servizi", "H2"), T("Uno dopo l'altro: si spostano e si annullano insieme, il cliente riceve un'email sola", size: 14, color: Res("Muted")), list));
        }
        // ----- Ripeti
        if (b.Bo("ripetibile") && write)
        {
            var every = new ComboBox { ItemsSource = new[] { "settimana", "2 settimane", "3 settimane", "4 settimane" }, SelectedIndex = 0 }; var until = new DatePicker();
            var plan = Col();
            var check = BA("Controlla le date", async () =>
            {
                var r = await Api.Current.Call("prenotazione.serie", new { id, ogni = every.SelectedIndex + 1, fino = until.SelectedDate?.ToString("yyyy-MM-dd") });
                plan.Children.Clear(); var free = 0;
                foreach (var x in r.A("date"))
                {
                    var why = x.S("perche"); if (why == "") free++;
                    var line = new TextBlock { FontSize = 14, Margin = new Thickness(0, 8, 0, 8) }; line.Inlines.Add($"{Local(x.S("inizio")).ToString("ddd d/MM/yyyy HH:mm", It)} · ");
                    if (why == "") line.Inlines.Add("libera"); else { var run = new Run("non si può: " + why); run.SetResourceReference(TextElement.ForegroundProperty, "Danger"); line.Inlines.Add(run); }
                    plan.Children.Add(line);
                }
                var make = BA($"Crea {free} date libere", async () =>
                {
                    var rr = await Api.Current.Call("prenotazione.serie", new { id, ogni = every.SelectedIndex + 1, fino = until.SelectedDate?.ToString("yyyy-MM-dd"), crea = true });
                    Main.Flash(rr.I("fatte") > 0 ? $"Serie creata: {rr.I("fatte")} date in più." : "Nessuna data libera da aggiungere."); await Load();
                }, "Primary");
                make.IsEnabled = free > 0; make.HorizontalAlignment = HorizontalAlignment.Left; plan.Children.Add(make);
            });
            check.Padding = new Thickness(11, 5, 11, 5); check.FontSize = 13; check.VerticalAlignment = VerticalAlignment.Bottom; check.HorizontalAlignment = HorizontalAlignment.Left;
            var head = Row(T("Ripeti", "H2"), b.S("serie") != "" ? MainWindow.Pill("In serie").M(8, 0, 0, 8) : null);
            left.Children.Add(F.Card(head, T($"Lo stesso orario{(b.S("operatore") != "" ? " con " + b.S("operatore") : "")} ogni settimana o ogni poche settimane, fino a un anno: prima vedi le date che non si possono prenotare, poi crei le altre. Il cliente riceve i promemoria di ciascuna", size: 14, color: Res("Muted")),
                F.Grid2(F.Field("Ogni", every), F.Field("Fino al", until)), check.M(0, 12, 0, 0), plan));
        }
        // ----- Sposta la serie
        if (b.S("serie") != "" && write && status is "pending" or "confirmed")
        {
            var day = new DatePicker { SelectedDate = a.Date }; var time = Box(a.ToString("HH:mm")); time.Width = 90;
            var ops = new ComboBox { ItemsSource = b.A("operatoriSerie").Select(o => new ComboBoxItem { Content = o.S("nome"), Tag = o.S("id") }).ToList() };
            ops.SelectedItem = ops.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == b.S("resourceId"));
            var following = new CheckBox { Content = "Questa e le successive", IsChecked = true, Margin = new Thickness(0, 14, 0, 6) };
            var plan = Col();
            object Args(bool go) => new { id, start = $"{day.SelectedDate:yyyy-MM-dd}T{time.Text.Trim()}", resource = (ops.SelectedItem as ComboBoxItem)?.Tag as string ?? "", successive = following.IsChecked == true, sposta = go };
            var check = BA("Controlla le date", async () =>
            {
                var r = await Api.Current.Call("prenotazione.spostaSerie", Args(false));
                plan.Children.Clear(); var free = 0;
                foreach (var x in r.A("date"))
                {
                    var why = x.S("perche"); if (why == "") free++;
                    var line = new TextBlock { FontSize = 14, Margin = new Thickness(0, 8, 0, 8) }; line.Inlines.Add($"{Local(x.S("da")).ToString("ddd d/MM HH:mm", It)} → {Local(x.S("a")).ToString("ddd d/MM/yyyy HH:mm", It)} · ");
                    if (why == "") line.Inlines.Add("libera"); else { var run = new Run("non si può: " + why); run.SetResourceReference(TextElement.ForegroundProperty, "Danger"); line.Inlines.Add(run); }
                    plan.Children.Add(line);
                }
                var make = BA($"Sposta {free} date", async () =>
                {
                    var rr = await Api.Current.Call("prenotazione.spostaSerie", Args(true)); var n = rr.I("fatte");
                    Main.Flash(n > 1 ? $"Spostate {n} date." : n == 1 ? "Spostata." : "Nessuna data spostata: gli orari non sono più liberi."); await Load();
                }, "Primary");
                make.IsEnabled = free > 0; make.HorizontalAlignment = HorizontalAlignment.Left; plan.Children.Add(make);
            });
            check.Padding = new Thickness(11, 5, 11, 5); check.FontSize = 13; check.HorizontalAlignment = HorizontalAlignment.Left;
            left.Children.Add(F.Card(T("Sposta la serie", "H2"), T("Scegli il nuovo giorno e l'ora di questa data: le prossime si spostano degli stessi giorni, alla stessa ora. Prima vedi quelle che non si possono spostare e perché, poi sposti le altre. Il cliente riceve un'email sola", size: 14, color: Res("Muted")),
                F.Grid2(F.Field("Nuovo giorno e ora", Row(day, time.M(8, 0, 0, 0))), ops.Items.Count > 0 ? F.Field("Con", ops) : null), following, check, plan));
        }
        if (b.Bo("iscritti") && Can("booking.read")) left.Children.Add(B($"Iscritti e presenze di questa {(kind == "event" ? "data" : "lezione")}", () => Say("Iscritti e presenze arrivano con i prossimi passaggi.")).M(0, 0, 0, 14));
        // ----- Partecipanti
        if (b.I("persone") > 1 && write && !d.Bo("masked"))
        {
            var names = Box(string.Join("\n", (d["participants"] as JsonArray ?? new JsonArray()).Select(x => x.ToString())), true); names.MinHeight = 22 * b.I("persone") + 16;
            var save = BA("Salva", () => Do("prenotazione.partecipanti", new { id, nomi = names.Text }, "Partecipanti salvati.")); save.Padding = new Thickness(11, 5, 11, 5); save.FontSize = 13; save.HorizontalAlignment = HorizontalAlignment.Left;
            left.Children.Add(F.Card(T("Partecipanti", "H2"), F.Label("Nomi di chi viene", "(facoltativi, uno per riga)"), names, save.M(0, 12, 0, 0)));
        }

        // ----- aside: Conto, Azioni, Note interne
        var aside = Col();
        if (write)
        {
            if (Can("sale.write") && status != "cancelled")
                aside.Children.Add(F.Card(T("Conto", "H2"), T("Lo scontrino lo stampa il registratore di cassa: qui restano i dati.", size: 14, color: Res("Muted")), Wide(B("Chiudi il conto", () => Main.Show("cassa"), "Primary"))));
            var actions = Col(T("Azioni", "H2"));
            if (b.Bo("annulla") && cancel)
            {
                var txt = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }; txt.Inlines.Add("Ora è "); txt.Inlines.Add(new Run(StatusName(status).ToLowerInvariant()) { FontWeight = FontWeights.Bold }); txt.Inlines.Add(". Sbagliato?");
                var undo = BA("Torna indietro", () => Do("prenotazione.stato", new { id, annulla = true })); undo.Padding = new Thickness(11, 5, 11, 5); undo.FontSize = 13;
                var bar = new DockPanel(); DockPanel.SetDock(undo, Dock.Right); bar.Children.Add(undo); bar.Children.Add(txt);
                actions.Children.Add(F.Notice(bar));
            }
            if (status is "pending" or "confirmed") actions.Children.Add(Wide(B("Sposta prenotazione", () => Main.ShowPage(new BookingPage { Moving = b, Done = () => Main.ShowPage(new BookingDetailPage(id)) }))));
            if (cancel)
            {
                Button Act(string text, string to, string style = null, string confirm = null) => Stack(BA(text, async () => { if (confirm == null || Ask(confirm)) await Do("prenotazione.stato", new { id, stato = to }); }, style));
                if (status == "pending")
                {
                    actions.Children.Add(T("Richiesta online: aspetta la vostra risposta e intanto tiene il posto. Il cliente riceve un'email in tutti e due i casi", size: 14, color: Res("Muted")).M(0, 12, 0, 0));
                    actions.Children.Add(Act("Accetta la richiesta", "confirmed", "Primary")); actions.Children.Add(Act("Rifiuta", "reject", "DangerButton", "Rifiutare questa richiesta?"));
                }
                if (status == "confirmed" && b.Bo("oggiOk")) actions.Children.Add(Act("Il cliente è arrivato", "arrived"));
                if ((status == "confirmed" && b.Bo("oggiOk") || status == "arrived") && b.Bo("inCorso")) actions.Children.Add(Act("In corso", "in_progress"));
                if (status is "confirmed" or "arrived" or "in_progress") actions.Children.Add(Act("Segna completata", "completed"));
                if (status == "confirmed") actions.Children.Add(Act("Cliente assente", "no_show", "Ghost"));
                if (status is "completed" or "no_show" or "cancelled")
                {
                    actions.Children.Add(T((status == "cancelled" ? "Annullata" : "Chiusa") + ": se è stato un errore, riportala a confermata" + (status == "cancelled" ? ", se il posto è ancora libero" : ""), size: 14, color: Res("Muted")).M(0, 12, 0, 0));
                    actions.Children.Add(Act("Riporta a confermata", "confirmed"));
                }
                else
                {
                    var reasons = new ComboBox { ItemsSource = b.A("motivi").Select(r => new ComboBoxItem { Content = r.S("nome"), Tag = r.S("id") }).ToList(), SelectedIndex = 0 };
                    var note = Box(""); note.MaxLength = 300;
                    var whole = new CheckBox { Content = $"Anche le prossime {b.I("prossimeSerie")} date della serie", Margin = new Thickness(0, 12, 0, 0), Visibility = b.I("prossimeSerie") > 0 ? Visibility.Visible : Visibility.Collapsed };
                    var go = Stack(BA("Annulla la prenotazione", async () =>
                    {
                        if (!Ask("Annullare questa prenotazione?")) return;
                        await Do("prenotazione.stato", new { id, stato = "cancelled", motivo = (string)((ComboBoxItem)reasons.SelectedItem).Tag, nota = note.Text, serie = whole.IsChecked == true });
                    }, "DangerButton"));
                    actions.Children.Add(new Expander { Header = "Annulla la prenotazione", Margin = new Thickness(0, 14, 0, 0), Content = Col(F.Label("Motivo"), reasons, F.Label("Nota", "(facoltativa)"), note, whole, go) });
                }
            }
            aside.Children.Add(new Border { Style = Style("Card"), Child = actions, Margin = new Thickness(0, 0, 0, 14) });
        }
        if (!d.Bo("notes_hidden"))
        {
            var box = Col(T("Note interne", size: 15, bold: true).M(0, 0, 0, 8), T("Visibili nel portale, mai inviate nelle email.", size: 14, color: Res("Muted")).M(0, 0, 0, 8));
            if (write)
            {
                var text = Box(d.S("internal_notes"), true); text.MinHeight = 110;
                box.Children.Add(text); box.Children.Add(Stack(BA("Salva note", () => Do("prenotazione.note", new { id, note = text.Text }, "Note salvate."))));
            }
            else box.Children.Add(T(d.S("internal_notes"), size: 14));
            aside.Children.Add(new Border { Style = Style("Card"), Child = box });
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetColumn(aside, 2); grid.Children.Add(left); grid.Children.Add(aside);
        Content = F.Page(1480, top, grid);
    }
    static T Wide<T>(T b) where T : FrameworkElement { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = new Thickness(0, 16, 0, 0); return b; }
    static T Stack<T>(T b) where T : FrameworkElement { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = new Thickness(0, 10, 0, 0); return b; }
}
