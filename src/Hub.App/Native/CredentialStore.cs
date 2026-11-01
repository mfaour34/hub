using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Hub.App.Native;

/// <summary>
/// The backup token in Windows Credential Manager (a generic credential of the signed-in user, encrypted by Windows
/// with the user's logon secret, like Git for Windows and VS Code keep theirs). Never written to Hub's own files.
/// </summary>
public static class CredentialStore
{
    /// <summary>A Hub run with its own HUB_DATA_DIR (portable, development) keeps its own token, apart from the installed Hub's.</summary>
    static string Target => Environment.GetEnvironmentVariable("HUB_DATA_DIR") is { Length: > 0 } dir
        ? $"Hub/GitHubBackup:{Path.GetFullPath(dir)}" : "Hub/GitHubBackup";
    const int CredTypeGeneric = 1, CredPersistLocalMachine = 2, ErrorNotFound = 1168;

    public static void Save(string repo, string token)
    {
        var blob = Encoding.UTF8.GetBytes(token);
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric, TargetName = Target, UserName = repo, Comment = "Hub settings backup token",
                CredentialBlobSize = blob.Length, CredentialBlob = ptr, Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't store the token in Windows Credential Manager");
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, ptr, blob.Length);
            Marshal.FreeHGlobal(ptr);
            Array.Clear(blob);
        }
    }

    /// <summary>The stored token, or null when there is none.</summary>
    public static string? Read()
    {
        if (!CredRead(Target, CredTypeGeneric, 0, out var p))
        {
            int e = Marshal.GetLastWin32Error();
            return e == ErrorNotFound ? null : throw new Win32Exception(e, "Couldn't read the token from Windows Credential Manager");
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(p);
            if (cred.CredentialBlobSize == 0) return null;
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            try { return Encoding.UTF8.GetString(blob); }
            finally { Array.Clear(blob); }
        }
        finally { CredFree(p); }
    }

    public static void Delete()
    {
        if (!CredDelete(Target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() is var e and not ErrorNotFound)
            throw new Win32Exception(e, "Couldn't remove the token from Windows Credential Manager");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
