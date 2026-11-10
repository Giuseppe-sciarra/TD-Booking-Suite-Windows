using TD.App.Ui;
using static TD.App.Ui.U;

namespace TD.App.Views;

/// <summary>templates/catalog.html: the lists of Configura l'attività (sedi, orari, chiusure, servizi, risorse, varianti,
/// turni, date, prodotti). Still to come in this build: the list and its forms.</summary>
public sealed class CatalogPage : Page
{
    public string Kind = "services";
    public override Task Load()
    {
        Content = F.Page(1180, Col(F.Back("Configura l'attività", () => Main.Show("configura")), F.Heading(null, "Catalogo", "Questa pagina arriva con il prossimo pezzo.")));
        return Task.CompletedTask;
    }
}

/// <summary>templates/allergens.html: Intolleranze e allergie. Still to come in this build.</summary>
public sealed class AllergensPage : Page
{
    public override Task Load()
    {
        Content = F.Page(1180, Col(F.Back("Configura l'attività", () => Main.Show("configura")), F.Heading(null, "Intolleranze e allergie", "Questa pagina arriva con il prossimo pezzo.")));
        return Task.CompletedTask;
    }
}
