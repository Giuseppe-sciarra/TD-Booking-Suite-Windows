using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace TD.App.Ui;

/// <summary>Tema: Automatico (as Windows), Chiaro, Scuro — the same colours as the web program's :root and [data-theme=dark].</summary>
public static class Palette
{
    public static string Mode { get; private set; } = "auto";
    public static string BrandLight = "#7a55ad", BrandDark = "#c5b4d9";
    public static event Action Changed;
    static readonly Dictionary<string, (string Light, string Dark)> Colors = new()
    {
        ["Bg"] = ("#F6F3FA", "#16111D"), ["Surface"] = ("#FFFFFF", "#211A2B"), ["Ink"] = ("#2B2236", "#EFEAF6"), ["Muted"] = ("#574C66", "#B0A4C2"),
        ["Line"] = ("#C6B7D8", "#4B3F5C"), ["LineStrong"] = ("#C6B7D8", "#4B3F5C"), ["BrandSoft"] = ("#F0E9F8", "#2C2238"), ["BrandInk"] = ("#FFFFFF", "#241A30"),
        ["Ok"] = ("#2B7353", "#7FD1AA"), ["OkSoft"] = ("#E4F3EC", "#1E3329"), ["Warn"] = ("#96590A", "#F0B866"), ["WarnSoft"] = ("#FBF0E1", "#3A2C17"),
        ["Danger"] = ("#B3264A", "#FF9DB4"), ["DangerSoft"] = ("#FBE7EC", "#3D1D27"), ["Side"] = ("#2B2236", "#110D17"), ["SideLine"] = ("#3D3249", "#2A2133"),
        ["Hatch"] = ("#1A574C66", "#1AB0A4C2"), ["Pastel"] = ("#B9A4D1", "#B9A4D1"), ["Pink"] = ("#E0569B", "#E0569B"),
    };

    public static bool Dark => Mode == "dark" || Mode == "auto" && SystemDark();
    static bool SystemDark()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return k?.GetValue("AppsUseLightTheme") is int v && v == 0; }
        catch { return false; }
    }

    static string Store => System.IO.Path.Combine(Net.ClientConfig.Folder, "tema.txt");
    public static void Load()
    {
        try { var m = System.IO.File.ReadAllText(Store).Trim(); if (m is "auto" or "light" or "dark") Mode = m; } catch { }
        Apply();
    }
    public static void Set(string mode)
    {
        Mode = mode;
        try { System.IO.Directory.CreateDirectory(Net.ClientConfig.Folder); System.IO.File.WriteAllText(Store, mode); } catch { }
        Apply(); Changed?.Invoke();
    }
    public static void Brand(string light, string dark) { if (!string.IsNullOrEmpty(light)) BrandLight = light; if (!string.IsNullOrEmpty(dark)) BrandDark = dark; Apply(); }

    public static void Apply()
    {
        var r = Application.Current.Resources; var dark = Dark;
        foreach (var (k, v) in Colors) r[k] = Brush(dark ? v.Dark : v.Light);
        var brand = (Color)ColorConverter.ConvertFromString(dark ? BrandDark : BrandLight);
        r["BrandColor"] = brand; r["Brand"] = new SolidColorBrush(brand);
        // color-mix(in srgb, brand 75%, ink): the edge of the filled buttons
        var ink = ((SolidColorBrush)r["Ink"]).Color;
        r["BrandEdge"] = new SolidColorBrush(Color.FromRgb((byte)(brand.R * .75 + ink.R * .25), (byte)(brand.G * .75 + ink.G * .25), (byte)(brand.B * .75 + ink.B * .25)));
    }
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
