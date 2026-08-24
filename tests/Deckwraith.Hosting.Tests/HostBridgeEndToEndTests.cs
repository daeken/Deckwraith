using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Hosting;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Hosting;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Hosting.Tests;

public sealed class HostBridgeEndToEndTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TypedBridgeOwnsLifecycleEventsReconnectAndIdentity()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var options = DeckwraithHostOptions.CreateDefault() with { EventCapacity = 128 };
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
                Assert.Equal(1, deck.Result!.Value.GetProperty("wraiths").GetArrayLength());
                Assert.Equal(1, deck.Result.Value.GetProperty("haunts").GetArrayLength());
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
}
