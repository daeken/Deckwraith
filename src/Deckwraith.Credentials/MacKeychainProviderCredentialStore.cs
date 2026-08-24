using System.Runtime.InteropServices;
using System.Text;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Credentials;

public sealed class MacKeychainProviderCredentialStore : IProviderCredentialStore
{
    private const string ServiceName = "local.deckwraith.credentials";
    private const int ItemNotFound = -25300;
    private static readonly SemaphoreSlim KeychainGate = new(1, 1);

    public MacKeychainProviderCredentialStore()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The macOS Keychain store is only available on macOS.");
        }
    }

    public string StorageKind => "macos-keychain";

    public async ValueTask<string?> ReadAsync(
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        await KeychainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = EncodeCredentialId(credentialId);
            var service = Encoding.UTF8.GetBytes(ServiceName);
            var result = NativeMethods.SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)service.Length),
                service,
                checked((uint)account.Length),
                account,
                out var passwordLength,
                out var passwordData,
                out var item);
            if (result == ItemNotFound)
            {
                return null;
            }

            ThrowIfFailed(result, "read");
            try
            {
                var bytes = new byte[passwordLength];
                if (passwordLength > 0)
                {
                    Marshal.Copy(passwordData, bytes, 0, checked((int)passwordLength));
                }

                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                if (passwordData != IntPtr.Zero)
                {
                    _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                }

                Release(item);
            }
        }
        finally
        {
            KeychainGate.Release();
        }
    }

    public async ValueTask WriteAsync(
        string credentialId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await KeychainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = EncodeCredentialId(credentialId);
            var service = Encoding.UTF8.GetBytes(ServiceName);
            var secret = Encoding.UTF8.GetBytes(payload);
            var result = NativeMethods.SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)service.Length),
                service,
                checked((uint)account.Length),
                account,
                out _,
                out var existingData,
                out var existingItem);
            if (existingData != IntPtr.Zero)
            {
                _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, existingData);
            }

            if (result == ItemNotFound)
            {
                result = NativeMethods.SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    checked((uint)service.Length),
                    service,
                    checked((uint)account.Length),
                    account,
                    checked((uint)secret.Length),
                    secret,
                    out var addedItem);
                try
                {
                    ThrowIfFailed(result, "write");
                }
                finally
                {
                    Release(addedItem);
                }

                return;
            }

            try
            {
                ThrowIfFailed(result, "find before write");
                result = NativeMethods.SecKeychainItemModifyAttributesAndData(
                    existingItem,
                    IntPtr.Zero,
                    checked((uint)secret.Length),
                    secret);
                ThrowIfFailed(result, "write");
            }
            finally
            {
                Release(existingItem);
            }
        }
        finally
        {
            KeychainGate.Release();
        }
    }

    public async ValueTask DeleteAsync(
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        await KeychainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = EncodeCredentialId(credentialId);
            var service = Encoding.UTF8.GetBytes(ServiceName);
            var result = NativeMethods.SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)service.Length),
                service,
                checked((uint)account.Length),
                account,
                out _,
                out var existingData,
                out var item);
            if (existingData != IntPtr.Zero)
            {
                _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, existingData);
            }

            if (result == ItemNotFound)
            {
                return;
            }

            try
            {
                ThrowIfFailed(result, "find before delete");
                ThrowIfFailed(NativeMethods.SecKeychainItemDelete(item), "delete");
            }
            finally
            {
                Release(item);
            }
        }
        finally
        {
            KeychainGate.Release();
        }
    }

    private static byte[] EncodeCredentialId(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        if (credentialId.Length > 256 || credentialId.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Credential IDs must be at most 256 printable characters.", nameof(credentialId));
        }

        return Encoding.UTF8.GetBytes(credentialId);
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result != 0)
        {
            throw new ProviderCredentialStoreException(
                $"macOS Keychain could not {operation} the Deckwraith credential (OSStatus {result}).",
                result);
        }
    }

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
        {
            NativeMethods.CFRelease(item);
        }
    }

    private static class NativeMethods
    {
        private const string SecurityFramework =
            "/System/Library/Frameworks/Security.framework/Versions/Current/Security";
        private const string CoreFoundationFramework =
            "/System/Library/Frameworks/CoreFoundation.framework/Versions/Current/CoreFoundation";

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef,
            IntPtr attrList,
            uint length,
            byte[] data);

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundationFramework)]
        internal static extern void CFRelease(IntPtr value);
    }
}

public sealed class ProviderCredentialStoreException(string message, int nativeCode)
    : Exception(message)
{
    public int NativeCode { get; } = nativeCode;
}
