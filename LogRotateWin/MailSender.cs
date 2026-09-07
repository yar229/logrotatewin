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
            var result = ProcessRunner.RunScript(mailCommand, 
                (ScriptEnviromentVariables.Log, mailFilename), 
                (ScriptEnviromentVariables.MailTo, log.LogAddress));
            return result;
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
                var feed = TaskHelper.Run(() =>
                {
                    using var src = mailInput;
                    src.CopyTo(mail.StandardInput.BaseStream);
                });
                feed.GetAwaiter().GetResult();
            }
            else if (internalUncompress)
            {
                /* in-process gunzip: decompress the .gz log and pipe it into
                 * the mail command exactly the way the reference pipes it
                 * through an external gunzip. */
                var feed = TaskHelper.Run(() =>
                {
                    using (var gz = new System.IO.Compression.GZipStream(mailInput,
                        System.IO.Compression.CompressionMode.Decompress))
                    {
                        gz.CopyTo(mail.StandardInput.BaseStream);
                    }
                });
                feed.GetAwaiter().GetResult();
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
                    feed.GetAwaiter().GetResult();
                    up.StandardInput.Close();
                    pump.GetAwaiter().GetResult();
                    up.WaitForExit();
                    uncompressRc = up.ExitCode;
                }
            }

            mail.StandardInput.Close();
            mail.WaitForExit();
            rc = 0;

            if (mail.ExitCode != 0)
            {
                Log.Message(MESS.ERROR, "mail command failed for {0}\n", logFile);
                rc = 1;
            }
            if (uncompress != null && uncompressRc != 0)
            {
                Log.Message(MESS.ERROR, "uncompress command failed mailing {0}\n", logFile);
                rc = 1;
            }
        }
        return rc;
    }
}