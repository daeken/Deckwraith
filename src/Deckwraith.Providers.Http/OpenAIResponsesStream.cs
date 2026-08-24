using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Providers.Http;

public static class OpenAIResponsesEventReader
{
    public static async IAsyncEnumerable<ModelEvent> ReadAsync(
        HttpResponseMessage response,
        string providerId,
        string fallbackRequestId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var started = false;
        var returnedTool = false;
        await foreach (var item in ProviderHttp.ReadSseDataAsync(response, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var type = item.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            switch (type)
            {
                case "response.created":
                case "response.in_progress":
                    if (!started &&
                        item.TryGetProperty("response", out var created) &&
                        created.TryGetProperty("id", out var createdId))
                    {
                        yield return new ModelResponseStarted(createdId.GetString() ?? fallbackRequestId);
                        started = true;
                    }

                    break;
                case "response.output_text.delta":
                    yield return new ModelTextDelta(
                        item.TryGetProperty("delta", out var delta)
                            ? delta.GetString() ?? string.Empty
                            : string.Empty);
                    break;
                case "response.output_item.done":
                    if (item.TryGetProperty("item", out var output) &&
                        output.TryGetProperty("type", out var outputType) &&
                        StringComparer.Ordinal.Equals(outputType.GetString(), "function_call"))
                    {
                        var argumentsText = output.TryGetProperty("arguments", out var arguments)
                            ? arguments.GetString() ?? "{}"
                            : "{}";
                        if (!TryParseArguments(argumentsText, out var parsedArguments))
                        {
                            yield return new ModelProviderError(
                                "invalid-tool-call",
                                $"{providerId} returned malformed function-call arguments.",
                                false);
                            yield break;
                        }

                        yield return new ModelToolCallCompleted(
                            output.TryGetProperty("call_id", out var callId)
                                ? callId.GetString() ?? ReadOutputId(output, fallbackRequestId)
                                : ReadOutputId(output, fallbackRequestId),
                            output.TryGetProperty("name", out var name)
                                ? name.GetString() ?? string.Empty
                                : string.Empty,
                            parsedArguments);
                        returnedTool = true;
                    }

                    break;
                case "response.completed":
                    var completed = item.GetProperty("response");
                    if (!started)
                    {
                        yield return new ModelResponseStarted(
                            completed.TryGetProperty("id", out var completedId)
                                ? completedId.GetString() ?? fallbackRequestId
                                : fallbackRequestId);
                    }

                    if (completed.TryGetProperty("usage", out var usage))
                    {
                        yield return new ModelUsageReported(
                            ReadInt64(usage, "input_tokens"),
                            ReadInt64(usage, "output_tokens"),
                            ReadInt64(usage, "input_tokens_details", "cached_tokens"));
                    }

                    yield return new ModelResponseCompleted(
                        returnedTool ? ModelFinishReason.ToolCalls : ModelFinishReason.Stop,
                        null);
                    yield break;
                case "response.failed":
                case "error":
                    yield return new ModelProviderError(
                        $"{providerId}-stream",
                        ReadError(item),
                        false);
                    yield break;
            }
        }

        yield return new ModelProviderError(
            "incomplete-stream", "Provider stream ended without response.completed.", true);
    }

    private static string ReadOutputId(JsonElement output, string fallbackRequestId) =>
        output.TryGetProperty("id", out var id)
            ? id.GetString() ?? fallbackRequestId
            : fallbackRequestId;

    private static bool TryParseArguments(string value, out JsonElement arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            arguments = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            arguments = default;
            return false;
        }
    }

    private static long ReadInt64(JsonElement root, params string[] path)
    {
        foreach (var segment in path)
        {
            if (root.ValueKind is not JsonValueKind.Object ||
                !root.TryGetProperty(segment, out root))
            {
                return 0;
            }
        }

        return root.TryGetInt64(out var value) ? value : 0;
    }

    private static string ReadError(JsonElement item)
    {
        if (item.TryGetProperty("message", out var direct) && direct.ValueKind is JsonValueKind.String)
        {
            return direct.GetString()!;
        }

        if (item.TryGetProperty("response", out var response) &&
            response.TryGetProperty("error", out var error) &&
            error.ValueKind is JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind is JsonValueKind.String)
        {
            return message.GetString()!;
        }

        return "Provider response failed.";
    }
}
