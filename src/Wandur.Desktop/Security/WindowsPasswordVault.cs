using L = Wandur.Core.Localization.Strings;
using System.Runtime.InteropServices;

namespace Wandur.Desktop.Security;

public sealed class WindowsPasswordVault : IPasswordVault
{
    public string Description => L.WindowsVaultName;
    private static string Target(string key) => "Wandur/world-login/" + key;
    private const uint Generic = 1, LocalMachine = 2;
    private const int NotFound = 1168;

    public Task<string?> ReadAsync(string key) => Task.Run(() =>
    {
        if (!CredReadW(Target(key), Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw Failure(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 8192 || credential.BlobSize % 2 != 0) throw new InvalidOperationException(L.SavedPasswordHasAnInvalidFormat);
            return Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
        }
        finally { CredFree(pointer); }
    });

    public Task WriteAsync(string key, string password) => Task.Run(() =>
    {
        PasswordVault.ValidatePassword(password);
        var pointer = Marshal.StringToHGlobalUni(password);
        try
        {
            var credential = new Credential { Type = Generic, TargetName = Target(key), UserName = "Wandur", Persist = LocalMachine, Blob = pointer, BlobSize = (uint)password.Length * 2 };
            if (!CredWriteW(ref credential, 0)) throw Failure(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    });

    public Task DeleteAsync(string key) => Task.Run(() =>
    {
        if (CredDeleteW(Target(key), Generic, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != NotFound) throw Failure(error);
    });

    private static Exception Failure(int error) => new InvalidOperationException(L.Format(L.WindowsCredentialManagerCouldNotCompleteTheRequestCode, error));
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
