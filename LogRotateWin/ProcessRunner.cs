using LogRotate.Consts;
using Microsoft.VisualBasic;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LogRotate
{
    /// <summary>
    /// Result of running an external process: exit code and captured stderr.
    /// </summary>
    public sealed class ProcessResult
    {
        public int ExitCode;
        public string StdErr = string.Empty;
        public string StdOut = string.Empty;
    }

    /// <summary>
    /// Runs scripts and external commands. On Windows scripts are executed
    /// through cmd.exe (equivalent of '/bin/sh -c' on Linux).
    /// </summary>
    internal static class ProcessRunner
    {
        private static readonly object _scriptFileLock = new object();

        /// <summary>
        /// Cache of script content -> temp .cmd file path. Scripts are written
        /// once and reused for identical content; all created files are removed
        /// by CleanupScriptCache() at program shutdown.
        /// </summary>
        private static readonly Dictionary<string, string> _scriptFileCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Executes an executable with arguments. Optionally captures stderr
        /// and returns it in the result instead of forwarding to console.
        /// </summary>
        public static ProcessResult Run(string fileName, IList<string> arguments,
                                        bool redirectStdErr, string? stdinFile = null,
                                        IDictionary<string, string>? env = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardError = redirectStdErr,
                RedirectStandardOutput = false,
                CreateNoWindow = true,
            };

            foreach (var a in arguments)
                psi.ArgumentList.Add(a);

            if (stdinFile != null)
            {
                psi.RedirectStandardInput = true;
            }

            if (env != null)
            {
                foreach (var kv in env)
                    psi.Environment[kv.Key] = kv.Value;
            }

            var result = new ProcessResult();
            try
            {
                using (var proc = Process.Start(psi)!)
                {
                    if (redirectStdErr)
                    {
                        result.StdErr = proc.StandardError.ReadToEnd();
                    }
                    if (stdinFile != null)
                    {
                        using (var stdin = proc.StandardInput)
                        using (var reader = File.OpenRead(stdinFile))
                        {
                            reader.CopyTo(stdin.BaseStream);
                        }
                    }
                    proc.WaitForExit();
                    result.ExitCode = proc.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                result.ExitCode = -1;
                result.StdErr = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Deletes all cached temp script files. Called once at program exit.
        /// </summary>
        public static void CleanupScriptCache()
        {
            lock (_scriptFileLock)
            {
                if (_scriptFileCache.Any())
                {
                    Log.Message(MESS.DEBUG, "clearing script files cache\n");
                    foreach (var path in _scriptFileCache.Values)
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch
                        {
                            Log.Message(MESS.ERROR, "cannot delete temp script file: {0}\n", path);
                        }
                    }
                    _scriptFileCache.Clear();
                }
            }
        }

        /// <summary>
        /// Returns a path to a .cmd file containing the given script. Files are
        /// cached by script content: identical scripts reuse the same file.
        /// Returns null if the file cannot be created.
        /// </summary>
        private static string? GetOrCreateScriptFile(string script, string? scriptDirectory)
        {
            lock (_scriptFileLock)
            {
                string cacheKey = (scriptDirectory ?? string.Empty) + "\x1f" + script;
                if (_scriptFileCache.TryGetValue(cacheKey, out var cached))
                    return cached;

                string tempScriptFilepath = string.Empty;
                try
                {
                    string dir = scriptDirectory ?? Path.GetTempPath();
                    Directory.CreateDirectory(dir);
                    tempScriptFilepath = Path.Combine(dir, "logrotate-" + Guid.NewGuid().ToString("N") + ".cmd");
                    File.WriteAllText(tempScriptFilepath, script);
                }
                catch (Exception ex)
                {
                    Log.Message(MESS.ERROR, "cannot create temp script file {0}: {1}\n", tempScriptFilepath, ex.Message);
                    return null;
                }

                _scriptFileCache[cacheKey] = tempScriptFilepath;
                return tempScriptFilepath;
            }
        }

        public static int RunScript(string script, string? scriptDirectory = null,
                                    params (string EnvVar, string Value)[] additionalParams)
        {
            return RunScript(script, scriptDirectory, null, additionalParams);
        }

        /// <summary>
        /// Executes the given script (through cmd.exe). When an impersonation
        /// account is supplied, cmd.exe is started as that user via
        /// CreateProcessWithLogonW (LOGON_WITH_PROFILE) so the script truly
        /// runs under the 'su' account; otherwise it runs under the current
        /// account.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static int RunScript(string script, string? scriptDirectory,
                                    Impersonation.Account? runAs,
                                    params (string EnvVar, string Value)[] additionalParams)
        {
            string? tempScriptFilepath = GetOrCreateScriptFile(script, scriptDirectory);
            if (tempScriptFilepath == null)
                return 1;

            string cmd = Environment.GetEnvironmentVariable("COMSPEC")
                    ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

            var cmdargs = new StringBuilder();
            {
                cmdargs.Append("/S /C ");
                cmdargs.Append("\"");
                {
                    cmdargs.Append("\"" + tempScriptFilepath + "\" ");
                    foreach (var arg in additionalParams)
                        cmdargs.Append("\"" + (arg.Value ?? string.Empty) + "\" ");
                }
                cmdargs.Append("\"");
            }

            if (runAs == null)
                return RunScriptAsCurrentUser(cmd, cmdargs.ToString(), additionalParams);

            return RunScriptAsUser(runAs.Value, scriptDirectory, cmd, cmdargs.ToString(), additionalParams);
        }

        private static int RunScriptAsCurrentUser(string cmd, string cmdargs,
                                                  (string EnvVar, string Value)[] additionalParams)
        {
            var psi = new ProcessStartInfo(cmd, cmdargs.ToString())
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = false,
            };

            foreach (var arg in additionalParams)
            {
                if (!string.IsNullOrWhiteSpace(arg.EnvVar))
                    psi.Environment[arg.EnvVar] = arg.Value;
            }

            var result = new ProcessResult();
            try
            {
                using var proc = Process.Start(psi)!;

                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();
                Task.WaitAll(outputTask, errorTask);
                result.StdOut = outputTask.Result;
                result.StdErr = errorTask.Result;

                proc.WaitForExit();
                result.ExitCode = proc.ExitCode;

                LogScriptResult(result);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                result.ExitCode = -1;
                result.StdErr = ex.Message;
            }
            return result.ExitCode;
        }

        [SupportedOSPlatform("windows")]
        private static int RunScriptAsUser(Impersonation.Account account, string? scriptDirectory,
                                           string cmd, string cmdargs,
                                           (string EnvVar, string Value)[] additionalParams)
        {
            string dir = scriptDirectory ?? Path.GetTempPath();
            string outFile = Path.Combine(dir, "logrotate-" + Guid.NewGuid().ToString("N") + ".out");
            string errFile = Path.Combine(dir, "logrotate-" + Guid.NewGuid().ToString("N") + ".err");

            var cmdRedirect = new StringBuilder(cmdargs);
            {
                cmdRedirect.Length--; // drop trailing closing quote
                cmdRedirect.Append(" > \"").Append(outFile).Append("\" 2> \"").Append(errFile).Append("\"");
                cmdRedirect.Append("\"");
            }

            IntPtr envBlock = BuildEnvironmentBlock(account, additionalParams);
            if (envBlock == IntPtr.Zero)
                return 1;

            var result = new ProcessResult();
            try
            {
                var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
                string fullCmdLine = "\"" + cmd + "\" " + cmdRedirect.ToString();

                if (!CreateProcessWithLogonW(account.User,
                        string.IsNullOrEmpty(account.Domain) ? null : account.Domain,
                        account.Password, LOGON_WITH_PROFILE, null, fullCmdLine,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, envBlock, IntPtr.Zero,
                        ref si, out var pi))
                {
                    result.ExitCode = -1;
                    int error = Marshal.GetLastWin32Error();
                    Log.Message(MESS.ERROR,
                        "su impersonation: CreateProcessWithLogonW failed for {0} (win32 error {1}); "
                        + "post/pretotate script did not run\n", account.User, error);
                    return -1;
                }

                WaitForSingleObject(pi.hProcess, uint.MaxValue);
                GetExitCodeProcess(pi.hProcess, out uint exitCode);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                result.ExitCode = (int)exitCode;
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.StdErr = ex.Message;
            }
            finally
            {
                Marshal.FreeHGlobal(envBlock);
            }

            result.StdOut = ReadIfExists(outFile);
            result.StdErr = ReadIfExists(errFile);
            TryDelete(outFile);
            TryDelete(errFile);

            LogScriptResult(result);
            return result.ExitCode;
        }

        private static string ReadIfExists(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private static void LogScriptResult(ProcessResult result)
        {
            if (!string.IsNullOrEmpty(result.StdOut))
                Log.Message(MESS.DEBUG, "process execution STDOUT log: {0}\n", result.StdOut);
            if (!string.IsNullOrEmpty(result.StdErr))
                Log.Message(MESS.ERROR, "process execution STDERR log: {0}\n", result.StdErr);
        }

        // ===================================================================
        // impersonated process launch (CreateProcessWithLogonW)
        // ===================================================================

        private const int LOGON_WITH_PROFILE = 0x00000001;
        private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int CREATE_NO_WINDOW = 0x08000000;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessWithLogonW(string lpUsername, string? lpDomain,
            string lpPassword, int dwLogonFlags, string? lpApplicationName, string lpCommandLine,
            int dwCreationFlags, IntPtr lpEnvironment, IntPtr lpCurrentDirectory,
            ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LogonUser(string lpszUsername, string lpszDomain,
            string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment,
            IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hObject, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        /// <summary>
        /// Builds the environment block for the impersonated process: the
        /// block of the account itself (CreateEnvironmentBlock on a LOGON32_
        /// LOGON_BATCH token) merged with the additional env vars (LOG,
        /// LOG_ROTATED, ...). Returns an unmanaged buffer to be freed with
        /// Marshal.FreeHGlobal, or IntPtr.Zero on failure.
        /// </summary>
        private static IntPtr BuildEnvironmentBlock(Impersonation.Account account,
                                                    (string EnvVar, string Value)[] additionalParams)
        {
            if (!LogonUser(account.User,
                    string.IsNullOrEmpty(account.Domain) ? "." : account.Domain,
                    account.Password, LOGON32_LOGON_BATCH, 0, out IntPtr token))
            {
                int error = Marshal.GetLastWin32Error();
                Log.Message(MESS.ERROR,
                    "su impersonation: cannot log on {0}\\{1} for env (win32 error {2})\n",
                    account.Domain, account.User, error);
                return IntPtr.Zero;
            }

            if (!CreateEnvironmentBlock(out IntPtr baseBlock, token, false))
            {
                int error = Marshal.GetLastWin32Error();
                Log.Message(MESS.ERROR,
                    "su impersonation: CreateEnvironmentBlock failed for {0} (win32 error {1})\n",
                    account.User, error);
                CloseHandle(token);
                return IntPtr.Zero;
            }

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in EnvBlockEntries(baseBlock))
            {
                int sep = entry.IndexOf('=');
                if (sep <= 0) continue;
                merged[entry.Substring(0, sep)] = entry.Substring(sep + 1);
            }
            DestroyEnvironmentBlock(baseBlock);

            foreach (var (envVar, value) in additionalParams)
            {
                if (!string.IsNullOrWhiteSpace(envVar))
                    merged[envVar] = value;
            }

            var sb = new StringBuilder();
            foreach (var kv in merged)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');

            byte[] bytes = Encoding.Unicode.GetBytes(sb.ToString());
            IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            CloseHandle(token);
            return buffer;
        }

        private static IEnumerable<string> EnvBlockEntries(IntPtr block)
        {
            var entries = new List<string>();
            var cur = new StringBuilder();
            int index = 0;
            for (;;)
            {
                char c = (char)Marshal.ReadInt16(block, index * 2);
                if (c == '\0')
                {
                    if (cur.Length == 0) break;
                    entries.Add(cur.ToString());
                    cur.Length = 0;
                }
                else
                {
                    cur.Append(c);
                }
                index++;
            }
            return entries;
        }

        private const int LOGON32_LOGON_BATCH = 4;

    }
}