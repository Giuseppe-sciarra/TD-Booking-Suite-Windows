using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/confirm.html, «Conferma che sei tu»: before the operations that change accesses, settings or
/// erase data, at most every 15 minutes. One click for who enters without password on this PC, the password for who has
/// no second factor, otherwise the code of the authenticator app or a recovery code.</summary>
public static class ConfirmDialog
{
    public static async Task<bool> Ask(string mode)
    {
        var w = new Window { Title = "Conferma che sei tu", Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height, Width = 460, ResizeMode = ResizeMode.NoResize, Background = Res("Bg"), ShowInTaskbar = false };
        var error = T("", size: 13.5, color: Res("Danger")); error.Visibility = Visibility.Collapsed; error.Margin = new Thickness(0, 8, 0, 0);
        var pw = new PasswordBox(); var code = Box(""); code.MaxLength = 64;
        var ok = new Button { Content = mode == "quick" ? "Sono io, continua" : "Conferma", Style = Style("Primary"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = F.Link("Annulla", () => w.Close()); cancel.Margin = new Thickness(0, 14, 0, 0);
        var done = false;
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            try { await Api.Current.Post("/api/conferma", new JsonObject { ["password"] = pw.Password, ["code"] = code.Text }); done = true; w.Close(); }
            catch (ApiError ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            finally { ok.IsEnabled = true; }
        };
        var p = mode switch
        {
            "quick" => "Questa pagina modifica accessi o impostazioni: conferma che sei tu, al massimo ogni 15 minuti. Da questo PC entri senza password, quindi basta un clic. Poi torni dove eri",
            "password" => "Questa pagina modifica accessi o impostazioni: inserisci di nuovo la password, al massimo ogni 15 minuti. Poi torni dove eri",
            _ => "Questa pagina modifica accessi o impostazioni: serve il secondo fattore, al massimo ogni 15 minuti. Poi torni dove eri",
        };
        var h1 = T("Conferma che sei tu", "H1"); h1.FontSize = 22;
        var body = Col(h1, T(p, size: 14, color: Res("Muted")));
        if (mode == "password") { body.Children.Add(F.Label("Password")); body.Children.Add(pw); }
        else if (mode != "quick") { body.Children.Add(F.Label("Codice dell'app di autenticazione, oppure un codice di recupero")); body.Children.Add(code); }
        body.Children.Add(error); body.Children.Add(ok);
        if (mode is not ("password" or "quick"))
        {
            var passkey = F.Link("Usa la passkey", async () =>
            {
                error.Foreground = Res("Muted"); error.Text = PasskeyFlow.WAIT; error.Visibility = Visibility.Visible;
                try { await PasskeyFlow.Run(new JsonObject { ["tipo"] = "confirm" }); done = true; w.Close(); }
                catch (ApiError ex) { error.Foreground = Res("Danger"); error.Text = ex.Message; }
            });
            passkey.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(passkey);
        }
        body.Children.Add(cancel);
        w.Content = new Border { Style = Style("Card"), Margin = new Thickness(18), Child = body };
        w.Loaded += (_, _) => { if (mode == "password") pw.Focus(); else if (mode == "quick") ok.Focus(); else code.Focus(); };
        w.ShowDialog();
        await Task.CompletedTask;
        return done;
    }
}
