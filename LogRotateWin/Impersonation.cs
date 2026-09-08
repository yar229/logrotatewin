using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LogRotate
{
    /// <summary>
    /// Logs on as another Windows user (LOGON32_LOGON_BATCH) and runs code
    /// impersonated as that account, i.e. the Windows equivalent of the
    /// reference's switch_user(). Used to run config scripts (prerotate,
    /// postrotate, ...) as the user named by the 'su' directive when the
    /// custom 'supasswd' directive provides the account password.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class Impersonation
    {
        private const int LOGON32_LOGON_BATCH = 4;
        private const int LOGON32_PROVIDER_DEFAULT = 0;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LogonUser(string lpszUsername, string lpszDomain,
            string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        /// <summary>
        /// Runs the given action impersonated as the 'su' account when
        /// 'su' + 'supasswd' are configured and the account differs from the
        /// process user. When no impersonation is wanted or the logon fails
        /// (a warning is logged), the action runs under the current account.
        /// </summary>
        public static int Run(LogInfo log, Func<int> action)
        {
            if ((log.Flags & LogFlags.Su) == 0 || log.SuOwnerSid == null
                    || string.IsNullOrEmpty(log.SuPassword))
                return action();

            try
            {
                if (new SecurityIdentifier(log.SuOwnerSid)
                        .Equals(WindowsIdentity.GetCurrent().User))
                    return action();
            }
            catch (Exception)
            {
                /* cannot compare identities; just try to impersonate */
            }

            using (var token = Logon(log.SuOwnerSid, log.SuPassword))
            {
                return token == null
                    ? action()
                    : WindowsIdentity.RunImpersonated(token, action);
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