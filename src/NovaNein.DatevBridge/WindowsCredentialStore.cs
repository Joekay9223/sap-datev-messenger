using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NovaNein.DatevBridge;

internal sealed record NetworkCredentialValue(string UserName, string Password);

internal static class WindowsCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;

    public static void Write(string target, string userName, string password)
    {
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Ziel, Benutzername und Kennwort sind erforderlich.");
        var bytes = Encoding.Unicode.GetBytes(password);
        try
        {
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = GenericCredential,
                    TargetName = target,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = LocalMachinePersistence,
                    UserName = userName
                };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Der DATEV-Dateiserver-Zugang konnte nicht im Windows Credential Manager gespeichert werden.");
            }
            finally { Marshal.FreeCoTaskMem(blob); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static NetworkCredentialValue Read(string target)
    {
        EnsureWindows();
        if (!CredRead(target, GenericCredential, 0, out var pointer))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Der DATEV-Dateiserver-Zugang fehlt im Windows Credential Manager.");
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var password = credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrEmpty(password))
                throw new InvalidDataException("Der gespeicherte DATEV-Dateiserver-Zugang ist unvollständig.");
            return new(credential.UserName, password);
        }
        finally { CredFree(pointer); }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Der Windows Credential Manager ist nur unter Windows verfügbar.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref NativeCredential credential, uint flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
