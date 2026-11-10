using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/photos.py: the client's photo album: one to four photos of the work on the visit of a day, a JPEG of
/// at most 1600 px plus a small copy, both sealed with the business key. Kept in this program, never sent to the site.
/// The window makes the two copies before sending (WIC); here they are checked to be the JPEGs they claim to be.</summary>
public sealed class Photos(Ctx c)
{
    public const int PER_DAY = 4, MAX_FULL = 4 * 1024 * 1024, MAX_THUMB = 400 * 1024;

    public sealed record Row(Guid Id, DateTime Date, Guid? BookingId, DateTime Created);
    public sealed record Day(DateOnly Date, string Label, List<Guid> Photos, int Left, Guid? Booking, bool Entry);

    List<Row> Rows(Guid contact, DateOnly? day = null) =>
        c.S.Query<Row>("SELECT id, date, booking_id AS BookingId, created FROM core_clientphoto WHERE contact_id=@c" + (day.HasValue ? " AND date=@d" : "") + " ORDER BY date DESC, created",
            new { c = contact, d = day?.ToDateTime(TimeOnly.MinValue) }).ToList();

    public int Count(Guid contact, DateOnly day) => (int)c.S.Scalar<long>("SELECT count(*) FROM core_clientphoto WHERE contact_id=@c AND date=@d", new { c = contact, d = day.ToDateTime(TimeOnly.MinValue) });

    /// <summary>album(): the client's photos by day, newest first; label: the services of that day.</summary>
    public List<Day> Album(Contact contact, DateOnly? day = null)
    {
        var days = Rows(contact.Id, day).GroupBy(p => DateOnly.FromDateTime(p.Date)).ToList();
        if (days.Count == 0) return new();
        var cl = new Clients(c); var dates = days.Select(g => g.Key).ToList();
        var entries = c.S.Query<Treatment>("SELECT * FROM core_treatment WHERE contact_id=@c AND date = ANY(@d) ORDER BY created", new { c = contact.Id, d = dates.Select(x => x.ToDateTime(TimeOnly.MinValue)).ToArray() })
            .GroupBy(e => DateOnly.FromDateTime(e.Date)).ToDictionary(g => g.Key, g => g.Select(e => cl.Info(e).Str("service")).ToList());
        var names = new Dictionary<DateOnly, List<string>>(); var first = new Dictionary<DateOnly, Guid>();
        var a = c.Cal.Local(dates.Min().AddDays(-1), TimeSpan.Zero)!.Value; var z = c.Cal.Local(dates.Max().AddDays(2), TimeSpan.Zero)!.Value;
        foreach (var b in c.S.Query<Booking>("SELECT * FROM core_booking WHERE contact_id=@c AND start>=@a AND start<@z AND status NOT IN ('cancelled','rejected','expired')", new { c = contact.Id, a, z }))
        {
            var d = c.Cal.DayOf(b.Start);
            if (!names.TryGetValue(d, out var list)) names[d] = list = new();
            list.Add(b.Snap.Str("service") is var s && s != "" ? s : c.Row<Service>(b.ServiceId).Name); first.TryAdd(d, b.Id);
        }
        return days.Select(g =>
        {
            var label = string.Join(" · ", entries.GetValueOrDefault(g.Key, new()).Concat(names.GetValueOrDefault(g.Key, new())).Distinct().Where(x => x != ""));
            return new Day(g.Key, label, g.Select(p => p.Id).ToList(), Math.Max(0, PER_DAY - g.Count()), g.FirstOrDefault(p => p.BookingId.HasValue)?.BookingId ?? (first.TryGetValue(g.Key, out var f) ? f : null), entries.ContainsKey(g.Key));
        }).ToList();
    }

    /// <summary>by_day(): {day: [photos]} of a client, oldest first: for the record entries and the earlier visits.</summary>
    public Dictionary<DateOnly, List<Guid>> ByDay(Guid contact) =>
        Rows(contact).OrderBy(p => p.Created).GroupBy(p => DateOnly.FromDateTime(p.Date)).ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToList());

    static bool Jpeg(byte[] b) => b is { Length: > 3 } && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>upload(): at most PER_DAY a day: the ones over are left out and said. Returns (made, error, left).</summary>
    public (List<Guid> Made, string Error, int Left) Add(Contact contact, Guid? booking, DateOnly day, List<(byte[] Full, byte[] Thumb)> files)
    {
        var room = PER_DAY - Count(contact.Id, day); var error = ""; var made = new List<Guid>();
        if (files.Count == 0) error = "Scegli una foto.";
        else if (room <= 0) error = $"Ci sono già {PER_DAY} foto per questo giorno: togline una per aggiungerne un'altra.";
        else
        {
            foreach (var (full, thumb) in files.Take(room))
            {
                if (!Jpeg(full) || !Jpeg(thumb) || full.Length > MAX_FULL || thumb.Length > MAX_THUMB) { error = "Usa una foto JPEG, PNG o WebP valida."; continue; }
                var p = new Clientphoto { TenantId = c.Tenant.Id, ContactId = contact.Id, BookingId = booking, Date = day.ToDateTime(TimeOnly.MinValue), Created = c.Now };
                p.Image = c.Crypto.EncryptBytes(c.Tenant.Id, c.Tenant.WrappedKey, full, "photo:" + p.Id); p.Thumb = c.Crypto.EncryptBytes(c.Tenant.Id, c.Tenant.WrappedKey, thumb, "photo-thumb:" + p.Id);
                c.S.Insert(p); made.Add(p.Id);
            }
            if (files.Count > room) error = $"Tenute le prime {room}: al massimo {PER_DAY} foto per giorno.";
        }
        if (made.Count > 0) c.Audit("photo.added", contact.Id, new { count = made.Count, day = day.ToString("yyyy-MM-dd") });
        return (made, error, Math.Max(0, PER_DAY - Count(contact.Id, day)));
    }

    public int Remove(Guid id)
    {
        var p = c.S.One<Row>("SELECT id, date, booking_id AS BookingId, created FROM core_clientphoto WHERE id=@id", new { id }) ?? throw new KeyNotFoundException();
        var contact = c.S.Scalar<Guid>("SELECT contact_id FROM core_clientphoto WHERE id=@id", new { id }); var day = DateOnly.FromDateTime(p.Date);
        c.S.Execute("DELETE FROM core_clientphoto WHERE id=@id", new { id });
        c.Audit("photo.removed", contact, new { day = day.ToString("yyyy-MM-dd") });
        return Math.Max(0, PER_DAY - Count(contact, day));
    }

    public (byte[] Data, DateOnly Day) Image(Guid id, bool small)
    {
        var row = c.S.One<(byte[], DateTime)>($"SELECT {(small ? "thumb" : "image")}, date FROM core_clientphoto WHERE id=@id", new { id });
        if (row.Item1 == null) throw new KeyNotFoundException();
        return (c.Crypto.DecryptBytes(c.Tenant.Id, c.Tenant.WrappedKey, row.Item1, (small ? "photo-thumb:" : "photo:") + id), DateOnly.FromDateTime(row.Item2));
    }
}
