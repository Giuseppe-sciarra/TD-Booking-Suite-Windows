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
    readonly Dictionary<string, Func<Page>> pages = new();

    public MainWindow(JsonNode me)
    {
        Me = me;
        Title = "TD Booking Suite"; Width = 1360; Height = 860; MinWidth = 1000; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
        var bar = new DockPanel { Width = 236, Background = Res("Surface") };
        var head = Col(businessName.M(20, 18, 16, 0), T("TD Booking Suite " + me.S("versione"), "MutedText").M(20, 2, 16, 14));
        DockPanel.SetDock(head, Dock.Top); bar.Children.Add(head);
        var account = Col(new Border { Height = 1, Background = Res("Line"), Margin = new Thickness(12, 6, 12, 8) },
            T(me.S("nome"), size: 13, bold: true).M(20, 0, 16, 2), T(RoleText(), "MutedText").M(20, 0, 16, 6),
            Row(B("Account", () => Show("account"), "Ghost"), B("Esci", () => Shell.Instance.SignedOut(true), "Ghost")).M(12, 0, 12, 14));
        DockPanel.SetDock(account, Dock.Bottom); bar.Children.Add(account);
        bar.Children.Add(new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var root = new DockPanel();
        var sep = new Border { Width = 1, Background = Res("Line") };
        DockPanel.SetDock(bar, Dock.Left); DockPanel.SetDock(sep, Dock.Left);
        root.Children.Add(bar); root.Children.Add(sep); root.Children.Add(content);
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
        if (ColorConverter.ConvertFromString(Business.S("colore")) is Color c) Application.Current.Resources["BrandColor"] = c;
        Application.Current.Resources["Brand"] = new SolidColorBrush((Color)Application.Current.Resources["BrandColor"]);
        Build();
        Show("agenda");
    }

    bool Can(string p) => Business.A("permessi").Any(x => x.ToString() == p);

    void Build()
    {
        nav.Children.Clear(); pages.Clear();
        void Add(string key, string label, Func<Page> make, bool when = true)
        {
            if (!when) return;
            pages[key] = make;
            var r = new RadioButton { Style = Style("Nav"), Content = label, GroupName = "nav", Tag = key };
            r.Checked += (_, _) => { if (current != r) Show(key); };
            nav.Children.Add(r);
        }
        void Section(string title) => nav.Children.Add(T(title.ToUpperInvariant(), "MutedText", size: 11).M(20, 14, 16, 4));
        Add("agenda", "Agenda", () => new AgendaPage(), Can("booking.read"));
        Add("prenotazioni", "Prenotazioni", () => new BookingsPage(), Can("booking.read"));
        Add("clienti", "Clienti", () => new Placeholder("Clienti"), Can("contacts.read"));
        Add("cassa", "Cassa", () => new Placeholder("Cassa"), Business.Bo("cassa") && Can("sale.write"));
        Add("standby", "Stand-by", () => new Placeholder("Stand-by"), Business.Bo("cassa") && Can("sale.write"));
        Add("attesa", "Lista d'attesa", () => new Placeholder("Lista d'attesa"), Can("booking.read"));
        Add("richiami", "Richiami", () => new Placeholder("Richiami"), Can("contacts.read"));
        Section("Attività");
        Add("report", "Report", () => new Placeholder("Report"), Can("export.operational"));
        Add("catalogo", "Servizi e operatori", () => new Placeholder("Servizi e operatori"), Can("catalog.write"));
        Add("impostazioni", "Impostazioni", () => new Placeholder("Impostazioni"), Can("users.manage"));
        if (Me.Bo("staff"))
        {
            Section("Programma");
            Add("licenza", "Licenza", () => new Placeholder("Licenza"));
            Add("postazioni", "Postazioni", () => new Placeholder("Postazioni"), !Me.Bo("standalone"));
            Add("backup", "Backup", () => new Placeholder("Backup"));
        }
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
        current = nav.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == key);
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
        Content = Col(T(title, "H1"), T("Questa sezione arriva nei prossimi passaggi del nuovo programma.", "MutedText")).M(32);
        return Task.CompletedTask;
    }
}
