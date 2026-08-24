using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Deckwraith.Core.Serialization;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Providers.Http;

public static class ProviderPrompt
{
    public static string BuildSystem(ModelRequest request)
    {
        var identity = Encoding.UTF8.GetString(CanonicalJson.Serialize(request.Identity));
        return $$"""
            You are a replaceable model shell inhabited by the durable Deckwraith identity below.
            The identity is authoritative and must inform every response.

            {{identity}}

            Respond as that identity. Deckwraith owns tool execution, persistence, and recovery.
            Return only the next assistant response requested by the supplied provider-neutral context.
            """;
    }

    public static string BuildInput(ModelRequest request)
    {
        var context = Encoding.UTF8.GetString(CanonicalJson.Serialize(request.Context));
        return $$"""
            Objective:
            {{request.Objective}}

            Materialized provider-neutral context:
            {{context}}

            Continue from the final context item. If a tool is needed, call one of the supplied
            Deckwraith tools; its durable result will appear in context on the next invocation.
            """;
    }
}

public static class ProviderHttp
{
    public static Uri Endpoint(Uri baseUri, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + '/', UriKind.Absolute);
        return new Uri(normalized, relativePath.TrimStart('/'));
    }

    public static string ResolveCredential(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        return Environment.GetEnvironmentVariable(environmentName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Provider credential environment variable '{environmentName}' is not set.");
    }

    public static bool TryResolveCredential(
        string environmentName,
        out string credential,
        out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        credential = Environment.GetEnvironmentVariable(environmentName) ?? string.Empty;
        error = credential.Length == 0
            ? $"Provider credential environment variable '{environmentName}' is not set."
            : string.Empty;
        return credential.Length > 0;
    }

    public static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)status >= 500;

    public static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 4096)
        {
            body = body[..4096];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var pointer in new[] { "message", "error" })
            {
                if (root.TryGetProperty(pointer, out var value) && value.ValueKind is JsonValueKind.String)
                {
                    return value.GetString()!;
                }
            }

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind is JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind is JsonValueKind.String)
            {
                return message.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})"
            : body;
    }

    public static async IAsyncEnumerable<JsonElement> ReadSseDataAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (TryParseData(data, out var value))
                {
                    yield return value;
                }

                data.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        if (TryParseData(data, out var trailing))
        {
            yield return trailing;
        }
    }

    private static bool TryParseData(StringBuilder data, out JsonElement value)
    {
        value = default;
        if (data.Length == 0 || StringComparer.Ordinal.Equals(data.ToString(), "[DONE]"))
        {
            return false;
        }

        using var document = JsonDocument.Parse(data.ToString());
        value = document.RootElement.Clone();
        return true;
    }
}
