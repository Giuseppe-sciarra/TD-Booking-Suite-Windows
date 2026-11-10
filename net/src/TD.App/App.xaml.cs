using System.Windows;
namespace TD.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new Views.Shell().Start(e.Args);
    }
}
