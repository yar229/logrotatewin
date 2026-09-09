using System;
using System.Collections.Generic;

namespace LogRotate
{
    /// <summary>
    /// Flags for the logInfo structure, mirroring the LOG_FLAG_* constants
    /// from logrotate.h.
    /// </summary>
    [Flags]
    public enum LogFlags : uint
    {
        Compress         = 1U << 0,
        Create           = 1U << 1,
        IfEmpty          = 1U << 2,
        DelayCompress    = 1U << 3,
        CopyTruncate     = 1U << 4,
        MissingOk        = 1U << 5,
        MailFirst        = 1U << 6,
        SharedScripts    = 1U << 7,
        Copy             = 1U << 8,
        DateExt          = 1U << 9,
        Shred            = 1U << 10,
        Su               = 1U << 11,
        DateYesterday    = 1U << 12,
        OldDirCreate     = 1U << 13,
        TmpFilename      = 1U << 14,
        DateHourAgo      = 1U << 15,
        AllowHardLink    = 1U << 16,
        IgnoreDuplicates = 1U << 17,
    }

    public enum Criterium
    {
        ROT_HOURLY,
        ROT_DAYS,
        ROT_WEEKLY,
        ROT_MONTHLY,
        ROT_YEARLY,
        ROT_SIZE
    }

    /// <summary>
    /// Sentinel values for "-1" (not set) on POSIX fields.
    /// </summary>
    public static class Sentinel
    {
        public const long NO_MODE = -1L;
        public const long NO_UID = -1L;
        public const long NO_GID = -1L;
    }

    /// <summary>
    /// Mirror of the C 'struct logInfo'.
    /// </summary>
    public class LogInfo
    {
        public string? Pattern { get; set; }
        public List<string> Files { get; } = new List<string>();
        public string? OldDir { get; set; }
        public Criterium Criterium = Criterium.ROT_SIZE;
        public int Weekday { get; set; }              /* used by ROT_WEEKLY only */
        public long Threshold = 0; //me 1024 * 1024;         /* default: 1 MB (ROT_SIZE) */
        public long MaxSize { get; set; }
        public long MinSize { get; set; }
        public int RotateCount { get; set; }
        public int RotateMinAge { get; set; }
        public int RotateAge { get; set; }
        public int LogStart { get; set; } = 1;
        public string? Pre { get; set; }
        public string? Post { get; set; }
        public string? First { get; set; }
        public string? Last { get; set; }
        public string? PreRemove { get; set; }
        public string? MailCmd { get; set; }
        public bool MailAsScript { get; set; }
        public string? LogAddress { get; set; }
        public string? Extension { get; set; }
        public string? AddExtension { get; set; }
        public string? CompressProg { get; set; }
        public string? UncompressProg { get; set; }
        public string? CompressExt { get; set; }
        public string? DateFormat { get; set; }   /* specify format for strftime (for dateext) */
        public LogFlags Flags = LogFlags.IfEmpty;

        public int ShredCycles { get; set; }       /* if != 0, pass -n shred_cycles to GNU shred */
        public long CreateMode = Sentinel.NO_MODE;
        public long CreateUid = Sentinel.NO_UID;
        public long CreateGid = Sentinel.NO_GID;
        public string? CreateOwnerSid;
        public string? CreateGroupSid;
        public long SuUid = Sentinel.NO_UID;
        public long SuGid = Sentinel.NO_GID;
        public string? SuOwnerSid;
        public string? SuGroupSid;
        public string? SuPassword;
        public long OlddirMode = Sentinel.NO_MODE;
        public long OlddirUid = Sentinel.NO_UID;
        public long OlddirGid = Sentinel.NO_GID;
        

        public List<string> CompressOptions { get; } = new List<string>();

        public List<string> UnCompressOptions { get; } = new List<string>();
    }
}