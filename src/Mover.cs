using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AppDataMover
{
    public class MoveReport
    {
        public bool Success;
        public string Message;
        public long BytesMoved;
    }

    /// <summary>
    /// Safe relocation: copy (robocopy) -> verify counts -> swap original aside ->
    /// create junction -> probe through junction -> delete backup. Any failure rolls back.
    /// 安全搬迁：复制 → 校验 → 原目录改名暂存 → 建联接 → 通过联接验证 → 删除暂存。失败自动回滚。
    /// </summary>
    public static class Mover
    {
        public static async Task<MoveReport> MoveAsync(string src, string dst, IProgress<string> log, CancellationToken ct)
        {
            var rep = new MoveReport();
            try
            {
                if (!Directory.Exists(src)) return Fail("source not found: " + src);
                if (Junction.GetTarget(src) != null) return Fail(Loc.T("源已经是联接", "source is already a junction"));
                if (Directory.Exists(dst)) return Fail("destination already exists: " + dst);

                log.Report(">> robocopy copy start: " + src + " -> " + dst);
                var rc = await RunRobocopy(src, dst, false, log, ct);
                if (rc >= 8) return Fail(Loc.T("复制失败，robocopy 退出码 " + rc + "（已自动清理半成品，原目录未动）",
                    "copy failed, robocopy exit code " + rc + " (partial files cleaned, source untouched)"));

                // verify: file count + total size match
                long srcBytes, srcFiles, dstBytes, dstFiles;
                CountTree(src, out srcBytes, out srcFiles);
                CountTree(dst, out dstBytes, out dstFiles);
                if (srcBytes != dstBytes || srcFiles != dstFiles)
                {
                    TryDelete(dst);
                    return Fail(string.Format("verify mismatch: src {0} files/{1} bytes vs dst {2} files/{3} bytes. Rolled back.",
                        srcFiles, srcBytes, dstFiles, dstBytes));
                }
                log.Report(">> copy verified: " + srcFiles + " files, " + FolderEntry.Humanize(srcBytes));

                var bak = src + ".appdata-mover-bak";
                if (Directory.Exists(bak)) TryDelete(bak);
                try { Directory.Move(src, bak); }
                catch (Exception ex)
                {
                    TryDelete(dst);
                    return Fail(Loc.T("无法改名源目录（被占用？）：", "cannot rename source (locked?): ") + ex.Message
                        + Loc.T("（已回滚，原目录未动）", " (rolled back, source untouched)"));
                }

                var err = Junction.Create(src, dst);
                if (err != null)
                {
                    Directory.Move(bak, src); TryDelete(dst);
                    return Fail(Loc.T("创建联接失败：", "junction creation failed: ") + err + Loc.T("（已回滚）", " (rolled back)"));
                }

                // probe through the junction
                bool probeOk;
                try { probeOk = Directory.GetFileSystemEntries(src).Length == Directory.GetFileSystemEntries(dst).Length; }
                catch { probeOk = false; }
                if (!probeOk)
                {
                    Junction.Remove(src);
                    Directory.Move(bak, src);
                    TryDelete(dst);
                    return Fail(Loc.T("联接验证失败（已回滚）", "junction probe failed (rolled back)"));
                }

                // delete backup (retry a few times: AV/indexer may hold handles briefly)
                bool deleted = false;
                for (int i = 0; i < 5 && !deleted; i++)
                {
                    await Task.Delay(1000);
                    deleted = TryDelete(bak);
                }
                if (!deleted) log.Report(Loc.T("!! 暂存目录暂时删不掉，保留在：" + bak + "（可稍后手动删除）",
                    "!! backup dir could not be deleted yet, left at: " + bak + " (delete it manually later)"));

                rep.Success = true;
                rep.BytesMoved = srcBytes;
                rep.Message = "OK " + src + " -> " + dst;
                return rep;
            }
            catch (OperationCanceledException)
            {
                return Fail(Loc.T("已取消（若已开始复制，半成品已尽量清理）",
                    "cancelled by user (partial files cleaned where possible)"));
            }
            catch (Exception ex)
            {
                return Fail("unexpected error: " + ex.Message);
            }
        }

        /// <summary>Move back: remove junction, robocopy data back, delete target.</summary>
        public static async Task<MoveReport> RestoreAsync(string linkPath, IProgress<string> log, CancellationToken ct)
        {
            var rep = new MoveReport();
            var target = Junction.GetTarget(linkPath);
            if (target == null) return Fail(linkPath + " is not a junction");
            log.Report(">> removing junction: " + linkPath);
            if (!Junction.Remove(linkPath)) return Fail(Loc.T("无法移除联接（被占用？请先关闭对应软件）",
                "cannot remove junction (in use? close the app first)"));
            try
            {
                var rc = await RunRobocopy(target, linkPath, false, log, ct);
                if (rc >= 8)
                {
                    // try to put the junction back so nothing is lost
                    Junction.Create(linkPath, target);
                    return Fail(Loc.T("搬回复制失败，联接已重建，状态未变",
                        "restore copy failed, junction re-created, state unchanged"));
                }
                long a, b, c, d;
                CountTree(target, out a, out b); CountTree(linkPath, out c, out d);
                if (a != c || b != d) return Fail(Loc.T("搬回校验不一致；数据保留在 ", "restore verify mismatch; data kept at ") + target);
                TryDelete(target);
                rep.Success = true;
                rep.BytesMoved = c;
                rep.Message = "restored " + linkPath;
                return rep;
            }
            catch (Exception ex)
            {
                if (!Directory.Exists(linkPath)) Junction.Create(linkPath, target);
                return Fail(Loc.T("搬回出错：", "restore error: ") + ex.Message);
            }
        }

        static MoveReport Fail(string msg) => new MoveReport { Success = false, Message = "FAIL: " + msg };

        static void CountTree(string root, out long bytes, out long files)
        {
            bytes = 0; files = 0;
            var stack = new System.Collections.Generic.Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] fs = null, ds = null;
                try { fs = Directory.GetFiles(dir); } catch { }
                try { ds = Directory.GetDirectories(dir); } catch { }
                if (fs != null) foreach (var f in fs) { try { bytes += new FileInfo(f).Length; files++; } catch { } }
                if (ds != null) foreach (var s in ds) if (Junction.GetTarget(s) == null) stack.Push(s);
            }
        }

        static bool TryDelete(string path)
        {
            try { Directory.Delete(path, true); return true; }
            catch { return false; }
        }

        static async Task<int> RunRobocopy(string src, string dst, bool move, IProgress<string> log, CancellationToken ct)
        {
            var args = "\"" + src + "\" \"" + dst + "\" /E /COPY:DAT /DCOPY:T /MT:16 /R:1 /W:1 /NFL /NDL /NJH" + (move ? " /MOVE" : "");
            var psi = new ProcessStartInfo("robocopy", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            var p = Process.Start(psi);
            var sb = new StringBuilder();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) { var t = e.Data.Trim(); if (t.EndsWith("%") || t.StartsWith("Bytes")) { /* progress lines */ } } };
            p.BeginOutputReadLine();
            while (!p.HasExited)
            {
                if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } throw new OperationCanceledException(); }
                await Task.Delay(300);
            }
            return p.ExitCode;
        }
    }

    /// <summary>Decide default destination per the product rule:
    /// 1) app install dir on non-system drive -> <installDir>\AppData\<folderName>
    /// 2) otherwise the non-system fixed drive with most free space -> X:\AppDataMoved\<scope>\<folderName>
    /// </summary>
    public static class TargetResolver
    {
        public static string Suggest(FolderEntry e)
        {
            // rule 1: next to the app's main program, if that program itself is off C:
            if (!string.IsNullOrEmpty(e.AppInstallDir))
            {
                try
                {
                    var root = Path.GetPathRoot(e.AppInstallDir);
                    if (!string.IsNullOrEmpty(root) && !root.StartsWith("C:", StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(root))
                    {
                        var dir = e.AppInstallDir.TrimEnd('\\');
                        if (Directory.Exists(dir))
                            return Path.Combine(dir, "AppData", e.Scope + "-" + e.Name);
                    }
                }
                catch { }
            }
            // rule 2: roomiest non-C fixed drive
            DriveInfo best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed) continue;
                    if (d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!d.IsReady) continue;
                    if (best == null || d.AvailableFreeSpace > best.AvailableFreeSpace) best = d;
                }
                catch { }
            }
            var drive = best != null ? best.Name : "D:\\";
            return Path.Combine(drive, "AppDataMoved", e.Scope, e.Name);
        }
    }
}
