using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TD.App.Net;
using static TD.App.Ui.U;

namespace TD.App.Ui;

/// <summary>templates/_photo_grid.html: the photos of one day of a client, squares of 130 px (84 the small ones); a click
/// opens the photo, × takes it away, «Aggiungi foto» while there is room (at most four a day). The photos are made a
/// JPEG of at most 1600 px and a small copy here, before sending (photos.compress).</summary>
public static class PhotoGrid
{
    const int FULL = 1600, THUMB = 360; const long MAX_UPLOAD = 20 * 1024 * 1024;

    /// <summary>add: where the new photos go ({cliente, prenotazione | voce}), null when photos cannot be added here.</summary>
    public static StackPanel Make(IEnumerable<string> ids, bool small, bool write, JsonObject add, int left, int max, Func<Task> changed)
    {
        var side = small ? 84.0 : 130.0; var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, small ? 6 : 10), MaxWidth = small ? 420 : 780, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var id in ids)
        {
            var img = new Image { Stretch = Stretch.UniformToFill, Width = side, Height = side };
            _ = Load(img, id, true);
            var cell = new Grid { Width = side, Height = side, Cursor = Cursors.Hand };
            cell.Children.Add(new Border { CornerRadius = new CornerRadius(small ? 10 : 12), BorderBrush = Res("Pastel"), BorderThickness = new Thickness(2), Background = Res("BrandSoft"), Child = img, ClipToBounds = true });
            cell.MouseLeftButtonUp += (_, _) => Open(id);
            if (write)
            {
                var x = new Button { Content = "×", Width = small ? 22 : 28, Height = small ? 22 : 28, MinHeight = 0, Padding = new Thickness(0), FontSize = small ? 15 : 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(184, 36, 26, 48)), BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, small ? 4 : 6, small ? 4 : 6, 0), ToolTip = "Togli", Style = null };
                x.Template = Round(); x.Click += async (_, e) => { e.Handled = true; if (!Ask("Togliere questa foto? Non si recupera.")) return; await Guard(async () => { await Api.Current.Call("foto.togli", new { id }); await changed(); }); };
                cell.Children.Add(x);
            }
            wrap.Children.Add(new Border { Child = cell, Margin = new Thickness(0, 0, small ? 8 : 10, small ? 8 : 10) });
        }
        var col = Col(); if (wrap.Children.Count > 0) col.Children.Add(wrap);
        if (write && add != null)
        {
            if (left > 0)
            {
                var status = T("", size: 13, color: Res("Muted")); status.VerticalAlignment = VerticalAlignment.Center; status.Margin = new Thickness(12, 0, 0, 0);
                var pick = F.Small(BA("Aggiungi foto", async () =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Foto|*.jpg;*.jpeg;*.png;*.webp|Tutti i file|*.*", Title = "Aggiungi foto" };
                    if (dlg.ShowDialog() != true) return;
                    status.Text = "Carico…"; var files = new JsonArray(); var error = "";
                    foreach (var path in dlg.FileNames.Take(left))
                    {
                        try { var (full, thumb) = await Task.Run(() => Compress(path)); files.Add(new JsonObject { ["grande"] = Convert.ToBase64String(full), ["piccola"] = Convert.ToBase64String(thumb) }); }
                        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException) { error = ex is InvalidOperationException && ex.Message.StartsWith("Foto") ? ex.Message : "Usa una foto JPEG, PNG o WebP valida."; }
                    }
                    if (dlg.FileNames.Length > left) error = $"Tenute le prime {left}: al massimo {max} foto per giorno.";
                    if (files.Count > 0)
                    {
                        var args = (JsonObject)add.DeepClone(); args["foto"] = files;
                        var r = await Api.Current.Call("foto.aggiungi", args); if (r.S("errore") != "") error = r.S("errore");
                    }
                    if (error != "") Say(error);
                    await changed();
                }));
                col.Children.Add(Row(pick, T(left == 1 ? "Ancora 1 foto" : $"Ancora {left} foto", size: 13, color: Res("Muted")).M(12, 0, 0, 0), status));
                ((FrameworkElement)((StackPanel)col.Children[^1]).Children[1]).VerticalAlignment = VerticalAlignment.Center;
            }
            else col.Children.Add(T($"Ci sono già {max} foto per questo giorno: togline una per aggiungerne un'altra.", size: 13, color: Res("Muted")));
        }
        return col;
    }

    static ControlTemplate Round()
    {
        var t = new ControlTemplate(typeof(Button)); var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(99)); b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var c = new FrameworkElementFactory(typeof(ContentPresenter)); c.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); c.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        b.AppendChild(c); t.VisualTree = b; return t;
    }

    static async Task Load(Image img, string id, bool small)
    {
        try { img.Source = Decode(await Api.Current.Bytes("foto.immagine", new { id, piccola = small })); }
        catch (Exception ex) when (ex is ApiError or NotSupportedException or IOException) { }
    }
    static BitmapImage Decode(byte[] data)
    {
        var bi = new BitmapImage(); bi.BeginInit(); bi.StreamSource = new MemoryStream(data); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze(); return bi;
    }

    /// <summary>The photo, large, with Scarica.</summary>
    static async void Open(string id)
    {
        await Guard(async () =>
        {
            var data = await Api.Current.Bytes("foto.immagine", new { id });
            var w = new Window { Title = "Foto", Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 900, Height = 760, Background = new SolidColorBrush(Color.FromRgb(36, 26, 48)), ShowInTaskbar = false };
            var save = F.Small(B("Scarica", () =>
            {
                Downloads.Save($"foto-{id[..6]}.jpg", data);
            }));
            var close = F.Small(B("Chiudi", () => w.Close(), "Ghost"));
            var bar = Row(save, close.M(8, 0, 0, 0)); bar.HorizontalAlignment = HorizontalAlignment.Right; bar.Margin = new Thickness(12);
            var dock = new DockPanel(); DockPanel.SetDock(bar, Dock.Bottom); dock.Children.Add(bar); dock.Children.Add(new Image { Source = Decode(data), Stretch = Stretch.Uniform, Margin = new Thickness(12, 12, 12, 0) });
            w.Content = dock; w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.ShowDialog();
        });
    }

    /// <summary>photos.compress: upright as it was taken, on white where it was transparent, at most 1600 px, JPEG 80; and
    /// its small copy, 360 px, JPEG 72.</summary>
    public static (byte[] Full, byte[] Thumb) Compress(string path)
    {
        if (new FileInfo(path).Length > MAX_UPLOAD) throw new InvalidOperationException("Foto troppo grande: massimo 20 MB.");
        var frame = BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 60_000_000) throw new NotSupportedException();
        var turn = 0;
        try { if ((frame.Metadata as BitmapMetadata)?.GetQuery("/app1/ifd/{ushort=274}") is ushort o) turn = o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 }; } catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { }
        BitmapSource src = turn == 0 ? frame : new TransformedBitmap(frame, new RotateTransform(turn));
        byte[] Copy(int side, int quality)
        {
            var scale = Math.Min(1.0, (double)side / Math.Max(src.PixelWidth, src.PixelHeight));
            int w = Math.Max(1, (int)Math.Round(src.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));
            var dv = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h)); dc.DrawImage(src, new Rect(0, 0, w, h)); }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv);
            var enc = new JpegBitmapEncoder { QualityLevel = quality }; enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
        }
        return (Copy(FULL, 80), Copy(THUMB, 72));
    }
}
