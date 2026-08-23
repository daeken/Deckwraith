using System.Text.Json;
using Deckwraith.Core.Context;
using Deckwraith.Core.State;

namespace Deckwraith.Providers.Abstractions;

public sealed record ProviderCapabilities(
    bool Streaming,
    bool NativeToolCalling,
    bool Images,
    bool ReasoningControls,
    bool ConversationContinuation);

public sealed record ModelToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);

public sealed record ModelRequest(
    string RequestId,
    string Model,
    string Objective,
    IdentityDocument Identity,
    CurrentContextDocument Context,
    ContextManifest Manifest,
    IReadOnlyList<ModelToolDefinition> Tools,
    string? ReasoningEffort,
    int? MaximumOutputTokens,
    string? ContinuationId);

public enum ModelFinishReason
{
    Stop,
    ToolCalls,
    Length,
    Cancelled,
    Error,
}

public abstract record ModelEvent;

public sealed record ModelResponseStarted(string ProviderRequestId) : ModelEvent;

public sealed record ModelTextDelta(string Delta) : ModelEvent;

public sealed record ModelToolCallCompleted(
    string CallId,
    string Name,
    JsonElement Arguments) : ModelEvent;

public sealed record ModelUsageReported(
    long InputTokens,
    long OutputTokens,
    long? CachedInputTokens) : ModelEvent;

public sealed record ModelResponseCompleted(
    ModelFinishReason FinishReason,
    string? ContinuationId) : ModelEvent;

public sealed record ModelProviderError(
    string Code,
    string Message,
    bool Retryable) : ModelEvent;

public interface IModelProvider
{
    string ProviderId { get; }

    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelEvent> RunAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}
