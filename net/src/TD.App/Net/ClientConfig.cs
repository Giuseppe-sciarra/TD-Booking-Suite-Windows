using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

namespace TD.App.Net;

/// <summary>The server this window opens and what it trusts: the same files as the 2.x client (postazione.txt in the
/// user's folder, server.txt written by the server's installation), so a workstation keeps its server and fingerprint.</summary>
public sealed class ClientConfig
{
    public string Server = "";
    public string Ca = "";
    public string Mode = "";
    public bool Manual;
    public const string CertPrefix = "cert:";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TD Booking Suite");
    static string FilePath => Path.Combine(Folder, "postazione.txt");
    static string TokenPath => Path.Combine(Folder, "postazione-net.bin");
    static string SessionPath => Path.Combine(Folder, "sessione-net.bin");
    static string SharedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TD Booking Suite Postazione", "server.txt");

    public bool Standalone => Mode == "standalone";
    public bool ThisPc => Uri.TryCreate(Server, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost" || string.Equals(u.Host.Split('.')[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The server installed on this PC (only where the installer wrote its registry value), else the one chosen here.</summary>
    public static ClientConfig Load()
    {
        var mine = Read(FilePath);
        var installed = Installed();
        if (mine != null && (mine.Manual || installed == null)) return mine;
        return installed ?? mine;
    }
    static ClientConfig Installed()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"Software\TD Booking Suite");
            if (key?.GetValue("Server") == null) return null;
        }
        catch { return null; }
        return Read(SharedPath);
    }
    static ClientConfig Read(string path)
    {
        if (!File.Exists(path)) return null;
        var c = new ClientConfig();
        foreach (var line in File.ReadAllLines(path))
        {
            var i = line.IndexOf('='); if (i < 0) continue;
            var k = line[..i].Trim(); var v = line[(i + 1)..].Trim();
            if (k == "server") c.Server = v; else if (k == "ca") c.Ca = v; else if (k == "modalita") c.Mode = v; else if (k == "manuale") c.Manual = v == "1";
        }
        return string.IsNullOrEmpty(c.Server) ? null : c;
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, $"server={Server}\r\nca={Ca}\r\nmanuale={(Manual ? "1" : "0")}\r\n");
    }

    /// <summary>The workstation's token and the remembered sign in, sealed for this Windows user.</summary>
    public static string ReadSecret(bool session) => ReadProtected(session ? SessionPath : TokenPath);
    public static void WriteSecret(bool session, string value) => WriteProtected(session ? SessionPath : TokenPath, value);
    static string ReadProtected(string path)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); } catch { return ""; }
    }
    static void WriteProtected(string path, string value)
    {
        Directory.CreateDirectory(Folder);
        if (string.IsNullOrEmpty(value)) { try { File.Delete(path); } catch { } return; }
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>https://host[:port] from what the person typed, or null.</summary>
    public static string Normalize(string text)
    {
        text = (text ?? "").Trim().TrimEnd('/');
        if (text == "") return null;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme != "https") return null;
        return $"https://{u.Authority}";
    }

    public static string Fingerprint(byte[] raw)
    {
        var hex = Convert.ToHexString(SHA256.HashData(raw));
        return string.Join(" ", Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>True when the certificate the server shows is the one this window trusts.</summary>
    public bool Trusts(X509Certificate2 leaf, X509Chain chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (errors == System.Net.Security.SslPolicyErrors.None) return true;
        if (string.IsNullOrEmpty(Ca) || leaf == null) return false;
        if (Ca.StartsWith(CertPrefix)) return Fingerprint(leaf.RawData) == Ca[CertPrefix.Length..];
        // the program's own authority: it must really have signed the certificate, its fingerprint alone proves nothing
        var authority = chain?.ChainElements.Cast<X509ChainElement>().Select(e => e.Certificate).FirstOrDefault(x => Fingerprint(x.RawData) == Ca);
        if (authority == null) return false;
        using var check = new X509Chain();
        check.ChainPolicy.ExtraStore.Add(authority);
        check.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        check.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return check.Build(leaf) && check.ChainElements[^1].Certificate.Thumbprint == authority.Thumbprint;
    }
}
