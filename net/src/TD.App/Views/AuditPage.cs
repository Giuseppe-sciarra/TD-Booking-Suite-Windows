using TD.App.Net;
using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/audit.html: Registro attività.</summary>
public sealed class AuditPage : Page
{
    int page = 1;
    public override async Task Load()
    {
        var z = new AgendaPage { Main = Main }.Also(Business.S("fuso")); var d = await Api.Current.Call("registro", new { pagina = page });
        var rows = d.A("righe").Select(r => new[] { z.Local(r.S("quando")).ToString("dd/MM/yyyy HH:mm:ss"), r.S("operazione"), r.S("utente"), r.S("riferimento") }).ToList();
        Content = F.Page(1480, Col(F.Heading(null, "Registro attività", "Operazioni registrate senza copiare recapiti, note o credenziali nei log."),
            F.Card(F.Table(new[] { "Quando", "Operazione", "Utente", "Riferimento" }, rows, 14), F.Pagination(d.I("pagina"), d.I("pagine"), async p => { page = p; await Guard(Load); }))));
    }
}
