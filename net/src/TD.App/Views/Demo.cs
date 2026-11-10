using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TD.App.Net;
using TD.App.Ui;

namespace TD.App.Views;

/// <summary>--demo folder [--shot out]: the windows on answers recorded from a real server (folder/name.json), for the
/// screenshots of the build on GitHub; nothing is written anywhere.</summary>
public static class Demo
{
    public static void Run(string folder, string[] args)
    {
        Api.Current = new Api(new ClientConfig { Server = "https://demo.invalid" });
        Api.Fake = (name, body) =>
        {
            var file = Path.Combine(folder, name.Replace("/api/rpc/", "") + ".json");
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : new JsonObject();
        };
        var me = Api.Fake("sessione.io", null);
        var main = new MainWindow(me) { WindowState = WindowState.Normal, Width = 1440, Height = 900 };
        Application.Current.MainWindow = main;
        main.Show();
        var shot = Array.IndexOf(args, "--shot");
        if (shot < 0) return;
        var outDir = args[shot + 1]; Directory.CreateDirectory(outDir);
        AgendaPage Ag() => main.CurrentPage as AgendaPage;
        Window extra = null;
        var steps = new (string Name, Action Do)[]
        {
            ("01-agenda-giorno", () => { }),
            ("02-agenda-settimana", () => _ = Ag().SetView("settimana")),
            ("03-agenda-mese", () => _ = Ag().SetView("mese")),
            ("04-nuova-prenotazione", () => { _ = Ag().SetView("giorno"); extra = new BookingWindow(Ag(), null, DateTime.Today.AddHours(10), false); extra.Show(); }),
            ("05-prenotazione", () => { extra?.Close(); var id = Api.Fake("prenotazione.dettaglio", null).S("id"); var w = new BookingDetailWindow(Ag(), id); extra = w; _ = w.Reload(); w.Show(); }),
            ("06-prenotazioni", () => { extra?.Close(); extra = null; main.Show("prenotazioni"); }),
        };
        var i = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            Save(extra is { IsVisible: true } ? extra : main, Path.Combine(outDir, steps[i].Name + ".png"));
            i++;
            if (i >= steps.Length) { timer.Stop(); Application.Current.Shutdown(); return; }
            steps[i].Do();
        };
        timer.Start();
    }

    public static void Save(Window w, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path); enc.Save(f);
    }
}
