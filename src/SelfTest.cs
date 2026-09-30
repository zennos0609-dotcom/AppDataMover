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
    }
}
