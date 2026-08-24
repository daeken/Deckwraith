namespace Deckwraith.Providers.Abstractions;

public enum ProviderAccessKind
{
    Api,
    Subscription,
}

public enum ProviderAuthenticationState
{
    Missing,
    Ready,
    Expiring,
    Expired,
    Refreshing,
    Rejected,
    Error,
}

public sealed record ProviderAuthenticationStatus(
    string ProviderId,
    string DisplayName,
    ProviderAccessKind AccessKind,
    ProviderAuthenticationState State,
    string Message,
    DateTimeOffset? ExpiresAt = null,
    string? AccountLabel = null);

public interface IProviderAuthenticationSource
{
    ValueTask<ProviderAuthenticationStatus> GetAuthenticationStatusAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores opaque provider credential payloads outside the deck repository.
/// Implementations must use a platform credential store where one is available.
/// </summary>
public interface IProviderCredentialStore
{
    string StorageKind { get; }

    ValueTask<string?> ReadAsync(
        string credentialId,
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        string credentialId,
        string payload,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        string credentialId,
        CancellationToken cancellationToken = default);
}
