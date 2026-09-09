using LogRotate.Consts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace LogRotate;

internal static class MailSender
{
    public static int MailLogWrapper(string mailFilename, string mailCommand,
                                      int logNum, LogInfo log)
    {
        if (string.IsNullOrEmpty(mailCommand))
        {
            Log.Message(MESS.DEBUG, "sending email for '{0}' skipped because mail command is empty\n", mailFilename);
            return 0;
        }

        if (log.MailAsScript)
        {
            var envs = new (string EnvVar, string Value)[]
            {
                (ScriptEnviromentVariables.Log, mailFilename),
                (ScriptEnviromentVariables.MailTo, log.LogAddress!)
            };
            return Impersonation.Run(log, (dir, account) =>
                ProcessRunner.RunScript(mailCommand, dir, account, envs));
        }

        /* The port never relies on external gzip/gunzip: compression is done
         * in-process, so the empty/default uncompress command is realized as
         * an in-process gunzip too. An explicitly configured uncompresscmd is
         * still spawned as an external program (mirrors the reference). */
        bool internalUncompress = (log.Flags & LogFlags.Compress) != 0
            && string.IsNullOrEmpty(log.UncompressProg);
        string? uncompressProg = (log.Flags & LogFlags.Compress) != 0
            ? (internalUncompress ? string.Empty : log.UncompressProg)
            : null;

        Log.Message(MESS.DEBUG, "executing mail command '{0}' for {1}\n", mailCommand, mailFilename);

        string subject = mailFilename;
        if ((log.Flags & LogFlags.MailFirst) != 0)
        {
            if ((log.Flags & LogFlags.DelayCompress) != 0)
                uncompressProg = null;
            if (uncompressProg != null)
                subject = log.Files[logNum];
        }

        return MailLog(log, mailFilename, mailCommand, uncompressProg,
                       internalUncompress, log.LogAddress!, subject);
    }

    /// <summary>
    /// Port of mailLog(): optionally decompress into a pipe feeding the mail
    /// command "mail -s subject address".
    /// </summary>
    private static int MailLog(LogInfo log, string logFile, string mailCommand,
                               string? uncompress, bool internalUncompress,
                               string address, string subject)
    {
        FileStream mailInput;
        try
        {
            mailInput = new FileStream(logFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex)
        {
            Log.Message(MESS.ERROR, "failed to open {0} for mailing: {1}\n", logFile, ex.Message);
            return 1;
        }

        int rc = 0;
        int uncompressRc = 0;
        using (mailInput)
        using (var mail = new Process())
        {
            mail.StartInfo = new ProcessStartInfo
            {
                FileName = mailCommand,
                UseShellExecute = false,  //me
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };
            mail.StartInfo.ArgumentList.Add("-s");
            mail.StartInfo.ArgumentList.Add(subject);
            mail.StartInfo.ArgumentList.Add(address);

            try
            {
                mail.Start();
            }
            catch (Exception ex)
            {
                Log.Message(MESS.ERROR, "cannot execute mail command: {0}\n", ex.Message);
                return 1;
            }

            if (uncompress == null)
            {
                rc |= FeedMailBody(() =>
                {
                    using var src = mailInput;
                    src.CopyTo(mail.StandardInput.BaseStream);
                });
            }
            else if (internalUncompress)
            {
                /* in-process gunzip: decompress the .gz log and pipe it into
                 * the mail command exactly the way the reference pipes it
                 * through an external gunzip. */
                rc |= FeedMailBody(() =>
                {
                    using (var gz = new System.IO.Compression.GZipStream(mailInput,
                        System.IO.Compression.CompressionMode.Decompress))
                    {
                        gz.CopyTo(mail.StandardInput.BaseStream);
                    }
                });
            }
            else
            {
                using (var up = new Process())
                {
                    up.StartInfo = new ProcessStartInfo
                    {
                        FileName = uncompress,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    };
                    foreach (var arg in log.UnCompressOptions)
                        up.StartInfo.ArgumentList.Add(arg);
                    
                    try
                    {
                        up.Start();
                    }
                    catch (Exception ex)
                    {
                        Log.Message(MESS.ERROR, "cannot execute uncompress command: {0}\n", ex.Message);
                        return 1;
                    }

                    /* pump: logFile -> uncompress stdin
                     *       uncompress stdout -> mail stdin */
                    var feed = TaskHelper.Run(() =>
                    {
                        using var src = mailInput;
                        src.CopyTo(up.StandardInput.BaseStream);
                    });
                    var pump = TaskHelper.Run(() =>
                    {
                        using var src = up.StandardOutput.BaseStream;
                        src.CopyTo(mail.StandardInput.BaseStream);
                    });
                    try
                    {
                        feed.GetAwaiter().GetResult();
                        up.StandardInput.Close();
                        pump.GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        /* the mail child may have exited without consuming the
                         * piped body, or the uncompress pipe closed early;
                         * feeding then throws and must not abort the run. */
                        Log.Message(MESS.ERROR,
                            "pipe to mail command broke while mailing {0}: {1}\n", logFile, ex.Message);
                        rc = 1;
                    }
                    up.WaitForExit();
                    uncompressRc = up.ExitCode;
                }
            }

            mail.StandardInput.Close();
            
            try
            {
                mail.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                /* process may already have exited after the broken pipe */
            }
            rc |= mail.ExitCode != 0 ? 1 : 0;

            if (mail.ExitCode != 0)
            {
                Log.Message(MESS.ERROR, "mail command failed for {0}\n", logFile);
            }
            if (uncompress != null && uncompressRc != 0)
            {
                Log.Message(MESS.ERROR, "uncompress command failed mailing {0}\n", logFile);
                rc = 1;
            }
        }
        return rc;
    }

    /// <summary>
    /// Feeds the mail command's stdin with the given body. If the mail (or an
    /// intermediate uncompress) child exits before consuming the whole pipe,
    /// writing into the broken pipe throws; that must be logged and reported
    /// instead of escaping and aborting the rotation before the state file is
    /// written.
    /// </summary>
    private static int FeedMailBody(Action feed)
    {
        try
        {
            var task = TaskHelper.Run(feed);
            task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            Log.Message(MESS.ERROR,
                "pipe to mail command broke while feeding the message body: {0}\n", ex.Message);
            return 1;
        }
    }
}