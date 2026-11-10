using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>A page of the main window: built on show, refreshed when asked.</summary>
public abstract class Page : UserControl
{
    public MainWindow Main { get; set; }
    public JsonNode Business => Main.Business;
    public bool Can(string perm) => Main.Business.A("permessi").Any(p => p.ToString() == perm);
    public abstract Task Load();
}

/// <summary>The program's window: the business's pages on the left, the account and Esci at the bottom of the bar.</summary>
public sealed class MainWindow : Window
{
    public bool Replaced;
    public JsonNode Me { get; }
    public JsonNode Business { get; private set; }
    readonly ContentControl content = new();
    readonly StackPanel nav = new();
    readonly TextBlock businessName = T("", size: 15, bold: true);
    RadioButton current;
    readonly StackPanel topLine = new() { Orientation = Orientation.Horizontal };
    static TextBlock SideLink(string text, Action click)
    {
        var t = T(text, size: 13.5, color: Res("Pastel")); t.Cursor = System.Windows.Input.Cursors.Hand; t.FontWeight = FontWeights.Medium;
        t.MouseLeftButtonUp += (_, _) => click(); return t;
    }
    readonly Dictionary<string, Func<Page>> pages = new();

    public MainWindow(JsonNode me)
    {
        Me = me;
        Title = "TD Booking Suite"; Width = 1360; Height = 860; MinWidth = 1000; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
        // the dark side bar of the web program: the logo, where you work, the pages in groups, the account at the bottom
        var bar = new DockPanel { Width = 256, Background = Res("Side") };
        var badge = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(Color.FromRgb(0x8B, 0x67, 0xB8), Color.FromRgb(0xE0, 0x56, 0x9B), 45),
            Child = new TextBlock { Text = "TD", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"] },
        };
        businessName.Foreground = Res("SideInk"); businessName.FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"]; businessName.FontSize = 16; businessName.TextTrimming = TextTrimming.CharacterEllipsis; businessName.TextWrapping = TextWrapping.NoWrap;
        var head = Row(badge, Col(businessName, T("Edizione Windows", size: 12.5, color: Res("SideMuted"))).M(12, 0, 0, 0)).M(18, 20, 14, 14);
        DockPanel.SetDock(head, Dock.Top); bar.Children.Add(head);
        var account = Col(T(me.S("nome"), size: 13.5, bold: true, color: Res("SideInk")).M(0, 0, 0, 1), T(RoleText(), size: 12.5, color: Res("SideMuted")).M(0, 0, 0, 6),
            Row(SideLink("Account", () => Show("account")), SideLink("Esci", () => Shell.Instance.SignedOut(true)).M(14, 0, 0, 0))).M(26, 12, 14, 18);
        DockPanel.SetDock(account, Dock.Bottom); bar.Children.Add(account);
        nav.Margin = new Thickness(12, 0, 12, 0);
        bar.Children.Add(new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        // the top bar: the business and the role, as the crumb of the web pages
        var top = new Border { Height = 56, Background = Res("Surface"), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = topLine };
        topLine.Margin = new Thickness(28, 0, 28, 0); topLine.VerticalAlignment = VerticalAlignment.Center;
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); right.Children.Add(top); right.Children.Add(content);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        root.Children.Add(bar); root.Children.Add(right);
        Content = root;
        Loaded += async (_, _) => await Guard(Enter);
    }

    string RoleText()
    {
        var list = Me.A("attivita").ToList();
        return list.Count == 1 ? list[0].S("ruoloNome") : Me.Bo("staff") ? "Amministratore del programma" : "";
    }

    async Task Enter()
    {
        var list = Me.A("attivita").ToList();
        if (list.Count == 0) { BuildProgramOnly(); return; }
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
        businessName.Text = Business.S("nome");
        topLine.Children.Clear();
        topLine.Children.Add(T(Business.S("nome"), size: 14, bold: true));
        topLine.Children.Add(new Border { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(10, 0, 0, 0), Child = T(RoleText() == "" ? Permissions(Business.S("ruolo")) : RoleText(), size: 12.5, color: Res("Brand")) });
        if (ColorConverter.ConvertFromString(Business.S("colore")) is Color c) Application.Current.Resources["BrandColor"] = c;
        Application.Current.Resources["Brand"] = new SolidColorBrush((Color)Application.Current.Resources["BrandColor"]);
        Build();
        Show("agenda");
    }

    static string Permissions(string role) => role switch { "owner" => "Titolare", "manager" => "Amministratore sede", "reception" => "Reception", "operator" => "Operatore", "marketing" => "Marketing", "viewer" => "Consultazione", _ => "Amministratore del programma" };
    bool Can(string p) => Business.A("permessi").Any(x => x.ToString() == p);

