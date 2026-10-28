using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
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
                Text = "Scrivi l'indirizzo del programma, quello della pagina Postazioni del server, per esempio https://ufficio-pc:8443. Va bene anche l'indirizzo della versione web, per esempio https://booking.tuosito.it",
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
                var ca = await Task.Run(() => Config.Probe(server));
                Result = new Config { Server = server, Ca = ca };
                if (ca == "") status.Text = "Collegamento sicuro: il certificato del server è riconosciuto da Windows.";
                else if (suggested != null && suggested.Ca == ca && string.Equals(suggested.Server, server, StringComparison.OrdinalIgnoreCase))
                    status.Text = "È il server installato su questo PC: impronta del certificato verificata.\r\n\r\n" + Config.Shown(ca);
                else if (ca.StartsWith(Config.CertPrefix))
                    status.Text = "Il certificato del server non è riconosciuto da Windows, per esempio perché è autofirmato. La sua impronta:\r\n\r\n" + Config.Shown(ca) +
                        "\r\n\r\nControlla che sia uguale a quella che ti dà chi gestisce il server (TD Booking Suite per Windows la mostra nella pagina Postazioni), poi premi Continua.";
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
    }
}
