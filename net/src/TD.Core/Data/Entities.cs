using System.Text.Json.Nodes;
using TD.Core.Domain;

namespace TD.Core.Data;

/// <summary>The JSON columns of the Python models, as objects; Flush() writes them back before a save.</summary>
public interface IJsonColumns { void Flush(); }

public partial class Booking : IJsonColumns
{
    JsonObject snap; List<string> more, tables;
    public JsonObject Snap { get => snap ??= Json.Obj(Snapshot); set => snap = value; }
    public List<string> MoreIds { get => more ??= Json.Strings(More); set => more = value; }
    public List<string> TableIds { get => tables ??= Json.Strings(Tables); set => tables = value; }
    public void Flush()
    {
        if (snap != null) Snapshot = Json.Dump(snap);
        if (more != null) More = Json.Dump(new JsonArray(more.Select(x => (JsonNode)x).ToArray()));
        if (tables != null) Tables = Json.Dump(new JsonArray(tables.Select(x => (JsonNode)x).ToArray()));
    }
}
public partial class Service
{
    public JsonArray FieldList => Json.Arr(Fields);
    public JsonObject LimitMap => Json.Obj(Limits);
}
public partial class Resource { public JsonObject LimitMap => Json.Obj(Limits); }
public partial class Tenant
{
    public bool Stays => BusinessType is "hotel" or "casa_vacanze";
    public List<string> AskedAllergens => AllergensOn && Presets.ServesFood(BusinessType) ? Json.Strings(Allergens) : new();
    public JsonArray Categories => Json.Arr(ClientCategories);
}

