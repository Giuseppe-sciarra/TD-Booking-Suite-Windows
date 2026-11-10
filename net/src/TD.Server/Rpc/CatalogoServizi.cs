using TD.Core.Domain;

namespace TD.Server.Rpc;

public static partial class Catalogo
{
    sealed record Special(List<Fld> Fields, object Extra);
    static Special ServiceForm(Call c, Guid? id) => throw new BookingException("Il modulo dei servizi arriva con il prossimo pezzo.");
    static Special ResourceForm(Call c, Guid? id) => throw new BookingException("Il modulo delle risorse arriva con il prossimo pezzo.");
    static Guid SaveService(Call c, Guid? id, Posted p) => throw new BookingException("Il modulo dei servizi arriva con il prossimo pezzo.");
    static Guid SaveResource(Call c, Guid? id, Posted p) => throw new BookingException("Il modulo delle risorse arriva con il prossimo pezzo.");
}