    void Build()
    {
        nav.Children.Clear(); pages.Clear();
        StackPanel group = null;
        void Section(string title)
        {
            group = new StackPanel();
            var label = T(title.ToUpperInvariant(), size: 11, bold: true, color: Res("SideMuted")).M(8, 6, 6, 7);
            var box = new Border { BorderBrush = Res("SideLine"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), Padding = new Thickness(6, 4, 6, 6), Margin = new Thickness(0, 0, 0, 12),
                Child = Col(label, new Border { Height = 1, Background = Res("SideLine"), Margin = new Thickness(2, 0, 2, 5) }, group) };
            nav.Children.Add(box);
        }
        void Add(string key, string label, Func<Page> make, bool when = true)
        {
            if (!when) return;
            pages[key] = make;
            var r = new RadioButton { Style = Style("Nav"), Content = label, GroupName = "nav", Tag = key };
            r.Checked += (_, _) => { if (current != r) Show(key); };
            group.Children.Add(r);
        }
        Section("Ogni giorno");
        Add("agenda", "Agenda e prenotazioni", () => new AgendaPage(), Can("booking.read"));
        Add("prenotazioni", "Elenco prenotazioni", () => new BookingsPage(), Can("booking.read"));
        Add("clienti", "Clienti", () => new Placeholder("Clienti"), Can("contacts.read"));
        Add("preventivi", "Preventivi", () => new Placeholder("Preventivi"), Can("sale.write"));
        if (Business.Bo("cassa") && Can("sale.write"))
        {
            Section("Cassa");
            Add("cassa", "Cassa", () => new Placeholder("Cassa"));
            Add("standby", "Stand-by", () => new Placeholder("Stand-by"));
            Add("giftcard", "Gift card", () => new Placeholder("Gift card"));
        }
        Section("Comunicazioni");
        Add("richiami", "Richiami", () => new Placeholder("Richiami"), Can("contacts.read"));
        Add("whatsapp", "Promemoria WhatsApp", () => new Placeholder("Promemoria WhatsApp"), Can("pii.read"));
        Add("messaggi", "Configurazione promemoria", () => new Placeholder("Configurazione promemoria"), Can("users.manage"));
        Section("La tua attività");
        Add("attesa", "Lista d'attesa", () => new Placeholder("Lista d'attesa"), Can("booking.read"));
        Add("report", "Report", () => new Placeholder("Report"), Can("export.operational"));
        Add("catalogo", "Configura l'attività", () => new Placeholder("Configura l'attività"), Can("catalog.write"));
        Section("Controlli");
        Add("registro", "Registro attività", () => new Placeholder("Registro attività"), Can("audit.read"));
        Add("persone", "Persone e permessi", () => new Placeholder("Persone e permessi"), Can("users.manage"));
        if (Me.Bo("staff"))
        {
            Section("Programma");
            Add("licenza", "Licenza", () => new Placeholder("Licenza"));
            Add("postazioni", "Postazioni", () => new Placeholder("Postazioni"), !Me.Bo("standalone"));
            Add("backup", "Backup", () => new Placeholder("Backup"));
            Add("rete", Me.Bo("standalone") ? "Online e sito" : "Rete e certificato", () => new Placeholder("Rete"));
            Add("stato", "Stato del programma", () => new Placeholder("Stato del programma"));
        }
        foreach (var box in nav.Children.OfType<Border>().Where(x => x.Child is StackPanel sp && sp.Children.OfType<StackPanel>().Last().Children.Count == 0).ToList()) nav.Children.Remove(box);
        pages["account"] = () => new Placeholder("Account");
    }

    void BuildProgramOnly()
    {
        businessName.Text = "Programma";
        nav.Children.Clear(); pages.Clear();
        pages["licenza"] = () => new Placeholder("Licenza");
        Show("licenza");
    }

    public Page CurrentPage => content.Content as Page;
    public async void Show(string key)
    {
        if (!pages.TryGetValue(key, out var make)) return;
        current = nav.Children.OfType<Border>().SelectMany(x => ((StackPanel)x.Child).Children.OfType<StackPanel>().Last().Children.OfType<RadioButton>()).FirstOrDefault(r => (string)r.Tag == key);
        if (current != null) current.IsChecked = true;
        var page = make(); page.Main = this;
        content.Content = page;
        await Guard(page.Load);
    }
    public void ShowPage(Page page) { page.Main = this; content.Content = page; _ = Guard(page.Load); }
}

/// <summary>A page still to come in this build.</summary>
public sealed class Placeholder(string title) : Page
{
    public override Task Load()
    {
        Content = Col(T(title, "H1"), T("Questa sezione arriva nei prossimi passaggi del nuovo programma.", "MutedText")).M(40, 28, 40, 28);
        return Task.CompletedTask;
    }
}
