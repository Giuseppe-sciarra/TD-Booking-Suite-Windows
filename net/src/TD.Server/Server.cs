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

    public object Info(HttpContext h) => new
    {
        versione = Version,
        standalone = Standalone,
        online = Online,
        serverPc = Web.Gate.IsServerPc(h),
        licenza = Licensing.Apply().Kind,
    };
}
