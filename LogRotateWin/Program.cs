using System;
using System.Collections.Generic;
using System.IO;

namespace LogRotate
{
    public static class Program
    {
        private static void PrintUsage()
        {
            Console.Error.WriteLine("logrotate {0} - Copyright (C) 1995-2001 Red Hat, Inc.", Options.Version);
            Console.Error.WriteLine("This may be freely redistributed under the terms of "
                        + "the GNU General Public License");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage: logrotate [OPTION...] <configfile>");
            Console.Error.WriteLine("  -d, --debug               Don't do anything, just test and print debug messages");
            Console.Error.WriteLine("  -f, --force               Force file rotation");
            Console.Error.WriteLine("  -m, --mail <command>      Command to send mail (instead of `mail')");
            Console.Error.WriteLine("  -s, --state <statefile>   Path of state file");
            Console.Error.WriteLine("      --skip-state-lock     Do not lock the state file");
            Console.Error.WriteLine("      --wait-for-state-lock Wait for lock on the state file");
            Console.Error.WriteLine("  -v, --verbose             Display messages during rotation");
            Console.Error.WriteLine("  -l, --log <logfile>       Log file or 'syslog' to log to syslog");
            Console.Error.WriteLine("      --version             Display version information");
            Console.Error.WriteLine("  -?, --help                Give this help list");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Report bugs to <logrotate-devel@lists.fedorahosted.org>.");
        }

        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            finally
            {
                ProcessRunner.CleanupScriptCache();
            }
        }

        private static int Run(string[] args)
        {
            bool force = false;
            bool skipStateLock = false;
            bool waitForStateLock = false;
            bool debug = false;
            string? stateFile = null;
            string? mailCommand = null;
            string? logFile = null;
            var files = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-d":
                    case "--debug":
                        debug = true;
                        break;
                    case "-f":
                    case "--force":
                        force = true;
                        break;
                    case "-m":
                    case "--mail":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("logrotate: missing argument for {0}", arg);
                            return 2;
                        }
                        mailCommand = args[++i];
                        break;
                    case "-s":
                    case "--state":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("logrotate: missing argument for {0}", arg);
                            return 2;
                        }
                        stateFile = args[++i];
                        break;
                    case "--skip-state-lock":
                        skipStateLock = true;
                        break;
                    case "--wait-for-state-lock":
                        waitForStateLock = true;
                        break;
                    case "-v":
                    case "--verbose":
                        Log.SetLevel(MESS.DEBUG);
                        break;
                    case "-l":
                    case "--log":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("logrotate: missing argument for {0}", arg);
                            return 2;
                        }
                        logFile = args[++i];
                        break;
                    case "--version":
                        Console.WriteLine("logrotate {0}", Options.Version);
                        Console.WriteLine();
                        Console.WriteLine("    Default mail command:       {0}", Options.DefaultMailCommand);
                        Console.WriteLine("    Default compress command:   {0}", Options.DefaultCompressCommand);
                        Console.WriteLine("    Default uncompress command: {0}", Options.DefaultUncompressCommand);
                        Console.WriteLine("    Default compress extension: {0}", Options.DefaultCompressExt);
                        Console.WriteLine("    Default state file path:    {0}", Options.DefaultStateFile);
                        Console.WriteLine("    ACL support:                yes");
                        Console.WriteLine("    SELinux support:            no");
                        return 0;
                    case "-?":
                    case "--help":
                        PrintUsage();
                        return 1;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                        {
                            Console.Error.WriteLine("logrotate: bad argument {0}", arg);
                            return 2;
                        }
                        files.Add(arg);
                        break;
                }
            }

            if (files.Count == 0)
            {
                PrintUsage();
                return 1;
            }

            if (skipStateLock && waitForStateLock)
            {
                Console.Error.WriteLine("logrotate: options --skip-state-lock and"
                        + " --wait-for-state-lock are mutually exclusive");
                return 1;
            }

            if (debug)
            {
                Log.SetLevel(MESS.DEBUG);
                Log.Message(MESS.WARN, "logrotate in debug mode does nothing"
                        + " except printing debug messages!  Consider using verbose"
                        + " mode (-v) instead if this is not what you want.\n\n");
            }

            if (logFile != null)
            {
                if (logFile == "syslog")
                {
                    Log.ToSyslog(true);
                }
                else
                {
                    try
                    {
                        var logFd = new StreamWriter(logFile, false, System.Text.Encoding.UTF8) { AutoFlush = true };
                        Log.SetMessageFile(logFd);
                    }
                    catch (Exception ex)
                    {
                        Log.Message(MESS.ERROR, "error opening log file {0}: {1}\n", logFile, ex.Message);
                    }
                }
            }

            var parser = new ConfigParser();
            var defConfig = new LogInfo();
            int rc = parser.ReadAllConfigPaths(files.ToArray(), defConfig);
            if (rc != 0)
                rc = 1;

            RotateEngine.Debug = debug;
            RotateEngine.StateFile = stateFile ?? Options.DefaultStateFile;
            RotateEngine.MailCommand = mailCommand ?? Options.DefaultMailCommand;
            RotateEngine.SkipStateLock = skipStateLock;
            RotateEngine.WaitForStateLock = waitForStateLock;

            int engineRc = RotateEngine.Execute(parser.Logs, force);
            if (engineRc == 3)
                return 3;
            if (rc != 0 || engineRc != 0)
                return 1;
            return 0;
        }
    }
}