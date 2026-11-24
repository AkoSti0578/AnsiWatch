using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WixToolset.Dtf.WindowsInstaller;

namespace AnsiWatch.Installer.CustomActions
{
    /// <summary>
    /// Normaliseert en controleert het serviceaccount uit SERVICEACCOUNT / SERVICEPASSWORD.
    /// Zet de properties die ServiceInstall, util:User (LogonAsService) en util:PermissionEx gebruiken:
    ///   SVC_ACCOUNT   canoniek "DOMEIN\gebruiker" (of "LocalSystem")
    ///   SVC_DOMAIN    domein (leeg voor lokale accounts)
    ///   SVC_USER      gebruikersnaam (leeg voor ingebouwde accounts)
    ///   SVC_IS_GMSA   "1" voor een group Managed Service Account (naam eindigt op $)
    ///   SVC_IS_BUILTIN "1" voor LocalSystem/LocalService/NetworkService
    /// Het wachtwoord wordt nooit gelogd.
    /// </summary>
    public static class ServiceAccountActions
    {
        /// <summary>Execute-sequence: normaliseert; faalt met duidelijke melding bij ongeldige gegevens.</summary>
        [CustomAction]
        public static ActionResult PrepareServiceAccount(Session session)
        {
            var error = Prepare(session);
            if (error == null)
            {
                return ActionResult.Success;
            }

            session.Log("AnsiWatch: " + error);
            using (var record = new Record(1))
            {
                record.FormatString = "[1]";
                record[1] = "AnsiWatch: " + error;
                session.Message(InstallMessage.Error, record);
            }

            return ActionResult.Failure;
        }

        /// <summary>UI-sequence (knop Volgende): zet SVC_VALIDATION_ERROR in plaats van te falen.</summary>
        [CustomAction]
        public static ActionResult ValidateServiceAccountUI(Session session)
        {
            session["SVC_VALIDATION_ERROR"] = Prepare(session) ?? string.Empty;
            return ActionResult.Success;
        }

        private static string Prepare(Session session)
        {
            session["SVC_ACCOUNT"] = string.Empty;
            session["SVC_DOMAIN"] = string.Empty;
            session["SVC_USER"] = string.Empty;
            session["SVC_IS_GMSA"] = string.Empty;
            session["SVC_IS_BUILTIN"] = string.Empty;

            var raw = (session["SERVICEACCOUNT"] ?? string.Empty).Trim().Trim('"');
            var password = session["SERVICEPASSWORD"] ?? string.Empty;

            if (raw.Length == 0 || IsLocalSystem(raw))
            {
                session.Log("AnsiWatch: geen serviceaccount opgegeven; de service draait als LocalSystem.");
                session["SVC_ACCOUNT"] = "LocalSystem";
                session["SVC_IS_BUILTIN"] = "1";
                return null;
            }

            var builtin = BuiltinAccount(raw);
            if (builtin != null)
            {
                session["SVC_ACCOUNT"] = builtin;
                session["SVC_IS_BUILTIN"] = "1";
                session.Log("AnsiWatch: ingebouwd serviceaccount " + builtin + ".");
                return null;
            }

            var name = raw.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + raw.Substring(1) : raw;
            string canonical;
            try
            {
                var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
                canonical = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException)
            {
                return "Het serviceaccount '" + raw + "' bestaat niet of kan niet worden opgezocht (" + ex.Message + "). " +
                       "Gebruik DOMEIN\\gebruiker, .\\gebruiker of DOMEIN\\gmsa$.";
            }

            var slash = canonical.IndexOf('\\');
            var domain = slash > 0 ? canonical.Substring(0, slash) : string.Empty;
            var user = slash > 0 ? canonical.Substring(slash + 1) : canonical;
            var isLocal = string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            var isGmsa = user.EndsWith("$", StringComparison.Ordinal);

            session["SVC_ACCOUNT"] = canonical;
            session["SVC_DOMAIN"] = isLocal ? string.Empty : domain;
            session["SVC_USER"] = user;
            session["SVC_IS_GMSA"] = isGmsa ? "1" : string.Empty;
            session.Log("AnsiWatch: serviceaccount '" + raw + "' -> '" + canonical + "'" + (isGmsa ? " (gMSA, geen wachtwoord)" : string.Empty) + ".");

            if (isGmsa)
            {
                if (password.Length > 0)
                {
                    session.Log("AnsiWatch: SERVICEPASSWORD wordt genegeerd voor een gMSA.");
                    session["SERVICEPASSWORD"] = string.Empty;
                }

                return null;
            }

            if (password.Length == 0)
            {
                return string.IsNullOrEmpty(session["WIX_UPGRADE_DETECTED"])
                    ? "Geef het wachtwoord van '" + canonical + "' op (SERVICEPASSWORD). Alleen een gMSA (naam eindigt op $) heeft geen wachtwoord."
                    : "Bij een upgrade moet het wachtwoord van '" + canonical + "' opnieuw opgegeven worden (SERVICEPASSWORD); AnsiWatch slaat wachtwoorden nooit op.";
            }

            if (session["SKIPACCOUNTVALIDATION"] == "1")
            {
                session.Log("AnsiWatch: wachtwoordcontrole overgeslagen (SKIPACCOUNTVALIDATION=1).");
                return null;
            }

            return ValidateCredentials(session, isLocal ? "." : domain, user, password, canonical);
        }

