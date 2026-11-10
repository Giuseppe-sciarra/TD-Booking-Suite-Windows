using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>A page of the main window: built on show, refreshed when asked.</summary>
public abstract class Page : UserControl
{
    public MainWindow Main { get; set; }
    public JsonNode Business => Main.Business;
    public bool Can(string perm) => Main.Can(perm);
    public abstract Task Load();
}

/// <summary>The program's window, laid out as templates/base.html: the dark side bar (logo, «Stai lavorando in», the
/// groups of pages with their icons, Guida, the account and Esci at the bottom), the top bar with the business and
/// Tema, the notices, the page.</summary>
public sealed class MainWindow : Window
{
    public bool Replaced;
    public JsonNode Me { get; }
    public JsonNode Business { get; private set; }
    readonly ContentControl content = new();
    readonly DockPanel bar = new() { Width = 228 };
    readonly Border top = new() { Height = 50 };
    readonly StackPanel notices = new(), nudges = new();
    readonly Dictionary<string, (Func<Page> Make, string Nav)> pages = new();
    readonly Dictionary<string, RadioButton> entries = new();
    string currentKey = "";
    Page currentPage;

    public MainWindow(JsonNode me)
    {
        Me = me;
        Title = "TD Booking Suite"; Width = 1440; Height = 900; MinWidth = 1000; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
        Palette.Load();
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(notices, Dock.Top); DockPanel.SetDock(nudges, Dock.Top);
        right.Children.Add(top); right.Children.Add(notices); right.Children.Add(nudges); right.Children.Add(content);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        root.Children.Add(bar); root.Children.Add(right);
        Content = root;
        SetResourceReference(BackgroundProperty, "Bg");
        Palette.Changed += Repaint;
        Closed += (_, _) => Palette.Changed -= Repaint;
        Loaded += async (_, _) => await Guard(Enter);
    }

    public bool Can(string p) => Business != null && Business.A("permessi").Any(x => x.ToString() == p);
    public Page CurrentPage => currentPage;

