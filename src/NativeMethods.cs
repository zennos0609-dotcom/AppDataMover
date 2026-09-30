using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppDataMover
{
    /// <summary>Directory junctions: create/remove via mklink (no admin needed), read target via FSCTL.</summary>
    public static class Junction
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_ALL = 0x7;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        public static string GetTarget(string path)
        {
            if (!Directory.Exists(path)) return null;
            var h = CreateFile(path, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h == INVALID_HANDLE) return null;
            try
            {
                var buf = new byte[16384];
                uint returned;
                if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buf, (uint)buf.Length, out returned, IntPtr.Zero))
                    return null;
                uint tag = BitConverter.ToUInt32(buf, 0);
                if (tag != IO_REPARSE_TAG_MOUNT_POINT) return null;
                ushort subsNameOffset = BitConverter.ToUInt16(buf, 8);
                ushort subsNameLength = BitConverter.ToUInt16(buf, 10);
                ushort printNameOffset = BitConverter.ToUInt16(buf, 12);
                ushort printNameLength = BitConverter.ToUInt16(buf, 14);
                string raw = Encoding.Unicode.GetString(buf, 16 + printNameOffset, printNameLength);
                if (string.IsNullOrEmpty(raw))
                    raw = Encoding.Unicode.GetString(buf, 16 + subsNameOffset, subsNameLength);
                // strip \??\ prefix
                if (raw.StartsWith("\\??\\")) raw = raw.Substring(4);
                return raw;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Create a junction. Returns null on success, error message on failure.</summary>
        public static string Create(string linkPath, string target)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + linkPath + "\" \"" + target + "\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            var p = System.Diagnostics.Process.Start(psi);
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(linkPath) ? null : (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
        }

        /// <summary>Remove the junction link itself (target untouched).</summary>
        public static bool Remove(string linkPath)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c rmdir \"" + linkPath + "\"")
            { CreateNoWindow = true, UseShellExecute = false };
            var p = System.Diagnostics.Process.Start(psi);
            p.WaitForExit();
            return !Directory.Exists(linkPath) || GetTarget(linkPath) == null;
        }
    }

    public class LockingProcess
    {
        public int Pid;
        public string Name;
        public string ExePath;
        public override string ToString() => Name + " (PID " + Pid + ")";
    }

    /// <summary>Restart Manager: find which processes lock a path, and ask them to shut down.</summary>
    public static class LockChecker
    {
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, IntPtr rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
            ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

        [DllImport("rstrtmgr.dll")]
        static extern int RmShutdown(uint dwSessionHandle, int lActionFlags, IntPtr fnStatus);

        [StructLayout(LayoutKind.Sequential)]
        struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        public static List<LockingProcess> WhoLocks(string path)
        {
            var result = new List<LockingProcess>();
            uint session;
            string key = Guid.NewGuid().ToString();
            if (RmStartSession(out session, 0, key) != 0) return result;
            try
            {
                string[] res = { path };
                if (RmRegisterResources(session, 1, res, 0, IntPtr.Zero, 0, null) != 0) return result;
                uint needed = 0, count = 0, reboot = 0;
                int rc = RmGetList(session, out needed, ref count, null, ref reboot);
                if (needed == 0) return result;
                count = needed;
                var infos = new RM_PROCESS_INFO[count];
                rc = RmGetList(session, out needed, ref count, infos, ref reboot);
                if (rc != 0) return result;
                for (int i = 0; i < count; i++)
                {
                    string exe = null;
                    try { exe = System.Diagnostics.Process.GetProcessById(infos[i].Process.dwProcessId).MainModule.FileName; } catch { }
                    result.Add(new LockingProcess { Pid = infos[i].Process.dwProcessId, Name = infos[i].strAppName, ExePath = exe });
                }
            }
            finally { RmEndSession(session); }
            return result;
        }

        /// <summary>Ask locking apps to shut down gracefully (RmShutdown). 0 = force.</summary>
        public static bool ShutdownLockers(string path, bool force)
        {
            uint session;
            string key = Guid.NewGuid().ToString();
            if (RmStartSession(out session, 0, key) != 0) return false;
            try
            {
                string[] res = { path };
                if (RmRegisterResources(session, 1, res, 0, IntPtr.Zero, 0, null) != 0) return false;
                return RmShutdown(session, force ? 0x1 : 0x0, IntPtr.Zero) == 0;
            }
            finally { RmEndSession(session); }
        }
    }
}
