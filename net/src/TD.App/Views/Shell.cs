using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>The way in: which server, its certificate, the workstation's code, the sign in, then the main window.</summary>
public sealed class Shell
{
    public static Shell Instance { get; private set; }
    MainWindow main;
    public JsonNode Me { get; private set; }

    public async void Start(string[] args)
    {
        Instance = this;
        var demo = Array.IndexOf(args, "--demo");
        if (demo >= 0 && args.Length > demo + 1) { Demo.Run(args[demo + 1], args); return; }
        Application.Current.DispatcherUnhandledException += (_, e) => { Say("Errore inatteso: " + e.Exception.Message); e.Handled = true; };
        var config = ClientConfig.Load();
        if (config == null && !Setup.Choose(out config)) { Application.Current.Shutdown(); return; }
        Api.Current = new Api(config); Api.Confirm = ConfirmDialog.Ask;
        try { await Api.Current.Get("/api/info"); }
        catch (ApiError ex) when (ex.Status == 0)
        {
            // the certificate is not the one trusted (a new server, a new authority): ask again
            if (Api.Current.SeenCertificate != null && Setup.Trust(config)) { config.Save(); Api.Current = new Api(config); }
            else { Say(ex.Message + "\n\nControlla che il PC server sia acceso e raggiungibile."); }
        }
        await SignIn();
    }

    public async Task SignIn()
    {
        var token = ClientConfig.ReadSecret(true);
        if (token != "")
        {
            Api.Current.Token = token;
            try { Me = await Api.Current.Call("sessione.io"); Open(); return; }
            catch (NeedsPairing) { if (!await Pair()) return; }
            catch (ApiError) { Api.Current.Token = ""; ClientConfig.WriteSecret(true, ""); }
        }
        // tdwin first_run: no administrator yet: he is created now, on the server PC, and goes straight on with the guided setup
        JsonNode info = null; try { info = await Api.Current.Get("/api/info"); } catch (ApiError) { }
        if (info != null && info["configurato"] != null && !info.Bo("configurato"))
        {
            if (info.S("primoAvvio") == "") { Say("Il primo avvio si fa dal PC server."); Application.Current.Shutdown(); return; }
            var first = new FirstRunWindow(info);
            if (first.ShowDialog() != true) { Application.Current.Shutdown(); return; }
            Api.Current.Token = first.Token; if (first.Remember) ClientConfig.WriteSecret(true, first.Token);
            try { Me = await Api.Current.Call("sessione.io"); Open("inizia"); main.Flash("Amministratore del programma creato. Ora la licenza e la tua attività."); return; }
            catch (ApiError ex) { Say(ex.Message); }
        }
        while (true)
        {
            var login = new LoginWindow(prefill); prefill = "";
            if (login.ShowDialog() != true) { Application.Current.Shutdown(); return; }
            if (login.NeedsPairing) { if (!await Pair()) return; continue; }
            Api.Current.Token = login.Token;
            if (login.Remember) ClientConfig.WriteSecret(true, login.Token);
            try { Me = await Api.Current.Call("sessione.io"); Open(); return; }
            catch (ApiError ex) { Say(ex.Message); }
        }
    }

    async Task<bool> Pair()
    {
        var w = new PairWindow();
        if (w.ShowDialog() != true) { Application.Current.Shutdown(); return false; }
        await Task.CompletedTask;
        return true;
    }

    /// <summary>After the businesses changed (the guided setup made one): the session read again, the window opened on that page.</summary>
    public async Task Reopen(string key)
    {
        Me = await Api.Current.Call("sessione.io");
        if (main != null) { main.Replaced = true; }
        Open(key);
    }

    void Open(string key = null)
    {
        main?.Close();
        main = new MainWindow(Me) { StartKey = key };
        Application.Current.MainWindow = main;
        main.Closed += (_, _) => { if (main != null && !main.Replaced) Application.Current.Shutdown(); };
        main.Show();
    }

    /// <summary>Esci, or a session that ended on the server: back to the sign in.</summary>
    /// <summary>A file the program has just put in the Download folder: the same line everywhere, with Apri.</summary>
    public void Saved(string path) =>
        main?.Flash($"Salvato nella cartella Download: {System.IO.Path.GetFileName(path)}", false, "Apri", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException) { } });

    /// <summary>A line on top of the window that is open now.</summary>
    public void Say(string text, bool error = false) => main?.Flash(text, error);
    string prefill = "";
    /// <summary>tdwin leave_as: Esci ed entra come titolare: the administrator's session ends, the sign in already has the owner's username.</summary>
    public void LeaveAs(string login) { prefill = login ?? ""; SignedOut(true); }

    public async void SignedOut(bool byUser = false)
    {
        if (byUser) try { await Api.Current.Post("/api/logout"); } catch { }
        Api.Current.Token = ""; ClientConfig.WriteSecret(true, "");
        if (main != null) { main.Replaced = true; main.Close(); main = null; }
        await SignIn();
    }
}

