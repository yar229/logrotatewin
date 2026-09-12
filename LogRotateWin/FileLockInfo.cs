using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LogRotate
{
    /// <summary>
    /// Reports which processes are holding a file open, using the Restart
    /// Manager (rstrtmgr.dll). Windows has no POSIX open-handle semantics: a
    /// writer that keeps a log open shares nothing, so the renamed (rotated)
    /// file cannot be read for compression. Unlike Unix, the port cannot just
    /// unlink the file, so it tells the user who holds the lock instead.
    /// Requires elevation; degrades to a plain hint when unavailable.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class FileLockInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public uint dwProcessId;
            public long ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public uint ProcessId;
            public RmUniqueProcess Application;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public uint ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        private const uint RmRebootReasonNone = 0;
        private const uint ErrMoreData = 234;

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RmStartSession(out uint pSessionHandle,
            int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RmRegisterResources(uint dwSessionHandle,
            uint nFiles, string[] rgsFilenames, uint nApplications,
            IntPtr rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll", SetLastError = true)]
        private static extern uint RmGetList(uint dwSessionHandle,
            out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RmProcessInfo[] rgAffectedApps, out uint lpdwRebootReasons);

        [DllImport("rstrtmgr.dll", SetLastError = true)]
        private static extern uint RmEndSession(uint dwSessionHandle);

        /// <summary>
        /// Returns a human-readable "name (PID)" list of the processes that
        /// currently have the given file open, or null when the lock cannot be
        /// attributed (no lock, missing privileges, ...).
        /// </summary>
        public static string? FindLockingProcesses(string path)
        {
            try
            {
                var infos = new List<string>();

                uint session;
                uint rc = RmStartSession(out session, 0, new StringBuilder(64));
                if (rc != 0)
                    return null;

                try
                {
                    rc = RmRegisterResources(session, 1, new[] { path }, 0,
                        IntPtr.Zero, 0, null);
                    if (rc != 0)
                        return null;

                    uint needed = 0;
                    uint count = 0;
                    uint rebootReasons;
                    rc = RmGetList(session, out needed, ref count,
                        new RmProcessInfo[1], out rebootReasons);
                    if (rc != ErrMoreData && rc != 0)
                        return null;
                    if (needed == 0)
                        return null;

                    var infosArr = new RmProcessInfo[needed];
                    count = needed;
                    rc = RmGetList(session, out needed, ref count, infosArr,
                        out rebootReasons);
                    if (rc != 0)
                        return null;

                    for (uint i = 0; i < count; i++)
                    {
                        var p = infosArr[i];
                        string? app = p.strAppName;
                        if (string.IsNullOrWhiteSpace(app))
                        {
                            try
                            {
                                app = Process.GetProcessById((int)p.ProcessId).ProcessName;
                            }
                            catch (Exception)
                            {
                                app = "process";
                            }
                        }
                        infos.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0} (PID {1})", app, p.ProcessId));
                    }
                }
                finally
                {
                    RmEndSession(session);
                }

                return infos.Count > 0
                    ? string.Join(", ", infos)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}