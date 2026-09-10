using LogRotate.Consts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace LogRotate
{
    /// <summary>
    /// Mirrors struct logNames from logrotate.c.
    /// </summary>
    internal sealed class LogNames
    {
        public string? FirstRotated;
        public string? DisposeName;
        public string? FinalName;
        public string? DirName;
        public string? BaseName;
    }

    /// <summary>
    /// Port of logrotate.c: rotation engine, state handling, compression,
    /// mailing, shredding and scripts.
    /// </summary>
    public static class RotateEngine
    {
        public static bool Debug;
        public static string MailCommand = Options.DefaultMailCommand;
        public static string StateFile = Options.DefaultStateFile;
        public static bool SkipStateLock;
        public static bool WaitForStateLock;

        private static readonly StateManager States = new StateManager();
        private static FileStream? _lockFile;

        private static DateTime _now;
        private static long _nowEpoch;

        public const long DAY_SECONDS = 86400;
        public const long SECONDS_IN_YEAR = 31556926;

        // =================================================================
        // time helpers
        // =================================================================

        /// <summary>
        /// Port of mktime(): normalize a RotatedTime (handles mday 0,
        /// negative hours, month overflow) and return epoch seconds (local).
        /// </summary>
        private static long MktimeSeconds(RotatedTime t)
        {
            DateTime baseDate;
            try
            {
                baseDate = new DateTime(t.Year + 1900, 1, 1, 0, 0, 0, DateTimeKind.Local);
            }
            catch
            {
                return 0;
            }
            DateTime dt;
            try
            {
                dt = baseDate
                    .AddMonths(t.Mon)
                    .AddDays(t.MDay - 1)
                    .AddHours(t.Hour)
                    .AddMinutes(t.Min)
                    .AddSeconds(t.Sec);
            }
            catch
            {
                return 0;
            }
            return new DateTimeOffset(dt).ToUnixTimeSeconds();
        }

        private static RotatedTime FromEpoch(long epoch)
        {
            DateTime dt;
            try
            {
                dt = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
            }
            catch
            {
                return new RotatedTime();
            }
            return RotatedTime.FromDateTime(dt);
        }

        private static long MktimeFromDateOnly(RotatedTime src)
        {
            var tmp = new RotatedTime
            {
                Year = src.Year,
                Mon = src.Mon,
                MDay = src.MDay,
                Hour = 0,
                Min = 0,
                Sec = 0,
                WDay = src.WDay,
                IsDst = src.IsDst,
            };
            return MktimeSeconds(tmp);
        }

        private static long DaysElapsed(RotatedTime now, RotatedTime last)
        {
            return (MktimeFromDateOnly(now) - MktimeFromDateOnly(last)) / DAY_SECONDS;
        }

        private static string Strftime(string fmt, DateTime dt)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < fmt.Length)
            {
                char c = fmt[i];
                if (c == '%' && i + 1 < fmt.Length)
                {
                    char sp = fmt[i + 1];
                    i += 2;
                    switch (sp)
                    {
                        case 'Y': sb.Append(dt.Year.ToString("D4", CultureInfo.InvariantCulture)); break;
                        case 'y': sb.Append((dt.Year % 100).ToString("D2", CultureInfo.InvariantCulture)); break;
                        case 'm': sb.Append(dt.Month.ToString("D2", CultureInfo.InvariantCulture)); break;
                        case 'd': sb.Append(dt.Day.ToString("D2", CultureInfo.InvariantCulture)); break;
                        case 'e': sb.Append(dt.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2, ' ')); break;
                        case 'H': sb.Append(dt.Hour.ToString("D2", CultureInfo.InvariantCulture)); break;
                        case 'I':
                        {
                            int h = dt.Hour % 12;
                            if (h == 0) h = 12;
                            sb.Append(h.ToString("D2", CultureInfo.InvariantCulture));
                            break;
                        }
                        case 'M': sb.Append(dt.Minute.ToString("D2", CultureInfo.InvariantCulture)); break;
                        case 'S': sb.Append(dt.Second.ToString("D2", CultureInfo.InvariantCulture)); break;
                        case 'j': sb.Append(dt.DayOfYear.ToString("D3", CultureInfo.InvariantCulture)); break;
                        case 'u':
                        {
                            int d = (int)dt.DayOfWeek;
                            sb.Append(d == 0 ? '7' : (char)('0' + d));
                            break;
                        }
                        case 'w': sb.Append(((int)dt.DayOfWeek).ToString(CultureInfo.InvariantCulture)); break;
                        case 'a': sb.Append(dt.ToString("ddd", CultureInfo.InvariantCulture)); break;
                        case 'A': sb.Append(dt.ToString("dddd", CultureInfo.InvariantCulture)); break;
                        case 'b':
                        case 'h': sb.Append(dt.ToString("MMM", CultureInfo.InvariantCulture)); break;
                        case 'B': sb.Append(dt.ToString("MMMM", CultureInfo.InvariantCulture)); break;
                        case 'p': sb.Append(dt.ToString("tt", CultureInfo.InvariantCulture).ToUpperInvariant()); break;
                        case 's': sb.Append(new DateTimeOffset(dt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)); break;
                        case 'V': sb.Append(ISOWeek.GetWeekOfYear(dt).ToString("D2", CultureInfo.InvariantCulture)); break;
                        case 'z':
                        {
                            var off = TimeZoneInfo.Local.GetUtcOffset(dt);
                            sb.Append(off.Ticks < 0 ? '-' : '+');
                            sb.Append(Math.Abs(off.Hours).ToString("D2", CultureInfo.InvariantCulture));
                            sb.Append(Math.Abs(off.Minutes).ToString("D2", CultureInfo.InvariantCulture));
                            break;
                        }
                        case 'Z': sb.Append("LOCAL"); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case '%': sb.Append('%'); break;
                        case 'T': sb.Append(dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)); break;
                        case 'D': sb.Append(dt.ToString("MM/dd/yy", CultureInfo.InvariantCulture)); break;
                        case 'F': sb.Append(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
                        case 'R': sb.Append(dt.ToString("HH:mm", CultureInfo.InvariantCulture)); break;
                        case 'r': sb.Append(dt.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture)); break;
                        default: sb.Append('%').Append(sp); break;
                    }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        // =================================================================
        // string helpers
        // =================================================================

        /// <summary>
        /// Port of unescape(): only \n and \\ are decoded.
        /// </summary>
        private static string Unescape(string arg)
        {
            if (arg.IndexOf('\\') < 0)
                return arg;
            var sb = new StringBuilder(arg.Length);
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\' && i + 1 < arg.Length)
                {
                    char n = arg[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// POSIX dirname() emulation (handles both / and \ on Windows).
        /// </summary>
        private static string DirName(string path)
        {
            string s = path;
            while (s.Length > 1 && (s.EndsWith("/") || s.EndsWith("\\")))
                s = s.Substring(0, s.Length - 1);
            int idx = s.LastIndexOfAny(new[] { '/', '\\' });
            if (idx < 0)
                return ".";
            if (idx == 0)
                return s.Substring(0, 1);
            return s.Substring(0, idx);
        }

        /// <summary>
        /// POSIX basename() emulation (handles both / and \ on Windows).
        /// </summary>
        private static string BaseName(string path)
        {
            string s = path;
            while (s.Length > 1 && (s.EndsWith("/") || s.EndsWith("\\")))
                s = s.Substring(0, s.Length - 1);
            int idx = s.LastIndexOfAny(new[] { '/', '\\' });
            return idx < 0 ? s : s.Substring(idx + 1);
        }

        private static bool IsNullDevice(string path)
        {
            return path == "/dev/null" || string.Equals(path, "nul", StringComparison.OrdinalIgnoreCase);
        }

        private static string ErrnoMessage(int errno)
        {
            switch (errno)
            {
                case 2: return "No such file or directory";
                case 13: return "Permission denied";
                case 21: return "Is a directory";
                case 95: return "Operation not supported";
                default: return "Error " + errno.ToString(CultureInfo.InvariantCulture);
            }
        }

        // =================================================================
        // state locking / reading / writing
        // =================================================================

        private static int LockState(string stateFilename, bool skip, bool wait)
        {
            if (IsNullDevice(stateFilename))
                return 0;

            if (!File.Exists(stateFilename))
            {
                Log.Message(MESS.DEBUG, "Creating stub state file: {0}\n", stateFilename);
                try
                {
                    using var stub = new FileStream(stateFilename, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (Exception ex)
                {
                    Log.Message(MESS.ERROR, "error creating stub state file {0}: {1}\n", stateFilename, ex.Message);
                    return 1;
                }
            }

            if (skip)
            {
                Log.Message(MESS.DEBUG, "Skip locking state file {0}\n", stateFilename);
                return 0;
            }

            FileStream fs;
            try
            {
                fs = new FileStream(stateFilename, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR, "error opening state file {0}: {1}\n", stateFilename, ex.Message);
                return 1;
            }

            /* flock() emulation: lock a byte range (like LOCK_EX|LOCK_NB).
             * Keeps fs open until process end. */
            for (;;)
            {
                try
                {
                    fs.Lock(0, 1);
                    break;
                }
                catch (IOException)
                {
                    if (wait)
                    {
                        Log.Message(MESS.DEBUG, "waiting for lock on state file {0}\n", stateFilename);
                        fs.Dispose();
                        try
                        {
                            fs = new FileStream(stateFilename, FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                        }
                        catch (Exception ex)
                        {
                            Log.Message(MESS.ERROR, "error opening state file {0}: {1}\n", stateFilename, ex.Message);
                            return 1;
                        }
                        System.Threading.Thread.Sleep(1000);
                        continue;
                    }
                    Log.Message(MESS.ERROR,
                        "state file {0} is already locked\n"
                        + "logrotate does not support parallel execution on the"
                        + " same set of logfiles.\n", stateFilename);
                    fs.Dispose();
                    return 1;
                }
                catch (Exception ex)
                {
                    Log.Message(MESS.ERROR, "error acquiring lock on state file {0}: {1}\n", stateFilename, ex.Message);
                    fs.Dispose();
                    return 1;
                }
            }

            Log.Message(MESS.DEBUG, "acquired lock on state file {0}\n", stateFilename);
            _lockFile = fs;
            return 0;
        }

        private static int ReadState(string stateFilename)
        {
            Log.Message(MESS.DEBUG, "Reading state from file: {0}\n", stateFilename);

            long fileSize = 0;
            Stream? f = null;
            bool shared = _lockFile != null;
            int rc = 0;

            if (shared)
            {
                /* Windows byte-range locks block I/O from any other handle,
                 * so read through the same stream that holds the lock. */
                f = _lockFile!;
                f.Position = 0;
            }
            else
            {
                try
                {
                    f = new FileStream(stateFilename, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch (FileNotFoundException)
                {
                    if (!Debug)
                    {
                        Log.Message(MESS.ERROR, "error opening state file {0}: {1}\n", stateFilename, ErrnoMessage(2));
                        rc = 1;
                    }
                    else
                    {
                        Log.Message(MESS.DEBUG, "state file {0} does not exist\n", stateFilename);
                    }
                }
                catch (Exception ex)
                {
                    if (Debug)
                        Log.Message(MESS.ERROR, "error opening state file {0}; assuming empty state: {1}\n",
                            stateFilename, ex.Message);
                    else
                        Log.Message(MESS.ERROR, "error opening state file {0}: {1}\n", stateFilename, ex.Message);
                    rc = 1;
                }
            }

            if (f != null)
            {
                try { fileSize = f.Length; }
                catch
                {
                    Log.Message(MESS.ERROR, "error stat()ing state file {0}\n", stateFilename);
                    rc = 1;
                }
            }

            States.AllocateHash(fileSize / 80 / 200);

            if (rc != 0 || fileSize == 0)
            {
                if (!shared)
                    f?.Dispose();
                return rc;
            }

            using (var reader = new StreamReader(f, Encoding.UTF8, true, 4096, leaveOpen: shared))
            {
                int line = 0;
                string? top = reader.ReadLine();
                if (top == null)
                {
                    Log.Message(MESS.ERROR, "error reading top line of {0}\n", stateFilename);
                    return 1;
                }
                if (top != "logrotate state -- version 1" && top != "logrotate state -- version 2")
                {
                    Log.Message(MESS.ERROR, "bad top line in state file {0}\n", stateFilename);
                    return 1;
                }
                line = 1;

                string? buf;
                while ((buf = reader.ReadLine()) != null)
                {
                    line++;
                    if (buf.Length == 0)
                    {
                        Log.Message(MESS.ERROR, "line {0} not parsable in state file {1}\n", line, stateFilename);
                        return 1;
                    }
                    if (buf.Length == 1)
                        continue;

                    var args = ArgvParser.Parse(buf);
                    if (args == null || args.Count != 2)
                    {
                        Log.Message(MESS.ERROR, "bad line {0} in state file {1}\n", line, stateFilename);
                        return 1;
                    }

                    var parts = args[1].Split(new[] { '-', ':' });
                    if (parts.Length < 3 ||
                        !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int year) ||
                        !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int month) ||
                        !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day))
                    {
                        Log.Message(MESS.ERROR, "bad line {0} in state file {1}\n", line, stateFilename);
                        return 1;
                    }
                    int hour = 0, minute = 0, second = 0;
                    if (parts.Length > 3) int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour);
                    if (parts.Length > 4) int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute);
                    if (parts.Length > 5) int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out second);

                    /* Hack to hide earlier bug */
                    if ((year != 1900) && (year < 1970 || year > 2100))
                    {
                        Log.Message(MESS.ERROR, "bad year {0} for file {1} in state file {2}\n",
                            year, args[0], stateFilename);
                        return 1;
                    }
                    if (month < 1 || month > 12)
                    {
                        Log.Message(MESS.ERROR, "bad month {0} for file {1} in state file {2}\n",
                            month, args[0], stateFilename);
                        return 1;
                    }
                    if (day < 0 || day > 31)
                    {
                        Log.Message(MESS.ERROR, "bad day {0} for file {1} in state file {2}\n",
                            day, args[0], stateFilename);
                        return 1;
                    }
                    if (hour < 0 || hour > 23)
                    {
                        Log.Message(MESS.ERROR, "bad hour {0} for file {1} in state file {2}\n",
                            hour, args[0], stateFilename);
                        return 1;
                    }
                    if (minute < 0 || minute > 59)
                    {
                        Log.Message(MESS.ERROR, "bad minute {0} for file {1} in state file {2}\n",
                            minute, args[0], stateFilename);
                        return 1;
                    }
                    if (second < 0 || second > 59)
                    {
                        Log.Message(MESS.ERROR, "bad second {0} for file {1} in state file {2}\n",
                            second, args[0], stateFilename);
                        return 1;
                    }

                    var st = States.FindState2(Unescape(args[0]), States.HashSize);
                    if (st == null)
                        return 1;

                    st.LastRotated = new RotatedTime
                    {
                        Year = year - 1900,
                        Mon = month - 1,
                        MDay = day,
                        Hour = hour,
                        Min = minute,
                        Sec = second,
                        IsDst = -1,
                    };
                    /* fill in the rest of the lastRotated fields (mktime + localtime) */
                    st.LastRotated = FromEpoch(MktimeSeconds(st.LastRotated));
                }
            }
            return 0;
        }

        private static void WriteEscapedChar(StringBuilder sb, string fn)
        {
            sb.Append('"');
            foreach (var c in fn)
            {
                switch (c)
                {
                    case '"':
                    case '\\':
                        sb.Append('\\');
                        sb.Append(c);
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static int WriteState(string stateFilename)
        {
            if (IsNullDevice(stateFilename))
                return 0;

            if (!File.Exists(stateFilename))
            {
                Log.Message(MESS.ERROR, "error opening state file {0}: {1}\n", stateFilename, ErrnoMessage(2));
                return 1;
            }

            var st0 = FileStat.Stat(stateFilename);
            if (st0 == null || !FileStat.IsRegular(st0))
            {
                Log.Message(MESS.ERROR, "not writing state to {0} because it is not a regular file\n", stateFilename);
                return 1;
            }

            /* writeState(): remember the old state file's access ACL so the
             * replacement keeps it (mirrors acl_get_fd in logrotate.c). */
            byte[]? prevAcl = AclApi.ReadDacl(stateFilename);

            string tmpFilename = stateFilename + ".tmp";
            /* Remove possible tmp state file from previous run */
            if (File.Exists(tmpFilename))
            {
                int r = FileUtil.Unlink(tmpFilename, out _);
                if (r != 0)
                {
                    Log.Message(MESS.ERROR, "error removing old temporary state file {0}: {1}\n",
                        tmpFilename, ErrnoMessage(13));
                    return 1;
                }
            }

            try
            {
                using (var f = new StreamWriter(tmpFilename, false, new UTF8Encoding(false)))
                {
                    f.NewLine = "\n";
                    f.Write("logrotate state -- version 2\n");

                    foreach (var bucket in States.Buckets)
                    {
                        foreach (var p in bucket)
                        {
                            long lastTime = MktimeSeconds(p.LastRotated);
                            /* Skip states which are not used for more than a year. */
                            if (!p.IsUsed && (_nowEpoch - lastTime) > SECONDS_IN_YEAR)
                            {
                                Log.Message(MESS.DEBUG, "Removing {0} from state file, "
                                    + "because it does not exist and has not been rotated for one year\n",
                                    p.Fn);
                                continue;
                            }

                            var line = new StringBuilder();
                            WriteEscapedChar(line, p.Fn);
                            line.Append(' ').Append(p.LastRotated.Year + 1900)
                                .Append('-').Append(p.LastRotated.Mon + 1)
                                .Append('-').Append(p.LastRotated.MDay)
                                .Append('-').Append(p.LastRotated.Hour)
                                .Append(':').Append(p.LastRotated.Min)
                                .Append(':').Append(p.LastRotated.Sec);
                            f.WriteLine(line.ToString());
                        }
                    }
                    f.Flush();
                }
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR, "error creating temp state file {0}: {1}\n", tmpFilename, ex.Message);
                FileUtil.DeleteFile(tmpFilename);
                return 1;
            }

            /* carry over the old state file's access ACL to the replacement
             * (mirrors createOutputFile(tmpFilename, ..., prev_acl, force_mode)) */
            if (prevAcl != null && prevAcl.Length > 0
                    && !AclApi.WriteDacl(tmpFilename, prevAcl))
            {
                Log.Message(MESS.ERROR, "setting ACL for {0}: failed\n", tmpFilename);
            }

            if (!FileUtil.Rename(tmpFilename, stateFilename))
            {
                Log.Message(MESS.ERROR, "error renaming temp state file {0} to {1}: {2}\n",
                    tmpFilename, stateFilename, ErrnoMessage(13));
                FileUtil.DeleteFile(tmpFilename);
                return 1;
            }
            return 0;
        }

        // =================================================================
        // file helpers
        // =================================================================

        /// <summary>
        /// Port of open_logfile(): checks symlink target is a regular file and
        /// nlink == 1 unless LOG_FLAG_ALLOWHARDLINK. Returns a FileStat when OK.
        /// </summary>
        private static FileStat? OpenLogFile(string path, LogInfo log, bool writeAccess)
        {
            var sb = FileStat.Lstat(path);
            if (sb == null)
                return null;
            if (!FileStat.IsRegular(sb))
                return null;
            if (sb.Nlink != 1 && (log.Flags & LogFlags.AllowHardLink) == 0)
                return null;
            return sb;
        }

        private static FileStream? OpenStream(string path, bool writeAccess)
        {
            try
            {
                return new FileStream(path,
                    writeAccess ? FileMode.Open : FileMode.Open,
                    writeAccess ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Port of createOutputFile(): create a brand new file, renaming an
        /// existing one to "name-YYYYMMDDHH.backup" first (two attempts).
        /// </summary>
        private static FileStream? CreateOutputFile(string fileName, FileStat sb)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return new FileStream(fileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (IOException)
                {
                    if (attempt == 1)
                        break;
                    string backupName = fileName + Strftime("-yyyyMMddHH", _now) + ".backup";
                    Log.Message(MESS.ERROR, "destination {0} already exists, renaming to {1}\n",
                        fileName, backupName);
                    if (!FileUtil.Rename(fileName, backupName))
                    {
                        Log.Message(MESS.ERROR, "error renaming already existing output file"
                            + " {0} to {1}: {2}\n", fileName, backupName, ErrnoMessage(13));
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Message(MESS.ERROR, "error creating output file {0}: {1}\n", fileName, ex.Message);
                    return null;
                }
            }
            Log.Message(MESS.ERROR, "error creating output file {0}: {1}\n", fileName, ErrnoMessage(17));
            return null;
        }

        /// <summary>
        /// Port of shred_file(): run preremove script, optionally shred, unlink.
        /// </summary>
        private static int ShredFile(string filename, LogInfo log, FileStat? sb)
        {
            if (log.PreRemove != null)
                {
                    Log.Message(MESS.DEBUG, "running preremove script\n");
                    if (RunScript(log, filename, null, log.PreRemove) != 0)
                    {
                        Log.Message(MESS.ERROR,
                            "error running preremove script "
                            + "for {0} of '{1}'. Not removing this file.\n",
                            filename, log.Pattern);
                        return 1;
                    }
                }

            bool doShred = (log.Flags & LogFlags.Shred) != 0;
            if (!doShred)
                goto unlink_file;

            if ((log.Flags & LogFlags.AllowHardLink) == 0)
            {
                if (sb == null || sb.Nlink != 1)
                {
                    Log.Message(MESS.ERROR, "failed to shred \"{0}\", because shredding files with"
                            + " multiple hard links is disabled for {1}.\n",
                        filename, log.Pattern);
                    return 1;
                }
            }

            Log.Message(MESS.DEBUG, "Using shred to remove the file {0}\n", filename);

            var args = new List<string> { "-u" };
            if (log.ShredCycles != 0)
            {
                args.Add("-n");
                args.Add(log.ShredCycles.ToString(CultureInfo.InvariantCulture));
            }
            args.Add(filename);

            var res = ProcessRunner.Run("shred", args, redirectStdErr: false);
            if (res.ExitCode != 0)
            {
                Log.Message(MESS.ERROR, "Failed to shred {0}, trying unlink\n", filename);
                return FileUtil.Unlink(filename, out _);
            }

        unlink_file:
            int r = FileUtil.Unlink(filename, out int errno);
            if (r == 0)
                return 0;
            if (errno != 2) /* ENOENT */
                return 1;
            Log.Message(MESS.ERROR, "error unlinking log file {0}: {1}\n", filename, ErrnoMessage(errno));
            return 0;
        }

        /// <summary>
        /// Port of removeLogFile().
        /// </summary>
        private static int RemoveLogFile(string name, LogInfo log)
        {
            Log.Message(MESS.DEBUG, "removing old log {0}\n", name);

            FileStat? sb = null;
            if ((log.Flags & LogFlags.Shred) != 0)
            {
                sb = OpenLogFile(name, log, true);
                if (sb == null)
                {
                    Log.Message(MESS.ERROR, "error opening {0}: {1}\n", name, ErrnoMessage(2));
                    return 1;
                }
            }

            if (!Debug && ShredFile(name, log, sb) != 0)
            {
                Log.Message(MESS.ERROR, "Failed to remove old log {0}: {1}\n", name, ErrnoMessage(13));
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// Port of copyTruncate()/sparse_copy() (simple byte copy; sparse hole
        /// detection omitted on Windows).
        /// </summary>
        private static int CopyTruncate(string currLog, string saveLog, FileStat sb,
                                        LogInfo log, bool skipCopy)
        {
            Log.Message(MESS.DEBUG, "{0}copying {1} to {2}\n",
                skipCopy ? "skip " : "", currLog, saveLog);

            if (Debug)
                return 0;

            bool readOnly = (log.Flags & LogFlags.Copy) != 0 && (log.Flags & LogFlags.CopyTruncate) == 0;

            using (var fdcurr = OpenStream(currLog, !readOnly))
            {
                if (fdcurr == null)
                {
                    Log.Message(MESS.ERROR, "error opening {0}: {1}\n", currLog, ErrnoMessage(13));
                    return 1;
                }

                if (!skipCopy)
                {
                    using (var fdsave = CreateOutputFile(saveLog, sb))
                    {
                        if (fdsave == null)
                            return 1;
                        try
                        {
                            fdcurr.CopyTo(fdsave);
                        }
                        catch (Exception ex)
                        {
                            Log.Message(MESS.ERROR, "error copying {0} to {1}: {2}\n",
                                currLog, saveLog, ex.Message);
                            FileUtil.DeleteFile(saveLog);
                            return 1;
                        }
                    }
                }

                if ((log.Flags & LogFlags.CopyTruncate) != 0)
                {
                    Log.Message(MESS.DEBUG, "truncating {0}\n", currLog);
                    if (!FileUtil.Truncate(fdcurr))
                    {
                        Log.Message(MESS.ERROR, "error truncating {0}: {1}\n", currLog, ErrnoMessage(13));
                        return 1;
                    }
                }
                else
                {
                    Log.Message(MESS.DEBUG, "Not truncating {0}\n", currLog);
                }
            }
            return 0;
        }

        /// <summary>
        /// Port of compressLogFile(): run $compress_prog with the log on stdin,
        /// writing stdout into the new compressed file. stderr is captured and
        /// relayed like the C version's pump.
        /// </summary>
        private static int CompressLogFile(string name, LogInfo log, FileStat sb)
        {
            Log.Message(MESS.DEBUG, "compressing log with: {0}\n", string.IsNullOrEmpty(log.CompressProg) ? "internal gzip" : log.CompressProg);
            if (Debug)
                return 0;

            if (log.CompressProg == null)
            {
                Log.Message(MESS.ERROR, "compression enabled, but compress command is not set\n");
                return 1;
            }

            int compressResult = 0;
            if (string.IsNullOrEmpty(log.CompressProg))
            {
                compressResult = CompressWithGZipStream(name, log);
            }
            else
            {
                compressResult = CompressWithExternalCommand(name, log, sb);
            }

            
            if (compressResult != 0)
            {
                return compressResult;
            }



            /* preserve timestamps of the original log */
            try
            {
                File.SetLastWriteTimeUtc(name + log.CompressExt, sb.Mtime.Kind == DateTimeKind.Utc
                    ? sb.Mtime : sb.Mtime.ToUniversalTime());
                File.SetLastAccessTimeUtc(name + log.CompressExt, sb.Atime.Kind == DateTimeKind.Utc
                    ? sb.Atime : sb.Atime.ToUniversalTime());
            }
            catch
            {
                /* best effort */
            }

            var afterSb = FileStat.Stat(name);
            if (!Debug && ShredFile(name, log, afterSb) != 0)
                return 1;
            return 0;
        }

        /// <summary>
        /// Compress a file using built-in .NET GZipStream
        /// </summary>
        /// <param name="filepath">Source file path</param>
        /// <param name="compressedFilepath">Destination compressed file path</param>
        private static int CompressWithGZipStream(string filepath, LogInfo log)
        {
            string compressedFilepath = AddExtension(filepath, log.CompressExt);
            try
            { 
                int chunkSize = 65536;
                using (FileStream fs = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs == null)
                    {
                        Log.Message(MESS.ERROR, "unable to open {0} ({1}) for compression: {2}\n",
                            filepath, (log.Flags & LogFlags.Shred) != 0 ? "read-write" : "read-only",
                            ErrnoMessage(2));
                        return 1;
                    }

                    using (FileStream outputFs = new FileStream(compressedFilepath, FileMode.Create))
                    using (System.IO.Compression.GZipStream zs = new System.IO.Compression.GZipStream(outputFs, System.IO.Compression.CompressionMode.Compress))
                    {
                        byte[] buffer = new byte[chunkSize];
                        while (true)
                        {
                            int bytesRead = fs.Read(buffer, 0, chunkSize);
                            if (bytesRead == 0)
                                break;
                            zs.Write(buffer, 0, bytesRead);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR, "cannot compress with internal gzip: {0}\n",
                    ex.Message);
                FileUtil.DeleteFile(compressedFilepath);
                return 1;
            }
            return 0;
        }

        private static int CompressWithExternalCommand(string name, LogInfo log, FileStat sb)
        {
            using (var inFile = OpenStream(name, false))
            {
                if (inFile == null)
                {
                    Log.Message(MESS.ERROR, "unable to open {0} ({1}) for compression: {2}\n",
                        name, (log.Flags & LogFlags.Shred) != 0 ? "read-write" : "read-only",
                        ErrnoMessage(2));
                    return 1;
                }

                string compressedName = AddExtension(name, log.CompressExt);
                using (var outFile = CreateOutputFile(compressedName, sb))
                {
                    if (outFile == null)
                    {
                        return 1;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = log.CompressProg,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    foreach (var arg in log.CompressOptions)
                        psi.ArgumentList.Add(arg);
                    psi.Environment[ScriptEnviromentVariables.CompressedFilename] = name;

                    try
                    {
                        using var proc = Process.Start(psi)!;
                        var stderrTask = proc.StandardError.ReadToEndAsync();

                        // The compressor blocks when its stdout pipe is full, so
                        // stdin and stdout must be fed and drained concurrently;
                        // a write-then-read sequence deadlocks on inputs larger
                        // than the pipe buffer. stdin is closed only after the
                        // write finishes to signal EOF to the compressor.
                        var stdinTask = TaskHelper.CopyAsync(inFile, proc.StandardInput.BaseStream);
                        var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(outFile);

                        stdinTask.GetAwaiter().GetResult();
                        proc.StandardInput.Close();
                        stdoutTask.GetAwaiter().GetResult();

                        proc.WaitForExit();

                        string stderr = stderrTask.GetAwaiter().GetResult();
                        if (stderr.Length > 0)
                        {
                            Log.Message(MESS.ERROR, "Compressing program wrote following message "
                                    + "to stderr when compressing log {0}:\n", name);
                            Console.Error.Write(stderr);
                        }

                        if (proc.ExitCode != 0)
                        {
                            Log.Message(MESS.ERROR, "failed to compress log {0}\n", name);
                            outFile.Dispose();
                            FileUtil.DeleteFile(compressedName);
                            return 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Message(MESS.ERROR, "cannot execute compress command '{0}': {1}\n",
                            log.CompressProg, ex.Message);
                        outFile.Dispose();
                        FileUtil.DeleteFile(compressedName);
                        return 1;
                    }
                }
            }

            return 0;
        }

        private static string AddExtension(string filepath, string extension) 
            => Path.ChangeExtension(filepath, Path.GetExtension(filepath) + AddDotToExtension(extension));

        private static string AddDotToExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return extension;
            return extension.StartsWith('.')
                        ? extension
                        : $".{extension}";
        }

        // =================================================================
        // dateext support
        // =================================================================

        /// <summary>
        /// Port of dateConversion(): turn a user dateformat into a strptime()-able
        /// dformat plus a global-pattern dext_pattern.
        /// Returns null and prints the error if the format is too long.
        /// </summary>
        private static (string dformat, string dextPattern)? DateConversion(string dateformat)
        {
            const int patternLen = 128; /* PATTERN_LEN */
            var dformat = new StringBuilder();
            var dext = new StringBuilder();
            int k = 0;
            int len = dateformat.Length;

            while (k < len)
            {
                if (dext.Length >= patternLen - 1 || dformat.Length >= patternLen - 2)
                {
                    Log.Message(MESS.ERROR, "Date format {0} is too long\n", dateformat);
                    return null;
                }
                char ch = dateformat[k];
                if (ch == '%' && k + 1 < len)
                {
                    char nc = dateformat[k + 1];
                    switch (nc)
                    {
                        case 'Y':
                            dext.Append("[0-9][0-9]");
                            goto two_digits;
                        case 'm':
                        case 'd':
                        case 'H':
                        case 'M':
                        case 'S':
                        case 'V':
                        two_digits:
                            dext.Append("[0-9][0-9]");
                            if (dext.Length >= patternLen - 1)
                            {
                                Log.Message(MESS.ERROR, "Date format {0} is too long\n", dateformat);
                                return null;
                            }
                            dformat.Append('%').Append(nc);
                            k += 2;
                            break;
                        case 's':
                            dext.Append("[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]");
                            if (dext.Length >= patternLen - 1)
                            {
                                Log.Message(MESS.ERROR, "Date format {0} is too long\n", dateformat);
                                return null;
                            }
                            dformat.Append('%').Append(nc);
                            k += 2;
                            break;
                        case 'z':
                            dext.Append("[-+][0-9][0-9][0-9][0-9]");
                            if (dext.Length >= patternLen - 1)
                            {
                                Log.Message(MESS.ERROR, "Date format {0} is too long\n", dateformat);
                                return null;
                            }
                            dformat.Append('%').Append(nc);
                            k += 2;
                            break;
                        default:
                            dformat.Append('%').Append('%');
                            dext.Append('%');
                            k++;
                            break;
                    }
                }
                else
                {
                    dformat.Append(ch);
                    dext.Append(ch);
                    k++;
                }
            }

            if (dformat.Length == 0)
                dformat.Append('%');
            return (dformat.ToString(), dext.ToString());
        }

        // =================================================================
        // findNeedRotating
        // =================================================================

        private static int FindNeedRotating(LogInfo log, int logNum, bool force)
        {
            Log.Message(MESS.DEBUG, "considering log {0}\n", log.Files[logNum]);

            var now = RotatedTime.FromDateTime(_now);

            /* Check if parent directory of this log has safe permissions.
             * Only done when running as root without 'su' - impossible on Windows. */

            var sb = FileStat.Lstat(log.Files[logNum]);
            if (sb == null)
            {
                if ((log.Flags & LogFlags.MissingOk) != 0)
                {
                    Log.Message(MESS.DEBUG, "  log {0} does not exist -- skipping\n", log.Files[logNum]);
                    return 0;
                }
                Log.Message(MESS.ERROR, "stat of {0} failed: {1}\n", log.Files[logNum], ErrnoMessage(2));
                return 1;
            }

            var state = States.FindState(log.Files[logNum]);
            if (state == null)
                return 1;

            state.DoRotate = false;
            state.Sb = sb;
            state.IsUsed = true;

            if (FileStat.IsSymlink(sb))
            {
                Log.Message(MESS.DEBUG, "  log {0} is symbolic link. Rotation of symbolic"
                        + " links is not allowed to avoid security issues -- skipping.\n",
                    log.Files[logNum]);
                return 0;
            }

            if ((log.Flags & LogFlags.AllowHardLink) == 0 && sb.Nlink != 1)
            {
                Log.Message(MESS.DEBUG, "  log {0} has multiple ({1}) hard links. Rotation of files"
                        + " with multiple hard links is not allowed for {2} -- skipping.\n",
                    log.Files[logNum], sb.Nlink, log.Pattern);
                return 0;
            }

            Log.Message(MESS.DEBUG, "  Now: {0}-{1:D2}-{2:D2} {3:D2}:{4:D2}\n",
                1900 + now.Year, 1 + now.Mon, now.MDay, now.Hour, now.Min);
            Log.Message(MESS.DEBUG, "  Last rotated at {0}-{1:D2}-{2:D2} {3:D2}:{4:D2}\n",
                1900 + state.LastRotated.Year, 1 + state.LastRotated.Mon,
                state.LastRotated.MDay, state.LastRotated.Hour, state.LastRotated.Min);

            if (force)
            {
                state.DoRotate = true;
            }
            else if (log.MaxSize > 0 && sb.Size > log.MaxSize)
            {
                state.DoRotate = true;
            }
            else if (log.Criterium == Criterium.ROT_SIZE)
            {
                state.DoRotate = sb.Size >= log.Threshold;
                if (!state.DoRotate)
                {
                    Log.Message(MESS.DEBUG, "  log does not need rotating "
                            + "(log size is below the 'size' threshold)\n");
                }
            }
            else if (MktimeSeconds(state.LastRotated) - MktimeSeconds(now) > (25 * 3600))
            {
                /* 25 hours allows for DST changes as well as geographical moves */
                Log.Message(MESS.ERROR,
                        "log {0} last rotated in the future -- rotation forced\n",
                    log.Files[logNum]);
                state.DoRotate = true;
            }
            else if (state.LastRotated.Year != now.Year ||
                     state.LastRotated.Mon != now.Mon ||
                     state.LastRotated.MDay != now.MDay ||
                     state.LastRotated.Hour != now.Hour)
            {
                long days;
                switch (log.Criterium)
                {
                    case Criterium.ROT_WEEKLY:
                        days = DaysElapsed(now, state.LastRotated);
                        state.DoRotate = (days >= 7)
                                || (days >= 1
                                    && now.WDay == log.Weekday);
                        if (!state.DoRotate)
                        {
                            Log.Message(MESS.DEBUG, "  log does not need rotating "
                                    + "(log has been rotated at {0}-{1:D2}-{2:D2} {3:D2}:{4:D2}, "
                                    + "which is less than a week ago)\n",
                                1900 + state.LastRotated.Year, 1 + state.LastRotated.Mon,
                                state.LastRotated.MDay, state.LastRotated.Hour, state.LastRotated.Min);
                        }
                        break;
                    case Criterium.ROT_HOURLY:
                        state.DoRotate = (now.Hour != state.LastRotated.Hour) ||
                                (now.MDay != state.LastRotated.MDay) ||
                                (now.Mon != state.LastRotated.Mon) ||
                                (now.Year != state.LastRotated.Year);
                        if (!state.DoRotate)
                        {
                            Log.Message(MESS.DEBUG, "  log does not need rotating "
                                    + "(log has been rotated at {0}-{1:D2}-{2:D2} {3:D2}:{4:D2}, "
                                    + "which is less than an hour ago)\n",
                                1900 + state.LastRotated.Year, 1 + state.LastRotated.Mon,
                                state.LastRotated.MDay, state.LastRotated.Hour, state.LastRotated.Min);
                        }
                        break;
                    case Criterium.ROT_DAYS:
                        state.DoRotate = (now.MDay != state.LastRotated.MDay) ||
                                (now.Mon != state.LastRotated.Mon) ||
                                (now.Year != state.LastRotated.Year);
                        if (!state.DoRotate)
                        {
                            Log.Message(MESS.DEBUG, "  log does not need rotating "
                                    + "(log has been rotated at {0}-{1:D2}-{2:D2} {3:D2}:{4:D2}, "
                                    + "which is less than a day ago)\n",
                                1900 + state.LastRotated.Year, 1 + state.LastRotated.Mon,
                                state.LastRotated.MDay, state.LastRotated.Hour, state.LastRotated.Min);
                        }
                        break;
                    case Criterium.ROT_MONTHLY:
                        state.DoRotate = (now.Mon != state.LastRotated.Mon) ||
                                (now.Year != state.LastRotated.Year);
                        if (!state.DoRotate)
                        {
                            Log.Message(MESS.DEBUG, "  log does not need rotating "
                                    + "(log has been rotated at {0}-{1:D2}-{2:D2} {3:D2}:{4:D2}, "
                                    + "which is less than a month ago)\n",
                                1900 + state.LastRotated.Year, 1 + state.LastRotated.Mon,
                                state.LastRotated.MDay, state.LastRotated.Hour, state.LastRotated.Min);
                        }
                        break;
                    case Criterium.ROT_YEARLY:
                        state.DoRotate = now.Year != state.LastRotated.Year;
                        if (!state.DoRotate)
                        {
                            Log.Message(MESS.DEBUG, "  log does not need rotating "
                                    + "(log has been rotated at {0}-{1:D2}-{2:D2} {3:D2}:{4:D2}, "
                                    + "which is less than a year ago)\n",
                                1900 + state.LastRotated.Year, 1 + state.LastRotated.Mon,
                                state.LastRotated.MDay, state.LastRotated.Hour, state.LastRotated.Min);
                        }
                        break;
                    case Criterium.ROT_SIZE:
                    default:
                        state.DoRotate = false;
                        break;
                }
                if (sb.Size < log.MinSize)
                {
                    if (log.MinSize > 0 && state.DoRotate)
                    {
                        state.DoRotate = false;
                        Log.Message(MESS.DEBUG, "  log does not need rotating "
                                + "('minsize' directive is used and the log "
                                + "size is smaller than the minsize value)\n");
                    }
                }
                if (state.DoRotate && log.RotateMinAge > 0
                        && log.RotateMinAge * DAY_SECONDS >= (long)(_now - ToLocal(sb.Mtime)).TotalSeconds)
                {
                    state.DoRotate = false;
                    Log.Message(MESS.DEBUG, "  log does not need rotating "
                            + "('minage' directive is used and the log "
                            + "age is smaller than the minage days)\n");
                }
            }
            else if (!state.DoRotate)
            {
                Log.Message(MESS.DEBUG, "  log does not need rotating "
                        + "(log has already been rotated)\n");
            }

            /* The notifempty flag overrides the normal criteria */
            if (state.DoRotate && (log.Flags & LogFlags.IfEmpty) == 0 && sb.Size == 0)
            {
                state.DoRotate = false;
                Log.Message(MESS.DEBUG, "  log does not need rotating "
                        + "(log is empty)\n");
            }

            if (state.DoRotate)
            {
                Log.Message(MESS.DEBUG, "  log needs rotating\n");
            }

            return 0;
        }

        private static DateTime ToLocal(DateTime dt)
        {
            return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        }

        // =================================================================
        // findLastRotated (non-dateext)
        // =================================================================

        private static int FindLastRotated(LogNames rotNames, string fileext, string compext)
        {
            string pattern = string.Format(CultureInfo.InvariantCulture, "{0}.*{1}{2}",
                Path.Combine(rotNames.DirName, rotNames.BaseName), fileext, compext);

            var (rc, paths) = Glob.GlobNoCheck(pattern);
            switch (rc)
            {
                case GlobResultCode.GLOB_NOMATCH:
                    return 0;
                case GlobResultCode.GLOB_SUCCESS:
                    break;
                default:
                    return -1;
            }

            int prefixLen = rotNames.DirName!.Length + 1 + rotNames.BaseName!.Length + 1;
            int suffixLen = fileext.Length + compext.Length;
            int last = 0;
            foreach (var path in paths)
            {
                if (path.Length <= prefixLen + suffixLen)
                    continue;
                string middle = path.Substring(prefixLen, path.Length - prefixLen - suffixLen);
                int num = 0;
                int k = 0;
                while (k < middle.Length && C.IsDigit(middle[k]))
                {
                    num = num * 10 + (middle[k] - '0');
                    k++;
                }
                if (k == 0)
                    continue;
                if (last < num)
                    last = num;
            }
            return last;
        }

        // =================================================================
        // prerotate / rotate / postrotate
        // =================================================================

        private static int PrerotateSingleLog(LogInfo log, int logNum,
                                              LogState state, LogNames rotNames)
        {
            string compext = "";
            string fileext = "";
            bool hasErrors = false;
            int rotateCount = log.RotateCount != 0 ? log.RotateCount : 1;

            if (!state.DoRotate)
                return 0;

            Log.Message(MESS.DEBUG, "rotating log {0}, log->rotateCount is {1}\n",
                log.Files[logNum], log.RotateCount);

            if ((log.Flags & LogFlags.Compress) != 0)
            {
                if (log.CompressExt == null)
                {
                    Log.Message(MESS.ERROR, "log {0}: compression enabled, but compression "
                            + "extension is not set\n", log.Files[logNum]);
                    return 1;
                }
                compext = AddDotToExtension(log.CompressExt);
            }

            var now = RotatedTime.FromDateTime(_now);
            state.LastRotated = now;

            rotNames.DirName = log.OldDir != null
                ? Path.GetFullPath(log.OldDir) != log.OldDir
                    ? Path.Combine(DirName(log.Files[logNum]), log.OldDir)
                    : log.OldDir
                : DirName(log.Files[logNum]);

            rotNames.BaseName = BaseName(log.Files[logNum]);

            if (log.AddExtension != null)
            {
                if (rotNames.BaseName.EndsWith(log.AddExtension, StringComparison.Ordinal))
                {
                    rotNames.BaseName = rotNames.BaseName.Substring(0,
                        rotNames.BaseName.Length - log.AddExtension.Length);
                }
                fileext = AddDotToExtension(log.AddExtension);
            }

            if (log.Extension != null)
            {
                if (rotNames.BaseName.EndsWith(log.Extension, StringComparison.Ordinal))
                {
                    fileext = AddDotToExtension(log.Extension);
                    rotNames.BaseName = rotNames.BaseName.Substring(0,
                        rotNames.BaseName.Length - log.Extension.Length);
                }
            }

            /* Adjust "now" if we want yesterday's date */
            if ((log.Flags & LogFlags.DateYesterday) != 0)
            {
                now.Hour = 12;
                now.MDay = now.MDay - 1;
                now = FromEpoch(MktimeSeconds(now));
            }

            if ((log.Flags & LogFlags.DateHourAgo) != 0)
            {
                now.Hour -= 1;
                now = FromEpoch(MktimeSeconds(now));
            }

            /* Construct the glob pattern corresponding to the date format */
            string dextStr;
            string dextPattern;
            string finalDformat;
            if (log.DateFormat != null)
            {
                var conv = DateConversion(log.DateFormat);
                if (conv == null)
                    return 1;
                finalDformat = conv.Value.dformat;
                dextPattern = conv.Value.dextPattern;
            }
            else
            {
                if (log.Criterium == Criterium.ROT_HOURLY)
                {
                    finalDformat = "-%Y%m%d%H";
                    dextPattern = "-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]";
                }
                else
                {
                    finalDformat = "-%Y%m%d";
                    dextPattern = "-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]";
                }
            }

            dextStr = Strftime(finalDformat, ToLocalDateTimeFromDext(now));
            if (dextStr.Length == 0)
            {
                Log.Message(MESS.ERROR, "failed to apply date format '{0}'\n", finalDformat);
                return 1;
            }

            Log.Message(MESS.DEBUG, "dateext suffix '{0}'\n", dextStr);
            Log.Message(MESS.DEBUG, "glob pattern '{0}'\n", dextPattern);

            /* First compress the previous log when necessary */
            if ((log.Flags & LogFlags.Compress) != 0 &&
                    (log.Flags & LogFlags.DelayCompress) != 0)
            {
                if ((log.Flags & LogFlags.DateExt) != 0)
                {
                    string globPattern = string.Format(CultureInfo.InvariantCulture,
                        "{0}{1}{2}", Path.Combine(rotNames.DirName, rotNames.BaseName), dextPattern, fileext);
                    var (grc, paths) = Glob.GlobNoCheck(globPattern);
                    if (grc == GlobResultCode.GLOB_SUCCESS && paths.Count > 0)
                    {
                        var sorted = SortGlobResult(paths,
                            rotNames.DirName!.Length + 1 + rotNames.BaseName!.Length, finalDformat);
                        foreach (var (oldName, _) in sorted)
                        {
                            var sbprev = FileStat.Stat(oldName);
                            if (sbprev == null)
                                Log.Message(MESS.DEBUG, "previous log {0} does not exist\n", oldName);
                            else
                                hasErrors = CompressLogFile(oldName, log, sbprev) != 0;
                            if (hasErrors)
                                break;
                        }
                    }
                    else
                    {
                        Log.Message(MESS.DEBUG, "glob finding logs to compress failed\n");
                    }
                }
                else
                {
                    string oldName = string.Format(CultureInfo.InvariantCulture,
                        "{0}.{1}{2}", Path.Combine(rotNames.DirName, rotNames.BaseName),
                        log.LogStart, fileext);
                    var sbprev = FileStat.Stat(oldName);
                    if (sbprev == null)
                        Log.Message(MESS.DEBUG, "previous log {0} does not exist\n", oldName);
                    else
                        hasErrors = CompressLogFile(oldName, log, sbprev) != 0;
                }
            }

            if ((log.Flags & LogFlags.DateExt) != 0)
            {
                /* glob for compressed files with our pattern and compress ext */
                string globPattern = string.Format(CultureInfo.InvariantCulture,
                    "{0}{1}{2}{3}", Path.Combine(rotNames.DirName, rotNames.BaseName),
                    dextPattern, fileext, compext);
                var (grc, paths) = Glob.GlobNoCheck(globPattern);
                string? disposeNameCandidate = null;
                if (grc == GlobResultCode.GLOB_SUCCESS)
                {
                    var sorted = SortGlobResult(paths,
                        rotNames.DirName!.Length + 1 + rotNames.BaseName!.Length, finalDformat);
                    long mailOut = -1;
                    ulong pathc = (ulong)sorted.Length;
                    ulong rcU = rotateCount < 0 ? ulong.MaxValue : (ulong)rotateCount;
                    for (int idx = 0; idx < sorted.Length; idx++)
                    {
                        var fst = FileStat.Stat(sorted[idx].path);
                        if (fst == null)
                            continue;
                        bool drop = (pathc >= rcU && (ulong)idx <= pathc - rcU);
                        if (!drop && log.RotateAge > 0)
                        {
                            long days = (long)((_nowEpoch - new DateTimeOffset(ToLocal(fst.Mtime)).ToUnixTimeSeconds())
                                / DAY_SECONDS);
                            if (days > log.RotateAge)
                                drop = true;
                        }
                        if (drop)
                        {
                            if (mailOut != -1)
                            {
                                string mailFilename = sorted[(int)mailOut].path;
                                if (!hasErrors && log.LogAddress != null)
                                    hasErrors = MailSender.MailLogWrapper(mailFilename, 
                                        string.IsNullOrEmpty(log.MailCmd) ? MailCommand : log.MailCmd,
                                        logNum, log) != 0;
                                if (!hasErrors)
                                {
                                    Log.Message(MESS.DEBUG, "removing {0}\n", mailFilename);
                                    hasErrors = RemoveLogFile(mailFilename, log) != 0;
                                }
                            }
                            mailOut = idx;
                        }
                    }
                    if (mailOut != -1)
                    {
                        disposeNameCandidate = sorted[(int)mailOut].path;
                    }
                }
                else
                {
                    Log.Message(MESS.DEBUG, "glob finding old rotated logs failed\n");
                }
                rotNames.DisposeName = disposeNameCandidate;

                /* firstRotated is most recently created/compressed rotated log */
                rotNames.FirstRotated = string.Format(CultureInfo.InvariantCulture,
                    "{0}{1}{2}{3}", Path.Combine(rotNames.DirName, rotNames.BaseName), dextStr, fileext,
                    (log.Flags & LogFlags.DelayCompress) != 0 ? "" : compext);
            }
            else
            {
                if (rotateCount == -1)
                {
                    rotateCount = FindLastRotated(rotNames, fileext, compext);
                    if (rotateCount < 0)
                    {
                        Log.Message(MESS.ERROR, "could not find last rotated file: {0}.*{1}{2}\n",
                            Path.Combine(rotNames.DirName, rotNames.BaseName), fileext, compext);
                        return 1;
                    }
                }

                string oldName = string.Format(CultureInfo.InvariantCulture,
                    "{0}.{1}{2}{3}", Path.Combine(rotNames.DirName, rotNames.BaseName),
                    log.LogStart + rotateCount, fileext, compext);

                if (log.RotateCount != -1)
                {
                    rotNames.DisposeName = oldName;
                }

                rotNames.FirstRotated = string.Format(CultureInfo.InvariantCulture,
                    "{0}.{1}{2}{3}", Path.Combine(rotNames.DirName, rotNames.BaseName),
                    log.LogStart, fileext,
                    (log.Flags & LogFlags.DelayCompress) != 0 ? "" : compext);

                for (int i = rotateCount + log.LogStart - 1; i >= log.LogStart && !hasErrors; i--)
                {
                    string newName = oldName;
                    oldName = string.Format(CultureInfo.InvariantCulture,
                        "{0}.{1}{2}{3}", Path.Combine(rotNames.DirName, rotNames.BaseName),
                        i, fileext, compext);

                    /* remove files hit by maxage */
                    if (log.RotateAge > 0)
                    {
                        var fst = FileStat.Stat(oldName);
                        if (fst == null)
                        {
                            Log.Message(MESS.DEBUG, "old log {0} does not exist\n", oldName);
                            continue;
                        }
                        long days = (long)((_nowEpoch - new DateTimeOffset(ToLocal(fst.Mtime)).ToUnixTimeSeconds())
                            / DAY_SECONDS);
                        if (days > log.RotateAge)
                        {
                            if (!hasErrors && log.LogAddress != null)
                                hasErrors = MailSender.MailLogWrapper(oldName,
                                    string.IsNullOrEmpty(log.MailCmd) ? MailCommand : log.MailCmd,
                                    logNum, log) != 0;
                            if (!hasErrors)
                                hasErrors = RemoveLogFile(oldName, log) != 0;
                            continue;
                        }
                    }

                    Log.Message(MESS.DEBUG,
                            "renaming {0} to {1} (rotatecount {2}, logstart {3}, i {4}), \n",
                        oldName, newName, rotateCount, log.LogStart, i);

                    if (!Debug && !FileUtil.Rename(oldName, newName))
                    {
                        if (!File.Exists(oldName))
                        {
                            Log.Message(MESS.DEBUG, "old log {0} does not exist\n", oldName);
                        }
                        else
                        {
                            Log.Message(MESS.ERROR, "error renaming {0} to {1}: {2}\n",
                                oldName, newName, ErrnoMessage(13));
                            hasErrors = true;
                        }
                    }
                }
            }

            if ((log.Flags & LogFlags.DateExt) != 0)
            {
                rotNames.FinalName = string.Format(CultureInfo.InvariantCulture,
                    "{0}{1}{2}", Path.Combine(rotNames.DirName, rotNames.BaseName), dextStr, fileext);
                string destFile = AddExtension(rotNames.FinalName, compext);
                if (File.Exists(destFile))
                {
                    Log.Message(MESS.ERROR,
                            "destination {0} already exists, skipping rotation\n",
                        rotNames.FirstRotated);
                    hasErrors = true;
                }
            }
            else
            {
                /* note: the gzip extension is *not* used here! */
                rotNames.FinalName = string.Format(CultureInfo.InvariantCulture,
                    "{0}.{1}{2}", Path.Combine(rotNames.DirName, rotNames.BaseName),
                    log.LogStart, fileext);
            }

            /* if the last rotation doesn't exist, that's okay */
            if (rotNames.DisposeName != null && !File.Exists(rotNames.DisposeName))
            {
                Log.Message(MESS.DEBUG,
                        "log {0} doesn't exist -- won't try to dispose of it\n",
                    rotNames.DisposeName);
                rotNames.DisposeName = null;
            }

            return hasErrors ? 1 : 0;
        }

        /// <summary>
        /// Builds a DateTime from a RotatedTime (used for strftime of dateext).
        /// </summary>
        private static DateTime ToLocalDateTimeFromDext(RotatedTime t)
        {
            return FromEpoch(MktimeSeconds(t)).ToDateTime();
        }

        private static (string path, long secs)[] SortGlobResult(List<string> paths, int prefixLen, string dformat)
        {
            var arr = new (string path, long secs)[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                string rest = paths[i].Length > prefixLen
                    ? paths[i].Substring(prefixLen)
                    : string.Empty;
                arr[i] = (paths[i], ParseDate(rest, dformat));
            }
            Array.Sort(arr, (a, b) =>
            {
                int cmp = a.secs.CompareTo(b.secs);
                if (cmp != 0) return cmp;
                return string.Compare(a.path, b.path, StringComparison.Ordinal);
            });
            return arr;
        }

        /// <summary>
        /// strptime()-style parse of rest against dformat; returns epoch secs.
        /// Returns 0 for unparseable remnants (sorting only needs consistency).
        /// </summary>
        private static long ParseDate(string text, string fmt)
        {
            int ti = 0, fi = 0;
            int year = -1, mon = -1, day = -1, hour = 0, min = 0, sec = 0;
            long epoch = 0;
            bool fromSeconds = false;

            while (fi < fmt.Length)
            {
                char fc = fmt[fi];
                if (fc == '%' && fi + 1 < fmt.Length)
                {
                    char sp = fmt[fi + 1];
                    switch (sp)
                    {
                        case 'Y':
                            if (!TryReadDigits(text, ref ti, 4, out int y)) return 0;
                            year = y;
                            break;
                        case 'y':
                            if (!TryReadDigits(text, ref ti, 2, out int yy)) return 0;
                            year = 1900 + yy;
                            break;
                        case 'm':
                            if (!TryReadDigits(text, ref ti, 0, out int mo)) return 0;
                            mon = mo;
                            break;
                        case 'd':
                            if (!TryReadDigits(text, ref ti, 0, out int d)) return 0;
                            day = d;
                            break;
                        case 'H':
                            if (!TryReadDigits(text, ref ti, 0, out int h)) return 0;
                            hour = h;
                            break;
                        case 'M':
                            if (!TryReadDigits(text, ref ti, 0, out int mi)) return 0;
                            min = mi;
                            break;
                        case 'S':
                            if (!TryReadDigits(text, ref ti, 0, out int s)) return 0;
                            sec = s;
                            break;
                        case 'j':
                            /* ignore day-of-year */
                            if (!TryReadDigits(text, ref ti, 0, out int _j)) return 0;
                            break;
                        case 'V':
                            if (!TryReadDigits(text, ref ti, 0, out int _v)) return 0;
                            break;
                        case 's':
                            if (!TryReadDigits(text, ref ti, 0, out long secs)) return 0;
                            epoch = secs;
                            fromSeconds = true;
                            break;
                        case 'z':
                            // [+-]HHMM
                            if (ti >= text.Length || (text[ti] != '+' && text[ti] != '-')) return 0;
                            ti++;
                            if (!TryReadDigits(text, ref ti, 4, out int _z)) return 0;
                            break;
                        default:
                            if (ti >= text.Length || text[ti] != sp) return 0;
                            ti++;
                            break;
                    }
                    fi += 2;
                }
                else
                {
                    if (fc == ' ' || fc == '\t')
                    {
                        while (ti < text.Length && char.IsWhiteSpace(text[ti])) ti++;
                    }
                    else
                    {
                        if (ti >= text.Length || text[ti] != fc) return 0;
                        ti++;
                    }
                    fi++;
                }
            }

            if (fromSeconds)
                return epoch;
            if (year < 0) year = 1900;
            if (mon < 0) mon = 1;
            if (day < 0) day = 1;
            return MktimeForDateParts(year, mon, day, hour, min, sec);
        }

        private static bool TryReadDigits(string text, ref int pos, int exact, out int value)
        {
            value = 0;
            if (pos >= text.Length || !C.IsDigit(text[pos]))
                return false;
            int start = pos;
            int v = 0;
            while (pos < text.Length && C.IsDigit(text[pos]))
            {
                if (exact > 0 && pos - start >= exact)
                    break;
                v = v * 10 + (text[pos] - '0');
                pos++;
            }
            if (exact > 0 && pos - start != exact)
                return false;
            if (exact == 0 && pos - start > 2)
                return false;
            value = v;
            return true;
        }

        private static bool TryReadDigits(string text, ref int pos, int exact, out long value)
        {
            int v;
            if (!TryReadDigits(text, ref pos, exact, out v))
            {
                value = 0;
                return false;
            }
            value = v;
            return true;
        }

        private static long MktimeForDateParts(int year, int month, int day, int hour, int min, int sec)
        {
            DateTime baseDate;
            try
            {
                baseDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local);
            }
            catch
            {
                return 0;
            }
            try
            {
                var dt = baseDate.AddMonths(month - 1).AddDays(day - 1)
                    .AddHours(hour).AddMinutes(min).AddSeconds(sec);
                return new DateTimeOffset(dt).ToUnixTimeSeconds();
            }
            catch
            {
                return 0;
            }
        }

        private static int RotateSingleLog(LogInfo log, int logNum,
                                           LogState state, LogNames rotNames)
        {
            bool hasErrors = false;

            if (!state.DoRotate)
                return 0;

            /* logrotate.c rotateSingleLog(): remember the old log's access ACL
             * so the newly created log can inherit it (WITH_ACL). */
            byte[]? prevAcl = null;
            if ((log.Flags & LogFlags.Create) != 0
                    && log.CreateMode == Sentinel.NO_MODE
                    && (log.Flags & (LogFlags.CopyTruncate | LogFlags.Copy)) == 0)
            {
                prevAcl = AclApi.ReadDacl(log.Files[logNum]);
            }

            if ((log.Flags & (LogFlags.CopyTruncate | LogFlags.Copy)) == 0)
            {
                if ((log.Flags & LogFlags.TmpFilename) != 0)
                {
                    string tmpFilename = log.Files[logNum] + ".tmp";
                    Log.Message(MESS.DEBUG, "renaming {0} to {1}\n", log.Files[logNum], tmpFilename);
                    if (!Debug && !hasErrors && !FileUtil.Rename(log.Files[logNum], tmpFilename))
                    {
                        Log.Message(MESS.ERROR, "failed to rename {0} to {1}: {2}\n",
                            log.Files[logNum], tmpFilename, ErrnoMessage(13));
                        hasErrors = true;
                    }
                }
                else
                {
                    Log.Message(MESS.DEBUG, "renaming {0} to {1}\n", log.Files[logNum], rotNames.FinalName);
                    if (!Debug && !hasErrors && !FileUtil.Rename(log.Files[logNum], rotNames.FinalName!))
                    {
                        Log.Message(MESS.ERROR, "failed to rename {0} to {1}: {2}\n",
                            log.Files[logNum], rotNames.FinalName, ErrnoMessage(13));
                        hasErrors = true;
                    }
                }

                if (log.RotateCount == 0)
                {
                    string ext = "";
                    if (log.CompressExt != null
                            && (log.Flags & LogFlags.Compress) != 0
                            && (log.Flags & LogFlags.DelayCompress) == 0)
                        ext = log.CompressExt;

                    rotNames.DisposeName = rotNames.FinalName + ext;
                    Log.Message(MESS.DEBUG, "disposeName will be {0}\n", rotNames.DisposeName);
                }
            }

            if (!hasErrors && (log.Flags & LogFlags.Create) != 0 &&
                    (log.Flags & (LogFlags.CopyTruncate | LogFlags.Copy)) == 0)
            {
                var sb = new FileStat();

                bool suActive = (log.Flags & LogFlags.Su) != 0;
                long createUid = log.CreateUid != Sentinel.NO_UID
                    ? log.CreateUid
                    : suActive ? log.SuUid : state.Sb.Uid;
                long createGid = log.CreateGid != Sentinel.NO_GID
                    ? log.CreateGid
                    : suActive ? log.SuGid : state.Sb.Gid;
                long createMode = log.CreateMode == Sentinel.NO_MODE
                    ? state.Sb.Mode & 0x1FF
                    : log.CreateMode;

                Log.Message(MESS.DEBUG, "creating new {0} mode = 0{1} uid = {2} "
                        + "gid = {3}\n",
                    log.Files[logNum],
                    Convert.ToString(createMode, 8),
                    createUid, createGid);

                if (!Debug)
                {
                    if (!hasErrors)
                    {
                        var fd = CreateOutputFile(log.Files[logNum],
                            new FileStat { Mode = createMode, Uid = createUid, Gid = createGid, Mtime = state.Sb.Mtime, Atime = state.Sb.Atime });
                        if (fd == null)
                            hasErrors = true;
                        else
                        {
                            fd.Dispose();

                            SecurityIdentifier? ownerSid = null;
                            if (log.CreateOwnerSid != null)
                                ownerSid = new SecurityIdentifier(log.CreateOwnerSid);
                            else if (suActive && log.SuOwnerSid != null)
                                ownerSid = new SecurityIdentifier(log.SuOwnerSid);
                            SecurityIdentifier? groupSid = null;
                            if (log.CreateGroupSid != null)
                                groupSid = new SecurityIdentifier(log.CreateGroupSid);
                            else if (suActive && log.SuGroupSid != null)
                                groupSid = new SecurityIdentifier(log.SuGroupSid);

                            if (log.CreateMode == Sentinel.NO_MODE)
                            {
                                /* no explicit mode: hand the fresh log over to the
                                 * requested owner/group and copy the old log's
                                 * access ACL (mirrors fchown + acl_set_fd). */
                                AclApi.ApplyOwnerGroup(log.Files[logNum], ownerSid, groupSid);
                                if (prevAcl != null && prevAcl.Length > 0
                                        && !AclApi.WriteDacl(log.Files[logNum], prevAcl))
                                {
                                    Log.Message(MESS.ERROR, "setting ACL for {0}: failed\n", log.Files[logNum]);
                                }
                            }
                            else
                            {
                                /* explicit mode: materialize it as a Windows DACL
                                 * (owner/group class -> resolved SIDs), i.e. the
                                 * chmod equivalent performed by the reference. */
                                AclApi.ApplyCreateAcl(log.Files[logNum], createMode, ownerSid, groupSid);
                            }
                        }
                    }
                }
            }

            if (!hasErrors
                    && (log.Flags & (LogFlags.CopyTruncate | LogFlags.Copy)) != 0
                    && (log.Flags & LogFlags.TmpFilename) == 0)
            {
                bool skipCopy = log.RotateCount == 0 && log.LogAddress == null;
hasErrors = CopyTruncate(log.Files[logNum], rotNames.FinalName!,
                            state.Sb, log, skipCopy) != 0;
            }

            return hasErrors ? 1 : 0;
        }

        private static int PostrotateSingleLog(LogInfo log, int logNum,
                                               LogState state, LogNames rotNames)
        {
            bool hasErrors = false;

            if (!state.DoRotate)
                return 0;

            if (!hasErrors && (log.Flags & LogFlags.TmpFilename) != 0)
            {
                string tmpFilename = log.Files[logNum] + ".tmp";
                hasErrors = CopyTruncate(tmpFilename, rotNames.FinalName!,
                                         state.Sb, log, skipCopy: false) != 0;
                Log.Message(MESS.DEBUG, "removing tmp log {0}\n", tmpFilename);
                if (!Debug && !hasErrors)
                {
                    FileUtil.Unlink(tmpFilename, out _);
                }
            }

            if (!hasErrors && (log.Flags & LogFlags.Compress) != 0 &&
                    (log.Flags & LogFlags.DelayCompress) == 0)
            {
                bool skippedCopy = (log.Flags & (LogFlags.CopyTruncate | LogFlags.Copy)) != 0 &&
                                   (log.Flags & LogFlags.TmpFilename) == 0 &&
                                   log.RotateCount == 0 &&
                                   log.LogAddress == null;

                if (!skippedCopy)
                    hasErrors = CompressLogFile(rotNames.FinalName!, log, state.Sb) != 0;
            }

            if (!hasErrors && log.LogAddress != null)
            {
                string? mailFilename;
                if ((log.Flags & LogFlags.MailFirst) != 0)
                    mailFilename = rotNames.FirstRotated;
                else
                    mailFilename = rotNames.DisposeName;

                if (mailFilename != null)
                    hasErrors = MailSender.MailLogWrapper(mailFilename,
                        string.IsNullOrEmpty(log.MailCmd) ? MailCommand : log.MailCmd,
                        logNum, log) != 0;
            }

            if (!hasErrors && rotNames.DisposeName != null)
                hasErrors = RemoveLogFile(rotNames.DisposeName, log) != 0;

            return hasErrors ? 1 : 0;
        }

        // =================================================================
        // scripts
        // =================================================================

        private static int RunScript(LogInfo log, string logfn, string? logrotfn, string script)
        {
            if (Debug)
            {
                Log.Message(MESS.DEBUG, "running script with args {0} {1}: \"{2}\"\n",
                    logfn, logrotfn ?? "", script);
                return 0;
            }
            //return ProcessRunner.RunScript(script, logfn, logrotfn);
            var envs = new (string EnvVar, string Value)[]
            {
                (ScriptEnviromentVariables.Log, logfn),
                (ScriptEnviromentVariables.LogRotated, logrotfn)
            };
            return Impersonation.Run(log, (dir, account) =>
                ProcessRunner.RunScript(script, dir, account, envs));
        }

        // =================================================================
        // RotateLogSet
        // =================================================================

        public static int RotateLogSet(LogInfo log, bool force)
        {
            bool hasErrors = false;
            int numRotated = 0;
            bool shared = (log.Flags & LogFlags.SharedScripts) != 0;
            int numFiles = log.Files.Count;

            Log.Message(MESS.DEBUG, "\nrotating pattern: {0} ", log.Pattern);
            if (force)
            {
                Log.Message(MESS.DEBUG, "forced from command line ");
            }
            else
            {
                switch (log.Criterium)
                {
                    case Criterium.ROT_HOURLY: Log.Message(MESS.DEBUG, "hourly "); break;
                    case Criterium.ROT_DAYS: Log.Message(MESS.DEBUG, "after {0} days ", log.Threshold); break;
                    case Criterium.ROT_WEEKLY: Log.Message(MESS.DEBUG, "weekly "); break;
                    case Criterium.ROT_MONTHLY: Log.Message(MESS.DEBUG, "monthly "); break;
                    case Criterium.ROT_YEARLY: Log.Message(MESS.DEBUG, "yearly "); break;
                    case Criterium.ROT_SIZE: Log.Message(MESS.DEBUG, "{0} bytes ", log.Threshold); break;
                    default: Log.Message(MESS.FATAL, "rotateLogSet() does not have case for: {0} ", (uint)log.Criterium); break;
                }
            }

            if (log.OldDir != null)
                Log.Message(MESS.DEBUG, "olddir is {0}, ", log.OldDir);

            if ((log.Flags & LogFlags.IfEmpty) != 0)
                Log.Message(MESS.DEBUG, "empty log files are rotated, ");
            else
                Log.Message(MESS.DEBUG, "empty log files are not rotated, ");

            if (log.MinSize != 0)
                Log.Message(MESS.DEBUG, "only log files >= {0} bytes are rotated, ", log.MinSize);

            if (log.MaxSize != 0)
                Log.Message(MESS.DEBUG, "log files >= {0} are rotated earlier, ", log.MaxSize);

            if (log.RotateMinAge != 0)
                Log.Message(MESS.DEBUG, "only log files older than {0} days are rotated, ", log.RotateMinAge);

            if ((log.RotateCount == -1) && (log.RotateAge == 0))
                Log.Message(MESS.DEBUG, "old logs are kept forever\n");
            else
            {
                if (log.LogAddress != null)
                    Log.Message(MESS.DEBUG, "old logs mailed to {0}, ", log.LogAddress);

                if (log.RotateCount == 0)
                    Log.Message(MESS.DEBUG, "no old logs will be kept\n");
                else
                {
                    if (log.RotateCount == -1)
                        Log.Message(MESS.DEBUG, "(unlimited rotations), ");
                    else
                        Log.Message(MESS.DEBUG, "({0} rotations), ", log.RotateCount);

                    Log.Message(MESS.DEBUG, "old logs are removed");

                    if (log.RotateAge > 0)
                        Log.Message(MESS.DEBUG, " after {0} days", log.RotateAge);

                    Log.Message(MESS.DEBUG, "\n");
                }
            }

            if (numFiles == 0)
            {
                Log.Message(MESS.DEBUG, "No logs found. Rotation not needed.\n");
                return 0;
            }

            var logHasErrors = new int[numFiles];

            /* su() is a no-op on Windows */

            for (int i = 0; i < numFiles; i++)
            {
                logHasErrors[i] = FindNeedRotating(log, i, force);
                if (logHasErrors[i] != 0) hasErrors = true;

                var logState = States.FindState(log.Files[i]);
                if (logState != null && logState.DoRotate)
                    numRotated++;
            }

            if (log.First != null)
            {
                if (numRotated == 0)
                {
                    Log.Message(MESS.DEBUG, "not running first action script, "
                            + "since no logs will be rotated\n");
                }
                else
                {
                    Log.Message(MESS.DEBUG, "running first action script\n");
                    if (RunScript(log, log.Pattern!, null, log.First) != 0)
                    {
                        Log.Message(MESS.ERROR, "error running first action script "
                                + "for {0}\n", log.Pattern);
                        hasErrors = true;
                        return 1;
                    }
                }
            }

            var state = new LogState[numFiles];
            var rotNames = new LogNames[numFiles];
            for (int i = 0; i < numFiles; i++)
            {
                state[i] = States.FindState(log.Files[i]);
                if (state[i] == null)
                    logHasErrors[i] = 1;
                rotNames[i] = new LogNames();
            }

            for (int j = 0; shared ? j < 1 : j < numFiles; j++)
            {
                for (int i = shared ? 0 : j;
                     shared ? i < numFiles : i == j;
                     i++)
                {
                    /* rotNames already allocated */
                }

                if (log.Pre != null
                        && !(
                                (!shared && (logHasErrors[j] != 0 || !state[j].DoRotate))
                                || (hasErrors && shared)
                            ))
                {
                    if (numRotated == 0)
                    {
                        Log.Message(MESS.DEBUG, "not running prerotate script, "
                                + "since no logs will be rotated\n");
                    }
                    else
                    {
                        Log.Message(MESS.DEBUG, "running prerotate script\n");
                        if (RunScript(log, shared ? log.Pattern! : log.Files[j], null, log.Pre!) != 0)
                        {
                            if (shared)
                                Log.Message(MESS.ERROR,
                                        "error running shared prerotate script "
                                        + "for '{0}'\n", log.Pattern);
                            else
                                Log.Message(MESS.ERROR,
                                        "error running non-shared prerotate script "
                                        + "for {0} of '{1}'\n", log.Files[j], log.Pattern);
                            logHasErrors[j] = 1;
                            hasErrors = true;
                        }
                    }
                }

                for (int i = shared ? 0 : j;
                     shared ? i < numFiles : i == j;
                     i++)
                {
                    if (!((logHasErrors[i] != 0 && !shared) || (hasErrors && shared)))
                    {
                        logHasErrors[i] |= PrerotateSingleLog(log, i, state[i], rotNames[i]);
                        if (logHasErrors[i] != 0) hasErrors = true;
                    }
                }

                for (int i = shared ? 0 : j;
                     shared ? i < numFiles : i == j;
                     i++)
                {
                    if (!((logHasErrors[i] != 0 && !shared) || (hasErrors && shared)))
                    {
                        logHasErrors[i] |= RotateSingleLog(log, i, state[i], rotNames[i]);
                        if (logHasErrors[i] != 0) hasErrors = true;
                    }
                }

                if (log.Post != null
                        && !(
                                (!shared && (logHasErrors[j] != 0 || !state[j].DoRotate))
                                || (hasErrors && shared)
                            ))
                {
                    if (numRotated == 0)
                    {
                        Log.Message(MESS.DEBUG, "not running postrotate script, "
                                + "since no logs were rotated\n");
                    }
                    else
                    {
                        string logfn = shared ? log.Pattern! : log.Files[j];
                        string? logrotfn = shared ? null : rotNames[j].FinalName;

                        Log.Message(MESS.DEBUG, "running postrotate script\n");
                        if (RunScript(log, logfn, logrotfn, log.Post!) != 0)
                        {
                            if (shared)
                                Log.Message(MESS.ERROR,
                                        "error running shared postrotate script "
                                        + "for '{0}'\n", log.Pattern);
                            else
                                Log.Message(MESS.ERROR,
                                        "error running non-shared postrotate script "
                                        + "for {0} of '{1}'\n", log.Files[j], log.Pattern);
                            logHasErrors[j] = 1;
                            hasErrors = true;
                        }
                    }
                }

                for (int i = shared ? 0 : j;
                     shared ? i < numFiles : i == j;
                     i++)
                {
                    if (!((logHasErrors[i] != 0 && !shared) || (hasErrors && shared)))
                    {
                        logHasErrors[i] |= PostrotateSingleLog(log, i, state[i], rotNames[i]);
                        if (logHasErrors[i] != 0) hasErrors = true;
                    }
                }
            }

            if (log.Last != null)
            {
                if (numRotated == 0)
                {
                    Log.Message(MESS.DEBUG, "not running last action script, "
                            + "since no logs will be rotated\n");
                }
                else
                {
                    Log.Message(MESS.DEBUG, "running last action script\n");
                    if (RunScript(log, log.Pattern!, null, log.Last) != 0)
                    {
                        Log.Message(MESS.ERROR, "error running last action script "
                                + "for {0}\n", log.Pattern);
                        hasErrors = true;
                    }
                }
            }

            return hasErrors ? 1 : 0;
        }

        // =================================================================
        // top level
        // =================================================================

        /// <summary>
        /// Port of main()'s work after config parsing.
        /// Returns 0 (ok), 1 (errors) or 3 (state lock failure).
        /// </summary>
        public static int Execute(List<LogInfo> logs, bool force)
        {
            _now = DateTime.Now;
            _nowEpoch = new DateTimeOffset(_now).ToUnixTimeSeconds();
            StateManager.CurrentTime = _now;

            if (!Debug)
            {
                int lc = LockState(StateFile, SkipStateLock, WaitForStateLock);
                if (lc != 0)
                    return 3;
            }

            int rc = 0;
            if (ReadState(StateFile) != 0)
                rc = 1;

            Log.Message(MESS.DEBUG, "\nHandling {0} logs\n", logs.Count);

            try
            {
                foreach (var log in logs)
                    rc |= RotateLogSet(log, force);
            }
            catch (Exception ex)
            {
                // Keep the state file in sync even when one log set throws
                // (e.g. NullReferenceException, OutOfMemoryException), so the
                // failed logs are not re-rotated on the next run.
                Log.Message(MESS.ERROR, "logrotate: error during rotation: {0}\n", ex.Message);
                rc = 1;
            }
            finally
            {
                if (!Debug)
                    rc |= WriteState(StateFile);
            }

            return rc != 0 ? 1 : 0;
        }
    }

    /// <summary>
    /// Minimal task helpers so the mail/compress pipelines don't deadlock.
    /// </summary>
    internal static class TaskHelper
    {
        public static System.Threading.Tasks.Task Run(Action action)
        {
            return System.Threading.Tasks.Task.Run(action);
        }

        public static System.Threading.Tasks.Task CopyAsync(Stream src, Stream dst)
        {
            return src.CopyToAsync(dst);
        }
    }
}