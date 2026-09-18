using L = Wandur.Core.Localization.Strings;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Wandur.Desktop.Security;

/// <summary>Generic passwords in the user's macOS Keychain via Security.framework. No shell or password arguments.</summary>
public sealed class MacPasswordVault : IPasswordVault
{
    public string Description => L.KeychainName;
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int NotFound = -25300, Duplicate = -25299;
    private static readonly Lazy<nint> SecurityHandle = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<nint> CfHandle = new(() => NativeLibrary.Load(CoreFoundation));
    private static nint Sec(string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle.Value, symbol));
    private static nint Cf(string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(CfHandle.Value, symbol));

    public Task<string?> ReadAsync(string key) => Task.Run(() =>
    {
        using var query = Query(key);
        CFDictionarySetValue(query.Handle, Sec("kSecReturnData"), Cf("kCFBooleanTrue"));
        var status = SecItemCopyMatching(query.Handle, out var data);
        if (status == NotFound) return null;
        Check(status);
        try
        {
            var length = checked((int)CFDataGetLength(data));
            if (length > 8192) throw new InvalidOperationException(L.SavedPasswordIsTooLarge);
            var bytes = new byte[length];
            try { Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length); return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CFRelease(data); }
    });

    public Task WriteAsync(string key, string password) => Task.Run(() =>
    {
        PasswordVault.ValidatePassword(password);
        using var query = Query(key);
        using var attributes = new Dictionary();
        var bytes = Encoding.UTF8.GetBytes(password);
        nint data = 0;
        try
        {
            data = CFDataCreate(0, bytes, bytes.Length);
            CFDictionarySetValue(attributes.Handle, Sec("kSecValueData"), data);
            var status = SecItemUpdate(query.Handle, attributes.Handle);
            if (status == NotFound)
            {
                CFDictionarySetValue(query.Handle, Sec("kSecValueData"), data);
                status = SecItemAdd(query.Handle, 0);
                if (status == Duplicate) status = SecItemUpdate(query.Handle, attributes.Handle);
            }
            Check(status);
        }
        finally { if (data != 0) CFRelease(data); CryptographicOperations.ZeroMemory(bytes); }
    });

    public Task DeleteAsync(string key) => Task.Run(() =>
    {
        using var query = Query(key);
        var status = SecItemDelete(query.Handle);
        if (status != NotFound) Check(status);
    });

    private static Dictionary Query(string key)
    {
        var result = new Dictionary();
        CFDictionarySetValue(result.Handle, Sec("kSecClass"), Sec("kSecClassGenericPassword"));
        result.String("kSecAttrService", "net.wandur.world-login");
        result.String("kSecAttrAccount", key);
        return result;
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException(L.Format(L.KeychainCouldNotCompleteTheRequestCodeUnlockYour, status));
    }

    private sealed class Dictionary : IDisposable
    {
        public nint Handle { get; } = CFDictionaryCreateMutable(0, 0,
            NativeLibrary.GetExport(CfHandle.Value, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CfHandle.Value, "kCFTypeDictionaryValueCallBacks"));
        public void String(string name, string value)
        {
            var text = CFStringCreateWithCString(0, value, 0x08000100);
            try { CFDictionarySetValue(Handle, Sec(name), text); }
            finally { CFRelease(text); }
        }
        public void Dispose() => CFRelease(Handle);
    }

    [DllImport(Security)] private static extern int SecItemCopyMatching(nint query, out nint result);
    [DllImport(Security)] private static extern int SecItemAdd(nint attributes, nint result);
    [DllImport(Security)] private static extern int SecItemUpdate(nint query, nint attributes);
    [DllImport(Security)] private static extern int SecItemDelete(nint query);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
    [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(nint data);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
}