/// <summary>A workstation without a server yet: its address, then its certificate's fingerprint to confirm.</summary>
public static class Setup
{
    public static bool Choose(out ClientConfig config)
    {
        config = null;
        var w = new Window { Title = "TD Booking Suite · Server", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        var box = Box("");
        var ok = false;
        var go = B("Continua", () => { ok = true; w.DialogResult = true; }, "Primary");
        go.IsDefault = true;
        w.Content = Col(T("Collega questa postazione", "H1"), T("Scrivi l'indirizzo del PC server, come lo trovi nella pagina Postazioni del programma: per esempio ufficio.local:8443 o 192.168.1.10:8443.", "MutedText").M(0, 0, 0, 16),
            Field("Indirizzo del server", box), go).M(28);
        box.Focus();
        if (w.ShowDialog() != true || !ok) return false;
        var server = ClientConfig.Normalize(box.Text);
        if (server == null) { Say("Indirizzo non valido: scrivi il nome o l'IP del PC server, per esempio 192.168.1.10:8443."); return Choose(out config); }
        config = new ClientConfig { Server = server, Manual = true };
        var api = new Api(config);
        try { api.Get("/api/info").GetAwaiter().GetResult(); config.Save(); return true; }
        catch (ApiError)
        {
            Api.Current = api;
            if (api.SeenCertificate == null) { Say("Il server non risponde a questo indirizzo."); return Choose(out config); }
            if (!Trust(config)) return false;
            config.Save(); return true;
        }
    }

    /// <summary>The fingerprint shown, to compare with the one on the server's Postazioni page.</summary>
    public static bool Trust(ClientConfig config)
    {
        var api = Api.Current;
        var authority = api.SeenAuthority; var leaf = api.SeenCertificate;
        var print = authority != null ? ClientConfig.Fingerprint(authority.RawData) : ClientConfig.CertPrefix + ClientConfig.Fingerprint(leaf.RawData);
        var shown = print.StartsWith(ClientConfig.CertPrefix) ? print[ClientConfig.CertPrefix.Length..] : print;
        if (!Ask($"Il server mostra questa impronta:\n\n{shown}\n\nÈ la stessa della pagina Postazioni sul PC server? Rispondi Sì solo se coincide.", "Conferma l'impronta del server")) return false;
        config.Ca = print;
        return true;
    }
}

/// <summary>The 6-digit code made on the server's Postazioni page.</summary>
public sealed class PairWindow : Window
{
    public PairWindow()
    {
        Title = "TD Booking Suite · Postazione"; Width = 460; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.NoResize;
        var name = Box(Environment.MachineName); var code = Box("");
        var error = T("", color: Res("Danger"));
        var go = BA("Registra questa postazione", async () =>
        {
            try
            {
                var r = await Api.Current.Post("/api/postazione/pair", new JsonObject { ["codice"] = code.Text, ["nome"] = name.Text });
                Api.Current.Workstation = r.S("token"); ClientConfig.WriteSecret(false, Api.Current.Workstation);
                DialogResult = true;
            }
            catch (ApiError ex) { error.Text = ex.Message; }
        }, "Primary");
        go.IsDefault = true;
        Content = Col(T("Registra la postazione", "H1"), T("Sul PC server apri Postazioni, crea un codice e scrivilo qui: vale 15 minuti.", "MutedText").M(0, 0, 0, 16),
            Field("Codice di 6 cifre", code), error.M(0, 0, 0, 10), go).M(28);
        Loaded += (_, _) => code.Focus();
    }
}

/// <summary>Sign in: username or email and password, the owners' quick buttons on trusted devices, then the code.</summary>
public sealed class LoginWindow : Window
{
    public string Token { get; private set; } = "";
    public bool Remember { get; private set; }
    public bool NeedsPairing { get; private set; }
    readonly StackPanel body = new();

    readonly string prefill;
    public LoginWindow(string prefill = "")
    {
        this.prefill = prefill;
        Title = "TD Booking Suite"; Width = 440; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.CanMinimize;
        Content = new Border { Child = body, Padding = new Thickness(32) };
        Password();
    }

    async void Password()
    {
        body.Children.Clear();
        var login = Box(prefill); var pw = new PasswordBox(); var remember = new CheckBox { Content = "Resta collegato su questo PC", IsChecked = true };
        var error = T("", color: Res("Danger"));
        var go = BA("Entra", async () =>
        {
            error.Text = "";
            try
            {
                var r = await Api.Current.Post("/api/login", new JsonObject { ["login"] = login.Text, ["password"] = pw.Password, ["remember"] = remember.IsChecked == true, ["device"] = Environment.MachineName });
                Remember = remember.IsChecked == true;
                if (r.Bo("mfa")) Code(r.S("pre")); else { Token = r.S("token"); DialogResult = true; }
            }
            catch (NeedsPairing) { NeedsPairing = true; DialogResult = true; }
            catch (ApiError ex) { error.Text = ex.Message; }
        }, "Primary");
        go.IsDefault = true; go.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.Children.Add(Col(T("TD Booking Suite", "H1"), T("Entra con il tuo username o la tua email.", "MutedText").M(0, 0, 0, 18),
            Field("Username o email", login), Field("Password", pw), remember.M(0, 0, 0, 12), error.M(0, 0, 0, 8), go));
        if (prefill != "") pw.Focus(); else login.Focus();
        try
        {
            var quick = await Api.Current.Get("/api/login/quick") as JsonArray;
            if (quick is { Count: > 0 })
            {
                var list = Col(T("Oppure entra senza password", size: 13, bold: true).M(0, 18, 0, 6));
                foreach (var q in quick)
                    list.Children.Add(BA($"{q.S("name")}  ·  {q.S("where")}", async () =>
                    {
                        try
                        {
                            var r = await Api.Current.Post("/api/login/quick", new JsonObject { ["user"] = q.S("id"), ["device"] = Environment.MachineName });
                            Remember = true;
                            if (r.Bo("mfa")) Code(r.S("pre")); else { Token = r.S("token"); DialogResult = true; }
                        }
                        catch (ApiError ex) { error.Text = ex.Message; }
                    }).M(0, 0, 0, 6));
                body.Children.Add(list);
            }
        }
        catch (NeedsPairing) { NeedsPairing = true; DialogResult = true; }
        catch { }
    }

