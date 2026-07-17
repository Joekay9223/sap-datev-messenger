using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace NovaNein.Server;

public static class WindowsCredentialManager
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct CREDENTIAL
	{
		public uint Flags;

		public uint Type;

		public nint TargetName;

		public nint Comment;

		public FILETIME LastWritten;

		public uint CredentialBlobSize;

		public nint CredentialBlob;

		public uint Persist;

		public uint AttributeCount;

		public nint Attributes;

		public nint TargetAlias;

		public nint UserName;
	}

	private const uint CRED_TYPE_GENERIC = 1u;

	private const uint CRED_TYPE_DOMAIN_PASSWORD = 2u;

	public static bool HasCredential(string targetName)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(targetName))
		{
			return false;
		}
		uint[] array = new uint[2] { 1u, 2u };
		foreach (uint type in array)
		{
			nint credential = IntPtr.Zero;
			try
			{
				if (CredRead(targetName.Trim(), type, 0u, out credential) && credential != IntPtr.Zero)
				{
					return true;
				}
			}
			finally
			{
				if (credential != IntPtr.Zero)
				{
					CredFree(credential);
				}
			}
		}
		return false;
	}

	public static bool TryReadSecret(string targetName, out string userName, out string secret)
	{
		userName = string.Empty;
		secret = string.Empty;
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(targetName))
		{
			return false;
		}
		uint[] array = new uint[2] { 1u, 2u };
		foreach (uint type in array)
		{
			nint pointer = IntPtr.Zero;
			try
			{
				if (!CredRead(targetName.Trim(), type, 0u, out pointer) || pointer == IntPtr.Zero)
				{
					continue;
				}
				CREDENTIAL native = Marshal.PtrToStructure<CREDENTIAL>(pointer);
				userName = ((native.UserName == IntPtr.Zero) ? string.Empty : (Marshal.PtrToStringUni(native.UserName) ?? string.Empty));
				if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize == 0)
				{
					return false;
				}
				if (native.CredentialBlobSize > 1048576)
				{
					return false;
				}
				byte[] bytes = new byte[native.CredentialBlobSize];
				Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
				secret = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
				return !string.IsNullOrEmpty(secret);
			}
			finally
			{
				if (pointer != IntPtr.Zero)
				{
					CredFree(pointer);
				}
			}
		}
		return false;
	}

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern void CredFree(nint credential);
}
