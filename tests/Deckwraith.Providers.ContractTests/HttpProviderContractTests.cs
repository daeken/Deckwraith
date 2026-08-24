using System.Net;
using System.Text;
using System.Text.Json;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Anthropic;
using Deckwraith.Providers.Google;
using Deckwraith.Providers.OpenAICompatible;

namespace Deckwraith.Providers.ContractTests;

public sealed class HttpProviderContractTests
{
    [Fact]
    public async Task AnthropicMapsNativeStreamToCanonicalEvents()
    {
        using var credential = new EnvironmentCredential("anthropic-test-key");
        var handler = new RecordingHandler("""
            data: {"type":"message_start","message":{"id":"msg-1","usage":{"input_tokens":11}}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello "}}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call-1","name":"Invoke-PowerShell","input":{}}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"script\":\"Get-Command\"}"}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}

            data: {"type":"message_stop"}

            """);
        var provider = new AnthropicProvider(
            new AnthropicProviderOptions(new Uri("https://anthropic.test/"), credential.Name),
            new HttpClient(handler));

        var events = await CollectAsync(provider, CreateRequest("anthropic"));

        Assert.Equal("msg-1", Assert.IsType<ModelResponseStarted>(events[0]).ProviderRequestId);
        Assert.Equal("hello ", Assert.IsType<ModelTextDelta>(events[1]).Delta);
        var call = Assert.IsType<ModelToolCallCompleted>(events[2]);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("Get-Command", call.Arguments.GetProperty("script").GetString());
        Assert.Equal(new ModelUsageReported(11, 7, null), Assert.IsType<ModelUsageReported>(events[3]));
        Assert.Equal(
            ModelFinishReason.ToolCalls,
            Assert.IsType<ModelResponseCompleted>(events[4]).FinishReason);
        Assert.Equal("test-secret", handler.RequestHeaders["x-api-key"]);
        Assert.Contains("curious and incisive", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("never disclose credentials", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAICompatibleMapsResponsesStreamToCanonicalEvents()
    {
        using var credential = new EnvironmentCredential("openai-compatible-test-key");
        var handler = new RecordingHandler("""
            data: {"type":"response.created","response":{"id":"resp-1"}}

            data: {"type":"response.output_text.delta","delta":"hello "}

            data: {"type":"response.output_item.done","item":{"id":"fc-1","type":"function_call","call_id":"call-1","name":"Invoke-PowerShell","arguments":"{\"script\":\"Get-Command\"}"}}

            data: {"type":"response.completed","response":{"id":"resp-1","usage":{"input_tokens":13,"output_tokens":5,"input_tokens_details":{"cached_tokens":3}}}}

            data: [DONE]

            """);
        var provider = new OpenAICompatibleProvider(
            new OpenAICompatibleProviderOptions(
                new Uri("https://openai.test/"),
                credential.Name,
                ProviderId: "xai-api"),
            new HttpClient(handler));

        var events = await CollectAsync(provider, CreateRequest("xai-api"));

        Assert.Equal("resp-1", Assert.IsType<ModelResponseStarted>(events[0]).ProviderRequestId);
        Assert.Equal("hello ", Assert.IsType<ModelTextDelta>(events[1]).Delta);
        var call = Assert.IsType<ModelToolCallCompleted>(events[2]);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("Get-Command", call.Arguments.GetProperty("script").GetString());
        Assert.Equal(new ModelUsageReported(13, 5, 3), Assert.IsType<ModelUsageReported>(events[3]));
        Assert.Equal(
            ModelFinishReason.ToolCalls,
            Assert.IsType<ModelResponseCompleted>(events[4]).FinishReason);
        Assert.Equal("Bearer test-secret", handler.Authorization);
        Assert.EndsWith("/v1/responses", handler.RequestUri, StringComparison.Ordinal);
        Assert.Equal("xai-api", provider.ProviderId);
    }

    [Fact]
    public async Task GoogleMapsGeminiStreamToCanonicalEvents()
    {
        using var credential = new EnvironmentCredential("google-test-key");
        var handler = new RecordingHandler("""
            data: {"responseId":"gemini-1","candidates":[{"content":{"parts":[{"text":"hello "}]}}]}

            data: {"candidates":[{"finishReason":"STOP","content":{"parts":[{"functionCall":{"name":"Invoke-PowerShell","args":{"script":"Get-Command"}}}]}}],"usageMetadata":{"promptTokenCount":17,"candidatesTokenCount":9,"cachedContentTokenCount":4}}

            """);
        var provider = new GoogleGeminiProvider(
            new GoogleGeminiProviderOptions(
                new Uri("https://google.test/"), credential.Name),
            new HttpClient(handler));

        var events = await CollectAsync(provider, CreateRequest("google-gemini"));

        Assert.Equal("gemini-1", Assert.IsType<ModelResponseStarted>(events[0]).ProviderRequestId);
        Assert.Equal("hello ", Assert.IsType<ModelTextDelta>(events[1]).Delta);
        var call = Assert.IsType<ModelToolCallCompleted>(events[2]);
        Assert.Equal("Get-Command", call.Arguments.GetProperty("script").GetString());
        Assert.Equal(new ModelUsageReported(17, 9, 4), Assert.IsType<ModelUsageReported>(events[3]));
        Assert.Equal(
            ModelFinishReason.ToolCalls,
            Assert.IsType<ModelResponseCompleted>(events[4]).FinishReason);
        Assert.Equal("test-secret", handler.RequestHeaders["x-goog-api-key"]);
        Assert.DoesNotContain("test-secret", handler.RequestUri, StringComparison.Ordinal);
    }

    private static async Task<List<ModelEvent>> CollectAsync(
        IModelProvider provider,
        ModelRequest request)
    {
        var events = new List<ModelEvent>();
        await foreach (var modelEvent in provider.RunAsync(request, CancellationToken.None))
        {
            events.Add(modelEvent);
        }

        return events;
    }

    private static ModelRequest CreateRequest(string provider)
    {
        var identity = IdentityDocument.CreateSparse(
            CanonicalName.Parse("lumen"), DateTimeOffset.UnixEpoch) with
        {
            Personality = "curious and incisive",
            Calibration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["register"] = "terse and playful",
                ["opsec"] = "never disclose credentials",
            },
        };
        var context = CurrentContextDocument.Create(
            CanonicalName.Parse("lumen"), CanonicalJson.Hash(identity), 8, DateTimeOffset.UnixEpoch) with
        {
            Items = [ContextItem.Message("message-1", ContextRole.User, "hello", 1)],
        };
        return new ModelRequest(
            "request-1",
            "test-model",
            "Prove provider independence",
            identity,
            context,
            ContextManifestBuilder.Build(
                identity, context, "Prove provider independence", provider, "test-model", []),
            [
                new ModelToolDefinition(
                    "Invoke-PowerShell",
                    "Run PowerShell.",
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new { script = new { type = "string" } },
                    })),
            ],
            "high",
            1024,
            null);
    }

    private sealed class EnvironmentCredential : IDisposable
    {
        public EnvironmentCredential(string name)
        {
            Name = "DECKWRAITH_TEST_" + name.ToUpperInvariant().Replace('-', '_');
            Environment.SetEnvironmentVariable(Name, "test-secret");
        }

        public string Name { get; }

        public void Dispose() => Environment.SetEnvironmentVariable(Name, null);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        public string RequestUri { get; private set; } = string.Empty;

        public string? Authorization { get; private set; }

        public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Authorization = request.Headers.Authorization?.ToString();
            foreach (var header in request.Headers)
            {
                RequestHeaders[header.Key] = string.Join(",", header.Value);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
