using LogRotate.Consts;
using System;
using System.IO;

namespace LogRotate
{
    /// <summary>
    /// Compile-time-ish defaults (mirrors #defines from configure).
    /// </summary>
    public static class Options
    {
        public const string Version = "3.22.0";


// Defaults for the Windows port. External programs are looked up in
        // PATH, mirroring the original behavior of invoking gzip/gunzip.
        // Compression is done in-process (see LogRotateEngine), and mailing
        // decompresses in-process the same way (see MailSender).
        public static string DefaultCompressCommand { get; } =
            Environment.GetEnvironmentVariable(EnviromentVariables.Compress)
            ?? ""; //"gzip";

        public static string DefaultUncompressCommand { get; } =
            Environment.GetEnvironmentVariable(EnviromentVariables.Uncompress)
            ?? ""; //"gunzip";

        public static string DefaultCompressExt { get; } = ".gz";

        public static string DefaultMailCommand { get; } =
            Environment.GetEnvironmentVariable(EnviromentVariables.MailCommand)
            ?? "mail";

        public static string DefaultStateFile
        {
            get
            {
                string folderpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "logrotate");
                if (!Path.Exists(folderpath))
                    Directory.CreateDirectory(folderpath);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "logrotate", "status");
            }
        }
    }
}