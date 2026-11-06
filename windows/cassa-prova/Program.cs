// TD Cassa di prova: the window. Choose the model and how it answers, write in TD Booking Suite the address shown
// here, and each document the program sends shows up on the right, as it would be printed.
using System;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Net.Sockets;
using System.Windows.Forms;

namespace TDCassaProva
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Main());
        }
    }

    class Main : Form
    {
        readonly Register reg = new Register();
        readonly ComboBox model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly ComboBox answer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly NumericUpDown port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 80, Width = 90 };
        readonly CheckBox popup = new CheckBox { Text = "Mostra ogni documento in una finestra", Checked = true, AutoSize = true };
        readonly Button start = new Button { Text = "Accendi la cassa di prova", AutoSize = true, Height = 34 };
        readonly Label address = new Label { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.FromArgb(70, 40, 120) };
        readonly Label status = new Label { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        readonly ListBox docs = new ListBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10), IntegralHeight = false };
        readonly TextBox paper = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 11), BackColor = Color.White };
        bool on;

        public Main()
        {
            Text = "TD Cassa di prova · non stampa niente";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = new Font("Segoe UI", 10); Width = 1080; Height = 720; StartPosition = FormStartPosition.CenterScreen;
            model.Items.AddRange(new object[] { "RCH Print!F (in rete)", "Epson FP-81II / FP-90III RT (in rete)" }); model.SelectedIndex = 0;
            answer.Items.AddRange(new object[] { "Tutto ok: scontrino emesso", "Carta finita: non emesso", "Occupata, poi ok (solo RCH)", "Non risponde: da verificare" }); answer.SelectedIndex = 0;
            model.SelectedIndexChanged += (s, e) => reg.Model = model.SelectedIndex == 0 ? Model.Rch : Model.Epson;
            answer.SelectedIndexChanged += (s, e) => reg.Answer = (Answer)answer.SelectedIndex;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row1.Controls.AddRange(new Control[] { Caption("Si presenta come"), model, Caption("Risponde"), answer, Caption("Porta"), port });
            var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row2.Controls.AddRange(new Control[] { start, popup, status });
            top.Controls.AddRange(new Control[] { row1, row2, address, new Label { AutoSize = true, MaximumSize = new Size(1040, 0), ForeColor = Color.DimGray,
                Text = "In TD Booking Suite, Registratore di cassa: scegli lo stesso modello, collegata in Rete, e scrivi l'indirizzo qui sopra (oppure premi Cerca la cassa). " +
                       "Poi Prova collegamento, Stampa scontrino di prova e i conti dalla Cassa: qui compare ogni documento, come sarebbe stampato. Non è un registratore vero: niente va all'Agenzia delle Entrate." } });

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380 };
            split.Panel1.Controls.Add(docs); split.Panel2.Controls.Add(paper);
            Controls.Add(split); Controls.Add(top);
            docs.SelectedIndexChanged += (s, e) => { if (docs.SelectedItem is Item i) paper.Text = Show(i.Doc); };

            start.Click += (s, e) => Toggle();
            reg.Printed += d => BeginInvoke((Action)(() => Got(d)));
            reg.Note += n => BeginInvoke((Action)(() => { status.ForeColor = Color.SeaGreen; status.Text = n; }));
            FormClosed += (s, e) => reg.Stop();
            Shown += (s, e) => Toggle();  // after the window exists: the answers are written in it
        }

        static Label Caption(string t) => new Label { Text = t, AutoSize = true, Padding = new Padding(8, 6, 2, 0) };
        static string Show(Doc d) => (d.Title + "\r\n" + d.At.ToString("dd/MM/yyyy HH:mm:ss") + "\r\n" + d.Outcome + "\r\n\r\n" + d.Text).Replace("\r\n", "\n").Replace("\n", "\r\n");

        void Toggle()
        {
            if (on) { reg.Stop(); on = false; start.Text = "Accendi la cassa di prova"; status.ForeColor = Color.Firebrick; status.Text = "Spenta: il programma non la trova"; address.Text = ""; port.Enabled = true; return; }
            try { reg.Start((int)port.Value); }
            catch (SocketException ex)
            {
                status.ForeColor = Color.Firebrick;
                status.Text = ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? $"La porta {port.Value} è già usata da un altro programma: scegline un'altra, per esempio 8080" : "Non si accende: " + ex.Message;
                return;
            }
            on = true; port.Enabled = false; start.Text = "Spegni";
            status.ForeColor = Color.SeaGreen; status.Text = "Accesa, in attesa del programma";
            var ips = Register.Addresses().ToList();
            address.Text = ips.Count == 0 ? "Indirizzo: nessuna rete trovata su questo PC" :
                "Indirizzo da scrivere nel programma: " + string.Join("  oppure  ", ips.Select(a => port.Value == 80 ? a : $"{a}:{port.Value}"));
        }

        void Got(Doc d)
        {
            var item = new Item { Doc = d };
            docs.Items.Insert(0, item); docs.SelectedIndex = 0;
            status.ForeColor = d.Outcome.StartsWith("Risposto: tutto ok") ? Color.SeaGreen : Color.DarkOrange;
            status.Text = (d.Outcome.StartsWith("Risposto: tutto ok") ? "Prova riuscita · " : "") + d.Title;
            try { SystemSounds.Asterisk.Play(); } catch { }
            if (popup.Checked)
            {
                var f = new Form { Text = d.Title, Width = 460, Height = 560, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false, Font = Font };
                f.Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - f.Width - 20, Screen.PrimaryScreen.WorkingArea.Bottom - f.Height - 20);
                var head = new Label { Dock = DockStyle.Top, Height = 44, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12, FontStyle.Bold),
                    ForeColor = Color.White, BackColor = d.Outcome.StartsWith("Risposto: tutto ok") ? Color.SeaGreen : Color.DarkOrange,
                    Text = d.Outcome.StartsWith("Risposto: tutto ok") ? "✓ Prova riuscita: stampato questo" : "! " + d.Outcome.Split(':')[0] };
                var body = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10.5f), Text = Show(d), BackColor = Color.White };
                var ok = new Button { Text = "Chiudi", Dock = DockStyle.Bottom, Height = 36 };
                ok.Click += (s, e) => f.Close();
                f.Controls.Add(body); f.Controls.Add(head); f.Controls.Add(ok);
                f.Show(this); body.SelectionLength = 0;
            }
        }

        class Item { public Doc Doc; public override string ToString() => Doc.At.ToString("HH:mm:ss") + "  " + Doc.Title; }
    }
}
