using System.Text.Json.Nodes;
using Dapper;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>core/photos.py: the photos of the work, on the visit of a booking, on an entry of the record or on today.</summary>
public static class Foto
{
    /// <summary>photos.on: where the business keeps a client record, for who sees the clients' data.</summary>
    public static bool On(Call c) => c.Can("pii.read") && Presets.RecordOf(c.Tenant.BusinessType, c.C.S.Scalar<long>("SELECT count(*) FROM core_service WHERE kind='resource'") > 0) != null;

    [Rpc("foto.aggiungi", Perm = "booking.write")]
    public static object Add(Call c)
    {
        c.Require("pii.read");
        if (!On(c)) throw new UnauthorizedAccessException("Le foto ci sono dove c'è la scheda cliente.");
        var x = c.C; var contact = x.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = c.NeedId("cliente") }) ?? throw new KeyNotFoundException();
        Guid? booking = null; DateOnly day;
        if (c.Id("voce") is Guid tid)
        {
            // on the day of an entry of the record, and its visit if it came from one
            var entry = x.S.One<Treatment>("SELECT * FROM core_treatment WHERE contact_id=@c AND id=@id", new { c = contact.Id, id = tid }) ?? throw new KeyNotFoundException();
            c.Require("contacts.read"); booking = entry.BookingId; day = DateOnly.FromDateTime(entry.Date);
        }
        else if (c.Id("prenotazione") is Guid bid)
        {
            var (vis, vargs) = Permissions.VisibleBookings(c.Member, c.Perms, "b"); var p = new DynamicParameters(vargs); p.Add("id", bid); p.Add("cid", contact.Id);
            var b = x.S.One<Booking>($"SELECT b.* FROM core_booking b WHERE b.id=@id AND b.contact_id=@cid AND ({vis})", p) ?? throw new KeyNotFoundException();
            booking = b.Id; day = x.Cal.DayOf(b.Start);
        }
        else { c.Require("contacts.read"); day = x.Today; }
        var files = new List<(byte[], byte[])>();
        foreach (var f in c["foto"] as JsonArray ?? new JsonArray())
        {
            try { files.Add((Convert.FromBase64String(f.Str("grande")), Convert.FromBase64String(f.Str("piccola")))); }
            catch (FormatException) { files.Add((Array.Empty<byte>(), Array.Empty<byte>())); }
        }
        var (made, error, left) = new Photos(x).Add(contact, booking, day, files);
        return new { fatte = made, errore = error, liberi = left };
    }

    [Rpc("foto.togli", Perm = "booking.write")]
    public static object Remove(Call c)
    {
        c.Require("pii.read");
        return new { ok = true, liberi = new Photos(c.C).Remove(c.NeedId("id")) };
    }

    /// <summary>The photo, or its small copy: the JPEG itself.</summary>
    [Rpc("foto.immagine", Perm = "pii.read")]
    public static IResult Image(Call c)
    {
        var (data, _) = new Photos(c.C).Image(c.NeedId("id"), c.Bool("piccola"));
        return Results.File(data, "image/jpeg");
    }
}
