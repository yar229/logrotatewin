using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace logrotate.Tests.Integration
{
    public abstract class IntegrationTestBase : IDisposable
    {
        protected const string BaseTestDir = "c:\\1";

        public string TestDir { get; internal set;}

        public string Log { get; private set; }

        private readonly string _exePath;

        public ITestOutputHelper Output { get; private set; }

        protected IntegrationTestBase(ITestOutputHelper output)
        {   
            Output = output;

            if (!string.IsNullOrEmpty(BaseTestDir))
            {
                if (!Directory.Exists(BaseTestDir))
                    Directory.CreateDirectory(BaseTestDir);

                var testDirPath = Path.Combine(BaseTestDir, Guid.NewGuid().ToString());
                TestDir = Directory.CreateDirectory(testDirPath).FullName;
            }
            else
            {
                TestDir = TestHelpers.CreateTempDirectory();
            }
            
            _exePath = GetLogRotateExePath();
        }

        public virtual void Dispose()
        {
            TestHelpers.CleanupPath(TestDir);
        }

        public int RunLogRotate(params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _exePath;
            psi.Arguments = string.Join(" ", args);

            if (Debugger.IsAttached)
            {
                psi.Arguments += " --verbose";
            }
            
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            var sbLog = new StringBuilder();

            using (Process process = new Process())
            {
                process.StartInfo = psi;

                process.EnableRaisingEvents = true;

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        var outline = $"[OUTPUT]: {e.Data}";
                        // Write the line to your debug output immediately
                        System.Diagnostics.Debug.WriteLine(outline);
                        sbLog.AppendLine(outline);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        var errline = $"[ERROR]: {e.Data}";
                        // Write the line to your debug output immediately
                        System.Diagnostics.Debug.WriteLine(errline);
                        sbLog.AppendLine(errline);
                    }
                };

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();

                process.CancelOutputRead();
                process.CancelErrorRead();

                Log = sbLog.ToString();

                Output.WriteLine("Execution Log:");
                Output.WriteLine(Log);
                Output.WriteLine($"ExitCode: {process.ExitCode}");

                return process.ExitCode;
            }
        }

        private string GetLogRotateExePath()
        {
            // Use CodeBase instead of Location to get the actual file path (not shadow copy)
            string testAssemblyCodeBase = this.GetType().Assembly.Location;
            Uri uri = new Uri(testAssemblyCodeBase);
            string testAssemblyPath = Uri.UnescapeDataString(uri.AbsolutePath);
            string testBinDir = Path.GetDirectoryName(testAssemblyPath);

            // Navigate to solution root and find the exe
            string exePath = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", "LogRotateWin", "bin", "Debug", "net10.0", "logrotate.exe"));

            // If debug build doesn't exist, try release
            if (!File.Exists(exePath))
            {
                exePath = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", "LogRotateWin", "bin", "Release", "net10.0", "logrotate.exe"));
            }

            // If still not found, throw a helpful error
            if (!File.Exists(exePath))
            {
                throw new FileNotFoundException(
                    $"Could not find logrotate.exe. Looked in:\n" +
                    $"- {Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", "LogRotateWin", "bin", "Debug", "net10.0", "logrotate.exe"))}\n" +
                    $"- {Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", "LogRotateWin", "bin", "Release", "net10.0", "logrotate.exe"))}\n" +
                    $"Test bin directory: {testBinDir}\n" +
                    $"CodeBase: {testAssemblyCodeBase}"
                );
            }

            return exePath;
        }
    }
}
