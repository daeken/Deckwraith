using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Http;

namespace Deckwraith.Providers.OpenAI;

public sealed record OpenAiSubscriptionProviderOptions(
    Uri BaseUri,
    string ResponsesPath = "backend-api/codex/responses")
{
    public static OpenAiSubscriptionProviderOptions CreateDefault() => new(
        new Uri("https://chatgpt.com/"));
}

/// <summary>
/// Talks directly to OpenAI's Codex subscription transport using a Deckwraith-owned
/// ChatGPT session. No provider CLI or local proxy participates in inference.
/// </summary>
public sealed class OpenAiSubscriptionProvider : IModelProvider, IProviderAuthenticationSource
{
    public const string Id = "openai-codex-subscription";
    private static readonly HttpClient SharedClient = new();
    private readonly OpenAiSubscriptionProviderOptions _options;
    private readonly OpenAiSubscriptionCredentialManager _credentials;
    private readonly HttpClient _client;

    public OpenAiSubscriptionProvider(
        OpenAiSubscriptionCredentialManager credentials,
        OpenAiSubscriptionProviderOptions? options = null,
        HttpClient? client = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _options = options ?? OpenAiSubscriptionProviderOptions.CreateDefault();
        _client = client ?? SharedClient;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ResponsesPath);
    }

    public string ProviderId => Id;

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        NativeToolCalling: true,
        Images: false,
        ReasoningControls: true,
        ConversationContinuation: false);

    public ValueTask<ProviderAuthenticationStatus> GetAuthenticationStatusAsync(
        CancellationToken cancellationToken = default) =>
        _credentials.GetAuthenticationStatusAsync(cancellationToken);

    public ValueTask<ProviderAuthenticationStatus> ImportCodexSessionAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        _credentials.ImportCodexSessionAsync(path, cancellationToken);

    public ValueTask<ProviderAuthenticationStatus> SignInWithBrowserAsync(
        Func<Uri, CancellationToken, ValueTask> openBrowser,
        OpenAiSubscriptionBrowserLoginOptions? options = null,
        CancellationToken cancellationToken = default) =>
        new OpenAiSubscriptionBrowserLogin(_credentials)
            .SignInAsync(openBrowser, options, cancellationToken);

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
        _credentials.DisconnectAsync(cancellationToken);

    public async IAsyncEnumerable<ModelEvent> RunAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            OpenAiSubscriptionSession? session = null;
            ModelProviderError? authenticationError = null;
            try
            {
                session = await _credentials.GetSessionAsync(
                    forceRefresh: attempt > 0,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OpenAiAuthenticationException exception)
            {
                authenticationError = new ModelProviderError(
                    exception.Code,
                    exception.Message,
                    exception.Retryable);
            }

            if (authenticationError is not null)
            {
                yield return authenticationError;
                yield break;
            }

            using var message = CreateRequest(request, session!);
            HttpResponseMessage? response = null;
            ModelProviderError? transportError = null;
            try
            {
                response = await _client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                transportError = new ModelProviderError(
                    "openai-subscription-transport",
                    "Deckwraith could not reach OpenAI's ChatGPT subscription service.",
                    true);
            }

            if (transportError is not null)
            {
                yield return transportError;
                yield break;
            }

            using (var receivedResponse = response!)
            {
                if (receivedResponse.StatusCode is HttpStatusCode.Unauthorized && attempt == 0)
                {
                    continue;
                }

                if (!receivedResponse.IsSuccessStatusCode)
                {
                    var detail = await ProviderHttp.ReadErrorAsync(receivedResponse, cancellationToken)
                        .ConfigureAwait(false);
                    var rejected = receivedResponse.StatusCode is HttpStatusCode.Unauthorized or
                        HttpStatusCode.Forbidden;
                    var messageText = rejected
                        ? "OpenAI rejected the ChatGPT subscription session. Reconnect the account."
                        : detail;
                    if (rejected)
                    {
                        _credentials.MarkRejected(messageText);
                    }

                    yield return new ModelProviderError(
                        rejected ? "credential-rejected" : "openai-subscription-http",
                        messageText,
                        ProviderHttp.IsRetryable(receivedResponse.StatusCode));
                    yield break;
                }

                await foreach (var modelEvent in OpenAIResponsesEventReader.ReadAsync(
                    receivedResponse,
                    ProviderId,
                    request.RequestId,
                    cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return modelEvent;
                }

                yield break;
            }
        }
    }

    private HttpRequestMessage CreateRequest(
        ModelRequest request,
        OpenAiSubscriptionSession session)
    {
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
            input = new[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = ProviderPrompt.BuildInput(request),
                        },
                    },
                },
            },
            stream = true,
            store = false,
            reasoning = request.ReasoningEffort is null
                ? null
                : new { effort = request.ReasoningEffort },
            tools = tools.Length == 0 ? null : tools,
        };
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderHttp.Endpoint(_options.BaseUri, _options.ResponsesPath))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("Deckwraith", "1.0"));
        message.Headers.TryAddWithoutValidation("chatgpt-account-id", session.AccountId);
        message.Headers.TryAddWithoutValidation("originator", "deckwraith");
        return message;
    }
}
