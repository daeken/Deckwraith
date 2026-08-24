using System.Diagnostics;
using System.Text.Json;
using Deckwraith.Core.Serialization;

namespace Deckwraith.Mcp;

internal sealed class StdioMcpClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly McpServerDefinition _definition;
    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task<string> _standardError;
    private long _nextRequestId;
    private bool _disposed;

    private StdioMcpClient(McpServerDefinition definition, Process process)
    {
        _definition = definition;
        _process = process;
        _standardError = process.StandardError.ReadToEndAsync();
    }

    public static async Task<StdioMcpClient> ConnectAsync(
        McpServerDefinition definition,
        string deckRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(definition.Command)
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = definition.WorkingDirectory is null
                ? deckRoot
                : Path.GetFullPath(definition.WorkingDirectory, deckRoot),
        };
        foreach (var argument in definition.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var reference in definition.EnvironmentReferences)
        {
            var value = Environment.GetEnvironmentVariable(reference.Value);
            if (value is null)
            {
                throw new McpProtocolException(
                    $"MCP server '{definition.Id}' requires host environment variable " +
                    $"'{reference.Value}' for '{reference.Key}'.");
            }

            startInfo.Environment[reference.Key] = value;
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new McpProtocolException($"Could not start MCP server '{definition.Id}'.");
        }

        var client = new StdioMcpClient(definition, process);
        try
        {
            _ = await client.RequestAsync(
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "deckwraith",
                        title = "Deckwraith",
                        version = "0.1.0",
                    },
                },
                cancellationToken).ConfigureAwait(false);
            await client.NotifyAsync(
                "notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<McpDiscoveredTool>> ListToolsAsync(
        CancellationToken cancellationToken)
    {
        var tools = new List<McpDiscoveredTool>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync(
                "tools/list", new { cursor }, cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("tools", out var toolsElement) ||
                toolsElement.ValueKind is not JsonValueKind.Array)
            {
                throw new McpProtocolException(
                    $"MCP server '{_definition.Id}' returned no tools array.");
            }

            foreach (var tool in toolsElement.EnumerateArray())
            {
                var name = tool.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new McpProtocolException(
                        $"MCP server '{_definition.Id}' returned a tool without a name.");
                }

                var inputSchema = tool.TryGetProperty("inputSchema", out var input)
                    ? input.Clone()
                    : CanonicalJson.ToElement(new
                    {
                        type = "object",
                        properties = new { },
                    });
                var outputSchema = tool.TryGetProperty("outputSchema", out var output)
                    ? output.Clone()
                    : (JsonElement?)null;
                tools.Add(new McpDiscoveredTool(
                    name,
                    tool.TryGetProperty("description", out var description)
                        ? description.GetString() ?? string.Empty
                        : string.Empty,
                    inputSchema,
                    outputSchema));
            }

            cursor = result.TryGetProperty("nextCursor", out var nextCursor) &&
                nextCursor.ValueKind is JsonValueKind.String
                ? nextCursor.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(cursor));

        return tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<McpToolCallResult> CallToolAsync(
        string tool,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var result = await RequestAsync(
            "tools/call",
            new { name = tool, arguments },
            cancellationToken).ConfigureAwait(false);
        var content = result.TryGetProperty("content", out var contentElement)
            ? contentElement.Clone()
            : CanonicalJson.ToElement(Array.Empty<object>());
        var structured = result.TryGetProperty("structuredContent", out var structuredElement)
            ? structuredElement.Clone()
            : CanonicalJson.ToElement(new { });
        return new McpToolCallResult(
            result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            structured,
            content,
            result.Clone());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        _process.Dispose();
        _gate.Dispose();
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = Interlocked.Increment(ref _nextRequestId);
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            }, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    var error = await _standardError.ConfigureAwait(false);
                    throw new McpProtocolException(
                        $"MCP server '{_definition.Id}' disconnected with exit code " +
                        $"{_process.ExitCode}: {error.Trim()}");
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) ||
                    responseId.ValueKind is not JsonValueKind.Number ||
                    responseId.GetInt64() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var errorElement))
                {
                    var message = errorElement.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : null;
                    throw new McpProtocolException(
                        $"MCP server '{_definition.Id}' failed {method}: " +
                        (message ?? "unknown JSON-RPC error"));
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new McpProtocolException(
                        $"MCP server '{_definition.Id}' returned no result for {method}.");
                }

                return result.Clone();
            }
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task NotifyAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        WriteAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, JsonOptions);
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
