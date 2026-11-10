using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TD.App.Net;

public class ApiError(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
/// <summary>Forza: what the booking goes beyond, to confirm (the call again with force_ok).</summary>
public sealed class ApiWarning(string message) : ApiError(message, 409);
public sealed class NeedsLogin(string message) : ApiError(message, 401);
public sealed class NeedsPairing(string message) : ApiError(message, 403);

/// <summary>The program's server: sign in, the workstation's pairing and every call of the windows (POST /api/rpc/name).</summary>
public sealed class Api
{
    public static Api Current { get; set; }
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public ClientConfig Config { get; }
    readonly HttpClient http;
    public string Token { get; set; } = "";
    public string Business { get; set; } = "";
    public string Workstation { get; set; } = ClientConfig.ReadSecret(false);
    /// <summary>The certificate seen at the last connection, for the first trust of a new server.</summary>
    public X509Certificate2 SeenCertificate { get; private set; }
    public X509Certificate2 SeenAuthority { get; private set; }

    public Api(ClientConfig config)
    {
        Config = config;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                SeenCertificate = cert == null ? null : new X509Certificate2(cert);
                SeenAuthority = chain?.ChainElements.Count > 1 ? new X509Certificate2(chain.ChainElements[^1].Certificate) : null;
                return config.Trusts(cert, chain, errors);
            },
        };
        http = new HttpClient(handler) { BaseAddress = new Uri(config.Server), Timeout = TimeSpan.FromSeconds(60) };
    }

    HttpRequestMessage Request(string path, JsonNode body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json") };
        if (Token != "") r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (Workstation != "") r.Headers.Add("X-TD-Postazione", Workstation);
        if (Business != "") r.Headers.Add("X-TD-Attivita", Business);
        return r;
    }

    /// <summary>The demo's recorded answers instead of the network (Views/Demo.cs).</summary>
    public static Func<string, JsonNode, JsonNode> Fake;

    public async Task<JsonNode> Post(string path, JsonNode body = null)
    {
        if (Fake != null) return Fake(path, body);
        HttpResponseMessage res;
        try { res = await http.SendAsync(Request(path, body)); }
        catch (HttpRequestException ex) { throw new ApiError("Il server non risponde: " + (ex.InnerException?.Message ?? ex.Message), 0); }
        catch (TaskCanceledException) { throw new ApiError("Il server non risponde: tempo scaduto.", 0); }
        var text = await res.Content.ReadAsStringAsync();
        JsonNode node = null;
        try { node = text.Length > 0 && (text[0] == '{' || text[0] == '[') ? JsonNode.Parse(text) : null; } catch (JsonException) { }
        if (res.IsSuccessStatusCode) return node;
        var msg = node?["warning"]?.ToString() ?? node?["error"]?.ToString() ?? (text.Length > 0 && text.Length < 400 ? text : $"Errore {(int)res.StatusCode}");
        throw (int)res.StatusCode switch
        {
            409 => new ApiWarning(msg),
            401 => new NeedsLogin(msg),
            403 when msg.Contains("postazione", StringComparison.OrdinalIgnoreCase) && msg.Contains("registrata") => new NeedsPairing(msg),
            _ => new ApiError(msg, (int)res.StatusCode),
        };
    }

    public async Task<JsonNode> Get(string path)
    {
        if (Fake != null) return Fake(path.Trim('/').Replace('/', '.'), null);
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        HttpResponseMessage res0 = null;
        if (Workstation != "") r.Headers.Add("X-TD-Postazione", Workstation);
        HttpResponseMessage res;
        try { res = res0 ?? await http.SendAsync(r); }
        catch (HttpRequestException ex) { throw new ApiError("Il server non risponde: " + (ex.InnerException?.Message ?? ex.Message), 0); }
        catch (TaskCanceledException) { throw new ApiError("Il server non risponde: tempo scaduto.", 0); }
        var text = await res.Content.ReadAsStringAsync();
        if ((int)res.StatusCode == 403 && text.Contains("registrata")) throw new NeedsPairing(text);
        if (!res.IsSuccessStatusCode) throw new ApiError(text, (int)res.StatusCode);
        return JsonNode.Parse(text);
    }

    public Task<JsonNode> Call(string name, object args = null) =>
        Post("/api/rpc/" + name, args == null ? new JsonObject() : args as JsonNode ?? JsonSerializer.SerializeToNode(args, Json));
    public async Task<T> Call<T>(string name, object args = null) => (await Call(name, args)).Deserialize<T>(Json);

    /// <summary>A file of the server (a PDF, a photo) with the session's headers.</summary>
    public async Task<byte[]> Bytes(string name, object args = null)
    {
        var res = await http.SendAsync(Request("/api/rpc/" + name, args == null ? new JsonObject() : JsonSerializer.SerializeToNode(args, Json)));
        if (!res.IsSuccessStatusCode) throw new ApiError(await res.Content.ReadAsStringAsync(), (int)res.StatusCode);
        return await res.Content.ReadAsByteArrayAsync();
    }
}
