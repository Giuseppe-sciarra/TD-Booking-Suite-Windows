using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TD.App.Net;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>Prenotazioni: the list of a period, with status and search, each row opening its booking.</summary>
public sealed class BookingsPage : Page
{
    readonly DatePicker from = new(), to = new();
    readonly ComboBox status = new() { MinWidth = 160 };
    readonly TextBox q = Box("");
    readonly StackPanel rows = new();
    AgendaPage helper;

    public override async Task Load()
    {
        helper = new AgendaPage { Main = Main };
        await helper.Load();  // the business's time zone and the detail window
        if (Content == null)
        {
            var today = DateTime.Parse(Business.S("oggi"));
            from.SelectedDate = today; to.SelectedDate = today.AddDays(30);
            status.ItemsSource = new[] { ("", "Tutti gli stati") }.Concat(STATUS.Select(s => (s.Key, s.Value))).Select(s => new ComboBoxItem { Content = s.Item2, Tag = s.Item1 }).ToList();
            status.SelectedIndex = 0;
            q.Width = 240;
            var bar = Row(T("Dal ", size: 13).M(0, 8, 6, 0), from, T(" al ", size: 13).M(8, 8, 6, 0), to, status.M(12, 0, 0, 0), q.M(12, 0, 0, 0), BA("Cerca", Refresh, "Primary").M(8, 0, 0, 0));
            var root = new DockPanel { Margin = new Thickness(24, 18, 24, 12) };
            var top = Col(T("Prenotazioni", "H1"), bar.M(0, 8, 0, 12)); DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            root.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Background = Res("Surface"), Child = new ScrollViewer { Content = rows } });
            Content = root;
        }
        await Refresh();
    }

    async Task Refresh()
    {
        var r = await Api.Current.Call("prenotazioni.elenco", new { dal = from.SelectedDate?.ToString("yyyy-MM-dd"), al = to.SelectedDate?.ToString("yyyy-MM-dd"), stato = (string)((ComboBoxItem)status.SelectedItem).Tag, q = q.Text });
        rows.Children.Clear();
        var list = r.A("righe").ToList();
        if (list.Count == 0) rows.Children.Add(T("Nessuna prenotazione con questi filtri.", "Muted").M(16));
        foreach (var x in list)
        {
            var a = helper.Local(x.S("start"));
            var row = new Border
            {
                BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 9, 14, 9), Cursor = Cursors.Hand,
                Child = Row(T(a.ToString("ddd dd/MM HH:mm", AgendaPage.It), size: 13, bold: true).M(0, 0, 14, 0), T(x.S("name"), size: 13, bold: true).M(0, 0, 12, 0),
                    T(x.S("service") + (x.S("who") != "" ? " · " + x.S("who") : ""), "Muted").M(0, 0, 12, 0), T(STATUS.GetValueOrDefault(x.S("status"), ""), "Muted")),
            };
            var id = x.S("id");
            row.MouseLeftButtonUp += async (_, _) => { await Guard(() => BookingDetailWindow.Open(helper, id)); await Guard(Refresh); };
            rows.Children.Add(row);
        }
        if (r.Bo("troppe")) rows.Children.Add(T("Ne mostro 1000: restringi il periodo.", "Muted").M(16));
    }
}