    void Code(string pre)
    {
        body.Children.Clear();
        var code = Box(""); var error = T("", color: Res("Danger"));
        var go = BA("Verifica", async () =>
        {
            try { var r = await Api.Current.Post("/api/verify", new JsonObject { ["pre"] = pre, ["code"] = code.Text }); Token = r.S("token"); DialogResult = true; }
            catch (ApiError ex) { error.Text = ex.Message; }
        }, "Primary");
        go.IsDefault = true;
        body.Children.Add(Col(T("Codice di verifica", "H1"), T("Scrivi il codice di 6 cifre dell'app Authenticator, o uno dei codici di recupero.", "MutedText").M(0, 0, 0, 18),
            Field("Codice", code), error.M(0, 0, 0, 8), Row(go, B("Indietro", Password, "Ghost").M(8, 0, 0, 0))));
        code.Focus();
    }
}

/// <summary>tdwin/primo_avvio.html: the program's administrator, created once from the server PC with the code only a Windows administrator reads.</summary>
public sealed class FirstRunWindow : Window
{
    public string Token { get; private set; } = "";
    public bool Remember { get; private set; }

    public FirstRunWindow(JsonNode info)
    {
        Title = "TD Booking Suite · Primo avvio"; Width = 520; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.CanMinimize;
        var code = Box(""); code.MaxLength = 20; code.CharacterCasing = CharacterCasing.Upper; code.Tag = "ABCD-EFGH"; AgendaPage.Hint(code);
        var username = Box(""); username.MaxLength = 40; username.Tag = "mario"; AgendaPage.Hint(username);
        var email = Box(""); email.MaxLength = 254; var pw = new PasswordBox(); var pw2 = new PasswordBox();
        var remember = new CheckBox { Content = "Resta collegato su questo PC", IsChecked = true };
        var error = T("", color: Res("Danger")); error.TextWrapping = TextWrapping.Wrap;
        var go = BA("Crea l'amministratore", async () =>
        {
            error.Text = "";
            try
            {
                var r = await Api.Current.Post("/api/primo-avvio", new JsonObject { ["codice"] = code.Text, ["username"] = username.Text, ["email"] = email.Text, ["password"] = pw.Password, ["password2"] = pw2.Password, ["remember"] = remember.IsChecked == true, ["device"] = Environment.MachineName });
                Remember = remember.IsChecked == true; Token = r.S("token"); DialogResult = true;
            }
            catch (ApiError ex) { error.Text = ex.Message; }
        }, "Primary");
        go.IsDefault = true; go.HorizontalAlignment = HorizontalAlignment.Stretch;
        TextBlock P(string text) { var t = T(text, "MutedText"); t.TextWrapping = TextWrapping.Wrap; return t; }
        var path = new TextBox { Text = info.S("primoAvvio"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0), FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12.5 };
        var notice = F.Notice(Col(T("Codice del primo avvio", size: 14, bold: true),
            P("Per sicurezza solo un amministratore di Windows di questo PC può creare l'amministratore del programma. Apri il menu Start, TD Booking Suite, Codice del primo avvio: Windows chiede il permesso di amministratore e mostra il codice da scrivere qui. Il codice sta anche nel file").M(0, 4, 0, 4), path));
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Col(T("Primo avvio", "H1"),
                P("Crea l'amministratore del programma: configura licenza, attività" + (info.Bo("standalone") ? "" : ", postazioni") + " e backup. Usa una password lunga e conservala bene: se la perdi si recupera solo da questo PC, dal menu Start, TD Booking Suite, Recupera accesso.").M(0, 0, 0, 14),
                notice.M(0, 0, 0, 12), Field("Codice del primo avvio", code), Field("Username", username, "Con questo entri nel programma: lettere, numeri, punto e trattino"), Field("Email, facoltativa", email),
                Field("Password, almeno 12 caratteri", pw), Field("Ripeti la password", pw2), remember.M(0, 0, 0, 12), error.M(0, 0, 0, 8), go).M(32),
        };
        Loaded += (_, _) => code.Focus();
    }
}
