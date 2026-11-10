using System.Text.Json.Nodes;
using TD.Core.Domain;
using Xunit;

namespace TD.Tests;

/// <summary>An outside calendar read as the Python program reads it (feeds.events and blocks; fixtures/calendar.json
/// is what it gives for fixtures/calendar.ics).</summary>
public class CalendarTests
{
    [Fact]
    public void Reads_an_outside_calendar_as_python()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "fixtures"); var want = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "calendar.json")))!;
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"); var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var events = OutsideCalendar.Events(File.ReadAllText(Path.Combine(folder, "calendar.ics")), tz, now).OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        Assert.Equal(want["events"]!.AsArray().Select(x => $"{x![0]} {x[1]}"), events.Select(x => $"{x.Start:yyyy-MM-ddTHH:mm:ssZ} {x.End:yyyy-MM-ddTHH:mm:ssZ}"));
        Assert.Equal(want["blocks"]!.AsArray().Select(x => $"{x![0]} {x[1]} {x[2]}"), OutsideCalendar.Blocks(events, tz).Select(x => $"{x.Day:yyyy-MM-dd} {x.Start:hh\\:mm\\:ss} {x.End:hh\\:mm\\:ss}"));
    }
}
