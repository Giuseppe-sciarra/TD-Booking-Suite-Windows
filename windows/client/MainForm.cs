using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace TDBookingSuite
{
    /// <summary>The program's window: the server's pages in WebView2, light or dark like Windows.</summary>
    class MainForm : Form
    {
        readonly Config config;
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
        // 2.10.1: while the server starts (after the installation, at the PC's start) the window waits and tries again
        readonly Timer retry = new Timer { Interval = 3000 };
        string target;
        int failures;
        const int MaxFailures = 40;  // about two minutes
        const int WM_SYSCOMMAND = 0x112, MF_STRING = 0, MF_SEPARATOR = 0x800, ID_RELOAD = 0x1000, ID_SERVER = 0x1010;

        [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static Icon AppIcon()
        {
            using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) return new Icon(stream);
        }

        public MainForm(Config config)
        {
            this.config = config;
            Text = "TD Booking Suite";
            Icon = AppIcon();
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(800, 560);
            Controls.Add(web);
            Load += async (s, e) => { FitScreen(); await Start(); };
            SystemEvents.UserPreferenceChanged += ThemeChanged;
            FormClosed += (s, e) => SystemEvents.UserPreferenceChanged -= ThemeChanged;
        }

        /// <summary>The window fits the screen it opens on: 90% of it, centred, and maximized when the screen is small
        /// for a desktop program (Full HD at 125% and below), where every point of height counts.</summary>
        void FitScreen()
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(area.Width * 9 / 10, area.Height * 9 / 10);
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            float scale = DeviceDpi / 96f;
            if (area.Width / scale < 1600 || area.Height / scale < 900) WindowState = FormWindowState.Maximized;
        }

        void ThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General && IsHandleCreated) BeginInvoke((Action)ApplyTheme);
        }

        static bool WindowsDark()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }

        void ApplyTheme()
        {
            int dark = WindowsDark() ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));  // dark title bar, Windows 10 2004 and later
            BackColor = dark == 1 ? Color.FromArgb(0x16, 0x11, 0x1d) : Color.FromArgb(0xf6, 0xf3, 0xfa);
            web.DefaultBackgroundColor = BackColor;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var menu = GetSystemMenu(Handle, false);
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, ID_RELOAD, "Ricarica\tF5");
            AppendMenu(menu, MF_STRING, ID_SERVER, "Cambia server...");
            ApplyTheme();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND)
            {
                var id = m.WParam.ToInt32();
                if (id == ID_RELOAD) { web.CoreWebView2?.Reload(); return; }
                if (id == ID_SERVER) { Process.Start(Application.ExecutablePath, "--configura"); Close(); return; }
            }
            base.WndProc(ref m);
        }

        async Task Start()
        {
            try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (WebView2RuntimeNotFoundException)
            {
                if (MessageBox.Show(this, "Manca Microsoft Edge WebView2, il componente di Windows che mostra il programma. Vuoi scaricarlo adesso? Dopo l'installazione riapri TD Booking Suite.",
                    "TD Booking Suite", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                Close();
                return;
            }
            var environment = await CoreWebView2Environment.CreateAsync(null, Config.WebViewFolder);
            await web.EnsureCoreWebView2Async(environment);
            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // Automatico in the program's theme menu follows Windows, light or dark
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
            core.ServerCertificateErrorDetected += CertificateError;
            core.NavigationStarting += (s, e) =>
            {
                if (config.SameOrigin(e.Uri) || e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:")) return;
                e.Cancel = true;
                OpenOutside(e.Uri);
            };
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (config.SameOrigin(e.Uri)) core.Navigate(e.Uri); else OpenOutside(e.Uri);
            };
            core.DocumentTitleChanged += (s, e) => Text = string.IsNullOrEmpty(core.DocumentTitle) ? "TD Booking Suite" : core.DocumentTitle + " - TD Booking Suite";
            core.ProcessFailed += (s, e) => { if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) Close(); else core.Reload(); };
            core.WebMessageReceived += Message;
            core.NavigationStarting += (s, e) => { if (config.SameOrigin(e.Uri)) target = e.Uri; };
            core.NavigationCompleted += Completed;
            retry.Tick += (s, e) => { retry.Stop(); web.CoreWebView2?.Navigate(target ?? config.Server + "/"); };
            FormClosed += (s, e) => retry.Dispose();
            core.Navigate(config.Server + "/");
        }

        /// <summary>2.10.0: Online e sito on Un PC solo. Turning the booking page on or off changes how this PC listens
        /// and opens or closes its port: setup-server.ps1, as administrator after the Windows prompt. Only this one
        /// message, from the program's own page, and only where the server is installed on this PC.</summary>
        async void Message(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string text;
            try { text = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
            if ((text != "tdwin-online:on" && text != "tdwin-online:off") || !config.SameOrigin(e.Source)
                || !Uri.TryCreate(e.Source, UriKind.Absolute, out var page) || page.AbsolutePath != "/platform/rete/") return;
            var script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "server", "script", "setup-server.ps1"));
            if (!File.Exists(script)) { Reply("Il server non è installato su questo PC: accendilo dal PC server."); return; }
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Online {text.Substring(13)}")
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            int code;
            try { using (var run = Process.Start(start)) { await Task.Run(() => run.WaitForExit()); code = run.ExitCode; } }
            catch (System.ComponentModel.Win32Exception) { Reply("Annullato: Windows non ha dato l'autorizzazione."); return; }
            if (code != 0) { Reply("Non riuscito: i dettagli sono in C:\\ProgramData\\TD Booking Suite\\log\\setup.log."); return; }
            // the services restart with the new setting: the page comes back when they answer
            await Task.Delay(6000);
            web.CoreWebView2?.Reload();
        }

        /// <summary>The server does not answer: our own page instead of the browser's, and a new try every few seconds.
        /// On the server PC that is the program starting; elsewhere the server PC is off or out of the network.</summary>
        void Completed(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var core = web.CoreWebView2;
            if (e.IsSuccess) { if (config.SameOrigin(core.Source)) failures = 0; return; }
            switch (e.WebErrorStatus)
            {
                case CoreWebView2WebErrorStatus.CannotConnect: case CoreWebView2WebErrorStatus.ConnectionAborted:
                case CoreWebView2WebErrorStatus.ConnectionReset: case CoreWebView2WebErrorStatus.Disconnected:
                case CoreWebView2WebErrorStatus.ServerUnreachable: case CoreWebView2WebErrorStatus.Timeout:
                case CoreWebView2WebErrorStatus.HostNameNotResolved: break;
                default: return;  // anything else keeps the browser's own explanation
            }
            failures++;
            var local = string.Equals(Config.Default()?.Server, config.Server, StringComparison.OrdinalIgnoreCase);
            var host = WebUtility.HtmlEncode(new Uri(config.Server).Authority);
            string title, text;
            if (failures <= MaxFailures)
            {
                title = local ? "Avvio di TD Booking Suite…" : "Il server non risponde";
                text = local ? "Il programma su questo PC si sta avviando: la pagina si apre da sola tra pochi secondi."
                             : $"Riprovo da solo ogni pochi secondi. Controlla che il PC server <strong>{host}</strong> sia acceso e collegato alla rete.";
                retry.Start();
            }
            else
            {
                title = local ? "Il programma non risponde" : $"{host} non risponde";
                text = local ? "Il servizio di TD Booking Suite su questo PC non è partito. Riavvia il PC; se succede ancora, apri <strong>Servizi</strong> di Windows e controlla <strong>TD Booking Suite Web</strong> e <strong>TD Booking Suite Database</strong>. I dettagli sono in <code>C:\\ProgramData\\TD Booking Suite\\log</code>."
                             : "Il PC server è spento, fuori rete o il suo indirizzo è cambiato. Quando è di nuovo acceso premi Riprova; per un altro server: menu di sistema della finestra, <strong>Cambia server</strong>.";
            }
            bool dark = WindowsDark();
            string ink = dark ? "#efeaf6" : "#2b2236", muted = dark ? "#b0a4c2" : "#6b6079", bg = dark ? "#16111d" : "#f6f3fa";
            core.NavigateToString("<!doctype html><html lang=\"it\"><meta charset=\"utf-8\"><style>body{margin:0;height:100vh;display:grid;place-items:center;"
                + $"background:{bg};color:{ink};font:15px/1.6 'Segoe UI',sans-serif}}main{{max-width:520px;padding:32px}}h1{{font-size:24px;margin:0 0 10px}}p{{color:{muted};margin:0 0 18px}}"
                + "code{font-size:13px}a{display:inline-block;padding:9px 18px;border-radius:10px;background:#8b67b8;color:#fff;text-decoration:none;font-weight:600}"
                + ".dot{display:inline-block;width:9px;height:9px;border-radius:50%;background:#8b67b8;margin-right:10px;animation:p 1.2s infinite}@keyframes p{50%{opacity:.25}}</style>"
                + $"<main><h1>{(failures <= MaxFailures ? "<span class=\"dot\"></span>" : "")}{title}</h1><p>{text}</p>"
                + $"<a href=\"{WebUtility.HtmlEncode(target ?? config.Server + "/")}\">Riprova</a></main></html>");
        }

        void Reply(string error) => web.CoreWebView2?.PostWebMessageAsString("tdwin-online-error:" + error);

        bool warned;

        /// <summary>A certificate Windows does not trust is accepted only when it is the one registered at the first
        /// start: the server's own authority, or the server's certificate itself when it is self-signed.</summary>
        void CertificateError(object sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
        {
            var accepted = false;
            var status = e.ErrorStatus;
            if (config.Ca != "" && config.SameOrigin(e.RequestUri) &&
                (status == CoreWebView2WebErrorStatus.CertificateIsInvalid || status == CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect))
            {
                var server = new X509Certificate2(Config.PemToDer(e.ServerCertificate.ToPemEncoding()));
                if (config.Ca.StartsWith(Config.CertPrefix)) accepted = Config.CertPrefix + Config.Fingerprint(server.RawData) == config.Ca;
                else
                {
                    // the last one must be the registered authority, and it must have signed the server's certificate
                    var chain = e.ServerCertificate.PemEncodedIssuerCertificateChain;
                    if (chain.Count > 1)
                    {
                        var authority = new X509Certificate2(Config.PemToDer(chain[chain.Count - 1]));
                        accepted = Config.Fingerprint(authority.RawData) == config.Ca && Config.SignedBy(server, authority);
                    }
                }
            }
            e.Action = accepted ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
            if (accepted || warned) return;
            warned = true;
            BeginInvoke((Action)(() =>
            {
                if (MessageBox.Show(this,
                    "Il certificato del server non è quello registrato su questa postazione: forse è stato rinnovato o cambiato, oppure qualcuno si mette in mezzo.\r\n\r\n" +
                    "Vuoi vedere il nuovo certificato con la sua impronta e decidere se accettarlo?",
                    "TD Booking Suite", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    Process.Start(Application.ExecutablePath, "--configura");
                    Close();
                }
            }));
        }

        static void OpenOutside(string url)
        {
            if (url.StartsWith("https://") || url.StartsWith("http://") || url.StartsWith("mailto:") || url.StartsWith("tel:"))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
