using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/stays.py: stays, units, tourist tax, the portals' calendars.</summary>
public sealed partial class Stays(Ctx c)
{
    public static readonly string[] WEEKDAYS = { "lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato", "domenica" };

    /// <summary>The rules of the periods holding the arrival and the departure; empty when the stay is allowed.</summary>
    public static string Restricted(Service service, DateOnly first, DateOnly last)
    {
        var f = first.ToString("yyyy-MM-dd"); var l = last.ToString("yyyy-MM-dd");
        foreach (var r in Json.Arr(service.StayRules).OfType<JsonObject>())
        {
            var from = r.Str("from"); var until = r.Str("until");
            bool In(string d) => string.CompareOrdinal(from, d) <= 0 && string.CompareOrdinal(d, until) <= 0;
            if (In(f))
            {
                if ((r["no_arrival"] as JsonArray ?? new()).Any(x => (int)x == Calendar.Weekday(first))) return $"In questo periodo non si arriva di {WEEKDAYS[Calendar.Weekday(first)]}.";
                if (last.DayNumber - first.DayNumber < ((int?)r["min"] ?? 0)) return $"In questo periodo il soggiorno minimo è di {(int?)r["min"] ?? 0} notti.";
            }
            if (In(l) && (r["no_departure"] as JsonArray ?? new()).Any(x => (int)x == Calendar.Weekday(last))) return $"In questo periodo non si parte di {WEEKDAYS[Calendar.Weekday(last)]}.";
        }
        return "";
    }
}
