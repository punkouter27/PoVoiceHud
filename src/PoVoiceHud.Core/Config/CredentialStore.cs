using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PoVoiceHud.Core.Config;

/// <summary>Generic credentials in Windows Credential Manager. Targets are "PoVoiceHud/&lt;name&gt;".</summary>
public static partial class CredentialStore
{
    public const string FoundryKey = "PoVoiceHud/FoundryKey";
    /// <summary>A GitHub or Azure DevOps personal access token for a private code repository.</summary>
    public const string RepoToken = "PoVoiceHud/RepoToken";
    /// <summary>The incoming-webhook URL meeting summaries are posted to: Slack, Teams (Workflows), Discord or any that takes JSON.</summary>
    public const string ShareWebhook = "PoVoiceHud/ShareWebhook";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static void Write(string target, string secret)
    {
        var blobBytes = Encoding.Unicode.GetBytes(secret);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var blobPtr = Marshal.AllocHGlobal(blobBytes.Length);
        try
        {
            Marshal.Copy(blobBytes, 0, blobPtr, blobBytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                UserName = targetPtr,
                CredentialBlobSize = (uint)blobBytes.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWriteW(in cred, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static string? Read(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out var credPtr))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(credPtr);
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDeleteW(target, CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() != ErrorNotFound)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    // Mirrors wincred.h CREDENTIALW; FILETIME split into two uints to stay blittable.
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public int Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(in Credential credential, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(nint buffer);
}
