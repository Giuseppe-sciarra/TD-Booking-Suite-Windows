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
            // 2.10.1: on Un PC solo nothing else is ever opened; and an address "chosen by hand" that is this very PC
            // (from before, when it was a server or a workstation of itself) follows the server installed here
            if (installed != null && installed.Ca != "" && (config == null || !config.Manual || installed.Standalone || config.ThisPc))
            {
                if (config != null && config.Manual && !string.Equals(config.Server, installed.Server, StringComparison.OrdinalIgnoreCase))
                {
                    config.Server = installed.Server; config.Ca = installed.Ca; config.Manual = false; config.Save();
                }
                config = installed;
            }
            if (config == null || Array.IndexOf(args, "--configura") >= 0 && !(installed != null && installed.Standalone))
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
