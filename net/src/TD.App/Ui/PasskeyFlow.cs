using System.Text.Json.Nodes;
using TD.App.Net;

namespace TD.App.Ui;

/// <summary>A passkey from the program's window: the page that makes or uses it opens in the browser (WebAuthn lives
/// there), and the window waits for the server to say how it went (Web/AuthPasskeys.cs).</summary>
public static class PasskeyFlow
{
    public const string WAIT = "Si è aperta una pagina nel browser: usa lì la passkey, qui si prosegue da soli.";

    /// <summary>Entra, second factor or Conferma che sei tu: asks for the ticket, then waits.</summary>
    public static async Task<JsonNode> Run(JsonObject ask)
    {
        var r = await Api.Current.Post("/api/passkey/avvia", ask);
        return await Wait(r.S("apri"));
    }
    /// <summary>Opens the page and asks every second and a half, for the five minutes the ticket lasts.</summary>
    public static async Task<JsonNode> Wait(string url)
    {
        var ticket = url.TrimEnd('/').Split('/')[^1];
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { throw new ApiError("Non riesco ad aprire il browser: apri tu " + url, 400); }
        for (var i = 0; i < 210; i++)
        {
            await Task.Delay(1500);
            var e = await Api.Current.Post("/api/passkey/esito", new JsonObject { ["ticket"] = ticket });
            if (e.S("stato") == "attesa") continue;
            if (e.S("stato") == "fatto") return e;
            throw new ApiError(e.S("error") != "" ? e.S("error") : "Passkey non valida o richiesta scaduta.", 400);
        }
        throw new ApiError("Richiesta scaduta: riprova.", 400);
    }
}
