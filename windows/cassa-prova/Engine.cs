// TD Cassa di prova: the register side, without the window. It answers on the network as the RCH Print!F web
// service (POST /service.cgi) or as Epson fpMate (POST /cgi-bin/fpmate.cgi), as TD Booking Suite talks to them, and
// turns each request into the document a real register would have printed. Nothing is printed, nothing is sent to
// the Agenzia delle Entrate: it only says what it got.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TDCassaProva
{
    public enum Model { Rch, Epson }
    public enum Answer { Ok, PaperEnd, BusyOnce, Silent }

    public class Doc
    {
        public DateTime At = DateTime.Now;
        public string Title = "";   // "Scontrino n. 0012 · 48,50 €"
        public string Text = "";    // the paper, line by line
        public bool Fiscal;         // a documento commerciale (or its annullo, or the closure)
        public string Outcome = ""; // what the program was told
    }

    public class Register
    {
        public Model Model = Model.Rch;
        public Answer Answer = Answer.Ok;
        public int Number = 1, Z = 1;
        public event Action<Doc> Printed;
        public event Action<string> Note;
        TcpListener listener;
        bool busyGiven;
        readonly object gate = new object();
        static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

        public void Start(int port)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            new Thread(() =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); } catch { return; }
                    Task.Run(() => Serve(c));
                }
            }) { IsBackground = true }.Start();
        }
        public void Stop() { try { listener?.Stop(); } catch { } }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    var s = client.GetStream();
                    var (path, body) = Read(s);
                    if (path == null) return;
                    int status; string answer;
                    lock (gate) (status, answer) = Handle(path, body, out _);
                    if (status == 0) { Thread.Sleep(25000); return; } // «Nessuna risposta»: the program gives up, the receipt is to check
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length); s.Write(bytes, 0, bytes.Length); s.Flush();
                }
                catch (Exception ex) { Note?.Invoke("Richiesta non letta: " + ex.Message); }
            }
        }

        static (string, string) Read(Stream s)
        {
            var buf = new MemoryStream(); var one = new byte[1]; int end = -1;
            while (end < 0 && buf.Length < 65536)
            {
                if (s.Read(one, 0, 1) <= 0) return (null, null);
                buf.WriteByte(one[0]);
                var a = buf.GetBuffer(); var n = (int)buf.Length;
                if (n >= 4 && a[n - 4] == 13 && a[n - 3] == 10 && a[n - 2] == 13 && a[n - 1] == 10) end = n;
            }
            var head = Encoding.ASCII.GetString(buf.ToArray());
            var first = head.Split('\n')[0].Trim().Split(' ');
            if (first.Length < 2) return (null, null);
            var len = Regex.Match(head, @"(?im)^content-length:\s*(\d+)");
            int size = len.Success ? int.Parse(len.Groups[1].Value) : 0;
            var body = new byte[Math.Min(size, 4 << 20)]; int got = 0;
            while (got < body.Length) { int r = s.Read(body, got, body.Length - got); if (r <= 0) break; got += r; }
            return (first[1], Encoding.UTF8.GetString(body, 0, got));
        }

        // the answer to one request: (0, "") for no answer at all
        public (int, string) Handle(string path, string body, out Doc doc)
        {
            doc = null;
            bool rch = path.StartsWith("/service.cgi"), epson = path.StartsWith("/cgi-bin/fpmate.cgi");
            if (rch && Model != Model.Rch || epson && Model != Model.Epson || !rch && !epson) return (404, "");
            doc = rch ? FromRch(body) : FromEpson(body);
            bool probe = doc == null;   // a status question, nothing to print
            if (probe) Note?.Invoke("Il programma ha chiesto lo stato della cassa alle " + DateTime.Now.ToString("HH:mm:ss") + ": risposto «pronta» (Prova collegamento riuscita)");
            if (!probe && Answer == Answer.BusyOnce && rch && !busyGiven)  // only RCH says «busy»: an Epson waits by itself
            {
                busyGiven = true;
                Note?.Invoke("Risposto «occupata»: il programma la rimanda tra un secondo.");
                return (200, rch ? RchAnswer(busy: true) : EpsonAnswer(false, "EPTR_REC_EMPTY", ""));
            }
            if (Answer == Answer.BusyOnce && !probe) busyGiven = false;
            if (!probe && Answer == Answer.Silent)
            {
                doc.Outcome = "Nessuna risposta (prova di «Da verificare»): il programma chiederà se lo scontrino è uscito";
                Printed?.Invoke(doc);
                return (0, "");
            }
            if (!probe && Answer == Answer.PaperEnd)
            {
                doc.Outcome = "Risposto «carta finita»: per il programma non è uscito (Non emesso, Riprova)";
                doc.Title = "NON STAMPATO · " + doc.Title;
                Printed?.Invoke(doc);
                return (200, rch ? RchAnswer(paper: true) : EpsonAnswer(false, "EPTR_REC_EMPTY", "2"));
            }
            string number = "";
            if (doc != null)
            {
                if (doc.Fiscal && doc.Title.StartsWith("Scontrino")) { number = Number.ToString("0000"); doc.Title = doc.Title.Replace("{n}", number + (Model == Model.Epson ? $" (chiusura {Z:0000})" : "")); doc.Text = doc.Text.Replace("{n}", $"{Z:0000}-{number}"); Number++; }
                if (doc.Title.StartsWith("Chiusura")) { doc.Title += $" n. {Z:0000}"; Z++; Number = 1; }
                doc.Outcome = "Risposto: tutto ok" + (number != "" && Model == Model.Epson ? $", documento n. {Z:0000}-{number}" : "");
                Printed?.Invoke(doc);
            }
            return (200, rch ? RchAnswer() : EpsonAnswer(true, "", "2", number));
        }

        static string RchAnswer(bool busy = false, bool paper = false) =>
            "<?xml version='1.0'?><Service><Request><errorCode>" + (paper ? "8" : "0") + "</errorCode><printerError>0</printerError><paperEnd>" + (paper ? "1" : "0") +
            "</paperEnd><coverOpen>0</coverOpen><lastCmd>0</lastCmd><busy>" + (busy ? "1" : "0") + "</busy></Request></Service>";
        string EpsonAnswer(bool ok, string code, string status, string number = "") =>
            "<?xml version=\"1.0\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body><response success=\"" + (ok ? "true" : "false") + "\" code=\"" + code + "\" status=\"" + status + "\">" +
            "<addInfo><elementList>lastCommand,printerStatus,fiscalReceiptNumber,zRepNumber</elementList><lastCommand>74</lastCommand><printerStatus>20010</printerStatus><fiscalReceiptNumber>" + (number == "" ? "0" : number) +
            "</fiscalReceiptNumber><zRepNumber>" + Z.ToString("0000") + "</zRepNumber></addInfo></response></soapenv:Body></soapenv:Envelope>";

        static string Euro(long cents) => (cents / 100m).ToString("#,0.00", It) + " €";
        static string Row(string left, string right, int width = 40) { left = left.Length > width - right.Length - 1 ? left.Substring(0, width - right.Length - 1) : left; return left + new string(' ', Math.Max(1, width - left.Length - right.Length)) + right; }
        static string Rule => new string('-', 40);

        // ---------- RCH: one command a line, as the program sends them
        public Doc FromRch(string body)
        {
            List<string> cmds;
            try { cmds = XDocument.Parse(body).Descendants().Where(e => e.Name.LocalName == "cmd").Select(e => e.Value.Trim()).ToList(); }
            catch { cmds = new List<string>(); }
            var real = cmds.Where(c => c != "=K" && c != "=C1").ToList();
            if (real.Count == 0) return null; // the status question of «Prova collegamento» and of «Cerca la cassa»
            var t = new StringBuilder(); var d = new Doc();
            if (real.Contains("=o"))
            {
                d.Title = "Documento non fiscale (stampa di prova)";
                t.AppendLine("DOCUMENTO NON FISCALE");
                foreach (var c in real) { var m = Regex.Match(c, "^=\"/\\((.*)\\)$"); if (m.Success) t.AppendLine(m.Groups[1].Value); }
                d.Text = t.ToString(); return d;
            }
            if (real.Any(c => c.StartsWith("=k")))
            {
                var m = Regex.Match(real.First(c => c.StartsWith("=k")), @"=k/&(\d{6})/(?:\[(\d+)/\])?(\d+)");
                d.Fiscal = true; d.Title = "Annullo" + (m.Success ? $" del documento {m.Groups[2].Value}-{m.Groups[3].Value} del {m.Groups[1].Value.Substring(0, 2)}/{m.Groups[1].Value.Substring(2, 2)}/{m.Groups[1].Value.Substring(4, 2)}" : "");
                d.Text = "DOCUMENTO DI ANNULLO\n" + string.Join("\n", real) + "\n"; return d;
            }
            if (real.Contains("=C3") || real.Contains("=C10"))
            {
                d.Fiscal = true; d.Title = "Chiusura giornaliera";
                d.Text = "CHIUSURA GIORNALIERA (Z)\nCorrispettivi trasmessi (per finta)\n" + string.Join(" ", real) + "\n"; return d;
            }
            long total = 0, paid = 0; var lines = new StringBuilder(); string cf = "", lottery = "";
            foreach (var c in real)
            {
                Match m;
                if ((m = Regex.Match(c, @"^=R(\d+)/\$(\d+)/\((.*)\)$")).Success)
                { long p = long.Parse(m.Groups[2].Value); total += p; lines.AppendLine(Row(m.Groups[3].Value + "  R" + m.Groups[1].Value, Euro(p))); }
                else if ((m = Regex.Match(c, @"^=V-/\$(\d+)$")).Success)
                { long p = long.Parse(m.Groups[1].Value); total -= p; lines.AppendLine(Row("  SCONTO", "-" + Euro(p))); }
                else if ((m = Regex.Match(c, @"^=T(\d+)(?:/\$(\d+))?$")).Success)
                { long p = m.Groups[2].Success ? long.Parse(m.Groups[2].Value) : total - paid; paid += p; lines.AppendLine(Row("PAGAMENTO " + m.Groups[1].Value + " " + RchPay(m.Groups[1].Value), Euro(p))); }
                else if (c == "=S") lines.AppendLine(Rule).AppendLine(Row("TOTALE", Euro(total)));
                else if ((m = Regex.Match(c, @"^=""/\?C/\((.*)\)$")).Success) cf = m.Groups[1].Value;
                else if ((m = Regex.Match(c, @"^=""/\?L/\$1/\((.*)\)$")).Success) lottery = m.Groups[1].Value;
                else lines.AppendLine("  (comando " + c + ")");
            }
            d.Fiscal = true; d.Title = "Scontrino n. {n} · " + Euro(total);
            t.AppendLine("DOCUMENTO COMMERCIALE").AppendLine("di vendita o prestazione").AppendLine(Rule).Append(lines);
            if (cf != "") t.AppendLine("C.F. CLIENTE " + cf);
            if (lottery != "") t.AppendLine("CODICE LOTTERIA " + lottery);
            t.AppendLine(Rule).AppendLine(DateTime.Now.ToString("dd-MM-yyyy HH:mm") + "  DOC. N. {n}");
            if (paid != total) t.AppendLine("ATTENZIONE: pagato " + Euro(paid) + " su " + Euro(total));
            d.Text = t.ToString(); return d;
        }
        static string RchPay(string n) => n == "1" ? "(contanti)" : n == "4" ? "(carta)" : n == "2" ? "(non riscosso / altro)" : "";

        // ---------- Epson fpMate: the XML of the Fiscal ePOS-Print
        public Doc FromEpson(string body)
        {
            XElement root;
            try { root = XDocument.Parse(body).Root; } catch { return null; }
            var all = root.Descendants().ToList();
            string Attr(XElement e, string n) => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == n) ?? "";
            if (all.Any(e => e.Name.LocalName == "queryPrinterStatus")) return null;
            var d = new Doc(); var t = new StringBuilder();
            if (all.Any(e => e.Name.LocalName == "printerNonFiscal"))
            {
                d.Title = "Documento non fiscale (stampa di prova)"; t.AppendLine("DOCUMENTO NON FISCALE");
                foreach (var e in all.Where(e => e.Name.LocalName == "printNormal")) t.AppendLine(Attr(e, "data"));
                d.Text = t.ToString(); return d;
            }
            if (all.Any(e => e.Name.LocalName == "printZReport"))
            { d.Fiscal = true; d.Title = "Chiusura giornaliera"; d.Text = "CHIUSURA GIORNALIERA (Z)\nCorrispettivi trasmessi (per finta)\n"; return d; }
            var msg = all.FirstOrDefault(e => e.Name.LocalName == "printRecMessage" && Attr(e, "messageType") == "4");
            if (msg != null && Attr(msg, "message").StartsWith("VOID"))
            {
                var p = Attr(msg, "message").Split(' ');
                d.Fiscal = true; d.Title = p.Length >= 4 ? $"Annullo del documento {p[1]}-{p[2]} del {p[3].Substring(0, 2)}/{p[3].Substring(2, 2)}/{p[3].Substring(4)}" : "Annullo";
                d.Text = "DOCUMENTO DI ANNULLO\n" + Attr(msg, "message") + "\n"; return d;
            }
            long total = 0, paid = 0; var lines = new StringBuilder();
            long Cents(string v) => (long)Math.Round(decimal.Parse(v.Replace(".", "").Replace(",", "."), CultureInfo.InvariantCulture) * 100);
            foreach (var e in all)
            {
                switch (e.Name.LocalName)
                {
                    case "printRecItem":
                        long q = long.Parse(Attr(e, "quantity") == "" ? "1" : Attr(e, "quantity")), u = Cents(Attr(e, "unitPrice"));
                        total += q * u; lines.AppendLine(Row((q > 1 ? q + " x " : "") + Attr(e, "description") + "  R" + Attr(e, "department"), Euro(q * u))); break;
                    case "printRecItemAdjustment":
                    case "printRecSubtotalAdjustment":
                        long x = Cents(Attr(e, "amount")); total -= x; lines.AppendLine(Row("  " + Attr(e, "description"), "-" + Euro(x))); break;
                    case "printRecMessage":
                        if (Attr(e, "messageType") == "7") lines.AppendLine("C.F. CLIENTE " + Attr(e, "message")); break;
                    case "printRecLotteryID":
                        lines.AppendLine("CODICE LOTTERIA " + Attr(e, "code")); break;
                    case "printRecTotal":
                        long pay = Cents(Attr(e, "payment")); paid += pay;
                        lines.AppendLine(Row(Attr(e, "description") + " (tipo " + Attr(e, "paymentType") + ")", Euro(pay))); break;
                }
            }
            d.Fiscal = true; d.Title = "Scontrino n. {n} · " + Euro(total);
            t.AppendLine("DOCUMENTO COMMERCIALE").AppendLine("di vendita o prestazione").AppendLine(Rule).Append(lines).AppendLine(Rule).AppendLine(Row("TOTALE COMPLESSIVO", Euro(total)));
            t.AppendLine(DateTime.Now.ToString("dd-MM-yyyy HH:mm") + "  DOC. N. {n}");
            if (paid != total) t.AppendLine("ATTENZIONE: pagato " + Euro(paid) + " su " + Euro(total));
            d.Text = t.ToString(); return d;
        }

        // the IPv4 addresses of this PC in the shop's network, to write in the program
        public static IEnumerable<string> Addresses()
        {
            try { return Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString()).Distinct().ToList(); }
            catch { return new string[0]; }
        }
    }
}
