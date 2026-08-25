using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Hosting;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Hosting;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.OpenAI;

namespace Deckwraith.Hosting.Tests;

public sealed class HostBridgeEndToEndTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DefaultHostRegistersFirstClassOpenAiXaiAndZaiApis()
    {
        var registry = DeckwraithHostOptions.CreateDefault().CreateProviderRegistry();
        var providers = registry.Providers;

        Assert.Contains(providers, provider => provider.ProviderId == "anthropic");
        Assert.Contains(providers, provider => provider is OpenAiSubscriptionProvider);
        Assert.Contains(providers, provider => provider.ProviderId == "openai-api");
        Assert.Contains(providers, provider => provider.ProviderId == "xai-api");
        Assert.Contains(providers, provider => provider.ProviderId == "zai-api");
        Assert.Contains(providers, provider => provider.ProviderId == "openai-compatible");
    }

    [Fact]
    public async Task ApiKeysStayOutsideDeckSnapshotsResponsesAndEvents()
    {
        const string secret = "deckwraith-host-secret-that-must-stay-opaque";
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-api-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var credentials = new RecordingCredentialStore();
            var options = DeckwraithHostOptions.CreateDefault() with
            {
                CredentialStore = credentials,
            };
            using var host = await DeckwraithHost.OpenAsync(rootPath, options);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-api-key")));
            var cursorBeforeCredentialWrite = host.LatestEventCursor;

            var status = await host.SetProviderApiKeyAsync("openai-api", secret);
            Assert.Equal(cursorBeforeCredentialWrite, host.LatestEventCursor);
            var snapshots = await host.ReadProviderSnapshotsAsync();
            var readsBeforeDeckSnapshot = credentials.ReadCount;
            var deck = await host.ExecuteAsync(Query(
                "deck.snapshot", new { }, "snapshot-after-api-key"));

            Assert.Equal(ProviderAuthenticationState.Ready, status.State);
            Assert.Equal("provider.openai-api.api-key", credentials.LastCredentialId);
            Assert.Equal(secret, credentials.LastPayload);
            Assert.Equal(readsBeforeDeckSnapshot, credentials.ReadCount);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(status), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(snapshots), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, deck.Result!.Value.GetRawText(), StringComparison.Ordinal);
            foreach (var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(
                    secret,
                    System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task AConversationCanProbeOneProviderWithoutReadingEveryCredential()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-provider-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var credentials = new RecordingCredentialStore();
            var options = DeckwraithHostOptions.CreateDefault() with
            {
                CredentialStore = credentials,
            };
            using var host = await DeckwraithHost.OpenAsync(rootPath, options);

            var snapshot = await host.ReadProviderSnapshotAsync("openai-api");

            Assert.Equal("openai-api", snapshot.ProviderId);
            Assert.Equal(ProviderAuthenticationState.Missing, snapshot.Authentication?.State);
            Assert.Equal(1, credentials.ReadCount);
            Assert.Equal("provider.openai-api.api-key", credentials.LastReadCredentialId);
            await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
                await host.ReadProviderSnapshotAsync("not-a-provider"));
            Assert.Equal(1, credentials.ReadCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task WraithCreationCommitsFreshContextBeforeStartupRecovery()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            using (var first = await DeckwraithHost.OpenAsync(rootPath))
            {
                AssertSuccess(await first.ExecuteAsync(Command(
                    "deck.initialize", new { }, "initialize-for-recovery")));
                AssertSuccess(await first.ExecuteAsync(Command(
                    "wraith.create", new { name = "guest" }, "create-guest-for-recovery")));
            }

            using (var validated = await DeckwraithHost.OpenAsync(rootPath))
            {
                Assert.Equal(0, validated.LatestEventCursor);
            }

            var contextPath = Path.Combine(rootPath, "agents", "guest", "context.json");
            Assert.True(File.Exists(contextPath));
            File.Delete(contextPath);

            using var recovered = await DeckwraithHost.OpenAsync(rootPath);
            var recovery = Assert.Single(
                await ReadEventsThroughAsync(recovered, recovered.LatestEventCursor),
                item => item.Name == "recovery.completed");

            Assert.Equal(0, recovery.Payload.GetProperty("contextRevision").GetInt32());
            Assert.Equal(0, recovery.Payload.GetProperty("contextTurn").GetInt32());
            Assert.True(recovery.Payload.GetProperty("incident")
                .GetProperty("contextRebuilt").GetBoolean());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task ConversationAttachmentsBecomeOpaqueDurableArtifacts()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-attachment-{Guid.NewGuid():N}");
        var rootPath = Path.Combine(temporaryRoot, "deck");
        var sourcePath = Path.Combine(temporaryRoot, "notes for steward.txt");
        Directory.CreateDirectory(rootPath);
        await File.WriteAllTextAsync(sourcePath, "This belongs in durable conversation context.");
        try
        {
            var options = DeckwraithHostOptions.CreateDefault() with
            {
                CredentialStore = new EmptyCredentialStore(),
            };
            using var host = await DeckwraithHost.OpenAsync(rootPath, options);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-attachment")));

            var attachment = await host.StoreConversationAttachmentAsync(
                "steward",
                "setup",
                sourcePath,
                "text/plain");

            Assert.Equal("notes for steward.txt", attachment.FileName);
            Assert.StartsWith("sha256:", attachment.Hash, StringComparison.Ordinal);
            Assert.Equal("text/plain", attachment.MediaType);
            Assert.Equal(new FileInfo(sourcePath).Length, attachment.Length);
            Assert.DoesNotContain(
                temporaryRoot,
                JsonSerializer.Serialize(attachment),
                StringComparison.Ordinal);
            var digest = attachment.Hash["sha256:".Length..];
            var storedPath = Path.Combine(
                rootPath,
                "haunts",
                "setup",
                "artifacts",
                "sha256",
                digest[..2],
                digest[2..]);
            Assert.Equal(
                "This belongs in durable conversation context.",
                await File.ReadAllTextAsync(storedPath));

            var archive = await host.ExecuteAsync(Query(
                "archive.snapshot",
                new { wraith = "steward", afterSequence = 0, limit = 100 },
                "attachment-archive"));
            AssertSuccess(archive);
            Assert.Contains(
                archive.Result!.Value.GetProperty("records").EnumerateArray(),
                record => record.GetProperty("kind").GetString() == "artifact.stored");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidAttachmentBatchDoesNotStoreItsValidMembers()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-attachment-batch-{Guid.NewGuid():N}");
        var rootPath = Path.Combine(temporaryRoot, "deck");
        var validPath = Path.Combine(temporaryRoot, "valid.txt");
        var oversizedPath = Path.Combine(temporaryRoot, "oversized.bin");
        var missingPath = Path.Combine(temporaryRoot, "missing.txt");
        Directory.CreateDirectory(rootPath);
        await File.WriteAllTextAsync(validPath, "This valid file must not be stored alone.");
        await using (var oversized = new FileStream(
            oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            oversized.SetLength(32L * 1024 * 1024 + 1);
        }

        try
        {
            var options = DeckwraithHostOptions.CreateDefault() with
            {
                CredentialStore = new EmptyCredentialStore(),
            };
            using var host = await DeckwraithHost.OpenAsync(rootPath, options);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-attachment-batch")));

            var tooLarge = await Assert.ThrowsAsync<HostProtocolException>(() =>
                host.StoreConversationAttachmentsAsync(
                    "steward",
                    "setup",
                    [
                        new ConversationAttachmentSource(validPath, "text/plain"),
                        new ConversationAttachmentSource(oversizedPath),
                    ]));
            Assert.Equal("attachment-too-large", tooLarge.Code);

            var missing = await Assert.ThrowsAsync<HostProtocolException>(() =>
                host.StoreConversationAttachmentsAsync(
                    "steward",
                    "setup",
                    [
                        new ConversationAttachmentSource(validPath, "text/plain"),
                        new ConversationAttachmentSource(missingPath),
                    ]));
            Assert.Equal("attachment-missing", missing.Code);

            var storedArtifactRoot = Path.Combine(
                rootPath, "haunts", "setup", "artifacts", "sha256");
            Assert.False(Directory.Exists(storedArtifactRoot));
            var archive = await host.ExecuteAsync(Query(
                "archive.snapshot",
                new { wraith = "steward", afterSequence = 0, limit = 100 },
                "attachment-batch-archive"));
            AssertSuccess(archive);
            Assert.DoesNotContain(
                archive.Result!.Value.GetProperty("records").EnumerateArray(),
                record => record.GetProperty("kind").GetString() == "artifact.stored");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TypedBridgeOwnsLifecycleEventsReconnectAndIdentity()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var options = DeckwraithHostOptions.CreateDefault() with
            {
                EventCapacity = 128,
                CredentialStore = new EmptyCredentialStore(),
            };
            using (var host = await DeckwraithHost.OpenAsync(
                rootPath, options, [new FakeProvider()]))
            {
                AssertSuccess(await host.ExecuteAsync(Command(
                    "deck.initialize", new { }, "initialize")));
                var create = await host.ExecuteAsync(Command(
                    "wraith.create", new { name = "lumen" }, "create-wraith"));
                AssertSuccess(create);
                var duplicate = await host.ExecuteAsync(Command(
                    "wraith.create", new { name = "lumen" }, "create-wraith"));
                Assert.Equal(create, duplicate);
                AssertSuccess(await host.ExecuteAsync(Command(
                    "haunt.create", new { name = "deckwraith" }, "create-haunt")));
                var projectPath = Path.Combine(rootPath, "project");
                Directory.CreateDirectory(projectPath);
                var configuredProject = await host.ExecuteAsync(Command(
                    "haunt.configure-project",
                    new
                    {
                        haunt = "deckwraith",
                        projectPath,
                        autoCommitEnabled = false,
                    },
                    "configure-haunt-project"));
                AssertSuccess(configuredProject);
                Assert.False(configuredProject.Result!.Value
                    .GetProperty("value")
                    .GetProperty("project")
                    .GetProperty("autoCommitEnabled")
                    .GetBoolean());

                var identity = IdentityDocument.CreateSparse(
                    CanonicalName.Parse("lumen"), DateTimeOffset.UnixEpoch) with
                {
                    Personality = "curious and incisive",
                    Calibration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["register"] = "terse and playful",
                        ["opsec"] = "never disclose credentials",
                    },
                    Pronouns = ["it", "she"],
                };
                AssertSuccess(await host.ExecuteAsync(Command(
                    "identity.update", new { wraith = "lumen", identity }, "update-identity")));

                var invalid = await host.ExecuteAsync(Command(
                    "run.start",
                    new
                    {
                        wraith = "lumen",
                        haunt = "deckwraith",
                        objective = "should not run",
                        provider = "fake",
                        model = "fake-model",
                        apiKey = "renderer-must-not-send-this",
                    },
                    "secret-in-payload"));
                Assert.False(invalid.Success);
                Assert.Equal("invalid-payload", invalid.Error?.Code);

                var started = await host.ExecuteAsync(Command(
                    "run.start",
                    new
                    {
                        wraith = "lumen",
                        haunt = "deckwraith",
                        objective = "Prove the bridge",
                        provider = "fake",
                        model = "fake-model",
                    },
                    "start-run"));
                AssertSuccess(started);
                var runId = started.Result!.Value.GetProperty("run").GetProperty("runId").GetString()!;
                var turn = await host.ExecuteAsync(Command(
                    "run.turn",
                    new { wraith = "lumen", runId, message = "Begin." },
                    "turn"));
                AssertSuccess(turn);
                Assert.Equal("hello from fake", turn.Result!.Value.GetProperty("text").GetString());

                AssertSuccess(await host.ExecuteAsync(Command(
                    "deckbook.insert",
                    new
                    {
                        wraith = "lumen",
                        haunt = "deckwraith",
                        name = "answer",
                        kind = "code",
                        source = "[pscustomobject]@{ answer = 42 }",
                        kernel = "powershell",
                    },
                    "insert-cell")));
                AssertSuccess(await host.ExecuteAsync(Command(
                    "deckbook.run-cell",
                    new
                    {
                        wraith = "lumen",
                        haunt = "deckwraith",
                        name = "answer",
                        runId,
                        input = new { },
                    },
                    "run-cell")));

                var wraith = await host.ExecuteAsync(Query(
                    "wraith.snapshot", new { wraith = "lumen" }, "wraith-snapshot"));
                AssertSuccess(wraith);
                Assert.Equal(
                    "curious and incisive",
                    wraith.Result!.Value.GetProperty("identity").GetProperty("personality").GetString());
                Assert.Equal(1, wraith.Result.Value.GetProperty("runs").GetArrayLength());
                Assert.Equal(1, wraith.Result.Value.GetProperty("deckbooks").GetArrayLength());

                var schema = await host.ExecuteAsync(Query("host.schema", new { }, "schema"));
                AssertSuccess(schema);
                Assert.Equal(
                    HostProtocol.CurrentVersion,
                    schema.Result!.Value.GetProperty("protocolVersion").GetInt32());
                Assert.DoesNotContain(
                    schema.Result.Value.GetRawText(),
                    "credential",
                    StringComparison.OrdinalIgnoreCase);

                var eventNames = await ReadThroughAsync(host, host.LatestEventCursor);
                Assert.Contains("model.text-delta", eventNames);
                Assert.Contains("model.completed", eventNames);
                Assert.Contains("kernel.started", eventNames);
                Assert.Contains("kernel.value", eventNames);
                Assert.Contains("kernel.completed", eventNames);

                AssertSuccess(await host.ExecuteAsync(Command(
                    "run.complete",
                    new { wraith = "lumen", runId, reason = "bridge acceptance complete" },
                    "complete-run")));
                var archived = await host.ExecuteAsync(Command(
                    "wraith.archive", new { wraith = "lumen" }, "archive-wraith"));
                AssertSuccess(archived);
                Assert.NotEqual(
                    JsonValueKind.Null,
                    archived.Result!.Value.GetProperty("value").GetProperty("archivedAt").ValueKind);
                AssertSuccess(await host.ExecuteAsync(Command(
                    "wraith.restore", new { wraith = "lumen" }, "restore-wraith")));

                var checkpoints = await host.ExecuteAsync(Query(
                    "checkpoint.snapshot", new { limit = 20 }, "checkpoints"));
                AssertSuccess(checkpoints);
                Assert.True(checkpoints.Result!.Value.GetArrayLength() >= 7);
            }

            using (var reopened = await DeckwraithHost.OpenAsync(
                rootPath, options, [new FakeProvider()]))
            {
                var deck = await reopened.ExecuteAsync(Query(
                    "deck.snapshot", new { }, "reopened-deck"));
                AssertSuccess(deck);
                Assert.Equal(2, deck.Result!.Value.GetProperty("wraiths").GetArrayLength());
                Assert.Equal(2, deck.Result.Value.GetProperty("haunts").GetArrayLength());
                Assert.Contains(
                    deck.Result.Value.GetProperty("wraiths").EnumerateArray(),
                    wraith => wraith.GetProperty("name").GetString() == "steward");
                Assert.Contains(
                    deck.Result.Value.GetProperty("haunts").EnumerateArray(),
                    haunt => haunt.GetProperty("name").GetString() == "setup");
                var reopenedProject = deck.Result.Value.GetProperty("haunts").EnumerateArray()
                    .Single(haunt => haunt.GetProperty("name").GetString() == "deckwraith")
                    .GetProperty("project");
                Assert.Equal(
                    Path.Combine(rootPath, "project"),
                    reopenedProject.GetProperty("projectPath").GetString());
                Assert.Contains(
                    deck.Result.Value.GetProperty("providers").EnumerateArray(),
                    provider => provider.GetProperty("providerId").GetString() == "fake");
            }

            Assert.Equal(string.Empty, await RunGitAsync(rootPath, ["status", "--porcelain"]));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task ReusedRequestIdCannotNameADifferentMutation()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-request-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            using var host = await DeckwraithHost.OpenAsync(
                rootPath, additionalProviders: [new FakeProvider()]);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "same-id")));

            var exception = await Assert.ThrowsAsync<HostProtocolException>(() =>
                host.ExecuteAsync(Command("wraith.create", new { name = "lumen" }, "same-id")));

            Assert.Equal("request-id-reused", exception.Code);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledModelTurnsPublishATerminalLifecycleEvent()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var provider = new BlockingProvider();
            using var host = await DeckwraithHost.OpenAsync(
                rootPath, additionalProviders: [provider]);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-cancel")));
            var started = await host.ExecuteAsync(Command(
                "run.start",
                new
                {
                    wraith = "steward",
                    haunt = "setup",
                    objective = "wait until stopped",
                    provider = "blocking",
                    model = "blocking-model",
                },
                "start-cancelled-run"));
            AssertSuccess(started);
            var runId = started.Result!.Value.GetProperty("run").GetProperty("runId").GetString();

            using var cancellation = new CancellationTokenSource();
            var turn = host.ExecuteAsync(Command(
                "run.turn",
                new { wraith = "steward", runId, message = "Please wait." },
                "cancelled-turn"), cancellation.Token);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
            var events = await ReadEventsThroughAsync(host, host.LatestEventCursor);
            var requested = Assert.Single(events, hostEvent =>
                hostEvent.Name == "model.requested" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
            var terminal = Assert.Single(events, hostEvent =>
                hostEvent.Name == "model.completed" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
            Assert.True(requested.Cursor < terminal.Cursor);
            Assert.Equal(
                "cancelled",
                terminal.Payload.GetProperty("finishReason").GetString());

            var snapshot = await host.ExecuteAsync(Query(
                "wraith.snapshot", new { wraith = "steward" }, "cancelled-wraith-snapshot"));
            AssertSuccess(snapshot);
            var cancelledRun = Assert.Single(
                snapshot.Result!.Value.GetProperty("runs").EnumerateArray(),
                run => run.GetProperty("runId").GetString() == runId);
            Assert.Equal("cancelled", cancelledRun.GetProperty("status").GetString());
            Assert.Equal(
                "run-cancelled",
                cancelledRun.GetProperty("shells")[0].GetProperty("endReason").GetString());
            var context = snapshot.Result.Value.GetProperty("context");
            Assert.Equal(0, context.GetProperty("turn").GetInt64());
            var userMessage = Assert.Single(context.GetProperty("items").EnumerateArray());
            Assert.Equal("user", userMessage.GetProperty("role").GetString());
            Assert.Equal("Please wait.", userMessage.GetProperty("text").GetString());

            var restarted = await host.ExecuteAsync(Command(
                "run.start",
                new
                {
                    wraith = "steward",
                    haunt = "setup",
                    objective = "continue after a safe stop",
                    provider = "blocking",
                    model = "blocking-model",
                },
                "restart-after-cancel"));
            AssertSuccess(restarted);
            Assert.NotEqual(
                runId,
                restarted.Result!.Value.GetProperty("run").GetProperty("runId").GetString());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderFailuresBeforeStreamingPublishATerminalLifecycleEvent()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-provider-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            using var host = await DeckwraithHost.OpenAsync(
                rootPath, additionalProviders: [new ThrowingProvider()]);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-provider-failure")));
            var started = await host.ExecuteAsync(Command(
                "run.start",
                new
                {
                    wraith = "steward",
                    haunt = "setup",
                    objective = "surface provider failures",
                    provider = "throwing",
                    model = "throwing-model",
                },
                "start-provider-failure-run"));
            AssertSuccess(started);
            var runId = started.Result!.Value.GetProperty("run").GetProperty("runId").GetString();

            var turn = await host.ExecuteAsync(Command(
                "run.turn",
                new { wraith = "steward", runId, message = "Fail before streaming." },
                "provider-failure-turn"));

            Assert.False(turn.Success);
            var events = await ReadEventsThroughAsync(host, host.LatestEventCursor);
            var requested = Assert.Single(events, hostEvent =>
                hostEvent.Name == "model.requested" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
            var terminal = Assert.Single(events, hostEvent =>
                hostEvent.Name == "model.error" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
            Assert.True(requested.Cursor < terminal.Cursor);
            Assert.Equal("provider-exception", terminal.Payload.GetProperty("code").GetString());
            Assert.Equal(
                "provider failed before streaming",
                terminal.Payload.GetProperty("message").GetString());
            Assert.DoesNotContain(events, hostEvent =>
                hostEvent.Name == "model.started" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderTimeoutsFailTheRunWithoutMasqueradingAsCancellation()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-provider-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            using var host = await DeckwraithHost.OpenAsync(
                rootPath, additionalProviders: [new TimingOutProvider()]);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-provider-timeout")));
            var started = await host.ExecuteAsync(Command(
                "run.start",
                new
                {
                    wraith = "steward",
                    haunt = "setup",
                    objective = "surface provider timeouts",
                    provider = "timing-out",
                    model = "timing-out-model",
                },
                "start-provider-timeout-run"));
            AssertSuccess(started);
            var runId = started.Result!.Value.GetProperty("run").GetProperty("runId").GetString();

            var turn = await host.ExecuteAsync(Command(
                "run.turn",
                new { wraith = "steward", runId, message = "Wait for the provider." },
                "provider-timeout-turn"));

            Assert.False(turn.Success);
            Assert.Equal("provider-timeout", turn.Error?.Code);
            Assert.True(turn.Error?.Retryable);
            var events = await ReadEventsThroughAsync(host, host.LatestEventCursor);
            var requested = Assert.Single(events, hostEvent =>
                hostEvent.Name == "model.requested" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
            var terminal = Assert.Single(events, hostEvent =>
                hostEvent.Name == "model.error" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
            Assert.True(requested.Cursor < terminal.Cursor);
            Assert.Equal("provider-timeout", terminal.Payload.GetProperty("code").GetString());
            Assert.True(terminal.Payload.GetProperty("retryable").GetBoolean());
            Assert.DoesNotContain(events, hostEvent =>
                hostEvent.Name == "model.completed" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId &&
                hostEvent.Payload.GetProperty("finishReason").GetString() == "cancelled");

            var snapshot = await host.ExecuteAsync(Query(
                "wraith.snapshot", new { wraith = "steward" }, "timed-out-wraith-snapshot"));
            AssertSuccess(snapshot);
            var failedRun = Assert.Single(
                snapshot.Result!.Value.GetProperty("runs").EnumerateArray(),
                run => run.GetProperty("runId").GetString() == runId);
            Assert.Equal("failed", failedRun.GetProperty("status").GetString());
            Assert.Equal(
                "run-failed",
                failedRun.GetProperty("shells")[0].GetProperty("endReason").GetString());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task AProviderTerminalEventEndsEnumerationImmediately()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-terminal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var provider = new EventsAfterTerminalProvider();
            using var host = await DeckwraithHost.OpenAsync(
                rootPath, additionalProviders: [provider]);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-terminal")));
            var started = await host.ExecuteAsync(Command(
                "run.start",
                new
                {
                    wraith = "steward",
                    haunt = "setup",
                    objective = "stop at the provider terminal event",
                    provider = "events-after-terminal",
                    model = "terminal-model",
                },
                "start-terminal-run"));
            AssertSuccess(started);
            var runId = started.Result!.Value.GetProperty("run").GetProperty("runId").GetString();

            var turn = await host.ExecuteAsync(Command(
                "run.turn",
                new { wraith = "steward", runId, message = "Respect the terminal event." },
                "terminal-turn"));

            AssertSuccess(turn);
            Assert.Equal("before terminal", turn.Result!.Value.GetProperty("text").GetString());
            Assert.Equal(3, provider.EventsRequested);
            var events = await ReadEventsThroughAsync(host, host.LatestEventCursor);
            var terminal = Assert.Single(events, hostEvent =>
                hostEvent.Name == "model.completed" &&
                hostEvent.Payload.GetProperty("runId").GetString() == runId);
            Assert.Equal("stop", terminal.Payload.GetProperty("finishReason").GetString());
            Assert.DoesNotContain(events, hostEvent =>
                hostEvent.Name == "model.text-delta" &&
                hostEvent.Payload.GetProperty("delta").GetString() == " after terminal");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentTurnsForOneWraithNeverStartASecondRunloop()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-concurrent-turns-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var provider = new CountingBlockingProvider();
            using var host = await DeckwraithHost.OpenAsync(
                rootPath, additionalProviders: [provider]);
            AssertSuccess(await host.ExecuteAsync(Command(
                "deck.initialize", new { }, "initialize-for-concurrency")));
            var started = await host.ExecuteAsync(Command(
                "run.start",
                new
                {
                    wraith = "steward",
                    haunt = "setup",
                    objective = "serialize attention",
                    provider = "counting-blocking",
                    model = "blocking-model",
                },
                "start-concurrent-run"));
            AssertSuccess(started);
            var runId = started.Result!.Value.GetProperty("run").GetProperty("runId").GetString();

            using var firstCancellation = new CancellationTokenSource();
            using var secondCancellation = new CancellationTokenSource();
            var firstTurn = host.ExecuteAsync(Command(
                "run.turn",
                new { wraith = "steward", runId, message = "First message." },
                "first-concurrent-turn"), firstCancellation.Token);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondTurn = host.ExecuteAsync(Command(
                "run.turn",
                new { wraith = "steward", runId, message = "Second message." },
                "second-concurrent-turn"), secondCancellation.Token);

            secondCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTurn);
            Assert.Equal(1, provider.InvocationCount);

            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTurn);
            Assert.Equal(1, provider.InvocationCount);

            var snapshot = await host.ExecuteAsync(Query(
                "wraith.snapshot", new { wraith = "steward" }, "concurrent-wraith-snapshot"));
            AssertSuccess(snapshot);
            var messages = snapshot.Result!.Value.GetProperty("context").GetProperty("items")
                .EnumerateArray()
                .Where(item => item.GetProperty("kind").GetString() == "message")
                .ToArray();
            var userMessage = Assert.Single(messages);
            Assert.Equal("First message.", userMessage.GetProperty("text").GetString());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static HostRequest Command(string name, object payload, string requestId) =>
        Request(HostRequestKind.Command, name, payload, requestId);

    private static HostRequest Query(string name, object payload, string requestId) =>
        Request(HostRequestKind.Query, name, payload, requestId);

    private static HostRequest Request(
        HostRequestKind kind,
        string name,
        object payload,
        string requestId) =>
        new(
            HostProtocol.CurrentVersion,
            requestId,
            kind,
            name,
            JsonSerializer.SerializeToElement(payload, JsonOptions));

    private static void AssertSuccess(HostResponse response) =>
        Assert.True(response.Success, response.Error?.Message);

    private static async Task<List<string>> ReadThroughAsync(
        DeckwraithHost host,
        long targetCursor)
    {
        var names = new List<string>();
        await foreach (var hostEvent in host.ReadEventsAsync(0))
        {
            names.Add(hostEvent.Name);
            if (hostEvent.Cursor == targetCursor)
            {
                break;
            }
        }

        return names;
    }

    private static async Task<List<HostEvent>> ReadEventsThroughAsync(
        DeckwraithHost host,
        long targetCursor)
    {
        var events = new List<HostEvent>();
        await foreach (var hostEvent in host.ReadEventsAsync(0))
        {
            events.Add(hostEvent);
            if (hostEvent.Cursor == targetCursor)
            {
                break;
            }
        }

        return events;
    }

    private static async Task<string> RunGitAsync(
        string rootPath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = rootPath,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private sealed class FakeProvider : IModelProvider
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelResponseStarted("fake-response");
            yield return new ModelTextDelta("hello ");
            yield return new ModelTextDelta("from fake");
            yield return new ModelUsageReported(10, 3, 0);
            yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public string ProviderId => "blocking";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            yield return new ModelResponseStarted("blocking-response");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        public string ProviderId => "throwing";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("provider failed before streaming");
    }

    private sealed class TimingOutProvider : IModelProvider
    {
        public string ProviderId => "timing-out";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException(
                "provider timed out",
                new TimeoutException("The provider exceeded its request timeout."));
    }

    private sealed class EventsAfterTerminalProvider : IModelProvider
    {
        public string ProviderId => "events-after-terminal";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public int EventsRequested { get; private set; }

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            EventsRequested++;
            yield return new ModelResponseStarted("terminal-response");
            EventsRequested++;
            yield return new ModelTextDelta("before terminal");
            EventsRequested++;
            yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
            EventsRequested++;
            yield return new ModelTextDelta(" after terminal");
            EventsRequested++;
            throw new InvalidOperationException("The runtime read beyond the terminal event.");
        }
    }

    private sealed class CountingBlockingProvider : IModelProvider
    {
        private int _invocationCount;

        public string ProviderId => "counting-blocking";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            Started.TrySetResult(true);
            yield return new ModelResponseStarted("counting-blocking-response");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class EmptyCredentialStore : IProviderCredentialStore
    {
        public string StorageKind => "test";

        public ValueTask<string?> ReadAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask WriteAsync(
            string credentialId,
            string payload,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingCredentialStore : IProviderCredentialStore
    {
        private readonly Dictionary<string, string> _credentials = new(StringComparer.Ordinal);

        public string StorageKind => "test";

        public string? LastCredentialId { get; private set; }

        public string? LastPayload { get; private set; }

        public int ReadCount { get; private set; }

        public string? LastReadCredentialId { get; private set; }

        public ValueTask<string?> ReadAsync(
            string credentialId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            LastReadCredentialId = credentialId;
            return ValueTask.FromResult(
                _credentials.TryGetValue(credentialId, out var payload) ? payload : null);
        }

        public ValueTask WriteAsync(
            string credentialId,
            string payload,
            CancellationToken cancellationToken = default)
        {
            LastCredentialId = credentialId;
            LastPayload = payload;
            _credentials[credentialId] = payload;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string credentialId,
            CancellationToken cancellationToken = default)
        {
            _credentials.Remove(credentialId);
            return ValueTask.CompletedTask;
        }
    }
}
