using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;
using TD.Core.Win;

namespace TD.Server;

/// <summary>db-admin.json: the administrator connection the worker uses for the tables, and the runtime role's password.</summary>
public sealed record Admin(string Url, string AppPassword)
{
    public static Admin Read(string folder)
    {
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "db-admin.json"))) as JsonObject;
            return new Admin((string)o["url"], (string)o["app_password"]);
        }
        catch { return null; }  // the web service may not read it, by design
    }
}

/// <summary>What every request shares: configuration, database, keys, licence, workstations, the certificate shown.</summary>
public sealed class Server
{
    public const string Version = "3.0.0";
    public AppConfig Config { get; }
    public Admin Admin { get; }
    public Db Db { get; }
    public Crypto Crypto { get; }
    public Identity Identity { get; }
    public Licensing Licensing { get; }
    public Workstations Workstations { get; }

    public Server(AppConfig config, Admin admin)
    {
        Config = config; Admin = admin;
        Db = new Db(AppConfig.ToNpgsql(config.DatabaseUrl));
        Crypto = new Crypto(config.KeysJson, config.ActiveKey);
        Identity = new Identity(Db, Crypto);
        Licensing = new Licensing(config.DataFolder, config.InstallId, Db);
        Workstations = new Workstations(Db, Licensing);
        Rete = new Web.Rete(this);
        // what the domain asks of the server: the accounts' emails (sealed with the master key), the signing key, the .env
        Notify.EmailOf = id => { using var g = Db.Global(); var u = g.Get<User>(id); return u == null ? "" : Identity.Email(u); };
        WpSync.Install(); WpSite.Face = Work.Faces.Small;  // every save of an archived row reaches the site's outbox, from the windows and from the jobs
        DjangoSigning.Secret = config.SecretKey; Mail.Env = name => config.Env(name);
        ClientImport.Folder = Path.Combine(config.DataFolder, "import");  // tdwin IMPORT_DIR: the installer makes it, with the web service's permission
    }

    public Web.Rete Rete { get; private set; }
    /// <summary>The address of the links in the emails: the public one from Rete e certificato, otherwise the office one.</summary>
    public string PublicOrigin => Rete.PublicOrigin;
    (DateTime At, string Value) edition;
    /// <summary>licensing.apply: "cloud" with a Portale licence, or with more businesses than one (an expired Portale licence keeps them visible), otherwise "private".</summary>
    public string Edition
    {
        get
        {
            if (edition.Value != null && DateTime.UtcNow - edition.At < TimeSpan.FromSeconds(60)) return edition.Value;
            using var g = Db.Global(); var portal = Licensing.Apply().Attivita > 1 || g.Scalar<long>("SELECT count(*) FROM core_tenant") > 1;
            edition = (DateTime.UtcNow, portal ? "cloud" : "private"); return edition.Value;
        }
    }
    public void EditionChanged() => edition = default;
    /// <summary>platform.tenant_limit.</summary>
    public int TenantLimit => Edition == "private" ? 1 : Licensing.Apply().Attivita;
    public bool Standalone => Config.Standalone;
    public bool Online => Config.Online;

    X509Certificate2 cert; DateTime certAt; (DateTime, DateTime) certStamp;
    /// <summary>The uploaded certificate while valid, otherwise the internal one (rete.paths), reloaded when the files change.</summary>
    public X509Certificate2 Certificate()
    {
        if (cert != null && DateTime.UtcNow - certAt < TimeSpan.FromSeconds(30)) return cert;
        certAt = DateTime.UtcNow;
        var (crt, key) = Certs.Paths(Config.DataFolder, Db);
        var stamp = (File.GetLastWriteTimeUtc(crt), File.GetLastWriteTimeUtc(key));
        if (cert == null || stamp != certStamp)
        {
            var c = X509Certificate2.CreateFromPemFile(crt, key);
            // Windows' TLS needs the key in a store-backed form
            cert = OperatingSystem.IsWindows() ? new X509Certificate2(c.Export(X509ContentType.Pkcs12)) : c;
            certStamp = stamp;
        }
        return cert;
    }

    bool configured;
    /// <summary>tdwin middleware.configured: the program has its administrator.</summary>
    public bool Configured()
    {
        if (!configured) { using var g = Db.Global(); configured = g.Scalar<long>("SELECT count(*) FROM core_user WHERE is_staff") > 0; }
        return configured;
    }
    public string FirstRunFile => Path.Combine(Config.DataFolder, "licenza", "primo-avvio.txt");
    /// <summary>A one-time code for the first start, in the data folder: only Windows administrators read that folder, so a
    /// normal user signed in on the server PC cannot create the program's administrator. Made once, by the web service,
    /// in the licence folder, the one part of the data folder it may write.</summary>
    public string FirstRunCode()
    {
        try { return File.ReadAllText(FirstRunFile).Trim(); }
        catch (IOException)
        {
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";  // no look-alike O/0, I/1
            var code = string.Join("-", Enumerable.Range(0, 2).Select(_ => new string(Enumerable.Range(0, 4).Select(_ => letters[System.Security.Cryptography.RandomNumberGenerator.GetInt32(letters.Length)]).ToArray())));
            Directory.CreateDirectory(Path.GetDirectoryName(FirstRunFile)!); File.WriteAllText(FirstRunFile, code + "\n"); return code;
        }
    }

    public object Info(HttpContext h) => new
    {
        configurato = Configured(),
        // the first start: where the code is, on the server PC only (the file is made now)
        primoAvvio = !Configured() && Web.Gate.IsServerPc(h) && FirstRunCode() != "" ? FirstRunFile : "",
        versione = Version,
        standalone = Standalone,
        online = Online,
        serverPc = Web.Gate.IsServerPc(h),
        licenza = Licensing.Apply().Kind,
    };
}
