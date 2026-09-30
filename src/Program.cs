using System;
using System.Windows;

namespace AppDataMover
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                SelfTest.Run(args.Length > 1 ? args[1] : null);
                return;
            }
            var app = new Application();
            app.Run(new MainWindow());
        }
    }
}
