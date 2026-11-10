using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/clients.py: the client card, its keyed indexes and how a booking finds it.</summary>
public sealed partial class Clients(Ctx c)
{
    public const string ERASED = "erased:";

    public string PhoneIndex(string phone) => c.Index("tel:" + phone, "email");
    public string EmailIndex(string email) => c.Index(Crypto.CaseFold(email), "email");

    /// <summary>Lower case without accents, in words: "Nicolò D'Amico" is nicolo, d, amico.</summary>
    public static List<string> Words(string text)
    {
        var t = Crypto.CaseFold(text ?? "").Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder();
        foreach (var ch in t) if (CharUnicodeInfo.GetUnicodeCategory(ch) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)) sb.Append(ch);
        return Regex.Matches(sb.ToString(), @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();
    }

    /// <summary>The short keyed index of a word, with the business key derived once.</summary>
    public static Func<string, string> SearchKeys(byte[] key)
    {
        var mac = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, key, new byte[32]), 32, Encoding.UTF8.GetBytes("search"));
        return v => Convert.ToHexString(HMACSHA256.HashData(mac, Encoding.UTF8.GetBytes(v))).ToLowerInvariant()[..16];
    }
    public static string TagKey(Func<string, string> h, string tag) => h("t:" + string.Join(" ", Words(tag)));

    public static readonly string[] INDEXES = { "phone_index", "name_index", "tag_index", "birthday_index" };
    /// <summary>The indexes of a card from its data: phone, each start of the words of its name, its tags, its birthday.</summary>
    public static void Stamp(Contact ct, JsonObject d, byte[] key, Func<string, string> h = null)
    {
        h ??= SearchKeys(key);
        ct.PhoneIndex = d.Str("phone") != "" ? Crypto.Index("tel:" + d.Str("phone"), key, "email") : "";
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var w in Words(d.Str("first_name") + " " + d.Str("last_name")))
            for (int i = 1; i <= Math.Min(w.Length, 20); i++) keys.Add(h("n:" + w[..i]));
        ct.NameIndex = string.Join(" ", keys);
        ct.TagIndex = string.Join(" ", (d["tags"] as JsonArray ?? new JsonArray()).Select(t => TagKey(h, t?.ToString() ?? "")));
        var bd = d.Str("birthday");
        ct.BirthdayIndex = bd.Length >= 10 ? h("b:" + bd[5..]) : "";
    }
    public void SealCard(Contact ct, JsonObject d)
    {
        ct.Pii = c.Encrypt(d, "contact:" + ct.Id); Stamp(ct, d, c.Key);
    }
    public JsonObject Data(Contact ct) => c.Decrypt(ct.Pii, "contact:" + ct.Id);
    public void SaveCard(Contact ct, params string[] extra) => c.S.Update(ct, new[] { "pii", "email_index" }.Concat(INDEXES).Concat(extra).Distinct().ToArray());

    public static bool ValidEmail(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 254 || s.Contains(' ')) return false;
        // Django's EmailValidator: user part, @, a domain with a dot (or a literal)
        return Regex.IsMatch(s, @"^(?:[-!#$%&'*+/=?^_`{}|~0-9A-Z]+(?:\.[-!#$%&'*+/=?^_`{}|~0-9A-Z]+)*|""(?:[\x01-\x08\x0b\x0c\x0e-\x1f!#-\[\]-\x7f]|\\[\x01-\x09\x0b\x0c\x0e-\x7f])*"")@(?:(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\.)+(?:[A-Z0-9-]{2,63}(?<!-))|\[[0-9.:A-F]+\])$",
            RegexOptions.IgnoreCase);
    }

    /// <summary>2.6.1: Italian numbers as people write them become +39...; empty when it cannot be a phone number.</summary>
    public static string NormalizePhone(string value)
    {
        var raw = Regex.Replace(value ?? "", @"[\s\-./()]", "");
        if (raw.StartsWith("00")) raw = "+" + raw[2..];
        if (!raw.StartsWith("+")) raw = "+39" + raw;
        return Regex.IsMatch(raw, @"^\+[1-9]\d{6,14}$") ? raw : "";
    }

    /// <summary>Name, surname, email and phone typed by the staff. Email optional, phone or email needed.</summary>
    public static JsonObject Clean(JsonObject data, bool requireContact = true)
    {
        var o = new JsonObject();
        foreach (var (k, limit) in new[] { ("first_name", 100), ("last_name", 100), ("email", 254), ("phone", 40) })
        {
            var v = (data[k] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : data[k] == null ? "" : Json.Truthy(data[k]) ? data[k].ToString() : "").Trim();
            if (v.Length > limit || v.Any(ch => ch < 32)) throw new BookingException("Dati del cliente non validi.");
            o[k] = v;
        }
        if (o.Str("first_name") == "") throw new BookingException("Scrivi almeno il nome del cliente.");
        if (o.Str("email") != "")
        {
            if (!ValidEmail(o.Str("email"))) throw new BookingException("Email non valida.");
            o["email"] = Crypto.CaseFold(o.Str("email"));
        }
        if (o.Str("phone") != "")
        {
            var p = NormalizePhone(o.Str("phone"));
            if (p == "") throw new BookingException("Numero di telefono non valido, per esempio 333 123 4567.");
            o["phone"] = p;
        }
        if (requireContact && o.Str("email") == "" && o.Str("phone") == "") throw new BookingException("Serve almeno il telefono o l'email del cliente.");
        return o;
    }

    public static string FullName(JsonObject d) => string.Join(" ", Crypto.CaseFold(d.Str("first_name") + " " + d.Str("last_name")).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The card's key: its email, otherwise its phone; a phone on another person's card is told apart by the name.</summary>
    public string KeyFor(JsonObject d, bool shared = false)
    {
        if (d.Str("email") != "") return EmailIndex(d.Str("email"));
        if (shared) return c.Index("tel:" + d.Str("phone") + "|" + FullName(d), "email");
        return PhoneIndex(d.Str("phone"));
    }

    public bool SamePerson(Contact card, JsonObject d)
    {
        var a = FullName(Data(card)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = FullName(d).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (shortW, longW) = a.Length <= b.Length ? (a, b) : (b, a);
        return shortW.All(longW.Contains);
    }

    public Contact ByIndex(string idx) => c.S.One<Contact>("SELECT * FROM core_contact WHERE email_index=@i LIMIT 1", new { i = idx });

    /// <summary>The card of a phone number: keyed by it, or with it among its data.</summary>
    public Contact ByPhone(string phone, bool withoutEmail = false)
    {
        if (string.IsNullOrEmpty(phone)) return null;
        var key = PhoneIndex(phone);
        var ct = ByIndex(key);
        if (ct != null) return ct;
        foreach (var x in c.S.Query<Contact>("SELECT * FROM core_contact WHERE phone_index=@k AND email_index NOT LIKE 'erased:%' ORDER BY created DESC", new { k = key }))
            if (!(withoutEmail && Data(x).Str("email") != "")) return x;
        return null;
    }

    /// <summary>The contact of a manual booking or of a client added by hand: by email, then by phone (named: by name).</summary>
    public (Contact, bool) FindOrCreate(JsonObject d, bool named = false)
    {
        var ct = d.Str("email") != "" ? ByIndex(EmailIndex(d.Str("email"))) : null;
        if (ct != null) return (ct, false);
        var known = ByPhone(d.Str("phone"));
        if (known != null && SamePerson(known, d)) return (known, false);
        if (known != null && d.Str("email") == "")
        {
            var mine = ByIndex(KeyFor(d, shared: true));
            if (mine != null) return (mine, false);
        }
        if (named && ByName(d) is Contact hit) return (hit, false);
        ct = new Contact { TenantId = c.Tenant.Id, EmailIndex = KeyFor(d, shared: known != null), Created = c.Now };
        SealCard(ct, new JsonObject { ["first_name"] = d.Str("first_name"), ["last_name"] = d.Str("last_name"), ["email"] = d.Str("email"), ["phone"] = d.Str("phone") });
        c.S.Insert(ct);
        return (ct, true);
    }

    /// <summary>2.21.0: the only card with this first and last name however written, or the one with the same phone or email.</summary>
    public Contact ByName(JsonObject d)
    {
        var w = Words(d.Str("first_name") + " " + d.Str("last_name")).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (w.Count < 2) return null;
        var h = SearchKeys(c.Key);
        var sql = new StringBuilder("SELECT * FROM core_contact WHERE email_index NOT LIKE 'erased:%' AND merged_into_id IS NULL");
        var p = new Dapper.DynamicParameters();
        for (int i = 0; i < w.Count; i++) { sql.Append($" AND name_index LIKE @n{i}"); p.Add("n" + i, "%" + h("n:" + (w[i].Length > 20 ? w[i][..20] : w[i])) + "%"); }
        sql.Append(" ORDER BY created DESC LIMIT 50");
        var hits = new List<(Contact, JsonObject)>();
        foreach (var ct in c.S.Query<Contact>(sql.ToString(), p))
        {
            var cd = Data(ct);
            if (Words(cd.Str("first_name") + " " + cd.Str("last_name")).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(w)) hits.Add((ct, cd));
        }
        if (hits.Count == 1) return hits[0].Item1;
        var phone = d.Str("phone") != "" ? NormalizePhone(d.Str("phone")) : ""; var email = Crypto.CaseFold(d.Str("email"));
        var same = hits.Where(x => phone != "" && NormalizePhone(x.Item2.Str("phone")) == phone || email != "" && Crypto.CaseFold(x.Item2.Str("email")) == email).ToList();
        return same.Count == 1 ? same[0].Item1 : null;
    }

    /// <summary>2.14.0: the limit of the client's category on the online page, in words; null when he may book.</summary>
    public string CategoryRefusal(Contact contact, DateTime start, DateTime now, IEnumerable<Guid> moving = null)
    {
        var name = contact != null ? Data(contact).Str("category") : "";
        if (name == "") return null;
        var cat = c.Tenant.Categories.FirstOrDefault(x => x.Str("name") == name);
        if (cat == null) return null;
        var ahead = (int?)cat["ahead"] ?? 0; var most = (int?)cat["max"] ?? 0;
        if (ahead > 0 && start > now.AddDays(ahead)) return $"Puoi prenotare al massimo {ahead} giorni avanti.";
        if (most > 0 && c.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE contact_id=@c AND status = ANY(@o) AND \"end\">@n AND NOT id = ANY(@x)",
                new { c = contact.Id, o = Bookings.OPEN, n = now, x = moving?.ToArray() ?? Array.Empty<Guid>() }) >= most)
            return $"Hai già {most} prenotazion{(most == 1 ? "e" : "i")} in programma: per prenotarne un'altra contatta l'attività.";
        return null;
    }
}
