using System;
using System.Collections.Generic;
using System.Text;

namespace LogRotate.Consts;

public static class ConfigSectionDirectives
{
    public const string Compress = "compress";
    public const string NoCompress = "nocompress";
    public const string Copy = "copy";
    public const string NoCopy = "nocopy";
    public const string CopyTruncate = "copytruncate";
    public const string NoCopyTruncate = "nocopytruncate";
    public const string RenameCopy = "renamecopy";
    public const string NoRenameCopy = "norenamecopy";
    public const string Create = "create";
    public const string NoCreate = "nocreate";
    public const string Hourly = "hourly";
    public const string Daily = "daily";
    public const string DelayCompress = "delaycompress";
    public const string NoDelayCompress = "nodelaycompress";
    public const string IfEmpty = "ifempty";
    public const string NotIfEmpty = "notifempty";
    public const string MissingOk = "missingok";
    public const string NoMissingOk = "nomissingok";
    public const string IgnoreDuplicates = "ignoreduplicates";
    public const string Monthly = "monthly";
    public const string SharedScripts = "sharedscripts";
    public const string NoSharedScripts = "nosharedscripts";
    public const string Weekly = "weekly";
    public const string Yearly = "yearly";
    public const string CompressCmd = "compresscmd";
    public const string UncompressCmd = "uncompresscmd";
    public const string CompressExt = "compressext";
    public const string CompressOptions = "compressoptions";
    public const string DateFormat = "dateformat";
    public const string Mail = "mail";
    public const string NoMail = "nomail";
    public const string MaxAge = "maxage";
    public const string MinAge = "minage";
    public const string OldDir = "olddir";
    public const string NoOldDir = "noolddir";
    public const string CreateOldDir = "createolddir";
    public const string NoCreateOldDir = "nocreateolddir";
    public const string Rotate = "rotate";
    public const string MinSize = "minsize";
    public const string MaxSize = "maxsize";
    public const string Shred = "shred";
    public const string NoShred = "noshred";
    public const string ShredCycles = "shredcycles";
    public const string Extension = "extension";
    public const string AddExtension = "addextension";
    public const string Start = "start";
    public const string PostRotate = "postrotate";
    public const string PreRotate = "prerotate";
    public const string FirstAction = "firstaction";
    public const string LastAction = "lastaction";
    public const string Preremove = "preremove";
    public const string Size = "size";
    public const string MailFirst = "mailfirst";
    public const string MailLast = "maillast";
    public const string DateExt = "dateext";
    public const string NoDateExt = "nodateext";
    public const string DateYesterday = "dateyesterday";
    public const string NoDateYesterday = "nodateyesterday";
    public const string DateHourAgo = "datehourago";
    public const string NoDateHourAgo = "nodatehourago";
    public const string TabooExt = "tabooext";
    public const string TabooPat = "taboopat";
    public const string Include = "include";

    //added in logrotatewin
    //public const string LogfileOpenRetry = "logfileopen_retry";
    //public const string LogfileOpenMsBetweenRetryAttempts = "logfileopen_msbetweenretryattempts";
    //public const string LogfileOpenNumRetryAttempts = "logfileopen_numretryattempts";
    //public const string Minutes = "minutes";

    //removed in logrotatewin
    public const string AllowHardlink = "allowhardlink";
    public const string NoAllowHardlink = "noallowhardlink";
    public const string Su = "su";
    public const string Errors = "errors";


    //custom non-standart directives
    public const string MailScript = "mailscript";
    public const string UnCompressOptions = "uncompressoptions";
}