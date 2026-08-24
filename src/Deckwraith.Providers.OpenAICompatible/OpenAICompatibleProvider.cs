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
    string ProviderId = "openai-compatible",
    string DisplayName = "OpenAI-compatible · API key")
{
    public static OpenAICompatibleProviderOptions CreateDefault() =>
        new(new Uri("https://api.openai.com/"));
}

public sealed class OpenAICompatibleProvider : IModelProvider, IProviderApiKeyAuthenticationSource
{
    private static readonly HttpClient SharedClient = new();
    private readonly OpenAICompatibleProviderOptions _options;
    private readonly HttpClient _client;
    private readonly ProviderApiKeyCredentialSource _credentials;

    public OpenAICompatibleProvider(
        OpenAICompatibleProviderOptions? options = null,
        HttpClient? client = null,
        ProviderApiKeyCredentialSource? credentialSource = null)
    {
        _options = options ?? OpenAICompatibleProviderOptions.CreateDefault();
        _client = client ?? SharedClient;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ApiKeyEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ResponsesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.DisplayName);
        _credentials = credentialSource ?? new ProviderApiKeyCredentialSource(
            new ProviderApiKeyCredentialOptions(
                _options.ProviderId,
                _options.DisplayName,
                _options.ApiKeyEnvironment));
        if (!StringComparer.Ordinal.Equals(_credentials.ProviderId, _options.ProviderId))
        {
            throw new ArgumentException(
                "The API-key credential source must belong to this provider.",
                nameof(credentialSource));
        }
    }

    public string ProviderId => _options.ProviderId;

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        NativeToolCalling: true,
        Images: false,
        ReasoningControls: true,
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

        await foreach (var modelEvent in OpenAIResponsesEventReader.ReadAsync(
            response,
            ProviderId,
            request.RequestId,
            cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return modelEvent;
        }
    }
}
