using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>The real IOutbox: client emails, staff notices, webhooks and the site's archive, as the Python's modules do
/// (client_mails.send, notify.notify_staff, api.emit, wpsync.touch). Filled in module by module.</summary>
public sealed class Outbox : IOutbox
{
    public static readonly Outbox Instance = new();
    public void ClientMail(Ctx c, Booking b, string kind) => ClientMails.Send(c, b, kind);
    public void NotifyStaff(Ctx c, Booking b, string kind) => Notify.Staff(c, b, kind);
    public void Emit(Ctx c, Booking b, string kind) => Webhooks.Emit(c, b, kind);
    public void Touch(Ctx c, string kind, IEnumerable<Guid> ids) => WpSync.Touch(c, kind, ids);
}
