using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;


/// <summary>Blocca orario: an operator not bookable for a while, once or every week.</summary>
public static class BlockDialog
{
    public static async void Open(AgendaPage page, JsonNode existing, DateOnly day, string resource)
    {
        await Guard(async () =>
        {
            var f = await Api.Current.Call("blocco.dati", new { id = existing?.S("id") });
            var bl = f["blocco"];
            var ops = new ComboBox { ItemsSource = f.A("operatori").Select(o => new ComboBoxItem { Content = o.S("nome"), Tag = o.S("id") }).ToList() };
            ops.SelectedItem = ops.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (bl?.S("operatore") ?? resource)) ?? ops.Items.Cast<ComboBoxItem>().FirstOrDefault();
            var date = new DatePicker { SelectedDate = bl != null ? DateTime.Parse(bl.S("giorno")) : day.ToDateTime(TimeOnly.MinValue) };
            var start = Box(bl?.S("inizio") ?? "13:00"); var end = Box(bl?.S("fine") ?? "14:00");
            var reason = new ComboBox { ItemsSource = f.A("motivi").Select(r => new ComboBoxItem { Content = r.S("nome"), Tag = r.S("id") }).ToList() };
            reason.SelectedItem = reason.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (bl?.S("motivo") ?? "break")) ?? reason.Items[0];
            var note = Box(bl?.S("nota") ?? ""); var weekly = new CheckBox { Content = "Ogni settimana, lo stesso giorno alla stessa ora", IsChecked = bl?.Bo("settimanale") == true };
            var until = new DatePicker { SelectedDate = bl != null && bl.S("fino") != "" ? DateTime.Parse(bl.S("fino")) : null };
            var w = new Window { Title = "Blocca orario", Width = 460, SizeToContent = SizeToContent.Height, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var times = new Grid(); times.ColumnDefinitions.Add(new ColumnDefinition()); times.ColumnDefinitions.Add(new ColumnDefinition());
            var e2 = Field("Fine", end).M(8, 0, 0, 12); Grid.SetColumn(e2, 1); times.Children.Add(Field("Inizio", start)); times.Children.Add(e2);
            var save = BA("Blocca", async () =>
            {
                var args = new JsonObject
                {
                    ["id"] = bl?.S("id"), ["operatore"] = (string)((ComboBoxItem)ops.SelectedItem).Tag, ["giorno"] = date.SelectedDate?.ToString("yyyy-MM-dd"), ["inizio"] = start.Text, ["fine"] = end.Text,
                    ["motivo"] = (string)((ComboBoxItem)reason.SelectedItem).Tag, ["nota"] = note.Text, ["settimanale"] = weekly.IsChecked == true, ["fino"] = until.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
                };
                if (await WithForce("blocco.salva", args) != null) w.DialogResult = true;
            }, "Primary");
            var remove = bl == null ? null : BA("Togli il blocco", async () => { if (Ask("Togliere il blocco? Quell'orario si prenota di nuovo.")) { await Api.Current.Call("blocco.togli", new { id = bl.S("id") }); w.DialogResult = true; } }, "DangerButton").M(8, 0, 0, 0);
            w.Content = Col(T("Blocca orario", "H2"), T("Online e al banco quell'orario non si prenota: non conta come una prenotazione.", "MutedText").M(0, 0, 0, 12),
                Field(f.S("etichetta"), ops), Field("Giorno", date), times, Field("Motivo", reason), Field("Nota (facoltativa)", note), weekly, Field("Fino al (vuoto: senza fine)", until).M(0, 6, 0, 12),
                Row(save, remove, B("Annulla", () => w.DialogResult = false, "Ghost").M(8, 0, 0, 0))).M(24);
            if (w.ShowDialog() == true) await page.Refresh();
        });
    }
}
