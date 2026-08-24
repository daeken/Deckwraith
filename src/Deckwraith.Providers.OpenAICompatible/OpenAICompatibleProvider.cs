using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Http;

namespace Deckwraith.Providers.OpenAICompatible;

public sealed record OpenAICompatibleProviderOptions(
    Uri BaseUri,
    string ApiKeyEnvironment = "OPENAI_API_KEY",
    string ResponsesPath = "v1/responses",
    IReadOnlyDictionary<string, string>? Headers = null,
    string ProviderId = "openai-compatible")
{
    public static OpenAICompatibleProviderOptions CreateDefault() =>
        new(new Uri("https://api.openai.com/"));
}

public sealed class OpenAICompatibleProvider : IModelProvider
{
    private static readonly HttpClient SharedClient = new();
    private readonly OpenAICompatibleProviderOptions _options;
    private readonly HttpClient _client;

    public OpenAICompatibleProvider(
        OpenAICompatibleProviderOptions? options = null,
        HttpClient? client = null)
    {
        _options = options ?? OpenAICompatibleProviderOptions.CreateDefault();
        _client = client ?? SharedClient;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ApiKeyEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ResponsesPath);
    }

    public string ProviderId => _options.ProviderId;

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        NativeToolCalling: true,
        Images: false,
        ReasoningControls: true,
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

        var tools = request.Tools.Select(tool => new
        {
            type = "function",
            name = tool.Name,
            description = tool.Description,
            parameters = tool.InputSchema,
            strict = false,
        }).ToArray();
        var body = new
        {
            model = request.Model,
            instructions = ProviderPrompt.BuildSystem(request),
            input = ProviderPrompt.BuildInput(request),
            stream = true,
            store = false,
            max_output_tokens = request.MaximumOutputTokens,
            reasoning = request.ReasoningEffort is null
                ? null
                : new { effort = request.ReasoningEffort },
            tools = tools.Length == 0 ? null : tools,
        };
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderHttp.Endpoint(_options.BaseUri, _options.ResponsesPath))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (_options.Headers is not null)
        {
            foreach (var (name, value) in _options.Headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await _client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            yield return new ModelProviderError(
                $"{ProviderId}-http",
                await ProviderHttp.ReadErrorAsync(response, cancellationToken).ConfigureAwait(false),
                ProviderHttp.IsRetryable(response.StatusCode));
            yield break;
        }

        var started = false;
        var returnedTool = false;
        string? responseId = null;
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
                        responseId = createdId.GetString();
                        yield return new ModelResponseStarted(responseId ?? request.RequestId);
                        started = true;
                    }

                    break;
                case "response.output_text.delta":
                    yield return new ModelTextDelta(
                        item.TryGetProperty("delta", out var delta) ? delta.GetString() ?? string.Empty : string.Empty);
                    break;
                case "response.output_item.done":
                    if (item.TryGetProperty("item", out var output) &&
                        output.TryGetProperty("type", out var outputType) &&
                        StringComparer.Ordinal.Equals(outputType.GetString(), "function_call"))
                    {
                        var argumentsText = output.TryGetProperty("arguments", out var arguments)
                            ? arguments.GetString() ?? "{}"
                            : "{}";
                        using var argumentsDocument = JsonDocument.Parse(argumentsText);
                        yield return new ModelToolCallCompleted(
                            output.TryGetProperty("call_id", out var callId)
                                ? callId.GetString() ?? output.GetProperty("id").GetString() ?? request.RequestId
                                : output.GetProperty("id").GetString() ?? request.RequestId,
                            output.GetProperty("name").GetString() ?? string.Empty,
                            argumentsDocument.RootElement.Clone());
                        returnedTool = true;
                    }

                    break;
                case "response.completed":
                    var completed = item.GetProperty("response");
                    if (!started)
                    {
                        responseId = completed.TryGetProperty("id", out var completedId)
                            ? completedId.GetString()
                            : null;
                        yield return new ModelResponseStarted(responseId ?? request.RequestId);
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
                        $"{ProviderId}-stream",
                        ReadError(item),
                        false);
                    yield break;
            }
        }

        yield return new ModelProviderError(
            "incomplete-stream", "OpenAI-compatible stream ended without response.completed.", true);
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

        return "OpenAI-compatible response failed.";
    }
}
