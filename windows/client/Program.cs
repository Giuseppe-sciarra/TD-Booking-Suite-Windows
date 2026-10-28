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
            if (config == null || Array.IndexOf(args, "--configura") >= 0)
            {
                using (var setup = new SetupForm(config ?? Config.Default()))
                {
                    if (setup.ShowDialog() != DialogResult.OK) return;
                    config = setup.Result;
                    config.Save();
                }
            }
            Application.Run(new MainForm(config));
        }
    }
}
