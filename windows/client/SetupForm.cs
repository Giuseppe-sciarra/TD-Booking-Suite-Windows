using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TDBookingSuite
{
    /// <summary>First start: the address of the server and, for a certificate made by the server itself,
    /// its fingerprint to compare with the one on the server's Postazioni page.</summary>
    class SetupForm : Form
    {
        public Config Result;
        readonly Config suggested;
        readonly TextBox address = new TextBox { Dock = DockStyle.Top };
        readonly Button connect = new Button { Text = "Collegati", AutoSize = true };
        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        readonly Button ok = new Button { Text = "Continua", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
        readonly Button cancel = new Button { Text = "Annulla", AutoSize = true, DialogResult = DialogResult.Cancel };

        public SetupForm(Config current)
        {
            suggested = current;
            Text = "TD Booking Suite";
            Icon = MainForm.AppIcon();
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 360);
            AcceptButton = connect;
            CancelButton = cancel;
            Padding = new Padding(24);

            var title = new Label { Text = "Collega questa postazione", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Dock = DockStyle.Top };
            var help = new Label
            {
                Text = "Scrivi l'indirizzo del programma, quello della pagina Postazioni del server. Per esempio https://ufficio-pc:8443",
                Dock = DockStyle.Top, Height = 52, Padding = new Padding(0, 8, 0, 0)
            };
            address.Text = current?.Server ?? "";
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            row.Controls.Add(connect);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Controls.Add(status);
            Controls.Add(buttons);
            Controls.Add(row);
            Controls.Add(address);
            Controls.Add(help);
            Controls.Add(title);
            connect.Click += async (s, e) => await Check();
            address.TextChanged += (s, e) => { ok.Enabled = false; AcceptButton = connect; };
        }

        async Task Check()
        {
            var server = Config.Normalize(address.Text);
            if (server == null) { status.Text = "Indirizzo non valido: scrivilo come https://nome-del-server:8443"; return; }
            connect.Enabled = false;
            status.Text = "Mi collego a " + server + "...";
            try
            {
                var ca = await Task.Run(() => Probe(server));
                Result = new Config { Server = server, Ca = ca };
                if (ca == "") status.Text = "Collegamento sicuro: il certificato del server è riconosciuto da Windows.";
                else if (suggested != null && suggested.Ca == ca && string.Equals(suggested.Server, server, StringComparison.OrdinalIgnoreCase))
                    status.Text = "È il server installato su questo PC: impronta del certificato verificata.\r\n\r\n" + ca;
                else status.Text = "Impronta del certificato del server:\r\n\r\n" + ca + "\r\n\r\nControlla che sia uguale a quella nella pagina Postazioni del server, poi premi Continua.";
                ok.Enabled = true;
                AcceptButton = ok;
                ok.Focus();
            }
            catch (Exception ex)
            {
                status.Text = "Non riesco a collegarmi: " + ex.Message;
            }
            finally { connect.Enabled = true; }
        }

        /// <summary>The fingerprint of the server's own certification authority, or "" when Windows trusts the certificate.</summary>
        static string Probe(string server)
        {
            X509Certificate2 leaf = null;
            var errors = SslPolicyErrors.None;
            var request = (HttpWebRequest)WebRequest.Create(server + "/postazione/certificato.crt");
            request.Timeout = 15000;
            request.AllowAutoRedirect = false;
            // only to read the certificate: it is checked below, before anything is trusted
            request.ServerCertificateValidationCallback = (s, cert, chain, err) => { leaf = new X509Certificate2(cert); errors = err; return true; };
            byte[] body;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var copy = new MemoryStream())
                {
                    response.GetResponseStream().CopyTo(copy);
                    body = copy.ToArray();
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r)
            {
                throw new Exception(r.StatusCode == HttpStatusCode.NotFound ? "a questo indirizzo non c'è un server TD Booking Suite." : "il server risponde " + (int)r.StatusCode + ".");
            }
            catch (WebException ex) { throw new Exception(ex.Status == WebExceptionStatus.NameResolutionFailure ? "nome del server sconosciuto." : ex.Status == WebExceptionStatus.Timeout ? "il server non risponde." : ex.Message); }
            if (errors == SslPolicyErrors.None) return "";
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) throw new Exception("questo indirizzo non è tra quelli del certificato del server. Usa l'indirizzo della pagina Postazioni.");
            var text = Encoding.ASCII.GetString(body);
            var ca = new X509Certificate2(text.Contains("-----BEGIN") ? Config.PemToDer(text) : body);
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.ExtraStore.Add(ca);
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                var built = chain.Build(leaf);
                var root = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                if (!built || root.Thumbprint != ca.Thumbprint) throw new Exception("il certificato del server non è firmato dalla sua autorità: collegamento non sicuro.");
            }
            return Config.Fingerprint(ca.RawData);
        }
    }
}
