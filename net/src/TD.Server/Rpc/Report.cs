using System.Globalization;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>reports.view: Report: how the period went, each table with its Excel (the PDF is made by the window).</summary>
public static class Report
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static (DateOnly First, DateOnly Last) Period(Call c)
    {
        var today = c.C.Today; DateOnly first, last;
        if (!DateOnly.TryParseExact(c.Str("dal", today.AddDays(-29).ToString("yyyy-MM-dd")), "yyyy-MM-dd", out first) || !DateOnly.TryParseExact(c.Str("al", today.ToString("yyyy-MM-dd")), "yyyy-MM-dd", out last)
            || last.DayNumber - first.DayNumber is < 0 or > 366) throw new BookingException("Periodo non valido: scegli un periodo di al massimo un anno.");
        return (first, last);
    }
    static (Dictionary<string, Reports.Table> Data, System.Text.Json.Nodes.JsonObject Dash, bool Heavy) Data(Call c, DateOnly first, DateOnly last, string wanted)
    {
        var x = c.C; var rp = new Reports(x, Prenotazioni.V(c));
        var a = x.Cal.Local(first, TimeSpan.Zero).Value; var b = x.Cal.Local(last.AddDays(1), TimeSpan.Zero).Value;
        var (mine, dash) = rp.RecordTables(first, last);
        // the occupancy reads the hours of every day of every operator: beyond three months it is worked out on request (or for its Excel)
        var heavy = last.DayNumber - first.DayNumber > 92 && !c.Bool("occupazione") && wanted != "occupazione";
        foreach (var (k, t) in rp.Tables(a, b, first, last, !heavy)) mine[k] = t;
        return (mine, dash, heavy);
    }
    /// <summary>As the page writes it: a decimal with the comma, the rest as it is.</summary>
    static string Shown(object x) => x is decimal d ? d.ToString(It) : Convert.ToString(x, CultureInfo.InvariantCulture) ?? "";

    [Rpc("report", Perm = "booking.read")]
    public static object Page(Call c)
    {
        var (first, last) = Period(c); var (data, dash, heavy) = Data(c, first, last, "");
        var years = c.C.S.Query<int>("SELECT DISTINCT extract(year from date)::int FROM core_treatment").Append(c.C.Today.Year).Distinct().OrderBy(y => y);
        return new
        {
            dal = first, al = last, anni = years, cruscotto = dash, pesante = heavy,
            // in the order the tables are made, as the page of the web
            sezioni = data.Select(e => (Key: e.Key, Title: Reports.TITLES.First(x => x.Key == e.Key).Title)).Select(t =>
            {
                var (head, rows) = (data[t.Key].Head, data[t.Key].Rows); var cut = t.Key is "lavori_cliente" or "elenco";
                return new { chiave = t.Key, titolo = t.Title, testa = head, righe = (cut ? rows.Take(200) : rows).Select(r => r.Select(Shown)), altri = cut && rows.Count > 200 ? rows.Count - 200 : 0,
                    numeri = Enumerable.Range(0, head.Length).Where(i => rows.Count > 0 && rows.All(r => r[i] is int or long or decimal)) };
            }),
        };
    }

    [Rpc("report.excel", Perm = "booking.read")]
    public static IResult Excel(Call c)
    {
        var (first, last) = Period(c); var name = c.Str("tabella"); var (data, _, _) = Data(c, first, last, name);
        if (!data.TryGetValue(name, out var t)) throw new KeyNotFoundException();
        c.C.Audit("export.report", name);
        return Results.File(Reports.Xlsx(t.Head, t.Rows, Reports.TITLES.First(x => x.Key == name).Title), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"report-{name}-{first:yyyy-MM-dd}-{last:yyyy-MM-dd}.xlsx");
    }
}
