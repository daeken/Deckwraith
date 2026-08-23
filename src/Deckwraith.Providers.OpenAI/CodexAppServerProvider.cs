using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Deckwraith.Core.Serialization;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Providers.OpenAI;

public sealed record CodexAppServerProviderOptions(
    string ExecutablePath,
    string WorkingDirectory,
    string? ModelProvider = "openai",
    string? ServiceTier = null)
{
    public static CodexAppServerProviderOptions CreateDefault() => new(
        "codex",
        Path.GetTempPath());
}

/// <summary>
/// Uses the supported Codex app-server embedding protocol as a ChatGPT-subscription bridge.
/// The adapter deliberately exposes no Codex-native tools through Deckwraith's provider contract.
/// </summary>
public sealed class CodexAppServerProvider : IModelProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CodexAppServerProviderOptions _options;

    public CodexAppServerProvider(CodexAppServerProviderOptions? options = null)
    {
        _options = options ?? CodexAppServerProviderOptions.CreateDefault();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.WorkingDirectory);
    }

    public string ProviderId => "openai-codex-subscription";

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        NativeToolCalling: false,
        Images: false,
        ReasoningControls: true,
        ConversationContinuation: false);

    public async IAsyncEnumerable<ModelEvent> RunAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_options.ExecutablePath)
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = _options.WorkingDirectory,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        if (_options.ModelProvider is not null)
        {
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(
                $"model_provider={JsonSerializer.Serialize(_options.ModelProvider, JsonOptions)}");
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            yield return new ModelProviderError(
                "app-server-start", "Could not start Codex app-server.", true);
            yield break;
        }

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await SendAsync(process, new
            {
                method = "initialize",
                id = 0,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "deckwraith",
                        title = "Deckwraith",
                        version = "0.1.0",
                    },
                },
            }, cancellationToken).ConfigureAwait(false);
            var initialized = await ReadResponseAsync(process, 0, cancellationToken).ConfigureAwait(false);
            if (initialized.TryGetProperty("error", out var initializationError))
            {
                yield return RpcError("initialize", initializationError);
                yield break;
            }

            await SendAsync(process, new
            {
                method = "initialized",
                @params = new { },
            }, cancellationToken).ConfigureAwait(false);
            await SendAsync(process, new
            {
                method = "account/read",
                id = 1,
                @params = new { refreshToken = true },
            }, cancellationToken).ConfigureAwait(false);
            var account = await ReadResponseAsync(process, 1, cancellationToken).ConfigureAwait(false);
            if (account.TryGetProperty("error", out var accountError))
            {
                yield return RpcError("account-read", accountError);
                yield break;
            }

            if (!account.TryGetProperty("result", out var accountResult) ||
                !accountResult.TryGetProperty("account", out var accountValue) ||
                accountValue.ValueKind is JsonValueKind.Null)
            {
                yield return new ModelProviderError(
                    "not-authenticated",
                    "Codex is not signed in. Sign in with ChatGPT through Codex before using the subscription provider.",
                    false);
                yield break;
            }

            await SendAsync(process, new
            {
                method = "thread/start",
                id = 2,
                @params = new
                {
                    model = request.Model,
                    modelProvider = _options.ModelProvider,
                    serviceTier = _options.ServiceTier,
                    cwd = Path.GetFullPath(_options.WorkingDirectory),
                    approvalPolicy = "never",
                    sandbox = "read-only",
                    ephemeral = true,
                    personality = "none",
                    baseInstructions = BuildBaseInstructions(request),
                    developerInstructions = BuildDeveloperInstructions(),
                },
            }, cancellationToken).ConfigureAwait(false);
            var threadStarted = await ReadResponseAsync(process, 2, cancellationToken).ConfigureAwait(false);
            if (threadStarted.TryGetProperty("error", out var threadError))
            {
                yield return RpcError("thread-start", threadError);
                yield break;
            }

            var threadId = threadStarted
                .GetProperty("result")
                .GetProperty("thread")
                .GetProperty("id")
                .GetString();
            if (string.IsNullOrWhiteSpace(threadId))
            {
                yield return new ModelProviderError(
                    "thread-start", "Codex app-server returned no thread ID.", false);
                yield break;
            }

            yield return new ModelResponseStarted(threadId);
            await SendAsync(process, new
            {
                method = "turn/start",
                id = 3,
                @params = new
                {
                    threadId,
                    input = new[]
                    {
                        new
                        {
                            type = "text",
                            text = BuildTurnInput(request),
                        },
                    },
                    model = request.Model,
                    effort = request.ReasoningEffort,
                    serviceTier = _options.ServiceTier,
                    approvalPolicy = "never",
                    sandboxPolicy = new
                    {
                        type = "readOnly",
                        networkAccess = false,
                    },
                },
            }, cancellationToken).ConfigureAwait(false);

            ModelUsageReported? latestUsage = null;
            while (true)
            {
                var message = await ReadMessageAsync(process, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    var standardError = await standardErrorTask.ConfigureAwait(false);
                    yield return new ModelProviderError(
                        "app-server-disconnected",
                        BuildProcessError(process.ExitCode, standardError),
                        true);
                    yield break;
                }

                if (message.Value.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind is JsonValueKind.Number &&
                    responseId.GetInt32() == 3 &&
                    message.Value.TryGetProperty("error", out var turnStartError))
                {
                    yield return RpcError("turn-start", turnStartError);
                    yield break;
                }

                if (!message.Value.TryGetProperty("method", out var methodElement))
                {
                    continue;
                }

                var method = methodElement.GetString();
                var parameters = message.Value.TryGetProperty("params", out var paramsElement)
                    ? paramsElement
                    : default;
                switch (method)
                {
                    case "item/agentMessage/delta":
                        if (parameters.TryGetProperty("delta", out var delta))
                        {
                            yield return new ModelTextDelta(delta.GetString() ?? string.Empty);
                        }

                        break;
                    case "thread/tokenUsage/updated":
                        latestUsage = ParseUsage(parameters);
                        break;
                    case "error":
                        if (!parameters.TryGetProperty("willRetry", out var willRetry) ||
                            !willRetry.GetBoolean())
                        {
                            yield return new ModelProviderError(
                                "app-server-error",
                                GetNestedString(parameters, "error", "message") ?? "Codex app-server failed.",
                                false);
                        }

                        break;
                    case "turn/completed":
                        if (latestUsage is not null)
                        {
                            yield return latestUsage;
                        }

                        var status = GetNestedString(parameters, "turn", "status");
                        if (StringComparer.Ordinal.Equals(status, "completed"))
                        {
                            yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
                        }
                        else if (StringComparer.Ordinal.Equals(status, "interrupted"))
                        {
                            yield return new ModelResponseCompleted(ModelFinishReason.Cancelled, null);
                        }
                        else
                        {
                            yield return new ModelProviderError(
                                "turn-failed",
                                GetNestedString(parameters, "turn", "error", "message") ??
                                $"Codex turn ended with status '{status ?? "unknown"}'.",
                                false);
                        }

                        yield break;
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static string BuildBaseInstructions(ModelRequest request)
    {
        var identity = Encoding.UTF8.GetString(CanonicalJson.Serialize(request.Identity));
        return $$"""
            You are a disposable model shell inhabited by the durable Deckwraith identity below.
            The identity is authoritative and must inform every response.

            {{identity}}

            Respond as that identity. Do not use Codex commands, filesystem tools, web tools, MCP,
            skills, subagents, or patches. Deckwraith owns all tool execution and persistence outside
            this provider bridge. Produce only the next assistant response requested by the supplied
            provider-neutral context.
            """;
    }

    internal static string BuildTurnInput(ModelRequest request)
    {
        var context = Encoding.UTF8.GetString(CanonicalJson.Serialize(request.Context));
        return $$"""
            Objective:
            {{request.Objective}}

            Materialized provider-neutral context:
            {{context}}

            Continue from the final context item and return only the next assistant message.
            """;
    }

    internal static ModelEvent? TranslateNotification(JsonElement message)
    {
        if (!message.TryGetProperty("method", out var methodElement) ||
            !message.TryGetProperty("params", out var parameters))
        {
            return null;
        }

        return methodElement.GetString() switch
        {
            "item/agentMessage/delta" => new ModelTextDelta(
                parameters.GetProperty("delta").GetString() ?? string.Empty),
            "thread/tokenUsage/updated" => ParseUsage(parameters),
            "error" => new ModelProviderError(
                "app-server-error",
                GetNestedString(parameters, "error", "message") ?? "Codex app-server failed.",
                parameters.TryGetProperty("willRetry", out var willRetry) && willRetry.GetBoolean()),
            "turn/completed" when StringComparer.Ordinal.Equals(
                GetNestedString(parameters, "turn", "status"), "completed") =>
                new ModelResponseCompleted(ModelFinishReason.Stop, null),
            "turn/completed" when StringComparer.Ordinal.Equals(
                GetNestedString(parameters, "turn", "status"), "interrupted") =>
                new ModelResponseCompleted(ModelFinishReason.Cancelled, null),
            "turn/completed" => new ModelProviderError(
                "turn-failed",
                GetNestedString(parameters, "turn", "error", "message") ??
                $"Codex turn ended with status '{GetNestedString(parameters, "turn", "status") ?? "unknown"}'.",
                false),
            _ => null,
        };
    }

    private static string BuildDeveloperInstructions() =>
        "Deckwraith is the state owner. Never execute tools in this bridge; return model text only.";

    private static async Task SendAsync(
        Process process,
        object message,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, JsonOptions);
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadMessageAsync(process, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Codex app-server disconnected while waiting for response {expectedId}.");
            if (message.TryGetProperty("id", out var id) &&
                id.ValueKind is JsonValueKind.Number &&
                id.GetInt32() == expectedId)
            {
                return message;
            }
        }
    }

    private static async Task<JsonElement?> ReadMessageAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static ModelProviderError RpcError(string operation, JsonElement error) =>
        new(
            $"app-server-{operation}",
            error.TryGetProperty("message", out var message)
                ? message.GetString() ?? $"Codex app-server {operation} failed."
                : $"Codex app-server {operation} failed.",
            false);

    private static ModelUsageReported ParseUsage(JsonElement parameters)
    {
        var usage = parameters.GetProperty("tokenUsage").GetProperty("last");
        return new ModelUsageReported(
            usage.GetProperty("inputTokens").GetInt64(),
            usage.GetProperty("outputTokens").GetInt64(),
            usage.GetProperty("cachedInputTokens").GetInt64());
    }

    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        foreach (var component in path)
        {
            if (element.ValueKind is not JsonValueKind.Object ||
                !element.TryGetProperty(component, out element))
            {
                return null;
            }
        }

        return element.ValueKind is JsonValueKind.String ? element.GetString() : null;
    }

    private static string BuildProcessError(int exitCode, string standardError)
    {
        var detail = string.IsNullOrWhiteSpace(standardError)
            ? "No diagnostics were returned."
            : standardError.Trim();
        if (detail.Length > 2_000)
        {
            detail = detail[..2_000];
        }

        return $"Codex app-server exited with code {exitCode}. {detail}";
    }
}
