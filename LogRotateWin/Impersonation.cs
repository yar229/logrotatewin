using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LogRotate
{
    /// <summary>
    /// Logs on as another Windows user (LOGON32_LOGON_BATCH) and launches
    /// child processes as that account, i.e. the Windows equivalent of the
    /// reference's switch_user(). Used to run config scripts (prerotate,
    /// postrotate, ...) as the user named by the 'su' directive when the
    /// custom 'supasswd' directive provides the account password.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class Impersonation
    {
        /// <summary>
        /// Credentials of the account scripts are to run under. A non-null
        /// value means ProcessRunner must spawn cmd.exe as that account
        /// (via CreateProcessWithLogonW + LOGON_WITH_PROFILE) instead of the
        /// current user.
        /// </summary>
        public readonly struct Account
        {
            public readonly string User;
            public readonly string Domain;
            public readonly string Password;
            public Account(string user, string domain, string password)
            {
                User = user;
                Domain = domain;
                Password = password;
            }
        }
        private const int LOGON32_LOGON_BATCH = 4;
        private const int LOGON32_PROVIDER_DEFAULT = 0;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LogonUser(string lpszUsername, string lpszDomain,
            string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        /// <summary>
        /// True when scripts for this log are to be run under another account
        /// ('su' + 'supasswd' configured and the account differs from the
        /// process identity).
        /// </summary>
        public static bool WouldImpersonate(LogInfo log)
        {
            if ((log.Flags & LogFlags.Su) == 0 || log.SuOwnerSid == null
                    || string.IsNullOrEmpty(log.SuPassword))
                return false;

            try
            {
                if (new SecurityIdentifier(log.SuOwnerSid)
                        .Equals(WindowsIdentity.GetCurrent().User))
                    return false;
            }
            catch (Exception)
            {
                /* cannot compare identities; assume impersonation is needed */
            }
            return true;
        }

        /// <summary>
        /// Directory for the temp scripts of an impersonated log. Scripts must
        /// be readable/writable by the impersonated account, so they cannot
        /// live under the current user's profile (intermediate directories
        /// block the other account). A directory under C:\ProgramData (whose
        /// path the built-in Users group can traverse) is created up-front by
        /// the current (privileged) process with an explicit ACL granting the
        /// impersonated account Modify access.
        /// </summary>
        private static readonly object _scriptsDirLock = new object();
        private static readonly Dictionary<string, string> _scriptsDirCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static string? ScriptsDirectory(LogInfo log)
        {
            if (!WouldImpersonate(log) || log.SuOwnerSid == null)
                return null;

            lock (_scriptsDirLock)
            {
                if (_scriptsDirCache.TryGetValue(log.SuOwnerSid, out var cached))
                    return cached;

                string dir = EnsureScriptsDirectory(log.SuOwnerSid);
                _scriptsDirCache[log.SuOwnerSid] = dir;
                return dir;
            }
        }

        private static string EnsureScriptsDirectory(string ownerSid)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LogRotateWin", "scripts", ownerSid.Replace("-", string.Empty));
            try
            {
                Directory.CreateDirectory(dir);
                var di = new DirectoryInfo(dir);
                var dacl = di.GetAccessControl();
                dacl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(ownerSid),
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(dacl);
            }
            catch (Exception ex)
            {
                Log.Message(MESS.WARN, "su impersonation: cannot prepare scripts directory {0}: {1}\n",
                    dir, ex.Message);
            }
            return dir;
        }

        /// <summary>
        /// Runs the given action, passing the directory where temp scripts
        /// must be created and (when 'su'+'supasswd' point at another account)
        /// the credentials scripts are to run under, plus the (already opened,
        /// valid for the duration of the action) logon token used to build the
        /// child's environment block. Child processes must be spawned with
        /// those credentials by the caller: a plain
        /// WindowsIdentity.RunImpersonated does NOT propagate to child
        /// processes, so scripts would keep running under the current account.
        /// The token is IntPtr.Zero when no impersonation takes place.
        /// </summary>
        public static int Run(LogInfo log, Func<string, Account?, IntPtr, int> action)
        {
            if (!WouldImpersonate(log))
            {
                return action(Path.GetTempPath(), null, IntPtr.Zero);
            }

            if (!TrySplitAccount(log.SuOwnerSid!, out string domain, out string user))
            {
                Log.Message(MESS.WARN, "su impersonation: cannot derive an account from SID {0}; "
                    + "scripts will run under the current account\n", log.SuOwnerSid);
                return action(Path.GetTempPath(), null, IntPtr.Zero);
            }

            /* prepare the scripts directory up-front (created by the current,
             * privileged process with an ACL the impersonated account can
             * traverse/read/write) before any impersonation happens */
            string? scriptDir = ScriptsDirectory(log);

            /* the single LogonUser for this run: the token is reused to build
             * the child's environment block (CreateEnvironmentBlock), so the
             * impersonated script needs only one logon, not two */
            using (var token = Logon(log.SuOwnerSid!, log.SuPassword!))
            {
                if (token == null)
                {
                    return action(Path.GetTempPath(), null, IntPtr.Zero);
                }

                return action(scriptDir ?? Path.GetTempPath(),
                    new Account(user, domain, log.SuPassword!),
                    token.DangerousGetHandle());
            }
        }

        private static SafeAccessTokenHandle? Logon(string userSid, string password)
        {
            if (!TrySplitAccount(userSid, out string domain, out string user))
            {
                Log.Message(MESS.WARN, "su impersonation: cannot derive an account from SID {0}; "
                    + "scripts will run under the current account\n", userSid);
                return null;
            }

            if (!LogonUser(user, domain, password, LOGON32_LOGON_BATCH,
                    LOGON32_PROVIDER_DEFAULT, out IntPtr token))
            {
                int error = Marshal.GetLastWin32Error();
                Log.Message(MESS.WARN, "su impersonation: LogonUser failed for {0}\\{1} "
                    + "(win32 error {2}); scripts will run under the current account\n",
                    domain, user, error);
                return null;
            }

            return new SafeAccessTokenHandle(token);
        }

        private static bool TrySplitAccount(string userSid, out string domain, out string user)
        {
            domain = ".";
            user = string.Empty;
            try
            {
                var account = new SecurityIdentifier(userSid).Translate(typeof(NTAccount)).ToString();
                int sep = account.IndexOf('\\');
                if (sep >= 0)
                {
                    domain = account.Substring(0, sep);
                    user = account.Substring(sep + 1);
                }
                else
                {
                    user = account;
                }
                return !string.IsNullOrEmpty(user);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}