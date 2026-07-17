using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NovaNein.DatevBridge;

internal static class NetworkShareConnection
{
    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 4;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorSessionCredentialConflict = 1219;

    public static void EnsureConnected(string shareRoot, NetworkCredentialValue credential)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SMB ist nur in der Windows-Bridge verfügbar.");
        var resource = new NetResource { ResourceType = ResourceTypeDisk, RemoteName = shareRoot };
        var result = WNetAddConnection2(ref resource, credential.Password, credential.UserName, ConnectTemporary);
        if (result is 0 or ErrorAlreadyAssigned) return;
        if (result == ErrorSessionCredentialConflict && Directory.Exists(shareRoot)) return;
        throw new Win32Exception(result, "Die SMB-Verbindung zum DATEV-Dateiserver konnte nicht hergestellt werden.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int ResourceType;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string password, string userName, int flags);
}
