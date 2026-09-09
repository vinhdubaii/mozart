using System;
using System.Runtime.InteropServices;

namespace MozartBrowser.Interop
{
    /// <summary>
    /// Re-checks the current Windows account's password before revealing a
    /// saved password as plaintext in PasswordManagerWindow — matching how
    /// Chrome/Edge gate this behind Windows Hello.
    ///
    /// True Windows Hello (Windows.Security.Credentials.UI.UserConsentVerifier)
    /// is a WinRT API that needs the project's TargetFramework switched to a
    /// Windows-SDK-versioned one (net8.0-windows10.0.xxxxx.0) plus a WinRT
    /// projection package — a bigger, riskier csproj change than this feature
    /// warrants (this project has been bitten by exactly this kind of build
    /// breakage before). LogonUser is the same trust boundary in practice —
    /// "prove you know this Windows account's password right now" — using
    /// only a plain Win32 P/Invoke already available via advapi32.dll, no new
    /// package, no TFM change. If you later want real Windows Hello (fingerprint/
    /// face/PIN) instead of typing the password again, that's a follow-up, not
    /// part of this pass.
    /// </summary>
    internal static class WindowsCredentialVerifier
    {
        private const int LOGON32_LOGON_INTERACTIVE = 2;
        private const int LOGON32_PROVIDER_DEFAULT = 0;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(
            string lpszUsername, string? lpszDomain, string lpszPassword,
            int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>True if <paramref name="password"/> is the correct password for the currently logged-in Windows account.</summary>
        public static bool VerifyCurrentUserPassword(string password)
        {
            var success = LogonUser(
                Environment.UserName,
                Environment.UserDomainName,
                password,
                LOGON32_LOGON_INTERACTIVE,
                LOGON32_PROVIDER_DEFAULT,
                out var token);

            if (success)
                CloseHandle(token);

            return success;
        }
    }
}
