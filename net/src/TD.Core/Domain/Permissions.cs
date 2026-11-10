using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>Roles and capabilities, as core/permissions.py: role -> permissions, plus the location/resource scope.</summary>
public static class Permissions
{
    public static readonly Dictionary<string, string> Perms = new()
    {
        ["booking.read"] = "Vedere l'agenda",
        ["booking.write"] = "Creare e spostare prenotazioni",
        ["booking.cancel"] = "Annullare e chiudere prenotazioni",
        ["pii.read"] = "Vedere nomi e recapiti",
        ["notes.read"] = "Vedere note del cliente e note interne",
        ["contacts.read"] = "Vedere l'anagrafica clienti",
        ["catalog.write"] = "Modificare servizi e disponibilità",
        ["export.operational"] = "Esportare le prenotazioni",
        ["export.marketing"] = "Esportare i contatti",
        ["campaign.manage"] = "Preparare e inviare campagne",
        ["users.manage"] = "Gestire utenti e accessi di assistenza",
        ["audit.read"] = "Leggere il registro attività",
        ["privacy.manage"] = "Gestire richieste privacy (accesso e cancellazione dei dati)",
        ["booking.force"] = "Forzare orari e posti al banco",
        ["sale.write"] = "Chiudere il conto alla cassa",
    };
    static readonly HashSet<string> All = new(Perms.Keys);
    public static readonly Dictionary<string, (string Label, HashSet<string> Perms)> Roles = new()
    {
        ["owner"] = ("Amministratore", All),
        ["manager"] = ("Amministratore sede", new() { "booking.read", "booking.write", "booking.cancel", "booking.force", "pii.read", "notes.read", "contacts.read", "export.operational", "sale.write" }),
        ["reception"] = ("Reception", new() { "booking.read", "booking.write", "booking.cancel", "pii.read", "notes.read", "contacts.read", "sale.write" }),
        ["operator"] = ("Operatore sede", new() { "booking.read", "booking.write", "pii.read", "notes.read" }),
        ["marketing"] = ("Marketing", new() { "contacts.read", "pii.read", "campaign.manage", "export.marketing" }),
        ["viewer"] = ("Consultazione", new() { "booking.read" }),
    };
    /// <summary>The Windows edition calls the owner Titolare (ROLE_LABELS).</summary>
    public static string RoleLabel(string role) => role == "owner" ? "Titolare" : Roles.TryGetValue(role, out var r) ? r.Label : role;

    /// <summary>What the administrator of the program may do in a business with an access of assistance granted by its owner.</summary>
    public static readonly string[] SUPPORT = { "booking.read", "booking.write", "booking.cancel", "pii.read", "notes.read", "contacts.read", "catalog.write", "audit.read" };
    public static HashSet<string> Effective(Membership m, Tenant t, bool staff)
    {
        var perms = new HashSet<string>();
        if (m != null)
        {
            if (Roles.TryGetValue(m.Role, out var r)) perms.UnionWith(r.Perms);
            if (m.CanSchedule && m.Role is not ("viewer" or "marketing")) perms.Add("catalog.write");
            if (staff) perms.Add("catalog.write");
            if (perms.Contains("booking.write") && t?.ForceAll == true) perms.Add("booking.force");
            if (m.Role == "operator" && t?.TillOperators == true) perms.Add("sale.write");
            if (t?.TillOn != true) perms.Remove("sale.write");
        }
        else if (staff) perms.Add("catalog.write");
        return perms;
    }

    public static List<string> Ids(string json) => (JsonNode.Parse(string.IsNullOrEmpty(json) ? "[]" : json) as JsonArray)?.Select(x => (string)x).Where(x => x != null).ToList() ?? new();

    /// <summary>The SQL condition on core_booking for the bookings this person may see (visible_bookings).</summary>
    public static (string Sql, object Args) VisibleBookings(Membership m, HashSet<string> perms, string alias = "b")
    {
        if (!perms.Contains("booking.read")) return ("false", null);
        if (m == null) return ("true", null);
        var res = Ids(m.Resources).Select(Guid.Parse).ToArray();
        var locs = Ids(m.Locations).Select(Guid.Parse).ToArray();
        if (m.Role == "operator")
            return res.Length > 0 ? ($"{alias}.resource_id = ANY(@__res)", new { __res = res }) : ($"{alias}.location_id = ANY(@__locs)", new { __locs = locs });
        var parts = new List<string>();
        if (locs.Length > 0) parts.Add($"{alias}.location_id = ANY(@__locs)");
        if (res.Length > 0) parts.Add($"{alias}.resource_id = ANY(@__res)");
        return (parts.Count == 0 ? "true" : string.Join(" AND ", parts), new { __locs = locs, __res = res });
    }

    /// <summary>Manual bookings stay inside the creator's own scope (check_new_booking). Null when allowed.</summary>
    public static string CheckNewBooking(Membership m, Guid serviceLocation, Guid? resource)
    {
        if (m == null) return null;
        var locs = Ids(m.Locations); var res = Ids(m.Resources);
        if (locs.Count > 0 && !locs.Contains(serviceLocation.ToString())) return "Sede fuori dal tuo ambito.";
        if (res.Count > 0 && (resource == null || !res.Contains(resource.ToString()))) return "Puoi prenotare solo sulle risorse assegnate.";
        return null;
    }

    static string Initials(string v) { v = (v ?? "").Trim(); return v.Length > 0 ? char.ToUpper(v[0]) + "." : ""; }
    static readonly string[] Fiscal = { "company", "tax_code", "vat", "address", "zip", "city", "province", "country", "sdi", "pec" };

    /// <summary>Field-level view of booking/contact data for the current role (shown).</summary>
    public static JsonObject Shown(HashSet<string> perms, JsonObject data)
    {
        var d = data == null ? new JsonObject() : (JsonObject)data.DeepClone();
        if (!perms.Contains("pii.read"))
        {
            foreach (var k in new[] { "first_name", "last_name" }) d[k] = Initials((string)d[k]);
            foreach (var k in new[] { "email", "phone" }) d[k] = string.IsNullOrEmpty((string)d[k]) ? "" : "•••";
            foreach (var k in Fiscal) if (!string.IsNullOrEmpty(d[k]?.ToString())) d[k] = "•••";
            d["answers"] = new JsonObject(); d["masked"] = true; d.Remove("guests");
        }
        if (!perms.Contains("notes.read"))
        {
            foreach (var k in new[] { "notes", "internal_notes", "allergies", "cancel_note" }) d.Remove(k);
            d["notes_hidden"] = true;
        }
        d.Remove("cancel_token");
        return d;
    }
}
