using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace AppDataMover
{
    public static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                SelfTest.Run(args.Length > 1 ? args[1] : null);
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--movetest")
            {
                AttachConsole(-1); // winexe has no console; attach to caller's
                SelfTest.MoveTest(args.Length > 1 ? args[1] : null);
                return;
            }
            var app = new Application();
            app.Run(new MainWindow());
        }
    }
}
