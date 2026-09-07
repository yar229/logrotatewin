using LogRotate.Consts;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public static class ProcessRunner
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
                    foreach (var path in _scriptFileCache.Values)
                    {
                        Log.Message(MESS.DEBUG, "clearing script files cache\n");
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
        private static string? GetOrCreateScriptFile(string script)
        {
            lock (_scriptFileLock)
            {
                if (_scriptFileCache.TryGetValue(script, out var cached))
                    return cached;

                string tempScriptFilepath = string.Empty;
                try
                {
                    string temp_path_orig = Path.GetTempFileName();
                    tempScriptFilepath = Path.ChangeExtension(temp_path_orig, "cmd");
                    File.Delete(temp_path_orig);

                    File.WriteAllText(tempScriptFilepath, script);
                }
                catch (Exception ex)
                {
                    Log.Message(MESS.ERROR, "cannot create temp script file {0}: {1}\n", tempScriptFilepath, ex.Message);
                    return null;
                }

                _scriptFileCache[script] = tempScriptFilepath;
                return tempScriptFilepath;
            }
        }

        public static int RunScript(string script, params (string EnvVar, string Value)[] additionalParams)
        {
            string? tempScriptFilepath = GetOrCreateScriptFile(script);
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

                if (!string.IsNullOrEmpty(result.StdOut))
                    Log.Message(MESS.DEBUG, "process execution STDOUT log: {0}\n", result.StdOut);
                if (!string.IsNullOrEmpty(result.StdErr))
                    Log.Message(MESS.ERROR, "process execution STDERR log: {0}\n", result.StdErr);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                result.ExitCode = -1;
                result.StdErr = ex.Message;
            }
            return result.ExitCode;
        }

    }
}