    async Task Enter()
    {
        var list = Me.A("attivita").ToList();
        if (list.Count == 0) { Build(); Show(Me.Bo("staff") ? "licenza" : "account"); return; }
        var slug = list[0].S("slug");
        if (list.Count > 1)
        {
            var pick = new ComboBox { ItemsSource = list.Select(x => x.S("nome")).ToList(), SelectedIndex = 0 };
            var w = new Window { Title = "Scegli l'attività", Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
            w.Content = Col(T("Quale attività apri?", "H2"), pick.M(0, 8, 0, 16), B("Apri", () => w.DialogResult = true, "Primary")).M(24);
            w.ShowDialog();
            slug = list[Math.Max(0, pick.SelectedIndex)].S("slug");
        }
        await Open(slug);
    }

    public async Task Open(string slug)
    {
        Api.Current.Business = slug;
        Business = await Api.Current.Call("attivita.io");
        Palette.Brand(Business.S("colore"), Business.S("coloreScuro"));
        Build();
        Show(Can("booking.read") ? "agenda" : pages.Keys.First());
    }

    /// <summary>After something of the business changed (logo, colour, type, till): its data read again, the menu rebuilt, the page shown.</summary>
    public async Task Refresh(string key)
    {
        Business = await Api.Current.Call("attivita.io");
        Palette.Brand(Business.S("colore"), Business.S("coloreScuro"));
        Build(); Show(key);
    }

    /// <summary>After Tema changes: the bars and the page drawn again with the new colours.</summary>
    void Repaint() { if (Business != null || Me != null) { Build(); if (currentKey != "") Show(currentKey); } }

    // ---------- the side bar and the top bar
    static FontFamily Head => (FontFamily)Application.Current.Resources["FontDisplay"];

    void Build()
    {
        bar.Children.Clear(); pages.Clear(); entries.Clear();
        bar.Background = Res("Side");
        var t = Business; var staff = Me.Bo("staff");
        var name = t?.S("nome") ?? "TD Booking Suite";
        // the logo: the business's own when it has one, else the TD mark
        FrameworkElement mark;
        if (t != null && t.S("logo") != "")
        {
            var img = new BitmapImage(); img.BeginInit(); img.StreamSource = new MemoryStream(Convert.FromBase64String(t.S("logo"))); img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit();
            mark = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(13), Padding = new Thickness(4), Width = t.Bo("logoLargo") ? 96 : 42, Height = 42, Child = new Image { Source = img, Stretch = Stretch.Uniform } };
        }
        else mark = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(13),
            Background = new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(0xE0, 0x56, 0x9B), 0), new(Color.FromRgb(0x8B, 0x67, 0xB8), 0.7) }, new Point(0, 0), new Point(1, 1)),
            Child = new TextBlock { Text = "TD", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = Head },
        };
        var title = T(name, size: 15, bold: true, color: Res("SideInk")); title.FontFamily = Head; title.TextTrimming = TextTrimming.CharacterEllipsis; title.MaxHeight = 40;
        var logo = new DockPanel { Margin = new Thickness(18, 16, 12, 0) };
        DockPanel.SetDock(mark, Dock.Left); logo.Children.Add(mark);
        logo.Children.Add(Col(title, T("Edizione Windows", size: 12, color: Res("SideMuted")).M(0, 2, 0, 0)).M(12, 0, 0, 0));
        ((StackPanel)logo.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(logo, Dock.Top); bar.Children.Add(logo);

        // the bottom: who, role, Account e sicurezza, Esci
        var bottom = Col(T(Me.S("nome"), size: 13, bold: true, color: Res("SideInk")), T(RoleText(), size: 13, color: Res("SideMuted")).M(0, 0, 0, 8),
            SideLink("user", "Account e sicurezza", () => Show("account")), SideLink("logout", "Esci", () => Shell.Instance.SignedOut(true)).M(0, 6, 0, 0)).M(22, 14, 12, 14);
        DockPanel.SetDock(bottom, Dock.Bottom); bar.Children.Add(bottom);

        var nav = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        if (t != null)
            nav.Children.Add(new Border
            {
                BorderBrush = Res("SideLine"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 14, 0, 10),
                Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)),
                Child = Col(T("Stai lavorando in", size: 11.5, color: Res("SideMuted")), T(name, size: 14, bold: true, color: Brushes.White).M(0, 2, 0, 2), T(t.S("ruoloNome"), size: 13, color: Res("Pastel"))),
            });
        StackPanel group = null;
        void Section(string label)
        {
            group = new StackPanel();
            var l = T(label.ToUpperInvariant(), size: 11, bold: true, color: Res("SideMuted")); l.FontWeight = FontWeights.Bold;
            Typography.SetCapitals(l, FontCapitals.Normal);
            nav.Children.Add(new Border
            {
                BorderBrush = Res("SideLine"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)),
                Padding = new Thickness(6, 4, 6, 6), Margin = new Thickness(0, 0, 0, 12), Tag = group,
                Child = Col(new Border { BorderBrush = Res("SideLine"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 6, 6, 7), Margin = new Thickness(4, 2, 4, 5), Child = l }, group),
            });
        }
        void Add(string key, string icon, string label, Func<Page> make, bool when = true, int count = 0)
        {
            if (!when) return;
            pages[key] = (make, key);
            var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel();
            var ico = Icons.Icon(icon, null).M(0, 0, 10, 0); DockPanel.SetDock(ico, Dock.Left); row.Children.Add(ico);
            if (count > 0)
            {
                var badge = new Border { Background = Res("Pink"), CornerRadius = new CornerRadius(999), MinWidth = 20, Height = 20, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = count.ToString(), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge);
            }
            row.Children.Add(text);
            var r = new RadioButton { Style = Style("Nav"), Content = row, GroupName = "nav", Tag = key };
            r.Click += (_, _) => Show(key);
            entries[key] = r; group.Children.Add(r);
        }
        if (t != null)
        {
            var type = t.S("tipo");
            Section("Ogni giorno");
            Add("dashboard", "grid", "Dashboard", () => new DashboardPage(), Can("booking.read"));
            Add("agenda", "day", "Agenda e prenotazioni", () => new AgendaPage(), Can("booking.read"));
            Add("clienti", "users", "Clienti", () => new ClientsPage(), Can("contacts.read"));
            Add("cassa", "tag", "Cassa", () => new CassaPage(), Can("sale.write"));
            Add("standby", "clock", "Stand-by", () => new StandbyPage(), Can("sale.write"), t.I("standby"));
            Add("giftcard", "gift", "Gift card", () => new GiftCardsPage(), Can("sale.write"));
            Add("preventivi", "briefcase", "Preventivi", () => new QuotesPage(), Can("booking.write") && Can("pii.read"));
            Add("tavoli", "grid", "Sale e tavoli", () => new TablesPage(), Can("booking.read") && type == "ristorante");
            Add("alloggi", "home", "Alloggi", () => new Placeholder("Alloggi"), Can("booking.read") && t.Bo("soggiorni"));
            Section("Comunicazioni");
            Add("richiami", "bell", "Richiami", () => new RecallsPage(), Can("contacts.read"));
            Add("whatsapp", "chat", "Promemoria WhatsApp", () => new WhatsAppPage(), Can("booking.read") && Can("pii.read"));
            Add("marketing", "megaphone", "Marketing", () => new MarketingPage(), Can("campaign.manage") && (t.Bo("marketing") || t.Bo("esporta")));
            Add("promemoria", "bell", "Configurazione promemoria", () => new NotificationsPage(), Can("users.manage"));
            Add("api", "plug", "API e webhook", () => new ApiPage(), Can("users.manage") && t.Bo("api") && !staff);
            Section("La tua attività");
            Add("attesa", "hourglass", "Lista d'attesa", () => new WaitlistPage(), Can("booking.read"));
            Add("report", "list", "Report", () => new ReportsPage(), Can("booking.read"));
            Add("configura", "shop", "Configura l'attività", () => new SetupPage(), Can("catalog.write"));
            Add("registratore", "sliders", "Registratore di cassa", () => new RegisterPage(), Can("catalog.write") && t.Bo("cassaTitolare") && t.S("ruolo") == "owner" && !staff);
            Section("Controlli");
            Add("registro", "list", "Registro attività", () => new AuditPage(), Can("audit.read"));
            Add("accesso", "key", "Accesso dell'amministratore", () => new SupportPage(), Can("users.manage"));
            if (staff)
            {
                Section("Gestione tecnica");
                Add("persone", "badge", "Persone e permessi", () => new PeoplePage());
                Add("impostazioni", "globe", "Impostazioni e sito", () => new SettingsPage());
                Add("smtp", "mail", "Invio email", () => new MailSetupPage(false));
                Add("promemoria", "bell", "Configurazione promemoria", () => new NotificationsPage(), !Can("users.manage"));
                Add("testi", "bell", "SMS, WhatsApp e calendari", () => new TextsPage());
                Add("api", "plug", "API e webhook", () => new ApiPage());
                Add("registratore", "sliders", "Registratore di cassa", () => new RegisterPage());
            }
        }
        if (staff)
        {
            Section("Configurazione del programma");
            Add("programma", "grid", "Dashboard", () => new Placeholder("Configurazione del programma"));
            Add("attivita", "briefcase", "Clienti", () => new Placeholder("Attività"));
            Add("generali", "sliders", "Impostazioni generali", () => new Placeholder("Impostazioni generali"));
            Add("licenza", "key", "Licenza", () => new LicencePage());
            Add("postazioni", "monitor", "Postazioni", () => new WorkstationsPage(), !Me.Bo("standalone"));
            Add("backup", "archive", "Backup", () => new BackupPage());
            Add("rete", "globe", Me.Bo("standalone") ? "Online e sito" : "Rete e certificato", () => new NetworkPage());
            Add("stato", "list", "Stato del programma", () => new StatePage());
        }
        foreach (var box in nav.Children.OfType<Border>().Where(x => x.Tag is StackPanel sp && sp.Children.Count == 0).ToList()) nav.Children.Remove(box);
        // Guida, outside the groups
        group = new StackPanel { Margin = new Thickness(6, 0, 6, 0) }; nav.Children.Add(group);
        Add("guida", "help", "Guida", () => new GuidePage());
        pages["account"] = (() => new AccountPage(), "");
        pages["prenotazioni"] = (() => new BookingsPage(), "agenda");
        bar.Children.Add(new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });

        // the top bar: the crumb on the left, Tema on the right
        top.Background = Res("Surface"); top.BorderBrush = Res("Line"); top.BorderThickness = new Thickness(0, 0, 0, 1);
        var crumb = Row();
        if (t != null)
        {
            if (staff) { crumb.Children.Add(T("Configurazione del programma", size: 14, color: Res("Muted"))); crumb.Children.Add(T("  /  ", size: 14, color: Res("Muted"))); }
            crumb.Children.Add(T(name, size: 14, bold: true));
            crumb.Children.Add(Pill(t.S("ruolo") != "" ? RoleName(t.S("ruolo")) : "Gestione tecnica").M(10, 0, 0, 0));
        }
        else { crumb.Children.Add(T("Configurazione del programma", size: 14, bold: true)); crumb.Children.Add(Pill("Amministratore").M(10, 0, 0, 0)); }
        crumb.VerticalAlignment = VerticalAlignment.Center;
        var theme = new ComboBox { Width = 124, MinHeight = 32, Height = 32, ItemsSource = new[] { ("auto", "Automatico"), ("light", "Chiaro"), ("dark", "Scuro") }.Select(x => new ComboBoxItem { Content = x.Item2, Tag = x.Item1 }).ToList() };
        theme.SelectedItem = theme.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == Palette.Mode);
        theme.SelectionChanged += (_, _) => { var m = (string)((ComboBoxItem)theme.SelectedItem).Tag; if (m != Palette.Mode) Dispatcher.BeginInvoke(() => Palette.Set(m)); };
        var tools = Row(T("Tema", size: 13.5, color: Res("Muted")).M(0, 0, 10, 0), theme); tools.VerticalAlignment = VerticalAlignment.Center; ((TextBlock)tools.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        var line = new DockPanel { Margin = new Thickness(26, 0, 26, 0), LastChildFill = false };
        DockPanel.SetDock(tools, Dock.Right); line.Children.Add(tools); line.Children.Add(crumb);
        top.Child = line;
    }

    string RoleText()
    {
        if (Me.Bo("staff")) return "Amministratore del programma";
        var list = Me.A("attivita").ToList();
        var mine = list.FirstOrDefault(x => x.S("slug") == Api.Current.Business) ?? list.FirstOrDefault();
        return mine?.S("ruoloNome") ?? "";
    }
    static string RoleName(string role) => role switch { "owner" => "Titolare", "manager" => "Amministratore sede", "reception" => "Reception", "operator" => "Operatore sede", "marketing" => "Marketing", "viewer" => "Consultazione", _ => role };

    public static Border Pill(string text, string kind = "") => new()
    {
        Background = Res(kind switch { "ok" => "OkSoft", "warn" => "WarnSoft", "danger" => "DangerSoft", _ => "BrandSoft" }), BorderBrush = Res("Pastel"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999),
        Padding = new Thickness(10, 2, 10, 3), VerticalAlignment = VerticalAlignment.Center,
        Child = T(text, size: 12, bold: true, color: Res(kind switch { "ok" => "Ok", "warn" => "Warn", "danger" => "Danger", _ => "Brand" })),
    };

    static FrameworkElement SideLink(string icon, string text, Action click)
    {
        var row = Row(Icons.Icon(icon, Res("Pastel")).M(0, 0, 8, 0), T(text, size: 13, color: Res("Pastel")));
        ((TextBlock)row.Children[1]).FontWeight = FontWeights.Medium;
        row.Cursor = Cursors.Hand; row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    // ---------- pages
    public async void Show(string key)
    {
        if (!pages.TryGetValue(key, out var p)) return;
        currentKey = key;
        foreach (var (k, r) in entries) r.IsChecked = k == p.Nav;
        var page = p.Make(); page.Main = this;
        Put(page);
        await Guard(page.Load);
    }
    /// <summary>A page reached from another (a booking, Nuova prenotazione, a client's card): the menu stays where it was.</summary>
    public void ShowPage(Page page) { page.Main = this; Put(page); _ = Guard(page.Load); }
    void Put(Page page) { currentPage = page; content.Content = page; Nudge(page); }

    /// <summary>base.html, block notices: «Promemoria WhatsApp: n clienti da avvisare» and «Grazie WhatsApp», on top of every
    /// page but Promemoria WhatsApp itself.</summary>
    async void Nudge(Page page)
    {
        nudges.Children.Clear();
        if (page is WhatsAppPage || Business == null) return;
        JsonNode d; try { d = await Api.Current.Call("avvisi", new { }); } catch { return; }
        if (currentPage != page) return;
        nudges.Children.Clear();
        void Add(int n, string strong, string span, string button)
        {
            if (n == 0) return;
            var go = F.Small(B(button, () => Show("whatsapp"), "Primary")); DockPanel.SetDock(go, Dock.Right);
            var row = new DockPanel(); row.Children.Add(go); row.Children.Add(Col(T(strong.Replace("{n}", n.ToString()).Replace("{c}", n == 1 ? "cliente" : "clienti"), size: 15, bold: true), T(span, size: 14, color: Res("Muted"))));
            var box = F.Notice(row); box.Margin = new Thickness(26, 12, 26, 0); box.Padding = new Thickness(18, 14, 18, 14); nudges.Children.Add(box);
        }
        Add(d.I("whatsapp"), "Promemoria WhatsApp: {n} {c} da avvisare", "Un clic per cliente apre WhatsApp con il messaggio pronto: poi premi Invio.", "Avvisa");
        Add(d.I("grazie"), "Grazie WhatsApp: {n} {c} di ieri da ringraziare", "Un clic per cliente apre WhatsApp con il grazie e il link della recensione: poi premi Invio.", "Ringrazia");
    }

    /// <summary>The green or red line on top of the pages, as Django's messages; it goes with OK or by itself.</summary>
    public void Flash(string text, bool error = false, string doLabel = null, Action doIt = null)
    {
        var ok = new Button { Content = "OK", Style = Style("Ghost"), Padding = new Thickness(10, 3, 10, 3) };
        var row = new DockPanel(); DockPanel.SetDock(ok, Dock.Right); row.Children.Add(ok);
        if (doLabel != null) { var act = new Button { Content = doLabel, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) }; act.Click += (_, _) => doIt(); DockPanel.SetDock(act, Dock.Right); row.Children.Add(act); }
        var line = T(text, size: 14.5); line.TextWrapping = TextWrapping.Wrap; line.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(line);
        var box = new Border { Background = Res(error ? "DangerSoft" : "OkSoft"), BorderBrush = Res(error ? "Danger" : "Ok"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(26, 12, 26, 0), Child = row };
        ok.Click += (_, _) => notices.Children.Remove(box);
        notices.Children.Clear(); notices.Children.Add(box);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(error || doLabel != null ? 12 : 6) };
        timer.Tick += (_, _) => { timer.Stop(); notices.Children.Remove(box); }; timer.Start();
    }
}

/// <summary>A page still to come in this build.</summary>
public sealed class Placeholder(string title) : Page
{
    public override Task Load()
    {
        Content = Col(T(title, "H1"), T("Questa sezione arriva nei prossimi passaggi del nuovo programma.", "MutedText")).M(26, 20, 26, 16);
        return Task.CompletedTask;
    }
}
