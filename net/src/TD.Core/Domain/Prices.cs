using System.Globalization;
using System.Text.Json.Nodes;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/prices.py: the price of a service or variant and the client's own discount, written into the booking
/// (snapshot price/total/discount as strings, as Python's str(Decimal)).</summary>
public static class Prices
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>25 → "25,00 €", 1234.5 → "1.234,50 €".</summary>
    public static string Euro(decimal? value) => value == null ? "" : Round(value.Value).ToString("#,##0.00", It) + " €";
    public static string Euro(string value) => string.IsNullOrEmpty(value) ? "" : Euro(Parse(value));

    public static decimal Parse(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);
    /// <summary>Python's str(Decimal) for the amounts it wrote: "25.00", "12.5" stay as the digits they have.</summary>
    public static string Str(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    /// <summary>Decimal.normalize(): no trailing zeros ("10.00" → "10").</summary>
    public static string Normal(decimal d) => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    public static string DiscountLabel(Contact c)
    {
        if (c == null || string.IsNullOrEmpty(c.DiscountKind) || c.Discount == 0) return "";
        if (c.DiscountKind == "percent") return Normal(c.Discount).Replace(".", ",") + "%";
        return Euro((decimal?)c.Discount);
    }

    public static Dictionary<string, string> Amounts(Scope s, Service service, Option option, Contact contact, Resource resource = null)
    {
        decimal? price = option?.Price;
        if (price == null && resource != null)
            price = s.Scalar<decimal?>("SELECT price FROM core_serviceresource WHERE service_id=@a AND resource_id=@b LIMIT 1", new { a = service.Id, b = resource.Id });
        price ??= service.Price;
        if (price == null) return new();
        var total = price.Value;
        var label = DiscountLabel(contact);
        if (label != "")
        {
            total = contact.DiscountKind == "percent" ? price.Value * (100 - contact.Discount) / 100 : price.Value - contact.Discount;
            total = Round(Math.Max(0, total));
        }
        var r = new Dictionary<string, string> { ["price"] = Str(price.Value), ["total"] = Str(total) };
        if (total != price.Value) r["discount"] = label;
        return r;
    }

    /// <summary>The amounts of the booking as it is now, into its snapshot.</summary>
    public static void Stamp(Scope s, JsonObject snapshot, Service service, Option option, Contact contact, Resource resource)
    {
        foreach (var k in new[] { "price", "total", "discount" }) snapshot.Remove(k);
        foreach (var (k, v) in Amounts(s, service, option, contact, resource)) snapshot[k] = v;
    }

    public static decimal? Spent(IEnumerable<JsonObject> snapshots)
    {
        var values = snapshots.Select(x => x["total"]?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(Parse).ToList();
        return values.Count > 0 ? values.Sum() : null;
    }
}
