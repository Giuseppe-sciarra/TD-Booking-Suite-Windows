using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TD.Core.Win;

/// <summary>tdwin/setup_files.py: the first configuration files of a server installation, run by setup-server.ps1
/// (TDBookingServer.exe prepara). Existing files are never replaced: keys and passwords made at the first
/// installation stay for its whole life. An update only changes address, port and paths when they are given again.</summary>
public static class SetupFiles
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string UrlSafe(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_');

    /// <summary>The address the program gets when nobody gives one: this PC by name for a server, localhost for Un PC solo.</summary>
    static string DefaultOrigin(JsonObject config)
    {
        var host = (string)config["modalita"] == "standalone" ? "localhost" : Environment.MachineName.ToLowerInvariant(); var port = (int)config["porta"];
        return $"https://{host}" + (port == 443 ? "" : $":{port}");
    }
    /// <summary>All or nothing: config.json holds the only copy of the keys. A new file must not be there already.</summary>
    static void Write(string path, JsonObject data, bool replace)
    {
        var text = data.ToJsonString(Pretty);
        if (!replace) { using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); using var w = new StreamWriter(f, new System.Text.UTF8Encoding(false)); w.Write(text); return; }
        var tmp = path + ".tmp"; File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false)); File.Move(tmp, path, true);
    }

    public static JsonObject Prepare(string data, string origin = null, int? port = null, string hosts = null, string pgBin = null, int pgPort = 55432, string mode = null, string online = null, string keep = null)
    {
        Directory.CreateDirectory(data); foreach (var folder in new[] { "licenza", "backup", "certs", "log", "tmp" }) Directory.CreateDirectory(Path.Combine(data, folder));
        string adminPath = Path.Combine(data, "db-admin.json"), configPath = Path.Combine(data, "config.json");
        if (!File.Exists(adminPath)) Write(adminPath, new JsonObject { ["url"] = $"postgresql://postgres:{UrlSafe(24)}@127.0.0.1:{pgPort}/tdbooking", ["app_password"] = UrlSafe(24) }, false);
        var admin = JsonNode.Parse(File.ReadAllText(adminPath))!.AsObject(); var existed = File.Exists(configPath); JsonObject config;
        if (existed) config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        else
        {
            config = new JsonObject
            {
                ["origine"] = "", ["indirizzi"] = new JsonArray(), ["porta"] = 8443, ["install_id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), ["pg_bin"] = "",
                ["env"] = new JsonObject
                {
                    ["DJANGO_SECRET_KEY"] = UrlSafe(64), ["TDBS_KEYS"] = new JsonObject { ["v1"] = Key(), ["index"] = Key() }.ToJsonString(), ["TDBS_ACTIVE_KEY"] = "v1",
                    ["DATABASE_URL"] = $"postgresql://tdbooking_app:{(string)admin["app_password"]}@127.0.0.1:{pgPort}/tdbooking", ["TDBS_DEV"] = "0", ["TRUST_PROXY"] = "0",
                },
            };
            // Ricomincia da zero: new keys and database, the technical settings of before, the installation code included
            // (the licence stays valid). What the installer says now (type, port) applies over them, as on a change.
            if (!string.IsNullOrEmpty(keep))
            {
                var before = JsonNode.Parse(File.ReadAllText(keep))!.AsObject();
                foreach (var k in new[] { "origine", "indirizzi", "porta", "install_id", "modalita", "online" }) if (before[k] != null) config[k] = before[k].DeepClone();
            }
        }
        config["porta"] ??= 8443;
        var automatic = (string)config["origine"] is null or "" || (string)config["origine"] == DefaultOrigin(config);
        if (port.HasValue) config["porta"] = port.Value;
        // Un PC solo listens on this PC only; a server on the network. Not given: as before
        if (!string.IsNullOrEmpty(mode)) { if (mode is not ("standalone" or "server")) throw new ArgumentException("Modalità non valida: standalone o server."); config["modalita"] = mode; }
        config["modalita"] ??= "server";
        // Un PC solo with the booking page and the website on: it answers from the network like a server
        if (!string.IsNullOrEmpty(online)) config["online"] = online is "on" or "1";
        config["online"] ??= false;
        config["ascolto"] = (string)config["modalita"] == "standalone" && !(bool)config["online"] ? "127.0.0.1" : "0.0.0.0";
        if (automatic && string.IsNullOrEmpty(origin)) config["origine"] = "";
        if (!string.IsNullOrEmpty(origin))
        {
            origin = origin.TrimEnd('/');
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host == "" || u.AbsolutePath != "/" || u.Query != "") throw new ArgumentException("L'indirizzo deve essere come https://nome-del-pc:8443");
            config["origine"] = origin;
        }
        if ((string)config["origine"] is null or "") config["origine"] = DefaultOrigin(config);
        if (hosts != null) config["indirizzi"] = new JsonArray(hosts.Split(',').Select(h => h.Trim()).Where(h => h != "").Select(h => (JsonNode)h).ToArray());
        if (!string.IsNullOrEmpty(pgBin)) config["pg_bin"] = pgBin;
        Write(configPath, config, existed);
        return config;
    }

    /// <summary>The arguments of setup_files.main: --data --origine --porta --indirizzi --pg-bin --pg-port --modalita --online --tieni.</summary>
    public static string Main(string[] args)
    {
        string Get(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var data = Get("--data") ?? throw new ArgumentException("Serve --data: la cartella dei dati.");
        var c = Prepare(data, Get("--origine"), int.TryParse(Get("--porta"), out var p) ? p : null, Get("--indirizzi"), Get("--pg-bin"), int.TryParse(Get("--pg-port"), out var pp) ? pp : 55432, Get("--modalita"), Get("--online"), Get("--tieni"));
        return new JsonObject { ["origine"] = c["origine"].DeepClone(), ["porta"] = c["porta"].DeepClone(), ["modalita"] = c["modalita"].DeepClone(), ["online"] = c["online"].DeepClone() }.ToJsonString();
    }
}
