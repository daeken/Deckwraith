using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.OpenAI;

namespace Deckwraith.Providers.ContractTests;

public sealed class OpenAiSubscriptionProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    [Fact]
    public async Task MissingCredentialHasExplicitStatusAndCanonicalError()
    {
        var store = new MemoryCredentialStore();
        var manager = CreateManager(store);
        var provider = new OpenAiSubscriptionProvider(manager);

        var status = await manager.GetAuthenticationStatusAsync();
        var events = await CollectAsync(provider, CreateRequest());

        Assert.Equal(ProviderAuthenticationState.Missing, status.State);
        Assert.Equal(ProviderAccessKind.Subscription, status.AccessKind);
        Assert.Equal("credential-missing", Assert.IsType<ModelProviderError>(Assert.Single(events)).Code);
    }

    [Fact]
    public async Task CredentialStoreFailureBecomesActionableStatus()
    {
        var manager = new OpenAiSubscriptionCredentialManager(
            new FailingCredentialStore(),
            timeProvider: new FixedTimeProvider(Now));

        var status = await manager.GetAuthenticationStatusAsync();

        Assert.Equal(ProviderAuthenticationState.Error, status.State);
        Assert.Contains("could not read", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingCodexSessionImportsWithoutPersistingItInTheDeck()
    {
        var store = new MemoryCredentialStore();
        var manager = CreateManager(store);
        var authPath = Path.Combine(Path.GetTempPath(), $"deckwraith-auth-{Guid.NewGuid():N}.json");
        var accessToken = Jwt(new
        {
            exp = Now.AddHours(1).ToUnixTimeSeconds(),
            chatgpt_account_id = "account-1",
        });
        var idToken = Jwt(new { exp = Now.AddHours(1).ToUnixTimeSeconds(), email = "sera@example.test" });
        try
        {
            await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
            {
                auth_mode = "chatgpt",
                tokens = new
                {
                    access_token = accessToken,
                    refresh_token = "refresh-token",
                    id_token = idToken,
                    account_id = "account-1",
                },
            }));

            var status = await manager.ImportCodexSessionAsync(authPath);

            Assert.Equal(ProviderAuthenticationState.Ready, status.State);
            Assert.Equal("sera@example.test", status.AccountLabel);
            Assert.Equal("memory", manager.StorageKind);
            Assert.NotNull(store.Payload);
            Assert.DoesNotContain(authPath, store.Payload, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(authPath);
        }
    }

    [Fact]
    public void BrowserAuthorizationUsesPkceWithoutExposingTheVerifier()
    {
        var manager = CreateManager(new MemoryCredentialStore());

        var authorization = manager.CreateAuthorizationRequest(
            new Uri("http://localhost:1455/auth/callback"));
        var query = ParseQuery(authorization.AuthorizationUri);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(authorization.CodeVerifier))),
            query["code_challenge"]);
        Assert.Equal("openid profile email offline_access", query["scope"]);
        Assert.Equal("http://localhost:1455/auth/callback", query["redirect_uri"]);
        Assert.Equal("deckwraith", query["originator"]);
        Assert.Equal("true", query["id_token_add_organizations"]);
        Assert.Equal("true", query["codex_cli_simplified_flow"]);
        Assert.Equal(authorization.State, query["state"]);
        Assert.DoesNotContain(
            authorization.CodeVerifier,
            authorization.AuthorizationUri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.InRange(authorization.CodeVerifier.Length, 43, 128);
    }

    [Fact]
    public async Task BrowserLoginOwnsLoopbackCallbackAndPersistsSession()
    {
        var store = new MemoryCredentialStore();
        var accessToken = Jwt(new
        {
            exp = Now.AddHours(1).ToUnixTimeSeconds(),
            chatgpt_account_id = "account-1",
        });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = accessToken,
                refresh_token = "browser-refresh-token",
                id_token = Jwt(new
                {
                    exp = Now.AddHours(1).ToUnixTimeSeconds(),
                    email = "sera@example.test",
                }),
                expires_in = 3600,
            }), Encoding.UTF8, "application/json"),
        });
        var manager = CreateManager(store, new HttpClient(handler));
        var provider = new OpenAiSubscriptionProvider(manager);
        var callbackPort = FindFreeTcpPort();
        using var callbackClient = new HttpClient();
        Task<HttpResponseMessage>? callbackTask = null;

        var status = await provider.SignInWithBrowserAsync(
            (authorizationUri, cancellationToken) =>
            {
                var query = ParseQuery(authorizationUri);
                var callbackUri = new UriBuilder(query["redirect_uri"])
                {
                    Query = "code=browser-code&state=" + Uri.EscapeDataString(query["state"]),
                }.Uri;
                callbackTask = callbackClient.GetAsync(callbackUri, cancellationToken);
                return ValueTask.CompletedTask;
            },
            new OpenAiSubscriptionBrowserLoginOptions(callbackPort, TimeoutSeconds: 10));
        using var callbackResponse = await callbackTask!;
        var callbackBody = await callbackResponse.Content.ReadAsStringAsync();

        Assert.Equal(ProviderAuthenticationState.Ready, status.State);
        Assert.Equal("sera@example.test", status.AccountLabel);
        Assert.Contains("grant_type=authorization_code", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("code=browser-code", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("browser-refresh-token", store.Payload, StringComparison.Ordinal);
        Assert.Contains("connected", callbackBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiringSessionRefreshesAndRotatesCredential()
    {
        var store = new MemoryCredentialStore();
        var refreshedAccessToken = Jwt(new
        {
            exp = Now.AddHours(2).ToUnixTimeSeconds(),
            chatgpt_account_id = "account-1",
        });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = refreshedAccessToken,
                refresh_token = "rotated-refresh-token",
                expires_in = 7200,
            }), Encoding.UTF8, "application/json"),
        });
        var manager = CreateManager(store, new HttpClient(handler));
        await manager.SaveSessionAsync(
            Jwt(new { exp = Now.AddSeconds(20).ToUnixTimeSeconds(), chatgpt_account_id = "account-1" }),
            "refresh-token",
            null,
            "account-1",
            Now.AddSeconds(20));

        var session = await manager.GetSessionAsync(false, CancellationToken.None);

        Assert.Equal("account-1", session.AccountId);
        Assert.Equal(Now.AddHours(2), session.ExpiresAt);
        Assert.Contains("grant_type=refresh_token", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("client_id=app_", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("rotated-refresh-token", store.Payload, StringComparison.Ordinal);
        Assert.Equal(
            ProviderAuthenticationState.Ready,
            (await manager.GetAuthenticationStatusAsync()).State);
    }

    [Fact]
    public async Task RejectedRefreshBecomesActionableAuthenticationState()
    {
        var store = new MemoryCredentialStore();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json"),
        });
        var manager = CreateManager(store, new HttpClient(handler));
        await manager.SaveSessionAsync(
            Jwt(new { exp = Now.AddSeconds(-1).ToUnixTimeSeconds(), chatgpt_account_id = "account-1" }),
            "refresh-token",
            null,
            "account-1",
            Now.AddSeconds(-1));

        var exception = await Assert.ThrowsAsync<OpenAiAuthenticationException>(
            async () => await manager.GetSessionAsync(false, CancellationToken.None));
        var status = await manager.GetAuthenticationStatusAsync();

        Assert.Equal("credential-rejected", exception.Code);
        Assert.Equal(ProviderAuthenticationState.Rejected, status.State);
        Assert.Contains("Reconnect", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeSubscriptionTransportMapsResponsesWithoutStartingAnotherProcess()
    {
        var store = new MemoryCredentialStore();
        var manager = CreateManager(store);
        await manager.SaveSessionAsync(
            Jwt(new { exp = Now.AddHours(1).ToUnixTimeSeconds(), chatgpt_account_id = "account-1" }),
            "refresh-token",
            null,
            "account-1",
            Now.AddHours(1));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                data: {"type":"response.created","response":{"id":"resp-1"}}

                data: {"type":"response.output_text.delta","delta":"hello "}

                data: {"type":"response.output_item.done","item":{"id":"fc-1","type":"function_call","call_id":"call-1","name":"Invoke-PowerShell","arguments":"{\"script\":\"Get-Command\"}"}}

                data: {"type":"response.completed","response":{"id":"resp-1","usage":{"input_tokens":13,"output_tokens":5,"input_tokens_details":{"cached_tokens":3}}}}

                data: [DONE]

                """, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiSubscriptionProvider(
            manager,
            new OpenAiSubscriptionProviderOptions(new Uri("https://chatgpt.test/")),
            new HttpClient(handler));

        var events = await CollectAsync(provider, CreateRequest());

        Assert.Equal("resp-1", Assert.IsType<ModelResponseStarted>(events[0]).ProviderRequestId);
        Assert.Equal("hello ", Assert.IsType<ModelTextDelta>(events[1]).Delta);
        Assert.Equal("call-1", Assert.IsType<ModelToolCallCompleted>(events[2]).CallId);
        Assert.Equal(new ModelUsageReported(13, 5, 3), Assert.IsType<ModelUsageReported>(events[3]));
        Assert.Equal(
            ModelFinishReason.ToolCalls,
            Assert.IsType<ModelResponseCompleted>(events[4]).FinishReason);
        Assert.Equal("Bearer " + Jwt(new { exp = Now.AddHours(1).ToUnixTimeSeconds(), chatgpt_account_id = "account-1" }), handler.Authorization);
        Assert.Equal("account-1", handler.RequestHeaders["chatgpt-account-id"]);
        Assert.Equal("deckwraith", handler.RequestHeaders["originator"]);
        Assert.EndsWith("/backend-api/codex/responses", handler.RequestUri, StringComparison.Ordinal);
        Assert.Contains("curious and incisive", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("never disclose credentials", handler.RequestBody, StringComparison.Ordinal);
        using var requestBody = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal(JsonValueKind.Array, requestBody.RootElement.GetProperty("input").ValueKind);
        Assert.False(requestBody.RootElement.TryGetProperty("max_output_tokens", out _));
    }

    [Fact]
    public async Task UnauthorizedTransportForcesOneRefreshBeforeRejecting()
    {
        var store = new MemoryCredentialStore();
        var token = Jwt(new { exp = Now.AddHours(1).ToUnixTimeSeconds(), chatgpt_account_id = "account-1" });
        var refreshHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = token,
                refresh_token = "rotated",
                expires_in = 3600,
            }), Encoding.UTF8, "application/json"),
        });
        var manager = CreateManager(store, new HttpClient(refreshHandler));
        await manager.SaveSessionAsync(token, "refresh-token", null, "account-1", Now.AddHours(1));
        var requests = 0;
        var providerHandler = new RecordingHandler(_ =>
        {
            requests++;
            return requests == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("unauthorized"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        data: {"type":"response.completed","response":{"id":"resp-1"}}

                        """, Encoding.UTF8, "text/event-stream"),
                };
        });
        var provider = new OpenAiSubscriptionProvider(
            manager,
            new OpenAiSubscriptionProviderOptions(new Uri("https://chatgpt.test/")),
            new HttpClient(providerHandler));

        var events = await CollectAsync(provider, CreateRequest());

        Assert.Equal(2, requests);
        Assert.Contains(events, modelEvent => modelEvent is ModelResponseCompleted);
        Assert.DoesNotContain(events, modelEvent => modelEvent is ModelProviderError);
    }

    [Fact]
    public async Task SubscriptionErrorsCannotEchoTheAccessToken()
    {
        var store = new MemoryCredentialStore();
        var token = Jwt(new
        {
            exp = Now.AddHours(1).ToUnixTimeSeconds(),
            chatgpt_account_id = "account-1",
        });
        var manager = CreateManager(store);
        await manager.SaveSessionAsync(token, "refresh-token", null, "account-1", Now.AddHours(1));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { message = $"Rejected {token}" } }),
                Encoding.UTF8,
                "application/json"),
        });
        var provider = new OpenAiSubscriptionProvider(
            manager,
            new OpenAiSubscriptionProviderOptions(new Uri("https://chatgpt.test/")),
            new HttpClient(handler));

        var error = Assert.IsType<ModelProviderError>(Assert.Single(
            await CollectAsync(provider, CreateRequest())));

        Assert.Equal("Rejected [redacted]", error.Message);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task OpenAiSubscriptionLiveSmokeIsManuallyGated()
    {
        var authPath = Environment.GetEnvironmentVariable("DECKWRAITH_OPENAI_LIVE_AUTH_PATH");
        if (string.IsNullOrWhiteSpace(authPath))
        {
            return;
        }

        var store = new MemoryCredentialStore();
        var manager = new OpenAiSubscriptionCredentialManager(store);
        await manager.ImportCodexSessionAsync(authPath);
        var provider = new OpenAiSubscriptionProvider(manager);
        var model = Environment.GetEnvironmentVariable("DECKWRAITH_OPENAI_LIVE_MODEL") ??
            "gpt-5.6-sol";
        var request = CreateRequest() with
        {
            Model = model,
            Objective = "Verify Deckwraith's native OpenAI subscription transport.",
            Tools = [],
        };

        var events = await CollectAsync(provider, request);

        Assert.DoesNotContain(events, modelEvent => modelEvent is ModelProviderError);
        Assert.Contains(events, modelEvent => modelEvent is ModelTextDelta);
        Assert.Contains(events, modelEvent => modelEvent is ModelResponseCompleted);
    }

    private static OpenAiSubscriptionCredentialManager CreateManager(
        MemoryCredentialStore store,
        HttpClient? client = null) => new(
        store,
        client: client,
        timeProvider: new FixedTimeProvider(Now));

    private static async Task<List<ModelEvent>> CollectAsync(
        OpenAiSubscriptionProvider provider,
        ModelRequest request)
    {
        var events = new List<ModelEvent>();
        await foreach (var modelEvent in provider.RunAsync(request, CancellationToken.None))
        {
            events.Add(modelEvent);
        }

        return events;
    }

    private static ModelRequest CreateRequest()
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
                identity,
                context,
                "Prove provider independence",
                OpenAiSubscriptionProvider.Id,
                "test-model",
                []),
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

    private static string Jwt(object claims)
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}")) + "." +
            Encode(JsonSerializer.SerializeToUtf8Bytes(claims)) + ".signature";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Split('=', 2))
            .ToDictionary(
                segment => Uri.UnescapeDataString(segment[0]),
                segment => Uri.UnescapeDataString(segment.ElementAtOrDefault(1) ?? string.Empty),
                StringComparer.Ordinal);

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class MemoryCredentialStore : IProviderCredentialStore
    {
        public string StorageKind => "memory";

        public string? Payload { get; private set; }

        public ValueTask<string?> ReadAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Payload);

        public ValueTask WriteAsync(
            string credentialId,
            string payload,
            CancellationToken cancellationToken = default)
        {
            Payload = payload;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string credentialId,
            CancellationToken cancellationToken = default)
        {
            Payload = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingCredentialStore : IProviderCredentialStore
    {
        public string StorageKind => "failing-test";

        public ValueTask<string?> ReadAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Synthetic credential-store failure.");

        public ValueTask WriteAsync(
            string credentialId,
            string payload,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(
            string credentialId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
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

            return responseFactory(request);
        }
    }
}
