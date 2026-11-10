using System.Security.Cryptography;
using System.Text;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/privacy.py: the consent history, access and erasure requests, retention.</summary>
public sealed partial class Privacy(Ctx c)
{
    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();

    public Consentevent Record(Guid contact, string kind, string channel, string text = null, string reference = "") =>
        c.S.Insert(new Consentevent
        {
            TenantId = c.Tenant.Id, ContactId = contact, Kind = kind, Channel = channel, Actor = c.Actor?.ToString() ?? "",
            TextHash = text != null ? TextHash(text) : "", Ref = reference ?? "", At = c.Now,
        });
}
