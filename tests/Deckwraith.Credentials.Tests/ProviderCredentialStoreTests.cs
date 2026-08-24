using Deckwraith.Credentials;

namespace Deckwraith.Credentials.Tests;

public sealed class ProviderCredentialStoreTests
{
    [Fact]
    public async Task RestrictedFileStoreRoundTripsReplacesAndDeletesOpaquePayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deckwraith-credentials-{Guid.NewGuid():N}");
        try
        {
            using var store = new FileProviderCredentialStore(root);

            Assert.Null(await store.ReadAsync("provider.openai.subscription"));
            await store.WriteAsync("provider.openai.subscription", "first-secret");
            Assert.Equal("first-secret", await store.ReadAsync("provider.openai.subscription"));

            await store.WriteAsync("provider.openai.subscription", "replacement-secret");
            Assert.Equal("replacement-secret", await store.ReadAsync("provider.openai.subscription"));
            Assert.DoesNotContain(
                "provider.openai.subscription",
                Assert.Single(Directory.GetFiles(root)),
                StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Assert.Single(Directory.GetFiles(root))));
            }

            await store.DeleteAsync("provider.openai.subscription");
            Assert.Null(await store.ReadAsync("provider.openai.subscription"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MacKeychainStoreRejectsUseOnOtherPlatforms()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("macos-keychain", new MacKeychainProviderCredentialStore().StorageKind);
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() => new MacKeychainProviderCredentialStore());
    }

    [Fact]
    public async Task MacKeychainLiveRoundTripIsManuallyGated()
    {
        if (!OperatingSystem.IsMacOS() ||
            !StringComparer.Ordinal.Equals(
                Environment.GetEnvironmentVariable("DECKWRAITH_LIVE_KEYCHAIN_TEST"),
                "1"))
        {
            return;
        }

        var credentialId = $"test.{Guid.NewGuid():N}";
        var secret = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var store = new MacKeychainProviderCredentialStore();
        try
        {
            await store.WriteAsync(credentialId, secret);
            Assert.Equal(secret, await store.ReadAsync(credentialId));
        }
        finally
        {
            await store.DeleteAsync(credentialId);
        }

        Assert.Null(await store.ReadAsync(credentialId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("line\nbreak")]
    public async Task RestrictedFileStoreRejectsInvalidCredentialIds(string credentialId)
    {
        using var store = new FileProviderCredentialStore(
            Path.Combine(Path.GetTempPath(), $"deckwraith-credentials-{Guid.NewGuid():N}"));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.WriteAsync(credentialId, "secret"));
    }
}
