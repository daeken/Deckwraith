using System.Text.Json;

if (args.Length != 1)
{
    return 2;
}

var markerPath = Path.GetFullPath(args[0]);
while (await Console.In.ReadLineAsync() is { } line)
{
    using var document = JsonDocument.Parse(line);
    var request = document.RootElement;
    if (!request.TryGetProperty("id", out var id))
    {
        continue;
    }

    var method = request.GetProperty("method").GetString();
    object response = method switch
    {
        "initialize" => new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            result = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "deckwraith-fake-mcp", version = "1.0.0" },
            },
        },
        "tools/list" => new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            result = new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "emit_structured_side_effect",
                        description = "Write an explicit marker and return a nested structured object.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                label = new
                                {
                                    type = "string",
                                    description = "Marker label.",
                                },
                                count = new
                                {
                                    type = "integer",
                                    minimum = 1,
                                },
                            },
                            required = new[] { "label", "count" },
                            additionalProperties = false,
                        },
                        outputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                label = new { type = "string" },
                                count = new { type = "integer" },
                                nested = new { type = "object" },
                            },
                        },
                    },
                    new
                    {
                        name = "hidden_probe",
                        description = "A tool used to prove explicit exclusion.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new { },
                        },
                        outputSchema = new
                        {
                            type = "object",
                            properties = new { hidden = new { type = "boolean" } },
                        },
                    },
                },
            },
        },
        "tools/call" => CallTool(request, id, markerPath),
        _ => new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            error = new { code = -32601, message = $"Unknown method {method}." },
        },
    };
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
    await Console.Out.FlushAsync();
}

return 0;

static object CallTool(JsonElement request, JsonElement id, string markerPath)
{
    var parameters = request.GetProperty("params");
    var name = parameters.GetProperty("name").GetString();
    if (!string.Equals(name, "emit_structured_side_effect", StringComparison.Ordinal))
    {
        return new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            result = new
            {
                isError = true,
                content = new[] { new { type = "text", text = $"Unknown tool {name}." } },
            },
        };
    }

    var arguments = parameters.GetProperty("arguments");
    var label = arguments.GetProperty("label").GetString()!;
    var count = arguments.GetProperty("count").GetInt32();
    File.AppendAllText(markerPath, $"{label}:{count}{Environment.NewLine}");
    var structured = new
    {
        label,
        count,
        nested = new
        {
            preserved = true,
            values = new[] { 1, 2, 3 },
        },
    };
    return new
    {
        jsonrpc = "2.0",
        id = id.Clone(),
        result = new
        {
            isError = false,
            structuredContent = structured,
            content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(structured) },
            },
        },
    };
}
