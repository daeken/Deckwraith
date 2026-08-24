using System.Text.Json;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Http;

namespace Deckwraith.Providers.ContractTests;

public sealed class ProviderApiKeyCredentialTests
{
    [Fact]
    public async Task StoredKeyTakesPrecedenceWithoutEnteringStatus()
    {
        const string storedSecret = "stored-secret-that-must-not-escape";
        const string environmentSecret = "environment-secret-that-must-not-escape";
        var environmentName = $"DECKWRAITH_TEST_API_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(environmentName, environmentSecret);
        try
        {
            var store = new MemoryCredentialStore();
            var source = CreateSource(environmentName, store);

            var written = await source.SetApiKeyAsync(storedSecret);
            var resolution = await source.ResolveAsync();
            var status = await source.GetAuthenticationStatusAsync();

            Assert.Equal(ProviderAuthenticationState.Ready, written.State);
            Assert.Equal(storedSecret, resolution.ApiKey);
            Assert.Equal("the installation credential store", status.CredentialSource);
            Assert.Equal("provider.test-api.api-key", store.LastCredentialId);
            var serialized = JsonSerializer.Serialize(status);
            Assert.DoesNotContain(storedSecret, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(environmentSecret, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(storedSecret, JsonSerializer.Serialize(resolution), StringComparison.Ordinal);
            Assert.DoesNotContain(storedSecret, resolution.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Fact]
    public async Task EnvironmentFallbackIsExplicitAndProcessScoped()
    {
        const string secret = "environment-only-secret";
        var environmentName = $"DECKWRAITH_TEST_API_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(environmentName, secret);
        try
        {
            var source = CreateSource(environmentName, new MemoryCredentialStore());

            var status = await source.GetAuthenticationStatusAsync();
            var resolution = await source.ResolveAsync();

            Assert.Equal(ProviderAuthenticationState.Ready, status.State);
            Assert.Equal(environmentName, status.CredentialSource);
            Assert.Contains(environmentName, status.Message, StringComparison.Ordinal);
            Assert.Equal(secret, resolution.ApiKey);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(status), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Fact]
    public async Task CredentialStoreFailureNeverEchoesTheKey()
    {
        const string secret = "failure-path-secret";
        var source = CreateSource(
            $"DECKWRAITH_TEST_API_KEY_{Guid.NewGuid():N}",
            new FailingCredentialStore());

        var status = await source.SetApiKeyAsync(secret);

        Assert.Equal(ProviderAuthenticationState.Error, status.State);
        Assert.DoesNotContain(secret, status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingStoredKeyRevealsEnvironmentFallbackWithoutReturningEitherKey()
    {
        const string storedSecret = "stored-delete-secret";
        const string environmentSecret = "environment-delete-secret";
        var environmentName = $"DECKWRAITH_TEST_API_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(environmentName, environmentSecret);
        try
        {
            var source = CreateSource(environmentName, new MemoryCredentialStore());
            await source.SetApiKeyAsync(storedSecret);

            var status = await source.DeleteStoredApiKeyAsync();

            Assert.Equal(ProviderAuthenticationState.Ready, status.State);
            Assert.Equal(environmentName, status.CredentialSource);
            var serialized = JsonSerializer.Serialize(status);
            Assert.DoesNotContain(storedSecret, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(environmentSecret, serialized, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    private static ProviderApiKeyCredentialSource CreateSource(
        string environmentName,
        IProviderCredentialStore store) => new(
        new ProviderApiKeyCredentialOptions(
            "test-api",
            "Test · API key",
            environmentName),
        store);

    private sealed class MemoryCredentialStore : IProviderCredentialStore
    {
        private string? _payload;

        public string StorageKind => "test";

        public string? LastCredentialId { get; private set; }

        public ValueTask<string?> ReadAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_payload);

        public ValueTask WriteAsync(
            string credentialId,
            string payload,
            CancellationToken cancellationToken = default)
        {
            LastCredentialId = credentialId;
            _payload = payload;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string credentialId,
            CancellationToken cancellationToken = default)
        {
            _payload = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingCredentialStore : IProviderCredentialStore
    {
        public string StorageKind => "test";

        public ValueTask<string?> ReadAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("read failed");

        public ValueTask WriteAsync(
            string credentialId,
            string payload,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("write failed for " + payload);

        public ValueTask DeleteAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("delete failed");
    }
}
