using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AppDataMover
{
    /// <summary>Headless verification: AppDataMover.exe --selftest [outputFile]</summary>
    public static class SelfTest
    {
        public static void Run(string outFile)
        {
            if (string.IsNullOrEmpty(outFile))
                outFile = Path.Combine(Path.GetTempPath(), "appdata-mover-selftest.txt");
            var sb = new StringBuilder();
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var apps = Attributor.LoadInstalledApps();
            sb.AppendLine("installed apps from registry: " + apps.Count);

            var entries = Scanner.Scan(profile);
            sb.AppendLine("appdata top-level folders: " + entries.Count);
            foreach (var e in entries)
            {
                Rules.Classify(e);
                Attributor.Attribute(e, apps);
                e.SuggestedTarget = TargetResolver.Suggest(e);
            }
            // measure the 15 most suspicious quickly (all, they are small on this machine)
            foreach (var e in entries) e.SizeBytes = Scanner.Measure(e.FullPath);

            foreach (var e in entries.OrderByDescending(x => x.SizeBytes).Take(20))
            {
                sb.AppendLine(string.Format("{0,12}  {1,-22} {2,-8} {3,-12} app={4}  kind={5}  state={6}",
                    FolderEntry.Humanize(e.SizeBytes), e.Name, e.Scope, "", e.AppName ?? "-", e.Kind, e.State));
                sb.AppendLine("             target: " + e.SuggestedTarget);
                if (e.JunctionTarget != null) sb.AppendLine("             junction: " + e.JunctionTarget);
            }
            // junction read sanity
            sb.AppendLine("--- junction sanity ---");
            foreach (var e in entries.Where(x => x.JunctionTarget != null).Take(5))
                sb.AppendLine(e.FullPath + "  ->  " + e.JunctionTarget);

            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// End-to-end move test: create a dummy folder in Roaming, move it out,
        /// verify the junction, move it back, verify integrity. Safe to run anytime.
        /// AppDataMover.exe --movetest [targetDriveRoot]
        /// </summary>
        public static void MoveTest(string driveRoot)
        {
            if (string.IsNullOrEmpty(driveRoot)) driveRoot = "D:\\";
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var src = Path.Combine(profile, "AppData", "Roaming", "__appdata_mover_test__");
            var dst = Path.Combine(driveRoot, "AppDataMoved", "Roaming", "__appdata_mover_test__");
            var log = new ActionProgress(s => Console.WriteLine("  " + s));

            try
            {
                // 1. create dummy data
                if (Directory.Exists(src)) Directory.Delete(src, true);
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
                File.WriteAllText(Path.Combine(src, "hello.txt"), "appdata mover test 迁移测试");
                File.WriteAllText(Path.Combine(src, "sub", "deep", "data.bin"), new string('x', 10000));
                Console.WriteLine("1. dummy folder created: " + src);

                // 2. lock detection sanity: open a file, expect ourselves to show up
                using (var fs = File.Open(Path.Combine(src, "hello.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var lockers = LockChecker.WhoLocks(src);
                    bool foundSelf = lockers.Any(l => l.Pid == System.Diagnostics.Process.GetCurrentProcess().Id);
                    Console.WriteLine("2. lock detection: found " + lockers.Count + " locker(s), self-detected=" + foundSelf);
                    if (!foundSelf) throw new Exception("lock detection failed to find our own open handle");
                }

                // 3. move out
                var rep = Mover.MoveAsync(src, dst, log, System.Threading.CancellationToken.None).Result;
                Console.WriteLine("3. move: " + rep.Message);
                if (!rep.Success) throw new Exception("move failed");

                // 4. verify junction + content through original path
                var jt = Junction.GetTarget(src);
                Console.WriteLine("4. junction: " + src + " -> " + jt);
                if (jt == null || !jt.Equals(dst, StringComparison.OrdinalIgnoreCase)) throw new Exception("junction target wrong");
                var throughJunction = File.ReadAllText(Path.Combine(src, "sub", "deep", "data.bin"));
                if (throughJunction.Length != 10000) throw new Exception("content mismatch through junction");
                Console.WriteLine("   content readable through junction: OK");

                // 5. move back
                var rep2 = Mover.RestoreAsync(src, log, System.Threading.CancellationToken.None).Result;
                Console.WriteLine("5. restore: " + rep2.Message);
                if (!rep2.Success) throw new Exception("restore failed");
                if (Junction.GetTarget(src) != null) throw new Exception("junction still present after restore");
                if (!File.Exists(Path.Combine(src, "hello.txt"))) throw new Exception("file missing after restore");
                if (Directory.Exists(dst)) throw new Exception("target dir not cleaned after restore");

                Console.WriteLine("MOVETEST PASS");
            }
            catch (Exception ex)
            {
                Console.WriteLine("MOVETEST FAIL: " + ex.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                try { if (Directory.Exists(src) && Junction.GetTarget(src) == null) Directory.Delete(src, true); } catch { }
                try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch { }
            }
        }

        class ActionProgress : IProgress<string>
        {
            readonly Action<string> _a;
            public ActionProgress(Action<string> a) { _a = a; }
            public void Report(string v) { _a(v); }
        }
    }
}
