using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Http;

namespace Deckwraith.Providers.Google;

public sealed record GoogleGeminiProviderOptions(
    Uri BaseUri,
    string ApiKeyEnvironment = "GEMINI_API_KEY")
{
    public static GoogleGeminiProviderOptions CreateDefault() =>
        new(new Uri("https://generativelanguage.googleapis.com/"));
}

public sealed class GoogleGeminiProvider : IModelProvider
{
    private static readonly HttpClient SharedClient = new();
    private readonly GoogleGeminiProviderOptions _options;
    private readonly HttpClient _client;

    public GoogleGeminiProvider(
        GoogleGeminiProviderOptions? options = null,
        HttpClient? client = null)
    {
        _options = options ?? GoogleGeminiProviderOptions.CreateDefault();
        _client = client ?? SharedClient;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ApiKeyEnvironment);
    }

    public string ProviderId => "google-gemini";

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        NativeToolCalling: true,
        Images: false,
        ReasoningControls: false,
        ConversationContinuation: false);

    public async IAsyncEnumerable<ModelEvent> RunAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!ProviderHttp.TryResolveCredential(
            _options.ApiKeyEnvironment, out var apiKey, out var credentialError))
        {
            yield return new ModelProviderError("credential-missing", credentialError, false);
            yield break;
        }

        var functions = request.Tools.Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = tool.InputSchema,
        }).ToArray();
        var body = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = ProviderPrompt.BuildSystem(request) } },
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = ProviderPrompt.BuildInput(request) } },
                },
            },
            generationConfig = request.MaximumOutputTokens is null
                ? null
                : new { maxOutputTokens = request.MaximumOutputTokens },
            tools = functions.Length == 0
                ? null
                : new[] { new { functionDeclarations = functions } },
        };
        var model = request.Model.StartsWith("models/", StringComparison.Ordinal)
            ? request.Model["models/".Length..]
            : request.Model;
        var endpoint = ProviderHttp.Endpoint(
            _options.BaseUri,
            $"v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add("x-goog-api-key", apiKey);
        using var response = await _client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            yield return new ModelProviderError(
                "google-http",
                await ProviderHttp.ReadErrorAsync(response, cancellationToken, apiKey)
                    .ConfigureAwait(false),
                ProviderHttp.IsRetryable(response.StatusCode));
            yield break;
        }

        var started = false;
        var returnedTool = false;
        var callIndex = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        long cachedTokens = 0;
        string? finishReason = null;
        await foreach (var item in ProviderHttp.ReadSseDataAsync(response, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var chunks = item.ValueKind is JsonValueKind.Array
                ? item.EnumerateArray().Select(value => value.Clone()).ToArray()
                : [item];
            foreach (var chunk in chunks)
            {
                if (chunk.TryGetProperty("error", out var error))
                {
                    yield return new ModelProviderError(
                        "google-stream",
                        error.TryGetProperty("message", out var errorMessage)
                            ? errorMessage.GetString() ?? "Google Gemini stream failed."
                            : "Google Gemini stream failed.",
                        false);
                    yield break;
                }

                if (!started)
                {
                    var responseId = chunk.TryGetProperty("responseId", out var responseIdElement)
                        ? responseIdElement.GetString()
                        : null;
                    yield return new ModelResponseStarted(responseId ?? request.RequestId);
                    started = true;
                }

                if (chunk.TryGetProperty("usageMetadata", out var usage))
                {
                    inputTokens = ReadInt64(usage, "promptTokenCount");
                    outputTokens = ReadInt64(usage, "candidatesTokenCount");
                    cachedTokens = ReadInt64(usage, "cachedContentTokenCount");
                }

                if (!chunk.TryGetProperty("candidates", out var candidates) ||
                    candidates.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (candidate.TryGetProperty("finishReason", out var finish) &&
                        finish.ValueKind is JsonValueKind.String)
                    {
                        finishReason = finish.GetString();
                    }

                    if (!candidate.TryGetProperty("content", out var content) ||
                        !content.TryGetProperty("parts", out var parts) ||
                        parts.ValueKind is not JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text) &&
                            text.ValueKind is JsonValueKind.String)
                        {
                            yield return new ModelTextDelta(text.GetString() ?? string.Empty);
                        }

                        if (part.TryGetProperty("functionCall", out var functionCall))
                        {
                            var arguments = functionCall.TryGetProperty("args", out var args)
                                ? args.Clone()
                                : JsonSerializer.SerializeToElement(new { });
                            yield return new ModelToolCallCompleted(
                                $"{request.RequestId}-google-{callIndex++}",
                                functionCall.GetProperty("name").GetString() ?? string.Empty,
                                arguments);
                            returnedTool = true;
                        }
                    }
                }
            }
        }

        if (!started)
        {
            yield return new ModelProviderError(
                "incomplete-stream", "Google Gemini returned an empty stream.", true);
            yield break;
        }

        yield return new ModelUsageReported(inputTokens, outputTokens, cachedTokens);
        yield return new ModelResponseCompleted(
            returnedTool
                ? ModelFinishReason.ToolCalls
                : StringComparer.OrdinalIgnoreCase.Equals(finishReason, "MAX_TOKENS")
                    ? ModelFinishReason.Length
                    : ModelFinishReason.Stop,
            null);
    }

    private static long ReadInt64(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;
}
