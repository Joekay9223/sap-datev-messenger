[CmdletBinding()]
param(
    [string]$TargetName = "NovaNein/Gmail/invoices@example.invalid",
    [string]$Mailbox = "invoices@example.invalid"
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Der Windows Credential Manager ist nur unter Windows verfügbar." }
if ($Mailbox -ne "invoices@example.invalid") { throw "NovaNein darf ausschließlich invoices@example.invalid anbinden." }

$clientId = Read-Host "Google OAuth Client-ID"
$clientSecretSecure = Read-Host "Google OAuth Client-Secret" -AsSecureString
$refreshTokenSecure = Read-Host "Google OAuth Refresh-Token (gmail.modify und Pub/Sub subscriber)" -AsSecureString

function ConvertFrom-SecureStringPlain([Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

$clientSecret = ConvertFrom-SecureStringPlain $clientSecretSecure
$refreshToken = ConvertFrom-SecureStringPlain $refreshTokenSecure
try {
    $json = @{
        client_id = $clientId.Trim()
        client_secret = $clientSecret
        refresh_token = $refreshToken
    } | ConvertTo-Json -Compress

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NovaNeinCredentialWriter {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct CREDENTIAL {
    public UInt32 Flags; public UInt32 Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public UInt32 CredentialBlobSize; public IntPtr CredentialBlob; public UInt32 Persist;
    public UInt32 AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
  }
  [DllImport("advapi32.dll", EntryPoint="CredWriteW", CharSet=CharSet.Unicode, SetLastError=true)]
  private static extern bool CredWrite(ref CREDENTIAL credential, UInt32 flags);
  public static void Write(string target, string user, string secret) {
    byte[] bytes = System.Text.Encoding.Unicode.GetBytes(secret);
    if (bytes.Length > 5120) throw new ArgumentOutOfRangeException("secret");
    IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length + 2);
    try {
      Marshal.WriteInt16(blob, bytes.Length, 0);
      Marshal.Copy(bytes, 0, blob, bytes.Length);
      var credential = new CREDENTIAL {
        Type = 1, TargetName = target, UserName = user, CredentialBlob = blob,
        CredentialBlobSize = (UInt32)bytes.Length, Persist = 2,
        Comment = "NovaNein Gmail OAuth refresh token"
      };
      if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    } finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
  }
}
'@
    [NovaNeinCredentialWriter]::Write($TargetName, $Mailbox, $json)
    Write-Host "Gmail-OAuth-Credential wurde im Windows Credential Manager gespeichert." -ForegroundColor Green
}
finally {
    $clientSecret = $null
    $refreshToken = $null
    $json = $null
}
