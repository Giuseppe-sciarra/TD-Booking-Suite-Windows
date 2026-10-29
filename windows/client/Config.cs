using System;
using System.IO;
using System.Net.Security;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TDBookingSuite
{
    /// <summary>The server this workstation opens and what it trusts: the fingerprint of the server's certification
    /// authority, "cert:" and the fingerprint of the server's own certificate (self-signed), or nothing for a
    /// certificate Windows already trusts (a server in the cloud, a proxy with Let's Encrypt).</summary>
    class Config
    {
        public string Server;
        public string Ca = "";
        /// <summary>2.10.1: from server.txt, how the server on this PC is installed: "standalone" is Un PC solo.</summary>
        public string Mode = "";
        /// <summary>Un PC solo: the program answers this PC only, so its window opens nothing but the server installed here.</summary>
        public bool Standalone => Mode == "standalone" || Uri.TryCreate(Server, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");
        /// <summary>The address is this PC itself (its name, localhost, a loopback address): the server installed here.</summary>
        public bool ThisPc => Uri.TryCreate(Server, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || uri.Host == "localhost" || string.Equals(uri.Host.Split('.')[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        /// <summary>Chosen by hand with Cambia server: on the server PC it wins over the server installed there.</summary>
        public bool Manual;
        /// <summary>False for a file of 2.8, written before Manual existed.</summary>
        public bool ManualKnown;
        /// <summary>Before a fingerprint: the server's own certificate is trusted, not an authority (self-signed).</summary>
        public const string CertPrefix = "cert:";
        public static string Shown(string ca) => ca.StartsWith(CertPrefix) ? ca.Substring(CertPrefix.Length) : ca;

        static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TD Booking Suite");
        static string FilePath => Path.Combine(Folder, "postazione.txt");
        public static string WebViewFolder => Path.Combine(Folder, "WebView2");
        // written by the server installation, so the program on the server PC needs no questions
        static string SharedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TD Booking Suite Postazione", "server.txt");

        public static Config Load() => Read(FilePath);
        /// <summary>The server installed on this PC, only where the installer put it (its own registry value, which only
        /// administrators write): elsewhere any user of the PC could plant the file and point the window to his server.</summary>
        public static Config Default()
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(@"Software\TD Booking Suite"))
                if (key?.GetValue("Server") == null) return null;
            return Read(SharedPath);
        }

        static Config Read(string path)
        {
            if (!File.Exists(path)) return null;
            var config = new Config();
            foreach (var line in File.ReadAllLines(path))
            {
                var i = line.IndexOf('=');
                if (i < 0) continue;
                var key = line.Substring(0, i).Trim();
                var value = line.Substring(i + 1).Trim();
                if (key == "server") config.Server = value;
                else if (key == "ca") config.Ca = value;
                else if (key == "modalita") config.Mode = value;
                else if (key == "manuale") { config.Manual = value == "1"; config.ManualKnown = true; }
            }
            return string.IsNullOrEmpty(config.Server) ? null : config;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, "server=" + Server + "\r\nca=" + Ca + "\r\nmanuale=" + (Manual ? "1" : "0") + "\r\n");
            ManualKnown = true;
        }

        /// <summary>https://host[:port] from what the person typed, or null.</summary>
        public static string Normalize(string text)
        {
            text = (text ?? "").Trim().TrimEnd('/');
            if (text.Length == 0) return null;
            if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) text = "https://" + Regex.Replace(text, "^[a-z]+://", "", RegexOptions.IgnoreCase);
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
            return uri.GetLeftPart(UriPartial.Authority);
        }

        public bool SameOrigin(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.GetLeftPart(UriPartial.Authority), Server, StringComparison.OrdinalIgnoreCase);

        /// <summary>SHA-256 in groups of four, as the Postazioni page of the server shows it.</summary>
        public static string Fingerprint(byte[] der)
        {
            using (var sha = SHA256.Create())
            {
                var hex = BitConverter.ToString(sha.ComputeHash(der)).Replace("-", "");
                var text = new StringBuilder();
                for (int i = 0; i < hex.Length; i += 4)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(hex, i, 4);
                }
                return text.ToString();
            }
        }

        public static byte[] PemToDer(string pem) => Convert.FromBase64String(Regex.Replace(pem, "-----[^-]+-----|\\s", ""));

        /// <summary>True only when the authority really signed the certificate and it is within its dates:
        /// the authority's certificate is public, matching its fingerprint alone proves nothing.</summary>
        public static bool SignedBy(X509Certificate2 certificate, X509Certificate2 authority)
        {
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.ExtraStore.Add(authority);
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (!chain.Build(certificate)) return false;
                return chain.ChainElements[chain.ChainElements.Count - 1].Certificate.Thumbprint == authority.Thumbprint;
            }
        }

        /// <summary>What this workstation will trust: "" when Windows trusts the certificate (Let's Encrypt, a company
        /// authority, the web version), the fingerprint of the program's own authority (TD Booking Suite per Windows
        /// with its internal certificate), or "cert:" and the fingerprint of the certificate itself (self-signed).</summary>
        public static string Probe(string server)
        {
            X509Certificate2 leaf = null;
            var errors = SslPolicyErrors.None;
            var request = (HttpWebRequest)WebRequest.Create(server + "/postazione/certificato.crt");
            request.Timeout = 15000;
            request.AllowAutoRedirect = false;
            // only to read the certificate: it is checked below, before anything is trusted
            request.ServerCertificateValidationCallback = (s, cert, chain, err) => { leaf = new X509Certificate2(cert); errors = err; return true; };
            byte[] body = null;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var copy = new MemoryStream())
                {
                    if (response.StatusCode == HttpStatusCode.OK) response.GetResponseStream().CopyTo(copy);
                    body = copy.ToArray();
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse) { }  // a server without that page: the web version, a proxy
            catch (WebException ex) { throw new Exception(ex.Status == WebExceptionStatus.NameResolutionFailure ? "nome del server sconosciuto." : ex.Status == WebExceptionStatus.Timeout ? "il server non risponde." : ex.Message); }
            if (leaf == null) throw new Exception("il server non ha mostrato un certificato: l'indirizzo deve essere https.");
            // trusted by Windows, but a name only in the CN and not among the alternative names is refused by
            // WebView2 as by every browser: then this very certificate is the one to recognise
            if (errors == SslPolicyErrors.None && leaf.Extensions["2.5.29.17"] != null) return "";
            if (body != null && body.Length > 0)
            {
                try
                {
                    var text = Encoding.ASCII.GetString(body);
                    var ca = new X509Certificate2(text.Contains("-----BEGIN") ? PemToDer(text) : body);
                    if (SignedBy(leaf, ca)) return Fingerprint(ca.RawData);
                }
                catch (CryptographicException) { }
                catch (FormatException) { }
            }
            // a certificate no authority known here vouches for: this very certificate, recognised by its fingerprint
            return CertPrefix + Fingerprint(leaf.RawData);
        }
    }
}
