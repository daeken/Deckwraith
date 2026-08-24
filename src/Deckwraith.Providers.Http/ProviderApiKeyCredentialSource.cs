using System.Text.Json.Serialization;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Providers.Http;

public sealed record ProviderApiKeyCredentialOptions(
    string ProviderId,
    string DisplayName,
    string EnvironmentVariable,
    string? CredentialId = null);

public sealed record ProviderApiKeyResolution(
    [property: JsonIgnore] string? ApiKey,
    ProviderAuthenticationState State,
    string Message,
    string? CredentialSource)
{
    public override string ToString() =>
        $"{nameof(ProviderApiKeyResolution)} {{ State = {State}, Message = {Message}, " +
        $"CredentialSource = {CredentialSource} }}";
}

/// <summary>
/// Resolves API keys from the installation credential store, with an explicit
/// environment-variable fallback for headless and compatibility use.
/// </summary>
public sealed class ProviderApiKeyCredentialSource : IProviderApiKeyAuthenticationSource
{
    private const int MaximumApiKeyLength = 16 * 1024;
    private readonly ProviderApiKeyCredentialOptions _options;
    private readonly IProviderCredentialStore? _credentialStore;

    public ProviderApiKeyCredentialSource(
        ProviderApiKeyCredentialOptions options,
        IProviderCredentialStore? credentialStore = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialStore = credentialStore;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.EnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(CredentialId);
    }

    public string ProviderId => _options.ProviderId;

    public string CredentialId => _options.CredentialId ?? $"provider.{_options.ProviderId}.api-key";

    public async ValueTask<ProviderApiKeyResolution> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        var storeUnavailable = false;
        if (_credentialStore is not null)
        {
            try
            {
                var stored = await _credentialStore.ReadAsync(CredentialId, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    return new ProviderApiKeyResolution(
                        stored,
                        ProviderAuthenticationState.Ready,
                        $"API key is stored in {StorageLabel(_credentialStore.StorageKind)}.",
                        StorageLabel(_credentialStore.StorageKind));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                storeUnavailable = true;
            }
        }

        var environment = Environment.GetEnvironmentVariable(_options.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return new ProviderApiKeyResolution(
                environment,
                ProviderAuthenticationState.Ready,
                storeUnavailable
                    ? $"Secure storage is unavailable; using {_options.EnvironmentVariable} for this process."
                    : $"Using {_options.EnvironmentVariable} for this process.",
                _options.EnvironmentVariable);
        }

        if (storeUnavailable)
        {
            return new ProviderApiKeyResolution(
                null,
                ProviderAuthenticationState.Error,
                "Deckwraith could not read the installation credential store.",
                null);
        }

        return new ProviderApiKeyResolution(
            null,
            ProviderAuthenticationState.Missing,
            _credentialStore is null
                ? $"Set {_options.EnvironmentVariable} to use this provider."
                : "Add an API key to use this provider.",
            null);
    }

    public async ValueTask<ProviderAuthenticationStatus> GetAuthenticationStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Status(resolution.State, resolution.Message, resolution.CredentialSource);
    }

    public async ValueTask<ProviderAuthenticationStatus> SetApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Length > MaximumApiKeyLength ||
            apiKey.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return Status(
                ProviderAuthenticationState.Error,
                "The API key is empty or has an invalid format.");
        }

        if (_credentialStore is null)
        {
            return Status(
                ProviderAuthenticationState.Error,
                "This host does not have a secure credential store configured.");
        }

        try
        {
            await _credentialStore.WriteAsync(CredentialId, apiKey, cancellationToken)
                .ConfigureAwait(false);
            return Status(
                ProviderAuthenticationState.Ready,
                $"API key is stored in {StorageLabel(_credentialStore.StorageKind)}.",
                StorageLabel(_credentialStore.StorageKind));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Status(
                ProviderAuthenticationState.Error,
                "Deckwraith could not store the API key securely.");
        }
    }

    public async ValueTask<ProviderAuthenticationStatus> DeleteStoredApiKeyAsync(
        CancellationToken cancellationToken = default)
    {
        if (_credentialStore is not null)
        {
            try
            {
                await _credentialStore.DeleteAsync(CredentialId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Status(
                    ProviderAuthenticationState.Error,
                    "Deckwraith could not remove the stored API key.");
            }
        }

        return await GetAuthenticationStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private ProviderAuthenticationStatus Status(
        ProviderAuthenticationState state,
        string message,
        string? credentialSource = null) => new(
        _options.ProviderId,
        _options.DisplayName,
        ProviderAccessKind.Api,
        state,
        message,
        CredentialSource: credentialSource);

    private static string StorageLabel(string storageKind) => storageKind switch
    {
        "macos-keychain" => "macOS Keychain",
        "windows-credential-manager" => "Windows Credential Manager",
        "secret-service" => "the system keyring",
        "restricted-file" => "restricted installation storage",
        _ => "the installation credential store",
    };
}
