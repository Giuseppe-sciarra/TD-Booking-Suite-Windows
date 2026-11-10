using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/security.html: Accesso e sicurezza: the password, Authenticator, the passkeys, the recovery codes.</summary>
public sealed class AccountPage : Page
{
    public override async Task Load() => Draw(await Api.Current.Call("account", new { }));
    async Task Act(JsonObject args) => Draw(await Api.Current.Call("account.azione", args));
    static TextBlock Muted(string text, double size = 14) { var t = T(text, size: size, color: Res("Muted")); t.TextWrapping = TextWrapping.Wrap; return t; }
    static Border Pill(string text) => new() { Background = Res("BrandSoft"), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = T(text, size: 11.5, bold: true, color: Res("Brand")) };
    static Button Left(Button b) { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 14, 0, 0); return b; }

    void Draw(JsonNode d)
    {
        bool mfa = d.Bo("mfa"), totp = d.Bo("totp"); var keys = d.A("chiavi").ToList();
        var intro = !mfa ? $"Entri con {(d.S("username") != "" ? "lo username " + d.S("username") : "l'email " + d.S("email"))} e la password. Qui la cambi"
            : totp || keys.Count > 0 ? "Per entrare servono la password e il secondo fattore che hai attivato"
            : "Ora entri con la sola password. Authenticator o una passkey aggiungono un secondo controllo: chi scopre la password da solo non entra";
        var page = Col(F.Heading(null, "Accesso e sicurezza", intro, B("Torna al portale", () => Main.Show("dashboard"))));
        if (d.S("avviso") != "") page.Children.Add(F.Notice(d.S("avviso")));
        if (d["codici"] is JsonArray codes)
            page.Children.Add(F.Card(T("Conserva questi codici di recupero", "H2"), T("Mostrati una sola volta, utilizzabili una volta ciascuno.", size: 14).M(0, 4, 0, 10),
                new TextBox { Text = string.Join("\n", codes.Select(x => x.ToString())), IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 14, BorderThickness = new Thickness(0), Background = Res("Bg"), Padding = new Thickness(14) }));
        if (mfa)
        {
            // App Authenticator
            var auth = Col(Pill("APP AUTHENTICATOR"), T("Codice temporaneo", "H2").M(0, 8, 0, 0), T("Compatibile con le app TOTP.", size: 14));
            if (totp) auth.Children.Add(F.Notice("Authenticator è attivo."));
            auth.Children.Add(Left(BA(totp ? "Sostituisci Authenticator" : "Configura Authenticator", () => Act(new JsonObject { ["azione"] = "totp_start" }))));
            if (d.S("seme") != "")
            {
                try { auth.Children.Add(new Image { Source = SetupPage.PictureOf(Convert.FromBase64String(d.S("qr"))), Width = 200, Height = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 8) }); } catch (Exception ex) when (ex is FormatException or NotSupportedException) { }
                auth.Children.Add(F.Details("Inserisci la chiave manualmente", new TextBox { Text = d.S("seme"), IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 14, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0, 6, 0, 0) }, plain: true));
                var code = Box(); code.MaxLength = 6; code.Width = 140; code.HorizontalAlignment = HorizontalAlignment.Left;
                auth.Children.Add(F.Field("Codice a 6 cifre", code)); auth.Children.Add(Left(BA("Verifica e attiva", () => Act(new JsonObject { ["azione"] = "totp_confirm", ["code"] = code.Text }), "Primary")));
            }
            // Passkey
            var name = Box("Il mio dispositivo"); name.MaxLength = 80;
            var pass = Col(Pill("PASSKEY"), T("Passkey", "H2").M(0, 8, 0, 0), T("Usa il dispositivo, l'impronta, il volto o una chiave di sicurezza.", size: 14), F.Field("Nome del dispositivo", name),
                Left(BA("Aggiungi una passkey", async () =>
                {
                    var r = await Api.Current.Call("account.azione", new JsonObject { ["azione"] = "passkey_start", ["nome"] = name.Text });
                    Main.Flash(PasskeyFlow.WAIT); await PasskeyFlow.Wait(r.S("apri")); Main.Flash("Passkey aggiunta."); await Load();
                }, "Primary")));
            foreach (var k in keys)
            {
                var drop = F.Small(BA("Rimuovi", () => Act(new JsonObject { ["azione"] = "delete_key", ["key"] = k.S("id") }), "Ghost")); DockPanel.SetDock(drop, Dock.Right);
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; row.Children.Add(drop); row.Children.Add(Col(T(k.S("nome"), size: 14, bold: true), Muted(k.S("creata"), 13))); pass.Children.Add(row);
            }
            if (keys.Count == 0) pass.Children.Add(T("Nessuna passkey registrata.", size: 14).M(0, 12, 0, 0));
            page.Children.Add(F.Cols(2, 22, F.Card(auth), F.Card(pass)));
        }
        var current = new PasswordBox(); var next = new PasswordBox();
        var password = F.Card(T("Cambia password", "H2"), F.Field("Password attuale", current), F.Field("Nuova password", next),
            Left(BA("Aggiorna password", () => Act(new JsonObject { ["azione"] = "password", ["current_password"] = current.Password, ["new_password"] = next.Password }))));
        var recovery = mfa ? F.Card(T("Codici di recupero", "H2"), Left(BA("Genera nuovi codici", () => Act(new JsonObject { ["azione"] = "recovery" }))), Muted("Per le modifiche sensibili il portale ti chiede di confermare che sei tu, al massimo ogni 15 minuti").M(0, 12, 0, 0)) : null;
        page.Children.Add(F.Cols(2, 22, password, recovery));
        Content = F.Page(1180, page);
    }
}
