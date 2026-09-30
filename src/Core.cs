using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AppDataMover
{
    public enum FolderKind { Unknown, Cache, AppData, SystemProtected, Special }
    public enum MoveState { Normal, AlreadyMoved, Moving, Moved, Failed }

    public class FolderEntry
    {
        public string FullPath;        // e.g. C:\Users\x\AppData\Roaming\Adobe
        public string Name;            // folder name
        public string Scope;           // Roaming / Local / LocalLow
        public long SizeBytes = -1;    // -1 = not measured yet
        public FolderKind Kind = FolderKind.Unknown;
        public string KindReason;      // why classified this way
        public string AppName;         // matched installed app
        public string AppInstallDir;   // app's install location (from registry)
        public string JunctionTarget;  // non-null when this path is already a junction
        public MoveState State = MoveState.Normal;
        public string SuggestedTarget; // computed default destination

        public string SizeText => SizeBytes < 0 ? "…" : Humanize(SizeBytes);
        public static string Humanize(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0.0") + " KB";
            return b + " B";
        }
    }

    public static class Scanner
    {
        public static List<FolderEntry> Scan(string profileRoot)
        {
            var roots = new[]
            {
                Tuple.Create(Path.Combine(profileRoot, "AppData", "Roaming"), "Roaming"),
                Tuple.Create(Path.Combine(profileRoot, "AppData", "Local"), "Local"),
                Tuple.Create(Path.Combine(profileRoot, "AppData", "LocalLow"), "LocalLow"),
            };
            var list = new List<FolderEntry>();
            foreach (var r in roots)
            {
                if (!Directory.Exists(r.Item1)) continue;
                foreach (var dir in Directory.GetDirectories(r.Item1))
                {
                    var e = new FolderEntry { FullPath = dir, Name = Path.GetFileName(dir), Scope = r.Item2 };
                    var target = Junction.GetTarget(dir);
                    if (target != null)
                    {
                        e.JunctionTarget = target;
                        e.State = MoveState.AlreadyMoved;
                    }
                    list.Add(e);
                }
            }
            return list;
        }

        /// <summary>Measure one directory. Returns total bytes. Best-effort, skips access-denied.</summary>
        public static long Measure(string path)
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files = null, subs = null;
                try { files = Directory.GetFiles(dir); } catch { }
                try { subs = Directory.GetDirectories(dir); } catch { }
                if (files != null)
                    foreach (var f in files)
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                if (subs != null)
                    foreach (var s in subs)
                    {
                        // do not follow junctions into other drives
                        if (Junction.GetTarget(s) == null) stack.Push(s);
                    }
            }
            return total;
        }

        public static Task MeasureAsync(IList<FolderEntry> entries, Action<FolderEntry> onOneDone)
        {
            return Task.Run(() =>
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = 4 };
                Parallel.ForEach(entries, opts, e =>
                {
                    e.SizeBytes = Measure(e.FullPath);
                    onOneDone?.Invoke(e);
                });
            });
        }
    }

    public class InstalledApp
    {
        public string Name, Publisher, InstallLocation;
    }

    /// <summary>Match AppData folders to installed software via uninstall registry keys.</summary>
    public static class Attributor
    {
        public static List<InstalledApp> LoadInstalledApps()
        {
            var apps = new List<InstalledApp>();
            var hives = new[]
            {
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            foreach (var h in hives)
            {
                try
                {
                    using (var key = h.Item1.OpenSubKey(h.Item2))
                    {
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = key.OpenSubKey(sub))
                                {
                                    var name = sk?.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(name)) continue;
                                    apps.Add(new InstalledApp
                                    {
                                        Name = name,
                                        Publisher = sk.GetValue("Publisher") as string ?? "",
                                        InstallLocation = sk.GetValue("InstallLocation") as string ?? ""
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return apps;
        }

        static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        public static void Attribute(FolderEntry e, List<InstalledApp> apps)
        {
            var fn = Norm(e.Name);
            if (fn.Length == 0) return;
            InstalledApp best = null; int bestScore = 0;
            foreach (var a in apps)
            {
                var an = Norm(a.Name); var pub = Norm(a.Publisher);
                int score = 0;
                if (an.Length >= 3 && fn == an) score = 100;
                else if (an.Length >= 3 && (fn.Contains(an) || an.Contains(fn))) score = 60 + Math.Min(an.Length, fn.Length);
                else if (pub.Length >= 4 && (fn.Contains(pub) || pub.Contains(fn))) score = 40;
                // token-level: folder may be "Adobe" while app is "Adobe Premiere Pro 2025"
                if (score == 0)
                {
                    foreach (var tok in Regex.Split(a.Name ?? "", @"\W+"))
                    {
                        var t = Norm(tok);
                        if (t.Length >= 4 && fn == t) { score = 70; break; }
                        if (t.Length >= 5 && fn.StartsWith(t)) { score = Math.Max(score, 50); }
                    }
                }
                if (score > bestScore) { bestScore = score; best = a; }
            }
            if (best != null)
            {
                e.AppName = best.Name;
                e.AppInstallDir = best.InstallLocation;
            }
            else
            {
                // registry matching failed; fall back to the built-in known-folder table
                // (covers per-user installs that hide from the uninstall keys, Chinese and
                // international software alike). Label only: install dir stays unknown,
                // so the default target falls back to the roomiest drive.
                string label;
                if (KnownApps.TryGetValue(fn, out label)) e.AppName = label;
            }
        }

        /// <summary>Folder name (normalized: lowercase, letters/digits only) -> display label.</summary>
        static readonly Dictionary<string, string> KnownApps = new Dictionary<string, string>
        {
            // Chinese software
            { "tencent", "腾讯 Tencent" }, { "wechat", "微信 WeChat" }, { "weixin", "微信 WeChat" },
            { "qq", "腾讯 QQ" }, { "kingsoft", "金山 Kingsoft / WPS" }, { "wps", "WPS Office" },
            { "baidu", "百度 Baidu" }, { "netease", "网易 NetEase" }, { "bytedance", "字节跳动 ByteDance" },
            { "alibaba", "阿里巴巴 Alibaba" }, { "alipay", "支付宝 Alipay" }, { "sogou", "搜狗 Sogou" },
            { "duowan", "YY 语音 duowan" }, { "dingtalk", "钉钉 DingTalk" }, { "feishu", "飞书 Feishu" },
            // International software
            { "google", "Google" }, { "mozilla", "Mozilla Firefox" }, { "adobe", "Adobe" },
            { "autodesk", "Autodesk" }, { "zoom", "Zoom" }, { "nvidia", "NVIDIA" },
            { "spotify", "Spotify" }, { "discord", "Discord" }, { "slack", "Slack" },
            { "telegramdesktop", "Telegram" }, { "whatsapp", "WhatsApp" }, { "signal", "Signal" },
            { "obsstudio", "OBS Studio" }, { "notion", "Notion" }, { "figma", "Figma" },
            { "epicgameslauncher", "Epic Games" }, { "steam", "Steam" }, { "riotgames", "Riot Games" },
            { "postman", "Postman" }, { "jetbrains", "JetBrains" }, { "code", "Visual Studio Code" },
            { "cursor", "Cursor" }, { "dropbox", "Dropbox" }, { "github", "GitHub" },
        };
    }

    /// <summary>Classify a folder: cache / app data / protected / special-handling.</summary>
    public static class Rules
    {
        static readonly string[] ProtectedNames =
        {
            "microsoft", "windows", "packages", "application data", "programs",
            "connecteddevicesplatform", "comms", "credential manager", "crypto",
            "protect", "systemcertificates", "backup", "history", "temporary internet files"
        };
        static readonly string[] CacheNames =
        {
            "cache", "caches", ".cache", "cache2", "uv", "pip", "npm-cache", "pnpm-store", "pnpm-cache",
            "gpucache", "crashdumps", "temp", "d3dscache", "shadercache", "fontconfig", "thumbnails", ".thumbnails"
        };
        static readonly string[] SpecialNames = { "docker", "wsl" };

        public static void Classify(FolderEntry e)
        {
            var n = e.Name.ToLowerInvariant();
            if (SpecialNames.Contains(n))
            {
                e.Kind = FolderKind.Special;
                e.KindReason = Loc.T("内含虚拟磁盘，需要特殊迁移流程", "contains virtual disks (vhdx); needs export/import instead of a plain move");
                return;
            }
            if (ProtectedNames.Contains(n))
            {
                e.Kind = FolderKind.SystemProtected;
                e.KindReason = Loc.T("系统或商店应用数据，搬移会破坏系统", "system or UWP data; moving breaks Windows");
                return;
            }
            if (CacheNames.Contains(n) || n.EndsWith("-cache") || n.EndsWith("cache"))
            {
                e.Kind = FolderKind.Cache;
                e.KindReason = Loc.T("缓存目录，可放心迁移", "cache directory; safe to relocate or even purge");
                return;
            }
            e.Kind = FolderKind.AppData;
            e.KindReason = Loc.T("应用数据，关闭对应软件后可迁移", "application data; movable when the app is closed");
        }
    }
}
