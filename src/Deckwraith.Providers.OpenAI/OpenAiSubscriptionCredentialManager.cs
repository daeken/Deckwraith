using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Providers.OpenAI;

public sealed record OpenAiSubscriptionAuthenticationOptions(
    Uri TokenEndpoint,
    string ClientId,
    string CredentialId = "provider.openai.subscription",
    int RefreshLookaheadSeconds = 300)
{
    public static OpenAiSubscriptionAuthenticationOptions CreateDefault() => new(
        new Uri("https://auth.openai.com/oauth/token"),
        "app_EMoamEEZ73f0CkXaXp7hrann");
}

public sealed class OpenAiSubscriptionCredentialManager : IProviderAuthenticationSource
{
    private static readonly HttpClient SharedClient = new();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly JsonSerializerOptions CredentialJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly IProviderCredentialStore _credentialStore;
    private readonly OpenAiSubscriptionAuthenticationOptions _options;
    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private volatile bool _refreshing;
    private string? _lastRejection;
    private string? _lastError;

    public OpenAiSubscriptionCredentialManager(
        IProviderCredentialStore credentialStore,
        OpenAiSubscriptionAuthenticationOptions? options = null,
        HttpClient? client = null,
        TimeProvider? timeProvider = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _options = options ?? OpenAiSubscriptionAuthenticationOptions.CreateDefault();
        _client = client ?? SharedClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.CredentialId);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.RefreshLookaheadSeconds);
    }

    public string StorageKind => _credentialStore.StorageKind;

    public async ValueTask<ProviderAuthenticationStatus> GetAuthenticationStatusAsync(
        CancellationToken cancellationToken = default)
    {
        StoredCredential? credential;
        try
        {
            credential = await ReadCredentialAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAiAuthenticationException exception)
        {
            return Status(ProviderAuthenticationState.Error, exception.Message);
        }

        if (credential is null)
        {
            return Status(
                ProviderAuthenticationState.Missing,
                "Connect a ChatGPT account to use subscription access.");
        }

        if (_refreshing)
        {
            return Status(
                ProviderAuthenticationState.Refreshing,
                "Refreshing the ChatGPT session.",
                credential);
        }

        if (_lastRejection is { } rejection)
        {
            return Status(ProviderAuthenticationState.Rejected, rejection, credential);
        }

        if (_lastError is { } error)
        {
            return Status(ProviderAuthenticationState.Error, error, credential);
        }

        var remaining = credential.ExpiresAt - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return Status(
                ProviderAuthenticationState.Expired,
                string.IsNullOrWhiteSpace(credential.RefreshToken)
                    ? "The ChatGPT session expired. Reconnect the account."
                    : "The ChatGPT access token expired and will refresh before the next request.",
                credential);
        }

        if (remaining <= TimeSpan.FromSeconds(_options.RefreshLookaheadSeconds))
        {
            return Status(
                ProviderAuthenticationState.Expiring,
                "The ChatGPT access token is close to expiry and will refresh before use.",
                credential);
        }

        return Status(
            ProviderAuthenticationState.Ready,
            $"ChatGPT subscription credentials are stored in {_credentialStore.StorageKind}.",
            credential);
    }

    public async ValueTask<ProviderAuthenticationStatus> ImportCodexSessionAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            await using var stream = File.OpenRead(Path.GetFullPath(path));
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind is not JsonValueKind.Object)
            {
                throw new OpenAiAuthenticationException(
                    "credential-invalid",
                    "The selected Codex authentication file has no ChatGPT token set.",
                    false);
            }

            var accessToken = RequiredString(tokens, "access_token");
            var refreshToken = OptionalString(tokens, "refresh_token");
            var idToken = OptionalString(tokens, "id_token");
            var accountId = OptionalString(tokens, "account_id") ??
                ReadAccountId(accessToken) ??
                ReadAccountId(idToken) ??
                throw new OpenAiAuthenticationException(
                    "credential-invalid",
                    "The ChatGPT session does not identify an account.",
                    false);
            var expiresAt = ReadExpiration(accessToken) ??
                ReadExpiration(idToken) ??
                throw new OpenAiAuthenticationException(
                    "credential-invalid",
                    "The ChatGPT session has no readable expiry.",
                    false);
            var accountLabel = ReadClaim(idToken, "email") ?? ReadClaim(accessToken, "email");
            await SaveCredentialAsync(
                new StoredCredential(
                    accessToken,
                    refreshToken,
                    idToken,
                    accountId,
                    accountLabel,
                    expiresAt,
                    _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAiAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new OpenAiAuthenticationException(
                "credential-import-failed",
                "Deckwraith could not import the existing Codex sign-in.",
                false,
                exception);
        }

        return await GetAuthenticationStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveSessionAsync(
        string accessToken,
        string? refreshToken,
        string? idToken,
        string? accountId,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var resolvedAccountId = accountId ?? ReadAccountId(accessToken) ?? ReadAccountId(idToken);
        if (string.IsNullOrWhiteSpace(resolvedAccountId))
        {
            throw new OpenAiAuthenticationException(
                "credential-invalid",
                "The ChatGPT session does not identify an account.",
                false);
        }

        var resolvedExpiry = expiresAt ?? ReadExpiration(accessToken) ?? ReadExpiration(idToken);
        if (resolvedExpiry is null)
        {
            throw new OpenAiAuthenticationException(
                "credential-invalid",
                "The ChatGPT session has no readable expiry.",
                false);
        }

        await SaveCredentialAsync(
            new StoredCredential(
                accessToken,
                refreshToken,
                idToken,
                resolvedAccountId,
                ReadClaim(idToken, "email") ?? ReadClaim(accessToken, "email"),
                resolvedExpiry.Value,
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _credentialStore.DeleteAsync(_options.CredentialId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OpenAiAuthenticationException(
                "credential-store-delete",
                "Deckwraith could not remove the stored ChatGPT credential.",
                true,
                exception);
        }

        _lastError = null;
        _lastRejection = null;
    }

    internal async ValueTask<OpenAiSubscriptionSession> GetSessionAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credential = await ReadCredentialAsync(cancellationToken).ConfigureAwait(false) ??
                throw new OpenAiAuthenticationException(
                    "credential-missing",
                    "Connect a ChatGPT account before using OpenAI subscription access.",
                    false);
            var refreshAt = credential.ExpiresAt -
                TimeSpan.FromSeconds(_options.RefreshLookaheadSeconds);
            if (forceRefresh || refreshAt <= _timeProvider.GetUtcNow())
            {
                credential = await RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
            }

            return new OpenAiSubscriptionSession(
                credential.AccessToken,
                credential.AccountId,
                credential.ExpiresAt);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    internal void MarkRejected(string message)
    {
        _lastRejection = string.IsNullOrWhiteSpace(message)
            ? "OpenAI rejected the ChatGPT subscription session. Reconnect the account."
            : message;
    }

    private async ValueTask<StoredCredential> RefreshAsync(
        StoredCredential current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            throw new OpenAiAuthenticationException(
                "credential-expired",
                "The ChatGPT session expired and has no refresh token. Reconnect the account.",
                false);
        }

        _refreshing = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _options.ClientId,
                    ["refresh_token"] = current.RefreshToken,
                }),
            };
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadRefreshErrorAsync(response, cancellationToken).ConfigureAwait(false);
                var rejected = response.StatusCode is HttpStatusCode.BadRequest or
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                var message = rejected
                    ? $"OpenAI rejected the ChatGPT session refresh ({detail}). Reconnect the account."
                    : $"OpenAI could not refresh the ChatGPT session ({detail}).";
                if (rejected)
                {
                    _lastRejection = message;
                }
                else
                {
                    _lastError = message;
                }

                throw new OpenAiAuthenticationException(
                    rejected ? "credential-rejected" : "credential-refresh-failed",
                    message,
                    !rejected);
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var payload = document.RootElement;
            var accessToken = RequiredString(payload, "access_token");
            var refreshToken = OptionalString(payload, "refresh_token") ?? current.RefreshToken;
            var idToken = OptionalString(payload, "id_token") ?? current.IdToken;
            var expiresAt = payload.TryGetProperty("expires_in", out var expiresIn) &&
                expiresIn.TryGetInt64(out var seconds)
                ? _timeProvider.GetUtcNow().AddSeconds(seconds)
                : ReadExpiration(accessToken) ??
                    ReadExpiration(idToken) ??
                    _timeProvider.GetUtcNow().AddHours(1);
            var refreshed = new StoredCredential(
                accessToken,
                refreshToken,
                idToken,
                ReadAccountId(accessToken) ?? ReadAccountId(idToken) ?? current.AccountId,
                ReadClaim(idToken, "email") ?? ReadClaim(accessToken, "email") ?? current.AccountLabel,
                expiresAt,
                _timeProvider.GetUtcNow());
            await SaveCredentialAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed;
        }
        catch (OpenAiAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _lastError = "Deckwraith could not refresh the ChatGPT session.";
            throw new OpenAiAuthenticationException(
                "credential-refresh-failed",
                _lastError,
                true,
                exception);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async ValueTask<StoredCredential?> ReadCredentialAsync(
        CancellationToken cancellationToken)
    {
        string? payload;
        try
        {
            payload = await _credentialStore.ReadAsync(_options.CredentialId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OpenAiAuthenticationException(
                "credential-store-read",
                "Deckwraith could not read the stored ChatGPT credential.",
                true,
                exception);
        }

        if (payload is null)
        {
            return null;
        }

        try
        {
            var credential = JsonSerializer.Deserialize<StoredCredential>(payload, CredentialJson);
            if (credential is null ||
                string.IsNullOrWhiteSpace(credential.AccessToken) ||
                string.IsNullOrWhiteSpace(credential.AccountId))
            {
                throw new JsonException("Required credential fields are missing.");
            }

            return credential;
        }
        catch (JsonException exception)
        {
            throw new OpenAiAuthenticationException(
                "credential-invalid",
                "The stored ChatGPT credential is invalid. Disconnect and reconnect the account.",
                false,
                exception);
        }
    }

    private async ValueTask SaveCredentialAsync(
        StoredCredential credential,
        CancellationToken cancellationToken)
    {
        try
        {
            await _credentialStore.WriteAsync(
                _options.CredentialId,
                JsonSerializer.Serialize(credential, CredentialJson),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OpenAiAuthenticationException(
                "credential-store-write",
                "Deckwraith could not store the ChatGPT credential securely.",
                true,
                exception);
        }

        _lastError = null;
        _lastRejection = null;
    }

    private static ProviderAuthenticationStatus Status(
        ProviderAuthenticationState state,
        string message,
        StoredCredential? credential = null) => new(
        OpenAiSubscriptionProvider.Id,
        "OpenAI · ChatGPT subscription",
        ProviderAccessKind.Subscription,
        state,
        message,
        credential?.ExpiresAt,
        credential?.AccountLabel ??
            (credential?.AccountId is { Length: > 8 } accountId ? accountId[..8] + "…" : credential?.AccountId));

    private static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) ?? throw new OpenAiAuthenticationException(
            "credential-invalid",
            $"The authentication response is missing {name.Replace('_', ' ')}.",
            false);

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadExpiration(string? token)
    {
        var claims = ReadClaims(token);
        if (claims is null ||
            !claims.Value.TryGetProperty("exp", out var expiration) ||
            !expiration.TryGetInt64(out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadAccountId(string? token)
    {
        var claims = ReadClaims(token);
        if (claims is null)
        {
            return null;
        }

        foreach (var name in new[]
        {
            "chatgpt_account_id",
            "https://api.openai.com/auth.chatgpt_account_id",
        })
        {
            if (claims.Value.TryGetProperty(name, out var direct) &&
                direct.ValueKind is JsonValueKind.String)
            {
                return direct.GetString();
            }
        }

        return claims.Value.TryGetProperty("https://api.openai.com/auth", out var authentication) &&
            authentication.ValueKind is JsonValueKind.Object &&
            authentication.TryGetProperty("chatgpt_account_id", out var nested) &&
            nested.ValueKind is JsonValueKind.String
                ? nested.GetString()
                : null;
    }

    private static string? ReadClaim(string? token, string name)
    {
        var claims = ReadClaims(token);
        return claims is not null &&
            claims.Value.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static JsonElement? ReadClaims(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            var encoded = segments[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            return document.RootElement.ValueKind is JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadRefreshErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var name in new[] { "error_description", "error" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind is JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
        }

        return ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
    }

    private sealed record StoredCredential(
        string AccessToken,
        string? RefreshToken,
        string? IdToken,
        string AccountId,
        string? AccountLabel,
        DateTimeOffset ExpiresAt,
        DateTimeOffset UpdatedAt);
}

internal sealed class OpenAiSubscriptionSession(
    string accessToken,
    string accountId,
    DateTimeOffset expiresAt)
{
    public string AccessToken { get; } = accessToken;

    public string AccountId { get; } = accountId;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public override string ToString() => $"OpenAI subscription session expiring {ExpiresAt:O}";
}

public sealed class OpenAiAuthenticationException : Exception
{
    public OpenAiAuthenticationException(
        string code,
        string message,
        bool retryable,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}
