using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Credentials;

public sealed class PlatformProviderCredentialStore : IProviderCredentialStore
{
    private readonly IProviderCredentialStore _inner;

    public PlatformProviderCredentialStore(string? fallbackDirectory = null)
    {
        _inner = OperatingSystem.IsMacOS()
            ? new MacKeychainProviderCredentialStore()
            : new FileProviderCredentialStore(fallbackDirectory ?? DefaultFallbackDirectory());
    }

    public string StorageKind => _inner.StorageKind;

    public ValueTask<string?> ReadAsync(
        string credentialId,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(credentialId, cancellationToken);

    public ValueTask WriteAsync(
        string credentialId,
        string payload,
        CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(credentialId, payload, cancellationToken);

    public ValueTask DeleteAsync(
        string credentialId,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(credentialId, cancellationToken);

    private static string DefaultFallbackDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Deckwraith",
        "credentials");
}
