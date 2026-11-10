using System.Text.Json.Nodes;

namespace TD.Core;

/// <summary>
/// config.json in the data folder (C:\ProgramData\TD Booking Suite), the same file the Python program and its installer
/// write: origin, addresses, port, installation code, mode (standalone/server), online, PostgreSQL folder and the
/// secrets in "env" (keys, database). Read only by the service and the administrators.
/// </summary>
public sealed class AppConfig
{
    public string DataFolder { get; init; }
    public JsonObject Raw { get; init; }
    public string Origin => (string)Raw["origine"] ?? "";
    public int Port => (int?)Raw["porta"] ?? 8443;
    public string InstallId => (string)Raw["install_id"] ?? "";
    public string Mode => (string)Raw["modalita"] ?? "server";
    public bool Standalone => Mode == "standalone";
    public bool Online => !Standalone || ((bool?)Raw["online"] ?? false);
    public string Listen => (string)Raw["ascolto"] ?? (Standalone && !Online ? "127.0.0.1" : "0.0.0.0");
    public string PgBin => (string)Raw["pg_bin"] ?? "";
    public IEnumerable<string> Addresses => (Raw["indirizzi"] as JsonArray)?.Select(x => (string)x) ?? Array.Empty<string>();
    public string Env(string name, string fallback = "") => (string)Raw["env"]?[name] ?? Environment.GetEnvironmentVariable(name) ?? fallback;
    public string DatabaseUrl => Env("DATABASE_URL");
    public string KeysJson => Env("TDBS_KEYS");
    public string ActiveKey => Env("TDBS_ACTIVE_KEY", "v1");
    public string SecretKey => Env("DJANGO_SECRET_KEY");

    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("TDBS_DATA") is { Length: > 0 } d ? d :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TD Booking Suite");

    public static AppConfig Load(string folder = null)
    {
        folder ??= DefaultFolder;
        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "config.json")))!.AsObject();
        return new AppConfig { DataFolder = folder, Raw = raw };
    }

    public void Save()
    {
        var path = Path.Combine(DataFolder, "config.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Raw.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tmp, path, true);
    }

    /// <summary>postgresql://user:pass@host:port/db as Npgsql wants it.</summary>
    public static string ToNpgsql(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.Contains("://")) return url;
        var u = new Uri(url);
        var userInfo = Uri.UnescapeDataString(u.UserInfo);
        var colon = userInfo.IndexOf(':');
        var user = colon < 0 ? userInfo : userInfo[..colon];
        var pass = colon < 0 ? "" : userInfo[(colon + 1)..];
        var db = u.AbsolutePath.Trim('/');
        var host = u.Host;
        var query = System.Web.HttpUtility.ParseQueryString(u.Query);
        if (!string.IsNullOrEmpty(query["host"])) host = query["host"];
        var port = u.Port > 0 ? u.Port : int.TryParse(query["port"], out var p) ? p : 5432;
        return $"Host={host};Port={port};Database={db};Username={user};Password={pass};Include Error Detail=true";
    }
}
