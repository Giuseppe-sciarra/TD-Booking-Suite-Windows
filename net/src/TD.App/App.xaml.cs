using System.IO;
using System.Windows;
namespace TD.App;
public partial class App : Application
{
    public static string CrashLog => Path.Combine(Path.GetTempPath(), "TDBookingSuite-errore.txt");
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, x) => File.AppendAllText(CrashLog, DateTime.Now + "\n" + x.ExceptionObject + "\n\n");
        DispatcherUnhandledException += (_, x) =>
        {
            File.AppendAllText(CrashLog, DateTime.Now + "\n" + x.Exception + "\n\n");
            if (e.Args.Contains("--shot")) { x.Handled = true; Shutdown(2); }
        };
        base.OnStartup(e);
        try { new Views.Shell().Start(e.Args); }
        catch (Exception ex) { File.AppendAllText(CrashLog, ex + "\n"); throw; }
    }
}
