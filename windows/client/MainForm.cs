using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
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
        // 2.13.0: a link that downloads a file (an export in CSV) ends its navigation without a page: that is not the
        // server down, so no retry (which downloaded the file again and again) and the page stays the one shown
        string shown;
        bool downloading;
        const int MaxFailures = 40;  // about two minutes
        // 2.14.0: a read-only copy of today's and tomorrow's agenda on this PC, for when the server does not answer:
        // saved every 15 minutes for the last activity opened, shown from the page of a server that does not answer
        readonly Timer copyTimer = new Timer { Interval = 15 * 60 * 1000 };
        string slug;
        const string CopyLink = "https://tdwin.invalid/copia";  // a link of our own page, never opened: it shows the copy
        // 2.14.0: Aggiorna ora links the MSI of a public release (tdwin/aggiornamenti.py): only from here, never another address
        const string Releases = "https://github.com/Giuseppe-sciarra/TD-Booking-Suite-per-Windows/releases/download/";
        // the day sheet fetched with the session of who is signed in (his permissions, his masking) as a refresh, which
        // does not keep the session awake; no copy when it is not there (an older server), signed out or not allowed.
        // The print rules apply on screen too, scripts and forms go, and the page reaches nothing: the sheet on paper
        const string CopyScript = @"(async () => {
  const get = url => fetch(url, {credentials: 'same-origin', headers: {'X-TDBS-Refresh': '1'}});
  let page;
  for (const day of [DAYS]) {
    const r = await get('/t/SLUG/agenda/stampa/?data=' + day);
    if (r.status == 404) chrome.webview.postMessage('tdwin-copia:');
    if (!r.ok || r.redirected) return;
    const doc = new DOMParser().parseFromString(await r.text(), 'text/html');
    if (page) page.querySelector('main').append(...doc.querySelector('main').children); else page = doc;
  }
  for (const link of page.querySelectorAll('link[rel=stylesheet]')) {
    const style = page.createElement('style');
    style.textContent = (await (await get(link.getAttribute('href'))).text()).replaceAll('@media print', '@media all');
    link.replaceWith(style);
  }
  page.querySelectorAll('script,link,form').forEach(e => e.remove());
  const csp = page.createElement('meta');
  csp.httpEquiv = 'Content-Security-Policy'; csp.content = ""default-src 'none'; style-src 'unsafe-inline'"";
  page.head.prepend(csp);
  const own = page.createElement('style');
  own.textContent = 'body{padding:0 24px 24px}.tdwin-copia{position:sticky;top:0;z-index:9;margin:0 -24px 18px;padding:10px 24px;background:#fff3cd;color:#5c4400;border-bottom:1px solid #e6c463;font-weight:600}.tdwin-copia a{margin-left:10px;text-decoration:underline}';
  page.head.append(own);
  const now = new Date(), note = page.createElement('p'), back = page.createElement('a');
  note.className = 'tdwin-copia';
  note.append('Copia del ' + now.toLocaleDateString('it-IT', {day: 'numeric', month: 'numeric'}) + ' alle ' + now.toLocaleTimeString('it-IT', {hour: '2-digit', minute: '2-digit'}) + ': il server non risponde, i dati potrebbero non essere aggiornati.');
  back.href = location.origin + '/'; back.textContent = 'Riprova'; note.append(back);
  page.body.prepend(note);
  chrome.webview.postMessage('tdwin-copia:<!doctype html>' + page.documentElement.outerHTML);
})().catch(() => {});";
        const int WM_SYSCOMMAND = 0x112, MF_STRING = 0, MF_SEPARATOR = 0x800, ID_RELOAD = 0x1000, ID_SERVER = 0x1010;

        [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outText, ref int outSize);

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
            // 2.21.0: the keys to the page again whenever the window comes back to the front: after a Windows window of the
            // program (a confirmation, a message, the print dialog) or another program, the page kept no keyboard focus
            // and its fields took no more typing until a click outside the program
            Activated += (s, e) => GiveFocus();
        }

        void GiveFocus()
        {
            if (web.CoreWebView2 == null || IsDisposed) return;
            BeginInvoke((Action)(() => { if (!IsDisposed) web.Focus(); }));
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
            // Un PC solo opens only its own program: there is no other server to choose
            if (!config.Standalone) AppendMenu(menu, MF_STRING, ID_SERVER, "Cambia server...");
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
            // 2.10.1: a program, not a browser: never the browser's error pages, no swipe back and forth,
            // and the right click offers only cut, copy and paste where there is something to cut, copy or paste
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            // 2.20.11: confirmations and messages of the pages in a Windows window, not the browser's «localhost:8443 dice»
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            // no browser keys: Ctrl+P, Ctrl+F, F5, Ctrl+R, Ctrl+U, Alt+arrows, F12… (cut, copy, paste and undo keep working)
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.ScriptDialogOpening += Dialog;
            // the pages print through this window, with the Windows print dialog (a window older than 2.20.11: the browser's)
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.tdwinPrint=1;");
            // 2.14.0: the server tells a postazione whose window is older than itself to update
            var mine = typeof(MainForm).Assembly.GetName().Version;
            core.Settings.UserAgent += $" TDBookingSuite/{mine.Major}.{mine.Minor}.{mine.Build}";
            core.ContextMenuRequested += (s, e) =>
            {
                var keep = new[] { "cut", "copy", "paste", "selectAll" };
                for (int i = e.MenuItems.Count - 1; i >= 0; i--)
                    if (Array.IndexOf(keep, e.MenuItems[i].Name) < 0) e.MenuItems.RemoveAt(i);
                while (e.MenuItems.Count > 0 && e.MenuItems[e.MenuItems.Count - 1].Kind == CoreWebView2ContextMenuItemKind.Separator) e.MenuItems.RemoveAt(e.MenuItems.Count - 1);
                if (e.MenuItems.Count == 0) e.Handled = true;
            };
            // Automatico in the program's theme menu follows Windows, light or dark
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
            core.ServerCertificateErrorDetected += CertificateError;
            core.NavigationStarting += (s, e) =>
            {
                if (config.SameOrigin(e.Uri) || e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:")) return;
                e.Cancel = true;
                if (e.Uri == CopyLink) BeginInvoke((Action)ShowCopy);
                else if (Regex.IsMatch(e.Uri, "^" + Regex.Escape(Releases) + @"v\d+\.\d+\.\d+/TD-Booking-Suite-\d+\.\d+\.\d+\.msi$")) BeginInvoke((Action)(() => Update(e.Uri)));
                else OpenOutside(e.Uri);
            };
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (config.SameOrigin(e.Uri)) core.Navigate(e.Uri); else OpenOutside(e.Uri);
            };
            core.DocumentTitleChanged += (s, e) => Text = string.IsNullOrEmpty(core.DocumentTitle) ? "TD Booking Suite" : core.DocumentTitle + " - TD Booking Suite";
            core.ProcessFailed += (s, e) => { if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) Close(); else core.Reload(); };
            core.WebMessageReceived += Message;
            core.NavigationStarting += (s, e) => { downloading = false; if (config.SameOrigin(e.Uri)) target = e.Uri; };
            core.DownloadStarting += Download;
            core.NavigationCompleted += Completed;
            retry.Tick += (s, e) => { retry.Stop(); web.CoreWebView2?.Navigate(target ?? config.Server + "/"); };
            copyTimer.Tick += (s, e) => SaveCopy();
            FormClosed += (s, e) => { retry.Dispose(); copyTimer.Dispose(); };
            core.Navigate(config.Server + "/");
        }

        /// <summary>2.10.0: Online e sito on Un PC solo. Turning the booking page on or off changes how this PC listens
        /// and opens or closes its port: setup-server.ps1, as administrator after the Windows prompt. Only this one
        /// message, from the program's own page, and only where the server is installed on this PC.</summary>
        async void Message(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string text;
            try { text = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
            if (text.StartsWith("tdwin-pdf:") && config.SameOrigin(e.Source)) { SavePdf(text.Substring(10)); return; }
            if (text == "tdwin-print:" && config.SameOrigin(e.Source))
            {
                try { web.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.System); }
                catch (Exception ex) when (ex is COMException || ex is InvalidOperationException) { }
                return;
            }
            if (text.StartsWith("tdwin-copia:") && config.SameOrigin(e.Source))
            {
                // empty: a server older than 2.14.0, without the sheet; no more tries, there each one keeps the session awake
                if (text.Length == 12) copyTimer.Stop(); else StoreCopy(text.Substring(12));
                return;
            }
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
            if (e.IsSuccess)
            {
                if (!config.SameOrigin(core.Source)) return;
                failures = 0; shown = core.Source;
                // 2.14.0: the activity whose agenda the copy keeps; the first one opened makes a copy at once
                var activity = Regex.Match(new Uri(core.Source).AbsolutePath, "^/t/([a-zA-Z0-9_-]+)/");
                if (!activity.Success) return;
                var first = slug == null;
                slug = activity.Groups[1].Value;
                if (first) { copyTimer.Start(); SaveCopy(); }
                return;
            }
            if (downloading) { downloading = false; target = shown; return; }
            switch (e.WebErrorStatus)
            {
                case CoreWebView2WebErrorStatus.CannotConnect: case CoreWebView2WebErrorStatus.ConnectionAborted:
                case CoreWebView2WebErrorStatus.ConnectionReset: case CoreWebView2WebErrorStatus.Disconnected:
                case CoreWebView2WebErrorStatus.ServerUnreachable: case CoreWebView2WebErrorStatus.Timeout:
                case CoreWebView2WebErrorStatus.HostNameNotResolved:
                // 2.10.2: an empty answer from a service that is stopping or starting (Errore Unknown): wait for it too
                case CoreWebView2WebErrorStatus.Unknown: break;
                case CoreWebView2WebErrorStatus.OperationCanceled: return;  // a click on another page meanwhile
                default:
                    // the browser's own error pages are off: our page, with what went wrong and Riprova
                    Page("La pagina non si apre", "Errore " + e.WebErrorStatus + ". Premi Riprova; se succede ancora, riavvia il programma.", false, true);
                    return;
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
            // the copy, but not while the program on this PC is only starting: its page comes in seconds
            Page(title, text, failures <= MaxFailures, !(local && failures <= MaxFailures));
        }

        /// <summary>A page of the window itself, light or dark like Windows, with Riprova to the page that was asked,
        /// and the copy of the agenda when there is one.</summary>
        void Page(string title, string text, bool waiting, bool copy)
        {
            copy = copy && LatestCopy() != null;
            bool dark = WindowsDark();
            string ink = dark ? "#efeaf6" : "#2b2236", muted = dark ? "#b0a4c2" : "#6b6079", bg = dark ? "#16111d" : "#f6f3fa";
            // its title, not about:blank, in the title bar
            web.CoreWebView2?.NavigateToString("<!doctype html><html lang=\"it\"><meta charset=\"utf-8\"><title>" + title + "</title><style>body{margin:0;height:100vh;display:grid;place-items:center;"
                + $"background:{bg};color:{ink};font:15px/1.6 'Segoe UI',sans-serif}}main{{max-width:520px;padding:32px}}h1{{font-size:24px;margin:0 0 10px}}p{{color:{muted};margin:0 0 18px}}"
                + "code{font-size:13px}a{display:inline-block;padding:9px 18px;border-radius:10px;background:#8b67b8;color:#fff;text-decoration:none;font-weight:600}"
                + "a.alt{margin:8px 0 0 8px;background:none;color:#8b67b8;box-shadow:inset 0 0 0 1.5px #8b67b8}"
                + ".dot{display:inline-block;width:9px;height:9px;border-radius:50%;background:#8b67b8;margin-right:10px;animation:p 1.2s infinite}@keyframes p{50%{opacity:.25}}</style>"
                + $"<main><h1>{(waiting ? "<span class=\"dot\"></span>" : "")}{title}</h1><p>{text}</p>"
                + (copy ? "<p>Intanto puoi consultare l'ultima copia dell'agenda salvata su questo PC, in sola lettura.</p>" : "")
                + $"<a href=\"{WebUtility.HtmlEncode(target ?? config.Server + "/")}\">Riprova</a>"
                + (copy ? $"<a class=\"alt\" href=\"{CopyLink}\">Apri la copia dell'agenda (sola lettura)</a>" : "") + "</main></html>");
        }

        static string Day(int offset) => DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>2.14.0: the page script fetches today's and tomorrow's sheet and sends it back (Message, StoreCopy).</summary>
        void SaveCopy()
        {
            var core = web.CoreWebView2;
            if (core == null || slug == null || !config.SameOrigin(core.Source)) return;
            _ = core.ExecuteScriptAsync(CopyScript.Replace("SLUG", slug).Replace("DAYS", $"'{Day(0)}', '{Day(1)}'"));
        }

        /// <summary>The copy encrypted for this Windows user (DPAPI), the last one of each day; the copies from before
        /// yesterday go, they hold only days gone by. A copy that fails never stops the window.</summary>
        static void StoreCopy(string html)
        {
            try
            {
                Directory.CreateDirectory(Config.CopyFolder);
                File.WriteAllBytes(Path.Combine(Config.CopyFolder, Day(0) + ".bin"), ProtectedData.Protect(Encoding.UTF8.GetBytes(html), null, DataProtectionScope.CurrentUser));
                foreach (var file in Directory.GetFiles(Config.CopyFolder, "*.bin"))
                    if (string.CompareOrdinal(Path.GetFileName(file), Day(-1)) < 0) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException) { }
        }

        /// <summary>The newest copy of today or yesterday (that one has today in it), or null.</summary>
        static string LatestCopy() => !Directory.Exists(Config.CopyFolder) ? null
            : Directory.GetFiles(Config.CopyFolder, "*.bin").Where(f => string.CompareOrdinal(Path.GetFileName(f), Day(-1)) >= 0).OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();

        /// <summary>Apri la copia dell'agenda: the copy, read-only, until Riprova; no new tries meanwhile.</summary>
        void ShowCopy()
        {
            try
            {
                var file = LatestCopy();
                if (file != null)
                {
                    var html = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
                    retry.Stop();
                    // shortcut: NavigateToString takes up to 2 MB, thousands of appointments in two days; beyond that, a temporary file
                    web.CoreWebView2?.NavigateToString(html);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException || ex is ArgumentException) { }
            Page("La copia dell'agenda non si apre", "La copia salvata su questo PC non si legge. Premi Riprova quando il server risponde.", false, false);
        }

        /// <summary>2.20.7: the PDF of a report table straight into Download, without the print dialog: after that dialog
        /// the window took no more clicks once Windows had switched to another window. The page keeps only that table.</summary>
        async void SavePdf(string name)
        {
            var core = web.CoreWebView2;
            if (core == null) return;
            var path = Free(name, ".pdf", "Report");
            var done = false;
            try
            {
                Directory.CreateDirectory(Downloads);
                var settings = core.Environment.CreatePrintSettings();
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                done = await core.PrintToPdfAsync(path, settings);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is COMException || ex is ArgumentException) { }
            web.CoreWebView2?.PostWebMessageAsString("tdwin-pdf-done:" + (done ? "ok" : ""));
            if (done) Show(path);
            else MessageBox.Show(this, "Non riesco a salvare il PDF nella cartella Download.", "TD Booking Suite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static string Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        /// <summary>A name in Download not taken yet: «Report.pdf», then «Report (2).pdf»…</summary>
        static string Free(string name, string ext, string fallback)
        {
            var safe = Regex.Replace(name, @"[^\p{L}\p{N} ._()-]", "").Trim();
            if (safe.Length == 0) safe = fallback;
            if (safe.Length > 80) safe = safe.Substring(0, 80).Trim();
            var path = Path.Combine(Downloads, safe + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(Downloads, $"{safe} ({i}){ext}");
            return path;
        }

        static readonly string[] Documents = { ".pdf", ".xlsx", ".xls", ".csv", ".ics", ".txt" };

        /// <summary>A document opens with its program (Excel, the PDF reader, the calendar); any other file (logs, backup,
        /// certificate) is shown selected in its folder, never opened: a certificate would start installing.</summary>
        void Show(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (Array.IndexOf(Documents, ext) >= 0) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return; }
            }
            catch (System.ComponentModel.Win32Exception) { }  // no program for it on this PC: the folder, below
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch (System.ComponentModel.Win32Exception) { }
        }

        /// <summary>2.20.11: every file the pages give (Excel, logs, backup, certificate) goes straight into Download like the
        /// PDF of a report, without asking, never the browser's download bar; then <see cref="Show"/>.</summary>
        void Download(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            downloading = true;
            var name = Path.GetFileName(e.ResultFilePath);
            var path = Free(Path.GetFileNameWithoutExtension(name), Path.GetExtension(name), "File");
            try { Directory.CreateDirectory(Downloads); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            e.ResultFilePath = path;
            e.Handled = true;
            var operation = e.DownloadOperation;
            operation.StateChanged += (o, x) =>
            {
                var state = operation.State;
                if (state == CoreWebView2DownloadState.Completed) BeginInvoke((Action)(() => Show(path)));
                else if (state == CoreWebView2DownloadState.Interrupted)
                    BeginInvoke((Action)(() => MessageBox.Show(this, "Il file non si è salvato nella cartella Download. Riprova.", "TD Booking Suite", MessageBoxButtons.OK, MessageBoxIcon.Warning)));
            };
        }

        /// <summary>2.20.11: alert, confirm and the leave-the-page question of the pages as Windows windows.</summary>
        void Dialog(object sender, CoreWebView2ScriptDialogOpeningEventArgs e)
        {
            var later = e.GetDeferral();
            BeginInvoke((Action)(() =>
            {
                try
                {
                    switch (e.Kind)
                    {
                        case CoreWebView2ScriptDialogKind.Alert:
                            MessageBox.Show(this, e.Message, "TD Booking Suite", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            e.Accept();
                            break;
                        case CoreWebView2ScriptDialogKind.Confirm:
                            if (MessageBox.Show(this, e.Message, "TD Booking Suite", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) e.Accept();
                            break;
                        case CoreWebView2ScriptDialogKind.Beforeunload:
                            if (MessageBox.Show(this, "Vuoi lasciare la pagina? Quello che non hai salvato si perde.", "TD Booking Suite", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) e.Accept();
                            break;
                        // a prompt: no page of the program asks one; not accepted, as if cancelled
                    }
                }
                finally { later.Complete(); GiveFocus(); }
            }));
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
                // 2.20.5: on the server PC the fingerprint comes from the installation, a new one chosen here would be
                // overwritten at the next start: Ripara writes it again, instead of a loop on this warning
                if (config.ThisPc || config.Standalone)
                {
                    MessageBox.Show(this,
                        "Il certificato del programma su questo PC è cambiato e la finestra ha ancora quello vecchio.\r\n\r\n" +
                        "Chiudi il programma e ripara l'installazione: Impostazioni > App > App installate > TD Booking Suite > Modifica > Ripara " +
                        "(serve un amministratore). Poi riapri. Le altre postazioni chiederanno una volta di confermare la nuova impronta.",
                        "TD Booking Suite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    // open, every page and every Salva would be cancelled without a word
                    Close();
                    return;
                }
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

        // 2.14.0: the MSI checked with the SHA-256 published next to it, then msiexec with Windows' confirmation (an
        // administrator of this PC). It keeps Un PC solo, server or postazione; this window closes so its files can change.
        // shortcut: no Authenticode check of its own while the MSI is unsigned, Windows shows the publisher in the confirmation
        async void Update(string url)
        {
            if (MessageBox.Show(this, "Scarico la versione nuova, la controllo e la installo. Il programma si chiude per qualche minuto: poi riaprilo.\r\n\r\nContinuo?",
                "TD Booking Suite", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var msi = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(url).AbsolutePath));
            try
            {
                Cursor = Cursors.WaitCursor;
                using (var http = new WebClient())
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    http.Headers[HttpRequestHeader.UserAgent] = "TD-Booking-Suite";
                    var expected = Regex.Match(await http.DownloadStringTaskAsync(url + ".sha256"), "[0-9a-fA-F]{64}").Value.ToLowerInvariant();
                    await http.DownloadFileTaskAsync(url, msi);
                    string got;
                    using (var file = File.OpenRead(msi)) using (var sha = SHA256.Create())
                        got = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                    if (expected.Length != 64 || got != expected)
                    {
                        File.Delete(msi);
                        MessageBox.Show(this, "Il file scaricato non corrisponde al suo SHA-256: non lo installo. Riprova più tardi.", "TD Booking Suite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /passive /norestart") { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Aggiornamento non riuscito: " + ex.Message, "TD Booking Suite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        static void OpenOutside(string url)
        {
            // 2.22.0: a WhatsApp chat (Promemoria WhatsApp, the WhatsApp buttons) opens in WhatsApp for Windows with its
            // text ready, when it is installed; otherwise wa.me in the browser, as before
            var wa = Regex.Match(url, @"^https://wa\.me/(\d{6,15})(?:\?text=([^#]*))?$");
            if (wa.Success && Handles("whatsapp"))
            {
                try { Process.Start(new ProcessStartInfo("whatsapp://send?phone=" + wa.Groups[1].Value + (wa.Groups[2].Success ? "&text=" + wa.Groups[2].Value : "")) { UseShellExecute = true }); return; }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (url.StartsWith("https://") || url.StartsWith("http://") || url.StartsWith("mailto:") || url.StartsWith("tel:"))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        // a program that opens links of this kind (whatsapp:), also an app of the Microsoft Store
        static bool Handles(string scheme)
        {
            const int IS_PROTOCOL = 0x1000, FRIENDLY_APP_NAME = 4;
            var text = new StringBuilder(260); int size = text.Capacity;
            try { return AssocQueryString(IS_PROTOCOL, FRIENDLY_APP_NAME, scheme, null, text, ref size) == 0 && text.Length > 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}
