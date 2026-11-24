using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AnsiWatch.Config.Services;

/// <summary>Resultaat van een rechtencontrole.</summary>
internal sealed record AccessCheckResult(bool? CanWrite, string Message);

/// <summary>
/// Bepaalt de EFFECTIEVE NTFS-rechten van het serviceaccount op een map met de Windows Authz-API
/// (inclusief groepslidmaatschappen, deny-regels en overerving) – dus niet de rechten van de
/// ingelogde beheerder. Sharerechten op UNC-paden worden hiermee niet gecontroleerd.
/// </summary>
internal static class AccessChecker
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileAddFile = 0x0002;
    private const uint FileTraverse = 0x0020;
    private const uint FileDeleteChild = 0x0040;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileWriteAttributes = 0x0100;
    private const uint Delete = 0x00010000;
    private const uint MaximumAllowed = 0x02000000;

    public static AccessCheckResult Check(string folder, string? serviceAccount)
    {
        if (!Directory.Exists(folder))
        {
            return new AccessCheckResult(false, "Map bestaat niet of is niet bereikbaar vanaf deze computer.");
        }

        SecurityIdentifier sid;
        string accountText;
        try
        {
            (sid, accountText) = ResolveAccount(serviceAccount);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return new AccessCheckResult(null, $"Serviceaccount '{serviceAccount}' kon niet worden opgezocht: {ex.Message}");
        }

        var isUnc = folder.StartsWith(@"\\", StringComparison.Ordinal);
        var shareNote = isUnc ? " Let op: controleer op de server ook de sharerechten (minimaal Wijzigen)." : string.Empty;
        if (isUnc && (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.LocalServiceSid)))
        {
            shareNote += " LocalSystem benadert een share als het computeraccount (DOMEIN\\COMPUTER$).";
        }

        try
        {
            var descriptor = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
                .GetSecurityDescriptorBinaryForm();
            var granted = GetGrantedAccess(sid, descriptor);

            const uint needed = FileListDirectory | FileAddFile | FileTraverse | FileReadAttributes | FileWriteAttributes;
            var canCreate = (granted & needed) == needed;
            var canReplace = (granted & (FileDeleteChild | Delete)) != 0;
            if (canCreate && canReplace)
            {
                return new AccessCheckResult(true, $"{accountText} heeft voldoende rechten (lezen, schrijven, vervangen).{shareNote}");
            }

            var missing = new List<string>();
            if ((granted & FileListDirectory) == 0)
            {
                missing.Add("map weergeven/lezen");
            }

            if ((granted & FileAddFile) == 0)
            {
                missing.Add("bestanden maken");
            }

            if (!canReplace)
            {
                missing.Add("verwijderen/vervangen (Wijzigen)");
            }

            if ((granted & FileWriteAttributes) == 0)
            {
                missing.Add("kenmerken schrijven");
            }

            return new AccessCheckResult(false, $"{accountText} mist: {string.Join(", ", missing)}. Geef minimaal 'Wijzigen' op deze map.{shareNote}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Win32Exception or PlatformNotSupportedException)
        {
            return new AccessCheckResult(null, $"Rechten konden niet bepaald worden: {ex.Message}{shareNote}");
        }
    }

    /// <summary>Vertaalt ObjectName uit de serviceregistratie naar een SID.</summary>
    public static (SecurityIdentifier Sid, string Display) ResolveAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            return (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), "LocalSystem");
        }

        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;
        var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
        return (sid, name);
    }

    private static uint GetGrantedAccess(SecurityIdentifier sid, byte[] securityDescriptor)
    {
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        if (!AuthzInitializeResourceManager(AuthzRmFlagNoAudit, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, out var resourceManager))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!AuthzInitializeContextFromSid(0, sidBytes, resourceManager, IntPtr.Zero, default, IntPtr.Zero, out var context))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var request = new AuthzAccessRequest { DesiredAccess = MaximumAllowed };
                var grantedBuffer = Marshal.AllocHGlobal(sizeof(uint));
                var errorBuffer = Marshal.AllocHGlobal(sizeof(uint));
                try
                {
                    var reply = new AuthzAccessReply { ResultListLength = 1, GrantedAccessMask = grantedBuffer, Error = errorBuffer };
                    if (!AuthzAccessCheck(0, context, ref request, IntPtr.Zero, securityDescriptor, null, 0, ref reply, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    return (uint)Marshal.ReadInt32(grantedBuffer);
                }
                finally
                {
                    Marshal.FreeHGlobal(grantedBuffer);
                    Marshal.FreeHGlobal(errorBuffer);
                }
            }
            finally
            {
                AuthzFreeContext(context);
            }
        }
        finally
        {
            AuthzFreeResourceManager(resourceManager);
        }
    }

    private const int AuthzRmFlagNoAudit = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthzAccessRequest
    {
        public uint DesiredAccess;
        public IntPtr PrincipalSelfSid;
        public IntPtr ObjectTypeList;
        public uint ObjectTypeListLength;
        public IntPtr OptionalArguments;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthzAccessReply
    {
        public uint ResultListLength;
        public IntPtr GrantedAccessMask;
        public IntPtr SaclEvaluationResults;
        public IntPtr Error;
    }

    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzInitializeResourceManager(int flags, IntPtr pfnAccessCheck, IntPtr pfnComputeDynamicGroups,
        IntPtr pfnFreeDynamicGroups, [MarshalAs(UnmanagedType.LPWStr)] string? name, out IntPtr resourceManager);

    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzInitializeContextFromSid(int flags, byte[] userSid, IntPtr resourceManager, IntPtr expirationTime,
        Luid identifier, IntPtr dynamicGroupArgs, out IntPtr clientContext);

    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzAccessCheck(int flags, IntPtr clientContext, ref AuthzAccessRequest request, IntPtr auditEvent,
        byte[] securityDescriptor, IntPtr[]? optionalSecurityDescriptors, int optionalSecurityDescriptorCount,
        ref AuthzAccessReply reply, IntPtr accessCheckResults);

    [DllImport("authz.dll")]
    private static extern bool AuthzFreeContext(IntPtr clientContext);

    [DllImport("authz.dll")]
    private static extern bool AuthzFreeResourceManager(IntPtr resourceManager);
}
