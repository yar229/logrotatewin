using LogRotate.Consts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace LogRotate
{
    /// <summary>
    /// Port of config.c: parses logrotate configuration files, building the
    /// list of LogInfo entries (and the default global config).
    /// </summary>
    public sealed class ConfigParser
    {
        public List<LogInfo> Logs { get; } = new List<LogInfo>();
        public int NumLogs => Logs.Count;

        // state machine states (mirror STATE_* from config.c)
        private const int STATE_DEFAULT = 2;
        private const int STATE_SKIP_LINE = 4;
        private const int STATE_DEFINITION_END = 8;
        private const int STATE_SKIP_CONFIG = 16;
        private const int STATE_LOAD_SCRIPT = 32;
        private const int STATE_ERROR = 64;

        private const int MAX_NESTING = 16;

        private static readonly string[] DefTabooExts =
        {
            ",v", ".bak", ".cfsaved", ".disabled", ".dpkg-bak", ".dpkg-del",
            ".dpkg-dist", ".dpkg-new", ".dpkg-old", ".dpkg-tmp", ".new", ".old",
            ".orig", ".rhn-cfg-tmp-*", ".rpmnew", ".rpmorig", ".rpmsave", ".swp",
            ".ucf-dist", ".ucf-new", ".ucf-old", "~"
        };

        private static readonly (string cmd, string ext)[] CompressCmdList =
        {
            ("gzip", ".gz"), ("bzip2", ".bz2"), ("xz", ".xz"), ("zstd", ".zst"),
            ("compress", ".Z"), ("zip", ".zip")
        };

        private readonly List<(string original, string pattern)> _tabooMatchList =
            new List<(string, string)>();

        private int _recursionDepth;
        private string? _globerrMsg;

        // =================================================================
        // helpers
        // =================================================================

        private static string CritToString(Criterium crit)
        {
            switch (crit)
            {
                case Criterium.ROT_HOURLY: return Op.Hourly;
                case Criterium.ROT_DAYS: return Op.Daily;
                case Criterium.ROT_WEEKLY: return Op.Weekly;
                case Criterium.ROT_MONTHLY: return Op.Monthly;
                case Criterium.ROT_YEARLY: return Op.Yearly;
                case Criterium.ROT_SIZE: return Op.Size;
                default: return "XXX";
            }
        }

        private static void SetCriterium(ref Criterium dst, Criterium src, ref int set)
        {
            if (set != 0 && dst != src)
            {
                Log.Message(MESS.DEBUG, "note: '{0}' overrides previously specified '{1}'\n",
                    CritToString(src), CritToString(dst));
            }
            dst = src;
            set = 1;
        }

        /// <summary>
        /// C isolateLine(): returns the trimmed line; the position ends up on the
        /// last whitespace character before the newline (mirrors C pointer
        /// behavior so the caller's next position lands on the newline).
        /// </summary>
        private static string? IsolateLine(string buf, ref int pos, int length)
        {
            int start = pos;
            int endtag = start;
            while (endtag < length && buf[endtag] != '\n')
                endtag++;
            int tmp = endtag - 1;
            while (endtag >= start && endtag < length && C.IsSpace(buf[endtag]))
                endtag--;
            int llen = endtag - start + 1;
            if (start + llen > length)
                llen = length - start;
            if (llen < 0)
                llen = 0;
            if (llen > length - start)
                llen = Math.Max(0, length - start);

            string key = buf.Substring(start, llen);
            pos = tmp;
            return key;
        }

        private static string? IsolateValue(string configFile, int lineNum, string key,
                                            string buf, ref int pos, int length)
        {
            int chptr = pos;
            while (chptr < length && C.IsBlank(buf[chptr]))
                chptr++;
            if (chptr < length && buf[chptr] == '=')
            {
                chptr++;
                while (chptr < length && C.IsBlank(buf[chptr]))
                    chptr++;
            }

            if (chptr < length && buf[chptr] == '\n')
            {
                Log.Message(MESS.ERROR, "{0}:{1} argument expected after {2}\n",
                    configFile, lineNum, key);
                return null;
            }

            pos = chptr;
            return IsolateLine(buf, ref pos, length);
        }

        private static string? IsolateWord(string buf, ref int pos, int length)
        {
            int start = pos;
            while (start < length && C.IsBlank(buf[start]))
                start++;
            int endtag = start;
            while (endtag < length && C.IsAlpha(buf[endtag]))
                endtag++;
            if (endtag > length)
                return null;
            int wlen = endtag - start;
            string key = buf.Substring(start, wlen);
            pos = endtag;
            return key;
        }

private static bool ResolveUid(string userName, out long uid, out string? sid)
        {
            uid = Sentinel.NO_UID;
            sid = null;
            if (string.Equals(userName, "root", StringComparison.Ordinal))
            {
                uid = 0;
                return true;
            }
            if (long.TryParse(userName, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                uid = parsed;
                Log.Message(MESS.DEBUG, "note: numeric uid {0} accepted (POSIX user lookup is not available on Windows)\n", parsed);
                return true;
            }
            if (TryResolveWindowsAccount(userName, out SecurityIdentifier? s))
            {
                sid = s.Value;
                uid = LastSidSubAuthority(sid);
                Log.Message(MESS.DEBUG, "note: Windows user '{0}' resolved to {1} (uid {2})\n",
                    userName, sid, uid);
                return true;
            }
            return false;
        }

        private static bool ResolveGid(string groupName, out long gid, out string? sid)
        {
            gid = Sentinel.NO_GID;
            sid = null;
            if (string.Equals(groupName, "root", StringComparison.Ordinal))
            {
                gid = 0;
                return true;
            }
            if (long.TryParse(groupName, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                gid = parsed;
                Log.Message(MESS.DEBUG, "note: numeric gid {0} accepted (POSIX group lookup is not available on Windows)\n", parsed);
                return true;
            }
            if (TryResolveWindowsAccount(groupName, out SecurityIdentifier? s))
            {
                sid = s.Value;
                gid = LastSidSubAuthority(sid);
                Log.Message(MESS.DEBUG, "note: Windows group '{0}' resolved to {1} (gid {2})\n",
                    groupName, sid, gid);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves a Windows account name (user or group) to a SID. Plain names
        /// are tried against the local machine as well as a few well-known
        /// prefixes, so 'Administrators', 'SYSTEM' or 'YAR\yar229' all work.
        /// </summary>
        private static bool TryResolveWindowsAccount(string name, out SecurityIdentifier? sid)
        {
            var candidates = new[] { name,
                ".\\" + name, Environment.MachineName + "\\" + name,
                "BUILTIN\\" + name, "NT AUTHORITY\\" + name };
            foreach (var candidate in candidates)
            {
                try
                {
                    var s = (SecurityIdentifier)new NTAccount(candidate).Translate(typeof(SecurityIdentifier));
                    if (s != null)
                    {
                        sid = s;
                        return true;
                    }
                }
                catch (Exception)
                {
                    /* try the next spelling */
                }
            }
            sid = null;
            return false;
        }

        /// <summary>
        /// Warns when 'su' names an account different from the one the process
        /// runs under: unlike Linux, Windows cannot switch the effective user
        /// (there is no setuid()), so scripts will keep running under the
        /// current account.
        /// </summary>
        private static void CheckSuIdentity(string configFile, int lineNum, LogInfo log)
        {
            if (log.SuUid == Sentinel.NO_UID)
                return;

            /* with supasswd, scripts can be run as the su user via
             * impersonation, so no warning is needed here */
            if (log.SuOwnerSid != null && !string.IsNullOrEmpty(log.SuPassword))
                return;

            if (log.SuOwnerSid == null)
            {
                /* numeric/root su user: there is no Windows account to compare */
                Log.Message(MESS.WARN,
                    "{0}:{1}: cannot verify the 'su' user {2} against a Windows account; "
                    + $"scripts ({Op.PreRotate}, {Op.PostRotate}, {Op.FirstAction}, {Op.LastAction}, {Op.Preremove}, {Op.MailScript}) "
                    + "will run under the current account ('su' cannot switch "
                    + "users on Windows)\n",
                    configFile, lineNum, log.SuUid);
                return;
            }

            var suSid = new SecurityIdentifier(log.SuOwnerSid);
            SecurityIdentifier? currentSid = null;
            try
            {
                currentSid = WindowsIdentity.GetCurrent().User;
            }
            catch (Exception)
            {
                /* account could not be determined; fall through to the warning */
            }

            if (currentSid != null && suSid.Equals(currentSid))
                return;

            Log.Message(MESS.WARN,
                "{0}:{1}: the process does not run as the 'su' user ({2}, current account {3}); "
                + $"scripts ({Op.PreRotate}, {Op.PostRotate}, {Op.FirstAction}, {Op.LastAction}, {Op.Preremove}, {Op.MailScript} "
                + "will run under the current account ('su' cannot switch "
                + "users on Windows)\n",
                configFile, lineNum, suSid, currentSid?.ToString() ?? "<unknown>");
        }

        /// <summary>
        /// The rightmost sub-authority of a SID, used as a POSIX-like uid/gid
        /// number (e.g. 'Administrators' S-1-5-32-544 -> 544).
        /// </summary>
        private static long LastSidSubAuthority(string sidValue)
        {
            int lastDash = sidValue.LastIndexOf('-');
            return long.TryParse(sidValue.Substring(lastDash + 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out long rid) ? rid : 0;
        }

        /// <summary>
        /// readModeUidGid() port. Returns true on error.
        /// </summary>
private static bool ReadModeUidGid(string configFile, int lineNum, string directive,
                                           string value, ref long mode, ref long uid, ref long gid,
                                           ref string? ownerSid, ref string? groupSid)
        {
            bool isSu = directive == Op.Su;
            int i = 0;
            var vals = new string[3];
            int p = 0;

            while (p < value.Length && C.IsSpace(value[p]))
                p++;
            if (p >= value.Length)
                return false;

            for (i = 0; i < 3; i++)
            {
                int start = p;
                char endchr = '\0';
                while (start < value.Length && C.IsSpace(value[start]))
                    start++;

                if (start < value.Length && (value[start] == '\'' || value[start] == '"'))
                    endchr = value[start++];

                p = start;
                while (p < value.Length && ((endchr != '\0') ? value[p] != endchr : !C.IsSpace(value[p])))
                    p++;

                if (endchr != '\0' && (p >= value.Length || value[p] != endchr))
                {
                    Log.Message(MESS.ERROR, "{0}:{1} invalid arguments for {2}\n",
                        configFile, lineNum, directive);
                    return true;
                }

                vals[i] = value.Substring(start, p - start);

                if (endchr != '\0')
                    p++;

                if (p >= value.Length)
                    break;
            }

            if (i >= 3 || (i == 2 && isSu))
            {
                Log.Message(MESS.ERROR, "{0}:{1} extra arguments for {2}\n",
                    configFile, lineNum, directive);
                return true;
            }

            string? modestr;
            string? userstr;
            string? groupstr;

            if (i == 2)
            {
                modestr = vals[0];
                userstr = vals[1];
                groupstr = vals[2];
            }
            else if (i == 1)
            {
                modestr = null;
                userstr = vals[0];
                groupstr = vals[1];
            }
            else
            {
                if (isSu)
                {
                    modestr = null;
                    userstr = vals[0];
                }
                else
                {
                    modestr = vals[0];
                    userstr = null;
                }
                groupstr = null;
            }

bool error = false;
            if (groupstr != null)
            {
                if (!ResolveGid(groupstr, out gid, out groupSid))
                {
                    Log.Message(MESS.ERROR, "{0}:{1} unknown group '{2}'\n",
                        configFile, lineNum, groupstr);
                    error = true;
                }
            }
            if (userstr != null)
            {
                if (!ResolveUid(userstr, out uid, out ownerSid))
                {
                    Log.Message(MESS.ERROR, "{0}:{1} unknown user '{2}'\n",
                        configFile, lineNum, userstr);
                    error = true;
                }
            }
            if (modestr != null)
            {
                if (!C.TryParseNumber(modestr, 0, out ulong uv, out int e) || e != modestr.Length
                    || (long)uv < 0 || (long)uv >= int.MaxValue)
                {
                    Log.Message(MESS.ERROR, "{0}:{1} invalid mode '{2}'\n",
                        configFile, lineNum, modestr);
                    error = true;
                }
                else
                {
                    mode = (long)uv;
                }
            }

            return error;
        }

        private static string? ReadAddress(string configFile, int lineNum, string key,
                                           string buf, ref int pos, int length)
        {
            string? address = IsolateValue(configFile, lineNum, key, buf, ref pos, length);
            if (address != null)
            {
                int chptr = 0;
                while (chptr < address.Length && C.IsPrint(address[chptr]) && address[chptr] != ' ')
                    chptr++;
                if (chptr < address.Length)
                {
                    Log.Message(MESS.ERROR, "{0}:{1} bad {2} address {3}\n",
                        configFile, lineNum, key, address);
                    return null;
                }
            }
            return address;
        }

        private static string? ReadPath(string configFile, int lineNum, string key,
                                        string buf, ref int pos, int length)
        {
            string? path = IsolateValue(configFile, lineNum, key, buf, ref pos, length);
            if (path != null)
            {
                foreach (var c in path)
                {
                    if (!C.IsPrint(c)) // not bad on windows // || C.IsBlank(c))
                    {
                        Log.Message(MESS.ERROR, "{0}:{1} bad {2} path {3}\n",
                            configFile, lineNum, key, path);
                        return null;
                    }
                }
            }
            return path;
        }

        /// <summary>
        /// Replaces '~' or '~/' with $HOME / $USERPROFILE.
        /// </summary>
        private static bool ExpandHomeRelativePath(ref string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '~'
                || (path.Length > 1 && path[1] != '/' && path[1] != '\\'))
                return false;

            string home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? ".";

            string newPath = path.Length <= 1 ? home : home + "\\" + path.Substring(2);
            Log.Message(MESS.DEBUG, "replaced '{0}' with '{1}'\n", path, newPath);
            path = newPath;
            return false;
        }

        private bool CheckFile(string fname)
        {
            if (fname == "." || fname == "..")
                return false;

            foreach (var (original, pattern) in _tabooMatchList)
            {
                if (Glob.Fnmatch(pattern, fname, fnmPeriod: true))
                {
                    Log.Message(MESS.DEBUG, "Ignoring {0}, because of {1} pattern match\n",
                        fname, original);
                    return false;
                }
            }
            return true;
        }

        // =================================================================
        // LogInfo copy/free
        // =================================================================

        private static LogInfo CopyLogInfo(LogInfo from)
        {
            var to = new LogInfo();
            to.Pattern = from.Pattern;
            to.Files.AddRange(from.Files);
            to.OldDir = from.OldDir;
            to.Criterium = from.Criterium;
            to.Weekday = from.Weekday;
            to.Threshold = from.Threshold;
            to.MaxSize = from.MaxSize;
            to.MinSize = from.MinSize;
            to.RotateCount = from.RotateCount;
            to.RotateMinAge = from.RotateMinAge;
            to.RotateAge = from.RotateAge;
            to.LogStart = from.LogStart;
            to.Pre = from.Pre;
            to.Post = from.Post;
            to.First = from.First;
            to.Last = from.Last;
            to.PreRemove = from.PreRemove;
            to.MailCmd = from.MailCmd;
            to.MailAsScript = from.MailAsScript;
            to.LogAddress = from.LogAddress;
            to.Extension = from.Extension;
            to.AddExtension = from.AddExtension;
            to.CompressProg = from.CompressProg;
            to.UncompressProg = from.UncompressProg;
            to.CompressExt = from.CompressExt;
            to.Flags = from.Flags;
            to.ShredCycles = from.ShredCycles;
to.CreateMode = from.CreateMode;
            to.CreateUid = from.CreateUid;
            to.CreateGid = from.CreateGid;
            to.CreateOwnerSid = from.CreateOwnerSid;
            to.CreateGroupSid = from.CreateGroupSid;
            to.SuUid = from.SuUid;
            to.SuGid = from.SuGid;
            to.SuOwnerSid = from.SuOwnerSid;
            to.SuGroupSid = from.SuGroupSid;
            to.SuPassword = from.SuPassword;
            to.OlddirMode = from.OlddirMode;
            to.OlddirUid = from.OlddirUid;
            to.OlddirGid = from.OlddirGid;
            to.CompressOptions.AddRange(from.CompressOptions);
            to.DateFormat = from.DateFormat;
            return to;
        }

        private static void RestoreInto(LogInfo target, LogInfo source)
        {
            var copy = CopyLogInfo(source);
            target.Pattern = copy.Pattern;
            target.Files.Clear();
            target.Files.AddRange(copy.Files);
            target.OldDir = copy.OldDir;
            target.Criterium = copy.Criterium;
            target.Weekday = copy.Weekday;
            target.Threshold = copy.Threshold;
            target.MaxSize = copy.MaxSize;
            target.MinSize = copy.MinSize;
            target.RotateCount = copy.RotateCount;
            target.RotateMinAge = copy.RotateMinAge;
            target.RotateAge = copy.RotateAge;
            target.LogStart = copy.LogStart;
            target.Pre = copy.Pre;
            target.Post = copy.Post;
            target.First = copy.First;
            target.Last = copy.Last;
            target.PreRemove = copy.PreRemove;
            target.MailCmd = copy.MailCmd;
            target.MailAsScript = copy.MailAsScript;
            target.LogAddress = copy.LogAddress;
            target.Extension = copy.Extension;
            target.AddExtension = copy.AddExtension;
            target.CompressProg = copy.CompressProg;
            target.UncompressProg = copy.UncompressProg;
            target.CompressExt = copy.CompressExt;
            target.Flags = copy.Flags;
            target.ShredCycles = copy.ShredCycles;
            target.CreateMode = copy.CreateMode;
            target.CreateUid = copy.CreateUid;
            target.CreateGid = copy.CreateGid;
            target.CreateOwnerSid = copy.CreateOwnerSid;
            target.CreateGroupSid = copy.CreateGroupSid;
            target.SuUid = copy.SuUid;
            target.SuGid = copy.SuGid;
            target.SuOwnerSid = copy.SuOwnerSid;
            target.SuGroupSid = copy.SuGroupSid;
            target.SuPassword = copy.SuPassword;
            target.OlddirMode = copy.OlddirMode;
            target.OlddirUid = copy.OlddirUid;
            target.OlddirGid = copy.OlddirGid;
            target.CompressOptions.Clear();
            target.CompressOptions.AddRange(copy.CompressOptions);
            target.DateFormat = copy.DateFormat;
        }

        private LogInfo NewLogInfo(LogInfo template)
        {
            var n = CopyLogInfo(template);
            Logs.Add(n);
            return n;
        }

        private void FreeTailLogs(int num)
        {
            Log.Message(MESS.DEBUG, "removing last {0} log configs\n", num);
            while (num-- > 0)
            {
                if (Logs.Count > 0)
                    Logs.RemoveAt(Logs.Count - 1);
            }
        }

        private bool MkPath(string path)
        {
            Log.Message(MESS.DEBUG, "creating new directory {0}\n", path);
            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR, "error creating {0}: {1}\n", path, ex.Message);
                return false;
            }
        }

        // =================================================================
        // main entry points
        // =================================================================

        public int ReadAllConfigPaths(string[] paths, LogInfo defaults)
        {
            int result = 0;

            _tabooMatchList.Clear();
            foreach (var ext in DefTabooExts)
                _tabooMatchList.Add(("*" + ext, "*" + ext));

            foreach (var path in paths)
            {
                if (ReadConfigPath(path, defaults) != 0)
                    result = 1;
            }
            return result;
        }

        public int ReadConfigPath(string path, LogInfo defConfig)
        {
            bool isDir;
            bool exists;
            try
            {
                isDir = Directory.Exists(path);
                exists = isDir || File.Exists(path);
            }
            catch
            {
                isDir = false;
                exists = false;
            }

            if (!exists)
            {
                Log.Message(MESS.ERROR, "cannot stat {0}: No such file or directory\n", path);
                return 1;
            }

            if (isDir)
            {
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(path);
                }
                catch (Exception ex)
                {
                    Log.Message(MESS.ERROR, "cannot open directory {0}: {1}\n", path, ex.Message);
                    return 1;
                }

                var namelist = new List<string>();
                foreach (var full in entries)
                {
                    string name = Path.GetFileName(full);
                    if (CheckFile(name))
                        namelist.Add(name);
                }

                if (namelist.Count == 0)
                    return 0;

                namelist.Sort((a, b) => C.StrColl(a, b));

                int result = 0;
                foreach (var name in namelist)
                {
                    string fullPath = Path.Combine(path, name);
                    var defBackup = CopyLogInfo(defConfig);
                    if (ReadConfigFile(fullPath, defConfig) != 0)
                    {
                        Log.Message(MESS.ERROR, "found error in file {0}, skipping\n", name);
                        RestoreInto(defConfig, defBackup);
                        result = 1;
                        continue;
                    }
                }
                return result;
            }
            else
            {
                var defBackup = CopyLogInfo(defConfig);
                int result = ReadConfigFile(path, defConfig);
                if (result != 0)
                    RestoreInto(defConfig, defBackup);
                return result;
            }
        }

        // =================================================================
        // parseGlobString
        // =================================================================

        private enum PgsState { Init, Data, Comment }

        private string? ParseGlobString(string configFile, int lineNum, string buf,
                                        ref int pos, int length)
        {
            var sb = new StringBuilder();
            var state = PgsState.Init;
            for (; pos < length && buf[pos] != '\0'; pos++)
            {
                switch (state)
                {
                    case PgsState.Init:
                        if (buf[pos] == '#')
                            state = PgsState.Comment;
                        else if (!C.IsSpace(buf[pos]))
                            state = PgsState.Data;
                        break;
                    default:
                        if (buf[pos] == '\n')
                            state = PgsState.Init;
                        break;
                }

                if (state == PgsState.Comment)
                    continue;

                switch (buf[pos])
                {
                    case '}':
                        Log.Message(MESS.ERROR, "{0}:{1} unexpected } (missing previous '{{')\n",
                            configFile, lineNum);
                        return null;
                    case '{':
                        while (sb.Length > 1 && C.IsSpace(sb[sb.Length - 1]))
                        {
                            sb.Length--;
                        }
                        return sb.ToString();
                    default:
                        break;
                }

                sb.Append(buf[pos]);
            }

            Log.Message(MESS.ERROR, "{0}:{1} missing '{{' after log files definition\n",
                configFile, lineNum);
            return null;
        }

        /// <summary>
        /// Parses a log file definition header (one or more names/globs followed by '{')
        /// and creates a new LogInfo for it. The caller must ensure newlog == defConfig.
        /// Returns 0 on success, 1 on fatal error (caller should goto error).
        /// </summary>
        private int LogFileSectionStart(string configFile, int lineNum, string buf,
                                        LogInfo defConfig, ref int pos, int length,
                                        ref int logerror, ref int inConfig, ref LogInfo newlog)
        {
            // If no compression options set, use defaults
            if (newlog.CompressProg == null)
                newlog.CompressProg = Options.DefaultCompressCommand;
            if (newlog.UncompressProg == null)
                newlog.UncompressProg = Options.DefaultUncompressCommand;
            if (newlog.CompressExt == null)
                newlog.CompressExt = Options.DefaultCompressExt;

            newlog = NewLogInfo(defConfig);

            string? globString = ParseGlobString(configFile, lineNum, buf, ref pos, length);
            if (globString != null)
                inConfig = 1;
            else
                return 1;

            var fileArgs = ArgvParser.Parse(globString);
            if (fileArgs == null)
            {
                Log.Message(MESS.ERROR, "{0}:{1} error parsing filename\n",
                    configFile, lineNum);
                return 1;
            }
            if (fileArgs.Count < 1)
            {
                Log.Message(MESS.ERROR,
                    "{0}:{1} {{ expected after log file name(s)\n",
                    configFile, lineNum);
                return 1;
            }

            newlog.Files.Clear();
            foreach (var arg in fileArgs)
            {
                if (_globerrMsg != null)
                    _globerrMsg = null;

                if (arg.Length > 2048)
                {
                    Log.Message(MESS.ERROR, "{0}:{1} glob too long ({2} > 2048)\n",
                        configFile, lineNum, arg.Length);
                    logerror = 1;
                    continue;
                }

                var (rc, matches) = Glob.GlobNoCheck(arg);
                if (rc == GlobResultCode.GLOB_ABORTED)
                {
                    if ((newlog.Flags & LogFlags.MissingOk) != 0)
                        continue;
                    _globerrMsg = string.Format("{0}:{1} glob failed for {2}: {3}\n",
                        configFile, lineNum, arg, "Error accessing path");
                    Log.Message(MESS.DEBUG, "{0}", _globerrMsg);
                    matches = new List<string>();
                }

                if (matches.Count == 0)
                {
                    Log.Message(MESS.DEBUG,
                        "{0}:{1} no matches for glob '{2}', skipping\n",
                        configFile, lineNum, arg);
                    continue;
                }

                foreach (var match in matches)
                {
                    // skip directories
                    var st = FileStat.Lstat(match);
                    if (st != null && FileStat.IsDirectory(st))
                        continue;

                    bool addFile = true;
                    foreach (var log in Logs)
                    {
                        foreach (var existing in log.Files)
                        {
                            if (existing == match)
                            {
                                if ((log.Flags & LogFlags.IgnoreDuplicates) != 0)
                                {
                                    addFile = false;
                                    Log.Message(MESS.DEBUG,
                                        "{0}:{1} ignore duplicate log entry for {2}\n",
                                        configFile, lineNum, match);
                                }
                                else
                                {
                                    Log.Message(MESS.ERROR,
                                        "{0}:{1} duplicate log entry for {2}\n",
                                        configFile, lineNum, match);
                                    logerror = 1;
                                    goto duperror;
                                }
                                break;
                            }
                        }
                        if (!addFile)
                            break;
                    }

                    if (addFile)
                    {
                        newlog.Files.Add(match);
                    }
                }
            duperror:
                ;
            }

            newlog.Pattern = globString;
            return 0;
        }

        // =================================================================
        // readConfigFile - the state machine
        // =================================================================

        public int ReadConfigFile(string configFile, LogInfo defConfig)
        {
            long fileSize;
            try
            {
                fileSize = new FileInfo(configFile).Length;
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR, "failed to open config file {0}: {1}\n",
                    configFile, ex.Message);
                return 1;
            }

            if (fileSize > 0xffffff)
            {
                Log.Message(MESS.ERROR, "file {0} too large, probably not a config file.\n",
                    configFile);
                return 1;
            }

            string buf;
            try
            {
                buf = File.ReadAllText(configFile, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR, "failed to open config file {0}: {1}\n",
                    configFile, ex.Message);
                return 1;
            }

            if (buf.Length == 0)
            {
                Log.Message(MESS.DEBUG, "Ignoring {0} because it's empty.\n", configFile);
                return 0;
            }

            if (buf.IndexOf('\r') >= 0)
                buf = buf.Replace("\r", "");

            Log.Message(MESS.DEBUG, "reading config file {0}\n", configFile);

            int length = buf.Length;
            int lineNum = 1;
            int state = STATE_DEFAULT;
            int logerror = 0;
            int criteriumSet = 0;
            int inConfig = 0;
            LogInfo newlog = defConfig;
            int scriptStart = -1;
            string? scriptDest = null;

            string? key = null;

            for (int pos = 0; pos < length; pos++)
            {
                char ch = buf[pos];
                switch (state)
                {
                    case STATE_DEFAULT:
                        if (C.IsBlank(ch))
                            continue;
                        if (ch == '#')
                        {
                            state = STATE_SKIP_LINE;
                            continue;
                        }

                        if (C.IsAlpha(ch))
                        {
                            key = IsolateWord(buf, ref pos, length);
                            if (key == null)
                            {
                                Log.Message(MESS.ERROR, "{0}:{1} failed to parse keyword\n",
                                    configFile, lineNum);
                                if (newlog != defConfig)
                                {
                                    state = STATE_ERROR;
                                    goto next_state;
                                }
                                goto error;
                            }
                            if (pos < length && !C.IsSpace(buf[pos]) && buf[pos] != '=')
                            {
                                if (newlog == defConfig)
                                {
                                    /* Not a property of a bare option keyword: the token is
                                       glued to a non-blank char (".", "\", "/", "*", ...).
                                       Treat it as the start of an unquoted log file
                                       name/glob, e.g. "app.log", "logs\app*.log" { */
                                    pos = pos - key.Length;
                                    if (LogFileSectionStart(configFile, lineNum, buf, defConfig, ref pos, length,
                                            ref logerror, ref inConfig, ref newlog) != 0)
                                        goto error;
                                    break;
                                }

                                Log.Message(MESS.ERROR,
                                    "{0}:{1} keyword '{2}' not properly separated, found {3:#x}\n",
                                    configFile, lineNum, key, (int)buf[pos]);
                                state = STATE_ERROR;
                                goto next_state;
                            }

                            if (key == Op.Compress) newlog.Flags |= LogFlags.Compress;
                            else if (key == Op.NoCompress) newlog.Flags &= ~LogFlags.Compress;
                            else if (key == Op.DelayCompress) newlog.Flags |= LogFlags.DelayCompress;
                            else if (key == Op.NoDelayCompress) newlog.Flags &= ~LogFlags.DelayCompress;
                            else if (key == Op.Shred) newlog.Flags |= LogFlags.Shred;
                            else if (key == Op.NoShred) newlog.Flags &= ~LogFlags.Shred;
                            else if (key == Op.AllowHardlink) newlog.Flags |= LogFlags.AllowHardLink;
                            else if (key == Op.NoAllowHardlink) newlog.Flags &= ~LogFlags.AllowHardLink;
                            else if (key == Op.SharedScripts) newlog.Flags |= LogFlags.SharedScripts;
                            else if (key == Op.NoSharedScripts) newlog.Flags &= ~LogFlags.SharedScripts;
                            else if (key == Op.CopyTruncate)
                            {
                                newlog.Flags |= LogFlags.CopyTruncate;
                                newlog.Flags &= ~LogFlags.TmpFilename;
                            }
                            else if (key == Op.NoCopyTruncate) newlog.Flags &= ~LogFlags.CopyTruncate;
                            else if (key == Op.RenameCopy)
                            {
                                newlog.Flags |= LogFlags.TmpFilename;
                                newlog.Flags &= ~LogFlags.CopyTruncate;
                            }
                            else if (key == Op.NoRenameCopy) newlog.Flags &= ~LogFlags.TmpFilename;
                            else if (key == Op.Copy) newlog.Flags |= LogFlags.Copy;
                            else if (key == Op.NoCopy) newlog.Flags &= ~LogFlags.Copy;
                            else if (key == Op.IfEmpty) newlog.Flags |= LogFlags.IfEmpty;
                            else if (key == Op.NotIfEmpty) newlog.Flags &= ~LogFlags.IfEmpty;
                            else if (key == Op.DateExt) newlog.Flags |= LogFlags.DateExt;
                            else if (key == Op.NoDateExt) newlog.Flags &= ~LogFlags.DateExt;
                            else if (key == Op.DateYesterday) newlog.Flags |= LogFlags.DateYesterday;
                            else if (key == Op.NoDateYesterday) newlog.Flags &= ~LogFlags.DateYesterday;
                            else if (key == Op.DateHourAgo) newlog.Flags |= LogFlags.DateHourAgo;
                            else if (key == Op.NoDateHourAgo) newlog.Flags &= ~LogFlags.DateHourAgo;
                            else if (key == Op.DateFormat)
                            {
                                newlog.DateFormat = IsolateValue(configFile, lineNum, key, buf, ref pos, length);
                            }
                            else if (key == Op.NoOldDir) newlog.OldDir = null;
                            else if (key == Op.MailFirst) newlog.Flags |= LogFlags.MailFirst;
                            else if (key == Op.MailLast) newlog.Flags &= ~LogFlags.MailFirst;
                            else if (key == Op.Su)
                            {
                                key = IsolateLine(buf, ref pos, length);
                                if (key == null)
                                {
                                    Log.Message(MESS.ERROR,
                                        "{0}:{1} failed to parse su option value\n",
                                        configFile, lineNum);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
long tmpMode = Sentinel.NO_MODE;
                                bool err = ReadModeUidGid(configFile, lineNum, Op.Su, key,
                                    ref tmpMode, ref newlog.SuUid, ref newlog.SuGid,
                                    ref newlog.SuOwnerSid, ref newlog.SuGroupSid);
                                if (err)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                if (tmpMode != Sentinel.NO_MODE)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} extra arguments for su\n",
                                        configFile, lineNum);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                if (newlog.SuUid == Sentinel.NO_UID)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} no user for su\n",
                                        configFile, lineNum);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                if (newlog.SuGid == Sentinel.NO_GID)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} no group for su\n",
                                        configFile, lineNum);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.Flags |= LogFlags.Su;

                                /* for a section, the identity check runs when the
                                 * section closes (supasswd may come after su) */
                                if (newlog == defConfig)
                                    CheckSuIdentity(configFile, lineNum, newlog);
                            }
                            else if (key == Op.SuPasswd)
                            {
                                key = IsolateValue(configFile, lineNum, key, buf, ref pos, length);
                                if (key == null)
                                    continue;
                                newlog.SuPassword = key;
                            }
                            else if (key == Op.Create)
                            {
                                key = IsolateLine(buf, ref pos, length);
                                if (key == null)
                                    continue;

                                bool err = ReadModeUidGid(configFile, lineNum, Op.Create, key,
                                    ref newlog.CreateMode, ref newlog.CreateUid, ref newlog.CreateGid,
                                    ref newlog.CreateOwnerSid, ref newlog.CreateGroupSid);
                                if (err)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.Flags |= LogFlags.Create;
                            }
                            else if (key == Op.CreateOldDir)
                            {
                                key = IsolateLine(buf, ref pos, length);
                                if (key == null)
                                    continue;

                                string? olddirOwnerSid = null;
                                string? olddirGroupSid = null;
                                bool err = ReadModeUidGid(configFile, lineNum, Op.CreateOldDir, key,
                                    ref newlog.OlddirMode, ref newlog.OlddirUid, ref newlog.OlddirGid,
                                    ref olddirOwnerSid, ref olddirGroupSid);
                                if (newlog.OlddirMode == Sentinel.NO_MODE)
                                    newlog.OlddirMode = 0x1ED; // 0755

                                if (err)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }

                                newlog.Flags |= LogFlags.OldDirCreate;
                            }
                            else if (key == Op.NoCreateOldDir) newlog.Flags &= ~LogFlags.OldDirCreate;
                            else if (key == Op.NoCreate) newlog.Flags &= ~LogFlags.Create;
                            else if (key == Op.Size || key == Op.MinSize || key == Op.MaxSize)
                            {
                                string opt = key;
                                key = IsolateValue(configFile, lineNum, opt, buf, ref pos, length);
                                if (key != null && key.Length > 0)
                                {
                                    long multiplier;
                                    char lastCh = key[key.Length - 1];
                                    if (lastCh == 'k' || lastCh == 'K')
                                    {
                                        key = key.Substring(0, key.Length - 1);
                                        multiplier = 1024;
                                    }
                                    else if (lastCh == 'M')
                                    {
                                        key = key.Substring(0, key.Length - 1);
                                        multiplier = 1024 * 1024;
                                    }
                                    else if (lastCh == 'G')
                                    {
                                        key = key.Substring(0, key.Length - 1);
                                        multiplier = 1024 * 1024 * 1024;
                                    }
                                    else if (!C.IsDigit(lastCh))
                                    {
                                        Log.Message(MESS.ERROR, "{0}:{1} unknown unit '{2}'\n",
                                            configFile, lineNum, lastCh);
                                        if (newlog != defConfig)
                                        {
                                            state = STATE_ERROR;
                                            goto next_state;
                                        }
                                        goto error;
                                    }
                                    else
                                    {
                                        multiplier = 1;
                                    }

                                    if (!C.TryParseNumber(key, 0, out ulong size, out int eptr) || eptr != key.Length)
                                    {
                                        Log.Message(MESS.ERROR, "{0}:{1} bad size '{2}'\n",
                                            configFile, lineNum, key);
                                        if (newlog != defConfig)
                                        {
                                            state = STATE_ERROR;
                                            goto next_state;
                                        }
                                        goto error;
                                    }

                                    long final = (long)((ulong)multiplier * size);
                                    if (opt.StartsWith("size", StringComparison.Ordinal))
                                    {
                                        SetCriterium(ref newlog.Criterium, Criterium.ROT_SIZE, ref criteriumSet);
                                        newlog.Threshold = final;
                                    }
                                    else if (opt.StartsWith("maxsize", StringComparison.Ordinal))
                                    {
                                        newlog.MaxSize = final;
                                    }
                                    else
                                    {
                                        newlog.MinSize = final;
                                    }
                                }
                            }
                            else if (key == Op.ShredCycles)
                            {
                                key = IsolateValue(configFile, lineNum, "shred cycles", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                if (!C.TryParseNumber(key, 0, out ulong sc, out int e2) || e2 != key.Length)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} bad shred cycles '{2}'\n",
                                        configFile, lineNum, key);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.ShredCycles = (int)sc;
                            }
                            else if (key == Op.Hourly)
                            {
                                SetCriterium(ref newlog.Criterium, Criterium.ROT_HOURLY, ref criteriumSet);
                            }
                            else if (key == Op.Daily)
                            {
                                SetCriterium(ref newlog.Criterium, Criterium.ROT_DAYS, ref criteriumSet);
                                newlog.Threshold = 1;
                            }
                            else if (key == Op.Monthly)
                            {
                                SetCriterium(ref newlog.Criterium, Criterium.ROT_MONTHLY, ref criteriumSet);
                            }
                            else if (key == Op.Weekly)
                            {
                                SetCriterium(ref newlog.Criterium, Criterium.ROT_WEEKLY, ref criteriumSet);
                                key = IsolateLine(buf, ref pos, length);
                                if (key == null || key.Length == 0)
                                {
                                    /* default to Sunday if no argument was given */
                                    newlog.Weekday = 0;
                                    continue;
                                }

                                if (int.TryParse(key.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int weekday)
                                    && weekday <= 7)
                                {
                                    newlog.Weekday = weekday;
                                    continue;
                                }
                                Log.Message(MESS.ERROR, "{0}:{1} bad weekly directive '{2}'\n",
                                    configFile, lineNum, key);
                                goto error;
                            }
                            else if (key == Op.Yearly)
                            {
                                SetCriterium(ref newlog.Criterium, Criterium.ROT_YEARLY, ref criteriumSet);
                            }
                            else if (key == Op.Rotate)
                            {
                                key = IsolateValue(configFile, lineNum, "rotate count", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                long rcValue;
                                if (!C.TryParseNumber(key, 0, out ulong rc2, out int e3) || e3 != key.Length)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} bad rotation count '{2}'\n",
                                        configFile, lineNum, key);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                rcValue = (long)rc2;
                                // allow -1 (unlimited)
                                if (key == "-1")
                                    rcValue = -1;
                                newlog.RotateCount = (int)rcValue;
                            }
                            else if (key == Op.Start)
                            {
                                key = IsolateValue(configFile, lineNum, "start count", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                if (!C.TryParseNumber(key, 0, out ulong sc2, out int e4) || e4 != key.Length)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} bad start count '{2}'\n",
                                        configFile, lineNum, key);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.LogStart = (int)sc2;
                            }
                            else if (key == Op.MinAge)
                            {
                                key = IsolateValue(configFile, lineNum, "minage count", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                if (!C.TryParseNumber(key, 0, out ulong ma, out int e5) || e5 != key.Length)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} bad minimum age '{2}'\n",
                                        configFile, lineNum, key);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.RotateMinAge = (int)ma;
                            }
                            else if (key == Op.MaxAge)
                            {
                                key = IsolateValue(configFile, lineNum, "maxage count", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                if (!C.TryParseNumber(key, 0, out ulong ma2, out int e6) || e6 != key.Length)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} bad maximum age '{2}'\n",
                                        configFile, lineNum, key);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.RotateAge = (int)ma2;
                            }
                            else if (key == Op.Errors)
                            {
                                Log.Message(MESS.WARN,
                                    "{0}: {1}: the errors directive is deprecated and no longer used.\n",
                                    configFile, lineNum);
                            }
                            else if (key == Op.Mail)
                            {
                                newlog.LogAddress = ReadAddress(configFile, lineNum, "mail", buf, ref pos, length);
                                if (newlog.LogAddress == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                            }
                            else if (key == Op.NoMail) newlog.LogAddress = null;
                            else if (key == Op.MissingOk) newlog.Flags |= LogFlags.MissingOk;
                            else if (key == Op.NoMissingOk) newlog.Flags &= ~LogFlags.MissingOk;
                            else if (key == Op.IgnoreDuplicates) newlog.Flags |= LogFlags.IgnoreDuplicates;
                            else if (key == Op.PreRotate)
                            {
                                newlog.Pre = null;
                                scriptStart = pos;
                                scriptDest = Op.PreRotate;
                                state = STATE_LOAD_SCRIPT;
                            }
                            else if (key == Op.FirstAction)
                            {
                                newlog.First = null;
                                scriptStart = pos;
                                scriptDest = Op.FirstAction;
                                state = STATE_LOAD_SCRIPT;
                            }
                            else if (key == Op.PostRotate)
                            {
                                newlog.Post = null;
                                scriptStart = pos;
                                scriptDest = Op.PostRotate;
                                state = STATE_LOAD_SCRIPT;
                            }
                            else if (key == Op.LastAction)
                            {
                                newlog.Last = null;
                                scriptStart = pos;
                                scriptDest = Op.LastAction;
                                state = STATE_LOAD_SCRIPT;
                            }
                            else if (key == Op.Preremove)
                            {
                                newlog.PreRemove = null;
                                scriptStart = pos;
                                scriptDest = Op.Preremove;
                                state = STATE_LOAD_SCRIPT;
                            }
                            else if (key == Op.MailScript)
                            {
                                newlog.MailCmd = null;
                                scriptStart = pos;
                                scriptDest = Op.MailScript;
                                state = STATE_LOAD_SCRIPT;
                            }
                            else if (key == Op.TabooExt)
                            {
                                if (newlog != defConfig)
                                {
                                    Log.Message(MESS.ERROR,
                                        "{0}:{1} tabooext may not appear inside of log file definition\n",
                                        configFile, lineNum);
                                    state = STATE_ERROR;
                                    continue;
                                }
                                key = IsolateValue(configFile, lineNum, "tabooext", buf, ref pos, length);
                                if (key == null)
                                    continue;
                                int endtag = 0;
                                if (key[endtag] == '+')
                                {
                                    endtag++;
                                    while (endtag < key.Length && C.IsSpace(key[endtag]))
                                        endtag++;
                                }
                                else
                                {
                                    _tabooMatchList.Clear();
                                }

                                while (endtag < key.Length)
                                {
                                    int chptr = endtag;
                                    while (chptr < key.Length && !C.IsSpace(key[chptr])
                                        && key[chptr] != ',')
                                        chptr++;

                                    if (endtag < chptr)
                                    {
                                        string pattern = "*" + key.Substring(endtag, chptr - endtag);
                                        _tabooMatchList.Add((pattern, pattern));
                                    }

                                    endtag = chptr;
                                    if (endtag < key.Length && key[endtag] == ',')
                                        endtag++;
                                    while (endtag < key.Length && C.IsSpace(key[endtag]))
                                        endtag++;
                                }
                            }
                            else if (key == Op.TabooPat)
                            {
                                if (newlog != defConfig)
                                {
                                    Log.Message(MESS.ERROR,
                                        "{0}:{1} taboopat may not appear inside of log file definition\n",
                                        configFile, lineNum);
                                    state = STATE_ERROR;
                                    continue;
                                }
                                key = IsolateValue(configFile, lineNum, "taboopat", buf, ref pos, length);
                                if (key == null)
                                    continue;

                                int endtag = 0;
                                if (key[endtag] == '+')
                                {
                                    endtag++;
                                    while (endtag < key.Length && C.IsSpace(key[endtag]))
                                        endtag++;
                                }
                                else
                                {
                                    _tabooMatchList.Clear();
                                }

                                while (endtag < key.Length)
                                {
                                    int chptr = endtag;
                                    while (chptr < key.Length && !C.IsSpace(key[chptr])
                                        && key[chptr] != ',')
                                        chptr++;

                                    if (endtag < chptr)
                                    {
                                        string pattern = key.Substring(endtag, chptr - endtag);
                                        _tabooMatchList.Add((pattern, pattern));
                                    }

                                    endtag = chptr;
                                    if (endtag < key.Length && key[endtag] == ',')
                                        endtag++;
                                    while (endtag < key.Length && C.IsSpace(key[endtag]))
                                        endtag++;
                                }
                            }
                            else if (key == Op.Include)
                            {
                                key = IsolateValue(configFile, lineNum, "include", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }

                                if (ExpandHomeRelativePath(ref key))
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }

                                Log.Message(MESS.DEBUG, "including {0}\n", key);
                                if (_recursionDepth >= MAX_NESTING)
                                {
                                    Log.Message(MESS.ERROR, "{0}:{1} include nesting too deep\n",
                                        configFile, lineNum);
                                    logerror = 1;
                                    continue;
                                }

                                _recursionDepth++;
                                int rv = ReadConfigPath(key, newlog);
                                _recursionDepth--;

                                if (rv != 0)
                                {
                                    logerror = 1;
                                    continue;
                                }
                            }
                            else if (key == Op.OldDir)
                            {
                                newlog.OldDir = ReadPath(configFile, lineNum, "olddir", buf, ref pos, length)?.Trim('"');
                                if (newlog.OldDir == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }

                                string tmp = newlog.OldDir;
                                if (ExpandHomeRelativePath(ref tmp))
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.OldDir = tmp;

                                Log.Message(MESS.DEBUG, "olddir is now {0}\n", newlog.OldDir);
                            }
                            else if (key == Op.Extension)
                            {
                                key = IsolateValue(configFile, lineNum, "extension name", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.Extension = key;
                                key = null;
                                Log.Message(MESS.DEBUG, "extension is now {0}\n", newlog.Extension);
                            }
                            else if (key == Op.AddExtension)
                            {
                                key = IsolateValue(configFile, lineNum, "addextension name", buf, ref pos, length);
                                if (key == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.AddExtension = key;
                                key = null;
                                Log.Message(MESS.DEBUG, "addextension is now {0}\n", newlog.AddExtension);
                            }
                            else if (key == Op.CompressCmd)
                            {
                                newlog.CompressProg = null;
                                newlog.CompressProg = ReadPath(configFile, lineNum, "compress", buf, ref pos, length);
                                if (newlog.CompressProg == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                Log.Message(MESS.DEBUG, "compress_prog is now {0}\n", newlog.CompressProg);

                                string baseName = Path.GetFileName(newlog.CompressProg);
                                foreach (var (cmd, ext) in CompressCmdList)
                                {
                                    if (cmd == baseName)
                                    {
                                        newlog.CompressExt = ext;
                                        Log.Message(MESS.DEBUG, "compress_ext was changed to {0}\n", newlog.CompressExt);
                                        break;
                                    }
                                }
                            }
                            else if (key == Op.UncompressCmd)
                            {
                                newlog.UncompressProg = null;
                                newlog.UncompressProg = ReadPath(configFile, lineNum, "uncompress", buf, ref pos, length);
                                if (newlog.UncompressProg == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                Log.Message(MESS.DEBUG, "uncompress_prog is now {0}\n", newlog.UncompressProg);
                            }
                            else if (key == Op.CompressOptions)
                            {
                                newlog.CompressOptions.Clear();
                                string? options = IsolateLine(buf, ref pos, length);
                                if (options == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                var parsed = ArgvParser.Parse(options);
                                if (parsed == null)
                                {
                                    Log.Message(MESS.ERROR,
                                        "{0}:{1} invalid compression options\n",
                                        configFile, lineNum);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.CompressOptions.AddRange(parsed);
                                Log.Message(MESS.DEBUG, "{0} is now {1}\n", Op.CompressOptions, options);
                            }
                            else if (key == Op.UnCompressOptions)
                            {
                                newlog.UnCompressOptions.Clear();
                                string? options = IsolateLine(buf, ref pos, length);
                                if (options == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                var parsed = ArgvParser.Parse(options);
                                if (parsed == null)
                                {
                                    Log.Message(MESS.ERROR,
                                        "{0}:{1} invalid uncompression options\n",
                                        configFile, lineNum);
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                newlog.UnCompressOptions.AddRange(parsed);
                                Log.Message(MESS.DEBUG, "{0} is now {1}\n", Op.UnCompressOptions, options);
                            }
                            else if (key == Op.CompressExt)
                            {
                                newlog.CompressExt = null;
                                newlog.CompressExt = ReadPath(configFile, lineNum, "compress-ext", buf, ref pos, length);
                                if (newlog.CompressExt == null)
                                {
                                    if (newlog != defConfig)
                                    {
                                        state = STATE_ERROR;
                                        goto next_state;
                                    }
                                    goto error;
                                }
                                Log.Message(MESS.DEBUG, "compress_ext is now {0}\n", newlog.CompressExt);
                            }
                            else
                            {
                                if (newlog == defConfig)
                                {
                                    /* Unknown alphabetic token at the top level is an
                                       unquoted log file name/glob, e.g. "custom.log {"
                                       or "logs\*.log {" */
                                    pos = pos - key.Length;
                                    if (LogFileSectionStart(configFile, lineNum, buf, defConfig, ref pos, length,
                                            ref logerror, ref inConfig, ref newlog) != 0)
                                        goto error;
                                    break;
                                }

                                Log.Message(MESS.WARN, "{0}:{1} unknown option '{2}' -- ignoring line\n",
                                    configFile, lineNum, key);
                                if (pos < length && buf[pos] != '\n')
                                    state = STATE_SKIP_LINE;
                            }
                        }
                        else if (ch == '}')
                        {
                            if (newlog == defConfig)
                            {
                                Log.Message(MESS.ERROR, "{0}:{1} unexpected }}\n",
                                    configFile, lineNum);
                                goto error;
                            }
                            if (inConfig == 0)
                            {
                                Log.Message(MESS.ERROR, "{0}:{1} unexpected } (missing previous '{{')\n",
                                    configFile, lineNum);
                                goto error;
                            }
                            inConfig = 0;
                            if (_globerrMsg != null)
                            {
                                if ((newlog.Flags & LogFlags.MissingOk) == 0)
                                    Log.Message(MESS.ERROR, "{0}", _globerrMsg);
                                _globerrMsg = null;
                                if ((newlog.Flags & LogFlags.MissingOk) == 0)
                                    goto error;
                            }

                            if (newlog.SuPassword != null
                                    && (newlog.Flags & LogFlags.Su) == 0)
                            {
                                Log.Message(MESS.WARN,
                                    "{0}:{1}: supasswd is ignored because 'su' is not set\n",
                                    configFile, lineNum);
                            }

                            if (newlog != defConfig && newlog.SuPassword == null)
                                CheckSuIdentity(configFile, lineNum, newlog);

                            if (newlog.OldDir != null)
                            {
                                for (int j = 0; j < newlog.Files.Count; j++)
                                {
                                    string dirPath = DirName(newlog.Files[j]);
                                    var sbLogdir = FileStat.Stat(dirPath);
                                    if (sbLogdir == null)
                                    {
                                        if ((newlog.Flags & LogFlags.MissingOk) == 0)
                                        {
                                            Log.Message(MESS.ERROR,
                                                "{0}:{1} error verifying log file path {2}: No such file or directory\n",
                                                configFile, lineNum, dirPath);
                                            goto error;
                                        }
                                        Log.Message(MESS.DEBUG,
                                            "{0}:{1} verifying log file path failed {2}, log is probably missing, "
                                            + "but missingok is set, so this is not an error.\n",
                                            configFile, lineNum, dirPath);
                                        continue;
                                    }

                                    string dirName;

                                    var fullPath = Path.GetFullPath(newlog.OldDir);
                                    dirName = newlog.OldDir != fullPath
                                        ? Path.Combine(dirPath, newlog.OldDir)
                                        : newlog.OldDir;

                                    var sbOlddir = FileStat.Stat(dirName);
                                    if (sbOlddir == null)
                                    {
                                        if ((newlog.Flags & LogFlags.OldDirCreate) != 0)
                                        {
                                            bool ret = MkPath(dirName);
                                            if (!ret)
                                            {
                                                Log.Message(MESS.ERROR,
                                                    "{0}:{1} failed to create olddir {2}\n",
                                                    configFile, lineNum, dirName);
                                                goto error;
                                            }

                                            /* with 'su' the new olddir gets the su
                                             * account as owner/group (mirrors
                                             * switch_user(suUid, suGid) + mkpath). */
                                            if ((newlog.Flags & LogFlags.Su) != 0)
                                            {
                                                SecurityIdentifier? olddirOwner = newlog.SuOwnerSid != null
                                                    ? new SecurityIdentifier(newlog.SuOwnerSid) : null;
                                                SecurityIdentifier? olddirGroup = newlog.SuGroupSid != null
                                                    ? new SecurityIdentifier(newlog.SuGroupSid) : null;
                                                long olddirMode = newlog.OlddirMode == Sentinel.NO_MODE
                                                    ? 0x1ED // 0755
                                                    : newlog.OlddirMode;
                                                AclApi.ApplyCreateAcl(dirName, olddirMode, olddirOwner, olddirGroup);
                                            }
                                            sbOlddir = FileStat.Stat(dirName);
                                            if (sbOlddir == null)
                                            {
                                                Log.Message(MESS.ERROR,
                                                    "{0}:{1} error verifying created olddir path {2}\n",
                                                    configFile, lineNum, dirName);
                                                goto error;
                                            }
                                        }
                                        else
                                        {
                                            Log.Message(MESS.ERROR, "{0}:{1} error verifying olddir path {2}\n",
                                                configFile, lineNum, dirName);
                                            goto error;
                                        }
                                    }

                                    if (sbLogdir.DeviceInfo != sbOlddir.DeviceInfo
                                        && (newlog.Flags & (LogFlags.CopyTruncate | LogFlags.Copy | LogFlags.TmpFilename)) == 0)
                                    {
                                        Log.Message(MESS.ERROR,
                                            "{0}:{1} olddir {2} and log file {3} are on different devices\n",
                                            configFile, lineNum, newlog.OldDir, newlog.Files[j]);
                                        goto error;
                                    }
                                }
                            }

                            criteriumSet = 0;
                            newlog = defConfig;
                            state = STATE_DEFINITION_END;
                        }
                        else if (ch != '\n')
                        {
                            /* A quoted name ("app log.log"), an absolute path, or an
                               unquoted name/glob (app.log, logs\*.log, *.log, ...): the
                               start of a log file definition. */
                            if (newlog != defConfig)
                            {
                                Log.Message(MESS.ERROR, "{0}:{1} unexpected log filename\n",
                                    configFile, lineNum);
                                state = STATE_ERROR;
                                continue;
                            }

                            if (LogFileSectionStart(configFile, lineNum, buf, defConfig, ref pos, length,
                                    ref logerror, ref inConfig, ref newlog) != 0)
                                goto error;
                        }
                        break;

                    case STATE_SKIP_LINE:
                    case STATE_SKIP_LINE | STATE_SKIP_CONFIG:
                        if (ch == '\n')
                            state = (state & STATE_SKIP_CONFIG) != 0 ? STATE_SKIP_CONFIG : STATE_DEFAULT;
                        break;

                    case STATE_SKIP_LINE | STATE_LOAD_SCRIPT:
                        if (ch == '\n')
                            state = STATE_LOAD_SCRIPT;
                        break;

                    case STATE_SKIP_LINE | STATE_LOAD_SCRIPT | STATE_SKIP_CONFIG:
                        if (ch == '\n')
                            state = STATE_LOAD_SCRIPT | STATE_SKIP_CONFIG;
                        break;

                    case STATE_DEFINITION_END:
                    case STATE_DEFINITION_END | STATE_SKIP_CONFIG:
                        if (C.IsBlank(ch))
                            continue;
                        if (ch != '\n')
                        {
                            Log.Message(MESS.ERROR, "{0}:{1}, unexpected text after }}\n",
                                configFile, lineNum);
                            state = STATE_SKIP_LINE | ((state & STATE_SKIP_CONFIG) != 0 ? STATE_SKIP_CONFIG : 0);
                        }
                        else
                        {
                            state = (state & STATE_SKIP_CONFIG) != 0 ? STATE_SKIP_CONFIG : STATE_DEFAULT;
                        }
                        break;

                    case STATE_ERROR:
                        Log.Message(MESS.ERROR, "found error in {0}, skipping\n",
                            newlog.Pattern ?? "log config");
                        logerror = 1;
                        state = STATE_SKIP_CONFIG;
                        break;

                    case STATE_LOAD_SCRIPT:
                    case STATE_LOAD_SCRIPT | STATE_SKIP_CONFIG:
                        key = IsolateWord(buf, ref pos, length);
                        if (key == null)
                            continue;

                        if (key == "endscript")
                        {
                            if ((state & STATE_SKIP_CONFIG) != 0)
                            {
                                state = STATE_SKIP_CONFIG;
                            }
                            else
                            {
                                int endtag = pos - 9;
                                while (endtag > scriptStart && buf[endtag] != '\n')
                                    endtag--;
                                endtag++;
                                if (endtag < scriptStart)
                                    endtag = scriptStart;
                                string script = buf.Substring(scriptStart, endtag - scriptStart);
                                switch (scriptDest)
                                {
                                    case Op.PreRotate: newlog.Pre = script; break;
                                    case Op.FirstAction: newlog.First = script; break;
                                    case Op.PostRotate: newlog.Post = script; break;
                                    case Op.LastAction: newlog.Last = script; break;
                                    case Op.Preremove: newlog.PreRemove = script; break;
                                    case Op.MailScript: newlog.MailCmd = script; newlog.MailAsScript = true; break;
                                }
                                scriptDest = null;
                                scriptStart = -1;
                            }
                            state = (state & STATE_SKIP_CONFIG) != 0 ? STATE_SKIP_CONFIG : STATE_DEFAULT;
                        }
                        else
                        {
                            state = (pos < length && buf[pos] == '\n' ? 0 : STATE_SKIP_LINE)
                                | STATE_LOAD_SCRIPT
                                | ((state & STATE_SKIP_CONFIG) != 0 ? STATE_SKIP_CONFIG : 0);
                        }
                        break;

                    case STATE_SKIP_CONFIG:
                        if (ch == '}')
                        {
                            state = STATE_DEFAULT;
                            FreeTailLogs(1);
                            newlog = defConfig;
                        }
                        else
                        {
                            key = IsolateWord(buf, ref pos, length);
                            if (key == null)
                                continue;
                            if (key == Op.PostRotate || key == Op.PreRotate || key == Op.FirstAction
                                || key == Op.LastAction || key == Op.Preremove || key == Op.MailScript)
                            {
                                state = STATE_LOAD_SCRIPT | STATE_SKIP_CONFIG;
                            }
                            else
                            {
                                if (pos < length && buf[pos] != '\n')
                                    state = STATE_SKIP_LINE | STATE_SKIP_CONFIG;
                            }
                        }
                        break;

                    default:
                        Log.Message(MESS.ERROR, "{0}: {1}: readConfigFile() unknown state: {2:#x}\n",
                            configFile, lineNum, state);
                        break;
                }

                if (pos < length && buf[pos] == '\n')
                    lineNum++;

            next_state: ;
            }

            if (scriptStart != -1)
            {
                Log.Message(MESS.ERROR,
                    $"{0}:{Op.PreRotate}, {Op.PostRotate}, {Op.Preremove} or {Op.MailScript} without endscript\n",
                    configFile);
                goto error;
            }

            return logerror;

        error:
            if (newlog != defConfig)
                FreeTailLogs(1);
            _globerrMsg = null;
            return 1;
        }

        private static string DirName(string path)
        {
            string d = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(d))
                return ".";
            return d;
        }
    }
}