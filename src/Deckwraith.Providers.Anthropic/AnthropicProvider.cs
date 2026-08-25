using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Http;

namespace Deckwraith.Providers.Anthropic;

public sealed record AnthropicProviderOptions(
    Uri BaseUri,
    string ApiKeyEnvironment = "ANTHROPIC_API_KEY",
    string ApiVersion = "2023-06-01",
    int DefaultMaximumOutputTokens = 8192)
{
    public static AnthropicProviderOptions CreateDefault() =>
        new(new Uri("https://api.anthropic.com/"));
}

public sealed class AnthropicProvider : IModelProvider, IProviderApiKeyAuthenticationSource
{
    private static readonly HttpClient SharedClient = new();
    private readonly AnthropicProviderOptions _options;
    private readonly HttpClient _client;
    private readonly ProviderApiKeyCredentialSource _credentials;

    public AnthropicProvider(
        AnthropicProviderOptions? options = null,
        HttpClient? client = null,
        ProviderApiKeyCredentialSource? credentialSource = null)
    {
        _options = options ?? AnthropicProviderOptions.CreateDefault();
        _client = client ?? SharedClient;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ApiKeyEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ApiVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.DefaultMaximumOutputTokens);
        _credentials = credentialSource ?? new ProviderApiKeyCredentialSource(
            new ProviderApiKeyCredentialOptions(
                ProviderId,
                "Anthropic · API key",
                _options.ApiKeyEnvironment));
        if (!StringComparer.Ordinal.Equals(_credentials.ProviderId, ProviderId))
        {
            throw new ArgumentException(
                "The API-key credential source must belong to Anthropic.",
                nameof(credentialSource));
        }
    }

    public string ProviderId => "anthropic";

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        NativeToolCalling: true,
        Images: false,
        ReasoningControls: false,
        ConversationContinuation: false);

    public ValueTask<ProviderAuthenticationStatus> GetAuthenticationStatusAsync(
        CancellationToken cancellationToken = default) =>
        _credentials.GetAuthenticationStatusAsync(cancellationToken);

    public ValueTask<ProviderAuthenticationStatus> SetApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default) =>
        _credentials.SetApiKeyAsync(apiKey, cancellationToken);

    public ValueTask<ProviderAuthenticationStatus> DeleteStoredApiKeyAsync(
        CancellationToken cancellationToken = default) =>
        _credentials.DeleteStoredApiKeyAsync(cancellationToken);

    public async IAsyncEnumerable<ModelEvent> RunAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var credential = await _credentials.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (credential.ApiKey is not { } apiKey)
        {
            yield return new ModelProviderError(
                credential.State is ProviderAuthenticationState.Error
                    ? "credential-error"
                    : "credential-missing",
                credential.Message,
                false);
            yield break;
        }

        var tools = request.Tools.Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            input_schema = tool.InputSchema,
        }).ToArray();
        var body = new
        {
            model = request.Model,
            system = ProviderPrompt.BuildSystem(request),
            messages = new[]
            {
                new { role = "user", content = ProviderPrompt.BuildInput(request) },
            },
            max_tokens = request.MaximumOutputTokens ?? _options.DefaultMaximumOutputTokens,
            stream = true,
            tools = tools.Length == 0 ? null : tools,
        };
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderHttp.Endpoint(_options.BaseUri, "v1/messages"))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", _options.ApiVersion);
        using var response = await _client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            yield return new ModelProviderError(
                "anthropic-http",
                await ProviderHttp.ReadErrorAsync(response, cancellationToken, apiKey)
                    .ConfigureAwait(false),
                ProviderHttp.IsRetryable(response.StatusCode));
            yield break;
        }

        var toolsInFlight = new Dictionary<int, ToolBuffer>();
        long inputTokens = 0;
        long outputTokens = 0;
        string? stopReason = null;
        var started = false;
        await foreach (var item in ProviderHttp.ReadSseDataAsync(response, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var type = item.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            switch (type)
            {
                case "message_start":
                    var providerRequestId = item.GetProperty("message").GetProperty("id").GetString()
                        ?? request.RequestId;
                    inputTokens = ReadInt64(item, "message", "usage", "input_tokens");
                    yield return new ModelResponseStarted(providerRequestId);
                    started = true;
                    break;
                case "content_block_start":
                    var index = item.GetProperty("index").GetInt32();
                    var block = item.GetProperty("content_block");
                    if (block.TryGetProperty("type", out var blockType) &&
                        StringComparer.Ordinal.Equals(blockType.GetString(), "tool_use"))
                    {
                        toolsInFlight[index] = new ToolBuffer(
                            block.GetProperty("id").GetString() ?? $"{request.RequestId}-{index}",
                            block.GetProperty("name").GetString() ?? string.Empty);
                    }

                    break;
                case "content_block_delta":
                    var delta = item.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString();
                    if (StringComparer.Ordinal.Equals(deltaType, "text_delta"))
                    {
                        yield return new ModelTextDelta(delta.GetProperty("text").GetString() ?? string.Empty);
                    }
                    else if (StringComparer.Ordinal.Equals(deltaType, "input_json_delta") &&
                        toolsInFlight.TryGetValue(item.GetProperty("index").GetInt32(), out var tool))
                    {
                        tool.Arguments.Append(delta.GetProperty("partial_json").GetString());
                    }

                    break;
                case "content_block_stop":
                    var stoppedIndex = item.GetProperty("index").GetInt32();
                    if (toolsInFlight.Remove(stoppedIndex, out var completedTool))
                    {
                        using var arguments = JsonDocument.Parse(
                            completedTool.Arguments.Length == 0 ? "{}" : completedTool.Arguments.ToString());
                        yield return new ModelToolCallCompleted(
                            completedTool.CallId,
                            completedTool.Name,
                            arguments.RootElement.Clone());
                    }

                    break;
                case "message_delta":
                    if (item.TryGetProperty("delta", out var messageDelta) &&
                        messageDelta.TryGetProperty("stop_reason", out var stop) &&
                        stop.ValueKind is JsonValueKind.String)
                    {
                        stopReason = stop.GetString();
                    }

                    outputTokens = ReadInt64(item, "usage", "output_tokens");
                    break;
                case "error":
                    yield return new ModelProviderError(
                        "anthropic-stream",
                        ReadString(item, "error", "message") ?? "Anthropic stream failed.",
                        false);
                    yield break;
                case "message_stop":
                    if (!started)
                    {
                        yield return new ModelResponseStarted(request.RequestId);
                    }

                    yield return new ModelUsageReported(inputTokens, outputTokens, null);
                    yield return new ModelResponseCompleted(MapFinishReason(stopReason), null);
                    yield break;
            }
        }

        yield return new ModelProviderError(
            "incomplete-stream", "Anthropic stream ended without message_stop.", true);
    }

    private static ModelFinishReason MapFinishReason(string? value) => value switch
    {
        "tool_use" => ModelFinishReason.ToolCalls,
        "max_tokens" => ModelFinishReason.Length,
        _ => ModelFinishReason.Stop,
    };

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

    private static string? ReadString(JsonElement root, params string[] path)
    {
        foreach (var segment in path)
        {
            if (root.ValueKind is not JsonValueKind.Object ||
                !root.TryGetProperty(segment, out root))
            {
                return null;
            }
        }

        return root.ValueKind is JsonValueKind.String ? root.GetString() : null;
    }

    private sealed record ToolBuffer(string CallId, string Name)
    {
        public StringBuilder Arguments { get; } = new();
    }
}
