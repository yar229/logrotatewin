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
        /// Cache of script content -> temp .cmd script file. Scripts are
        /// written once and reused for identical content; all created files
        /// are removed by CleanupScriptCache() at program shutdown.
        /// </summary>
        private static readonly Dictionary<string, ScriptFile> _scriptFileCache =
            new Dictionary<string, ScriptFile>(StringComparer.Ordinal);

        /// <summary>
        /// A temp .cmd script file. The write handle is kept open with
        /// FILE_SHARE_READ for the whole lifetime of the file: no other
        /// process may open it for write/delete/rename, so the file cannot be
        /// swapped between creation and execution (TOCTOU), including while it
        /// sits in the cache between runs. cmd.exe (and an impersonated child)
        /// can still open the file for reading.
        /// </summary>
        private sealed class ScriptFile : IDisposable
        {
            private readonly FileStream _handle;

            public ScriptFile(string path, FileStream handle)
            {
                Path = path;
                _handle = handle;
            }

            public string Path { get; }

            public void Dispose()
                => _handle.Dispose();
        }

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
                    Log.Message(MESS.DEBUG, $"clearing script files cache ({_scriptFileCache.Count} files)\n");
                    foreach (var scriptFile in _scriptFileCache.Values)
                    {
                        try
                        {
                            scriptFile.Dispose();
                            File.Delete(scriptFile.Path);
                        }
                        catch
                        {
                            Log.Message(MESS.ERROR, "cannot delete temp script file: {0}\n", scriptFile.Path);
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
                    return cached.Path;

                string tempScriptFilepath = string.Empty;
                try
                {
                    string dir = scriptDirectory ?? Path.GetTempPath();
                    Directory.CreateDirectory(dir);

                    // CreateNew + write through this very handle and keep it
                    // open with FILE_SHARE_READ only, so nobody else can open
                    // the file for write/delete/rename and swap the script
                    // between creation and execution (TOCTOU). The handle stays
                    // open for the file's lifetime, cache hits included.
                    var utf8 = new UTF8Encoding(false);
                    for (int attempt = 0; ; attempt++)
                    {
                        tempScriptFilepath = Path.Combine(dir,
                            "logrotate-" + Guid.NewGuid().ToString("N") + ".cmd");
                        try
                        {
                            var handle = new FileStream(tempScriptFilepath, FileMode.CreateNew,
                                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                            try
                            {
                                byte[] bytes = utf8.GetBytes(script);
                                handle.Write(bytes, 0, bytes.Length);
                                handle.Flush(flushToDisk: true);
                            }
                            catch
                            {
                                handle.Dispose();
                                throw;
                            }
                            _scriptFileCache[cacheKey] = new ScriptFile(tempScriptFilepath, handle);
                            return tempScriptFilepath;
                        }
                        catch (IOException) when (attempt < 10 && File.Exists(tempScriptFilepath))
                        {
                            // collision on the random name; pick another one
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Message(MESS.ERROR, "cannot create temp script file {0}: {1}\n",
                        tempScriptFilepath, ex.Message);
                    return null;
                }
            }
        }

        public static int RunScript(string script, string? scriptDirectory = null,
                                    params (string EnvVar, string Value)[] additionalParams)
        {
            return RunScript(script, scriptDirectory, null, IntPtr.Zero, additionalParams);
        }

        /// <summary>
        /// Executes the given script (through cmd.exe). When an impersonation
        /// account is supplied, cmd.exe is started as that user via
        /// CreateProcessWithLogonW (LOGON_WITH_PROFILE) so the script truly
        /// runs under the 'su' account; otherwise it runs under the current
        /// account. <paramref name="runAsToken"/> is the logon token for
        /// <paramref name="runAs"/> opened by Impersonation.Run (reused to
        /// build the child's environment block instead of logging on twice);
        /// IntPtr.Zero when no impersonation applies.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static int RunScript(string script, string? scriptDirectory,
                                    Impersonation.Account? runAs, IntPtr runAsToken,
                                    params (string EnvVar, string Value)[] additionalParams)
        {
            string? tempScriptFilepath = GetOrCreateScriptFile(script, scriptDirectory);
            if (tempScriptFilepath == null)
                return 1;

            for (int i = 0; i < additionalParams.Length; i++)
            {
                string value = additionalParams[i].Value;
                if (string.IsNullOrEmpty(value))
                    continue;
                if (value.IndexOf('"') >= 0)
                {
                    Log.Message(MESS.WARN,
                        "script argument \"{0}\" contains a double quote that cmd.exe cannot "
                        + "pass through; quotes will be removed\n", additionalParams[i].EnvVar);
                    additionalParams[i] = (additionalParams[i].EnvVar,
                        value.Replace("\"", string.Empty));
                }
            }

            string cmd = Environment.GetEnvironmentVariable("COMSPEC")
                    ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

            string cmdargs = BuildScriptInvocation(tempScriptFilepath, additionalParams);

            if (runAs == null)
                return RunScriptAsCurrentUser(cmd, cmdargs, additionalParams);

            return RunScriptAsUser(runAs.Value, scriptDirectory, cmd, cmdargs, runAsToken, additionalParams);
        }

        /// <summary>
        /// Builds the /S /C command line that runs a temp script and forwards
        /// its arguments. Values are never placed on the command line: each
        /// argument is a %ENVVAR% reference that cmd.exe expands from the
        /// child-process environment (set separately on the process). cmd
        /// expands each %NAME% a single time and does not re-scan the
        /// substituted text, so meta characters (&amp; | &lt; &gt; ^ ( )) inside
        /// a value stay literal within the fixed quotes and a literal '%'
        /// inside a value is not expanded. Only the script path (a constant
        /// temp file path created here) and the env-var names (code constants)
        /// ever sit on the raw command line, which blocks cmd.exe command
        /// injection from log/script values.
        /// </summary>
        private static string BuildScriptInvocation(string scriptPath,
                                                    (string EnvVar, string Value)[] additionalParams)
        {
            var sb = new StringBuilder();
            sb.Append("/S /C ");
            sb.Append('"');
            sb.Append('"').Append(scriptPath).Append('"');
            foreach (var arg in additionalParams)
            {
                if (string.IsNullOrEmpty(arg.EnvVar))
                    continue;
                if (string.IsNullOrEmpty(arg.Value))
                {
                    // an empty positional argument keeps the historical
                    // "" token so the script sees %N = "" (cmd cannot hold an
                    // empty environment variable for a %NAME% reference).
                    sb.Append(" \"\"");
                    continue;
                }
                sb.Append(" \"%").Append(arg.EnvVar).Append("%\"");
            }
            sb.Append('"');
            return sb.ToString();
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
                    psi.Environment[arg.EnvVar] = arg.Value ?? string.Empty;
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
                                           string cmd, string cmdargs, IntPtr runAsToken,
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

            IntPtr envBlock = BuildEnvironmentBlock(account, runAsToken, additionalParams);
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

                try
                {
                    uint wait = WaitForSingleObject(pi.hProcess, uint.MaxValue);
                    if (wait != 0 /* WAIT_OBJECT_0 */)
                    {
                        Log.Message(MESS.ERROR,
                            "su impersonation: WaitForSingleObject failed for {0} (result {1}); "
                            + "post/pretotate script did not run\n", account.User, wait);
                        result.ExitCode = -1;
                        return -1;
                    }

                    if (!GetExitCodeProcess(pi.hProcess, out uint exitCode))
                    {
                        int error = Marshal.GetLastWin32Error();
                        Log.Message(MESS.ERROR,
                            "su impersonation: GetExitCodeProcess failed for {0} (win32 error {1})\n",
                            account.User, error);
                        result.ExitCode = -1;
                        return -1;
                    }

                    result.ExitCode = (int)exitCode;
                }
                finally
                {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
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
        /// Marshal.FreeHGlobal, or IntPtr.Zero on failure. When
        /// <paramref name="runAsToken"/> is not IntPtr.Zero it is reused (the
        /// token was already opened by Impersonation.Run and stays valid for
        /// the duration of the call); otherwise the account is logged on here.
        /// </summary>
        private static IntPtr BuildEnvironmentBlock(Impersonation.Account account,
                                                    IntPtr runAsToken,
                                                    (string EnvVar, string Value)[] additionalParams)
        {
            bool ownsToken = false;
            IntPtr token = runAsToken;
            if (token == IntPtr.Zero)
            {
                if (!LogonUser(account.User,
                        string.IsNullOrEmpty(account.Domain) ? "." : account.Domain,
                        account.Password, LOGON32_LOGON_BATCH, 0, out token))
                {
                    int error = Marshal.GetLastWin32Error();
                    Log.Message(MESS.ERROR,
                        "su impersonation: cannot log on {0}\\{1} for env (win32 error {2})\n",
                        account.Domain, account.User, error);
                    return IntPtr.Zero;
                }
                ownsToken = true;
            }

            IntPtr baseBlock = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out baseBlock, token, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    Log.Message(MESS.ERROR,
                        "su impersonation: CreateEnvironmentBlock failed for {0} (win32 error {1})\n",
                        account.User, error);
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
                baseBlock = IntPtr.Zero;

                foreach (var (envVar, value) in additionalParams)
                {
                    if (!string.IsNullOrWhiteSpace(envVar))
                        merged[envVar] = value ?? string.Empty;
                }

                var sb = new StringBuilder();
                foreach (var kv in merged)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
                sb.Append('\0');

                byte[] bytes = Encoding.Unicode.GetBytes(sb.ToString());
                buffer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                return buffer;
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR,
                    "su impersonation: cannot build environment block for {0}: {1}\n",
                    account.User, ex.Message);
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                }
                return IntPtr.Zero;
            }
            finally
            {
                if (baseBlock != IntPtr.Zero)
                    DestroyEnvironmentBlock(baseBlock);
                if (ownsToken)
                    CloseHandle(token);
            }
        }

        private static IEnumerable<string> EnvBlockEntries(IntPtr block)
        {
            var entries = new List<string>();
            if (block == IntPtr.Zero)
                return entries;

            var cur = new StringBuilder();
            int index = 0;
            // Defensive cap: a malformed block without the terminating
            // double-null must not let the loop read past the buffer.
            const int maxChars = 1 << 20;
            while (index < maxChars)
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