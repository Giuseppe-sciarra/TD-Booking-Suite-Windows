using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace TDBookingSuite
{
    /// <summary>The server this workstation opens and the fingerprint of its certification authority.
    /// An empty fingerprint means a certificate Windows already trusts (a server in the cloud).</summary>
    class Config
    {
        public string Server;
        public string Ca = "";

        static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TD Booking Suite");
        static string FilePath => Path.Combine(Folder, "postazione.txt");
        public static string WebViewFolder => Path.Combine(Folder, "WebView2");
        // written by the server installation, so the program on the server PC needs no questions
        static string SharedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TD Booking Suite Postazione", "server.txt");

        public static Config Load() => Read(FilePath);
        public static Config Default() => Read(SharedPath);

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
            }
            return string.IsNullOrEmpty(config.Server) ? null : config;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, "server=" + Server + "\r\nca=" + Ca + "\r\n");
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
    }
}
