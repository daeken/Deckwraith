using System.Text.Json;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.OpenAI;

namespace Deckwraith.Providers.ContractTests;

public sealed class CodexAppServerProviderTests
{
    [Fact]
    public void InstructionsContainTheCompleteIdentity()
    {
        var request = CreateRequest();

        var instructions = CodexAppServerProvider.BuildBaseInstructions(request);

        Assert.Contains("\"name\":\"lumen\"", instructions, StringComparison.Ordinal);
        Assert.Contains("\"personality\":\"curious and incisive\"", instructions, StringComparison.Ordinal);
        Assert.Contains("\"register\":\"terse and playful\"", instructions, StringComparison.Ordinal);
        Assert.Contains("\"opsec\":\"never disclose credentials\"", instructions, StringComparison.Ordinal);
        Assert.Contains("\"pronouns\":[\"it\",\"she\"]", instructions, StringComparison.Ordinal);
        Assert.Contains("Do not use Codex commands", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnInputProjectsObjectiveAndMaterializedContext()
    {
        var request = CreateRequest();

        var input = CodexAppServerProvider.BuildTurnInput(request);

        Assert.Contains("Objective:\nProve provider independence", input, StringComparison.Ordinal);
        Assert.Contains("\"agent\":\"lumen\"", input, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"Hello from durable context.\"", input, StringComparison.Ordinal);
        Assert.Contains("Continue from the final context item", input, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("completed", ModelFinishReason.Stop)]
    [InlineData("interrupted", ModelFinishReason.Cancelled)]
    public void CompletionNotificationsMapToCanonicalFinishReasons(
        string status,
        ModelFinishReason expected)
    {
        using var message = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            method = "turn/completed",
            @params = new { turn = new { status } },
        }));

        var translated = Assert.IsType<ModelResponseCompleted>(
            CodexAppServerProvider.TranslateNotification(message.RootElement));

        Assert.Equal(expected, translated.FinishReason);
    }

    [Fact]
    public void DeltaAndUsageNotificationsMapWithoutVendorTypes()
    {
        using var delta = JsonDocument.Parse(
            """{"method":"item/agentMessage/delta","params":{"delta":"hello"}}""");
        using var usage = JsonDocument.Parse(
            """{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"last":{"inputTokens":11,"outputTokens":7,"cachedInputTokens":3}}}}""");

        Assert.Equal(
            "hello",
            Assert.IsType<ModelTextDelta>(
                CodexAppServerProvider.TranslateNotification(delta.RootElement)).Delta);
        Assert.Equal(
            new ModelUsageReported(11, 7, 3),
            Assert.IsType<ModelUsageReported>(
                CodexAppServerProvider.TranslateNotification(usage.RootElement)));
    }

    [Fact]
    public void ErrorAndFailedTurnNotificationsMapToCanonicalErrors()
    {
        using var retrying = JsonDocument.Parse(
            """{"method":"error","params":{"error":{"message":"upstream unavailable"},"willRetry":true}}""");
        using var failed = JsonDocument.Parse(
            """{"method":"turn/completed","params":{"turn":{"status":"failed","error":{"message":"bad turn"}}}}""");

        var providerError = Assert.IsType<ModelProviderError>(
            CodexAppServerProvider.TranslateNotification(retrying.RootElement));
        Assert.Equal("upstream unavailable", providerError.Message);
        Assert.True(providerError.Retryable);

        var turnError = Assert.IsType<ModelProviderError>(
            CodexAppServerProvider.TranslateNotification(failed.RootElement));
        Assert.Equal("bad turn", turnError.Message);
        Assert.False(turnError.Retryable);
    }

    private static ModelRequest CreateRequest()
    {
        var now = DateTimeOffset.UnixEpoch;
        var identity = IdentityDocument.CreateSparse(CanonicalName.Parse("lumen"), now) with
        {
            Personality = "curious and incisive",
            Calibration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["register"] = "terse and playful",
                ["opsec"] = "never disclose credentials",
            },
            Pronouns = ["it", "she"],
            SelfDescription = "A durable collaborator.",
            KnownTendencies = ["checks invariants"],
            OpenQuestions = ["what should persist?"],
        };
        var context = CurrentContextDocument.Create(
            CanonicalName.Parse("lumen"), CanonicalJson.Hash(identity), 8, now) with
        {
            Revision = 1,
            Items = [ContextItem.Message("message-1", ContextRole.User, "Hello from durable context.", 1)],
        };
        var manifest = ContextManifestBuilder.Build(
            identity,
            context,
            "Prove provider independence",
            "openai-codex-subscription",
            "test-model",
            []);
        return new ModelRequest(
            "request-1",
            "test-model",
            "Prove provider independence",
            identity,
            context,
            manifest,
            [],
            "high",
            null,
            null);
    }
}
