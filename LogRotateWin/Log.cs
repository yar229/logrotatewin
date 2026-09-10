using System;
using System.IO;

namespace LogRotate
{
    /// <summary>
    /// Message levels, mirroring MESS_* from log.h.
    /// </summary>
    public static class MESS
    {
        public const int REALDEBUG = 1;
        public const int DEBUG = 2;
        public const int WARN = 4;
        public const int ERROR = 5;
        public const int FATAL = 6;
    }

    /// <summary>
    /// Port of log.c.
    /// </summary>
    public static class Log
    {
        private static int _logLevel = MESS.DEBUG;
        private static TextWriter? _messageFile = null;
        private static bool _logToSyslog = false;

        public static void SetLevel(int level) => _logLevel = level;

        public static void SetMessageFile(TextWriter? f) => _messageFile = f;

        public static void ToSyslog(bool enable) => _logToSyslog = enable;

        public static int Level => _logLevel;

        private static void LogOnce(TextWriter where, int level, string format, params object?[] args)
        {
            switch (level)
            {
                case MESS.DEBUG:
                    break;
                case MESS.WARN:
                    where.Write("warning: ");
                    break;
                default:
                    where.Write("error: ");
                    break;
            }

            where.Write(format, args);
            where.Flush();
        }

        public static void Message(int level, string format, params object?[] args)
        {
            if (level >= _logLevel)
            {
                if (level < MESS.WARN)
                    LogOnce(Console.Out, level, format, args);
                else
                    LogOnce(Console.Error, level, format, args);
            }

            if (_messageFile != null)
            {
                LogOnce(_messageFile, level, format, args);
            }

            if (level == MESS.FATAL)
            {
                // Never terminate the process here: Environment.Exit would skip
                // all finally blocks (script cache cleanup, finally in Main,
                // state-file lock release). Throw instead so the caller unwinds
                // cleanly and Program.Main reports the fatal error.
                throw new InvalidOperationException(string.Format(format, args).TrimEnd());
            }
        }

        public static void OutOfMemory()
        {
            Message(MESS.ERROR, "cannot allocate memory [callsite]");
        }
    }
}