        private static string ValidateCredentials(Session session, string domain, string user, string password, string display)
        {
            IntPtr token;
            if (LogonUser(user, domain, password, Logon32LogonNetwork, Logon32ProviderDefault, out token))
            {
                CloseHandle(token);
                session.Log("AnsiWatch: aanmeldgegevens van '" + display + "' zijn gecontroleerd.");
                return null;
            }

            var code = Marshal.GetLastWin32Error();
            switch (code)
            {
                case 1326: // ERROR_LOGON_FAILURE
                    return "Onjuiste gebruikersnaam of wachtwoord voor '" + display + "'.";
                case 1327: // ERROR_ACCOUNT_RESTRICTION
                case 1331: // ERROR_ACCOUNT_DISABLED
                case 1909: // ERROR_ACCOUNT_LOCKED_OUT
                case 1793: // ERROR_ACCOUNT_EXPIRED
                case 1330: // ERROR_PASSWORD_EXPIRED
                case 1907: // ERROR_PASSWORD_MUST_CHANGE
                    return "Het account '" + display + "' kan zich niet aanmelden: " + new Win32Exception(code).Message;
                default:
                    // Bijv. 1385 (aanmeldingstype niet toegestaan via beleid) of geen DC bereikbaar: niet blokkeren.
                    session.Log("AnsiWatch: wachtwoord kon niet definitief gecontroleerd worden (" + code + ": " + new Win32Exception(code).Message + "); installatie gaat door.");
                    return null;
            }
        }

        private static bool IsLocalSystem(string account)
        {
            var a = account.ToUpperInvariant();
            return a == "LOCALSYSTEM" || a == @".\LOCALSYSTEM" || a == @"NT AUTHORITY\SYSTEM" || a == "SYSTEM";
        }

        private static string BuiltinAccount(string account)
        {
            switch (account.ToUpperInvariant())
            {
                case "LOCALSERVICE":
                case @"NT AUTHORITY\LOCALSERVICE":
                case @"NT AUTHORITY\LOCAL SERVICE":
                    return @"NT AUTHORITY\LocalService";
                case "NETWORKSERVICE":
                case @"NT AUTHORITY\NETWORKSERVICE":
                case @"NT AUTHORITY\NETWORK SERVICE":
                    return @"NT AUTHORITY\NetworkService";
                default:
                    return null;
            }
        }

        private const int Logon32LogonNetwork = 3;
        private const int Logon32ProviderDefault = 0;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string user, string domain, string password, int logonType, int logonProvider, out IntPtr token);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
