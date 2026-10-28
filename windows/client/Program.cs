using System;
using System.Windows.Forms;

namespace TDBookingSuite
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var config = Config.Load();
            // on the server PC the server installed there, with the fingerprint the installation wrote: no questions,
            // and it follows that server when it changes (Un PC solo, another port), unless another one was chosen by hand
            var installed = Config.Default();
            if (config != null && !config.ManualKnown && installed != null)
            {
                // a 2.8 workstation that also had the server installed: a server chosen there by hand stays, once
                config.Manual = !string.Equals(config.Server, installed.Server, StringComparison.OrdinalIgnoreCase);
                config.Save();
            }
            if (installed != null && installed.Ca != "" && (config == null || !config.Manual)) config = installed;
            if (config == null || Array.IndexOf(args, "--configura") >= 0)
            {
                using (var setup = new SetupForm(config ?? installed))
                {
                    if (setup.ShowDialog() != DialogResult.OK) return;
                    config = setup.Result;
                    config.Manual = installed == null || !string.Equals(config.Server, installed.Server, StringComparison.OrdinalIgnoreCase);
                    config.Save();
                }
            }
            Application.Run(new MainForm(config));
        }
    }
}
